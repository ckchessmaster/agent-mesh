using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Api.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Route("api/drivers")]
[Authorize]
public class AgentsController(
    AgentMeshDbContext db,
    IApiKeyService apiKeyService,
    IHubContext<AgentMeshHub>? hubContext = null) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAgent([FromBody] RegisterAgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Agent Id and Name are required." });
        }

        var normalizedId = req.Id.Trim().ToLowerInvariant();

        var existing = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (existing != null)
        {
            return Conflict(new { error = $"Agent with Id '{normalizedId}' already exists." });
        }

        var rawToken = apiKeyService.GenerateToken();
        var hashedToken = apiKeyService.HashToken(rawToken);

        var agent = new Agent
        {
            Id = normalizedId,
            Name = req.Name.Trim(),
            Role = req.Role ?? "Autonomous Agent",
            Status = req.Status ?? (AgentMeshHub.IsAgentConnected(normalizedId) ? "online" : "offline"),
            RuntimeType = req.RuntimeType,
            TrustTier = req.TrustTier,
            PersonaPrompt = req.PersonaPrompt ?? $"Autonomous AI Agent ({normalizedId})",
            MemorySummary = req.MemorySummary ?? string.Empty,
            CapabilitiesJson = req.CapabilitiesJson ?? "[]",
            ApiKeyHash = hashedToken,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var dto = ToDto(agent, 0);
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("AgentRegistered", dto);
        }

        return CreatedAtAction(nameof(GetAgent), new { id = agent.Id }, new
        {
            agent = dto,
            token = rawToken,
            instructions = $"Run 'agentmesh start {agent.Id}' or include 'Authorization: Bearer <token>' in your agent requests."
        });
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> UpsertAgent([FromBody] RegisterAgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Agent Id and Name are required." });
        }

        var normalizedId = req.Id.Trim().ToLowerInvariant();
        var existing = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);

        if (existing != null)
        {
            existing.Name = req.Name.Trim();
            if (!string.IsNullOrWhiteSpace(req.Role)) existing.Role = req.Role.Trim();
            if (!string.IsNullOrWhiteSpace(req.Status)) existing.Status = req.Status.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(req.PersonaPrompt)) existing.PersonaPrompt = req.PersonaPrompt;
            // Patch, never replace: callers only know the fields they render, and a rebuilt blob
            // used to erase whatever the other caller had written.
            if (!string.IsNullOrWhiteSpace(req.CapabilitiesJson))
            {
                existing.CapabilitiesJson = DriverCapabilities.Merge(existing.CapabilitiesJson, req.CapabilitiesJson)
                    ?? existing.CapabilitiesJson;
            }
            existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);
            var personas = await GetDriverPersonasSummaryAsync(normalizedId);
            var updatedDto = ToDto(existing, sessionCount, personas);

            if (hubContext != null)
            {
                await hubContext.Clients.All.SendAsync("AgentUpdated", updatedDto);
            }

            return Ok(updatedDto);
        }

        return await RegisterAgent(req);
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentAgent()
    {
        var agentId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("AgentId")?.Value;

        if (string.IsNullOrEmpty(agentId))
        {
            return Unauthorized(new { error = "No agent identity found in token claims." });
        }

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{agentId}' not found." });
        }

        var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == agent.Id || s.CreatorAgentId == agent.Id);
        var personas = await GetDriverPersonasSummaryAsync(agent.Id);
        return Ok(ToDto(agent, sessionCount, personas));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgents()
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync();

        var sessionCounts = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.CreatorAgentId != null || s.DriverId != null)
            .GroupBy(s => s.DriverId ?? s.CreatorAgentId!)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var allPersonas = await db.Personas
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();

        var personasByDriver = allPersonas
            .GroupBy(p => p.DriverId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new DriverPersonaSummaryDto(p.Id, p.Name, p.Role, p.DefaultModel)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var dtos = agents
            .Select(a => ToDto(
                a,
                sessionCounts.GetValueOrDefault(a.Id, 0),
                personasByDriver.GetValueOrDefault(a.Id, new List<DriverPersonaSummaryDto>())))
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgent(string id)
    {
        var normalizedId = id.ToLowerInvariant();
        var agent = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == normalizedId);

        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{id}' not found." });
        }

        var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);
        var personas = await GetDriverPersonasSummaryAsync(normalizedId);
        return Ok(ToDto(agent, sessionCount, personas));
    }

    /// <summary>
    /// Reads a driver's runtime configuration out of its capability blob, without the API key.
    /// </summary>
    [HttpGet("{id}/config")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDriverConfig(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        return Ok(DriverCapabilities.Parse(agent.CapabilitiesJson));
    }

    /// <summary>
    /// Updates a driver's runtime configuration. Sends only the fields being changed; the blob is
    /// merged, so unset fields keep their stored values.
    /// </summary>
    [HttpPut("{id}/config")]
    public async Task<IActionResult> UpdateDriverConfig(string id, [FromBody] UpdateDriverConfigRequest req)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var patch = new JsonObject();
        if (req.Endpoint is not null) patch["endpoint"] = req.Endpoint;
        if (req.DefaultModel is not null) patch["defaultModel"] = req.DefaultModel;
        if (req.TimeoutSeconds is not null) patch["timeoutSeconds"] = req.TimeoutSeconds;
        if (req.MaxRetries is not null) patch["maxRetries"] = req.MaxRetries;
        if (req.RetryDelayMilliseconds is not null) patch["retryDelayMilliseconds"] = req.RetryDelayMilliseconds;
        if (req.MaxSteps is not null) patch["maxSteps"] = req.MaxSteps;
        if (req.Capabilities is not null)
        {
            var caps = new JsonArray();
            foreach (var cap in req.Capabilities) caps.Add(JsonValue.Create(cap));
            patch["capabilities"] = caps;
        }

        // The key is write-only from the dashboard's point of view: an empty/absent value means
        // "leave what's stored", never "erase". Clearing it requires the CLI's local config.
        if (!string.IsNullOrWhiteSpace(req.ApiKey)) patch["apiKey"] = req.ApiKey;

        if (req.Adapter is not null) patch["adapter"] = req.Adapter;
        if (req.LoginCommand is not null) patch["loginCommand"] = req.LoginCommand;
        if (req.AuthCheckCommand is not null) patch["authCheckCommand"] = req.AuthCheckCommand;
        if (req.Sidecar is not null) patch["sidecar"] = JsonSerializer.SerializeToNode(req.Sidecar);

        if (patch.Count == 0)
        {
            return Ok(DriverCapabilities.Parse(agent.CapabilitiesJson));
        }

        agent.CapabilitiesJson = DriverCapabilities.Merge(agent.CapabilitiesJson, patch.ToJsonString())
            ?? agent.CapabilitiesJson;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);
        var personas = await GetDriverPersonasSummaryAsync(normalizedId);
        var dto = ToDto(agent, sessionCount, personas);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("AgentUpdated", dto);
        }

        return Ok(DriverCapabilities.Parse(agent.CapabilitiesJson));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAgent(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{id}' not found." });
        }

        var boundPersonas = await db.Personas.Where(p => p.DriverId == normalizedId).ToListAsync();
        if (boundPersonas.Count > 0)
        {
            var names = string.Join(", ", boundPersonas.Select(p => $"@{p.Id} ({p.Name})"));
            return BadRequest(new { error = $"Cannot delete driver '{normalizedId}' because it is assigned to {boundPersonas.Count} persona(s): {names}. Reassign those personas to another driver first." });
        }

        var agentJobs = await db.AgentJobs.Where(j => j.RequestedByAgentId == normalizedId).ToListAsync();
        if (agentJobs.Count > 0)
        {
            db.AgentJobs.RemoveRange(agentJobs);
        }

        var sessions = await db.AgentSessions.Where(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId).ToListAsync();
        foreach (var session in sessions)
        {
            if (session.DriverId == normalizedId) session.DriverId = null;
            if (session.CreatorAgentId == normalizedId) session.CreatorAgentId = null;
        }

        db.Agents.Remove(agent);
        await db.SaveChangesAsync();

        AgentMeshHub.UnregisterAgent(normalizedId);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("AgentDeleted", new { id = normalizedId });
        }

        return NoContent();
    }

    [HttpGet("{id}/personas")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDriverPersonas(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var driver = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (driver == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var personas = await db.Personas
            .AsNoTracking()
            .Where(p => p.DriverId == normalizedId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var sessionCounts = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.PersonaId != null)
            .GroupBy(s => s.PersonaId!)
            .Select(g => new { PersonaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PersonaId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var dtos = personas
            .Select(p => PersonasController.ToDto(p, driver, sessionCounts.GetValueOrDefault(p.Id, 0)))
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}/sessions")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgentSessions(string id, [FromQuery] int? take = null, [FromQuery] int? skip = null)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var driver = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == normalizedId);
        var personas = await db.Personas
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

        IQueryable<AgentSession> filtered = db.AgentSessions
            .AsNoTracking()
            .Where(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);

        // Mirrors the persona card contract: an exact owner-scoped total (the global /api/sessions
        // window is capped at 100 rows) plus an opt-in page. The CLI polls this un-paged.
        // Counted off the unordered queryable so the aggregate carries no sort node.
        var totalCount = await filtered.CountAsync();

        IQueryable<AgentSession> sessionQuery = filtered.OrderByDescending(s => s.UpdatedAt);
        // ControllerContext.HttpContext is null for direct (non-HTTP) action invocation.
        if (ControllerContext.HttpContext is { } httpContext)
        {
            httpContext.Response.Headers["X-Total-Count"] = totalCount.ToString();
        }

        if (take is > 0)
        {
            sessionQuery = sessionQuery.Skip(Math.Max(0, skip ?? 0)).Take(take.Value);
        }

        var rawSessions = await sessionQuery
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                s.CreatorAgentId,
                s.DriverId,
                s.PersonaId,
                MessageCount = s.Messages.Count,
                s.CreatedAt,
                s.UpdatedAt
            })
            .ToListAsync();

        var sessions = rawSessions.Select(s =>
        {
            var dId = s.DriverId ?? s.CreatorAgentId;
            return new SessionSummaryDto(
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                dId,
                s.MessageCount,
                s.CreatedAt,
                s.UpdatedAt,
                DriverId: dId,
                DriverName: driver?.Name ?? dId,
                PersonaId: s.PersonaId,
                PersonaName: s.PersonaId != null ? personas.GetValueOrDefault(s.PersonaId, s.PersonaId) : null
            );
        }).ToList();

        return Ok(sessions);
    }

    [HttpPost("{id}/sessions")]
    public async Task<IActionResult> CreateAgentSession(string id, [FromBody] CreateAgentSessionRequest req)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{normalizedId}' not found." });
        }

        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return BadRequest(new { error = "Session Title is required." });
        }

        Persona? persona = null;
        if (!string.IsNullOrWhiteSpace(req.PersonaId))
        {
            var normPersonaId = req.PersonaId.Trim().ToLowerInvariant();
            persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normPersonaId);
        }

        var effectiveWorkDir = !string.IsNullOrWhiteSpace(req.WorkingDirectory) ? req.WorkingDirectory : null;
        var effectiveModel = !string.IsNullOrWhiteSpace(req.Model) ? req.Model : persona?.DefaultModel;
        var effectiveEffort = req.Effort != null ? req.Effort : persona?.DefaultEffort;
        var effectiveMode = !string.IsNullOrWhiteSpace(req.Mode) ? req.Mode : persona?.DefaultMode;
        var effectiveTier = !string.IsNullOrWhiteSpace(req.Tier) ? req.Tier : persona?.DefaultTier;
        var effectiveContextSize = req.ContextSize != null ? req.ContextSize : persona?.DefaultContextSize;

        var hasAttachments = req.Attachments is { Count: > 0 };
        var hasInitialPrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt) || hasAttachments;
        var initialMetadata = SessionsController.MergeSessionMetadata(
            req.MetadataJson,
            effectiveWorkDir,
            effectiveModel,
            effectiveEffort,
            effectiveMode,
            effectiveTier,
            persona?.Id,
            persona?.Name,
            persona?.Role,
            persona?.PersonaPrompt,
            effectiveContextSize);

        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = hasInitialPrompt ? "thinking" : "active",
            MetadataJson = initialMetadata,
            PersonaId = persona?.Id,
            DriverId = normalizedId,
            CreatorAgentId = normalizedId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        SessionMessage? promptMsg = null;
        if (hasInitialPrompt)
        {
            var effectivePrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt)
                ? req.InitialPrompt.Trim()
                : SessionsController.DefaultAttachmentPrompt;
            promptMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "operator",
                TargetAgentId = normalizedId,
                EventType = "prompt",
                Content = effectivePrompt,
                StructuredPayloadJson = SessionsController.MergeAttachmentsIntoStructuredPayload(null, req.Attachments),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(promptMsg);
            if (AgentMeshHub.IsAgentConnected(normalizedId))
            {
                agent.Status = "busy";
                agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("SessionCreated", new SessionSummaryDto(
                session.Id,
                session.Title,
                session.Status,
                session.MetadataJson,
                session.DriverId,
                hasInitialPrompt ? 1 : 0,
                session.CreatedAt,
                session.UpdatedAt,
                DriverId: session.DriverId,
                DriverName: agent.Name,
                PersonaId: persona?.Id,
                PersonaName: persona?.Name
            ));

            if (promptMsg != null)
            {
                var (workDir, model, effort, mode, tier) = SessionsController.ExtractSessionSettings(session.MetadataJson);
                var contextSize = SessionsController.ExtractSessionContextSize(session.MetadataJson);
                await hubContext.Clients.Group($"agent-{normalizedId}").SendAsync("PromptReceived", new
                {
                    sessionId = session.Id,
                    messageId = promptMsg.Id,
                    agentId = normalizedId,
                    driverId = normalizedId,
                    personaId = persona?.Id,
                    personaName = persona?.Name,
                    personaRole = persona?.Role,
                    personaPrompt = persona?.PersonaPrompt,
                    prompt = promptMsg.Content,
                    sender = promptMsg.SenderAgentId,
                    workingDirectory = workDir,
                    model = model,
                    effort = effort,
                    mode = mode,
                    tier = tier,
                    contextSize = contextSize,
                    attachments = req.Attachments,
                    createdAt = promptMsg.CreatedAt
                });
            }
        }

        return CreatedAtAction("GetSession", "Sessions", new { id = session.Id }, session);
    }

    private async Task<List<DriverPersonaSummaryDto>> GetDriverPersonasSummaryAsync(string driverId)
    {
        return await db.Personas
            .AsNoTracking()
            .Where(p => p.DriverId == driverId)
            .OrderBy(p => p.Name)
            .Select(p => new DriverPersonaSummaryDto(p.Id, p.Name, p.Role, p.DefaultModel))
            .ToListAsync();
    }

    [HttpGet("{id}/usage")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgentUsage(string id, [FromQuery] bool refresh = false)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{normalizedId}' not found." });
        }

        var sessions = await db.AgentSessions
            .Where(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId)
            .ToListAsync();

        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        long totalThinkingTokens = 0;
        long totalTokens = 0;
        double totalDurationSeconds = 0;

        foreach (var sess in sessions)
        {
            if (string.IsNullOrWhiteSpace(sess.MetadataJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(sess.MetadataJson);
                if (doc.RootElement.TryGetProperty("cumulativeUsage", out var usage))
                {
                    if (usage.TryGetProperty("inputTokens", out var ip)) totalInputTokens += ip.GetInt64();
                    if (usage.TryGetProperty("outputTokens", out var op)) totalOutputTokens += op.GetInt64();
                    if (usage.TryGetProperty("thinkingTokens", out var tp)) totalThinkingTokens += tp.GetInt64();
                    if (usage.TryGetProperty("totalTokens", out var totp)) totalTokens += totp.GetInt64();
                    if (usage.TryGetProperty("totalDurationSeconds", out var dp)) totalDurationSeconds += dp.GetDouble();
                }
            }
            catch { }
        }

        if (totalTokens == 0 && (totalInputTokens > 0 || totalOutputTokens > 0))
        {
            totalTokens = totalInputTokens + totalOutputTokens;
        }

        var providerQuota = await GetAgentProviderQuotaAsync(agent, refresh);

        return Ok(new AgentUsageResponse(
            AgentId: normalizedId,
            AgentName: agent.Name,
            SessionCount: sessions.Count,
            LifetimeTokens: new LifetimeTokensDto(
                InputTokens: totalInputTokens,
                OutputTokens: totalOutputTokens,
                ThinkingTokens: totalThinkingTokens,
                TotalTokens: totalTokens,
                TotalDurationSeconds: totalDurationSeconds
            ),
            ProviderQuota: providerQuota
        ));
    }

    private static readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, ProviderQuotaDto Quota)> QuotaCache = new();

    private static async Task<ProviderQuotaDto?> GetAgentProviderQuotaAsync(Agent agent, bool refresh = false)
    {
        var id = agent.Id.ToLowerInvariant();
        var isAgy = id == "agy" || id.Contains("agy") || id.Contains("antigravity");
        var isCopilot = id == "copilot" || id.Contains("copilot");

        if (isAgy)
        {
            if (!refresh &&
                QuotaCache.TryGetValue("agy", out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(60))
            {
                return cached.Quota;
            }

            var agyPath = ResolveExecutablePath("agy");
            if (agyPath != null)
            {
                try
                {
                    using var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = agyPath,
                            ArgumentList = { "-p", "/usage", "--output-format", "json" },
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    proc.Start();
                    var outputTask = proc.StandardOutput.ReadToEndAsync();
                    var errorTask = proc.StandardError.ReadToEndAsync();
                    var exitTask = proc.WaitForExitAsync();
                    if (await Task.WhenAny(exitTask, Task.Delay(8000)) == exitTask)
                    {
                        var stdOut = await outputTask;
                        if (!string.IsNullOrWhiteSpace(stdOut))
                        {
                            var parsed = ParseAgyUsageJson(stdOut);
                            if (parsed != null)
                            {
                                QuotaCache["agy"] = (DateTimeOffset.UtcNow, parsed);
                                return parsed;
                            }
                        }
                    }
                    else
                    {
                        try { proc.Kill(true); } catch { }
                    }
                }
                catch { }
            }

            // Fallback default mock if binary not callable
            var defaultAgyQuota = new ProviderQuotaDto(
                Provider: "Google Antigravity",
                PlanName: "Pro Tier",
                PrimaryRemainingFraction: 0.88,
                Groups: new List<QuotaGroupDto>
                {
                    new("Gemini Models", "Gemini Flash & Gemini Pro", new List<QuotaBucketDto>
                    {
                        new("gemini-weekly", "Weekly Limit Remaining", "Refreshes weekly", "weekly", 0.88, DateTimeOffset.UtcNow.AddDays(6).ToString("u")),
                        new("gemini-5h", "Five Hour Limit Remaining", "Refreshes every 5 hours", "5h", 0.90, DateTimeOffset.UtcNow.AddHours(3).ToString("u"))
                    }),
                    new("Claude & GPT Models", "Claude Opus, Sonnet & GPT-OSS", new List<QuotaBucketDto>
                    {
                        new("3p-weekly", "Weekly Limit Remaining", "Shared 3rd-party weekly limit", "weekly", 1.0, DateTimeOffset.UtcNow.AddDays(7).ToString("u")),
                        new("3p-5h", "Five Hour Limit Remaining", "Shared 3rd-party 5-hour limit", "5h", 1.0, DateTimeOffset.UtcNow.AddHours(5).ToString("u"))
                    }),
                    new("Gemini 4 Argon", "Argon Weekly Allowance", new List<QuotaBucketDto>
                    {
                        new("gemini-argon-wtu-weekly", "Gemini 4 Argon Weekly Limit", "Dedicated Argon capacity", "weekly", 0.20, DateTimeOffset.UtcNow.AddDays(6).ToString("u"))
                    })
                }
            );
            return defaultAgyQuota;
        }

        if (isCopilot)
        {
            return new ProviderQuotaDto(
                Provider: "GitHub Copilot",
                PlanName: "Copilot Free / Pro",
                PrimaryRemainingFraction: 1.0,
                Groups: new List<QuotaGroupDto>
                {
                    new("AI Credits & Completions", "Monthly Model Access Allowance", new List<QuotaBucketDto>
                    {
                        new("copilot-credits", "Monthly AI Credits", "Active subscription quota", "monthly", 1.0, DateTimeOffset.UtcNow.AddDays(14).ToString("u")),
                        new("copilot-autopilot", "Autopilot Continuation Cap", "Default 5 continues per turn", "turn", 1.0, null)
                    })
                }
            );
        }

        return null;
    }

    private static ProviderQuotaDto? ParseAgyUsageJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmdProp) ||
                !cmdProp.TryGetProperty("data", out var dataProp) ||
                !dataProp.TryGetProperty("groups", out var groupsElem))
            {
                return null;
            }

            var groups = new List<QuotaGroupDto>();
            double? primaryRemaining = null;

            foreach (var g in groupsElem.EnumerateArray())
            {
                var gName = g.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "";
                var gDesc = g.TryGetProperty("description", out var gd) ? gd.GetString() : null;
                var buckets = new List<QuotaBucketDto>();

                if (g.TryGetProperty("buckets", out var bucketsElem))
                {
                    foreach (var b in bucketsElem.EnumerateArray())
                    {
                        var bId = b.TryGetProperty("id", out var bi) ? bi.GetString() ?? "" : "";
                        var bName = b.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "";
                        var bDesc = b.TryGetProperty("description", out var bd) ? bd.GetString() : null;
                        var bWindow = b.TryGetProperty("window", out var bw) ? bw.GetString() : null;
                        var bFrac = b.TryGetProperty("remaining_fraction", out var bf) ? bf.GetDouble() : 1.0;
                        var bReset = b.TryGetProperty("reset_time", out var br) ? br.GetString() : null;

                        if (bId == "gemini-weekly" || (primaryRemaining == null && bWindow == "weekly"))
                        {
                            primaryRemaining = bFrac;
                        }

                        buckets.Add(new QuotaBucketDto(bId, bName, bDesc, bWindow, bFrac, bReset));
                    }
                }

                groups.Add(new QuotaGroupDto(gName, gDesc, buckets));
            }

            if (primaryRemaining == null && groups.Count > 0 && groups[0].Buckets.Count > 0)
            {
                primaryRemaining = groups[0].Buckets[0].RemainingFraction;
            }

            return new ProviderQuotaDto(
                Provider: "Google Antigravity",
                PlanName: "Pro Tier",
                PrimaryRemainingFraction: primaryRemaining ?? 1.0,
                Groups: groups
            );
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (System.IO.File.Exists(command)) return Path.GetFullPath(command);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidateDirs = new List<string>();

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidateDirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        if (!string.IsNullOrWhiteSpace(home))
        {
            candidateDirs.Add(Path.Combine(home, ".local", "bin"));
            candidateDirs.Add(Path.Combine(home, ".gemini", "antigravity-cli", "bin"));
        }

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "", ".exe", ".cmd", ".bat", ".ps1" }
            : new[] { "" };

        foreach (var dir in candidateDirs)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var fullPath = Path.Combine(dir, command + ext);
                    if (System.IO.File.Exists(fullPath)) return fullPath;
                }
                catch { }
            }
        }

        return null;
    }

    private static AgentDto ToDto(Agent a, int sessionCount, List<DriverPersonaSummaryDto>? personas = null)
    {
        var isConnected = AgentMeshHub.IsAgentConnected(a.Id);
        var status = isConnected
            ? (a.Status == "busy" ? "busy" : "online")
            : (a.Status == "busy" && a.LastHeartbeatAt > DateTimeOffset.UtcNow.AddMinutes(-5)
                ? "busy"
                : (a.LastHeartbeatAt > DateTimeOffset.UtcNow.AddSeconds(-60)
                    ? a.Status
                    : "offline"));

        return new(
            a.Id,
            a.Name,
            a.RuntimeType.ToString(),
            (int)a.TrustTier,
            a.PersonaPrompt,
            a.CreatedAt,
            a.Role,
            status,
            a.CapabilitiesJson,
            a.LastHeartbeatAt,
            sessionCount,
            personas ?? new List<DriverPersonaSummaryDto>(),
            DriverCapabilities.Parse(a.CapabilitiesJson)
        );
    }
}

public record DriverPersonaSummaryDto(
    string Id,
    string Name,
    string Role,
    string? DefaultModel
);

public record RegisterAgentRequest(
    string Id,
    string Name,
    RuntimeType RuntimeType = RuntimeType.CliAgy,
    TrustTier TrustTier = TrustTier.Intern,
    string? PersonaPrompt = null,
    string? MemorySummary = null,
    string? Role = null,
    string? Status = null,
    string? CapabilitiesJson = null
);

/// <summary>
/// Patch-style driver config update. Null fields are left untouched; a blank ApiKey means
/// "keep the stored one" rather than "clear it".
/// </summary>
public record UpdateDriverConfigRequest(
    string? Adapter = null,
    string? Endpoint = null,
    string? ApiKey = null,
    string? DefaultModel = null,
    int? TimeoutSeconds = null,
    int? MaxRetries = null,
    int? RetryDelayMilliseconds = null,
    int? MaxSteps = null,
    List<string>? Capabilities = null,
    DriverSidecarConfig? Sidecar = null,
    string? LoginCommand = null,
    string? AuthCheckCommand = null
);

public record CreateAgentSessionRequest(
    string Title,
    string? InitialPrompt = null,
    string? MetadataJson = null,
    string? WorkingDirectory = null,
    string? Model = null,
    string? Effort = null,
    string? Mode = null,
    string? Tier = null,
    string? PersonaId = null,
    List<SessionAttachmentDto>? Attachments = null,
    string? ContextSize = null
);

public record AgentDto(
    string Id,
    string Name,
    string RuntimeType,
    int TrustTier,
    string PersonaPrompt,
    DateTimeOffset CreatedAt,
    string Role = "Autonomous Agent",
    string Status = "online",
    string CapabilitiesJson = "[]",
    DateTimeOffset? LastHeartbeatAt = null,
    int SessionCount = 0,
    List<DriverPersonaSummaryDto>? Personas = null,
    /// <summary>Parsed runtime limits. Safe to render: never contains the API key.</summary>
    DriverCapabilitiesView? Config = null
);

public record QuotaBucketDto(
    string Id,
    string Name,
    string? Description,
    string? Window,
    double RemainingFraction,
    string? ResetTime
);

public record QuotaGroupDto(
    string Name,
    string? Description,
    List<QuotaBucketDto> Buckets
);

public record ProviderQuotaDto(
    string Provider,
    string PlanName,
    double PrimaryRemainingFraction,
    List<QuotaGroupDto> Groups
);

public record LifetimeTokensDto(
    long InputTokens,
    long OutputTokens,
    long ThinkingTokens,
    long TotalTokens,
    double TotalDurationSeconds
);

public record AgentUsageResponse(
    string AgentId,
    string AgentName,
    int SessionCount,
    LifetimeTokensDto LifetimeTokens,
    ProviderQuotaDto? ProviderQuota
);
