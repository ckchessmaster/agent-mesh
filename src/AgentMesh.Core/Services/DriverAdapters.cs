namespace AgentMesh.Core.Services;

/// <summary>
/// Canonical adapter identifiers as they appear in a driver's <c>capabilitiesJson.adapter</c>.
/// </summary>
/// <remarks>
/// These strings were previously scattered across <c>SystemController</c>, <c>AgentsController</c>,
/// <c>AgyDriverQuotaProbe</c> and the dashboard as bare literals, which is how the same driver
/// ended up being called <c>"agy"</c> in one place and <c>"antigravity"</c> in another.
/// </remarks>
public static class DriverAdapters
{
    public const string Agy = "agy";
    public const string Antigravity = "antigravity";
    public const string Claude = "claude";
    public const string Copilot = "copilot";
    public const string OpenAi = "openai";
    public const string Generic = "generic";

    /// <summary>
    /// Maps a recorded adapter string to its canonical form, or null when it is unrecognized.
    /// </summary>
    public static string? Canonicalize(string? adapter)
    {
        var normalized = adapter?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return null;

        return normalized switch
        {
            Agy or Antigravity => Agy,
            Claude or "claude-code" => Claude,
            Copilot or "github-copilot" => Copilot,
            OpenAi or "ollama" or "vllm" or "lmstudio" or "lm-studio" => OpenAi,
            Generic or "custom" or "command" => Generic,
            _ => normalized
        };
    }
}
