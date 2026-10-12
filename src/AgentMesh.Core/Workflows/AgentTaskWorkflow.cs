using Temporalio.Workflows;
using AgentMesh.Core.Activities;

namespace AgentMesh.Core.Workflows;

[Workflow]
public class AgentTaskWorkflow
{
    private bool _approvalReceived = false;
    private bool _isApproved = false;
    private string? _reviewer;
    private string? _rejectionReason;

    [WorkflowSignal]
    public Task SubmitApproval(bool approved, string reviewer, string? reason = null)
    {
        _isApproved = approved;
        _reviewer = reviewer;
        _rejectionReason = reason;
        _approvalReceived = true;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<string> RunAsync(AgentTaskWorkflowInput input)
    {
        // 1. Stage initial record
        await Workflow.ExecuteActivityAsync(
            (IDatabaseActivities act) => act.CreateStagedJobRecord(input),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        // 2. High risk boundary evaluation
        bool requiresHumanReview = input.TargetService != "platform_ops";

        if (requiresHumanReview)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "PendingHuman"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            // Send interactive Discord notification
            await Workflow.ExecuteActivityAsync(
                (INotificationActivities act) => act.SendDiscordApprovalRequest(input),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) });

            // Durable pause up to 7 days
            await Workflow.WaitConditionAsync(() => _approvalReceived, TimeSpan.FromDays(7));

            if (!_isApproved)
            {
                var reason = _rejectionReason ?? "Rejected by operator";
                await Workflow.ExecuteActivityAsync(
                    (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "Rejected"),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

                await Workflow.ExecuteActivityAsync(
                    (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "Failed", null, reason),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

                return $"Task rejected by {_reviewer}: {reason}";
            }

            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "Approved"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
        }

        // 3. Execution Phase
        try
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateExecutionStatus(input.JobId, "Running"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            var result = await Workflow.ExecuteActivityAsync(
                (IExecutionActivities act) => act.ExecuteSubsystemAction(input.TargetService, input.ActionVerb, input.PayloadJson),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new() { InitialInterval = TimeSpan.FromSeconds(2), MaximumAttempts = 3 }
                });

            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "Completed", result, null),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            return result;
        }
        catch (Exception ex)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "Failed", null, ex.Message),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
            throw;
        }
    }
}
