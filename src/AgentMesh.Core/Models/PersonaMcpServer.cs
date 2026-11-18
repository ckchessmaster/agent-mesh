using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

/// <summary>
/// Assigns an <see cref="McpServer"/> to a <see cref="Persona"/> with optional
/// per-persona credential overrides and tool allow/block filters.
/// </summary>
public class PersonaMcpServer
{
    public string PersonaId { get; set; } = string.Empty;

    [JsonIgnore]
    public Persona? Persona { get; set; }

    public string McpServerId { get; set; } = string.Empty;

    [JsonIgnore]
    public McpServer? McpServer { get; set; }

    /// <summary>Per-persona enable toggle; allows disabling without deleting overrides.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// AES-256-GCM encrypted JSON dictionary of environment variable overrides merged onto
    /// <see cref="McpServer.EnvEncrypted"/> at dispatch time.
    /// </summary>
    [JsonIgnore]
    public string? EnvOverridesEncrypted { get; set; }

    /// <summary>
    /// AES-256-GCM encrypted JSON dictionary of HTTP header overrides merged onto
    /// <see cref="McpServer.HeadersEncrypted"/> at dispatch time.
    /// </summary>
    [JsonIgnore]
    public string? HeadersOverridesEncrypted { get; set; }

    /// <summary>
    /// Optional JSON array of allowed tool names (persisted as jsonb).
    /// Null means all tools from the server are allowed (subject to <see cref="BlockedToolsJson"/>).
    /// </summary>
    public string? AllowedToolsJson { get; set; }

    /// <summary>
    /// Optional JSON array of blocked tool names (persisted as jsonb).
    /// Any tool listed here is excluded even if matched by <see cref="AllowedToolsJson"/>.
    /// </summary>
    public string? BlockedToolsJson { get; set; }

    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
