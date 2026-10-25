using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AgentMesh.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace AgentMesh.Api.Hubs;

public class TerminalHub(
    IPersonaContainerManager? containerManager = null,
    ILogger<TerminalHub>? logger = null) : Hub
{
    private static readonly ConcurrentDictionary<string, TerminalSession> Sessions = new();

    public async Task Attach(string personaId, int cols = 80, int rows = 24)
    {
        if (string.IsNullOrWhiteSpace(personaId))
        {
            await Clients.Caller.SendAsync("Output", "\r\n\x1b[31mError: Persona ID is required.\x1b[0m\r\n");
            return;
        }

        var normPersonaId = personaId.Trim().ToLowerInvariant();

        // Close any prior session on this connection
        if (Sessions.TryRemove(Context.ConnectionId, out var oldSession))
        {
            await oldSession.DisposeAsync();
        }

        if (containerManager != null)
        {
            try
            {
                await containerManager.EnsureRunningAsync(normPersonaId);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("Output", $"\r\n\x1b[31mFailed to start persona environment: {ex.Message}\x1b[0m\r\n");
                return;
            }
        }

        var containerName = $"agentmesh-persona-{normPersonaId}";
        var cli = ResolveCliBinary();

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            ArgumentList = { "exec", "-i", "-e", $"COLUMNS={cols}", "-e", $"LINES={rows}", "-e", "TERM=xterm-256color", containerName, "/bin/bash" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.Start();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to launch terminal process for container {Container}", containerName);
            await Clients.Caller.SendAsync("Output", $"\r\n\x1b[31mFailed to attach terminal to container: {ex.Message}\x1b[0m\r\n");
            return;
        }

        var session = new TerminalSession(proc, proc.StandardInput, containerName, cli);
        Sessions[Context.ConnectionId] = session;

        await Clients.Caller.SendAsync("Output", $"\x1b[36mConnected to dedicated persona terminal: @{normPersonaId}\x1b[0m\r\n\r\n");

        _ = Task.Run(async () =>
        {
            var buffer = new char[1024];
            try
            {
                while (!proc.HasExited)
                {
                    var read = await proc.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    var text = new string(buffer, 0, read);
                    await Clients.Client(Context.ConnectionId).SendAsync("Output", text);
                }
            }
            catch { }
            finally
            {
                await Clients.Client(Context.ConnectionId).SendAsync("Output", "\r\n\x1b[33mTerminal session closed.\x1b[0m\r\n");
                await Clients.Client(Context.ConnectionId).SendAsync("Closed", proc.ExitCode);
            }
        });

        _ = Task.Run(async () =>
        {
            var errBuffer = new char[1024];
            try
            {
                while (!proc.HasExited)
                {
                    var read = await proc.StandardError.ReadAsync(errBuffer, 0, errBuffer.Length);
                    if (read <= 0) break;
                    var text = new string(errBuffer, 0, read);
                    await Clients.Client(Context.ConnectionId).SendAsync("Output", text);
                }
            }
            catch { }
        });
    }

    public async Task Input(string data)
    {
        if (Sessions.TryGetValue(Context.ConnectionId, out var session))
        {
            try
            {
                await session.Writer.WriteAsync(data);
                await session.Writer.FlushAsync();
            }
            catch { }
        }
    }

    public async Task Resize(int cols, int rows)
    {
        if (Sessions.TryGetValue(Context.ConnectionId, out var session))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = session.CliBinary,
                    ArgumentList = { "exec", session.ContainerName, "stty", "cols", cols.ToString(), "rows", rows.ToString() },
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                if (p != null) await p.WaitForExitAsync();
            }
            catch { }
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

    private static string ResolveCliBinary()
    {
        var candidates = new[] { "podman", "docker" };
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var c in candidates)
        {
            foreach (var dir in paths)
            {
                var full = Path.Combine(dir, c);
                if (File.Exists(full)) return c;
            }
        }

        return "docker";
    }

    private sealed class TerminalSession(Process process, StreamWriter writer, string containerName, string cliBinary) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public StreamWriter Writer { get; } = writer;
        public string ContainerName { get; } = containerName;
        public string CliBinary { get; } = cliBinary;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(true);
                }
            }
            catch { }
            try { Writer.Dispose(); } catch { }
            try { Process.Dispose(); } catch { }
            await Task.CompletedTask;
        }
    }
}
