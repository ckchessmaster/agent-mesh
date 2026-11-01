using System.Security.Claims;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Temporalio.Client;

namespace AgentMesh.Api.Controllers;

public record ApprovePlanRequest(
    string? ApproverId = null,
    string? ApproverRole = null,
    string? Comments = null
);

public record SubmitJobPrReviewRequest(
    string? ReviewerId = null,
    bool IsApproved = true,
    string? ReviewComments = null,
    int? PrNumber = null
);

[ApiController]
[Route("api/jobs")]
public class JobChatController(
    AgentMeshDbContext dbContext,
    ITemporalClient? temporalClient = null,
    IHubContext<AgentMeshHub>? hubContext = null,
    ILogger<JobChatController>? logger = null) : ControllerBase
{
    private string CurrentUserId =>
        User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User?.FindFirst("AgentId")?.Value
        ?? User?.FindFirst("PersonaId")?.Value
        ?? "operator";

    [HttpPost("{jobId}/chat")]
    public async Task<IActionResult> PostChatMessage(string jobId, [FromBody] PostMessageRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "Message content is required." });
        }

        Guid? parsedGuid = Guid.TryParse(jobId, out var g) ? g : null;

        // Try finding as AgentJob first, then ProjectJob
        AgentJob? agentJob = null;
        ProjectJob? projectJob = null;

        if (parsedGuid.HasValue)
        {
            agentJob = await dbContext.AgentJobs.FindAsync([parsedGuid.Value]);
            if (agentJob is null)
            {
                projectJob = await dbContext.ProjectJobs.FindAsync([parsedGuid.Value]);
            }
        }

        if (agentJob is null && projectJob is null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found." });
        }

        var senderId = !string.IsNullOrWhiteSpace(request.SenderAgentId)
            ? request.SenderAgentId.Trim()
            : CurrentUserId;

        // 1. Record message to job_messages ledger and broadcast via SignalR
        var messageRecord = new JobMessageRecord(jobId, senderId, request.Content, DateTimeOffset.UtcNow);

        if (parsedGuid.HasValue)
        {
            var message = new JobMessage
            {
                Id = Guid.NewGuid(),
                JobId = parsedGuid.Value,
                SenderType = string.Equals(senderId, "operator", StringComparison.OrdinalIgnoreCase) || string.Equals(senderId, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "persona",
                SenderId = senderId,
                Content = request.Content,
                CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.JobMessages.Add(message);
            await dbContext.SaveChangesAsync();
        }

        if (hubContext is not null)
        {
            await hubContext.Clients.Group(jobId).SendAsync("ReceiveJobChatMessage", messageRecord);
            await hubContext.Clients.All.SendAsync("ReceiveJobChatMessage", messageRecord);
        }

        // 2. SignalWithStart to ensure Temporal workflow handles the message
        if (temporalClient is not null)
        {
            var workflowId = $"job-{jobId}";
            var assignedAgentId = agentJob?.AssignedAgentId ?? projectJob?.AssignedAgentId ?? "implementer";
            var projectId = agentJob?.ProjectId ?? projectJob?.ProjectId ?? "default";
            var workspacePath = agentJob?.WorkingDirectory ?? projectJob?.WorkingDirectory ?? $"/workspace/repos/{projectId}";
            var targetBranch = agentJob?.TargetBranch ?? projectJob?.TargetBranch ?? "main";
            var approvalPolicy = agentJob?.ApprovalPolicy ?? projectJob?.ApprovalPolicy ?? PlanApprovalPolicy.LeadOrHuman;

            try
            {
                var chatSignal = new ChatMessageSignal(
                    SenderId: senderId,
                    Content: request.Content,
                    Timestamp: DateTimeOffset.UtcNow
                );

                var options = new WorkflowOptions(workflowId, "job-execution-queue")
                {
                    StartSignal = "ReceiveChatMessage",
                    StartSignalArgs = new object?[] { chatSignal }
                };

                try
                {
                    await temporalClient.StartWorkflowAsync(
                        (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                            JobId: jobId,
                            AssignedAgentId: assignedAgentId,
                            ProjectId: projectId,
                            WorkspacePath: workspacePath,
                            InitialPrompt: request.Content,
                            TargetBranch: targetBranch,
                            ApprovalPolicy: approvalPolicy
                        )),
                        options
                    );
                }
                catch (Temporalio.Exceptions.WorkflowAlreadyStartedException)
                {
                    var handle = temporalClient.GetWorkflowHandle<JobExecutionWorkflow>(workflowId);
                    await handle.SignalAsync(wf => wf.ReceiveChatMessageAsync(chatSignal));
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to SignalWithStart workflow for job {JobId}", jobId);
            }
        }

        return Ok(new { success = true });
    }

    [HttpPost("{jobId}/approve-plan")]
    public async Task<IActionResult> ApprovePlan(string jobId, [FromBody] ApprovePlanRequest? request = null)
    {
        Guid? parsedGuid = Guid.TryParse(jobId, out var g) ? g : null;

        AgentJob? agentJob = null;
        ProjectJob? projectJob = null;

        if (parsedGuid.HasValue)
        {
            agentJob = await dbContext.AgentJobs.FindAsync([parsedGuid.Value]);
            if (agentJob is null)
            {
                projectJob = await dbContext.ProjectJobs.FindAsync([parsedGuid.Value]);
            }
        }

        if (agentJob is null && projectJob is null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found." });
        }

        var policy = agentJob?.ApprovalPolicy ?? projectJob?.ApprovalPolicy ?? PlanApprovalPolicy.LeadOrHuman;

        var approverId = !string.IsNullOrWhiteSpace(request?.ApproverId)
            ? request.ApproverId.Trim()
            : CurrentUserId;

        var approverRole = !string.IsNullOrWhiteSpace(request?.ApproverRole)
            ? request.ApproverRole.Trim().ToLowerInvariant()
            : (string.Equals(approverId, "operator", StringComparison.OrdinalIgnoreCase) ? "operator" : "tech_lead");

        if (policy == PlanApprovalPolicy.HumanOnly && approverRole != "operator")
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "human_only_policy",
                message = "Plan approval requires an authenticated Human Operator sign-off."
            });
        }

        var signal = new PlanApprovedSignal(
            ApproverId: approverId,
            ApproverRole: approverRole,
            Comments: request?.Comments,
            Timestamp: DateTimeOffset.UtcNow
        );

        if (temporalClient is not null)
        {
            var workflowId = $"job-{jobId}";
            try
            {
                var handle = temporalClient.GetWorkflowHandle<JobExecutionWorkflow>(workflowId);
                await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(signal));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to deliver PlanApprovedSignal to workflow {WorkflowId}", workflowId);
            }
        }

        if (agentJob is not null)
        {
            agentJob.ProgressHeartbeat = "Plan approved. Starting implementation";
            agentJob.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }
        else if (projectJob is not null)
        {
            projectJob.ProgressHeartbeat = "Plan approved. Starting implementation";
            projectJob.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }

        if (hubContext is not null)
        {
            await hubContext.Clients.All.SendAsync("JobPlanApproved", new { jobId, approverId, approverRole });
            await hubContext.Clients.Group(jobId).SendAsync("JobPlanApproved", new { jobId, approverId, approverRole });
        }

        return Ok(new { success = true, approverId, approverRole });
    }

    [HttpPost("{jobId}/reviews")]
    public async Task<IActionResult> SubmitReview(string jobId, [FromBody] SubmitJobPrReviewRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Review request body is required." });
        }

        Guid? parsedGuid = Guid.TryParse(jobId, out var g) ? g : null;

        AgentJob? agentJob = null;
        ProjectJob? projectJob = null;

        if (parsedGuid.HasValue)
        {
            agentJob = await dbContext.AgentJobs.FindAsync([parsedGuid.Value]);
            if (agentJob is null)
            {
                projectJob = await dbContext.ProjectJobs.FindAsync([parsedGuid.Value]);
            }
        }

        if (agentJob is null && projectJob is null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found." });
        }

        var reviewer = !string.IsNullOrWhiteSpace(request.ReviewerId)
            ? request.ReviewerId.Trim().ToLowerInvariant()
            : CurrentUserId.ToLowerInvariant();

        var author = (agentJob?.AssignedAgentId ?? projectJob?.AssignedAgentId ?? "implementer").ToLowerInvariant();

        // Self-review guard
        if (string.Equals(reviewer, author, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "self_review_not_allowed",
                message = $"'{reviewer}' is the author of this pull request and cannot approve its own merge."
            });
        }

        var signal = new PrReviewSignal(
            ReviewerId: reviewer,
            IsApproved: request.IsApproved,
            ReviewComments: request.ReviewComments,
            Timestamp: DateTimeOffset.UtcNow
        );

        if (temporalClient is not null)
        {
            var workflowId = $"job-{jobId}";
            try
            {
                var handle = temporalClient.GetWorkflowHandle<JobExecutionWorkflow>(workflowId);
                await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(signal));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to deliver PrReviewSignal to workflow {WorkflowId}", workflowId);
            }
        }

        return Ok(new { success = true, isApproved = request.IsApproved, reviewer });
    }
}
