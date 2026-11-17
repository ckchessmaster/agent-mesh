using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace AgentMesh.Api.Services;

/// <summary>What recovery actually did to the job's orchestration.</summary>
/// <remarks>
/// Serialized as a name rather than an index, matching <c>TimelineCardType</c>. Two reasons: the value is
/// the whole content of an operator-facing response ("SettledOnly" versus "Resumed" is the difference
/// between retrying and walking away), and an ordinal on the wire silently renumbers every meaning the
/// moment a member is inserted. The dashboard's <c>JobRecoveryResult.action</c> is typed as these names.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum JobRecoveryAction
{
    /// <summary>Nothing was live and nothing was stalled; the job already needed nothing.</summary>
    NothingToDo,

    /// <summary>Phantom turns were settled and the workflow was deliberately left alone.</summary>
    SettledOnly,

    /// <summary>A workflow run is alive, so its wedged turn was aborted and it will advance itself.</summary>
    AbortedLiveTurn,

    /// <summary>A new run was started from the job's own recorded progress.</summary>
    Resumed,

    /// <summary>No recorded checklist to resume from, so the job was started again from discovery.</summary>
    RestartedFromDiscovery,

    /// <summary>Turns were settled, but no orchestrator is configured, so nothing can be resumed.</summary>
    OrchestratorUnavailable
}

/// <summary>Recovery request as an operator or the cockpit sends it.</summary>
/// <param name="Resume">
/// <see langword="false"/> to only erase the phantom turns. Useful when the operator intends to drive the
/// persona by hand and wants the lie gone without a workflow starting underneath them.
/// </param>
/// <param name="Note">Free text recorded on the timeline card so the reason outlives the caller.</param>
/// <param name="QuietSeconds">
/// How long a session must have been silent to count as abandoned. This is the only guard against
/// settling a turn that is genuinely mid-flight when a live observer is about to conclude it, so it is
/// deliberately not zero.
/// </param>
/// <param name="Caller">
/// Who asked, recorded on the timeline card. Optional in the payload because the endpoint falls back to the
/// authenticated identity; a request that names someone else is still auditable either way.
/// </param>
public record JobRecoveryRequest(
    bool Resume = true,
    string? Note = null,
    int QuietSeconds = 120,
    string? Caller = null);

/// <summary>What recovery did, in terms an operator can act on.</summary>
public record JobRecoveryResult(
    Guid JobId,
    string? WorkflowId,
    JobRecoveryAction Action,
    IReadOnlyList<Guid> SettledSessions,
    string? ResumedAtTask,
    int ResumedAtNumber,
    int TotalTasks,
    string? PrUrl,
    string Message);

/// <summary>Raised when the job does not exist.</summary>
public sealed class JobRecoveryNotFoundException(Guid jobId)
    : Exception($"No project job with id '{jobId}' exists to recover.");

/// <summary>Raised when the job exists but recovery does not apply to it.</summary>
public sealed class JobRecoveryBlockedException(string message) : Exception(message);

/// <summary>
/// The slice of orchestration recovery needs from Temporal, kept behind an interface so the repair logic
/// is testable without a server.
/// </summary>
public interface IJobExecutionWorkflowGateway
{
    /// <summary>The job's workflow run, if one was ever started, and whether it is still open.</summary>
    Task<JobRunState?> FindRunAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Tells an open run to stop waiting on the turn it is holding.</summary>
    Task AbortTurnAsync(string workflowId, AbortActiveTurnSignal signal, CancellationToken ct = default);

    /// <summary>Starts a run, optionally carrying a continuation that resumes recorded progress.</summary>
    Task StartAsync(JobExecutionInput input, string workflowId, string? memoPersonaId, CancellationToken ct = default);
}

/// <summary>A job's workflow run, identified by the id that actually resolved.</summary>
public readonly record struct JobRunState(string WorkflowId, bool IsOpen);

/// <summary>
/// Thrown when the gateway cannot reach Temporal.
/// </summary>
public sealed class JobRecoveryOrchestrationException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// <see cref="IJobExecutionWorkflowGateway"/> over the real Temporal client.
/// </summary>
/// <remarks>
/// Id candidates mirror <see cref="WorkflowSignalRouter"/>, which probes several historical shapes of the
/// job workflow id. Recovery has to probe the same list: resume a job whose run is recorded under an id
/// form this helper does not try, and the operator gets "no run found" followed by a second run of work
/// that was already done.
/// </remarks>
public sealed class TemporalJobExecutionWorkflowGateway(
    ITemporalClient temporal,
    ILogger<TemporalJobExecutionWorkflowGateway>? logger = null) : IJobExecutionWorkflowGateway
{
    private const string JobWorkflowPrefix = "job-";

    public async Task<JobRunState?> FindRunAsync(Guid jobId, CancellationToken ct = default)
    {
        foreach (var workflowId in CandidateIds(jobId))
        {
            try
            {
                var description = await temporal.GetWorkflowHandle(workflowId)
                    .DescribeAsync(new WorkflowDescribeOptions { Rpc = new RpcOptions { CancellationToken = ct } });

                return new JobRunState(workflowId, description.Status == WorkflowExecutionStatus.Running);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                // Try the next id shape. A job that was never orchestrated is a normal answer here.
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Temporal describe failed for {WorkflowId}", workflowId);
                throw new JobRecoveryOrchestrationException(
                    $"Could not ask the orchestrator whether a run exists for job '{jobId}'.", ex);
            }
        }

        return null;
    }

    public async Task AbortTurnAsync(string workflowId, AbortActiveTurnSignal signal, CancellationToken ct = default)
    {
        var rpc = new RpcOptions { CancellationToken = ct };
        await temporal.GetWorkflowHandle<IJobExecutionWorkflow>(workflowId)
            .SignalAsync(w => w.AbortTurnAsync(signal), new WorkflowSignalOptions { Rpc = rpc });
    }

    public async Task StartAsync(
        JobExecutionInput input,
        string workflowId,
        string? memoPersonaId,
        CancellationToken ct = default)
    {
        var options = new WorkflowOptions(workflowId, WorkflowSignalRouter.TaskQueue)
        {
            // The whole point of recovery is that the previous run for this id has closed. Without
            // AllowDuplicate the start is rejected, and the operator is told to go reset history by hand,
            // which is the thing this endpoint exists to remove.
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
            Rpc = new RpcOptions { CancellationToken = ct }
        };

        if (!string.IsNullOrWhiteSpace(memoPersonaId))
        {
            options.Memo = new Dictionary<string, object> { ["personaId"] = memoPersonaId };
        }

        try
        {
            await temporal.StartWorkflowAsync(
                (IJobExecutionWorkflow wf) => wf.ExecuteJobAsync(input), options);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Temporal start failed for {WorkflowId}", workflowId);
            throw new JobRecoveryOrchestrationException(
                $"The orchestrator refused to start a resumed run for job '{input.JobId}'.", ex);
        }
    }

    private static IEnumerable<string> CandidateIds(Guid jobId) => new[]
    {
        $"{JobWorkflowPrefix}{jobId}",
        $"{JobWorkflowPrefix}{jobId:D}",
        $"{JobWorkflowPrefix}{jobId:N}"
    }.Distinct();

    /// <summary>
    /// True for Temporal's "no such execution".
    /// </summary>
    /// <remarks>
    /// Matched on the status name rather than the <c>Grpc.Core.StatusCode</c> enum because the SDK wraps
    /// the call and the enum type is one more dependency to keep pinned for a three-word check. The
    /// message is stable across SDK versions: it is what the server returns for an unknown run.
    /// </remarks>
    private static bool IsNotFound(Exception ex)
    {
        for (Exception? cursor = ex; cursor is not null; cursor = cursor.InnerException)
        {
            if (cursor.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                cursor.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
                cursor.GetType().Name.Contains("RpcException", StringComparison.Ordinal))
            {
                return cursor.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                       cursor.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}

/// <summary>
/// Puts a job back into a state where work can proceed, without anyone touching Temporal's history.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Before it, the only honest response to a wedged turn was to open the
/// Temporal UI, find the run, and reset or terminate it — a repair that requires cluster access, knows the
/// workflow id shape, and is easy to get subtly wrong (resetting the wrong event replays completed stages,
/// terminating one and starting fresh loses the checklist the persona built). The facts needed to resume are
/// already in the platform's own rows: which tasks finished, which session was holding the job, which PR
/// exists. Recovery reads those facts and asks for one new run.
/// </para>
/// <para>
/// <strong>Why it resumes instead of restarting.</strong> A persona that spent five turns on a job has
/// committed work on disk and a checklist it wrote itself. Re-running from discovery would ask it to
/// rediscover a codebase it already changed, and the new plan would disagree with the tree. Resuming from
/// <c>IsCompleted</c> over the job's own task list is the same rule the workflow uses for its own
/// continue-as-new, so recovery cannot introduce a notion of progress that the orchestrator disagrees with.
/// </para>
/// <para>
/// <strong>Why it settles sessions before starting anything.</strong> A resumed run mints a new turn session.
/// If the abandoned one is still claiming to be live, the cockpit then shows two turns running on one job and
/// the operator has learned nothing. The settle and the resume are one operation for that reason, and the
/// reason is written into both transcripts.
/// </para>
/// <para>
/// <strong>Why an open run is aborted rather than resumed.</strong> If a run is still open it is the owner of
/// the stalled task, and starting a second run would put two orchestrators on one workspace lease. Aborting
/// the live turn is the workflow's own documented path for this, and it keeps the run's history intact.
/// </para>
/// </remarks>
public interface IJobTurnRecovery
{
    Task<JobRecoveryResult> RecoverAsync(
        Guid jobId,
        JobRecoveryRequest request,
        string caller,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class JobTurnRecovery(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub>? hubContext,
    IJobExecutionWorkflowGateway? gateway = null,
    ILogger<JobTurnRecovery>? logger = null) : IJobTurnRecovery
{
    /// <summary>Author recorded when a human triggers a settle, distinct from the sweeper.</summary>
    public const string RecoveryAgentId = "operator-recovery";

    /// <summary>Floors the quiet window so a request cannot ask for an instant, racy settle.</summary>
    public const int MinimumQuietSeconds = 15;

    public async Task<JobRecoveryResult> RecoverAsync(
        Guid jobId,
        JobRecoveryRequest request,
        string caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new JobRecoveryNotFoundException(jobId);

        if (job.Status == ProjectJobStatus.Archived)
        {
            throw new JobRecoveryBlockedException(
                $"Job '{jobId}' is archived. Unarchive it first, or start a new job instead of recovering this one.");
        }

        var settled = await SettlePhantomTurnsAsync(job, request, caller, ct);

        if (!request.Resume)
        {
            return Finish(job, settled, JobRecoveryAction.SettledOnly, resumed: null, prUrl: null,
                message: settled.Count > 0
                    ? $"Settled {settled.Count} abandoned turn(s). Nothing was started, because resume was declined."
                    : "Nothing was abandoned, and nothing was started, because resume was declined.");
        }

        if (gateway is null)
        {
            return Finish(job, settled, JobRecoveryAction.OrchestratorUnavailable, resumed: null, prUrl: null,
                message: "This deployment has no workflow orchestrator, so a stalled job cannot be resumed. " +
                         "The abandoned turns were settled; dispatch the next turn by hand if you want work to continue.");
        }

        var run = await gateway.FindRunAsync(jobId, ct);
        if (run?.IsOpen == true)
        {
            await gateway.AbortTurnAsync(run.Value.WorkflowId,
                new AbortActiveTurnSignal(BuildAbortReason(request.Note, caller), caller), ct);

            await AddRecoveryCardAsync(job, settled, run.Value.WorkflowId, "aborted-live-turn",
                resumedTitle: null, resumedNumber: 0, totalTasks: 0, prUrl: null, request.Note, caller, ct);

            return Finish(job, settled, JobRecoveryAction.AbortedLiveTurn,
                resumed: (Title: null, Number: 0, Total: 0), prUrl: null,
                workflowId: run.Value.WorkflowId,
                message: $"A run is open on {run.Value.WorkflowId}, so its stalled turn was aborted. " +
                         "The workflow will advance from its own checkpoint; no second run was started.");
        }

        var cards = await db.JobTimelineCards.AsNoTracking()
            .Where(c => c.JobId == jobId.ToString())
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var prUrl = LatestPrUrl(cards);
        var continuation = BuildContinuation(job, cards, out var resumedTitle, out var resumedNumber, out var totalTasks);
        var input = await BuildInputAsync(job, continuation, ct);
        var workflowId = run?.WorkflowId ?? $"job-{jobId}";

        await gateway.StartAsync(input, workflowId, memoPersonaId: job.AssignedAgentId, ct);

        await MarkRecoveredAsync(job, resumedTitle, resumedNumber, totalTasks, ct);
        await AddRecoveryCardAsync(job, settled, workflowId,
            continuation is null ? "restarted-from-discovery" : "resumed",
            resumedTitle, resumedNumber, totalTasks, prUrl, request.Note, caller, ct);

        var action = continuation is null ? JobRecoveryAction.RestartedFromDiscovery : JobRecoveryAction.Resumed;
        return Finish(job, settled, action,
            resumed: (resumedTitle, resumedNumber, totalTasks), prUrl: prUrl, workflowId: workflowId,
            message: continuation is null
                ? $"Started a fresh run on {workflowId}: the job has no recorded checklist, so there was " +
                  "nothing to resume from and the persona will run discovery again."
                : $"Resumed {workflowId} at task {resumedNumber}/{totalTasks}" +
                  (string.IsNullOrWhiteSpace(resumedTitle) ? "." : $": {resumedTitle}.") +
                  (continuation.PendingTasks.Count == 0
                      ? " Every checklist task is complete, so the run picks up at the pull-request stage."
                      : string.Empty));
    }

    /// <summary>
    /// Settles the job's live-but-abandoned sessions and clears the workspace state they left behind.
    /// </summary>
    /// <remarks>
    /// Scoped to this job, and gated on silence, so a concurrent turn on another job is never touched and a
    /// turn whose next event is in flight is not overwritten. Both matter: recovery is an operator action
    /// taken in a hurry, and an over-broad settle would turn one wedged job into a incident across several.
    /// </remarks>
    private async Task<IReadOnlyList<AgentSession>> SettlePhantomTurnsAsync(
        ProjectJob job,
        JobRecoveryRequest request,
        string caller,
        CancellationToken ct)
    {
        var quiet = TimeSpan.FromSeconds(Math.Max(MinimumQuietSeconds, request.QuietSeconds));
        var cutoff = DateTimeOffset.UtcNow - quiet;

        var notice = $"An operator ({caller}) recovered this job" +
                     (string.IsNullOrWhiteSpace(request.Note) ? "." : $": {request.Note.Trim()}") +
                     $" Turns quieter than {(int)quiet.TotalSeconds}s were abandoned.";

        var settled = await TurnStallSettler.SettleOrphansAsync(
            db,
            cutoff,
            notice,
            RecoveryAgentId,
            filter: s => TurnStallSettler.TryGetJobIdFromMetadata(s.MetadataJson, out var id) && id == job.Id,
            ct);

        foreach (var session in settled)
        {
            await ReleaseWorkspaceAsync(job.Id, session.Id, ct);

            if (hubContext is not null)
            {
                await hubContext.Clients.All.SendAsync("SessionUpdated", session, ct);
            }
        }

        if (settled.Count > 0)
        {
            logger?.LogWarning(
                "Recovery settled {Count} abandoned session(s) on job {JobId} for {Caller}: {Sessions}",
                settled.Count, job.Id, caller, string.Join(", ", settled.Select(s => s.Id.ToString("N"))));
        }

        return settled;
    }

    /// <summary>
    /// Rewinds the workspace bookkeeping a dead turn left mid-flight.
    /// </summary>
    /// <remarks>
    /// A task checkpoint parked at <c>in_progress</c> is a claim that somebody is still holding this tree,
    /// and a lease naming the abandoned session's driver says the same thing. Neither is enforced on
    /// acquire, so leaving them does not block a resumed run — but the next turn reads the checkpoint as
    /// context, and "in progress" describing a turn that died hours ago is exactly the kind of stale fact
    /// that makes an agent refuse to touch files it has already committed.
    /// </remarks>
    private async Task ReleaseWorkspaceAsync(Guid jobId, Guid sessionId, CancellationToken ct)
    {
        var held = await db.JobWorkspaceStates
            .Where(w => w.JobId == jobId && w.CheckpointTurnSessionId == sessionId && w.CheckpointStatus == "in_progress")
            .ToListAsync(ct);

        if (held.Count == 0) return;

        foreach (var state in held)
        {
            state.CheckpointStatus = "todo";
            state.CheckpointSummary = TurnStallSettler.StalledHeartbeatPrefix +
                " A turn holding this workspace was abandoned; the task is not in progress.";
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rebuilds the orchestrator's resume state from the job's own recorded progress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offset is the first task that is not complete, which is the same rule the workflow applies to
    /// itself when it continues-as-new, so recovery cannot create a notion of progress the orchestrator
    /// disagrees with. When every task is complete, the pending list is empty and the run resumes at the
    /// pull-request stage — the case this was written for: five tasks finished, a branch committed inside
    /// the persona's workspace, and the turn that was going to open the PR dead in the water.
    /// </para>
    /// <para>
    /// A job with no checklist returns <see langword="null"/> rather than a guess. Inventing an empty
    /// pending list for a job that never had tasks would send the run straight to the PR stage with nothing
    /// done, which is worse than starting discovery again.
    /// </para>
    /// </remarks>
    internal static JobExecutionContinuationState? BuildContinuation(
        ProjectJob job,
        IReadOnlyList<JobTimelineCard> cards,
        out string? resumedTitle,
        out int resumedNumber,
        out int totalTasks)
    {
        resumedTitle = null;
        resumedNumber = 0;
        totalTasks = 0;

        var tasks = ProjectJobsController.ParseTasks(job.TasksJson);
        if (tasks.Count == 0) return null;

        var firstOpen = tasks.FindIndex(t => !t.IsCompleted);
        var offset = firstOpen < 0 ? tasks.Count : firstOpen;
        totalTasks = tasks.Count;

        var pending = tasks.Skip(offset)
            .Select(t => new ProposedChecklistItem(
                t.Id.ToString(),
                t.Title,
                t.TargetFile ?? string.Empty,
                t.VerificationMethod ?? t.AcceptanceCriteria ?? string.Empty))
            .ToList();

        resumedTitle = pending.FirstOrDefault()?.Title;
        resumedNumber = offset + 1;

        var prCard = cards.LastOrDefault(c =>
            c.CardType == TimelineCardType.PrReview && !string.IsNullOrWhiteSpace(c.StructuredPayloadJson));

        var (prUrl, prNumber, headSha, round) = ReadPrCard(prCard);

        return new JobExecutionContinuationState(
            ReviewRound: round,
            InitCardId: cards.FirstOrDefault(c => c.CardType == TimelineCardType.JobInitialized)?.Id,
            PrCardId: prCard?.Id,
            ActivePrUrl: prUrl,
            ActivePrNumber: prNumber,
            CandidateHeadSha: headSha,
            CompletedTaskOffset: offset,
            PendingTasks: pending,
            RemediationContext: null);
    }

    /// <summary>Parses the fields a resumed run needs from the newest PR card.</summary>
    /// <remarks>
    /// Deliberately tolerant: these cards are written by the workflow, by the PR tool, and by older builds
    /// with different key sets, and a missing field must degrade to "the resumed run will find out for
    /// itself" rather than fail an operator's recovery. A card that cannot be parsed is treated as no card.
    /// </remarks>
    private static (string? Url, int? Number, string? Sha, int Round) ReadPrCard(JobTimelineCard? card)
    {
        if (card is null) return (null, null, null, 0);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(card.StructuredPayloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null, null, 0);

            return (
                String(root, "prUrl"),
                Int(root, "prNumber"),
                String(root, "headSha"),
                Int(root, "round") ?? 0);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null, null, 0);
        }
    }

    private static string? String(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    private static string? LatestPrUrl(IReadOnlyList<JobTimelineCard> cards) =>
        ReadPrCard(cards.LastOrDefault(c => c.CardType == TimelineCardType.PrReview)).Url;

    /// <summary>
    /// Rebuilds the workflow input exactly as <c>StartJob</c> builds it.
    /// </summary>
    /// <remarks>
    /// The workspace path and target branch have to match what the original run used: the persona's
    /// checkout is keyed off the job id, and a resumed run pointed at a different path would hydrate a
    /// second tree and lose the commits the abandoned turn left in the first.
    /// </remarks>
    private async Task<JobExecutionInput> BuildInputAsync(
        ProjectJob job,
        JobExecutionContinuationState? continuation,
        CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);

        var workspacePath = !string.IsNullOrWhiteSpace(job.WorkingDirectory)
            ? WorkspacePathResolver.Normalize(job.WorkingDirectory.Trim())
            : project?.GetResolvedWorkingDirectory() ?? $"/workspace/repos/{job.ProjectId}";

        var initialPrompt = !string.IsNullOrWhiteSpace(job.Description)
            ? $"{job.Title}\n\n{job.Description}"
            : job.Title;

        return new JobExecutionInput(
            JobId: job.Id.ToString(),
            AssignedAgentId: string.IsNullOrWhiteSpace(job.AssignedAgentId) ? "astra" : job.AssignedAgentId,
            ProjectId: job.ProjectId,
            WorkspacePath: workspacePath,
            InitialPrompt: initialPrompt,
            TargetBranch: !string.IsNullOrWhiteSpace(job.GitBranchName) ? job.GitBranchName : "main",
            ApprovalPolicy: job.ApprovalPolicy)
        {
            Continuation = continuation
        };
    }

    /// <summary>Clears the stall marker and says where the job picked up.</summary>
    /// <remarks>
    /// Overwriting the heartbeat is what makes the cockpit's recovery button disappear, so it has to happen
    /// in the same operation as the start. A resumed run that leaves a "Stalled:" heartbeat behind would
    /// invite a second recovery on top of a live job.
    /// </remarks>
    private async Task MarkRecoveredAsync(
        ProjectJob job,
        string? resumedTitle,
        int resumedNumber,
        int totalTasks,
        CancellationToken ct)
    {
        var live = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == job.Id, ct);
        if (live is null) return;

        var position = totalTasks > 0 ? $"task {resumedNumber}/{totalTasks}" : "discovery";
        live.ProgressHeartbeat = $"Recovering: resuming at {position}" +
                                 (string.IsNullOrWhiteSpace(resumedTitle) ? "." : $" - {resumedTitle}");
        live.UpdatedAt = DateTimeOffset.UtcNow;
        job.ProgressHeartbeat = live.ProgressHeartbeat;
        job.UpdatedAt = live.UpdatedAt;
        await db.SaveChangesAsync(ct);

        if (hubContext is not null)
        {
            await hubContext.Clients.All.SendAsync("ProgressHeartbeatUpdated", new
            {
                jobId = live.Id,
                progressHeartbeat = live.ProgressHeartbeat,
                currentTaskId = live.CurrentTaskId,
                lifecycleStatus = ProjectJobsController.DeriveLifecycleStatus(live)
            }, ct);
        }
    }

    private async Task AddRecoveryCardAsync(
        ProjectJob job,
        IReadOnlyList<AgentSession> settled,
        string workflowId,
        string outcome,
        string? resumedTitle,
        int resumedNumber,
        int totalTasks,
        string? prUrl,
        string? note,
        string caller,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            JobId = job.Id.ToString(),
            CardType = TimelineCardType.TurnInterrupted,
            Status = TimelineCardStatus.Resolved,
            Title = $"Recovered by {caller}: {outcome}",
            StructuredPayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                recoveredBy = caller,
                outcome,
                workflowId,
                settledSessions = settled.Select(s => s.Id.ToString()).ToList(),
                resumedAtTask = resumedTitle,
                resumedAtNumber = resumedNumber,
                totalTasks,
                prUrl,
                note,
                recoveredAt = now
            }),
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync(ct);

        if (hubContext is not null)
        {
            await hubContext.Clients.Group($"job-{job.Id}").SendAsync("TimelineCardAdded", new
            {
                jobId = job.Id,
                title = $"Recovered by {caller}: {outcome}",
                cardType = nameof(TimelineCardType.TurnInterrupted),
                recoveredAt = now
            }, ct);
        }
    }

    private static string BuildAbortReason(string? note, string caller) =>
        "Operator recovery: " +
        (string.IsNullOrWhiteSpace(note) ? "abort the stalled turn so the workflow can advance" : note.Trim()) +
        $" (requested by {caller})";

    private JobRecoveryResult Finish(
        ProjectJob job,
        IReadOnlyList<AgentSession> settled,
        JobRecoveryAction action,
        (string? Title, int Number, int Total)? resumed,
        string? prUrl,
        string message,
        string? workflowId = null) =>
        new(job.Id,
            workflowId,
            action,
            settled.Select(s => s.Id).ToList(),
            resumed?.Title,
            resumed?.Number ?? 0,
            resumed?.Total ?? 0,
            prUrl,
            message);
}
