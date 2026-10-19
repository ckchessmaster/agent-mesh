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
        string? mode,
        string? tier = null)
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
        if (effort != null)
        {
            if (string.IsNullOrWhiteSpace(effort)) dict.Remove("effort");
            else dict["effort"] = effort.Trim();
        }
        if (!string.IsNullOrWhiteSpace(mode)) dict["mode"] = mode.Trim();
        if (!string.IsNullOrWhiteSpace(tier)) dict["tier"] = tier.Trim();

        return System.Text.Json.JsonSerializer.Serialize(dict);
    }

    public static (string? workingDirectory, string? model, string? effort, string? mode, string? tier) ExtractSessionSettings(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return (null, null, null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            var workDir = root.TryGetProperty("workingDirectory", out var wd) ? wd.GetString() : null;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var effort = root.TryGetProperty("effort", out var e) ? e.GetString() : null;
            var mode = root.TryGetProperty("mode", out var md) ? md.GetString() : null;
            var tier = root.TryGetProperty("tier", out var tr) ? tr.GetString() : null;
            return (workDir, model, effort, mode, tier);
        }
        catch
        {
            return (null, null, null, null, null);
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

    public static List<QueuedPromptDto> ExtractPromptQueue(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("promptQueue", out var qElem) && qElem.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<QueuedPromptDto>>(qElem.GetRawText(), new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static string UpdatePromptQueueMetadata(string? metadataJson, List<QueuedPromptDto> queue)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            dict["promptQueue"] = queue;
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    private async Task TriggerStopInternalAsync(AgentSession session)
    {
        var auditMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "system",
            Content = "🛑 Interrupted by operator for new prompt.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(auditMsg);
        var auditDto = ToMessageDto(auditMsg);
        await hubContext.Clients.Group($"session-{session.Id}").SendAsync("ReceiveMessage", auditDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);

        await hubContext.Clients.Group($"session-{session.Id}").SendAsync("StopExecution", new
        {
            sessionId = session.Id,
            agentId = session.CreatorAgentId,
            reason = "Interrupted by incoming prompt"
        });
        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("StopExecution", new
            {
                sessionId = session.Id,
                agentId = session.CreatorAgentId,
                reason = "Interrupted by incoming prompt"
            });
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

        var initialMetadata = MergeSessionMetadata(req.MetadataJson, req.WorkingDirectory, req.Model, req.Effort, req.Mode, req.Tier);

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
            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
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
                mode = mode,
                tier = tier
            });
        }
        else if (eventType == "response")
        {
            // Auto-advance queued prompts sequentially if any are pending
            var queue = ExtractPromptQueue(session.MetadataJson);
            if (queue.Count > 0)
            {
                var nextPrompt = queue[0];
                queue.RemoveAt(0);
                session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);

                var nextMsg = new SessionMessage
                {
                    SessionId = id,
                    SenderAgentId = nextPrompt.Sender,
                    TargetAgentId = session.CreatorAgentId,
                    EventType = "prompt",
                    Content = nextPrompt.Prompt,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.SessionMessages.Add(nextMsg);
                session.Status = "thinking";
                session.UpdatedAt = DateTimeOffset.UtcNow;

                if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
                {
                    var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == session.CreatorAgentId);
                    if (agent != null)
                    {
                        agent.Status = "busy";
                        agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
                    }
                }

                await db.SaveChangesAsync();

                var nextDto = ToMessageDto(nextMsg);
                await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", nextDto);
                await hubContext.Clients.All.SendAsync("ReceiveMessage", nextDto);
                await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
                await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
                await hubContext.Clients.All.SendAsync("SessionUpdated", session);

                var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
                if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
                {
                    var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
                    await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", new
                    {
                        sessionId = id,
                        messageId = nextMsg.Id,
                        agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                        prompt = nextMsg.Content,
                        sender = nextMsg.SenderAgentId,
                        workingDirectory = nextPrompt.WorkingDirectory ?? workDir,
                        model = nextPrompt.Model ?? model,
                        effort = nextPrompt.Effort ?? effort,
                        mode = nextPrompt.Mode ?? mode,
                        tier = nextPrompt.Tier ?? tier
                    });
                }
            }
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

        var (oldWorkDir, oldModel, oldEffort, oldMode, oldTier) = ExtractSessionSettings(session.MetadataJson);

        if (req.MetadataJson != null || req.Model != null || req.Effort != null || req.Tier != null || req.Mode != null || req.WorkingDirectory != null)
        {
            session.MetadataJson = MergeSessionMetadata(
                req.MetadataJson ?? session.MetadataJson,
                req.WorkingDirectory,
                req.Model,
                req.Effort,
                req.Mode,
                req.Tier
            );
        }

        // Detect control parameter changes and append audit event
        var changes = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.Model) && req.Model != oldModel)
            changes.Add($"Model changed to '{req.Model}'");
        if (req.Effort != null && req.Effort != oldEffort)
            changes.Add(string.IsNullOrWhiteSpace(req.Effort) ? "Reasoning effort reset to default" : $"Effort set to '{req.Effort}'");
        if (!string.IsNullOrWhiteSpace(req.Tier) && req.Tier != oldTier)
            changes.Add($"Tier set to '{req.Tier}'");
        if (!string.IsNullOrWhiteSpace(req.Mode) && req.Mode != oldMode)
            changes.Add($"Mode set to '{req.Mode}'");
        if (!string.IsNullOrWhiteSpace(req.WorkingDirectory) && req.WorkingDirectory != oldWorkDir)
            changes.Add($"Working directory set to '{req.WorkingDirectory}'");

        if (changes.Count > 0)
        {
            var auditMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "system",
                TargetAgentId = session.CreatorAgentId,
                EventType = "system",
                Content = $"⚙️ Session updated: {string.Join(", ", changes)}",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(auditMsg);
            var auditDto = ToMessageDto(auditMsg);
            await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", auditDto);
            await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(session);
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> StopSession(Guid id)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        session.Status = "active";
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == session.CreatorAgentId);
            if (agent != null && agent.Status == "busy")
            {
                agent.Status = "online";
                agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }
        }

        var auditMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "system",
            Content = "🛑 Model execution stopped by operator.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(auditMsg);
        await db.SaveChangesAsync();

        var auditDto = ToMessageDto(auditMsg);
        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", auditDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);

        await hubContext.Clients.Group($"session-{id}").SendAsync("StopExecution", new
        {
            sessionId = id,
            agentId = session.CreatorAgentId,
            reason = "Operator clicked stop"
        });

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("StopExecution", new
            {
                sessionId = id,
                agentId = session.CreatorAgentId,
                reason = "Operator clicked stop"
            });
        }

        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(new { status = "stopped", sessionId = id });
    }

    [HttpPost("{id:guid}/commands/{commandId}/stop")]
    public async Task<IActionResult> StopCommand(Guid id, string commandId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var auditMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "tool_result",
            ToolName = "command",
            Content = $"🛑 Command '{commandId}' stopped by operator.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(auditMsg);
        await db.SaveChangesAsync();

        var auditDto = ToMessageDto(auditMsg);
        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", auditDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);

        await hubContext.Clients.Group($"session-{id}").SendAsync("StopCommand", new
        {
            sessionId = id,
            commandId,
            agentId = session.CreatorAgentId
        });

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("StopCommand", new
            {
                sessionId = id,
                commandId,
                agentId = session.CreatorAgentId
            });
        }

        return Ok(new { status = "stopped", commandId });
    }

    [HttpPost("{id:guid}/queue")]
    public async Task<IActionResult> EnqueuePrompt(Guid id, [FromBody] EnqueuePromptRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            return BadRequest(new { error = "Prompt cannot be empty." });
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
              ?? "operator";

        var queue = ExtractPromptQueue(session.MetadataJson);

        if (req.SendNow)
        {
            if (session.Status == "thinking")
            {
                await TriggerStopInternalAsync(session);
            }

            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var msg = new SessionMessage
            {
                SessionId = id,
                SenderAgentId = senderId,
                TargetAgentId = session.CreatorAgentId,
                EventType = "prompt",
                Content = req.Prompt.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(msg);
            session.Status = "thinking";
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var msgDto = ToMessageDto(msg);
            await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("SessionUpdated", session);

            if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
            {
                var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
                await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", new
                {
                    sessionId = id,
                    messageId = msg.Id,
                    agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                    prompt = msg.Content,
                    sender = msg.SenderAgentId,
                    workingDirectory = req.WorkingDirectory ?? workDir,
                    model = req.Model ?? model,
                    effort = req.Effort ?? effort,
                    mode = req.Mode ?? mode,
                    tier = req.Tier ?? tier
                });
            }

            return Ok(new { session, queue, dispatched = true });
        }

        // If session is idle and queue is empty, dispatch immediately
        if (session.Status != "thinking" && queue.Count == 0)
        {
            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var msg = new SessionMessage
            {
                SessionId = id,
                SenderAgentId = senderId,
                TargetAgentId = session.CreatorAgentId,
                EventType = "prompt",
                Content = req.Prompt.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(msg);
            session.Status = "thinking";
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var msgDto = ToMessageDto(msg);
            await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("SessionUpdated", session);

            if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
            {
                var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
                await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", new
                {
                    sessionId = id,
                    messageId = msg.Id,
                    agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                    prompt = msg.Content,
                    sender = msg.SenderAgentId,
                    workingDirectory = req.WorkingDirectory ?? workDir,
                    model = req.Model ?? model,
                    effort = req.Effort ?? effort,
                    mode = req.Mode ?? mode,
                    tier = req.Tier ?? tier
                });
            }

            return Ok(new { session, queue, dispatched = true });
        }

        // Otherwise enqueue for sequential execution
        var queuedItem = new QueuedPromptDto(
            Id: Guid.NewGuid().ToString(),
            Prompt: req.Prompt.Trim(),
            Sender: senderId,
            CreatedAt: DateTimeOffset.UtcNow,
            Model: req.Model,
            Effort: req.Effort,
            Tier: req.Tier,
            Mode: req.Mode,
            WorkingDirectory: req.WorkingDirectory
        );
        queue.Add(queuedItem);

        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(new { session, queue, dispatched = false });
    }

    [HttpDelete("{id:guid}/queue/{queueItemId}")]
    public async Task<IActionResult> DequeuePrompt(Guid id, string queueItemId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var queue = ExtractPromptQueue(session.MetadataJson);
        queue.RemoveAll(q => string.Equals(q.Id, queueItemId, StringComparison.OrdinalIgnoreCase));

        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(queue);
    }

    [HttpPost("{id:guid}/queue/{queueItemId}/send-now")]
    public async Task<IActionResult> SendQueuedPromptNow(Guid id, string queueItemId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var queue = ExtractPromptQueue(session.MetadataJson);
        var item = queue.FirstOrDefault(q => string.Equals(q.Id, queueItemId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            return NotFound(new { error = $"Queued prompt '{queueItemId}' not found." });
        }

        queue.Remove(item);
        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);

        if (session.Status == "thinking")
        {
            await TriggerStopInternalAsync(session);
        }

        var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);

        var msg = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = item.Sender,
            TargetAgentId = session.CreatorAgentId,
            EventType = "prompt",
            Content = item.Prompt,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(msg);
        session.Status = "thinking";
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var msgDto = ToMessageDto(msg);
        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", new
            {
                sessionId = id,
                messageId = msg.Id,
                agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                prompt = msg.Content,
                sender = msg.SenderAgentId,
                workingDirectory = item.WorkingDirectory ?? workDir,
                model = item.Model ?? model,
                effort = item.Effort ?? effort,
                mode = item.Mode ?? mode,
                tier = item.Tier ?? tier
            });
        }

        return Ok(new { session, queue, dispatched = true });
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
    string? Mode = null,
    string? Tier = null
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

public record UpdateSessionRequest(
    string? Title = null,
    string? Status = null,
    string? MetadataJson = null,
    string? Model = null,
    string? Effort = null,
    string? Tier = null,
    string? Mode = null,
    string? WorkingDirectory = null
);

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

public record QueuedPromptDto(
    string Id,
    string Prompt,
    string Sender,
    DateTimeOffset CreatedAt,
    string? Model = null,
    string? Effort = null,
    string? Tier = null,
    string? Mode = null,
    string? WorkingDirectory = null
);

public record EnqueuePromptRequest(
    string Prompt,
    string? SenderAgentId = null,
    bool SendNow = false,
    string? Model = null,
    string? Effort = null,
    string? Tier = null,
    string? Mode = null,
    string? WorkingDirectory = null
);

