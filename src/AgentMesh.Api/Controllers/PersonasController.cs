using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
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

        var dtos = personas.Select(p =>
        {
            drivers.TryGetValue(p.DriverId, out var driver);
            return ToDto(p, driver, sessionCounts.GetValueOrDefault(p.Id, 0), encryptionService);
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
        return Ok(ToDto(persona, driver, sessionCount, encryptionService));
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

        var driver = await db.Agents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == persona.DriverId);
        var sessionCount = await db.AgentSessions.CountAsync(s => s.PersonaId == normalizedId);
        var dto = ToDto(persona, driver, sessionCount, encryptionService);

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
    public async Task<IActionResult> GetPersonaSessions(string id)
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

        var sessions = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.PersonaId == normalizedId)
            .OrderByDescending(s => s.UpdatedAt)
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

    [HttpGet("{id}/environment/logs")]
    public async Task<IActionResult> GetPersonaEnvironmentLogs(string id, [FromQuery] int tail = 100, CancellationToken ct = default)
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

        var logs = await containerManager.GetLogsAsync(normalizedId, Math.Clamp(tail, 10, 1000), ct);
        return Ok(new { logs });
    }

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

    public record SubmitAuthCodeRequest(string Code);

    [HttpPost("{id}/environment/auth/submit")]
    public async Task<IActionResult> SubmitPersonaAuthCode(string id, [FromBody] SubmitAuthCodeRequest req, CancellationToken ct)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(req.Code))
        {
            return BadRequest(new { error = "Authorization code is required." });
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

        var result = await containerManager.SubmitAuthCodeAsync(normalizedId, req.Code, ct);
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
                var payload = new
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
                    createdAt = promptMsg.CreatedAt
                };
                await hubContext.Clients.Group($"persona-{persona.Id.Trim().ToLowerInvariant()}").SendAsync("PromptReceived", payload);
                await hubContext.Clients.Group($"agent-{driverId}").SendAsync("PromptReceived", payload);
                await hubContext.Clients.All.SendAsync("PromptReceived", payload);
            }
        }

        return CreatedAtAction("GetSession", "Sessions", new { id = session.Id }, session);
    }

    private async Task<Agent> EnsureDriverExistsAsync(string driverId)
    {
        var existing = await db.Agents.FirstOrDefaultAsync(a => a.Id == driverId);
        if (existing != null) return existing;

        var (name, role) = driverId switch
        {
            "agy" => ("Antigravity CLI", "Autonomous Codebase Driver"),
            "copilot" => ("GitHub Copilot CLI", "Shell & Git Driver"),
            "claude" => ("Claude Code CLI", "Anthropic Terminal Driver"),
            _ => ($"Driver {driverId}", "Autonomous Driver")
        };

        var driver = new Agent
        {
            Id = driverId,
            Name = name,
            Role = role,
            Status = AgentMeshHub.IsAgentConnected(driverId) ? "online" : "offline",
            RuntimeType = RuntimeType.CliAgy,
            TrustTier = TrustTier.Intern,
            PersonaPrompt = $"Execution driver ({driverId})",
            CapabilitiesJson = "[]",
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

    public static PersonaDto ToDto(Persona p, Agent? driver, int sessionCount, ICredentialEncryptionService? encryption = null)
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
            GitAuthorEmail: p.GitAuthorEmail
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
    string? GitAuthorEmail = null
);
