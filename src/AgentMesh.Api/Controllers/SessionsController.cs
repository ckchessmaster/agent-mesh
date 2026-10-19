using System.Security.Claims;
using System.Text;
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

    public static List<SessionArtifactDto> ExtractArtifacts(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("artifacts", out var aElem) && aElem.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<SessionArtifactDto>>(aElem.GetRawText(), new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static string UpdateArtifactsMetadata(string? metadataJson, List<SessionArtifactDto> artifacts)
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
            dict["artifacts"] = artifacts;
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    public static string GetArtifactDirectory(Guid sessionId)
    {
        var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "artifacts", sessionId.ToString());
        Directory.CreateDirectory(basePath);
        return basePath;
    }

    public static DateTimeOffset? ExtractStoppedAt(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("stoppedAt", out var sElem) && sElem.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }
        }
        catch { }
        return null;
    }

    public static DateTimeOffset? ExtractLastStoppedAt(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("lastStoppedAt", out var sElem) && sElem.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }
            if (doc.RootElement.TryGetProperty("stoppedAt", out var sElem2) && sElem2.TryGetDateTimeOffset(out var dto2))
            {
                return dto2;
            }
        }
        catch { }
        return null;
    }

    public static DateTimeOffset? ExtractTurnStartedAt(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("currentTurnStartedAt", out var sElem) && sElem.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }
        }
        catch { }
        return null;
    }

    public static string RecordStopMetadata(string? metadataJson, DateTimeOffset stoppedAt)
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
            dict["stoppedAt"] = stoppedAt;
            dict["lastStoppedAt"] = stoppedAt;
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    public static string ClearStopMetadata(string? metadataJson)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed)
                    {
                        if (!string.Equals(k, "stoppedAt", StringComparison.OrdinalIgnoreCase))
                        {
                            dict[k] = v;
                        }
                    }
                }
            }
            dict["currentTurnStartedAt"] = DateTimeOffset.UtcNow;
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    private async Task TriggerStopInternalAsync(AgentSession session)
    {
        session.MetadataJson = RecordStopMetadata(session.MetadataJson, DateTimeOffset.UtcNow);
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
        await hubContext.Clients.All.SendAsync("StopExecution", new
        {
            sessionId = session.Id,
            agentId = session.CreatorAgentId,
            reason = "Interrupted by incoming prompt"
        });
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

    private async Task<IActionResult> HandleMeshCommandInternalAsync(AgentSession session, string commandText)
    {
        var trimmed = commandText.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subCmd = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
        var arg = parts.Length > 2 ? string.Join(' ', parts.Skip(2)).Trim() : null;

        string systemResponse;

        if (string.IsNullOrWhiteSpace(subCmd) || subCmd == "help")
        {
            systemResponse = "💡 **AgentMesh Command Reference**\n\n" +
                "• **`/mesh stop`**: Halt active model execution immediately (<500ms)\n" +
                "• **`/mesh mode <plan|auto|accept-edits>`**: Switch session execution mode\n" +
                "• **`/mesh model <model-id>`**: Set active model (e.g. gemini-3.8-flash)\n" +
                "• **`/mesh effort <low|medium|high|max>`**: Set reasoning effort\n" +
                "• **`/mesh artifacts`**: Toggle side-by-side Artifacts panel\n" +
                "• **`/mesh queue <prompt>`**: Enqueue prompt sequentially\n" +
                "• **`/mesh clear`**: Clear event timeline filters\n\n" +
                "*Agent Workflows*: `/plan`, `/grill-me`, `/goal`, `/compact`, `/browser`, `/schedule`, `/skills`, `/help`";
        }
        else if (subCmd == "stop")
        {
            await TriggerStopInternalAsync(session);
            systemResponse = "🛑 Stop signal dispatched for active model execution.";
        }
        else if (subCmd == "effort")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var eff = arg.ToLowerInvariant();
                if (eff is "low" or "medium" or "high" or "max")
                {
                    session.MetadataJson = MergeSessionMetadata(session.MetadataJson, null, null, eff, null, null);
                    systemResponse = $"✓ Reasoning effort set to \"{eff}\" for session '{session.Title}'.";
                }
                else
                {
                    systemResponse = $"⚠️ Invalid effort level \"{arg}\". Supported levels: low, medium, high, max.";
                }
            }
            else
            {
                var (_, _, currentEffort, _, _) = ExtractSessionSettings(session.MetadataJson);
                systemResponse = $"⚙️ **Reasoning Effort Configuration**\n\n" +
                    $"• Current Level: **`{currentEffort ?? "medium"}`**\n" +
                    $"• Supported Levels: `low` | `medium` | `high` | `max`\n" +
                    $"• Usage: `/mesh effort <level>` (e.g. `/mesh effort high`)";
            }
        }
        else if (subCmd == "model")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                session.MetadataJson = MergeSessionMetadata(session.MetadataJson, null, arg, null, null, null);
                systemResponse = $"✓ Model updated to \"{arg}\" for session '{session.Title}'.";
            }
            else
            {
                var (_, currentModel, _, _, _) = ExtractSessionSettings(session.MetadataJson);
                systemResponse = $"⚙️ **Model Configuration**\n\n" +
                    $"• Current Model: **`{currentModel ?? "default"}`**\n" +
                    $"• Usage: `/mesh model <model-id>` (e.g. `/mesh model gemini-3.8-flash`)";
            }
        }
        else if (subCmd == "mode")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var mode = arg.ToLowerInvariant();
                if (mode is "plan" or "auto" or "accept-edits")
                {
                    session.MetadataJson = MergeSessionMetadata(session.MetadataJson, null, null, null, mode, null);
                    systemResponse = $"✓ Execution mode set to \"{mode}\" for session '{session.Title}'.";
                }
                else
                {
                    systemResponse = $"⚠️ Invalid mode \"{arg}\". Supported modes: plan, auto, accept-edits.";
                }
            }
            else
            {
                var (_, _, _, currentMode, _) = ExtractSessionSettings(session.MetadataJson);
                systemResponse = $"⚙️ **Execution Mode Configuration**\n\n" +
                    $"• Current Mode: **`{currentMode ?? "auto"}`**\n" +
                    $"• Supported Modes: `plan` | `auto` | `accept-edits`\n" +
                    $"• Usage: `/mesh mode <mode>` (e.g. `/mesh mode plan`)";
            }
        }
        else if (subCmd == "queue")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var queue = ExtractPromptQueue(session.MetadataJson);
                queue.Add(new QueuedPromptDto(
                    Id: Guid.NewGuid().ToString(),
                    Prompt: arg,
                    Sender: "operator",
                    CreatedAt: DateTimeOffset.UtcNow
                ));
                session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);
                systemResponse = $"✓ Prompt enqueued sequentially ({queue.Count} prompt(s) in queue).";
            }
            else
            {
                var queue = ExtractPromptQueue(session.MetadataJson);
                systemResponse = $"📋 **Prompt Queue Status**\n\n" +
                    $"• Pending in Queue: **{queue.Count}** prompt(s)\n" +
                    $"• Usage: `/mesh queue <prompt>` to enqueue a prompt sequentially.";
            }
        }
        else
        {
            systemResponse = $"⚠️ Unknown mesh subcommand: \"{subCmd}\". Type `/mesh help` for available commands.";
        }

        var sysMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "system",
            Content = systemResponse,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(sysMsg);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var msgDto = ToMessageDto(sysMsg);
        await hubContext.Clients.Group($"session-{session.Id}").SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(new { command = trimmed, systemMessage = msgDto, session });
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

        if (eventType == "prompt")
        {
            if (req.Content.StartsWith("/mesh", StringComparison.OrdinalIgnoreCase) ||
                req.Content.StartsWith("\\mesh", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleMeshCommandInternalAsync(session, req.Content);
            }

            session.MetadataJson = ClearStopMetadata(session.MetadataJson);
        }
        else
        {
            if (session.Status == "stopped")
            {
                return Ok(new { status = "dropped_due_to_stop" });
            }

            var stoppedAt = ExtractStoppedAt(session.MetadataJson);
            if (stoppedAt.HasValue && session.Status != "thinking" && session.Status != "waiting_for_input")
            {
                // Discard stale events emitted by stopped agent turns
                return Ok(new { status = "dropped_due_to_stop" });
            }

            var lastStoppedAt = ExtractLastStoppedAt(session.MetadataJson);
            if (lastStoppedAt.HasValue)
            {
                var turnStartedAt = ExtractTurnStartedAt(session.MetadataJson);
                if (!turnStartedAt.HasValue || turnStartedAt.Value <= lastStoppedAt.Value)
                {
                    return Ok(new { status = "dropped_due_to_stop" });
                }
                if (req.CreatedAt.HasValue && req.CreatedAt.Value <= lastStoppedAt.Value)
                {
                    return Ok(new { status = "dropped_due_to_stop" });
                }
            }
        }

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

        var wasThinking = session.Status == "thinking";

        if (!string.IsNullOrWhiteSpace(req.StructuredPayloadJson))
        {
            session.MetadataJson = AccumulateUsageMetadata(session.MetadataJson, req.StructuredPayloadJson);
        }

        db.SessionMessages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (eventType == "tool_call" && string.Equals(req.ToolName, "ask_question", StringComparison.OrdinalIgnoreCase))
        {
            session.Status = "waiting_for_input";
        }
        else if (eventType is "prompt" or "thought" or "tool_call")
        {
            session.Status = "thinking";
        }
        else if (eventType == "response")
        {
            if (session.Status != "waiting_for_input")
            {
                session.Status = "active";
            }
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
                tier = tier,
                createdAt = message.CreatedAt
            });
        }
        else if (eventType == "response" && wasThinking)
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
                        tier = nextPrompt.Tier ?? tier,
                        createdAt = nextMsg.CreatedAt
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

        var emptyQueue = new List<QueuedPromptDto>();
        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, emptyQueue);
        session.MetadataJson = RecordStopMetadata(session.MetadataJson, DateTimeOffset.UtcNow);

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
        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue = emptyQueue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue = emptyQueue });

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

        await hubContext.Clients.All.SendAsync("StopExecution", new
        {
            sessionId = id,
            agentId = session.CreatorAgentId,
            reason = "Operator clicked stop"
        });

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

        await hubContext.Clients.All.SendAsync("StopCommand", new
        {
            sessionId = id,
            commandId,
            agentId = session.CreatorAgentId
        });

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

        if (req.Prompt.StartsWith("/mesh", StringComparison.OrdinalIgnoreCase) ||
            req.Prompt.StartsWith("\\mesh", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleMeshCommandInternalAsync(session, req.Prompt);
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
                    tier = req.Tier ?? tier,
                    createdAt = msg.CreatedAt
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
                    tier = req.Tier ?? tier,
                    createdAt = msg.CreatedAt
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
                tier = item.Tier ?? tier,
                createdAt = msg.CreatedAt
            });
        }

        return Ok(new { session, queue, dispatched = true });
    }

    [HttpPost("{id:guid}/answer")]
    public async Task<IActionResult> SubmitAnswer(Guid id, [FromBody] SubmitAnswerRequest req)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var senderId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("AgentId")?.Value
            ?? "operator";

        var sb = new StringBuilder();
        foreach (var ans in req.Answers)
        {
            var parts = new List<string>();
            if (ans.SelectedOptions != null && ans.SelectedOptions.Count > 0)
            {
                parts.Add(string.Join(", ", ans.SelectedOptions));
            }
            if (!string.IsNullOrWhiteSpace(ans.CustomText))
            {
                parts.Add(ans.CustomText.Trim());
            }
            var answerStr = parts.Count > 0 ? string.Join("; ", parts) : "User skipped";
            sb.AppendLine($"Q: {ans.Question} -> A: {answerStr}");
        }

        var answerSummary = sb.ToString().Trim();
        var answersJson = System.Text.Json.JsonSerializer.Serialize(req.Answers);

        var toolResultMsg = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = session.CreatorAgentId,
            EventType = "tool_result",
            ToolName = "ask_question",
            Content = answerSummary,
            ToolResultJson = answersJson,
            StructuredPayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                toolCallId = req.ToolCallId,
                answers = req.Answers
            }),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(toolResultMsg);

        var promptContent = $"User answered question:\n{answerSummary}\nPlease proceed with this answer.";
        var promptMsg = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = session.CreatorAgentId,
            EventType = "prompt",
            Content = promptContent,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(promptMsg);

        session.MetadataJson = ClearStopMetadata(session.MetadataJson);
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

        var toolResultDto = ToMessageDto(toolResultMsg);
        var promptDto = ToMessageDto(promptMsg);

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", toolResultDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", toolResultDto);

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", promptDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", promptDto);

        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", new
            {
                sessionId = id,
                messageId = promptMsg.Id,
                agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                prompt = promptMsg.Content,
                sender = promptMsg.SenderAgentId,
                workingDirectory = workDir,
                model = model,
                effort = effort,
                mode = mode,
                tier = tier,
                createdAt = promptMsg.CreatedAt
            });
        }

        return Ok(new { toolResult = toolResultDto, prompt = promptDto, session });
    }

    [HttpGet("{id:guid}/artifacts")]
    [AllowAnonymous]
    public async Task<IActionResult> GetArtifacts(Guid id)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var artifacts = ExtractArtifacts(session.MetadataJson);

        var dir = GetArtifactDirectory(id);
        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir);
            bool updated = false;
            foreach (var f in files)
            {
                var fInfo = new FileInfo(f);
                var fName = fInfo.Name;
                if (!artifacts.Any(a => string.Equals(a.Name, fName, StringComparison.OrdinalIgnoreCase)))
                {
                    artifacts.Add(new SessionArtifactDto(
                        Id: Guid.NewGuid().ToString(),
                        SessionId: id,
                        Name: fName,
                        Summary: null,
                        ContentType: fName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown" : "application/octet-stream",
                        SizeBytes: fInfo.Length,
                        CreatedAt: fInfo.CreationTimeUtc,
                        UpdatedAt: fInfo.LastWriteTimeUtc
                    ));
                    updated = true;
                }
            }
            if (updated)
            {
                session.MetadataJson = UpdateArtifactsMetadata(session.MetadataJson, artifacts);
                await db.SaveChangesAsync();
            }
        }

        return Ok(artifacts);
    }

    [HttpGet("{id:guid}/artifacts/{artifactId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetArtifact(Guid id, string artifactId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var artifacts = ExtractArtifacts(session.MetadataJson);
        var artifact = artifacts.FirstOrDefault(a => string.Equals(a.Id, artifactId, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, artifactId, StringComparison.OrdinalIgnoreCase));

        var dir = GetArtifactDirectory(id);
        var safeName = artifact != null ? artifact.Name : Path.GetFileName(artifactId);
        var filePath = Path.Combine(dir, safeName);

        if (!System.IO.File.Exists(filePath) && artifact == null)
        {
            return NotFound(new { error = $"Artifact '{artifactId}' not found in session '{id}'." });
        }

        var content = System.IO.File.Exists(filePath) ? await System.IO.File.ReadAllTextAsync(filePath) : artifact?.Content ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        var resultDto = artifact != null
            ? artifact with { Content = content }
            : new SessionArtifactDto(
                Id: Guid.NewGuid().ToString(),
                SessionId: id,
                Name: safeName,
                Summary: null,
                ContentType: safeName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown" : "application/octet-stream",
                SizeBytes: System.IO.File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
                CreatedAt: now,
                UpdatedAt: now,
                Content: content
            );

        return Ok(resultDto);
    }

    [HttpPost("{id:guid}/artifacts")]
    public async Task<IActionResult> SyncArtifact(Guid id, [FromBody] SyncArtifactRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Artifact Name is required." });
        }

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var dir = GetArtifactDirectory(id);
        var safeFileName = Path.GetFileName(req.Name.Trim());
        var filePath = Path.Combine(dir, safeFileName);
        await System.IO.File.WriteAllTextAsync(filePath, req.Content ?? string.Empty);

        var fileInfo = new FileInfo(filePath);
        var artifacts = ExtractArtifacts(session.MetadataJson);
        var existingIndex = artifacts.FindIndex(a => string.Equals(a.Name, safeFileName, StringComparison.OrdinalIgnoreCase));

        var now = DateTimeOffset.UtcNow;
        SessionArtifactDto artifactDto;
        bool isNew = existingIndex < 0;

        if (isNew)
        {
            artifactDto = new SessionArtifactDto(
                Id: Guid.NewGuid().ToString(),
                SessionId: id,
                Name: safeFileName,
                Summary: req.Summary,
                ContentType: req.ContentType ?? (safeFileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown" : "application/octet-stream"),
                SizeBytes: fileInfo.Length,
                CreatedAt: now,
                UpdatedAt: now,
                Content: req.Content
            );
            artifacts.Add(artifactDto);
        }
        else
        {
            var old = artifacts[existingIndex];
            artifactDto = old with
            {
                Summary = req.Summary ?? old.Summary,
                ContentType = req.ContentType ?? old.ContentType,
                SizeBytes = fileInfo.Length,
                UpdatedAt = now,
                Content = req.Content
            };
            artifacts[existingIndex] = artifactDto;
        }

        session.MetadataJson = UpdateArtifactsMetadata(session.MetadataJson, artifacts);
        session.UpdatedAt = now;
        await db.SaveChangesAsync();

        var eventName = isNew ? "ArtifactCreated" : "ArtifactUpdated";
        await hubContext.Clients.Group($"session-{id}").SendAsync(eventName, artifactDto);
        await hubContext.Clients.All.SendAsync(eventName, artifactDto);
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(artifactDto);
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
    string? ToolResultJson = null,
    DateTimeOffset? CreatedAt = null
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

public record QuestionAnswerDto(
    int QuestionIndex,
    string Question,
    List<string> SelectedOptions,
    string? CustomText = null
);

public record SubmitAnswerRequest(
    Guid? ToolCallId,
    List<QuestionAnswerDto> Answers
);

public record SessionArtifactDto(
    string Id,
    Guid SessionId,
    string Name,
    string? Summary,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Content = null
);

public record SyncArtifactRequest(
    string Name,
    string Content,
    string? Summary = null,
    string? ContentType = "text/markdown"
);


