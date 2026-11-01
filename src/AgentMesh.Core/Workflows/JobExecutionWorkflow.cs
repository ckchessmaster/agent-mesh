using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[Workflow]
public class JobExecutionWorkflow : IJobExecutionWorkflow
{
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
    private PlanApprovedSignal? _planApproval;
    private PrReviewSignal? _prReview;
    private List<ChecklistTaskItem> _tasks = new();
    private int _currentTaskIndex = 0;
    private string _currentHeartbeat = "Initializing job";

    [WorkflowRun]
    public async Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input)
    {
        // 1. Stage 1: Discovery Turn
        _currentHeartbeat = "Exploring codebase and generating checklist";
        var discoveryOutcome = await Workflow.ExecuteActivityAsync(
            (IExecutionActivities act) => act.RunAgentDiscoveryTurnAsync(new DiscoveryTurnInput(
                input.JobId,
                input.AssignedAgentId,
                input.WorkspacePath,
                input.InitialPrompt,
                input.ProjectId,
                input.TargetBranch)),
            TurnActivityOptions);

        // Discovery turn consumed the initial prompt; clear inbox so it is not re-processed in Stage 3
        _chatInbox.Clear();

        _currentHeartbeat = "Awaiting plan approval";

        // 2. Stage 2: Plan Approval Gate
        await Workflow.WaitConditionAsync(() =>
        {
            if (_tasks.Count == 0 || _planApproval == null) return false;
            var isApproved = input.ApprovalPolicy switch
            {
                PlanApprovalPolicy.LeadOrHuman =>
                    _planApproval.ApproverRole is "operator" or "tech_lead",
                PlanApprovalPolicy.HumanOnly =>
                    _planApproval.ApproverRole == "operator",
                _ => false
            };

            if (!isApproved)
            {
                // Explicitly discard rejected approval signal so it does not linger
                var rejectedRole = _planApproval.ApproverRole;
                _planApproval = null;
                _currentHeartbeat = $"Plan approval rejected: role '{rejectedRole}' is not permitted by policy {input.ApprovalPolicy}";
                return false;
            }

            return true;
        });

        _currentHeartbeat = "Plan approved. Starting implementation";

        string? activePrUrl = null;

        // 3. Stage 3: Autonomous Task Iteration Loop
        while (_currentTaskIndex < _tasks.Count)
        {
            var currentTask = _tasks[_currentTaskIndex];
            _currentHeartbeat = $"Executing task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";

            // Check for inbound chat interjections
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
                return new JobExecutionResult(Success: false, Summary: $"Task failed: {turnOutcome.Message}");
            }

            if (turnOutcome.Status is TurnOutcomeStatus.Continue or TurnOutcomeStatus.TaskCompleted or TurnOutcomeStatus.PrOpenedAwaitingReview)
            {
                _currentHeartbeat = $"Completed task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";
                _currentTaskIndex++;
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
                // Changes requested; re-enter coding turn
                _currentHeartbeat = "Changes requested on PR; resuming coding turn";
                var reviewFeedback = new ChatMessageSignal(
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

                continue;
            }

            prApproved = true;
        }

        // 5. Final Server-Side Merge Activity (reached ONLY when prApproved == true)
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
        return new JobExecutionResult(Success: true, Summary: mergeSummary ?? "Job completed and merged.");
    }

    [WorkflowSignal]
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        _chatInbox.Enqueue(signal);
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceivePlanApprovalAsync(PlanApprovedSignal signal)
    {
        _planApproval = signal;
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
        _currentHeartbeat = "Checklist registered. Awaiting plan approval.";
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
        new(_currentTaskIndex, _tasks.Count, _currentHeartbeat);
}
