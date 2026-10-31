using AgentMesh.Api.Services;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Workflows;
using AgentMesh.Tests.Sandbox;
using FluentAssertions;
using Temporalio.Activities;
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
    // Wiring
    // ──────────────────────────────────────────────────────────────────────────

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
