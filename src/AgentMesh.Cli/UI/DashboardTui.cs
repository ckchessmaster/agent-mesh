using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using Spectre.Console;

namespace AgentMesh.Cli.UI;

public class DashboardTui
{
    private readonly CliConfig _config;
    private readonly MeshClient _meshClient;
    private readonly AgentProcessManager _manager;

    public DashboardTui(CliConfig config, MeshClient meshClient, AgentProcessManager manager)
    {
        _config = config;
        _meshClient = meshClient;
        _manager = manager;
    }

    public async Task RunAsync(IEnumerable<string>? initialAgentsToStart = null, CancellationToken ct = default)
    {
        AnsiConsole.Clear();

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

        var isHealthy = await _meshClient.CheckHealthAsync(ct);
        var lastHealthCheck = DateTimeOffset.UtcNow;
        var exitRequested = false;

        while (!exitRequested && !ct.IsCancellationRequested)
        {
            if ((DateTimeOffset.UtcNow - lastHealthCheck).TotalSeconds > 5)
            {
                isHealthy = await _meshClient.CheckHealthAsync(ct);
                lastHealthCheck = DateTimeOffset.UtcNow;
            }

            RenderScreen(isHealthy);

            // Wait up to 600ms for a keypress before refreshing the TUI
            var waitStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - waitStart).TotalMilliseconds < 600 && !ct.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    exitRequested = await HandleKeyPressAsync(key, ct);
                    break;
                }
                await Task.Delay(50, ct);
            }
        }

        AnsiConsole.MarkupLine("\n[yellow]Disconnecting agents from Mesh...[/]");
        await _manager.StopAllAsync();
        AnsiConsole.MarkupLine("[green]All agents disconnected cleanly.[/]");
    }

    private void RenderScreen(bool meshHealthy)
    {
        Console.SetCursorPosition(0, 0);

        var healthBadge = meshHealthy
            ? "[bold black on green] MESH ONLINE [/]"
            : "[bold white on red] MESH UNREACHABLE [/]";

        var header = new Panel(
            new Markup($"[bold skyblue1]AgentMesh Local Multi-Agent Runner[/]   {healthBadge}   [grey]Target:[/] [underline]{Markup.Escape(_config.MeshUrl)}[/]"))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.SkyBlue1)
        };

        var states = _manager.GetAllAgentStates();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Expand()
            .AddColumn(new TableColumn("[grey]#[/]").Centered())
            .AddColumn("[bold]Agent ID[/]")
            .AddColumn("[bold]Display Name[/]")
            .AddColumn("[bold]Adapter[/]")
            .AddColumn("[bold]Role[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn(new TableColumn("[bold]Prompts[/]").RightAligned())
            .AddColumn("[bold]Last Activity[/]");

        for (var i = 0; i < states.Count; i++)
        {
            var s = states[i];
            var statusMarkup = s.State switch
            {
                RunnerConnectionState.Online => "[bold green]● ONLINE[/]",
                RunnerConnectionState.Busy => "[bold yellow]◐ BUSY (THINKING)[/]",
                RunnerConnectionState.Connecting => "[bold cyan]◌ CONNECTING[/]",
                RunnerConnectionState.Error => "[bold red]✖ ERROR[/]",
                _ => "[grey]○ OFFLINE[/]"
            };

            var lastAct = s.LastActivityAt.HasValue
                ? s.LastActivityAt.Value.ToLocalTime().ToString("HH:mm:ss")
                : "-";

            table.AddRow(
                $"[skyblue1]{i + 1}[/]",
                $"[bold white]@{Markup.Escape(s.Id)}[/]",
                Markup.Escape(s.Name),
                $"[cyan]{Markup.Escape(s.AdapterType)}[/]",
                $"[grey]{Markup.Escape(s.Role)}[/]",
                statusMarkup,
                s.PromptsHandled.ToString(),
                $"[grey]{lastAct}[/]"
            );
        }

        var logs = _manager.GetRecentLogs(12);
        var logLines = logs.Count == 0
            ? "[grey italic]No agent activity yet. Press [bold white]1-9[/] to start an agent or [bold white]A[/] to start all.[/]"
            : string.Join("\n", logs.Select(l =>
                $"[grey]{l.Timestamp:HH:mm:ss}[/] [bold skyblue1]@{Markup.Escape(l.AgentId),-10}[/] {Markup.Escape(l.Message)}"));

        var logPanel = new Panel(new Markup(logLines))
        {
            Header = new PanelHeader(" [bold]Live Trajectory & Hub Events[/] "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey35),
            Expand = true
        };

        var controls = new Markup(
            " [bold skyblue1][[1-9]][/] Toggle Agent   " +
            "[bold green][[A]][/] Start All   " +
            "[bold yellow][[S]][/] Stop All   " +
            "[bold cyan][[N]][/] New Agent   " +
            "[bold red][[Q]][/] Quit & Disconnect");

        AnsiConsole.Write(header);
        AnsiConsole.Write(table);
        AnsiConsole.Write(logPanel);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(controls);
        AnsiConsole.WriteLine();
    }

    private async Task<bool> HandleKeyPressAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            return true;
        }

        var states = _manager.GetAllAgentStates();

        if (key.Key >= ConsoleKey.D1 && key.Key <= ConsoleKey.D9)
        {
            var idx = key.Key - ConsoleKey.D1;
            if (idx < states.Count)
            {
                var target = states[idx];
                if (_manager.IsRunning(target.Id))
                {
                    await _manager.StopAgentAsync(target.Id);
                }
                else
                {
                    try { await _manager.StartAgentAsync(target.Id, ct: ct); } catch { }
                }
                AnsiConsole.Clear();
            }
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.A:
                foreach (var s in states)
                {
                    if (!_manager.IsRunning(s.Id))
                    {
                        try { await _manager.StartAgentAsync(s.Id, ct: ct); } catch { }
                    }
                }
                AnsiConsole.Clear();
                break;

            case ConsoleKey.S:
                await _manager.StopAllAsync();
                AnsiConsole.Clear();
                break;

            case ConsoleKey.N:
                AnsiConsole.Clear();
                var newId = AnsiConsole.Ask<string>("Enter new [bold skyblue1]Agent ID[/] (e.g. [green]test[/], [green]agy-dev[/], [green]claude-review[/]):");
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
                AnsiConsole.Clear();
                break;
        }

        return false;
    }
}
