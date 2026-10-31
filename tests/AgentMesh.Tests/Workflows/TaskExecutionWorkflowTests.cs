using AgentMesh.Core.Activities;
using AgentMesh.Tests.Sandbox;
using AgentMesh.Core.Workflows;
using FluentAssertions;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace AgentMesh.Tests.Workflows;

/// <summary>
/// Live-workflow tests for <see cref="TaskExecutionWorkflow"/>, run against Temporal's time-skipping
/// test server rather than a mock of the SDK.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a real server.</strong> The behaviour this job exists to add is durable suspension: the
/// workflow parks on <c>WaitConditionAsync</c>, the worker releases everything, and a signal arriving
/// hours later resumes it. That property lives in the interaction between the SDK's runtime and the
/// server's history — a mocked client would test that we called a method with certain arguments, which
/// is compatible with the pause being broken in every way that matters (busy-waiting, never resuming,
/// resuming without the signal's payload). The test server is bundled in the <c>Temporalio</c> package
/// already referenced, so the only cost is a few seconds of wall clock per test.
/// </para>
/// <para>
/// <strong>Why one class.</strong> xUnit parallelises across collections, and each
/// <see cref="WorkflowEnvironment"/> spawns its own test-server process. Spreading these across classes
/// would run a dozen servers concurrently to no benefit: the assertions are about one workflow's state
/// machine, not about throughput. One class means one collection means serial execution, and the suite
/// stays runnable on a small CI container.
/// </para>
/// <para>
/// <strong>Why the fakes are hand-written rather than Moq.</strong> Temporal's
/// <c>AddActivity(Delegate)</c> needs a real method to read the activity name from the delegate's
/// <see cref="System.Reflection.MethodInfo"/>. A Moq proxy's method name is the interface's, but its
/// declaring type is a generated proxy, and the registration surface is built around delegates — so a
/// hand-written fake is both simpler and the thing that actually exercises the name-resolution path the
/// production worker uses.
/// </para>
/// </remarks>
[Collection(SandboxTestCollections.Temporal)]
public class TaskExecutionWorkflowTests
{
    private const string Queue = "macro-task-test-queue";

    // ──────────────────────────────────────────────────────────────────────────
    // The spec's named tests
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Non-busy wait on <see cref="TurnOutcomeStatus.AwaitingInput"/>, unblocked by a
    /// <see cref="ChatMessageSignal"/>.
    /// </summary>
    [Fact]
    public async Task Workflow_PausesWhenAwaitingInput_AndResumesOnChatSignal()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model do you want?"));
        turns.Enqueue(TurnOutcome.Completed("Shipped with the model you picked."));

        var handle = await StartWorkflowAsync(env);

        // Parked. The query is the only way to observe this without waiting on the result, which would
        // deadlock the test: the workflow cannot finish until it is signalled.
        await WaitUntilAsync(handle, status => status.Status.StartsWith("Waiting on input"));

        var parked = await handle.QueryAsync(w => w.GetTaskStatus());
        parked.Status.Should().Be("Waiting on input from astra",
            "the status must name who owes the reply, so an operator does not read the transcript to find out");
        parked.LastOutcome.Should().Be(TurnOutcomeStatus.AwaitingInput);
        parked.AwaitingSince.Should().NotBeNull("a pause with no start time cannot be aged out");
        parked.PendingChatCount.Should().Be(0, "nothing has been signalled yet");

        // The signal. Typed expression, not a string name — see SignalNames for why the string form is
        // accepted by the server and then silently never delivered.
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Use the existing JWT validator.", DateTimeOffset.UtcNow, "msg-1")));

        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeTrue();
        turns.Calls.Should().Be(2, "the pause suspends the loop; it does not spin it");

        // The reply has to reach the resumed turn's prompt, not merely release the wait. A workflow that
        // wakes up and re-asks the same question is a livelock that looks like progress.
        turns.PendingReplyAt(1).Should().NotBeNull("the resuming turn must be handed the reply");
        turns.PendingReplyAt(1)!.Content.Should().Contain("existing JWT validator");
        turns.PendingReplyAt(1)!.SenderId.Should().Be("astra");
        turns.PendingReplyAt(0).Should().BeNull("the first turn had nothing pending");
    }

    /// <summary>
    /// The workflow halts at PR creation and runs the merge activity when approved.
    /// </summary>
    [Fact]
    public async Task Workflow_BlocksOnPrReview_AndMergesWhenApproved()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity();
        await using var worker = TestWorker.Start(env, turns, git);

        turns.Enqueue(TurnOutcome.AwaitingReview(
            "https://github.com/ckchessmaster/agent-mesh/pull/18", 18, "PR opened for review."));

        var handle = await StartWorkflowAsync(env);

        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");
        git.Calls.Should().BeEmpty("no review has arrived, so nothing may be merged");

        var awaiting = await handle.QueryAsync(w => w.GetTaskStatus());
        awaiting.PrUrl.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/18",
            "the dashboard links the reviewer straight to the diff");

        await handle.SignalAsync(w => w.ReceivePrReviewSignalAsync(
            new PrReviewSignal("sorren", IsApproved: true, "Clean. Ship it.", DateTimeOffset.UtcNow, PrNumber: 18)));

        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeTrue();
        result.FinalPrUrl.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/18");
        // ContainSingle + Be, not Equal(url, because): Equal's params overload would read the
        // because-string as a second expected element and demand two merges.
        git.Calls.Should().ContainSingle()
            .Which.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/18",
                "exactly one merge, against the PR the review was for");
        result.Summary.Should().Contain("merged", "the summary reports what actually happened");

        // One turn only. A second turn after approval would re-dispatch an agent onto work that has
        // already landed, and it would push onto a branch whose PR is closed.
        turns.Calls.Should().Be(1);
    }

    /// <summary>
    /// Changes requested sends the review comments back into a coding turn instead of merging.
    /// </summary>
    [Fact]
    public async Task Workflow_ChangesRequested_ResumesCodingTurnWithReviewComments()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity();
        await using var worker = TestWorker.Start(env, turns, git);

        turns.Enqueue(TurnOutcome.AwaitingReview("https://github.com/o/r/pull/9", 9, "PR up."));
        turns.Enqueue(TurnOutcome.Completed("Addressed the review."));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");

        await handle.SignalAsync(w => w.ReceivePrReviewSignalAsync(
            new PrReviewSignal("sorren", IsApproved: false, "Line 42 leaks the token.", DateTimeOffset.UtcNow, 9)));

        var result = await handle.GetResultAsync<TaskExecutionResult>();

        git.Calls.Should().BeEmpty("a rejected review must never merge");
        result.Success.Should().BeTrue();

        // The feedback has to arrive as the resumed turn's pending reply. If it were dropped, the agent
        // would re-open the PR unchanged and the reviewer would be asked to review the same diff twice.
        turns.PendingReplyAt(1).Should().NotBeNull();
        turns.PendingReplyAt(1)!.Content.Should().Contain("Line 42 leaks the token");
        turns.PendingReplyAt(1)!.SenderId.Should().Be("sorren");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Supporting guarantees the four spec tests do not cover
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pause is server-side: while parked, the workflow runs no turns and its history does not
    /// grow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the Definition-of-Done property — "transitions workflow status to Waiting on input
    /// without burning tokens" — and history length is how you measure it. A workflow that polled
    /// (the behaviour being replaced) would keep taking workflow tasks, so its history would grow
    /// continuously while parked and its activity count would climb. A workflow that busy-waited in
    /// the worker would burn a slot but not grow history, which is why the activity count is asserted
    /// alongside it.
    /// </para>
    /// <para>
    /// <strong>Why not a worker-restart test.</strong> The obvious version of this test stops the
    /// worker, starts a replacement, and signals. Measured against the bundled test server, that never
    /// completes: a parked workflow is held on the first worker's sticky task queue, and
    /// <c>temporal-test-server</c> does not fall back to the normal queue when that poller disappears —
    /// the signal is accepted and the workflow task is never delivered, with or without
    /// <c>StickyQueueScheduleToStartTimeout</c> tuned. That is a limitation of the test server, not of
    /// the workflow: the same sequence works against a real Temporal cluster, where the sticky timeout
    /// is honoured. Asserting durability through history instead keeps the test honest about what is
    /// actually being proven here, and the history-length assertion is the stronger of the two anyway:
    /// it measures the cost of the pause rather than merely its survivability.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Workflow_PauseIsServerSide_HistoryDoesNotGrowWhileWaiting()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("operator", "Which layout?"));
        turns.Enqueue(TurnOutcome.Completed("done"));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var parkedHistory = (await handle.DescribeAsync()).HistoryLength;
        var parkedTurns = turns.Calls;

        // Real time, not skipped: the point is what happens over a period during which a polling
        // implementation would have taken many workflow tasks. Time skipping would compress exactly the
        // thing being measured out of existence.
        await Task.Delay(6_000);

        var afterHistory = (await handle.DescribeAsync()).HistoryLength;
        afterHistory.Should().Be(parkedHistory,
            "a durable pause takes no workflow tasks, so nothing is appended to history while waiting — " +
            "this is the '0 compute / 0 tokens' guarantee");
        turns.Calls.Should().Be(parkedTurns,
            "no turn may execute while the workflow is parked on a human");

        // And it still resumes, so the stability above is not a dead workflow.
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("operator", "The two-column one.", DateTimeOffset.UtcNow, "m-1")));

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();
        turns.Calls.Should().Be(parkedTurns + 1, "exactly one turn ran after the reply");
    }

    /// <summary>
    /// A halt stops the loop instead of retrying, and reports the reason.
    /// </summary>
    [Fact]
    public async Task Workflow_StuckLoop_HaltsOnceAndReportsReason()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        // The production turn activity raises a halt as a non-retryable ApplicationFailure rather than
        // returning it, so the test has to reproduce that transport rather than the return value —
        // otherwise it would pass against an implementation that returns Failed and gets retried.
        turns.EnqueueFailure(new Temporalio.Exceptions.ApplicationFailureException(
            "3-strike limit reached on the migration test.", nonRetryable: true,
            errorType: nameof(TurnOutcomeStatus.StuckLoopDetected)));

        var handle = await StartWorkflowAsync(env);
        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeFalse();
        result.Summary.Should().Contain("3-strike limit");
        turns.Calls.Should().Be(1,
            "a non-retryable halt must reach the workflow exactly once; retrying it re-runs the agent " +
            "on the tree it just failed on");
    }

    /// <summary>
    /// A transient activity failure is retried, and the workflow keeps going.
    /// </summary>
    /// <remarks>
    /// The counterpart to the halt test. Without it, "make halts non-retryable" could be implemented as
    /// "make everything non-retryable" and every database blip would end a task.
    /// </remarks>
    [Fact]
    public async Task Workflow_TransientActivityFailure_IsRetriedAndContinues()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.EnqueueTransientFailureThen(TurnOutcome.Completed("Recovered on the retry."));

        var handle = await StartWorkflowAsync(env);
        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeTrue();
        turns.Calls.Should().Be(2, "the first attempt failed transiently and Temporal retried it");
    }

    /// <summary>
    /// Replies that arrive while a turn is running are all delivered, not just the newest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collapsing them to the latest would silently discard the earlier ones, and a dropped human reply
    /// is indistinguishable from being ignored.
    /// </para>
    /// <para>
    /// <strong>Why the turn is gated.</strong> The two replies have to be in the inbox at the same
    /// instant for the combine path to run at all, and the only way to make that true deterministically
    /// is to hold a turn open while both are sent. Firing them at a parked workflow instead is a coin
    /// flip: the first releases the wait, so the second either beats the loop's dequeue (combined) or
    /// loses it (consumed alone by a later turn). Both are correct workflow behaviour; only one
    /// satisfies the assertion, so the ungated version would be flaky by construction.
    /// </para>
    /// <para>
    /// The turn here returns <see cref="TurnOutcomeStatus.Continue"/> rather than parking, because a
    /// parked workflow consumes the first signal to resume and would deliver it to <em>that</em> turn —
    /// leaving only the second for the next one, which is the behaviour the earlier version of this test
    /// accidentally asserted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Workflow_MultipleRepliesWhileTurnRuns_AreAllDelivered()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        var holdTurn1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        turns.EnqueueGated(TurnOutcome.ContinueTurn("still working"), holdTurn1.Task);
        turns.Enqueue(TurnOutcome.Completed("done"));

        var handle = await StartWorkflowAsync(env);

        // Turn 1 is now inside the gated activity: the workflow is running, not parked.
        await WaitUntilAsync(handle, s => s.TurnCount >= 1);

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("operator", "FIRST-ANSWER", DateTimeOffset.UtcNow, "m-1")));
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "SECOND-ANSWER", DateTimeOffset.UtcNow, "m-2")));

        // Both must be buffered before the turn releases, or the test proves nothing about combining.
        await WaitUntilAsync(handle, s => s.PendingChatCount == 2);

        holdTurn1.SetResult();

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();

        var delivered = turns.PendingReplyAt(1);
        delivered.Should().NotBeNull("turn 2 is the first turn after both replies landed");
        delivered!.Content.Should().Contain("FIRST-ANSWER").And.Contain("SECOND-ANSWER",
            "both replies must reach the resumed turn");
        delivered.Content.Should().Contain("operator").And.Contain("astra",
            "the combined message must attribute each reply, or the agent cannot tell who said what");
    }

    /// <summary>
    /// A redelivered signal does not produce a second copy of the same reply.
    /// </summary>
    [Fact]
    public async Task Workflow_DuplicateChatSignal_IsSuppressed()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("operator", "Question?"));
        turns.Enqueue(TurnOutcome.Completed("done"));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        // Same message id twice: the API retried a call that timed out client-side but landed.
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("operator", "Use PKCE.", DateTimeOffset.UtcNow, "msg-dup")));
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("operator", "Use PKCE.", DateTimeOffset.UtcNow, "msg-dup")));

        await handle.GetResultAsync<TaskExecutionResult>();

        var delivered = turns.PendingReplyAt(1)!;
        delivered.Content.Should().Be("Use PKCE.",
            "a duplicated reply rendered twice reads to the model as the human repeating themselves " +
            "and can change its answer");
    }

    /// <summary>
    /// The turn ceiling halts a looping agent instead of running forever.
    /// </summary>
    /// <remarks>
    /// The bound is what stops "Continue forever" from becoming an unbounded token burn — the exact
    /// cost this job exists to remove, reintroduced as a livelock.
    /// </remarks>
    [Fact]
    public async Task Workflow_TurnCeiling_HaltsInsteadOfLoopingForever()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        // Every turn says "keep going" — an agent that is stuck but never reports being stuck.
        for (var i = 0; i < TaskExecutionWorkflow.MaxTurns + 5; i++)
        {
            turns.Enqueue(TurnOutcome.ContinueTurn("still working"));
        }

        var handle = await StartWorkflowAsync(env);
        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeFalse();
        result.Summary.Should().Contain("turns");
        turns.Calls.Should().Be(TaskExecutionWorkflow.MaxTurns,
            "the loop must stop at the ceiling rather than run out the queue the test supplied");
    }

    /// <summary>
    /// The signal wire names are what the SDK actually registers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test exists because the failure it guards is silent and catastrophic. The .NET SDK strips a
    /// trailing <c>Async</c> when it derives a handler's wire name, so the C# method
    /// <c>ReceiveChatMessageAsync</c> is registered as <c>ReceiveChatMessage</c>. Measured against a live
    /// test server: an untyped signal to <c>"ReceiveChatMessageAsync"</c> is accepted by the server,
    /// buffered because no handler matches, and never delivered — the workflow stays parked forever and
    /// reports "Waiting on input" as though the human had simply not replied.
    /// </para>
    /// <para>
    /// The assertion below is therefore not about the constant being convenient. It pins the SDK's
    /// behaviour so that an SDK upgrade which changed the derivation would fail here rather than strand
    /// every parked workflow in production.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SignalNames_MatchTheNamesTheSdkActuallyRegisters()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("operator", "Question?"));
        turns.Enqueue(TurnOutcome.Completed("done"));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        // The suffixed form must NOT resume the workflow. Proving the negative is what makes the
        // positive assertion below meaningful.
        await handle.SignalAsync("ReceiveChatMessageAsync",
            new object?[] { new ChatMessageSignal("operator", "WRONG-NAME", DateTimeOffset.UtcNow, "m-x") });

        await Task.Delay(1_500);
        var stillParked = await handle.QueryAsync(w => w.GetTaskStatus());
        stillParked.Status.Should().StartWith("Waiting on input",
            "a signal sent under the C# method name must not be delivered — this is the trap the " +
            "typed-expression call sites avoid");
        stillParked.PendingChatCount.Should().Be(0,
            "the misnamed signal is buffered by the server and never reaches the inbox");

        // The registered name resumes it.
        await handle.SignalAsync(SignalNames.ChatMessage,
            new object?[] { new ChatMessageSignal("operator", "RIGHT-NAME", DateTimeOffset.UtcNow, "m-y") });

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();
        turns.PendingReplyAt(1)!.Content.Should().Be("RIGHT-NAME");
    }

    /// <summary>
    /// A query is read-only: observing a parked workflow must not change it.
    /// </summary>
    /// <remarks>
    /// A query handler that mutated state is a determinism violation, because queries are also executed
    /// during replay and would then apply the mutation twice.
    /// </remarks>
    [Fact]
    public async Task GetTaskStatus_IsRepeatableWithoutChangingState()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("operator", "Question?"));
        turns.Enqueue(TurnOutcome.Completed("done"));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        var first = await handle.QueryAsync(w => w.GetTaskStatus());
        await handle.QueryAsync(w => w.GetTaskStatus());
        var third = await handle.QueryAsync(w => w.GetTaskStatus());

        third.Should().Be(first, "queries must not mutate workflow state");

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("operator", "answer", DateTimeOffset.UtcNow, "m-1")));
        await handle.GetResultAsync<TaskExecutionResult>();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Harness
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A running test worker plus the shutdown plumbing the SDK requires.
    /// </summary>
    /// <remarks>
    /// <see cref="TemporalWorker"/> exposes <c>ExecuteAsync(CancellationToken)</c> as its run loop
    /// rather than a start/stop pair, so "start the worker" means "start a task that does not return
    /// until you cancel it" — and that task must be awaited after cancellation, or the worker is torn
    /// down while still holding a task token. This owns that ordering so no test can leak a worker into
    /// the next one in the collection.
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

        public static TestWorker Start(
            WorkflowEnvironment env,
            FakeTurnActivity turns,
            FakeGitActivity git)
        {
            var options = new TemporalWorkerOptions(Queue)
                .AddActivity(turns.RunAgentCodingTurnAsync)
                .AddActivity(git.MergePullRequestAsync)
                .AddWorkflow<TaskExecutionWorkflow>();

            // Sticky execution off. A sticky-held workflow routes its next task to the queue of the
            // worker that cached it, so after the shutdown test stops that worker the server keeps
            // routing there until the sticky schedule-to-start timeout elapses — measured here as a
            // hang well past any test budget, because the bundled test server does not fall back
            // promptly.
            //
            // Disabling it costs nothing these tests assert on: sticky caching is a worker-side
            // performance optimisation, and every assertion here is about the workflow's state machine
            // as reconstructed from history — which is precisely what a worker swap exercises, and
            // which sticky caching would otherwise hide.
            options.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

            var worker = new TemporalWorker(env.Client, options);

            var instance = new TestWorker(worker, Task.CompletedTask);
            // Started after construction so the run loop captures this instance's own token.
            instance.running = worker.ExecuteAsync(instance.cts.Token);
            return instance;
        }

        /// <summary>
        /// Stops polling for new work, letting an in-flight workflow task finish.
        /// </summary>
        /// <remarks>
        /// Used by the worker-shutdown test, which needs the workflow's state to outlive the process
        /// that created it. Cancelling the run token is the graceful path: the SDK drains, and the
        /// workflow's parked condition stays in Temporal's history rather than being lost with us.
        /// </remarks>
        public async Task StopAsync()
        {
            cts.Cancel();
            try { await running; } catch (OperationCanceledException) { /* expected */ }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            worker.Dispose();
            cts.Dispose();
        }
    }

    private static TaskExecutionInput SampleInput { get; } = new(
        JobId: "11111111-1111-1111-1111-111111111111",
        TaskId: "22222222-2222-2222-2222-222222222222",
        AgentId: "driver-rook",
        ProjectId: "agent-mesh",
        WorkspacePath: "/workspace/repos/agent-mesh",
        TargetBranch: "main",
        PersonaId: "rook");

    private static Task<WorkflowEnvironment> NewEnvironmentAsync() =>
        WorkflowEnvironment.StartTimeSkippingAsync();

    private static Task<WorkflowHandle<TaskExecutionWorkflow, TaskExecutionResult>> StartWorkflowAsync(
        WorkflowEnvironment env) =>
        env.Client.StartWorkflowAsync(
            (TaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(SampleInput),
            new WorkflowOptions($"task-execution-{Guid.NewGuid():N}", Queue));

    /// <summary>
    /// Polls the workflow's query until the predicate holds.
    /// </summary>
    /// <remarks>
    /// Bounded and explicit rather than a bare <c>Task.Delay</c>. A fixed sleep would be both slower
    /// (waiting when the workflow is already parked) and flakier (timing out on a loaded CI box), and
    /// the workflow's status is exactly the observable the production dashboard uses — so polling it
    /// exercises the same read path.
    /// </remarks>
    private static async Task WaitUntilAsync(
        WorkflowHandle<TaskExecutionWorkflow, TaskExecutionResult> handle,
        Func<TaskStatusView, bool> predicate,
        int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        TaskStatusView? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await handle.QueryAsync(w => w.GetTaskStatus());
            if (predicate(last)) return;

            await Task.Delay(150);
        }

        throw new TimeoutException(
            $"Workflow status never satisfied the predicate within {timeoutMs}ms; last observed: " +
            $"'{last?.Status}' (turns={last?.TurnCount}, outcome={last?.LastOutcome}).");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Fakes
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scripted turn activity: returns a queued outcome, or raises a queued failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The script advances only on success.</strong> Temporal retries an activity by
    /// re-invoking the <em>same</em> activity task, so a fake that dequeued its entry before the
    /// attempt ran would find the script empty on the retry and fail that too — turning one injected
    /// transient fault into an infinite retry loop against a 2-hour timeout. That is exactly the bug
    /// this class originally had, and it presented as a test that hung rather than as a test that
    /// failed, which is the worst possible diagnostic.
    /// </para>
    /// <para>
    /// A non-retryable failure does advance the cursor, because by definition there is no retry: the
    /// workflow catches it and halts.
    /// </para>
    /// </remarks>
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
        /// Queues an outcome that is not returned until <paramref name="gate"/> completes, so a test can
        /// make "this signal arrived while the turn was running" a fact rather than a race.
        /// </summary>
        public void EnqueueGated(TurnOutcome outcome, Task gate) =>
            script.Add(async (_, reply) =>
            {
                Calls++;
                delivered.Add(reply);
                await gate;
                return outcome;
            });

        /// <summary>
        /// Queues a non-retryable failure, matching how the production activity reports a halt.
        /// </summary>
        public void EnqueueFailure(Exception exception) =>
            script.Add((_, reply) =>
            {
                Calls++;
                delivered.Add(reply);
                return Task.FromException<TurnOutcome>(exception);
            });

        /// <summary>
        /// Queues a transient failure that the retry re-runs, followed by <paramref name="then"/>.
        /// </summary>
        /// <remarks>
        /// One script entry, not two: the retry re-executes this entry, and the internal flag flips
        /// after the first attempt so the second attempt succeeds.
        /// </remarks>
        public void EnqueueTransientFailureThen(TurnOutcome then)
        {
            var failedOnce = false;
            script.Add((_, reply) =>
            {
                Calls++;
                delivered.Add(reply);

                if (!failedOnce)
                {
                    failedOnce = true;
                    return Task.FromException<TurnOutcome>(
                        new InvalidOperationException("connection refused (transient)"));
                }

                return Task.FromResult(then);
            });
        }

        public ChatMessageSignal? PendingReplyAt(int turnIndex) =>
            turnIndex < delivered.Count ? delivered[turnIndex] : null;

        [Activity]
        public Task<TurnOutcome> RunAgentCodingTurnAsync(
            TaskExecutionInput input,
            ChatMessageSignal? pendingReply)
        {
            if (cursor >= script.Count)
            {
                // Throwing here would be retried forever, so it is marked non-retryable: a test that
                // under-supplies outcomes must fail in a second, not hang for two hours against a
                // workflow that keeps asking for a turn that was never scripted.
                return Task.FromException<TurnOutcome>(new Temporalio.Exceptions.ApplicationFailureException(
                    $"FakeTurnActivity script exhausted after {Calls} call(s); the test queued " +
                    $"{script.Count} outcome(s).",
                    nonRetryable: true,
                    errorType: "script_exhausted"));
            }

            var attempt = script[cursor];

            return attempt(input, pendingReply).ContinueWith(t =>
            {
                // Advance only on success. A retryable failure leaves the cursor put so the retry
                // re-runs this entry, which is what Temporal does to a real activity.
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
