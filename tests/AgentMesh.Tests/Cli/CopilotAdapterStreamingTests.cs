using System.Diagnostics;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
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
}
