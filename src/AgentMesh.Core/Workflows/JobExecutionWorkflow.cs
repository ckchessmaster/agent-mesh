using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using Temporalio.Common;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[Workflow]
public class JobExecutionWorkflow : IJobExecutionWorkflow
{
    private static readonly ActivityOptions ActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 3,
            NonRetryableErrorTypes = ["script_exhausted", "halt"]
        }
    };

    private readonly Queue<ChatMessageSignal> _chatInbox = new();
    private PlanApprovedSignal? _planApproval;
    private PrReviewSignal? _prReview;
    private List<ChecklistTaskItem> _tasks = new();
    private int _currentTaskIndex = 0;
    private string _currentHeartbeat = "Initializing job";

    [WorkflowRun]
    [WorkflowMethod]
    public async Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input)
    {
        // 1. Stage 1: Discovery Turn
        _currentHeartbeat = "Exploring codebase and generating checklist";
        var discoveryOutcome = await Workflow.ExecuteActivityAsync(
            (IExecutionActivities act) => act.RunAgentDiscoveryTurnAsync(new DiscoveryTurnInput(input.JobId, input.AssignedAgentId, input.WorkspacePath, input.InitialPrompt)),
            ActivityOptions);

        _currentHeartbeat = "Awaiting plan approval";

        // 2. Stage 2: Plan Approval Gate
        await Workflow.WaitConditionAsync(() =>
        {
            if (_planApproval == null) return false;
            return input.ApprovalPolicy switch
            {
                PlanApprovalPolicy.LeadOrHuman => 
                    _planApproval.ApproverRole is "operator" or "tech_lead",
                PlanApprovalPolicy.HumanOnly => 
                    _planApproval.ApproverRole == "operator",
                _ => false
            };
        });

        _currentHeartbeat = "Plan approved. Starting implementation";

        // 3. Stage 3: Autonomous Task Iteration Loop
        while (_currentTaskIndex < _tasks.Count)
        {
            var currentTask = _tasks[_currentTaskIndex];
            _currentHeartbeat = $"Executing task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";

            // Check for inbound chat interjections
            ChatMessageSignal? pendingMessage = _chatInbox.Count > 0 ? _chatInbox.Dequeue() : null;

            var turnOutcome = await Workflow.ExecuteActivityAsync(
                (IExecutionActivities act) => act.RunAgentCodingTurnAsync(new CodingTurnInput(input.JobId, currentTask, pendingMessage)),
                ActivityOptions);

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

            if (turnOutcome.Status == TurnOutcomeStatus.Continue || turnOutcome.Status == TurnOutcomeStatus.TaskCompleted)
            {
                _currentHeartbeat = $"Completed task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";
                _currentTaskIndex++;
            }
        }

        // 4. Stage 4: Pull Request & Mandatory Review Gate
        _currentHeartbeat = "Awaiting pull request review";
        await Workflow.WaitConditionAsync(() => _prReview != null);

        if (!_prReview!.IsApproved)
        {
            // Changes requested; re-enter coding turn
            _currentHeartbeat = "Changes requested on PR; resuming coding turn";
            _chatInbox.Enqueue(new ChatMessageSignal(_prReview.ReviewerId, _prReview.ReviewComments ?? "Changes requested", Workflow.UtcNow));
            // Loop back to coding activities...
        }

        // 5. Final Server-Side Merge Activity
        _currentHeartbeat = "Merging pull request";
        await Workflow.ExecuteActivityAsync(
            (IGitActivities act) => act.MergePullRequestAsync(new MergePullRequestInput(input.JobId, input.TargetBranch)),
            ActivityOptions);

        _currentHeartbeat = "Job completed and merged";
        return new JobExecutionResult(Success: true, Summary: "Job completed and merged.");
    }

    [WorkflowSignal]
    [SignalMethod]
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        _chatInbox.Enqueue(signal);
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    [SignalMethod]
    public Task ReceivePlanApprovalAsync(PlanApprovedSignal signal)
    {
        _planApproval = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    [SignalMethod]
    public Task ReceivePrReviewAsync(PrReviewSignal signal)
    {
        _prReview = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceiveSetChecklistAsync(List<ChecklistTaskItem> tasks)
    {
        _tasks = tasks ?? new();
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task UpdateHeartbeatAsync(string heartbeat)
    {
        _currentHeartbeat = heartbeat;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    [QueryMethod]
    public JobWorkflowState GetCurrentState() =>
        new(_currentTaskIndex, _tasks.Count, _currentHeartbeat);
}
