using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

public interface IExecutionActivities
{
    [Activity]
    Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson, Guid? jobId = null);
}
