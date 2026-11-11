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

    /// <summary>
    /// Infers an adapter when no adapter is recorded on the driver row.
    /// Returns null if the driver ID doesn't match any known adapter family and no transport implies one.
    /// </summary>
    public static string? InferFromDriverId(
        string? driverId,
        string? endpoint = null,
        AgentMesh.Core.Enums.RuntimeType? runtimeType = null)
    {
        if (runtimeType is AgentMesh.Core.Enums.RuntimeType.OpenAiCompatible or AgentMesh.Core.Enums.RuntimeType.LocalOllama ||
            (!string.IsNullOrWhiteSpace(endpoint) &&
             endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            return OpenAi;
        }

        var id = driverId?.Trim().TrimStart('@').ToLowerInvariant() ?? string.Empty;
        if (id.StartsWith("copilot", StringComparison.OrdinalIgnoreCase) || id.Contains("copilot", StringComparison.OrdinalIgnoreCase))
            return Copilot;

        if (id.StartsWith("claude", StringComparison.OrdinalIgnoreCase) || id.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return Claude;

        if (id.StartsWith("openai", StringComparison.OrdinalIgnoreCase) || id.Contains("openai", StringComparison.OrdinalIgnoreCase))
            return OpenAi;

        if (id.StartsWith("agy", StringComparison.OrdinalIgnoreCase) ||
            id.EndsWith("-agy", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("antigravity", StringComparison.OrdinalIgnoreCase))
            return Agy;

        return null;
    }

    /// <summary>
    /// Resolves the adapter for a driver, checking explicit adapter first, then inferring from driverId / transport,
    /// defaulting to fallback if provided.
    /// </summary>
    public static string ResolveAdapter(
        string? adapter,
        string? driverId = null,
        string? endpoint = null,
        AgentMesh.Core.Enums.RuntimeType? runtimeType = null,
        string fallback = Agy)
    {
        var canonical = Canonicalize(adapter);
        if (canonical != null) return canonical;

        var inferred = InferFromDriverId(driverId, endpoint, runtimeType);
        return inferred ?? fallback;
    }
}
