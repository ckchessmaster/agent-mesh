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
    IHubContext<AgentMeshHub>? hubContext = null) : ControllerBase
{
    private WorkflowHandle<IJobExecutionWorkflow> GetWorkflowHandle(string jobId)
    {
        try
        {
            var handle = temporalClient.GetWorkflowHandle<IJobExecutionWorkflow>(jobId);
            if (handle != null) return handle;
        }
        catch
        {
        }

        var workflowId = jobId.StartsWith("job-", StringComparison.OrdinalIgnoreCase) ? jobId : $"job-{jobId}";
        return temporalClient.GetWorkflowHandle<IJobExecutionWorkflow>(workflowId);
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

    [HttpPost("cards/{cardId}/comments")]
    public async Task<IActionResult> AddComment(string jobId, Guid cardId, [FromBody] AddCommentRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Content))
        {
            return BadRequest(new { error = "Comment content cannot be empty." });
        }

        var comment = new JobCardComment
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            SenderId = string.IsNullOrWhiteSpace(req.SenderId) ? "user" : req.SenderId,
            SenderType = string.IsNullOrWhiteSpace(req.SenderType) ? "user" : req.SenderType,
            Content = req.Content,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.JobCardComments.Add(comment);
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.Group($"job-{jobId}")
                .SendAsync("ReceiveTimelineComment", jobId, cardId, comment);
            await hubContext.Clients.Group(jobId)
                .SendAsync("ReceiveTimelineComment", jobId, cardId, comment);
        }

        return Ok(comment);
    }

    [HttpPost("cards/{cardId}/submit-answers")]
    public async Task<IActionResult> SubmitAnswers(string jobId, Guid cardId, [FromBody] DiscoveryAnswersPayload payload)
    {
        var handle = GetWorkflowHandle(jobId);
        await handle.SignalAsync(w => w.SubmitDiscoveryAnswersAsync(
            new DiscoveryAnswersSignal(cardId, payload.Answers ?? new Dictionary<string, string>(), payload.Feedback)));

        return Accepted();
    }

    [HttpPost("cards/{cardId}/plan-decision")]
    public async Task<IActionResult> SubmitPlanDecision(string jobId, Guid cardId, [FromBody] PlanDecisionPayload payload)
    {
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
