using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Api.Services;

/// <summary>
/// Server-side truth for "where does a job's work live", now scoped per task.
/// </summary>
/// <remarks>
/// <para>
/// <c>job_workspace_states</c> moved from one row per job to one row per <c>(job, task)</c> so a
/// per-task working checkpoint is representable (see
/// <c>20261030171746_AddMultiRepoPathsAndTaskCheckpoints</c>). This service is what has to absorb that
/// change without breaking its existing callers, all of which are job-scoped: the CLI runner's save
/// endpoint, <c>JobHandoffService</c>, and <c>JobTurnDispatcher</c>'s hydration lookup.
/// </para>
/// <para>
/// <strong>The bug this rewrite removes.</strong> Every lookup here used to be
/// <c>FirstOrDefaultAsync(s =&gt; s.JobId == jobId)</c>. Against a single row per job that is
/// unambiguous. Against the new composite key it returns <em>an arbitrary row</em> — Postgres makes no
/// ordering promise — so a hydration lookup could hand a runner the branch and head commit of a task
/// it is not working on, and a lease release could clear the wrong task's lock while leaving the real
/// holder locked out. Silent, order-dependent, and it only appears once a job has two tasks. Every
/// query below now names an explicit ordering or an exact key.
/// </para>
/// <para>
/// <strong>What "the active row" means for a job-scoped caller.</strong> The newest row by
/// <c>updated_at</c>. That is not a guess at intent, it is the closest thing to a definition available
/// without reading the checklist: the row the platform touched most recently is the row the work is
/// on. For a job with one row — every job that exists today — it is that row, so existing behaviour is
/// bit-for-bit unchanged. Deriving it from <c>TasksJson</c> instead (the "pending task assigned to
/// this persona" rule <c>ProjectJobsController.ResolveTargetTask</c> uses) would duplicate checklist
/// semantics into the storage layer, and the two would drift the first time the checklist rule changed.
/// </para>
/// </remarks>
public class JobWorkspaceService(
    AgentMeshDbContext db,
    ILogger<JobWorkspaceService>? logger = null) : IJobWorkspaceService
{
    public Task<JobWorkspaceState?> GetAsync(Guid jobId, CancellationToken ct = default) =>
        FindActiveAsync(jobId, ct);

    public Task<JobWorkspaceState?> GetAsync(Guid jobId, string? taskId, CancellationToken ct = default) =>
        FindAsync(jobId, taskId, ct);

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

        var state = await FindAsync(req.JobId, req.TaskId, ct, track: true);
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
                // A runner that does not name a task writes the job-level row, exactly as before this
                // feature existed. It is not "the first task" and not "all tasks" — it is job-level
                // state, and the sentinel says so.
                TaskId = JobWorkspaceState.NormalizeTaskId(req.TaskId),
                ProjectId = job.ProjectId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.JobWorkspaceStates.Add(state);
        }

        // A save from a runner that does not hold the lease means two agents were working the same
        // task at once. That should be rare, but silently discarding the bytes would be worse than
        // accepting them and shouting about it — last writer wins, loudly.
        string? warning = null;
        var holder = state.LeaseHolderAgentId;
        if (!string.IsNullOrWhiteSpace(holder) &&
            !string.Equals(holder, req.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            warning = $"Workspace lease was held by '@{holder}' but save was accepted from '@{req.AgentId}'. " +
                      "Concurrent work on this task was detected; last write wins.";
            logger?.LogWarning(
                "Workspace lease conflict on job {JobId} task {TaskId}: holder={Holder}, saver={Saver}",
                req.JobId, state.TaskId, holder, req.AgentId);
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
            "Workspace {Action} for job {JobId} task {TaskId}: head={Head} hash={Hash} remote={Remote}@{Ref} bundle={Bundle} saver={Agent} released={Released}",
            isNew ? "created" : "updated", req.JobId, state.TaskId, state.HeadCommit, state.ContentHash,
            state.GitRemoteUrl, state.GitRef, state.BundleS3Key, req.AgentId, req.ReleaseLease);

        return new SaveWorkspaceResult(true, warning, ToSnapshot(state));
    }

    public async Task AcquireLeaseAsync(Guid jobId, string agentId, string? personaId, CancellationToken ct = default) =>
        await AcquireLeaseAsync(jobId, agentId, personaId, taskId: null, ct);

    public async Task AcquireLeaseAsync(
        Guid jobId,
        string agentId,
        string? personaId,
        string? taskId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;

        var state = await FindAsync(jobId, taskId, ct, track: true);
        if (state == null)
        {
            var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job == null) return;

            state = new JobWorkspaceState
            {
                JobId = jobId,
                TaskId = JobWorkspaceState.NormalizeTaskId(taskId),
                ProjectId = job.ProjectId,
            };
            db.JobWorkspaceStates.Add(state);
        }

        state.LeaseHolderAgentId = agentId;
        state.LeaseHolderPersonaId = personaId;
        state.LeaseAcquiredAt = DateTimeOffset.UtcNow;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public Task ReleaseLeaseAsync(Guid jobId, string agentId, CancellationToken ct = default) =>
        ReleaseLeaseAsync(jobId, agentId, taskId: null, ct);

    public async Task ReleaseLeaseAsync(Guid jobId, string agentId, string? taskId, CancellationToken ct = default)
    {
        var state = await FindAsync(jobId, taskId, ct, track: true);
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

        // Distinct job ids: a runner that held leases on three tasks of one job released three rows,
        // and the caller counts *jobs* it no longer owns. Reporting the job three times would make the
        // reaper's log and any downstream per-job bookkeeping overstate how much work was orphaned.
        return held.Select(s => s.JobId).Distinct().ToList();
    }

    /// <summary>
    /// The row a job-scoped caller means: the most recently updated one for that job.
    /// </summary>
    /// <remarks>
    /// Ordering is <c>updated_at</c> then <c>task_id</c>. The tiebreak is not decoration: two rows
    /// written in the same instant (a checkpoint save and a lease acquisition from one turn) would
    /// otherwise resolve arbitrarily, which is the exact non-determinism this rewrite exists to remove.
    /// </remarks>
    private Task<JobWorkspaceState?> FindActiveAsync(Guid jobId, CancellationToken ct, bool track = false) =>
        Query(track)
            .Where(s => s.JobId == jobId)
            .OrderByDescending(s => s.UpdatedAt)
            .ThenBy(s => s.TaskId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The exact row when a task is named, otherwise the active one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A named task that does not exist returns null rather than falling back to the active row. That is
    /// deliberate: a caller that asked for task X and got task Y's head commit would hydrate a runner
    /// onto the wrong baseline, and the resulting push rejection would be blamed on the remote. Null
    /// means "no state for that task yet", which callers already handle as a first save.
    /// </para>
    /// <para>
    /// <strong>Why the test here is "non-blank" and not <see cref="JobWorkspaceState.IsTaskScoped"/>.</strong>
    /// The sentinel is a real key, and a caller that names it is asking for the job-level row. Routing it
    /// through <c>IsTaskScoped</c> would classify the sentinel as "no task named" and return the newest
    /// <em>task</em> row instead — a task's state served to a caller that explicitly asked not for a
    /// task's state, which is the exact substitution this rewrite exists to make impossible. Blank means
    /// "no task named"; the sentinel means "the job-level row"; a GUID means that task.
    /// </para>
    /// </remarks>
    private async Task<JobWorkspaceState?> FindAsync(
        Guid jobId,
        string? taskId,
        CancellationToken ct,
        bool track = false)
    {
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var normalized = JobWorkspaceState.NormalizeTaskId(taskId);
            return await Query(track)
                .FirstOrDefaultAsync(s => s.JobId == jobId && s.TaskId == normalized, ct);
        }

        return await FindActiveAsync(jobId, ct, track);
    }

    private IQueryable<JobWorkspaceState> Query(bool track) =>
        track ? db.JobWorkspaceStates : db.JobWorkspaceStates.AsNoTracking();

    private static readonly WorkspaceSnapshot Empty = new(null, null, null, null, null, false);

    private static WorkspaceSnapshot ToSnapshot(JobWorkspaceState s) =>
        new(s.GitRemoteUrl, s.GitRef, s.HeadCommit, s.ContentHash, s.BundleS3Key,
            HasDurableState: !string.IsNullOrWhiteSpace(s.HeadCommit)
                          || !string.IsNullOrWhiteSpace(s.ContentHash)
                          || !string.IsNullOrWhiteSpace(s.BundleS3Key));
}
