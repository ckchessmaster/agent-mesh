using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

public class ClaudeAdapterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-adapter-test-{Guid.NewGuid():N}");

    public ClaudeAdapterTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildClaudeProcessStartInfo_IncludesContextMaxStepsMcpArgsAndTurnEnv()
    {
        var sessionId = Guid.NewGuid();
        var preset = new AgentPresetConfig { Id = "claude-dev", AdapterType = "claude", MaxSteps = 40 };
        var promptEvent = new PromptReceivedEvent
        {
            SessionId = sessionId,
            JobId = "job-99",
            PersonaId = "astra",
            Prompt = "Audit the repository",
            ContextSize = "200k"
        };

        var psi = ClaudeAdapter.BuildClaudeProcessStartInfo(
            "/usr/bin/claude",
            _tempDir,
            preset,
            promptEvent,
            "Enveloped prompt",
            ["--mcp-config", "/tmp/mcp.json", "--strict-mcp-config", "--disallowedTools", "mcp__gh__delete_repo"]);

        var args = psi.ArgumentList.ToList();
        args.Should().ContainInOrder("--autocompact", "200k");
        args.Should().ContainInOrder("--max-steps", "40");
        args.Should().ContainInOrder("--mcp-config", "/tmp/mcp.json", "--strict-mcp-config");
        args.Should().ContainInOrder("--disallowedTools", "mcp__gh__delete_repo");
        args.Should().ContainInOrder("-p", "Enveloped prompt");

        psi.Environment["AGENTMESH_SESSION_ID"].Should().Be(sessionId.ToString());
        psi.Environment["AGENTMESH_JOB_ID"].Should().Be("job-99");
        psi.Environment["AGENTMESH_PERSONA_ID"].Should().Be("astra");
    }

    [Fact]
    public async Task ExecutePromptAsync_WithMcpServers_ProvisionsStrictMcpConfigDuringRunAndDeletesAfterExit()
    {
        var capturedArgsPath = Path.Combine(_tempDir, "captured-args.txt");
        var capturedConfigPath = Path.Combine(_tempDir, "captured-config.json");
        var fakeClaude = Path.Combine(_tempDir, "fake-claude.sh");

        var script = $$"""
            #!/usr/bin/env bash
            printf '%s\n' "$@" > "{{capturedArgsPath}}"
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--mcp-config" && $# -ge 2 ]]; then
                cp "$2" "{{capturedConfigPath}}"
                shift 2
              else
                shift
              fi
            done
            echo "Claude saw MCP config."
            """;
        await File.WriteAllTextAsync(fakeClaude, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var adapter = new ClaudeAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "claude-mcp",
            AdapterType = "claude",
            Command = fakeClaude
        };
        var promptEvent = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            WorkingDirectory = _tempDir,
            Prompt = "Check GitHub issues",
            McpServers =
            [
                new ResolvedMcpServerConfig
                {
                    Id = "github",
                    Name = "GitHub",
                    Transport = McpTransportType.Stdio,
                    Command = "npx",
                    Args = ["-y", "@modelcontextprotocol/server-github"],
                    Env = new Dictionary<string, string> { ["GH_TOKEN"] = "ghp_claude_never_leak" },
                    BlockedTools = ["delete_repo"]
                }
            ]
        };

        var events = new List<TrajectoryEventPayload>();
        await adapter.ExecutePromptAsync(preset, promptEvent, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        }, CancellationToken.None);

        File.Exists(capturedConfigPath).Should().BeTrue("claude invocation must receive --mcp-config pointing to an existing file");
        var capturedArgs = await File.ReadAllLinesAsync(capturedArgsPath);
        capturedArgs.Should().Contain("--mcp-config");
        capturedArgs.Should().Contain("--strict-mcp-config");
        capturedArgs.Should().ContainInOrder("--disallowedTools", "mcp__github__delete_repo");

        var tempConfigArg = capturedArgs[Array.IndexOf(capturedArgs, "--mcp-config") + 1];
        File.Exists(tempConfigArg).Should().BeFalse("temp MCP config must be deleted once Claude exits");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(capturedConfigPath));
        doc.RootElement.GetProperty("mcpServers")
            .GetProperty("github")
            .GetProperty("env")
            .GetProperty("GH_TOKEN")
            .GetString().Should().Be("ghp_claude_never_leak");

        events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Provisioned 1 MCP server(s) for claude"));
        events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Claude saw MCP config."));
        string.Join("\n", events.Select(e => e.Content)).Should().NotContain("ghp_claude_never_leak");
    }

    [Fact]
    public async Task ExecutePromptAsync_DirectMode_DoesNotProvisionMcpConfig()
    {
        var capturedArgsPath = Path.Combine(_tempDir, "captured-direct-args.txt");
        var fakeClaude = Path.Combine(_tempDir, "fake-claude-direct.sh");

        var script = $$"""
            #!/usr/bin/env bash
            printf '%s\n' "$@" > "{{capturedArgsPath}}"
            echo "Direct reply."
            """;
        await File.WriteAllTextAsync(fakeClaude, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var adapter = new ClaudeAdapter();
        var preset = new AgentPresetConfig { Id = "claude-direct", AdapterType = "claude", Command = fakeClaude };
        var promptEvent = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            WorkingDirectory = _tempDir,
            Mode = "direct",
            Prompt = "Quick hello",
            McpServers =
            [
                new ResolvedMcpServerConfig { Id = "github", Name = "GitHub", Transport = McpTransportType.Stdio, Command = "npx" }
            ]
        };

        var events = new List<TrajectoryEventPayload>();
        await adapter.ExecutePromptAsync(preset, promptEvent, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        }, CancellationToken.None);

        var args = await File.ReadAllLinesAsync(capturedArgsPath);
        args.Should().NotContain("--mcp-config");
        args.Should().NotContain("--strict-mcp-config");
        events.Should().NotContain(e => e.EventType == "thought" && e.Content.Contains("Provisioned"));
    }
}
