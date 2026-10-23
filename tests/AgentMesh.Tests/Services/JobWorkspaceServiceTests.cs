using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Services;

public class JobWorkspaceServiceTests
{
    private static ProjectJob NewJob(AgentMesh.Infrastructure.Data.AgentMeshDbContext db, Guid? id = null)
    {
        if (db.Projects.Find("app") == null)
        {
            db.Projects.Add(new Project { Id = "app", Name = "App", WorkspaceType = WorkspaceType.Git });
        }

        var job = new ProjectJob
        {
            Id = id ?? Guid.NewGuid(),
            ProjectId = "app",
            Title = "Job",
            GitBranchName = "mesh/job-abc"
        };
        db.ProjectJobs.Add(job);
        db.SaveChanges();
        return job;
    }

    [Fact]
    public async Task Save_CreatesStateAndRecordsAllCoordinationPointers()
    {
        using var db = TestDbContextFactory.Create();
        var job = NewJob(db);
        var svc = new JobWorkspaceService(db);

        var result = await svc.SaveAsync(new SaveWorkspaceRequest(
            job.Id, "rook-runner", "rook", Guid.NewGuid(),
            HeadCommit: "abc123", ContentHash: "sha256:def",
            GitRemoteUrl: "http://git/app.git", GitRef: "mesh/job-abc",
            BundleS3Key: "jobs/abc/bundle.tgz"));

        result.Accepted.Should().BeTrue();
        result.Warning.Should().BeNull();
        result.Snapshot.HasDurableState.Should().BeTrue();
        result.Snapshot.HeadCommit.Should().Be("abc123");

        var state = await db.JobWorkspaceStates.SingleAsync(s => s.JobId == job.Id);
        state.HeadCommit.Should().Be("abc123");
        state.GitRemoteUrl.Should().Be("http://git/app.git");
        state.BundleS3Key.Should().Be("jobs/abc/bundle.tgz");
        state.LastSavedByAgentId.Should().Be("rook-runner");
        state.LastSavedByPersonaId.Should().Be("rook");
        state.LastSavedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Save_PartialUpdate_DoesNotBlankOutKnownGoodPointers()
    {
        using var db = TestDbContextFactory.Create();
        var job = NewJob(db);
        var svc = new JobWorkspaceService(db);

        await svc.SaveAsync(new SaveWorkspaceRequest(job.Id, "runner-a", "rook", null,
            HeadCommit: "aaa", GitRemoteUrl: "http://git/app.git", GitRef: "mesh/job-abc"));

        // Second runner could not reach the remote but has a bundle. It must not erase the remote.
        var second = await svc.SaveAsync(new SaveWorkspaceRequest(job.Id, "runner-a", "rook", null,
            HeadCommit: "bbb", BundleS3Key: "jobs/abc/bundle2.tgz"));

        second.Accepted.Should().BeTrue();
        var state = await db.JobWorkspaceStates.SingleAsync(s => s.JobId == job.Id);
        state.HeadCommit.Should().Be("bbb", "a newer save advances HEAD");
        state.GitRemoteUrl.Should().Be("http://git/app.git", "an unreachable remote must not clear the pointer");
        state.GitRef.Should().Be("mesh/job-abc");
        state.BundleS3Key.Should().Be("jobs/abc/bundle2.tgz");
    }

    [Fact]
    public async Task Save_WithReleaseLease_FreesWorkspaceForNextHolder()
    {
        using var db = TestDbContextFactory.Create();
        var job = NewJob(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(job.Id, "runner-a", "rook");
        await svc.SaveAsync(new SaveWorkspaceRequest(job.Id, "runner-a", "rook", null,
            HeadCommit: "aaa", ReleaseLease: true));

        var state = await db.JobWorkspaceStates.SingleAsync(s => s.JobId == job.Id);
        state.LeaseHolderAgentId.Should().BeNull();
        state.HeadCommit.Should().Be("aaa", "releasing the lock must not discard the work");
    }

    [Fact]
    public async Task Save_FromNonHolder_AcceptsBitsButWarnsAboutConcurrency()
    {
        using var db = TestDbContextFactory.Create();
        var job = NewJob(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(job.Id, "runner-a", "rook");

        var result = await svc.SaveAsync(new SaveWorkspaceRequest(job.Id, "runner-b", "sorrn", null,
            HeadCommit: "ccc"));

        // We accept (last write wins) but the conflict must be visible, not swallowed.
        result.Accepted.Should().BeTrue();
        result.Warning.Should().NotBeNull();
        result.Warning.Should().Contain("runner-a").And.Contain("runner-b");

        var state = await db.JobWorkspaceStates.SingleAsync(s => s.JobId == job.Id);
        state.LeaseHolderAgentId.Should().Be("runner-b", "the lease follows the bits");
    }

    [Fact]
    public async Task ReleaseLease_FromWrongAgent_DoesNotUnlockCurrentHolder()
    {
        using var db = TestDbContextFactory.Create();
        var job = NewJob(db);
        var svc = new JobWorkspaceService(db);

        await svc.AcquireLeaseAsync(job.Id, "runner-b", "sorrn");

        // A late-waking previous holder must not be able to unlock the new one.
        await svc.ReleaseLeaseAsync(job.Id, "runner-a");

        var state = await db.JobWorkspaceStates.SingleAsync(s => s.JobId == job.Id);
        state.LeaseHolderAgentId.Should().Be("runner-b");
    }

    [Fact]
    public async Task ReleaseLeasesForAgent_ClearsOnlyThatAgentsLeasesAndKeepsSnapshots()
    {
        using var db = TestDbContextFactory.Create();
        var jobA = NewJob(db);
        var jobB = NewJob(db);
        var svc = new JobWorkspaceService(db);

        await svc.SaveAsync(new SaveWorkspaceRequest(jobA.Id, "runner-a", "rook", null, HeadCommit: "a1"));
        await svc.SaveAsync(new SaveWorkspaceRequest(jobB.Id, "runner-a", "rook", null, HeadCommit: "b1"));
        await svc.SaveAsync(new SaveWorkspaceRequest(jobB.Id, "runner-b", "sorrn", null, HeadCommit: "b2"));

        var freed = await svc.ReleaseLeasesForAgentAsync("runner-a");

        freed.Should().BeEquivalentTo(new[] { jobA.Id });
        (await db.JobWorkspaceStates.SingleAsync(s => s.JobId == jobA.Id)).LeaseHolderAgentId.Should().BeNull();
        (await db.JobWorkspaceStates.SingleAsync(s => s.JobId == jobB.Id)).LeaseHolderAgentId.Should().Be("runner-b");
        (await db.JobWorkspaceStates.SingleAsync(s => s.JobId == jobA.Id)).HeadCommit.Should().Be("a1");
    }

    [Fact]
    public async Task GetSnapshot_WhenNothingSaved_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();
        var job = NewJob(db);
        var svc = new JobWorkspaceService(db);

        (await svc.GetSnapshotAsync(job.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_LeaseOnlyWithoutAnyBits_IsNotTreatedAsDurable()
    {
        using var db = TestDbContextFactory.Create();
        var job = NewJob(db);
        var svc = new JobWorkspaceService(db);

        // A lease alone means "someone is working", not "there is work to hydrate from".
        await svc.AcquireLeaseAsync(job.Id, "runner-a", "rook");

        var snapshot = await svc.GetSnapshotAsync(job.Id);
        snapshot.Should().NotBeNull();
        snapshot!.HasDurableState.Should().BeFalse();
    }

    [Fact]
    public async Task Save_ForUnknownJob_IsRejected()
    {
        using var db = TestDbContextFactory.Create();
        var svc = new JobWorkspaceService(db);

        var result = await svc.SaveAsync(new SaveWorkspaceRequest(Guid.NewGuid(), "runner-a", null, null, "aaa"));

        result.Accepted.Should().BeFalse();
        result.Warning.Should().Contain("not found");
    }
}
