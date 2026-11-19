using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Temporalio.Client;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The push half of the attention feature: one job changed, every open Cockpit is told about it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is actually being protected here.</strong> The rollup behind <c>GET /api/attention</c>
/// already answers "what needs me"; these tests are about the two ways a push can be wrong in ways a
/// poll cannot be. Too loud: an event for a job that is quietly working, which trains the operator to
/// ignore the bell — so half of these tests assert that a near-miss produces a resolution or nothing at
/// all. Silent: a wait that never arrives because the notifier swallowed an exception, or because a
/// write site forgot to call it — which is why <see cref="AttentionBroadcastWriteSiteTests"/> drives the
/// real write paths instead of trusting the call sites to be visible in a diff.
/// </para>
/// <para>
/// The hub is mocked at the <see cref="IClientProxy.SendCoreAsync"/> seam rather than at
/// <c>SendAsync</c>, because <c>SendAsync</c> is an extension method and Moq cannot intercept it;
/// everything the hub does with an event name and a payload funnels through that one method, which is
/// exactly the seam worth pinning.
/// </para>
/// </remarks>
public class AttentionBroadcastTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly List<(string Name, object?[] Args)> _sent = [];
    private readonly Mock<IHubContext<AgentMeshHub>> _hub = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();

    public AttentionBroadcastTests()
    {
        _clients.Setup(c => c.All).Returns(_clientProxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        _hub.Setup(h => h.Clients).Returns(_clients.Object);

        _clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((name, args, _) => _sent.Add((name, args)))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task WaitingJob_BroadcastsNeeded_CarryingTheRollupsOwnItem()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = SeedWaitingJob(db, "Awaiting plan approval");
        await db.SaveChangesAsync();

        await Notifier(db).RefreshAttentionAsync(jobId);

        var (name, args) = _sent.Should().ContainSingle().Subject;
        name.Should().Be(AttentionEvents.Needed);
        var item = args.Should().ContainSingle().Which.Should().BeOfType<AttentionItemDto>().Subject;

        // The event carries the same object the endpoint would return for this job, id included: that
        // id is the client's dedupe key across poll and push.
        item.Id.Should().Be($"{jobId}:planApproval");
        item.Type.Should().Be(AttentionTypes.PlanApproval);
        item.JobId.Should().Be(jobId.ToString());
        item.ProjectName.Should().Be("Agent Mesh");
    }

    [Fact]
    public async Task JobThatStoppedWaiting_BroadcastsResolved()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = SeedWaitingJob(db, "Executing task #2: backend broadcasts");
        await db.SaveChangesAsync();

        await Notifier(db).RefreshAttentionAsync(jobId);

        var (name, args) = _sent.Should().ContainSingle().Subject;
        name.Should().Be(AttentionEvents.Resolved);
        var payload = args.Should().ContainSingle().Which.Should().BeOfType<AttentionResolvedDto>().Subject;
        payload.JobId.Should().Be(jobId.ToString());
        payload.ResolvedAt.Should().BeAfter(Base);
    }

    [Fact]
    public async Task ArchivedJobWithAPendingPlanCard_BroadcastsResolved()
    {
        // Resolution is derived, never written: there is no code path anywhere that records "this wait
        // ended". Finishing the job takes it out of OpenJobsPredicate, so the same call that returned an
        // item now returns null, and the client clears by job id.
        using var db = TestDbContextFactory.Create();
        var jobId = SeedWaitingJob(db, "Awaiting plan approval", ProjectJobStatus.Archived);
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            JobId = jobId.ToString(),
            CardType = TimelineCardType.PlanApproval,
            Status = TimelineCardStatus.PendingInput,
            Title = "Plan Approval: Proposed Implementation Checklist",
            StructuredPayloadJson = "{}",
            CreatedAt = Base,
            UpdatedAt = Base
        });
        await db.SaveChangesAsync();

        await Notifier(db).RefreshAttentionAsync(jobId);

        _sent.Should().ContainSingle().Which.Name.Should().Be(AttentionEvents.Resolved);
    }

    [Fact]
    public async Task UnansweredAsk_BroadcastsNeededAsAnEscalation()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = SeedWaitingJob(db, heartbeat: null);
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            JobId = jobId.ToString(),
            CardType = TimelineCardType.Escalation3R,
            Status = TimelineCardStatus.PendingInput,
            Title = "Escalation from @rook to @operator",
            StructuredPayloadJson =
                """{"content":"Result: blocked on a decision. Reasoning: two shapes. Risk: none.","requiresReply":true,"targetPersona":"operator"}""",
            CreatedAt = Base.AddMinutes(4),
            UpdatedAt = Base.AddMinutes(4)
        });
        await db.SaveChangesAsync();

        await Notifier(db).RefreshAttentionAsync(jobId);

        var item = _sent.Should().ContainSingle().Which.Args[0].Should().BeOfType<AttentionItemDto>().Subject;
        item.Type.Should().Be(AttentionTypes.Escalation);
        item.Id.Should().Be($"{jobId}:escalation");
    }

    [Fact]
    public async Task UnparsableJobId_SendsNothingAtAll()
    {
        using var db = TestDbContextFactory.Create();
        await Notifier(db).RefreshAsync("stage-1-discovery");
        await Notifier(db).RefreshAsync((string?)null);
        await Notifier(db).RefreshAsync("   ");

        // Not "Resolved for an empty id": announcing a resolution for a job that never existed would let
        // a malformed route value clear a bell entry the operator still needs.
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownJobGuid_SendsOneResolutionAndNoItem()
    {
        using var db = TestDbContextFactory.Create();
        await db.SaveChangesAsync();

        await Notifier(db).RefreshAttentionAsync(Guid.NewGuid());

        // Nothing is waiting on a job that is not here, and the client's clear-by-job-id is a no-op, so
        // a single resolution is both correct and cheap.
        _sent.Should().ContainSingle().Which.Name.Should().Be(AttentionEvents.Resolved);
    }

    [Fact]
    public async Task OnlyTheRefreshedJobIsAnnounced_EvenWhenAnotherProjectIsAlsoBlocked()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project { Id = "beta", Name = "Other Project" });
        var announced = SeedWaitingJob(db, "Awaiting plan approval");
        var quiet = SeedWaitingJob(db, "Awaiting pull request review (PR #12)", projectId: "beta");
        await db.SaveChangesAsync();

        await Notifier(db).RefreshAttentionAsync(announced);

        var item = _sent.Should().ContainSingle().Which.Args[0].Should().BeOfType<AttentionItemDto>().Subject;
        item.JobId.Should().Be(announced.ToString());
        item.JobId.Should().NotBe(quiet.ToString());
    }

    [Fact]
    public async Task HubFailure_IsSwallowed_BecauseAWriteAlreadyCommitted()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = SeedWaitingJob(db, "Awaiting plan approval");
        await db.SaveChangesAsync();

        _clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the hub is gone"));

        var act = () => Notifier(db).RefreshAttentionAsync(jobId);

        // The write is durable by the time this runs. Losing a banner is not worth a 500 on a POST that
        // succeeded, and the client converges on its next poll of GET /api/attention.
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RollupReadFailure_IsSwallowed()
    {
        using var db = TestDbContextFactory.CreateUnreachable();

        var act = () => Notifier(db).RefreshAttentionAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task RepeatedRefresh_EmitsTheSameItemTwice_AndTheIdMakesThatHarmless()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = SeedWaitingJob(db, "Awaiting plan approval");
        await db.SaveChangesAsync();

        var notifier = Notifier(db);
        await notifier.RefreshAttentionAsync(jobId);
        await notifier.RefreshAttentionAsync(jobId);

        // Deliberate: several write sites fire for one logical transition (a plan approval rewrites the
        // heartbeat, flips the card and posts a chat row), and the server keeps no record of what it
        // already announced. Deduplicating on the server would need per-process state that a restart or
        // a second replica turns into a lost notification; the client already dedupes on this id.
        _sent.Should().HaveCount(2);
        _sent.Select(s => s.Name).Should().AllBeEquivalentTo(AttentionEvents.Needed);
        var first = (AttentionItemDto)_sent[0].Args[0]!;
        var second = (AttentionItemDto)_sent[1].Args[0]!;
        second.Id.Should().Be(first.Id);
        second.Since.Should().Be(first.Since);
    }

    [Fact]
    public async Task EventNamesAreTheFrozenWireContract()
    {
        using var db = TestDbContextFactory.Create();
        var waiting = SeedWaitingJob(db, "Awaiting plan approval");
        var working = SeedWaitingJob(db, "Executing task #1");
        await db.SaveChangesAsync();

        await Notifier(db).RefreshAttentionAsync(waiting);
        await Notifier(db).RefreshAttentionAsync(working);

        // The Cockpit subscribes by these literals and they are compiled into already-open browser tabs,
        // so the strings are asserted here rather than only referenced through the constants.
        AttentionEvents.Needed.Should().Be("AttentionNeeded");
        AttentionEvents.Resolved.Should().Be("AttentionResolved");
        _sent.Select(s => s.Name).Should().ContainInOrder("AttentionNeeded", "AttentionResolved");
    }

    private AttentionNotifier Notifier(AgentMeshDbContext db) => new(
        new AttentionQueryService(db, NullLogger<AttentionQueryService>.Instance),
        _hub.Object,
        NullLogger<AttentionNotifier>.Instance);

    private Guid SeedWaitingJob(
        AgentMeshDbContext db,
        string? heartbeat,
        ProjectJobStatus status = ProjectJobStatus.Active,
        string projectId = "alpha")
    {
        if (!db.Projects.Local.Any(p => p.Id == projectId))
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = projectId == "alpha" ? "Agent Mesh" : projectId
            });
        }

        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = projectId,
            Title = "Ship the bell",
            Description = "Ship the bell",
            Status = status,
            ProgressHeartbeat = heartbeat,
            TasksJson = "[]",
            GitBranchName = "mesh/job-eeb562f5",
            CreatedAt = Base,
            UpdatedAt = Base.AddMinutes(1)
        });

        return jobId;
    }
}

/// <summary>
/// The wire shape of the two events, pinned independently of the classifier.
/// </summary>
/// <remarks>
/// The Cockpit compiles one TypeScript interface and uses it for both the poll and the push. If the
/// event payload ever serialises differently from <c>GET /api/attention</c> — PascalCase, a record
/// renamed mid-flight, a <c>PrUrl</c> that falls out of the contract — the bell reads those fields as
/// undefined and the failure is silent in exactly the way a notification feature punishes. SignalR's
/// JSON protocol uses the web defaults (camelCase, case-insensitive), which is what
/// <see cref="JsonSerializerDefaults.Web"/> models here.
/// </remarks>
public class AttentionBroadcastContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void NeededPayload_IsCamelCaseAndCarriesEveryEndpointField()
    {
        var item = new AttentionItemDto(
            Id: "job-1:prReview",
            Type: AttentionTypes.PrReview,
            Title: "Pull request awaiting review",
            Detail: "Awaiting pull request review (PR #75)",
            JobId: "job-1",
            JobTitle: "Notifications",
            ProjectId: "alpha",
            ProjectName: "Agent Mesh",
            CardId: null,
            PrUrl: "https://github.com/ckchessmaster/agent-mesh/pull/75",
            PrNumber: 75,
            Since: new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(item, WebDefaults);

        json.Should().Contain("\"id\":\"job-1:prReview\"");
        json.Should().Contain("\"type\":\"prReview\"");
        json.Should().Contain("\"jobId\"");
        json.Should().Contain("\"jobTitle\"");
        json.Should().Contain("\"projectId\"");
        json.Should().Contain("\"projectName\"");
        json.Should().Contain("\"cardId\"");
        json.Should().Contain("\"prUrl\"");
        json.Should().Contain("\"prNumber\":75");
        json.Should().Contain("\"since\"");
        json.Should().NotContain("\"Id\":");
    }

    [Fact]
    public void ResolvedPayload_IsCamelCaseAndNamesTheJobToClear()
    {
        var resolved = new AttentionResolvedDto(
            "job-1", null, null, new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(resolved, WebDefaults);

        json.Should().Contain("\"jobId\":\"job-1\"");
        json.Should().Contain("\"resolvedAt\"");
        // The client clears every seen-set key prefixed "{jobId}:", so a null item id is not a gap in
        // the contract; it is the reserved slot for a later, more precise close.
        json.Should().Contain("\"itemId\":null");
    }
}

/// <summary>
/// Records what the write sites asked the server to announce.
/// </summary>
internal sealed class RecordingAttentionNotifier : IAttentionNotifier
{
    public List<string?> Refreshed { get; } = [];

    public Task RefreshAsync(string? jobId, CancellationToken cancellationToken = default)
    {
        Refreshed.Add(jobId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The write sites themselves.
/// </summary>
/// <remarks>
/// <para>
/// Each test drives a real controller or tool action with a recording notifier and asserts the job it
/// named. A call site that is deleted in a refactor is invisible to every other test in this suite: the
/// rollup still answers correctly, the endpoint still returns the item, and only the desktop banner is
/// missing — which no assertion below the transport layer can see. These are the pins that keep the
/// three source families (heartbeat, timeline cards, ledger) wired to the push.
/// </para>
/// <para>
/// One representative per family per process, plus the two transitions that end a wait: the worker's
/// card callback (which is how every gate the Temporal orchestration raises reaches a browser, since
/// the worker has no hub of its own) and job completion.
/// </para>
/// </remarks>
public class AttentionBroadcastWriteSiteTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task JobTimeline_WorkerCardCallback_AnnouncesTheJobsCardWasWrittenFor()
    {
        // The worker writes the DiscoveryQa/PlanApproval/TurnInterrupted card and the matching heartbeat
        // in one activity, then POSTs here. This single hook is therefore the whole orchestrated path.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProjectAndJob(db, jobId, "Awaiting plan approval");
        var cardId = Guid.NewGuid();
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            Id = cardId,
            JobId = jobId.ToString(),
            CardType = TimelineCardType.PlanApproval,
            Status = TimelineCardStatus.PendingInput,
            Title = "Plan Approval: Proposed Implementation Checklist",
            StructuredPayloadJson = """{"tasks":[{"title":"Backend: broadcasts"}]}""",
            CreatedAt = Base,
            UpdatedAt = Base
        });
        await db.SaveChangesAsync();

        var attention = new RecordingAttentionNotifier();
        var controller = new JobTimelineController(
            db, new Mock<ITemporalClient>().Object, attention: attention);

        var result = await controller.NotifyCardUpdated(jobId.ToString(), cardId);

        result.Should().BeOfType<OkObjectResult>();
        attention.Refreshed.Should().Equal(jobId.ToString());
    }

    [Fact]
    public async Task JobTimeline_EscalationCardComment_AnnouncesTheClose()
    {
        // The escalation close is a card status flip now: a comment on a PendingInput Escalation3R row is
        // the only thing that retires the wait, because the job ledger that used to record the reply is
        // gone. If this route stops announcing, an answered question keeps ringing until the bell polls.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProjectAndJob(db, jobId, "Executing task #2");
        var cardId = Guid.NewGuid();
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            Id = cardId,
            JobId = jobId.ToString(),
            CardType = TimelineCardType.Escalation3R,
            Status = TimelineCardStatus.PendingInput,
            Title = "Escalation from @rook to @operator",
            StructuredPayloadJson =
                """{"content":"Which branch should I target?","requiresReply":true,"targetPersona":"operator"}""",
            CreatedAt = Base,
            UpdatedAt = Base
        });
        await db.SaveChangesAsync();

        // Prove the wait existed before the comment, so "gone afterwards" means something.
        (await QueryService(db).QueryJobAsync(jobId)).Should().NotBeNull();

        var attention = new RecordingAttentionNotifier();
        var controller = new JobTimelineController(
            db, new Mock<ITemporalClient>().Object, attention: attention);

        var result = await controller.AddComment(
            jobId.ToString(), cardId, new AddCommentRequest("operator", "operator", "origin/main, obviously"));

        result.Should().BeOfType<OkObjectResult>();
        attention.Refreshed.Should().Equal(jobId.ToString());

        var reloaded = await db.JobTimelineCards.SingleAsync(c => c.Id == cardId);
        reloaded.Status.Should().Be(TimelineCardStatus.Resolved,
            "the flip is the closure signal the rollup reads; if a comment stops resolving the card, the bell never clears");
        (await QueryService(db).QueryJobAsync(jobId)).Should().BeNull();
    }

    [Fact]
    public async Task JobTimeline_PlanDecisionRoute_AnnouncesAfterItsOwnWrites()
    {
        // Both sides of the gate change the wait — approval clears it, rejection keeps it with a new
        // heartbeat — and only the recomputed state knows which, so the announcement has to come after
        // the card and heartbeat writes, not before the Temporal signal.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProjectAndJob(db, jobId, "Awaiting plan approval");
        var cardId = Guid.NewGuid();
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            Id = cardId,
            JobId = jobId.ToString(),
            CardType = TimelineCardType.PlanApproval,
            Status = TimelineCardStatus.PendingInput,
            Title = "Plan Approval: Proposed Implementation Checklist",
            StructuredPayloadJson = """{"tasks":[{"title":"Backend: broadcasts"}]}""",
            CreatedAt = Base,
            UpdatedAt = Base
        });
        await db.SaveChangesAsync();

        var attention = new RecordingAttentionNotifier();
        // The route signals the workflow before it records the verdict, so a handle that refuses the
        // signal returns Conflict and announces nothing — the mock has to accept it for this to be a
        // test of the announcement rather than of Temporal.
        var temporal = new Mock<ITemporalClient>();
        var workflowId = $"job-{jobId}";
        var handle = new Mock<WorkflowHandle<IJobExecutionWorkflow>>(
            temporal.Object, workflowId, (string?)null, (string?)null, (string?)null);
        temporal.Setup(c => c.GetWorkflowHandle<IJobExecutionWorkflow>(workflowId, null))
            .Returns(handle.Object);

        var controller = new JobTimelineController(
            db, temporal.Object, attention: attention);

        var result = await controller.SubmitPlanDecision(
            jobId.ToString(), cardId, new PlanDecisionPayload(Approved: true), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        attention.Refreshed.Should().Equal(jobId.ToString());
    }

    [Fact]
    public async Task ProjectJobs_CompleteJob_AnnouncesSoTheWaitLeavesTheInbox()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProjectAndJob(db, jobId, "Awaiting plan approval");
        await db.SaveChangesAsync();

        var attention = new RecordingAttentionNotifier();
        var controller = new ProjectJobsController(db, attention: attention);

        var result = await controller.CompleteJob(jobId, new CompleteJobRequest("Done"));

        result.Should().BeOfType<OkObjectResult>();
        attention.Refreshed.Should().Equal(jobId.ToString());
    }

    [Fact]
    public async Task PlatformMcpTools_EscalationWithRequiresReply_AnnouncesTheAsk()
    {
        // The MCP surface is how an agent in a live turn parks, and on the card-less discovery path the
        // ledger row plus the heartbeat are the only record that it did.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProjectAndJob(db, jobId, "Executing task #2");
        await db.SaveChangesAsync();

        var attention = new RecordingAttentionNotifier();
        var hub = new Mock<IHubContext<AgentMeshHub>>();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.All).Returns(proxy.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        hub.Setup(h => h.Clients).Returns(clients.Object);
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var resolver = new Mock<IPlatformCallContextResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformCallContext(
                AgentId: "rook",
                AgentIdSource: "test",
                PersonaId: "rook",
                PersonaIdSource: "test",
                SessionId: Guid.NewGuid(),
                SessionIdSource: "test",
                JobId: jobId,
                JobIdSource: "test",
                ProjectId: "alpha",
                GitBranch: "mesh/job-eeb562f5",
                GitRemoteUrl: null,
                GitBaseBranch: null,
                WorkspaceDirectory: "/workspace"));

        var tools = new PlatformMcpTools(
            db,
            resolver.Object,
            new Mock<IGitHubPullRequestService>().Object,
            hub.Object,
            NullLogger<PlatformMcpTools>.Instance,
            null,
            null,
            attention);

        var raw = await tools.PostJobMessageAsync(
            new PostJobMessageArgs("Result: blocked. Reasoning: needs a call. Risk: none.", "operator", true),
            jobId.ToString());

        raw.Should().NotContain("\"error\"");
        attention.Refreshed.Should().Equal(jobId.ToString());
    }

    /// <summary>
    /// The read model behind the push, so a write-site test can assert what <c>GET /api/attention</c>
    /// would answer before and after the route runs. "The event fired" only means something if the state
    /// actually changed: the notifier recomputes rather than classifies the write, by design.
    /// </summary>
    private static AttentionQueryService QueryService(AgentMeshDbContext db)
        => new(db, NullLogger<AttentionQueryService>.Instance);

    private static void SeedProjectAndJob(
        AgentMeshDbContext db, Guid jobId, string? heartbeat, ProjectJobStatus status = ProjectJobStatus.Active)
    {
        if (!db.Projects.Local.Any(p => p.Id == "alpha"))
        {
            db.Projects.Add(new Project { Id = "alpha", Name = "Agent Mesh" });
        }

        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "alpha",
            Title = "Ship the bell",
            Description = "Ship the bell",
            Status = status,
            ProgressHeartbeat = heartbeat,
            TasksJson = "[]",
            GitBranchName = "mesh/job-eeb562f5",
            CreatedAt = Base,
            UpdatedAt = Base
        });
    }

    private static ControllerContext OperatorContext()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "operator"),
            new Claim("agent_id", "operator")
        ], "TestAuth"));

        return new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
    }
}
