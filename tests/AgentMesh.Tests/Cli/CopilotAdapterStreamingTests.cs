using System.Diagnostics;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Cli;

public class CopilotAdapterStreamingTests
{
    [Fact]
    public void BuildCopilotProcessStartInfo_WhenJsonFormat_IncludesOutputFormatAndStream()
    {
        var promptEvent = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            Prompt = "Hello Copilot",
            Model = "gemini-4-carbon"
        };

        var psi = CopilotAdapter.BuildCopilotProcessStartInfo(
            "/usr/bin/copilot",
            isViaGh: false,
            cwd: "/workspace",
            promptEvent,
            promptEnvelope: "Enveloped Prompt",
            useJsonFormat: true);

        var args = psi.ArgumentList.ToList();
        args.Should().Contain("-p");
        args[args.IndexOf("-p") + 1].Should().Be("Enveloped Prompt");
        args.Should().Contain("--allow-all");
        args.Should().Contain("--no-color");
        args.Should().Contain("--output-format");
        args[args.IndexOf("--output-format") + 1].Should().Be("json");
        args.Should().Contain("--stream");
        args[args.IndexOf("--stream") + 1].Should().Be("on");
        args.Should().Contain("--model");
        args[args.IndexOf("--model") + 1].Should().Be("gemini-4-carbon");
    }

    [Fact]
    public void BuildCopilotProcessStartInfo_WhenFallbackTextFormat_OmitsOutputFormat()
    {
        var promptEvent = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            Prompt = "Hello Copilot"
        };

        var psi = CopilotAdapter.BuildCopilotProcessStartInfo(
            "/usr/bin/copilot",
            isViaGh: false,
            cwd: "/workspace",
            promptEvent,
            promptEnvelope: "Enveloped Prompt",
            useJsonFormat: false);

        var args = psi.ArgumentList.ToList();
        args.Should().NotContain("--output-format");
        args.Should().NotContain("--stream");
    }

    [Fact]
    public void TryParseCopilotJsonLine_SessionStart_ExtractsSessionId()
    {
        const string line = """{"type":"session.start","data":{"sessionId":"test-session-123","version":1}}""";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out var toolName,
            out var toolArgs,
            out var toolResult,
            out var sessionId,
            out var usage);

        parsed.Should().BeTrue();
        eventType.Should().Be("session.start");
        sessionId.Should().Be("test-session-123");
    }

    [Fact]
    public void TryParseCopilotJsonLine_AssistantReasoning_ExtractsThought()
    {
        const string line = """{"type":"assistant.reasoning","data":{"content":"Analyzing Helm chart persistence templates..."}}""";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out var toolName,
            out var toolArgs,
            out var toolResult,
            out var sessionId,
            out var usage);

        parsed.Should().BeTrue();
        eventType.Should().Be("thought");
        content.Should().Be("Analyzing Helm chart persistence templates...");
    }

    /// <summary>
    /// The incident of 2026-11-18: the copilot CLI's `session.*` control events
    /// (mcp_server_status_changed, background_tasks_changed, all `"ephemeral":true`) matched no
    /// branch, the parser returned false, and the caller's non-JSON text fallback appended every
    /// one to the response builder - two sessions settled with ~500KB event-dump "replies".
    /// These events must be claimed by the parser so they never reach the text fallback.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"session.background_tasks_changed","data":{},"ephemeral":true,"id":"1f6d41f5-93fd-4112-a769-0551879bd216","timestamp":"2026-11-18T22:17:01.571Z","parentId":"cc515dff-2475-422d-bc5a-b0c7f2518291"}""")]
    [InlineData("""{"type":"session.mcp_server_status_changed","data":{"serverName":"github-mcp-server","status":"pending"},"ephemeral":true,"id":"d86f1dc9-8d2d-4300-bc05-412ed8d527d8","timestamp":"2026-11-18T21:49:17.041Z"}""")]
    [InlineData("""{"type":"session.mcp_server_tools_loaded","data":{"serverName":"github-mcp-server","tools":[]},"ephemeral":true}""")]
    [InlineData("""{"type":"ping","data":{},"ephemeral":true}""")]
    [InlineData("""{"type":"session.thinking_toggled","data":{"enabled":true},"ephemeral":true}""")]
    public void TryParseCopilotJsonLine_ControlPlaneEvents_AreClaimedWithNoContent(string line)
    {
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out _, out _, out _, out _, out _);

        parsed.Should().BeTrue($"a line the parser rejects falls through to the raw-text fallback and pollutes the reply: {line}");
        content.Should().BeNullOrWhiteSpace();
        eventType.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryParseCopilotJsonLine_SessionNamespacedContentEvents_StillMapToHandlers()
    {
        // Prefix-stripping must route real content through the normal handlers, not the
        // control-event claim: the two are indistinguishable at the namespace level.
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            """{"type":"session.assistant.message","data":{"content":"PR opened"}}""",
            out var eventType, out var content, out _, out _, out _, out _, out _);

        parsed.Should().BeTrue();
        eventType.Should().Be("message");
        content.Should().Be("PR opened");

        parsed = CopilotAdapter.TryParseCopilotJsonLine(
            """{"type":"session.assistant.reasoning_delta","data":{"delta":"checking"}}""",
            out eventType, out content, out _, out _, out _, out _, out _);

        parsed.Should().BeTrue();
        eventType.Should().Be("thought_delta");
        content.Should().Be("checking");
    }

    [Fact]
    public void TryParseCopilotJsonLine_UnknownNonEphemeralJson_StillFallsBack()
    {
        // Deliberately narrow: only namespaced or ephemeral events are claimed. An unknown event
        // shape the CLI actually emits as prose-ish JSON keeps the old raw-text behavior so we do
        // not silently swallow output we have not learned to parse.
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            """{"type":"some_future_notice","data":{"content":"hello"}}""",
            out _, out _, out _, out _, out _, out _, out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParseCopilotJsonLine_ToolCallShell_ExtractsToolAndSummary()
    {
        const string line = """{"type":"tool.call","data":{"tool":"shell","args":{"command":"grep -rn persistence charts/"}}}""";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out var toolName,
            out var toolArgs,
            out var toolResult,
            out var sessionId,
            out var usage);

        parsed.Should().BeTrue();
        eventType.Should().Be("tool_call");
        toolName.Should().Be("shell");
        content.Should().Be("Running shell: grep -rn persistence charts/");
        toolArgs.Should().Contain("grep -rn persistence");
    }

    [Fact]
    public void TryParseCopilotJsonLine_ToolCallReadFile_ExtractsToolAndPath()
    {
        const string line = """{"type":"tool.call","data":{"tool":"read","args":{"path":"charts/agent-mesh/values.yaml"}}}""";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out var toolName,
            out var toolArgs,
            out var toolResult,
            out var sessionId,
            out var usage);

        parsed.Should().BeTrue();
        eventType.Should().Be("tool_call");
        toolName.Should().Be("read");
        content.Should().Be("Reading file: charts/agent-mesh/values.yaml");
    }

    [Fact]
    public void TryParseCopilotJsonLine_ToolResult_ExtractsResult()
    {
        const string line = """{"type":"tool.result","data":{"tool":"shell","result":"persistence:\n  size: 10Gi"}}""";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out var toolName,
            out var toolArgs,
            out var toolResult,
            out var sessionId,
            out var usage);

        parsed.Should().BeTrue();
        eventType.Should().Be("tool_result");
        toolName.Should().Be("shell");
        toolResult.Should().Contain("persistence:");
    }

    [Fact]
    public void TryParseCopilotJsonLine_AssistantMessage_ExtractsMessage()
    {
        const string line = """{"type":"assistant.message","data":{"content":"You can change PVC size using persistence.size in values.yaml."}}""";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out var toolName,
            out var toolArgs,
            out var toolResult,
            out var sessionId,
            out var usage);

        parsed.Should().BeTrue();
        eventType.Should().Be("message");
        content.Should().Be("You can change PVC size using persistence.size in values.yaml.");
    }

    [Fact]
    public void TryParseCopilotJsonLine_ResultWithUsage_ExtractsResultAndUsage()
    {
        const string line = """{"type":"result","data":{"result":"Turn completed."},"usage":{"premiumRequests":1,"totalApiDurationMs":1500,"sessionDurationMs":3000}}""";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out var eventType,
            out var content,
            out var toolName,
            out var toolArgs,
            out var toolResult,
            out var sessionId,
            out var usage);

        parsed.Should().BeTrue();
        eventType.Should().Be("result");
        content.Should().Be("Turn completed.");
        usage.Should().Contain("premiumRequests");
    }

    [Fact]
    public void TryParseCopilotJsonLine_PlainText_ReturnsFalse()
    {
        const string line = "Running shell command: grep -rn persistence charts/";
        var parsed = CopilotAdapter.TryParseCopilotJsonLine(
            line,
            out _, out _, out _, out _, out _, out _, out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParseCopilotJsonLine_ToolExecutionStartAndComplete_MapsStartToToolCall_AndCompleteToToolResult()
    {
        const string startLine = """{"type":"tool.execution_start","data":{"toolName":"bash","arguments":{"command":"dotnet test"}}}""";
        var startParsed = CopilotAdapter.TryParseCopilotJsonLine(
            startLine,
            out var startEvType,
            out _,
            out var startToolName,
            out var startArgs,
            out _,
            out _,
            out _);

        startParsed.Should().BeTrue();
        startEvType.Should().Be("tool_call");
        startToolName.Should().Be("bash");
        startArgs.Should().Contain("dotnet test");

        const string completeLine = """{"type":"tool.execution_complete","data":{"toolName":"bash","result":{"content":"Passed! 42 tests"}}}""";
        var completeParsed = CopilotAdapter.TryParseCopilotJsonLine(
            completeLine,
            out var completeEvType,
            out _,
            out var completeToolName,
            out _,
            out var completeResult,
            out _,
            out _);

        completeParsed.Should().BeTrue();
        completeEvType.Should().Be("tool_result", "tool.execution_complete must not match tool_call's 'execution' substring");
        completeToolName.Should().Be("bash");
        completeResult.Should().Contain("Passed! 42 tests");
    }

    [Fact]
    public async Task RunCopilotSubprocessAsync_ToolOnlyStreamEndingWithTaskComplete_EmitsTaskCompleteSummaryInsteadOfRawJsonl()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "copilot-stream-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var fakeScript = Path.Combine(tempDir, "fake-copilot.sh");

        var hugeToolOutput = new string('Z', 120_000);
        const string scriptTemplate = """
            #!/usr/bin/env bash
            echo '{"type":"session.start","data":{"sessionId":"cop-sess-99"}}'
            echo '{"type":"tool.execution_start","data":{"toolName":"bash","arguments":{"command":"cat big.log"}}}'
            echo '{"type":"tool.execution_complete","data":{"toolName":"bash","result":{"content":"__HUGE_OUTPUT__"}}}'
            echo '{"type":"session.task_complete","data":{"summary":"Completed stabilizing CopilotAdapter JSONL parsing and verified all unit tests."}}'
            """;
        var scriptContent = scriptTemplate.Replace("__HUGE_OUTPUT__", hugeToolOutput);
        await File.WriteAllTextAsync(fakeScript, scriptContent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            var events = new List<TrajectoryEventPayload>();
            var preset = new AgentPresetConfig { Id = "copilot-cli", AdapterType = "copilot" };
            var promptEvent = new PromptReceivedEvent { SessionId = Guid.NewGuid(), Prompt = "Fix bug" };

            var (exitCode, _, _, succeeded) = await CopilotAdapter.RunCopilotSubprocessAsync(
                fakeScript,
                isViaGh: false,
                cwd: tempDir,
                preset,
                promptEvent,
                promptEnvelope: "Fix bug",
                useJsonFormat: true,
                ev => { events.Add(ev); return Task.CompletedTask; },
                CancellationToken.None);

            exitCode.Should().Be(0);
            succeeded.Should().BeTrue();

            events.Count(e => e.EventType == "tool_call").Should().Be(1);
            events.Count(e => e.EventType == "tool_result").Should().Be(1);

            var responseEvent = events.Single(e => e.EventType == "response");
            responseEvent.Content.Should().Be("Completed stabilizing CopilotAdapter JSONL parsing and verified all unit tests.");
            responseEvent.Content.Should().NotContain(hugeToolOutput);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveFinalCopilotOutput_WhenStreamedAndTaskCompleteAreEmpty_BoundsLargeRawJsonlFallbackTo16Kb()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 1500; i++)
        {
            sb.AppendLine($$$"""{"type":"tool.execution_complete","data":{"toolName":"bash","result":"line-{{{i}}}-{{{new string('a', 90)}}}"}}""");
        }
        var rawJsonl = sb.ToString();
        rawJsonl.Length.Should().BeGreaterThan(100_000);

        var resolved = CopilotAdapter.ResolveFinalCopilotOutput(
            streamedResponse: string.Empty,
            taskCompleteSummary: null,
            rawStdOut: rawJsonl,
            stdErr: string.Empty);

        resolved.Length.Should().BeLessThanOrEqualTo(CopilotAdapter.MaxRawFallbackPreviewChars);
        resolved.Should().Contain("line-1499-", "bounded fallback keeps the tail where exit diagnostics appear");
    }

    [Fact]
    public async Task MeshClient_EnsureSuccessWithBodySnippetAsync_IncludesTruncatedResponseBodyOn400()
    {
        var errorBody = "{\"error\":\"validation_failed\",\"detail\":\"" + new string('E', 800) + "\"}";
        using var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new System.Net.Http.StringContent(errorBody)
        };

        var act = async () => await AgentMesh.Cli.Services.MeshClient.EnsureSuccessWithBodySnippetAsync(
            response, "POST api/sessions/123/events (response)");

        var ex = await act.Should().ThrowAsync<System.Net.Http.HttpRequestException>();
        ex.Which.Message.Should().Contain("400");
        ex.Which.Message.Should().Contain("validation_failed");
        ex.Which.Message.Length.Should().BeLessThan(650);
    }

    [Fact]
    public void IsolateCopilotEnvironment_WhenClassicPatInGhAndCopilotEnvVars_StripsThemAndPreservesAgentMeshGitHubToken()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GH_TOKEN"] = "ghp_classicRepoPat12345",
            ["GITHUB_TOKEN"] = "ghp_classicRepoPat12345",
            ["COPILOT_GITHUB_TOKEN"] = "ghp_classicRepoPat12345"
        };

        var tempHome = Path.Combine(Path.GetTempPath(), $"copilot-env-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            CopilotAdapter.IsolateCopilotEnvironment(env, tempHome);

            env.Should().NotContainKey("GH_TOKEN");
            env.Should().NotContainKey("GITHUB_TOKEN");
            env.Should().NotContainKey("COPILOT_GITHUB_TOKEN");
            env["AGENTMESH_GITHUB_TOKEN"].Should().Be("ghp_classicRepoPat12345");
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void IsolateCopilotEnvironment_WhenSeparateFineGrainedPersonaPatAndCopilotOAuth_KeepsBothIdentitiesSeparate()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"copilot-env-sep-{Guid.NewGuid():N}");
        var copilotDir = Path.Combine(tempHome, ".copilot");
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(
            Path.Combine(copilotDir, "config.json"),
            """{"copilot_tokens":{"https://github.com:CKingdon_bee":"gho_emuCopilotLogin"}}""");

        try
        {
            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AGENTMESH_GITHUB_TOKEN"] = "github_pat_ckchessmaster_repo",
                ["GH_TOKEN"] = "github_pat_ckchessmaster_repo",
                ["GITHUB_TOKEN"] = "github_pat_ckchessmaster_repo"
            };

            CopilotAdapter.IsolateCopilotEnvironment(env, tempHome);

            env.Should().NotContainKey("GH_TOKEN", "GH_TOKEN would override ~/.copilot/config.json inside Copilot CLI");
            env.Should().NotContainKey("GITHUB_TOKEN");
            env.Should().NotContainKey("COPILOT_GITHUB_TOKEN");
            env["AGENTMESH_GITHUB_TOKEN"].Should().Be("github_pat_ckchessmaster_repo");
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void IsolateCopilotEnvironment_WhenCopilotOAuthTokenSetWithoutConfigFile_ForwardsToCopilotGitHubToken()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"copilot-env-oauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AGENTMESH_GITHUB_TOKEN"] = "ghp_classicRepoPat12345",
                ["GH_TOKEN"] = "ghp_classicRepoPat12345",
                ["COPILOT_OAUTH_TOKEN"] = "gho_driverSeededCopilotToken"
            };

            CopilotAdapter.IsolateCopilotEnvironment(env, tempHome);

            env.Should().NotContainKey("GH_TOKEN");
            env["COPILOT_GITHUB_TOKEN"].Should().Be("gho_driverSeededCopilotToken");
            env["AGENTMESH_GITHUB_TOKEN"].Should().Be("ghp_classicRepoPat12345");
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void IsolateCopilotEnvironment_WhenConfigHasOrphanedLoggedInUsersAndAuthTokens_ForwardsCopilotOAuthToken()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"copilot-env-orphan-{Guid.NewGuid():N}");
        var copilotDir = Path.Combine(tempHome, ".copilot");
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(
            Path.Combine(copilotDir, "config.json"),
            """{"logged_in_users":[{"host":"https://github.com","login":"emu_user"}],"authTokens":{"https://github.com":{"token":"gho_stale"}}}""");

        try
        {
            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AGENTMESH_GITHUB_TOKEN"] = "ghp_personaRepoPat",
                ["GH_TOKEN"] = "ghp_personaRepoPat",
                ["COPILOT_OAUTH_TOKEN"] = "gho_freshDriverOAuth"
            };

            CopilotAdapter.IsolateCopilotEnvironment(env, tempHome);

            env.Should().NotContainKey("GH_TOKEN");
            env.Should().NotContainKey("GITHUB_TOKEN");
            env["COPILOT_GITHUB_TOKEN"].Should().Be("gho_freshDriverOAuth");
            env["AGENTMESH_GITHUB_TOKEN"].Should().Be("ghp_personaRepoPat");
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void IsolateCopilotEnvironment_WhenDriverSpecificCopilotHomeExists_UsesIsolatedCopilotHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"copilot-env-drvhome-{Guid.NewGuid():N}");
        var driverHome = Path.Combine(tempHome, ".agentmesh", "copilot-homes", "beeline-gh-copilot");
        Directory.CreateDirectory(driverHome);
        File.WriteAllText(
            Path.Combine(driverHome, "config.json"),
            """{"copilot_tokens":{"https://github.com:emu_user":"gho_emuToken"},"logged_in_users":[{"host":"https://github.com","login":"emu_user"}]}""");

        try
        {
            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GH_TOKEN"] = "ghp_personaRepoPat"
            };

            CopilotAdapter.IsolateCopilotEnvironment(env, tempHome, driverId: "beeline-gh-copilot");

            env.Should().NotContainKey("GH_TOKEN");
            env.Should().NotContainKey("COPILOT_GITHUB_TOKEN");
            env["COPILOT_HOME"].Should().Be(driverHome);
            env["AGENTMESH_GITHUB_TOKEN"].Should().Be("ghp_personaRepoPat");
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void FormatToolCallSummary_RichToolSummaries_FormatAllSupportedTools()
    {
        CopilotAdapter.FormatToolCallSummary("bash", """{"command":"dotnet test","description":"Run unit tests"}""")
            .Should().Be("Run unit tests — dotnet test");

        CopilotAdapter.FormatToolCallSummary("bash", """{"command":"git status"}""")
            .Should().Be("Running bash: git status");

        CopilotAdapter.FormatToolCallSummary("view", """{"path":"/workspace/repos/agent-mesh/src/App.tsx","view_range":[1,250]}""")
            .Should().Be("Viewing App.tsx (lines 1-250)");

        CopilotAdapter.FormatToolCallSummary("view", """{"path":"/workspace/repos/agent-mesh/package.json"}""")
            .Should().Be("Viewing package.json");

        CopilotAdapter.FormatToolCallSummary("glob", """{"paths":"/workspace/repos/agent-mesh/src/Dashboard","pattern":"**/*.tsx"}""")
            .Should().Be("Finding files: **/*.tsx in Dashboard");

        CopilotAdapter.FormatToolCallSummary("grep", """{"paths":"/workspace/repos/agent-mesh/src","pattern":"TryParseCopilotJsonLine"}""")
            .Should().Be("Searching for 'TryParseCopilotJsonLine' in src");

        CopilotAdapter.FormatToolCallSummary("sql", """{"query":"SELECT id FROM sessions","description":"List sessions"}""")
            .Should().Be("Querying (List sessions): SELECT id FROM sessions");

        CopilotAdapter.InferCopilotToolName("tool", """{"command":"ls -la"}""").Should().Be("bash");
        CopilotAdapter.FormatToolCallSummary("tool", """{"command":"ls -la"}""")
            .Should().Be("Running bash: ls -la");
    }

    [Fact]
    public void FormatCopilotToolResultSummary_UnwrapsJsonAndHandlesEmptyOutput()
    {
        CopilotAdapter.FormatCopilotToolResultSummary("bash", """{"content":"All 42 tests passed"}""")
            .Should().Be("All 42 tests passed");

        CopilotAdapter.FormatCopilotToolResultSummary("bash", "")
            .Should().Be("Completed bash (no output)");

        CopilotAdapter.FormatCopilotToolResultSummary("bash", null)
            .Should().Be("Completed bash (no output)");

        CopilotAdapter.FormatCopilotToolResultSummary("bash", """{"content":""}""")
            .Should().Be("Completed bash (no output)");
    }

    [Fact]
    public async Task RunCopilotSubprocessAsync_CoalescesArglessPreludeAndSurvivesEmptyToolOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "copilot-coalesce-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var fakeScript = Path.Combine(tempDir, "fake-copilot-coalesce.sh");

        const string scriptContent = """
            #!/usr/bin/env bash
            echo '{"type":"session.start","data":{"sessionId":"cop-sess-coalesce"}}'
            echo '{"type":"tool.call","data":{"toolName":"bash"}}'
            echo '{"type":"assistant.reasoning","data":{"content":"Inspecting repository status first."}}'
            echo '{"type":"tool.execution_start","data":{"toolName":"bash","arguments":{"command":"git status","description":"Check git status"}}}'
            echo '{"type":"tool.execution_complete","data":{"toolName":"bash","result":{"content":""}}}'
            echo '{"type":"assistant.message","data":{"content":"Repository is clean."}}'
            """;
        await File.WriteAllTextAsync(fakeScript, scriptContent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            var events = new List<TrajectoryEventPayload>();
            var preset = new AgentPresetConfig { Id = "copilot-cli", AdapterType = "copilot" };
            var promptEvent = new PromptReceivedEvent { SessionId = Guid.NewGuid(), Prompt = "Check status" };

            var (exitCode, _, _, succeeded) = await CopilotAdapter.RunCopilotSubprocessAsync(
                fakeScript,
                isViaGh: false,
                cwd: tempDir,
                preset,
                promptEvent,
                promptEnvelope: "Check status",
                useJsonFormat: true,
                ev =>
                {
                    if (string.IsNullOrWhiteSpace(ev.Content))
                    {
                        throw new InvalidOperationException("Message content is required.");
                    }
                    events.Add(ev);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            exitCode.Should().Be(0);
            succeeded.Should().BeTrue();

            var thoughts = events.Where(e => e.EventType == "thought").ToList();
            thoughts.Should().ContainSingle(t => t.Content == "Inspecting repository status first.");

            var toolCalls = events.Where(e => e.EventType == "tool_call").ToList();
            toolCalls.Should().HaveCount(1);
            toolCalls[0].ToolName.Should().Be("bash");
            toolCalls[0].Content.Should().Be("Check git status — git status");
            toolCalls[0].ToolArgsJson.Should().Contain("git status");

            var toolResults = events.Where(e => e.EventType == "tool_result").ToList();
            toolResults.Should().HaveCount(1);
            toolResults[0].ToolName.Should().Be("bash");
            toolResults[0].Content.Should().Be("Completed bash (no output)");

            var responseEvent = events.Single(e => e.EventType == "response");
            responseEvent.Content.Should().Be("Repository is clean.");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildCopilotProcessStartInfo_WhenExtraMcpArgumentsProvided_AppendsConfigAndDenyFlags()
    {
        var promptEvent = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            Prompt = "Hello Copilot"
        };

        var psi = CopilotAdapter.BuildCopilotProcessStartInfo(
            "/usr/bin/copilot",
            isViaGh: false,
            cwd: "/workspace",
            promptEvent,
            promptEnvelope: "Enveloped Prompt",
            useJsonFormat: true,
            driverId: "copilot-cli",
            extraMcpArguments: ["--additional-mcp-config", "@/tmp/copilot-mcp.json", "--deny-tool", "github(delete_repo)"]);

        var args = psi.ArgumentList.ToList();
        args.Should().ContainInOrder("--additional-mcp-config", "@/tmp/copilot-mcp.json");
        args.Should().ContainInOrder("--deny-tool", "github(delete_repo)");
    }

    [Fact]
    public async Task RunCopilotSubprocessAsync_WithMcpServers_ProvisionsTempConfigDuringRunAndCleansUpAfterExit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "copilot-mcp-run-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var fakeScript = Path.Combine(tempDir, "fake-copilot-mcp.sh");
        var capturedArgsPath = Path.Combine(tempDir, "args.txt");
        var capturedConfigPath = Path.Combine(tempDir, "config.json");

        var script = $$$"""
            #!/usr/bin/env bash
            printf '%s\n' "$@" > "{{{capturedArgsPath}}}"
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--additional-mcp-config" && $# -ge 2 ]]; then
                cfg="${2#@}"
                cp "$cfg" "{{{capturedConfigPath}}}"
                shift 2
              else
                shift
              fi
            done
            echo '{"type":"assistant.message","data":{"content":"Copilot used MCP."}}'
            """;
        await File.WriteAllTextAsync(fakeScript, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            var events = new List<TrajectoryEventPayload>();
            var preset = new AgentPresetConfig { Id = "copilot-cli", AdapterType = "copilot" };
            var promptEvent = new PromptReceivedEvent
            {
                SessionId = Guid.NewGuid(),
                Prompt = "Inspect PRs",
                McpServers =
                [
                    new ResolvedMcpServerConfig
                    {
                        Id = "github",
                        Name = "GitHub",
                        Transport = McpTransportType.Http,
                        Url = "https://api.githubcopilot.com/mcp/",
                        Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer copilot_mcp_secret" },
                        AllowedTools = ["list_prs", "delete_repo"],
                        BlockedTools = ["delete_repo"]
                    }
                ]
            };

            var (exitCode, _, _, succeeded) = await CopilotAdapter.RunCopilotSubprocessAsync(
                fakeScript,
                isViaGh: false,
                cwd: tempDir,
                preset,
                promptEvent,
                promptEnvelope: "Inspect PRs",
                useJsonFormat: true,
                ev => { events.Add(ev); return Task.CompletedTask; },
                CancellationToken.None);

            exitCode.Should().Be(0);
            succeeded.Should().BeTrue();
            File.Exists(capturedConfigPath).Should().BeTrue();

            var args = await File.ReadAllLinesAsync(capturedArgsPath);
            args.Should().Contain("--additional-mcp-config");
            args.Should().ContainInOrder("--deny-tool", "github(delete_repo)");

            var atPath = args[Array.IndexOf(args, "--additional-mcp-config") + 1];
            atPath.Should().StartWith("@");
            File.Exists(atPath[1..]).Should().BeFalse("temp Copilot MCP config must be deleted after exit");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(capturedConfigPath));
            var gh = doc.RootElement.GetProperty("mcpServers").GetProperty("github");
            gh.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer copilot_mcp_secret");
            gh.GetProperty("tools").EnumerateArray().Select(e => e.GetString()).Should().Equal("list_prs");

            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Provisioned 1 MCP server(s) for copilot"));
            string.Join("\n", events.Select(e => e.Content)).Should().NotContain("copilot_mcp_secret");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
