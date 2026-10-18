using AgentMesh.Api.Hubs;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Cli;

public class AgentMeshCliTests
{
    [Fact]
    public void ConfigStore_LoadsDefaultBuiltInPresets_AgyClaudeCopilot()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"agentmesh-test-{Guid.NewGuid()}.json");
        try
        {
            var store = new ConfigStore(tempPath);
            var config = store.Load();

            config.Agents.Should().ContainKey("agy");
            config.Agents.Should().ContainKey("claude");
            config.Agents.Should().ContainKey("copilot");

            config.Agents["agy"].AdapterType.Should().Be("agy");
            config.Agents["claude"].AdapterType.Should().Be("claude");
            config.Agents["copilot"].AdapterType.Should().Be("copilot");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void ConfigStore_ResolveOrCreatePreset_CreatesAndPersistsCustomAgent()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"agentmesh-test-{Guid.NewGuid()}.json");
        try
        {
            var store = new ConfigStore(tempPath);
            var config = store.Load();

            var preset = store.ResolveOrCreatePreset(config, "my-reviewer", "echo hello", "Code Reviewer");

            preset.Id.Should().Be("my-reviewer");
            preset.Command.Should().Be("echo hello");
            preset.Role.Should().Be("Code Reviewer");
            preset.AdapterType.Should().Be("generic");

            var reloaded = store.Load();
            reloaded.Agents.Should().ContainKey("my-reviewer");
            reloaded.Agents["my-reviewer"].Role.Should().Be("Code Reviewer");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void TryParseJsonEventLine_ParsesStructuredTrajectoryEvents_AndRejectsPlainText()
    {
        var jsonLine = "{\"type\":\"tool_call\",\"name\":\"run_command\",\"content\":\"Running git status\",\"args\":{\"cmd\":\"git status\"}}";
        var ok = AgentAdapterFactory.TryParseJsonEventLine(jsonLine, out var payload);

        ok.Should().BeTrue();
        payload.Should().NotBeNull();
        payload!.EventType.Should().Be("tool_call");
        payload.ToolName.Should().Be("run_command");
        payload.Content.Should().Be("Running git status");
        payload.ToolArgsJson.Should().Contain("git status");

        var plainOk = AgentAdapterFactory.TryParseJsonEventLine("Just plain stdout output", out var plainPayload);
        plainOk.Should().BeFalse();
        plainPayload.Should().BeNull();
    }

    [Fact]
    public async Task GenericCommandAdapter_WithCustomCommand_StreamsJsonlAndPlainTextEvents()
    {
        var preset = new AgentPresetConfig
        {
            Id = "custom-bot",
            Name = "Custom Bot",
            AdapterType = "generic",
            Command = "echo '{\"type\":\"thought\",\"content\":\"Thinking step 1\"}' && echo 'Final plain output'"
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "custom-bot",
            Prompt = "Check system",
            Sender = "admin"
        };

        var events = new List<TrajectoryEventPayload>();
        var adapter = AgentAdapterFactory.Create(preset);

        await adapter.ExecutePromptAsync(preset, promptEv, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        }, CancellationToken.None);

        events.Select(e => e.EventType).Should().Contain("thought");
        events.Select(e => e.EventType).Should().Contain("response");
        events.Last().Content.Should().Contain("Final plain output");
    }

    [Fact]
    public async Task AgentMeshHub_RegisterAgentRunner_FlipsStatusToOnline_AndOnDisconnectFlipsToOffline()
    {
        AgentMeshHub.ClearConnections();
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "test-runner", Name = "Test Runner", Status = "offline" });
        await db.SaveChangesAsync();

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");

        var mockGroups = new Mock<IGroupManager>();
        var mockClients = new Mock<IHubCallerClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

        var hub = new AgentMeshHub(db)
        {
            Context = mockContext.Object,
            Groups = mockGroups.Object,
            Clients = mockClients.Object
        };

        // Act 1: Connect
        await hub.RegisterAgentRunner("test-runner");

        // Assert 1: Online
        AgentMeshHub.IsAgentConnected("test-runner").Should().BeTrue();
        (await db.Agents.FindAsync("test-runner"))!.Status.Should().Be("online");

        // Act 2: Disconnect
        await hub.OnDisconnectedAsync(null);

        // Assert 2: Offline
        AgentMeshHub.IsAgentConnected("test-runner").Should().BeFalse();
        (await db.Agents.FindAsync("test-runner"))!.Status.Should().Be("offline");
    }

    [Fact]
    public async Task GenericCommandAdapter_ExecutesInSpecifiedWorkingDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"agentmesh-workdir-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var preset = new AgentPresetConfig
            {
                Id = "workdir-bot",
                Name = "WorkDir Bot",
                AdapterType = "generic",
                Command = isWindows ? "cd" : "pwd"
            };

            var promptEv = new PromptReceivedEvent
            {
                SessionId = Guid.NewGuid(),
                MessageId = Guid.NewGuid(),
                AgentId = "workdir-bot",
                Prompt = "where am i",
                WorkingDirectory = tempDir
            };

            var events = new List<TrajectoryEventPayload>();
            var adapter = AgentAdapterFactory.Create(preset);

            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            var resp = events.FirstOrDefault(e => e.EventType == "response");
            resp.Should().NotBeNull();
            resp!.Content.Trim().Replace('\\', '/').Should().Contain(Path.GetFileName(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PromptReceivedEvent_DeserializesControlParameters()
    {
        var json = """
        {
            "sessionId": "a0000000-0000-0000-0000-000000000001",
            "messageId": "b0000000-0000-0000-0000-000000000002",
            "agentId": "agy",
            "prompt": "Refactor API",
            "sender": "operator",
            "workingDirectory": "/var/workspace",
            "model": "gemini-2.5-pro",
            "effort": "max",
            "mode": "auto"
        }
        """;

        var ev = System.Text.Json.JsonSerializer.Deserialize<PromptReceivedEvent>(json);
        ev.Should().NotBeNull();
        ev!.WorkingDirectory.Should().Be("/var/workspace");
        ev.Model.Should().Be("gemini-2.5-pro");
        ev.Effort.Should().Be("max");
        ev.Mode.Should().Be("auto");
    }
}
