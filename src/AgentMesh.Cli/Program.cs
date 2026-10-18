using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Cli.UI;
using Spectre.Console;

namespace AgentMesh.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var configStore = new ConfigStore();
        var config = configStore.Load();

        // Parse global options (--url, --token, --command, --role, --adapter, --name, --once)
        var positional = new List<string>();
        string? customCommand = null;
        string? customRole = null;
        string? customName = null;
        string? customAdapter = null;
        var runOnce = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--url" or "--mesh-url" && i + 1 < args.Length)
            {
                config.MeshUrl = args[++i].TrimEnd('/');
            }
            else if (arg is "--token" && i + 1 < args.Length)
            {
                config.MasterToken = args[++i].Trim();
            }
            else if (arg is "--command" or "-c" && i + 1 < args.Length)
            {
                customCommand = args[++i];
            }
            else if (arg is "--role" or "-r" && i + 1 < args.Length)
            {
                customRole = args[++i];
            }
            else if (arg is "--name" or "-n" && i + 1 < args.Length)
            {
                customName = args[++i];
            }
            else if (arg is "--adapter" or "-a" && i + 1 < args.Length)
            {
                customAdapter = args[++i];
            }
            else if (arg is "--once")
            {
                runOnce = true;
            }
            else
            {
                positional.Add(arg);
            }
        }

        var subcommand = positional.Count > 0 ? positional[0].ToLowerInvariant() : "tui";
        var subArgs = positional.Skip(1).ToList();

        if (subcommand is "help" or "--help" or "-h")
        {
            PrintHelp(configStore.ConfigFilePath);
            return 0;
        }

        var meshClient = new MeshClient(config, configStore);
        await using var manager = new AgentProcessManager(config, configStore, meshClient);

        switch (subcommand)
        {
            case "tui":
            {
                var tui = new DashboardTui(config, meshClient, manager);
                await tui.RunAsync(subArgs, cts.Token);
                return 0;
            }

            case "start" or "connect" or "run":
            {
                var targets = subArgs.Count > 0 ? subArgs : new List<string> { "agy" };
                if (targets.Contains("--all", StringComparer.OrdinalIgnoreCase))
                {
                    targets = config.Agents.Keys.ToList();
                }

                AnsiConsole.MarkupLine($"[bold skyblue1]AgentMesh CLI[/] connecting to [underline]{Markup.Escape(config.MeshUrl)}[/]...");

                manager.OnActivityLog += (agentId, msg) =>
                {
                    AnsiConsole.MarkupLine($"[grey]{DateTimeOffset.Now:HH:mm:ss}[/] [bold skyblue1]@{Markup.Escape(agentId),-10}[/] {Markup.Escape(msg)}");
                };

                var startedCount = 0;
                foreach (var targetId in targets)
                {
                    try
                    {
                        await manager.StartAgentAsync(targetId, customCommand, customRole, cts.Token);
                        startedCount++;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to start @{Markup.Escape(targetId)}:[/] {Markup.Escape(ex.Message)}");
                    }
                }

                if (startedCount == 0)
                {
                    AnsiConsole.MarkupLine("[red]No agents could be started. Verify the AgentMesh API is running at {0}.[/]", Markup.Escape(config.MeshUrl));
                    return 1;
                }

                if (runOnce)
                {
                    // Wait briefly for any queued prompt execution to complete, then disconnect cleanly
                    await Task.Delay(1500, cts.Token);
                    while (manager.GetAllAgentStates().Any(s => s.State == RunnerConnectionState.Busy) && !cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(300, cts.Token);
                    }
                    await manager.StopAllAsync();
                    return 0;
                }

                AnsiConsole.MarkupLine("[green]Listening for session prompts. Press Ctrl+C to stop and disconnect.[/]");
                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown on Ctrl+C
                }

                AnsiConsole.MarkupLine("\n[yellow]Stopping connected agents...[/]");
                await manager.StopAllAsync();
                AnsiConsole.MarkupLine("[green]Disconnected cleanly.[/]");
                return 0;
            }

            case "list" or "status" or "ls":
            {
                await RenderStatusTableAsync(config, meshClient, cts.Token);
                return 0;
            }

            case "add" or "register":
            {
                if (subArgs.Count == 0)
                {
                    AnsiConsole.MarkupLine("[red]Usage:[/] agentmesh add <agent-id> [[--name <name>]] [[--role <role>]] [[--adapter <agy|claude|copilot|generic>]] [[--command <cmd>]]");
                    return 1;
                }

                var id = subArgs[0].Trim().ToLowerInvariant();
                var preset = configStore.ResolveOrCreatePreset(config, id, customCommand, customRole);
                if (!string.IsNullOrWhiteSpace(customName)) preset.Name = customName;
                if (!string.IsNullOrWhiteSpace(customAdapter)) preset.AdapterType = customAdapter.ToLowerInvariant();
                configStore.Save(config);

                try
                {
                    var token = await meshClient.EnsureAgentRegisteredAsync(preset, cts.Token);
                    AnsiConsole.MarkupLine($"[green]✓ Registered agent[/] [bold white]@{Markup.Escape(preset.Id)}[/] ({Markup.Escape(preset.Name)})");
                    AnsiConsole.MarkupLine($"  [grey]Adapter:[/] {Markup.Escape(preset.AdapterType)}");
                    AnsiConsole.MarkupLine($"  [grey]Token:[/]   {Markup.Escape(token[..Math.Min(16, token.Length)])}...");
                    AnsiConsole.MarkupLine($"  [grey]Start:[/]   [cyan]agentmesh start {Markup.Escape(preset.Id)}[/]");
                    return 0;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Saved preset @{Markup.Escape(preset.Id)} locally, but could not reach Mesh API:[/] {Markup.Escape(ex.Message)}");
                    return 0;
                }
            }

            case "config":
            {
                configStore.Save(config);
                AnsiConsole.MarkupLine($"[bold skyblue1]AgentMesh CLI Configuration[/] ([grey]{Markup.Escape(configStore.ConfigFilePath)}[/])");
                AnsiConsole.MarkupLine($"  [bold]Mesh URL:[/]     {Markup.Escape(config.MeshUrl)}");
                AnsiConsole.MarkupLine($"  [bold]Master Token:[/] {Markup.Escape(config.MasterToken[..Math.Min(10, config.MasterToken.Length)])}***");
                AnsiConsole.MarkupLine($"  [bold]Presets:[/]      {string.Join(", ", config.Agents.Keys.Select(k => "@" + k))}");
                return 0;
            }

            default:
            {
                PrintHelp(configStore.ConfigFilePath);
                return 1;
            }
        }
    }

    private static async Task RenderStatusTableAsync(CliConfig config, MeshClient meshClient, CancellationToken ct)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Agent ID[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Role[/]")
            .AddColumn("[bold]Local Adapter[/]")
            .AddColumn("[bold]Mesh Status[/]")
            .AddColumn("[bold]Sessions[/]");

        List<RemoteAgentDto> remoteAgents = new();
        var meshReachable = false;
        try
        {
            remoteAgents = await meshClient.GetMeshAgentsAsync(ct);
            meshReachable = true;
        }
        catch
        {
            meshReachable = false;
        }

        var allIds = config.Agents.Keys
            .Union(remoteAgents.Select(r => r.Id), StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        foreach (var id in allIds)
        {
            config.Agents.TryGetValue(id, out var local);
            var remote = remoteAgents.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

            var name = remote?.Name ?? local?.Name ?? id;
            var role = remote?.Role ?? local?.Role ?? "Agent";
            var adapter = local?.AdapterType ?? ConfigStore.InferAdapterType(id);
            var status = remote?.Status?.ToLowerInvariant() ?? (meshReachable ? "unregistered" : "unknown");

            var statusMarkup = status switch
            {
                "online" => "[bold green]● online[/]",
                "busy" => "[bold yellow]◐ busy[/]",
                "offline" => "[grey]○ offline[/]",
                "unregistered" => "[dim]not registered[/]",
                _ => $"[grey]{Markup.Escape(status)}[/]"
            };

            table.AddRow(
                $"[bold white]@{Markup.Escape(id)}[/]",
                Markup.Escape(name),
                Markup.Escape(role),
                $"[cyan]{Markup.Escape(adapter)}[/]",
                statusMarkup,
                (remote?.SessionCount ?? 0).ToString()
            );
        }

        AnsiConsole.MarkupLine($"[bold skyblue1]Mesh Endpoint:[/] {Markup.Escape(config.MeshUrl)} ({(meshReachable ? "[green]Connected[/]" : "[red]Offline[/]")})");
        AnsiConsole.Write(table);
    }

    private static void PrintHelp(string configPath)
    {
        AnsiConsole.MarkupLine("[bold skyblue1]AgentMesh CLI (agentmesh)[/] - Multi-Agent Local Runner & Bridge\n");
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  [cyan]agentmesh[/]                             Launch interactive multi-agent TUI dashboard");
        AnsiConsole.MarkupLine("  [cyan]agentmesh tui [[agent1 agent2...]][/]      Launch TUI and immediately start specified agents");
        AnsiConsole.MarkupLine("  [cyan]agentmesh start <agent1> [[agent2...]][/]  Start and connect one or more agents (e.g. agy, claude, test)");
        AnsiConsole.MarkupLine("  [cyan]agentmesh start --all[/]                 Start all configured agent presets concurrently");
        AnsiConsole.MarkupLine("  [cyan]agentmesh list[/]                        Show all local presets and live Mesh connection status");
        AnsiConsole.MarkupLine("  [cyan]agentmesh add <id> [[options]][/]          Add or configure a custom agent preset");
        AnsiConsole.MarkupLine("  [cyan]agentmesh config [[--url <url>]][/]        View or update Mesh URL and operator token\n");
        AnsiConsole.MarkupLine("[bold]Options:[/]");
        AnsiConsole.MarkupLine("  [grey]--url <url>[/]         AgentMesh API URL (default: http://localhost:5000)");
        AnsiConsole.MarkupLine("  [grey]--token <token>[/]     Operator/master token for auto-registration");
        AnsiConsole.MarkupLine("  [grey]--command, -c <cmd>[/] Custom CLI command to execute for prompts");
        AnsiConsole.MarkupLine("  [grey]--role, -r <role>[/]   Agent role description");
        AnsiConsole.MarkupLine("  [grey]--once[/]              Connect, process any pending prompts, and exit\n");
        AnsiConsole.MarkupLine($"[grey]Config file: {Markup.Escape(configPath)}[/]");
    }
}
