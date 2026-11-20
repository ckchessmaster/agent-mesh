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

/// <param name="Note">Optional operator note recorded as the closing comment on the card.</param>
public record ResolveCardRequest(
    string? Note = null
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
    AgentMesh.Api.Services.IWorkflowSignalRouter? workflows = null,
    // Optional: this controller is about cards, and the tests that exercise card payloads should
    // not need a hub or a bell. Program.cs registers it; ApiServiceRegistrationTests pins that.
    IAttentionNotifier? attention = null) : ControllerBase
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

        // The card family, and the only way the Temporal worker's own writes reach the bell. The worker
        // has no hub of its own — the runner may not even be online — so its activities call THIS
        // endpoint after they commit (TimelineActivities.NotifyCardUpdatedAsync). Those activities write
        // the DiscoveryQa/PlanApproval/TurnInterrupted cards and the matching heartbeat in one save, so
        // one refresh here covers both sources for every gate the orchestration raises.
        await attention.RefreshAttentionAsync(jobId);

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

        // The escalation close. A comment on a PendingInput Escalation3R card is what retires the wait
        // now that the job ledger is gone, and the card flip above is the only record of it — so the bell
        // has to be told here or an answered question keeps ringing until the next poll.
        await attention.RefreshAttentionAsync(jobId);

        return Ok(comment);
    }

    /// <summary>
    /// Retires a human-waiting escalation card without deciding a workflow gate or touching the run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Escalations ("agent paused on error", "turn budget exhausted") and consultations park a card in
    /// a state the Needs-Attention queue keeps ringing for. A comment retires the wait when the
    /// operator has something to say; this is the exit for the other case — the problem was fixed
    /// somewhere else, the job was recovered, or the escalation simply went stale while its workflow
    /// was closed. Before this endpoint existed, nothing flipped a <c>TurnInterrupted</c> card to a
    /// terminal status, so a resolved incident rang the bell forever.
    /// </para>
    /// <para>
    /// Gate cards are deliberately refused: <c>PlanApproval</c>, <c>PrReview</c> and <c>DiscoveryQa</c>
    /// are held by a running workflow that blocks on their decision signal, and silently marking one
    /// Resolved would leave the workflow waiting on a card the ledger says is finished. Decide those
    /// with their own controls.
    /// </para>
    /// </remarks>
    [HttpPost("cards/{cardId}/resolve")]
    public async Task<IActionResult> ResolveCard(
        string jobId,
        Guid cardId,
        [FromBody] ResolveCardRequest? req,
        CancellationToken ct = default)
    {
        var card = await db.JobTimelineCards
            .Include(c => c.Comments)
            .FirstOrDefaultAsync(c => c.Id == cardId && c.JobId == jobId, ct);

        if (card is null)
        {
            return NotFound(new { error = $"Card '{cardId}' not found on job '{jobId}'." });
        }

        var isRetirableEscalation = card.CardType is TimelineCardType.Escalation3R
            or TimelineCardType.InternalConsultation
            or TimelineCardType.TurnInterrupted;
        if (!isRetirableEscalation)
        {
            return BadRequest(new
            {
                error = $"{card.CardType} cards are gates a workflow is holding; decide them with their own " +
                        "control (approve/reject/submit answers) rather than resolving the card."
            });
        }

        // Idempotent by design: an operator double-clicking, or the bell retrying after a refresh,
        // must not fail loudly against work that is genuinely already done.
        if (card.Status is TimelineCardStatus.Resolved or TimelineCardStatus.Closed)
        {
            return Ok(card);
        }

        // Same actor resolution as the comments route: a persona calling with its bearer token is an
        // agent retiring its own escalation; everyone else is the operator.
        var personaHeader = Request?.Headers.TryGetValue(AgentMesh.Api.Services.PlatformCallContextResolver.PersonaIdHeader, out var ph) == true
            ? ph.ToString()
            : null;
        var actor = !string.IsNullOrWhiteSpace(personaHeader)
            ? personaHeader.Trim().TrimStart('@').ToLowerInvariant()
            : "operator";
        var actorType = !string.IsNullOrWhiteSpace(personaHeader) ? "agent" : "operator";

        var note = string.IsNullOrWhiteSpace(req?.Note) ? null : req.Note.Trim();
        var comment = new JobCardComment
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            SenderId = actor,
            SenderType = "system",
            Content = note is null
                ? $"⚒️ Escalation marked resolved by {actorType} '{actor}' — no reply expected."
                : $"⚒️ Escalation marked resolved by {actorType} '{actor}': {note}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.JobCardComments.Add(comment);

        card.Status = TimelineCardStatus.Resolved;
        card.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastCardUpdatedAsync(jobId, card);

        // The bell's liveness check reads this same card state, so retiring the wait and refreshing
        // the attention row are two halves of one promise: the item leaves Needs-Attention when it
        // is resolved here, not when the next poll happens to agree.
        await attention.RefreshAttentionAsync(jobId);

        return Ok(card);
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

            // Answering the questionnaire is the end of a discoveryQuestions wait. The workflow also
            // rewrites the heartbeat to "Exploring codebase…" a moment later, but that happens inside
            // Temporal and may land after this response, so the state is read here as "no longer
            // waiting on the operator" rather than inferred from what the signal will do.
            await attention.RefreshAttentionAsync(jobId);
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

        // Last, after every write above: an approval clears the wait and a rejection loops back
        // to the discovery/plan stage and keeps it, and only the recomputed state knows which —
        // the heartbeat this route just wrote is the difference between the two.
        await attention.RefreshAttentionAsync(jobId);

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
