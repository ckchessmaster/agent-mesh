using AgentMesh.Core.Workflows;
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

public interface INotificationActivities
{
    [Activity]
    Task SendDiscordApprovalRequest(AgentTaskWorkflowInput input);
}
