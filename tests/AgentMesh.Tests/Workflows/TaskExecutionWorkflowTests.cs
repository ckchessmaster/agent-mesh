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
    /// A non-retryable merge rejection halts the task with the reason, rather than crashing the run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The defect this pins.</strong> <c>GitActivities.MergePullRequestAsync</c> raises a
    /// rejection — merge conflict, head moved after approval, not mergeable, no PR URL — as a
    /// non-retryable <c>ApplicationFailureException</c>, and its comment states the workflow "catches
    /// [it] and turns [it] into a halted task with the reason intact". It did not: the merge call sits
    /// outside the <c>try/catch (IsTurnHalt)</c> that wraps the turn activity, so the exception escaped
    /// the run and the workflow ended <em>Failed</em> with an unhandled <c>ActivityFailureException</c>.
    /// </para>
    /// <para>
    /// That is not merely untidy. A crashed workflow returns no <see cref="TaskExecutionResult"/>, so
    /// <c>_currentStatus</c> is never set to "Halted: …" and the operator polling the status endpoint
    /// sees the last value written before the crash — <c>"Merging PR"</c> — forever. The one rejection
    /// class that is <em>actionable by a human</em> ("the head moved after your approval, re-review
    /// required") was reported as a stuck merge in progress. The fix is the same halt translation the
    /// turn activity already gets, applied to the merge.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("merge_conflict", "The head branch has conflicts with the base branch.")]
    [InlineData("sha_mismatch", "The head moved after approval; re-review required.")]
    [InlineData("not_mergeable", "GitHub reports this PR cannot be merged.")]
    public async Task Workflow_NonRetryableMergeRejection_HaltsWithReason(
        string errorType, string reason)
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity
        {
            Failure = new Temporalio.Exceptions.ApplicationFailureException(
                reason, nonRetryable: true, errorType: errorType),
        };
        await using var worker = TestWorker.Start(env, turns, git);

        turns.Enqueue(TurnOutcome.AwaitingReview(
            "https://github.com/ckchessmaster/agent-mesh/pull/18", 18, "PR opened for review."));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");

        await handle.SignalAsync(w => w.ReceivePrReviewSignalAsync(
            new PrReviewSignal("sorren", IsApproved: true, "Clean. Ship it.", DateTimeOffset.UtcNow, 18)));

        // The decisive assertion: GetResultAsync returns a result rather than throwing. Before the fix
        // this line raised ActivityFailureException, because a crashed workflow has no result to hand
        // back — which is exactly why the reason was unreachable for whoever was watching.
        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeFalse("a rejected merge is not a completed task");
        result.Summary.Should().Contain(reason,
            "the operator must see which rejection occurred, not that the workflow died");
        result.FinalPrUrl.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/18",
            "the PR still exists and still needs attention; dropping the URL would lose the pointer " +
            "to the thing the human has to fix");

        // And the status query must agree with the result, since the status endpoint is what a
        // dashboard polls. "Merging PR" as a terminal state is the bug being fixed.
        var status = await handle.QueryAsync(w => w.GetTaskStatus());
        status.Status.Should().StartWith("Halted:").And.Contain(reason);
        status.LastOutcome.Should().Be(TurnOutcomeStatus.Failed);

        git.Calls.Should().ContainSingle()
            .Which.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/18");
        turns.Calls.Should().Be(1,
            "a halted task must not start another coding turn after a rejected merge");
    }

    /// <summary>
    /// A <em>transient</em> merge failure is still retried, and still exhausts into a visible failure.
    /// </summary>
    /// <remarks>
    /// The counterpart to the rejection tests, and the reason the fix must be a filtered catch rather
    /// than <c>catch (Exception)</c>. Rate limits and 5xx responses are raised as ordinary exceptions so
    /// the merge activity's <c>RetryPolicy</c> applies; if the halt translation swallowed those, a
    /// GitHub blip would be reported to the operator as a rejected merge and the task would halt
    /// without ever retrying. This asserts the retry actually happens — three attempts, then the
    /// workflow fails visibly, which is the correct outcome for an outage.
    /// </remarks>
    [Fact]
    public async Task Workflow_TransientMergeFailure_IsRetriedAndFailsVisibly()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity
        {
            // Exactly what GitActivities throws for FailureReason == "api_error".
            Failure = new InvalidOperationException(
                "Merge of https://github.com/ckchessmaster/agent-mesh/pull/18 failed transiently " +
                "(api_error): GitHub returned 503."),
        };
        await using var worker = TestWorker.Start(env, turns, git);

        turns.Enqueue(TurnOutcome.AwaitingReview(
            "https://github.com/ckchessmaster/agent-mesh/pull/18", 18, "PR opened for review."));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");

        await handle.SignalAsync(w => w.ReceivePrReviewSignalAsync(
            new PrReviewSignal("sorren", IsApproved: true, "Clean. Ship it.", DateTimeOffset.UtcNow, 18)));

        // No result to await: the workflow is expected to end Failed, which is the point. A catch-all
        // here would return Success=false and make an outage look like a rejection.
        //
        // The type is WorkflowFailedException, not ActivityFailureException. Measured, not assumed: the
        // client wraps the exhausted activity failure one level up when the workflow lets it escape the
        // run, so asserting the inner type would fail for a reason unrelated to the behaviour under test.
        // The 503 is still in the chain, which is what makes the failure diagnosable.
        var act = async () => await handle.GetResultAsync<TaskExecutionResult>();

        // Chain, measured: WorkflowFailedException -> ActivityFailureException("Activity task failed")
        // -> ApplicationFailureException carrying the 503 text. Asserting the outermost type plus the
        // deepest message covers both halves of the claim — the run failed rather than halted, and it
        // failed for the reason the activity reported — without depending on how many wrappers the client
        // happens to add.
        var thrown = (await act.Should().ThrowAsync<Temporalio.Exceptions.WorkflowFailedException>());
        thrown.WithInnerException<Temporalio.Exceptions.ActivityFailureException>();
        thrown.And.Should().Match<Exception>(
            e => Flatten(e).Contains("503"),
            "the operator has to be able to see why the merge died; a bare 'Workflow failed' would make " +
            "a GitHub outage indistinguishable from a bug in the workflow");

        git.Calls.Should().HaveCount(3,
            "MaximumAttempts = 3 on the merge RetryPolicy; a single attempt would mean the transient " +
            "classification was lost and a GitHub blip halted a task that could have recovered");
    }

    /// <summary>
    /// A real workflow cancellation during the merge propagates rather than becoming a halted result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guarantee: an operator stopping a run must end it as <em>Cancelled</em>, not as
    /// "Halted: …". The merge reuses <c>IsTurnHalt</c> rather than writing its own filter, and that
    /// predicate explicitly refuses <c>CanceledFailureException</c> — so the guarantee carries over to
    /// the new call site for free. It still needs a test of its own because the obvious implementation
    /// of the new catch, <c>catch (Exception)</c>, would silently convert a human's stop into an agent
    /// failure, and the merge sits in a branch of the run the turn-side tests cannot reach.
    /// </para>
    /// <para>
    /// <strong>Why this cancels the workflow instead of throwing from the activity.</strong> An earlier
    /// version of this test had the merge activity throw <c>OperationCanceledException</c> and asserted
    /// the workflow ended cancelled. That premise was wrong, and the measurement is worth recording: the
    /// .NET SDK converts an activity-side <c>OperationCanceledException</c> into a <em>non-retryable</em>
    /// <c>ApplicationFailureException</c> before the workflow ever sees it (chain:
    /// <c>ActivityFailureException → ApplicationFailureException("merge activity was cancelled")</c>), so
    /// <c>IsTurnHalt</c> correctly reads it as a halt and the run ends "Halted: …". That is the right
    /// outcome — an activity that abandons itself is a failed activity, not a cancelled workflow — but it
    /// means the test proved nothing about cancellation. A genuine cancellation is a
    /// <c>CanceledFailureException> raised in the workflow's own scope, which only a real
    /// <c>CancelAsync</c> produces.
    /// </para>
    /// <para>
    /// The activity therefore blocks on the SDK's own cancellation signal
    /// (<c>ActivityExecutionContext.Current.CancellationToken</c>) rather than on a delay, so the test has
    /// no timing dependency: the interface deliberately takes no cancellation-token parameter for
    /// serialization reasons, and the execution context is the supported way to observe one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Workflow_CancelledDuringMerge_PropagatesRatherThanHalting()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity { BlockUntilCancelled = true };
        await using var worker = TestWorker.Start(env, turns, git);

        turns.Enqueue(TurnOutcome.AwaitingReview(
            "https://github.com/ckchessmaster/agent-mesh/pull/18", 18, "PR opened for review."));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");

        await handle.SignalAsync(w => w.ReceivePrReviewSignalAsync(
            new PrReviewSignal("sorren", IsApproved: true, "Clean. Ship it.", DateTimeOffset.UtcNow, 18)));

        // The merge is genuinely in flight before the cancel is issued, so the cancellation lands inside
        // the try/catch added for the rejection handling rather than somewhere else in the run.
        await WaitUntilAsync(handle, s => s.Status == "Merging PR");
        await git.Entered.WaitAsync(TimeSpan.FromSeconds(20));

        await handle.CancelAsync();

        var act = async () => await handle.GetResultAsync<TaskExecutionResult>();

        // The client surfaces a cancelled run as WorkflowFailedException wrapping
        // CanceledFailureException("Workflow canceled"). The inner type is the whole assertion: had the
        // new catch swallowed the cancellation, the run would have ended COMPLETED with a
        // TaskExecutionResult and there would be no failure at all to unwrap — and the chain would name
        // an ActivityFailureException instead, which is what a halt looks like.
        (await act.Should().ThrowAsync<Temporalio.Exceptions.WorkflowFailedException>())
            .WithInnerException<Temporalio.Exceptions.CanceledFailureException>()
            .WithMessage("Workflow canceled");

        // The activity was demonstrably in flight when the cancel was issued (Entered gated that above),
        // and the run ended cancelled rather than returning a result. That is the claim.
        //
        // What is deliberately NOT asserted: that the activity itself observed its cancellation token.
        // The merge activity is dispatched with the SDK default ActivityCancellationType.TryCancel, which
        // tells the workflow "your task is cancelled" without waiting for the activity to acknowledge —
        // so GetResultAsync throws while the activity may still be unwinding, and reading a flag it sets
        // in its catch block is a race that loses more often than it wins. Asserting it would have made
        // this test flaky without making it prove anything more: the branch under test is the workflow's
        // catch filter, and the workflow's own terminal state is what that filter decides.

        // And the status must not read as a halt. Asserted after the fact rather than as a live poll:
        // a closed workflow still answers its queries from history, so this is the value the run ended
        // with, not a race against teardown.
        var status = await handle.QueryAsync(w => w.GetTaskStatus());
        status.Status.Should().Be("Merging PR",
            "the last status written before the cancellation is all there is; promoting it to " +
            "'Halted: …' would report an operator's stop as an agent failure");
    }

    /// <summary>
    /// A persona cannot release its own escalation by posting again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The escalation exists to force a decision from somebody else. If the asker could answer it, the
    /// durable pause becomes a no-op that costs a turn instead of a pod — the same self-approval class as
    /// the review-gate bypass fixed elsewhere in this PR, reached from the other direction. The run is
    /// started with <c>PersonaId: "rook"</c> and <c>AgentId: "driver-rook"</c>, so a signal from either id
    /// is the same principal.
    /// </para>
    /// <para>
    /// Asserted as "still parked, and the drop is visible" rather than merely "not resumed": a test that
    /// only waited on the park would also pass if the signal were dropped for an unrelated reason, and an
    /// invisible drop is what makes an operator suspect the router rather than the sender.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("rook", "the persona id the run was started with")]
    [InlineData("ROOK", "casing must not defeat the check; the start endpoint takes whatever was posted")]
    [InlineData("@rook", "a leading @ is how the Cockpit and post_job_message both render a persona")]
    [InlineData("driver-rook", "the driver agent id, which is the same principal and how an MCP caller may identify itself")]
    public async Task SelfSignal_IsDroppedAndCounted(string senderId, string why)
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("operator", "Which auth model do you want?"));
        turns.Enqueue(TurnOutcome.Completed("should never run"));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(senderId, "I'll just answer my own question.", DateTimeOffset.UtcNow, "msg-self")));

        // The drop must be observable, not silent.
        await WaitUntilAsync(handle, s => s.IgnoredSelfSignalCount == 1);

        var parked = await handle.QueryAsync(w => w.GetTaskStatus());
        parked!.PendingChatCount.Should().Be(0,
            $"a self-signal from {senderId} must not even be buffered — {why}");
        parked.Status.Should().StartWith("Waiting on input",
            "the gate stays closed; releasing it on the asker's own message is the defect");

        // Release it properly and confirm the run was intact rather than wedged by the drop.
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("operator", "Use OIDC.", DateTimeOffset.UtcNow, "msg-real")));

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue("dropping a self-signal must not damage the run");
        turns.Calls.Should().Be(2);
    }

    /// <summary>
    /// Somebody else can still release the park — the fix must not seal the gate shut.
    /// </summary>
    /// <remarks>
    /// The mirror of the self-signal test, and the one that keeps the check honest: an over-broad
    /// comparison (say, "drop anything that is not the operator") would pass the test above while
    /// reintroducing the deadlock this change exists to remove.
    /// </remarks>
    [Theory]
    [InlineData("astra")]
    [InlineData("sorren")]
    [InlineData("operator")]
    [InlineData("vane")]
    public async Task ASignalFromAnotherParty_ReleasesThePark(string senderId)
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting(senderId, "Which auth model do you want?"));
        turns.Enqueue(TurnOutcome.Completed("Shipped with the model you picked."));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(senderId, "Use OIDC, not passwords.", DateTimeOffset.UtcNow, "msg-1")));

        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeTrue();
        var status = await handle.QueryAsync(w => w.GetTaskStatus());
        status!.IgnoredSelfSignalCount.Should().Be(0,
            $"{senderId} is not this run's persona, so nothing may be counted against it");
    }

    /// <summary>
    /// A chat signal explicitly targeted at another persona must not release this run's wait condition.
    /// </summary>
    [Fact]
    public async Task SignalAddressedToDifferentPersona_IsIgnoredAndDoesNotWakeParkedWorkflow()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("astra", "Waiting for Astra"));
        turns.Enqueue(TurnOutcome.Completed("Done with work"));

        // SampleInput has PersonaId: "rook"
        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        // Operator mentions @astra
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(
                SenderId: "operator",
                Content: "@astra please review rook's PR",
                Timestamp: DateTimeOffset.UtcNow,
                MessageId: "msg-peer",
                TargetPersonaIds: new[] { "astra" })));

        var parkedStatus = await handle.QueryAsync(w => w.GetTaskStatus());
        parkedStatus!.PendingChatCount.Should().Be(0, "a message addressed to @astra must not be enqueued into @rook's inbox");
        parkedStatus.IgnoredMisaddressedSignalCount.Should().Be(1, "a message addressed to @astra must be counted as misaddressed");
        parkedStatus.Status.Should().StartWith("Waiting on input");
        parkedStatus.PersonaId.Should().Be("rook");

        // Message addressed to operator (e.g. MCP post_job_message(target_persona: "operator")) must not wake this run
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(
                SenderId: "astra",
                Content: "FYI operator, reviewed",
                Timestamp: DateTimeOffset.UtcNow,
                MessageId: "msg-op",
                TargetPersonaIds: new[] { "operator" })));

        parkedStatus = await handle.QueryAsync(w => w.GetTaskStatus());
        parkedStatus!.PendingChatCount.Should().Be(0, "a message addressed to operator must not wake rook's parked workflow");
        parkedStatus.IgnoredMisaddressedSignalCount.Should().Be(2, "a message addressed to operator must also be counted as misaddressed");

        // Now deliver a message addressed to @rook
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(
                SenderId: "astra",
                Content: "Approved, proceed!",
                Timestamp: DateTimeOffset.UtcNow,
                MessageId: "msg-addressed",
                TargetPersonaIds: new[] { "rook" })));

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();
        turns.Calls.Should().Be(2);
    }

    /// <summary>
    /// When a reply is addressed to several personas, one of them this workflow's persona,
    /// it wakes this workflow as long as the sender is NOT this persona itself.
    /// If the sender IS this persona, it is dropped by the self-check and does not wake.
    /// </summary>
    [Fact]
    public async Task SignalAddressedToMultiplePersonas_WakesWhenAddressedToSelf_UnlessSenderIsSelf()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("astra", "Waiting for input"));
        turns.Enqueue(TurnOutcome.Completed("Finished task after multi-target wake"));

        // SampleInput has PersonaId: "rook"
        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        // 1. Reply addressed to multiple personas that do NOT include "rook" -> ignored as misaddressed
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(
                SenderId: "operator",
                Content: "@astra @soren please check this out",
                Timestamp: DateTimeOffset.UtcNow,
                MessageId: "msg-multi-peer",
                TargetPersonaIds: new[] { "astra", "soren" })));

        var status = await handle.QueryAsync(w => w.GetTaskStatus());
        status!.PendingChatCount.Should().Be(0);
        status.IgnoredMisaddressedSignalCount.Should().Be(1);
        status.Status.Should().StartWith("Waiting on input");

        // 2. Reply addressed to "rook" AND "astra", but sent BY "rook" -> ignored by self-check (never wake self)
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(
                SenderId: "rook",
                Content: "@rook @astra here is my self note",
                Timestamp: DateTimeOffset.UtcNow,
                MessageId: "msg-multi-self",
                TargetPersonaIds: new[] { "rook", "astra" })));

        status = await handle.QueryAsync(w => w.GetTaskStatus());
        status!.PendingChatCount.Should().Be(0);
        status.IgnoredSelfSignalCount.Should().Be(1, "messages sent by the driving persona itself must not wake it");
        status.Status.Should().StartWith("Waiting on input");

        // 3. Reply addressed to "rook" AND "astra", sent by a peer or operator -> wakes this workflow!
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal(
                SenderId: "astra",
                Content: "@rook @astra I reviewed and approved both parts",
                Timestamp: DateTimeOffset.UtcNow,
                MessageId: "msg-multi-valid",
                TargetPersonaIds: new[] { "rook", "astra" })));

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();
        turns.Calls.Should().Be(2);
    }

    /// <summary>
    /// A run started without a persona id cannot self-check, and must not drop everything.
    /// </summary>
    /// <remarks>
    /// <c>PersonaId</c> is optional on <see cref="TaskExecutionInput"/>. A null-normalizing bug here would
    /// fail closed — every signal treated as self-sent — which would look identical to the deadlock this
    /// feature fixes, and would be reported as "the router is broken" rather than as a null check.
    /// </remarks>
    [Fact]
    public async Task NoPersonaOnInput_StillAcceptsSignals()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("astra", "Which auth model?"));
        turns.Enqueue(TurnOutcome.Completed("done"));

        var input = SampleInput with { PersonaId = null, AgentId = null };
        var handle = await env.Client.StartWorkflowAsync(
            (TaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(input),
            new WorkflowOptions($"task-execution-{Guid.NewGuid():N}", Queue));

        await WaitUntilAsync(handle, s => s.Status.StartsWith("Waiting on input"));

        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "OIDC.", DateTimeOffset.UtcNow, "msg-1")));

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();
    }

    /// <summary>
    /// A workflow that has no PR URL cannot be merged, and halts rather than crashing.
    /// </summary>
    /// <remarks>
    /// Exercises the <c>no_pr_url</c> rejection class end to end. The activity throws non-retryable for it,
    /// so without the merge-side halt translation this would crash the run exactly like a conflict did.
    /// </remarks>
    [Fact]
    public async Task Workflow_MergeWithoutAPrUrl_HaltsRatherThanCrashing()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity
        {
            Failure = new Temporalio.Exceptions.ApplicationFailureException(
                "Merge requested without a pull request URL.", nonRetryable: true, errorType: "no_pr_url"),
        };
        await using var worker = TestWorker.Start(env, turns, git);

        // AwaitingReview with no URL: reachable in production, because the ledger row can carry the flag
        // without a link if the push succeeded and the gh call did not.
        turns.Enqueue(new TurnOutcome(TurnOutcomeStatus.PrOpenedAwaitingReview, "PR up.", PrNumber: null));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");

        await handle.SignalAsync(w => w.ReceivePrReviewSignalAsync(
            new PrReviewSignal("sorren", IsApproved: true, "Ship it.", DateTimeOffset.UtcNow, 18)));

        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeFalse();
        result.Summary.Should().Contain("pull request URL");
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

    /// <summary>
    /// A non-retryable merge failure halts the task cleanly with status and structured result rather than crashing with an unhandled exception.
    /// </summary>
    [Fact]
    public async Task Workflow_NonRetryableMergeFailure_HaltsCleanlyAndReportsReason()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        var git = new FakeGitActivity
        {
            FailureToThrow = new Temporalio.Exceptions.ApplicationFailureException(
                "Merge conflict detected on https://github.com/o/r/pull/18",
                nonRetryable: true,
                errorType: "merge_conflict")
        };
        await using var worker = TestWorker.Start(env, turns, git);

        turns.Enqueue(TurnOutcome.AwaitingReview("https://github.com/o/r/pull/18", 18, "Review ready"));

        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status == "Awaiting PR Review");

        await handle.SignalAsync(w => w.ReceivePrReviewSignalAsync(
            new PrReviewSignal("sorren", IsApproved: true, "Approved", DateTimeOffset.UtcNow, 18)));

        var result = await handle.GetResultAsync<TaskExecutionResult>();

        result.Success.Should().BeFalse();
        result.Summary.Should().Contain("Merge conflict detected");
        git.Calls.Should().ContainSingle();

        var finalStatus = await handle.QueryAsync(w => w.GetTaskStatus());
        finalStatus.Status.Should().StartWith("Halted: ").And.Contain("Merge conflict detected");
    }

    /// <summary>
    /// A self-message from the workflow's own persona is ignored and does not unblock an AwaitingInput gate.
    /// </summary>
    [Fact]
    public async Task Workflow_SelfChatMessage_IsIgnoredAndDoesNotUnblockAwaitingInput()
    {
        await using var env = await NewEnvironmentAsync();
        var turns = new FakeTurnActivity();
        await using var worker = TestWorker.Start(env, turns, new FakeGitActivity());

        turns.Enqueue(TurnOutcome.Awaiting("astra", "Waiting on Astra"));
        turns.Enqueue(TurnOutcome.Completed("Finished after response"));

        // SampleInput has PersonaId: "rook"
        var handle = await StartWorkflowAsync(env);
        await WaitUntilAsync(handle, s => s.Status == "Waiting on input from astra");

        // Send a self-message authored by "rook"
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("rook", "My own notes", DateTimeOffset.UtcNow, "self-1")));

        // Give time for any potential handling
        await Task.Delay(2000);

        // Status should still be parked on Astra
        var status = await handle.QueryAsync(w => w.GetTaskStatus());
        status.Status.Should().Be("Waiting on input from astra");
        turns.Calls.Should().Be(1, "self-message must not release the wait gate");

        // Now send reply from Astra
        await handle.SignalAsync(w => w.ReceiveChatMessageAsync(
            new ChatMessageSignal("astra", "Here is the guidance.", DateTimeOffset.UtcNow, "astra-1")));

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();
        turns.Calls.Should().Be(2, "reply from Astra unblocks the turn");
        turns.PendingReplyAt(1)!.SenderId.Should().Be("astra");
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

    /// <summary>
    /// Every message in an exception chain, joined for a substring check.
    /// </summary>
    /// <remarks>
    /// Temporal's client nests failures arbitrarily deep and the depth is not part of the contract, so a
    /// fixed <c>InnerException.InnerException</c> walk would break on an SDK bump that adds or removes a
    /// wrapper. Walking to the root keeps the assertion about the information rather than about the shape.
    /// </remarks>
    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            parts.Add(ex.Message);
        }

        return string.Join(" | ", parts);
    }

    private static Task<WorkflowEnvironment> NewEnvironmentAsync() =>
        WorkflowEnvironment.StartTimeSkippingAsync();

    private static Task<WorkflowHandle<TaskExecutionWorkflow, TaskExecutionResult>> StartWorkflowAsync(
        WorkflowEnvironment env,
        TaskExecutionInput? input = null) =>
        env.Client.StartWorkflowAsync(
            (TaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(input ?? SampleInput),
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

    /// <summary>A one-shot "the activity was reached" gate.</summary>
    private sealed class TaskEnteredMarker
    {
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Mark() => entered.TrySetResult();

        public async Task WaitAsync(TimeSpan timeout)
        {
            var done = await Task.WhenAny(entered.Task, Task.Delay(timeout));
            if (done != entered.Task)
            {
                throw new InvalidOperationException(
                    $"the merge activity was never entered within {timeout.TotalSeconds}s, so the " +
                    "cancellation would have landed somewhere else in the run and this test would " +
                    "prove nothing about the merge path");
            }
        }
    }

    private sealed class FakeGitActivity : IGitActivities
    {
        public List<string> Calls { get; } = [];
        public Exception? FailureToThrow { get; set; }

        /// <summary>
        /// When set, the merge fails with this instead of succeeding.
        /// </summary>
        /// <remarks>
        /// Holds an exception rather than a bool because the two failure classes must be distinguishable
        /// in the test: the production activity raises a <em>non-retryable</em>
        /// <see cref="Temporalio.Exceptions.ApplicationFailureException"/> for a rejection (conflict,
        /// head moved) and an ordinary exception for a transient one (rate limit, 5xx) so the workflow's
        /// <c>RetryPolicy</c> applies. A single "fail" flag would let the test pass against an
        /// implementation that got the classification wrong.
        /// </remarks>
        public Exception? Failure { get; set; }

        /// <summary>
        /// When true, the merge stays in flight until the SDK cancels it.
        /// </summary>
        /// <remarks>
        /// A mode on this fake rather than a separate class so the worker harness keeps one concrete
        /// activity type to register — <c>TestWorker.Start</c> takes <c>FakeGitActivity</c> and its
        /// <c>AddActivity</c> binding is a method group on that type.
        /// <para>
        /// Waits on <see cref="Temporalio.Activities.ActivityExecutionContext.CancellationToken"/> rather
        /// than on a delay: the interface takes no token parameter (the serialization constraint
        /// documented on <see cref="ITaskExecutionActivities.RunAgentCodingTurnAsync"/>) and the execution
        /// context is the supported way to reach one. It also means the test asserts the cancellation
        /// <em>arrived</em> instead of assuming it did — <see cref="Cancelled"/> is only true if the token
        /// actually fired.
        /// </para>
        /// </remarks>
        public bool BlockUntilCancelled { get; set; }

        /// <summary>Signalled as soon as the merge is entered, so a test can cancel mid-merge.</summary>
        public TaskEnteredMarker Entered { get; } = new();

        [Activity]
        public async Task<string> MergePullRequestAsync(string prUrl)
        {
            Calls.Add(prUrl);

            if (BlockUntilCancelled)
            {
                Entered.Mark();
                var token = Temporalio.Activities.ActivityExecutionContext.HasCurrent
                    ? Temporalio.Activities.ActivityExecutionContext.Current.CancellationToken
                    : CancellationToken.None;
                try
                {
                    // No timeout of its own: the test's cancel is the only thing that ends this. If it
                    // never arrives, the workflow's 5-minute StartToCloseTimeout surfaces it — and under
                    // the time-skipping server that means a visible failure rather than a silent pass.
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return $"merged {prUrl}";
                }
                catch (OperationCanceledException)
                {
                    // The SDK surfaces this to the workflow as CanceledFailureException, which is the
                    // exception type IsTurnHalt refuses. Re-thrown unchanged: an activity that abandons
                    // itself must not be made to look like it succeeded.
                    throw;
                }
            }

            // Mirrors GitActivities.MergePullRequestAsync: a rejection is raised, not returned, because
            // the activity's contract is "return the merge message or throw".
            var failure = Failure ?? FailureToThrow;
            if (failure is not null)
            {
                throw failure;
            }

            return $"merged {prUrl}";
        }
    }


}
