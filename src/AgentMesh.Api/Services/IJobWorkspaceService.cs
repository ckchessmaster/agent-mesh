using AgentMesh.Core.Models;

namespace AgentMesh.Api.Services;

public record WorkspaceSnapshot(
    string? GitRemoteUrl,
    string? GitRef,
    string? HeadCommit,
    string? ContentHash,
    string? BundleS3Key,
    /// <summary>True when the server has durable bits for this job. False = nothing to hydrate.</summary>
    bool HasDurableState);

public record SaveWorkspaceRequest(
    Guid JobId,
    /// <summary>Runner that saved. Must hold (or be the recorded holder of) the lease.</summary>
    string AgentId,
    string? PersonaId = null,
    Guid? TurnSessionId = null,
    string? HeadCommit = null,
    string? ContentHash = null,
    string? GitRemoteUrl = null,
    string? GitRef = null,
    string? BundleS3Key = null,
    /// <summary>Set when the runner is done with the job and is releasing the write lease.</summary>
    bool ReleaseLease = false,
    /// <summary>
    /// Checklist item the saved tree belongs to. Null writes the job-level row
    /// (<c>JobWorkspaceState.UnassignedTaskId</c>), which is what every runner did before task-scoped
    /// state existed and remains correct for a job with one unit of work.
    /// </summary>
    /// <remarks>
    /// Optional and trailing on purpose: <c>SaveWorkspaceRequest</c> is constructed positionally by
    /// existing callers, and a required parameter inserted mid-list would silently reassign every
    /// argument after it rather than failing to compile in the case where the types happen to line up.
    /// </remarks>
    string? TaskId = null);

public record SaveWorkspaceResult(
    bool Accepted,
    string? Warning,
    WorkspaceSnapshot Snapshot);

/// <summary>
/// Server-side truth for "where does a job's work live".
///
/// Answer: a per-task workspace row, mirrored durably here. Runners keep a local checkout at
/// <c>~/.agentmesh/workspaces/jobs/&lt;jobId&gt;</c>, but that directory is a cache — it is not the
/// record, because the next persona to pick the job up may be on another machine and will never
/// see it. Anything not recorded here is treated as lost on handoff.
/// </summary>
/// <remarks>
/// Rows are keyed on <c>(job, task)</c> since the checkpoint feature; the job-scoped overloads below
/// resolve "the row a job-level caller means" rather than pretending the distinction away. See
/// <see cref="JobWorkspaceService"/> for the resolution rule and why an unordered
/// <c>FirstOrDefault</c> on <c>job_id</c> stopped being safe.
/// </remarks>
public interface IJobWorkspaceService
{
    /// <summary>The active row for a job — the most recently updated one.</summary>
    Task<JobWorkspaceState?> GetAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// The row for one task when <paramref name="taskId"/> names one, otherwise the active row.
    /// </summary>
    /// <remarks>
    /// A named task with no row returns null rather than another task's row: hydrating a runner onto
    /// a head commit chosen for different work is how a push gets rejected for reasons nobody can
    /// reproduce.
    /// </remarks>
    Task<JobWorkspaceState?> GetAsync(Guid jobId, string? taskId, CancellationToken ct = default);

    Task<WorkspaceSnapshot?> GetSnapshotAsync(Guid jobId, CancellationToken ct = default);

    Task<SaveWorkspaceResult> SaveAsync(SaveWorkspaceRequest req, CancellationToken ct = default);

    Task AcquireLeaseAsync(Guid jobId, string agentId, string? personaId, CancellationToken ct = default);

    /// <summary>
    /// Takes the lease on one task's row, or on the job's active row when no task is named.
    /// </summary>
    /// <remarks>
    /// Task-scoped leasing is what lets two personas work two tasks of one job concurrently without
    /// either save being reported as a lease conflict. The job-scoped overload above is retained for
    /// callers that genuinely have no task context (the runner-death reaper, handoff).
    /// </remarks>
    Task AcquireLeaseAsync(
        Guid jobId, string agentId, string? personaId, string? taskId, CancellationToken ct = default);

    Task ReleaseLeaseAsync(Guid jobId, string agentId, CancellationToken ct = default);

    /// <summary>Releases one task's lease, or the job's active row when no task is named.</summary>
    Task ReleaseLeaseAsync(Guid jobId, string agentId, string? taskId, CancellationToken ct = default);

    /// <summary>
    /// Drops the lease held by a runner that went away mid-job. Returns the jobs it was holding.
    /// The durable snapshot is deliberately left intact — we release the lock, not the work.
    /// </summary>
    /// <remarks>
    /// Distinct job ids: one runner can hold several task rows of the same job, and the caller counts
    /// jobs, not rows.
    /// </remarks>
    Task<IReadOnlyList<Guid>?> ReleaseLeasesForAgentAsync(string agentId, CancellationToken ct = default);
}
