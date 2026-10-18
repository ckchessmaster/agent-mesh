using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
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
public class DevController(AgentMeshDbContext db, IHubContext<AgentMeshHub> hubContext) : ControllerBase
{
    [HttpPost("reset-mesh")]
    public async Task<IActionResult> ResetMesh()
    {
        var messages = await db.SessionMessages.ToListAsync();
        db.SessionMessages.RemoveRange(messages);

        var jobs = await db.AgentJobs.ToListAsync();
        db.AgentJobs.RemoveRange(jobs);

        var sessions = await db.AgentSessions.ToListAsync();
        db.AgentSessions.RemoveRange(sessions);

        var agents = await db.Agents.ToListAsync();
        db.Agents.RemoveRange(agents);

        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("MeshReset", new { timestamp = DateTimeOffset.UtcNow });

        return Ok(new
        {
            status = "reset",
            message = "All agents, sessions, and events have been purged.",
            timestamp = DateTimeOffset.UtcNow
        });
    }

    [HttpPost("seed-demo")]
    public async Task<IActionResult> SeedDemo()
    {
        var now = DateTimeOffset.UtcNow;

        var agy = await db.Agents.FirstOrDefaultAsync(a => a.Id == "agy-core");
        if (agy == null)
        {
            agy = new Agent
            {
                Id = "agy-core",
                Name = "Agy Lead Architect",
                Role = "Codebase Engineer",
                Status = "online",
                RuntimeType = RuntimeType.CliAgy,
                TrustTier = TrustTier.LeadHuman,
                PersonaPrompt = "Lead autonomous coding assistant for AgentMesh.",
                CapabilitiesJson = JsonSerializer.Serialize(new[] { "view_file", "replace_file_content", "run_command", "search_workspace" }),
                LastHeartbeatAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Agents.Add(agy);
        }
        else
        {
            agy.Status = "online";
            agy.LastHeartbeatAt = now;
            agy.UpdatedAt = now;
        }

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Fix OIDC Token Exchange & CORS Handshake",
            Status = "active",
            CreatorAgentId = "agy-core",
            MetadataJson = "{\"branch\":\"rebuild/basic-skeleton\"}",
            CreatedAt = now.AddMinutes(-3),
            UpdatedAt = now
        };
        db.AgentSessions.Add(session);

        var events = new List<SessionMessage>
        {
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "operator",
                TargetAgentId = "agy-core",
                EventType = "prompt",
                Content = "Investigate why the OIDC login callback is getting stuck verifying tokens against Dex.",
                CreatedAt = now.AddSeconds(-40)
            },
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "agy-core",
                EventType = "thought",
                Content = "The browser received an authorization code from Dex at /auth/callback, which confirms the redirect URI and CORS discovery succeeded. Let me check the Dex container logs and staticClients configuration to see why the POST /dex/token request failed.",
                CreatedAt = now.AddSeconds(-35)
            },
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "agy-core",
                EventType = "tool_call",
                Content = "Reading Dex configuration file",
                ToolName = "view_file",
                ToolArgsJson = JsonSerializer.Serialize(new { AbsolutePath = "src/AgentMesh.AppHost/dex/config.yaml" }, new JsonSerializerOptions { WriteIndented = true }),
                CreatedAt = now.AddSeconds(-30)
            },
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "agy-core",
                EventType = "tool_result",
                Content = "Read 32 lines from src/AgentMesh.AppHost/dex/config.yaml",
                ToolName = "view_file",
                ToolResultJson = "staticClients:\n  - id: agentmesh-dashboard\n    name: 'AgentMesh Dashboard'\n    secret: agentmesh-local-secret\n    public: true",
                CreatedAt = now.AddSeconds(-25)
            },
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "agy-core",
                EventType = "thought",
                Content = "Root cause identified: In Dex, defining a 'secret' on a staticClient forces Dex to treat it as a confidential client, even when 'public: true' is set. Removing 'secret' allows pure PKCE token exchange from the browser.",
                CreatedAt = now.AddSeconds(-18)
            },
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "agy-core",
                EventType = "tool_call",
                Content = "Removing client secret from public PKCE client in Dex config",
                ToolName = "replace_file_content",
                ToolArgsJson = JsonSerializer.Serialize(new { TargetFile = "src/AgentMesh.AppHost/dex/config.yaml", Instruction = "Remove secret from agentmesh-dashboard client" }, new JsonSerializerOptions { WriteIndented = true }),
                CreatedAt = now.AddSeconds(-12)
            },
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "agy-core",
                EventType = "tool_result",
                Content = "Successfully updated src/AgentMesh.AppHost/dex/config.yaml and verified tests.",
                ToolName = "replace_file_content",
                ToolResultJson = "Passed! - Failed: 0, Passed: 45, Skipped: 0",
                CreatedAt = now.AddSeconds(-6)
            },
            new()
            {
                SessionId = session.Id,
                SenderAgentId = "agy-core",
                EventType = "response",
                Content = "I've resolved the OIDC token handshake issue:\n\n1. Removed `secret` from the `agentmesh-dashboard` public client in `dex/config.yaml` so PKCE requests succeed without requiring a confidential secret.\n2. Updated `AuthGate` to prioritize error states over the loading spinner.",
                CreatedAt = now
            }
        };

        db.SessionMessages.AddRange(events);
        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("MeshSeeded", new { agentId = agy.Id, sessionId = session.Id });

        return Ok(new
        {
            status = "seeded",
            agentId = agy.Id,
            sessionId = session.Id,
            eventCount = events.Count
        });
    }
}
