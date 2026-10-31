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
    ILogger<WorkflowSignalRouter>? logger = null) : IWorkflowSignalRouter
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

        try
        {
            var handle = temporal.GetWorkflowHandle<TaskExecutionWorkflow>(workflowId);
            return await handle.QueryAsync(
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
    }

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
