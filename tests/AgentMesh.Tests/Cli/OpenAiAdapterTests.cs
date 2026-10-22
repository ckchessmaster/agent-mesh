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

    [Fact]
    public void ConfigStore_PopulateFromRemote_ExtractsOpenAiSettingsCorrectly()
    {
        var configStore = new ConfigStore();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Agent spence-ai",
            AdapterType = "generic"
        };

        var remote = new RemoteAgentDto(
            Id: "spence-ai",
            Name: "Spence Private AI",
            Role: "Autonomous Agent",
            Status: "offline",
            SessionCount: 1,
            LastHeartbeatAt: DateTimeOffset.UtcNow,
            CapabilitiesJson: JsonSerializer.Serialize(new
            {
                adapter = "openai",
                endpoint = "http://127.0.0.1:11435/v1",
                apiKey = "sk-test-key",
                defaultModel = "qwen3.8-flash-next"
            })
        );

        configStore.PopulateFromRemote(preset, remote);

        preset.Name.Should().Be("Spence Private AI");
        preset.Role.Should().Be("Autonomous Agent");
        preset.AdapterType.Should().Be("openai");
        preset.Endpoint.Should().Be("http://127.0.0.1:11435/v1");
        preset.ApiKey.Should().Be("sk-test-key");
        preset.DefaultModel.Should().Be("qwen3.8-flash-next");
        preset.Capabilities.Should().Contain("run_command");
    }

    [Fact]
    public void MeshClient_SerializePresetCapabilities_SerializesOpenAiMetadata()
    {
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence Private AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            ApiKey = "sk-key",
            DefaultModel = "qwen3.8-flash-next",
            Capabilities = new List<string> { "run_command", "read_file" }
        };

        var json = MeshClient.SerializePresetCapabilities(preset);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("adapter").GetString().Should().Be("openai");
        doc.RootElement.GetProperty("endpoint").GetString().Should().Be("http://127.0.0.1:11435/v1");
        doc.RootElement.GetProperty("apiKey").GetString().Should().Be("sk-key");
        doc.RootElement.GetProperty("defaultModel").GetString().Should().Be("qwen3.8-flash-next");
    }

    [Fact]
    public void ResolveTimeoutSeconds_UsesPresetOrEnvOrDefault()
    {
        var presetWithTimeout = new AgentPresetConfig { TimeoutSeconds = 900 };
        OpenAiAdapter.ResolveTimeoutSeconds(presetWithTimeout).Should().Be(900);

        var presetDefault = new AgentPresetConfig();
        OpenAiAdapter.ResolveTimeoutSeconds(presetDefault).Should().Be(600); // 10 minutes default
    }

    [Fact]
    public void ResolveModelContextLimit_ResolvesKnownModelsAndOptions()
    {
        OpenAiAdapter.ResolveModelContextLimit("qwen3.8-flash-next", null).Should().Be(524_288);
        OpenAiAdapter.ResolveModelContextLimit("llama3.3", null).Should().Be(131_072);
        OpenAiAdapter.ResolveModelContextLimit("qwen2.5-coder", null).Should().Be(131_072);
        OpenAiAdapter.ResolveModelContextLimit("custom", "256k").Should().Be(262_144);
        OpenAiAdapter.ResolveModelContextLimit("custom", "1m").Should().Be(1_048_576);
        OpenAiAdapter.ResolveModelContextLimit("unknown", "default").Should().Be(131_072);
    }

    [Fact]
    public void ParseContextWindowError_ParsesLiteLlmAndOpenAiFormats()
    {
        var litellmError = "{\"error\":{\"message\":\"litellm.BadRequestError: OpenAIException - request exceeds the context window: prompt (548337 tokens) leaves no room to answer in the context (524288); requests are never truncated. Received Model Group=qwen3.8-flash-next\\nAvailable Model Group Fallbacks=None\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":\"400\"}}";
        var (isLiteLlm, promptTokens, contextLimit) = OpenAiAdapter.ParseContextWindowError(litellmError);
        isLiteLlm.Should().BeTrue();
        promptTokens.Should().Be(548_337);
        contextLimit.Should().Be(524_288);

        var openAiError = "maximum context length is 131072 tokens. However, your messages resulted in 140000 tokens.";
        var (isOpenAi, pt2, cl2) = OpenAiAdapter.ParseContextWindowError(openAiError);
        isOpenAi.Should().BeTrue();
        pt2.Should().Be(140_000);
        cl2.Should().Be(131_072);

        var normalError = "Invalid model identifier";
        var (isNormal, _, _) = OpenAiAdapter.ParseContextWindowError(normalError);
        isNormal.Should().BeFalse();
    }

    [Fact]
    public void CompactHistory_TruncatesOlderToolOutputsAndPrunesEarlierTurns()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "You are an autonomous engineer." },
            new() { Role = "user", Content = "Please inspect files." },
            new() { Role = "assistant", Content = "I will read the big file.", ToolCalls = new List<OpenAiToolCall> { new() { Id = "call_1", Function = new() { Name = "read_file" } } } },
            new() { Role = "tool", ToolCallId = "call_1", Content = new string('A', 5000) },
            new() { Role = "assistant", Content = "I read it, now editing." },
            new() { Role = "user", Content = "Continue your work." },
            new() { Role = "assistant", Content = "Running final build.", ToolCalls = new List<OpenAiToolCall> { new() { Id = "call_2", Function = new() { Name = "run_command" } } } },
            new() { Role = "tool", ToolCallId = "call_2", Content = "Exit Code: 0" }
        };

        var initialTokens = OpenAiAdapter.EstimateTotalTokens(history);
        initialTokens.Should().BeGreaterThan(1000);

        // Compact to 400 tokens
        var compacted = OpenAiAdapter.CompactHistory(history, 400);
        compacted.Should().BeTrue();

        // System prompt preserved
        history[0].Role.Should().Be("system");
        history[0].Content.Should().Contain("autonomous engineer");

        // Older tool output truncated
        var compactedTokens = OpenAiAdapter.EstimateTotalTokens(history);
        compactedTokens.Should().BeLessThan(initialTokens);
    }

    [Fact]
    public void TruncateToolResult_CapsLargeResultsAtHeadAndTail()
    {
        var small = "Small output";
        OpenAiAdapter.TruncateToolResult(small, 50_000).Should().Be(small);

        var large = new string('X', 60_000);
        var truncated = OpenAiAdapter.TruncateToolResult(large, 50_000);
        truncated.Length.Should().BeLessThan(large.Length);
        truncated.Should().Contain("Output truncated");
        truncated.Should().StartWith(new string('X', 100));
        truncated.Should().EndWith(new string('X', 100));
    }

    [Fact]
    public void IsRetryableStatusCode_IdentifiesTransientErrors()
    {
        OpenAiAdapter.IsRetryableStatusCode(429).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(500).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(502).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(503).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(504).Should().BeTrue();

        OpenAiAdapter.IsRetryableStatusCode(400).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(401).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(403).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(404).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(200).Should().BeFalse();
    }

    [Fact]
    public async Task ExecutePromptAsync_WithTransient503_RetriesAndSucceeds()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence Private AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            DefaultModel = "qwen3.8-flash-next",
            MaxRetries = 2,
            RetryDelayMilliseconds = 1
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "spence-ai",
            Prompt = "Hello after retry",
            Sender = "operator"
        };

        var attempt = 0;
        using var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            attempt++;
            if (attempt == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    ReasonPhrase = "Service Unavailable",
                    Content = new StringContent("Model loading into VRAM...")
                };
            }

            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello back!\"}}]}\n\ndata: [DONE]\n\n";
            var res = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
            return res;
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            attempt.Should().Be(2);
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("HTTP 503"));
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Hello back!"));
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WithContextLimit400_CompactsHistoryAndRetries()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence Private AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            DefaultModel = "qwen3.8-flash-next",
            MaxRetries = 2,
            RetryDelayMilliseconds = 1
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "spence-ai",
            Prompt = "Please help me",
            Sender = "operator"
        };

        var attempt = 0;
        using var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            attempt++;
            if (attempt == 1)
            {
                var errJson = "{\"error\":{\"message\":\"litellm.BadRequestError: OpenAIException - request exceeds the context window: prompt (548337 tokens) leaves no room to answer in the context (524288); requests are never truncated.\"}}";
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    ReasonPhrase = "Bad Request",
                    Content = new StringContent(errJson)
                };
            }

            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Context recovered!\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            attempt.Should().Be(2);
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Context window exceeded"));
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Context recovered!"));
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    private class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
