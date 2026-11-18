using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentMesh.Tests.Cli;

public class McpClientManagerTests
{
    [Fact]
    public async Task CreateAsync_EmptyOrNull_ReturnsEmptyManager()
    {
        await using var empty1 = await McpClientManager.CreateAsync(null);
        await using var empty2 = await McpClientManager.CreateAsync(Array.Empty<ResolvedMcpServerConfig>());

        empty1.Tools.Should().BeEmpty();
        empty1.Diagnostics.Should().BeEmpty();
        empty1.GetOpenAiToolDefinitions().Should().BeEmpty();

        empty2.Tools.Should().BeEmpty();
        empty2.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task StdioTransport_ConnectsListsToolsAndExecutesWithEnvAndArgs()
    {
        // Real POSIX /bin/sh stdio MCP JSON-RPC 2.0 server verifying args + env reach the child process.
        const string shellServer = """
            ARG_VAL="$1"
            while IFS= read -r line; do
              case "$line" in
                *'"method":"server/discover"'*)
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  printf '{"jsonrpc":"2.0","id":%s,"error":{"code":-32601,"message":"Method not found"}}\n' "${id:-1}"
                  ;;
                *'"method":"initialize"'*)
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"sh-mcp","version":"1.0.0"}}}\n' "${id:-1}"
                  ;;
                *'"method":"tools/list"'*)
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"echo_env","description":"Echoes env and arg","inputSchema":{"type":"object","properties":{"msg":{"type":"string"}},"required":["msg"]}},{"name":"blocked_op","description":"Should be filtered","inputSchema":{"type":"object","properties":{}}}]}}\n' "${id:-2}"
                  ;;
                *'"method":"tools/call"'*)
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  msg=$(printf '%s' "$line" | sed -n 's/.*"msg":"\([^"]*\)".*/\1/p')
                  printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"env=%s arg=%s msg=%s"}]}}\n' "${id:-3}" "$MCP_TEST_SECRET" "$ARG_VAL" "$msg"
                  ;;
              esac
            done
            """;

        var servers = new List<ResolvedMcpServerConfig>
        {
            new()
            {
                Id = "shell-srv",
                Name = "Shell MCP",
                Transport = McpTransportType.Stdio,
                Command = "/bin/sh",
                Args = ["-c", shellServer, "sh", "cli-arg-42"],
                Env = new Dictionary<string, string>
                {
                    ["MCP_TEST_SECRET"] = "secret_xyz"
                },
                TimeoutSeconds = 15,
                BlockedTools = ["blocked_op"]
            }
        };

        await using var manager = await McpClientManager.CreateAsync(servers, Path.GetTempPath());

        manager.Diagnostics.Should().ContainSingle();
        manager.Diagnostics[0].Connected.Should().BeTrue();
        manager.Diagnostics[0].ToolCount.Should().Be(1);
        manager.Tools.Should().ContainSingle();

        var tool = manager.Tools[0];
        tool.BridgedName.Should().Be("mcp__shell-srv__echo_env");
        tool.OriginalName.Should().Be("echo_env");
        manager.HasTool("mcp__shell-srv__blocked_op").Should().BeFalse();

        var defs = manager.GetOpenAiToolDefinitions();
        defs.Should().ContainSingle();

        var result = await manager.CallToolAsync(tool.BridgedName, """{"msg":"hello"}""", CancellationToken.None);
        result.Should().Be("env=secret_xyz arg=cli-arg-42 msg=hello");
    }

    [Fact]
    public async Task HttpTransport_ConnectsWithCustomHeaders_AppliesFilters_AndCallsTools()
    {
        var capturedAuth = new ConcurrentQueue<string?>();
        var capturedCustom = new ConcurrentQueue<string?>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<SampleHttpMcpTools>();

        await using var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            capturedAuth.Enqueue(ctx.Request.Headers.Authorization.ToString());
            capturedCustom.Enqueue(ctx.Request.Headers["X-Custom-Tenant"].ToString());
            await next();
        });
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Urls.First();
            var servers = new List<ResolvedMcpServerConfig>
            {
                new()
                {
                    Id = "docs-http",
                    Name = "Docs HTTP",
                    Transport = McpTransportType.Http,
                    Url = $"{baseUrl}/mcp",
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer http-secret-token",
                        ["X-Custom-Tenant"] = "tenant-99"
                    },
                    TimeoutSeconds = 15,
                    AllowedTools = ["lookup_doc", "danger_tool"],
                    BlockedTools = ["danger_tool"]
                }
            };

            await using var manager = await McpClientManager.CreateAsync(servers);

            manager.Diagnostics.Should().ContainSingle();
            manager.Diagnostics[0].Connected.Should().BeTrue(manager.Diagnostics[0].ErrorMessage);
            manager.Diagnostics[0].ToolCount.Should().Be(1);

            manager.HasTool("mcp__docs-http__lookup_doc").Should().BeTrue();
            manager.HasTool("mcp__docs-http__danger_tool").Should().BeFalse();
            manager.HasTool("mcp__docs-http__extra_tool").Should().BeFalse();

            manager.TryGetTool("mcp__docs-http__lookup_doc", out var bridged).Should().BeTrue();
            bridged!.IsReadOnly.Should().BeTrue();

            var result = await manager.CallToolAsync(
                "mcp__docs-http__lookup_doc",
                """{"topic":"aspire"}""",
                CancellationToken.None);
            result.Should().Be("doc:aspire");

            capturedAuth.Should().Contain("Bearer http-secret-token");
            capturedCustom.Should().Contain("tenant-99");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task SseTransport_ConnectsWithCustomHeaders_ListsAndCallsTools()
    {
        var seenHeaderValues = new ConcurrentQueue<string?>();
        var sessions = new ConcurrentDictionary<string, Channel<string>>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        app.MapGet("/sse", async (HttpContext ctx) =>
        {
            seenHeaderValues.Enqueue(ctx.Request.Headers["X-Sse-Key"].ToString());
            var sessionId = Guid.NewGuid().ToString("N");
            var channel = Channel.CreateUnbounded<string>();
            sessions[sessionId] = channel;

            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync($"event: endpoint\ndata: /messages?sessionId={sessionId}\n\n");
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            try
            {
                await foreach (var payload in channel.Reader.ReadAllAsync(ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"event: message\ndata: {payload}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            finally
            {
                sessions.TryRemove(sessionId, out _);
            }
        });

        app.MapPost("/messages", async (HttpContext ctx) =>
        {
            seenHeaderValues.Enqueue(ctx.Request.Headers["X-Sse-Key"].ToString());
            var sessionId = ctx.Request.Query["sessionId"].ToString();
            if (!sessions.TryGetValue(sessionId, out var channel))
            {
                return Results.NotFound();
            }

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var hasId = root.TryGetProperty("id", out var idElem);

            if (method == "server/discover" && hasId)
            {
                var resp = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = idElem,
                    error = new { code = -32601, message = "Method not found" }
                });
                await channel.Writer.WriteAsync(resp);
            }
            else if (method == "initialize" && hasId)
            {
                var resp = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = idElem,
                    result = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "sse-test", version = "1.0.0" }
                    }
                });
                await channel.Writer.WriteAsync(resp);
            }
            else if (method == "tools/list" && hasId)
            {
                var resp = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = idElem,
                    result = new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "ping_sse",
                                description = "Ping over SSE",
                                inputSchema = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        value = new { type = "string" }
                                    },
                                    required = new[] { "value" }
                                }
                            }
                        }
                    }
                });
                await channel.Writer.WriteAsync(resp);
            }
            else if (method == "tools/call" && hasId)
            {
                var val = root.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString();
                var resp = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = idElem,
                    result = new
                    {
                        content = new[]
                        {
                            new { type = "text", text = $"sse-pong:{val}" }
                        }
                    }
                });
                await channel.Writer.WriteAsync(resp);
            }

            return Results.Accepted();
        });

        await app.StartAsync();

        try
        {
            var baseUrl = app.Urls.First();
            var servers = new List<ResolvedMcpServerConfig>
            {
                new()
                {
                    Id = "sse-srv",
                    Name = "SSE Server",
                    Transport = McpTransportType.Sse,
                    Url = $"{baseUrl}/sse",
                    Headers = new Dictionary<string, string>
                    {
                        ["X-Sse-Key"] = "sse-secret-123"
                    },
                    TimeoutSeconds = 15
                }
            };

            await using var manager = await McpClientManager.CreateAsync(servers);
            manager.Diagnostics.Should().ContainSingle();
            manager.Diagnostics[0].Connected.Should().BeTrue(manager.Diagnostics[0].ErrorMessage);
            manager.HasTool("mcp__sse-srv__ping_sse").Should().BeTrue();

            var result = await manager.CallToolAsync(
                "mcp__sse-srv__ping_sse",
                """{"value":"ok"}""",
                CancellationToken.None);
            result.Should().Be("sse-pong:ok");
            seenHeaderValues.Should().Contain("sse-secret-123");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CreateAsync_WhenOneServerFails_IsolatesFailureAndConnectsRemainingServers()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<SampleHttpMcpTools>();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Urls.First();
            var servers = new List<ResolvedMcpServerConfig>
            {
                new()
                {
                    Id = "broken-stdio",
                    Name = "Broken Stdio",
                    Transport = McpTransportType.Stdio,
                    Command = "/nonexistent/binary/agentmesh-missing-mcp",
                    TimeoutSeconds = 5
                },
                new()
                {
                    Id = "broken-http",
                    Name = "Broken HTTP",
                    Transport = McpTransportType.Http,
                    Url = "not-a-valid-url",
                    TimeoutSeconds = 5
                },
                new()
                {
                    Id = "healthy-http",
                    Name = "Healthy HTTP",
                    Transport = McpTransportType.Http,
                    Url = $"{baseUrl}/mcp",
                    TimeoutSeconds = 15
                }
            };

            await using var manager = await McpClientManager.CreateAsync(servers);

            manager.Diagnostics.Should().HaveCount(3);
            manager.Diagnostics[0].Connected.Should().BeFalse();
            manager.Diagnostics[0].ErrorMessage.Should().NotBeNullOrWhiteSpace();
            manager.Diagnostics[1].Connected.Should().BeFalse();
            manager.Diagnostics[1].ErrorMessage.Should().Contain("invalid URL");
            manager.Diagnostics[2].Connected.Should().BeTrue();
            manager.Diagnostics[2].ToolCount.Should().Be(3);

            manager.HasTool("mcp__healthy-http__lookup_doc").Should().BeTrue();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CallToolAsync_InvalidJsonOrErrorResult_ReturnsActionableErrorWithoutThrowing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<SlowAndErrorMcpTools>();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        try
        {
            var baseUrl = app.Urls.First();
            var servers = new List<ResolvedMcpServerConfig>
            {
                new()
                {
                    Id = "err-srv",
                    Name = "Error Server",
                    Transport = McpTransportType.Http,
                    Url = $"{baseUrl}/mcp",
                    TimeoutSeconds = 15
                }
            };

            await using var manager = await McpClientManager.CreateAsync(servers);
            manager.Diagnostics[0].Connected.Should().BeTrue(manager.Diagnostics[0].ErrorMessage);

            var badJson = await manager.CallToolAsync("mcp__err-srv__throwing_tool", "{bad json", CancellationToken.None);
            badJson.Should().StartWith("Error: Invalid JSON arguments");

            var nonObject = await manager.CallToolAsync("mcp__err-srv__throwing_tool", "[1,2,3]", CancellationToken.None);
            nonObject.Should().StartWith("Error: Invalid arguments");

            var unknown = await manager.CallToolAsync("mcp__err-srv__does_not_exist", "{}", CancellationToken.None);
            unknown.Should().StartWith("Error: Unknown MCP tool");

            var errorResult = await manager.CallToolAsync("mcp__err-srv__throwing_tool", "{}", CancellationToken.None);
            errorResult.Should().StartWith("Error: upstream failure");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CallToolAsync_WhenServerExceedsTimeout_ReturnsTimeoutError()
    {
        const string slowShellServer = """
            while IFS= read -r line; do
              case "$line" in
                *'"method":"server/discover"'*)
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  printf '{"jsonrpc":"2.0","id":%s,"error":{"code":-32601,"message":"Method not found"}}\n' "${id:-1}"
                  ;;
                *'"method":"initialize"'*)
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"slow-sh","version":"1.0.0"}}}\n' "${id:-1}"
                  ;;
                *'"method":"tools/list"'*)
                  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
                  printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"slow_tool","description":"Sleeps","inputSchema":{"type":"object","properties":{}}}]}}\n' "${id:-2}"
                  ;;
                *'"method":"tools/call"'*)
                  sleep 6
                  ;;
              esac
            done
            """;

        var servers = new List<ResolvedMcpServerConfig>
        {
            new()
            {
                Id = "slow-sh",
                Name = "Slow Shell",
                Transport = McpTransportType.Stdio,
                Command = "/bin/sh",
                Args = ["-c", slowShellServer],
                TimeoutSeconds = 2
            }
        };

        await using var manager = await McpClientManager.CreateAsync(servers);
        manager.Diagnostics[0].Connected.Should().BeTrue(manager.Diagnostics[0].ErrorMessage);

        var timedOut = await manager.CallToolAsync("mcp__slow-sh__slow_tool", "{}", CancellationToken.None);
        timedOut.Should().Be("Error: MCP tool 'mcp__slow-sh__slow_tool' timed out after 2s.");
    }

    [Fact]
    public void IsToolAllowed_AppliesAllowListAndBlockListPrecedence()
    {
        // Null allowlist permits everything unless blocked
        McpClientManager.IsToolAllowed("get_issue", null, null).Should().BeTrue();
        McpClientManager.IsToolAllowed("delete_repo", null, ["delete_repo"]).Should().BeFalse();

        // Explicit empty allowlist permits nothing
        McpClientManager.IsToolAllowed("get_issue", Array.Empty<string>(), null).Should().BeFalse();

        // Blocked always wins over allowed (case-insensitive)
        McpClientManager.IsToolAllowed("GET_ISSUE", ["get_issue", "create_issue"], null).Should().BeTrue();
        McpClientManager.IsToolAllowed("create_issue", ["get_issue", "create_issue"], ["CREATE_ISSUE"]).Should().BeFalse();
    }

    [Fact]
    public void BuildBridgedToolName_SanitizesCapsAt64CharsAndAvoidsCollisions()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dotTool = McpClientManager.BuildBridgedToolName("my.server", "git/status:v1", used);
        dotTool.Should().Be("mcp__my_server__git_status_v1");

        // Built-in name is safely namespaced so it never collides with run_command
        var runCmd = McpClientManager.BuildBridgedToolName("github", "run_command", used);
        runCmd.Should().Be("mcp__github__run_command");

        // Same tool on two different servers remains distinct
        var ghSearch = McpClientManager.BuildBridgedToolName("github", "search", used);
        var c7Search = McpClientManager.BuildBridgedToolName("context7", "search", used);
        ghSearch.Should().Be("mcp__github__search");
        c7Search.Should().Be("mcp__context7__search");

        // Overly long names cap at 64 chars and stay distinct when sharing prefix
        var longA = McpClientManager.BuildBridgedToolName("very-long-server-identifier-alpha", "extremely_long_mcp_tool_operation_name_one", used);
        var longB = McpClientManager.BuildBridgedToolName("very-long-server-identifier-alpha", "extremely_long_mcp_tool_operation_name_two", used);
        longA.Length.Should().BeLessThanOrEqualTo(64);
        longB.Length.Should().BeLessThanOrEqualTo(64);
        longA.Should().NotBe(longB);
        longA.Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$");
        longB.Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$");

        // Identical sanitized names still disambiguate
        var dup1 = McpClientManager.BuildBridgedToolName("srv", "a.b", used);
        var dup2 = McpClientManager.BuildBridgedToolName("srv", "a/b", used);
        dup1.Should().Be("mcp__srv__a_b");
        dup2.Should().Be("mcp__srv__a_b_2");
    }

    [Fact]
    public void NormalizeToolParametersSchema_StripsDollarSchemaAndEnsuresObjectProperties()
    {
        using var doc = JsonDocument.Parse("""{"$schema":"http://json-schema.org/draft-07/schema#","type":"object"}""");
        var normalized = McpClientManager.NormalizeToolParametersSchema(doc.RootElement);

        normalized.TryGetProperty("$schema", out _).Should().BeFalse();
        normalized.GetProperty("type").GetString().Should().Be("object");
        normalized.TryGetProperty("properties", out var props).Should().BeTrue();
        props.ValueKind.Should().Be(JsonValueKind.Object);

        // Union type array should not throw
        using var unionDoc = JsonDocument.Parse("""{"type":["object","null"]}""");
        var normalizedUnion = McpClientManager.NormalizeToolParametersSchema(unionDoc.RootElement);
        normalizedUnion.ValueKind.Should().Be(JsonValueKind.Object);

        // Non-object schema defaults cleanly
        using var undefinedDoc = JsonDocument.Parse("null");
        var normalizedNull = McpClientManager.NormalizeToolParametersSchema(undefinedDoc.RootElement);
        normalizedNull.GetProperty("type").GetString().Should().Be("object");
        normalizedNull.TryGetProperty("properties", out _).Should().BeTrue();
    }

    [Fact]
    public void FormatCallToolResult_HandlesAllContentBlockTypesAndErrors()
    {
        var result = new CallToolResult
        {
            IsError = false,
            Content =
            [
                new TextContentBlock { Text = "line one" },
                new ImageContentBlock { MimeType = "image/png", Data = new byte[] { 1, 2, 3 } },
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents { Uri = "mem://doc", Text = "embedded text" }
                },
                new ResourceLinkBlock { Uri = "https://example.com/spec", Name = "Spec" }
            ]
        };

        var formatted = McpClientManager.FormatCallToolResult(result);
        formatted.Should().Contain("line one");
        formatted.Should().Contain("[Image content: image/png, 3 bytes]");
        formatted.Should().Contain("embedded text");
        formatted.Should().Contain("[Resource link: Spec (https://example.com/spec)]");

        var errResult = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Permission denied" }]
        };
        McpClientManager.FormatCallToolResult(errResult).Should().Be("Error: Permission denied");

        var structuredOnly = new CallToolResult
        {
            IsError = false,
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new { ok = true, count = 7 })
        };
        McpClientManager.FormatCallToolResult(structuredOnly).Should().Contain("\"count\":7");
    }

    [McpServerToolType]
    public sealed class SampleHttpMcpTools
    {
        [McpServerTool(Name = "lookup_doc", ReadOnly = true), Description("Look up documentation")]
        public static string LookupDoc([Description("Topic to look up")] string topic) => $"doc:{topic}";

        [McpServerTool(Name = "danger_tool"), Description("Dangerous tool")]
        public static string DangerTool() => "boom";

        [McpServerTool(Name = "extra_tool"), Description("Extra unallowed tool")]
        public static string ExtraTool() => "extra";
    }

    [McpServerToolType]
    public sealed class SlowAndErrorMcpTools
    {
        [McpServerTool(Name = "slow_tool"), Description("Sleeps past timeout")]
        public static async Task<string> SlowTool(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return "done";
        }

        [McpServerTool(Name = "throwing_tool"), Description("Returns an error result")]
        public static CallToolResult ThrowingTool() =>
            new()
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "upstream failure" }]
            };
    }
}
