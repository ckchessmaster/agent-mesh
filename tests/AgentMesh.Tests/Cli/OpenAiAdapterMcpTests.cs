using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace AgentMesh.Tests.Cli;

[Collection("OpenAiAdapterStaticSeams")]
public class OpenAiAdapterMcpTests : IDisposable
{
    private readonly List<Guid> _sessions = [];

    public void Dispose()
    {
        OpenAiAdapter.ResetHttpClientForTest();
        OpenAiAdapter.ResetMcpTransportFactoryForTest();
        foreach (var id in _sessions)
        {
            OpenAiAdapter.ClearSessionHistory(id);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_BridgesMcpToolsIntoOpenAiPayload_AndRoutesNativeToolCalls()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<BridgeTools>();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Urls.First();
            var capturedBodies = new List<string>();
            var callNumber = 0;

            var mockOpenAiClient = new HttpClient(new DelegateHttpMessageHandler(req =>
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                capturedBodies.Add(body);
                var step = Interlocked.Increment(ref callNumber);

                if (step == 1)
                {
                    var toolCallResponse = """
                    {
                        "id": "chatcmpl-1",
                        "choices": [{
                            "message": {
                                "role": "assistant",
                                "content": null,
                                "tool_calls": [{
                                    "id": "call_mcp_1",
                                    "type": "function",
                                    "function": {
                                        "name": "mcp__calc__add_numbers",
                                        "arguments": "{\"a\":19,\"b\":23}"
                                    }
                                }]
                            }
                        }]
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(toolCallResponse, Encoding.UTF8, "application/json")
                    };
                }

                var finalResponse = """
                {
                    "id": "chatcmpl-2",
                    "choices": [{
                        "message": {
                            "role": "assistant",
                            "content": "The sum is 42."
                        }
                    }]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(finalResponse, Encoding.UTF8, "application/json")
                };
            }));

            OpenAiAdapter.SetHttpClientForTest(mockOpenAiClient);

            var sessionId = Guid.NewGuid();
            _sessions.Add(sessionId);

            var adapter = new OpenAiAdapter();
            var preset = new AgentPresetConfig
            {
                Id = "openai-mcp-test",
                Name = "OpenAI MCP Test",
                AdapterType = "openai",
                Endpoint = "http://127.0.0.1:11434/v1",
                DefaultModel = "test-model"
            };

            var promptEvent = new PromptReceivedEvent
            {
                SessionId = sessionId,
                Prompt = "Add 19 and 23 using MCP.",
                Model = "test-model",
                McpServers =
                [
                    new ResolvedMcpServerConfig
                    {
                        Id = "calc",
                        Name = "Calculator",
                        Transport = McpTransportType.Http,
                        Url = $"{baseUrl}/mcp",
                        TimeoutSeconds = 15,
                        AllowedTools = ["add_numbers"]
                    },
                    new ResolvedMcpServerConfig
                    {
                        Id = "broken-srv",
                        Name = "Broken Server",
                        Transport = McpTransportType.Stdio,
                        Command = "/nonexistent/mcp-binary",
                        Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer super-secret-never-log" },
                        TimeoutSeconds = 5
                    }
                ]
            };

            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEvent, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            // Step 1 request must include the 10 built-in tools + 1 bridged MCP tool
            capturedBodies.Should().HaveCount(2);
            using (var doc1 = JsonDocument.Parse(capturedBodies[0]))
            {
                var tools = doc1.RootElement.GetProperty("tools").EnumerateArray().ToList();
                tools.Should().HaveCount(11);
                var toolNames = tools.Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
                toolNames.Should().Contain("run_command");
                toolNames.Should().Contain("mcp__calc__add_numbers");
                toolNames.Should().NotContain("mcp__calc__hidden_tool");
            }

            // Step 2 request must carry the tool result from the MCP server
            using (var doc2 = JsonDocument.Parse(capturedBodies[1]))
            {
                var messages = doc2.RootElement.GetProperty("messages").EnumerateArray().ToList();
                messages.Should().Contain(m =>
                    m.GetProperty("role").GetString() == "tool" &&
                    m.GetProperty("tool_call_id").GetString() == "call_mcp_1" &&
                    m.GetProperty("content").GetString() == "42");
            }

            // Trajectory events include connection summary, failed server warning (without secrets), tool_call, tool_result, and response
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Connected 1/2 MCP server(s); bridged 1 MCP tool(s)"));
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Broken Server") && !e.Content.Contains("super-secret-never-log"));
            events.Should().Contain(e => e.EventType == "tool_call" && e.ToolName == "mcp__calc__add_numbers");
            events.Should().Contain(e => e.EventType == "tool_result" && e.ToolName == "mcp__calc__add_numbers" && e.ToolResultJson == "42");
            events.Should().Contain(e => e.EventType == "response" && e.Content == "The sum is 42.");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_DirectMode_DoesNotConnectOrBridgeMcpTools()
    {
        string? capturedBody = null;
        var mockOpenAiClient = new HttpClient(new DelegateHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"Direct reply."}}]}""", Encoding.UTF8, "application/json")
            };
        }));
        OpenAiAdapter.SetHttpClientForTest(mockOpenAiClient);

        var sessionId = Guid.NewGuid();
        _sessions.Add(sessionId);

        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "openai-direct-test",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "test-model"
        };

        var promptEvent = new PromptReceivedEvent
        {
            SessionId = sessionId,
            Mode = "direct",
            Prompt = "Quick chat",
            Model = "test-model",
            McpServers =
            [
                new ResolvedMcpServerConfig
                {
                    Id = "unreachable",
                    Name = "Unreachable",
                    Transport = McpTransportType.Stdio,
                    Command = "/nonexistent/should-not-run"
                }
            ]
        };

        var events = new List<TrajectoryEventPayload>();
        await adapter.ExecutePromptAsync(preset, promptEvent, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        }, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.TryGetProperty("tools", out _).Should().BeFalse();
        events.Should().NotContain(e => e.EventType == "thought" && e.Content.Contains("MCP server"));
        events.Should().Contain(e => e.EventType == "response" && e.Content == "Direct reply.");
    }

    [Fact]
    public async Task ExecutePromptAsync_RoutesFallbackToolCallToBridgedMcpTool()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<BridgeTools>();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Urls.First();
            var step = 0;

            var mockOpenAiClient = new HttpClient(new DelegateHttpMessageHandler(_ =>
            {
                var current = Interlocked.Increment(ref step);
                var content = current == 1
                    ? """<tool_call>{"name":"mcp__calc__add_numbers","arguments":{"a":5,"b":7}}</tool_call>"""
                    : "Fallback sum is 12.";

                var json = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                role = "assistant",
                                content
                            }
                        }
                    }
                });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }));
            OpenAiAdapter.SetHttpClientForTest(mockOpenAiClient);

            var sessionId = Guid.NewGuid();
            _sessions.Add(sessionId);

            var adapter = new OpenAiAdapter();
            var preset = new AgentPresetConfig
            {
                Id = "openai-fallback-mcp",
                AdapterType = "openai",
                Endpoint = "http://127.0.0.1:11434/v1",
                DefaultModel = "test-model"
            };

            var promptEvent = new PromptReceivedEvent
            {
                SessionId = sessionId,
                Prompt = "Add 5 and 7.",
                Model = "test-model",
                McpServers =
                [
                    new ResolvedMcpServerConfig
                    {
                        Id = "calc",
                        Name = "Calculator",
                        Transport = McpTransportType.Http,
                        Url = $"{baseUrl}/mcp",
                        TimeoutSeconds = 15
                    }
                ]
            };

            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEvent, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            events.Should().Contain(e => e.EventType == "tool_result" && e.ToolName == "mcp__calc__add_numbers" && e.ToolResultJson == "12");
            events.Should().Contain(e => e.EventType == "response" && e.Content == "Fallback sum is 12.");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [McpServerToolType]
    public sealed class BridgeTools
    {
        [McpServerTool(Name = "add_numbers", ReadOnly = true), Description("Add two integers")]
        public static string AddNumbers(int a, int b) => (a + b).ToString();

        [McpServerTool(Name = "hidden_tool"), Description("Hidden by allowlist")]
        public static string HiddenTool() => "hidden";
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
