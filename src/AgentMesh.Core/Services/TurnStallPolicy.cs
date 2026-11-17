namespace AgentMesh.Core.Services;

/// <summary>
/// The single set of numbers and status words that decide when a turn is no longer running.
/// </summary>
/// <remarks>
/// <para>
/// Three components have to agree about what "this turn is dead" means, and they ship independently:
/// the worker observes a turn and gives up on it, the API sweeps sessions nobody owns, and the CLI
/// waits on a subprocess driver that may never print again. When those thresholds live in three
/// files they drift, and the drift is silent: a sweeper tuned tighter than the worker's own ceiling
/// starts marking live turns <c>stopped</c> while the agent is mid-command, which is a worse bug than
/// the one it is trying to fix.
/// </para>
/// <para>
/// The ordering that must hold, and that <see cref="OrphanedTurnAfter"/> exists to encode:
/// the driver's stream bound fires first (a wedged subprocess should be reaped in minutes, not hours),
/// then the worker's observation ceiling, and only then may the sweeper touch a session. Every
/// threshold below is expressed relative to that ladder rather than as an independent guess.
/// </para>
/// </remarks>
public static class TurnStallPolicy
{
    /// <summary>
    /// How long the worker will watch one turn before declaring it stuck.
    /// </summary>
    /// <remarks>
    /// Named here rather than in the worker because the sweeper's threshold is defined as this number
    /// plus a grace window, and the sweeper lives in the API, which cannot reference the worker.
    /// </remarks>
    public static readonly TimeSpan MaxObservedTurn = TimeSpan.FromMinutes(110);

    /// <summary>
    /// Slack added to <see cref="MaxObservedTurn"/> before the sweeper may settle a session.
    /// </summary>
    /// <remarks>
    /// The worker polls on an interval and then has to run its post-turn activities, so a session can
    /// legitimately sit in a live status for a little past the ceiling. Anything under a few minutes
    /// risks the sweeper racing the worker and settling a turn the worker is about to conclude.
    /// </remarks>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(20);

    /// <summary>
    /// A live-status session quieter than this has outlived every owner that could have advanced it.
    /// </summary>
    public static TimeSpan OrphanedTurnAfter => MaxObservedTurn + OrphanGrace;

    /// <summary>
    /// Silence from a subprocess driver's stdout that means the driver is wedged, not thinking.
    /// </summary>
    /// <remarks>
    /// Deliberately generous. A subprocess driver (<c>agy</c>, <c>claude -p</c>, <c>copilot</c>) runs
    /// its own tools inside its own process, so a single <c>dotnet test</c> or <c>npm ci</c> can be
    /// legitimately silent for many minutes. The bound is not a command timeout — the CLI's own
    /// <see cref="CommandExecutionPolicy"/> clamps that to
    /// <see cref="CommandExecutionPolicy.MaxTimeoutSeconds"/> for tools the platform executes — it is
    /// the backstop for the case where the driver never comes back at all, so it has to sit above the
    /// longest ordinary build and below <see cref="MaxObservedTurn"/>.
    /// <para>
    /// The incident this was written for: <c>agy</c> hung inside its own shell tool for two hours, the
    /// adapter blocked on the stdout read the whole time, and the turn never produced a terminal event.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DriverStreamIdleLimit = TimeSpan.FromMinutes(20);

    /// <summary>Environment override for <see cref="DriverStreamIdleLimit"/>, in minutes.</summary>
    public const string DriverStreamIdleLimitEnvVar = "AGENTMESH_DRIVER_IDLE_TIMEOUT_MINUTES";

    /// <summary>
    /// Resolves the driver stream bound, honouring <see cref="DriverStreamIdleLimitEnvVar"/>.
    /// </summary>
    /// <remarks>
    /// A persona container that legitimately runs slow builds can raise this without a rebuild of the
    /// CLI image. Values below one minute are rejected rather than clamped: a bound that tight would
    /// kill ordinary turns, and an operator who types <c>0</c> or a negative number is asking for
    /// "no limit", which is exactly the failure mode this guard exists to prevent. Use the default.
    /// </remarks>
    public static TimeSpan ResolveDriverStreamIdleLimit(string? rawOverride)
    {
        if (string.IsNullOrWhiteSpace(rawOverride)) return DriverStreamIdleLimit;
        if (!double.TryParse(rawOverride, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes))
        {
            return DriverStreamIdleLimit;
        }
        if (minutes < 1) return DriverStreamIdleLimit;

        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Statuses that mean an agent is still working on this session.</summary>
    public static readonly string[] LiveTurnStatuses = ["thinking", "busy"];

    /// <summary>
    /// The status written when a live turn is settled without an agent.
    /// </summary>
    /// <remarks>
    /// <c>stopped</c> and not <c>error</c>: the agent may well have finished its edits and died during
    /// verification, and a status that reads as a failure invites an operator to discard work that is
    /// actually on disk. It is also already a terminal status to the turn runner, which treats
    /// <c>stopped</c> as "not working any more" and settles the workflow from the recorded facts.
    /// </remarks>
    public const string SettledStatus = "stopped";

    /// <summary>Prefix on the transcript row that explains a forced settle, for operators and tests.</summary>
    public const string SettleNoticePrefix = "[turn-stalled]";
}
