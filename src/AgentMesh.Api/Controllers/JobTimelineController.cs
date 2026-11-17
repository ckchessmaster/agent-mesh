using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
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

/// <summary>
/// A free-form message to the agent that does not decide the plan gate.
/// </summary>
/// <param name="Content">What the operator wants the agent to know.</param>
/// <param name="SenderId">Who wrote it; defaults to the cockpit operator.</param>
public record PlanGuidanceRequest(
    string Content,
    string? SenderId = null
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

        await UnparkJobWaitingSessionsAsync(jobId, HttpContext?.RequestAborted ?? CancellationToken.None);

        return Accepted();
    }

    [HttpPost("cards/{cardId}/plan-decision")]
    public async Task<IActionResult> SubmitPlanDecision(string jobId, Guid cardId, [FromBody] PlanDecisionPayload payload, CancellationToken ct)
    {
        var card = await db.JobTimelineCards
            .Include(c => c.Comments)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.JobId == jobId, ct);
        if (card is null)
        {
            return NotFound(new { error = $"Timeline card '{cardId}' was not found for job '{jobId}'." });
        }

        // A gate that has already been answered is not a gate. Approving a stale card used to be accepted
        // silently: the card flipped, the signal was buffered by a workflow long past planning, and the
        // operator watched an approval that orchestrated nothing.
        if (card.Status is not (TimelineCardStatus.PendingInput or TimelineCardStatus.Active))
        {
            return Conflict(new { error = $"This plan gate is already {card.Status}, so nothing was sent to the agent." });
        }

        if (!Guid.TryParse(jobId, out var parsedJobGuid))
        {
            return BadRequest(new { error = $"Job id '{jobId}' is not a job id, so no workflow can be signalled." });
        }

        // Signal first, then record the verdict. The order is the fix: this endpoint used to write
        // Approved onto the card before Temporal had accepted anything, so a refused signal (workflow
        // finished, worker restart, wrong id) left a cockpit showing an approved plan on a job parked
        // forever, with no error anywhere in the UI. A failed delivery now leaves the gate visibly open
        // and tells the operator why.
        try
        {
            var handle = GetWorkflowHandle(jobId);
            await handle.SignalAsync(w => w.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(cardId, payload.Approved, payload.Feedback, payload.ApproverId ?? "human")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Conflict(new
            {
                error = "The decision did not reach the running workflow, so the plan gate is still open. " +
                        $"Reason: {ex.Message}"
            });
        }

        card.Status = payload.Approved ? TimelineCardStatus.Approved : TimelineCardStatus.Rejected;
        card.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await BroadcastCardUpdatedAsync(jobId, card);

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == parsedJobGuid, ct);
        if (job != null)
        {
            job.ProgressHeartbeat = payload.Approved
                ? "Plan approved. Starting implementation."
                : $"Plan rejected: {payload.Feedback ?? "realign requested"}";
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

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

        await UnparkJobWaitingSessionsAsync(jobId, ct);

        return Accepted();
    }

    /// <summary>
    /// Sends the agent a raw message without deciding the plan gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the operator's recovery route. Discovery Q&A is skipped whenever the agent asks nothing —
    /// which is normal, and also what happens when it asks in prose nobody shows anyone. Before, the only
    /// thing to do about a plan built on a wrong assumption was to reject it, which marks the record as a
    /// refusal and costs a full re-plan, or to say nothing and let the implementation run. Guidance keeps
    /// the gate open and the verdict untouched, so "change this, then show me again" is expressible
    /// without pretending to reject a plan.
    /// </para>
    /// <para>
    /// The comment is persisted whether or not delivery succeeds, and the response says which happened.
    /// Losing a typed message to a workflow that had already moved on is the failure an operator cannot
    /// recover from, so it is reported rather than swallowed.
    /// </para>
    /// </remarks>
    [HttpPost("cards/{cardId}/plan-guidance")]
    public async Task<IActionResult> SubmitPlanGuidance(
        string jobId,
        Guid cardId,
        [FromBody] PlanGuidanceRequest req,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Content))
        {
            return BadRequest(new { error = "content is required." });
        }

        var card = await db.JobTimelineCards
            .Include(c => c.Comments)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.JobId == jobId, ct);
        if (card is null)
        {
            return NotFound(new { error = $"Timeline card '{cardId}' was not found for job '{jobId}'." });
        }

        if (card.CardType != TimelineCardType.PlanApproval)
        {
            return BadRequest(new { error = $"Card '{cardId}' is a {card.CardType} card; guidance only applies to a plan gate." });
        }

        // Resolved is accepted on purpose: guidance written while Stage 1B is already re-planning is still
        // an instruction the operator meant, and the workflow buffers it for the next round. A decided
        // gate is different — nothing downstream is going to read it.
        if (card.Status is TimelineCardStatus.Approved or TimelineCardStatus.Rejected or TimelineCardStatus.Closed)
        {
            return Conflict(new { error = $"This plan gate was {card.Status}, so there is nothing to guide. Reject the plan to send feedback instead." });
        }

        if (!Guid.TryParse(jobId, out var parsedJobId))
        {
            return BadRequest(new { error = $"Job id '{jobId}' is not a job id, so no workflow can be signalled." });
        }

        var senderId = string.IsNullOrWhiteSpace(req.SenderId) ? "operator" : req.SenderId.Trim();
        var comment = new JobCardComment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            SenderId = senderId,
            SenderType = "operator",
            Content = req.Content.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.JobCardComments.Add(comment);
        await db.SaveChangesAsync(ct);

        if (hubContext != null)
        {
            await hubContext.Clients.Group($"job-{jobId}")
                .SendAsync("ReceiveTimelineComment", jobId, cardId, comment, ct);
        }

        if (workflows is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                commentId = comment.Id,
                error = "The workflow router is not configured on this API, so the guidance was saved to the " +
                        "card but no agent was told. It is not lost — send it again once the router is available."
            });
        }

        var delivery = await workflows.SendPlanGuidanceAsync(
            parsedJobId,
            new PlanGuidanceSignal(cardId, comment.Content, senderId, comment.Id.ToString()),
            ct);

        if (!delivery.Delivered)
        {
            return Conflict(new
            {
                commentId = comment.Id,
                delivered = false,
                error = "The guidance is saved on the card but no agent has been told: " +
                        (delivery.Detail ?? "no running workflow is holding this job's plan gate.")
            });
        }

        await UnparkJobWaitingSessionsAsync(jobId, ct);

        return Accepted(new { commentId = comment.Id, delivered = true, workflowId = delivery.WorkflowId });
    }

    private async Task UnparkJobWaitingSessionsAsync(string jobId, CancellationToken ct)
    {
        var waitingSessions = await db.AgentSessions
            .Where(s => s.Status == "waiting_for_input")
            .ToListAsync(ct);

        var matching = waitingSessions
            .Where(s => JobTurnDispatcher.MetadataMatchesJob(s.MetadataJson, jobId))
            .ToList();

        if (matching.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var session in matching)
        {
            session.Status = "active";
            session.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        if (hubContext != null)
        {
            foreach (var session in matching)
            {
                await hubContext.Clients.All.SendAsync("SessionUpdated", session, ct);
            }
        }
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
