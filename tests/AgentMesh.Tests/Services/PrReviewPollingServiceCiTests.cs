using AgentMesh.Api.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The poller's CI rules: which build results send a persona back to fix the code, which stay silent,
/// and which must never be allowed to fire a second time on the same commit.
/// </summary>
/// <remarks>
/// These cases are the whole safety argument for letting a machine review a pull request. A CI verdict
/// travels the same <c>PrReviewSignal</c> path a human's does, so every false positive costs a full
/// remediation round — a persona editing files to fix a build that was never broken — and every false
/// negative is the bug this feature exists to fix. The fixtures therefore centre on the two states the
/// poller must never confuse: a check that failed, and a check that has not finished.
/// </remarks>
public class PrReviewPollingServiceCiTests
{
    private const string Head = "ABCDEF1234567890abcdef1234567890abcdef12";
    private const string HeadLower = "abcdef1234567890abcdef1234567890abcdef12";
    private const string PrUrl = "https://github.com/ckchessmaster/agent-mesh/pull/93";

    private static readonly DateTimeOffset Parked = new(2026, 11, 17, 18, 17, 43, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Parked.AddMinutes(4);
    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan Skew = PrReviewPollingService.ParkSkewTolerance;

    private static GitHubCiCheck Run(
        string name,
        string status,
        string? conclusion = null,
        DateTimeOffset? completedAt = null,
        string? url = null) =>
        new(GitHubCiCheckKind.CheckRun, name, status, conclusion, conclusion ?? status,
            url ?? $"https://github.com/ckchessmaster/agent-mesh/actions/runs/1/{name.Replace(' ', '-').ToLowerInvariant()}",
            completedAt ?? Parked.AddMinutes(4));

    private static GitHubCiCheck Status(
        string context,
        string state,
        DateTimeOffset? updatedAt = null) =>
        new(GitHubCiCheckKind.CommitStatus, context, state, null, state, null,
            state == "pending" ? null : updatedAt ?? Parked.AddMinutes(4));

    private static GitHubCiSnapshot Ci(params GitHubCiCheck[] checks) => new(true, Head, checks);

    private static PrGateVerdict? Evaluate(GitHubCiSnapshot snapshot) =>
        PrReviewPollingService.EvaluateCiFailures(snapshot, PrUrl, Parked, Now, Lookback, Skew);

    // ── What counts as a failure ────────────────────────────────────────────────────

    [Fact]
    public void FailingCheckRunOnTheHeadRequestsChangesUnderTheCiReviewer()
    {
        var verdict = Evaluate(Ci(Run(".NET 10 Solution Build & Test", "completed", "failure")));

        verdict.Should().NotBeNull();
        verdict!.IsApproved.Should().BeFalse();
        verdict.ReviewerId.Should().Be(PrReviewPollingService.CiReviewerId);
        verdict.CiHeadSha.Should().Be(HeadLower);
        verdict.ApprovedHeadSha.Should().BeNull();
        verdict.ReviewId.Should().Be(0);
        verdict.Comments.Should().Contain(".NET 10 Solution Build & Test");
    }

    [Fact]
    public void FailingCommitStatusOnTheHeadIsReportedToo()
    {
        var verdict = Evaluate(Ci(Status("codecoss/patch", "failure")));

        verdict.Should().NotBeNull();
        verdict!.Comments.Should().Contain("codecoss/patch");
        verdict.ReviewerId.Should().Be(PrReviewPollingService.CiReviewerId);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("neutral")]
    [InlineData("skipped")]
    [InlineData("cancelled")]
    [InlineData("timed_out")]
    [InlineData("action_required")]
    [InlineData("stale")]
    public void OtherNonGreenEndingsAreNotCodeFailures(string conclusion)
    {
        var verdict = Evaluate(Ci(Run("Helm & Docker Manifest Validation", "completed", conclusion)));

        // Each of these says more about the runner, or about a human who stopped a run, than about the
        // diff. Acting on them sends a persona to rewrite code that is probably correct.
        verdict.Should().BeNull();
        PrReviewPollingService.CiIsRed(Ci(Run("any", "completed", conclusion))).Should().BeFalse();
    }

    [Fact]
    public void ACommitStatusThatErroredIsNotAToldTheAuthorToFixTheirCodeFailure()
    {
        // `error` from a legacy publisher usually means the integration itself broke.
        Evaluate(Ci(Status("some-integration", "error"))).Should().BeNull();
    }

    // ── What is not yet a failure ───────────────────────────────────────────────────

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    [InlineData("requested")]
    public void ACheckThatHasNotFinishedIsNeverAVerdict(string status)
    {
        var snapshot = Ci(Run("React 19 Cockpit Build & Test", status));

        // CI is almost always mid-flight at the instant a gate parks. Treating "not done" as "broken"
        // would fire a remediation round at a commit nobody has finished testing.
        Evaluate(snapshot).Should().BeNull();
        PrReviewPollingService.CiIsRed(snapshot).Should().BeFalse();
    }

    [Fact]
    public void OneFinishedFailureAmongStillRunningChecksIsStillAFailure()
    {
        var verdict = Evaluate(Ci(
            Run(".NET 10 Solution Build & Test", "completed", "failure"),
            Run("Helm & Docker Manifest Validation", "in_progress")));

        verdict.Should().NotBeNull();
        // The author is told what has not finished, so it does not conclude the suite is done and
        // re-request review on a commit that is only half tested.
        verdict!.Comments.Should().Contain("Still running on this commit: Helm & Docker Manifest Validation");
    }

    [Fact]
    public void AGreenSuiteSaysNothingBecauseCiApprovesNothing()
    {
        var snapshot = Ci(
            Run("React 19 Cockpit Build & Test", "completed", "success"),
            Run("Helm & Docker Manifest Validation", "completed", "success"));

        // The point of the gate is a second pair of eyes. A machine must not be able to supply them.
        Evaluate(snapshot).Should().BeNull();
        PrReviewPollingService.CiIsRed(snapshot).Should().BeFalse();
    }

    // ── When a failure is this park's business ──────────────────────────────────────

    [Fact]
    public void AFailureThatLandsJustBeforeTheParkIsStillThisParksBusiness()
    {
        // The common shape in practice: CI goes red while the persona is writing its own summary, and
        // the gate parks a minute later. A strict "after the park" rule would miss the case the whole
        // feature exists for.
        var verdict = Evaluate(Ci(Run(".NET 10 Solution Build & Test", "completed", "failure",
            completedAt: Parked.AddMinutes(-2))));

        verdict.Should().NotBeNull();
    }

    [Fact]
    public void AFailureOutsideTheLookbackIsLeftToTheHumansWhoHaveBeenLookingAtIt()
    {
        var ancient = Ci(Run("Old Suite", "completed", "failure", completedAt: Parked.AddHours(-5)));

        // Hours of a human's attention have been on this PR, possibly including a decision to merge
        // through it. That is theirs to overrule, not a poller's.
        Evaluate(ancient).Should().BeNull();
        PrReviewPollingService.CiIsRed(ancient).Should().BeTrue();
    }

    [Fact]
    public void AFailureGitHubCouldNotDateIsReportedRatherThanDropped()
    {
        Evaluate(Ci(Run("Build", "completed", "failure", completedAt: null))).Should().NotBeNull();
    }

    // ── Not twice for the same commit ───────────────────────────────────────────────

    [Fact]
    public void TheHeadCommitIsTheEpochTheLoopPreventionRestsOn()
    {
        var first = Evaluate(Ci(Run("Build", "completed", "failure")));
        var again = Evaluate(Ci(Run("Build", "completed", "failure")));

        // A human reviewer stops talking when satisfied; CI fails a commit forever. Suppression keys on
        // the commit, so a new complaint requires a new push.
        first!.CiHeadSha.Should().Be(again!.CiHeadSha);
        again.Key.Should().Be(first.Key);
    }

    [Fact]
    public void AFailureOnANewCommitIsANewComplaint()
    {
        var pushed = new GitHubCiSnapshot(true, "9999999999999999999999999999999999999999",
            [Run("Build", "completed", "failure")]);

        var verdict = PrReviewPollingService.EvaluateCiFailures(pushed, PrUrl, Parked, Now, Lookback, Skew);

        verdict.Should().NotBeNull();
        verdict!.CiHeadSha.Should().NotBe(HeadLower);
    }

    [Fact]
    public void AnUnreadableCiResultIsNeverTreatedAsGreenOrRed()
    {
        var unreadable = GitHubCiSnapshot.Failure("api_error", "HTTP 502 from the checks API.");

        // A network blip must not start a remediation round, and must not be reported as a passing
        // build to whatever reads the card next.
        PrReviewPollingService.EvaluateCiFailures(unreadable, PrUrl, Parked, Now, Lookback, Skew)
            .Should().BeNull();
        PrReviewPollingService.CiIsRed(unreadable).Should().BeFalse();
    }

    [Fact]
    public void ASnapshotWithoutAHeadCommitSaysNothing()
    {
        var headless = new GitHubCiSnapshot(true, null, [Run("Build", "completed", "failure")]);

        PrReviewPollingService.EvaluateCiFailures(headless, PrUrl, Parked, Now, Lookback, Skew)
            .Should().BeNull();
    }

    [Fact]
    public void ARepositoryWithNoChecksAtAllIsAnAnswerNotAHalt()
    {
        Evaluate(Ci()).Should().BeNull();
    }

    // ── The text the author persona reads ───────────────────────────────────────────

    [Fact]
    public void EachFailingCheckBecomesExactlyOneRemediationTaskForTheAuthor()
    {
        var verdict = Evaluate(Ci(
            Run(".NET 10 Solution Build & Test", "completed", "failure"),
            Run("React 19 Cockpit Build & Test", "completed", "failure"),
            Run("Helm & Docker Manifest Validation", "completed", "success")))!;

        var tasks = JobExecutionWorkflow.BuildReviewRemediationTasks(
            new PrReviewSignal(
                ReviewerId: verdict.ReviewerId,
                IsApproved: verdict.IsApproved,
                ReviewComments: verdict.Comments,
                Timestamp: verdict.At ?? Now,
                PrNumber: 93,
                ApprovedHeadSha: null),
            round: 1);

        // The integration claim, stated plainly: a red build arrives as a request-changes review, and
        // the existing review machinery turns it into work without knowing a machine wrote it down.
        tasks.Should().HaveCount(2);
        tasks.Select(t => t.Title).Should().AllSatisfy(t => t.Should().StartWith("[PR Review R1]"));
        tasks[0].Title.Should().Contain(".NET 10 Solution Build & Test");
    }

    [Fact]
    public void TheNotesTellTheAuthorToPushBecauseTheGateReParksOnTheNewCommit()
    {
        var verdict = Evaluate(Ci(Run("Build", "completed", "failure")))!;

        // A fix committed but not pushed leaves everyone waiting for a build of code GitHub has never
        // seen, so the instruction is part of the feedback rather than implied by it.
        verdict.Comments.Should().Contain("push")
            .And.Contain("re-parks")
            .And.Contain(PrUrl);
    }

    [Fact]
    public void ManyFailuresItemiseTheFirstFewAndNameTheRest()
    {
        var checks = Enumerable.Range(1, 18)
            .Select(i => Run($"Suite {i}", "completed", "failure"))
            .ToArray();

        var comments = Evaluate(Ci(checks))!.Comments!;

        comments.Split('\n').Count(l => l.StartsWith("- Suite")).Should().Be(15);
        comments.Should().Contain("Also failing, not listed individually: Suite 16");
    }
}
