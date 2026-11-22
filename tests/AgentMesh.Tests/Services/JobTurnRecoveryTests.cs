using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// Operator-facing recovery for a job whose turn stopped advancing, with no Temporal console involved.
/// </summary>
/// <remarks>
/// <para>
/// The requirement that produced this class: "I need a way to recover without having to go into temporal
/// and reset things." The job that motivated it had five commits on a branch, a checklist that said
/// exactly how far it had got, and a workflow that had already completed with <c>Success: false</c>.
/// The only repair available was to open the Temporal UI and hand-reset history.
/// </remarks>
public class JobTurnRecoveryTests
{
    private readonly AgentMeshDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<IHubContext<AgentMeshHub>> _hub = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly FakeGateway _gateway = new();

    public JobTurnRecoveryTests()
    {
        _hub.Setup(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.All).Returns(_proxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);
    }

    /// <param name="withGateway">
    /// False models a deployment with no orchestrator registered, which is a different thing from a
    /// gateway that fails: the recovery must still settle and report, not throw.
    /// </param>
    private JobTurnRecovery Create(bool withGateway = true) =>
        new(_db, _hub.Object, withGateway ? _gateway : null, NullLogger<JobTurnRecovery>.Instance);

    // ── fixtures ─────────────────────────────────────────────────────────────

    private ProjectJob AddJob(
        ProjectJobStatus status = ProjectJobStatus.Active,
        string? tasksJson = null,
        string? heartbeat = "Executing task 5/5: open the pull request")
    {
        var job = new ProjectJob
        {
            ProjectId = "proj-1",
            Title = "Standardized project setup",
            Description = "Give every service the same bootstrap",
            Status = status,
            GitBranchName = "mesh/job-recovered",
            WorkingDirectory = "/workspace/agent-mesh",
            AssignedAgentId = "astra",
            TasksJson = tasksJson ?? Checklist(("add policy", true), ("add route", true), ("open pr", false)),
            ProgressHeartbeat = heartbeat
        };
        _db.ProjectJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private AgentSession AddTurn(Guid? jobId, TimeSpan silence, string status = "thinking")
    {
        var session = new AgentSession
        {
            Title = "Standardized project setup",
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow - silence,
            MetadataJson = jobId is null ? "{}" : $$"""{"jobId":"{{jobId}}"}"""
        };
        _db.AgentSessions.Add(session);
        _db.SaveChanges();
        return session;
    }

    private JobWorkspaceState AddCheckpoint(
        ProjectJob job,
        Guid? turnSessionId,
        string status = "in_progress",
        string taskId = "task-open-pr")
    {
        var state = new JobWorkspaceState
        {
            JobId = job.Id,
            ProjectId = job.ProjectId,
            TaskId = taskId,
            CheckpointStatus = status,
            CheckpointSummary = "Editing the controller",
            CheckpointTurnSessionId = turnSessionId
        };
        _db.JobWorkspaceStates.Add(state);
        _db.SaveChanges();
        return state;
    }

    private JobTimelineCard AddCard(
        ProjectJob job,
        TimelineCardType type,
        string payload,
        DateTimeOffset? at = null,
        string taskId = "task-open-pr")
    {
        var card = new JobTimelineCard
        {
            JobId = job.Id.ToString(),
            CardType = type,
            TaskId = taskId,
            Title = type.ToString(),
            StructuredPayloadJson = payload,
            CreatedAt = at ?? DateTimeOffset.UtcNow
        };
        _db.JobTimelineCards.Add(card);
        _db.SaveChanges();
        return card;
    }

    /// <summary>The serializer the controller writes <c>tasks_json</c> with, so fixtures round-trip.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string Checklist(params (string Title, bool Done)[] tasks) =>
        "[" + string.Join(",", tasks.Select(t =>
            $$"""{"id":"{{Guid.NewGuid()}}","title":"{{t.Title}}","isCompleted":{{(t.Done ? "true" : "false")}}}""")) + "]";

    // ── refusal paths ────────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverAsync_ThrowsNotFound_ForAnUnknownJob()
    {
        var act = async () => await Create().RecoverAsync(Guid.NewGuid(), new JobRecoveryRequest(), "ckingdon");

        await act.Should().ThrowAsync<JobRecoveryNotFoundException>();
    }

    [Fact]
    public async Task RecoverAsync_RefusesAnArchivedJob_AndTouchesNothing()
    {
        var job = AddJob(ProjectJobStatus.Archived);
        var session = AddTurn(job.Id, TimeSpan.FromMinutes(300));

        var act = async () => await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // Starting a run for an archived job would resurrect it. The operator has to say so explicitly
        // by unarchiving, which is a deliberate act rather than a side effect of pressing Recover.
        await act.Should().ThrowAsync<JobRecoveryBlockedException>().WithMessage("*archived*");
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id))
            .Status.Should().Be("thinking");
        _gateway.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoverAsync_RequiresARequest()
    {
        var job = AddJob();

        var act = async () => await Create().RecoverAsync(job.Id, null!, "ckingdon");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── settle-only mode ─────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverAsync_WithResumeDeclined_SettlesAndStartsNothing()
    {
        var job = AddJob();
        var session = AddTurn(job.Id, TimeSpan.FromMinutes(200));

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(Resume: false), "ckingdon");

        // An operator who intends to drive the persona by hand needs the lie gone without a workflow
        // starting underneath them. This is the "make it honest, do not move it" mode.
        result.Action.Should().Be(JobRecoveryAction.SettledOnly);
        result.SettledSessions.Should().Equal(session.Id);
        _gateway.Starts.Should().BeEmpty();
        _gateway.Aborts.Should().BeEmpty();
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id))
            .Status.Should().Be(TurnStallPolicy.SettledStatus);
    }

    [Fact]
    public async Task RecoverAsync_SaysSo_WhenThereWasNothingToSettleAndResumeWasDeclined()
    {
        var job = AddJob();

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(Resume: false), "ckingdon");

        result.Action.Should().Be(JobRecoveryAction.SettledOnly);
        result.SettledSessions.Should().BeEmpty();
        result.Message.Should().Contain("Nothing was abandoned");
    }

    [Fact]
    public async Task RecoverAsync_ReportsOrchestratorUnavailable_ButStillSettles()
    {
        var job = AddJob();
        var session = AddTurn(job.Id, TimeSpan.FromMinutes(200));

        // A deployment without Temporal must not fail the call: erasing the phantom turn is a real
        // improvement, and telling the operator "500" would leave them with both the lie and no info.
        var result = await Create(withGateway: false).RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        result.Action.Should().Be(JobRecoveryAction.OrchestratorUnavailable);
        result.SettledSessions.Should().HaveCount(1);
        result.Message.Should().Contain("no workflow orchestrator");
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id))
            .Status.Should().Be(TurnStallPolicy.SettledStatus);
    }

    // ── live run: abort, never double-start ──────────────────────────────────

    [Fact]
    public async Task RecoverAsync_AbortsTheLiveTurn_WhenARunIsStillOpen()
    {
        var job = AddJob();
        var session = AddTurn(job.Id, TimeSpan.FromMinutes(200));
        _gateway.Run = new JobRunState($"job-{job.Id}", IsOpen: true);

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        result.Action.Should().Be(JobRecoveryAction.AbortedLiveTurn);
        result.WorkflowId.Should().Be($"job-{job.Id}");
        // Two orchestrators on one workspace lease is how a job ends up with two branches.
        _gateway.Starts.Should().BeEmpty();
        _gateway.Aborts.Should().ContainSingle();
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id))
            .Status.Should().Be(TurnStallPolicy.SettledStatus);
    }

    [Fact]
    public async Task RecoverAsync_AttributesTheAbortToTheCaller_WithTheirNote()
    {
        var job = AddJob();
        _gateway.Run = new JobRunState($"job-{job.Id}", IsOpen: true);

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(Note: "the agy process is wedged"), "ckingdon");

        var signal = _gateway.Aborts.Single().Signal;
        signal.Reason.Should().Contain("Operator recovery");
        signal.Reason.Should().Contain("agy process is wedged");
        signal.Reason.Should().Contain("requested by ckingdon");
        signal.RequestedBy.Should().Be("ckingdon");
    }

    [Fact]
    public async Task RecoverAsync_LeavesTheStalledBadgeOn_WhenOnlyALiveRunWasAborted()
    {
        // Only a run that this recovery started may clear the badge. The abort path hands off to a
        // workflow that has not done anything yet, so claiming "Recovered" would hide a job that is
        // still parked behind the very activity the operator just asked it to let go of.
        var job = AddJob(heartbeat: "Stalled: the 110-minute ceiling expired");
        _gateway.Run = new JobRunState($"job-{job.Id}", IsOpen: true);

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        var reloaded = await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        TurnStallSettler.IsStalledHeartbeat(reloaded.ProgressHeartbeat).Should().BeTrue();
    }

    // ── closed run: resume from recorded progress ────────────────────────────

    [Fact]
    public async Task RecoverAsync_ResumesFromTheFirstIncompleteTask()
    {
        var job = AddJob(tasksJson: Checklist(("add policy", true), ("add route", true), ("open pr", false)));
        var session = AddTurn(job.Id, TimeSpan.FromMinutes(200));
        AddCheckpoint(job, session.Id);

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        result.Action.Should().Be(JobRecoveryAction.Resumed);
        result.ResumedAtNumber.Should().Be(3);
        result.TotalTasks.Should().Be(3);
        result.ResumedAtTask.Should().Be("open pr");

        var input = _gateway.Starts.Single().Input;
        input.Continuation!.CompletedTaskOffset.Should().Be(2);
        input.Continuation.PendingTasks.Select(t => t.Title).Should().Equal("open pr");
    }

    [Fact]
    public async Task RecoverAsync_ResumesIntoTheSameWorkspace_AndOnTheSameBranch()
    {
        var job = AddJob();

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // The persona's checkout is keyed off the job id. Pointing a resumed run somewhere else would
        // hydrate a second tree and lose the five commits the abandoned turn had already made.
        var input = _gateway.Starts.Single().Input;
        input.WorkspacePath.Should().Be("/workspace/agent-mesh");
        input.TargetBranch.Should().Be("mesh/job-recovered");
        input.JobId.Should().Be(job.Id.ToString());
        input.AssignedAgentId.Should().Be("astra");
        _gateway.Starts.Single().Memo.Should().Be("astra");
    }

    [Fact]
    public async Task RecoverAsync_CarriesTheJobsTurnBudget_IntoTheStartedRun()
    {
        // The recovery path is how a turn-budget halt actually gets rerun: the operator raises the
        // budget in Settings, presses Recover, and this is the call that decides what the fresh run
        // enforces. Dropping the field here would make the Settings control a lie on exactly the
        // jobs that need it.
        var job = AddJob();
        job.TurnBudget = 80;
        _db.SaveChanges();

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        _gateway.Starts.Single().Input.TurnBudget.Should().Be(80);
    }

    [Fact]
    public async Task RecoverAsync_StartsOnTheIdTemporalAlreadyKnows()
    {
        var job = AddJob();
        _gateway.Run = new JobRunState($"job-{job.Id:N}", IsOpen: false);

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // The gateway probes several historical id shapes. Resuming under a different spelling would
        // leave the old run's history orphaned while a second timeline accumulated work.
        _gateway.Starts.Single().WorkflowId.Should().Be($"job-{job.Id:N}");
    }

    [Fact]
    public async Task RecoverAsync_ClearsTheStalledBadge_AndSaysWhereItResumed()
    {
        var job = AddJob();

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        var reloaded = await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        reloaded.ProgressHeartbeat.Should().StartWith("Recovering:");
        reloaded.ProgressHeartbeat.Should().Contain("task 3/3");
        ProjectJobsController.DeriveLifecycleStatus(reloaded).Should().NotBe("Stalled",
            "otherwise the Recover button stays on screen and invites a second recovery on a live job");
    }

    [Fact]
    public async Task RecoverAsync_RewindsTheAbandonedTurns_WorkspaceClaim()
    {
        var job = AddJob();
        var dead = AddTurn(job.Id, TimeSpan.FromMinutes(200));
        var held = AddCheckpoint(job, dead.Id);
        var otherTask = AddCheckpoint(job, turnSessionId: null, status: "completed", taskId: "task-add-policy");

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // The next turn reads the checkpoint as context. "in_progress" describing a session that died
        // hours ago is the stale fact that makes an agent refuse to touch files it already committed.
        var reloaded = await _db.JobWorkspaceStates.AsNoTracking()
            .FirstAsync(w => w.JobId == job.Id && w.TaskId == "task-open-pr" && w.CheckpointTurnSessionId == dead.Id);
        reloaded.CheckpointStatus.Should().Be("todo");
        reloaded.CheckpointSummary.Should().StartWith(TurnStallSettler.StalledHeartbeatPrefix);

        // A checkpoint belonging to a different session, or already completed, is somebody else's fact.
        var untouched = await _db.JobWorkspaceStates.AsNoTracking()
            .FirstAsync(w => w.TaskId == "task-add-policy");
        untouched.CheckpointStatus.Should().Be("completed");
    }

    [Fact]
    public async Task RecoverAsync_CarriesTheOpenPullRequest_IntoTheResumedRun()
    {
        var job = AddJob(tasksJson: Checklist(("add policy", true), ("add route", true), ("open pr", true)));
        AddCard(job, TimelineCardType.JobInitialized, "{}", DateTimeOffset.UtcNow.AddHours(-6));
        var prCard = new JobTimelineCard
        {
            JobId = job.Id.ToString(),
            CardType = TimelineCardType.PrReview,
            Title = "PR #91",
            StructuredPayloadJson = """{"prUrl":"https://github.com/o/r/pull/91","prNumber":91,"headSha":"abc123","round":2}""",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        _db.JobTimelineCards.Add(prCard);
        await _db.SaveChangesAsync();

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // All tasks done and the turn that was going to open the PR is dead: the resumed run must go
        // back to the review loop for PR 91, not rediscover the repo or open a second pull request.
        var continuation = _gateway.Starts.Single().Input.Continuation!;
        continuation.CompletedTaskOffset.Should().Be(3);
        continuation.PendingTasks.Should().BeEmpty();
        continuation.ActivePrUrl.Should().Be("https://github.com/o/r/pull/91");
        continuation.ActivePrNumber.Should().Be(91);
        continuation.CandidateHeadSha.Should().Be("abc123");
        continuation.ReviewRound.Should().Be(2);
        continuation.PrCardId.Should().Be(prCard.Id);
        continuation.InitCardId.Should().NotBeNull();

        result.PrUrl.Should().Be("https://github.com/o/r/pull/91");
        result.Message.Should().Contain("pull-request stage");

        // Every checklist item is done, so the offset is one past the end. "Resuming at task 4/3" reads
        // like a bug in the tool the operator is leaning on, and this is the shape prod's first real
        // recovery actually had (five tasks done, the turn that was going to open the PR dead).
        result.Message.Should().NotContain("4/3");
        (await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id))
            .ProgressHeartbeat.Should().Be("Recovering: resuming at the pull-request stage.");
    }

    [Fact]
    public async Task RecoverAsync_RestartsFromDiscovery_WhenTheJobHasNoChecklist()
    {
        var job = AddJob(tasksJson: "[]");

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // A job that never had tasks has no recorded progress. Inventing an empty pending list would
        // send the run straight to the PR stage with nothing done, which is worse than re-planning.
        result.Action.Should().Be(JobRecoveryAction.RestartedFromDiscovery);
        _gateway.Starts.Single().Input.Continuation.Should().BeNull();
        result.Message.Should().Contain("discovery");
        (await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id))
            .ProgressHeartbeat.Should().Be("Recovering: resuming at discovery.");
    }

    [Fact]
    public async Task RecoverAsync_LeavesThePromptAsStartJobWould()
    {
        var job = AddJob();

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        _gateway.Starts.Single().Input.InitialPrompt.Should().Be($"{job.Title}\n\n{job.Description}");
    }

    // ── blast radius ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverAsync_DoesNotTouchAnotherJobs_Turn()
    {
        var job = AddJob();
        var other = AddJob();
        var otherTurn = AddTurn(other.Id, TimeSpan.FromMinutes(200));

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // Recovery is pressed in a hurry on one wedged job. An unscoped settle would turn one incident
        // into an fleet-wide one by killing every other persona's abandoned-looking-but-live session.
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == otherTurn.Id))
            .Status.Should().Be("thinking");
    }

    [Fact]
    public async Task RecoverAsync_DoesNotSettleATurnThatIsStillReporting()
    {
        var job = AddJob();
        var inFlight = AddTurn(job.Id, TimeSpan.FromSeconds(5));

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(QuietSeconds: 120), "ckingdon");

        result.SettledSessions.Should().BeEmpty();
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == inFlight.Id))
            .Status.Should().Be("thinking", "its next event is in flight; settling it would overwrite a live turn");
    }

    [Fact]
    public async Task RecoverAsync_FloorsTheQuietWindow_SoARequestCannotAskForARace()
    {
        var job = AddJob();
        var quietJustBeforeTheFloor = AddTurn(job.Id, TimeSpan.FromSeconds(10));
        var clearlyAbandoned = AddTurn(job.Id, TimeSpan.FromMinutes(20));

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(QuietSeconds: 0), "ckingdon");

        // Zero would mean "settle whatever is live right now", which is a self-inflicted version of the
        // bug this endpoint fixes: the guard against settling a mid-flight turn must not be removable by
        // the caller.
        result.SettledSessions.Should().Equal(clearlyAbandoned.Id);
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == quietJustBeforeTheFloor.Id))
            .Status.Should().Be("thinking");
    }

    [Fact]
    public async Task RecoverAsync_LeavesACompletedTurnOfThisJobAlone()
    {
        var job = AddJob();
        var responded = AddTurn(job.Id, TimeSpan.FromMinutes(200), status: "active");

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == responded.Id))
            .Status.Should().Be("active");
    }

    // ── audit trail ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverAsync_WritesATimelineCardNamingTheCallerAndTheOutcome()
    {
        var job = AddJob();
        var session = AddTurn(job.Id, TimeSpan.FromMinutes(200));

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(Note: "driver wedged"), "ckingdon");

        // Recovery rewrites durable state on a human's say-so. Six months later the card is what answers
        // "why did this job start a second run at 4am", without needing the API's request logs.
        var card = (await _db.JobTimelineCards.AsNoTracking()
            .Where(c => c.JobId == job.Id.ToString() && c.CardType == TimelineCardType.TurnInterrupted)
            .ToListAsync()).Single();

        card.Title.Should().Be("Recovered by ckingdon: resumed");
        card.Status.Should().Be(TimelineCardStatus.Resolved);
        card.StructuredPayloadJson.Should().Contain("\"recoveredBy\":\"ckingdon\"")
            .And.Contain("driver wedged")
            .And.Contain(session.Id.ToString());
    }

    [Fact]
    public async Task RecoverAsync_TellsTheCockpitAboutTheNewState()
    {
        var job = AddJob();
        AddTurn(job.Id, TimeSpan.FromMinutes(200));
        var seen = new List<string>();
        // SendAsync is a SignalR extension method and cannot be mocked; SendCoreAsync is the interface
        // member it delegates to, so that is where a recording double belongs.
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((m, _, _) => seen.Add(m))
            .Returns(Task.CompletedTask);

        await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // Without the heartbeat push the operator's screen keeps the amber badge until a refresh, which
        // is the same "correct in the database, wrong on the screen" failure the sweeper fixes.
        seen.Should().Contain("SessionUpdated");
        seen.Should().Contain("ProgressHeartbeatUpdated");
        seen.Should().Contain("TimelineCardAdded");
    }

    [Fact]
    public async Task RecoverAsync_ReportsTheStableShape_TheDashboardDeserialises()
    {
        var job = AddJob();
        _gateway.Run = new JobRunState($"job-{job.Id}", IsOpen: false);

        var result = await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        result.JobId.Should().Be(job.Id);
        result.WorkflowId.Should().Be($"job-{job.Id}");
        result.Action.Should().Be(JobRecoveryAction.Resumed);
        // The dashboard keys its button state off the returned job DTO, so recovery has to hand it back.
        result.SettledSessions.Should().NotBeNull();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    // ── orchestration failures ───────────────────────────────────────────────

    [Fact]
    public async Task RecoverAsync_SurfacesARefusedStart_RatherThanReportingSuccess()
    {
        var job = AddJob();
        _gateway.StartFailure = new JobRecoveryOrchestrationException("temporal down", new InvalidOperationException());

        var act = async () => await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        // The tempting shortcut is to catch-and-report "resumed", because the sessions really were
        // settled. But a job whose badge clears while nothing is running is a lie of exactly the kind
        // this endpoint was written to remove; the caller has to find out the run never started.
        await act.Should().ThrowAsync<JobRecoveryOrchestrationException>();
        _gateway.Starts.Should().BeEmpty();
        var reloaded = await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        reloaded.ProgressHeartbeat.Should().NotStartWith("Recovering:");
    }

    [Fact]
    public async Task RecoverAsync_PropagatesAnUnreachableOrchestrator()
    {
        var job = AddJob();
        _gateway.FindFailure = new JobRecoveryOrchestrationException("cluster unreachable", new TimeoutException());

        var act = async () => await Create().RecoverAsync(job.Id, new JobRecoveryRequest(), "ckingdon");

        await act.Should().ThrowAsync<JobRecoveryOrchestrationException>();
    }

    // ── BuildContinuation ────────────────────────────────────────────────────

    [Fact]
    public void BuildContinuation_ReturnsNull_WhenThereIsNoChecklist()
    {
        var job = new ProjectJob { TasksJson = "" };

        JobTurnRecovery.BuildContinuation(job, [], out var title, out var number, out var total).Should().BeNull();
        (title, number, total).Should().Be((null, 0, 0));
    }

    [Fact]
    public void BuildContinuation_ReturnsNull_ForUnparseableChecklistJson()
    {
        var job = new ProjectJob { TasksJson = "{ not json" };

        // ParseTasks swallows the error and yields nothing, which must mean "restart discovery" rather
        // than "resume at task 1 of 0" — the latter would send the run to the PR stage having done no work.
        JobTurnRecovery.BuildContinuation(job, [], out _, out _, out _).Should().BeNull();
    }

    [Fact]
    public void BuildContinuation_ResumesAtTheFirstIncompleteTask()
    {
        var job = new ProjectJob { TasksJson = Checklist(("one", true), ("two", false), ("three", false)) };

        var state = JobTurnRecovery.BuildContinuation(job, [], out var title, out var number, out var total);

        state!.CompletedTaskOffset.Should().Be(1);
        state.PendingTasks.Select(t => t.Title).Should().Equal("two", "three");
        (title, number, total).Should().Be(("two", 2, 3));
    }

    [Fact]
    public void BuildContinuation_TreatsAGapInTheChecklistAsUnfinishedWork()
    {
        var job = new ProjectJob { TasksJson = Checklist(("one", true), ("two", false), ("three", true)) };

        var state = JobTurnRecovery.BuildContinuation(job, [], out var title, out _, out _);

        // Resuming past an unchecked box would silently drop a task the persona never finished. The
        // offset is the first open item, not the count of completed ones.
        state!.CompletedTaskOffset.Should().Be(1);
        state.PendingTasks.Select(t => t.Title).Should().Equal("two", "three");
        title.Should().Be("two");
    }

    [Fact]
    public void BuildContinuation_HandlesAChecklistWithNothingDone()
    {
        var job = new ProjectJob { TasksJson = Checklist(("one", false), ("two", false)) };

        var state = JobTurnRecovery.BuildContinuation(job, [], out _, out var number, out var total);

        state!.CompletedTaskOffset.Should().Be(0);
        state.PendingTasks.Should().HaveCount(2);
        (number, total).Should().Be((1, 2));
    }

    [Fact]
    public void BuildContinuation_PreservesTheTaskIds_TheWorkflowTracksBy()
    {
        var job = new ProjectJob { TasksJson = Checklist(("one", true), ("two", false)) };

        var state = JobTurnRecovery.BuildContinuation(job, [], out _, out _, out _);

        // The workflow matches completions against these ids. Minting fresh ones on resume would make
        // every already-recorded completion unrecognisable, and the resumed run would redo finished work.
        var ids = System.Text.Json.JsonSerializer
            .Deserialize<List<JobTaskDto>>(job.TasksJson, ApiJson)!
            .Select(t => t.Id.ToString())
            .ToList();

        state!.PendingTasks.Select(t => t.TaskId).Should().Equal(ids[1..]);
    }

    [Fact]
    public void BuildContinuation_IgnoresUnrelatedCards()
    {
        var job = new ProjectJob { TasksJson = Checklist(("one", true)) };
        var unrelated = new JobTimelineCard { CardType = TimelineCardType.TaskStep, StructuredPayloadJson = """{"prUrl":"x"}""" };

        var state = JobTurnRecovery.BuildContinuation(job, [unrelated], out _, out _, out _);

        state!.PrCardId.Should().BeNull();
        state.ActivePrUrl.Should().BeNull();
        state.InitCardId.Should().BeNull();
    }

    [Fact]
    public void BuildContinuation_ToleratesADamagedPrCard()
    {
        var job = new ProjectJob { TasksJson = Checklist(("one", true)) };
        var broken = new JobTimelineCard { CardType = TimelineCardType.PrReview, StructuredPayloadJson = "{ nope" };
        var init = new JobTimelineCard { CardType = TimelineCardType.JobInitialized, StructuredPayloadJson = "{}" };

        // These cards are written by the workflow, by the PR tool, and by older builds. A card that will
        // not parse must degrade to "the resumed run will find out for itself", never fail a recovery
        // that is being attempted precisely because something already went wrong.
        var state = JobTurnRecovery.BuildContinuation(job, [init, broken], out _, out _, out _);

        // The card is still remembered (the resumed run can re-read it from the timeline) but none of the
        // fields are invented, because a guessed PR number would have the persona poll the wrong pull.
        state!.PrCardId.Should().Be(broken.Id);
        state.ActivePrUrl.Should().BeNull();
        state.ActivePrNumber.Should().BeNull();
        state.CandidateHeadSha.Should().BeNull();
        state.InitCardId.Should().Be(init.Id);
        state.ReviewRound.Should().Be(0);
    }

    [Fact]
    public void BuildContinuation_UsesTheMostRecentPrCard()
    {
        var job = new ProjectJob { TasksJson = Checklist(("one", true)) };
        var older = new JobTimelineCard
        {
            CardType = TimelineCardType.PrReview,
            StructuredPayloadJson = """{"prUrl":"https://x/pull/1","prNumber":1,"round":1}""",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-5)
        };
        var newer = new JobTimelineCard
        {
            CardType = TimelineCardType.PrReview,
            StructuredPayloadJson = """{"prUrl":"https://x/pull/2","prNumber":2,"round":3}""",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        var state = JobTurnRecovery.BuildContinuation(job, [older, newer], out _, out _, out _);

        // A review round two exists on top of round one. Resuming against the closed PR would ask the
        // persona to poll a merged pull request forever.
        state!.ActivePrUrl.Should().Be("https://x/pull/2");
        state.ReviewRound.Should().Be(3);
        state.PrCardId.Should().Be(newer.Id);
    }

    [Fact]
    public void BuildContinuation_KeepsTaskGuidance_ForTheResumedRun()
    {
        var tasks = new[]
        {
            new JobTaskDto
            {
                Title = "add route", IsCompleted = false,
                TargetFile = "src/Api/Jobs.cs", VerificationMethod = "curl /health"
            }
        };
        var job = new ProjectJob { TasksJson = System.Text.Json.JsonSerializer.Serialize(tasks) };

        var state = JobTurnRecovery.BuildContinuation(job, [], out _, out _, out _);

        // Without the target file and verification method, the resumed persona is told what to do but
        // not where to do it or how to know it worked, and it re-derives both by burning a turn.
        state!.PendingTasks.Single().TargetFile.Should().Be("src/Api/Jobs.cs");
        state.PendingTasks.Single().VerificationMethod.Should().Be("curl /health");
    }

    // ── fake gateway ─────────────────────────────────────────────────────────

    private sealed class FakeGateway : IJobExecutionWorkflowGateway
    {
        public JobRunState? Run { get; set; }
        public Exception? FindFailure { get; set; }
        public Exception? StartFailure { get; set; }
        public List<(string WorkflowId, AbortActiveTurnSignal Signal)> Aborts { get; } = [];
        public List<(JobExecutionInput Input, string WorkflowId, string? Memo)> Starts { get; } = [];

        public Task<JobRunState?> FindRunAsync(Guid jobId, CancellationToken ct = default)
        {
            if (FindFailure is not null) throw FindFailure;
            return Task.FromResult(Run);
        }

        public Task AbortTurnAsync(string workflowId, AbortActiveTurnSignal signal, CancellationToken ct = default)
        {
            Aborts.Add((workflowId, signal));
            return Task.CompletedTask;
        }

        public Task StartAsync(JobExecutionInput input, string workflowId, string? memoPersonaId, CancellationToken ct = default)
        {
            if (StartFailure is not null) throw StartFailure;
            Starts.Add((input, workflowId, memoPersonaId));
            return Task.CompletedTask;
        }
    }
}
