using System.Security.Claims;
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
            if (!string.IsNullOrWhiteSpace(req.CapabilitiesJson)) existing.CapabilitiesJson = req.CapabilitiesJson;
            existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == normalizedId);
            var updatedDto = ToDto(existing, sessionCount);

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

        var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == agent.Id);
        return Ok(ToDto(agent, sessionCount));
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
            .Where(s => s.CreatorAgentId != null)
            .GroupBy(s => s.CreatorAgentId!)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count);

        var dtos = agents
            .Select(a => ToDto(a, sessionCounts.GetValueOrDefault(a.Id, 0)))
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

        var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == normalizedId);
        return Ok(ToDto(agent, sessionCount));
    }

    [HttpGet("{id}/sessions")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgentSessions(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var sessions = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.CreatorAgentId == normalizedId)
            .OrderByDescending(s => s.UpdatedAt)
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

        var hasInitialPrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt);
        var initialMetadata = SessionsController.MergeSessionMetadata(req.MetadataJson, req.WorkingDirectory, req.Model, req.Effort, req.Mode);
        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = hasInitialPrompt ? "thinking" : "active",
            MetadataJson = initialMetadata,
            CreatorAgentId = normalizedId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        SessionMessage? promptMsg = null;
        if (hasInitialPrompt)
        {
            promptMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "operator",
                TargetAgentId = normalizedId,
                EventType = "prompt",
                Content = req.InitialPrompt!.Trim(),
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
                session.CreatorAgentId,
                hasInitialPrompt ? 1 : 0,
                session.CreatedAt,
                session.UpdatedAt
            ));

            if (promptMsg != null)
            {
                var (workDir, model, effort, mode) = SessionsController.ExtractSessionSettings(session.MetadataJson);
                await hubContext.Clients.Group($"agent-{normalizedId}").SendAsync("PromptReceived", new
                {
                    sessionId = session.Id,
                    messageId = promptMsg.Id,
                    agentId = normalizedId,
                    prompt = promptMsg.Content,
                    sender = promptMsg.SenderAgentId,
                    workingDirectory = workDir,
                    model = model,
                    effort = effort,
                    mode = mode
                });
            }
        }

        return CreatedAtAction("GetSession", "Sessions", new { id = session.Id }, session);
    }

    private static AgentDto ToDto(Agent a, int sessionCount) => new(
        a.Id,
        a.Name,
        a.RuntimeType.ToString(),
        (int)a.TrustTier,
        a.PersonaPrompt,
        a.CreatedAt,
        a.Role,
        a.Status,
        a.CapabilitiesJson,
        a.LastHeartbeatAt,
        sessionCount
    );
}

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

public record CreateAgentSessionRequest(
    string Title,
    string? InitialPrompt = null,
    string? MetadataJson = null,
    string? WorkingDirectory = null,
    string? Model = null,
    string? Effort = null,
    string? Mode = null
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
    int SessionCount = 0
);
