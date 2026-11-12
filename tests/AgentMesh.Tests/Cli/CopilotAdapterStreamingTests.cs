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
}
