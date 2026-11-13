using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[Workflow("JobExecutionWorkflow")]
public class JobExecutionWorkflow : IJobExecutionWorkflow
{
    public const int DefaultContinueAsNewHistoryThreshold = 4_000;
    public const int MaxPrOpenAttempts = 3;
    public static readonly TimeSpan MaxReviewWait = TimeSpan.FromDays(7);

    private static readonly ActivityOptions TimelineActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromHours(1),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 3
        }
    };

    private static readonly ActivityOptions MergeActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 3
        }
    };

    private readonly Queue<ChatMessageSignal> _chatInbox = new();
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private DiscoveryAnswersSignal? _discoveryAnswers;
    private PlanApprovalSignal? _timelinePlanApproval;
    private PrReviewSignal? _prReview;
    private AbortActiveTurnSignal? _pendingAbort;
    private TurnInterventionSignal? _pendingIntervention;
    private ChildWorkflowHandle<ITaskExecutionWorkflow, TaskExecutionResult>? _activeChildHandle;
    private string? _activeChildWorkflowId;
    private List<ChecklistTaskItem> _tasks = new();
    private int _currentTaskIndex;
    private int _totalTasks;
    private int _completedTaskOffset;
    private int _reviewRound;
    private int _roundsInThisExecution;
    private int _ignoredSelfReviewCount;
    private Guid _initCardId;
    private Guid _prCardId;
    private string? _activePrUrl;
    private int? _activePrNumber;
    private string? _candidateHeadSha;
    private bool _prNeedsPushUpdate;
    private string? _shaMovedNotice;
    private string _currentHeartbeat = "Initializing job";
    private PlanApprovalPolicy _approvalPolicy = PlanApprovalPolicy.LeadOrHuman;
    private string? _assignedAgentId;

    [WorkflowRun]
    public async Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input)
    {
        _assignedAgentId = input.AssignedAgentId;
        if (Enum.TryParse<PlanApprovalPolicy>(input.ApprovalPolicy, ignoreCase: true, out var parsedPolicy))
        {
            _approvalPolicy = parsedPolicy;
        }
        else
        {
            _approvalPolicy = PlanApprovalPolicy.LeadOrHuman;
        }

        return await ExecuteTimelineLoopAsync(input);
    }

    public Task<JobExecutionResult> RunAsync(JobExecutionInput input) => ExecuteJobAsync(input);

    private async Task<JobExecutionResult> ExecuteTimelineLoopAsync(JobExecutionInput input)
    {
        List<ProposedChecklistItem> currentTasks;
        string? currentRemediationContext = null;

        if (input.Continuation is { } cont)
        {
            _reviewRound = cont.ReviewRound;
            _initCardId = cont.InitCardId ?? Guid.Empty;
            _prCardId = cont.PrCardId ?? Guid.Empty;
            _activePrUrl = cont.ActivePrUrl;
            _activePrNumber = cont.ActivePrNumber;
            _candidateHeadSha = cont.CandidateHeadSha;
            _completedTaskOffset = cont.CompletedTaskOffset;
            _prNeedsPushUpdate = true;
            currentTasks = cont.PendingTasks.ToList();
            currentRemediationContext = cont.RemediationContext;
            _totalTasks = _completedTaskOffset + currentTasks.Count;
            _currentHeartbeat = $"Resuming remediation round {_reviewRound}";
        }
        else
        {
            _currentHeartbeat = "Initializing job";

            var initPayload = JsonSerializer.Serialize(new
            {
                jobId = input.JobId,
                projectId = input.ProjectId,
                workspacePath = input.WorkspacePath,
                targetBranch = input.TargetBranch,
                assignedAgentId = input.AssignedAgentId,
                initialPrompt = input.InitialPrompt,
                approvalPolicy = input.ApprovalPolicy
            });

            var initCard = await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.CreateCardAsync(new CreateTimelineCardInput(
                    input.JobId,
                    null,
                    TimelineCardType.JobInitialized,
                    TimelineCardStatus.Active,
                    $"Job Initialized: {input.JobId}",
                    initPayload
                )),
                TimelineActivityOptions
            );
            _initCardId = initCard?.Id ?? Guid.Empty;

            // Stage 1A: Discovery Q&A activity -> wait on DiscoveryAnswersSignal if clarifying questions were asked
            var discoveryResult = await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.ExecuteDiscoveryQaActivityAsync(new DiscoveryQaActivityInput(
                    input.JobId,
                    input.InitialPrompt,
                    input.WorkspacePath
                )),
                TimelineActivityOptions
            );

            if (discoveryResult.Payload.Questions.Count > 0 && discoveryResult.CardId != Guid.Empty)
            {
                _currentHeartbeat = "Awaiting discovery Q&A answers";
                await Workflow.WaitConditionAsync(() => _discoveryAnswers != null);

                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                        discoveryResult.CardId,
                        TimelineCardStatus.Approved
                    )),
                    TimelineActivityOptions
                );
            }

            currentTasks = await RunDecompositionAndPlanApprovalAsync(input, planFeedback: null);
        }

        // Stage 3 (Sequential Task Loop without per-task PRs) + Stage 4 (Job-Level PR & Unbounded Review Loop)
        while (true)
        {
            if (currentTasks.Count > 0)
            {
                _totalTasks = _completedTaskOffset + currentTasks.Count;
                bool realigned = false;
                string? realignFeedback = null;

                for (int idx = 0; idx < currentTasks.Count; idx++)
                {
                    var globalIdx = _completedTaskOffset + idx;
                    _currentTaskIndex = globalIdx;
                    var task = currentTasks[idx];
                    _currentHeartbeat = $"Executing task {globalIdx + 1}/{_totalTasks}: {task.Title}";

                    _pendingAbort = null;
                    _pendingIntervention = null;

                    var taskSpecificContext = BuildChildRemediationContext(task, currentRemediationContext);
                    var childInput = new ChildTaskExecutionInput(
                        input.JobId,
                        task.TaskId,
                        globalIdx,
                        task.Title,
                        input.AssignedAgentId,
                        input.WorkspacePath,
                        input.TargetBranch,
                        input.ProjectId,
                        taskSpecificContext
                    );

                    var childWorkflowId = _reviewRound == 0
                        ? $"job-{input.JobId}-task-{idx}"
                        : $"job-{input.JobId}-r{_reviewRound}-task-{idx}";
                    _activeChildWorkflowId = childWorkflowId;

                    var childHandle = await Workflow.StartChildWorkflowAsync(
                        (ITaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(childInput),
                        new ChildWorkflowOptions
                        {
                            Id = childWorkflowId,
                            TaskQueue = Workflow.Info.TaskQueue,
                            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
                        }
                    );

                    _activeChildHandle = childHandle;

                    if (_pendingAbort != null)
                    {
                        await childHandle.SignalAsync(wf => wf.AbortTurnAsync(_pendingAbort));
                        _pendingAbort = null;
                    }
                    if (_pendingIntervention != null)
                    {
                        await childHandle.SignalAsync(wf => wf.InterveneTurnAsync(_pendingIntervention));
                        _pendingIntervention = null;
                    }

                    var result = await childHandle.GetResultAsync();
                    _activeChildHandle = null;
                    _activeChildWorkflowId = null;
                    _pendingAbort = null;
                    _pendingIntervention = null;

                    if (!result.Success)
                    {
                        var err = result.ErrorMessage ?? result.Summary ?? "Unknown error";
                        if (err.StartsWith("Realigned:", StringComparison.OrdinalIgnoreCase))
                        {
                            realignFeedback = err;
                            realigned = true;
                            break;
                        }

                        _currentHeartbeat = $"Failed on task {globalIdx + 1}";
                        return new JobExecutionResult(false, $"Task {globalIdx} ({task.Title}) failed: {err}");
                    }

                    if (!string.IsNullOrWhiteSpace(result.FinalPrUrl))
                    {
                        _activePrUrl = result.FinalPrUrl;
                    }
                    if (result.PrNumber.HasValue)
                    {
                        _activePrNumber = result.PrNumber;
                    }
                    if (!string.IsNullOrWhiteSpace(result.HeadSha))
                    {
                        _candidateHeadSha = result.HeadSha;
                    }
                }

                if (realigned)
                {
                    currentTasks = await RunDecompositionAndPlanApprovalAsync(input, realignFeedback);
                    currentRemediationContext = null;
                    _completedTaskOffset = 0;
                    continue;
                }

                _completedTaskOffset += currentTasks.Count;
                currentTasks = [];
                currentRemediationContext = null;
            }

            // Stage 4A: Transition to Job PR & Review Stage — sync base branch & open/update single job-level PR
            var prStage = await EnsureJobPullRequestReadyAsync(input);
            if (prStage.ConflictDetected)
            {
                _reviewRound++;
                _roundsInThisExecution++;
                var conflictDetail = prStage.Message ?? "Base branch merge conflict during PR sync.";
                var conflictTasks = BuildMergeConflictRemediationTasks(conflictDetail, input.TargetBranch, _reviewRound);
                conflictTasks = (await AppendRemediationTasksAsync(
                    input.JobId, _reviewRound, "merge_conflict", conflictTasks)).ToList();
                currentRemediationContext = BuildConflictRemediationPrompt(conflictDetail, input.TargetBranch, _reviewRound);
                _prNeedsPushUpdate = true;

                if (ShouldContinueAsNew(input))
                {
                    TriggerContinueAsNew(input, conflictTasks, currentRemediationContext);
                }

                currentTasks = conflictTasks;
                continue;
            }

            if (!prStage.Success)
            {
                _currentHeartbeat = $"Halted: {prStage.Message}";
                return new JobExecutionResult(false, prStage.Message ?? "Failed to open job-level pull request.");
            }

            // Stage 4B: Job-Level PR Review & Merge Gate (re-parks in place on sha_moved)
            while (true)
            {
                await UpsertPrReviewCardAsync(input, TimelineCardStatus.PendingInput, note: _shaMovedNotice);
                _shaMovedNotice = null;
                _prReview = null;
                _currentHeartbeat = $"Awaiting PR review on {_activePrUrl}";

                var gotReview = await Workflow.WaitConditionAsync(() => _prReview != null, MaxReviewWait);
                if (!gotReview)
                {
                    var reviewTimeout =
                        $"No review on {_activePrUrl ?? "the open pull request"} within {MaxReviewWait.TotalDays} days; halting.";
                    _currentHeartbeat = $"Halted: {reviewTimeout}";
                    return new JobExecutionResult(false, reviewTimeout);
                }

                var review = _prReview!;
                _prReview = null;

                if (!review.IsApproved)
                {
                    await UpsertPrReviewCardAsync(
                        input,
                        TimelineCardStatus.Rejected,
                        note: review.ReviewComments,
                        reviewerId: review.ReviewerId);

                    _reviewRound++;
                    _roundsInThisExecution++;
                    var reviewTasks = BuildReviewRemediationTasks(review, _reviewRound);
                    reviewTasks = (await AppendRemediationTasksAsync(
                        input.JobId, _reviewRound, "changes_requested", reviewTasks)).ToList();
                    currentRemediationContext = BuildReviewRemediationPrompt(review, _activePrUrl, _reviewRound);
                    _prNeedsPushUpdate = true;

                    if (ShouldContinueAsNew(input))
                    {
                        TriggerContinueAsNew(input, reviewTasks, currentRemediationContext);
                    }

                    currentTasks = reviewTasks;
                    break;
                }

                // Approved: execute merge passing approved HEAD SHA
                if (!string.IsNullOrWhiteSpace(review.ApprovedHeadSha))
                {
                    _candidateHeadSha = review.ApprovedHeadSha.Trim();
                }

                await UpsertPrReviewCardAsync(
                    input,
                    TimelineCardStatus.Approved,
                    note: review.ReviewComments,
                    reviewerId: review.ReviewerId);

                _currentHeartbeat = $"Merging approved PR {_activePrUrl}";

                string mergeSummary;
                try
                {
                    mergeSummary = await Workflow.ExecuteActivityAsync(
                        (IGitActivities act) => act.MergeJobPullRequestAsync(new MergePullRequestInput(
                            input.JobId,
                            input.TargetBranch,
                            _activePrUrl,
                            _candidateHeadSha)),
                        MergeActivityOptions);
                }
                catch (Exception ex) when (IsNonRetryableMergeFailure(ex, out var failureCode, out var failureReason))
                {
                    if (string.Equals(failureCode, "sha_moved", StringComparison.OrdinalIgnoreCase))
                    {
                        // Fail closed back to the reviewer gate for re-approval without merging unreviewed commits
                        _candidateHeadSha = null;
                        _shaMovedNotice = failureReason;
                        _currentHeartbeat = $"HEAD moved after approval on {_activePrUrl}; awaiting re-approval";
                        continue;
                    }

                    if (string.Equals(failureCode, "merge_conflict", StringComparison.OrdinalIgnoreCase))
                    {
                        // Route merge_conflict back into the task loop as a base-merge remediation task
                        await UpsertPrReviewCardAsync(
                            input,
                            TimelineCardStatus.Rejected,
                            note: failureReason,
                            reviewerId: "github-merge-guard");

                        _reviewRound++;
                        _roundsInThisExecution++;
                        var conflictTasks = BuildMergeConflictRemediationTasks(failureReason, input.TargetBranch, _reviewRound);
                        conflictTasks = (await AppendRemediationTasksAsync(
                            input.JobId, _reviewRound, "merge_conflict", conflictTasks)).ToList();
                        currentRemediationContext = BuildConflictRemediationPrompt(failureReason, input.TargetBranch, _reviewRound);
                        _prNeedsPushUpdate = true;

                        if (ShouldContinueAsNew(input))
                        {
                            TriggerContinueAsNew(input, conflictTasks, currentRemediationContext);
                        }

                        currentTasks = conflictTasks;
                        break;
                    }

                    _currentHeartbeat = $"Halted: Approved merge rejected ({failureCode}): {failureReason}";
                    return new JobExecutionResult(
                        false,
                        $"Approved merge could not complete ({failureCode}): {failureReason}");
                }

                await UpsertPrReviewCardAsync(
                    input,
                    TimelineCardStatus.Resolved,
                    note: mergeSummary,
                    reviewerId: review.ReviewerId);

                if (_initCardId != Guid.Empty)
                {
                    await Workflow.ExecuteActivityAsync(
                        (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                            _initCardId,
                            TimelineCardStatus.Resolved)),
                        TimelineActivityOptions);
                }

                _currentHeartbeat = "Pull request merged; job completed";
                return new JobExecutionResult(
                    true,
                    string.IsNullOrWhiteSpace(mergeSummary)
                        ? $"Pull request {_activePrUrl} merged; job completed."
                        : mergeSummary);
            }
        }
    }

    private async Task<List<ProposedChecklistItem>> RunDecompositionAndPlanApprovalAsync(
        JobExecutionInput input,
        string? planFeedback)
    {
        var currentFeedback = planFeedback;
        while (true)
        {
            _timelinePlanApproval = null;
            _currentHeartbeat = currentFeedback == null ? "Decomposing job tasks" : "Re-decomposing plan with feedback";
            var answers = _discoveryAnswers?.Answers ?? new Dictionary<string, string>();
            var decompositionResult = await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.ExecuteDecompositionActivityAsync(new DecompositionActivityInput(
                    input.JobId,
                    input.InitialPrompt,
                    input.WorkspacePath,
                    currentFeedback,
                    answers
                )),
                TimelineActivityOptions
            );

            _currentHeartbeat = "Awaiting plan approval";
            await Workflow.WaitConditionAsync(() => _timelinePlanApproval != null);

            if (_timelinePlanApproval!.Approved)
            {
                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                        decompositionResult.CardId,
                        TimelineCardStatus.Approved
                    )),
                    TimelineActivityOptions
                );
                return decompositionResult.Payload.Tasks.ToList();
            }

            currentFeedback = _timelinePlanApproval.Feedback ?? "Plan rejected by approver";
            await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                    decompositionResult.CardId,
                    TimelineCardStatus.Rejected
                )),
                TimelineActivityOptions
            );
        }
    }

    private readonly record struct PrStageOutcome(bool Success, bool ConflictDetected, string? Message = null)
    {
        public static PrStageOutcome Ready() => new(true, false);
        public static PrStageOutcome Conflict(string message) => new(false, true, message);
        public static PrStageOutcome Failed(string message) => new(false, false, message);
    }

    private async Task<PrStageOutcome> EnsureJobPullRequestReadyAsync(JobExecutionInput input)
    {
        if (!string.IsNullOrWhiteSpace(_activePrUrl) && !_prNeedsPushUpdate)
        {
            return PrStageOutcome.Ready();
        }

        ChatMessageSignal? pendingReply = DequeueJobChatReplies();

        for (int attempt = 1; attempt <= MaxPrOpenAttempts; attempt++)
        {
            _currentHeartbeat = string.IsNullOrWhiteSpace(_activePrUrl)
                ? $"Stage 4: Syncing {input.TargetBranch} and opening job pull request"
                : $"Stage 4 (Round {_reviewRound}): Pushing updates to {_activePrUrl}";

            var instruction = string.IsNullOrWhiteSpace(_activePrUrl)
                ? $"All checklist tasks are complete. Sync '{input.TargetBranch}' (`agentmesh pr sync --base {input.TargetBranch}`), " +
                  "verify `git status --porcelain` is clean, and open the single job-level pull request via " +
                  "`agentmesh pr open --title \"...\" --result \"...\" --reasoning \"...\" --risk \"...\"`." +
                  (attempt > 1 ? " Reminder: No pull request URL was registered on the previous attempt; run `agentmesh pr open` now." : "")
                : $"All remediation tasks for round {_reviewRound} are complete. Sync '{input.TargetBranch}' " +
                  $"(`agentmesh pr sync --base {input.TargetBranch}`), push your commits, and update/re-register PR " +
                  $"{_activePrUrl} via `agentmesh pr open` so the latest HEAD SHA is recorded for re-review.";

            var stageInput = new TaskExecutionInput(
                JobId: input.JobId,
                TaskId: $"stage-4-pr-open-{_reviewRound}",
                AgentId: input.AssignedAgentId,
                ProjectId: input.ProjectId,
                WorkspacePath: input.WorkspacePath,
                TargetBranch: input.TargetBranch,
                PersonaId: input.AssignedAgentId,
                TaskTitle: string.IsNullOrWhiteSpace(_activePrUrl)
                    ? "Stage 4: Open Job Pull Request"
                    : $"Stage 4: Update Job Pull Request (Round {_reviewRound})",
                RemediationContext: instruction);

            TurnOutcome outcome;
            try
            {
                outcome = await Workflow.ExecuteActivityAsync(
                    (ITaskExecutionActivities act) => act.RunAgentCodingTurnAsync(stageInput, pendingReply),
                    new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromHours(2),
                        HeartbeatTimeout = TimeSpan.FromMinutes(2),
                        Summary = $"Stage 4 PR sync/open (round {_reviewRound}, attempt {attempt}) for job {input.JobId}"
                    });
            }
            catch (Exception ex) when (IsNonRetryableMergeFailure(ex, out _, out var haltReason))
            {
                if (ContainsMergeConflictMarker(haltReason))
                {
                    return PrStageOutcome.Conflict(haltReason);
                }
                return PrStageOutcome.Failed(haltReason);
            }

            pendingReply = null;

            if (ContainsMergeConflictMarker(outcome.Message))
            {
                return PrStageOutcome.Conflict(outcome.Message!);
            }

            if (outcome.Status == TurnOutcomeStatus.PrOpenedAwaitingReview || !string.IsNullOrWhiteSpace(outcome.PrUrl))
            {
                _activePrUrl = !string.IsNullOrWhiteSpace(outcome.PrUrl) ? outcome.PrUrl : _activePrUrl;
                _activePrNumber = outcome.PrNumber ?? _activePrNumber;
                if (!string.IsNullOrWhiteSpace(outcome.HeadSha))
                {
                    _candidateHeadSha = outcome.HeadSha;
                }
                _prNeedsPushUpdate = false;

                if (!string.IsNullOrWhiteSpace(_activePrUrl))
                {
                    return PrStageOutcome.Ready();
                }
            }

            if (outcome.Status == TurnOutcomeStatus.AwaitingInput)
            {
                var target = outcome.TargetPersona ?? "operator";
                _currentHeartbeat = $"Waiting on input from {target} during PR stage";
                var gotInput = await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0, MaxReviewWait);
                if (!gotInput)
                {
                    return PrStageOutcome.Failed($"No reply from {target} within {MaxReviewWait.TotalDays} days during Stage 4.");
                }
                pendingReply = DequeueJobChatReplies();
                attempt--; // Escalation wait does not consume an open attempt
                continue;
            }

            if (outcome.Status == TurnOutcomeStatus.TaskCompleted && !string.IsNullOrWhiteSpace(_activePrUrl))
            {
                if (!string.IsNullOrWhiteSpace(outcome.HeadSha))
                {
                    _candidateHeadSha = outcome.HeadSha;
                }
                _prNeedsPushUpdate = false;
                return PrStageOutcome.Ready();
            }

            if (outcome.Status is TurnOutcomeStatus.Failed or TurnOutcomeStatus.StuckLoopDetected)
            {
                return PrStageOutcome.Failed(outcome.Message ?? $"Stage 4 halted with status {outcome.Status}.");
            }
        }

        return PrStageOutcome.Failed(
            $"Stage 4 halted: Persona did not register a job-level pull request after {MaxPrOpenAttempts} attempt(s). " +
            "Ensure `agentmesh pr open` runs cleanly with valid GitHub credentials.");
    }

    private async Task UpsertPrReviewCardAsync(
        JobExecutionInput input,
        TimelineCardStatus status,
        string? note = null,
        string? reviewerId = null)
    {
        var prLabel = _activePrNumber.HasValue
            ? $"PR #{_activePrNumber.Value.ToString(CultureInfo.InvariantCulture)}"
            : (_activePrUrl ?? "Job Pull Request");

        var title = status switch
        {
            TimelineCardStatus.PendingInput => $"Awaiting Review: {prLabel}",
            TimelineCardStatus.Approved => $"Approved: {prLabel}",
            TimelineCardStatus.Rejected => $"Changes Requested: {prLabel}",
            TimelineCardStatus.Resolved => $"Merged: {prLabel}",
            _ => prLabel
        };

        var payloadJson = JsonSerializer.Serialize(new
        {
            prUrl = _activePrUrl,
            prNumber = _activePrNumber,
            headSha = _candidateHeadSha,
            authorPersonaId = input.AssignedAgentId,
            senderPersona = input.AssignedAgentId,
            targetBranch = input.TargetBranch,
            round = _reviewRound + 1,
            reviewerId,
            note,
            source = "workflow:job_pr_gate"
        });

        if (_prCardId == Guid.Empty)
        {
            var created = await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.CreateCardAsync(new CreateTimelineCardInput(
                    input.JobId,
                    null,
                    TimelineCardType.PrReview,
                    status,
                    title,
                    payloadJson)),
                TimelineActivityOptions);
            _prCardId = created?.Id ?? Guid.Empty;
        }
        else
        {
            await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                    _prCardId,
                    status,
                    payloadJson,
                    title)),
                TimelineActivityOptions);
        }
    }

    private async Task<IReadOnlyList<ProposedChecklistItem>> AppendRemediationTasksAsync(
        string jobId,
        int round,
        string source,
        IReadOnlyList<ProposedChecklistItem> tasks)
    {
        return await Workflow.ExecuteActivityAsync(
            (ITimelineActivities act) => act.AppendRemediationTasksAsync(new AppendRemediationTasksInput(
                jobId,
                round,
                source,
                tasks)),
            TimelineActivityOptions);
    }

    private bool ShouldContinueAsNew(JobExecutionInput input)
    {
        if (input.ContinueAsNewAfterRounds is > 0 &&
            _roundsInThisExecution >= input.ContinueAsNewAfterRounds.Value)
        {
            return true;
        }

        return Workflow.ContinueAsNewSuggested ||
               Workflow.CurrentHistoryLength >= DefaultContinueAsNewHistoryThreshold;
    }

    private void TriggerContinueAsNew(
        JobExecutionInput input,
        IReadOnlyList<ProposedChecklistItem> nextTasks,
        string? remediationContext)
    {
        var continuation = new JobExecutionContinuationState(
            ReviewRound: _reviewRound,
            InitCardId: _initCardId == Guid.Empty ? null : _initCardId,
            PrCardId: _prCardId == Guid.Empty ? null : _prCardId,
            ActivePrUrl: _activePrUrl,
            ActivePrNumber: _activePrNumber,
            CandidateHeadSha: _candidateHeadSha,
            CompletedTaskOffset: _completedTaskOffset,
            PendingTasks: nextTasks,
            RemediationContext: remediationContext);

        var nextInput = input with { Continuation = continuation };
        throw Workflow.CreateContinueAsNewException((JobExecutionWorkflow wf) => wf.ExecuteJobAsync(nextInput));
    }

    internal static List<ProposedChecklistItem> BuildReviewRemediationTasks(PrReviewSignal review, int round)
    {
        var raw = review.ReviewComments?.Trim();
        var items = new List<ProposedChecklistItem>();

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var lines = raw
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var bulletLines = lines
                .Where(l => Regex.IsMatch(l, @"^(?:[-*•]|\d+[.)])\s+\S"))
                .Select(l => Regex.Replace(l, @"^(?:[-*•]|\d+[.)])\s+", "").Trim())
                .Where(l => l.Length > 0)
                .Take(25)
                .ToList();

            if (bulletLines.Count > 0)
            {
                for (int i = 0; i < bulletLines.Count; i++)
                {
                    var text = bulletLines[i];
                    var title = text.Length > 140 ? text[..137] + "..." : text;
                    items.Add(new ProposedChecklistItem(
                        TaskId: $"remediation-r{round}-{i + 1}",
                        Title: $"[PR Review R{round}] {title}",
                        TargetFile: ExtractLikelyFilePath(text) ?? "workspace",
                        VerificationMethod: $"Resolve review comment ({text}) and verify scoped tests pass"));
                }

                return items;
            }

            var summaryTitle = lines[0].Length > 140 ? lines[0][..137] + "..." : lines[0];
            items.Add(new ProposedChecklistItem(
                TaskId: $"remediation-r{round}-1",
                Title: $"[PR Review R{round}] {summaryTitle}",
                TargetFile: ExtractLikelyFilePath(raw) ?? "workspace",
                VerificationMethod: "Address PR review feedback, run tests, and commit cleanly to the job branch"));
            return items;
        }

        var reviewer = string.IsNullOrWhiteSpace(review.ReviewerId) ? "reviewer" : review.ReviewerId;
        items.Add(new ProposedChecklistItem(
            TaskId: $"remediation-r{round}-1",
            Title: $"[PR Review R{round}] Address requested changes from @{reviewer}",
            TargetFile: "workspace",
            VerificationMethod: "Address PR review feedback, run tests, and commit cleanly to the job branch"));
        return items;
    }

    internal static List<ProposedChecklistItem> BuildMergeConflictRemediationTasks(
        string conflictDetail,
        string targetBranch,
        int round)
    {
        var files = ExtractConflictedFiles(conflictDetail);
        var targetFile = files.Count > 0 ? string.Join(", ", files) : "workspace";
        var fileSuffix = files.Count > 0 ? $" in {targetFile}" : "";

        return
        [
            new ProposedChecklistItem(
                TaskId: $"conflict-r{round}-1",
                Title: $"[Merge Conflict R{round}] Merge origin/{targetBranch} and resolve conflicts{fileSuffix}",
                TargetFile: targetFile,
                VerificationMethod: $"Run `agentmesh pr sync --base {targetBranch}`, resolve all conflict markers, verify build & tests pass, and commit cleanly")
        ];
    }

    private static string BuildReviewRemediationPrompt(PrReviewSignal review, string? prUrl, int round)
    {
        var reviewer = string.IsNullOrWhiteSpace(review.ReviewerId) ? "reviewer" : review.ReviewerId;
        var comments = string.IsNullOrWhiteSpace(review.ReviewComments) ? "(no detailed comments provided)" : review.ReviewComments.Trim();
        return $"PR review round {round} on {prUrl ?? "the job pull request"} — @{reviewer} requested changes:\n{comments}\n\n" +
               "Implement the requested changes, verify with tests, commit cleanly to the active job branch, and mark this remediation task `completed` via `manage_task`. Do NOT open a separate PR; Stage 4 will push and update the existing job PR once remediation tasks complete.";
    }

    private static string BuildConflictRemediationPrompt(string conflictDetail, string targetBranch, int round) =>
        $"Base-branch sync/merge conflict detected (round {round}) against '{targetBranch}':\n{conflictDetail}\n\n" +
        $"Run `agentmesh pr sync --base {targetBranch}`, resolve any conflicted files cleanly, run tests to verify, commit the merge on the job branch, and mark this remediation task `completed` via `manage_task`.";

    private static string? BuildChildRemediationContext(ProposedChecklistItem task, string? roundContext)
    {
        if (string.IsNullOrWhiteSpace(roundContext))
        {
            return null;
        }

        return $"Work remediation checklist item: {task.Title}\n\n{roundContext}";
    }

    private static List<string> ExtractConflictedFiles(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var match = Regex.Match(text, @"MERGE_CONFLICT:\s*(?<files>[^\r\n]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return [];

        return match.Groups["files"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.Length > 0 && !f.StartsWith('('))
            .ToList();
    }

    private static string? ExtractLikelyFilePath(string text)
    {
        var match = Regex.Match(text, @"`?(?<path>[A-Za-z0-9_./-]+\.[A-Za-z0-9]{1,8})`?");
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static bool ContainsMergeConflictMarker(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("MERGE_CONFLICT:", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonRetryableMergeFailure(
        Exception exception,
        out string errorType,
        out string reason)
    {
        errorType = "merge_failed";
        reason = exception.Message;

        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is CanceledFailureException or OperationCanceledException)
            {
                return false;
            }

            if (ex is ApplicationFailureException { NonRetryable: true } appEx)
            {
                errorType = string.IsNullOrWhiteSpace(appEx.ErrorType) ? "merge_failed" : appEx.ErrorType;
                if (!string.IsNullOrWhiteSpace(appEx.Message))
                {
                    reason = appEx.Message;
                }
                return true;
            }
        }

        return false;
    }

    private ChatMessageSignal? DequeueJobChatReplies()
    {
        if (_chatInbox.Count == 0) return null;

        var items = new List<ChatMessageSignal>();
        while (_chatInbox.Count > 0)
        {
            items.Add(_chatInbox.Dequeue());
        }

        if (items.Count == 1) return items[0];

        var newest = items[^1];
        var combined = string.Join(
            "\n\n",
            items.Select((m, i) => $"[{i + 1} of {items.Count}] {m.SenderId}: {m.Content}"));
        return newest with { Content = combined };
    }

    [WorkflowSignal]
    public Task SubmitDiscoveryAnswersAsync(DiscoveryAnswersSignal signal)
    {
        _discoveryAnswers = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task SubmitPlanDecisionAsync(PlanApprovalSignal signal)
    {
        _timelinePlanApproval = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public async Task AbortTurnAsync(AbortActiveTurnSignal signal)
    {
        if (_activeChildHandle != null)
        {
            await _activeChildHandle.SignalAsync(wf => wf.AbortTurnAsync(signal));
            _pendingAbort = null;
        }
        else if (_activeChildWorkflowId != null)
        {
            _pendingAbort = signal;
        }
        else
        {
            _pendingAbort = null;
        }
    }

    [WorkflowSignal]
    public async Task InterveneTurnAsync(TurnInterventionSignal signal)
    {
        _pendingAbort = null;
        if (_activeChildHandle != null)
        {
            await _activeChildHandle.SignalAsync(wf => wf.InterveneTurnAsync(signal));
            _pendingIntervention = null;
        }
        else if (_activeChildWorkflowId != null)
        {
            _pendingIntervention = signal;
        }
        else
        {
            _pendingIntervention = null;
        }
    }

    [WorkflowSignal]
    public async Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        if (signal is null || string.IsNullOrWhiteSpace(signal.Content))
        {
            return;
        }

        if (IsSameAgentIdentity(signal.SenderId, _assignedAgentId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(signal.MessageId) && !_seenMessageIds.Add(signal.MessageId))
        {
            return;
        }

        _chatInbox.Enqueue(signal);
        if (_activeChildHandle != null)
        {
            await _activeChildHandle.SignalAsync(wf => wf.ReceiveChatMessageAsync(signal));
        }
    }

    private static bool IsSameAgentIdentity(string? senderId, string? assignedAgentId)
    {
        if (string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(assignedAgentId))
        {
            return false;
        }

        var normSender = NormalizeIdentity(senderId);
        var normAssigned = NormalizeIdentity(assignedAgentId);
        return normSender.Length > 0 && string.Equals(normSender, normAssigned, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIdentity(string id)
    {
        var trimmed = id.Trim().TrimStart('@');
        if (trimmed.StartsWith("driver-", StringComparison.OrdinalIgnoreCase) && trimmed.Length > "driver-".Length)
        {
            trimmed = trimmed["driver-".Length..];
        }
        return trimmed;
    }

    [WorkflowSignal]
    public Task ReceivePlanApprovalAsync(PlanApprovedSignal signal)
    {
        if (_approvalPolicy == PlanApprovalPolicy.HumanOnly &&
            !string.Equals(signal.ApproverRole, "operator", StringComparison.OrdinalIgnoreCase))
        {
            _currentHeartbeat = $"Plan approval rejected: role '{signal.ApproverRole}' is not permitted by policy {_approvalPolicy}";
            return Task.CompletedTask;
        }

        _timelinePlanApproval = new PlanApprovalSignal(Guid.Empty, true, signal.Comments, signal.ApproverId);
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceivePrReviewAsync(PrReviewSignal signal)
    {
        if (signal is null)
        {
            return Task.CompletedTask;
        }

        // Defense-in-depth Two-Man Rule: author persona cannot release its own PR review gate
        if (IsSameAgentIdentity(signal.ReviewerId, _assignedAgentId))
        {
            _ignoredSelfReviewCount++;
            _currentHeartbeat = $"Self-review from '{signal.ReviewerId}' rejected by Two-Man Rule; awaiting independent review";
            return Task.CompletedTask;
        }

        _prReview = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceiveSetChecklistAsync(List<ChecklistTaskItem> tasks)
    {
        _tasks = tasks ?? new();
        _totalTasks = _tasks.Count;
        _currentHeartbeat = _approvalPolicy == PlanApprovalPolicy.Autonomous
            ? "Checklist registered. Auto-approving plan."
            : "Checklist registered. Awaiting plan approval.";
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task UpdateHeartbeatAsync(string heartbeat)
    {
        _currentHeartbeat = heartbeat;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public JobWorkflowState GetCurrentState() =>
        new(
            _currentTaskIndex,
            _tasks.Count > 0 ? _tasks.Count : _totalTasks,
            _currentHeartbeat,
            _reviewRound,
            _activePrUrl,
            _ignoredSelfReviewCount);
}
