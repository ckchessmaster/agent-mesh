using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

/// <summary>
/// Server-side description of the durable working state for one task in a job (at most one row per
/// <c>(job, task)</c>).
///
/// A job's work no longer lives in the agent's local directory: it lives here. Each runner
/// keeps a local checkout at <c>~/.agentmesh/workspaces/jobs/&lt;jobId&gt;</c> and syncs it against
/// this state, which is what makes handoff between personas on *different machines* possible.
///
/// Two transports are recorded because they cover different needs:
///  - <see cref="BundleS3Key"/>: a full tar.gz of the worktree. Portable, works for non-git
///    workspaces, but heavy and lossy for history.
///  - <see cref="GitRemoteUrl"/> + <see cref="GitRef"/>: the real coordination mechanism for git
///    projects. Push/fetch gives us history, conflict detection, and a fast path.
///
/// At most one row per <c>(job, task)</c>. The row used to be per job; see <see cref="TaskId"/> for
/// why that stopped being true.
/// </summary>
public class JobWorkspaceState
{
    public Guid JobId { get; set; }

    /// <summary>
    /// Which checklist item this row describes. Part of the primary key with <see cref="JobId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A job is a checklist and a task is the unit of execution, so one row per job cannot describe
    /// a job with two active tasks: whichever task saved last overwrote the other's branch, head
    /// commit and checkpoint, and the overwritten task resumed onto the wrong branch with the wrong
    /// idea of what was uncommitted. Keying on the pair is what lets
    /// <see cref="TaskWorkingCheckpoint"/> exist at all.
    /// </para>
    /// <para>
    /// <see cref="UnassignedTaskId"/> is the sentinel for rows written before this column existed,
    /// and for job-level saves that were never about a specific task. It is a real value rather than
    /// null because a nullable column cannot participate in a primary key — the database would
    /// reject the composite index on the existing rows.
    /// </para>
    /// </remarks>
    public string TaskId { get; set; } = UnassignedTaskId;

    /// <summary>
    /// The task id stored for job-level state that is not scoped to a checklist item: legacy rows
    /// and runner saves that never named a task.
    /// </summary>
    /// <remarks>
    /// Lowercase with a leading underscore so it can never collide with a real task id, which are
    /// GUIDs rendered in either case. <c>Guid.TryParse</c> on this returns false, which is how
    /// callers distinguish "job-level row" from "a task row" without a second flag column.
    /// </remarks>
    public const string UnassignedTaskId = "_unassigned";

    /// <summary>True when <paramref name="taskId"/> names a real checklist item rather than the sentinel.</summary>
    public static bool IsTaskScoped(string? taskId) =>
        !string.IsNullOrWhiteSpace(taskId) &&
        !string.Equals(taskId, UnassignedTaskId, StringComparison.Ordinal);

    /// <summary>
    /// Normalizes a caller-supplied task id to the stored form: the sentinel for null/blank,
    /// otherwise the trimmed value.
    /// </summary>
    public static string NormalizeTaskId(string? taskId) =>
        string.IsNullOrWhiteSpace(taskId) ? UnassignedTaskId : taskId.Trim();

    [JsonIgnore]
    public ProjectJob? Job { get; set; }

    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Bundle transport: key of the latest tar.gz in object storage.</summary>
    public string? BundleS3Key { get; set; }

    /// <summary>Git transport: remote the job branch is pushed to.</summary>
    public string? GitRemoteUrl { get; set; }

    /// <summary>Git transport: branch name (normally <c>job.GitBranchName</c>).</summary>
    public string? GitRef { get; set; }

    /// <summary>HEAD commit of the last durable snapshot. The invariant every sync is checked against.</summary>
    public string? HeadCommit { get; set; }

    /// <summary>Content hash of the worktree at snapshot time, including uncommitted changes.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Persona that last durably saved the workspace.</summary>
    public string? LastSavedByPersonaId { get; set; }

    /// <summary>Driver (agent) that last durably saved the workspace — the machine the bits came from.</summary>
    public string? LastSavedByAgentId { get; set; }

    /// <summary>Turn session that produced the last durable save.</summary>
    public Guid? LastSavedTurnSessionId { get; set; }

    public DateTimeOffset? LastSavedAt { get; set; }

    /// <summary>
    /// Current holder of the write lease. Null means the workspace is free.
    /// Informational for coordination; the authoritative "is this agent still working" signal is
    /// the live turn session, because a killed runner cannot release a lease.
    /// </summary>
    public string? LeaseHolderAgentId { get; set; }

    public string? LeaseHolderPersonaId { get; set; }
    public DateTimeOffset? LeaseAcquiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ─────────────────────────────────────────────────────────────────────────────
    // Working checkpoint (TaskWorkingCheckpoint projection)
    //
    // These columns are the "what was I doing" state the harness writes at every turn
    // boundary and reads at the next one. They live here rather than in a table of
    // their own because they describe the same tree, observed at the same instant, as
    // HeadCommit/ContentHash above — a save that updated one set and not the other
    // would produce a checkpoint that confidently describes the wrong commit.
    //
    // Written by the harness from shell measurements, never from the agent's prose:
    // "I committed everything" is a claim, `git status --short` returning nothing is a
    // fact, and recovery has to be built out of the second kind.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Branch HEAD was on at checkpoint time. Null means "never inspected".</summary>
    public string? CheckpointBranch { get; set; }

    /// <summary>Lifecycle status at checkpoint time (<c>JobTaskStatus</c> value).</summary>
    public string? CheckpointStatus { get; set; }

    /// <summary>What the last turn accomplished, in the agent's own words.</summary>
    public string? CheckpointSummary { get; set; }

    /// <summary>
    /// JSON array of paths reported by <c>git status --short</c>.
    /// </summary>
    /// <remarks>
    /// Stored as JSON text rather than a child table because the list is only ever read whole, as
    /// part of the checkpoint, and never queried by file. A child table would buy a join and a
    /// delete-then-insert on every turn boundary in exchange for a capability nothing uses.
    /// <para>
    /// Null and <c>"[]"</c> are different: the first means the tree was never inspected, the second
    /// means it was inspected and found clean. Collapsing them would make a fresh checkpoint claim
    /// the tree is clean, which is the one wrong answer that can cause an agent to discard work.
    /// </para>
    /// </remarks>
    public string? CheckpointModifiedFilesJson { get; set; }

    /// <summary>Most recent stash entry, when one exists.</summary>
    public string? CheckpointActiveStash { get; set; }

    /// <summary>Short HEAD commit at checkpoint time (<c>git log -1 --oneline</c>).</summary>
    public string? CheckpointHeadCommit { get; set; }

    /// <summary>Task title at checkpoint time, for prompt rendering.</summary>
    public string? CheckpointFocus { get; set; }

    /// <summary>Turn session that produced the checkpoint.</summary>
    public Guid? CheckpointTurnSessionId { get; set; }

    /// <summary>When the checkpoint was last written. Null when there is no checkpoint.</summary>
    public DateTimeOffset? CheckpointUpdatedAt { get; set; }

    /// <summary>
    /// True when this row carries a checkpoint, as opposed to existing only for the lease or the
    /// durable snapshot.
    /// </summary>
    /// <remarks>
    /// Keyed on <see cref="CheckpointUpdatedAt"/> rather than on any individual field because the
    /// writer always stamps it, so it is the one column that cannot be stale while the others are
    /// fresh. A row created by <c>AcquireLeaseAsync</c> has a lease and no checkpoint, and the
    /// recovery path must not render an environment note for it.
    /// </remarks>
    [JsonIgnore]
    public bool HasCheckpoint => CheckpointUpdatedAt.HasValue;
}
