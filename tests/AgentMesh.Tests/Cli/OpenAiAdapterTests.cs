using System.Diagnostics;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

/// <remarks>
/// Shares the <c>OpenAiAdapterStaticSeams</c> collection with
/// <see cref="OpenAiAdapterMessageOrderingTests"/>. Both drive the adapter through its static test
/// seams — <c>SetHttpClientForTest</c>, <c>HistoryDirectoryPath</c> — which are process-wide. xUnit
/// parallelises distinct classes by default, so two classes swapping the same static under test would
/// intermittently send each other's requests to each other's stub endpoints.
/// </remarks>
[Collection("OpenAiAdapterStaticSeams")]
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
        tools.Should().HaveCount(9);

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
        toolNames.Should().Contain("manage_task");
        toolNames.Should().Contain("post_job_message");
        toolNames.Should().Contain("open_pull_request");
        toolNames.Should().Contain("save_memory");
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
            Id = "test-openai-driver",
            Name = "Agent test-openai-driver",
            AdapterType = "generic"
        };

        var remote = new RemoteAgentDto(
            Id: "test-openai-driver",
            Name: "Test OpenAI Driver",
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

        preset.Name.Should().Be("Test OpenAI Driver");
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
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
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
        AgentMesh.Core.Services.ModelContextLimits.ClearLearnedLimits();
        OpenAiAdapter.ResolveModelContextLimit("qwen3.8-flash-next", null).Should().Be(524_288);
        OpenAiAdapter.ResolveModelContextLimit("llama3.3", null).Should().Be(131_072);
        OpenAiAdapter.ResolveModelContextLimit("qwen2.5-coder", null).Should().Be(131_072);
        OpenAiAdapter.ResolveModelContextLimit("custom", "256k").Should().Be(262_144);
        OpenAiAdapter.ResolveModelContextLimit("custom", "512k").Should().Be(524_288);
        OpenAiAdapter.ResolveModelContextLimit("custom", "1m").Should().Be(1_048_576);
        OpenAiAdapter.ResolveModelContextLimit("unknown", "default").Should().Be(1_048_576);

        AgentMesh.Core.Services.ModelContextLimits.RecordLearnedLimit("unknown-custom-model", 262_144);
        OpenAiAdapter.ResolveModelContextLimit("unknown-custom-model", "default").Should().Be(262_144);
        AgentMesh.Core.Services.ModelContextLimits.ClearLearnedLimits();
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
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
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
            AgentId = "test-openai-driver",
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
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
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
            AgentId = "test-openai-driver",
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

    [Fact]
    public void ResolveMaxSteps_ReturnsPresetValueWhenConfigured()
    {
        var preset = new AgentPresetConfig { MaxSteps = 50 };
        OpenAiAdapter.ResolveMaxSteps(preset).Should().Be(50);
    }

    [Fact]
    public void ResolveMaxSteps_ReturnsDefaultWhenUnset()
    {
        var preset = new AgentPresetConfig();
        var origEnv = Environment.GetEnvironmentVariable("OPENAI_MAX_STEPS");
        var origMeshEnv = Environment.GetEnvironmentVariable("AGENTMESH_MAX_STEPS");
        var origAgentEnv = Environment.GetEnvironmentVariable("AGENT_MAX_STEPS");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", null);
            Environment.SetEnvironmentVariable("AGENTMESH_MAX_STEPS", null);
            Environment.SetEnvironmentVariable("AGENT_MAX_STEPS", null);
            OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false).Should().Be(50);
            OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true).Should().Be(50);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", origEnv);
            Environment.SetEnvironmentVariable("AGENTMESH_MAX_STEPS", origMeshEnv);
            Environment.SetEnvironmentVariable("AGENT_MAX_STEPS", origAgentEnv);
        }
    }

    [Fact]
    public void ResolveMaxSteps_ReturnsEnvVarWhenSet()
    {
        var preset = new AgentPresetConfig();
        var origEnv = Environment.GetEnvironmentVariable("OPENAI_MAX_STEPS");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", "75");
            OpenAiAdapter.ResolveMaxSteps(preset).Should().Be(75);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", origEnv);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenMaxStepsReached_EmitsThoughtAndSummaryResponse()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            MaxSteps = 2 // Low step limit for test
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "test-openai-driver",
            Prompt = "Run tasks",
            Sender = "operator"
        };

        var requestCount = 0;
        using var mockClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (requestCount <= 2)
            {
                // Return a tool call for each step to consume maxSteps
                var toolCallSse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_" + requestCount + "\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"echo step" + requestCount + "\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(toolCallSse, System.Text.Encoding.UTF8, "text/event-stream")
                };
            }

            // Summary request (stream = false)
            var summaryJson = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Completed 2 steps of tool execution. Ready to continue.\"}}]}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(summaryJson, System.Text.Encoding.UTF8, "application/json")
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

            // Verify that it executed 2 tool calls, then hit max steps
            events.Where(e => e.EventType == "tool_call").Should().HaveCount(2);
            // Must emit thought about reaching progress checkpoint
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Reached progress checkpoint (2 steps)"));
            // Must emit final response so session does NOT hang in thinking
            var finalResp = events.FirstOrDefault(e => e.EventType == "response");
            finalResp.Should().NotBeNull();
            finalResp!.Content.Should().Contain("Progress Checkpoint (2 steps)");
            finalResp.Content.Should().Contain("Completed 2 steps");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenFinalContentEmpty_EmitsNonEmptyFallbackResponse()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1"
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "test-openai-driver",
            Prompt = "Hello",
            Sender = "operator"
        };

        using var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            // Model returns empty content delta, only reasoning
            var sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"I am thinking deeply about this...\"}}]}\n\ndata: [DONE]\n\n";
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

            var resp = events.FirstOrDefault(e => e.EventType == "response");
            resp.Should().NotBeNull();
            resp!.Content.Should().NotBeNullOrWhiteSpace();
            resp.Content.Should().Be("I am thinking deeply about this...");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_ReadFile_OnImageFile_ReturnsImageIndicatorWithoutBinaryGarbage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_img_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "mock_ui.png");
            // Standard PNG header bytes with null bytes
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
            await File.WriteAllBytesAsync(imagePath, pngBytes);

            var args = JsonSerializer.Serialize(new { path = "mock_ui.png" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("read_file", args, tempDir, CancellationToken.None);

            result.Should().Contain("[Image file: 'mock_ui.png'");
            result.Should().Contain("format: PNG");
            result.Should().Contain("cannot be read as plain text");
            result.Should().Contain("Do NOT attempt to install OCR packages");
            result.Should().NotContain("\0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RethrowsOperationCanceledException()
    {
        // Was hardcoded to "/workspace", which only exists inside a persona
        // container running as root. On a CI runner (non-root, repo checked out
        // under /home/runner) that path neither exists nor can be created, so
        // the tool's Directory.CreateDirectory threw UnauthorizedAccessException
        // and the generic catch turned it into a "Tool execution error" *string*
        // instead of propagating cancellation. The test passed locally and failed
        // in CI. A temp dir keeps it hermetic.
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_cancel_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var args = JsonSerializer.Serialize(new { command = "sleep 10" });
            var act = async () => await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_ReadFile_OnBinaryFile_ReturnsBinaryIndicatorWithoutNullBytes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_bin_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var binPath = Path.Combine(tempDir, "binary.dat");
            var binBytes = new byte[] { 0x01, 0x02, 0x00, 0x03, 0x04, 0x00, 0x05 };
            await File.WriteAllBytesAsync(binPath, binBytes);

            var args = JsonSerializer.Serialize(new { path = "binary.dat" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("read_file", args, tempDir, CancellationToken.None);

            result.Should().Contain("[Binary file: 'binary.dat'");
            result.Should().Contain("cannot be read as plain text");
            result.Should().NotContain("\0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_ToolResultSanitizesNullBytes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_null_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Execute a bash echo with null byte
            var args = JsonSerializer.Serialize(new { command = "printf 'hello\\x00world\\n'" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, CancellationToken.None);

            result.Should().Contain("helloworld");
            result.Should().NotContain("\0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OpenAiMessage_ToSerializableObject_SerializesMultimodalContentParts()
    {
        var msg = new OpenAiMessage
        {
            Role = "user",
            Content = "Look at this screenshot",
            ContentParts = new List<object>
            {
                new OpenAiTextContentPart { Type = "text", Text = "Look at this screenshot" },
                new OpenAiImageUrlContentPart
                {
                    Type = "image_url",
                    ImageUrl = new OpenAiImageUrl { Url = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==" }
                }
            }
        };

        var serialized = JsonSerializer.Serialize(msg.ToSerializableObject());
        serialized.Should().Contain("\"role\":\"user\"");
        serialized.Should().Contain("\"type\":\"text\"");
        serialized.Should().Contain("\"type\":\"image_url\"");
        serialized.Should().Contain("\"url\":\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==\"");
    }

    [Fact]
    public async Task ExecutePromptAsync_WithImageAttachment_IncludesMultimodalImageContentPart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_mm_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var sessionId = Guid.NewGuid();
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"I see the image clearly.\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        });

        OpenAiAdapter.SetHttpClientForTest(new HttpClient(handler));
        try
        {
            var imagePath = Path.Combine(tempDir, "sample.png");
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
            await File.WriteAllBytesAsync(imagePath, pngBytes);

            var adapter = new OpenAiAdapter();
            var preset = new AgentPresetConfig
            {
                Id = "test-openai-driver",
                Name = "Test OpenAI Driver",
                AdapterType = "openai",
                Endpoint = "http://127.0.0.1:11435/v1",
                DefaultModel = "qwen3.8-flash-next"
            };

            var promptEv = new PromptReceivedEvent
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid(),
                AgentId = "test-openai-driver",
                Prompt = "What is in this image?",
                Sender = "operator",
                WorkingDirectory = tempDir,
                Attachments = new List<AttachmentItem>
                {
                    new AttachmentItem
                    {
                        Id = "att1",
                        FileName = "sample.png",
                        ContentType = "image/png",
                        SizeBytes = pngBytes.Length,
                        LocalFilePath = imagePath
                    }
                }
            };

            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev => { events.Add(ev); return Task.CompletedTask; }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().Contain("\"type\":\"image_url\"");
            capturedBody.Should().Contain("data:image/png;base64,");
            events.Should().Contain(e => e.Content.Contains("I see the image clearly."));
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OpenAiAdapter_HistoryDiskPersistence_RoundTripsAcrossRestarts()
    {
        var sessionId = Guid.NewGuid();
        var tempHistoryDir = Path.Combine(Path.GetTempPath(), $"openai-hist-{Guid.NewGuid()}");
        var origDir = OpenAiAdapter.HistoryDirectoryPath;
        OpenAiAdapter.HistoryDirectoryPath = tempHistoryDir;

        try
        {
            var initialHistory = new List<OpenAiMessage>
            {
                new OpenAiMessage { Role = "system", Content = "You are a senior tech lead." },
                new OpenAiMessage { Role = "user", Content = "Inspect the codebase." },
                new OpenAiMessage { Role = "assistant", Content = "Inspected." }
            };

            OpenAiAdapter.SaveHistoryToDisk(sessionId, initialHistory);

            // Simulate full memory wipe / CLI process restart
            OpenAiAdapter.ClearSessionHistory(sessionId);

            // Re-save to disk for restoration test
            OpenAiAdapter.SaveHistoryToDisk(sessionId, initialHistory);

            var restored = OpenAiAdapter.GetSessionHistory(sessionId);
            restored.Should().NotBeNull();
            restored.Should().HaveCount(3);
            restored![0].Content.Should().Be("You are a senior tech lead.");
            restored[1].Content.Should().Be("Inspect the codebase.");
            restored[2].Content.Should().Be("Inspected.");
        }
        finally
        {
            OpenAiAdapter.ClearSessionHistory(sessionId);
            OpenAiAdapter.HistoryDirectoryPath = origDir;
            if (Directory.Exists(tempHistoryDir)) Directory.Delete(tempHistoryDir, true);
        }
    }

    [Fact]
    public void ActiveCommandRegistry_AppendAndGetLiveOutput_TracksRunningOutput()
    {
        var sessionId = Guid.NewGuid();
        var commandId = "cmd-live-test";
        using var proc = new Process();
        using var cts = new CancellationTokenSource();

        using var reg = ActiveCommandRegistry.Register(sessionId, commandId, proc, cts);

        ActiveCommandRegistry.AppendOutput(sessionId, commandId, "Compiling project...\n");
        ActiveCommandRegistry.AppendOutput(sessionId, commandId, "Build succeeded.\n");

        var liveOutput = ActiveCommandRegistry.GetLiveOutput(sessionId, commandId);
        liveOutput.Should().NotBeNull();
        liveOutput.Should().Contain("Compiling project...");
        liveOutput.Should().Contain("Build succeeded.");
    }

    [Fact]
    public void ResolveMaxSteps_HonorsPresetExplicitValue_OverDefaultsAndDiscovery()
    {
        var preset = new AgentPresetConfig { MaxSteps = 42 };
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true).Should().Be(42);
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false).Should().Be(42);
    }

    [Fact]
    public void ResolveMaxSteps_HonorsGlobalDefault_WhenPresetUnset()
    {
        var preset = new AgentPresetConfig { MaxSteps = null };
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false, globalDefault: 35).Should().Be(35);
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true, globalDefault: 35).Should().Be(35);
    }

    [Fact]
    public void ResolveMaxSteps_FallsBackTo50_RegardlessOfPhase()
    {
        var preset = new AgentPresetConfig { MaxSteps = null };
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true).Should().Be(50);
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false).Should().Be(50);
    }

    [Fact]
    public void GetToolDefinitions_MasksMutatingTools_DuringDiscovery()
    {
        var discoveryTools = OpenAiAdapter.GetToolDefinitions(isDiscoveryPhase: true);
        var discoveryNames = discoveryTools
            .Select(t => ((dynamic)t).function.name as string)
            .ToList();

        discoveryNames.Should().Contain("run_command");
        discoveryNames.Should().Contain("read_file");
        discoveryNames.Should().Contain("ask_question");
        discoveryNames.Should().Contain("manage_task");
        discoveryNames.Should().Contain("post_job_message");
        discoveryNames.Should().Contain("save_memory");

        discoveryNames.Should().NotContain("write_file", "write_file must be hidden during discovery");
        discoveryNames.Should().NotContain("edit_file", "edit_file must be hidden during discovery");
        discoveryNames.Should().NotContain("open_pull_request", "open_pull_request must be hidden during discovery");

        var executionTools = OpenAiAdapter.GetToolDefinitions(isDiscoveryPhase: false);
        var executionNames = executionTools
            .Select(t => ((dynamic)t).function.name as string)
            .ToList();

        executionNames.Should().Contain("write_file");
        executionNames.Should().Contain("edit_file");
        executionNames.Should().Contain("open_pull_request");
    }

    [Theory]
    [InlineData("git commit -m \"fix\"", true)]
    [InlineData("git push origin main", true)]
    [InlineData("gh pr create --title \"PR\"", true)]
    [InlineData("git rebase main", true)]
    [InlineData("git merge feature", true)]
    [InlineData("git cherry-pick 12345", true)]
    [InlineData("git status", false)]
    [InlineData("git log -n 5", false)]
    [InlineData("git diff", false)]
    [InlineData("git branch -a", false)]
    [InlineData("dotnet test", false)]
    public void IsMutatingGitCommand_CorrectlyIdentifiesMutations(string cmd, bool expectedMutating)
    {
        OpenAiAdapter.IsMutatingGitCommand(cmd).Should().Be(expectedMutating);
    }

    [Theory]
    [InlineData("read_file", "{\"path\":\"a.cs\"}", true)]
    [InlineData("ask_question", "{}", true)]
    [InlineData("save_memory", "{}", true)]
    [InlineData("run_command", "{\"command\":\"git status\"}", true)]
    [InlineData("run_command", "{\"command\":\"git log -n 5\"}", true)]
    [InlineData("run_command", "{\"command\":\"git diff\"}", true)]
    [InlineData("write_file", "{\"path\":\"a.cs\"}", false)]
    [InlineData("edit_file", "{\"path\":\"a.cs\"}", false)]
    [InlineData("run_command", "{\"command\":\"dotnet build\"}", false)]
    [InlineData("manage_task", "{\"action\":\"set_checklist\"}", false)]
    public void IsReadOnlyTool_CategorizesOperationsAccurately(string toolName, string argsJson, bool expectedReadOnly)
    {
        OpenAiAdapter.IsReadOnlyTool(toolName, argsJson).Should().Be(expectedReadOnly);
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RejectsMutations_DuringDiscovery()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mesh-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var writeFileRes = await OpenAiAdapter.ExecuteToolLocallyAsync(
                "write_file",
                "{\"path\":\"test.txt\",\"content\":\"hello\"}",
                tempDir,
                CancellationToken.None,
                isDiscoveryPhase: true);

            writeFileRes.Should().Contain("File modifications are forbidden during Stage 1 Discovery");

            var gitCommitRes = await OpenAiAdapter.ExecuteToolLocallyAsync(
                "run_command",
                "{\"command\":\"git commit -m 'early'\"}",
                tempDir,
                CancellationToken.None,
                isDiscoveryPhase: true);

            gitCommitRes.Should().Contain("forbidden during Stage 1 Discovery");

            var ghPrRes = await OpenAiAdapter.ExecuteToolLocallyAsync(
                "run_command",
                "{\"command\":\"gh pr create --title 'early'\"}",
                tempDir,
                CancellationToken.None,
                isDiscoveryPhase: true);

            ghPrRes.Should().Contain("forbidden during Stage 1 Discovery");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("Error: file not found", true)]
    [InlineData("Error writing file", true)]
    [InlineData("Tool execution error: command exited with code 1", true)]
    [InlineData("An error occurred invoking 'open_pull_request'.", true)]
    [InlineData("""{"success":false,"error":"missing_argument"}""", true)]
    [InlineData("""{"isError":true,"content":[{"text":"failed"}]}""", true)]
    [InlineData("""{"error":"unauthorized"}""", true)]
    [InlineData("PR #4242 opened: https://github.com/ckchessmaster/agent-mesh/pull/4242", false)]
    [InlineData("""{"success":true,"pr_number":4242}""", false)]
    [InlineData("Successfully wrote 120 bytes to test.txt", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void IsToolResultError_DetectsFailuresCorrectly(string? output, bool expectedIsError)
    {
        var isError = OpenAiAdapter.IsToolResultError(output);
        isError.Should().Be(expectedIsError);
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_WriteToFileAlias_And_CaseInsensitiveArgs_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_alias_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var args = JsonSerializer.Serialize(new
            {
                TargetFile = "aliased_output.txt",
                CodeContent = "Hello from write_to_file tool alias"
            });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("write_to_file", args, tempDir, CancellationToken.None);
            res.Should().Contain("Successfully wrote");

            var written = await File.ReadAllTextAsync(Path.Combine(tempDir, "aliased_output.txt"));
            written.Should().Be("Hello from write_to_file tool alias");

            // Now test replace_file_content alias
            var editArgs = JsonSerializer.Serialize(new
            {
                TargetFile = "aliased_output.txt",
                TargetContent = "Hello from write_to_file",
                ReplacementContent = "Replaced in replace_file_content"
            });
            var editRes = await OpenAiAdapter.ExecuteToolLocallyAsync("replace_file_content", editArgs, tempDir, CancellationToken.None);
            editRes.Should().Contain("Successfully replaced");

            var updated = await File.ReadAllTextAsync(Path.Combine(tempDir, "aliased_output.txt"));
            updated.Should().Be("Replaced in replace_file_content tool alias");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_AskQuestion_ReturnsNotedResponse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_ask_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var args = JsonSerializer.Serialize(new
            {
                question = "Should we enable strict linting?",
                options = new[] { "Yes", "No" }
            });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("ask_question", args, tempDir, CancellationToken.None);
            res.Should().Contain("Question submitted to operator");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OpenAiAdapter_GetToolDefinitions_OpenPullRequestSchema_ContainsSummary3RAndBranchName()
    {
        var tools = OpenAiAdapter.GetToolDefinitions();
        var prTool = tools.FirstOrDefault(t =>
        {
            var json = JsonSerializer.Serialize(t);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("function").GetProperty("name").GetString() == "open_pull_request";
        });

        prTool.Should().NotBeNull();
        var prJson = JsonSerializer.Serialize(prTool);
        using var prDoc = JsonDocument.Parse(prJson);
        var props = prDoc.RootElement.GetProperty("function").GetProperty("parameters").GetProperty("properties");

        props.TryGetProperty("summary_3r", out var s3r).Should().BeTrue();
        props.TryGetProperty("branch_name", out var bn).Should().BeTrue();
        props.TryGetProperty("title", out var t).Should().BeTrue();
    }

    [Fact]
    public void OpenAiAdapter_GetToolDefinitions_SaveMemorySchema_ContainsValueAndCanonicalCategories()
    {
        var tools = OpenAiAdapter.GetToolDefinitions();
        var memTool = tools.FirstOrDefault(t =>
        {
            var json = JsonSerializer.Serialize(t);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("function").GetProperty("name").GetString() == "save_memory";
        });

        memTool.Should().NotBeNull();
        var memJson = JsonSerializer.Serialize(memTool);
        using var memDoc = JsonDocument.Parse(memJson);
        var props = memDoc.RootElement.GetProperty("function").GetProperty("parameters").GetProperty("properties");

        props.TryGetProperty("value", out var val).Should().BeTrue();
        props.TryGetProperty("key", out var k).Should().BeTrue();
        props.TryGetProperty("category", out var cat).Should().BeTrue();
        props.TryGetProperty("is_global", out var ig).Should().BeTrue();

        var catEnums = cat.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        catEnums.Should().Contain(AgentMemoryCategories.Environment);
        catEnums.Should().Contain(AgentMemoryCategories.BuildAndTest);
        catEnums.Should().Contain(AgentMemoryCategories.CodebaseGotcha);
    }

    [Theory]
    [InlineData("""{"requires_reply": true}""", true)]
    [InlineData("""{"requires_reply": "true"}""", true)]
    [InlineData("""{"requires_reply": "True"}""", true)]
    [InlineData("""{"requiresReply": true}""", true)]
    [InlineData("""{"requiresReply": "true"}""", true)]
    [InlineData("""{"requires_reply": false}""", false)]
    [InlineData("""{"requires_reply": "false"}""", false)]
    [InlineData("""{"content": "hello"}""", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRequiresReply_ParsesBooleanAndStringValues(string? toolArgs, bool expected)
    {
        OpenAiAdapter.IsRequiresReply(toolArgs).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"action": "set_checklist"}""", true)]
    [InlineData("""{"Action": "set_checklist"}""", true)]
    [InlineData("""{"operation": "SET_CHECKLIST"}""", true)]
    [InlineData("""{"command": "set_checklist"}""", true)]
    [InlineData("""{"action": "update_task_status"}""", false)]
    [InlineData("""{"action": "get_checklist"}""", false)]
    [InlineData("{}", false)]
    [InlineData(null, false)]
    public void IsSetChecklistAction_DetectsSetChecklistInVariousKeysAndCasings(string? toolArgs, bool expected)
    {
        OpenAiAdapter.IsSetChecklistAction(toolArgs).Should().Be(expected);
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenProxyReturnsDirectJsonResponse_ParsesContentAndUsage()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "qwen3.8-flash-next"
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            Prompt = "In agent-mesh is there an easy way to increase the size of the PVC?",
            Model = "qwen3.8-flash-next"
        };

        var directJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "Yes, you can configure personas.pvc.size in helm values."
                    }
                }
            ],
            "usage": {
                "prompt_tokens": 40,
                "completion_tokens": 15,
                "total_tokens": 55
            }
        }
        """;

        var mockClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(directJson, System.Text.Encoding.UTF8, "application/json")
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

            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Yes, you can configure personas.pvc.size in helm values."));
            events.Should().NotContain(e => e.EventType == "response" && e.Content == "Turn completed.");

            var resp = events.First(e => e.EventType == "response");
            resp.StructuredPayloadJson.Should().NotBeNull();
            resp.StructuredPayloadJson.Should().Contain("\"totalTokens\":55");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_SendsStreamOptionsWithIncludeUsage()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "qwen3.8-flash-next"
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            Prompt = "Hello",
            Model = "qwen3.8-flash-next"
        };

        string? capturedBody = null;
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n";

        var mockClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            await adapter.ExecutePromptAsync(preset, promptEv, _ => Task.CompletedTask, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            using var doc = JsonDocument.Parse(capturedBody!);
            doc.RootElement.TryGetProperty("stream_options", out var streamOpts).Should().BeTrue();
            streamOpts.GetProperty("include_usage").GetBoolean().Should().BeTrue();
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenToolBatchHasSetChecklistAndPostJobMessage_ExecutesBothAndParksTurn()
    {
        var executedTools = new List<string>();
        OpenAiAdapter.PlatformToolExecutor = (toolName, args, sId, pId, aId, ct) =>
        {
            executedTools.Add(toolName);
            return Task.FromResult("{\"success\":true}");
        };

        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-agent",
            Name = "Test Agent",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "test-model"
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            Prompt = "Run discovery",
            Model = "test-model"
        };

        var directJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                            {
                                "id": "call_1",
                                "type": "function",
                                "function": {
                                    "name": "manage_task",
                                    "arguments": "{\"action\":\"set_checklist\",\"tasks\":[{\"id\":\"t1\",\"title\":\"Task 1\"}]}"
                                }
                            },
                            {
                                "id": "call_2",
                                "type": "function",
                                "function": {
                                    "name": "post_job_message",
                                    "arguments": "{\"message\":\"Clarification question: table vs metadata?\",\"requires_reply\":true}"
                                }
                            }
                        ]
                    }
                }
            ]
        }
        """;

        var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(directJson, System.Text.Encoding.UTF8, "application/json")
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

            executedTools.Should().Contain("manage_task");
            executedTools.Should().Contain("post_job_message");
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Turn paused: plan / question posted to Job Chat awaiting reply."));

            var history = OpenAiAdapter.GetSessionHistory(sessionId)!;
            history.Should().Contain(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count == 2);
            history.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
            history.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_2");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.PlatformToolExecutor = null;
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenToolBatchHasOnlySetChecklist_ConcludesTurnAwaitingPlanApproval()
    {
        var executedTools = new List<string>();
        OpenAiAdapter.PlatformToolExecutor = (toolName, args, sId, pId, aId, ct) =>
        {
            executedTools.Add(toolName);
            return Task.FromResult("{\"success\":true}");
        };

        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-agent",
            Name = "Test Agent",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "test-model"
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            Prompt = "Run discovery",
            Model = "test-model"
        };

        var directJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                            {
                                "id": "call_1",
                                "type": "function",
                                "function": {
                                    "name": "manage_task",
                                    "arguments": "{\"action\":\"set_checklist\",\"tasks\":[{\"id\":\"t1\",\"title\":\"Task 1\"}]}"
                                }
                            }
                        ]
                    }
                }
            ]
        }
        """;

        var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(directJson, System.Text.Encoding.UTF8, "application/json")
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

            executedTools.Should().ContainSingle().Which.Should().Be("manage_task");
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Turn concluded: Living task checklist registered. Awaiting Stage 2 Plan Approval."));

            var history = OpenAiAdapter.GetSessionHistory(sessionId)!;
            history.Should().Contain(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count == 1);
            history.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.PlatformToolExecutor = null;
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
