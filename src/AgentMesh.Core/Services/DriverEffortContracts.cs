namespace AgentMesh.Core.Services;

/// <summary>
/// The effort values each driver binary actually accepts, as one definition shared by the adapter
/// that forwards them and the API that advertises them.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the two sides used to disagree in the worst possible direction:
/// <c>AgyAdapter</c> rewrote <c>xhigh</c> and <c>max</c> down to <c>high</c> while the capability
/// surface advertised all five. A user who selected <c>max</c> got <c>high</c>, and the transcript
/// reported <c>max</c>.
/// </para>
/// <para>
/// The rule the members below encode: an effort is advertised only if the adapter forwards it
/// verbatim. When a value is not in the contract, the adapter omits the flag and lets the driver
/// apply its own default - it does not substitute a nearby value, because substituting is exactly
/// the "plausible default for missing telemetry" failure this whole change removes.
/// </para>
/// <para>
/// Verified against <c>--help</c> on the installed binaries: <c>agy --effort</c> documents
/// <c>(low|medium|high|xhigh|max)</c> and <c>claude --effort</c> documents
/// <c>(low, medium, high, xhigh, max)</c>.
/// </para>
/// </remarks>
public static class DriverEffortContracts
{
    public static readonly IReadOnlyList<string> Agy = new[] { "low", "medium", "high", "xhigh", "max" };

    public static readonly IReadOnlyList<string> Claude = new[] { "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    /// Copilot has no effort flag: <c>CopilotAdapter</c> builds <c>--model</c>, <c>--mode</c> and
    /// <c>--context</c> only. Advertising efforts here would put a control on a driver that ignores
    /// it.
    /// </summary>
    public static readonly IReadOnlyList<string> Copilot = Array.Empty<string>();

    /// <summary>
    /// An OpenAI-compatible request carries whatever the server documents; we do not invent a
    /// ladder for it.
    /// </summary>
    public static readonly IReadOnlyList<string> OpenAi = Array.Empty<string>();

    /// <summary>The effort ladder for a canonical adapter, empty when it has none.</summary>
    public static IReadOnlyList<string> For(string? adapter) => DriverAdapters.Canonicalize(adapter) switch
    {
        DriverAdapters.Agy => Agy,
        DriverAdapters.Claude => Claude,
        DriverAdapters.Copilot => Copilot,
        DriverAdapters.OpenAi => OpenAi,
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// True when <paramref name="effort"/> may be forwarded to <paramref name="adapter"/> verbatim.
    /// </summary>
    public static bool Supports(string? adapter, string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return false;

        var normalized = effort.Trim().ToLowerInvariant();
        return For(adapter).Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}
