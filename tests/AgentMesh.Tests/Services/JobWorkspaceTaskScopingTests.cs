using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// <see cref="JobWorkspaceService"/> after <c>job_workspace_states</c> moved to a composite
/// <c>(job_id, task_id)</c> key.
/// </summary>
/// <remarks>
/// <para>
/// The service's existing callers are all job-scoped, and every lookup used to be
/// <c>FirstOrDefaultAsync(s =&gt; s.JobId == jobId)</c>. Against one row per job that is unambiguous.
/// Against the new key it returns an arbitrary row, so these tests exist to pin what "the row a
/// job-scoped caller means" now is, and to prove the two failure modes the change would otherwise
/// introduce cannot happen: hydrating a runner from another task's head commit, and releasing the wrong
/// task's lease.
/// </para>
/// <para>
/// The in-memory provider does not implement an unordered <c>FirstOrDefault</c> deterministically
/// either, which is the point: a test that passes here while the production query is non-deterministic
/// is the failure mode this class is written to avoid, so each test asserts on a specific row rather
/// than on "some row".
/// </para>
/// </remarks>
public class JobWorkspaceTaskScopingTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherJobId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TaskA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TaskB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly string _dbName = "WorkspaceScoping_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    [Fact]
    public async Task GetAsync_WithNoTaskReturnsTheMostRecentlyUpdatedRow()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        var now = DateTimeOffset.UtcNow;
        await AddRowAsync(db, JobId, TaskA.ToString(), "feat/a", "aaa1111", updated: now.AddMinutes(-30));
        await AddRowAsync(db, JobId, TaskB.ToString(), "feat/b", "bbb2222", updated: now);

        var active = await svc.GetAsync(JobId);

        active.Should().NotBeNull();
        active!.TaskId.Should().Be(TaskB.ToString(),
            "the row the platform touched most recently is the row the work is on");
        active.HeadCommit.Should().Be("bbb2222");
    }

    /// <summary>
    /// The tiebreak that makes the job-scoped read deterministic.
    /// </summary>
    /// <remarks>
    /// Two rows written in the same instant (a checkpoint save and a lease acquisition from one turn)
    /// would otherwise resolve arbitrarily. That is not a theoretical concern: both writes happen in
    /// the same activity and Postgres can stamp identical <c>updated_at</c> values.
    /// </remarks>
    [Fact]
    public async Task GetAsync_BreaksTiesOnTaskId()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        var same = DateTimeOffset.UtcNow;
        await AddRowAsync(db, JobId, TaskB.ToString(), "feat/b", "b", updated: same);
        await AddRowAsync(db, JobId, TaskA.ToString(), "feat/a", "a", updated: same);

        var first = await svc.GetAsync(JobId);
        var second = await svc.GetAsync(JobId);

        first!.TaskId.Should().Be(second!.TaskId, "repeated reads must agree");
        first.TaskId.Should().Be(TaskA.ToString(), "the tiebreak is task_id ascending, not insertion order");
    }

    [Fact]
    public async Task GetAsync_WithATaskReturnsThatExactRow()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        var now = DateTimeOffset.UtcNow;
        await AddRowAsync(db, JobId, TaskA.ToString(), "feat/a", "aaa", updated: now.AddMinutes(-10));
        await AddRowAsync(db, JobId, TaskB.ToString(), "feat/b", "bbb", updated: now);

        var a = await svc.GetAsync(JobId, TaskA.ToString());

        a!.TaskId.Should().Be(TaskA.ToString());
        a.HeadCommit.Should().Be("aaa",
            "a caller that named a task must not receive the newest task's baseline");
    }

    [Fact]
    public async Task GetAsync_WithAnUnknownTask_ReturnsNullRatherThanAnotherTasksRow()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        await AddRowAsync(db, JobId, TaskA.ToString(), "feat/a", "aaa", updated: DateTimeOffset.UtcNow);

        var missing = await svc.GetAsync(JobId, TaskB.ToString());

        // Null means "no state for that task yet", which callers already treat as a first save.
        // Returning TaskA's row would hydrate a runner onto a head commit chosen for different work,
        // and the resulting push rejection would be blamed on the remote.
        missing.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithTheSentinelTask_ReturnsTheJobLevelRow()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        var now = DateTimeOffset.UtcNow;
        await AddRowAsync(db, JobId, JobWorkspaceState.UnassignedTaskId, "feat/job-level", "joblevel", updated: now.AddMinutes(-5));
        await AddRowAsync(db, JobId, TaskA.ToString(), "feat/a", "taskrow", updated: now);

        // A null task id is the job-scoped form and resolves to the active row, which is the task row
        // here. The sentinel is addressed by naming it, not by omitting it.
        (await svc.GetAsync(JobId, (string?)null))!.HeadCommit.Should().Be("taskrow");
        (await svc.GetAsync(JobId, JobWorkspaceState.UnassignedTaskId))!.HeadCommit.Should().Be("joblevel");
    }

    /// <summary>
    /// Two tasks of one job hold independent leases.
    /// </summary>
    /// <remarks>
    /// This is what makes concurrent work on two checklist items possible without either save being
    /// reported as a lease conflict. Under the old single-row key it was impossible: one lease per job
    /// meant one worker per job, full stop.
    /// </remarks>
    [Fact]
    public async Task Leases_AreIndependentPerTask()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(JobId, "runner-a", "rook", TaskA.ToString());
        await svc.AcquireLeaseAsync(JobId, "runner-b", "sorren", TaskB.ToString());

        (await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskA.ToString()))
            .LeaseHolderAgentId.Should().Be("runner-a");
        (await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskB.ToString()))
            .LeaseHolderAgentId.Should().Be("runner-b");

        // Releasing one must not touch the other.
        await svc.ReleaseLeaseAsync(JobId, "runner-a", TaskA.ToString());

        (await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskA.ToString()))
            .LeaseHolderAgentId.Should().BeNull();
        (await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskB.ToString()))
            .LeaseHolderAgentId.Should().Be("runner-b",
                "a release scoped to task A must not unlock whoever holds task B");
    }

    [Fact]
    public async Task SaveAsync_WithATaskWritesThatTaskRowOnly()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(JobId, "runner-a", "rook", TaskA.ToString());
        await svc.AcquireLeaseAsync(JobId, "runner-a", "rook", TaskB.ToString());

        var result = await svc.SaveAsync(new SaveWorkspaceRequest(
            JobId, "runner-a", "rook", HeadCommit: "newhead", TaskId: TaskA.ToString()));

        result.Accepted.Should().BeTrue(result.Warning ?? "");
        result.Snapshot.HeadCommit.Should().Be("newhead");

        (await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskA.ToString()))
            .HeadCommit.Should().Be("newhead");
        (await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskB.ToString()))
            .HeadCommit.Should().BeNull(
                "saving task A must not claim task B's tree was captured — that is how work gets lost " +
                "on handoff, which is the one thing this table exists to prevent");
    }

    [Fact]
    public async Task SaveAsync_WithNoTaskWritesTheJobLevelRow()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        await svc.SaveAsync(new SaveWorkspaceRequest(JobId, "runner-a", "rook", HeadCommit: "legacy"));

        var row = await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == JobWorkspaceState.UnassignedTaskId);
        row.HeadCommit.Should().Be("legacy",
            "a runner that does not name a task writes job-level state, exactly as before this feature");
    }

    [Fact]
    public async Task SaveAsync_WhenAnotherAgentHoldsThatTasksLease_Warns()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(JobId, "runner-a", "rook", TaskA.ToString());

        var result = await svc.SaveAsync(new SaveWorkspaceRequest(
            JobId, "runner-b", "sorren", HeadCommit: "h", TaskId: TaskA.ToString()));

        // Last writer wins, loudly — the same contract the job-scoped version had.
        result.Accepted.Should().BeTrue();
        result.Warning.Should().Contain("runner-a").And.Contain("runner-b");
    }

    /// <summary>
    /// The reaper releases distinct jobs, not one entry per task row.
    /// </summary>
    /// <remarks>
    /// The caller counts jobs it no longer owns. Reporting a three-task job three times would make the
    /// reaper's log and any per-job bookkeeping overstate how much work was orphaned.
    /// </remarks>
    [Fact]
    public async Task ReleaseLeasesForAgent_ReturnsDistinctJobIds()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(JobId, "runner-a", "rook", TaskA.ToString());
        await svc.AcquireLeaseAsync(JobId, "runner-a", "rook", TaskB.ToString());
        await svc.AcquireLeaseAsync(OtherJobId, "runner-a", "rook", TaskA.ToString());

        var released = await svc.ReleaseLeasesForAgentAsync("runner-a");

        released.Should().BeEquivalentTo(new[] { JobId, OtherJobId });

        (await db.JobWorkspaceStates.CountAsync(s => s.LeaseHolderAgentId == "runner-a")).Should().Be(0);
    }

    [Fact]
    public async Task ReleaseLeasesForAgent_LeavesTheDurableSnapshotIntact()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(JobId, "runner-a", "rook", TaskA.ToString());
        await svc.SaveAsync(new SaveWorkspaceRequest(
            JobId, "runner-a", "rook", HeadCommit: "keepme", TaskId: TaskA.ToString()));

        await svc.ReleaseLeasesForAgentAsync("runner-a");

        var row = await db.JobWorkspaceStates.SingleAsync(s => s.TaskId == TaskA.ToString());
        row.LeaseHolderAgentId.Should().BeNull();
        row.HeadCommit.Should().Be("keepme", "we release the lock, not the work");
    }

    [Fact]
    public async Task GetSnapshotAsync_PrefersTheActiveTasksDurableBits()
    {
        using var db = Db();
        await SeedAsync(db);
        var svc = new JobWorkspaceService(db);

        var now = DateTimeOffset.UtcNow;
        await AddRowAsync(db, JobId, TaskA.ToString(), "feat/a", "old-head", updated: now.AddMinutes(-20));
        await AddRowAsync(db, JobId, TaskB.ToString(), "feat/b", "new-head", updated: now);

        var snapshot = await svc.GetSnapshotAsync(JobId);

        snapshot!.HasDurableState.Should().BeTrue();
        snapshot.HeadCommit.Should().Be("new-head");
    }

    [Fact]
    public async Task AcquireLeaseAsync_WhenJobUnknown_CreatesNothing()
    {
        using var db = Db();

        await new JobWorkspaceService(db).AcquireLeaseAsync(
            Guid.NewGuid(), "runner-a", "rook", TaskA.ToString());

        // project_id is NOT NULL and only the job knows it, so there is nothing to create from.
        (await db.JobWorkspaceStates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void NormalizeTaskId_MapsBlankToTheSentinel()
    {
        JobWorkspaceState.NormalizeTaskId(null).Should().Be(JobWorkspaceState.UnassignedTaskId);
        JobWorkspaceState.NormalizeTaskId("   ").Should().Be(JobWorkspaceState.UnassignedTaskId);
        JobWorkspaceState.NormalizeTaskId("  feat  ").Should().Be("feat");
    }

    [Fact]
    public void IsTaskScoped_RejectsTheSentinelAndBlank()
    {
        JobWorkspaceState.IsTaskScoped(null).Should().BeFalse();
        JobWorkspaceState.IsTaskScoped("").Should().BeFalse();
        JobWorkspaceState.IsTaskScoped(JobWorkspaceState.UnassignedTaskId).Should().BeFalse();
        JobWorkspaceState.IsTaskScoped(TaskA.ToString()).Should().BeTrue();
    }

    /// <summary>
    /// The sentinel can never collide with a real task id.
    /// </summary>
    /// <remarks>
    /// Task ids are GUIDs rendered in either case. If <c>Guid.TryParse</c> ever accepted the sentinel,
    /// a job-level row could be mistaken for a task row and the two would fight over the same key.
    /// </remarks>
    [Fact]
    public void SentinelIsNotParsableAsATaskId()
    {
        Guid.TryParse(JobWorkspaceState.UnassignedTaskId, out var parsed).Should().BeFalse();
        parsed.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────

    private static async Task SeedAsync(AgentMeshDbContext db)
    {
        db.Projects.Add(new Project { Id = "agent-mesh", Name = "Agent Mesh" });
        db.ProjectJobs.Add(new ProjectJob { Id = JobId, ProjectId = "agent-mesh", Title = "A" });
        db.ProjectJobs.Add(new ProjectJob { Id = OtherJobId, ProjectId = "agent-mesh", Title = "B" });
        await db.SaveChangesAsync();
    }

    private static async Task AddRowAsync(
        AgentMeshDbContext db,
        Guid jobId,
        string taskId,
        string gitRef,
        string? headCommit,
        DateTimeOffset updated)
    {
        db.JobWorkspaceStates.Add(new JobWorkspaceState
        {
            JobId = jobId,
            TaskId = taskId,
            ProjectId = "agent-mesh",
            GitRef = gitRef,
            HeadCommit = headCommit,
            UpdatedAt = updated,
            CreatedAt = updated,
        });

        await db.SaveChangesAsync();
    }
}
