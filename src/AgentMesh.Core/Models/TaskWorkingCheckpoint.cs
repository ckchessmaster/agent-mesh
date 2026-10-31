using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

/// <summary>
/// The rolling "what was I doing" state for one checklist task inside a job.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to post-restart amnesia. Without it, a container that comes back cold hands
/// the agent a bare prompt ("I restarted the pod, resume"), and the agent spends its first fifteen
/// turns on <c>pwd</c>, <c>git status</c> and <c>git stash list</c> rediscovering state the harness
/// already knows. With it, turn one opens with the branch, the dirty files, the stash entry and the
/// last summary already in the prompt.
/// </para>
/// <para>
/// Keyed on <c>(JobId, TaskId)</c> rather than <c>JobId</c> alone because a job is a checklist, not a
/// unit of work: two tasks in the same job can be on different branches with different uncommitted
/// files, and one checkpoint per job means whichever task wrote last silently describes the other.
/// The composite key is what makes "resume the task I was on" a lookup instead of a guess.
/// </para>
/// <para>
/// Persisted into <c>job_workspace_states</c> alongside the durable snapshot columns it shares a row
/// with, not into a table of its own: the checkpoint and the snapshot describe the same tree at the
/// same instant, and splitting them across tables would let a save update one and lose the other.
/// </para>
/// </remarks>
/// <param name="JobId">The <c>project_jobs</c> row.</param>
/// <param name="TaskId">
/// The checklist item this checkpoint belongs to. Never blank: a blank here would collapse every
/// task in a job onto one row, which is the bug this key exists to prevent.
/// </param>
/// <param name="ActiveBranch">
/// Branch HEAD was on at checkpoint time — the branch to return to, not merely the branch that was
/// observed. Written by the harness from <c>git branch --show-current</c>, so it is a measurement
/// rather than the agent's claim about its own work.
/// </param>
/// <param name="Status">
/// Lifecycle position as a <c>job_task_status</c> value (<c>todo</c>/<c>in_progress</c>/
/// <c>awaiting_review</c>/<c>completed</c>). Kept as a string rather than an enum so a checkpoint
/// written by a newer build does not fail to deserialize on an older worker mid-rollout.
/// </param>
/// <param name="LastSummary">
/// What the last turn accomplished, in the agent's own words. The single most valuable field for
/// recovery: it is the only thing that says <em>why</em> the tree looks the way it does.
/// </param>
/// <param name="ModifiedFiles">
/// Paths reported by <c>git status --short</c> at checkpoint time, relative to the repository root.
/// Empty means "the tree was clean", which is different from null ("we never looked").
/// </param>
/// <param name="ActiveStash">
/// The most recent stash entry (<c>stash@{0}: …</c>) when one exists. Surfaced because an agent that
/// does not know it stashed will reimplement the work from scratch and then hit a conflict it cannot
/// explain.
/// </param>
/// <param name="UpdatedAt">When this checkpoint was written, in UTC.</param>
/// <param name="HeadCommit">
/// Short HEAD commit at checkpoint time (<c>git log -1 --oneline</c>). Lets recovery distinguish
/// "same commit, dirty tree" from "someone else pushed while I was down".
/// </param>
/// <param name="ChecklistFocus">
/// The task title at checkpoint time. Stored because <see cref="TaskId"/> is a GUID and the prompt
/// needs to say which checklist item this is in terms the agent itself wrote earlier.
/// </param>
/// <param name="TurnSessionId">
/// The turn session that produced this checkpoint, so a resumed prompt can name the transcript it is
/// continuing rather than starting a fresh one.
/// </param>
public record TaskWorkingCheckpoint(
    string JobId,
    string TaskId,
    string ActiveBranch,
    string Status,
    string LastSummary,
    IReadOnlyList<string> ModifiedFiles,
    string? ActiveStash,
    DateTimeOffset UpdatedAt,
    string? HeadCommit = null,
    string? ChecklistFocus = null,
    string? TurnSessionId = null)
{
    /// <summary>
    /// True when the working tree had uncommitted changes at checkpoint time.
    /// </summary>
    /// <remarks>
    /// Drives the wording of the environment recovery note: "3 uncommitted files" is an instruction
    /// not to blow the tree away, whereas "clean" tells the agent a hard reset would be safe.
    /// </remarks>
    /// <summary>The list, or empty when it was never populated. Never null.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> FilesOrEmpty => ModifiedFiles ?? [];

    /// <summary>
    /// True when the working tree had uncommitted changes at checkpoint time.
    /// </summary>
    /// <remarks>
    /// Null-safe by necessity: <see cref="ModifiedFiles"/> is typed non-nullable but the persisted form
    /// distinguishes null ("never inspected") from empty ("inspected, clean"), so a checkpoint
    /// reconstructed from a row where the column is null reaches this property. Treating null as
    /// "no uncommitted work" is the safe reading — it renders no claim rather than a false one.
    /// </remarks>
    [JsonIgnore]
    public bool HasUncommittedWork => ModifiedFiles is { Count: > 0 };

    /// <summary>
    /// A checkpoint for a task that has never been worked: clean tree, no summary, unknown branch.
    /// </summary>
    /// <remarks>
    /// Exists so callers can render "first turn on this task" without null-testing every field, and
    /// so the prompt builder can omit the recovery block entirely instead of printing a header full
    /// of "unknown" — a note that says nothing is worse than no note, because the agent treats it as
    /// authoritative and stops checking.
    /// </remarks>
    public static TaskWorkingCheckpoint Empty(string jobId, string taskId) => new(
        jobId,
        taskId,
        ActiveBranch: string.Empty,
        Status: JobTaskStatus.Todo,
        LastSummary: string.Empty,
        ModifiedFiles: [],
        ActiveStash: null,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>True when this is a placeholder rather than a real observation.</summary>
    [JsonIgnore]
    public bool IsEmpty => UpdatedAt == DateTimeOffset.UnixEpoch;
}
