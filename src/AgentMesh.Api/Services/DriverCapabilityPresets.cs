using AgentMesh.Core.Services;

namespace AgentMesh.Api.Services;

/// <summary>
/// Bundled capability presets for drivers that expose no discovery surface.
/// </summary>
/// <remarks>
/// <para>
/// A preset is a claim about a CLI contract, not about an account. It is safe when it describes
/// what the binary accepts (<c>claude --effort</c> really does take low..max) and unsafe when it
/// pretends to know what a specific install can run - that is exactly how the old
/// <c>GetDefaultAgyCatalog()</c> handed a five-model catalogue to a host where <c>agy</c> was
/// signed out. Only the first kind of claim belongs in this file.
/// </para>
/// <para>
/// Every value below was re-checked against <c>--help</c> on the installed binary on the build
/// host. Where a binary is not installed (copilot), that is stated on the entry: an unverified
/// preset is narrower than a verified one, never wider.
/// </para>
/// </remarks>
public static class DriverCapabilityPresets
{
    /// <summary>Effort levels <c>claude --help</c> documents for <c>--effort</c>.</summary>
    public static readonly IReadOnlyList<string> ClaudeEfforts =
        new[] { "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    /// Context options for <c>claude --autocompact</c>, which takes <c>auto</c> or a token count
    /// between 100k and 1M. Nothing outside that range is offered because the binary rejects it.
    /// </summary>
    public static readonly IReadOnlyList<string> ClaudeContextSizes =
        new[] { "default", "auto", "100k", "200k", "500k", "1m" };

    /// <summary>
    /// Model aliases <c>claude --help</c> documents. Aliases are what the CLI is designed to
    /// receive; full model ids are also accepted but are install-specific, so they are not
    /// bundled - an operator who knows their install adds them via <c>models</c>.
    /// </summary>
    private static readonly (string Id, string Name)[] ClaudeModelAliases =
    {
        ("fable", "Fable (latest-model alias)"),
        ("sonnet", "Sonnet (latest-model alias)"),
        ("opus", "Opus (latest-model alias)"),
        ("haiku", "Haiku (latest-model alias)")
    };

    /// <summary>
    /// Context tiers <c>CopilotAdapter</c> forwards via <c>--context</c>.
    /// </summary>
    /// <remarks>
    /// <c>2m</c> is deliberately absent: the adapter maps <c>2m</c> onto the CLI's <c>1m</c> tier,
    /// so advertising it would promise a window the driver cannot request.
    /// </remarks>
    public static readonly IReadOnlyList<string> CopilotContextSizes =
        new[] { "default", "long_context", "128k", "200k", "1m" };

    /// <summary>
    /// A preset reading for an adapter, or null when we have no verified opinion about it.
    /// </summary>
    /// <remarks>
    /// <c>agy</c> and <c>openai</c> are probed, not preset, and <c>generic</c> has no discovery
    /// surface and no opinion to offer. Returning null here is what stopped the API from
    /// inventing a catalogue for a driver it could not read.
    /// </remarks>
    public static DriverCapabilityReading? For(string adapter, DateTimeOffset observedAt)
    {
        switch (DriverAdapters.Canonicalize(adapter))
        {
            case DriverAdapters.Claude:
                return new DriverCapabilityReading(
                    Adapter: DriverAdapters.Claude,
                    Models: ClaudeModelAliases
                        .Select(m => new DriverModelCapability(
                            m.Id,
                            m.Name,
                            ClaudeEfforts,
                            ClaudeContextSizes))
                        .ToList(),
                    ModelSource: CapabilitySourceKind.StaticPreset,
                    ObservedAt: observedAt,
                    SupportsContextSelection: true,
                    // Verified against `claude --help` on the installed binary, not against an
                    // account: the picker must say "bundled preset - not verified by driver".
                    EffortSource: "preset:claude-cli-contract");

            case DriverAdapters.Copilot:
                // The Copilot catalogue is account-scoped and there is no discovery command, so
                // the only honest bundled entry is "let the CLI choose". Anything else must come
                // from the operator's `models` array.
                //
                // The copilot binary is not installed on the build host, so unlike the claude
                // preset this one is asserted against CopilotAdapter's own argument construction
                // rather than `copilot --help`. That is why it is one entry wide.
                return new DriverCapabilityReading(
                    Adapter: DriverAdapters.Copilot,
                    Models: new List<DriverModelCapability>
                    {
                        new("auto", "Auto (Copilot picks the model)", Array.Empty<string>(), CopilotContextSizes)
                    },
                    ModelSource: CapabilitySourceKind.StaticPreset,
                    ObservedAt: observedAt,
                    SupportsContextSelection: true,
                    EffortSource: null);

            default:
                return null;
        }
    }

    /// <summary>
    /// Context options to offer a driver that supports selection but was probed without per-model
    /// sizes (an OpenAI <c>/models</c> response carries ids and nothing else).
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="ModelContextLimits.MaxSelectableTokenLimit"/>: the picker must not
    /// offer a window the runtime will silently clamp. <c>2m</c> used to be offered for every
    /// OpenAI-compatible model while <c>ResolveModelContextLimit</c> capped the run at 128k.
    /// </remarks>
    public static IReadOnlyList<string> GenericContextSizes { get; } =
        new[] { "default", "32k", "64k", "128k", "200k", "256k", "500k", "512k", "1m" };
}
