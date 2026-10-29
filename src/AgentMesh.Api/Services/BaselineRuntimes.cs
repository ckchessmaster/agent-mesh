using AgentMesh.Api.Controllers;
using AgentMesh.Core.Services;

namespace AgentMesh.Api.Services;

/// <summary>
/// The adapter-level interaction surface: execution modes, tiers, and the fallback context ladder.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <c>SystemController</c> and, importantly, stripped of model lists. Modes and tiers
/// are concepts <em>our adapters</em> implement (<c>AgyAdapter</c> maps <c>--mode</c>,
/// <c>CopilotAdapter</c> maps <c>--mode</c>), so a static table is an accurate statement about our
/// own code. A model list is a statement about someone else's account or server, and every bug in
/// the old table came from treating those two the same way.
/// </para>
/// <para>
/// Entries therefore ship an empty <see cref="RuntimeCapabilitiesDto.Models"/> with
/// <c>modelSource = "unavailable"</c>: the baseline exists so the dashboard can render mode and
/// tier pickers before it has resolved any driver, and must never be mistaken for a catalogue.
/// Per-driver models come from <see cref="IDriverCapabilityProbe"/>.
/// </para>
/// </remarks>
public static class BaselineRuntimes
{
    /// <summary>
    /// The context ladder for a driver that supports selection but was not probed with per-model
    /// sizes. Bounded by <see cref="ModelContextLimits.MaxSelectableTokenLimit"/>: offering 2m to a
    /// model the runtime clamps at 128k is the drift this change removes.
    /// </summary>
    /// <remarks>
    /// Hands out a fresh list per call: these DTOs are mutable <c>List</c>s and a shared instance
    /// would let one request's post-processing leak into the next caller's response.
    /// </remarks>
    public static List<ContextSizeOptionDto> DefaultContextSizes() => new()
    {
        new("default", "Default Context", "Let the driver use its own default window"),
        new("32k", "32k Tokens", "Compact 32k token context window"),
        new("64k", "64k Tokens", "Medium 64k token context window"),
        new("128k", "128k Tokens", "Standard 128k token context window"),
        new("200k", "200k Tokens", "Extended 200k token context window"),
        new("256k", "256k Tokens", "Long 256k token context window"),
        new("500k", "500k Tokens", "Very long 500k token context window"),
        new("512k", "512k Tokens", "Extended 512k token context window"),
        new("1m", "1M Tokens", "Maximum 1M token context window")
    };

    /// <summary>
    /// <c>agy --help</c> documents <c>--mode (accept-edits, auto, plan)</c>.
    /// </summary>
    private static List<ModeOptionDto> AgyModes() => new()
    {
        new("auto", "Auto (Autonomous)", "Autonomous execution with step-by-step tool invocation"),
        new("plan", "Plan Only", "Explores codebase and produces an architectural plan without making edits"),
        new("accept-edits", "Accept Edits", "Automatically approves and applies code edits")
    };

    /// <summary>
    /// Copilot modes as <c>CopilotAdapter</c> maps them: <c>plan</c> passes through,
    /// <c>auto</c>/<c>autopilot</c> map to the CLI's <c>autopilot</c>, anything else is interactive.
    /// </summary>
    private static List<ModeOptionDto> CopilotModes() => new()
    {
        new("autopilot", "Autopilot", "Executes prompt autonomously up to continuation limit"),
        new("plan", "Plan Mode", "Analyzes task and creates an implementation plan"),
        new("interactive", "Interactive", "Standard interactive guidance and chat")
    };

    private static List<ModeOptionDto> ClaudeModes() => new()
    {
        new("auto", "Auto", "Autonomous task execution"),
        new("plan", "Plan", "Planning only")
    };

    private static List<ModeOptionDto> OpenAiModes() => new()
    {
        new("auto", "Auto (Autonomous)", "Autonomous tool loop using OpenAI function calling"),
        new("plan", "Plan Only", "Explores codebase and plans changes without modifying files"),
        new("direct", "Direct Chat", "Direct conversational responses without executing tools")
    };

    private static List<TierOptionDto> CopilotTiers() => new()
    {
        new("auto", "Auto", "Automatically select the best available model for each task"),
        new("efficiency", "Efficiency", "Favors faster models while preserving quality needed for the task"),
        new("balance", "Balance", "Balances model quality and speed while adapting to each task"),
        new("intelligence", "Intelligence", "Favors stronger models for complex work while remaining adaptive")
    };

    /// <summary>
    /// Copilot's context tiers as <c>CopilotAdapter</c> forwards them: the CLI takes
    /// <c>--context default|long_context</c>, and every size option collapses onto one of those two.
    /// </summary>
    /// <remarks>
    /// <c>2m</c> is not offered. The adapter used to map <c>2m</c> onto <c>long_context</c>
    /// (i.e. the same tier as 1m), so advertising it promised a window the driver cannot request.
    /// </remarks>
    private static List<ContextSizeOptionDto> CopilotContextSizes() => new()
    {
        new("default", "Default Context", "Standard context tier for normal token usage"),
        new("long_context", "Long Context", "Extended long-context tier for eligible models"),
        new("128k", "128k Tokens", "Maps to the default tier"),
        new("200k", "200k Tokens", "Maps to the default tier"),
        new("1m", "1M Tokens", "Maps to the long-context tier")
    };

    /// <summary>
    /// Claude's context ladder: <c>claude --autocompact</c> takes <c>auto</c> or a token count
    /// between 100k and 1M.
    /// </summary>
    private static List<ContextSizeOptionDto> ClaudeContextSizes() => new()
    {
        new("default", "Driver Default", "Leave the auto-compact window at the CLI default"),
        new("auto", "Auto Compact", "Automatic context compaction managed by Claude Code"),
        new("100k", "100k Tokens", "Compact 100k token auto-compact window"),
        new("200k", "200k Tokens", "Standard 200k token context window"),
        new("500k", "500k Tokens", "Extended 500k token context window"),
        new("1m", "1M Tokens", "Maximum 1M token auto-compact window")
    };

    /// <summary>
    /// Every baseline entry, keyed by canonical adapter name.
    /// </summary>
    public static Dictionary<string, RuntimeCapabilitiesDto> All() => new(StringComparer.OrdinalIgnoreCase)
    {
        // agy has no context-size flag and AgyAdapter forwards none, so the picker is hidden.
        [DriverAdapters.Agy] = Entry(
            DriverAdapters.Agy, "Antigravity CLI (agy)", AgyModes(),
            supportsContextSelection: false,
            contextSizes: null,
            effortSource: $"contract:{DriverAdapters.Agy}"),

        [DriverAdapters.Copilot] = Entry(
            DriverAdapters.Copilot, "GitHub Copilot CLI", CopilotModes(),
            supportsContextSelection: true,
            contextSizes: CopilotContextSizes(),
            tiers: CopilotTiers(),
            // CopilotAdapter builds --model/--mode/--context and no effort flag, so the effort
            // picker has nothing to bind to on this driver.
            effortSource: $"contract:{DriverAdapters.Copilot}"),

        [DriverAdapters.Claude] = Entry(
            DriverAdapters.Claude, "Claude Code CLI", ClaudeModes(),
            supportsContextSelection: true,
            contextSizes: ClaudeContextSizes(),
            effortSource: $"contract:{DriverAdapters.Claude}"),

        [DriverAdapters.OpenAi] = Entry(
            DriverAdapters.OpenAi, "OpenAI Compatible Server", OpenAiModes(),
            supportsContextSelection: true,
            contextSizes: DefaultContextSizes(),
            effortSource: $"contract:{DriverAdapters.OpenAi}"),

        [DriverAdapters.Generic] = Entry(
            DriverAdapters.Generic, "Custom Command Agent", new List<ModeOptionDto>(),
            supportsContextSelection: false,
            contextSizes: null,
            effortSource: $"contract:{DriverAdapters.Generic}")
    };

    /// <summary>
    /// The baseline entry for one adapter, or null when we have no interaction surface for it.
    /// </summary>
    public static RuntimeCapabilitiesDto? For(string? adapter)
    {
        var canonical = DriverAdapters.Canonicalize(adapter);
        if (canonical == null) return null;

        var all = All();
        return all.TryGetValue(canonical, out var entry) ? entry : null;
    }

    private static RuntimeCapabilitiesDto Entry(
        string adapter,
        string name,
        List<ModeOptionDto> modes,
        bool supportsContextSelection,
        List<ContextSizeOptionDto>? contextSizes,
        string effortSource,
        List<TierOptionDto>? tiers = null)
        => new(
            Adapter: adapter,
            Name: name,
            Modes: modes,
            // Empty on purpose. A baseline model list is what made an unreadable driver look
            // readable; models now arrive per-driver from the probe.
            Models: new List<ModelCapabilityDto>(),
            Tiers: tiers,
            ContextSizes: contextSizes,
            SupportsContextSelection: supportsContextSelection,
            ModelSource: "unavailable",
            EffortSource: effortSource,
            UnavailableReason:
                "The baseline map describes interaction modes only. Model lists are resolved per " +
                "driver from the driver itself and are never bundled here.");
}
