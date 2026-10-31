namespace AgentMesh.Core.Workflows;

/// <summary>
/// Where a single macro coding turn ended, which is the only thing the Temporal workflow is
/// allowed to branch on.
/// </summary>
/// <remarks>
/// <para>
/// The workflow is a <em>macro</em> orchestrator: one <c>RunAgentCodingTurnAsync</c> activity
/// encapsulates the whole local loop (pre-turn reconciliation, prompt assembly, N inference steps,
/// N tool calls, post-turn checkpoint). Everything below therefore describes the state of the
/// <em>task</em> at the moment the turn yielded, not the state of any individual tool call.
/// </para>
/// <para>
/// The numeric values are part of the wire contract, not decoration. A <see cref="TurnOutcome"/>
/// is serialized into Temporal's event history by the worker and re-read on replay; renumbering or
/// reordering these members would make every in-flight workflow reinterpret a stored outcome as a
/// different one — a paused workflow would resume as a completed one. New states are appended.
/// </para>
/// </remarks>
public enum TurnOutcomeStatus
{
    /// <summary>Turn finished; proceed straight to the next automated step.</summary>
    Continue = 0,

    /// <summary>
    /// Blocked on a question in Job Chat (<c>post_job_message</c> with <c>requires_reply=true</c>).
    /// The workflow must suspend durably rather than poll.
    /// </summary>
    AwaitingInput = 1,

    /// <summary>A pull request was opened; awaiting an external code review decision.</summary>
    PrOpenedAwaitingReview = 2,

    /// <summary>All acceptance criteria met and verified.</summary>
    TaskCompleted = 3,

    /// <summary>3-strike failure limit reached; halted for lead intervention.</summary>
    StuckLoopDetected = 4,

    /// <summary>Fatal unrecoverable runtime exception.</summary>
    Failed = 5,
}

/// <summary>
/// The result of one macro coding turn.
/// </summary>
/// <param name="Status">What the workflow should do next.</param>
/// <param name="Message">Human-readable summary, or the halt reason for the terminal states.</param>
/// <param name="TargetPersona">
/// Who owes the reply when <see cref="Status"/> is <see cref="TurnOutcomeStatus.AwaitingInput"/>
/// ("operator", "astra", …). Surfaced so the workflow's status string names the blocker instead of
/// reporting a bare "waiting", which is the difference between an actionable dashboard and one that
/// makes a human read the transcript to find out whose turn it is.
/// </param>
/// <param name="PrUrl">Pull request URL for <see cref="TurnOutcomeStatus.PrOpenedAwaitingReview"/>.</param>
/// <param name="PrNumber">Pull request number, when the host knows it.</param>
/// <remarks>
/// The trailing <c>TurnSessionId</c> is deliberately <em>not</em> a field: it is a per-attempt
/// identity, and an activity that times out and retries would otherwise record a second session id
/// for the same task and orphan the first. See
/// <see cref="AgentMesh.Core.Services.IAgentTurnRunner"/> for where the pairing lives instead.
/// </remarks>
public record TurnOutcome(
    TurnOutcomeStatus Status,
    string? Message = null,
    string? TargetPersona = null,
    string? PrUrl = null,
    int? PrNumber = null)
{
    /// <summary>Convenience for the "keep going" case.</summary>
    public static TurnOutcome ContinueTurn(string? message = null) =>
        new(TurnOutcomeStatus.Continue, message);

    /// <summary>Convenience for the "blocked on a human" case.</summary>
    public static TurnOutcome Awaiting(string? targetPersona, string? message = null) =>
        new(TurnOutcomeStatus.AwaitingInput, message, targetPersona);

    /// <summary>Convenience for the "review my PR" case.</summary>
    public static TurnOutcome AwaitingReview(string? prUrl, int? prNumber, string? message = null) =>
        new(TurnOutcomeStatus.PrOpenedAwaitingReview, message, PrUrl: prUrl, PrNumber: prNumber);

    /// <summary>Convenience for the terminal success case.</summary>
    public static TurnOutcome Completed(string? message = null, string? prUrl = null, int? prNumber = null) =>
        new(TurnOutcomeStatus.TaskCompleted, message, PrUrl: prUrl, PrNumber: prNumber);

    /// <summary>Convenience for the two halt cases, which differ only in why they stopped.</summary>
    public static TurnOutcome Halt(TurnOutcomeStatus status, string reason) =>
        new(status is TurnOutcomeStatus.StuckLoopDetected or TurnOutcomeStatus.Failed
            ? status
            : throw new ArgumentException($"'{status}' is not a halt state", nameof(status)), reason);
}
