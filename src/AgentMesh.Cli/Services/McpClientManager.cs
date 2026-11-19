using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Adapters;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentMesh.Cli.Services;

public sealed record McpServerDiagnostic(
    string ServerId,
    string ServerName,
    bool Connected,
    int ToolCount,
    string? ErrorMessage);

public sealed record BridgedMcpTool(
    string BridgedName,
    string ServerId,
    string ServerName,
    string OriginalName,
    string Description,
    JsonElement InputSchema,
    int TimeoutSeconds,
    bool IsReadOnly);

/// <summary>
/// Manages per-turn MCP client connections (stdio, HTTP, SSE), applies tool allow/block filters,
/// bridges MCP tool schemas into OpenAI function definitions, and routes tool calls back to the
/// owning MCP server.
/// </summary>
public sealed class McpClientManager : IAsyncDisposable
{
    private const int MaxOpenAiToolNameLength = 64;
    private const int MaxStderrTailLines = 15;

    private readonly List<McpClient> _clients;
    private readonly List<McpServerDiagnostic> _diagnostics;
    private readonly List<BridgedMcpTool> _orderedTools;
    private readonly Dictionary<string, (McpClient Client, BridgedMcpTool Tool)> _toolsByBridgedName;
    private int _disposed;

    private McpClientManager(
        List<McpClient> clients,
        List<McpServerDiagnostic> diagnostics,
        List<BridgedMcpTool> orderedTools,
        Dictionary<string, (McpClient Client, BridgedMcpTool Tool)> toolsByBridgedName)
    {
        _clients = clients;
        _diagnostics = diagnostics;
        _orderedTools = orderedTools;
        _toolsByBridgedName = toolsByBridgedName;
    }

    public IReadOnlyList<McpServerDiagnostic> Diagnostics => _diagnostics;
    public IReadOnlyList<BridgedMcpTool> Tools => _orderedTools;

    public static McpClientManager Empty() =>
        new(
            new List<McpClient>(),
            new List<McpServerDiagnostic>(),
            new List<BridgedMcpTool>(),
            new Dictionary<string, (McpClient, BridgedMcpTool)>(StringComparer.OrdinalIgnoreCase));

    public static Task<McpClientManager> CreateAsync(
        IReadOnlyList<ResolvedMcpServerConfig>? servers,
        string? workingDirectory = null,
        CancellationToken ct = default) =>
        CreateAsync(servers, workingDirectory, transportFactory: null, ct);

    internal static async Task<McpClientManager> CreateAsync(
        IReadOnlyList<ResolvedMcpServerConfig>? servers,
        string? workingDirectory,
        Func<ResolvedMcpServerConfig, string?, IClientTransport?>? transportFactory,
        CancellationToken ct = default)
    {
        if (servers == null || servers.Count == 0)
        {
            return Empty();
        }

        var clients = new List<McpClient>();
        var diagnostics = new List<McpServerDiagnostic>(servers.Count);
        var orderedTools = new List<BridgedMcpTool>();
        var toolsByBridgedName = new Dictionary<string, (McpClient Client, BridgedMcpTool Tool)>(StringComparer.OrdinalIgnoreCase);
        var usedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();

            var serverId = string.IsNullOrWhiteSpace(server.Id) ? server.Name : server.Id;
            var serverName = string.IsNullOrWhiteSpace(server.Name) ? serverId : server.Name;
            var timeoutSeconds = server.TimeoutSeconds > 0 ? server.TimeoutSeconds : 30;
            var stderrLines = new ConcurrentQueue<string>();

            IClientTransport? transport = null;
            McpClient? client = null;

            try
            {
                transport = transportFactory?.Invoke(server, workingDirectory)
                    ?? CreateDefaultTransport(server, workingDirectory, timeoutSeconds, stderrLines);

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                client = await McpClient.CreateAsync(transport, cancellationToken: connectCts.Token);
                var discoveredTools = await client.ListToolsAsync(cancellationToken: connectCts.Token);

                var registeredCount = 0;
                foreach (var mcpTool in discoveredTools)
                {
                    if (string.IsNullOrWhiteSpace(mcpTool.Name))
                    {
                        continue;
                    }

                    if (!IsToolAllowed(mcpTool.Name, server.AllowedTools, server.BlockedTools))
                    {
                        continue;
                    }

                    var bridgedName = BuildBridgedToolName(serverId, mcpTool.Name, usedToolNames);
                    var rawDescription = string.IsNullOrWhiteSpace(mcpTool.Description)
                        ? $"Tool '{mcpTool.Name}'"
                        : mcpTool.Description.Trim();
                    var bridgedDescription = $"[MCP: {serverName}] {rawDescription}";
                    var normalizedSchema = NormalizeToolParametersSchema(mcpTool.JsonSchema);
                    var isReadOnly = mcpTool.ProtocolTool?.Annotations?.ReadOnlyHint == true;

                    var bridgedTool = new BridgedMcpTool(
                        bridgedName,
                        serverId,
                        serverName,
                        mcpTool.Name,
                        bridgedDescription,
                        normalizedSchema,
                        timeoutSeconds,
                        isReadOnly);

                    orderedTools.Add(bridgedTool);
                    toolsByBridgedName[bridgedName] = (client, bridgedTool);
                    registeredCount++;
                }

                clients.Add(client);
                diagnostics.Add(new McpServerDiagnostic(serverId, serverName, Connected: true, registeredCount, ErrorMessage: null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await DisposeFailedClientOrTransportAsync(client, transport);
                await DisposeClientsAsync(clients);
                throw;
            }
            catch (OperationCanceledException)
            {
                await DisposeFailedClientOrTransportAsync(client, transport);
                var stderrSuffix = FormatStderrSuffix(stderrLines);
                diagnostics.Add(new McpServerDiagnostic(
                    serverId,
                    serverName,
                    Connected: false,
                    ToolCount: 0,
                    ErrorMessage: $"Timed out after {timeoutSeconds}s while connecting/listing tools.{stderrSuffix}"));
            }
            catch (Exception ex)
            {
                await DisposeFailedClientOrTransportAsync(client, transport);
                var stderrSuffix = FormatStderrSuffix(stderrLines);
                diagnostics.Add(new McpServerDiagnostic(
                    serverId,
                    serverName,
                    Connected: false,
                    ToolCount: 0,
                    ErrorMessage: $"{ex.Message}{stderrSuffix}"));
            }
        }

        return new McpClientManager(clients, diagnostics, orderedTools, toolsByBridgedName);
    }

    public bool HasTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _toolsByBridgedName.ContainsKey(toolName.Trim());

    public bool TryGetTool(string? toolName, out BridgedMcpTool? tool)
    {
        if (!string.IsNullOrWhiteSpace(toolName) &&
            _toolsByBridgedName.TryGetValue(toolName.Trim(), out var entry))
        {
            tool = entry.Tool;
            return true;
        }

        tool = null;
        return false;
    }

    public List<object> GetOpenAiToolDefinitions()
    {
        var list = new List<object>(_orderedTools.Count);
        foreach (var tool in _orderedTools)
        {
            list.Add(new
            {
                type = "function",
                function = new
                {
                    name = tool.BridgedName,
                    description = tool.Description,
                    parameters = tool.InputSchema
                }
            });
        }
        return list;
    }

    public async Task<string> CallToolAsync(string toolName, string? argsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolName) ||
            !_toolsByBridgedName.TryGetValue(toolName.Trim(), out var entry))
        {
            return $"Error: Unknown MCP tool '{toolName}'.";
        }

        var (args, parseError) = ParseToolArguments(toolName, argsJson);
        if (parseError != null)
        {
            return parseError;
        }

        try
        {
            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            callCts.CancelAfter(TimeSpan.FromSeconds(entry.Tool.TimeoutSeconds));

            var result = await entry.Client.CallToolAsync(
                entry.Tool.OriginalName,
                args,
                cancellationToken: callCts.Token);

            var formatted = FormatCallToolResult(result);
            var truncated = OpenAiAdapter.TruncateToolResult(formatted);
            return truncated.IndexOf('\0') >= 0 ? truncated.Replace("\0", string.Empty) : truncated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"Error: MCP tool '{toolName}' timed out after {entry.Tool.TimeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{toolName}' failed: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisposeClientsAsync(_clients);
    }

    internal static bool IsToolAllowed(
        string toolName,
        IReadOnlyCollection<string>? allowedTools,
        IReadOnlyCollection<string>? blockedTools)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        if (blockedTools != null &&
            blockedTools.Any(b => string.Equals(b?.Trim(), toolName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (allowedTools == null)
        {
            return true;
        }

        return allowedTools.Any(a => string.Equals(a?.Trim(), toolName, StringComparison.OrdinalIgnoreCase));
    }

    internal static string BuildBridgedToolName(
        string serverId,
        string originalToolName,
        ISet<string> usedNames)
    {
        var cleanServer = SanitizeIdentifier(serverId, "server");
        var cleanTool = SanitizeIdentifier(originalToolName, "tool");
        var candidate = $"mcp__{cleanServer}__{cleanTool}";

        if (candidate.Length > MaxOpenAiToolNameLength)
        {
            var suffix = ComputeShortHash($"{serverId}:{originalToolName}");
            var prefixMaxLen = MaxOpenAiToolNameLength - suffix.Length - 1;
            candidate = $"{candidate[..prefixMaxLen]}_{suffix}";
        }

        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        var counter = 2;
        while (true)
        {
            var disambiguator = $"_{counter++}";
            var baseLen = Math.Min(candidate.Length, MaxOpenAiToolNameLength - disambiguator.Length);
            var next = candidate[..baseLen] + disambiguator;
            if (usedNames.Add(next))
            {
                return next;
            }
        }
    }

    internal static JsonElement NormalizeToolParametersSchema(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new Dictionary<string, object>()
            });
        }

        var node = JsonNode.Parse(inputSchema.GetRawText()) as JsonObject;
        if (node == null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new Dictionary<string, object>()
            });
        }

        // Several OpenAI-compatible gateways reject top-level $schema inside function.parameters.
        node.Remove("$schema");

        if (!node.ContainsKey("type"))
        {
            node["type"] = "object";
        }

        if (node["type"] is JsonValue typeVal &&
            typeVal.TryGetValue<string>(out var typeStr) &&
            string.Equals(typeStr, "object", StringComparison.OrdinalIgnoreCase) &&
            !node.ContainsKey("properties"))
        {
            node["properties"] = new JsonObject();
        }

        return JsonSerializer.SerializeToElement(node);
    }

    internal static string FormatCallToolResult(CallToolResult? result)
    {
        if (result == null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (result.Content != null)
        {
            foreach (var block in result.Content)
            {
                switch (block)
                {
                    case TextContentBlock textBlock when textBlock.Text != null:
                        parts.Add(textBlock.Text);
                        break;
                    case ImageContentBlock imageBlock:
                        parts.Add($"[Image content: {imageBlock.MimeType ?? "application/octet-stream"}, {imageBlock.Data.Length} bytes]");
                        break;
                    case AudioContentBlock audioBlock:
                        parts.Add($"[Audio content: {audioBlock.MimeType ?? "application/octet-stream"}, {audioBlock.Data.Length} bytes]");
                        break;
                    case EmbeddedResourceBlock resourceBlock:
                        parts.Add(FormatEmbeddedResource(resourceBlock));
                        break;
                    case ResourceLinkBlock linkBlock:
                        var label = string.IsNullOrWhiteSpace(linkBlock.Name) ? linkBlock.Uri : $"{linkBlock.Name} ({linkBlock.Uri})";
                        parts.Add($"[Resource link: {label}]");
                        break;
                    default:
                        try
                        {
                            parts.Add(JsonSerializer.Serialize(block));
                        }
                        catch
                        {
                            parts.Add(block?.ToString() ?? string.Empty);
                        }
                        break;
                }
            }
        }

        var combined = string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
        if (result.StructuredContent.HasValue &&
            result.StructuredContent.Value.ValueKind != JsonValueKind.Null &&
            result.StructuredContent.Value.ValueKind != JsonValueKind.Undefined &&
            string.IsNullOrWhiteSpace(combined))
        {
            combined = result.StructuredContent.Value.GetRawText();
        }

        if (result.IsError == true)
        {
            if (string.IsNullOrWhiteSpace(combined))
            {
                return "Error: MCP tool returned an unspecified error.";
            }

            if (!combined.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                return $"Error: {combined}";
            }
        }

        return combined;
    }

    private static string FormatEmbeddedResource(EmbeddedResourceBlock block)
    {
        if (block.Resource is TextResourceContents textRes)
        {
            return string.IsNullOrEmpty(textRes.Text)
                ? $"[Embedded resource: {textRes.Uri}]"
                : textRes.Text;
        }

        if (block.Resource is BlobResourceContents blobRes)
        {
            return $"[Embedded binary resource: {blobRes.Uri} ({blobRes.MimeType ?? "application/octet-stream"}, {blobRes.Blob.Length} bytes)]";
        }

        return $"[Embedded resource: {block.Resource?.Uri ?? "unknown"}]";
    }

    private static (Dictionary<string, object?> Args, string? Error) ParseToolArguments(string toolName, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || string.Equals(argsJson.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return (new Dictionary<string, object?>(StringComparer.Ordinal), null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (new Dictionary<string, object?>(), $"Error: Invalid arguments for MCP tool '{toolName}': expected a JSON object.");
            }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
            return (dict, null);
        }
        catch (JsonException ex)
        {
            return (new Dictionary<string, object?>(), $"Error: Invalid JSON arguments for MCP tool '{toolName}': {ex.Message}");
        }
    }

    private static IClientTransport CreateDefaultTransport(
        ResolvedMcpServerConfig server,
        string? workingDirectory,
        int timeoutSeconds,
        ConcurrentQueue<string> stderrLines)
    {
        var serverName = !string.IsNullOrWhiteSpace(server.Name) ? server.Name : server.Id;

        if (server.Transport == McpTransportType.Stdio)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
            {
                throw new InvalidOperationException($"Stdio MCP server '{server.Id}' has no command configured.");
            }

            var options = new StdioClientTransportOptions
            {
                Name = serverName,
                Command = server.Command.Trim(),
                Arguments = server.Args?.ToList() ?? new List<string>(),
                WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                    ? workingDirectory
                    : null,
                InheritEnvironmentVariables = true,
                StandardErrorLines = line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    stderrLines.Enqueue(line.Trim());
                    while (stderrLines.Count > MaxStderrTailLines)
                    {
                        stderrLines.TryDequeue(out _);
                    }
                }
            };

            if (server.Env is { Count: > 0 })
            {
                options.EnvironmentVariables = server.Env.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (string?)kvp.Value,
                    StringComparer.Ordinal);
            }

            return new StdioClientTransport(options);
        }

        if (string.IsNullOrWhiteSpace(server.Url) ||
            !Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"HTTP/SSE MCP server '{server.Id}' has an invalid URL.");
        }

        var httpOptions = new HttpClientTransportOptions
        {
            Name = serverName,
            Endpoint = uri,
            TransportMode = server.Transport == McpTransportType.Sse
                ? HttpTransportMode.Sse
                : HttpTransportMode.AutoDetect,
            ConnectionTimeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        if (server.Headers is { Count: > 0 })
        {
            httpOptions.AdditionalHeaders = new Dictionary<string, string>(server.Headers, StringComparer.OrdinalIgnoreCase);
        }

        return new HttpClientTransport(httpOptions);
    }

    private static string SanitizeIdentifier(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var sanitized = Regex.Replace(value.Trim(), @"[^a-zA-Z0-9_-]", "_");
        sanitized = Regex.Replace(sanitized, @"_{2,}", "_").Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static string ComputeShortHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static string FormatStderrSuffix(ConcurrentQueue<string> stderrLines)
    {
        if (stderrLines.IsEmpty)
        {
            return string.Empty;
        }

        var tail = string.Join(" | ", stderrLines.TakeLast(5));
        if (tail.Length > 300)
        {
            tail = tail[..297] + "...";
        }
        return $" (stderr: {tail})";
    }

    private static async Task DisposeFailedClientOrTransportAsync(McpClient? client, IClientTransport? transport)
    {
        if (client != null)
        {
            try { await client.DisposeAsync(); } catch { }
            return;
        }

        if (transport is IAsyncDisposable asyncDisposable)
        {
            try { await asyncDisposable.DisposeAsync(); } catch { }
        }
        else if (transport is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }
    }

    private static async Task DisposeClientsAsync(IEnumerable<McpClient> clients)
    {
        foreach (var client in clients)
        {
            try { await client.DisposeAsync(); } catch { }
        }
    }
}
