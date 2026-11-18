using System.Text.Json.Serialization;
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

/// <summary>
/// Fully resolved, decrypted per-persona MCP server configuration delivered to a runner for a turn.
/// </summary>
public class ResolvedMcpServerConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("transport")]
    public McpTransportType Transport { get; set; } = McpTransportType.Stdio;

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("allowedTools")]
    public List<string>? AllowedTools { get; set; }

    [JsonPropertyName("blockedTools")]
    public List<string>? BlockedTools { get; set; }
}
