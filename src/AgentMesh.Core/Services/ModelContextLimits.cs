using System.Collections.Concurrent;

namespace AgentMesh.Core.Services;

/// <summary>
/// The single source of truth for turning a model id plus a user-selected context option into a
/// token budget.
/// </summary>
/// <remarks>
/// <para>
/// This used to live inside <c>OpenAiAdapter.ResolveModelContextLimit</c> while
/// <c>SystemController</c> kept its own, unrelated table of context options for the picker. Two
/// silent peers: the picker advertised <c>256k</c>, the adapter clamped the run to 128k, and
/// nothing in the response told the user which one had won. Moving the resolution here means the
/// API that renders the choice and the adapter that honours it cannot drift apart.
/// </para>
/// <para>
/// The model table is a <see cref="Fallback"/> reading, not a provider answer. It is a guess
/// about whatever server happens to sit behind an OpenAI-compatible endpoint, so callers that
/// surface this to a human must say so (see <see cref="ModelContextResolution.IsFallback"/>).
/// </para>
/// </remarks>
public static class ModelContextLimits
{
    /// <summary>
    /// The budget applied when neither the user, a learned overflow limit, nor the model table says
    /// anything. Defaults to 1M (1,048,576 tokens) and auto-detects the true limit if the server
    /// returns a context-window overflow error.
    /// </summary>
    public const int DefaultTokenLimit = 1_048_576;

    /// <summary>
    /// Largest window the picker may offer. Sized to the widest contract any driver we forward to
    /// was verified to accept (<c>agy</c> reports 1M-class models; <c>claude --autocompact</c>
    /// caps at 1M).
    /// </summary>
    public const int MaxSelectableTokenLimit = 1_048_576;

    private static readonly ConcurrentDictionary<string, int> LearnedLimits =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a model's true context limit discovered from a server response or context-exceeded error.
    /// </summary>
    public static void RecordLearnedLimit(string? model, int tokens)
    {
        if (string.IsNullOrWhiteSpace(model) || tokens <= 0) return;
        LearnedLimits[model.Trim()] = tokens;
    }

    /// <summary>
    /// Returns a model's learned context limit if one has been recorded at runtime.
    /// </summary>
    public static int? GetLearnedLimit(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        return LearnedLimits.TryGetValue(model.Trim(), out var tokens) && tokens > 0 ? tokens : null;
    }

    /// <summary>
    /// Clears runtime learned context limits (primarily for test isolation).
    /// </summary>
    public static void ClearLearnedLimits() => LearnedLimits.Clear();

    /// <summary>
    /// Known model families and the windows they were verified against. A model that is not
    /// listed here has no known limit - the caller gets <see cref="DefaultTokenLimit"/> with
    /// <see cref="ModelContextResolution.IsFallback"/> set.
    /// </summary>
    private static readonly (string[] Match, int Tokens)[] KnownModels =
    {
        (new[] { "qwen3", "qwen-3", "flash-next" }, 524_288), // 512k
        (new[] { "qwen2.5", "qwen-2.5" }, 131_072),      // 128k
        (new[] { "llama-3", "llama3" }, 131_072),        // 128k
        (new[] { "gpt-4o" }, 131_072),                   // 128k
        (new[] { "deepseek" }, 131_072)                  // 128k
    };

    /// <summary>
    /// Resolves the effective token budget for a run.
    /// </summary>
    /// <param name="model">The model id the run will use. May be empty for driver defaults.</param>
    /// <param name="contextSizeOption">
    /// The option the user picked (<c>"200k"</c>, <c>"1m"</c>, a raw token count, or
    /// <c>"default"</c>/empty to let the model table decide).
    /// </param>
    public static ModelContextResolution Resolve(string? model, string? contextSizeOption)
    {
        var requested = ParseOption(contextSizeOption);
        if (requested != null)
        {
            return new ModelContextResolution(
                requested.Value,
                IsFallback: false,
                Source: $"option:{contextSizeOption!.Trim().ToLowerInvariant()}");
        }

        if (GetLearnedLimit(model) is { } learned)
        {
            return new ModelContextResolution(
                learned,
                IsFallback: false,
                Source: "learned:overflow");
        }

        var lowered = (model ?? string.Empty).ToLowerInvariant();
        foreach (var (match, tokens) in KnownModels)
        {
            foreach (var needle in match)
            {
                if (lowered.Contains(needle, StringComparison.Ordinal))
                {
                    return new ModelContextResolution(tokens, IsFallback: false, Source: $"modelTable:{needle}");
                }
            }
        }

        return new ModelContextResolution(DefaultTokenLimit, IsFallback: true, Source: "default");
    }

    /// <summary>
    /// Convenience for callers that only ever need the number (the adapters).
    /// </summary>
    public static int ResolveTokens(string? model, string? contextSizeOption)
        => Resolve(model, contextSizeOption).Tokens;

    /// <summary>
    /// Parses a context option into tokens. Returns null for anything that is not a size
    /// (<c>"default"</c>, <c>"auto"</c>, <c>"long_context"</c>, empty, garbage) so the caller
    /// falls through to the model table rather than inventing a number.
    /// </summary>
    public static int? ParseOption(string? contextSizeOption)
    {
        if (string.IsNullOrWhiteSpace(contextSizeOption)) return null;

        var normalized = contextSizeOption.Trim().ToLowerInvariant();
        if (normalized is "default" or "auto" or "long_context" or "long-context") return null;

        if (normalized.EndsWith('k') && int.TryParse(normalized[..^1], out var thousands) && thousands > 0)
            return thousands * 1_024;

        if (normalized.EndsWith('m') && int.TryParse(normalized[..^1], out var millions) && millions > 0)
            return millions * 1_024 * 1_024;

        if (int.TryParse(normalized, out var exact) && exact > 0) return exact;

        return null;
    }

    /// <summary>
    /// True when a driver can actually be told how big its context is.
    /// </summary>
    /// <remarks>
    /// A driver with no CLI flag for it cannot be told, which makes the picker a no-op on that
    /// driver. <c>agy</c> is the case that motivated this: the selector offered 128k-2M and the
    /// adapter never forwarded any of it.
    /// </remarks>
    public static bool SupportsContextSelection(string? adapter)
    {
        var normalized = adapter?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "agy" or "antigravity" => false,
            "generic" => false,
            null or "" => false,
            _ => true
        };
    }
}

/// <summary>
/// A context budget plus the provenance needed to display it honestly.
/// </summary>
/// <param name="Tokens">The effective window in tokens.</param>
/// <param name="IsFallback">
/// True when the number came from the catch-all default rather than from a user choice or a
/// known model. Surfacing this is the difference between "128k" and "we had no idea, so 128k".
/// </param>
/// <param name="Source">Which branch produced the value, for logs and tooltips.</param>
public readonly record struct ModelContextResolution(int Tokens, bool IsFallback, string Source);
