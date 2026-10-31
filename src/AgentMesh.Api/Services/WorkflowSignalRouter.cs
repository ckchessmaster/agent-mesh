using AgentMesh.Core.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace AgentMesh.Api.Services;

/// <summary>
/// Routes an incoming chat reply or review decision into the Temporal workflow that is parked waiting
/// for it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the router is its own service.</strong> Two endpoints need it (job chat and reviews)
/// and both need the same three decisions: which workflow id to target, whether one is actually
/// running, and what to do when it is not. Duplicating that in each controller is how the two drift —
/// and the drift that matters is the "workflow not running" case, where one endpoint silently no-ops
/// while the other returns 409 and the human sees a reply accepted by nothing.
/// </para>
/// <para>
/// <strong>The failure mode this exists to prevent.</strong> A signal sent to a workflow id that does
/// not exist is, by default, an RPC error the caller sees. A signal sent with
/// <see cref="SignalWithStartWorkflowAsync"/> semantics to an id that has already <em>completed</em>
/// would start a brand-new workflow run and re-execute the task from turn one. Neither endpoint wants
/// either outcome, so the router signals an existing execution only, and reports "nobody is listening"
/// as a first-class result the controller turns into an actionable response.
/// </para>
/// </remarks>
public interface IWorkflowSignalRouter
{
    /// <summary>
    /// Delivers a chat reply to the workflow parked on input for this job.
    /// </summary>
    /// <param name="jobId">The job whose workflow should receive the reply.</param>
    /// <param name="signal">The reply, carrying the persisted message id for duplicate suppression.</param>
    /// <param name="ct">Cancellation.</param>
    Task<SignalDelivery> SendChatMessageAsync(
        Guid jobId,
        ChatMessageSignal signal,
        CancellationToken ct = default);

    /// <summary>
    /// Delivers a review decision to the workflow parked on review for this job.
    /// </summary>
    Task<SignalDelivery> SendPrReviewAsync(
        Guid jobId,
        PrReviewSignal signal,
        CancellationToken ct = default);

    /// <summary>
    /// Starts a macro task workflow for one checklist task.
    /// </summary>
    /// <remarks>
    /// Uses <c>WorkflowIdReusePolicy.AllowDuplicate</c> semantics via a per-task id and refuses to
    /// start a second <em>running</em> run for the same task, which is what stops two agents being
    /// dispatched onto one branch by a double-clicked button.
    /// </remarks>
    Task<WorkflowStartResult> StartTaskWorkflowAsync(
        TaskExecutionInput input,
        CancellationToken ct = default);

    /// <summary>
    /// The workflow's live status, for the dashboard.
    /// </summary>
    /// <remarks>
    /// Returns null when no execution exists. A query against a closed workflow throws rather than
    /// returning null, so the caller must not treat an exception as "not started" — this method
    /// distinguishes the two.
    /// </remarks>
    Task<TaskStatusView?> GetStatusAsync(Guid jobId, string? taskId, CancellationToken ct = default);

    /// <summary>
    /// Whether a running macro task workflow owns this job's next turn.
    /// </summary>
    /// <remarks>
    /// Consulted by <c>PostJobMessage</c> before it dispatches a legacy SignalR runner, so a job under
    /// Temporal management cannot also acquire a second agent on the same branch. Fails toward false:
    /// an unreachable Temporal must not swallow chat replies. See the implementation for why the probe is
    /// bounded to <see cref="WorkflowSignalRouter.ProbeTimeout"/>.
    /// </remarks>
    /// <summary>
    /// Whether a running macro task workflow owns this job's next turn for the specified target persona,
    /// or for any persona when <paramref name="targetPersonaId"/> is null or whitespace.
    /// </summary>
    /// <remarks>
    /// Scopes ownership to the orchestrated persona: when <paramref name="targetPersonaId"/> is provided,
    /// returns true only if the running workflow is driving that persona (or falls back to true if the
    /// workflow's persona is unset). Fails toward false: an unreachable Temporal must not swallow chat replies.
    /// </remarks>
    Task<bool> IsWorkflowDrivingAsync(Guid jobId, string? targetPersonaId = null, CancellationToken ct = default);
}

/// <summary>What happened when a signal was routed.</summary>
public enum SignalDeliveryStatus
{
    /// <summary>Temporal accepted the signal.</summary>
    Delivered,

    /// <summary>No running workflow exists for this job, so nothing was signalled.</summary>
    NoRunningWorkflow,

    /// <summary>The workflow exists but is not in a state that waits for this signal.</summary>
    NotWaiting,

    /// <summary>The signal was rejected by the workflow (a handler error).</summary>
    Rejected,
}

/// <summary>
/// The outcome of a routing attempt.
/// </summary>
/// <param name="WorkflowId">The id that was targeted, whether or not the signal landed.</param>
/// <param name="Status">What happened.</param>
/// <param name="Detail">Why, for the non-success cases.</param>
/// <remarks>
/// A record rather than a bool because "not delivered" has three causes that need different responses:
/// no workflow (the job is not under orchestration — normal, and the reply still belongs in the chat),
/// a workflow that is not waiting (the reply arrived while a turn was running — also normal, and the
/// workflow will drain it on the next turn), and a rejection (a bug, and the caller should see the
/// error).
/// </remarks>
public record SignalDelivery(
    string WorkflowId,
    SignalDeliveryStatus Status,
    string? Detail = null)
{
    public bool Delivered => Status == SignalDeliveryStatus.Delivered;
}

/// <summary>What happened when a workflow start was attempted.</summary>
/// <param name="Started">True when a new run began.</param>
/// <param name="AlreadyRunning">
/// True when a run for this task is already open, so nothing new started and the caller got the
/// existing id back. This is the answer a double-clicked start button should produce.
/// </param>
public record WorkflowStartResult(
    bool Started,
    string WorkflowId,
    bool AlreadyRunning = false,
    string? Detail = null);

/// <summary>
/// Temporal-backed implementation of <see cref="IWorkflowSignalRouter"/>.
/// </summary>
/// <remarks>
/// <strong>Workflow id scheme.</strong> <c>task-execution-{jobId}</c>, with the task id carried in the
/// workflow's own input and in its memo rather than in the id. Two reasons the id is job-scoped:
/// <list type="bullet">
/// <item><description>
/// The endpoints that route here are job-scoped (<c>/api/jobs/{jobId}/chat</c>,
/// <c>/api/jobs/{jobId}/reviews</c>) and a caller there knows the job, not the task. Deriving a task
/// from a job id would mean reading the checklist to guess which task is active — the same
/// "guess the active task" logic the workspace service already refuses to duplicate.
/// </description></item>
/// <item><description>
/// A job is the unit a human converses in. Two workflows for one job means two places a reply could
/// land, and the one that is not waiting for input would accept the signal into its inbox and never
/// show it to anyone.
/// </description></item>
/// </list>
/// <para>
/// The consequence, stated plainly: one concurrently-orchestrated task per job. A job whose checklist
/// has two tasks under Temporal management needs the id to include the task, and that change has to be
/// made in one place — <see cref="WorkflowIdFor"/> — which is why it is a single method rather than an
/// interpolated string at each call site.
/// </para>
/// </remarks>
public class WorkflowSignalRouter(
    ITemporalClient temporal,
    ILogger<WorkflowSignalRouter>? logger = null,
    AgentMesh.Core.Repositories.IWorkspaceStateRepository? checkpoints = null) : IWorkflowSignalRouter
{
    /// <summary>Prefix for every macro task workflow id.</summary>
    public const string WorkflowIdPrefix = "task-execution-";

    /// <summary>The task queue the worker listens on. Must match the worker's registration.</summary>
    public const string TaskQueue = "agent-task-queue";

    /// <summary>
    /// The workflow id for a job's macro task workflow.
    /// </summary>
    public static string WorkflowIdFor(Guid jobId) => $"{WorkflowIdPrefix}{jobId:N}";

    public async Task<SignalDelivery> SendChatMessageAsync(
        Guid jobId,
        ChatMessageSignal signal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var workflowId = WorkflowIdFor(jobId);

        // Read the live state before signalling. Two reasons, both about the human's experience:
        //
        // 1. A signal to a workflow that does not exist is an RPC error whose text is Temporal's
        //    ("Execution not found in mutable state"), which is useless in a chat UI. Checking first
        //    turns it into "this job is not under workflow orchestration", which is true and actionable.
        //
        // 2. A signal to a workflow that is *running but not waiting* is accepted and buffered in the
        //    inbox — which is correct behaviour (the next turn drains it) but must not be reported to
        //    the caller as "the agent has been woken", because it has not been.
        var description = await DescribeAsync(workflowId, ct);

        if (description is null)
        {
            return new SignalDelivery(workflowId, SignalDeliveryStatus.NoRunningWorkflow,
                "No macro task workflow is running for this job; the message was saved to the chat " +
                "but no agent turn is parked waiting for it.");
        }

        if (description.Status != WorkflowExecutionStatus.Running)
        {
            return new SignalDelivery(workflowId, SignalDeliveryStatus.NoRunningWorkflow,
                $"The workflow for this job is {description.Status}, not running.");
        }

        try
        {
            var handle = temporal.GetWorkflowHandle<TaskExecutionWorkflow>(workflowId);

            // Typed expression, not the string name. The SDK registers the handler as
            // "ReceiveChatMessage" (it strips the Async suffix); an untyped call to
            // "ReceiveChatMessageAsync" is accepted by the server and silently never delivered, which
            // is the single most dangerous failure mode in this feature. Going through the expression
            // makes the compiler resolve the name.
            await handle.SignalAsync(
                w => w.ReceiveChatMessageAsync(signal),
                new WorkflowSignalOptions { Rpc = Rpc(ct) });

            logger?.LogInformation(
                "Signalled workflow {WorkflowId} with a chat reply from {SenderId}",
                workflowId, signal.SenderId);

            return new SignalDelivery(workflowId, SignalDeliveryStatus.Delivered);
        }
        catch (RpcException ex)
        {
            return new SignalDelivery(workflowId, SignalDeliveryStatus.Rejected, Describe(ex));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A handler that rejects the payload shape (a null field the serializer refuses) surfaces
            // here. Reported as Rejected rather than allowed to throw, because the message is already
            // persisted and failing the HTTP call would suggest it was lost.
            logger?.LogWarning(ex, "Signal for {WorkflowId} was rejected", workflowId);
            return new SignalDelivery(workflowId, SignalDeliveryStatus.Rejected, ex.Message);
        }
    }

    public async Task<SignalDelivery> SendPrReviewAsync(
        Guid jobId,
        PrReviewSignal signal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var workflowId = WorkflowIdFor(jobId);
        var description = await DescribeAsync(workflowId, ct);

        if (description is null)
        {
            return new SignalDelivery(workflowId, SignalDeliveryStatus.NoRunningWorkflow,
                "No macro task workflow is running for this job, so there is no review gate to release.");
        }

        if (description.Status != WorkflowExecutionStatus.Running)
        {
            return new SignalDelivery(workflowId, SignalDeliveryStatus.NoRunningWorkflow,
                $"The workflow for this job is {description.Status}, not running.");
        }

        try
        {
            var handle = temporal.GetWorkflowHandle<TaskExecutionWorkflow>(workflowId);
            await handle.SignalAsync(
                w => w.ReceivePrReviewSignalAsync(signal),
                new WorkflowSignalOptions { Rpc = Rpc(ct) });

            logger?.LogInformation(
                "Signalled workflow {WorkflowId} with a {Decision} review from {ReviewerId}",
                workflowId, signal.IsApproved ? "approving" : "changes-requested", signal.ReviewerId);

            return new SignalDelivery(workflowId, SignalDeliveryStatus.Delivered);
        }
        catch (RpcException ex)
        {
            return new SignalDelivery(workflowId, SignalDeliveryStatus.Rejected, Describe(ex));
        }
    }

    public async Task<WorkflowStartResult> StartTaskWorkflowAsync(
        TaskExecutionInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!Guid.TryParse(input.JobId.Trim(), out var jobId))
        {
            return new WorkflowStartResult(false, $"{WorkflowIdPrefix}{input.JobId}",
                Detail: $"'{input.JobId}' is not a job GUID.");
        }

        var workflowId = WorkflowIdFor(jobId);

        var existing = await DescribeAsync(workflowId, ct);
        if (existing?.Status == WorkflowExecutionStatus.Running)
        {
            logger?.LogInformation(
                "Macro workflow {WorkflowId} is already running; not starting a second one for task {TaskId}",
                workflowId, input.TaskId);

            return new WorkflowStartResult(false, workflowId, AlreadyRunning: true,
                Detail: "A macro task workflow is already running for this job.");
        }

        await temporal.StartWorkflowAsync(
            (TaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(input),
            new WorkflowOptions(workflowId, TaskQueue)
            {
                Memo = new Dictionary<string, object>
                {
                    ["personaId"] = input.PersonaId ?? string.Empty,
                },
                // AllowDuplicate is the default and is what we want: a workflow that completed for this
                // job id can be re-run for a *different* task, which is the normal case for a checklist.
                // The guard against double-running is the DescribeAsync check above, not the reuse
                // policy — the policy cannot distinguish "same task, retry" from "next task", and the
                // workflow id is deliberately job-scoped.
                //
                // A race between the check and the start is closed by Temporal's own id uniqueness for
                // a *running* execution: the second StartWorkflowAsync fails rather than starting a
                // duplicate, and that error surfaces to the caller instead of silently running two
                // agents on one branch.
                StaticSummary = $"macro task execution for job {jobId} / task {input.TaskId}",
                RetryPolicy = new()
                {
                    // The workflow itself is not retried: it owns its own turn loop and its own halt
                    // conditions. A workflow-level retry would restart the loop from turn one with the
                    // inbox cleared, re-dispatching an agent onto work that is already in progress.
                    MaximumAttempts = 1,
                },
            });

        logger?.LogInformation(
            "Started macro workflow {WorkflowId} for job {JobId} task {TaskId} in {Workspace}",
            workflowId, input.JobId, input.TaskId, input.WorkspacePath);

        return new WorkflowStartResult(true, workflowId);
    }

    public async Task<TaskStatusView?> GetStatusAsync(
        Guid jobId,
        string? taskId,
        CancellationToken ct = default)
    {
        var workflowId = WorkflowIdFor(jobId);
        TaskStatusView? status;

        try
        {
            var handle = temporal.GetWorkflowHandle<TaskExecutionWorkflow>(workflowId);
            status = await handle.QueryAsync(
                w => w.GetTaskStatus(),
                new WorkflowQueryOptions { Rpc = Rpc(ct) });
        }
        catch (RpcException ex)
        {
            // A query against a workflow that never existed, or one that is closed, both land here.
            // Returning null is correct for the dashboard: "no live status" is the honest answer, and
            // the difference between the two is recoverable from the workflow's history if somebody
            // needs it.
            logger?.LogDebug("Status query for {WorkflowId} returned nothing: {Detail}",
                workflowId, Describe(ex));
            return null;
        }

        return status is null ? null : await WithTurnSessionId(status, jobId, taskId, ct);
    }

    /// <summary>
    /// Fills in the session id the workflow cannot know.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the workflow cannot report this itself.</strong> <c>TurnOutcome</c> deliberately has
    /// no <c>TurnSessionId</c> field: the session id is a per-<em>attempt</em> identity, and threading it
    /// back through the workflow would record a second id for a task whose first attempt timed out —
    /// orphaning the very session the retry is supposed to re-attach to. The pairing therefore lives in
    /// the durable checkpoint row, written by the runner mid-turn. So the workflow's own
    /// <c>_turnSessionId</c> can only ever be the value the run was started with, which for the normal
    /// case (start without one) is null forever, and the status endpoint's
    /// <c>TurnSessionId</c> — documented as "latest turn session, for deep-linking into the transcript"
    /// — was a permanently empty column.
    /// </para>
    /// <para>
    /// Reading it here rather than in the workflow keeps the workflow deterministic and adds no new wire
    /// type: the checkpoint is already the single source of truth for the pairing, and this is a read of
    /// it on the display path only.
    /// </para>
    /// <para>
    /// <strong>Never fails the request.</strong> The workflow query already succeeded; a database hiccup
    /// here must not turn a working status endpoint into a 500, so an unavailable pairing degrades to the
    /// un-enriched view.
    /// </para>
    /// </remarks>
    private async Task<TaskStatusView> WithTurnSessionId(
        TaskStatusView status,
        Guid jobId,
        string? taskId,
        CancellationToken ct)
    {
        if (checkpoints is null)
        {
            return status;
        }

        try
        {
            // The checkpoint wins even when the workflow already carries a value. Its own field can only
            // ever be the id the run was *started* with, so on an explicit start it would deep-link to
            // turn 1's transcript for the rest of the run — while the field is documented as "latest turn
            // session". The checkpoint is rewritten at every turn boundary, so it is the only freshest
            // answer available. The workflow's value remains the fallback for a task whose row has no
            // pairing yet.
            var pairing = string.IsNullOrWhiteSpace(taskId)
                ? (await checkpoints.GetCheckpointsForJobAsync(jobId.ToString(), ct))
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.TurnSessionId))?.TurnSessionId
                : (await checkpoints.GetCheckpointAsync(jobId.ToString(), taskId, ct))?.TurnSessionId;

            return string.IsNullOrWhiteSpace(pairing)
                ? status
                : status with { TurnSessionId = pairing };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Could not read the turn-session pairing for job {JobId}; the status view will carry no " +
                "transcript deep-link", jobId);
            return status;
        }
    }

    /// <summary>
    /// Whether a macro task workflow is currently running for this job, and therefore owns its next turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists as a separate call.</strong> <c>PostJobMessage</c> has to decide, before it
    /// dispatches anything, whether a legacy SignalR runner or the durable workflow should act on the
    /// reply. Both acting is not a benign race: each starts an agent on the same working tree, so two
    /// agents edit one branch, push over each other, and the operator sees a rejected push nobody can
    /// explain. The answer is "exactly one owner per job", and the only authority on that is whether the
    /// workflow is live.
    /// </para>
    /// <para>
    /// <strong>Fails toward the legacy path.</strong> A describe that errors, times out, or returns
    /// anything unexpected yields false, which leaves <c>PostJobMessage</c> behaving exactly as it did
    /// before orchestration existed. The alternative — treating an unreachable Temporal as "a workflow is
    /// driving, so suppress the dispatch" — would mean a control-plane outage silently swallows every chat
    /// reply on every job: no runner, no signal, no error to the sender. A double-drive under an outage is
    /// recoverable; a dropped turn is not.
    /// </para>
    /// <para>
    /// <strong>Bounded by a deadline, not by an RPC timeout.</strong> This runs on the hottest chat
    /// endpoint in the product, in the request path, on every message that mentions a persona, so it must
    /// not be able to hold a request thread.
    /// </para>
    /// <para>
    /// The obvious implementation — <c>RpcOptions.Timeout</c> — does not do that, and I wrote it that way
    /// before measuring. Against a dead endpoint with the SDK's default retry policy, a 1.5-second RPC
    /// timeout took <strong>9.08 seconds</strong> end to end: the timeout bounds a single attempt and each
    /// retry then re-dials from scratch. A cancellation deadline on the same call took <strong>1.52
    /// seconds</strong>, because a deadline bounds the whole operation including retries. So the bound here
    /// is a linked <see cref="CancellationTokenSource"/>, and <see cref="ProbeTimeout"/> is a total budget,
    /// not a per-attempt timeout.
    /// </para>
    /// </remarks>
    public async Task<bool> IsWorkflowDrivingAsync(Guid jobId, string? targetPersonaId = null, CancellationToken ct = default)
    {
        try
        {
            // Linked, so the caller's own cancellation still propagates immediately and independently of
            // the budget. CancelAfter alone would also work, but linking keeps both sources observable and
            // leaves ct unmutated for the rest of the request.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ProbeTimeout);

            var handle = temporal.GetWorkflowHandle<TaskExecutionWorkflow>(WorkflowIdFor(jobId));
            var description = await handle.DescribeAsync(
                new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = deadline.Token } });

            if (description.Status != WorkflowExecutionStatus.Running)
            {
                return false;
            }

            // If no target persona was specified, running status is sufficient.
            if (string.IsNullOrWhiteSpace(targetPersonaId))
            {
                return true;
            }

            // Scope ownership to the orchestrated persona:
            // 1. Prefer reading the persona from workflow Memo (answered by the Temporal server directly,
            // with no worker dependency so worker rollouts do not block or time out probe requests).
            if (description.Memo != null &&
                description.Memo.TryGetValue("personaId", out var raw) &&
                raw != null &&
                await raw.ToValueAsync<string>() is { Length: > 0 } driving)
            {
                return string.Equals(NormalizePersona(driving), NormalizePersona(targetPersonaId), StringComparison.Ordinal);
            }

            // 2. Fall back to querying the running workflow only when the memo is missing
            // (e.g. workflows started before the memo was recorded).
            try
            {
                var status = await handle.QueryAsync(
                    w => w.GetTaskStatus(),
                    new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = deadline.Token } });

                // Fall back to job-level suppression only if the workflow's PersonaId is null/unset.
                if (string.IsNullOrWhiteSpace(status?.PersonaId))
                {
                    return true;
                }

                var normTarget = NormalizePersona(targetPersonaId);
                var normDriving = NormalizePersona(status.PersonaId);

                return string.Equals(normTarget, normDriving, StringComparison.Ordinal);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogDebug("Could not query task status for workflow {WorkflowId}; failing toward dispatch: {Detail}",
                    WorkflowIdFor(jobId), Describe(ex));
                return false;
            }
        }
        // Re-thrown only when the CALLER went away. Our own budget expiring is also an
        // OperationCanceledException, and must degrade to false like any other probe failure: the request
        // is still live, and the right answer under a slow control plane is "not orchestrated, dispatch as
        // usual". Distinguishing the two by testing ct rather than the linked token is why the filter is on
        // ct.IsCancellationRequested and not a bare catch.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes the ordinary case of a job that has never been orchestrated: the server answers
            // with "execution not found", which is not an error condition here, so it is Debug-level.
            // Also includes the budget expiring, which is the whole point of the bound.
            logger?.LogDebug("Orchestration probe for {WorkflowId} answered negatively: {Detail}",
                WorkflowIdFor(jobId), Describe(ex));
            return false;
        }
    }

    private static string? NormalizePersona(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>
    /// Total budget for the per-message orchestration probe, <em>including</em> SDK retries.
    /// </summary>
    /// <remarks>
    /// Enforced as a cancellation deadline rather than as <c>RpcOptions.Timeout</c>, for the measured
    /// reason on <see cref="IsWorkflowDrivingAsync"/>: the latter bounds one attempt, and the default retry
    /// policy then re-dials, tripling it (1.5s configured, 9.08s observed).
    /// </remarks>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The execution's status, or null when there is no such execution.
    /// </summary>
    /// <remarks>
    /// <see cref="IWorkflowHandle.DescribeAsync"/> throws for an unknown id, which is the common case
    /// here (a job that has never been put under orchestration). Catching it rather than pre-checking
    /// with a ListWorkflow query keeps this to one round trip for the normal path.
    /// </remarks>
    private async Task<WorkflowExecutionDescription?> DescribeAsync(string workflowId, CancellationToken ct)
    {
        try
        {
            var handle = temporal.GetWorkflowHandle<TaskExecutionWorkflow>(workflowId);
            return await handle.DescribeAsync(new WorkflowDescribeOptions { Rpc = Rpc(ct) });
        }
        catch (RpcException ex)
        {
            logger?.LogDebug("No workflow execution for {WorkflowId}: {Detail}", workflowId, Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// RPC options carrying the caller's cancellation token.
    /// </summary>
    /// <remarks>
    /// The SDK's signal/query/describe calls take an options object rather than a
    /// <see cref="CancellationToken"/>, and cancellation lives on <see cref="RpcOptions"/>. Wrapping it
    /// once keeps every call site honest: a signal that cannot be cancelled because someone passed the
    /// options positionally and forgot the token is the kind of detail that only shows up when a client
    /// disconnects mid-request and the server keeps working.
    /// </remarks>
    private static RpcOptions Rpc(CancellationToken ct) => new() { CancellationToken = ct };

    private static string Describe(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message.Split('\n')[0].Trim();
}
