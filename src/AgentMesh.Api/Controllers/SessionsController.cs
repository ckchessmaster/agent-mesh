using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SessionsController(AgentMeshDbContext db, IHubContext<AgentMeshHub> hubContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSessions()
    {
        var sessions = await db.AgentSessions
            .AsNoTracking()
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Ok(sessions);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetSession(Guid id)
    {
        var session = await db.AgentSessions
            .AsNoTracking()
            .Include(s => s.Messages)
            .Include(s => s.Jobs)
            .FirstOrDefaultAsync(s => s.Id == id);

        return session == null ? NotFound() : Ok(session);
    }

    [HttpPost]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var session = new AgentSession
        {
            Title = req.Title,
            Status = "active",
            MetadataJson = req.MetadataJson ?? "{}"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("SessionCreated", session);

        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, session);
    }

    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        var messages = await db.SessionMessages
            .AsNoTracking()
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.CreatedAt)
            .Take(200)
            .ToListAsync();

        return Ok(messages);
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> PostMessage(Guid id, [FromBody] PostMessageRequest req)
    {
        var senderId = req.SenderAgentId ?? User.Identity?.Name ?? "gemini-spark";

        var message = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = req.TargetAgentId,
            Content = req.Content,
            StructuredPayloadJson = req.StructuredPayloadJson
        };

        db.SessionMessages.Add(message);
        await db.SaveChangesAsync();

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", message);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", message);

        return Ok(message);
    }
}

public record CreateSessionRequest(string Title, string? MetadataJson);
public record PostMessageRequest(string Content, string? SenderAgentId = null, string? TargetAgentId = null, string? StructuredPayloadJson = null);
