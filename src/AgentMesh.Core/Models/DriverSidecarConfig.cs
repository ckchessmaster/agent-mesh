using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

public class DriverSidecarConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "tailscale"; // "tailscale" | "custom"

    // Tailscale-specific settings
    [JsonPropertyName("authKey")]
    public string? AuthKey { get; set; }

    [JsonPropertyName("secretRefName")]
    public string? SecretRefName { get; set; }

    [JsonPropertyName("secretRefKey")]
    public string? SecretRefKey { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("tags")]
    public string? Tags { get; set; } // e.g. "tag:agentmesh,tag:ci"

    [JsonPropertyName("userspaceMode")]
    public bool UserspaceMode { get; set; } = false;

    [JsonPropertyName("extraArgs")]
    public string? ExtraArgs { get; set; }

    // Custom container settings
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("arguments")]
    public List<string>? Arguments { get; set; }

    [JsonPropertyName("environmentVariables")]
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
}
