using System.Collections.Concurrent;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Hubs;

public class AgentMeshHub(AgentMeshDbContext? db = null) : Hub
{
    private static readonly ConcurrentDictionary<string, string> ConnectionAgents = new();
    private static readonly ConcurrentDictionary<string, string> ConnectionPersonas = new();

    public static bool IsAgentConnected(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return false;
        var normalized = agentId.Trim().ToLowerInvariant();
        return ConnectionAgents.Values.Any(v => v == normalized);
    }

    public static bool IsPersonaConnected(string personaId)
    {
        if (string.IsNullOrWhiteSpace(personaId)) return false;
        var normalized = personaId.Trim().ToLowerInvariant();
        return ConnectionPersonas.Values.Any(v => v == normalized);
    }

    public static IReadOnlyCollection<string> GetConnectedAgentIds() =>
        ConnectionAgents.Values.Distinct().ToList();

    public static IReadOnlyCollection<string> GetConnectedPersonaIds() =>
        ConnectionPersonas.Values.Distinct().ToList();

    public static void ClearConnections()
    {
        ConnectionAgents.Clear();
        ConnectionPersonas.Clear();
    }

    public static void UnregisterAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        var normalized = agentId.Trim().ToLowerInvariant();
        var keysToRemove = ConnectionAgents.Where(kvp => kvp.Value == normalized).Select(kvp => kvp.Key).ToList();
        foreach (var key in keysToRemove)
        {
            ConnectionAgents.TryRemove(key, out _);
        }
    }

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session-{sessionId}");
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session-{sessionId}");
    }

    public async Task ReportCommandOutput(string sessionId, string commandId, string output)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var payload = new
        {
            sessionId,
            commandId,
            output = output ?? string.Empty,
            timestamp = DateTimeOffset.UtcNow
        };
        await Clients.Group($"session-{sessionId}").SendAsync("CommandOutput", payload);
        await Clients.All.SendAsync("CommandOutput", payload);
    }

    public async Task RegisterPersonaRunner(string personaId, string? driverId = null)
    {
        if (string.IsNullOrWhiteSpace(personaId)) return;

        var normalizedPersonaId = personaId.Trim().ToLowerInvariant();
        ConnectionPersonas[Context.ConnectionId] = normalizedPersonaId;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"persona-{normalizedPersonaId}");

        if (!string.IsNullOrWhiteSpace(driverId))
        {
            await RegisterAgentRunner(driverId);
        }

        await Clients.All.SendAsync("PersonaRunnerStatusChanged", new
        {
            personaId = normalizedPersonaId,
            driverId = driverId?.Trim().ToLowerInvariant(),
            status = "online",
            connectionId = Context.ConnectionId,
            timestamp = DateTimeOffset.UtcNow
        });
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
        if (ConnectionPersonas.TryRemove(Context.ConnectionId, out var personaId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"persona-{personaId}");
            var stillConnected = ConnectionPersonas.Values.Any(v => v == personaId);
            if (!stillConnected)
            {
                await Clients.All.SendAsync("PersonaRunnerStatusChanged", new
                {
                    personaId,
                    status = "offline",
                    timestamp = DateTimeOffset.UtcNow
                });
            }
        }

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
