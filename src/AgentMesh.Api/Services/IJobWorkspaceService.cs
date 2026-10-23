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
    bool ReleaseLease = false);

public record SaveWorkspaceResult(
    bool Accepted,
    string? Warning,
    WorkspaceSnapshot Snapshot);

/// <summary>
/// Server-side truth for "where does a job's work live".
///
/// Answer: a per-job workspace, mirrored durably here. Runners keep a local checkout at
/// <c>~/.agentmesh/workspaces/jobs/&lt;jobId&gt;</c>, but that directory is a cache — it is not the
/// record, because the next persona to pick the job up may be on another machine and will never
/// see it. Anything not recorded here is treated as lost on handoff.
/// </summary>
public interface IJobWorkspaceService
{
    Task<JobWorkspaceState?> GetAsync(Guid jobId, CancellationToken ct = default);

    Task<WorkspaceSnapshot?> GetSnapshotAsync(Guid jobId, CancellationToken ct = default);

    Task<SaveWorkspaceResult> SaveAsync(SaveWorkspaceRequest req, CancellationToken ct = default);

    Task AcquireLeaseAsync(Guid jobId, string agentId, string? personaId, CancellationToken ct = default);

    Task ReleaseLeaseAsync(Guid jobId, string agentId, CancellationToken ct = default);

    /// <summary>
    /// Drops the lease held by a runner that went away mid-job. Returns the jobs it was holding.
    /// The durable snapshot is deliberately left intact — we release the lock, not the work.
    /// </summary>
    Task<IReadOnlyList<Guid>?> ReleaseLeasesForAgentAsync(string agentId, CancellationToken ct = default);
}
