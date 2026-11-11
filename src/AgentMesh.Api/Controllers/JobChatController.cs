using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Temporalio.Client;
using Temporalio.Common;

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
[Authorize]
public class JobChatController(
    AgentMeshDbContext dbContext,
    ITemporalClient? temporalClient = null,
    IHubContext<AgentMeshHub>? hubContext = null,
    ILogger<JobChatController>? logger = null,
    JobTurnDispatcher? turnDispatcher = null,
    IWorkflowSignalRouter? workflows = null) : ControllerBase
{
    private static readonly HashSet<string> TechLeadRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tech Lead",
        "tech_lead",
        "TechLead",
        "Technical Lead"
    };

    private static readonly HashSet<string> OperatorRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Operator",
        "operator",
        "Admin",
        "admin"
    };

    private string? ResolveCallerId()
    {
        var claim = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User?.FindFirst("AgentId")?.Value
                    ?? User?.FindFirst("agent_id")?.Value
                    ?? User?.FindFirst("PersonaId")?.Value
                    ?? User?.FindFirst(ClaimTypes.Name)?.Value
                    ?? User?.FindFirst(ClaimTypes.Email)?.Value
                    ?? User?.FindFirst("email")?.Value
                    ?? User?.FindFirst("sub")?.Value
                    ?? User?.Identity?.Name;

        if (!string.IsNullOrWhiteSpace(claim))
        {
            return claim.Trim();
        }

        return null;
    }

    private async Task<string> ResolveCallerRoleAsync(string callerId)
    {
        // 1. Check roles/claims on the authenticated principal
        if (User.IsInRole("Operator") || User.IsInRole("Admin") ||
            User.HasClaim("IsMasterKey", "true") ||
            User.Claims.Any(c => (c.Type == ClaimTypes.Role || c.Type.Equals("role", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("roles", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("groups", StringComparison.OrdinalIgnoreCase) || c.Type.EndsWith("/role", StringComparison.OrdinalIgnoreCase)) && OperatorRoles.Contains(c.Value)))
        {
            return "operator";
        }

        // An explicit agent token / agent principal is not an operator unless granted an operator role above
        var isExplicitAgent = User.HasClaim(c => c.Type == "agent_id" || c.Type == "AgentId" || c.Type == "persona_id" || c.Type == "PersonaId");

        // Authenticated human users (e.g. from OIDC with email address)
        if (!isExplicitAgent && (User.HasClaim(c => c.Type == ClaimTypes.Email || c.Type.Equals("email", StringComparison.OrdinalIgnoreCase)) || callerId.Contains('@')))
        {
            return "operator";
        }

        if (User.IsInRole("TechLead") || User.IsInRole("tech_lead") ||
            User.Claims.Any(c => (c.Type == ClaimTypes.Role || c.Type.Equals("role", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("roles", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("groups", StringComparison.OrdinalIgnoreCase) || c.Type.EndsWith("/role", StringComparison.OrdinalIgnoreCase)) && TechLeadRoles.Contains(c.Value)))
        {
            return "tech_lead";
        }

        // 2. Check persona in database to determine if they hold the tech_lead role
        var persona = await dbContext.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == callerId.ToLowerInvariant());
        if (persona != null && !string.IsNullOrWhiteSpace(persona.Role) && TechLeadRoles.Contains(persona.Role.Trim()))
        {
            return "tech_lead";
        }

        return "agent";
    }

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
            projectJob = await dbContext.ProjectJobs.FindAsync([parsedGuid.Value]);
        }

        if (agentJob is null && projectJob is null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found." });
        }

        var senderId = ResolveCallerId();
        if (string.IsNullOrWhiteSpace(senderId))
        {
            return Unauthorized(new { error = "unauthorized", message = "Caller identity claim is missing." });
        }

        // 1. Record message to job_messages ledger and broadcast via SignalR group
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
        }

        // 2. SignalWithStart to ensure Temporal workflow handles the message
        if (temporalClient is not null)
        {
            var workflowId = $"job-{jobId}";
            var projectId = agentJob?.ProjectId ?? projectJob?.ProjectId ?? "default";
            var assignedAgentId = agentJob?.AssignedAgentId ?? projectJob?.AssignedAgentId;
            if (string.IsNullOrWhiteSpace(assignedAgentId) || assignedAgentId.Equals("implementer", StringComparison.OrdinalIgnoreCase))
            {
                var prj = await dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
                assignedAgentId = prj?.DefaultLeadPersonaId;
            }
            if (string.IsNullOrWhiteSpace(assignedAgentId) || assignedAgentId.Equals("implementer", StringComparison.OrdinalIgnoreCase))
            {
                var anyPersona = await dbContext.Personas.AsNoTracking().Select(p => p.Id).FirstOrDefaultAsync();
                assignedAgentId = anyPersona ?? "astra";
            }
            var workspacePath = agentJob?.WorkingDirectory ?? projectJob?.WorkingDirectory ?? $"/workspace/repos/{projectId}";
            var targetBranch = agentJob?.TargetBranch ?? projectJob?.TargetBranch ?? "main";
            var approvalPolicy = agentJob?.ApprovalPolicy ?? projectJob?.ApprovalPolicy ?? PlanApprovalPolicy.LeadOrHuman;

            var chatSignal = new ChatMessageSignal(
                SenderId: senderId,
                Content: request.Content,
                Timestamp: DateTimeOffset.UtcNow
            );

            var options = new WorkflowOptions(workflowId, WorkflowSignalRouter.TaskQueue)
            {
                IdReusePolicy = Temporalio.Api.Enums.V1.WorkflowIdReusePolicy.AllowDuplicate,
                StartSignal = "ReceiveChatMessage",
                StartSignalArgs = new object?[] { chatSignal },
                Memo = new Dictionary<string, object> { ["personaId"] = assignedAgentId }
            };

            try
            {
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
                logger?.LogError(ex, "Failed to dispatch or signal workflow for job {JobId}", jobId);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "workflow_dispatch_failed",
                    message = $"Failed to dispatch or signal workflow {workflowId}: {ex.Message}"
                });
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
            projectJob = await dbContext.ProjectJobs.FindAsync([parsedGuid.Value]);
        }

        if (agentJob is null && projectJob is null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found." });
        }

        var approverId = ResolveCallerId() ?? request?.ApproverId?.Trim();
        if (string.IsNullOrWhiteSpace(approverId))
        {
            return Unauthorized(new { error = "unauthorized", message = "Caller identity claim is missing." });
        }

        var policy = agentJob?.ApprovalPolicy ?? projectJob?.ApprovalPolicy ?? PlanApprovalPolicy.LeadOrHuman;
        var approverRole = await ResolveCallerRoleAsync(approverId);

        // Self-approval guard: an implementer cannot approve their own plan
        var implementer = (agentJob?.AssignedAgentId ?? projectJob?.AssignedAgentId)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(implementer) && string.Equals(approverId.ToLowerInvariant(), implementer, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "self_approval_not_allowed",
                message = $"'{approverId}' is the assigned implementer of this job and cannot approve its own plan."
            });
        }

        if (policy == PlanApprovalPolicy.HumanOnly && approverRole != "operator")
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "human_only_policy",
                message = "Plan approval requires an authenticated Human Operator sign-off."
            });
        }

        if (policy == PlanApprovalPolicy.LeadOrHuman && approverRole is not ("operator" or "tech_lead"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "unauthorized_role",
                message = $"Role '{approverRole}' is not authorized to approve plan under policy '{policy}'."
            });
        }

        var signal = new PlanApprovedSignal(
            ApproverId: approverId,
            ApproverRole: approverRole,
            Comments: request?.Comments,
            Timestamp: DateTimeOffset.UtcNow
        );

        if (workflows != null && parsedGuid.HasValue)
        {
            try
            {
                var delivery = await workflows.SendPlanApprovalAsync(parsedGuid.Value, signal);
                if (delivery.Status == SignalDeliveryStatus.Rejected)
                {
                    logger?.LogWarning("Plan approval signal for {WorkflowId} was rejected: {Detail}", delivery.WorkflowId, delivery.Detail);
                }
            }
            catch (Exception ex)
            {
                logger?.LogInformation(ex, "Could not signal Temporal workflow for job {JobId}; proceeding with direct dispatch.", jobId);
            }
        }
        else if (temporalClient is not null)
        {
            var workflowId = $"job-{jobId}";
            try
            {
                var handle = temporalClient.GetWorkflowHandle<IJobExecutionWorkflow>(workflowId);
                await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(signal));
            }
            catch (Exception ex)
            {
                logger?.LogInformation(ex, "Failed to deliver PlanApprovedSignal to workflow {WorkflowId}; proceeding with direct dispatch.", workflowId);
            }
        }

        if (agentJob is not null)
        {
            agentJob.ProgressHeartbeat = "Plan approved. Starting implementation";
            if (string.Equals(agentJob.CurrentTaskId, "stage-1-discovery", StringComparison.OrdinalIgnoreCase))
            {
                agentJob.CurrentTaskId = null;
            }
            agentJob.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (projectJob is not null)
        {
            projectJob.ProgressHeartbeat = "Plan approved. Starting implementation";
            if (string.Equals(projectJob.CurrentTaskId, "stage-1-discovery", StringComparison.OrdinalIgnoreCase))
            {
                var parsedTasks = ProjectJobsController.ParseTasks(projectJob.TasksJson);
                var firstTask = parsedTasks.FirstOrDefault();
                projectJob.CurrentTaskId = firstTask != null
                    ? (firstTask.Id != Guid.Empty ? firstTask.Id.ToString("D") : firstTask.Title)
                    : null;
            }
            projectJob.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync();

        if (hubContext is not null)
        {
            await hubContext.Clients.Group(jobId).SendAsync("JobPlanApproved", new { jobId, approverId, approverRole });

            var lifecycleStatus = projectJob != null ? ProjectJobsController.DeriveLifecycleStatus(projectJob) : "InProgress";
            var currentTaskId = projectJob?.CurrentTaskId ?? agentJob?.CurrentTaskId;
            await hubContext.Clients.All.SendAsync("ProgressHeartbeatUpdated", new
            {
                jobId = parsedGuid ?? (Guid.TryParse(jobId, out var parsedJid) ? parsedJid : Guid.Empty),
                progressHeartbeat = "Plan approved. Starting implementation",
                currentTaskId = currentTaskId,
                lifecycleStatus = lifecycleStatus
            });

            if (projectJob != null)
            {
                var project = await dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectJob.ProjectId);
                var msgCount = await dbContext.JobMessages.CountAsync(m => m.JobId == projectJob.Id);
                var dto = ProjectJobsController.ToDto(projectJob, project?.Name, msgCount);
                await hubContext.Clients.All.SendAsync("JobUpdated", dto);
            }
        }

        if (projectJob != null && turnDispatcher != null)
        {
            var isDriving = workflows != null && await workflows.IsWorkflowDrivingAsync(projectJob.Id, projectJob.AssignedAgentId);
            if (!isDriving)
            {
                var project = await dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectJob.ProjectId);
                var assignedId = !string.IsNullOrWhiteSpace(projectJob.AssignedAgentId) ? projectJob.AssignedAgentId.Trim().ToLowerInvariant() : null;
                var persona = assignedId != null ? await dbContext.Personas.FirstOrDefaultAsync(p => p.Id == assignedId) : null;
                persona ??= await dbContext.Personas.FirstOrDefaultAsync();

                if (persona != null && project != null)
                {
                    var tasks = ProjectJobsController.ParseTasks(projectJob.TasksJson);
                    var firstTask = tasks.FirstOrDefault(t => !t.IsCompleted);
                    var targetTaskId = firstTask?.Id != null && firstTask.Id != Guid.Empty
                        ? firstTask.Id.ToString("D")
                        : (firstTask?.Title ?? projectJob.CurrentTaskId);

                    var prompt = !string.IsNullOrWhiteSpace(firstTask?.Title)
                        ? $"Plan approved by {approverRole} (@{approverId}). Proceed with implementing task: {firstTask.Title}."
                        : "Plan approved. Proceed with implementation.";

                    try
                    {
                        await turnDispatcher.DispatchAsync(new JobTurnRequest(
                            projectJob,
                            project,
                            persona,
                            prompt,
                            Sender: approverId,
                            TaskId: targetTaskId
                        ));
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Failed to dispatch approved implementation turn for job {JobId}", jobId);
                    }
                }
            }
        }

        if (projectJob != null && hubContext != null && !string.IsNullOrWhiteSpace(projectJob.AssignedAgentId))
        {
            await ProjectJobsController.CheckAndDispatchQueuedJobTurnsAsync(dbContext, hubContext, projectJob.AssignedAgentId);
        }

        return Ok(new { success = true, approverId, approverRole });
    }

    [HttpPost("{jobId}/reviews")]
    [HttpPost("{jobId}/pr-review")]
    public async Task<IActionResult> SubmitReview(string jobId, [FromBody] SubmitJobPrReviewRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Review request body is required." });
        }

        var reviewer = ResolveCallerId()?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            return Unauthorized(new { error = "unauthorized", message = "Caller identity claim is missing." });
        }

        Guid? parsedGuid = Guid.TryParse(jobId, out var g) ? g : null;

        AgentJob? agentJob = null;
        ProjectJob? projectJob = null;

        if (parsedGuid.HasValue)
        {
            agentJob = await dbContext.AgentJobs.FindAsync([parsedGuid.Value]);
            projectJob = await dbContext.ProjectJobs.FindAsync([parsedGuid.Value]);
        }

        if (agentJob is null && projectJob is null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found." });
        }

        var author = (agentJob?.AssignedAgentId ?? projectJob?.AssignedAgentId)?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(author) && parsedGuid.HasValue)
        {
            var prRows = await dbContext.JobMessages
                .AsNoTracking()
                .Where(m => m.JobId == parsedGuid.Value && m.SenderType == "persona" && m.StructuredPayloadJson != null)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            foreach (var row in prRows)
            {
                if (!TryReadPrLinkage(row.StructuredPayloadJson, out _, out var rowPrNum)) continue;
                if (request.PrNumber.HasValue && rowPrNum.HasValue && rowPrNum.Value != request.PrNumber.Value) continue;

                if (!string.IsNullOrWhiteSpace(row.SenderId))
                {
                    author = row.SenderId.Trim().ToLowerInvariant();
                    break;
                }
            }
        }

        var reviewerRole = await ResolveCallerRoleAsync(reviewer);

        // Self-review guard
        if (!string.IsNullOrEmpty(author) && string.Equals(reviewer, author, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "self_review_not_allowed",
                message = $"'{reviewer}' is the author of this pull request and cannot approve its own merge."
            });
        }

        // If author is unrecorded and caller is not an operator, reject to enforce two-man review
        if (string.IsNullOrEmpty(author) && reviewerRole != "operator")
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "unverifiable_author",
                message = "The author of this pull request could not be determined to verify two-man review. Only an operator can approve when author is unrecorded."
            });
        }

        var signal = new PrReviewSignal(
            ReviewerId: reviewer,
            IsApproved: request.IsApproved,
            ReviewComments: request.ReviewComments,
            Timestamp: DateTimeOffset.UtcNow,
            PrNumber: request.PrNumber
        );

        if (temporalClient is not null)
        {
            var workflowId = $"job-{jobId}";
            try
            {
                var handle = temporalClient.GetWorkflowHandle<IJobExecutionWorkflow>(workflowId);
                await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(signal));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to deliver PrReviewSignal to workflow {WorkflowId}", workflowId);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "workflow_signal_failed",
                    message = $"Failed to deliver review signal to workflow {workflowId}: {ex.Message}"
                });
            }
        }

        return Ok(new { success = true, isApproved = request.IsApproved, reviewer });
    }

    private static bool TryReadPrLinkage(
        string? structuredPayloadJson,
        out string? prUrl,
        out int? prNumber)
    {
        prUrl = null;
        prNumber = null;
        if (string.IsNullOrWhiteSpace(structuredPayloadJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(structuredPayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (doc.RootElement.TryGetProperty("prUrl", out var url) &&
                url.ValueKind == JsonValueKind.String)
            {
                prUrl = url.GetString();
            }

            if (doc.RootElement.TryGetProperty("prNumber", out var number) &&
                number.ValueKind == JsonValueKind.Number &&
                number.TryGetInt32(out var parsed))
            {
                prNumber = parsed;
            }

            return prUrl is not null || prNumber is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
