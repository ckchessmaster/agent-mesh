using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentMesh.Worker.Activities;

/// <summary>
/// The macro coding-turn activity: pre-turn hooks, the agent's own loop, post-turn checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// Partial class over <see cref="ExecutionActivities"/> so Job A's shell activity and Job D's turn
/// activity share one registered activity type. The worker registers the class once
/// (<c>AddScopedActivities&lt;ExecutionActivities&gt;()</c>), so splitting the file does not split the
/// registration — and keeping them in one type is what guarantees the turn's internal shell calls and
/// the workflow's direct <c>ExecuteCommand</c> calls resolve the working directory by identical rules.
/// </para>
/// <para>
/// <strong>Why a halted turn is an exception and a transient one is too, but for different
/// reasons.</strong> The workflow branches on <see cref="TurnOutcome.Status"/>, and
/// <see cref="TurnOutcomeStatus.Failed"/>/<see cref="TurnOutcomeStatus.StuckLoopDetected"/> are
/// terminal states it halts on. So the return channel carries the four non-terminal statuses plus
/// <see cref="TurnOutcomeStatus.TaskCompleted"/>; a genuine halt is raised as a non-retryable
/// <see cref="ApplicationFailureException"/> so it reaches the workflow exactly once instead of being
/// re-run by the default retry policy, which would re-execute the agent on a tree it already modified.
/// An infrastructure failure (assembly, dispatch) propagates as an ordinary exception and <em>is</em>
/// retried, because a database blip must not end a task.
/// </para>
/// </remarks>
public partial class ExecutionActivities
{
    /// <summary>
    /// Runs one coding turn for a task.
    /// </summary>
    /// <remarks>
    /// Registered name: <c>RunAgentCodingTurn</c> (the SDK strips <c>Async</c>).
    /// <para>
    /// The activity re-derives everything each time it runs — reconciliation, checkpoint, memories —
    /// so a retry after a worker crash starts from what is true on disk now rather than from what the
    /// dead attempt believed. What a retry cannot undo is the agent's own edits, which is why the turn
    /// runner re-attaches to an existing turn session instead of dispatching a second agent onto the
    /// same task.
    /// </para>
    /// </remarks>
    /// <param name="input">The task being worked.</param>
    /// <param name="pendingReply">
    /// The reply or review comment that unblocked this turn, or null on an autonomous step.
    /// </param>
    [Activity]
    public async Task<TurnOutcome> RunAgentCodingTurnAsync(
        TaskExecutionInput input,
        ChatMessageSignal? pendingReply)
    {
        // Read from the activity context rather than a parameter: see the remark on the interface
        // declaration. HasCurrent is false when a test calls this method directly, and the token
        // defaults to "never cancelled", which is the right behaviour for a direct call.
        var cancellationToken = ActivityExecutionContext.HasCurrent
            ? ActivityExecutionContext.Current.CancellationToken
            : default;

        if (input is null)
        {
            // A null input means the workflow was started with a payload this build cannot bind — a
            // deployment error, not a task failure. Throwing keeps it out of the "the agent failed"
            // channel and in the "the platform is broken" one.
            throw new ArgumentNullException(nameof(input));
        }

        logger.LogInformation(
            "Macro turn starting: job={JobId} task={TaskId} agent={AgentId} workspace={Workspace} pendingReply={HasReply}",
            input.JobId, input.TaskId, input.AgentId, input.WorkspacePath, pendingReply is not null);

        // ── Pre-turn hooks ───────────────────────────────────────────────────────
        // Outside the runner's try/catch below: if assembly itself fails there is no turn to report
        // on, and the right answer is for the activity to fail so Temporal retries it. Returning
        // Failed would halt the task on a transient outage.
        var context = await ResolveAssembler().AssembleAsync(input, pendingReply, cancellationToken);

        logger.LogInformation(
            "Turn context assembled for task {TaskId}: {Chars} chars, {History} history message(s), " +
            "{Memories} memories, environment note {NoteState}, checkpoint {CheckpointState}",
            input.TaskId,
            context.Prompt.Length,
            context.HistoryMessageCount,
            context.MemoryCount,
            context.EnvironmentNote is null ? "omitted" : "present",
            context.Checkpoint is null ? "none" : "loaded");

        var request = new AgentTurnRequest(
            Input: input,
            Prompt: context.Prompt,
            WorkspacePath: context.WorkspacePath,
            PersonaId: input.PersonaId,
            AgentId: input.AgentId,
            TurnSessionId: input.TurnSessionId);

        // ── Execution ────────────────────────────────────────────────────────────
        TurnOutcome outcome;
        try
        {
            outcome = await ResolveTurnRunner().RunTurnAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The workflow told this activity to stop. Re-throwing is the only correct move: catching
            // it and returning an outcome would let the workflow record a completed turn for work that
            // was aborted mid-flight, and the checkpoint below would then be written from a
            // half-finished tree.
            logger.LogInformation("Turn cancelled for task {TaskId}; no checkpoint written", input.TaskId);
            throw;
        }

        // ── Post-turn hook ───────────────────────────────────────────────────────
        // Runs for every outcome, including the halting ones. A turn that ended in
        // StuckLoopDetected is precisely the state an operator needs to see next: the dirty tree, the
        // stash, and the summary saying where it got stuck. Skipping the checkpoint on failure would
        // erase the evidence the halt is being reported for.
        await WriteCheckpointAsync(input, context, outcome, cancellationToken);

        logger.LogInformation(
            "Macro turn finished: job={JobId} task={TaskId} outcome={Outcome} target={Target}",
            input.JobId, input.TaskId, outcome.Status, outcome.TargetPersona ?? "(none)");

        // A halt has to survive the trip to the workflow without being retried, and Temporal decides
        // that from the exception type rather than from a return value. Non-retryable is the whole
        // point: the default policy would otherwise re-run the agent on the tree it just failed on.
        if (outcome.Status is TurnOutcomeStatus.Failed or TurnOutcomeStatus.StuckLoopDetected)
        {
            throw new ApplicationFailureException(
                outcome.Message ?? $"Turn halted with status {outcome.Status}.",
                errorType: outcome.Status.ToString(),
                nonRetryable: true);
        }

        return outcome;
    }

    /// <summary>
    /// Records what the turn left behind, so the next turn (or the next pod) starts informed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the branch and file list come from a fresh probe rather than from
    /// <see cref="AssembledTurnContext.EnvironmentNote"/>.</strong> The note was taken <em>before</em>
    /// the turn ran. After a turn that edited files and committed them, the pre-turn state is not
    /// merely stale, it is actively wrong: it would report the files the agent just committed as still
    /// uncommitted, and the next turn would open with a warning not to reset work that is already safe.
    /// Re-probing costs one bounded shell call per turn and buys a checkpoint that is true.
    /// </para>
    /// <para>
    /// A failed probe degrades to carrying the pre-turn facts forward rather than writing nothing.
    /// Fields that cannot be justified stay null, which the note renderer already renders as "unknown"
    /// rather than as "clean" — the distinction that stops an agent from discarding work.
    /// </para>
    /// </remarks>
    private async Task WriteCheckpointAsync(
        TaskExecutionInput input,
        AssembledTurnContext context,
        TurnOutcome outcome,
        CancellationToken ct)
    {
        try
        {
            var postTurn = await ResolveReconciler().ReconcileAsync(context.WorkspacePath, ct);
            var report = postTurn.Inspected ? postTurn : null;

            // Branch: live probe, then the pre-turn note, then the workflow's target branch. The last
            // fallback states intent rather than observation, so it is used only when nothing was
            // measured — and it beats an empty branch, which the note renderer reports as "unknown".
            var branch = report?.Branch
                         ?? context.Checkpoint?.ActiveBranch
                         ?? input.TargetBranch;

            var checkpoint = new TaskWorkingCheckpoint(
                JobId: input.JobId,
                TaskId: input.TaskId,
                ActiveBranch: branch ?? string.Empty,
                Status: MapStatus(outcome),
                LastSummary: BuildSummary(outcome, context),
                ModifiedFiles: report?.FilesOrEmpty ?? context.Checkpoint?.ModifiedFiles ?? [],
                ActiveStash: report?.StashesOrEmpty.FirstOrDefault() ?? context.Checkpoint?.ActiveStash,
                UpdatedAt: DateTimeOffset.UtcNow,
                HeadCommit: report?.HeadCommitOneline ?? context.Checkpoint?.HeadCommit,
                ChecklistFocus: context.Checkpoint?.ChecklistFocus,
                TurnSessionId: input.TurnSessionId ?? context.Checkpoint?.TurnSessionId);

            var saved = await ResolveCheckpoints().SaveCheckpointAsync(checkpoint, ct);

            if (!saved)
            {
                // Logged, not thrown: the turn already did its work and the outcome is about to reach
                // the workflow. Failing here would make Temporal retry a turn that succeeded,
                // re-running the agent on a tree it already modified — trading a missing checkpoint for
                // duplicated work.
                logger.LogWarning(
                    "Checkpoint was not persisted for job {JobId} task {TaskId}; the next turn will " +
                    "re-derive its state from the environment probe alone",
                    input.JobId, input.TaskId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Checkpoint write failed for job {JobId} task {TaskId}", input.JobId, input.TaskId);
        }
    }

    /// <summary>
    /// Maps a turn outcome onto the checklist vocabulary the checkpoint stores.
    /// </summary>
    /// <remarks>
    /// <see cref="TurnOutcomeStatus.PrOpenedAwaitingReview"/> maps to
    /// <see cref="JobTaskStatus.AwaitingReview"/> because that is the state the platform's own
    /// <c>manage_task</c> tool writes when an agent opens a PR. If the two disagreed, the checkpoint
    /// and the checklist would describe the same task differently, and the agent resolves that
    /// contradiction by re-running <c>manage_task list_all</c> — the exact behaviour this feature
    /// exists to remove.
    /// <para>
    /// A halt maps to <c>in_progress</c> rather than to a new status: the task genuinely is unfinished
    /// and blocked, and inventing a fifth checklist value would break every consumer that switches on
    /// the four the Cockpit already draws.
    /// </para>
    /// </remarks>
    internal static string MapStatus(TurnOutcome outcome) => outcome.Status switch
    {
        TurnOutcomeStatus.TaskCompleted => JobTaskStatus.Completed,
        TurnOutcomeStatus.PrOpenedAwaitingReview => JobTaskStatus.AwaitingReview,
        _ => JobTaskStatus.InProgress,
    };

    /// <summary>
    /// The checkpoint's summary line: what the turn concluded, plus the state the outcome names.
    /// </summary>
    /// <remarks>
    /// Bounded below <c>checkpoint_summary</c>'s 4096-char column so the truncation happens here, where
    /// the marker says what was dropped, rather than in the repository where it would be an ellipsis
    /// with no explanation.
    /// </remarks>
    internal static string BuildSummary(TurnOutcome outcome, AssembledTurnContext context)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(outcome.Message)) parts.Add(outcome.Message!.Trim());
        if (!string.IsNullOrWhiteSpace(outcome.PrUrl)) parts.Add($"PR: {outcome.PrUrl.Trim()}");

        if (outcome.Status == TurnOutcomeStatus.AwaitingInput)
        {
            parts.Add(string.IsNullOrWhiteSpace(outcome.TargetPersona)
                ? "Blocked: waiting for a reply in Job Chat."
                : $"Blocked: waiting for a reply from @{outcome.TargetPersona.Trim()} in Job Chat.");
        }

        // The environment note is the durable record of what the workspace looked like when this turn
        // started. Keeping it means a later reader can tell "the tree was already dirty when this turn
        // began" from "this turn made it dirty" — the distinction that decides whether a stray
        // uncommitted file is this task's work or its predecessor's.
        if (!string.IsNullOrWhiteSpace(context.EnvironmentNote))
        {
            parts.Add("Workspace at turn start: " + context.EnvironmentNote!.Replace('\n', ' '));
        }

        var summary = parts.Count == 0
            ? $"Turn ended with outcome {outcome.Status}."
            : string.Join(" | ", parts);

        return summary.Length <= MaxSummaryChars ? summary : summary[..MaxSummaryChars] + "…";
    }

    /// <summary>Cap applied before the repository's own column-width truncation.</summary>
    internal const int MaxSummaryChars = 4_000;

    // ──────────────────────────────────────────────────────────────────────────────
    // Dependency resolution
    //
    // ExecutionActivities is constructed three ways: by the worker host with the full set, by Job A's
    // tests with only a logger (which never invoke the turn activity), and by any host that registers
    // just the shell activity. These accessors state the requirement once per dependency instead of
    // null-checking at each use site, and each message names the interface to register — because the
    // failure mode they prevent is a turn that silently does nothing, which looks to an operator like
    // an agent that is simply not making progress.
    // ──────────────────────────────────────────────────────────────────────────────

    private ITurnContextAssembler ResolveAssembler() =>
        assembler ?? throw new InvalidOperationException(
            $"No {nameof(ITurnContextAssembler)} is registered on this worker, so a macro turn has no " +
            "prompt to dispatch. Register the worker's database (connection string 'agentdb') so " +
            $"{nameof(TurnContextAssembler)} can be resolved.");

    private IAgentTurnRunner ResolveTurnRunner() =>
        turnRunner ?? throw new InvalidOperationException(
            $"No {nameof(IAgentTurnRunner)} is registered on this worker. The worker holds no LLM " +
            "client by design; turns are dispatched through the platform. Register " +
            "an IAgentTurnRunner implementation (the worker registers the platform dispatch runner) " +
            "to execute turns.");

    /// <remarks>
    /// Built per call rather than cached: the worker registers this type with
    /// <c>AddScopedActivities</c>, so a fresh instance already exists for every activity task and a
    /// cache field would live exactly as long as the object holding it.
    /// </remarks>
    private IWorkspaceReconciler ResolveReconciler() =>
        reconciler ?? new ShellWorkspaceReconciler(
            log: message => logger.LogWarning("{Message}", message));

    private IWorkspaceStateRepository ResolveCheckpoints() =>
        checkpoints ?? throw new InvalidOperationException(
            $"No {nameof(IWorkspaceStateRepository)} is registered, so no checkpoint can be written " +
            "and context recovery cannot work. The worker needs its database connection string.");

    // The four dependencies arrive as optional constructor parameters on the other partial
    // (assembler / checkpoints / turnRunner / reconciler) and are read through the primary-constructor
    // parameters directly; the accessors below keep the null-handling in one place per dependency.
}