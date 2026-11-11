using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using Temporalio.Common;
using Temporalio.Workflows;

namespace AgentMesh.Worker.Workflows;

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

    private DiscoveryAnswersSignal? _discoveryAnswers;
    private PlanApprovalSignal? _planApproval;
    private AbortActiveTurnSignal? _pendingAbort;
    private TurnInterventionSignal? _pendingIntervention;
    private ChildWorkflowHandle<ITaskExecutionWorkflow, TaskExecutionResult>? _activeChildHandle;
    private string? _activeChildWorkflowId;
    private int _currentTaskIndex = 0;
    private int _totalTasks = 0;
    private string _currentHeartbeat = "Initializing";

    [WorkflowRun]
    public async Task<JobExecutionResult> RunAsync(JobExecutionInput input)
    {
        _currentHeartbeat = "Initializing job";

        // Create JobInitialized card
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

        // Mark discovery card approved/resolved
        await Workflow.ExecuteActivityAsync(
            (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                discoveryResult.CardId,
                TimelineCardStatus.Approved
            )),
            TimelineActivityOptions
        );

        // Stage 1B & Stage 2: Decomposition loop with plan approval gate
        _currentHeartbeat = "Awaiting plan approval";
        string? planFeedback = null;
        DecompositionActivityResult decompositionResult;

        while (true)
        {
            _planApproval = null;
            _currentHeartbeat = planFeedback == null ? "Decomposing job tasks" : "Re-decomposing plan with feedback";
            decompositionResult = await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.ExecuteDecompositionActivityAsync(new DecompositionActivityInput(
                    input.JobId,
                    input.InitialPrompt,
                    input.WorkspacePath,
                    planFeedback,
                    _discoveryAnswers.Answers
                )),
                TimelineActivityOptions
            );

            _currentHeartbeat = "Awaiting plan approval";
            await Workflow.WaitConditionAsync(() => _planApproval != null);

            if (_planApproval.Approved)
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
            else
            {
                // Stage 2: Rejected with feedback, re-run decomposition with feedback
                planFeedback = _planApproval.Feedback ?? "Plan rejected by approver";
                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                        decompositionResult.CardId,
                        TimelineCardStatus.Rejected
                    )),
                    TimelineActivityOptions
                );
            }
        }

        // Stage 3: Sequentially dispatch child workflows wf => wf.ExecuteTaskAsync(childInput) with ID job-{jobId}-task-{idx}
        var tasks = decompositionResult.Payload.Tasks;
        _totalTasks = tasks.Count;

        for (int idx = 0; idx < tasks.Count; idx++)
        {
            _currentTaskIndex = idx;
            var task = tasks[idx];
            _currentHeartbeat = $"Executing task {idx + 1}/{_totalTasks}: {task.Title}";

            // Clear any stale abort/intervention from prior completed tasks
            _pendingAbort = null;
            _pendingIntervention = null;

            var childInput = new ChildTaskExecutionInput(
                input.JobId,
                task.TaskId,
                idx,
                task.Title,
                input.AssignedAgentId,
                input.WorkspacePath,
                input.TargetBranch
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

            // If an abort was requested while starting this specific task, forward and clear it
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
                _currentHeartbeat = $"Failed on task {idx + 1}";
                return new JobExecutionResult(false, $"Task {idx} ({task.Title}) failed: {result.ErrorMessage}");
            }
        }

        _currentHeartbeat = "Completed all tasks successfully";
        return new JobExecutionResult(true, "All tasks completed successfully.");
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
        _planApproval = signal;
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
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal) => Task.CompletedTask;

    [WorkflowSignal]
    public Task ReceivePlanApprovalAsync(PlanApprovedSignal signal)
    {
        _planApproval = new PlanApprovalSignal(Guid.Empty, true, signal.Comments, signal.ApproverId);
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceivePrReviewAsync(PrReviewSignal signal) => Task.CompletedTask;

    [WorkflowSignal]
    public Task ReceiveSetChecklistAsync(List<ChecklistTaskItem> tasks) => Task.CompletedTask;

    [WorkflowSignal]
    public Task UpdateHeartbeatAsync(string heartbeat)
    {
        _currentHeartbeat = heartbeat;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public JobWorkflowState GetCurrentState() =>
        new(_currentTaskIndex, _totalTasks, _currentHeartbeat);

    public Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input) => RunAsync(input);
}
