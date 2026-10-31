using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Repositories;

/// <summary>
/// The per-task working checkpoint: what it takes to store one, and what it takes to get it back.
/// </summary>
/// <remarks>
/// The interesting cases are the ones where a checkpoint would lie: a job-level row that is not a
/// checkpoint, two tasks of one job overwriting each other, a dirty-file list truncated without saying
/// so, and a null list rendered as "clean". Each is asserted here because each is a wrong answer an
/// agent would act on.
/// </remarks>
public class WorkspaceStateRepositoryTests
{
    private static readonly Guid JobId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TaskA = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TaskB = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly string _dbName = "WorkspaceState_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    private static TaskWorkingCheckpoint Checkpoint(
        Guid taskId,
        string branch,
        string summary,
        IReadOnlyList<string>? modified = null,
        string? stash = null,
        DateTimeOffset? updatedAt = null) => new(
            JobId.ToString(),
            taskId.ToString(),
            branch,
            JobTaskStatus.InProgress,
            summary,
            modified ?? [],
            stash,
            updatedAt ?? DateTimeOffset.UtcNow,
            HeadCommit: "abc1234 " + summary);

    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveCheckpoint_WhenRowMissing_CreatesItWithTheJobsProject()
    {
        using var db = Db();
        await SeedJobAsync(db);

        var saved = await new WorkspaceStateRepository(db)
            .SaveCheckpointAsync(Checkpoint(TaskA, "feat/a", "started"));

        saved.Should().BeTrue();

        var row = await db.JobWorkspaceStates.SingleAsync(s => s.JobId == JobId);
        row.ProjectId.Should().Be("agent-mesh",
            "project_id is NOT NULL and only the job knows it, so the create path must read the job");
        row.TaskId.Should().Be(TaskA.ToString());
        row.HasCheckpoint.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCheckpoint_WhenJobUnknown_ReturnsFalseRatherThanThrowing()
    {
        using var db = Db();

        var saved = await new WorkspaceStateRepository(db)
            .SaveCheckpointAsync(Checkpoint(TaskA, "feat/a", "orphan work"));

        // A checkpoint write must never fail a turn that just did real work; the caller logs the false.
        saved.Should().BeFalse();
        (await db.JobWorkspaceStates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SaveCheckpoint_WithBlankTaskId_IsRefused()
    {
        using var db = Db();
        await SeedJobAsync(db);

        var blank = Checkpoint(TaskA, "feat/a", "x") with { TaskId = "  " };

        (await new WorkspaceStateRepository(db).SaveCheckpointAsync(blank)).Should().BeFalse(
            "a blank task id would collapse every task in the job onto the job-level row and silently " +
            "overwrite the other tasks' state — the exact bug the composite key exists to prevent");
    }

    /// <summary>
    /// Two tasks of one job keep independent checkpoints.
    /// </summary>
    /// <remarks>
    /// The regression this whole feature turned on. Before the composite key, the second save replaced
    /// the first row's branch and summary, and the first task resumed onto the second task's branch
    /// while believing its own work was the summary the harness had just overwritten.
    /// </remarks>
    [Fact]
    public async Task TwoTasksOfOneJob_KeepIndependentCheckpoints()
    {
        using var db = Db();
        await SeedJobAsync(db);
        var repo = new WorkspaceStateRepository(db);

        await repo.SaveCheckpointAsync(Checkpoint(TaskA, "feat/task-a", "A: wired the parser", ["src/a.cs"]));
        await repo.SaveCheckpointAsync(Checkpoint(TaskB, "feat/task-b", "B: fixed the flake", ["src/b.cs"], stash: "stash@{0}: On feat/task-b: wip"));

        var a = await repo.GetCheckpointAsync(JobId.ToString(), TaskA.ToString());
        var b = await repo.GetCheckpointAsync(JobId.ToString(), TaskB.ToString());

        a!.ActiveBranch.Should().Be("feat/task-a");
        a.LastSummary.Should().Contain("wired the parser");
        a.ModifiedFiles.Should().Equal(new[] { "src/a.cs" });
        a.ActiveStash.Should().BeNull();

        b!.ActiveBranch.Should().Be("feat/task-b");
        b.LastSummary.Should().Contain("fixed the flake");
        b.ModifiedFiles.Should().Equal(new[] { "src/b.cs" });
        b.ActiveStash.Should().Be("stash@{0}: On feat/task-b: wip");

        (await db.JobWorkspaceStates.CountAsync(s => s.JobId == JobId)).Should().Be(2);
    }

    [Fact]
    public async Task SaveCheckpoint_IsIdempotentPerTask_AndReplacesRatherThanAppends()
    {
        using var db = Db();
        await SeedJobAsync(db);
        var repo = new WorkspaceStateRepository(db);

        await repo.SaveCheckpointAsync(Checkpoint(TaskA, "feat/a", "first summary", ["src/one.cs"]));
        await repo.SaveCheckpointAsync(Checkpoint(TaskA, "feat/a", "second summary", ["src/two.cs"]));

        var loaded = await repo.GetCheckpointAsync(JobId.ToString(), TaskA.ToString());
        loaded!.LastSummary.Should().Be("second summary");
        // Explicit array: Equal's params overload would read the because-string as a second expected
        // element and demand two paths.
        loaded.ModifiedFiles.Should().Equal(new[] { "src/two.cs" },
            "the list is replaced, not merged — a stale path from an earlier turn would send the agent " +
            "to a file that is already committed");

        (await db.JobWorkspaceStates.CountAsync(s => s.JobId == JobId)).Should().Be(1);
    }

    [Fact]
    public async Task GetCheckpoint_WhenNeverSaved_ReturnsNull()
    {
        using var db = Db();
        await SeedJobAsync(db);

        // A lease acquisition creates the row without a checkpoint. Returning an empty checkpoint for it
        // would claim the task was previously worked, and the note would report an unknown branch as
        // though it had measured one.
        await new ApiLease(db).AcquireAsync();

        var loaded = await new WorkspaceStateRepository(db).GetCheckpointAsync(JobId.ToString(), TaskA.ToString());
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetCheckpoint_WithBlankTaskId_ReturnsNullRatherThanTheJobLevelRow()
    {
        using var db = Db();
        await SeedJobAsync(db);
        var repo = new WorkspaceStateRepository(db);

        await repo.SaveCheckpointAsync(Checkpoint(TaskA, "feat/a", "real work"));

        (await repo.GetCheckpointAsync(JobId.ToString(), "   ")).Should().BeNull(
            "falling back to another task's checkpoint would hand the agent a confident description of " +
            "work it is not doing");
    }

    /// <summary>
    /// A null dirty-file list stays distinguishable from an empty one.
    /// </summary>
    /// <remarks>
    /// Null is "never inspected", empty is "inspected and clean". Collapsing them would let a
    /// checkpoint written by a path that never looked assert the tree is clean — and an agent that
    /// believes that runs <c>git checkout .</c> on work that exists only in its stash.
    /// </remarks>
    [Fact]
    public async Task ModifiedFiles_NullAndEmptyAreDistinct()
    {
        using var db = Db();
        await SeedJobAsync(db);
        var repo = new WorkspaceStateRepository(db);

        // Constructed directly rather than through Checkpoint(): that helper coalesces a null list to
        // an empty one, which is the right default for every other test here but would make this
        // assertion pass without ever storing a NULL.
        await repo.SaveCheckpointAsync(new TaskWorkingCheckpoint(
            JobId.ToString(), TaskA.ToString(), "feat/a", JobTaskStatus.InProgress, "null list",
            ModifiedFiles: null!, ActiveStash: null, UpdatedAt: DateTimeOffset.UtcNow));
        await repo.SaveCheckpointAsync(Checkpoint(TaskB, "feat/b", "empty list", modified: []));

        var rowNull = await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskA.ToString());
        var rowEmpty = await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskB.ToString());

        rowNull.CheckpointModifiedFilesJson.Should().BeNull();
        rowEmpty.CheckpointModifiedFilesJson.Should().Be("[]");

        (await repo.GetCheckpointAsync(JobId.ToString(), TaskA.ToString()))!.ModifiedFiles.Should().BeEmpty();
        (await repo.GetCheckpointAsync(JobId.ToString(), TaskB.ToString()))!.ModifiedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ModifiedFiles_AreCappedWithTheElisionRecordedInTheList()
    {
        using var db = Db();
        await SeedJobAsync(db);

        var many = Enumerable.Range(0, WorkspaceStateRepository.MaxPersistedModifiedFiles + 25)
            .Select(i => $"src/generated/file{i:D4}.cs")
            .ToList();

        await new WorkspaceStateRepository(db).SaveCheckpointAsync(
            Checkpoint(TaskA, "feat/a", "generated a lot", many));

        using var reader = Db();
        var loaded = await new WorkspaceStateRepository(reader)
            .GetCheckpointAsync(JobId.ToString(), TaskA.ToString());

        loaded!.ModifiedFiles.Should().HaveCount(WorkspaceStateRepository.MaxPersistedModifiedFiles + 1,
            "the stored list carries one extra marker element naming how much was elided");
        loaded.ModifiedFiles[^1].Should().Contain("25 more").And.Contain("count is exact");
    }

    [Fact]
    public void SerializeModifiedFiles_KeepsNullAndEmptyApart()
    {
        WorkspaceStateRepository.SerializeModifiedFiles(null).Should().BeNull();
        WorkspaceStateRepository.SerializeModifiedFiles([]).Should().Be("[]");
    }

    [Fact]
    public void DeserializeModifiedFiles_ToleratesANonArrayJsonbValue()
    {
        // jsonb accepts any JSON value; an operator debugging a stuck job can write one by hand. The
        // read must degrade to "no paths named" rather than throw inside prompt assembly.
        WorkspaceStateRepository.DeserializeModifiedFiles("""{"unexpected":"object"}""").Should().BeEmpty();
        WorkspaceStateRepository.DeserializeModifiedFiles("\"a string\"").Should().BeEmpty();
        WorkspaceStateRepository.DeserializeModifiedFiles(null).Should().BeEmpty();
        WorkspaceStateRepository.DeserializeModifiedFiles("").Should().BeEmpty();
    }

    [Fact]
    public async Task LongSummary_IsTruncatedRatherThanRejectedByTheColumn()
    {
        using var db = Db();
        await SeedJobAsync(db);

        var huge = new string('x', WorkspaceStateRepository.MaxSummaryLength * 2);

        var saved = await new WorkspaceStateRepository(db)
            .SaveCheckpointAsync(Checkpoint(TaskA, "feat/a", huge));

        saved.Should().BeTrue("an over-long summary must not fail the whole checkpoint write");

        using var reader = Db();
        var loaded = await new WorkspaceStateRepository(reader)
            .GetCheckpointAsync(JobId.ToString(), TaskA.ToString());

        loaded!.LastSummary.Should().HaveLength(WorkspaceStateRepository.MaxSummaryLength)
            .And.EndWith("…", "the truncation stays visible in the stored value");
    }

    [Fact]
    public async Task GetCheckpointsForJob_ReturnsOnlyTaskCheckpointsNewestFirst()
    {
        using var db = Db();
        await SeedJobAsync(db);
        var repo = new WorkspaceStateRepository(db);

        var old = DateTimeOffset.UtcNow.AddHours(-3);
        var recent = DateTimeOffset.UtcNow.AddMinutes(-5);

        await repo.SaveCheckpointAsync(Checkpoint(TaskA, "feat/a", "older", updatedAt: old));
        await repo.SaveCheckpointAsync(Checkpoint(TaskB, "feat/b", "newer", updatedAt: recent));

        // A job-level row with no checkpoint must not appear: it is not a task the agent was working on.
        await new ApiLease(db).AcquireAsync();

        var all = await repo.GetCheckpointsForJobAsync(JobId.ToString());

        all.Should().HaveCount(2);
        all[0].TaskId.Should().Be(TaskB.ToString(), "the caller is asking 'which task was I on'");
        all[1].TaskId.Should().Be(TaskA.ToString());
    }

    [Fact]
    public async Task RoundTrips_ThroughAFreshContext()
    {
        using var db = Db();
        await SeedJobAsync(db);

        var updatedAt = new DateTimeOffset(2026, 10, 30, 12, 34, 56, TimeSpan.Zero);
        await new WorkspaceStateRepository(db).SaveCheckpointAsync(
            Checkpoint(TaskA, "feat/round-trip", "the summary", ["src/x.cs", "src/y.cs"],
                stash: "stash@{0}: On feat/round-trip: wip", updatedAt: updatedAt)
            with { HeadCommit = "deadbee fix: thing", ChecklistFocus = "Implement the reconciler" });

        // A second context over the same store: proves the row landed in the database rather than
        // merely sitting in the writer's change tracker.
        using var reader = Db();
        var loaded = await new WorkspaceStateRepository(reader)
            .GetCheckpointAsync(JobId.ToString(), TaskA.ToString());

        loaded!.JobId.Should().Be(JobId.ToString());
        loaded.TaskId.Should().Be(TaskA.ToString());
        loaded.ActiveBranch.Should().Be("feat/round-trip");
        loaded.Status.Should().Be(JobTaskStatus.InProgress);
        loaded.LastSummary.Should().Be("the summary");
        loaded.ModifiedFiles.Should().Equal(new[] { "src/x.cs", "src/y.cs" });
        loaded.ActiveStash.Should().Be("stash@{0}: On feat/round-trip: wip");
        loaded.HeadCommit.Should().Be("deadbee fix: thing");
        loaded.ChecklistFocus.Should().Be("Implement the reconciler");
        loaded.UpdatedAt.Should().Be(updatedAt);
        loaded.HasUncommittedWork.Should().BeTrue();
    }

    [Fact]
    public async Task SaveCheckpoint_WithANonGuidJobId_ReturnsFalse()
    {
        using var db = Db();

        var saved = await new WorkspaceStateRepository(db).SaveCheckpointAsync(
            Checkpoint(TaskA, "feat/a", "x") with { JobId = "not-a-guid" });

        saved.Should().BeFalse();
        (await db.JobWorkspaceStates.CountAsync()).Should().Be(0);
    }

    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a job-level workspace row through the lease path, which writes no checkpoint.
    /// </summary>
    /// <remarks>
    /// Inlined rather than calling <c>JobWorkspaceService</c> so this fixture does not depend on the
    /// API project's service; what matters here is the row shape (lease set, checkpoint columns null),
    /// which is reproduced exactly.
    /// </remarks>
    private sealed class ApiLease(AgentMeshDbContext db)
    {
        public async Task AcquireAsync()
        {
            db.JobWorkspaceStates.Add(new JobWorkspaceState
            {
                JobId = JobId,
                TaskId = JobWorkspaceState.UnassignedTaskId,
                ProjectId = "agent-mesh",
                LeaseHolderAgentId = "driver-rook",
                LeaseAcquiredAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedJobAsync(AgentMeshDbContext db)
    {
        db.Projects.Add(new Project { Id = "agent-mesh", Name = "Agent Mesh" });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = JobId,
            ProjectId = "agent-mesh",
            Title = "Context recovery",
            GitBranchName = "feat/recovery",
        });

        await db.SaveChangesAsync();
    }
}
