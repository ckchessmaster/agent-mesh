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
    int? PrNumber = null,
    string? ApprovedHeadSha = null
);

[ApiController]
[Route("api/jobs")]
[Authorize]
public class JobReviewsController(
    AgentMeshDbContext dbContext,
    ITemporalClient? temporalClient = null,
    IHubContext<AgentMeshHub>? hubContext = null,
    ILogger<JobReviewsController>? logger = null,
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
        var personaHeader = Request?.Headers.TryGetValue(PlatformCallContextResolver.PersonaIdHeader, out var ph) == true
            ? ph.ToString()
            : null;
        var isDriverOrMaster = User?.HasClaim("IsMasterKey", "true") == true || User?.IsInRole("Driver") == true;
        if (isDriverOrMaster && !string.IsNullOrWhiteSpace(personaHeader))
        {
            return personaHeader.Trim();
        }

        var claim = User?.FindFirst("PersonaId")?.Value
                    ?? User?.FindFirst("persona_id")?.Value
                    ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User?.FindFirst("AgentId")?.Value
                    ?? User?.FindFirst("agent_id")?.Value
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
        if (User.IsInRole("Operator") || User.IsInRole("Admin") ||
            User.HasClaim("IsMasterKey", "true") ||
            User.Claims.Any(c => (c.Type == ClaimTypes.Role || c.Type.Equals("role", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("roles", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("groups", StringComparison.OrdinalIgnoreCase) || c.Type.EndsWith("/role", StringComparison.OrdinalIgnoreCase)) && OperatorRoles.Contains(c.Value)))
        {
            return "operator";
        }

        var isExplicitAgent = User.HasClaim(c => c.Type == "agent_id" || c.Type == "AgentId" || c.Type == "persona_id" || c.Type == "PersonaId");

        if (!isExplicitAgent && (User.HasClaim(c => c.Type == ClaimTypes.Email || c.Type.Equals("email", StringComparison.OrdinalIgnoreCase)) || callerId.Contains('@')))
        {
            return "operator";
        }

        if (User.IsInRole("TechLead") || User.IsInRole("tech_lead") ||
            User.Claims.Any(c => (c.Type == ClaimTypes.Role || c.Type.Equals("role", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("roles", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("groups", StringComparison.OrdinalIgnoreCase) || c.Type.EndsWith("/role", StringComparison.OrdinalIgnoreCase)) && TechLeadRoles.Contains(c.Value)))
        {
            return "tech_lead";
        }

        var persona = await dbContext.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == callerId.ToLowerInvariant());
        if (persona != null && !string.IsNullOrWhiteSpace(persona.Role) && TechLeadRoles.Contains(persona.Role.Trim()))
        {
            return "tech_lead";
        }

        return "agent";
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
                var cardCount = await dbContext.JobTimelineCards.CountAsync(c => c.JobId == projectJob.Id.ToString());
                var dto = ProjectJobsController.ToDto(projectJob, project?.Name, cardCount);
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

        var reviewer = (ResolveCallerId() ?? request.ReviewerId)?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            return Unauthorized(new { error = "unauthorized", message = "Caller identity claim is missing." });
        }

        // If caller authenticated via a generic/master principal and passed ReviewerId explicitly,
        // honour request.ReviewerId unless caller is the authoring agent attempting to spoof ReviewerId.
        var authenticatedId = ResolveCallerId()?.Trim().TrimStart('@').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(request.ReviewerId))
        {
            reviewer = request.ReviewerId.Trim().TrimStart('@').ToLowerInvariant();
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
        string? cardHeadSha = null;

        if (parsedGuid.HasValue)
        {
            var jobIdStr = parsedGuid.Value.ToString();
            var prCards = await dbContext.JobTimelineCards
                .AsNoTracking()
                .Where(c => c.JobId == jobIdStr)
                .OrderByDescending(c => c.UpdatedAt)
                .ThenByDescending(c => c.CreatedAt)
                .ToListAsync();

            foreach (var card in prCards)
            {
                if (!TryReadPrLinkage(card.StructuredPayloadJson, out var cardAuthor, out _, out var rowPrNum, out var rowHeadSha)) continue;
                if (request.PrNumber.HasValue && rowPrNum.HasValue && rowPrNum.Value != request.PrNumber.Value) continue;

                cardHeadSha ??= rowHeadSha;
                if (string.IsNullOrEmpty(author) && !string.IsNullOrWhiteSpace(cardAuthor))
                {
                    author = cardAuthor.Trim().ToLowerInvariant();
                }

                if (!string.IsNullOrEmpty(author) && cardHeadSha is not null)
                {
                    break;
                }
            }
        }

        var reviewerRole = await ResolveCallerRoleAsync(authenticatedId ?? reviewer);

        // Enforce Two-Man Rule: reject self-approval/self-review when either the authenticated principal
        // or the claimed reviewerId matches the authoring persona (including driver-<persona> aliases).
        if (!string.IsNullOrEmpty(author) &&
            (IsSameAgentOrPersona(reviewer, author) ||
             (!string.IsNullOrEmpty(authenticatedId) && IsSameAgentOrPersona(authenticatedId, author))))
        {
            var matchedAuthor = IsSameAgentOrPersona(reviewer, author) ? reviewer : authenticatedId!;
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "self_review_not_allowed",
                message = $"'{matchedAuthor}' is the author of this pull request and cannot approve its own merge."
            });
        }

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
            PrNumber: request.PrNumber,
            ApprovedHeadSha: !string.IsNullOrWhiteSpace(request.ApprovedHeadSha) ? request.ApprovedHeadSha.Trim() : cardHeadSha
        );

        if (workflows is not null && parsedGuid.HasValue)
        {
            var delivery = await workflows.SendPrReviewAsync(parsedGuid.Value, signal);
            if (!delivery.Delivered)
            {
                return Conflict(new
                {
                    error = "review_not_delivered",
                    delivery.Status,
                    detail = delivery.Detail,
                    workflowId = delivery.WorkflowId,
                });
            }
        }
        else if (temporalClient is not null)
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

    private static bool IsSameAgentOrPersona(string a, string b)
    {
        static string Norm(string s)
        {
            var t = s.Trim().TrimStart('@').ToLowerInvariant();
            return t.StartsWith("driver-", StringComparison.Ordinal) && t.Length > "driver-".Length
                ? t["driver-".Length..]
                : t;
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.Ordinal);
    }

    private static bool TryReadPrLinkage(
        string? structuredPayloadJson,
        out string? authorPersona,
        out string? prUrl,
        out int? prNumber,
        out string? headSha)
    {
        authorPersona = null;
        prUrl = null;
        prNumber = null;
        headSha = null;
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

            foreach (var shaKey in new[] { "headSha", "head_sha" })
            {
                if (doc.RootElement.TryGetProperty(shaKey, out var shaProp) &&
                    shaProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(shaProp.GetString()))
                {
                    headSha = shaProp.GetString()!.Trim();
                    break;
                }
            }

            foreach (var authorKey in new[] { "authorPersonaId", "senderPersona", "senderId", "author" })
            {
                if (doc.RootElement.TryGetProperty(authorKey, out var prop) &&
                    prop.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(prop.GetString()))
                {
                    authorPersona = prop.GetString()!.Trim().ToLowerInvariant();
                    break;
                }
            }

            return prUrl is not null || prNumber is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
