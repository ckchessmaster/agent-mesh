using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Api.Services;

public class JobWorkspaceService(
    AgentMeshDbContext db,
    ILogger<JobWorkspaceService>? logger = null) : IJobWorkspaceService
{
    public async Task<JobWorkspaceState?> GetAsync(Guid jobId, CancellationToken ct = default) =>
        await db.JobWorkspaceStates.AsNoTracking().FirstOrDefaultAsync(s => s.JobId == jobId, ct);

    public async Task<WorkspaceSnapshot?> GetSnapshotAsync(Guid jobId, CancellationToken ct = default)
    {
        var state = await GetAsync(jobId, ct);
        if (state == null) return null;

        return ToSnapshot(state);
    }

    public async Task<SaveWorkspaceResult> SaveAsync(SaveWorkspaceRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.AgentId))
        {
            return new SaveWorkspaceResult(false, "agentId is required.", Empty);
        }

        var state = await db.JobWorkspaceStates.FirstOrDefaultAsync(s => s.JobId == req.JobId, ct);
        var isNew = state == null;
        if (state == null)
        {
            var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == req.JobId, ct);
            if (job == null)
            {
                return new SaveWorkspaceResult(false, $"Job '{req.JobId}' not found.", Empty);
            }

            state = new JobWorkspaceState
            {
                JobId = req.JobId,
                ProjectId = job.ProjectId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.JobWorkspaceStates.Add(state);
        }

        // A save from a runner that does not hold the lease means two agents were working the same
        // job at once. That should be rare, but silently discarding the bytes would be worse than
        // accepting them and shouting about it — last writer wins, loudly.
        string? warning = null;
        var holder = state.LeaseHolderAgentId;
        if (!string.IsNullOrWhiteSpace(holder) &&
            !string.Equals(holder, req.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            warning = $"Workspace lease was held by '@{holder}' but save was accepted from '@{req.AgentId}'. " +
                      "Concurrent work on this job was detected; last write wins.";
            logger?.LogWarning(
                "Workspace lease conflict on job {JobId}: holder={Holder}, saver={Saver}",
                req.JobId, holder, req.AgentId);
        }

        // Only overwrite pointers the caller actually knows about. A runner that could not reach
        // the git remote still gets to report its bundle, and must not blank out a good remote ref.
        if (!string.IsNullOrWhiteSpace(req.HeadCommit)) state.HeadCommit = req.HeadCommit;
        if (!string.IsNullOrWhiteSpace(req.ContentHash)) state.ContentHash = req.ContentHash;
        if (!string.IsNullOrWhiteSpace(req.GitRemoteUrl)) state.GitRemoteUrl = req.GitRemoteUrl;
        if (!string.IsNullOrWhiteSpace(req.GitRef)) state.GitRef = req.GitRef;
        if (!string.IsNullOrWhiteSpace(req.BundleS3Key)) state.BundleS3Key = req.BundleS3Key;

        state.LastSavedByPersonaId = req.PersonaId ?? state.LastSavedByPersonaId;
        state.LastSavedByAgentId = req.AgentId;
        state.LastSavedTurnSessionId = req.TurnSessionId ?? state.LastSavedTurnSessionId;
        state.LastSavedAt = DateTimeOffset.UtcNow;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        if (req.ReleaseLease)
        {
            state.LeaseHolderAgentId = null;
            state.LeaseHolderPersonaId = null;
            state.LeaseAcquiredAt = null;
        }
        else
        {
            // Lease follows the bits. Leaving it with the old holder while the tree came from the
            // new one would make every subsequent save look like a conflict and hide the next real one.
            state.LeaseHolderAgentId = req.AgentId;
            state.LeaseHolderPersonaId = req.PersonaId ?? state.LeaseHolderPersonaId;
            state.LeaseAcquiredAt ??= DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Workspace {Action} for job {JobId}: head={Head} hash={Hash} remote={Remote}@{Ref} bundle={Bundle} saver={Agent} released={Released}",
            isNew ? "created" : "updated", req.JobId, state.HeadCommit, state.ContentHash,
            state.GitRemoteUrl, state.GitRef, state.BundleS3Key, req.AgentId, req.ReleaseLease);

        return new SaveWorkspaceResult(true, warning, ToSnapshot(state));
    }

    public async Task AcquireLeaseAsync(Guid jobId, string agentId, string? personaId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;

        var state = await db.JobWorkspaceStates.FirstOrDefaultAsync(s => s.JobId == jobId, ct);
        if (state == null)
        {
            var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job == null) return;

            state = new JobWorkspaceState { JobId = jobId, ProjectId = job.ProjectId };
            db.JobWorkspaceStates.Add(state);
        }

        state.LeaseHolderAgentId = agentId;
        state.LeaseHolderPersonaId = personaId;
        state.LeaseAcquiredAt = DateTimeOffset.UtcNow;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseLeaseAsync(Guid jobId, string agentId, CancellationToken ct = default)
    {
        var state = await db.JobWorkspaceStates.FirstOrDefaultAsync(s => s.JobId == jobId, ct);
        if (state == null) return;

        // Never release somebody else's lease. If an old runner wakes up late after a handoff, its
        // release must not unlock the agent that took over.
        if (!string.Equals(state.LeaseHolderAgentId, agentId, StringComparison.OrdinalIgnoreCase)) return;

        state.LeaseHolderAgentId = null;
        state.LeaseHolderPersonaId = null;
        state.LeaseAcquiredAt = null;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>?> ReleaseLeasesForAgentAsync(string agentId, CancellationToken ct = default)
    {
        var held = await db.JobWorkspaceStates
            .Where(s => s.LeaseHolderAgentId == agentId)
            .ToListAsync(ct);

        if (held.Count == 0) return Array.Empty<Guid>();

        foreach (var state in held)
        {
            state.LeaseHolderAgentId = null;
            state.LeaseHolderPersonaId = null;
            state.LeaseAcquiredAt = null;
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return held.Select(s => s.JobId).ToList();
    }

    private static readonly WorkspaceSnapshot Empty = new(null, null, null, null, null, false);

    private static WorkspaceSnapshot ToSnapshot(JobWorkspaceState s) =>
        new(s.GitRemoteUrl, s.GitRef, s.HeadCommit, s.ContentHash, s.BundleS3Key,
            HasDurableState: !string.IsNullOrWhiteSpace(s.HeadCommit)
                          || !string.IsNullOrWhiteSpace(s.ContentHash)
                          || !string.IsNullOrWhiteSpace(s.BundleS3Key));
}
