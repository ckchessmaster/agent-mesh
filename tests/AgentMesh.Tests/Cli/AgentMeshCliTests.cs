using AgentMesh.Api.Hubs;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Cli;

[Collection("AgentMeshHub")]
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
        var agentId = $"test-runner-{Guid.NewGuid():N}";
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = agentId, Name = "Test Runner", Status = "offline" });
        await db.SaveChangesAsync();

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid():N}");

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
        await hub.RegisterAgentRunner(agentId);

        // Assert 1: Online
        AgentMeshHub.IsAgentConnected(agentId).Should().BeTrue();
        (await db.Agents.FindAsync(agentId))!.Status.Should().Be("online");

        // Act 2: Disconnect
        await hub.OnDisconnectedAsync(null);

        // Assert 2: Offline
        AgentMeshHub.IsAgentConnected(agentId).Should().BeFalse();
        (await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId))!.Status.Should().Be("offline");
        AgentMeshHub.ClearConnections();
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

    [Fact]
    public void StopExecutionEvent_DeserializesPersonaIdAndSessionId()
    {
        var json = """
        {
            "sessionId": "a0000000-0000-0000-0000-000000000001",
            "agentId": "spence-ai",
            "personaId": "rook",
            "reason": "Operator clicked stop"
        }
        """;

        var ev = System.Text.Json.JsonSerializer.Deserialize<StopExecutionEvent>(json);
        ev.Should().NotBeNull();
        ev!.SessionId.Should().Be(Guid.Parse("a0000000-0000-0000-0000-000000000001"));
        ev.AgentId.Should().Be("spence-ai");
        ev.PersonaId.Should().Be("rook");
        ev.Reason.Should().Be("Operator clicked stop");
    }

    [Fact]
    public void AgyAdapter_GetMaxTranscriptStep_CalculatesHighestStepIndex()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"transcript-test-{Guid.NewGuid()}.jsonl");
        try
        {
            File.WriteAllLines(tempFile, new[]
            {
                "{\"step_index\": 0, \"type\": \"USER_INPUT\"}",
                "{\"step_index\": 5, \"type\": \"PLANNER_RESPONSE\"}",
                "{\"step_index\": 12, \"type\": \"PLANNER_RESPONSE\"}"
            });

            var max = AgyAdapter.GetMaxTranscriptStep(tempFile);
            max.Should().Be(12);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void AgyAdapter_CheckForAskQuestionToolCall_DetectsInteractiveQuestion()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"transcript-test-{Guid.NewGuid()}.jsonl");
        try
        {
            var line1 = "{\"step_index\": 0, \"type\": \"USER_INPUT\"}";
            var line2 = """
            {"step_index": 1, "type": "PLANNER_RESPONSE", "content": "Here is an explanation of the differences between Kernel TUN and Userspace Proxy.", "tool_calls": [{"name": "ask_question", "args": {"questions": "[{\"question\":\"Which db?\",\"options\":[\"Postgres\",\"SQLite\"],\"is_multi_select\":false}]", "toolSummary": "Database choice"}}]}
            """;
            File.WriteAllLines(tempFile, new[] { line1, line2 });

            var lastStep = 0;
            var payload = AgyAdapter.CheckForAskQuestionToolCall(tempFile, ref lastStep);

            payload.Should().NotBeNull();
            payload!.EventType.Should().Be("tool_call");
            payload.ToolName.Should().Be("ask_question");
            payload.Content.Should().Be("Database choice");
            payload.ToolArgsJson.Should().Contain("Which db?");
            payload.PriorResponseContent.Should().Be("Here is an explanation of the differences between Kernel TUN and Userspace Proxy.");
            lastStep.Should().Be(1);

            // Re-checking should not re-detect the same step
            var secondCheck = AgyAdapter.CheckForAskQuestionToolCall(tempFile, ref lastStep);
            secondCheck.Should().BeNull();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GitWorkspaceManager_DetectsRepo_PreparesBranch_AndCommitsChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"agentmesh-git-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Initially not a git repo
            var isGitBefore = await GitWorkspaceManager.IsGitRepositoryAsync(tempDir);
            isGitBefore.Should().BeFalse();

            // Initialize git repo
            using var initProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "init -b main",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            await initProc.WaitForExitAsync();

            // Set git user config locally for the test repo
            using var configName = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "config user.name \"Test Runner\"",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            await configName.WaitForExitAsync();

            using var configEmail = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "config user.email \"runner@test.local\"",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            await configEmail.WaitForExitAsync();

            // Create initial commit on main
            File.WriteAllText(Path.Combine(tempDir, "README.md"), "# Test Repo");
            using var addProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "add -A",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            await addProc.WaitForExitAsync();

            using var commitProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "commit -m \"initial commit\"",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            await commitProc.WaitForExitAsync();

            // Now detected as git repo
            var isGitAfter = await GitWorkspaceManager.IsGitRepositoryAsync(tempDir);
            isGitAfter.Should().BeTrue();

            // Prepare job branch
            var logs = new List<string>();
            await GitWorkspaceManager.PrepareJobBranchAsync(tempDir, "mesh/job-auth-oauth", msg => logs.Add(msg));

            // Verify branch switched
            using var branchProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "branch --show-current",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            var currentBranch = (await branchProc.StandardOutput.ReadToEndAsync()).Trim();
            await branchProc.WaitForExitAsync();
            currentBranch.Should().Be("mesh/job-auth-oauth");

            // Make a file change during persona turn
            File.WriteAllText(Path.Combine(tempDir, "auth.ts"), "export const auth = () => true;");

            // Commit turn changes
            await GitWorkspaceManager.CommitTurnChangesAsync(
                tempDir,
                "mesh/job-auth-oauth",
                "astra",
                "Astra",
                "Implement OAuth handlers for authentication",
                msg => logs.Add(msg));

            // Verify git log
            using var logProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "log -1 --pretty=format:\"%an <%ae> - %s\"",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            var lastCommit = (await logProc.StandardOutput.ReadToEndAsync()).Trim();
            await logProc.WaitForExitAsync();

            lastCommit.Should().Contain("Astra <astra-bot@agentmesh.local>");
            lastCommit.Should().Contain("feat(job): turn by @astra");
            lastCommit.Should().Contain("Implement OAuth handlers");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
