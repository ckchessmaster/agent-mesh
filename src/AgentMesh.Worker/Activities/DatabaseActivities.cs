using AgentMesh.Core.Activities;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class DatabaseActivities(AgentMeshDbContext dbContext, ILogger<DatabaseActivities> logger) : IDatabaseActivities
{
    [Activity]
    public async Task CreateStagedJobRecord(AgentTaskWorkflowInput input)
    {
        var job = new AgentJob
        {
            Id = input.JobId,
            SessionId = input.SessionId,
            RequestedByAgentId = input.RequestedByAgentId,
            TargetService = input.TargetService,
            ActionVerb = input.ActionVerb,
            PayloadJson = input.PayloadJson,
            ApprovalStatus = input.TargetService == "platform_ops" ? ApprovalStatus.AutoApproved : ApprovalStatus.PendingHuman,
            ExecutionStatus = ExecutionStatus.Staged,
            TemporalWorkflowId = $"job-{input.JobId}"
        };

        dbContext.AgentJobs.Add(job);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persisted staged job {JobId}", input.JobId);
    }

    [Activity]
    public async Task UpdateApprovalStatus(Guid jobId, string approvalStatus)
    {
        var job = await dbContext.AgentJobs.FindAsync(jobId);
        if (job != null)
        {
            job.ApprovalStatus = Enum.Parse<ApprovalStatus>(approvalStatus, ignoreCase: true);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }

    [Activity]
    public async Task UpdateExecutionStatus(Guid jobId, string executionStatus)
    {
        var job = await dbContext.AgentJobs.FindAsync(jobId);
        if (job != null)
        {
            job.ExecutionStatus = Enum.Parse<ExecutionStatus>(executionStatus, ignoreCase: true);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }

    [Activity]
    public async Task UpdateJobStatus(Guid jobId, string executionStatus, string? resultJson, string? errorMessage)
    {
        var job = await dbContext.AgentJobs.FindAsync(jobId);
        if (job != null)
        {
            job.ExecutionStatus = Enum.Parse<ExecutionStatus>(executionStatus, ignoreCase: true);
            job.ResultJson = resultJson;
            job.ErrorMessage = errorMessage;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }
}
