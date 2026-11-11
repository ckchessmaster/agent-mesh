using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[Workflow("TaskExecutionWorkflow")]
public class TaskExecutionWorkflow : ITaskExecutionWorkflow
{
    public const int MaxTurns = 40;

    public static readonly TimeSpan MaxWait = TimeSpan.FromDays(7);

    private static readonly ActivityOptions TimelineActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 3
        }
    };

    private readonly Queue<ChatMessageSignal> _chatInbox = new();
    private readonly Queue<string> _recentMessageIds = new();
    private PrReviewSignal? _prReview;
    private int _ignoredSelfSignals;
    private int _ignoredMisaddressedSignals;
    private string _currentStatus = "Initializing";
    private string? _awaitingFrom;
    private string? _personaId;
    private string? _agentId;
    private DateTimeOffset? _awaitingSince;
    private int _turnCount;
    private TurnOutcomeStatus? _lastOutcome;
    private string? _activePrUrl;
    private int? _activePrNumber;
    private string? _turnSessionId;
    private bool _isTerminated;

    private AbortActiveTurnSignal? _abortSignal;
    private TurnInterventionSignal? _intervention;
    private CancellationTokenSource? _activeTurnCts;

    [WorkflowRun]
    public async Task<TaskExecutionResult> ExecuteTaskAsync(TaskExecutionInput input)
    {
        _currentStatus = "Running";
        _turnSessionId = input.TurnSessionId;

        _personaId = NormalizeIdentity(input.PersonaId);
        _agentId = NormalizeIdentity(input.AgentId);

        var isTimelineTask = input.TaskIndex.HasValue;

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

            var pendingReply = DequeuePendingReplies();

            _turnCount++;
            _currentStatus = $"Turn {_turnCount}";

            if (isTimelineTask)
            {
                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.UpdateTaskStepCardAsync(
                        new TaskStepCardInput(
                            input.JobId,
                            input.TaskId,
                            input.TaskIndex!.Value,
                            input.TaskTitle ?? input.TaskId,
                            TimelineCardStatus.Active,
                            $"Step {_turnCount}: In progress",
                            null)),
                    TimelineActivityOptions);
            }

            TurnOutcome outcome;
            using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(Workflow.CancellationToken);
            _activeTurnCts = turnCancellation;

            if (_abortSignal != null)
            {
                turnCancellation.Cancel();
            }

            try
            {
                outcome = await ExecuteTurnActivityAsync(input, pendingReply, turnCancellation.Token);
                _activeTurnCts = null;
            }
            catch (Exception ex) when (_abortSignal != null && IsActivityCancellation(ex))
            {
                _activeTurnCts = null;
                var reason = _abortSignal.Reason ?? "Active turn stopped by operator";
                var requestedBy = _abortSignal.RequestedBy ?? "operator";
                _abortSignal = null;

                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.RecordTurnInterruptedAsync(
                        new TurnInterruptedCardInput(input.JobId, input.TaskId, reason, requestedBy)),
                    TimelineActivityOptions);

                _intervention = null;
                _currentStatus = $"Interrupted: {reason}";
                await Workflow.WaitConditionAsync(() => _intervention != null);

                var intervention = _intervention!;
                _intervention = null;

                if (string.Equals(intervention.Action, "realign", StringComparison.OrdinalIgnoreCase))
                {
                    _isTerminated = true;
                    var realignMsg = $"Realigned: {intervention.AdditionalContext}";
                    return new TaskExecutionResult(false, realignMsg, input.TaskId, _activePrUrl, realignMsg);
                }

                if (!string.IsNullOrWhiteSpace(intervention.AdditionalContext))
                {
                    _chatInbox.Enqueue(new ChatMessageSignal(
                        SenderId: "operator",
                        Content: intervention.AdditionalContext,
                        Timestamp: Workflow.UtcNow));
                }

                continue;
            }
            catch (Exception ex) when (IsTurnHalt(ex))
            {
                _activeTurnCts = null;
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
                    _currentStatus = $"Turn {_turnCount} complete; continuing";
                    break;

                case TurnOutcomeStatus.AwaitingInput:
                    _awaitingFrom = outcome.TargetPersona ?? "Operator";
                    _currentStatus = $"Waiting on input from {_awaitingFrom}";
                    _awaitingSince = Workflow.UtcNow;

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

                        string? mergeResult;
                        try
                        {
                            mergeResult = await Workflow.ExecuteActivityAsync(
                                (IGitActivities act) => act.MergePullRequestAsync(_activePrUrl!),
                                new ActivityOptions
                                {
                                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
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

                    _chatInbox.Enqueue(new ChatMessageSignal(
                        SenderId: string.IsNullOrWhiteSpace(review.ReviewerId) ? "reviewer" : review.ReviewerId,
                        Content: $"Review feedback on PR {_activePrUrl}:\n{review.ReviewComments ?? "(no comments provided)"}",
                        Timestamp: review.Timestamp,
                        MessageId: null));
                    break;

                case TurnOutcomeStatus.TaskCompleted:
                    if (isTimelineTask)
                    {
                        await Workflow.ExecuteActivityAsync(
                            (ITimelineActivities act) => act.UpdateTaskStepCardAsync(
                                new TaskStepCardInput(
                                    input.JobId,
                                    input.TaskId,
                                    input.TaskIndex!.Value,
                                    input.TaskTitle ?? input.TaskId,
                                    TimelineCardStatus.Resolved,
                                    $"Step {_turnCount}: Completed",
                                    outcome.Message)),
                            TimelineActivityOptions);
                    }

                    _currentStatus = "Completed";
                    _isTerminated = true;

                    return new TaskExecutionResult(
                        true, input.TaskId, outcome.PrUrl ?? _activePrUrl,
                        outcome.Message ?? "Task completed and verified successfully.");

                case TurnOutcomeStatus.StuckLoopDetected:
                case TurnOutcomeStatus.Failed:
                    if (isTimelineTask)
                    {
                        await Workflow.ExecuteActivityAsync(
                            (ITimelineActivities act) => act.RecordTurnInterruptedAsync(
                                new TurnInterruptedCardInput(input.JobId, input.TaskId, outcome.Message ?? "Turn failed", "system")),
                            TimelineActivityOptions);

                        _intervention = null;
                        await Workflow.WaitConditionAsync(() => _intervention != null);

                        var intervention = _intervention!;
                        _intervention = null;

                        if (string.Equals(intervention.Action, "realign", StringComparison.OrdinalIgnoreCase))
                        {
                            _isTerminated = true;
                            var realignMsg = $"Realigned: {intervention.AdditionalContext}";
                            return new TaskExecutionResult(false, realignMsg, input.TaskId, _activePrUrl, realignMsg);
                        }

                        if (!string.IsNullOrWhiteSpace(intervention.AdditionalContext))
                        {
                            _chatInbox.Enqueue(new ChatMessageSignal(
                                SenderId: "operator",
                                Content: intervention.AdditionalContext,
                                Timestamp: Workflow.UtcNow));
                        }

                        break;
                    }

                    var haltReason = outcome.Message ?? "Task failed.";
                    _currentStatus = $"Halted: {haltReason}";
                    _isTerminated = true;

                    return new TaskExecutionResult(false, input.TaskId, _activePrUrl, haltReason);

                default:
                    var unknown = $"Unrecognized turn outcome '{outcome.Status}'; halting rather than guessing.";
                    _currentStatus = $"Halted: {unknown}";
                    _isTerminated = true;
                    return new TaskExecutionResult(false, input.TaskId, _activePrUrl, unknown);
            }
        }

        return new TaskExecutionResult(true, input.TaskId, _activePrUrl, "Task completed.");
    }

    [WorkflowSignal]
    public Task AbortTurnAsync(AbortActiveTurnSignal signal)
    {
        _abortSignal = signal;
        _activeTurnCts?.Cancel();
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task InterveneTurnAsync(TurnInterventionSignal signal)
    {
        _intervention = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        if (signal is null) return Task.CompletedTask;
        if (IsDuplicate(signal.MessageId)) return Task.CompletedTask;

        if (IsSelf(signal.SenderId))
        {
            _ignoredSelfSignals++;
            return Task.CompletedTask;
        }

        if (!IsAddressedToThisRun(signal.TargetPersonaIds))
        {
            _ignoredMisaddressedSignals++;
            return Task.CompletedTask;
        }

        _chatInbox.Enqueue(signal);
        return Task.CompletedTask;
    }

    private bool IsAddressedToThisRun(IReadOnlyList<string>? targetPersonaIds)
    {
        if (targetPersonaIds is null || targetPersonaIds.Count == 0)
        {
            return true;
        }

        foreach (var rawTarget in targetPersonaIds)
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

    private bool IsSelf(string? senderId)
    {
        var sender = NormalizeIdentity(senderId);
        if (sender is null) return false;

        return string.Equals(sender, _personaId, StringComparison.Ordinal) ||
               string.Equals(sender, _agentId, StringComparison.Ordinal);
    }

    private static string? NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@').ToLowerInvariant();

    [WorkflowSignal]
    public Task ReceivePrReviewSignalAsync(PrReviewSignal signal)
    {
        if (signal is null) return Task.CompletedTask;

        _prReview = signal;
        return Task.CompletedTask;
    }

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

    private Task<TurnOutcome> ExecuteTurnActivityAsync(
        TaskExecutionInput input,
        ChatMessageSignal? pendingReply,
        CancellationToken cancellationToken) =>
        Workflow.ExecuteActivityAsync(
            (ITaskExecutionActivities act) => act.RunAgentCodingTurnAsync(input, pendingReply),
            new ActivityOptions
            {
                StartToCloseTimeout = TimeSpan.FromHours(2),
                HeartbeatTimeout = TimeSpan.FromMinutes(2),
                CancellationToken = cancellationToken,
                Summary = $"agent coding turn {_turnCount} for task {input.TaskId}",
            });

    private static bool IsActivityCancellation(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is CanceledFailureException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTurnHalt(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is ApplicationFailureException { NonRetryable: true })
            {
                return true;
            }

            if (ex is CanceledFailureException or OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

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

        return newest with { Content = combined };
    }

    private bool IsDuplicate(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return false;

        var trimmed = messageId.Trim();
        return _recentMessageIds.Contains(trimmed) ||
               _chatInbox.Any(m => string.Equals(m.MessageId, trimmed, StringComparison.Ordinal));
    }

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
