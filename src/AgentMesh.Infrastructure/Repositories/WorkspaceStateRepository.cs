using System.Text.Json;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Repositories;

/// <summary>
/// EF Core backed store for the per-task working checkpoint, projected onto
/// <see cref="JobWorkspaceState"/>'s checkpoint columns.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it shares a row with the durable snapshot instead of owning a table:</strong> the
/// checkpoint and the snapshot describe the same tree at the same instant. One row means one write
/// and one read, and it makes it structurally impossible for the checkpoint to name a commit the
/// snapshot does not cover. See the migration
/// (<c>20261030171746_AddMultiRepoPathsAndTaskCheckpoints</c>) for the key change that made a
/// per-task row representable.
/// </para>
/// <para>
/// <strong>Why the write is a read-modify-write rather than <c>ExecuteUpdate</c>:</strong> the row
/// may not exist yet (a task's first turn), and creating it requires <c>project_id</c>, which lives
/// on the job. A targeted update could not create the row, and <c>ExecuteUpdate</c> is unsupported
/// by the in-memory provider every unit test in this assembly runs on — so the create path would be
/// the untested path, which is the one that gets the <c>project_id</c> foreign-key shape right.
/// </para>
/// <para>
/// <strong>Concurrency:</strong> last-write-wins, deliberately. Two writers on one task happen when
/// a timed-out activity's retry overlaps the turn it was retrying, and the loser's information is a
/// strictly older observation of the same tree. A concurrency token here would turn that benign
/// overlap into a failed checkpoint write, which is the outcome recovery cannot afford: the whole
/// point of the checkpoint is to survive a crash, and a crash is exactly when two writers overlap.
/// </para>
/// </remarks>
public class WorkspaceStateRepository(
    AgentMeshDbContext db,
    ILogger<WorkspaceStateRepository>? logger = null) : IWorkspaceStateRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Arrays of paths are written once and read once, and a human debugging a stuck job reads
        // the jsonb column directly. Indented output would double the stored size for no benefit.
        WriteIndented = false,
    };

    /// <summary>
    /// Cap on the persisted modified-file list.
    /// </summary>
    /// <remarks>
    /// <c>git status --short</c> on a repository with a stray <c>node_modules</c> or a botched
    /// <c>git rm -r</c> can emit thousands of paths. The checkpoint's job is to tell the next turn
    /// "you have uncommitted work in these places", and past a few dozen entries that message is
    /// already delivered; the remainder costs prompt tokens and column bytes to say nothing new.
    /// <para>
    /// The count is preserved separately from the truncated list, so the recovery note can still
    /// report the true number ("2,318 uncommitted files") and the agent can infer that the list is a
    /// sample rather than the whole truth. Silently truncating without saying so would be worse than
    /// not storing the list at all: the agent would plan around an incomplete file set.
    /// </para>
    /// </remarks>
    public const int MaxPersistedModifiedFiles = 60;

    /// <summary>Cap on the free-text summary, matched to <c>checkpoint_summary varchar(4096)</c>.</summary>
    public const int MaxSummaryLength = 4096;

    public async Task<bool> SaveCheckpointAsync(TaskWorkingCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (string.IsNullOrWhiteSpace(checkpoint.JobId) || !Guid.TryParse(checkpoint.JobId.Trim(), out var jobId))
        {
            logger?.LogWarning(
                "Checkpoint write skipped: job id '{JobId}' is not a GUID; nothing was persisted",
                checkpoint.JobId);
            return false;
        }

        if (!JobWorkspaceState.IsTaskScoped(checkpoint.TaskId))
        {
            logger?.LogWarning(
                "Checkpoint write skipped for job {JobId}: task id is blank, which would collapse every " +
                "task in the job onto the job-level row and overwrite the other tasks' state",
                jobId);
            return false;
        }

        var taskId = checkpoint.TaskId.Trim();

        var state = await db.JobWorkspaceStates
            .FirstOrDefaultAsync(s => s.JobId == jobId && s.TaskId == taskId, ct);

        if (state is null)
        {
            var projectId = await db.ProjectJobs
                .AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => j.ProjectId)
                .FirstOrDefaultAsync(ct);

            if (projectId is null)
            {
                // Not fatal by design: a checkpoint is orientation state, and losing it must not
                // fail a turn that just did real work. The caller logs the false and moves on.
                logger?.LogWarning(
                    "Checkpoint write skipped: job {JobId} does not exist, so project_id could be resolved",
                    jobId);
                return false;
            }

            state = new JobWorkspaceState { JobId = jobId, TaskId = taskId, ProjectId = projectId };
            db.JobWorkspaceStates.Add(state);
        }

        state.CheckpointBranch = Truncate(checkpoint.ActiveBranch, 128);
        state.CheckpointStatus = Truncate(checkpoint.Status, 32);
        state.CheckpointSummary = Truncate(checkpoint.LastSummary, MaxSummaryLength);
        state.CheckpointModifiedFilesJson = SerializeModifiedFiles(checkpoint.ModifiedFiles);
        state.CheckpointActiveStash = Truncate(checkpoint.ActiveStash, 512);
        state.CheckpointHeadCommit = Truncate(checkpoint.HeadCommit, 64);
        state.CheckpointFocus = Truncate(checkpoint.ChecklistFocus, 256);
        state.CheckpointTurnSessionId = TryParseGuid(checkpoint.TurnSessionId);
        state.CheckpointUpdatedAt = checkpoint.UpdatedAt == DateTimeOffset.UnixEpoch
            ? DateTimeOffset.UtcNow
            : checkpoint.UpdatedAt;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Checkpoint saved for job {JobId} task {TaskId}: branch={Branch} status={Status} " +
            "dirty={Dirty} stash={HasStash} head={Head}",
            jobId, taskId, state.CheckpointBranch, state.CheckpointStatus,
            checkpoint.FilesOrEmpty.Count, state.CheckpointActiveStash is not null,
            state.CheckpointHeadCommit);

        return true;
    }

    public async Task<TaskWorkingCheckpoint?> GetCheckpointAsync(
        string jobId,
        string taskId,
        CancellationToken ct = default)
    {
        if (!TryParseRequired(jobId, out var parsedJob) || !JobWorkspaceState.IsTaskScoped(taskId))
        {
            return null;
        }

        var normalizedTask = taskId.Trim();

        var row = await db.JobWorkspaceStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.JobId == parsedJob && s.TaskId == normalizedTask, ct);

        // HasCheckpoint, not row != null: a lease acquisition creates the row without ever writing a
        // checkpoint, and reporting that as "the task was previously worked with an unknown branch"
        // would put a confident lie into the recovery note.
        return row is null || !row.HasCheckpoint ? null : ToCheckpoint(row);
    }

    public async Task<IReadOnlyList<TaskWorkingCheckpoint>> GetCheckpointsForJobAsync(
        string jobId,
        CancellationToken ct = default)
    {
        if (!TryParseRequired(jobId, out var parsedJob))
        {
            return [];
        }

        // Newest first: the caller is asking "which task was I on", and the answer is the first row.
        // Id is the tiebreak so two checkpoints stamped in the same instant still return in a stable
        // order across reads — the prompt stack renders these, and a shuffled prefix costs the driver
        // its cached context.
        var rows = await db.JobWorkspaceStates
            .AsNoTracking()
            .Where(s => s.JobId == parsedJob
                        && s.CheckpointUpdatedAt != null
                        && s.TaskId != JobWorkspaceState.UnassignedTaskId)
            .OrderByDescending(s => s.CheckpointUpdatedAt)
            .ThenBy(s => s.TaskId)
            .ToListAsync(ct);

        return rows.Select(ToCheckpoint).ToList();
    }

    private static TaskWorkingCheckpoint ToCheckpoint(JobWorkspaceState row) => new(
        JobId: row.JobId.ToString(),
        TaskId: row.TaskId,
        ActiveBranch: row.CheckpointBranch ?? string.Empty,
        Status: row.CheckpointStatus ?? JobTaskStatus.Todo,
        LastSummary: row.CheckpointSummary ?? string.Empty,
        // Null in, null out: the column being NULL is the only representation of "the tree was never
        // inspected", and replacing it with an empty list here would turn that into a claim that the
        // tree was clean.
        ModifiedFiles: DeserializeModifiedFiles(row.CheckpointModifiedFilesJson),
        ActiveStash: row.CheckpointActiveStash,
        UpdatedAt: row.CheckpointUpdatedAt ?? row.UpdatedAt,
        HeadCommit: row.CheckpointHeadCommit,
        ChecklistFocus: row.CheckpointFocus,
        TurnSessionId: row.CheckpointTurnSessionId?.ToString());

    /// <summary>
    /// Serializes the dirty-file list, appending a marker element when the list was truncated.
    /// </summary>
    /// <remarks>
    /// The marker travels inside the array rather than in a parallel column because the array is the
    /// only place the list is stored, and a count column that disagrees with the array it describes
    /// is a second source of truth for the same fact. Null is returned for a null list so "never
    /// inspected" stays distinguishable from "inspected, clean" — see the migration note on
    /// <c>checkpoint_modified_files_json</c>.
    /// </remarks>
    internal static string? SerializeModifiedFiles(IReadOnlyList<string>? files)
    {
        if (files is null) return null;

        var stored = files.Take(MaxPersistedModifiedFiles).ToList();
        if (files.Count > stored.Count)
        {
            stored.Add($"… {files.Count - stored.Count} more (truncated; count is exact)");
        }

        return JsonSerializer.Serialize(stored, JsonOpts);
    }

    internal static IReadOnlyList<string> DeserializeModifiedFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
            return parsed ?? [];
        }
        catch (JsonException)
        {
            // The column is jsonb, so a non-array JSON value (an object someone wrote by hand, or a
            // string) is the realistic failure. Returning empty degrades the note to "uncommitted work
            // present, files unknown" rather than throwing inside a turn's prompt assembly.
            return [];
        }
    }

    private static bool TryParseRequired(string? value, out Guid parsed)
    {
        parsed = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Guid.TryParse(value.Trim(), out parsed);
    }

    private static Guid? TryParseGuid(string? value) =>
        TryParseRequired(value, out var parsed) ? parsed : null;

    /// <summary>
    /// Clamps to the column width, appending an ellipsis when text was dropped.
    /// </summary>
    /// <remarks>
    /// Truncation rather than an exception: Postgres raises 22001 on an over-long value, which would
    /// fail the whole save — and the field most likely to overflow, <c>checkpoint_summary</c>, is
    /// free text an agent wrote. Losing part of a summary is a smaller loss than losing the
    /// checkpoint, and the ellipsis keeps the truncation visible in the stored value instead of only
    /// in a log line that has already scrolled away.
    /// </remarks>
    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= max) return value;

        // Leave room for the marker so the result is exactly `max`, not max+1.
        const string marker = "…";
        var keep = Math.Max(0, max - marker.Length);
        return string.Concat(value.AsSpan(0, keep), marker);
    }
}
