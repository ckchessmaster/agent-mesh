namespace AgentMesh.Core.Models;

/// <summary>
/// The lifecycle of a checklist task, and the rules for moving between its states.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the status is a string and not a C# enum.</strong> Two reasons, both load-bearing.
/// First, <see cref="JobTaskDto"/> lives inside the <c>project_jobs.tasks_json</c> JSONB blob, so
/// adding a column-free status costs no migration — but only if the stored representation is
/// stable text. An enum serialized as a number would make every existing row's implicit
/// <c>0</c> mean <c>todo</c> by accident rather than by declaration, and would make the blob
/// unreadable to anything that is not this assembly. Second, the MCP tool has to answer a bad
/// value with the list of allowed values; a bound enum parameter instead produces the library's
/// generic invocation error, which tells a model nothing. Same trade-off already documented on
/// <c>save_memory</c>'s <c>category</c> parameter.
/// </para>
/// <para>
/// <strong>Back-compatibility.</strong> Every task written before this field existed has
/// <c>Status == null</c>. <see cref="Effective"/> derives the state from the pre-existing
/// <see cref="JobTaskDto.IsCompleted"/> flag in that case, so historical checklists report the
/// same status the Cockpit already draws for them, and the two fields cannot disagree about a
/// task that has only ever been toggled through the dashboard.
/// </para>
/// </remarks>
public static class JobTaskStatus
{
    public const string Todo = "todo";
    public const string InProgress = "in_progress";
    public const string AwaitingReview = "awaiting_review";
    public const string Completed = "completed";

    /// <summary>The four states, in lifecycle order. This is the single place they are spelled.</summary>
    public static readonly IReadOnlyList<string> All = [Todo, InProgress, AwaitingReview, Completed];

    /// <summary>
    /// Where a task is, counting a null (legacy) status as whatever <c>IsCompleted</c> implies.
    /// </summary>
    /// <remarks>
    /// A non-null status always wins: an explicit write is newer information than the boolean,
    /// and the tool keeps the two in sync on write anyway.
    /// </remarks>
    public static string Effective(string? status, bool isCompleted) =>
        Normalize(status) ?? (isCompleted ? Completed : Todo);

    /// <summary>Trim/lowercase a caller-supplied status, or null if it is not one of the four.</summary>
    public static string? Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var candidate = status.Trim().ToLowerInvariant();

        // Accept the two spellings an agent is most likely to reach for: the hyphenated form
        // (markdown checkboxes and most issue trackers) and the space-separated prose form.
        candidate = candidate.Replace('-', '_').Replace(' ', '_');

        return All.Contains(candidate) ? candidate : null;
    }

    /// <summary>
    /// Try-parse a caller-supplied status. Mirrors <c>AgentMemoryCategories.TryNormalize</c> so the
    /// two closed vocabularies on the MCP surface read the same way at their call sites.
    /// </summary>
    public static bool TryNormalize(string? status, out string normalized)
    {
        var result = Normalize(status);
        normalized = result ?? string.Empty;
        return result is not null;
    }

    /// <summary>
    /// Whether <paramref name="from"/> may move to <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// The graph is deliberately permissive about going backwards and strict about skipping the
    /// review gate:
    /// <list type="bullet">
    /// <item><description>
    /// A same-state request is allowed and is a no-op. Agents retry; refusing an idempotent
    /// replay would turn a transient network failure into a stuck turn.
    /// </description></item>
    /// <item><description>
    /// <c>todo → awaiting_review</c> and <c>todo → completed</c> are rejected. A task cannot be
    /// reviewed or finished without having been started, which is the one invariant the checklist
    /// exists to enforce. This is also what stops an agent from opening a PR against a task it
    /// never marked in progress.
    /// </description></item>
    /// <item><description>
    /// Reopening (<c>completed → in_progress</c>) is allowed. Reviewers send work back, and a
    /// model that cannot reopen will work around the restriction by inventing a new task.
    /// </description></item>
    /// </list>
    /// Callers that want to allow an out-of-band jump (an operator dragging a checkbox in the
    /// Cockpit) can pass <c>skipTransitionValidation</c> at their own layer; the MCP path never does.
    /// </remarks>
    public static bool CanTransition(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return true;

        return (from, to) switch
        {
            (Todo, InProgress) => true,
            (Todo, Completed) => false,
            (Todo, AwaitingReview) => false,

            (InProgress, AwaitingReview) => true,
            (InProgress, Completed) => true,
            (InProgress, Todo) => true,

            (AwaitingReview, Completed) => true,
            (AwaitingReview, InProgress) => true,
            (AwaitingReview, Todo) => false,

            (Completed, InProgress) => true,
            (Completed, Todo) => true,
            (Completed, AwaitingReview) => false,

            _ => false,
        };
    }

    /// <summary>Render the allowed values for an error message, e.g. <c>'todo', 'in_progress', …</c>.</summary>
    public static string AllowedValuesHint() => string.Join(", ", All.Select(v => $"'{v}'"));

    /// <summary>
    /// The states a task in <paramref name="from"/> may move to. Used to advertise the constraint
    /// on the published MCP schema without duplicating the graph.
    /// </summary>
    public static IReadOnlyList<string> AllowedNext(string from) =>
        All.Where(to => CanTransition(from, to)).ToList();
}
