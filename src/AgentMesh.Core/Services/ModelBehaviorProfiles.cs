using System.Collections.Concurrent;

namespace AgentMesh.Core.Services;

/// <summary>
/// Which sampling-parameter names a strict OpenAI-compatible endpoint will reject as unknown.
/// </summary>
public enum SamplingDialect
{
    /// <summary>Resolve from the endpoint: loopback/self-hosted is Extended, anything else Strict.</summary>
    Auto,
    /// <summary>Only OpenAI-standard parameters may be sent (temperature, top_p, presence/frequency penalty).</summary>
    Strict,
    /// <summary>Vendor extensions (top_k, min_p, repetition_penalty, chat_template_kwargs) are acceptable.</summary>
    Extended,
}

/// <summary>
/// How reasoning the server sent us should travel back in assistant history. The vendor-correct
/// answer differs per serving stack, so the default is to mirror the inbound shape rather than
/// assert one.
/// </summary>
public enum ThinkingRoundTrip
{
    /// <summary>Never send reasoning back. Required for stacks with history-rendering bugs
    /// (e.g. Ollama's unclosed &lt;/think&gt; corruption class).</summary>
    Off,
    /// <summary>Default: reasoning arrived as a dedicated field → echo it as that field;
    /// reasoning arrived inline inside content → leave it inline. Never both.</summary>
    Echo,
    /// <summary>Force the dedicated reasoning field on assistant history messages.</summary>
    Field,
    /// <summary>Force inline &lt;think&gt;…&lt;/think&gt; re-embedding inside content (guarded:
    /// only when the tags are balanced).</summary>
    Inline,
}

/// <summary>
/// The resolved request-shaping policy for one model on one endpoint. Like
/// <see cref="ModelContextLimits"/>, this is a <em>guess about a deployment</em>, not a provider
/// answer — callers that surface it must carry <see cref="Source"/> and <see cref="IsDefault"/>.
/// </summary>
/// <remarks>
/// Precedence, highest first: explicit preset override → learned endpoint facts → family table →
/// empty default. The empty default reproduces the pre-registry behavior byte-for-byte (send no
/// sampling parameters, echo what the server echoed), so an unknown model can regress nothing.
/// </remarks>
public sealed record ModelBehaviorProfile(
    IReadOnlyDictionary<string, double>? Sampling,
    SamplingDialect Dialect,
    ThinkingRoundTrip ThinkingRoundTripMode,
    bool IsDefault,
    string Source)
{
    /// <summary>True when the parameter may be sent given the dialect and learned rejections.</summary>
    public bool MaySendSamplingKey(string model, string key)
    {
        if (Sampling == null || !Sampling.ContainsKey(key)) return false;
        if (!StandardSamplingKeys.Contains(key) && Dialect != SamplingDialect.Extended) return false;
        return !ModelBehaviorProfiles.IsSamplingKeyRejected(model, key);
    }

    public static readonly HashSet<string> StandardSamplingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "temperature", "top_p", "presence_penalty", "frequency_penalty", "max_tokens", "seed",
    };

    public static readonly HashSet<string> ExtendedSamplingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "top_k", "min_p", "repetition_penalty", "chat_template_kwargs", "thinking_budget", "reasoning_effort",
    };
}

/// <summary>
/// The registry, learned endpoint facts, and family table that produce
/// <see cref="ModelBehaviorProfile"/> values. Same lifetime policy as the learned context limits
/// in <see cref="ModelContextLimits"/>: process-static, optionally surfaced through the platform
/// reporter channel for display.
/// </summary>
public static class ModelBehaviorProfiles
{
    // ---- learned request-shape state (in-process, same lifetime policy as learned context limits) ----

    private static readonly ConcurrentDictionary<string, HashSet<string>> RejectedSamplingKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> ReasoningFieldRejectedByModel = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RejectedKeysLock = new();

    /// <summary>Records that an endpoint named this sampling key as invalid for this model.
    /// Returns true when the rejection is newly learned (worth a log line).</summary>
    public static bool RecordRejectedSamplingKey(string? model, string key)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var set = RejectedSamplingKeys.GetOrAdd(model.Trim(), _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (RejectedKeysLock)
        {
            return set.Add(key);
        }
    }

    public static bool IsSamplingKeyRejected(string? model, string key)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (!RejectedSamplingKeys.TryGetValue(model.Trim(), out var set)) return false;
        lock (RejectedKeysLock)
        {
            return set.Contains(key);
        }
    }

    /// <summary>True when this model's endpoint rejected assistant history carrying a reasoning
    /// field; the round-trip degrades to Inline (balanced tags) or Off.</summary>
    public static void RecordReasoningFieldRejected(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model)) ReasoningFieldRejectedByModel[model.Trim()] = true;
    }

    public static bool IsReasoningFieldRejected(string? model) =>
        !string.IsNullOrWhiteSpace(model) && ReasoningFieldRejectedByModel.TryGetValue(model.Trim(), out _);

    /// <summary>Test isolation; learned state is process-static by design otherwise.</summary>
    public static void ClearLearnedState()
    {
        RejectedSamplingKeys.Clear();
        ReasoningFieldRejectedByModel.Clear();
    }

    // ---- family table (needle-matched against the model id, same style as ModelContextLimits) ----

    private record FamilyEntry(string[] Match, Dictionary<string, double>? Sampling, ThinkingRoundTrip Thinking, string DialectHint);

    private static readonly FamilyEntry[] KnownFamilies =
    {
        // Qwen3-family thinking-mode defaults, verbatim from qwen.readthedocs.io quickstart
        // (temp 0.6, top_p 0.95, top_k 20, min_p 0; never greedy). Non-thinking presets differ
        // (0.7/0.8 + presence_penalty 1.5) — a deployment running the model in non-thinking mode
        // must say so via a preset override, because the client cannot see the server's template
        // default. DialectHint: the numbers include extended keys, so they ride only Extended.
        new(new[] { "qwen3", "qwen-3", "qwq", "flash-next" },
            new() { ["temperature"] = 0.6, ["top_p"] = 0.95, ["top_k"] = 20, ["min_p"] = 0 },
            ThinkingRoundTrip.Echo, "extended"),
    };

    /// <summary>
    /// Resolves the profile for a model id and endpoint. Sampling is only returned when the
    /// endpoint resolves to Extended dialect, so strict public APIs keep receiving exactly the
    /// fields they always did.
    /// </summary>
    public static ModelBehaviorProfile Resolve(
        string? model,
        bool endpointIsLoopback,
        SamplingDialect? presetDialectOverride = null,
        IReadOnlyDictionary<string, double>? presetSamplingOverride = null,
        ThinkingRoundTrip? presetThinkingOverride = null)
    {
        var dialect = presetDialectOverride
            ?? (endpointIsLoopback ? SamplingDialect.Extended : SamplingDialect.Strict);

        if (presetSamplingOverride != null)
        {
            return new ModelBehaviorProfile(
                presetSamplingOverride, dialect,
                presetThinkingOverride ?? ThinkingRoundTrip.Echo,
                IsDefault: false, Source: "preset");
        }

        var lowered = (model ?? string.Empty).ToLowerInvariant();
        foreach (var family in KnownFamilies)
        {
            if (family.Match.Any(needle => lowered.Contains(needle, StringComparison.Ordinal)))
            {
                var sendSampling = dialect == SamplingDialect.Extended ? family.Sampling : null;
                return new ModelBehaviorProfile(
                    sendSampling, dialect,
                    presetThinkingOverride ?? family.Thinking,
                    IsDefault: false, Source: $"familyTable:{family.Match[0]}");
            }
        }

        // Unknown model: today's behavior. No sampling keys, echo thinking shape.
        return new ModelBehaviorProfile(
            null, dialect, presetThinkingOverride ?? ThinkingRoundTrip.Echo,
            IsDefault: true, Source: "default");
    }

    /// <summary>
    /// Guards inline &lt;think&gt;…&lt;/think&gt; re-embedding: a truncated or unbalanced block
    /// poisons every subsequent turn's rendering (the unclosed-&lt;/think&gt; failure class).
    /// Returns the input only when it is safe to place inside content; null otherwise.
    /// </summary>
    public static string? SanitizeInlineThinking(string? reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning)) return null;
        var trimmed = reasoning.Trim();
        var opens = trimmed.Split("<think>").Length - 1;
        var closes = trimmed.Split("</think>").Length - 1;
        if (opens != closes) return null;
        // A block that ends mid-thought (more opens than closes was checked; also reject a
        // trailing unclosed tag pair order violation: </think> appearing before any <think>).
        var firstClose = trimmed.IndexOf("</think>", StringComparison.Ordinal);
        var firstOpen = trimmed.IndexOf("<think>", StringComparison.Ordinal);
        if (firstClose >= 0 && firstOpen < 0) return null;
        // Re-embed WITHOUT the model's own tags (the server's template adds them): strip them
        // so we can re-wrap exactly once.
        var inner = trimmed.Replace("<think>", "").Replace("</think>", "").Trim();
        return inner.Length == 0 ? null : inner;
    }
}
