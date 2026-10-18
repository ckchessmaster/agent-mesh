using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AgentMesh.Api.Tools;

[McpServerToolType]
public class SessionTools(
    AgentMeshDbContext db,
    IApiKeyService apiKeyService,
    ILogger<SessionTools> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    IHubContext<AgentMeshHub>? hubContext = null)
{
    private string? GetAuthenticatedAgentId()
    {
        var user = httpContextAccessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("AgentId")?.Value;
    }

    private static string UnauthorizedResponse() =>
        JsonSerializer.Serialize(new
        {
            error = "Unauthorized",
            message = "Missing or invalid API token. Please register via 'register_agent' and provide 'Authorization: Bearer <token>' in your MCP client headers."
        }, new JsonSerializerOptions { WriteIndented = true });

    [McpServerTool(Name = "register_agent")]
    [Description("Registers a new autonomous AI agent (such as agy) into AgentMesh and generates a Bearer API token.")]
    public async Task<string> RegisterAgent(
        [Description("Unique lowercase agent identifier, e.g. 'agy' or 'gemini-spark'")] string agentId,
        [Description("Human-readable display name, e.g. 'Antigravity CLI'")] string name,
        [Description("Runtime environment type: 'Cli', 'Subagent', 'Cloud', 'Copilot', or 'Ide'")] string runtimeType = "Cli",
        [Description("Optional persona prompt or core mission description")] string? personaPrompt = null,
        [Description("Optional role description, e.g. 'Lead Engineer'")] string? role = null,
        CancellationToken ct = default)
    {
        var normalizedId = agentId.Trim().ToLowerInvariant();

        var existing = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId, ct);
        if (existing != null)
        {
            existing.Status = "online";
            existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return JsonSerializer.Serialize(new
            {
                status = "already_registered",
                agentId = existing.Id,
                name = existing.Name,
                role = existing.Role,
                runtimeType = existing.RuntimeType.ToString(),
                trustTier = existing.TrustTier.ToString(),
                message = $"Agent '{normalizedId}' is already registered and marked online."
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        if (!Enum.TryParse<RuntimeType>(runtimeType, true, out var parsedRuntime))
        {
            parsedRuntime = RuntimeType.CliAgy;
        }

        var rawToken = apiKeyService.GenerateToken();
        var hashedToken = apiKeyService.HashToken(rawToken);

        var agent = new Agent
        {
            Id = normalizedId,
            Name = name.Trim(),
            Role = role ?? "Autonomous Agent",
            Status = "online",
            RuntimeType = parsedRuntime,
            TrustTier = TrustTier.Intern,
            PersonaPrompt = personaPrompt ?? $"Autonomous Agent ({normalizedId})",
            MemorySummary = string.Empty,
            CapabilitiesJson = "[]",
            ApiKeyHash = hashedToken,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Agent registered via MCP: {AgentId} ({Name})", agent.Id, agent.Name);

        return JsonSerializer.Serialize(new
        {
            status = "registered",
            agentId = agent.Id,
            name = agent.Name,
            role = agent.Role,
            runtimeType = agent.RuntimeType.ToString(),
            token = rawToken,
            instructions = "Set 'Authorization: Bearer <token>' in your MCP client headers for all future tool calls."
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "create_session")]
    [Description("Creates a new session strictly owned by an agent and records it in PostgreSQL.")]
    public async Task<string> CreateSession(
        [Description("Descriptive title for the work session")] string title,
        [Description("Optional JSON metadata string describing branch, directory, or objectives")] string? metadataJson = null,
        [Description("Optional initial message to seed the session ledger")] string? initialMessage = null,
        CancellationToken ct = default)
    {
        var callerAgentId = GetAuthenticatedAgentId();
        if (callerAgentId == null)
        {
            return UnauthorizedResponse();
        }

        var session = new AgentSession
        {
            Title = title.Trim(),
            Status = "active",
            MetadataJson = metadataJson ?? "{}",
            CreatorAgentId = callerAgentId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        if (!string.IsNullOrWhiteSpace(initialMessage))
        {
            db.SessionMessages.Add(new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = callerAgentId,
                EventType = "prompt",
                Content = initialMessage.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Session created via MCP: {SessionId} ('{Title}') by {AgentId}", session.Id, session.Title, callerAgentId);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("SessionCreated", session, ct);
        }

        return JsonSerializer.Serialize(new
        {
            status = "created",
            sessionId = session.Id,
            title = session.Title,
            creatorAgentId = session.CreatorAgentId,
            createdAt = session.CreatedAt
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "list_sessions")]
    [Description("Lists active and past sessions, ordered by most recently updated.")]
    public async Task<string> ListSessions(
        [Description("Optional filter: 'active', 'completed', or 'all' (default is 'active')")] string? status = "active",
        CancellationToken ct = default)
    {
        var query = db.AgentSessions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && status.ToLower() != "all")
        {
            query = query.Where(s => s.Status.ToLower() == status.Trim().ToLower());
        }

        var sessions = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(50)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Status,
                s.CreatorAgentId,
                MessageCount = s.Messages.Count,
                s.CreatedAt,
                s.UpdatedAt
            })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "get_session")]
    [Description("Retrieves details of a specific session including full trajectory history.")]
    public async Task<string> GetSession(
        [Description("The GUID string of the session to retrieve")] string sessionId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(sessionId, out var id))
        {
            return JsonSerializer.Serialize(new { error = "Invalid session UUID format." });
        }

        var session = await db.AgentSessions
            .AsNoTracking()
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (session == null)
        {
            return JsonSerializer.Serialize(new { error = $"Session '{sessionId}' not found." });
        }

        string? creatorAgentName = null;
        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            creatorAgentName = await db.Agents
                .AsNoTracking()
                .Where(a => a.Id == session.CreatorAgentId)
                .Select(a => a.Name)
                .FirstOrDefaultAsync(ct);
        }

        var result = new
        {
            session.Id,
            session.Title,
            session.Status,
            session.MetadataJson,
            session.CreatorAgentId,
            CreatorAgentName = creatorAgentName,
            session.CreatedAt,
            session.UpdatedAt,
            Messages = session.Messages.Select(m => new
            {
                m.Id,
                m.SenderAgentId,
                m.TargetAgentId,
                m.EventType,
                m.Content,
                m.ToolName,
                m.ToolArgsJson,
                m.ToolResultJson,
                m.StructuredPayloadJson,
                m.CreatedAt
            })
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "append_thought")]
    [Description("Streams a reasoning / chain-of-thought step into the agent session timeline.")]
    public Task<string> AppendThought(
        [Description("The GUID string of the session")] string sessionId,
        [Description("The internal reasoning or thought content")] string thought,
        CancellationToken ct = default) =>
        AddSessionMessage(sessionId, thought, eventType: "thought", ct: ct);

    [McpServerTool(Name = "record_tool_call")]
    [Description("Records a tool invocation in the agent session timeline.")]
    public Task<string> RecordToolCall(
        [Description("The GUID string of the session")] string sessionId,
        [Description("Name of the invoked tool, e.g. 'run_command'")] string toolName,
        [Description("JSON string of tool arguments")] string toolArgsJson,
        [Description("Brief summary of the tool invocation")] string summary = "Invoking tool",
        CancellationToken ct = default) =>
        AddSessionMessage(sessionId, summary, eventType: "tool_call", toolName: toolName, toolArgsJson: toolArgsJson, ct: ct);

    [McpServerTool(Name = "record_tool_result")]
    [Description("Records the output/result of a tool invocation in the agent session timeline.")]
    public Task<string> RecordToolResult(
        [Description("The GUID string of the session")] string sessionId,
        [Description("Name of the invoked tool")] string toolName,
        [Description("Output or result of the tool execution")] string toolResult,
        [Description("Brief summary of the tool completion")] string summary = "Tool execution completed",
        CancellationToken ct = default) =>
        AddSessionMessage(sessionId, summary, eventType: "tool_result", toolName: toolName, toolResultJson: toolResult, ct: ct);

    [McpServerTool(Name = "add_session_message")]
    [Description("Appends a trajectory event or message into the session ledger transcript.")]
    public async Task<string> AddSessionMessage(
        [Description("The GUID string of the session")] string sessionId,
        [Description("Message content or text update from the agent")] string content,
        [Description("Optional target recipient agent ID")] string? targetAgentId = null,
        [Description("Optional JSON payload with structured data")] string? structuredPayloadJson = null,
        [Description("Trajectory event type: 'prompt', 'thought', 'tool_call', 'tool_result', or 'response'")] string eventType = "response",
        [Description("Optional tool name when eventType is 'tool_call' or 'tool_result'")] string? toolName = null,
        [Description("Optional JSON arguments when eventType is 'tool_call'")] string? toolArgsJson = null,
        [Description("Optional output when eventType is 'tool_result'")] string? toolResultJson = null,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(sessionId, out var id))
        {
            return JsonSerializer.Serialize(new { error = "Invalid session UUID format." });
        }

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session == null)
        {
            return JsonSerializer.Serialize(new { error = $"Session '{sessionId}' not found." });
        }

        var senderId = GetAuthenticatedAgentId();
        if (senderId == null)
        {
            return UnauthorizedResponse();
        }

        var normalizedEventType = string.IsNullOrWhiteSpace(eventType) ? "response" : eventType.Trim().ToLowerInvariant();

        var message = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = targetAgentId ?? session.CreatorAgentId,
            EventType = normalizedEventType,
            Content = content.Trim(),
            ToolName = toolName,
            ToolArgsJson = toolArgsJson,
            ToolResultJson = toolResultJson,
            StructuredPayloadJson = structuredPayloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.SessionMessages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.Status = normalizedEventType is "prompt" or "thought" or "tool_call" ? "thinking" : "active";
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Event ({EventType}) appended to session {SessionId} by {SenderId}", normalizedEventType, id, senderId);

        if (hubContext != null)
        {
            var dto = new SessionMessageDto(
                message.Id,
                message.SessionId,
                message.SenderAgentId,
                message.TargetAgentId,
                message.Content,
                message.StructuredPayloadJson,
                message.CreatedAt,
                message.EventType,
                message.ToolName,
                message.ToolArgsJson,
                message.ToolResultJson
            );
            await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", dto, ct);
            await hubContext.Clients.All.SendAsync("ReceiveMessage", dto, ct);
        }

        return JsonSerializer.Serialize(new
        {
            status = "appended",
            messageId = message.Id,
            sessionId = message.SessionId,
            eventType = message.EventType,
            senderAgentId = message.SenderAgentId,
            createdAt = message.CreatedAt
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "update_session_status")]
    [Description("Updates the status of an agent session ('active', 'thinking', 'completed', or 'archived').")]
    public async Task<string> UpdateSessionStatus(
        [Description("The GUID string of the session")] string sessionId,
        [Description("New status: 'active', 'thinking', 'completed', or 'archived'")] string status,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(sessionId, out var id))
        {
            return JsonSerializer.Serialize(new { error = "Invalid session UUID format." });
        }

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session == null)
        {
            return JsonSerializer.Serialize(new { error = $"Session '{sessionId}' not found." });
        }

        session.Status = status.Trim().ToLowerInvariant();
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("SessionUpdated", session, ct);
        }

        return JsonSerializer.Serialize(new
        {
            status = "updated",
            sessionId = session.Id,
            newStatus = session.Status,
            updatedAt = session.UpdatedAt
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
