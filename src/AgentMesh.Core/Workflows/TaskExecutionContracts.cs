namespace AgentMesh.Core.Workflows;

/// <summary>
/// A chat reply delivered into a running task workflow.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Async</c> suffix convention matters here. Temporal's .NET SDK derives a handler's wire
/// name from the method name and strips a trailing <c>Async</c>, so
/// <c>ReceiveChatMessageAsync</c> is registered as <c>"ReceiveChatMessage"</c>. Verified against
/// 1.20.0 against a live test server: an untyped signal sent to <c>"ReceiveChatMessageAsync"</c> is
/// accepted by the server and silently dropped — the workflow stays parked forever. Any caller that
/// must signal by name has to use <see cref="SignalNames.ChatMessage"/>.
/// </para>
/// <para>
/// <paramref name="MessageId"/> is the id of the persisted <c>job_messages</c> row when one exists.
/// It is what makes redelivery idempotent: a signal can arrive twice (the API retries a timed-out
/// call, or a workflow replays a buffered signal after a worker restart), and a duplicate reply must
/// not be rendered into the prompt twice.
/// </para>
/// </remarks>
/// <param name="SenderId">Persona id or "operator" of whoever replied.</param>
/// <param name="Content">Message body, already Markdown.</param>
/// <param name="Timestamp">When the sender wrote it — server time, not the agent's clock.</param>
/// <param name="MessageId">Persisted chat row id, when the reply was stored.</param>
/// <param name="TargetPersonaId">
/// Optional explicit target persona or comma-delimited mentions this reply was addressed to.
/// When supplied, workflows driving a different persona drop the signal so they are not woken prematurely.
/// </param>
public record ChatMessageSignal(
    string SenderId,
    string Content,
    DateTimeOffset Timestamp,
    string? MessageId = null,
    string? TargetPersonaId = null);

/// <summary>
/// An external code-review decision on the task's pull request.
/// </summary>
/// <param name="ReviewerId">Who reviewed. The API refuses a decision from the PR's author agent.</param>
/// <param name="IsApproved">True to merge, false to send the task back for changes.</param>
/// <param name="ReviewComments">Findings; injected into the next turn's prompt when not approved.</param>
/// <param name="Timestamp">Decision time.</param>
/// <param name="PrNumber">The PR the decision applies to, when the caller knows it.</param>
public record PrReviewSignal(
    string ReviewerId,
    bool IsApproved,
    string? ReviewComments,
    DateTimeOffset Timestamp,
    int? PrNumber = null);

/// <summary>
/// Everything the workflow needs to drive one task to completion.
/// </summary>
/// <remarks>
/// Immutable and fully serialized into the workflow's history at start. Two consequences worth
/// knowing about: (1) changing the shape of this record does not break a workflow that is already
/// running, but changing the <em>meaning</em> of a field silently reinterprets its stored value on
/// replay; (2) nothing here may be a live handle or a secret — it is readable by anyone with
/// history access.
/// </remarks>
/// <param name="JobId">The <c>project_jobs</c> row.</param>
/// <param name="TaskId">
/// Which checklist item inside the job this workflow owns. Checkpoints are keyed on
/// (JobId, TaskId), so a job with three tasks gets three independent recovery states.
/// </param>
/// <param name="AgentId">The driver identity that executes turns.</param>
/// <param name="ProjectId">The <c>projects</c> slug; scopes memory reads.</param>
/// <param name="WorkspacePath">
/// Resolved checkout root, e.g. <c>/workspace/repos/agent-mesh</c>. Carried on the input rather
/// than re-derived per activity so every turn in one workflow run agrees on one path even if the
/// project row is edited mid-flight.
/// </param>
/// <param name="TargetBranch">Branch the PR merges into.</param>
/// <param name="PersonaId">The persona whose persona-prompt and credentials the turn uses.</param>
/// <param name="TurnSessionId">
/// The most recent turn session for this task, when one exists. Read by the workflow's
/// <c>GetTaskStatus</c> query so an operator can open the transcript of a parked task directly
/// from the workflow, instead of only from the activity's log line.
/// </param>
public record TaskExecutionInput(
    string JobId,
    string TaskId,
    string AgentId,
    string ProjectId,
    string WorkspacePath,
    string TargetBranch,
    string? PersonaId = null,
    string? TurnSessionId = null);

/// <summary>
/// The workflow's terminal result.
/// </summary>
public record TaskExecutionResult(
    bool Success,
    string TaskId,
    string? FinalPrUrl,
    string Summary);

/// <summary>
/// The workflow's observable state, returned by the <c>GetTaskStatus</c> query.
/// </summary>
/// <param name="Status">Human-readable lifecycle position.</param>
/// <param name="AwaitingSince">When the current wait began, null when not waiting.</param>
/// <param name="TurnCount">How many coding turns have completed. The loop bound lives here.</param>
/// <param name="LastOutcome">Status of the most recent turn.</param>
/// <param name="PendingChatCount">Replies buffered but not yet consumed by a turn.</param>
/// <param name="PrUrl">Latest pull request, if one was opened.</param>
/// <param name="TurnSessionId">Latest turn session, for deep-linking into the transcript.</param>
public record TaskStatusView(
    string Status,
    DateTimeOffset? AwaitingSince,
    int TurnCount,
    TurnOutcomeStatus? LastOutcome,
    int PendingChatCount,
    string? PrUrl = null,
    string? TurnSessionId = null,
    /// <summary>
    /// Chat signals this run refused because they came from its own persona.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Surfaced rather than logged-and-forgotten because <c>AgentMesh.Core</c> carries no logging
    /// abstraction by design — it references only <c>Temporalio</c> — and a self-signal drop is otherwise
    /// invisible. Without it, an agent that escalates and then posts again to the same job sees its task
    /// refuse to resume with no explanation anywhere: not in the status, not in the result, not in the
    /// ledger. That is the "confidently reports the wrong thing" failure this whole feature exists to
    /// remove, applied to the control plane itself.
    /// </para>
    /// <para>
    /// A count rather than the last sender id, because the sender id is always the run's own persona —
    /// the number is the only part that varies and the only part that answers "did my reply land?".
    /// </para>
    /// </remarks>
    int IgnoredSelfSignalCount = 0,
    /// <summary>
    /// Chat signals this run refused because they were addressed to another persona or operator.
    /// </summary>
    /// <remarks>
    /// Surfaced alongside <see cref="IgnoredSelfSignalCount"/> so that misaddressed chat signals
    /// dropped by third-party isolation are visible on the status view instead of appearing lost.
    /// </remarks>
    int IgnoredMisaddressedSignalCount = 0,
    /// <summary>
    /// The persona this workflow run is driving, or null if unassigned.
    /// </summary>
    string? PersonaId = null);

/// <summary>
/// The wire names of <see cref="ITaskExecutionWorkflow"/>'s handlers.
/// </summary>
/// <remarks>
/// <para>
/// These are the names the SDK actually registers, with the <c>Async</c> suffix already removed.
/// They live in Core because there are two independent callers that must agree on them: the API's
/// signal router, and the worker's own <c>SignalNames</c> guard that fails fast when a caller
/// guesses the suffixed form.
/// </para>
/// <para>
/// A caller that gets this wrong gets no error. The server accepts an unknown signal name, the
/// workflow buffers it, no condition ever matches, and the task hangs in a way that looks like a
/// stuck agent rather than a typo. That is why the workflow validates inbound names too.
/// </para>
/// </remarks>
public static class SignalNames
{
    /// <summary>Registered name for <c>ReceiveChatMessageAsync</c>.</summary>
    public const string ChatMessage = "ReceiveChatMessage";

    /// <summary>Registered name for <c>ReceivePrReviewSignalAsync</c>.</summary>
    public const string PrReview = "ReceivePrReviewSignal";

    /// <summary>Registered name for the <c>GetTaskStatus</c> query.</summary>
    public const string StatusQuery = "GetTaskStatus";

    /// <summary>
    /// The names a caller is most likely to guess wrong: the C# method names, which are <em>not</em>
    /// the wire names.
    /// </summary>
    public static IReadOnlyList<string> CommonMistakes { get; } =
        ["ReceiveChatMessageAsync", "ReceivePrReviewSignalAsync"];
}
