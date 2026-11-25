using System.Net;
using System.Text;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

[CollectionDefinition("OpenRouterAdapterStaticSeams", DisableParallelization = true)]
public class OpenRouterAdapterStaticSeamsCollection { }

[Collection("OpenRouterAdapterStaticSeams")]
public class OpenRouterAdapterTests : IDisposable
{
    private readonly string _originalHistoryDir;
    private readonly Func<string, string?, CancellationToken, Task<(int StatusCode, string Body, string? Error)>> _originalModelsReader;

    public OpenRouterAdapterTests()
    {
        ModelContextLimits.ClearLearnedLimits();
        _originalHistoryDir = OpenRouterAdapter.HistoryDirectoryPath;
        _originalModelsReader = DriverCapabilityDescriber.ModelsEndpointReader;
    }

    public void Dispose()
    {
        OpenRouterAdapter.ResetHttpClientForTest();
        OpenRouterAdapter.HistoryDirectoryPath = _originalHistoryDir;
        DriverCapabilityDescriber.ModelsEndpointReader = _originalModelsReader;
        ModelContextLimits.ClearLearnedLimits();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int _attempt;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _attempt);
            return responder(request, current);
        }
    }

    private static AgentPresetConfig CreatePreset(OpenRouterConfig? openRouter = null, string? apiKey = "sk-or-test-key") => new()
    {
        Id = "openrouter",
        Name = "OpenRouter",
        AdapterType = "openrouter",
        Endpoint = OpenRouterConfig.DefaultEndpoint,
        ApiKey = apiKey,
        DefaultModel = "anthropic/claude-sonnet-4",
        MaxRetries = 2,
        RetryDelayMilliseconds = 1,
        OpenRouter = openRouter
    };

    [Fact]
    public void CoreIdentityAndEfforts_RecognizeOpenRouterAndSevenEffortLevels()
    {
        DriverAdapters.Canonicalize("openrouter").Should().Be(DriverAdapters.OpenRouter);
        DriverAdapters.Canonicalize("open-router").Should().Be(DriverAdapters.OpenRouter);

        DriverAdapters.InferFromDriverId("my-gateway", "https://openrouter.ai/api/v1", RuntimeType.CliAgy)
            .Should().Be(DriverAdapters.OpenRouter);
        DriverAdapters.InferFromDriverId("custom-id", null, RuntimeType.OpenRouter)
            .Should().Be(DriverAdapters.OpenRouter);
        DriverAdapters.InferFromDriverId("openrouter", null, RuntimeType.CliAgy)
            .Should().Be(DriverAdapters.OpenRouter);

        DriverEffortContracts.For(DriverAdapters.OpenRouter).Should().Equal(
            "none", "minimal", "low", "medium", "high", "xhigh", "max");
    }

    [Fact]
    public void ParseOpenRouterModels_FiltersToolsAndText_MapsPricingAndReasoningEfforts_PrependsAuto()
    {
        const string json = """
        {
          "data": [
            {
              "id": "anthropic/claude-sonnet-4",
              "name": "Claude Sonnet 4",
              "context_length": 200000,
              "supported_parameters": ["tools", "reasoning"],
              "architecture": { "output_modalities": ["text"] },
              "pricing": { "prompt": "0.000003", "completion": "0.000015" }
            },
            {
              "id": "deepseek/deepseek-chat-free",
              "name": "DeepSeek Chat Free",
              "context_length": 131072,
              "supported_parameters": ["tools"],
              "architecture": { "modality": "text->text" },
              "pricing": { "prompt": "0", "completion": "0" }
            },
            {
              "id": "deepseek/deepseek-v4.1-flash-1017:batch",
              "name": "DeepSeek Batch Only",
              "context_length": 131072,
              "supported_parameters": ["tools"],
              "architecture": { "output_modalities": ["text"] },
              "pricing": { "prompt": "0.000001", "completion": "0.000002" }
            },
            {
              "id": "openai/gpt-image-1",
              "name": "Image Only",
              "context_length": 32000,
              "supported_parameters": ["tools"],
              "architecture": { "output_modalities": ["image"] },
              "pricing": { "prompt": "0.00001", "completion": "0.00002" }
            },
            {
              "id": "legacy/no-tools",
              "name": "No Tools Model",
              "context_length": 8192,
              "supported_parameters": ["temperature"],
              "architecture": { "output_modalities": ["text"] },
              "pricing": { "prompt": "-1", "completion": "-1" }
            }
          ]
        }
        """;

        var parsed = DriverModelDiscovery.ParseOpenRouterModels(json);

        parsed.Select(m => m.Id).Should().Equal(
            "openrouter/auto",
            "anthropic/claude-sonnet-4",
            "deepseek/deepseek-chat-free");

        var claude = parsed.Single(m => m.Id == "anthropic/claude-sonnet-4");
        claude.MaxContextTokens.Should().Be(200_000);
        claude.Efforts.Should().Equal("none", "minimal", "low", "medium", "high", "xhigh", "max");
        claude.PromptPricePerMillion.Should().BeApproximately(3.0, 0.0001);
        claude.CompletionPricePerMillion.Should().BeApproximately(15.0, 0.0001);

        var freeModel = parsed.Single(m => m.Id == "deepseek/deepseek-chat-free");
        freeModel.Efforts.Should().BeEmpty();
        freeModel.PromptPricePerMillion.Should().Be(0);
        freeModel.CompletionPricePerMillion.Should().Be(0);

        ModelContextLimits.GetLearnedLimit("anthropic/claude-sonnet-4").Should().Be(200_000);
    }

    [Fact]
    public void ConfigStore_SeedsBuiltInOpenRouterPreset_AndAdapterFactoryCreatesOpenRouterAdapter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_cfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfgPath = Path.Combine(tempDir, "config.json");
            var store = new ConfigStore(cfgPath);
            var cfg = store.Load();

            cfg.Agents.Should().ContainKey("openrouter");
            var preset = cfg.Agents["openrouter"];
            preset.AdapterType.Should().Be("openrouter");
            preset.Endpoint.Should().Be(OpenRouterConfig.DefaultEndpoint);
            preset.DefaultModel.Should().Be("openrouter/auto");

            var adapter = AgentAdapterFactory.Create(preset);
            adapter.Should().BeOfType<OpenRouterAdapter>();
            adapter.AdapterType.Should().Be("openrouter");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DriverCapabilityDescriber_DescribeOpenRouterAsync_PreservesContextPricingAndEfforts()
    {
        DriverCapabilityDescriber.ModelsEndpointReader = (url, apiKey, _) =>
        {
            url.Should().Be("https://openrouter.ai/api/v1/models");
            apiKey.Should().Be("sk-or-test-key");
            return Task.FromResult((200, """
            {
              "data": [
                {
                  "id": "google/gemini-2.5-pro",
                  "name": "Gemini 2.5 Pro",
                  "context_length": 1000000,
                  "supported_parameters": ["tools", "reasoning"],
                  "architecture": { "output_modalities": ["text"] },
                  "pricing": { "prompt": "0.00000125", "completion": "0.00001" }
                }
              ]
            }
            """, (string?)null));
        };

        var declaration = await DriverCapabilityDescriber.DescribeOpenRouterAsync(
            CreatePreset(), "sk-or-test-key", CancellationToken.None);

        declaration.Adapter.Should().Be("openrouter");
        declaration.SupportsContextSelection.Should().BeTrue();
        declaration.Models.Should().Contain(m => m.Id == "openrouter/auto");
        var gemini = declaration.Models.Single(m => m.Id == "google/gemini-2.5-pro");
        gemini.MaxContextTokens.Should().Be(1_000_000);
        gemini.PromptPricePerMillion.Should().BeApproximately(1.25, 0.001);
        gemini.CompletionPricePerMillion.Should().BeApproximately(10.0, 0.001);
        gemini.Efforts.Should().Equal("none", "minimal", "low", "medium", "high", "xhigh", "max");
    }

    [Fact]
    public async Task ExecutePromptAsync_SendsOpenRouterHeadersCacheControlRoutingAndCapturesCostAndCachedUsage()
    {
        var tempHistory = Path.Combine(Path.GetTempPath(), "or_hist_" + Guid.NewGuid().ToString("N")[..8]);
        OpenRouterAdapter.HistoryDirectoryPath = tempHistory;
        var sessionId = Guid.NewGuid();
        string? capturedAuth = null;
        string? capturedTitle = null;
        string? capturedReferer = null;
        string? capturedSessionHeader = null;
        string? capturedBody = null;

        try
        {
            var preset = CreatePreset(new OpenRouterConfig
            {
                HttpReferer = "https://agentmesh.example",
                AppTitle = "AgentMesh-Test",
                FallbackModels = new List<string> { "openai/gpt-4.1" },
                Provider = new OpenRouterProviderRouting
                {
                    Sort = "throughput",
                    Order = new List<string> { "anthropic", "bedrock" },
                    AllowFallbacks = false,
                    DataCollection = "deny",
                    Zdr = true,
                    RequireParameters = true
                }
            });

            using var mockClient = new HttpClient(new StubHttpMessageHandler(async (req, _) =>
            {
                capturedAuth = req.Headers.Authorization?.ToString();
                capturedTitle = req.Headers.TryGetValues("X-Title", out var tv) ? tv.FirstOrDefault() : null;
                capturedReferer = req.Headers.TryGetValues("HTTP-Referer", out var rv) ? rv.FirstOrDefault() : null;
                capturedSessionHeader = req.Headers.TryGetValues("x-session-id", out var sv) ? sv.FirstOrDefault() : null;
                capturedBody = await req.Content!.ReadAsStringAsync();

                var sse = string.Join("\n", new[]
                {
                    ": OPENROUTER PROCESSING",
                    "",
                    "data: {\"choices\":[{\"delta\":{\"reasoning\":\"Thinking step 1\",\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"Thinking step 1\"}]}}]}",
                    "",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Final OpenRouter answer\"}}]}",
                    "",
                    "data: {\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":300,\"total_tokens\":1500,\"cost\":0.00425,\"prompt_tokens_details\":{\"cached_tokens\":800},\"completion_tokens_details\":{\"reasoning_tokens\":120}}}",
                    "",
                    "data: [DONE]",
                    ""
                });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }));
            OpenRouterAdapter.SetHttpClientForTest(mockClient);

            var events = new List<TrajectoryEventPayload>();
            var adapter = new OpenRouterAdapter();
            await adapter.ExecutePromptAsync(preset, new PromptReceivedEvent
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid(),
                AgentId = "openrouter",
                Prompt = "Summarize architecture",
                Effort = "high",
                Sender = "operator"
            }, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            capturedAuth.Should().Be("Bearer sk-or-test-key");
            capturedTitle.Should().Be("AgentMesh-Test");
            capturedReferer.Should().Be("https://agentmesh.example");
            capturedSessionHeader.Should().Be($"session:{sessionId}");

            using var bodyDoc = JsonDocument.Parse(capturedBody!);
            var root = bodyDoc.RootElement;
            root.GetProperty("session_id").GetString().Should().Be($"session:{sessionId}");
            root.GetProperty("usage").GetProperty("include").GetBoolean().Should().BeTrue();
            root.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("high");
            root.GetProperty("models").EnumerateArray().Select(e => e.GetString())
                .Should().Equal("anthropic/claude-sonnet-4", "openai/gpt-4.1");

            var providerJson = root.GetProperty("provider");
            providerJson.GetProperty("sort").GetString().Should().Be("throughput");
            providerJson.GetProperty("allow_fallbacks").GetBoolean().Should().BeFalse();
            providerJson.GetProperty("data_collection").GetString().Should().Be("deny");
            providerJson.GetProperty("zdr").GetBoolean().Should().BeTrue();
            providerJson.GetProperty("require_parameters").GetBoolean().Should().BeTrue();

            var sysMsg = root.GetProperty("messages")[0];
            sysMsg.GetProperty("role").GetString().Should().Be("system");
            sysMsg.GetProperty("content").ValueKind.Should().Be(JsonValueKind.Array);
            sysMsg.GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");

            events.Count(e => e.EventType == "thought" && e.Content == "Thinking step 1")
                .Should().Be(1, "identical reasoning and reasoning_details should not double-stream");

            var respEvent = events.Single(e => e.EventType == "response");
            respEvent.Content.Should().Contain("Final OpenRouter answer");
            respEvent.StructuredPayloadJson.Should().NotBeNullOrWhiteSpace();
            using var metaDoc = JsonDocument.Parse(respEvent.StructuredPayloadJson!);
            var usage = metaDoc.RootElement.GetProperty("usage");
            usage.GetProperty("thinkingTokens").GetInt64().Should().Be(120);
            usage.GetProperty("cachedTokens").GetInt64().Should().Be(800);
            usage.GetProperty("costUsd").GetDouble().Should().BeApproximately(0.00425, 0.00001);
        }
        finally
        {
            OpenRouterAdapter.ClearSessionHistory(sessionId);
            if (Directory.Exists(tempHistory)) Directory.Delete(tempHistory, true);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_FallsBackOnMultipartSystem400_AndReasoning400()
    {
        var tempHistory = Path.Combine(Path.GetTempPath(), "or_hist_fb_" + Guid.NewGuid().ToString("N")[..8]);
        OpenRouterAdapter.HistoryDirectoryPath = tempHistory;
        var sessionId = Guid.NewGuid();
        var requestBodies = new List<string>();

        try
        {
            using var mockClient = new HttpClient(new StubHttpMessageHandler(async (req, attempt) =>
            {
                requestBodies.Add(await req.Content!.ReadAsStringAsync());

                if (attempt == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "Bad Request",
                        Content = new StringContent("""{"error":{"message":"messages[0].content: expected string, got array (cache_control unsupported)"}}""")
                    };
                }

                if (attempt == 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "Bad Request",
                        Content = new StringContent("""{"error":{"message":"Unsupported parameter: reasoning.effort is not supported by this model"}}""")
                    };
                }

                const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Recovered cleanly\"}}]}\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }));
            OpenRouterAdapter.SetHttpClientForTest(mockClient);

            var events = new List<TrajectoryEventPayload>();
            await new OpenRouterAdapter().ExecutePromptAsync(CreatePreset(), new PromptReceivedEvent
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid(),
                AgentId = "openrouter",
                Prompt = "Say hi",
                Effort = "xhigh",
                Sender = "operator"
            }, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            requestBodies.Should().HaveCount(3);
            using var firstDoc = JsonDocument.Parse(requestBodies[0]);
            firstDoc.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind.Should().Be(JsonValueKind.Array);
            firstDoc.RootElement.TryGetProperty("reasoning", out _).Should().BeTrue();

            using var secondDoc = JsonDocument.Parse(requestBodies[1]);
            secondDoc.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind.Should().Be(JsonValueKind.String);
            secondDoc.RootElement.TryGetProperty("reasoning", out _).Should().BeTrue();

            using var thirdDoc = JsonDocument.Parse(requestBodies[2]);
            thirdDoc.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind.Should().Be(JsonValueKind.String);
            thirdDoc.RootElement.TryGetProperty("reasoning", out _).Should().BeFalse();

            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Recovered cleanly"));
        }
        finally
        {
            OpenRouterAdapter.ClearSessionHistory(sessionId);
            if (Directory.Exists(tempHistory)) Directory.Delete(tempHistory, true);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_ReplaysReasoningDetailsAcrossToolCalls_AndHandlesInlineSseErrors()
    {
        var tempHistory = Path.Combine(Path.GetTempPath(), "or_hist_rd_" + Guid.NewGuid().ToString("N")[..8]);
        var workDir = Path.Combine(Path.GetTempPath(), "or_work_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        await File.WriteAllTextAsync(Path.Combine(workDir, "readme.txt"), "hello openrouter");
        OpenRouterAdapter.HistoryDirectoryPath = tempHistory;
        var sessionId = Guid.NewGuid();
        var requestBodies = new List<string>();

        try
        {
            using var mockClient = new HttpClient(new StubHttpMessageHandler(async (req, attempt) =>
            {
                requestBodies.Add(await req.Content!.ReadAsStringAsync());

                if (attempt == 1)
                {
                    var toolSse = string.Join("\n", new[]
                    {
                        "data: {\"choices\":[{\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"id\":\"sig_1\",\"data\":\"abc123\"}],\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"readme.txt\\\"}\"}}]}}]}",
                        "",
                        "data: [DONE]",
                        ""
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(toolSse, Encoding.UTF8, "text/event-stream")
                    };
                }

                var finalSse = "data: {\"choices\":[{\"delta\":{\"content\":\"File says hello openrouter\"}}]}\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(finalSse, Encoding.UTF8, "text/event-stream")
                };
            }));
            OpenRouterAdapter.SetHttpClientForTest(mockClient);

            var events = new List<TrajectoryEventPayload>();
            await new OpenRouterAdapter().ExecutePromptAsync(CreatePreset(), new PromptReceivedEvent
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid(),
                AgentId = "openrouter",
                Prompt = "Read readme.txt",
                WorkingDirectory = workDir,
                Sender = "operator"
            }, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            requestBodies.Should().HaveCount(2);
            using var secondReqDoc = JsonDocument.Parse(requestBodies[1]);
            var messages = secondReqDoc.RootElement.GetProperty("messages").EnumerateArray().ToList();
            var assistantMsg = messages.Single(m => m.GetProperty("role").GetString() == "assistant");
            assistantMsg.GetProperty("reasoning_details")[0].GetProperty("id").GetString().Should().Be("sig_1");
            assistantMsg.GetProperty("reasoning_details")[0].GetProperty("data").GetString().Should().Be("abc123");

            events.Should().Contain(e => e.EventType == "tool_call" && e.ToolName == "read_file");
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("File says hello openrouter"));

            // Now verify mid-stream inline SSE error terminates cleanly with error response
            using var errorClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            {
                const string errSse = "data: {\"error\":{\"code\":402,\"message\":\"Insufficient credits on OpenRouter key\"}}\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(errSse, Encoding.UTF8, "text/event-stream")
                });
            }));
            OpenRouterAdapter.SetHttpClientForTest(errorClient);

            var errEvents = new List<TrajectoryEventPayload>();
            await new OpenRouterAdapter().ExecutePromptAsync(CreatePreset(), new PromptReceivedEvent
            {
                SessionId = Guid.NewGuid(),
                MessageId = Guid.NewGuid(),
                AgentId = "openrouter",
                Prompt = "Trigger inline error",
                Sender = "operator"
            }, ev =>
            {
                errEvents.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            errEvents.Should().Contain(e =>
                e.EventType == "response" &&
                e.Content.Contains("Insufficient credits on OpenRouter key"));
        }
        finally
        {
            OpenRouterAdapter.ClearSessionHistory(sessionId);
            if (Directory.Exists(tempHistory)) Directory.Delete(tempHistory, true);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_EnforcesPlanReadOnlyTools_AndDirectModeOmitsTools()
    {
        var tempHistory = Path.Combine(Path.GetTempPath(), "or_hist_modes_" + Guid.NewGuid().ToString("N")[..8]);
        var workDir = Path.Combine(Path.GetTempPath(), "or_work_modes_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        OpenRouterAdapter.HistoryDirectoryPath = tempHistory;
        var directSession = Guid.NewGuid();
        var planSession = Guid.NewGuid();
        var bodies = new List<string>();

        try
        {
            // Direct mode must omit tools and tool_choice
            using var directClient = new HttpClient(new StubHttpMessageHandler(async (req, _) =>
            {
                bodies.Add(await req.Content!.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Direct reply\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
                };
            }));
            OpenRouterAdapter.SetHttpClientForTest(directClient);

            await new OpenRouterAdapter().ExecutePromptAsync(CreatePreset(), new PromptReceivedEvent
            {
                SessionId = directSession,
                MessageId = Guid.NewGuid(),
                AgentId = "openrouter",
                Prompt = "Just chat",
                Mode = "direct",
                Sender = "operator"
            }, _ => Task.CompletedTask, CancellationToken.None);

            using var directDoc = JsonDocument.Parse(bodies.Single());
            directDoc.RootElement.TryGetProperty("tools", out _).Should().BeFalse();

            // Plan mode must not advertise write tools and rejects write_file if a model still calls it
            bodies.Clear();
            using var planClient = new HttpClient(new StubHttpMessageHandler(async (req, attempt) =>
            {
                bodies.Add(await req.Content!.ReadAsStringAsync());
                if (attempt == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_w\",\"type\":\"function\",\"function\":{\"name\":\"write_file\",\"arguments\":\"{\\\"path\\\":\\\"blocked.txt\\\",\\\"content\\\":\\\"x\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n",
                            Encoding.UTF8,
                            "text/event-stream")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"Plan produced\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
                };
            }));
            OpenRouterAdapter.SetHttpClientForTest(planClient);

            var planEvents = new List<TrajectoryEventPayload>();
            await new OpenRouterAdapter().ExecutePromptAsync(CreatePreset(), new PromptReceivedEvent
            {
                SessionId = planSession,
                MessageId = Guid.NewGuid(),
                AgentId = "openrouter",
                Prompt = "Plan changes",
                Mode = "plan",
                WorkingDirectory = workDir,
                Sender = "operator"
            }, ev =>
            {
                planEvents.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            using var planDoc = JsonDocument.Parse(bodies[0]);
            var toolNames = planDoc.RootElement.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("function").GetProperty("name").GetString())
                .ToList();
            toolNames.Should().Contain("read_file");
            // OpenRouterAdapter keeps tool schema stable for cache prefix hit rate and enforces
            // read-only execution during Plan mode via AgentToolExecutor.
            File.Exists(Path.Combine(workDir, "blocked.txt")).Should().BeFalse();
            planEvents.Should().Contain(e =>
                e.EventType == "tool_result" &&
                e.ToolResultJson != null &&
                e.ToolResultJson.Contains("File modifications are forbidden during Stage 1 Discovery, Plan mode"));
        }
        finally
        {
            OpenRouterAdapter.ClearSessionHistory(directSession);
            OpenRouterAdapter.ClearSessionHistory(planSession);
            if (Directory.Exists(tempHistory)) Directory.Delete(tempHistory, true);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
    }
}
