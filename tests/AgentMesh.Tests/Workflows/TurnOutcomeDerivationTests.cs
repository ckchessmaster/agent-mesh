using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Workflows;

/// <summary>
/// Deriving a workflow-visible <see cref="TurnOutcome"/> from what the agent actually recorded.
/// </summary>
/// <remarks>
/// <see cref="HttpAgentTurnRunner.ClassifySignals"/> is the decision function: it turns rows in
/// <c>session_messages</c> and <c>job_messages</c> into the status the workflow branches on. Getting it
/// wrong is not a crash, it is a workflow that waits forever for a review that was never requested, or
/// one that never pauses on a question a human was asked. Both look like a stuck agent, so the
/// derivation is worth pinning directly rather than only through an end-to-end run.
/// </remarks>
public class TurnOutcomeDerivationTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static HttpAgentTurnRunner.TurnState State(
        string status = "thinking",
        IReadOnlyList<HttpAgentTurnRunner.ToolCallRecord>? toolCalls = null,
        IReadOnlyList<HttpAgentTurnRunner.JobLedgerRecord>? ledger = null,
        bool scopedToSession = true) => new(
            status,
            toolCalls ?? [],
            ledger ?? [],
            LastActivityAt: T,
            LedgerScopedToSession: scopedToSession);

    private static HttpAgentTurnRunner.ToolCallRecord Call(string tool, string? args = null, int offsetSeconds = 0) =>
        new(tool, args, T.AddSeconds(offsetSeconds));

    private static HttpAgentTurnRunner.JobLedgerRecord Ledger(string? payload, string content = "msg", int offsetSeconds = 0) =>
        new(payload, content, T.AddSeconds(offsetSeconds));

    // ──────────────────────────────────────────────────────────────────────────
    // Pull request
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_APrOpenedThroughTheLedger_PausesForReview()
    {
        // The shape PlatformMcpTools.OpenPullRequestAsync actually persists.
        var state = State(ledger: [Ledger("""{"prNumber":18,"prUrl":"https://github.com/o/r/pull/18","source":"mcp:open_pull_request"}""")]);

        var outcome = HttpAgentTurnRunner.ClassifySignals(state);

        outcome!.Status.Should().Be(TurnOutcomeStatus.PrOpenedAwaitingReview);
        outcome.PrUrl.Should().Be("https://github.com/o/r/pull/18");
        outcome.PrNumber.Should().Be(18);
    }

    /// <summary>
    /// A recorded <c>open_pull_request</c> call with no ledger row still pauses for review.
    /// </summary>
    /// <remarks>
    /// The runner resolves its session id from the optional <c>X-Session-Id</c> header, so a tool call
    /// made without it writes the ledger row with <c>turn_session_id = null</c>. Requiring the ledger
    /// row would mean the workflow never pauses on a PR that exists — the review gate silently absent.
    /// </remarks>
    [Fact]
    public void Classify_APrCallWithNoLedgerRow_StillPausesForReview()
    {
        var state = State(toolCalls: [Call("open_pull_request", """{"title":"feat: x","branch_name":"feat/x"}""")]);

        HttpAgentTurnRunner.ClassifySignals(state)!.Status
            .Should().Be(TurnOutcomeStatus.PrOpenedAwaitingReview);
    }

    /// <summary>
    /// A failed <c>open_pull_request</c> does not open a review gate.
    /// </summary>
    /// <remarks>
    /// The tool records the attempt as a <c>tool_call</c> before returning an error payload (no
    /// credentials, dirty tree). Treating the attempt as the outcome would park the workflow on a review
    /// of a PR that was never opened — a hang with no visible cause.
    /// </remarks>
    [Fact]
    public void Classify_APrCallWithABlankTitle_IsNotTreatedAsOpened()
    {
        var state = State(toolCalls: [Call("open_pull_request", """{"title":"   ","branch_name":"feat/x"}""")]);

        HttpAgentTurnRunner.ClassifySignals(state).Should().BeNull();
    }

    /// <summary>
    /// A driver that prefixes tool names still resolves to the PR gate.
    /// </summary>
    /// <remarks>
    /// Different drivers echo the MCP tool with or without a server prefix. A strict equality check
    /// silently never matches, and the failure is a workflow waiting for a review it never learns was
    /// requested.
    /// </remarks>
    [Theory]
    [InlineData("open_pull_request")]
    [InlineData("OPEN_PULL_REQUEST")]
    [InlineData("  open_pull_request  ")]
    [InlineData("mcp:agentmesh/open_pull_request")]
    [InlineData("agentmesh.open_pull_request")]
    [InlineData("create_pull_request")]
    public void Classify_PullRequestToolIsMatchedAcrossDriverNaming(string toolName) =>
        HttpAgentTurnRunner.IsPullRequestTool(toolName).Should().BeTrue();

    [Theory]
    [InlineData("post_job_message")]
    [InlineData("run_command")]
    [InlineData("save_memory")]
    [InlineData("manage_task")]
    [InlineData("")]
    [InlineData(null)]
    public void IsPullRequestTool_DoesNotMatchOtherTools(string? toolName) =>
        HttpAgentTurnRunner.IsPullRequestTool(toolName).Should().BeFalse();

    // ──────────────────────────────────────────────────────────────────────────
    // Awaiting input
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_AReplyRequestingOne_PausesForInput()
    {
        // The shape PlatformMcpTools.PostJobMessageAsync persists.
        var state = State(ledger: [Ledger("""{"requiresReply":true,"targetPersona":"astra","senderPersona":"rook"}""",
            "Which auth model?")]);

        var outcome = HttpAgentTurnRunner.ClassifySignals(state);

        outcome!.Status.Should().Be(TurnOutcomeStatus.AwaitingInput);
        outcome.TargetPersona.Should().Be("astra",
            "the workflow's status must name who owes the reply, not just that someone does");
        outcome.Message.Should().Contain("Which auth model?");
    }

    [Fact]
    public void Classify_AReplyNotRequested_DoesNotPause()
    {
        var state = State(ledger: [Ledger("""{"requiresReply":false,"targetPersona":"operator"}""", "FYI, done.")]);

        HttpAgentTurnRunner.ClassifySignals(state).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"requiresReply":true}""", true)]
    [InlineData("""{"requiresReply":false}""", false)]
    [InlineData("""{"requiresReply":"true"}""", true)]
    [InlineData("""{"requiresReply":"false"}""", false)]
    [InlineData("""{"requiresReply":1}""", true)]
    [InlineData("""{"requiresReply":0}""", false)]
    [InlineData("""{"other":true}""", false)]
    [InlineData("{}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // Malformed JSON must degrade to "not requested": defaulting to true would park the workflow on a
    // message that never asked for a reply.
    [InlineData("{ not json", false)]
    [InlineData("[1,2,3]", false)]
    public void ReadRequiresReply_HandlesThePayloadShapesThatOccur(string? json, bool expected) =>
        HttpAgentTurnRunner.ReadRequiresReply(json).Should().Be(expected);

    // ──────────────────────────────────────────────────────────────────────────
    // Precedence
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A PR beats a pending question.
    /// </summary>
    /// <remarks>
    /// An agent that opened a PR and then asked a follow-up still needs the PR reviewed; the question is
    /// answered in the PR thread. The reverse precedence would merge past an open question or, worse,
    /// never reach the review gate because a chat pause consumed the turn first.
    /// </remarks>
    [Fact]
    public void Classify_PullRequestTakesPrecedenceOverAPendingQuestion()
    {
        var state = State(
            toolCalls: [Call("open_pull_request", """{"title":"feat: x"}""", offsetSeconds: 1)],
            ledger:
            [
                Ledger("""{"requiresReply":true,"targetPersona":"operator"}""", "Quick question", offsetSeconds: 0),
                Ledger("""{"prNumber":22,"prUrl":"https://github.com/o/r/pull/22"}""", "PR up", offsetSeconds: 2),
            ]);

        HttpAgentTurnRunner.ClassifySignals(state)!.Status
            .Should().Be(TurnOutcomeStatus.PrOpenedAwaitingReview);
    }

    [Fact]
    public void Classify_NoSignals_ReturnsNullSoTheLoopKeepsObserving()
    {
        HttpAgentTurnRunner.ClassifySignals(State()).Should().BeNull();
    }

    /// <summary>
    /// Completion needs both the checklist flag and a finished session.
    /// </summary>
    /// <remarks>
    /// A <c>manage_task completed</c> row alone is not done: the agent may still be mid-turn and could
    /// reopen the task. Marking the workflow complete while the runner is still writing would cut off
    /// work in progress.
    /// </remarks>
    [Fact]
    public void Classify_CompletedTaskWhileStillThinking_IsNotYetComplete()
    {
        var state = State(
            status: "thinking",
            ledger: [Ledger("""{"task":{"status":"completed","isCompleted":true}}""")]);

        HttpAgentTurnRunner.ClassifySignals(state).Should().BeNull();
    }

    [Fact]
    public void ReadTaskCompletedFlag_ReadsBothTheNestedAndFlatShapes()
    {
        HttpAgentTurnRunner.ReadTaskCompletedFlag("""{"task":{"status":"completed"}}""").Should().BeTrue();
        HttpAgentTurnRunner.ReadTaskCompletedFlag("""{"status":"completed"}""").Should().BeTrue();
        HttpAgentTurnRunner.ReadTaskCompletedFlag("""{"task":{"isCompleted":true}}""").Should().BeTrue();
        HttpAgentTurnRunner.ReadTaskCompletedFlag("""{"task":{"status":"in_progress"}}""").Should().BeFalse();
        HttpAgentTurnRunner.ReadTaskCompletedFlag("""{"task":{"status":"awaiting_review"}}""").Should().BeFalse();
        HttpAgentTurnRunner.ReadTaskCompletedFlag("{ bad json").Should().BeFalse();
        HttpAgentTurnRunner.ReadTaskCompletedFlag(null).Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Turn-boundary scoping
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The newest signal of a kind wins, so a turn that resolved an earlier one is not re-triggered.
    /// </summary>
    [Fact]
    public void Classify_UsesTheNewestLedgerRowOfEachKind()
    {
        var state = State(ledger:
        [
            Ledger("""{"requiresReply":true,"targetPersona":"operator"}""", "old question", offsetSeconds: 0),
            Ledger("""{"requiresReply":false,"targetPersona":"operator"}""", "answered, moving on", offsetSeconds: 10),
        ]);

        // The newest row says no reply is wanted, so the earlier request must not re-park the workflow.
        // Without ordering, a job whose first turn asked a question would park every later turn on it.
        HttpAgentTurnRunner.ClassifySignals(state).Should().BeNull();
    }

    [Fact]
    public void ReadPrFacts_PrefersTheLedgerRowOverTheToolArguments()
    {
        var (url, number) = HttpAgentTurnRunner.ReadPrFacts(
            toolArgsJson: """{"pr_url":"https://from/args/1","pr_number":1}""",
            ledgerPayloadJson: """{"prUrl":"https://from/ledger/2","prNumber":2}""");

        // The ledger row is what the tool wrote after GitHub answered; the args are what the model asked
        // for, and the two differ whenever the call was rewritten or rejected.
        url.Should().Be("https://from/ledger/2");
        number.Should().Be(2);
    }

    [Fact]
    public void ReadPrFacts_FallsBackToTheToolArgumentsWhenThereIsNoLedgerRow()
    {
        var (url, number) = HttpAgentTurnRunner.ReadPrFacts(
            toolArgsJson: """{"pr_url":"https://from/args/7","pr_number":7}""",
            ledgerPayloadJson: null);

        url.Should().Be("https://from/args/7");
        number.Should().Be(7);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Turn budget
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The observation ceiling sits inside the workflow's activity timeout.
    /// </summary>
    /// <remarks>
    /// The activity is what does the waiting, so if the runner's own ceiling were longer than the
    /// workflow's <c>StartToCloseTimeout</c>, Temporal would cancel the activity first and the runner's
    /// clean "stuck turn, inspect session X" outcome would never be produced — the operator would get a
    /// Temporal timeout with no pointer to the wedged session.
    /// </remarks>
    [Fact]
    public void MaxTurnDuration_FitsInsideTheWorkflowsActivityTimeout()
    {
        HttpAgentTurnRunner.MaxTurnDuration.Should().BeLessThan(TimeSpan.FromHours(2));
        HttpAgentTurnRunner.MaxTurnDuration.Should().BeGreaterThan(TimeSpan.FromMinutes(30),
            "a legitimate long build-and-test cycle must not be reported as stuck");
    }

    [Fact]
    public void PollInterval_IsShortEnoughToBeInvisibleAndLongEnoughToBeCheap()
    {
        HttpAgentTurnRunner.PollInterval.Should().BeLessThan(TimeSpan.FromSeconds(5));
        HttpAgentTurnRunner.PollInterval.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
