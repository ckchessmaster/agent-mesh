using AgentMesh.Core.Models;

namespace AgentMesh.Core.Repositories;

/// <summary>
/// Durable storage for the per-task working checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// Two methods, both on the turn boundary: the harness loads before it prompts and saves after it
/// yields. That ordering is the whole contract, and it is why neither method takes a "version" or
/// returns a concurrency token — a checkpoint is a cache of observed state, not a ledger. Two
/// writers racing on the same task (which happens when a timed-out activity's retry overlaps the
/// turn it was retrying) resolve to last-write-wins, and the loser's information is a strictly
/// older observation of the same tree.
/// </para>
/// <para>
/// Contrast with the lease logic in the API's <c>IJobWorkspaceService</c>, which shares this table
/// and <em>does</em> care about who holds the write lock. That service guards durable work products;
/// this one guards orientation state. Making the checkpoint participate in lease arbitration would
/// mean a checkpoint write fails exactly when the agent most needs it recovered — after a crash that
/// already dropped its lease.
/// </para>
/// </remarks>
public interface IWorkspaceStateRepository
{
    /// <summary>
    /// Inserts or replaces the checkpoint for <c>(JobId, TaskId)</c>.
    /// </summary>
    /// <remarks>
    /// Creates the owning row if none exists, which requires the job to exist so
    /// <c>project_id</c> can be filled in. Returns false rather than throwing when the job is
    /// unknown: a checkpoint write must never fail a turn that just did real work, and the caller
    /// logs the false instead.
    /// </remarks>
    Task<bool> SaveCheckpointAsync(TaskWorkingCheckpoint checkpoint, CancellationToken ct = default);

    /// <summary>
    /// Loads the checkpoint for one task, or null when the task has never been checkpointed.
    /// </summary>
    /// <remarks>
    /// Null (not <see cref="TaskWorkingCheckpoint.Empty"/>) is returned deliberately: the caller
    /// has to be able to tell "no prior state, skip the recovery note" from "prior state that
    /// happened to be empty", and only the repository knows which rows exist.
    /// </remarks>
    Task<TaskWorkingCheckpoint?> GetCheckpointAsync(string jobId, string taskId, CancellationToken ct = default);

    /// <summary>
    /// Every checkpoint currently stored for a job, newest first.
    /// </summary>
    /// <remarks>
    /// Used by the recovery path when a task id is not known — a restarted runner that only has its
    /// job id asks "which task was I on", and the answer is the most recently updated checkpoint.
    /// </remarks>
    Task<IReadOnlyList<TaskWorkingCheckpoint>> GetCheckpointsForJobAsync(string jobId, CancellationToken ct = default);
}
