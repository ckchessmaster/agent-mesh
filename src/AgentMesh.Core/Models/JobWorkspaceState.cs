using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

/// <summary>
/// Server-side description of the durable working state for a job.
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
/// At most one row per job.
/// </summary>
public class JobWorkspaceState
{
    public Guid JobId { get; set; }

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
}
