namespace AgentMesh.Core.Models;

/// <summary>
/// The closed set of functional buckets a memory may be filed under, and the only place that set is
/// spelled. The API tool surface publishes <see cref="All"/> as the JSON-schema <c>enum</c> for its
/// <c>category</c> argument, and the domain model refuses anything else, so the advertised contract and
/// the enforced contract cannot drift apart.
/// </summary>
/// <remarks>
/// Categories are stored as text rather than as a database enum or an integer: the buckets are read in
/// raw SQL, grouped by in the prompt stack, and are expected to grow by code change rather than by
/// migration. An integer column would make every row in <c>agent_memories</c> legible only with this
/// file open.
/// </remarks>
public static class AgentMemoryCategories
{
    /// <summary>Paths, SDK versions, port conventions, daemon sockets.</summary>
    public const string Environment = "environment";

    /// <summary>Required build/test flags, runner mocks (SignalR/Vitest hooks), compilation quirks.</summary>
    public const string BuildAndTest = "build_and_test";

    /// <summary>Domain invariants, repository rules (e.g. CLI rebuild contracts), architectural constraints.</summary>
    public const string CodebaseGotcha = "codebase_gotcha";

    /// <summary>Every allowable category, in the order the tool schema advertises them.</summary>
    public static readonly IReadOnlyList<string> All = [Environment, BuildAndTest, CodebaseGotcha];

    /// <summary>
    /// Maps a caller-supplied category onto its canonical wire form.
    /// </summary>
    /// <remarks>
    /// Matching is case-insensitive and whitespace-tolerant because the value arrives from a model's tool
    /// call, but the stored form is always the exact lowercase literal: a <c>WHERE category = 'build_and_test'</c>
    /// sweep must not miss rows a sloppier caller wrote as <c>"Build_And_Test"</c>.
    ///
    /// Unknown input returns false rather than defaulting to a bucket. A memory filed under the wrong
    /// heading is retrieved under the wrong heading forever, which is worse than a rejected write the
    /// agent can retry with a different category.
    /// </remarks>
    public static bool TryNormalize(string? raw, out string canonical)
    {
        var value = raw?.Trim();
        if (!string.IsNullOrEmpty(value))
        {
            foreach (var candidate in All)
            {
                if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = candidate;
                    return true;
                }
            }
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>True when <paramref name="raw"/> names a known category after normalization.</summary>
    public static bool IsAllowed(string? raw) => TryNormalize(raw, out _);

    /// <summary>The rejection text used verbatim in tool error payloads, so the caller sees what to send.</summary>
    public static string AllowedValuesHint() => string.Join(", ", All.Select(category => $"'{category}'"));
}
