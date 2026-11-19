using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PersonasController(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub>? hubContext = null,
    AgentMesh.Core.Services.IPersonaContainerManager? containerManager = null,
    ICredentialEncryptionService? encryptionService = null) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetPersonas([FromQuery] string? driverId = null)
    {
        var query = db.Personas.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(driverId))
        {
            var normDriver = driverId.Trim().ToLowerInvariant();
            query = query.Where(p => p.DriverId == normDriver);
        }

        var personas = await query
            .OrderBy(p => p.Name)
            .ToListAsync();

        var drivers = await db.Agents
            .AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d, StringComparer.OrdinalIgnoreCase);

        var sessionCounts = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.PersonaId != null)
            .GroupBy(s => s.PersonaId!)
            .Select(g => new { PersonaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PersonaId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var mcpCounts = await db.PersonaMcpServers
            .AsNoTracking()
            .GroupBy(pm => pm.PersonaId)
            .Select(g => new { PersonaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PersonaId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var dtos = personas.Select(p =>
        {
            drivers.TryGetValue(p.DriverId, out var driver);
            return ToDto(
                p,
                driver,
                sessionCounts.GetValueOrDefault(p.Id, 0),
                encryptionService,
                mcpCounts.GetValueOrDefault(p.Id, 0));
        }).ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPersona(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == normalizedId);

        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var driver = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == persona.DriverId);

        var sessionCount = await db.AgentSessions.CountAsync(s => s.PersonaId == normalizedId);
        var mcpCount = await db.PersonaMcpServers.CountAsync(pm => pm.PersonaId == normalizedId);
        return Ok(ToDto(persona, driver, sessionCount, encryptionService, mcpCount));
    }

    [HttpPost]
    public async Task<IActionResult> CreatePersona([FromBody] CreatePersonaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Persona Name is required." });
        }

        if (string.IsNullOrWhiteSpace(req.DriverId))
        {
            return BadRequest(new { error = "Persona DriverId is required." });
        }

        var normalizedId = !string.IsNullOrWhiteSpace(req.Id)
            ? NormalizeSlug(req.Id)
            : NormalizeSlug(req.Name);

        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return BadRequest(new { error = "A valid Persona Id or Name is required." });
        }

        var existing = await db.Personas.FirstOrDefaultAsync(p => p.Id == normalizedId);
        if (existing != null)
        {
            return Conflict(new { error = $"Persona with Id '{normalizedId}' already exists." });
        }

        var normalizedDriverId = req.DriverId.Trim().ToLowerInvariant();
        var driver = await EnsureDriverExistsAsync(normalizedDriverId);

        string? encryptedToken = null;
        if (!string.IsNullOrWhiteSpace(req.GitHubToken) && encryptionService != null)
        {
            encryptedToken = encryptionService.Encrypt(req.GitHubToken.Trim());
        }

        var persona = new Persona
        {
            Id = normalizedId,
            Name = req.Name.Trim(),
            Role = !string.IsNullOrWhiteSpace(req.Role) ? req.Role.Trim() : "Autonomous Persona",
            PersonaPrompt = req.PersonaPrompt?.Trim() ?? $"You are {req.Name.Trim()}, acting as {req.Role?.Trim() ?? "an Autonomous Persona"}.",
            DriverId = normalizedDriverId,
            DefaultModel = NormalizeOptional(req.DefaultModel),
            DefaultEffort = NormalizeOptional(req.DefaultEffort),
            DefaultMode = NormalizeOptional(req.DefaultMode),
            DefaultTier = NormalizeOptional(req.DefaultTier),
            DefaultContextSize = NormalizeOptional(req.DefaultContextSize),
            MaxConcurrentJobs = req.MaxConcurrentJobs > 0 ? req.MaxConcurrentJobs : null,
            TrustTier = req.TrustTier,
            GitHubTokenEncrypted = encryptedToken,
            GitAuthorName = NormalizeOptional(req.GitAuthorName),
            GitAuthorEmail = NormalizeOptional(req.GitAuthorEmail),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        var dto = ToDto(persona, driver, 0, encryptionService);
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("PersonaCreated", dto);
        }

        return CreatedAtAction(nameof(GetPersona), new { id = persona.Id }, dto);
    }

    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdatePersona(string id, [FromBody] UpdatePersonaRequest req)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normalizedId);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (!string.IsNullOrWhiteSpace(req.Name))
        {
            persona.Name = req.Name.Trim();
        }

        if (req.Role != null)
        {
            persona.Role = string.IsNullOrWhiteSpace(req.Role) ? "Autonomous Persona" : req.Role.Trim();
        }

        if (req.PersonaPrompt != null)
        {
            persona.PersonaPrompt = req.PersonaPrompt.Trim();
        }

        if (!string.IsNullOrWhiteSpace(req.DriverId))
        {
            var normalizedDriverId = req.DriverId.Trim().ToLowerInvariant();
            await EnsureDriverExistsAsync(normalizedDriverId);
            persona.DriverId = normalizedDriverId;
        }

        if (req.DefaultModel != null)
        {
            persona.DefaultModel = NormalizeOptional(req.DefaultModel);
        }

        if (req.DefaultEffort != null)
        {
            persona.DefaultEffort = NormalizeOptional(req.DefaultEffort);
        }

        if (req.DefaultMode != null)
        {
            persona.DefaultMode = NormalizeOptional(req.DefaultMode);
        }

        if (req.DefaultTier != null)
        {
            persona.DefaultTier = NormalizeOptional(req.DefaultTier);
        }

        if (req.DefaultContextSize != null)
        {
            persona.DefaultContextSize = NormalizeOptional(req.DefaultContextSize);
        }

        if (req.MaxConcurrentJobs.HasValue)
        {
            persona.MaxConcurrentJobs = req.MaxConcurrentJobs.Value > 0 ? req.MaxConcurrentJobs.Value : null;
        }

        if (req.TrustTier.HasValue)
        {
            persona.TrustTier = req.TrustTier.Value;
        }

        var gitIdentityChanged = req.GitHubToken != null || req.GitAuthorName != null || req.GitAuthorEmail != null;

        if (req.GitHubToken != null)
        {
            if (string.IsNullOrWhiteSpace(req.GitHubToken))
            {
                persona.GitHubTokenEncrypted = null;
            }
            else if (encryptionService != null)
            {
                persona.GitHubTokenEncrypted = encryptionService.Encrypt(req.GitHubToken.Trim());
            }
        }

        if (req.GitAuthorName != null)
        {
            persona.GitAuthorName = NormalizeOptional(req.GitAuthorName);
        }

        if (req.GitAuthorEmail != null)
        {
            persona.GitAuthorEmail = NormalizeOptional(req.GitAuthorEmail);
        }

        persona.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (gitIdentityChanged && containerManager != null)
        {
            var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
            try
            {
                var envStatus = await containerManager.GetStatusAsync(normalizedId, ct);
                if (envStatus.Status == "error")
                {
                    await containerManager.RestartAsync(normalizedId, ct);
                }
                else if (envStatus.Status is "running" or "starting")
                {
                    await containerManager.EnsureRunningAsync(normalizedId, ct);
                }
            }
            catch { }
        }

        var driver = await db.Agents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == persona.DriverId);
        var sessionCount = await db.AgentSessions.CountAsync(s => s.PersonaId == normalizedId);
        var mcpCount = await db.PersonaMcpServers.CountAsync(pm => pm.PersonaId == normalizedId);
        var dto = ToDto(persona, driver, sessionCount, encryptionService, mcpCount);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("PersonaUpdated", dto);
        }

        return Ok(dto);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePersona(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normalizedId);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var mcpAssignments = await db.PersonaMcpServers
            .Where(pm => pm.PersonaId == normalizedId)
            .ToListAsync();
        if (mcpAssignments.Count > 0)
        {
            db.PersonaMcpServers.RemoveRange(mcpAssignments);
        }

        var personaSessions = await db.AgentSessions
            .Where(s => s.PersonaId == normalizedId)
            .ToListAsync();

        var sessionIds = personaSessions.Select(s => s.Id).ToList();
        if (sessionIds.Count > 0)
        {
            var messages = await db.SessionMessages
                .Where(m => sessionIds.Contains(m.SessionId))
                .ToListAsync();
            db.SessionMessages.RemoveRange(messages);

            var jobs = await db.AgentJobs
                .Where(j => sessionIds.Contains(j.SessionId))
                .ToListAsync();
            db.AgentJobs.RemoveRange(jobs);

            db.AgentSessions.RemoveRange(personaSessions);
        }

        db.Personas.Remove(persona);
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("PersonaDeleted", new
            {
                id = normalizedId,
                deletedSessionIds = sessionIds
            });
        }

        return Ok(new
        {
            status = "deleted",
            id = normalizedId,
            deletedSessionsCount = sessionIds.Count
        });
    }

    [HttpGet("{id}/sessions")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPersonaSessions(string id, [FromQuery] int? take = null, [FromQuery] int? skip = null)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var drivers = await db.Agents
            .AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.Name, StringComparer.OrdinalIgnoreCase);

        IQueryable<AgentSession> filtered = db.AgentSessions
            .AsNoTracking()
            .Where(s => s.PersonaId == normalizedId);

        // The dashboard's persona card collapses its session list and lazily expands it, so it needs
        // the owner's true total (the global /api/sessions window is capped) plus a page-sized slice.
        // Paging is opt-in: the CLI polls this endpoint un-paged and must keep receiving everything.
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

        var sessions = await sessionQuery
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                s.DriverId,
                s.CreatorAgentId,
                s.PersonaId,
                MessageCount = s.Messages.Count,
                s.CreatedAt,
                s.UpdatedAt
            })
            .ToListAsync();

        var result = sessions.Select(s =>
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
                DriverName: dId != null ? drivers.GetValueOrDefault(dId, dId) : null,
                PersonaId: s.PersonaId,
                PersonaName: persona.Name
            );
        }).ToList();

        return Ok(result);
    }

    [HttpGet("{id}/environment")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPersonaEnvironment(string id, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return Ok(new AgentMesh.Core.Services.PersonaEnvironmentStatus(normalizedId, "none", "none"));
        }

        var status = await containerManager.GetStatusAsync(normalizedId, ct);
        return Ok(status);
    }

    [HttpPost("{id}/environment/wake")]
    public async Task<IActionResult> WakePersonaEnvironment(string id, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return BadRequest(new { error = "Container manager is not available." });
        }

        await containerManager.EnsureRunningAsync(normalizedId, ct);
        var status = await containerManager.GetStatusAsync(normalizedId, ct);
        return Ok(status);
    }

    [HttpPost("{id}/environment/stop")]
    public async Task<IActionResult> StopPersonaEnvironment(string id, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return BadRequest(new { error = "Container manager is not available." });
        }

        await containerManager.StopAsync(normalizedId, ct);
        var status = await containerManager.GetStatusAsync(normalizedId, ct);
        return Ok(status);
    }

    [HttpPost("{id}/environment/restart")]
    public async Task<IActionResult> RestartPersonaEnvironment(string id, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return BadRequest(new { error = "Container manager is not available." });
        }

        await containerManager.RestartAsync(normalizedId, ct);
        var status = await containerManager.GetStatusAsync(normalizedId, ct);
        return Ok(status);
    }

    [HttpGet("{id}/environment/logs")]
    public async Task<IActionResult> GetPersonaEnvironmentLogs(
        string id,
        [FromQuery] int tail = 100,
        [FromQuery] string? container = null,
        CancellationToken ct = default)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return Ok(new { logs = "Container manager is not available." });
        }

        var logs = !string.IsNullOrWhiteSpace(container)
            ? await containerManager.GetLogsAsync(normalizedId, Math.Clamp(tail, 10, 1000), container, ct)
            : await containerManager.GetLogsAsync(normalizedId, Math.Clamp(tail, 10, 1000), ct);
        return Ok(new { logs });
    }

    [NonAction]
    public Task<IActionResult> GetPersonaEnvironmentLogs(string id, int tail, CancellationToken ct)
        => GetPersonaEnvironmentLogs(id, tail, null, ct);

    [HttpPost("{id}/environment/verify")]
    public async Task<IActionResult> VerifyPersonaDriver(string id, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return BadRequest(new { error = "Container manager is not available." });
        }

        var result = await containerManager.VerifyDriverAsync(normalizedId, ct);
        return Ok(result);
    }

    [HttpPost("{id}/environment/auth/start")]
    public async Task<IActionResult> StartPersonaAuth(string id, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return BadRequest(new { error = "Container manager is not available." });
        }

        var result = await containerManager.StartAuthAsync(normalizedId, ct);
        return Ok(result);
    }

    public record SubmitAuthCodeRequest(string? Code = null);

    [HttpPost("{id}/environment/auth/submit")]
    public async Task<IActionResult> SubmitPersonaAuthCode(string id, [FromBody] SubmitAuthCodeRequest req, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return BadRequest(new { error = "Container manager is not available." });
        }

        var result = await containerManager.SubmitAuthCodeAsync(normalizedId, req.Code ?? string.Empty, ct);
        return Ok(result);
    }

    public record ConfigureApiKeyRequest(string ApiKey, string? Provider = null);

    [HttpPost("{id}/environment/auth/apikey")]
    public async Task<IActionResult> ConfigurePersonaApiKey(string id, [FromBody] ConfigureApiKeyRequest req, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(req.ApiKey))
        {
            return BadRequest(new { error = "API key is required." });
        }

        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normalizedId, ct);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (containerManager == null)
        {
            return BadRequest(new { error = "Container manager is not available." });
        }

        var result = await containerManager.ConfigureApiKeyAsync(normalizedId, req.ApiKey, req.Provider, ct);
        return Ok(result);
    }

    [HttpPost("{id}/sessions")]
    public async Task<IActionResult> CreatePersonaSession(string id, [FromBody] CreateAgentSessionRequest req)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normalizedId);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{normalizedId}' not found." });
        }

        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return BadRequest(new { error = "Session Title is required." });
        }

        var driverId = persona.DriverId.Trim().ToLowerInvariant();
        var driver = await EnsureDriverExistsAsync(driverId);

        var effectiveWorkDir = !string.IsNullOrWhiteSpace(req.WorkingDirectory)
            ? req.WorkingDirectory
            : null;
        var effectiveModel = !string.IsNullOrWhiteSpace(req.Model)
            ? req.Model
            : persona.DefaultModel;
        var effectiveEffort = req.Effort != null
            ? req.Effort
            : persona.DefaultEffort;
        var effectiveMode = !string.IsNullOrWhiteSpace(req.Mode)
            ? req.Mode
            : persona.DefaultMode;
        var effectiveTier = !string.IsNullOrWhiteSpace(req.Tier)
            ? req.Tier
            : persona.DefaultTier;
        var effectiveContextSize = req.ContextSize != null
            ? req.ContextSize
            : persona.DefaultContextSize;

        var initialMetadata = SessionsController.MergeSessionMetadata(
            req.MetadataJson,
            effectiveWorkDir,
            effectiveModel,
            effectiveEffort,
            effectiveMode,
            effectiveTier,
            persona.Id,
            persona.Name,
            persona.Role,
            persona.PersonaPrompt,
            effectiveContextSize);

        var hasAttachments = req.Attachments is { Count: > 0 };
        var hasInitialPrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt) || hasAttachments;
        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = hasInitialPrompt ? "thinking" : "active",
            MetadataJson = initialMetadata,
            PersonaId = persona.Id,
            DriverId = driverId,
            CreatorAgentId = driverId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        SessionMessage? promptMsg = null;
        if (hasInitialPrompt)
        {
            if (containerManager != null)
            {
                var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
                try { await containerManager.EnsureRunningAsync(persona.Id, ct); } catch { }
            }

            var effectivePrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt)
                ? req.InitialPrompt.Trim()
                : SessionsController.DefaultAttachmentPrompt;
            promptMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "operator",
                TargetAgentId = driverId,
                EventType = "prompt",
                Content = effectivePrompt,
                StructuredPayloadJson = SessionsController.MergeAttachmentsIntoStructuredPayload(null, req.Attachments),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(promptMsg);

            if (AgentMeshHub.IsAgentConnected(driverId))
            {
                driver.Status = "busy";
                driver.LastHeartbeatAt = DateTimeOffset.UtcNow;
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
                DriverName: driver.Name,
                PersonaId: persona.Id,
                PersonaName: persona.Name
            ));

            if (promptMsg != null)
            {
                var (workDir, model, effort, mode, tier) = SessionsController.ExtractSessionSettings(session.MetadataJson);
                var contextSize = SessionsController.ExtractSessionContextSize(session.MetadataJson);
                var mcpServers = await PersonaMcpResolver.ResolveForPersonaAsync(db, persona.Id, encryptionService);

                object BuildPayload(List<ResolvedMcpServerConfig>? servers) => new
                {
                    sessionId = session.Id,
                    messageId = promptMsg.Id,
                    agentId = driverId,
                    driverId = driverId,
                    personaId = persona.Id,
                    personaName = persona.Name,
                    personaRole = persona.Role,
                    personaPrompt = persona.PersonaPrompt,
                    gitAuthorName = persona.GitAuthorName,
                    gitAuthorEmail = persona.GitAuthorEmail,
                    prompt = promptMsg.Content,
                    sender = promptMsg.SenderAgentId,
                    workingDirectory = workDir,
                    model = model,
                    effort = effort,
                    mode = mode,
                    tier = tier,
                    contextSize = contextSize,
                    attachments = req.Attachments,
                    mcpServers = servers,
                    createdAt = promptMsg.CreatedAt
                };

                var targetedPayload = BuildPayload(mcpServers);
                var broadcastPayload = BuildPayload(null);

                await hubContext.Clients.Group($"persona-{persona.Id.Trim().ToLowerInvariant()}").SendAsync("PromptReceived", targetedPayload);
                await hubContext.Clients.Group($"agent-{driverId}").SendAsync("PromptReceived", targetedPayload);
                await hubContext.Clients.All.SendAsync("PromptReceived", broadcastPayload);
            }
        }

        return CreatedAtAction("GetSession", "Sessions", new { id = session.Id }, session);
    }

    [HttpGet("{id}/mcp-servers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPersonaMcpServers(string id)
    {
        var normalizedId = McpControllerHelpers.NormalizeId(id);
        var personaExists = await db.Personas.AsNoTracking().AnyAsync(p => p.Id == normalizedId);
        if (!personaExists)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var assignments = await db.PersonaMcpServers
            .AsNoTracking()
            .Include(pm => pm.McpServer)
            .Where(pm => pm.PersonaId == normalizedId && pm.McpServer != null)
            .OrderBy(pm => pm.McpServer!.Name)
            .ToListAsync();

        var dtos = assignments
            .Select(pm => ToMcpDto(pm, pm.McpServer!, encryptionService))
            .ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Returns decrypted, merged MCP server configurations assigned and enabled for this persona.
    /// Authenticated runners/drivers call this when polling pending prompts or backfilling turn context.
    /// </summary>
    [HttpGet("{id}/mcp-servers/resolved")]
    public async Task<IActionResult> GetResolvedPersonaMcpServers(string id, CancellationToken ct)
    {
        var normalizedId = McpControllerHelpers.NormalizeId(id);
        var persona = await db.Personas
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == normalizedId, ct);

        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        if (User?.Identity?.IsAuthenticated == true &&
            User.IsInRole("Driver") &&
            !User.IsInRole("Admin") &&
            !User.HasClaim("IsMasterKey", "true"))
        {
            var callerAgentId = User.FindFirst("AgentId")?.Value;
            if (!string.Equals(callerAgentId, persona.DriverId, StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }
        }

        var resolved = await PersonaMcpResolver.ResolveForPersonaAsync(db, normalizedId, encryptionService, ct);
        return Ok(resolved);
    }

    [HttpGet("{id}/mcp-servers/{serverId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPersonaMcpServer(string id, string serverId)
    {
        var normalizedPersonaId = McpControllerHelpers.NormalizeId(id);
        var personaExists = await db.Personas.AsNoTracking().AnyAsync(p => p.Id == normalizedPersonaId);
        if (!personaExists)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var normalizedServerId = McpControllerHelpers.NormalizeId(serverId);
        var assignment = await db.PersonaMcpServers
            .AsNoTracking()
            .Include(pm => pm.McpServer)
            .FirstOrDefaultAsync(pm => pm.PersonaId == normalizedPersonaId && pm.McpServerId == normalizedServerId);

        if (assignment?.McpServer == null)
        {
            return NotFound(new { error = $"MCP server '{serverId}' is not assigned to persona '{id}'." });
        }

        return Ok(ToMcpDto(assignment, assignment.McpServer, encryptionService));
    }

    [HttpPost("{id}/mcp-servers")]
    public async Task<IActionResult> AssignPersonaMcpServer(string id, [FromBody] AssignPersonaMcpServerRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.McpServerId))
        {
            return BadRequest(new { error = "McpServerId is required." });
        }

        var normalizedPersonaId = McpControllerHelpers.NormalizeId(id);
        var personaExists = await db.Personas.AsNoTracking().AnyAsync(p => p.Id == normalizedPersonaId);
        if (!personaExists)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var normalizedServerId = McpControllerHelpers.NormalizeId(req.McpServerId);
        var server = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == normalizedServerId);
        if (server == null)
        {
            return NotFound(new { error = $"MCP server '{req.McpServerId}' not found." });
        }

        var existing = await db.PersonaMcpServers
            .AsNoTracking()
            .AnyAsync(pm => pm.PersonaId == normalizedPersonaId && pm.McpServerId == normalizedServerId);
        if (existing)
        {
            return Conflict(new { error = $"MCP server '{normalizedServerId}' is already assigned to persona '{normalizedPersonaId}'." });
        }

        if (!McpControllerHelpers.TryEncryptSecretMap(encryptionService, req.EnvOverrides, out var envOverridesEncrypted, out var envError))
        {
            return envError!;
        }

        if (!McpControllerHelpers.TryEncryptSecretMap(encryptionService, req.HeadersOverrides, out var headersOverridesEncrypted, out var headersError))
        {
            return headersError!;
        }

        var now = DateTimeOffset.UtcNow;
        var assignment = new PersonaMcpServer
        {
            PersonaId = normalizedPersonaId,
            McpServerId = normalizedServerId,
            Enabled = req.Enabled,
            EnvOverridesEncrypted = envOverridesEncrypted,
            HeadersOverridesEncrypted = headersOverridesEncrypted,
            AllowedToolsJson = req.AllowedTools != null
                ? JsonSerializer.Serialize(McpControllerHelpers.NormalizeToolList(req.AllowedTools))
                : null,
            BlockedToolsJson = req.BlockedTools != null
                ? JsonSerializer.Serialize(McpControllerHelpers.NormalizeToolList(req.BlockedTools))
                : null,
            AssignedAt = now,
            UpdatedAt = now
        };

        db.PersonaMcpServers.Add(assignment);
        await db.SaveChangesAsync();

        var dto = ToMcpDto(assignment, server, encryptionService);
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("PersonaMcpServerAssigned", dto);
        }

        return CreatedAtAction(
            nameof(GetPersonaMcpServer),
            new { id = normalizedPersonaId, serverId = normalizedServerId },
            dto);
    }

    [HttpPut("{id}/mcp-servers/{serverId}")]
    public async Task<IActionResult> UpsertPersonaMcpServer(
        string id,
        string serverId,
        [FromBody] UpsertPersonaMcpServerRequest req)
    {
        var normalizedPersonaId = McpControllerHelpers.NormalizeId(id);
        var personaExists = await db.Personas.AsNoTracking().AnyAsync(p => p.Id == normalizedPersonaId);
        if (!personaExists)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var normalizedServerId = McpControllerHelpers.NormalizeId(serverId);
        var server = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == normalizedServerId);
        if (server == null)
        {
            return NotFound(new { error = $"MCP server '{serverId}' not found." });
        }

        var assignment = await db.PersonaMcpServers
            .FirstOrDefaultAsync(pm => pm.PersonaId == normalizedPersonaId && pm.McpServerId == normalizedServerId);

        var isNew = assignment == null;
        if (assignment == null)
        {
            var now = DateTimeOffset.UtcNow;
            assignment = new PersonaMcpServer
            {
                PersonaId = normalizedPersonaId,
                McpServerId = normalizedServerId,
                Enabled = req.Enabled ?? true,
                AssignedAt = now,
                UpdatedAt = now
            };
        }

        if (!TryApplyAssignmentUpdates(assignment, req, encryptionService, out var updateError))
        {
            return updateError!;
        }

        if (isNew)
        {
            db.PersonaMcpServers.Add(assignment);
        }

        await db.SaveChangesAsync();

        var dto = ToMcpDto(assignment, server, encryptionService);
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync(
                isNew ? "PersonaMcpServerAssigned" : "PersonaMcpServerUpdated",
                dto);
        }

        return Ok(dto);
    }

    [HttpPatch("{id}/mcp-servers/{serverId}")]
    public async Task<IActionResult> UpdatePersonaMcpServer(
        string id,
        string serverId,
        [FromBody] UpsertPersonaMcpServerRequest req)
    {
        var normalizedPersonaId = McpControllerHelpers.NormalizeId(id);
        var personaExists = await db.Personas.AsNoTracking().AnyAsync(p => p.Id == normalizedPersonaId);
        if (!personaExists)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var normalizedServerId = McpControllerHelpers.NormalizeId(serverId);
        var assignment = await db.PersonaMcpServers
            .Include(pm => pm.McpServer)
            .FirstOrDefaultAsync(pm => pm.PersonaId == normalizedPersonaId && pm.McpServerId == normalizedServerId);

        if (assignment?.McpServer == null)
        {
            return NotFound(new { error = $"MCP server '{serverId}' is not assigned to persona '{id}'." });
        }

        if (!TryApplyAssignmentUpdates(assignment, req, encryptionService, out var updateError))
        {
            return updateError!;
        }

        await db.SaveChangesAsync();

        var dto = ToMcpDto(assignment, assignment.McpServer, encryptionService);
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("PersonaMcpServerUpdated", dto);
        }

        return Ok(dto);
    }

    [HttpDelete("{id}/mcp-servers/{serverId}")]
    public async Task<IActionResult> RemovePersonaMcpServer(string id, string serverId)
    {
        var normalizedPersonaId = McpControllerHelpers.NormalizeId(id);
        var personaExists = await db.Personas.AsNoTracking().AnyAsync(p => p.Id == normalizedPersonaId);
        if (!personaExists)
        {
            return NotFound(new { error = $"Persona '{id}' not found." });
        }

        var normalizedServerId = McpControllerHelpers.NormalizeId(serverId);
        var assignment = await db.PersonaMcpServers
            .FirstOrDefaultAsync(pm => pm.PersonaId == normalizedPersonaId && pm.McpServerId == normalizedServerId);

        if (assignment == null)
        {
            return NotFound(new { error = $"MCP server '{serverId}' is not assigned to persona '{id}'." });
        }

        db.PersonaMcpServers.Remove(assignment);
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("PersonaMcpServerRemoved", new
            {
                personaId = normalizedPersonaId,
                mcpServerId = normalizedServerId
            });
        }

        return Ok(new
        {
            status = "deleted",
            personaId = normalizedPersonaId,
            mcpServerId = normalizedServerId
        });
    }

    private static bool TryApplyAssignmentUpdates(
        PersonaMcpServer assignment,
        UpsertPersonaMcpServerRequest req,
        ICredentialEncryptionService? encryptionService,
        out IActionResult? errorResult)
    {
        if (!McpControllerHelpers.TryUpdateSecretMap(
                encryptionService,
                assignment.EnvOverridesEncrypted,
                req.EnvOverrides,
                req.MergeEnvOverrides,
                req.RemoveEnvOverrideKeys,
                out var nextEnvOverrides,
                out var envError))
        {
            errorResult = envError;
            return false;
        }

        if (!McpControllerHelpers.TryUpdateSecretMap(
                encryptionService,
                assignment.HeadersOverridesEncrypted,
                req.HeadersOverrides,
                req.MergeHeadersOverrides,
                req.RemoveHeaderOverrideKeys,
                out var nextHeadersOverrides,
                out var headersError))
        {
            errorResult = headersError;
            return false;
        }

        if (req.Enabled.HasValue)
        {
            assignment.Enabled = req.Enabled.Value;
        }

        assignment.EnvOverridesEncrypted = nextEnvOverrides;
        assignment.HeadersOverridesEncrypted = nextHeadersOverrides;

        if (req.ClearAllowedTools)
        {
            assignment.AllowedToolsJson = null;
        }
        else if (req.AllowedTools != null)
        {
            assignment.AllowedToolsJson = JsonSerializer.Serialize(McpControllerHelpers.NormalizeToolList(req.AllowedTools));
        }

        if (req.ClearBlockedTools)
        {
            assignment.BlockedToolsJson = null;
        }
        else if (req.BlockedTools != null)
        {
            assignment.BlockedToolsJson = JsonSerializer.Serialize(McpControllerHelpers.NormalizeToolList(req.BlockedTools));
        }

        assignment.UpdatedAt = DateTimeOffset.UtcNow;
        errorResult = null;
        return true;
    }

    public static PersonaMcpServerDto ToMcpDto(
        PersonaMcpServer assignment,
        McpServer server,
        ICredentialEncryptionService? encryption = null)
    {
        return new PersonaMcpServerDto(
            PersonaId: assignment.PersonaId,
            McpServerId: assignment.McpServerId,
            McpServerName: server.Name,
            McpServerDescription: server.Description,
            Transport: server.Transport,
            Command: server.Command,
            Args: McpControllerHelpers.ParseStringList(server.ArgsJson),
            Url: server.Url,
            ServerEnabled: server.Enabled,
            Enabled: assignment.Enabled,
            EffectiveEnabled: server.Enabled && assignment.Enabled,
            TimeoutSeconds: server.TimeoutSeconds,
            ServerEnvMasked: McpControllerHelpers.MaskOrEmpty(encryption, server.EnvEncrypted),
            ServerHeadersMasked: McpControllerHelpers.MaskOrEmpty(encryption, server.HeadersEncrypted),
            EnvOverridesMasked: McpControllerHelpers.MaskOrEmpty(encryption, assignment.EnvOverridesEncrypted),
            HeadersOverridesMasked: McpControllerHelpers.MaskOrEmpty(encryption, assignment.HeadersOverridesEncrypted),
            HasEnvOverrides: !string.IsNullOrWhiteSpace(assignment.EnvOverridesEncrypted),
            HasHeadersOverrides: !string.IsNullOrWhiteSpace(assignment.HeadersOverridesEncrypted),
            AllowedTools: McpControllerHelpers.ParseNullableStringList(assignment.AllowedToolsJson),
            BlockedTools: McpControllerHelpers.ParseNullableStringList(assignment.BlockedToolsJson),
            AssignedAt: assignment.AssignedAt,
            UpdatedAt: assignment.UpdatedAt
        );
    }

    private async Task<Agent> EnsureDriverExistsAsync(string driverId)
    {
        var existing = await db.Agents.FirstOrDefaultAsync(a => a.Id == driverId);
        if (existing != null) return existing;

        // A driver is a runtime, not a character: it must not inherit the persona's role, and the
        // drivers card already renders `@{id}` next to the name, so a `$"Driver {driverId}"` name
        // reads as "Driver agy" under "@agy".
        var name = driverId switch
        {
            "agy" => "Antigravity CLI",
            "copilot" => "GitHub Copilot CLI",
            "claude" => "Claude Code CLI",
            _ => driverId
        };

        var inferredAdapter = DriverAdapters.InferFromDriverId(driverId, runtimeType: RuntimeType.CliAgy) ?? DriverAdapters.Agy;
        var driver = new Agent
        {
            Id = driverId,
            Name = name,
            Role = AgentKind.DriverRole,
            Status = AgentMeshHub.IsAgentConnected(driverId) ? "online" : "offline",
            RuntimeType = RuntimeType.CliAgy,
            TrustTier = TrustTier.Intern,
            PersonaPrompt = $"Execution driver ({driverId})",
            CapabilitiesJson = JsonSerializer.Serialize(new { adapter = inferredAdapter }),
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Agents.Add(driver);
        await db.SaveChangesAsync();
        return driver;
    }

    private static string NormalizeSlug(string input)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9\-_]+", "-").Trim('-');
        return slug.Length > 64 ? slug[..64] : slug;
    }

    private static string? NormalizeOptional(string? val) =>
        string.IsNullOrWhiteSpace(val) ? null : val.Trim();

    public static PersonaDto ToDto(
        Persona p,
        Agent? driver,
        int sessionCount,
        ICredentialEncryptionService? encryption = null,
        int mcpServerCount = 0)
    {
        var hasToken = !string.IsNullOrWhiteSpace(p.GitHubTokenEncrypted);
        string? preview = null;
        if (hasToken && encryption != null)
        {
            var decrypted = encryption.Decrypt(p.GitHubTokenEncrypted!);
            if (!string.IsNullOrWhiteSpace(decrypted))
            {
                preview = encryption.Mask(decrypted);
            }
        }

        return new(
            p.Id,
            p.Name,
            p.Role,
            p.PersonaPrompt,
            p.DriverId,
            driver?.Name ?? p.DriverId,
            driver?.Status ?? (AgentMeshHub.IsAgentConnected(p.DriverId) ? "online" : "offline"),
            p.DefaultModel,
            p.DefaultEffort,
            p.DefaultMode,
            p.DefaultTier,
            (int)p.TrustTier,
            sessionCount,
            p.CreatedAt,
            p.UpdatedAt,
            p.DefaultContextSize,
            p.MaxConcurrentJobs,
            HasGitHubToken: hasToken,
            GitHubTokenPreview: preview,
            GitAuthorName: p.GitAuthorName,
            GitAuthorEmail: p.GitAuthorEmail,
            McpServerCount: mcpServerCount
        );
    }
}

public record CreatePersonaRequest(
    string Name,
    string DriverId,
    string? Id = null,
    string? Role = null,
    string? PersonaPrompt = null,
    string? DefaultModel = null,
    string? DefaultEffort = null,
    string? DefaultMode = null,
    string? DefaultTier = null,
    TrustTier TrustTier = TrustTier.Intern,
    string? DefaultContextSize = null,
    int? MaxConcurrentJobs = null,
    string? GitHubToken = null,
    string? GitAuthorName = null,
    string? GitAuthorEmail = null
);

public record UpdatePersonaRequest(
    string? Name = null,
    string? Role = null,
    string? PersonaPrompt = null,
    string? DriverId = null,
    string? DefaultModel = null,
    string? DefaultEffort = null,
    string? DefaultMode = null,
    string? DefaultTier = null,
    TrustTier? TrustTier = null,
    string? DefaultContextSize = null,
    int? MaxConcurrentJobs = null,
    string? GitHubToken = null,
    string? GitAuthorName = null,
    string? GitAuthorEmail = null
);

public record PersonaDto(
    string Id,
    string Name,
    string Role,
    string PersonaPrompt,
    string DriverId,
    string DriverName,
    string DriverStatus,
    string? DefaultModel,
    string? DefaultEffort,
    string? DefaultMode,
    string? DefaultTier,
    int TrustTier,
    int SessionCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? DefaultContextSize = null,
    int? MaxConcurrentJobs = null,
    bool HasGitHubToken = false,
    string? GitHubTokenPreview = null,
    string? GitAuthorName = null,
    string? GitAuthorEmail = null,
    int McpServerCount = 0
);

public record AssignPersonaMcpServerRequest(
    string McpServerId,
    bool Enabled = true,
    Dictionary<string, string>? EnvOverrides = null,
    Dictionary<string, string>? HeadersOverrides = null,
    List<string>? AllowedTools = null,
    List<string>? BlockedTools = null
);

public record UpsertPersonaMcpServerRequest(
    bool? Enabled = null,
    Dictionary<string, string>? EnvOverrides = null,
    Dictionary<string, string>? HeadersOverrides = null,
    bool MergeEnvOverrides = false,
    bool MergeHeadersOverrides = false,
    List<string>? RemoveEnvOverrideKeys = null,
    List<string>? RemoveHeaderOverrideKeys = null,
    List<string>? AllowedTools = null,
    List<string>? BlockedTools = null,
    bool ClearAllowedTools = false,
    bool ClearBlockedTools = false
);

public record PersonaMcpServerDto(
    string PersonaId,
    string McpServerId,
    string McpServerName,
    string McpServerDescription,
    McpTransportType Transport,
    string? Command,
    List<string> Args,
    string? Url,
    bool ServerEnabled,
    bool Enabled,
    bool EffectiveEnabled,
    int TimeoutSeconds,
    Dictionary<string, string> ServerEnvMasked,
    Dictionary<string, string> ServerHeadersMasked,
    Dictionary<string, string> EnvOverridesMasked,
    Dictionary<string, string> HeadersOverridesMasked,
    bool HasEnvOverrides,
    bool HasHeadersOverrides,
    List<string>? AllowedTools,
    List<string>? BlockedTools,
    DateTimeOffset AssignedAt,
    DateTimeOffset UpdatedAt
);
