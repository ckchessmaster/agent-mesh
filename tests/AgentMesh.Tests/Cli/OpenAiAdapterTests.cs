using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

public class OpenAiAdapterTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("openai-compatible")]
    [InlineData("ollama")]
    [InlineData("vllm")]
    public void AgentAdapterFactory_Create_ReturnsOpenAiAdapter_ForSupportedAdapterTypes(string adapterType)
    {
        var preset = new AgentPresetConfig
        {
            Id = "my-llm",
            Name = "Local LLM",
            AdapterType = adapterType
        };

        var adapter = AgentAdapterFactory.Create(preset);
        adapter.Should().BeOfType<OpenAiAdapter>();
        adapter.AdapterType.Should().Be("openai");
    }

    [Theory]
    [InlineData("openai-server", "openai")]
    [InlineData("ollama-local", "openai")]
    [InlineData("vllm-node", "openai")]
    [InlineData("lmstudio-box", "openai")]
    [InlineData("copilot-agent", "copilot")]
    [InlineData("claude-bot", "claude")]
    [InlineData("agy-dev", "agy")]
    public void ConfigStore_InferAdapterType_IdentifiesOpenAiFamily(string id, string expected)
    {
        var inferred = ConfigStore.InferAdapterType(id);
        inferred.Should().Be(expected);
    }

    [Fact]
    public void OpenAiAdapter_GetToolDefinitions_ContainsAllRequiredAutonomousTools()
    {
        var tools = OpenAiAdapter.GetToolDefinitions();
        tools.Should().HaveCount(5);

        var toolNames = new List<string>();
        foreach (var t in tools)
        {
            var json = JsonSerializer.Serialize(t);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("function", out var fn) &&
                fn.TryGetProperty("name", out var n))
            {
                toolNames.Add(n.GetString()!);
            }
        }

        toolNames.Should().Contain("run_command");
        toolNames.Should().Contain("read_file");
        toolNames.Should().Contain("write_file");
        toolNames.Should().Contain("edit_file");
        toolNames.Should().Contain("ask_question");
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_ExecutesAndReturnsOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var args = JsonSerializer.Serialize(new { command = "echo 'OpenAiAdapter tool test'" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, CancellationToken.None);

            result.Should().Contain("OpenAiAdapter tool test");
            result.Should().Contain("Exit Code: 0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_WriteAndReadFile_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Write
            var writeArgs = JsonSerializer.Serialize(new
            {
                path = "test.txt",
                content = "Line 1\nLine 2\nLine 3"
            });
            var writeRes = await OpenAiAdapter.ExecuteToolLocallyAsync("write_file", writeArgs, tempDir, CancellationToken.None);
            writeRes.Should().Contain("Successfully wrote");

            // Read
            var readArgs = JsonSerializer.Serialize(new
            {
                path = "test.txt",
                startLine = 2,
                endLine = 3
            });
            var readRes = await OpenAiAdapter.ExecuteToolLocallyAsync("read_file", readArgs, tempDir, CancellationToken.None);
            readRes.Should().Contain("2: Line 2");
            readRes.Should().Contain("3: Line 3");
            readRes.Should().NotContain("1: Line 1");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_EditFile_ReplacesTargetAndReportsMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "config.json");
            await File.WriteAllTextAsync(filePath, "{\"port\": 8080, \"debug\": false}");

            // Successful edit
            var editArgs = JsonSerializer.Serialize(new
            {
                path = "config.json",
                target = "\"debug\": false",
                replacement = "\"debug\": true"
            });
            var editRes = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", editArgs, tempDir, CancellationToken.None);
            editRes.Should().Contain("Successfully replaced");

            var updated = await File.ReadAllTextAsync(filePath);
            updated.Should().Contain("\"debug\": true");

            // Missing target error
            var failArgs = JsonSerializer.Serialize(new
            {
                path = "config.json",
                target = "non_existent_key",
                replacement = "new_val"
            });
            var failRes = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", failArgs, tempDir, CancellationToken.None);
            failRes.Should().Contain("Error: Target string not found");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WithUnreachableServer_EmitsDescriptiveErrorMessage()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-ollama",
            Name = "Local Ollama",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:59998/v1"
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "test-ollama",
            Prompt = "Hello world",
            Sender = "operator"
        };

        var events = new List<TrajectoryEventPayload>();
        await adapter.ExecutePromptAsync(preset, promptEv, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        }, CancellationToken.None);

        events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Connecting to OpenAI-compatible server"));
        events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Failed to connect to OpenAI-compatible endpoint"));
    }
}
