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
public class SessionsController(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub> hubContext) : ControllerBase
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

    public static string MergeSessionMetadata(
        string? existingJson,
        string? workingDirectory,
        string? model,
        string? effort,
        string? mode)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory)) dict["workingDirectory"] = workingDirectory.Trim();
        if (!string.IsNullOrWhiteSpace(model)) dict["model"] = model.Trim();
        if (!string.IsNullOrWhiteSpace(effort)) dict["effort"] = effort.Trim();
        if (!string.IsNullOrWhiteSpace(mode)) dict["mode"] = mode.Trim();

        return System.Text.Json.JsonSerializer.Serialize(dict);
    }

    public static (string? workingDirectory, string? model, string? effort, string? mode) ExtractSessionSettings(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return (null, null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            var workDir = root.TryGetProperty("workingDirectory", out var wd) ? wd.GetString() : null;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var effort = root.TryGetProperty("effort", out var e) ? e.GetString() : null;
            var mode = root.TryGetProperty("mode", out var md) ? md.GetString() : null;
            return (workDir, model, effort, mode);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    public static string AccumulateUsageMetadata(string? metadataJson, string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson)) return metadataJson ?? "{}";
        try
        {
            using var payloadDoc = System.Text.Json.JsonDocument.Parse(structuredPayloadJson);
            if (!payloadDoc.RootElement.TryGetProperty("usage", out var usageElem)) return metadataJson ?? "{}";

            var input = usageElem.TryGetProperty("inputTokens", out var ip) ? ip.GetInt64() : 0;
            var output = usageElem.TryGetProperty("outputTokens", out var op) ? op.GetInt64() : 0;
            var thinking = usageElem.TryGetProperty("thinkingTokens", out var tp) ? tp.GetInt64() : 0;
            var total = usageElem.TryGetProperty("totalTokens", out var totp) ? totp.GetInt64() : (input + output);
            var duration = usageElem.TryGetProperty("durationSeconds", out var dp) ? dp.GetDouble() : 0.0;

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }

            long existingInput = 0, existingOutput = 0, existingThinking = 0, existingTotal = 0;
            double existingDuration = 0.0;

            if (dict.TryGetValue("cumulativeUsage", out var existingUsageObj) && existingUsageObj is System.Text.Json.JsonElement existingElem)
            {
                if (existingElem.TryGetProperty("inputTokens", out var ei)) existingInput = ei.GetInt64();
                if (existingElem.TryGetProperty("outputTokens", out var eo)) existingOutput = eo.GetInt64();
                if (existingElem.TryGetProperty("thinkingTokens", out var et)) existingThinking = et.GetInt64();
                if (existingElem.TryGetProperty("totalTokens", out var etot)) existingTotal = etot.GetInt64();
                if (existingElem.TryGetProperty("totalDurationSeconds", out var ed)) existingDuration = ed.GetDouble();
            }

            dict["cumulativeUsage"] = new
            {
                inputTokens = existingInput + input,
                outputTokens = existingOutput + output,
                thinkingTokens = existingThinking + thinking,
                totalTokens = existingTotal + total,
                totalDurationSeconds = existingDuration + duration
            };

            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
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

        var initialMetadata = MergeSessionMetadata(req.MetadataJson, req.WorkingDirectory, req.Model, req.Effort, req.Mode);

        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = "active",
            MetadataJson = initialMetadata,
            CreatorAgentId = callerAgentId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

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

        if (!string.IsNullOrWhiteSpace(req.StructuredPayloadJson))
        {
            session.MetadataJson = AccumulateUsageMetadata(session.MetadataJson, req.StructuredPayloadJson);
        }

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
                var isFromAgent = string.Equals(senderId, agent.Id, StringComparison.OrdinalIgnoreCase);
                var isConnected = isFromAgent || AgentMeshHub.IsAgentConnected(agent.Id) || agent.Status is "online" or "busy";

                if (isConnected)
                {
                    agent.Status = session.Status == "thinking" ? "busy" : "online";
                    agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
                }
            }
        }

        await db.SaveChangesAsync();

        var messageDto = ToMessageDto(message);

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", messageDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", messageDto);

        if (eventType == "prompt" && !string.IsNullOrWhiteSpace(message.TargetAgentId))
        {
            var (workDir, model, effort, mode) = ExtractSessionSettings(session.MetadataJson);
            var targetGroup = $"agent-{message.TargetAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", new
            {
                sessionId = id,
                messageId = message.Id,
                agentId = message.TargetAgentId.Trim().ToLowerInvariant(),
                prompt = message.Content,
                sender = message.SenderAgentId,
                workingDirectory = workDir,
                model = model,
                effort = effort,
                mode = mode
            });
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
}

public record CreateSessionRequest(
    string Title,
    string? MetadataJson = null,
    string? InitialMessage = null,
    string? CreatorAgentId = null,
    string? WorkingDirectory = null,
    string? Model = null,
    string? Effort = null,
    string? Mode = null
);

public record PostMessageRequest(
    string Content,
    string? SenderAgentId = null,
    string? TargetAgentId = null,
    string? StructuredPayloadJson = null,
    string? EventType = null,
    string? ToolName = null,
    string? ToolArgsJson = null,
    string? ToolResultJson = null
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
