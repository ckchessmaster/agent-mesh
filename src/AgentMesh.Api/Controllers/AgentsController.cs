using System.Security.Claims;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AgentsController(AgentMeshDbContext db, IApiKeyService apiKeyService) : ControllerBase
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
            RuntimeType = req.RuntimeType,
            TrustTier = req.TrustTier,
            PersonaPrompt = req.PersonaPrompt ?? $"Autonomous AI Agent ({normalizedId})",
            MemorySummary = req.MemorySummary ?? string.Empty,
            ApiKeyHash = hashedToken,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAgent), new { id = agent.Id }, new
        {
            agent = new AgentDto(agent.Id, agent.Name, agent.RuntimeType.ToString(), (int)agent.TrustTier, agent.PersonaPrompt, agent.CreatedAt),
            token = rawToken,
            instructions = "Include 'Authorization: Bearer <token>' in your HTTP and MCP requests."
        });
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

        return Ok(new AgentDto(agent.Id, agent.Name, agent.RuntimeType.ToString(), (int)agent.TrustTier, agent.PersonaPrompt, agent.CreatedAt));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgents()
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new AgentDto(a.Id, a.Name, a.RuntimeType.ToString(), (int)a.TrustTier, a.PersonaPrompt, a.CreatedAt))
            .ToListAsync();

        return Ok(agents);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgent(string id)
    {
        var agent = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id.ToLowerInvariant());

        return agent == null
            ? NotFound(new { error = $"Agent '{id}' not found." })
            : Ok(new AgentDto(agent.Id, agent.Name, agent.RuntimeType.ToString(), (int)agent.TrustTier, agent.PersonaPrompt, agent.CreatedAt));
    }
}

public record RegisterAgentRequest(
    string Id,
    string Name,
    RuntimeType RuntimeType = RuntimeType.CliAgy,
    TrustTier TrustTier = TrustTier.Intern,
    string? PersonaPrompt = null,
    string? MemorySummary = null
);

public record AgentDto(
    string Id,
    string Name,
    string RuntimeType,
    int TrustTier,
    string PersonaPrompt,
    DateTimeOffset CreatedAt
);
