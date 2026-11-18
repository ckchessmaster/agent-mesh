using System.Text.Json.Serialization;
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

/// <summary>
/// A configured Model Context Protocol (MCP) server in the platform catalog.
/// Can be assigned to one or more personas via <see cref="PersonaMcpServer"/>.
/// </summary>
public class McpServer
{
    /// <summary>Slug identifier, e.g. 'github', 'context7', 'postgres-readonly'.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description displayed in the catalog UI.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Transport protocol used to connect to the server.</summary>
    public McpTransportType Transport { get; set; } = McpTransportType.Stdio;

    /// <summary>Executable command for Stdio servers (e.g. 'npx', 'uvx', '/usr/local/bin/mcp').</summary>
    public string? Command { get; set; }

    /// <summary>JSON array of command-line arguments for Stdio servers (persisted as jsonb).</summary>
    public string ArgsJson { get; set; } = "[]";

    /// <summary>Endpoint URL for Http or Sse servers.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// AES-256-GCM encrypted JSON dictionary of default environment variables.
    /// Null when no environment variables are configured. Never serialized directly in API responses.
    /// </summary>
    [JsonIgnore]
    public string? EnvEncrypted { get; set; }

    /// <summary>
    /// AES-256-GCM encrypted JSON dictionary of default HTTP headers.
    /// Null when no headers are configured. Never serialized directly in API responses.
    /// </summary>
    [JsonIgnore]
    public string? HeadersEncrypted { get; set; }

    /// <summary>Global catalog toggle; disabled servers are skipped during turn dispatch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Default per-request / connection timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<PersonaMcpServer> PersonaMcpServers { get; set; } = new List<PersonaMcpServer>();
}
