using AgentMesh.Core.Workflows;
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

public interface IDatabaseActivities
{
    [Activity]
    Task CreateStagedJobRecord(AgentTaskWorkflowInput input);

    [Activity]
    Task UpdateApprovalStatus(Guid jobId, string approvalStatus);

    [Activity]
    Task UpdateExecutionStatus(Guid jobId, string executionStatus);

    [Activity]
    Task UpdateJobStatus(Guid jobId, string executionStatus, string? resultJson, string? errorMessage, string? resultS3Uri = null, string? artifactsManifestJson = null);
}
