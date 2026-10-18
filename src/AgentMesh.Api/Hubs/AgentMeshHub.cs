using System.Collections.Concurrent;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Hubs;

public class AgentMeshHub(AgentMeshDbContext? db = null) : Hub
{
    private static readonly ConcurrentDictionary<string, string> ConnectionAgents = new();

    public static bool IsAgentConnected(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return false;
        var normalized = agentId.Trim().ToLowerInvariant();
        return ConnectionAgents.Values.Any(v => v == normalized);
    }

    public static IReadOnlyCollection<string> GetConnectedAgentIds() =>
        ConnectionAgents.Values.Distinct().ToList();

    public static void ClearConnections() => ConnectionAgents.Clear();

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session-{sessionId}");
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session-{sessionId}");
    }

    public async Task RegisterAgentRunner(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;

        var normalizedId = agentId.Trim().ToLowerInvariant();
        ConnectionAgents[Context.ConnectionId] = normalizedId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"agent-{normalizedId}");

        if (db != null)
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
            if (agent != null)
            {
                agent.Status = "online";
                agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
                agent.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();

                var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == normalizedId);
                await Clients.All.SendAsync("AgentUpdated", new
                {
                    id = agent.Id,
                    name = agent.Name,
                    runtimeType = agent.RuntimeType.ToString(),
                    trustTier = (int)agent.TrustTier,
                    personaPrompt = agent.PersonaPrompt,
                    createdAt = agent.CreatedAt,
                    role = agent.Role,
                    status = agent.Status,
                    capabilitiesJson = agent.CapabilitiesJson,
                    lastHeartbeatAt = agent.LastHeartbeatAt,
                    sessionCount
                });
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (ConnectionAgents.TryRemove(Context.ConnectionId, out var agentId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"agent-{agentId}");

            var stillConnected = ConnectionAgents.Values.Any(v => v == agentId);
            if (!stillConnected && db != null)
            {
                var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
                if (agent != null)
                {
                    agent.Status = "offline";
                    agent.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();

                    var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == agentId);
                    await Clients.All.SendAsync("AgentUpdated", new
                    {
                        id = agent.Id,
                        name = agent.Name,
                        runtimeType = agent.RuntimeType.ToString(),
                        trustTier = (int)agent.TrustTier,
                        personaPrompt = agent.PersonaPrompt,
                        createdAt = agent.CreatedAt,
                        role = agent.Role,
                        status = agent.Status,
                        capabilitiesJson = agent.CapabilitiesJson,
                        lastHeartbeatAt = agent.LastHeartbeatAt,
                        sessionCount
                    });
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
