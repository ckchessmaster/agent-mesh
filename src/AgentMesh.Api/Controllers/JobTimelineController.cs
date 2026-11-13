using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Temporalio.Client;

namespace AgentMesh.Api.Controllers;

public record AddCommentRequest(
    string SenderId,
    string SenderType,
    string Content
);

public record DiscoveryAnswersPayload(
    Dictionary<string, string> Answers,
    string? Feedback = null
);

public record PlanDecisionPayload(
    bool Approved,
    string? Feedback = null,
    string ApproverId = "human"
);

public record AbortTurnRequest(
    string Reason,
    string RequestedBy = "human"
);

public record InterventionRequest(
    string Action, // "resume" | "retry_step" | "realign"
    string? AdditionalContext = null
);

[ApiController]
[Route("api/jobs/{jobId}/timeline")]
public class JobTimelineController(
    AgentMeshDbContext db,
    ITemporalClient temporalClient,
    IHubContext<AgentMeshHub>? hubContext = null,
    AgentMesh.Api.Services.IWorkflowSignalRouter? workflows = null) : ControllerBase
{
    private WorkflowHandle<IJobExecutionWorkflow> GetWorkflowHandle(string jobId)
    {
        var workflowId = jobId.StartsWith("job-", StringComparison.OrdinalIgnoreCase)
            ? jobId
            : $"job-{jobId}";
        return temporalClient.GetWorkflowHandle<IJobExecutionWorkflow>(workflowId);
    }

    private async Task BroadcastCardUpdatedAsync(string jobId, JobTimelineCard card)
    {
        if (hubContext != null)
        {
            await hubContext.Clients.Group($"job-{jobId}")
                .SendAsync("ReceiveTimelineCardUpdated", card);
        }
    }

    [HttpGet("cards")]
    public async Task<ActionResult<List<JobTimelineCard>>> GetCards(string jobId)
    {
        var cards = await db.JobTimelineCards
            .AsNoTracking()
            .Include(c => c.Comments)
            .Where(c => c.JobId == jobId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        return Ok(cards);
    }

    [HttpPost("cards/{cardId}/notify-updated")]
    public async Task<IActionResult> NotifyCardUpdated(string jobId, Guid cardId)
    {
        var card = await db.JobTimelineCards
            .AsNoTracking()
            .Include(c => c.Comments)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.JobId == jobId);
        if (card == null)
        {
            return NotFound();
        }

        await BroadcastCardUpdatedAsync(jobId, card);
        return Ok(card);
    }

    [HttpPost("cards/{cardId}/comments")]
    public async Task<IActionResult> AddComment(
        string jobId,
        Guid cardId,
        [FromBody] AddCommentRequest req,
        CancellationToken ct = default)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Content))
        {
            return BadRequest(new { error = "Comment content cannot be empty." });
        }

        var personaHeader = Request?.Headers.TryGetValue(AgentMesh.Api.Services.PlatformCallContextResolver.PersonaIdHeader, out var ph) == true
            ? ph.ToString()
            : null;
        var resolvedSenderId = !string.IsNullOrWhiteSpace(personaHeader)
            ? personaHeader.Trim().TrimStart('@').ToLowerInvariant()
            : (string.IsNullOrWhiteSpace(req.SenderId) ? "operator" : req.SenderId.Trim());
        var resolvedSenderType = !string.IsNullOrWhiteSpace(personaHeader)
            ? "agent"
            : (string.IsNullOrWhiteSpace(req.SenderType) ? "operator" : req.SenderType.Trim());

        var comment = new JobCardComment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            SenderId = resolvedSenderId,
            SenderType = resolvedSenderType,
            Content = req.Content.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.JobCardComments.Add(comment);

        var card = await db.JobTimelineCards
            .Include(c => c.Comments)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.JobId == jobId, ct);

        if (card != null && card.Status == TimelineCardStatus.PendingInput &&
            card.CardType is TimelineCardType.Escalation3R or TimelineCardType.InternalConsultation)
        {
            card.Status = TimelineCardStatus.Resolved;
            card.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        if (hubContext != null)
        {
            await hubContext.Clients.Group($"job-{jobId}")
                .SendAsync("ReceiveTimelineComment", jobId, cardId, comment, ct);
            if (card != null)
            {
                await BroadcastCardUpdatedAsync(jobId, card);
            }
        }

        if (workflows != null &&
            !string.Equals(resolvedSenderType, "system", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(jobId, out var parsedJobId))
        {
            try
            {
                await workflows.SendChatMessageAsync(
                    parsedJobId,
                    new ChatMessageSignal(
                        SenderId: resolvedSenderId,
                        Content: comment.Content,
                        Timestamp: comment.CreatedAt,
                        MessageId: comment.Id.ToString()),
                    ct);
            }
            catch
            {
                // Best-effort workflow routing; persisted comment remains durable.
            }
        }

        return Ok(comment);
    }

    [HttpPost("cards/{cardId}/submit-answers")]
    public async Task<IActionResult> SubmitAnswers(string jobId, Guid cardId, [FromBody] DiscoveryAnswersPayload payload)
    {
        var answers = payload.Answers ?? new Dictionary<string, string>();
        var card = await db.JobTimelineCards
            .Include(c => c.Comments)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.JobId == jobId);
        if (card != null)
        {
            try
            {
                var node = JsonNode.Parse(string.IsNullOrWhiteSpace(card.StructuredPayloadJson) ? "{}" : card.StructuredPayloadJson) as JsonObject ?? new JsonObject();
                node["answers"] = JsonSerializer.SerializeToNode(answers);
                if (!string.IsNullOrWhiteSpace(payload.Feedback))
                {
                    node["feedback"] = payload.Feedback;
                }
                card.StructuredPayloadJson = node.ToJsonString();
            }
            catch
            {
                // Preserve existing payload if not a JSON object
            }

            card.Status = TimelineCardStatus.Approved;
            card.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await BroadcastCardUpdatedAsync(jobId, card);
        }

        var handle = GetWorkflowHandle(jobId);
        await handle.SignalAsync(w => w.SubmitDiscoveryAnswersAsync(
            new DiscoveryAnswersSignal(cardId, answers, payload.Feedback)));

        return Accepted();
    }

    [HttpPost("cards/{cardId}/plan-decision")]
    public async Task<IActionResult> SubmitPlanDecision(string jobId, Guid cardId, [FromBody] PlanDecisionPayload payload)
    {
        var card = await db.JobTimelineCards
            .Include(c => c.Comments)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.JobId == jobId);
        if (card != null)
        {
            card.Status = payload.Approved ? TimelineCardStatus.Approved : TimelineCardStatus.Rejected;
            card.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await BroadcastCardUpdatedAsync(jobId, card);
        }

        if (Guid.TryParse(jobId, out var parsedJobGuid))
        {
            var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == parsedJobGuid);
            if (job != null)
            {
                job.ProgressHeartbeat = payload.Approved
                    ? "Plan approved. Starting implementation."
                    : $"Plan rejected: {payload.Feedback ?? "realign requested"}";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();

                if (hubContext != null)
                {
                    await hubContext.Clients.All.SendAsync("ProgressHeartbeatUpdated", new
                    {
                        jobId = job.Id,
                        progressHeartbeat = job.ProgressHeartbeat,
                        currentTaskId = job.CurrentTaskId,
                        lifecycleStatus = ProjectJobsController.DeriveLifecycleStatus(job)
                    });
                }
            }
        }

        var handle = GetWorkflowHandle(jobId);
        await handle.SignalAsync(w => w.SubmitPlanDecisionAsync(
            new PlanApprovalSignal(cardId, payload.Approved, payload.Feedback, payload.ApproverId ?? "human")));

        return Accepted();
    }

    [HttpPost("abort-turn")]
    public async Task<IActionResult> AbortTurn(string jobId, [FromBody] AbortTurnRequest req)
    {
        var handle = GetWorkflowHandle(jobId);
        await handle.SignalAsync(w => w.AbortTurnAsync(
            new AbortActiveTurnSignal(req.Reason ?? "Stop requested by operator", req.RequestedBy ?? "operator")));

        return Accepted();
    }

    [HttpPost("intervention")]
    public async Task<IActionResult> SubmitIntervention(string jobId, [FromBody] InterventionRequest req)
    {
        var handle = GetWorkflowHandle(jobId);
        await handle.SignalAsync(w => w.InterveneTurnAsync(
            new TurnInterventionSignal(req.Action, req.AdditionalContext)));

        return Accepted();
    }
}
