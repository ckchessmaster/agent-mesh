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

    // Companion proxy / daemon settings
    [JsonPropertyName("companionSetup")]
    public string? CompanionSetup { get; set; }

    [JsonPropertyName("companionCommand")]
    public string? CompanionCommand { get; set; }

    [JsonPropertyName("companionPort")]
    public int? CompanionPort { get; set; }

    /// <summary>
    /// Sanitizes extra arguments for Tailscale CLI. Tailscale `tailscale up` does not support `--ephemeral`
    /// as a command line flag (ephemerality is configured on the Tailscale auth key itself or OAuth credentials).
    /// </summary>
    public static string CleanTailscaleExtraArgs(string? extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs)) return string.Empty;
        var tokens = extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = tokens.Where(t =>
            !string.Equals(t, "--ephemeral", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(t, "-ephemeral", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("--ephemeral=", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("-ephemeral=", StringComparison.OrdinalIgnoreCase)
        );
        return string.Join(" ", filtered);
    }
}
