namespace AgentMesh.Cli.Adapters;

/// <summary>
/// Configuration settings for <see cref="OpenAiAdapter"/> context management and SSE streaming resilience.
/// </summary>
public class OpenAISettings
{
    public const int Default512kContextTokens = 524_288;
    public const int DefaultCompactionThresholdPercent = 75;
    public const int DefaultStreamChunkIdleTimeoutSeconds = 120;

    /// <summary>
    /// Optional explicit context window in tokens configured on the agent/driver profile (e.g., 524288 for 512k).
    /// Respected directly without clamping to legacy 131072 tables.
    /// </summary>
    public int? ContextWindowTokens { get; set; }

    /// <summary>
    /// Optional human-readable context size string (e.g., "512k", "524288", "1m").
    /// </summary>
    public string? DefaultContextSize { get; set; }

    /// <summary>
    /// Percentage of the resolved context window at which proactive history compaction triggers (default 75%).
    /// </summary>
    public int? CompactionThresholdPercent { get; set; } = DefaultCompactionThresholdPercent;

    /// <summary>
    /// Per-chunk idle timeout in seconds enforced once the first SSE data chunk has arrived (default 120s).
    /// Initial connection and prompt prefill (TTFT) rely on the outer HTTP timeout instead.
    /// </summary>
    public int? StreamChunkIdleTimeoutSeconds { get; set; } = DefaultStreamChunkIdleTimeoutSeconds;
}
