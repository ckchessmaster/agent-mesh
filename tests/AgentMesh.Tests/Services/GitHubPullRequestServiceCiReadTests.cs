using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The two GitHub CI reads behind <see cref="GitHubPullRequestService.ReadPullRequestCiStateAsync"/>:
/// the check-run list and the legacy commit-status list.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is tested in isolation because it is the layer where a wrong answer becomes a wrong verdict.
/// The poller treats a parsed <c>failure</c> as a request-changes review and treats the absence of a
/// failure as nothing to do, so the two shapes that matter are a failed parse reporting nothing (safe:
/// the poller stays silent) and a field being misread as a different field — a <c>pending</c> status
/// read as a completed one, or a rollup <c>state</c> substituting for the individual context that
/// actually broke.
/// </para>
/// <para>
/// A nameless failing check must survive parsing. Dropping it would turn a real red build into a
/// silent green one, which is the one error direction this reader cannot be allowed to make; the
/// poller gives it a placeholder name instead.
/// </para>
/// </remarks>
public class GitHubPullRequestServiceCiReadTests
{
    private const string Head = "70a1842457cc0000000000000000000000000093";

    // ── Check runs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckRunsAreReadWithTheirVerdictStatusLinkAndCompletionTime()
    {
        const string json = """
            {
              "total_count": 2,
              "check_runs": [
                {
                  "id": 1,
                  "name": ".NET 10 Solution Build & Test",
                  "status": "completed",
                  "conclusion": "failure",
                  "html_url": "https://github.com/ckchessmaster/agent-mesh/actions/runs/44146279265/job/120002",
                  "completed_at": "2026-11-17T18:21:45Z"
                },
                {
                  "id": 2,
                  "name": "Cockpit",
                  "status": "completed",
                  "conclusion": "success",
                  "html_url": "https://github.com/ckchessmaster/agent-mesh/actions/runs/44146279265/job/120003",
                  "completed_at": "2026-11-17T18:19:02Z"
                }
              ]
            }
            """;

        var checks = GitHubPullRequestService.TryReadCheckRuns(json);

        checks.Should().HaveCount(2);
        checks[0].Kind.Should().Be(GitHubCiCheckKind.CheckRun);
        checks[0].Name.Should().Be(".NET 10 Solution Build & Test");
        checks[0].Status.Should().Be("completed");
        checks[0].Conclusion.Should().Be("failure");
        checks[0].State.Should().Be("failure");
        checks[0].HtmlUrl.Should().EndWith("/job/120002");
        checks[0].CompletedAt.Should().Be(new DateTimeOffset(2026, 11, 17, 18, 21, 45, TimeSpan.Zero));
        checks[1].Conclusion.Should().Be("success");
    }

    [Fact]
    public void AnUnfinishedCheckRunReportsItsStatusAsItsStateAndNoCompletionTime()
    {
        // This is the shape the gate parks into: CI is mid-flight when the poller first looks. Reporting
        // `state: "in_progress"` is what keeps the poller from reading it as either a failure or a pass.
        const string json = """
            { "check_runs": [ { "name": "Helm", "status": "in_progress", "conclusion": null } ] }
            """;

        var check = GitHubPullRequestService.TryReadCheckRuns(json).Single();

        check.Status.Should().Be("in_progress");
        check.Conclusion.Should().BeNull();
        check.State.Should().Be("in_progress");
        check.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void ACompletedRunWithNoConclusionStillReportsAsCompletedRatherThanAsAFailure()
    {
        // GitHub reports this for runs a suite starts and never resolves. `state` falls back to the
        // status, which is `completed` and is not one of the values the poller acts on.
        const string json = """
            { "check_runs": [ { "name": "docs", "status": "completed" } ] }
            """;

        var check = GitHubPullRequestService.TryReadCheckRuns(json).Single();

        check.Conclusion.Should().BeNull();
        check.State.Should().Be("completed");
    }

    [Fact]
    public void AStatusOfCompletedIsAssumedWhenTheRunOmitsIt()
    {
        // A run with no `status` but a `conclusion` has plainly finished; defaulting it to anything else
        // would leave the parser claiming a dated verdict is still in flight.
        const string json = """
            { "check_runs": [ { "name": "lint", "conclusion": "failure" } ] }
            """;

        var check = GitHubPullRequestService.TryReadCheckRuns(json).Single();

        check.Status.Should().Be("completed");
        check.Conclusion.Should().Be("failure");
    }

    [Fact]
    public void ACheckRunWithNoHtmlUrlFallsBackToItsDetailsUrl()
    {
        // External check suites (BuildKite, Azure Pipelines) report only `details_url`, and the log link
        // is the part of the record that makes the feedback actionable for the author.
        const string json = """
            {
              "check_runs": [
                { "name": "windows", "status": "completed", "conclusion": "failure",
                  "details_url": "https://buildkite.com/org/pipelines/builds/9" }
              ]
            }
            """;

        GitHubPullRequestService.TryReadCheckRuns(json).Single().HtmlUrl
            .Should().Be("https://buildkite.com/org/pipelines/builds/9");
    }

    [Fact]
    public void ANamelessFailingCheckIsKeptSoItCannotQuietlyBecomeAGreenLight()
    {
        const string json = """
            { "check_runs": [ { "status": "completed", "conclusion": "failure" } ] }
            """;

        var check = GitHubPullRequestService.TryReadCheckRuns(json).Single();

        check.Name.Should().BeNull();
        check.Conclusion.Should().Be("failure");
    }

    [Fact]
    public void NonObjectEntriesInTheCheckArrayAreSkippedAndTheRestSurvive()
    {
        // One stray value in the array must not cost the poller every other check on the commit.
        const string json = """
            { "check_runs": [ null, 42, "nope", { "name": "real", "conclusion": "success" } ] }
            """;

        GitHubPullRequestService.TryReadCheckRuns(json).Select(c => c.Name).Should()
            .BeEquivalentTo(new[] { "real" });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"check_runs": "not-an-array"}""")]
    [InlineData("""{"total_count": 3}""")]
    public void AnUnusableCheckRunPayloadParsesAsNoChecksInsteadOfThrowing(string? json)
    {
        // Parsing returns "nothing known" here; the caller's separate rule is that a failed HTTP read is
        // a failed read, not an empty list. Neither path may throw at a poll loop.
        var act = () => GitHubPullRequestService.TryReadCheckRuns(json);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Fact]
    public void DatesAreNormalisedToUtcSoFreshnessComparesAgainstTheParkTime()
    {
        // The checks API mixes offsets across suites. Without the conversion, a check that finished at
        // 13:21-05:00 would parse eleven hours before the park that happened at 18:17Z and be judged too
        // old to report.
        const string json = """
            { "check_runs": [ { "name": "net", "conclusion": "failure",
                                "completed_at": "2026-11-17T13:21:45-05:00" } ] }
            """;

        GitHubPullRequestService.TryReadCheckRuns(json).Single().CompletedAt
            .Should().Be(new DateTimeOffset(2026, 11, 17, 18, 21, 45, TimeSpan.Zero));
    }

    // ── Commit statuses ─────────────────────────────────────────────────────────────

    [Fact]
    public void CommitStatusesUseTheirContextAsTheirNameAndTheirStateAsTheirVerdict()
    {
        const string json = """
            {
              "state": "failure",
              "statuses": [
                { "context": "ci/azure/build", "state": "failure",
                  "target_url": "https://dev.azure.com/org/proj/_build/results?buildId=771",
                  "updated_at": "2026-11-17T18:20:00Z" },
                { "context": "ci/azure/lint", "state": "success",
                  "updated_at": "2026-11-17T18:18:00Z" }
              ]
            }
            """;

        var checks = GitHubPullRequestService.TryReadCommitStatuses(json);

        checks.Should().HaveCount(2);
        checks[0].Kind.Should().Be(GitHubCiCheckKind.CommitStatus);
        checks[0].Name.Should().Be("ci/azure/build");
        checks[0].State.Should().Be("failure");
        checks[0].Status.Should().Be("failure");
        checks[0].Conclusion.Should().BeNull();
        checks[0].HtmlUrl.Should().Contain("buildId=771");
        checks[0].CompletedAt.Should().Be(new DateTimeOffset(2026, 11, 17, 18, 20, 0, TimeSpan.Zero));
        checks[1].State.Should().Be("success");
    }

    [Fact]
    public void TheRollupStateIsIgnoredSoAFailureNamesTheContextThatBroke()
    {
        // The top-level rollup says only "failure" about six contexts. If it parsed into a record the
        // poller would either report a check called nothing or report the wrong one, and the author
        // would have no URL to open.
        const string json = """
            { "state": "failure", "statuses": [ { "context": "ci/x", "state": "success" } ] }
            """;

        var check = GitHubPullRequestService.TryReadCommitStatuses(json).Single();

        check.Name.Should().Be("ci/x");
        check.State.Should().Be("success");
    }

    [Fact]
    public void APendingCommitStatusHasNoCompletionTime()
    {
        const string json = """
            { "statuses": [ { "context": "ci/x", "state": "pending", "updated_at": "2026-11-17T18:20:00Z" } ] }
            """;

        var check = GitHubPullRequestService.TryReadCommitStatuses(json).Single();

        // Cleared deliberately: an in-flight status carrying a timestamp would satisfy the freshness
        // rule the moment anyone looked at it.
        check.CompletedAt.Should().BeNull();
        check.State.Should().Be("pending");
    }

    [Fact]
    public void AStatusWithNoStateIsSkipped()
    {
        const string json = """
            { "statuses": [ { "context": "ci/x" }, { "context": "ci/y", "state": "" },
                            { "context": "ci/z", "state": "success" } ] }
            """;

        GitHubPullRequestService.TryReadCommitStatuses(json).Select(c => c.Name).Should()
            .BeEquivalentTo(new[] { "ci/z" });
    }

    [Fact]
    public void AStatusEntryPointThatIsNotAnObjectIsSkippedAndTheRestSurvive()
    {
        const string json = """
            { "statuses": [ "junk", { "context": "ci/x", "state": "failure" } ] }
            """;

        GitHubPullRequestService.TryReadCommitStatuses(json).Should().HaveCount(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ unterminated")]
    [InlineData("""{"statuses": null}""")]
    [InlineData("""{"state": "failure"}""")]
    public void AnUnusableStatusPayloadParsesAsNoChecksInsteadOfThrowing(string? json)
    {
        // The status read is best-effort in the service — a commit with no statuses is normal — so an
        // unreadable body must degrade to "none", never to an exception that fails the whole snapshot.
        var act = () => GitHubPullRequestService.TryReadCommitStatuses(json);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    // ── Cross-reader invariants ─────────────────────────────────────────────────────

    [Fact]
    public void BothReadersLabelTheirKindSoThePollerCanTellTheApisApartWhenLogging()
    {
        var checkRun = GitHubPullRequestService.TryReadCheckRuns(
            """{"check_runs":[{"name":"a","conclusion":"success"}]}""").Single();
        var status = GitHubPullRequestService.TryReadCommitStatuses(
            """{"statuses":[{"context":"b","state":"success"}]}""").Single();

        // A snapshot that mixes both kinds is normal, and a mixed verdict has to say which API said it.
        checkRun.Kind.Should().NotBe(status.Kind);
    }

    [Fact]
    public void AFailedCiReadCarriesNoHeadAndNoChecksSoNothingCanBeActedOn()
    {
        // The head SHA comes from the pull-request read and `Success` from the HTTP outcome, never from
        // these parsers. That matters upstream in one direction only: a snapshot that failed to read has
        // to be unable to look like a commit whose build passed, and it cannot be attributed to a SHA
        // nobody actually fetched.
        var unreadable = GitHubCiSnapshot.Failure("http_503", "GitHub did not answer");

        unreadable.Success.Should().BeFalse();
        unreadable.HeadSha.Should().BeNull();
        unreadable.Checks.Should().BeEmpty();

        // An empty-but-successful snapshot is a genuinely green commit, which is why the two shapes have
        // to stay distinguishable rather than both arriving as an empty list.
        var green = new GitHubCiSnapshot(true, Head, Array.Empty<GitHubCiCheck>());
        green.Success.Should().BeTrue();
        green.HeadSha.Should().Be(Head);
    }
}
