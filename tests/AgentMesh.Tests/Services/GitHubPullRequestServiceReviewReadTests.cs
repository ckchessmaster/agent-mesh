using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The GitHub review read: URL-first repository resolution, tolerant parsing, and the rule that a
/// failed read is never presented as "no reviews yet".
/// </summary>
/// <remarks>
/// Verdict policy lives in <see cref="PrReviewPollingServiceTests"/>; these tests pin the transport
/// layer it trusts. The two failure shapes that matter are (1) a reviews call that 403s answering as
/// an empty list — which a poller would read as "nobody has reviewed" and sit on for seven days —
/// and (2) any path where a token could reach a returned string.
/// </remarks>
public class GitHubPullRequestServiceReviewReadTests
{
    private const string Token = "ghp_supersecrettoken1234567890";
    private const string Head = "abc123abc123abc123abc123abc123abc123abcdef";

    private const string OpenPrJson = """
        {
          "number": 1,
          "state": "open",
          "user": { "login": "bee-bot", "type": "User" },
          "head": { "sha": "abc123abc123abc123abc123abc123abc123abcdef" },
          "merged": false,
          "merged_by": null,
          "merged_at": null,
          "merge_commit_sha": null
        }
        """;

    private const string ReviewsJson = """
        [
          {
            "id": 10,
            "user": { "login": "humancoworker", "type": "User" },
            "state": "APPROVED",
            "commit_sha": "abc123abc123abc123abc123abc123abc123abcdef",
            "body": "LGTM",
            "submitted_at": "2026-11-15T12:05:00Z"
          },
          {
            "id": 11,
            "user": { "login": "ghost-reviewer", "type": "Bot" },
            "state": "CHANGES_REQUESTED",
            "commit_sha": "9999999999999999999999999999999999999999",
            "body": "",
            "submitted_at": "2026-11-15T12:06:00Z"
          },
          {
            "id": 12,
            "state": "APPROVED"
          }
        ]
        """;

    private const string MergedPrJson = """
        {
          "number": 1,
          "state": "merged",
          "user": { "login": "bee-bot" },
          "head": { "sha": "abc123abc123abc123abc123abc123abc123abcdef" },
          "merged": true,
          "merged_by": { "login": "ckingdon-chris" },
          "merged_at": "2026-11-15T14:00:00Z",
          "merge_commit_sha": "ff00ff00ff00ff00ff00ff00ff00ff00ff00ff"
        }
        """;

    private static GitHubPullRequestService Build(
        StubHttpHandler handler,
        Dictionary<string, string?>? settings = null)
    {
        var merged = new Dictionary<string, string?>(settings ?? new())
        {
            ["GitHub:Token"] = Token,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(merged).Build();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GitHubPullRequestService.HttpClientName))
            .Returns(() => new HttpClient(handler));

        return new GitHubPullRequestService(
            factory.Object, configuration, NullLogger<GitHubPullRequestService>.Instance,
            (_, _) => throw new InvalidOperationException("the review read must never shell out to git"));
    }

    private static GitHubPullRequestService BuildWithoutCredentials(StubHttpHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GitHubPullRequestService.HttpClientName))
            .Returns(() => new HttpClient(handler));

        return new GitHubPullRequestService(
            factory.Object, configuration, NullLogger<GitHubPullRequestService>.Instance,
            (_, _) => throw new InvalidOperationException("no credentials means no calls at all"));
    }

    // ── The successful read ───────────────────────────────────────────────────────

    [Fact]
    public async Task OpenPullRequest_ReturnsStateAuthorHeadAndEverySubmittedReview()
    {
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/ckchessmaster/homelab-manager/pulls/1", 200, OpenPrJson)
            .Respond(HttpMethod.Get, "/repos/ckchessmaster/homelab-manager/pulls/1/reviews", 200, ReviewsJson);

        var service = Build(handler);
        var snapshot = await service.ReadPullRequestReviewStateAsync(
            "https://github.com/ckchessmaster/homelab-manager/pull/1");

        snapshot.Success.Should().BeTrue();
        snapshot.PrState.Should().Be("open");
        snapshot.AuthorLogin.Should().Be("bee-bot");
        snapshot.HeadSha.Should().Be(Head);

        snapshot.Reviews.Should().HaveCount(3);
        snapshot.Reviews[0].Id.Should().Be(10);
        snapshot.Reviews[0].Login.Should().Be("humancoworker");
        snapshot.Reviews[0].State.Should().Be("APPROVED");
        snapshot.Reviews[0].CommitSha.Should().Be(Head);
        snapshot.Reviews[0].SubmittedAt.Should().Be(new DateTimeOffset(2026, 11, 15, 12, 5, 0, TimeSpan.Zero));

        // An entry with no user object keeps its state and arrives with a null login; the poller
        // drops unattributable reviews, and this layer's job is to lose nothing on the way up.
        snapshot.Reviews[2].Login.Should().BeNull();
        snapshot.Reviews[2].State.Should().Be("APPROVED");

        handler.Requests.Should().OnlyContain(r => r.Authorization == $"Bearer {Token}");
    }

    [Fact]
    public async Task MergedPullRequest_CarriesTheMergeFactsThePollerNeeds()
    {
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1", 200, MergedPrJson)
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1/reviews", 200, "[]");

        var snapshot = await Build(handler)
            .ReadPullRequestReviewStateAsync("https://github.com/o/r/pull/1");

        snapshot.Success.Should().BeTrue();
        snapshot.PrState.Should().Be("merged");
        snapshot.MergedByLogin.Should().Be("ckingdon-chris");
        snapshot.MergedAt.Should().Be(new DateTimeOffset(2026, 11, 15, 14, 0, 0, TimeSpan.Zero));
        snapshot.MergeCommitSha.Should().Be("ff00ff00ff00ff00ff00ff00ff00ff00ff00ff");
    }

    // ── Repository resolution ─────────────────────────────────────────────────────

    [Fact]
    public async Task FullUrl_WinsOverConfiguration_BecauseTheUrlNamesThisExactPullRequest()
    {
        // GitHub:Repository is the deployment default; a PR URL from another repo must read there,
        // or the same number in a different repository answers for someone else's reviews.
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/someowner/somerepo/pulls/9", 200, OpenPrJson)
            .Respond(HttpMethod.Get, "/repos/someowner/somerepo/pulls/9/reviews", 200, "[]");

        var service = Build(handler, new() { ["GitHub:Repository"] = "ckchessmaster/homelab-manager" });
        var snapshot = await service
            .ReadPullRequestReviewStateAsync("https://github.com/someowner/somerepo/pull/9");

        snapshot.Success.Should().BeTrue();
        handler.Requests.Select(r => r.Path).Should().AllSatisfy(p =>
            p.Should().Contain("someowner/somerepo"));
    }

    [Fact]
    public async Task BareNumber_FallsBackToTheConfiguredRepository()
    {
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/cfg/repo/pulls/7", 200, OpenPrJson)
            .Respond(HttpMethod.Get, "/repos/cfg/repo/pulls/7/reviews", 200, "[]");

        var service = Build(handler, new() { ["GitHub:Repository"] = "cfg/repo" });
        var snapshot = await service.ReadPullRequestReviewStateAsync("7");

        snapshot.Success.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task BareNumberWithoutAnyRepository_IsRefusedNotGuessed()
    {
        var handler = new StubHttpHandler();
        var snapshot = await Build(handler).ReadPullRequestReviewStateAsync("7");

        snapshot.Success.Should().BeFalse();
        snapshot.FailureReason.Should().Be("no_repository");
        handler.Requests.Should().BeEmpty();
    }

    // ── Failure paths that must not look like "no reviews" ────────────────────────

    [Fact]
    public async Task NoCredentials_IsAFailureWithZeroRequests()
    {
        var handler = new StubHttpHandler();
        var snapshot = await BuildWithoutCredentials(handler)
            .ReadPullRequestReviewStateAsync("https://github.com/o/r/pull/1");

        snapshot.Success.Should().BeFalse();
        snapshot.FailureReason.Should().Be("no_credentials");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TokenOverride_ReadsWithItWhenTheDeploymentConfiguresNone()
    {
        // The persona-credential deployment: the poller passes the author persona's PAT because the
        // control plane has no token of its own. The account that opened the PR can read its reviews.
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1", 200, OpenPrJson)
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1/reviews", 200, ReviewsJson);

        var snapshot = await BuildWithoutCredentials(handler)
            .ReadPullRequestReviewStateAsync("https://github.com/o/r/pull/1", tokenOverride: "ghp_persona_pat");

        snapshot.Success.Should().BeTrue(snapshot.Message);
        snapshot.Reviews.Should().HaveCount(3);
        handler.Requests.Should().OnlyContain(r => r.Authorization == "Bearer ghp_persona_pat");
    }

    [Fact]
    public async Task TokenOverride_WinsOverConfiguredToken()
    {
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1", 200, OpenPrJson)
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1/reviews", 200, "[]");

        var snapshot = await Build(handler)
            .ReadPullRequestReviewStateAsync("https://github.com/o/r/pull/1", tokenOverride: "ghp_persona_pat");

        snapshot.Success.Should().BeTrue();
        handler.Requests.Should().OnlyContain(r => r.Authorization == "Bearer ghp_persona_pat");
    }

    [Fact]
    public async Task ReviewsCallRejected_FailsTheWholeRead_InsteadOfReportingAnEmptyReviewList()
    {
        // Success with zero reviews is the true statement "nobody has said anything yet". A 403 is
        // not that statement, and conflating them makes the poller wait seven days on a review that
        // exists behind a rate limit.
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1", 200, OpenPrJson)
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1/reviews", 403,
                """{"message":"rate limit exceeded"}""");

        var snapshot = await Build(handler).ReadPullRequestReviewStateAsync("https://github.com/o/r/pull/1");

        snapshot.Success.Should().BeFalse();
        snapshot.FailureReason.Should().Be("api_error");
        snapshot.Reviews.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingPullRequest_ReportsNotFound()
    {
        var handler = new StubHttpHandler(); // unmatched paths answer 404

        var snapshot = await Build(handler).ReadPullRequestReviewStateAsync("https://github.com/o/r/pull/999");

        snapshot.Success.Should().BeFalse();
        snapshot.FailureReason.Should().Be("not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("neither a number nor a pull url")]
    public async Task UnusableReference_IsRefusedWithoutNetwork(string value)
    {
        var handler = new StubHttpHandler();
        var snapshot = await Build(handler).ReadPullRequestReviewStateAsync(value);

        snapshot.Success.Should().BeFalse();
        snapshot.FailureReason.Should().BeOneOf("invalid_request", "invalid_pr_reference");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FailureMessages_NeverEchoTheToken()
    {
        // Every string this service returns can land in a timeline card and the Cockpit.
        var handler = new StubHttpHandler()
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1", 200, OpenPrJson)
            .Respond(HttpMethod.Get, "/repos/o/r/pulls/1/reviews", 401,
                $$"""{"message":"Bad credentials url=https://x-access-token:{{Token}}@api.github.com"}""");

        var snapshot = await Build(handler).ReadPullRequestReviewStateAsync("https://github.com/o/r/pull/1");

        snapshot.Success.Should().BeFalse();
        (snapshot.Message ?? string.Empty).Should().NotContain(Token);
    }

    // ── Parser tolerance ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"not":"an array"}""")]
    public void TryReadReviews_UnparseablePayloads_YieldEmptyNotThrow(string? json)
    {
        GitHubPullRequestService.TryReadReviews(json).Should().BeEmpty();
    }

    [Fact]
    public void TryReadReviews_SkipsItemsWithoutAState_ButKeepsTheRest()
    {
        var json = """
            [
              {"id": 1, "user": {"login": "a"}, "state": "APPROVED", "commit_sha": "sha"},
              {"id": 2, "user": {"login": "b"}},
              {"id": 3, "user": {"login": "c"}, "state": "  ", "commit_sha": "sha"},
              {"id": 4, "user": {"login": "d"}, "state": "COMMENTED", "body": "note", "submitted_at": "garbage"}
            ]
            """;

        var reviews = GitHubPullRequestService.TryReadReviews(json);

        reviews.Should().HaveCount(2);
        reviews[0].Login.Should().Be("a");
        reviews[1].Login.Should().Be("d");
        // An unparseable timestamp degrades to null (ordering falls back to id), not to a failed read.
        reviews[1].SubmittedAt.Should().BeNull();
    }

    [Fact]
    public void TryReadPullRequestSummary_ReadsIdentityFields_BesideTheExistingStateTriple()
    {
        var (state, headSha, author, mergedBy, mergedAt, mergeSha) =
            GitHubPullRequestService.TryReadPullRequestSummary(MergedPrJson);

        state.Should().Be("merged");
        headSha.Should().Be(Head);
        author.Should().Be("bee-bot");
        mergedBy.Should().Be("ckingdon-chris");
        mergedAt.Should().NotBeNull();
        mergeSha.Should().Be("ff00ff00ff00ff00ff00ff00ff00ff00ff00ff");
    }

    [Fact]
    public void TryReadPullRequestSummary_MalformedOrPartial_YieldsNullsWithoutThrowing()
    {
        GitHubPullRequestService.TryReadPullRequestSummary("not json")
            .State.Should().BeNull();

        // State and head survive even when the identity extras are absent entirely.
        var partial = GitHubPullRequestService.TryReadPullRequestSummary("""{"state":"open","head":{"sha":"x"}}""");
        partial.State.Should().Be("open");
        partial.HeadSha.Should().Be("x");
        partial.AuthorLogin.Should().BeNull();
        partial.MergedByLogin.Should().BeNull();
    }
}
