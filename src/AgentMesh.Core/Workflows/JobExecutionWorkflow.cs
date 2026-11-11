using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[Workflow("JobExecutionWorkflow")]
public class JobExecutionWorkflow : IJobExecutionWorkflow
{
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

    private static readonly ActivityOptions TurnActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 3,
            NonRetryableErrorTypes = ["deliberate_halt", "invalid_input", "unauthorized"]
        }
    };

    private static readonly ActivityOptions GitActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 3,
            NonRetryableErrorTypes = ["merge_failed", "no_pr_url"]
        }
    };

    private readonly Queue<ChatMessageSignal> _chatInbox = new();
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private DiscoveryAnswersSignal? _discoveryAnswers;
    private PlanApprovalSignal? _timelinePlanApproval;
    private PlanApprovedSignal? _legacyPlanApproval;
    private PrReviewSignal? _prReview;
    private AbortActiveTurnSignal? _pendingAbort;
    private TurnInterventionSignal? _pendingIntervention;
    private ChildWorkflowHandle<ITaskExecutionWorkflow, TaskExecutionResult>? _activeChildHandle;
    private string? _activeChildWorkflowId;
    private List<ChecklistTaskItem> _tasks = new();
    private int _currentTaskIndex;
    private int _totalTasks;
    private string _currentHeartbeat = "Initializing job";
    private PlanApprovalPolicy _approvalPolicy = PlanApprovalPolicy.Autonomous;
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

        if (input.UseLegacyTurnLoop)
        {
            return await ExecuteLegacyTurnLoopAsync(input);
        }

        return await ExecuteTimelineLoopAsync(input);
    }

    public Task<JobExecutionResult> RunAsync(JobExecutionInput input) => ExecuteJobAsync(input);

    private async Task<JobExecutionResult> ExecuteTimelineLoopAsync(JobExecutionInput input)
    {
        _currentHeartbeat = "Initializing job";

        var initPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            jobId = input.JobId,
            projectId = input.ProjectId,
            workspacePath = input.WorkspacePath,
            targetBranch = input.TargetBranch,
            assignedAgentId = input.AssignedAgentId,
            initialPrompt = input.InitialPrompt,
            approvalPolicy = input.ApprovalPolicy
        });

        await Workflow.ExecuteActivityAsync(
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

        // Stage 1A: Discovery Q&A activity -> wait on DiscoveryAnswersSignal
        _currentHeartbeat = "Awaiting discovery Q&A answers";
        var discoveryResult = await Workflow.ExecuteActivityAsync(
            (ITimelineActivities act) => act.ExecuteDiscoveryQaActivityAsync(new DiscoveryQaActivityInput(
                input.JobId,
                input.InitialPrompt,
                input.WorkspacePath
            )),
            TimelineActivityOptions
        );

        await Workflow.WaitConditionAsync(() => _discoveryAnswers != null);

        await Workflow.ExecuteActivityAsync(
            (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                discoveryResult.CardId,
                TimelineCardStatus.Approved
            )),
            TimelineActivityOptions
        );

        // Stage 1B, Stage 2 & Stage 3: Decomposition + Approval + Child Task Execution (with Realign loop)
        string? planFeedback = null;

        while (true)
        {
            DecompositionActivityResult decompositionResult;

            while (true)
            {
                _timelinePlanApproval = null;
                _currentHeartbeat = planFeedback == null ? "Decomposing job tasks" : "Re-decomposing plan with feedback";
                decompositionResult = await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.ExecuteDecompositionActivityAsync(new DecompositionActivityInput(
                        input.JobId,
                        input.InitialPrompt,
                        input.WorkspacePath,
                        planFeedback,
                        _discoveryAnswers!.Answers
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
                    break;
                }

                planFeedback = _timelinePlanApproval.Feedback ?? "Plan rejected by approver";
                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                        decompositionResult.CardId,
                        TimelineCardStatus.Rejected
                    )),
                    TimelineActivityOptions
                );
            }

            var tasks = decompositionResult.Payload.Tasks;
            _totalTasks = tasks.Count;
            bool realigned = false;

            for (int idx = 0; idx < tasks.Count; idx++)
            {
                _currentTaskIndex = idx;
                var task = tasks[idx];
                _currentHeartbeat = $"Executing task {idx + 1}/{_totalTasks}: {task.Title}";

                _pendingAbort = null;
                _pendingIntervention = null;

                var childInput = new ChildTaskExecutionInput(
                    input.JobId,
                    task.TaskId,
                    idx,
                    task.Title,
                    input.AssignedAgentId,
                    input.WorkspacePath,
                    input.TargetBranch,
                    input.ProjectId
                );

                var childWorkflowId = $"job-{input.JobId}-task-{idx}";
                _activeChildWorkflowId = childWorkflowId;

                var childHandle = await Workflow.StartChildWorkflowAsync(
                    (ITaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(childInput),
                    new ChildWorkflowOptions
                    {
                        Id = childWorkflowId,
                        TaskQueue = Workflow.Info.TaskQueue
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
                        planFeedback = err;
                        realigned = true;
                        break;
                    }

                    _currentHeartbeat = $"Failed on task {idx + 1}";
                    return new JobExecutionResult(false, $"Task {idx} ({task.Title}) failed: {err}");
                }
            }

            if (realigned)
            {
                continue;
            }

            break;
        }

        _currentHeartbeat = "Completed all tasks successfully";
        return new JobExecutionResult(true, "All tasks completed successfully.");
    }

    private async Task<JobExecutionResult> ExecuteLegacyTurnLoopAsync(JobExecutionInput input)
    {
        // 1. Stage 1: Discovery & Planning Loop
        _currentHeartbeat = "Exploring codebase and generating checklist";
        int discoveryTurns = 0;
        const int maxDiscoveryTurns = 15;
        string? activePrUrl = null;

        while (_tasks.Count == 0 && discoveryTurns < maxDiscoveryTurns)
        {
            discoveryTurns++;
            ChatMessageSignal? pendingDiscoveryMessage = _chatInbox.Count > 0 ? _chatInbox.Dequeue() : null;

            var discoveryOutcome = await Workflow.ExecuteActivityAsync(
                (IExecutionActivities act) => act.RunAgentDiscoveryTurnAsync(new DiscoveryTurnInput(
                    input.JobId,
                    input.AssignedAgentId,
                    input.WorkspacePath,
                    input.InitialPrompt,
                    input.ProjectId,
                    input.TargetBranch,
                    pendingDiscoveryMessage)),
                TurnActivityOptions);

            if (discoveryOutcome.Status == TurnOutcomeStatus.Failed)
            {
                _currentHeartbeat = $"Discovery failed: {discoveryOutcome.Message}";
                return new JobExecutionResult(Success: false, Message: $"Discovery failed: {discoveryOutcome.Message}");
            }

            if (!string.IsNullOrWhiteSpace(discoveryOutcome.PrUrl) || discoveryOutcome.Status == TurnOutcomeStatus.PrOpenedAwaitingReview)
            {
                activePrUrl = discoveryOutcome.PrUrl;
                _currentHeartbeat = "Pull request active; awaiting code review";
                break;
            }

            if (_tasks.Count > 0)
            {
                break;
            }

            if (discoveryOutcome.Status == TurnOutcomeStatus.AwaitingInput)
            {
                _currentHeartbeat = string.IsNullOrWhiteSpace(discoveryOutcome.Message)
                    ? "Awaiting user input on planning questions"
                    : $"Awaiting user input: {discoveryOutcome.Message}";

                await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0 || _tasks.Count > 0);
                _currentHeartbeat = "Resuming discovery with user feedback";
                continue;
            }

            if (discoveryOutcome.Status == TurnOutcomeStatus.StuckLoopDetected)
            {
                _currentHeartbeat = $"Stuck loop detected during discovery: {discoveryOutcome.Message}";
                await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0 || _tasks.Count > 0);
                continue;
            }

            if (discoveryOutcome.Status == TurnOutcomeStatus.Continue)
            {
                _currentHeartbeat = $"Continuing discovery turn {discoveryTurns + 1}/{maxDiscoveryTurns}";
                continue;
            }

            break;
        }

        if (_tasks.Count == 0 && string.IsNullOrWhiteSpace(activePrUrl))
        {
            _tasks.Add(new ChecklistTaskItem(
                Title: !string.IsNullOrWhiteSpace(input.InitialPrompt) ? input.InitialPrompt : "Execute job implementation",
                TargetFile: null,
                VerificationMethod: null));
        }

        if (!string.IsNullOrWhiteSpace(activePrUrl))
        {
            _currentHeartbeat = "Pull request active; awaiting code review";
        }
        else
        {
            if (_approvalPolicy == PlanApprovalPolicy.Autonomous)
            {
                _currentHeartbeat = "Plan auto-approved. Starting implementation";
            }
            else
            {
                if (_currentHeartbeat.StartsWith("Exploring", StringComparison.OrdinalIgnoreCase) ||
                    _currentHeartbeat.StartsWith("Awaiting user input", StringComparison.OrdinalIgnoreCase) ||
                    _currentHeartbeat.StartsWith("Resuming", StringComparison.OrdinalIgnoreCase))
                {
                    _currentHeartbeat = "Awaiting plan approval";
                }

                // 2. Stage 2: Plan Approval Gate
                await Workflow.WaitConditionAsync(() =>
                {
                    if (_tasks.Count == 0 || _legacyPlanApproval == null) return false;
                    var isApproved = _approvalPolicy switch
                    {
                        PlanApprovalPolicy.LeadOrHuman =>
                            _legacyPlanApproval.ApproverRole is "operator" or "tech_lead",
                        PlanApprovalPolicy.HumanOnly =>
                            _legacyPlanApproval.ApproverRole == "operator",
                        _ => false
                    };

                    if (!isApproved)
                    {
                        var rejectedRole = _legacyPlanApproval.ApproverRole;
                        _legacyPlanApproval = null;
                        _currentHeartbeat = $"Plan approval rejected: role '{rejectedRole}' is not permitted by policy {_approvalPolicy}";
                        return false;
                    }

                    return true;
                });

                _currentHeartbeat = "Plan approved. Starting implementation";
            }

            // 3. Stage 3: Autonomous Task Iteration Loop
            while (_currentTaskIndex < _tasks.Count)
            {
                var currentTask = _tasks[_currentTaskIndex];
                _currentHeartbeat = $"Executing task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";

                ChatMessageSignal? pendingMessage = _chatInbox.Count > 0 ? _chatInbox.Dequeue() : null;

                var turnOutcome = await Workflow.ExecuteActivityAsync(
                    (IExecutionActivities act) => act.RunJobCodingTurnAsync(new CodingTurnInput(
                        input.JobId,
                        currentTask,
                        pendingMessage,
                        input.AssignedAgentId,
                        input.ProjectId,
                        input.WorkspacePath,
                        input.TargetBranch)),
                    TurnActivityOptions);

                if (!string.IsNullOrWhiteSpace(turnOutcome.PrUrl))
                {
                    activePrUrl = turnOutcome.PrUrl;
                }

                if (turnOutcome.Status == TurnOutcomeStatus.AwaitingInput)
                {
                    _currentHeartbeat = $"Awaiting input for task: {currentTask.Title}";
                    await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0);
                    continue;
                }

                if (turnOutcome.Status == TurnOutcomeStatus.StuckLoopDetected)
                {
                    _currentHeartbeat = $"Stuck loop detected on task: {currentTask.Title}";
                    await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0);
                    continue;
                }

                if (turnOutcome.Status == TurnOutcomeStatus.Failed)
                {
                    _currentHeartbeat = $"Task failed: {turnOutcome.Message ?? currentTask.Title}";
                    return new JobExecutionResult(Success: false, Message: $"Task failed: {turnOutcome.Message}");
                }

                if (turnOutcome.Status is TurnOutcomeStatus.TaskCompleted or TurnOutcomeStatus.PrOpenedAwaitingReview)
                {
                    _currentHeartbeat = $"Completed task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";
                    _currentTaskIndex++;
                }
                else if (turnOutcome.Status == TurnOutcomeStatus.Continue)
                {
                    _currentHeartbeat = $"Continuing task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";
                }
            }
        }

        // 4. Stage 4: Pull Request & Mandatory Review Gate
        bool prApproved = false;
        while (!prApproved)
        {
            _currentHeartbeat = "Awaiting pull request review";
            await Workflow.WaitConditionAsync(() => _prReview != null);

            if (!_prReview!.IsApproved)
            {
                _currentHeartbeat = "Changes requested on PR; resuming coding turn";
                ChatMessageSignal? reviewFeedback = new ChatMessageSignal(
                    _prReview.ReviewerId,
                    $"Review feedback on pull request:\n{_prReview.ReviewComments ?? "Changes requested."}",
                    Workflow.UtcNow
                );
                _prReview = null;

                var reviewFixTask = new ChecklistTaskItem(
                    Title: "Address PR review feedback",
                    TargetFile: null,
                    VerificationMethod: "dotnet test"
                );

                while (true)
                {
                    var fixOutcome = await Workflow.ExecuteActivityAsync(
                        (IExecutionActivities act) => act.RunJobCodingTurnAsync(new CodingTurnInput(
                            input.JobId,
                            reviewFixTask,
                            reviewFeedback,
                            input.AssignedAgentId,
                            input.ProjectId,
                            input.WorkspacePath,
                            input.TargetBranch)),
                        TurnActivityOptions);

                    if (!string.IsNullOrWhiteSpace(fixOutcome.PrUrl))
                    {
                        activePrUrl = fixOutcome.PrUrl;
                    }

                    if (fixOutcome.Status == TurnOutcomeStatus.AwaitingInput)
                    {
                        _currentHeartbeat = "Awaiting input on PR review fixes";
                        await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0 || _prReview != null);
                        if (_prReview != null) break;
                        reviewFeedback = _chatInbox.Dequeue();
                        continue;
                    }

                    if (fixOutcome.Status == TurnOutcomeStatus.Continue)
                    {
                        _currentHeartbeat = "Continuing PR review fixes";
                        reviewFeedback = _chatInbox.Count > 0 ? _chatInbox.Dequeue() : null;
                        continue;
                    }

                    break;
                }

                continue;
            }

            prApproved = true;
        }

        // 5. Final Server-Side Merge Activity
        _currentHeartbeat = "Merging pull request";
        var mergePrRef = activePrUrl;
        if (string.IsNullOrWhiteSpace(mergePrRef) && _prReview?.PrNumber != null)
        {
            mergePrRef = _prReview.PrNumber.Value.ToString();
        }

        if (string.IsNullOrWhiteSpace(mergePrRef))
        {
            throw new ApplicationFailureException(
                "Merge requested without a pull request URL or number.", nonRetryable: true, errorType: "no_pr_url");
        }

        var mergeSummary = await Workflow.ExecuteActivityAsync(
            (IGitActivities act) => act.MergePullRequestAsync(mergePrRef),
            GitActivityOptions);

        _currentHeartbeat = "Job completed and merged";
        return new JobExecutionResult(Success: true, Message: mergeSummary ?? "Job completed and merged.");
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
        if (signal.Approved)
        {
            _legacyPlanApproval = new PlanApprovedSignal(signal.ApproverId, "operator", signal.Feedback, Workflow.UtcNow);
        }
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
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        if (signal is null || string.IsNullOrWhiteSpace(signal.Content))
        {
            return Task.CompletedTask;
        }

        if (IsSameAgentIdentity(signal.SenderId, _assignedAgentId))
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(signal.MessageId) && !_seenMessageIds.Add(signal.MessageId))
        {
            return Task.CompletedTask;
        }

        _chatInbox.Enqueue(signal);
        return Task.CompletedTask;
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
        _legacyPlanApproval = signal;
        _timelinePlanApproval = new PlanApprovalSignal(Guid.Empty, true, signal.Comments, signal.ApproverId);
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceivePrReviewAsync(PrReviewSignal signal)
    {
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
        new(_currentTaskIndex, _tasks.Count > 0 ? _tasks.Count : _totalTasks, _currentHeartbeat);
}
