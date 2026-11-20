using AgentMesh.Core.Activities;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMesh.Tests.Activities;

/// <summary>
/// What a halted <c>JobExecutionWorkflow</c> writes onto the job row, and what it refuses to overwrite.
/// </summary>
/// <remarks>
/// <para>
/// Every path that starts this workflow starts it fire-and-forget — the two API start sites and
/// recovery all discard the handle, and nothing awaits the result anywhere in the platform. The
/// incident this activity exists for: a run ended in an honest <c>Success: false</c> after 40
/// turns, and the job row went on claiming "Executing task" forever because the outcome had no
/// reader. The workflow now reports its own ending through this activity.
/// </para>
/// <para>
/// The write keeps the job <see cref="ProjectJobStatus.Active"/> on purpose: "Blocked" is derived
/// from the <c>Halted:</c> heartbeat prefix (see <c>DeriveLifecycleStatus</c>), and archiving on
/// the workflow's own authority would delete the operator's ability to <c>/recover</c> the job —
/// recovery refuses archived rows.
/// </para>
/// </remarks>
public class JobHaltPersistenceTests
{
    private static async Task<(AgentMeshDbContext Db, Guid JobId)> SeedAsync(
        ProjectJobStatus status,
        string? heartbeat)
    {
        // No `using` here: the returned context is the test's to dispose.
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "mesh-core",
            Title = "Job attachments",
            Status = status,
            ProgressHeartbeat = heartbeat
        });
        await db.SaveChangesAsync();
        return (db, jobId);
    }

    private static TimelineActivities Activities(AgentMeshDbContext db) =>
        new(db, NullLogger<TimelineActivities>.Instance);

    [Fact]
    public async Task HaltOnAnActiveJob_WritesTheHaltedHeartbeat_AndLeavesTheJobRecoverable()
    {
        var (db, jobId) = await SeedAsync(ProjectJobStatus.Active, "Executing task 1/6: Add ProjectJob.AttachmentsJson");
        using (db)
        {
            var reason = "Task 0 failed: Halted after 40 turns without reaching a terminal state.";

            await Activities(db).PersistJobHaltAsync(new PersistJobHaltInput(jobId.ToString(), reason));

            var job = db.ProjectJobs.Single(j => j.Id == jobId);
            job.ProgressHeartbeat.Should().Be($"Halted: {reason}",
                "the Halted: prefix is what the lifecycle derivation reads as Blocked");
            job.Status.Should().Be(ProjectJobStatus.Active,
                "a halt must stay recoverable — recovery refuses archived jobs, and /recover " +
                "is the documented next step after a halt");
        }
    }

    [Fact]
    public async Task HaltReasonThatAlreadyCarriesThePrefix_IsNotDoubled()
    {
        var (db, jobId) = await SeedAsync(ProjectJobStatus.Active, "Awaiting Review: PR #9");
        using (db)
        {
            await Activities(db).PersistJobHaltAsync(
                new PersistJobHaltInput(jobId.ToString(), "Halted: no review within 7 days; halting."));

            db.ProjectJobs.Single(j => j.Id == jobId)
                .ProgressHeartbeat.Should().Be("Halted: no review within 7 days; halting.");
        }
    }

    [Theory]
    [InlineData(ProjectJobStatus.Completed)]
    [InlineData(ProjectJobStatus.Archived)]
    public async Task TerminalJobs_AreNeverOverwritten(ProjectJobStatus terminal)
    {
        // The operator who clicked Complete stamped their own truth on the row; a straggling halt
        // from the cancelled run must not turn a deliberate close into a red Blocked badge.
        var (db, jobId) = await SeedAsync(terminal, "Pull request merged; job completed");
        using (db)
        {
            await Activities(db).PersistJobHaltAsync(new PersistJobHaltInput(jobId.ToString(), "late halt"));

            var job = db.ProjectJobs.Single(j => j.Id == jobId);
            job.ProgressHeartbeat.Should().Be("Pull request merged; job completed");
            job.Status.Should().Be(terminal);
        }
    }

    [Fact]
    public async Task UnknownJob_DoesNotThrow()
    {
        // The halt reason is still returned to whoever started the run (a result-await embedding,
        // or Temporal's own description of the closed run); a missing row must not make the
        // workflow's final activity fail and mask it.
        using var db = TestDbContextFactory.Create();

        var act = async () => await Activities(db)
            .PersistJobHaltAsync(new PersistJobHaltInput(Guid.NewGuid().ToString(), "any reason"));

        await act.Should().NotThrowAsync();
    }
}
