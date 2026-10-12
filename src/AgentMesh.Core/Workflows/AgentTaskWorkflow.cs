using Temporalio.Workflows;
using AgentMesh.Core.Activities;

namespace AgentMesh.Core.Workflows;

[Workflow]
public class AgentTaskWorkflow
{
    private bool _approvalReceived = false;
    private bool _isApproved = false;

    [WorkflowSignal]
    public Task SubmitApproval(bool approved)
    {
        _isApproved = approved;
        _approvalReceived = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<string> RunAsync(AgentTaskWorkflowInput input)
    {
        // 1. Persist initial staged job record
        await Workflow.ExecuteActivityAsync(
            (IDatabaseActivities act) => act.CreateStagedJobRecord(input),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        // 2. Routing logic: platform_ops auto-approves; others await approval (Phase 4 integration)
        bool requiresApproval = input.TargetService != "platform_ops";
        if (requiresApproval)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "pending_human"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            // Durable wait for approval signal (up to 7 days)
            await Workflow.WaitConditionAsync(() => _approvalReceived, TimeSpan.FromDays(7));

            if (!_isApproved)
            {
                await Workflow.ExecuteActivityAsync(
                    (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "rejected", null, "Rejected by operator"),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
                return "Task rejected by operator.";
            }

            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "approved"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
        }

        // 3. Subsystem execution
        try
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateExecutionStatus(input.JobId, "running"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            var result = await Workflow.ExecuteActivityAsync(
                (IExecutionActivities act) => act.ExecuteSubsystemAction(input.TargetService, input.ActionVerb, input.PayloadJson),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new() { InitialInterval = TimeSpan.FromSeconds(2), MaximumAttempts = 3 }
                });

            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "completed", result, null),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            return result;
        }
        catch (Exception ex)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "failed", null, ex.Message),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
            throw;
        }
    }
}
