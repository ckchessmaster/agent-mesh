using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using Spectre.Console;

namespace AgentMesh.Cli.UI;

public class DashboardTui
{
    private readonly CliConfig _config;
    private readonly ConfigStore _configStore;
    private readonly MeshClient _meshClient;
    private readonly AgentProcessManager _manager;
    private int _selectedIndex;
    private int _needsRedraw = 1;
    private readonly object _renderLock = new();

    public DashboardTui(CliConfig config, ConfigStore configStore, MeshClient meshClient, AgentProcessManager manager)
    {
        _config = config;
        _configStore = configStore;
        _meshClient = meshClient;
        _manager = manager;

        _manager.OnStateChanged += () => Interlocked.Exchange(ref _needsRedraw, 1);
    }

    public async Task RunAsync(IEnumerable<string>? initialAgentsToStart = null, CancellationToken ct = default)
    {
        var isHealthy = await _meshClient.CheckHealthAsync(ct);

        // Sync any agents registered in the Web Cockpit into our local preset list
        if (isHealthy)
        {
            await SyncRemoteAgentsAsync(ct);
        }

        if (initialAgentsToStart != null)
        {
            foreach (var id in initialAgentsToStart)
            {
                try
                {
                    await _manager.StartAgentAsync(id, ct: ct);
                }
                catch
                {
                    // Logged inside manager
                }
            }
        }

        Interlocked.Exchange(ref _needsRedraw, 1);
        var exitRequested = false;

        while (!exitRequested && !ct.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref _needsRedraw, 0) == 1)
            {
                RenderScreen(isHealthy);
            }

            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                exitRequested = await HandleKeyPressAsync(key, ct);
                if (!exitRequested)
                {
                    isHealthy = await _meshClient.CheckHealthAsync(ct);
                    Interlocked.Exchange(ref _needsRedraw, 1);
                }
            }
            else
            {
                await Task.Delay(50, ct);
            }
        }

        AnsiConsole.MarkupLine("\n[yellow]Disconnecting running agents from Mesh...[/]");
        await _manager.StopAllAsync();
        AnsiConsole.MarkupLine("[green]All agents disconnected cleanly.[/]");
    }

    private async Task SyncRemoteAgentsAsync(CancellationToken ct)
    {
        try
        {
            var remoteAgents = await _meshClient.GetMeshAgentsAsync(ct);
            var changed = false;
            foreach (var remote in remoteAgents)
            {
                if (!_config.Agents.TryGetValue(remote.Id, out var existing))
                {
                    var preset = _configStore.ResolveOrCreatePreset(_config, remote.Id, customRole: remote.Role);
                    _configStore.PopulateFromRemote(preset, remote);
                    changed = true;
                }
                else
                {
                    _configStore.PopulateFromRemote(existing, remote);
                    changed = true;
                }
            }
            if (changed)
            {
                _configStore.Save(_config);
            }
        }
        catch
        {
            // Ignore if offline
        }
    }

    private void RenderScreen(bool meshHealthy)
    {
        lock (_renderLock)
        {
            AnsiConsole.Clear();

            var healthBadge = meshHealthy
                ? "[bold black on green] MESH ONLINE [/]"
                : "[bold white on red] MESH OFFLINE [/]";

            // Format endpoint without "http://localhost:PORT" so VS Code terminal never spams port-forward popups
            var displayTarget = FormatSafeEndpoint(_config.MeshUrl);

            AnsiConsole.MarkupLine($"[bold skyblue1]AgentMesh Multi-Agent CLI[/]  {healthBadge}  [grey]Endpoint:[/] [white]{Markup.Escape(displayTarget)}[/]");
            AnsiConsole.WriteLine();

            var states = _manager.GetAllAgentStates();
            if (_selectedIndex >= states.Count) _selectedIndex = Math.Max(0, states.Count - 1);

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey35)
                .AddColumn(new TableColumn("[grey]#[/]").Centered())
                .AddColumn("[bold]Agent ID[/]")
                .AddColumn("[bold]Name[/]")
                .AddColumn("[bold]Adapter[/]")
                .AddColumn("[bold]Status[/]")
                .AddColumn(new TableColumn("[bold]Prompts[/]").RightAligned())
                .AddColumn(new TableColumn("[bold]Tokens[/]").RightAligned())
                .AddColumn("[bold]Last Activity / Info[/]");

            for (var i = 0; i < states.Count; i++)
            {
                var s = states[i];
                var isSelected = i == _selectedIndex;
                var selector = isSelected ? $"[bold skyblue1]> {i + 1}[/]" : $"[grey]  {i + 1}[/]";

                var statusMarkup = s.State switch
                {
                    RunnerConnectionState.Online => "[bold green]● ONLINE[/]",
                    RunnerConnectionState.Busy => "[bold yellow]◐ BUSY[/]",
                    RunnerConnectionState.Connecting => "[bold cyan]◌ STARTING[/]",
                    RunnerConnectionState.Error => "[bold red]✖ ERROR[/]",
                    _ => "[grey]○ OFFLINE[/]"
                };

                var tokenDisplay = s.TotalTokensUsed > 0
                    ? $"[bold skyblue1]{s.TotalTokensUsed:N0}[/]"
                    : "[grey]-[/]";

                var info = !string.IsNullOrWhiteSpace(s.LastError)
                    ? $"[red]{Markup.Escape(s.LastError)}[/]"
                    : s.LastActivityAt.HasValue
                        ? $"[grey]{s.LastActivityAt.Value.ToLocalTime():HH:mm:ss}[/]"
                        : $"[dim]{Markup.Escape(s.Role)}[/]";

                // A persona runner registers against its driver, but the operator cares which
                // character it is running, so label the row with the persona and note the driver.
                var runnerId = string.IsNullOrWhiteSpace(s.PersonaId) ? s.Id : s.PersonaId;
                var runnerName = string.IsNullOrWhiteSpace(s.PersonaName)
                    ? s.Name
                    : $"{s.PersonaName} (on @{s.Id})";

                table.AddRow(
                    selector,
                    isSelected ? $"[bold skyblue1]@{Markup.Escape(runnerId)}[/]" : $"[white]@{Markup.Escape(runnerId)}[/]",
                    Markup.Escape(runnerName),
                    $"[cyan]{Markup.Escape(s.AdapterType)}[/]",
                    statusMarkup,
                    s.PromptsHandled.ToString(),
                    tokenDisplay,
                    info
                );
            }

            AnsiConsole.Write(table);

            var logs = _manager.GetRecentLogs(6);
            var logLines = logs.Count == 0
                ? "[grey italic]Press [bold white]Enter/Space[/] or [bold white]1-9[/] to connect an agent to the Mesh.[/]"
                : string.Join("\n", logs.Select(l =>
                    $"[grey]{l.Timestamp:HH:mm:ss}[/] [bold skyblue1]@{Markup.Escape(l.AgentId),-10}[/] {Markup.Escape(l.Message)}"));

            var logPanel = new Panel(new Markup(logLines))
            {
                Header = new PanelHeader(" [bold]Activity Log[/] "),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Grey35)
            };

            AnsiConsole.Write(logPanel);
            AnsiConsole.MarkupLine(
                " [bold skyblue1][[↑/↓]][/] Select  " +
                "[bold green][[Enter/Space]][/] Toggle Selected  " +
                "[bold skyblue1][[1-9]][/] Toggle #  " +
                "[bold green][[A]][/] Start All  " +
                "[bold yellow][[S]][/] Stop All  " +
                "[bold cyan][[N]][/] New Agent  " +
                "[bold red][[Q]][/] Quit");
        }
    }

    private async Task<bool> HandleKeyPressAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            return true;
        }

        var states = _manager.GetAllAgentStates();

        if (key.Key == ConsoleKey.UpArrow)
        {
            if (states.Count > 0)
            {
                _selectedIndex = (_selectedIndex - 1 + states.Count) % states.Count;
            }
            return false;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            if (states.Count > 0)
            {
                _selectedIndex = (_selectedIndex + 1) % states.Count;
            }
            return false;
        }

        if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
        {
            if (_selectedIndex >= 0 && _selectedIndex < states.Count)
            {
                await ToggleAgentAsync(states[_selectedIndex].Id, ct);
            }
            return false;
        }

        if (key.KeyChar >= '1' && key.KeyChar <= '9')
        {
            var idx = key.KeyChar - '1';
            if (idx < states.Count)
            {
                _selectedIndex = idx;
                await ToggleAgentAsync(states[idx].Id, ct);
            }
            return false;
        }

        switch (char.ToUpperInvariant(key.KeyChar))
        {
            case 'A':
                foreach (var s in states)
                {
                    if (!_manager.IsRunning(s.Id))
                    {
                        try { await _manager.StartAgentAsync(s.Id, ct: ct); } catch { }
                    }
                }
                break;

            case 'S':
                await _manager.StopAllAsync();
                break;

            case 'R':
                await SyncRemoteAgentsAsync(ct);
                break;

            case 'N':
                AnsiConsole.WriteLine();
                var newId = AnsiConsole.Ask<string>("Enter new [bold skyblue1]Agent ID[/] (e.g. [green]test[/], [green]agy-dev[/]):");
                if (!string.IsNullOrWhiteSpace(newId))
                {
                    try
                    {
                        await _manager.StartAgentAsync(newId.Trim(), ct: ct);
                    }
                    catch
                    {
                        // Logged in manager
                    }
                }
                break;
        }

        return false;
    }

    private async Task ToggleAgentAsync(string agentId, CancellationToken ct)
    {
        if (_manager.IsRunning(agentId))
        {
            await _manager.StopAgentAsync(agentId);
        }
        else
        {
            try
            {
                await _manager.StartAgentAsync(agentId, ct: ct);
            }
            catch
            {
                // Error is stored on runner state and logged in Activity Log
            }
        }
    }

    private static string FormatSafeEndpoint(string rawUrl)
    {
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return $"{uri.Host} (port {uri.Port})";
        }
        return rawUrl.Replace("://", " ").Replace(":", " port ");
    }
}
