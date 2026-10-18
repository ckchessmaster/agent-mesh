using System.Security.Claims;
using System.Text.Json;
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
public class SessionsController(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub> hubContext,
    IServiceScopeFactory? scopeFactory = null) : ControllerBase
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
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        string? creatorAgentName = null;
        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            creatorAgentName = await db.Agents
                .AsNoTracking()
                .Where(a => a.Id == session.CreatorAgentId)
                .Select(a => a.Name)
                .FirstOrDefaultAsync();
        }

        var dto = new SessionDetailDto(
            session.Id,
            session.Title,
            session.Status,
            session.MetadataJson,
            session.CreatorAgentId,
            creatorAgentName,
            session.CreatedAt,
            session.UpdatedAt,
            session.Messages.Select(ToMessageDto).ToList()
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

        var callerAgentId = !string.IsNullOrWhiteSpace(req.CreatorAgentId)
            ? req.CreatorAgentId.Trim().ToLowerInvariant()
            : User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? User?.FindFirst("AgentId")?.Value
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
                SenderAgentId = "operator",
                TargetAgentId = callerAgentId,
                EventType = "prompt",
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
    [HttpGet("{id:guid}/events")]
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
            .ToListAsync();

        return Ok(messages.Select(ToMessageDto).ToList());
    }

    [HttpPost("{id:guid}/messages")]
    [HttpPost("{id:guid}/events")]
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

        var senderId = !string.IsNullOrWhiteSpace(req.SenderAgentId)
            ? req.SenderAgentId.Trim()
            : User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? User?.FindFirst("AgentId")?.Value
              ?? "agy";

        var eventType = !string.IsNullOrWhiteSpace(req.EventType)
            ? req.EventType.Trim().ToLowerInvariant()
            : "response";

        var message = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = req.TargetAgentId ?? session.CreatorAgentId,
            EventType = eventType,
            Content = req.Content.Trim(),
            ToolName = req.ToolName,
            ToolArgsJson = req.ToolArgsJson,
            ToolResultJson = req.ToolResultJson,
            StructuredPayloadJson = req.StructuredPayloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.SessionMessages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (eventType is "prompt" or "thought" or "tool_call")
        {
            session.Status = "thinking";
        }
        else if (eventType == "response")
        {
            session.Status = "active";
        }

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == session.CreatorAgentId);
            if (agent != null)
            {
                agent.Status = session.Status == "thinking" ? "busy" : "online";
                agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync();

        var messageDto = ToMessageDto(message);

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", messageDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", messageDto);

        // If user sent a prompt with SimulateExecution=true, trigger background step simulation
        if (eventType == "prompt" && req.SimulateExecution == true && scopeFactory != null)
        {
            var targetAgentId = session.CreatorAgentId ?? "agy-core";
            var promptText = req.Content.Trim();
            _ = Task.Run(() => SimulateAgentTrajectoryAsync(scopeFactory, hubContext, id, targetAgentId, promptText));
        }

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

    private static SessionMessageDto ToMessageDto(SessionMessage m) => new(
        m.Id,
        m.SessionId,
        m.SenderAgentId,
        m.TargetAgentId,
        m.Content,
        m.StructuredPayloadJson,
        m.CreatedAt,
        m.EventType,
        m.ToolName,
        m.ToolArgsJson,
        m.ToolResultJson
    );

    private static async Task SimulateAgentTrajectoryAsync(
        IServiceScopeFactory scopeFactory,
        IHubContext<AgentMeshHub> hubContext,
        Guid sessionId,
        string agentId,
        string promptText)
    {
        try
        {
            await Task.Delay(600);
            await AppendSimulatedStepAsync(scopeFactory, hubContext, sessionId, agentId,
                eventType: "thought",
                content: $"Analyzing instruction: \"{promptText}\". Determining the optimal codebase inspection steps and required tools.");

            await Task.Delay(900);
            var toolArgs = JsonSerializer.Serialize(new { query = promptText, path = "src/" }, new JsonSerializerOptions { WriteIndented = true });
            await AppendSimulatedStepAsync(scopeFactory, hubContext, sessionId, agentId,
                eventType: "tool_call",
                content: "Executing workspace search and inspecting project structure",
                toolName: "search_workspace",
                toolArgsJson: toolArgs);

            await Task.Delay(800);
            var toolOutput = JsonSerializer.Serialize(new
            {
                matchesFound = 3,
                files = new[] { "src/AgentMesh.Api/Program.cs", "src/AgentMesh.Api/Controllers/SessionsController.cs", "src/AgentMesh.Dashboard/src/App.tsx" },
                durationMs = 42
            }, new JsonSerializerOptions { WriteIndented = true });
            await AppendSimulatedStepAsync(scopeFactory, hubContext, sessionId, agentId,
                eventType: "tool_result",
                content: "Workspace inspection complete (3 files matched in 42ms)",
                toolName: "search_workspace",
                toolResultJson: toolOutput);

            await Task.Delay(700);
            await AppendSimulatedStepAsync(scopeFactory, hubContext, sessionId, agentId,
                eventType: "thought",
                content: "Verified the target files and confirmed the state transitions. Synthesizing final answer for the operator.");

            await Task.Delay(600);
            await AppendSimulatedStepAsync(scopeFactory, hubContext, sessionId, agentId,
                eventType: "response",
                content: $"Completed analysis for: **{promptText}**.\n\n- Inspected `AgentMesh.Api` and `AgentMesh.Dashboard`.\n- All trajectory events (`prompt`, `thought`, `tool_call`, `tool_result`, `response`) were recorded to the session ledger and streamed over SignalR.");
        }
        catch
        {
            // Ignore simulation errors in background task
        }
    }

    private static async Task AppendSimulatedStepAsync(
        IServiceScopeFactory scopeFactory,
        IHubContext<AgentMeshHub> hubContext,
        Guid sessionId,
        string agentId,
        string eventType,
        string content,
        string? toolName = null,
        string? toolArgsJson = null,
        string? toolResultJson = null)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

        var session = await scopedDb.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session == null) return;

        var msg = new SessionMessage
        {
            SessionId = sessionId,
            SenderAgentId = agentId,
            EventType = eventType,
            Content = content,
            ToolName = toolName,
            ToolArgsJson = toolArgsJson,
            ToolResultJson = toolResultJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

        scopedDb.SessionMessages.Add(msg);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.Status = eventType == "response" ? "active" : "thinking";

        var agent = await scopedDb.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent != null)
        {
            agent.Status = eventType == "response" ? "online" : "busy";
            agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
        }

        await scopedDb.SaveChangesAsync();

        var dto = ToMessageDto(msg);
        await hubContext.Clients.Group($"session-{sessionId}").SendAsync("ReceiveMessage", dto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", dto);
        await hubContext.Clients.All.SendAsync("SessionUpdated", new SessionSummaryDto(
            session.Id,
            session.Title,
            session.Status,
            session.MetadataJson,
            session.CreatorAgentId,
            0,
            session.CreatedAt,
            session.UpdatedAt
        ));
    }
}

public record CreateSessionRequest(string Title, string? MetadataJson = null, string? InitialMessage = null, string? CreatorAgentId = null);

public record PostMessageRequest(
    string Content,
    string? SenderAgentId = null,
    string? TargetAgentId = null,
    string? StructuredPayloadJson = null,
    string? EventType = null,
    string? ToolName = null,
    string? ToolArgsJson = null,
    string? ToolResultJson = null,
    bool? SimulateExecution = null
);

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
    DateTimeOffset CreatedAt,
    string EventType = "response",
    string? ToolName = null,
    string? ToolArgsJson = null,
    string? ToolResultJson = null
);
