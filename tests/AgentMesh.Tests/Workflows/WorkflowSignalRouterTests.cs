using System.Diagnostics;
using AgentMesh.Api.Services;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Tests.Sandbox;
using FluentAssertions;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;
using ChatMessageSignal = AgentMesh.Core.Workflows.ChatMessageSignal;
using PrReviewSignal = AgentMesh.Core.Workflows.PrReviewSignal;

namespace AgentMesh.Tests.Workflows;

/// <summary>
/// The API's signal router, driven against a real workflow on Temporal's time-skipping test server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a live server rather than a mocked <c>ITemporalClient</c>.</strong> The single most
/// dangerous failure mode in this feature is invisible to a mock, so a mock cannot be the guard. The
/// SDK registers a signal handler under the name with the <c>Async</c> suffix stripped
/// (<c>ReceiveChatMessage</c>, not <c>ReceiveChatMessageAsync</c>), and a signal sent to a name the
/// workflow does not handle is <em>accepted by the server and silently buffered forever</em>: no error,
/// no timeout, a task parked until the day someone re-reads the transcript. A mock would happily verify
/// "we called <c>SignalAsync</c>" while the payload went nowhere. These tests send through the router
/// into a workflow that is genuinely parked, and assert the workflow <em>resumed with the signal's
/// contents</em> — which is only possible if the name resolved and the payload deserialized.
/// </para>
/// <para>
/// <c>WorkflowHandle</c> is sealed and its members are non-virtual, so the router cannot be unit-tested
/// against a fake client at all without reflection gymnastics; the live environment is both the honest
/// seam and the one that catches the silent-drop class.
/// </para>
/// <para>
/// Serialised in the shared Temporal collection for the reason documented on
/// <see cref="SandboxTestCollections.Temporal"/>: one test-server process at a time.
/// </para>
/// </remarks>
[Collection(SandboxTestCollections.Temporal)]
public class WorkflowSignalRouterTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // The silent-drop guard
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A chat reply routed through the API's router releases a genuinely parked workflow, and the
    /// reply's content reaches the next turn.
    /// </summary>
    [Fact]
    public async Task SendChatMessage_ResumesTheParkedWorkflow_AndDeliversTheReply()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        turns.Enqueue(TurnOutcome.Completed("Shipped the model you picked."));

        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(env.Client);
        var reply = new ChatMessageSignal(
            SenderId: "astra",
            Content: "Use OIDC, not passwords.",
            Timestamp: DateTimeOffset.UtcNow,
            MessageId: "msg-1");

        var delivery = await router.SendChatMessageAsync(jobId, reply);

        delivery.Status.Should().Be(SignalDeliveryStatus.Delivered);
        delivery.WorkflowId.Should().Be(WorkflowSignalRouter.WorkflowIdFor(jobId));

        var result = await handle.GetResultAsync();
        result.Success.Should().BeTrue();

        // The decisive assertion: the second turn received the reply as its pending input. If the
        // router had signalled the suffixed name, the workflow would still be parked here and this
        // await would hang rather than fail — which is why the environment's own 10s default
        // cancellation on GetResultAsync matters more than any Assert in this file.
        turns.PendingReplyAt(1).Should().NotBeNull("the parked turn must be resumed by the signal");
        turns.PendingReplyAt(1)!.Content.Should().Be("Use OIDC, not passwords.");
        turns.PendingReplyAt(1)!.SenderId.Should().Be("astra");
    }

    /// <summary>
    /// A review decision routed through the router releases the PR-review pause and triggers the merge.
    /// </summary>
    [Fact]
    public async Task SendPrReview_ResumesTheWorkflow_AndTheMergeActivityRuns()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity();
        await using var worker = TestWorker.Start(env, turns, git);

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.AwaitingReview("https://github.com/o/r/pull/77", 77, "PR up."));
        turns.Enqueue(TurnOutcome.Completed("Merged."));

        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");

        var router = new WorkflowSignalRouter(env.Client);
        var delivery = await router.SendPrReviewAsync(
            jobId,
            new PrReviewSignal("sorren", IsApproved: true, null, DateTimeOffset.UtcNow, 77));

        delivery.Status.Should().Be(SignalDeliveryStatus.Delivered);

        var result = await handle.GetResultAsync();
        result.Success.Should().BeTrue();
        git.Calls.Should().ContainSingle().Which.Should().Be("https://github.com/o/r/pull/77");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The "nobody is listening" cases
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A reply to a job that was never put under orchestration is a reported result, not an RPC error.
    /// </summary>
    /// <remarks>
    /// The message is already persisted by the time the router runs, so throwing here would tell a user
    /// their reply was lost when it was not. The controller needs a value it can render as "saved, but
    /// no agent is waiting".
    /// </remarks>
    [Fact]
    public async Task SendChatMessage_WithNoWorkflowRunning_ReportsNoRunningWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var router = new WorkflowSignalRouter(env.Client);

        var delivery = await router.SendChatMessageAsync(
            Guid.NewGuid(),
            new ChatMessageSignal("astra", "hello?", DateTimeOffset.UtcNow, "msg-9"));

        delivery.Status.Should().Be(SignalDeliveryStatus.NoRunningWorkflow);
        delivery.Detail.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>An approval for a job with no workflow cannot silently look like a queued decision.</summary>
    [Fact]
    public async Task SendPrReview_WithNoWorkflowRunning_ReportsNoRunningWorkflow()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var router = new WorkflowSignalRouter(env.Client);

        var delivery = await router.SendPrReviewAsync(
            Guid.NewGuid(),
            new PrReviewSignal("sorren", true, null, DateTimeOffset.UtcNow, 3));

        delivery.Status.Should().Be(SignalDeliveryStatus.NoRunningWorkflow);
    }

    /// <summary>
    /// A reply that arrives while a turn is still running is accepted and buffered, not dropped.
    /// </summary>
    /// <remarks>
    /// This is the case the router's two-phase design (describe, then signal) exists to serve: the
    /// workflow is Running but not parked, the signal lands in the inbox, and the next turn drains it.
    /// Asserting the drain — not just the acceptance — is what proves buffering is real rather than a
    /// silent drop with a friendlier name.
    /// </remarks>
    [Fact]
    public async Task SendChatMessage_WhileATurnIsRunning_IsDrainedByTheNextTurn()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // Turn 1 stays inside the activity until the test releases it, so "the workflow is running
            // but not waiting" is a fact the test establishes rather than a race it hopes to win.
            turns.EnqueueGatedBy(gate, TurnOutcome.ContinueTurn("step one done"));
            turns.Enqueue(TurnOutcome.Completed("step two done"));

            var handle = await StartAsync(env, jobId);

            // TurnCount, not Status: the run loop sets "Running" only until it increments the counter
            // and issues the first activity, after which the observable status is "Turn 1". A predicate
            // on "Running" would never hold once a turn is genuinely in flight.
            await WaitUntilAsync(handle, s => s.TurnCount >= 1);

            var router = new WorkflowSignalRouter(env.Client);
            var delivery = await router.SendChatMessageAsync(
                jobId,
                new ChatMessageSignal("astra", "Also cover the refresh path.", DateTimeOffset.UtcNow, "msg-2"));

            delivery.Status.Should().Be(SignalDeliveryStatus.Delivered);

            // Buffered, and visible to an observer as pending — the dashboard's "1 unread reply" state.
            await WaitUntilAsync(handle, s => s.PendingChatCount == 1);

            gate.SetResult();
            var result = await handle.GetResultAsync();
            result.Success.Should().BeTrue();

            var drained = turns.PendingReplyAt(1);
            drained.Should().NotBeNull()
                .And.Subject.As<ChatMessageSignal>().Content.Should().Be("Also cover the refresh path.");
        }
        finally
        {
            // Released on every exit path. A gated activity that is never released holds the worker's
            // graceful shutdown indefinitely, which turns an assertion failure into a hung test run —
            // the worst possible diagnostic, and the one this suite's own history warns about.
            gate.TrySetResult();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Start / status
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A second start for a job that is already running returns the existing id instead of dispatching
    /// a second agent onto the same branch.
    /// </summary>
    [Fact]
    public async Task StartTaskWorkflow_WhenOneIsAlreadyRunning_ReportsAlreadyRunning()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        turns.EnqueueGatedBy(gate, TurnOutcome.Completed("eventually"));

        try
        {
            var router = new WorkflowSignalRouter(env.Client);

            var first = await router.StartTaskWorkflowAsync(InputFor(jobId.ToString(), "task-a"));
            first.Started.Should().BeTrue();
            first.AlreadyRunning.Should().BeFalse();
            first.WorkflowId.Should().Be(WorkflowSignalRouter.WorkflowIdFor(jobId));

            // The run loop's own status is "Turn 1" by the time a turn is in flight; TurnCount is the
            // observable that says "the workflow is live and has begun work".
            await WaitUntilAsync(
                env.Client.GetWorkflowHandle<TaskExecutionWorkflow>(first.WorkflowId),
                s => s.TurnCount >= 1);

            var second = await router.StartTaskWorkflowAsync(InputFor(jobId.ToString(), "task-b"));
            second.Started.Should().BeFalse();
            second.AlreadyRunning.Should().BeTrue();
            second.WorkflowId.Should().Be(first.WorkflowId);

            // One workflow, therefore exactly one turn in flight — the property the double-click guard
            // is for. If the second start had begun a run, the fake would have been invoked twice.
            turns.Calls.Should().Be(1);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>A job id that is not a GUID is refused before any RPC is attempted.</summary>
    [Fact]
    public async Task StartTaskWorkflow_WithANonGuidJobId_IsRefused()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var router = new WorkflowSignalRouter(env.Client);

        var result = await router.StartTaskWorkflowAsync(InputFor("not-a-guid", "task-a"));

        result.Started.Should().BeFalse();
        result.Detail.Should().Contain("not a job GUID");
    }

    /// <summary>
    /// The status query returns the live view for a running workflow.
    /// </summary>
    [Fact]
    public async Task GetStatus_ForARunningWorkflow_ReturnsTheLiveView()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which layout?"));
        turns.Enqueue(TurnOutcome.Completed("done"));

        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(env.Client);
        var status = await router.GetStatusAsync(jobId, taskId: null);

        status.Should().NotBeNull();
        status!.Status.Should().StartWith("Waiting on input");
        status.AwaitingSince.Should().NotBeNull();
        status.TurnCount.Should().Be(1);

        // Release the park so the worker can shut down cleanly.
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Layout A", DateTimeOffset.UtcNow, "msg-x")));
        await handle.GetResultAsync();
    }

    /// <summary>
    /// A status query for a job that has no workflow returns null rather than throwing.
    /// </summary>
    /// <remarks>
    /// The controller maps null to a 404 with an actionable message. An <c>RpcException</c> escaping
    /// here would surface as a 500 on a dashboard poll, which is the difference between "this job is
    /// not orchestrated" and "the control plane is broken".
    /// </remarks>
    [Fact]
    public async Task GetStatus_ForAnUnknownJob_ReturnsNullRatherThanThrowing()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var router = new WorkflowSignalRouter(env.Client);

        var status = await router.GetStatusAsync(Guid.NewGuid(), taskId: null);

        status.Should().BeNull();
    }

    /// <summary>
    /// The id scheme is stable and matches what the worker's registrations assume.
    /// </summary>
    /// <remarks>
    /// Pinned as a pure unit assertion because the format is a wire contract between the API (which
    /// derives it to signal) and anything else that looks a workflow up. The "N" format is load-bearing:
    /// a hyphenated GUID would make two code paths that both interpolate <c>:N</c> disagree.
    /// </remarks>
    [Fact]
    public void WorkflowIdFor_IsThePrefixPlusANHyphenlessGuid()
    {
        var jobId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        WorkflowSignalRouter.WorkflowIdFor(jobId)
            .Should().Be("task-execution-11111111222233334444555555555555");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The ownership probe (PostJobMessage consults this before dispatching)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A running workflow owns the job's next turn.
    /// </summary>
    /// <remarks>
    /// The positive case of the rule that keeps a legacy SignalR runner off a branch the workflow is
    /// already driving. Without it, an unaddressed human reply resolves its target to the pending task's
    /// assignee — the orchestrated persona — and both paths start an agent.
    /// </remarks>
    [Fact]
    public async Task IsWorkflowDriving_ForARunningWorkflow_IsTrue()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(env.Client);

        (await router.IsWorkflowDrivingAsync(jobId)).Should().BeTrue();

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Layout A", DateTimeOffset.UtcNow, "msg-x")));
        await handle.GetResultAsync();
    }

    /// <summary>
    /// When scoped to the persona the workflow is driving, IsWorkflowDrivingAsync answers true.
    /// When scoped to a different peer persona, it answers false so that peer can be dispatched.
    /// </summary>
    [Fact]
    public async Task IsWorkflowDriving_ScopedToPersona_DistinguishesDrivingFromPeerPersonas()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        // StartAsync launches the workflow with PersonaId = "rook"
        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(env.Client);

        // Targeted check for the driving persona (rook) -> true
        (await router.IsWorkflowDrivingAsync(jobId, "rook")).Should().BeTrue();
        (await router.IsWorkflowDrivingAsync(jobId, "@Rook")).Should().BeTrue("normalization handles casing and leading @");

        // Targeted check for a peer persona (astra) -> false (peer should not be locked out)
        (await router.IsWorkflowDrivingAsync(jobId, "astra")).Should().BeFalse("workflow is driving rook, not astra");

        // Unscoped check (null or empty) -> true (job-level running check)
        (await router.IsWorkflowDrivingAsync(jobId, null)).Should().BeTrue();

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Use OIDC", DateTimeOffset.UtcNow, "msg-fin")));
        await handle.GetResultAsync();
    }

    /// <summary>
    /// When workflow is started with Memo, the ownership probe reads from Temporal server description
    /// and succeeds even when no worker is running (no worker dependency).
    /// </summary>
    [Fact]
    public async Task IsWorkflowDriving_WithMemo_AnswersWithoutWorkerDependency()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var router = new WorkflowSignalRouter(env.Client);
        var jobId = Guid.NewGuid();
        var input = InputFor(jobId.ToString(), "task-memo") with { PersonaId = "rook" };

        var startResult = await router.StartTaskWorkflowAsync(input);
        startResult.Started.Should().BeTrue();

        // Notice: NO worker is started or running.
        // A Temporal query would hang/fail because there is no worker to answer it.
        // But the describe call reads Memo directly from Temporal server:
        (await router.IsWorkflowDrivingAsync(jobId, "rook")).Should().BeTrue("memo identifies rook as driving persona without worker");
        (await router.IsWorkflowDrivingAsync(jobId, "astra")).Should().BeFalse("memo distinguishes peer persona without worker");
    }

    /// <summary>
    /// When workflow was started without Memo (e.g. legacy workflow run) and worker is down,
    /// the status query times out/fails and probe fails toward dispatch (returns false).
    /// </summary>
    [Fact]
    public async Task IsWorkflowDriving_WithoutMemo_WhenWorkerIsDown_QueryFailureReturnsFalse()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var router = new WorkflowSignalRouter(env.Client);
        var jobId = Guid.NewGuid();
        var workflowId = WorkflowSignalRouter.WorkflowIdFor(jobId);
        var input = InputFor(jobId.ToString(), "task-no-memo") with { PersonaId = "rook" };

        // Start workflow directly without Memo so the describe call finds no persona memo
        await env.Client.StartWorkflowAsync(
            (TaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(input),
            new WorkflowOptions(workflowId, WorkflowSignalRouter.TaskQueue));

        // No worker is running to answer the status query.
        // Query failure must return false to fail toward legacy dispatch rather than swallowing the message.
        var driving = await router.IsWorkflowDrivingAsync(jobId, "astra");
        driving.Should().BeFalse("query failure without memo must fail toward dispatch (false), not swallow peer messages");
    }

    /// <summary>
    /// A job that was never orchestrated answers false rather than throwing.
    /// </summary>
    /// <remarks>
    /// This is the overwhelmingly common case — orchestration is opt-in per task, so almost every job in
    /// the product hits this path on every chat message. The server's "execution not found" RPC error must
    /// be an ordinary false here, or the probe would fail the hottest endpoint in the product for the
    /// majority of jobs.
    /// </remarks>
    [Fact]
    public async Task IsWorkflowDriving_ForAnUnknownJob_IsFalseRatherThanThrowing()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var router = new WorkflowSignalRouter(env.Client);

        var act = async () => await router.IsWorkflowDrivingAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
        (await router.IsWorkflowDrivingAsync(Guid.NewGuid())).Should().BeFalse();
    }

    /// <summary>
    /// A workflow that has closed stops owning the job, so the legacy path is available again.
    /// </summary>
    /// <remarks>
    /// The other half of "the workflow's own liveness is the only answer that cannot drift". A completed
    /// run must not keep suppressing dispatches forever — that would freeze a job whose orchestration
    /// ended but which still takes chat replies, which is the same silent-swallow failure an unreachable
    /// Temporal would cause.
    /// </remarks>
    [Fact]
    public async Task IsWorkflowDriving_AfterTheWorkflowCompletes_IsFalse()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Completed("done"));
        var handle = await StartAsync(env, jobId);
        await handle.GetResultAsync();

        var router = new WorkflowSignalRouter(env.Client);

        (await router.IsWorkflowDrivingAsync(jobId)).Should().BeFalse(
            "a finished run must release the job back to the ordinary chat path");
    }

    /// <summary>
    /// The probe returns within its budget when Temporal is unreachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is measured rather than asserted on a constant.</strong> The first version of this
    /// test checked that <c>ProbeTimeout</c> was ≤ 2 seconds. That is theatre: it pins a number, not the
    /// behaviour, and it stayed green while the implementation was wrong. The implementation it was
    /// guarding passed <c>RpcOptions.Timeout = 1.5s</c> — which bounds a single <em>attempt</em>, after
    /// which the SDK's default retry policy re-dials from scratch. Measured against a dead endpoint:
    /// <strong>9.08 seconds</strong> for a call configured to give up in 1.5. A cancellation deadline on
    /// the same call: <strong>1.52 seconds</strong>.
    /// </para>
    /// <para>
    /// So this test drives the real method against a real client whose server has gone away, and asserts
    /// the elapsed wall-clock bound. It fails against the <c>RpcOptions.Timeout</c> version and passes
    /// against the deadline version — verified by running it against both. That is the property that
    /// matters: this probe sits in the request path of every job-chat message that mentions a persona, and
    /// a stalled control plane must degrade to "not orchestrated" in about a second rather than hold a
    /// request thread for nine.
    /// </para>
    /// <para>
    /// The upper bound is 3 seconds against a 1.5-second budget. It is not tight enough to be a latency
    /// assertion — the deadline is enforced by a timer, so the passing case lands near 1.5s on any machine
    /// speed — but it is wide enough that ordinary scheduling noise cannot fail it, and narrow enough that
    /// the regression it guards (measured at 5.9–9.1s) trips it immediately.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IsWorkflowDriving_WhenTemporalIsUnreachable_ReturnsWithinItsBudget()
    {
        // A real client, connected to a real server, then orphaned by that server shutting down. This is
        // the only way to exercise the actual gRPC path: WorkflowHandle is not mockable (Moq cannot proxy
        // it — it has no accessible constructor), and a hand-rolled TCP black hole fails at ConnectAsync
        // before the probe is ever reached.
        var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var client = env.Client;
        var router = new WorkflowSignalRouter(client);
        await env.DisposeAsync();

        var elapsed = Stopwatch.StartNew();
        var driving = await router.IsWorkflowDrivingAsync(Guid.NewGuid());
        elapsed.Stop();

        driving.Should().BeFalse("an unreachable control plane must answer 'not orchestrated'");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            $"the probe's budget is {WorkflowSignalRouter.ProbeTimeout.TotalSeconds:0.#}s and it bounds " +
            "the whole operation; ~9s means it regressed to RpcOptions.Timeout, which bounds one attempt " +
            "and then lets the retry policy re-dial");
    }

    /// <summary>
    /// The caller's own cancellation still propagates through the probe's budget.
    /// </summary>
    /// <remarks>
    /// The budget is a linked token, so it must not swallow the caller's reason for cancelling: the
    /// request thread is going away and continuing to dispatch would leak a turn for a client that has
    /// left. Distinguishing "my deadline" (degrade to false) from "the caller left" (re-throw) is the whole
    /// job of the exception filter on the probe.
    /// </remarks>
    [Fact]
    public async Task IsWorkflowDriving_WhenTheCallerCancels_Propagates()
    {
        var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var client = env.Client;
        var router = new WorkflowSignalRouter(client);
        await env.DisposeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await router.IsWorkflowDrivingAsync(Guid.NewGuid(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller went away; degrading that to 'false' would keep the request alive to dispatch a " +
            "turn nobody is waiting for");
    }
    // ──────────────────────────────────────────────────────────────────────────
    // The status view's transcript deep-link (2d)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The status view carries the session id from the durable checkpoint, not the one the workflow
    /// happens to remember.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workflow's own <c>_turnSessionId</c> is assigned exactly once, from
    /// <c>input.TurnSessionId</c>, and never updated — by design. <see cref="TurnOutcome"/> deliberately
    /// has no session-id field because the id is per-<em>attempt</em>: recording it in the workflow would
    /// store a second id for a task whose first attempt timed out and orphan the session the retry is
    /// meant to re-attach to. The consequence, before this was fixed, was that the field was null for
    /// every workflow started without an explicit id (the normal case) and pinned to turn 1 for every
    /// workflow started with one — so the endpoint's documented "latest turn session, for deep-linking
    /// into the transcript" was either empty or wrong.
    /// </para>
    /// <para>
    /// The pairing lives in the checkpoint row, which the runner writes mid-turn. Reading it here keeps
    /// the workflow deterministic and adds no wire type; the checkpoint stays the single source of truth.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetStatus_EnrichesWithTheCheckpointSessionId()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        var taskId = "task-a";
        var sessionId = Guid.NewGuid();

        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(
            env.Client, checkpoints: new StubCheckpoints((taskId, sessionId)));

        var status = await router.GetStatusAsync(jobId, taskId);

        status.Should().NotBeNull();
        status!.TurnSessionId.Should().Be(sessionId.ToString(),
            "the checkpoint is the only freshest record of the pairing; the workflow cannot know it");

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Layout A", DateTimeOffset.UtcNow, "msg-x")));
        await handle.GetResultAsync();
    }

    /// <summary>
    /// With no task id supplied, the newest checkpoint on the job supplies the deep-link.
    /// </summary>
    /// <remarks>
    /// The status endpoint's <c>taskId</c> is an optional query parameter and the dashboard polls without
    /// one, so the job-level path is the common case rather than an edge case.
    /// </remarks>
    [Fact]
    public async Task GetStatus_WithoutATaskId_UsesTheNewestPairedCheckpointOnTheJob()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        var older = Guid.NewGuid();
        var newest = Guid.NewGuid();

        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        // Ordered newest-first, matching the repository's documented contract, and the first row carries
        // no pairing so the scan has to skip it.
        var router = new WorkflowSignalRouter(
            env.Client,
            checkpoints: new StubCheckpoints(
                ("task-unpaired", null),
                ("task-b", newest),
                ("task-a", older)));

        var status = await router.GetStatusAsync(jobId, taskId: null);

        status!.TurnSessionId.Should().Be(newest.ToString());

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Layout A", DateTimeOffset.UtcNow, "msg-x")));
        await handle.GetResultAsync();
    }

    /// <summary>
    /// No pairing anywhere leaves the rest of the status view intact.
    /// </summary>
    /// <remarks>
    /// The enrichment is additive. A task that has not dispatched a turn yet has no session id, and that
    /// must not cost the caller the lifecycle state it did ask for.
    /// </remarks>
    [Fact]
    public async Task GetStatus_WithNoPairing_StillReturnsTheStatusView()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(env.Client, checkpoints: new StubCheckpoints(("task-a", null)));

        var status = await router.GetStatusAsync(jobId, "task-a");

        status.Should().NotBeNull();
        status!.TurnSessionId.Should().BeNull();
        status.Status.Should().StartWith("Waiting on input");

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Layout A", DateTimeOffset.UtcNow, "msg-x")));
        await handle.GetResultAsync();
    }

    /// <summary>
    /// A pairing lookup that throws must not turn a working status endpoint into a failure.
    /// </summary>
    /// <remarks>
    /// The workflow query has already succeeded by this point; the database read is a display nicety. A
    /// 500 here would make the dashboard claim the control plane is broken when only the deep-link is
    /// unavailable — the same "unusable gate gets routed around" failure this branch already fixed once in
    /// the review gate.
    /// </remarks>
    [Fact]
    public async Task GetStatus_WhenThePairingLookupThrows_StillReturnsTheStatusView()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(
            env.Client, checkpoints: new ThrowingCheckpoints());

        var status = await router.GetStatusAsync(jobId, "task-a");

        status.Should().NotBeNull("a failed deep-link lookup must not fail the status request");
        status!.Status.Should().StartWith("Waiting on input");

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Layout A", DateTimeOffset.UtcNow, "msg-x")));
        await handle.GetResultAsync();
    }

    /// <summary>
    /// A router built without the repository — the shape every pre-existing test uses — is unchanged.
    /// </summary>
    /// <remarks>
    /// Pins the backward compatibility of the new optional constructor parameter: the enrichment is
    /// additive, and a host that has not wired the repository still answers status queries exactly as
    /// before rather than throwing on a null dependency.
    /// </remarks>
    [Fact]
    public async Task GetStatus_WithoutACheckpointRepository_BehavesAsBefore()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var jobId = Guid.NewGuid();
        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        var handle = await StartAsync(env, jobId);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var router = new WorkflowSignalRouter(env.Client);

        var status = await router.GetStatusAsync(jobId, "task-a");

        status.Should().NotBeNull();
        status!.TurnSessionId.Should().BeNull();

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Layout A", DateTimeOffset.UtcNow, "msg-x")));
        await handle.GetResultAsync();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Wiring
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A checkpoint store that answers only the two reads the status enrichment performs.
    /// </summary>
    private sealed class StubCheckpoints(params (string TaskId, Guid? SessionId)[] rows)
        : AgentMesh.Core.Repositories.IWorkspaceStateRepository
    {
        public Task<bool> SaveCheckpointAsync(
            TaskWorkingCheckpoint checkpoint, CancellationToken ct = default) => Task.FromResult(true);

        public Task<TaskWorkingCheckpoint?> GetCheckpointAsync(
            string jobId, string taskId, CancellationToken ct = default) =>
            Task.FromResult(For(taskId));

        public Task<IReadOnlyList<TaskWorkingCheckpoint>> GetCheckpointsForJobAsync(
            string jobId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TaskWorkingCheckpoint>>(
                rows.Select(r => For(r.TaskId)).OfType<TaskWorkingCheckpoint>().ToList());

        private TaskWorkingCheckpoint? For(string taskId)
        {
            var match = rows.FirstOrDefault(r => r.TaskId == taskId);
            if (match.TaskId is null) return null;

            return new TaskWorkingCheckpoint(
                JobId: "job", TaskId: taskId, ActiveBranch: "fix/x", Status: "working",
                LastSummary: "s", ModifiedFiles: [], ActiveStash: null,
                UpdatedAt: DateTimeOffset.UtcNow, TurnSessionId: match.SessionId?.ToString());
        }
    }

    private sealed class ThrowingCheckpoints
        : AgentMesh.Core.Repositories.IWorkspaceStateRepository
    {
        public Task<bool> SaveCheckpointAsync(
            TaskWorkingCheckpoint checkpoint, CancellationToken ct = default) => Task.FromResult(true);

        public Task<TaskWorkingCheckpoint?> GetCheckpointAsync(
            string jobId, string taskId, CancellationToken ct = default) =>
            Task.FromException<TaskWorkingCheckpoint?>(
                new InvalidOperationException("simulated store outage"));

        public Task<IReadOnlyList<TaskWorkingCheckpoint>> GetCheckpointsForJobAsync(
            string jobId, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<TaskWorkingCheckpoint>>(
                new InvalidOperationException("simulated store outage"));
    }

    private static TaskExecutionInput InputFor(string jobId, string taskId) => new(
        JobId: jobId,
        TaskId: taskId,
        AgentId: "driver-rook",
        ProjectId: "agent-mesh",
        WorkspacePath: "/workspace/repos/agent-mesh",
        TargetBranch: "main",
        PersonaId: "rook");

    /// <summary>
    /// Starts the workflow under the id the router will target, so the test exercises the real lookup.
    /// </summary>
    private static async Task<Temporalio.Client.WorkflowHandle<TaskExecutionWorkflow, TaskExecutionResult>>
        StartAsync(WorkflowEnvironment env, Guid jobId)
    {
        var input = InputFor(jobId.ToString(), "task-a");

        return await env.Client.StartWorkflowAsync(
            (TaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(input),
            new Temporalio.Client.WorkflowOptions(
                WorkflowSignalRouter.WorkflowIdFor(jobId),
                WorkflowSignalRouter.TaskQueue));
    }

    /// <summary>
    /// Polls a status-query delegate until the predicate holds.
    /// </summary>
    /// <remarks>
    /// Bounded and explicit rather than a fixed sleep, for the reasons recorded on the equivalent helper
    /// in <c>TaskExecutionWorkflowTests</c>: the status query is the same read path the production
    /// dashboard uses, so polling it exercises real behaviour instead of adding a delay.
    /// <para>
    /// Takes the query as a delegate rather than a handle because the SDK's <c>GetWorkflowHandle</c>
    /// overloads return differently-shaped handle types (<c>WorkflowHandle&lt;T&gt;</c> vs
    /// <c>WorkflowHandle&lt;TResult&gt;</c>) and both can answer this query; the delegate keeps one
    /// helper for both call sites.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Convenience overload: polls a handle's <c>GetTaskStatus</c> query.
    /// </summary>
    /// <remarks>
    /// Two overloads rather than one generic because the SDK's handle types are
    /// <c>WorkflowHandle&lt;TWorkflow&gt;</c> and <c>WorkflowHandle&lt;TWorkflow,TResult&gt;</c>, which
    /// both expose the typed query but share no generic base that carries it.
    /// </remarks>
    private static Task WaitUntilAsync(
        Temporalio.Client.WorkflowHandle<TaskExecutionWorkflow> handle,
        Func<TaskStatusView, bool> predicate,
        int timeoutMs = 20_000) =>
        WaitUntilAsync(() => handle.QueryAsync(w => w.GetTaskStatus()), predicate, timeoutMs);

    private static Task WaitUntilAsync(
        Temporalio.Client.WorkflowHandle<TaskExecutionWorkflow, TaskExecutionResult> handle,
        Func<TaskStatusView, bool> predicate,
        int timeoutMs = 20_000) =>
        WaitUntilAsync(() => handle.QueryAsync(w => w.GetTaskStatus()), predicate, timeoutMs);

    private static async Task WaitUntilAsync(
        Func<Task<TaskStatusView>> query,
        Func<TaskStatusView, bool> predicate,
        int timeoutMs = 20_000)
    {
        TaskStatusView? last = null;
        Exception? lastError = null;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = await query();
                if (predicate(last)) return;
            }
            catch (Exception ex)
            {
                // A query against a workflow whose worker has not picked up its first task throws; that
                // is a "not yet" and the loop should keep going, not a verdict.
                lastError = ex;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException(
            $"Workflow status never satisfied the predicate within {timeoutMs}ms; last observed: " +
            $"'{last?.Status}' (turns={last?.TurnCount}, outcome={last?.LastOutcome})" +
            (lastError is null ? "." : $"; last error: {lastError.Message}"));
    }

    /// <summary>
    /// A running worker plus the shutdown plumbing the SDK requires.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>TaskExecutionWorkflowTests.TestWorker</c>, including the sticky-queue disable, for the
    /// reasons documented there. Duplicated rather than extracted to a shared fixture: the two classes
    /// are in different collections and a cross-collection fixture would serialise them implicitly,
    /// which is the coupling the collection attribute already expresses more honestly.
    /// </remarks>
    private sealed class TestWorker : IAsyncDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private readonly TemporalWorker worker;
        private Task running;

        private TestWorker(TemporalWorker worker, Task running)
        {
            this.worker = worker;
            this.running = running;
        }

        public static TestWorker Start(WorkflowEnvironment env, FakeTurnActivity turns, FakeGitActivity git)
        {
            var options = new TemporalWorkerOptions(WorkflowSignalRouter.TaskQueue)
                .AddActivity(turns.RunAgentCodingTurnAsync)
                .AddActivity(git.MergePullRequestAsync)
                .AddWorkflow<TaskExecutionWorkflow>();

            options.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

            var worker = new TemporalWorker(env.Client, options);

            // Started after construction so the run loop captures this instance's own token.
            var instance = new TestWorker(worker, Task.CompletedTask);
            instance.running = worker.ExecuteAsync(instance.cts.Token);
            return instance;
        }

        /// <summary>Stops polling for new work, letting an in-flight workflow task finish.</summary>
        public async Task StopAsync()
        {
            cts.Cancel();
            try { await running; } catch (OperationCanceledException) { /* the shutdown contract */ }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            worker.Dispose();
            cts.Dispose();
        }
    }

    /// <summary>Scripted turn activity, mirroring the one in <c>TaskExecutionWorkflowTests</c>.</summary>
    private sealed class FakeTurnActivity : ITaskExecutionActivities
    {
        private readonly List<Func<TaskExecutionInput, ChatMessageSignal?, Task<TurnOutcome>>> script = [];
        private readonly List<ChatMessageSignal?> delivered = [];
        private int cursor;

        public int Calls { get; private set; }

        public void Enqueue(TurnOutcome outcome) =>
            script.Add((_, reply) =>
            {
                Calls++;
                delivered.Add(reply);
                return Task.FromResult(outcome);
            });

        /// <summary>
        /// Queues an outcome withheld until <paramref name="gate"/> completes, so "a turn is in flight"
        /// is a fact the test establishes rather than a window it races through.
        /// </summary>
        public void EnqueueGatedBy(TaskCompletionSource gate, TurnOutcome outcome) =>
            script.Add(async (_, reply) =>
            {
                Calls++;
                delivered.Add(reply);
                await gate.Task;
                return outcome;
            });

        public ChatMessageSignal? PendingReplyAt(int turnIndex) =>
            turnIndex < delivered.Count ? delivered[turnIndex] : null;

        [Activity]
        public Task<TurnOutcome> RunAgentCodingTurnAsync(
            TaskExecutionInput input,
            ChatMessageSignal? pendingReply)
        {
            if (cursor >= script.Count)
            {
                // Non-retryable for the reason recorded in TaskExecutionWorkflowTests: an
                // under-supplied script must fail the test in a second, not retry against a 2-hour
                // activity timeout.
                return Task.FromException<TurnOutcome>(new Temporalio.Exceptions.ApplicationFailureException(
                    $"FakeTurnActivity script exhausted after {Calls} call(s); the test queued " +
                    $"{script.Count} outcome(s).",
                    nonRetryable: true,
                    errorType: "script_exhausted"));
            }

            var attempt = script[cursor];

            return attempt(input, pendingReply).ContinueWith(t =>
            {
                // Advance only on success, so a retry re-runs the same entry the way Temporal would.
                if (!t.IsFaulted) cursor++;
                return t;
            }, TaskContinuationOptions.ExecuteSynchronously).Unwrap();
        }
    }

    private sealed class FakeGitActivity : IGitActivities
    {
        public List<string> Calls { get; } = [];

        [Activity]
        public Task<string> MergePullRequestAsync(string prUrl)
        {
            Calls.Add(prUrl);
            return Task.FromResult($"merged {prUrl}");
        }
    }
}
