using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMesh.Tests.Services;

public class JobHandoffServiceTests : IDisposable
{
    private readonly AgentMeshDbContext db = TestDbContextFactory.Create();
    private readonly JobWorkspaceService workspaces;
    private readonly JobHandoffService handoffs;
    private readonly Guid jobId = Guid.NewGuid();

    public JobHandoffServiceTests()
    {
        workspaces = new JobWorkspaceService(db);
        var turns = new JobTurnDispatcher(db, hubContext: null, workspaces: workspaces);
        handoffs = new JobHandoffService(
            db, turns, workspaces,
            hubContext: null,
            options: Options.Create(new JobHandoffOptions { GraceSeconds = 180 }));

        db.Projects.Add(new Project
        {
            Id = "app",
            Name = "App",
            WorkspaceType = WorkspaceType.Git,
            GitRepositoryUrl = "http://git/app.git",
            GitBaseBranch = "main"
        });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "app",
            Title = "Fix retry ordering",
            GitBranchName = "mesh/job-abc"
        });
        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", Role = "Engineer", DriverId = "runner-rook" });
        db.Personas.Add(new Persona { Id = "sorrn", Name = "Sorrn", Role = "Architect", DriverId = "runner-sorrn" });
        db.Agents.Add(new Agent { Id = "runner-rook", Name = "Rook Runner", RuntimeType = RuntimeType.CliAgy });
        db.Agents.Add(new Agent { Id = "runner-sorrn", Name = "Sorrn Runner", RuntimeType = RuntimeType.CliAgy });
        db.JobMessages.Add(new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            SenderType = "persona",
            SenderId = "rook",
            Content = "Pushed the retry logic, @sorrn please review the ordering assumptions.",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }

    private Guid NewOutgoingSession()
    {
        var s = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Job Turn (@rook)",
            Status = "thinking",
            PersonaId = "rook",
            DriverId = "runner-rook"
        };
        db.AgentSessions.Add(s);
        db.SaveChanges();
        return s.Id;
    }

    private DeclareHandoffRequest Declare(string to = "sorrn") =>
        new(jobId, "rook", to, NewOutgoingSession());

    [Fact]
    public async Task Declare_KeepsIncomingAgentGated_UntilConfirmation()
    {
        var decision = await handoffs.DeclareAsync(Declare());

        decision.Accepted.Should().BeTrue();
        decision.Handoff!.Status.Should().Be(JobHandoffStatus.Pending);
        decision.Handoff.ToTurnSessionId.Should().BeNull();

        // The gate is the whole point: no session may exist for the incoming persona yet.
        db.AgentSessions.Should().NotContain(s => s.PersonaId == "sorrn");
        db.JobMessages.Should().Contain(m => m.SenderType == "system" && m.Content.Contains("@Sorrn"));
    }

    [Fact]
    public async Task Declare_SetsGraceDeadlineAtConfiguredWindow()
    {
        var before = DateTimeOffset.UtcNow;
        var decision = await handoffs.DeclareAsync(Declare());

        var deadline = decision.Handoff!.ReleaseDeadlineAt;
        deadline.Should().BeOnOrAfter(before + TimeSpan.FromSeconds(179));
        deadline.Should().BeOnOrBefore(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(181));
    }

    [Fact]
    public async Task Confirm_ByOutgoingAgent_ReleasesAndDispatchesIncomingTurn()
    {
        await handoffs.DeclareAsync(Declare());

        var decision = await handoffs.ConfirmAsync(jobId, agentId: "runner-rook", note: "Bundle pushed, HEAD is 3f2a1c.");

        decision.Accepted.Should().BeTrue();
        decision.Handoff!.Status.Should().Be(JobHandoffStatus.InFlight);
        decision.Handoff.ReleaseReason.Should().Be(JobHandoffReleaseReason.OutgoingConfirmed);
        decision.Handoff.ToTurnSessionId.Should().NotBeNull();

        var incoming = db.AgentSessions.Single(s => s.PersonaId == "sorrn");
        incoming.Status.Should().Be("thinking");
        incoming.DriverId.Should().Be("runner-sorrn");

        var prompt = db.SessionMessages.Single(m => m.SessionId == incoming.Id && m.EventType == "prompt");
        prompt.Content.Should().Contain("Handoff from @Rook");
        prompt.Content.Should().Contain("Bundle pushed, HEAD is 3f2a1c.");
        prompt.Content.Should().Contain("retry logic", "the incoming agent gets the outgoing agent's last message");
    }

    [Fact]
    public async Task Confirm_ByWrongAgent_IsRefused()
    {
        await handoffs.DeclareAsync(Declare());

        // The incoming agent must not be able to release itself into a still-writing workspace.
        var decision = await handoffs.ConfirmAsync(jobId, agentId: "runner-sorrn", note: "I'll take it now thanks");

        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Contain("runner-rook");
        db.AgentSessions.Should().NotContain(s => s.PersonaId == "sorrn");
    }

    [Fact]
    public async Task Confirm_OperatorOverride_BypassesOutgoingAgentCheck()
    {
        await handoffs.DeclareAsync(Declare());

        var decision = await handoffs.ConfirmAsync(
            jobId, agentId: "operator", note: "hand over now", operatorOverride: true);

        decision.Accepted.Should().BeTrue();
        decision.Handoff!.ReleaseReason.Should().Be(JobHandoffReleaseReason.OperatorOverride);
        db.AgentSessions.Should().Contain(s => s.PersonaId == "sorrn");
    }

    [Fact]
    public async Task Expire_ReleasesAfterGraceWindow_WithTimeoutReason()
    {
        await handoffs.DeclareAsync(Declare());

        // Simulate the outgoing runner dying without confirming.
        var handoff = await handoffs.GetOpenAsync(jobId);
        handoff!.ReleaseDeadlineAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        await db.SaveChangesAsync();

        var released = await handoffs.ExpirePendingAsync();

        released.Should().Be(1);
        var settled = db.JobHandoffs.Single(h => h.JobId == jobId);
        settled.ReleaseReason.Should().Be(JobHandoffReleaseReason.GraceExpired);
        settled.Status.Should().Be(JobHandoffStatus.InFlight, "a stuck job is worse than a dirty handoff");
        db.AgentSessions.Should().Contain(s => s.PersonaId == "sorrn");
    }

    [Fact]
    public async Task Expire_LeavesFutureDeadlinesAlone()
    {
        await handoffs.DeclareAsync(Declare());

        (await handoffs.ExpirePendingAsync()).Should().Be(0);
        db.AgentSessions.Should().NotContain(s => s.PersonaId == "sorrn");
    }

    [Fact]
    public async Task Declare_AgainstSameTarget_IsIdempotent()
    {
        var first = await handoffs.DeclareAsync(Declare());
        var second = await handoffs.DeclareAsync(Declare());

        second.Accepted.Should().BeTrue();
        second.Handoff!.Id.Should().Be(first.Handoff!.Id, "a repeated mention must not open a second gate");
        db.JobHandoffs.Count(h => h.Status == JobHandoffStatus.Pending).Should().Be(1);
    }

    [Fact]
    public async Task Declare_ToDifferentTarget_SupersedesPendingHandoff()
    {
        db.Personas.Add(new Persona { Id = "astra", Name = "Astra", Role = "Lead", DriverId = "runner-astra" });
        await db.SaveChangesAsync();

        await handoffs.DeclareAsync(Declare("sorrn"));
        var retargeted = await handoffs.DeclareAsync(Declare("astra"));

        retargeted.Accepted.Should().BeTrue();
        db.JobHandoffs.Single(h => h.ToPersonaId == "sorrn").Status.Should().Be(JobHandoffStatus.Cancelled);
        retargeted.Handoff!.Status.Should().Be(JobHandoffStatus.Pending);
    }

    [Fact]
    public async Task Declare_ToSelf_IsRefused()
    {
        var decision = await handoffs.DeclareAsync(new DeclareHandoffRequest(jobId, "rook", "rook", null));

        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Contain("already holding");
    }

    [Fact]
    public async Task Declare_ForUnknownTargetPersona_IsRefused()
    {
        var decision = await handoffs.DeclareAsync(Declare("nobody"));

        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Contain("not found");
    }

    [Fact]
    public async Task Release_HandsIncomingAgentTheDurableWorkspacePointer()
    {
        await handoffs.DeclareAsync(Declare());

        // Outgoing runner commits + pushes, then reports the snapshot.
        await workspaces.SaveAsync(new SaveWorkspaceRequest(
            jobId, "runner-rook", "rook", null,
            HeadCommit: "3f2a1c9", GitRemoteUrl: "http://git/app.git",
            GitRef: "mesh/job-abc", BundleS3Key: "jobs/abc/bundle.tgz", ReleaseLease: true));

        await handoffs.ConfirmAsync(jobId, agentId: "runner-rook");

        var incoming = db.AgentSessions.Single(s => s.PersonaId == "sorrn");
        incoming.MetadataJson.Should().Contain("3f2a1c9", "the incoming runner must know its expected baseline");
        incoming.MetadataJson.Should().Contain("jobs/abc/bundle.tgz");
        incoming.MetadataJson.Should().Contain("\"workspaceScope\":\"job\"");

        db.JobHandoffs.Single(h => h.JobId == jobId).ExpectedHeadCommit.Should().Be("3f2a1c9");
    }

    [Fact]
    public async Task Release_TakesOverLeaseForIncomingAgent()
    {
        await handoffs.DeclareAsync(Declare());
        await workspaces.AcquireLeaseAsync(jobId, "runner-rook", "rook");

        await handoffs.ConfirmAsync(jobId, agentId: "runner-rook");

        var state = db.JobWorkspaceStates.Single(s => s.JobId == jobId);
        state.LeaseHolderAgentId.Should().Be("runner-sorrn");
        state.LeaseHolderPersonaId.Should().Be("sorrn");
    }

    [Fact]
    public async Task Complete_MarksHandoffDoneAndReleasesLease()
    {
        await handoffs.DeclareAsync(Declare());
        var released = await handoffs.ConfirmAsync(jobId, agentId: "runner-rook");
        var incomingSession = released.Handoff!.ToTurnSessionId!.Value;

        await handoffs.CompleteAsync(incomingSession);

        var settled = db.JobHandoffs.Single(h => h.JobId == jobId);
        settled.Status.Should().Be(JobHandoffStatus.Completed);
        db.JobWorkspaceStates.Single(s => s.JobId == jobId).LeaseHolderAgentId.Should().BeNull();
        (await handoffs.GetOpenAsync(jobId)).Should().BeNull();
    }

    [Fact]
    public async Task Confirm_AfterRelease_IsIdempotentAndDoesNotDoubleDispatch()
    {
        await handoffs.DeclareAsync(Declare());
        await handoffs.ConfirmAsync(jobId, agentId: "runner-rook");

        var again = await handoffs.ConfirmAsync(jobId, agentId: "runner-rook");

        again.Accepted.Should().BeTrue();
        again.Reason.Should().Contain("already released");
        db.AgentSessions.Count(s => s.PersonaId == "sorrn").Should().Be(1);
    }

    [Fact]
    public async Task Declare_OnArchivedJob_IsRefused()
    {
        var job = db.ProjectJobs.Single(j => j.Id == jobId);
        job.Status = ProjectJobStatus.Archived;
        await db.SaveChangesAsync();

        var decision = await handoffs.DeclareAsync(Declare());

        decision.Accepted.Should().BeFalse();
        decision.Reason.Should().Contain("archived");
    }

    [Fact]
    public async Task Cancel_DropsTheGateWithoutDispatching()
    {
        await handoffs.DeclareAsync(Declare());
        await handoffs.CancelAsync(jobId, "operator cancelled");

        (await handoffs.GetOpenAsync(jobId)).Should().BeNull();
        db.AgentSessions.Should().NotContain(s => s.PersonaId == "sorrn");
    }

    public void Dispose() => db.Dispose();
}
