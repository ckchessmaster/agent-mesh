using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentMesh.Tests.Workflows;

/// <summary>
/// Which turn session a task is attached to, and whether that attachment survives a turn boundary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The invariant these tests protect.</strong> The pairing of a task to its turn session is the
/// only thing that makes Temporal's retry of a timed-out activity re-attach to the agent that is already
/// running instead of starting a second one on the same branch. Three separate code paths had inverted
/// the precedence for finding that pairing, and each inversion fails differently:
/// <list type="number">
/// <item><description>
/// <see cref="HttpAgentTurnRunner.ResolveExistingSessionAsync"/> returned the workflow-start value
/// unconditionally and unchecked, so a workflow started with a session id never dispatched turn 2 — the
/// assembled prompt and the human's <c>pendingReply</c> were both dropped — and then observed the already
/// closed session, re-derived the previous turn's outcome from its transcript, and burned all 40 turns
/// into <c>StuckLoopDetected</c> while doing no work.
/// </description></item>
/// <item><description>
/// <see cref="TurnContextAssembler.ResolveTurnSessionIdAsync"/> read the workflow-start value before the
/// checkpoint, pinning the sliding context window to turn 1's conversation for the life of the run —
/// contradicting its own documented order.
/// </description></item>
/// <item><description>
/// <see cref="ExecutionActivities"/> rebuilt the checkpoint from the <em>pre-turn</em> snapshot at the
/// post-turn boundary, which cleared the session id the runner had written mid-turn. This one is the
/// reason the other two were not merely theoretical: it meant the durable pairing was destroyed at the
/// end of every <em>successful</em> turn, so the re-attach guarantee held only for turns that failed.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Why the third is tested through the public activity.</strong> The clobber is a three-party
/// interaction — assembler snapshot, runner write, post-turn save — and asserting on the save alone would
/// pass with the snapshot still stale. Driving <c>RunAgentCodingTurnAsync</c> exercises the real sequence,
/// including the ordering that caused it.
/// </para>
/// <para>
/// All three were reproduced as failing tests against the pre-fix code before the fixes were written.
/// </para>
/// </remarks>
public class TurnSessionResolutionTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Named so the assertions can say which session is which.</summary>
    private static readonly Guid StartSession = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CheckpointSession = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid FreshSession = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly string _dbName = "TurnSession_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    // ──────────────────────────────────────────────────────────────────────────
    // 2a — the runner must not attach to a closed session, and must prefer the
    //      durable pairing over the value fixed at workflow start
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A workflow-start session id that has already reached a terminal status must not be re-attached.
    /// </summary>
    /// <remarks>
    /// The pre-fix code returned this id unconditionally. Attaching to a closed session does not throw:
    /// <c>ObserveAsync</c> settles immediately because the status is already terminal, and
    /// <c>ClassifySignals</c> then re-reads the <em>closed</em> transcript and re-derives the previous
    /// turn's outcome. The workflow sees <c>Continue</c>, loops, derives it again, and repeats until the
    /// 40-turn ceiling. That is the spin this pins shut.
    /// </remarks>
    [Fact]
    public async Task ResolveExistingSession_StartTimeSessionIsClosed_DoesNotAttach()
    {
        using var db = Db();
        await SeedSessionAsync(db, StartSession, status: "completed");

        var runner = NewRunner(db);
        var request = Request(turnSessionId: StartSession);

        var resolved = await runner.ResolveExistingSessionAsync(request, CancellationToken.None);

        resolved.Should().BeNull(
            "attaching to a closed session silently re-observes the previous turn's transcript; null " +
            "means the caller dispatches a genuinely new turn");
    }

    /// <summary>
    /// A live workflow-start session is still a valid attachment — the fix must not disable re-attach.
    /// </summary>
    /// <remarks>
    /// The mirror of the test above. Without it, "never return the request value" would also pass, and
    /// the result would be a second agent dispatched onto a branch that already has one running — the
    /// exact hazard the re-attach exists to prevent, reintroduced by over-correcting.
    /// </remarks>
    [Fact]
    public async Task ResolveExistingSession_StartTimeSessionIsLive_StillAttaches()
    {
        using var db = Db();
        await SeedSessionAsync(db, StartSession, status: "thinking");

        var runner = NewRunner(db);

        var resolved = await runner.ResolveExistingSessionAsync(Request(turnSessionId: StartSession), CancellationToken.None);

        resolved.Should().Be(StartSession);
    }

    /// <summary>
    /// The durable checkpoint pairing outranks the value fixed at workflow start.
    /// </summary>
    /// <remarks>
    /// The input cannot know about the session turn three created; the checkpoint is rewritten at every
    /// turn boundary. Preferring the input — which is what the code did, and what its comment asserted
    /// was correct — pins the task to turn 1's session for the whole run.
    /// </remarks>
    [Fact]
    public async Task ResolveExistingSession_PrefersDurableCheckpointOverWorkflowStart()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, StartSession, status: "thinking");
        await SeedSessionAsync(db, CheckpointSession, status: "busy");
        await SeedCheckpointAsync(db, CheckpointSession);

        var runner = NewRunner(db);

        var resolved = await runner.ResolveExistingSessionAsync(
            Request(turnSessionId: StartSession), CancellationToken.None);

        resolved.Should().Be(CheckpointSession,
            "the checkpoint is written mid-turn by the runner and is therefore fresher than a value " +
            "frozen when the workflow started");
    }

    /// <summary>
    /// When the fresher pairing points at a closed session, resolution falls through to a dispatch
    /// rather than to the older candidate.
    /// </summary>
    /// <remarks>
    /// Both candidates closed, both rejected: the answer is null. Falling back to the older live session
    /// would be wrong here — a checkpoint naming a closed session means the last turn finished, so the
    /// next turn is new work and needs a new session.
    /// </remarks>
    [Fact]
    public async Task ResolveExistingSession_WhenBothCandidatesAreClosed_ReturnsNull()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, StartSession, status: "completed");
        await SeedSessionAsync(db, CheckpointSession, status: "error");
        await SeedCheckpointAsync(db, CheckpointSession);

        var runner = NewRunner(db);

        var resolved = await runner.ResolveExistingSessionAsync(Request(turnSessionId: StartSession), CancellationToken.None);

        resolved.Should().BeNull();
    }

    /// <summary>
    /// When the checkpoint points at a closed session and the workflow-start candidate is live,
    /// resolution does NOT fall back to the older candidate; it returns null to trigger a fresh dispatch.
    /// </summary>
    [Fact]
    public async Task ResolveExistingSession_WhenCheckpointIsTerminalAndWorkflowStartIsLive_ReturnsNull()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, StartSession, status: "thinking");
        await SeedSessionAsync(db, CheckpointSession, status: "completed");
        await SeedCheckpointAsync(db, CheckpointSession);

        var runner = NewRunner(db);

        var resolved = await runner.ResolveExistingSessionAsync(Request(turnSessionId: StartSession), CancellationToken.None);

        resolved.Should().BeNull(
            "a closed session in the checkpoint means the last turn finished; falling back to the older live session " +
            "would re-attach to an obsolete session from before that finished turn");
    }

    /// <summary>
    /// A session id whose row no longer exists is treated as not live.
    /// </summary>
    /// <remarks>
    /// "Unknown" is not "assume fine". Observing a session that cannot exist produces the same silent
    /// misbehaviour as observing a closed one: nothing to watch, so the outcome is re-derived from the
    /// ledger fallback.
    /// </remarks>
    [Fact]
    public async Task ResolveExistingSession_SessionRowIsMissing_DoesNotAttach()
    {
        using var db = Db();

        var runner = NewRunner(db);

        var resolved = await runner.ResolveExistingSessionAsync(
            Request(turnSessionId: FreshSession), CancellationToken.None);

        resolved.Should().BeNull();
    }

    /// <summary>
    /// A malformed or empty-GUID session id degrades to a fresh dispatch rather than to session zero.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ResolveExistingSession_UnusableSessionId_ReturnsNull(string? rawTurnSessionId)
    {
        using var db = Db();

        var runner = NewRunner(db);

        var resolved = await runner.ResolveExistingSessionAsync(
            Request(turnSessionId: null) with { TurnSessionId = rawTurnSessionId }, CancellationToken.None);

        resolved.Should().BeNull();
    }

    /// <summary>
    /// Every terminal status is rejected; every in-flight status is accepted.
    /// </summary>
    /// <remarks>
    /// Table-driven because the set is a contract with the session state machine, and a status added to
    /// <c>TerminalStatuses</c> should be covered here rather than discovered in production.
    /// </remarks>
    [Theory]
    [InlineData("completed", false)]
    [InlineData("stopped", false)]
    [InlineData("error", false)]
    [InlineData("archived", false)]
    [InlineData("idle", false)]
    [InlineData("waiting_for_input", false)]
    [InlineData("COMPLETED", false)]
    [InlineData("thinking", true)]
    [InlineData("busy", true)]
    [InlineData("queued", true)]
    public async Task ResolveExistingSession_AppliesTheTerminalStatusSet(string status, bool expectAttach)
    {
        using var db = Db();
        await SeedSessionAsync(db, StartSession, status: status);

        var runner = NewRunner(db);

        var resolved = await runner.ResolveExistingSessionAsync(Request(turnSessionId: StartSession), CancellationToken.None);

        resolved.Should().Be(expectAttach ? StartSession : null,
            $"'{status}' is {(expectAttach ? "in-flight, so re-attaching is correct" : "terminal, so attaching would re-observe a finished transcript")}");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2b — the assembler's window must follow its documented precedence
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The conversation window is loaded from the checkpoint's session, not the workflow-start one.
    /// </summary>
    /// <remarks>
    /// The method's own XML doc has always listed the checkpoint first. The implementation checked the
    /// input first and returned, so the doc described behaviour the code did not have — the worst kind of
    /// comment, because a reader trusts it. Concretely: turn 2 dispatches a new session, and the window
    /// kept loading turn 1's messages, so the agent was handed its opening context and never its recent
    /// work.
    /// </remarks>
    [Fact]
    public async Task ResolveTurnSessionId_PrefersCheckpointOverWorkflowInput()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, CheckpointSession, status: "completed");
        await SeedSessionAsync(db, StartSession, status: "completed");
        await SeedCheckpointAsync(db, CheckpointSession);

        var assembler = NewAssembler(db);

        var resolved = await assembler.ResolveTurnSessionIdAsync(
            Input(turnSessionId: StartSession.ToString()), CancellationToken.None);

        resolved.Should().Be(CheckpointSession);
    }

    /// <summary>
    /// With no checkpoint recorded, the workflow-start value is used — the fallback still works.
    /// </summary>
    [Fact]
    public async Task ResolveTurnSessionId_WithNoCheckpoint_UsesWorkflowInput()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, StartSession, status: "completed");

        var assembler = NewAssembler(db);

        var resolved = await assembler.ResolveTurnSessionIdAsync(
            Input(turnSessionId: StartSession.ToString()), CancellationToken.None);

        resolved.Should().Be(StartSession);
    }

    /// <summary>
    /// Neither source usable falls through to the metadata scan, which is step 3 of the documented order.
    /// </summary>
    [Fact]
    public async Task ResolveTurnSessionId_FallsThroughToTheJobMetadataScan()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, FreshSession, status: "completed", metadataJobId: JobId);

        var assembler = NewAssembler(db);

        var resolved = await assembler.ResolveTurnSessionIdAsync(
            Input(turnSessionId: null), CancellationToken.None);

        resolved.Should().Be(FreshSession);
    }

    /// <summary>
    /// The window the agent actually receives is the checkpoint session's, end to end.
    /// </summary>
    /// <remarks>
    /// The two tests above assert the id; this asserts the consequence, which is what the review's
    /// "never advance the sliding window" described. Turn 1's session holds "OLD" and turn 2's holds
    /// "NEW"; the prompt must contain the new one.
    /// </remarks>
    [Fact]
    public async Task Assemble_UsesTheCheckpointSessionsConversationWindow()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);

        await SeedSessionAsync(db, StartSession, status: "completed");
        await SeedSessionAsync(db, CheckpointSession, status: "completed");
        await SeedMessageAsync(db, StartSession, "the opening instruction from turn one", "OLD-MARKER");
        await SeedMessageAsync(db, CheckpointSession, "the most recent exchange", "NEW-MARKER");
        await SeedCheckpointAsync(db, CheckpointSession);

        var assembler = NewAssembler(db);

        var context = await assembler.AssembleAsync(
            Input(turnSessionId: StartSession.ToString()), pendingReply: null);

        context.Prompt.Should().Contain("NEW-MARKER");
        context.Prompt.Should().NotContain("OLD-MARKER",
            "the window must advance to the current session; pinning it to turn 1 is the defect");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2c — the post-turn save must not clear the pairing written mid-turn
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A turn that records its session id mid-flight still has it on the checkpoint afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The clobber, step by step, for a workflow started without an explicit session id (the normal
    /// case): the assembler loads a checkpoint that does not exist yet; the runner dispatches and writes
    /// S1 to the durable row; the post-turn hook then builds the next checkpoint from
    /// <c>input.TurnSessionId ?? context.Checkpoint?.TurnSessionId</c> — both null — and
    /// <c>SaveCheckpointAsync</c> assigns the column unconditionally, clearing S1.
    /// </para>
    /// <para>
    /// The consequence is an inversion of the design's intent: the re-attach pairing survived only for a
    /// turn that failed mid-flight and was destroyed by every turn that <em>succeeded</em>, which is
    /// precisely backwards.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunAgentCodingTurn_PreservesTheSessionIdTheRunnerRecordedMidTurn()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, FreshSession, status: "completed");

        var checkpoints = new WorkspaceStateRepository(db);

        // Stands in for HttpAgentTurnRunner.RecordSessionPairingAsync, which writes the dispatched
        // session id to the durable row before the first poll.
        var runner = new PairingThenCompletingRunner(checkpoints, FreshSession);

        var activities = new ExecutionActivities(
            NullLogger<ExecutionActivities>.Instance,
            storageService: null,
            config: null,
            assembler: new StubAssembler(),
            checkpoints: checkpoints,
            turnRunner: runner,
            reconciler: new NoProbeReconciler());

        await activities.RunAgentCodingTurnAsync(Input(turnSessionId: null), pendingReply: null);

        var saved = await checkpoints.GetCheckpointAsync(JobId.ToString(), TaskId.ToString());

        saved.Should().NotBeNull("the post-turn hook must write a checkpoint");
        saved!.TurnSessionId.Should().Be(FreshSession.ToString(),
            "the pairing the runner recorded mid-turn is the only thing that lets a retry of a " +
            "timed-out activity re-attach instead of dispatching a second agent onto the same branch");
    }

    /// <summary>
    /// The same turn still records its branch and summary — the fix must not narrow the write.
    /// </summary>
    /// <remarks>
    /// Guards against a "fix" that preserves the session id by skipping the rest of the checkpoint, and
    /// against the re-read throwing and short-circuiting the save.
    /// </remarks>
    [Fact]
    public async Task RunAgentCodingTurn_KeepsWritingTheOtherCheckpointFields()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db, FreshSession, status: "completed");

        var checkpoints = new WorkspaceStateRepository(db);
        var runner = new PairingThenCompletingRunner(checkpoints, FreshSession);

        var activities = new ExecutionActivities(
            NullLogger<ExecutionActivities>.Instance,
            storageService: null,
            config: null,
            assembler: new StubAssembler(),
            checkpoints: checkpoints,
            turnRunner: runner,
            reconciler: new FixedReconciler("fix/macro-turns", "a1b2c3d implement macro turns"));

        await activities.RunAgentCodingTurnAsync(Input(turnSessionId: null), pendingReply: null);

        var saved = await checkpoints.GetCheckpointAsync(JobId.ToString(), TaskId.ToString());

        saved!.ActiveBranch.Should().Be("fix/macro-turns");
        saved.HeadCommit.Should().Be("a1b2c3d implement macro turns");
        saved.Status.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A checkpoint re-read that fails must not cost the whole checkpoint.
    /// </summary>
    /// <remarks>
    /// The re-read is an optimization for one column. Letting its failure propagate would skip the save
    /// and lose the branch, the dirty-file list and the summary too — trading a possibly one-turn-stale
    /// session id for the entire recovery note. The turn already did real work.
    /// </remarks>
    [Fact]
    public async Task RunAgentCodingTurn_WhenThePairingRereadFails_StillWritesTheCheckpoint()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);

        var checkpoints = new WorkspaceStateRepository(db);
        var runner = new PairingThenCompletingRunner(checkpoints, FreshSession);

        var activities = new ExecutionActivities(
            NullLogger<ExecutionActivities>.Instance,
            storageService: null,
            config: null,
            assembler: new StubAssembler(),
            checkpoints: new ThrowingReadRepository(checkpoints),
            turnRunner: runner,
            reconciler: new FixedReconciler("fix/macro-turns", "a1b2c3d implement macro turns"));

        var act = () => activities.RunAgentCodingTurnAsync(Input(turnSessionId: null), pendingReply: null);

        await act.Should().NotThrowAsync("a failed re-read must degrade, not abort the turn");

        var saved = await checkpoints.GetCheckpointAsync(JobId.ToString(), TaskId.ToString());
        saved.Should().NotBeNull();
        saved!.ActiveBranch.Should().Be("fix/macro-turns",
            "losing the session id for one turn is recoverable; losing the whole checkpoint is not");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Wiring
    // ──────────────────────────────────────────────────────────────────────────

    private static HttpAgentTurnRunner NewRunner(AgentMeshDbContext db) => new(
        db,
        new WorkspaceStateRepository(db),
        new StubHttpClientFactory(),
        new Microsoft.Extensions.Configuration.ConfigurationManager(),
        NullLogger<HttpAgentTurnRunner>.Instance);

    private static TurnContextAssembler NewAssembler(AgentMeshDbContext db) => new(
        db,
        new WorkspaceStateRepository(db),
        new NoProbeReconciler(),
        null,
        null);

    private static TaskExecutionInput Input(string? turnSessionId) => new(
        JobId: JobId.ToString(),
        TaskId: TaskId.ToString(),
        AgentId: "driver-rook",
        ProjectId: "agent-mesh",
        WorkspacePath: "/workspace/repos/agent-mesh",
        TargetBranch: "main",
        PersonaId: "rook",
        TurnSessionId: turnSessionId);

    private static AgentTurnRequest Request(Guid? turnSessionId) => new(
        Input: Input(turnSessionId?.ToString("D")),
        Prompt: "do the thing",
        WorkspacePath: "/workspace/repos/agent-mesh",
        PersonaId: "rook",
        AgentId: "driver-rook",
        // The wire type is a string; the workflow carries it as one because it is replayed from
        // Temporal's history, where a Guid would serialize to the same text anyway.
        TurnSessionId: turnSessionId?.ToString("D"));

    private static async Task SeedProjectAndJobAsync(AgentMeshDbContext db)
    {
        db.Projects.Add(new Project { Id = "agent-mesh", Name = "AgentMesh" });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = JobId,
            ProjectId = "agent-mesh",
            Title = "Macro turns",
            TasksJson = "[]",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSessionAsync(
        AgentMeshDbContext db,
        Guid sessionId,
        string status,
        Guid? metadataJobId = null)
    {
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = $"turn {status}",
            Status = status,
            PersonaId = "rook",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            MetadataJson = metadataJobId is null
                ? "{}"
                : $"{{\"jobId\":\"{metadataJobId.Value}\"}}",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedMessageAsync(
        AgentMeshDbContext db, Guid sessionId, string content, string marker)
    {
        db.SessionMessages.Add(new SessionMessage
        {
            SessionId = sessionId,
            SenderAgentId = "driver-rook",
            // Anything but "tool_result": the window query filters those out before Take, so a
            // tool_result row here would be invisible and the assertion would fail for the wrong reason.
            EventType = "response",
            Content = $"{content} {marker}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedCheckpointAsync(AgentMeshDbContext db, Guid sessionId)
    {
        var checkpoints = new WorkspaceStateRepository(db);
        await checkpoints.SaveCheckpointAsync(new TaskWorkingCheckpoint(
            JobId: JobId.ToString(),
            TaskId: TaskId.ToString(),
            ActiveBranch: "fix/macro-turns",
            Status: "working",
            LastSummary: "in progress",
            ModifiedFiles: [],
            ActiveStash: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            TurnSessionId: sessionId.ToString()));
    }

    /// <summary>
    /// A runner that records its session id to the durable row, then completes the turn.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>HttpAgentTurnRunner</c>: the pairing is written <em>before</em> the turn resolves, so a
    /// cancelled attempt can still be found by its retry. Reproducing that ordering here is the point —
    /// the clobber only exists because the write happens mid-turn, after the assembler's snapshot.
    /// </remarks>
    private sealed class PairingThenCompletingRunner : IAgentTurnRunner
    {
        private readonly IWorkspaceStateRepository checkpoints;
        private readonly Guid sessionId;

        public PairingThenCompletingRunner(IWorkspaceStateRepository checkpoints, Guid sessionId)
        {
            this.checkpoints = checkpoints;
            this.sessionId = sessionId;
        }

        public async Task<TurnOutcome> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
        {
            await checkpoints.SaveCheckpointAsync(new TaskWorkingCheckpoint(
                JobId: request.Input.JobId,
                TaskId: request.Input.TaskId,
                ActiveBranch: "fix/macro-turns",
                Status: "working",
                LastSummary: "dispatched",
                ModifiedFiles: [],
                ActiveStash: null,
                UpdatedAt: DateTimeOffset.UtcNow,
                TurnSessionId: sessionId.ToString()), ct);

            return TurnOutcome.Completed("done");
        }
    }

    /// <summary>An assembler that supplies a prompt without touching the database.</summary>
    private sealed class StubAssembler : ITurnContextAssembler
    {
        public Task<AssembledTurnContext> AssembleAsync(
            TaskExecutionInput input,
            ChatMessageSignal? pendingReply = null,
            CancellationToken ct = default) =>
            Task.FromResult(new AssembledTurnContext(
                Prompt: "[TASK ENVELOPE] stub prompt",
                WorkspacePath: input.WorkspacePath,
                EnvironmentNote: null,
                // Deliberately null: this is the pre-turn snapshot, and on turn 1 there is nothing to
                // snapshot. A non-null value here would mask the clobber.
                Checkpoint: null));
    }

    /// <summary>A reconciler whose probe reports nothing, so the post-turn facts carry forward.</summary>
    private sealed class NoProbeReconciler : IWorkspaceReconciler
    {
        public Task<WorkspaceReconciliationReport> ReconcileAsync(
            string workspacePath, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceReconciliationReport(workspacePath, Inspected: false));
    }

    private sealed class FixedReconciler(string branch, string headCommit) : IWorkspaceReconciler
    {
        public Task<WorkspaceReconciliationReport> ReconcileAsync(
            string workspacePath, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceReconciliationReport(
                workspacePath,
                Inspected: true,
                IsGitRepository: true,
                Branch: branch,
                HeadCommitOneline: headCommit));
    }

    /// <summary>
    /// Fails only the mid-turn re-read, so the degradation path is the thing under test.
    /// </summary>
    /// <remarks>
    /// Writes pass through: the runner's pairing write and the final save must both succeed for the test
    /// to isolate the re-read.
    /// </remarks>
    private sealed class ThrowingReadRepository(IWorkspaceStateRepository inner) : IWorkspaceStateRepository
    {
        public Task<TaskWorkingCheckpoint?> GetCheckpointAsync(
            string jobId, string taskId, CancellationToken ct = default) =>
            Task.FromException<TaskWorkingCheckpoint?>(
                new InvalidOperationException("simulated store failure on the pairing re-read"));

        public Task<bool> SaveCheckpointAsync(
            TaskWorkingCheckpoint checkpoint, CancellationToken ct = default) =>
            inner.SaveCheckpointAsync(checkpoint, ct);

        public Task<IReadOnlyList<TaskWorkingCheckpoint>> GetCheckpointsForJobAsync(
            string jobId, CancellationToken ct = default) => inner.GetCheckpointsForJobAsync(jobId, ct);
    }

    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }
}
