using System.Collections.Concurrent;
using AgentMesh.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace AgentMesh.Api.Hubs;

public class TerminalHub(
    IPersonaContainerManager? containerManager = null,
    ILogger<TerminalHub>? logger = null) : Hub
{
    private static readonly ConcurrentDictionary<string, IPersonaTerminalSession> Sessions = new();

    public async Task Attach(string personaId, int cols = 80, int rows = 24)
    {
        if (string.IsNullOrWhiteSpace(personaId))
        {
            await Clients.Caller.SendAsync("Output", "\r\n\x1b[31mError: Persona ID is required.\x1b[0m\r\n");
            return;
        }

        var normPersonaId = personaId.Trim().ToLowerInvariant();

        if (Sessions.TryRemove(Context.ConnectionId, out var oldSession))
        {
            await oldSession.DisposeAsync();
        }

        if (containerManager == null)
        {
            await Clients.Caller.SendAsync("Output", "\r\n\x1b[31mTerminal runtime is not configured.\x1b[0m\r\n");
            return;
        }

        try
        {
            var session = await containerManager.AttachTerminalAsync(
                normPersonaId,
                cols,
                rows,
                onOutput: text => Clients.Client(Context.ConnectionId).SendAsync("Output", text),
                onClosed: code => Clients.Client(Context.ConnectionId).SendAsync("Closed", code),
                ct: Context.ConnectionAborted);

            Sessions[Context.ConnectionId] = session;
            await Clients.Caller.SendAsync("Output", $"\x1b[36mConnected to dedicated persona terminal: @{normPersonaId}\x1b[0m\r\n\r\n");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to attach terminal for persona @{PersonaId}", normPersonaId);
            await Clients.Caller.SendAsync("Output", $"\r\n\x1b[31mFailed to attach terminal to container: {ex.Message}\x1b[0m\r\n");
        }
    }

    public async Task Input(string data)
    {
        if (Sessions.TryGetValue(Context.ConnectionId, out var session))
        {
            await session.SendInputAsync(data);
        }
    }

    public async Task Resize(int cols, int rows)
    {
        if (Sessions.TryGetValue(Context.ConnectionId, out var session))
        {
            await session.ResizeAsync(cols, rows);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Sessions.TryRemove(Context.ConnectionId, out var session))
        {
            await session.DisposeAsync();
        }
        await base.OnDisconnectedAsync(exception);
    }
}
