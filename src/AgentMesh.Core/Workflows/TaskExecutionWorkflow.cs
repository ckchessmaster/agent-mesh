using AgentMesh.Core.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

/// <summary>
/// The macro task-execution workflow: one checklist task, driven turn by turn to a terminal state,
/// with durable pauses where a human owes the agent something.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this workflow does and does not do.</strong> It decides what happens <em>between</em>
/// turns. Everything that happens <em>inside</em> a turn — reconciliation, prompt assembly, N
/// inference steps, N tool calls, checkpointing — is inside the single
/// <c>RunAgentCodingTurn</c> activity. That boundary is the load-bearing design decision of this
/// feature, and it exists because of Temporal's history limit: a workflow records every activity in
/// its event history, and the defaults are 51,200 events and 50 MB. A coding session is easily
/// thousands of tool calls, so per-tool-call activities would produce a workflow that fails at the
/// moment of maximum invested work — after hours of agent time, with the history too large to
/// replay. One activity per turn keeps history growth proportional to turns, which is tens, not
/// thousands.
/// </para>
/// <para>
/// <strong>The durable pause is the point.</strong> <see cref="Workflow.WaitConditionAsync"/>
/// suspends the workflow server-side: no worker slot is held, no process is polled, no tokens are
/// spent. Before this, an agent that asked a question either hung its turn open or polled, and both
/// burn the thing the pause exists to save. The workflow's state survives the pause because it lives
/// in Temporal's history, not in this object's fields — the fields below are reconstructed by
/// replaying that history, which is why every one of them is assigned only from deterministic or
/// signal-driven code.
/// </para>
/// <para>
/// <strong>Determinism rules this class must not break</strong> (a violation does not fail at
/// compile time; it fails at replay time, in production, on a workflow that has been running for
/// days):
/// <list type="bullet">
/// <item><description>No <see cref="DateTimeOffset.UtcNow"/> — use <see cref="Workflow.UtcNow"/>.</description></item>
/// <item><description>No <see cref="Guid.NewGuid()"/> — use <see cref="Workflow.NewGuid"/>.</description></item>
/// <item><description>No <c>Random</c>, no filesystem, no network, no <c>DateTime</c> formatting of
/// culture-dependent values.</description></item>
/// <item><description>No iteration over a <see cref="Dictionary{TKey,TValue}"/> — insertion order is
/// not guaranteed to survive serialization.</description></item>
/// <item><description>No reading mutable state that a signal handler did not write.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Why this lives in Core and not in the Worker.</strong> The job spec places it at
/// <c>src/AgentMesh.Worker/Workflows/</c>, but the same spec's §E requires the API to hold a typed
/// handle (<c>GetWorkflowHandle&lt;…&gt;</c> + <c>SignalAsync(w =&gt; w.ReceiveChatMessageAsync(…))</c>)
/// and the .NET SDK resolves that from the concrete workflow class. The API cannot reference the Worker
/// (it is a host executable, and the reference would be a cycle), so a Worker-local workflow forces the
/// API to signal by string name — the exact silently-dropped-signal trap described below. Core is where
/// the sibling <see cref="AgentTaskWorkflow"/> already lives for the identical reason, and the workflow
/// body depends only on the activity <em>interfaces</em> in <c>AgentMesh.Core.Activities</c>, so nothing
/// here needs the Worker. The Worker still registers it.
/// </para>
/// <para>
/// <strong>Not the spec's <c>[WorkflowInterface]</c>.</strong> The job spec sketches this class with
/// <c>[WorkflowInterface]</c>/<c>[WorkflowMethod]</c> attributes and
/// <c>GetWorkflowHandle&lt;ITaskExecutionWorkflow&gt;(workflowId)</c>. Neither exists in the .NET SDK
/// (verified against 1.20.0 by reflection): its surface is <c>[Workflow]</c> on the class and
/// <c>[WorkflowRun]</c> on exactly one method, and <c>GetWorkflowHandle&lt;T&gt;</c> requires the
/// concrete workflow class, because the SDK derives the registered workflow name from the type it is
/// given. <see cref="ITaskExecutionWorkflow"/> is therefore a plain documentation interface, and the
/// API binds handles to <see cref="TaskExecutionWorkflow"/>. See <see cref="SignalNames"/> for the
/// related naming trap on signals.
/// </para>
/// </remarks>
[Workflow]
public class TaskExecutionWorkflow : ITaskExecutionWorkflow
{
    /// <summary>
    /// Ceiling on coding turns for one task.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop needs a bound. Without one, an agent that keeps reporting
    /// <see cref="TurnOutcomeStatus.Continue"/> — because it is stuck, because the environment note
    /// is misleading it, because the task is not actually well-formed — loops forever, and each turn
    /// is a real 2-hour activity against a real model. That is the exact idle-burn this job exists to
    /// remove, reintroduced as a livelock instead of a hang.
    /// </para>
    /// <para>
    /// 40 turns is deliberately generous: a legitimate multi-file feature with test-fix iterations
    /// can take twenty turns, and halting a productive agent is a worse outcome than letting an
    /// unproductive one run longer, because the productive one's work is saved and the unproductive
    /// one's is caught by the stuck-loop detector inside the turn activity. The ceiling is the backstop
    /// for the case that detector misses, and hitting it is reported as
    /// <see cref="TurnOutcomeStatus.StuckLoopDetected"/> so the operator sees a halt with a reason
    /// rather than a workflow that is still "Running" next month.
    /// </para>
    /// </remarks>
    public const int MaxTurns = 40;

    /// <summary>
    /// How long a durable pause waits before giving up.
    /// </summary>
    /// <remarks>
    /// Matches the precedent this repository already sets in <c>AgentTaskWorkflow</c>, which parks on
    /// a human approval for up to 7 days. A pause shorter than a long weekend would fail tasks over a
    /// reviewer's time off; unbounded is worse, because a workflow nobody is coming back for keeps its
    /// history open forever and reports "Waiting on input" indefinitely. On expiry the workflow halts
    /// with a reason instead of silently looping.
    /// </remarks>
    public static readonly TimeSpan MaxWait = TimeSpan.FromDays(7);

    /// <summary>
    /// Chat replies buffered but not yet consumed by a turn.
    /// </summary>
    /// <remarks>
    /// A <see cref="Queue{T}"/> rather than a single field because signals can arrive while a turn
    /// activity is still running — a human answering a previous question, or adding context
    /// unprompted. Collapsing them to "the latest message" would silently drop the earlier ones, and
    /// a dropped human reply is indistinguishable from being ignored.
    /// </remarks>
    private readonly Queue<ChatMessageSignal> _chatInbox = new();

    /// <summary>
    /// Message ids already consumed, for duplicate suppression.
    /// </summary>
    /// <remarks>
    /// Bounded because a workflow's memory is its history: an unbounded set growing with every
    /// message would be replayed on every workflow task, and a job with a long chat history would pay
    /// that cost forever to protect against a rare redelivery. Oldest entries are dropped first, so
    /// the window covers recent redelivery (the realistic case) and not a signal replayed months later.
    /// </remarks>
    private readonly Queue<string> _recentMessageIds = new();

    /// <summary>Most recent review decision awaiting consumption.</summary>
    private PrReviewSignal? _prReview;

    /// <summary>
    /// Chat signals dropped by the self-escalation check in <see cref="ReceiveChatMessageAsync"/>.
    /// </summary>
    /// <remarks>
    /// Reported through the status query because <c>AgentMesh.Core</c> deliberately carries no logging
    /// abstraction (it references only <c>Temporalio</c>), and a silent drop is indistinguishable from a
    /// signal that never arrived. See <see cref="TaskStatusView.IgnoredSelfSignalCount"/>.
    /// </remarks>
    private int _ignoredSelfSignals;

    /// <summary>
    /// Chat signals dropped by the third-party persona isolation check in <see cref="ReceiveChatMessageAsync"/>.
    /// Reported through the status query: see <see cref="TaskStatusView.IgnoredMisaddressedSignalCount"/>.
    /// </summary>
    private int _ignoredMisaddressedSignals;

    private string _currentStatus = "Initializing";
    private string? _awaitingFrom;

    /// <summary>
    /// The persona this workflow is driving, for the self-escalation check in
    /// <see cref="ReceiveChatMessageAsync"/>.
    /// </summary>
    /// <remarks>
    /// Captured from the run input rather than re-read, so it is part of the deterministic history and
    /// identical on replay. Normalized to lowercase at assignment so the comparison against a sender id
    /// (which arrives lowercased from the MCP tool but as-typed from the start endpoint) cannot be
    /// defeated by casing.
    /// </remarks>
    private string? _personaId;

    /// <summary>
    /// The driver agent id this run dispatches turns as, for the same self-check.
    /// </summary>
    /// <remarks>
    /// Needed because <c>post_job_message</c> resolves its sender as <c>personaId ?? agentId</c>
    /// (PlatformMcpTools.SenderIdentity), and the persona half is derived from the calling session. An
    /// out-of-band or header-only caller can therefore present just the agent id — which for the
    /// orchestrated agent is the same principal. Comparing the persona alone would let that message
    /// through and the agent could still answer its own escalation.
    /// </remarks>
    private string? _agentId;
    private DateTimeOffset? _awaitingSince;
    private int _turnCount;
    private TurnOutcomeStatus? _lastOutcome;
    private string? _activePrUrl;
    private int? _activePrNumber;
    private string? _turnSessionId;
    private bool _isTerminated;

    /// <summary>
    /// Drives one task to a terminal state.
    /// </summary>
    /// <remarks>
    /// Marked <c>[WorkflowRun]</c> rather than <c>[WorkflowMethod]</c> — the .NET SDK's spelling, and
    /// the only one that registers. The method name is irrelevant to the wire protocol (unlike signal
    /// and query names, it is not derived from the method), so <c>ExecuteTaskAsync</c> is kept for
    /// readability against the spec's naming.
    /// </remarks>
    [WorkflowRun]
    public async Task<TaskExecutionResult> ExecuteTaskAsync(TaskExecutionInput input)
    {
        _currentStatus = "Running";
        _turnSessionId = input.TurnSessionId;

        // Identity for the signal filters. Normalized here rather than at each comparison so the run's
        // view of "who am I" is fixed once, deterministically, from the input that was recorded in
        // history — a replay cannot disagree with the original execution about which sender ids to drop.
        _personaId = NormalizeIdentity(input.PersonaId);
        _agentId = NormalizeIdentity(input.AgentId);

        while (!_isTerminated)
        {
            if (_turnCount >= MaxTurns)
            {
                var reason =
                    $"Halted after {_turnCount} turns without reaching a terminal state. The task is " +
                    "not progressing; review the transcript for a repeated failure the turn-level " +
                    "stuck-loop detector did not catch.";
                _currentStatus = $"Halted: {reason}";
                _lastOutcome = TurnOutcomeStatus.StuckLoopDetected;

                return new TaskExecutionResult(false, input.TaskId, _activePrUrl, reason);
            }

            // Drain everything buffered while the previous turn ran. Combining rather than consuming
            // one-per-turn matters: three replies would otherwise cost three full turns, and the
            // agent would act on the first without knowing the other two had arrived.
            var pendingReply = DequeuePendingReplies();

            _turnCount++;
            _currentStatus = $"Turn {_turnCount}";

            TurnOutcome outcome;
            try
            {
                outcome = await ExecuteTurnActivityAsync(input, pendingReply);
            }
            catch (Exception ex) when (IsTurnHalt(ex))
            {
                // The turn activity reports a deliberate halt (Failed / StuckLoopDetected) as a
                // non-retryable ApplicationFailure so Temporal does not re-run the agent on a tree it
                // already modified. That arrives here as an ActivityFailureException wrapping an
                // ApplicationFailureException, and it is a verdict about the task, not a platform
                // fault — so it halts with the activity's own message rather than propagating.
                //
                // Anything else (a transient activity failure that exhausted its retries, a workflow
                // cancellation, a task queue that never got the activity) is deliberately NOT caught:
                // those must surface so the workflow itself fails visibly and Temporal's own retry and
                // alerting machinery applies. Swallowing them into a "task failed" result would make an
                // infrastructure outage indistinguishable from an agent that gave up.
                var reason = HaltReasonOf(ex);
                _currentStatus = $"Halted: {reason}";
                _lastOutcome = TurnOutcomeStatus.Failed;
                _isTerminated = true;

                return new TaskExecutionResult(false, input.TaskId, _activePrUrl, reason);
            }

            _lastOutcome = outcome.Status;

            switch (outcome.Status)
            {
                case TurnOutcomeStatus.Continue:
                    // Autonomous step completed. Loop continues; the pod stays warm and the next turn
                    // re-runs reconciliation, which is what catches a tree that changed mid-turn.
                    _currentStatus = $"Turn {_turnCount} complete; continuing";
                    break;

                case TurnOutcomeStatus.AwaitingInput:
                    _awaitingFrom = outcome.TargetPersona ?? "Operator";
                    _currentStatus = $"Waiting on input from {_awaitingFrom}";
                    _awaitingSince = Workflow.UtcNow;

                    // Durable suspension. Zero compute, zero tokens, no worker slot held.
                    var gotInput = await Workflow.WaitConditionAsync(
                        () => _chatInbox.Count > 0, MaxWait);

                    _awaitingSince = null;

                    if (!gotInput)
                    {
                        var timeoutReason =
                            $"No reply from {_awaitingFrom} within {MaxWait.TotalDays} days; halting.";
                        _currentStatus = $"Halted: {timeoutReason}";
                        _lastOutcome = TurnOutcomeStatus.Failed;
                        return new TaskExecutionResult(false, input.TaskId, _activePrUrl, timeoutReason);
                    }

                    _currentStatus = "Resuming after input";
                    break;

                case TurnOutcomeStatus.PrOpenedAwaitingReview:
                    _activePrUrl = outcome.PrUrl ?? _activePrUrl;
                    _activePrNumber = outcome.PrNumber ?? _activePrNumber;
                    _currentStatus = "Awaiting PR Review";
                    _awaitingSince = Workflow.UtcNow;

                    var gotReview = await Workflow.WaitConditionAsync(
                        () => _prReview != null, MaxWait);

                    _awaitingSince = null;

                    if (!gotReview)
                    {
                        var reviewTimeout =
                            $"No review on {_activePrUrl ?? "the open pull request"} within " +
                            $"{MaxWait.TotalDays} days; halting.";
                        _currentStatus = $"Halted: {reviewTimeout}";
                        _lastOutcome = TurnOutcomeStatus.Failed;
                        return new TaskExecutionResult(false, input.TaskId, _activePrUrl, reviewTimeout);
                    }

                    var review = _prReview!;
                    _prReview = null;

                    if (review.IsApproved)
                    {
                        _currentStatus = "Merging PR";

                        // The merge is its own activity rather than a step inside the next turn
                        // because it is a privileged, externally-authorized action: it happens once,
                        // it needs server-side credentials the agent never holds, and its failure must
                        // be visible as a distinct event in the history rather than buried in a turn's
                        // prose.
                        string? mergeResult;
                        try
                        {
                            mergeResult = await Workflow.ExecuteActivityAsync(
                                (IGitActivities act) => act.MergePullRequestAsync(_activePrUrl!),
                                new ActivityOptions
                                {
                                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                                    // Retry a merge that failed transiently (rate limit, 5xx). Safe
                                    // because the activity treats "already merged" as success, so a retry
                                    // after a timeout that GitHub actually honoured does not double-apply.
                                    RetryPolicy = new()
                                    {
                                        InitialInterval = TimeSpan.FromSeconds(5),
                                        MaximumInterval = TimeSpan.FromMinutes(1),
                                        MaximumAttempts = 3,
                                    },
                                });
                        }
                        catch (Exception ex) when (IsTurnHalt(ex))
                        {
                            // The same translation the turn activity gets, and for the same reason: the
                            // activity raises a rejection (merge conflict, head moved after approval, not
                            // mergeable, no PR URL) as a non-retryable ApplicationFailure precisely so
                            // Temporal does not hammer GitHub with a call that will never succeed, and its
                            // own comment states the workflow turns that into a halted task with the reason
                            // intact. Until now it did not — the call sat outside this filter, so the
                            // exception escaped the run and the workflow ended Failed with no result at all.
                            //
                            // The cost of that was not cosmetic. A crashed workflow writes no
                            // TaskExecutionResult and never reaches the _currentStatus assignment below, so
                            // the status endpoint kept serving "Merging PR" — the last value written before
                            // the crash — indefinitely. The rejection classes are exactly the ones a human
                            // can resolve (fix the conflict, re-review the new head), and the operator's
                            // only clue was a merge that appeared to be in progress forever.
                            //
                            // Reusing IsTurnHalt rather than writing a merge-specific filter is the point:
                            // it keeps transient failures (raised as ordinary exceptions, retryable) out of
                            // this branch so the RetryPolicy above still applies, and keeps a genuine
                            // workflow cancellation propagating so an operator's stop is not reported as an
                            // agent failure.
                            var reason = $"Approved merge could not complete: {HaltReasonOf(ex)}";
                            _currentStatus = $"Halted: {reason}";
                            _lastOutcome = TurnOutcomeStatus.Failed;
                            _isTerminated = true;

                            return new TaskExecutionResult(false, input.TaskId, _activePrUrl, reason);
                        }

                        _currentStatus = "Completed";
                        _isTerminated = true;

                        return new TaskExecutionResult(
                            true, input.TaskId, _activePrUrl,
                            mergeResult ?? "Pull request merged; task completed.");
                    }

                    _currentStatus = "Changes requested on PR; resuming coding turn";

                    // Review feedback enters through the same inbox as a chat reply rather than as a
                    // second channel the turn has to know to check. One path means the turn activity
                    // cannot forget to look at review comments, and the agent sees them in the same
                    // [INCOMING MESSAGE] position as any other reply.
                    _chatInbox.Enqueue(new ChatMessageSignal(
                        SenderId: string.IsNullOrWhiteSpace(review.ReviewerId) ? "reviewer" : review.ReviewerId,
                        Content: $"Review feedback on PR {_activePrUrl}:\n{review.ReviewComments ?? "(no comments provided)"}",
                        Timestamp: review.Timestamp,
                        // No MessageId: this is synthesized inside the workflow, so there is no
                        // persisted row to deduplicate against, and inventing one would put a fake
                        // identity into the dedup window.
                        MessageId: null));
                    break;

                case TurnOutcomeStatus.TaskCompleted:
                    _currentStatus = "Completed";
                    _isTerminated = true;

                    return new TaskExecutionResult(
                        true, input.TaskId, outcome.PrUrl ?? _activePrUrl,
                        outcome.Message ?? "Task completed and verified successfully.");

                case TurnOutcomeStatus.StuckLoopDetected:
                case TurnOutcomeStatus.Failed:
                    var haltReason = outcome.Message ?? "Task failed.";
                    _currentStatus = $"Halted: {haltReason}";
                    _isTerminated = true;

                    return new TaskExecutionResult(false, input.TaskId, _activePrUrl, haltReason);

                default:
                    // An outcome the workflow does not recognize means a newer worker wrote a status
                    // this build cannot interpret. Halting with that stated is the only safe move:
                    // falling through to `Continue` would keep executing turns against an unknown
                    // terminal condition, and the unknown ones are precisely the states that mean
                    // "stop".
                    var unknown = $"Unrecognized turn outcome '{outcome.Status}'; halting rather than guessing.";
                    _currentStatus = $"Halted: {unknown}";
                    _isTerminated = true;
                    return new TaskExecutionResult(false, input.TaskId, _activePrUrl, unknown);
            }
        }

        return new TaskExecutionResult(true, input.TaskId, _activePrUrl, "Task completed.");
    }

    /// <summary>
    /// Accepts a Job Chat reply, releasing an <c>AwaitingInput</c> pause.
    /// </summary>
    /// <remarks>
    /// Registered wire name: <c>ReceiveChatMessage</c>. The <c>Async</c> suffix is stripped by the SDK
    /// — see <see cref="SignalNames"/> for the silent-drop failure that follows from not knowing this.
    /// <para>
    /// Signal handlers must be deterministic and must not await anything that could reorder against
    /// another handler. Enqueueing is the whole job here, which is what makes the pause release safe:
    /// <see cref="Workflow.WaitConditionAsync"/> re-evaluates the predicate after each handler runs.
    /// </para>
    /// </remarks>
    [WorkflowSignal]
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        if (signal is null) return Task.CompletedTask;

        // Duplicate suppression. A redelivery is normal — the API retried a call that timed out
        // client-side but landed server-side — and a duplicated reply would be rendered into the
        // prompt twice, which reads to the model as the human repeating themselves and can change its
        // answer.
        if (IsDuplicate(signal.MessageId)) return Task.CompletedTask;

        // A persona must not be able to answer its own escalation.
        //
        // Without this, an agent that parked with requires_reply could release itself by posting a
        // second message to the job — the same self-approval class as the review-gate bypass fixed
        // elsewhere in this PR, reached from the other direction. The escalation exists to force a
        // decision from somebody else; letting the asker supply it turns the durable pause into a no-op
        // that costs a turn instead of a pod.
        //
        // This is deliberately the ONLY filter on the gate. The tempting stronger rule — "release only
        // for the party named in _awaitingFrom" — cannot be enforced from a sender id, and I wrote it
        // here before measuring why it must not be:
        //
        //   The REST router stamps a human reply's SenderId from whatever the caller supplied
        //   (ProjectJobsController: `req.SenderId ?? "operator"`), and a persona's from its persona id.
        //   A human posting as "sorren" and a persona named "sorren" therefore arrive as the identical
        //   string. So an awaited-party gate would (a) lock the operator out of answering an escalation
        //   their own agent raised — the exact deadlock this change exists to remove, reintroduced in a
        //   new form — and (b) be trivially bypassable by a persona that happens to share a name.
        //
        // Distinguishing them needs the sender's *kind* on the wire, not its id. That is a change to
        // ChatMessageSignal, which is a workflow wire contract; flagged for @Architect rather than
        // invented here. Until then the self-check is the property that is actually enforceable, and
        // everything else is left to the turn, which reads the full job ledger anyway.
        if (IsSelf(signal.SenderId))
        {
            // Counted rather than logged: see IgnoredSelfSignalCount. A drop with no observable trace
            // would leave an operator staring at a task that will not resume, with nothing to point at.
            _ignoredSelfSignals++;
            return Task.CompletedTask;
        }

        // A message addressed to a third-party persona on the job must not release this run's wait gate.
        if (!IsAddressedToThisRun(signal.TargetPersonaId))
        {
            _ignoredMisaddressedSignals++;
            return Task.CompletedTask;
        }

        _chatInbox.Enqueue(signal);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether an incoming chat signal is addressed to this workflow run.
    /// </summary>
    /// <remarks>
    /// A message addressed explicitly to another persona or to "operator" (via REST @mentions or MCP target_persona)
    /// must not release this run's wait condition. Only unaddressed messages (TargetPersonaId is null)
    /// and messages explicitly mentioning this persona or its driver agent are accepted.
    /// </remarks>
    private bool IsAddressedToThisRun(string? targetPersonaId)
    {
        if (string.IsNullOrWhiteSpace(targetPersonaId))
        {
            return true;
        }

        var targets = targetPersonaId.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (targets.Length == 0)
        {
            return true;
        }

        foreach (var rawTarget in targets)
        {
            var target = NormalizeIdentity(rawTarget);
            if (target is null) continue;

            if (string.Equals(target, _personaId, StringComparison.Ordinal) ||
                string.Equals(target, _agentId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a sender is the persona this workflow is driving.</summary>
    /// <remarks>
    /// Case-insensitive because persona ids arrive lowercased from the MCP tool's
    /// <c>Trim().TrimStart('@').ToLowerInvariant()</c> and from the REST mentions regex, but the workflow
    /// input is whatever the operator posted to the start endpoint. A case-sensitive comparison would
    /// silently disable the self-check for a starter who typed "Rook".
    /// </remarks>
    private bool IsSelf(string? senderId)
    {
        var sender = NormalizeIdentity(senderId);
        if (sender is null) return false;

        return string.Equals(sender, _personaId, StringComparison.Ordinal) ||
               string.Equals(sender, _agentId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonical form for identity comparisons: trimmed, leading '@' removed, lowercased.
    /// </summary>
    /// <remarks>
    /// Applied to both sides so the comparison is exact rather than fuzzy. The three components that
    /// produce these ids normalize differently — the MCP tool lowercases, the REST mentions regex
    /// lowercases, and the start endpoint takes whatever the operator posted — so normalizing only the
    /// incoming sender would leave the stored value as-typed and re-open the hole for a starter who typed
    /// "Rook". Null when blank, so an unset identity never matches a blank sender.
    /// </remarks>
    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>
    /// Accepts a review decision, releasing a PR-review pause.
    /// </summary>
    /// <remarks>
    /// Registered wire name: <c>ReceivePrReviewSignal</c>. Last decision wins rather than queueing: a
    /// reviewer who changes their mind after requesting changes is genuinely redirecting the work, and
    /// the alternative (consuming an obsolete rejection before seeing the approval) would burn a full
    /// coding turn on feedback the reviewer already withdrew.
    /// </remarks>
    [WorkflowSignal]
    public Task ReceivePrReviewSignalAsync(PrReviewSignal signal)
    {
        if (signal is null) return Task.CompletedTask;

        _prReview = signal;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The workflow's observable state, for the dashboard and for tests.
    /// </summary>
    /// <remarks>
    /// Read-only by contract: a query handler that mutates state is a determinism violation, because
    /// queries run during replay and would then apply the mutation twice.
    /// </remarks>
    [WorkflowQuery]
    public TaskStatusView GetTaskStatus() => new(
        Status: _currentStatus,
        AwaitingSince: _awaitingSince,
        TurnCount: _turnCount,
        LastOutcome: _lastOutcome,
        PendingChatCount: _chatInbox.Count,
        PrUrl: _activePrUrl,
        TurnSessionId: _turnSessionId,
        IgnoredSelfSignalCount: _ignoredSelfSignals,
        IgnoredMisaddressedSignalCount: _ignoredMisaddressedSignals,
        PersonaId: _personaId);

    /// <summary>
    /// Issues the macro turn activity. Split out so the try/catch around it reads as one decision
    /// rather than a try block wrapped around a hundred lines of switch.
    /// </summary>
    private Task<TurnOutcome> ExecuteTurnActivityAsync(
        TaskExecutionInput input,
        ChatMessageSignal? pendingReply) =>
        Workflow.ExecuteActivityAsync(
            (ITaskExecutionActivities act) => act.RunAgentCodingTurnAsync(input, pendingReply),
            new ActivityOptions
            {
                // 2 hours: a turn is the whole local loop, and a long debugging session with a
                // build-and-test cycle legitimately takes this long.
                StartToCloseTimeout = TimeSpan.FromHours(2),
                // 2 minutes: the heartbeat is what distinguishes "the agent is thinking" from "the
                // worker died holding this activity". The turn runner heartbeats on every poll, so
                // this fires only when the worker is genuinely gone.
                //
                // Verified against a live test server: an activity that declares HeartbeatTimeout and
                // then never heartbeats is killed at the deadline, not left to run. The timeout is
                // therefore load-bearing on the runner actually heartbeating — which is why
                // HttpAgentTurnRunner does so from its poll loop rather than treating it as optional.
                HeartbeatTimeout = TimeSpan.FromMinutes(2),
                // No RetryPolicy override: the default retries a *transient* failure (worker crash,
                // network) which is correct, and does not retry a non-retryable ApplicationFailure,
                // which is how the turn activity reports a deliberate halt. Retrying a turn would
                // re-run the agent on a tree it already modified.
                Summary = $"agent coding turn {_turnCount} for task {input.TaskId}",
            });

    /// <summary>
    /// True when an exception from the turn activity is a verdict about the task rather than a fault
    /// in the platform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction is what keeps a database blip from closing a task. A transient activity failure
    /// exhausts its retries and surfaces here too, and if this predicate matched on "any exception from
    /// the activity" then every outage would produce a <c>Success=false</c> workflow result — a
    /// completed-looking workflow that an operator has to read the history of to understand. Letting
    /// those propagate leaves the workflow failed, which is the honest state and the one Temporal
    /// alerts on.
    /// </para>
    /// <para>
    /// Matching is on the exception type and the <c>nonRetryable</c> flag rather than on the message
    /// text. String matching on an error message is the classic version of this bug: it works until
    /// someone rewords a sentence, and then it silently starts classifying halts as outages.
    /// </para>
    /// </remarks>
    private static bool IsTurnHalt(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is ApplicationFailureException { NonRetryable: true })
            {
                return true;
            }

            // A cancellation is not a halt: it means the workflow itself was asked to stop, and
            // converting that into a returned result would report a task as "failed by the agent" when
            // an operator cancelled it. Re-thrown so the workflow ends as Cancelled.
            if (ex is CanceledFailureException or OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The halt reason to report, preferring the turn activity's own message.
    /// </summary>
    /// <remarks>
    /// The innermost non-retryable failure's message is the one the turn activity wrote ("3-strike
    /// limit reached on test X"), which is actionable. The outer wrappers are Temporal's boilerplate
    /// ("activity call failed"), which is not.
    /// </remarks>
    private static string HaltReasonOf(Exception exception)
    {
        string? reason = null;

        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is ApplicationFailureException { NonRetryable: true } &&
                !string.IsNullOrWhiteSpace(ex.Message))
            {
                reason = ex.Message;
            }
        }

        return reason ?? exception.Message;
    }

    /// <summary>
    /// Combines every buffered reply into the single message handed to the next turn.
    /// </summary>
    /// <remarks>
    /// Returns null when nothing is pending, which is what distinguishes an autonomous step from a
    /// resumed one in the assembled prompt. Joined oldest-first so the conversation reads in order;
    /// reversing it would put the human's follow-up before the question it follows.
    /// </remarks>
    private ChatMessageSignal? DequeuePendingReplies()
    {
        if (_chatInbox.Count == 0) return null;

        var messages = new List<ChatMessageSignal>();
        while (_chatInbox.Count > 0)
        {
            var next = _chatInbox.Dequeue();
            messages.Add(next);
            RememberMessageId(next.MessageId);
        }

        if (messages.Count == 1) return messages[0];

        var newest = messages[^1];
        var combined = string.Join(
            "\n\n",
            messages.Select((m, i) =>
                $"[{i + 1} of {messages.Count}] {m.SenderId} @ {m.Timestamp.ToUniversalTime():O}:\n{m.Content}"));

        // The newest id is carried forward so the combined message still occupies the dedup window;
        // the earlier ids were already remembered individually by the loop above.
        return newest with { Content = combined };
    }

    private bool IsDuplicate(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return false;

        var trimmed = messageId.Trim();
        return _recentMessageIds.Contains(trimmed) ||
               _chatInbox.Any(m => string.Equals(m.MessageId, trimmed, StringComparison.Ordinal));
    }

    /// <summary>Window of message ids kept for redelivery suppression.</summary>
    /// <remarks>
    /// 200 is far above any plausible burst of redeliveries and small enough that replaying it costs
    /// nothing measurable per workflow task.
    /// </remarks>
    private const int DedupWindow = 200;

    private void RememberMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;

        _recentMessageIds.Enqueue(messageId.Trim());
        while (_recentMessageIds.Count > DedupWindow)
        {
            _recentMessageIds.Dequeue();
        }
    }

}
