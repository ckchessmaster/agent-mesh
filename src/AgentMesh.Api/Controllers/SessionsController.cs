using System.Security.Claims;
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
    [AllowAnonymous]
    public async Task<IActionResult> GetSessions([FromQuery] string? status = null, [FromQuery] string? creatorAgentId = null)
    {
        var query = db.AgentSessions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.Status.ToLower() == status.Trim().ToLower());
        }

        if (!string.IsNullOrWhiteSpace(creatorAgentId))
        {
            query = query.Where(s => s.CreatorAgentId == creatorAgentId.Trim().ToLowerInvariant());
        }

        var sessions = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(100)
            .Select(s => new SessionSummaryDto(
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                s.CreatorAgentId,
                s.Messages.Count,
                s.CreatedAt,
                s.UpdatedAt
            ))
            .ToListAsync();

        return Ok(sessions);
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSession(Guid id)
    {
        var session = await db.AgentSessions
            .AsNoTracking()
            .Include(s => s.CreatorAgent)
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var dto = new SessionDetailDto(
            session.Id,
            session.Title,
            session.Status,
            session.MetadataJson,
            session.CreatorAgentId,
            session.CreatorAgent?.Name,
            session.CreatedAt,
            session.UpdatedAt,
            session.Messages.Select(m => new SessionMessageDto(
                m.Id,
                m.SessionId,
                m.SenderAgentId,
                m.TargetAgentId,
                m.Content,
                m.StructuredPayloadJson,
                m.CreatedAt
            )).ToList()
        );

        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return BadRequest(new { error = "Session Title is required." });
        }

        var callerAgentId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("AgentId")?.Value
            ?? req.CreatorAgentId
            ?? "agy";

        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = "active",
            MetadataJson = req.MetadataJson ?? "{}",
            CreatorAgentId = callerAgentId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        // Optional initial message
        if (!string.IsNullOrWhiteSpace(req.InitialMessage))
        {
            var initialMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = callerAgentId,
                Content = req.InitialMessage.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(initialMsg);
        }

        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("SessionCreated", session);

        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, session);
    }

    [HttpGet("{id:guid}/messages")]
    [AllowAnonymous]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        var sessionExists = await db.AgentSessions.AnyAsync(s => s.Id == id);
        if (!sessionExists)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var messages = await db.SessionMessages
            .AsNoTracking()
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.CreatedAt)
            .Take(500)
            .Select(m => new SessionMessageDto(
                m.Id,
                m.SessionId,
                m.SenderAgentId,
                m.TargetAgentId,
                m.Content,
                m.StructuredPayloadJson,
                m.CreatedAt
            ))
            .ToListAsync();

        return Ok(messages);
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> PostMessage(Guid id, [FromBody] PostMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content))
        {
            return BadRequest(new { error = "Message content is required." });
        }

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var senderId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("AgentId")?.Value
            ?? req.SenderAgentId
            ?? "agy";

        var message = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = req.TargetAgentId,
            Content = req.Content.Trim(),
            StructuredPayloadJson = req.StructuredPayloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.SessionMessages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        var messageDto = new SessionMessageDto(
            message.Id,
            message.SessionId,
            message.SenderAgentId,
            message.TargetAgentId,
            message.Content,
            message.StructuredPayloadJson,
            message.CreatedAt
        );

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", messageDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", messageDto);

        return CreatedAtAction(nameof(GetMessages), new { id }, messageDto);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateSession(Guid id, [FromBody] UpdateSessionRequest req)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        if (!string.IsNullOrWhiteSpace(req.Title))
        {
            session.Title = req.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            session.Status = req.Status.Trim().ToLowerInvariant();
        }

        if (req.MetadataJson != null)
        {
            session.MetadataJson = req.MetadataJson;
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(session);
    }
}

public record CreateSessionRequest(string Title, string? MetadataJson = null, string? InitialMessage = null, string? CreatorAgentId = null);
public record PostMessageRequest(string Content, string? SenderAgentId = null, string? TargetAgentId = null, string? StructuredPayloadJson = null);
public record UpdateSessionRequest(string? Title = null, string? Status = null, string? MetadataJson = null);

public record SessionSummaryDto(
    Guid Id,
    string Title,
    string Status,
    string MetadataJson,
    string? CreatorAgentId,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record SessionDetailDto(
    Guid Id,
    string Title,
    string Status,
    string MetadataJson,
    string? CreatorAgentId,
    string? CreatorAgentName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<SessionMessageDto> Messages
);

public record SessionMessageDto(
    Guid Id,
    Guid SessionId,
    string SenderAgentId,
    string? TargetAgentId,
    string Content,
    string? StructuredPayloadJson,
    DateTimeOffset CreatedAt
);
