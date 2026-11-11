using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Worker.Workflows;

[Workflow("TaskExecutionWorkflow")]
public class TaskExecutionWorkflow : ITaskExecutionWorkflow
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

    private AbortActiveTurnSignal? _abortSignal;
    private TurnInterventionSignal? _intervention;
    private CancellationTokenSource? _activeTurnCts;

    [WorkflowRun]
    public async Task<TaskExecutionResult> ExecuteTaskAsync(ChildTaskExecutionInput input)
    {
        var taskInput = new TaskExecutionInput(
            JobId: input.JobId,
            TaskId: input.TaskId,
            AgentId: input.AssignedAgentId,
            ProjectId: input.JobId,
            WorkspacePath: input.WorkspacePath,
            TargetBranch: input.TargetBranch
        );

        bool completed = false;
        int step = 0;
        ChatMessageSignal? pendingReply = null;

        while (!completed)
        {
            step++;
            await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.UpdateTaskStepCardAsync(
                    new TaskStepCardInput(input.JobId, input.TaskId, input.TaskIndex, input.TaskTitle, TimelineCardStatus.Active, $"Step {step}: In progress", null)),
                TimelineActivityOptions);

            try
            {
                using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(Workflow.CancellationToken);
                _activeTurnCts = turnCancellation;

                if (_abortSignal != null)
                {
                    turnCancellation.Cancel();
                }

                var outcome = await Workflow.ExecuteActivityAsync(
                    (IExecutionActivities act) => act.RunAgentCodingTurnAsync(taskInput, pendingReply),
                    new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromHours(1),
                        CancellationToken = turnCancellation.Token,
                        RetryPolicy = new RetryPolicy
                        {
                            MaximumAttempts = 1
                        }
                    });

                _activeTurnCts = null;

                if (outcome.Status == TurnOutcomeStatus.TaskCompleted)
                {
                    completed = true;
                    await Workflow.ExecuteActivityAsync(
                        (ITimelineActivities act) => act.UpdateTaskStepCardAsync(
                            new TaskStepCardInput(input.JobId, input.TaskId, input.TaskIndex, input.TaskTitle, TimelineCardStatus.Resolved, $"Step {step}: Completed", outcome.Summary)),
                        TimelineActivityOptions);
                    break;
                }
                else if (outcome.Status == TurnOutcomeStatus.Failed || outcome.Status == TurnOutcomeStatus.StuckLoopDetected)
                {
                    await Workflow.ExecuteActivityAsync(
                        (ITimelineActivities act) => act.RecordTurnInterruptedAsync(
                            new TurnInterruptedCardInput(input.JobId, input.TaskId, outcome.Summary ?? "Turn failed", "system")),
                        TimelineActivityOptions);

                    _intervention = null;
                    await Workflow.WaitConditionAsync(() => _intervention != null);

                    if (_intervention.Action == "realign")
                    {
                        return new TaskExecutionResult(false, $"Realigned: {_intervention.AdditionalContext}");
                    }
                    else // "resume" or "retry_step"
                    {
                        continue;
                    }
                }
                else
                {
                    completed = true;
                }
            }
            catch (Exception ex) when (IsActivityCancellation(ex))
            {
                _activeTurnCts = null;
                var reason = _abortSignal?.Reason ?? "Active turn stopped by operator";
                var requestedBy = _abortSignal?.RequestedBy ?? "operator";

                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.RecordTurnInterruptedAsync(
                        new TurnInterruptedCardInput(input.JobId, input.TaskId, reason, requestedBy)),
                    TimelineActivityOptions);

                _intervention = null;
                _abortSignal = null;
                await Workflow.WaitConditionAsync(() => _intervention != null);

                if (_intervention.Action == "realign")
                {
                    return new TaskExecutionResult(false, $"Realigned: {_intervention.AdditionalContext}");
                }
                else // "resume" or "retry_step"
                {
                    continue;
                }
            }
        }

        return new TaskExecutionResult(true, null);
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
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal) => Task.CompletedTask;

    [WorkflowSignal]
    public Task ReceivePrReviewSignalAsync(PrReviewSignal signal) => Task.CompletedTask;

    [WorkflowQuery]
    public TaskStatusView GetTaskStatus() =>
        new("Active", null, 1, TurnOutcomeStatus.Continue, 0, null, null);

    private static bool IsActivityCancellation(Exception ex)
    {
        return ex is ActivityFailureException afe && afe.InnerException is CanceledFailureException
            || ex is OperationCanceledException
            || ex is CanceledFailureException;
    }

    public const int MaxTurns = 10;

    public Task<TaskExecutionResult> ExecuteTaskAsync(TaskExecutionInput input) => ExecuteTaskAsync(
        new ChildTaskExecutionInput(input.JobId, input.TaskId, 0, input.TaskId, input.AgentId, input.WorkspacePath, input.TargetBranch));
}
