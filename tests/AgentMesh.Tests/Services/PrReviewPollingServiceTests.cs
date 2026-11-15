using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The poller's verdict rules: which GitHub review states release the gate, which stay silent, and
/// which never fire twice.
/// </summary>
/// <remarks>
/// These tests pin policy, not plumbing. The policy is what the two-man merge gate rests on, so the
/// cases that matter are the ones where firing would merge unreviewed code (stale approvals), stay
/// parked forever (over-suppression), or loop (re-delivering a decision the park already acted on).
/// The parked-at timestamp in fixtures stands in for the gate card's <c>UpdatedAt</c>: reviews
/// submitted after it are this park's business, reviews before it were some earlier park's.
/// </remarks>
public class PrReviewPollingServiceTests
{
    private const string Head = "abc123abc123abc123abc123abc123abc123abcdef";
    private const string StaleHead = "9999999999999999999999999999999999999999";

    private static readonly DateTimeOffset Parked = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static GitHubPullReview Review(
        long id,
        string login,
        string state,
        string? commitSha = Head,
        string? body = null,
        DateTimeOffset? at = null) =>
        new(id, login, state, commitSha, body, at ?? Parked.AddMinutes(5));

    // ── Approvals ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ApprovalOnCurrentHeadByNonAuthor_ApprovesWithTheLiveHeadSha()
    {
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "CKingdon-Chris", "APPROVED", body: "LGTM")],
            currentHeadSha: Head,
            authorLogin: "bee-bot",
            parkedAtUtc: Parked);

        verdict.Should().NotBeNull();
        verdict!.IsApproved.Should().BeTrue();
        verdict.ReviewerId.Should().Be("github:ckingdon-chris");
        verdict.ApprovedHeadSha.Should().Be(Head);
        verdict.Comments.Should().Be("LGTM");
    }

    [Fact]
    public void ApprovalWasOnOlderCommit_IsSilentRatherThanAMergeOnStaleConsent()
    {
        // The reviewer approved commit X; the author pushed Y. Delivering X's approval would either
        // merge unreviewed code or make the merge guard answer sha_moved and re-park into the same
        // stale approval — silence is the only terminal option, so a human reviews the new head.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "humancoworker", "APPROVED", commitSha: StaleHead)],
            currentHeadSha: Head,
            authorLogin: "bee-bot",
            parkedAtUtc: Parked);

        verdict.Should().BeNull();
    }

    [Fact]
    public void ApprovalHeadShaComparison_IsCaseAndWhitespaceTolerant()
    {
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, " reviewer-one ", "approved", commitSha: "  " + Head.ToUpperInvariant() + " ")],
            currentHeadSha: Head.ToUpperInvariant(),
            authorLogin: null,
            parkedAtUtc: Parked);

        verdict.Should().NotBeNull();
        verdict!.IsApproved.Should().BeTrue();
    }

    // ── Changes requested ─────────────────────────────────────────────────────────

    [Fact]
    public void ChangesRequested_OutrankApprovals_FromAnyone()
    {
        // One approval and one request-changes is, on GitHub and in this loop, not a ready PR.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [
                Review(1, "secondreviewer", "APPROVED", at: Parked.AddMinutes(9)),
                Review(2, "techlead", "CHANGES_REQUESTED", body: "Fix the error handling", at: Parked.AddMinutes(10)),
            ],
            currentHeadSha: Head,
            authorLogin: "bee-bot",
            parkedAtUtc: Parked);

        verdict.Should().NotBeNull();
        verdict!.IsApproved.Should().BeFalse();
        verdict.ReviewerId.Should().Be("github:techlead");
        verdict.Comments.Should().Be("Fix the error handling");
        verdict.ApprovedHeadSha.Should().BeNull();
    }

    [Fact]
    public void ChangesRequested_AfterTheAuthorPushed_StillDeliversTheFeedback()
    {
        // Approval binding and comment delivery are different questions: an approval of a superseded
        // commit is void, but comments written before a push do not become less true after it.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "techlead", "CHANGES_REQUESTED", commitSha: StaleHead, body: "Please rename this")],
            currentHeadSha: Head,
            authorLogin: "bee-bot",
            parkedAtUtc: Parked);

        verdict.Should().NotBeNull();
        verdict!.IsApproved.Should().BeFalse();
    }

    // ── Two-man rule ──────────────────────────────────────────────────────────────

    [Fact]
    public void AuthorReviews_AreIgnoredInBothDirections()
    {
        var fromAuthor = new[]
        {
            Review(1, "bee-bot", "APPROVED"),
            Review(2, "bee-bot", "CHANGES_REQUESTED", body: "self-flag"),
        };

        PrReviewPollingService
            .EvaluateReviews(fromAuthor, Head, authorLogin: "bee-bot", parkedAtUtc: Parked)
            .Should().BeNull();

        // Once the author's reviews are out, another reviewer's genuine approval still counts.
        var mixed = fromAuthor.Append(Review(3, "humancoworker", "CHANGES_REQUESTED", body: "real note"));
        var verdict = PrReviewPollingService.EvaluateReviews(mixed.ToList(), Head, "bee-bot", Parked);
        verdict!.ReviewerId.Should().Be("github:humancoworker");
    }

    [Fact]
    public void ReviewWithoutALogin_CannotBeAttributedAndIsDropped()
    {
        // An unattributable approval is the shape a bypass would take; it must never release the gate.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "", "APPROVED")],
            Head, authorLogin: null, parkedAtUtc: Parked);

        verdict.Should().BeNull();
    }

    // ── Latest-word-per-reviewer and non-verdict states ───────────────────────────

    [Fact]
    public void RequestedChangesThenApprovalOnCurrentHead_EndsApproved()
    {
        var verdict = PrReviewPollingService.EvaluateReviews(
            [
                Review(1, "techlead", "CHANGES_REQUESTED", body: "first word", at: Parked.AddMinutes(1)),
                Review(2, "techlead", "APPROVED", body: "second word", at: Parked.AddMinutes(20)),
            ],
            Head, authorLogin: "bee-bot", parkedAtUtc: Parked);

        verdict.Should().NotBeNull();
        verdict!.IsApproved.Should().BeTrue();
        verdict.ReviewId.Should().Be(2);
    }

    [Fact]
    public void ApprovalThenDismissalByPush_LeavesNoVerdict()
    {
        // A push dismisses the approval; delivering it anyway is precisely the unreviewed-merge bug.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [
                Review(1, "techlead", "APPROVED", at: Parked.AddMinutes(1)),
                Review(2, "techlead", "DISMISSED", at: Parked.AddMinutes(2)),
            ],
            Head, authorLogin: "bee-bot", parkedAtUtc: Parked);

        verdict.Should().BeNull();
    }

    [Fact]
    public void CommentOnlyReview_IsNotADecision()
    {
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "passerby", "COMMENTED", body: "drive-by nit")],
            Head, authorLogin: "bee-bot", parkedAtUtc: Parked);

        verdict.Should().BeNull();
    }

    [Fact]
    public void UndatedReview_OrdersAsOldestButStillDelivers()
    {
        // The ordering half matters for latest-per-login; the delivery half is the fail-open choice
        // documented on EvaluateReviews: never discard a decision for a timestamp GitHub omitted.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [
                Review(1, "techlead", "APPROVED", at: null),
                Review(7, "techlead", "CHANGES_REQUESTED", body: "newer word", at: Parked.AddMinutes(30)),
            ],
            Head, authorLogin: "bee-bot", parkedAtUtc: Parked);

        verdict!.IsApproved.Should().BeFalse();
        verdict.ReviewId.Should().Be(7);
    }

    // ── Duplicate suppression across parks ────────────────────────────────────────

    [Fact]
    public void ReviewPredatingTheCurrentPark_IsOneThisParkAlreadyActedOn()
    {
        // A changes-requested round that pushed nothing re-parks on the same head; the same review
        // still matches, and without this guard the remediation loop would spin on it forever.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "techlead", "CHANGES_REQUESTED", body: "already delivered", at: Parked.AddDays(-1))],
            Head, authorLogin: "bee-bot", parkedAtUtc: Parked);

        verdict.Should().BeNull();
    }

    [Fact]
    public void ReviewJustAfterThePark_IsDeliveredEvenAgainstClockSkew()
    {
        // GitHub's clock may run ahead of or behind this host's; tolerance covers the seconds of
        // difference without letting it hide a review that came in moments after parking.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "humancoworker", "APPROVED", at: Parked.AddSeconds(3))],
            Head, authorLogin: "bee-bot", parkedAtUtc: Parked.AddMinutes(1));

        verdict.Should().NotBeNull();
    }

    [Fact]
    public void MissingHeadSha_CannotFreeezAnApprovalSoNothingIsDelivered()
    {
        PrReviewPollingService.EvaluateReviews(
            [Review(1, "techlead", "APPROVED")], null, "bee-bot", Parked).Should().BeNull();
        PrReviewPollingService.EvaluateReviews(
            [Review(1, "techlead", "CHANGES_REQUESTED", body: "note")], "  ", "bee-bot", Parked).Should().BeNull();
    }

    // ── Merged-by-hand path ───────────────────────────────────────────────────────

    private static GitHubPrReviewSnapshot Snapshot(
        string state,
        string? headSha = Head,
        string? mergedBy = "ckingdon-chris",
        DateTimeOffset? mergedAt = null,
        string? mergeCommitSha = "ff00ff00ff00ff00ff00ff00ff00ff00ff00ff") =>
        new(true, state, "bee-bot", headSha, mergedBy, mergedAt, mergeCommitSha, Array.Empty<GitHubPullReview>());

    [Fact]
    public void MergedAfterThePark_ApprovesForTheMergedHead()
    {
        var verdict = PrReviewPollingService.EvaluateMergedPullRequest(
            Snapshot("merged", mergedAt: Parked.AddMinutes(10)), Parked);

        verdict.Should().NotBeNull();
        verdict!.IsApproved.Should().BeTrue();
        verdict.ReviewerId.Should().Be("github:ckingdon-chris");
        verdict.ApprovedHeadSha.Should().Be(Head);
        verdict.Key.Should().Be("merged:ff00ff00ff00ff00ff00ff00ff00ff00ff00ff");
    }

    [Fact]
    public void MergedBeforeThePark_WasNotThisParksNews()
    {
        PrReviewPollingService
            .EvaluateMergedPullRequest(Snapshot("merged", mergedAt: Parked.AddDays(-1)), Parked)
            .Should().BeNull();
    }

    [Fact]
    public void MergedWithoutAttribution_StillCompletesButSaysWhoItReallyWas()
    {
        var verdict = PrReviewPollingService.EvaluateMergedPullRequest(
            Snapshot("merged", mergedBy: null, mergedAt: Parked.AddMinutes(2)), Parked);

        verdict!.ReviewerId.Should().Be("github:merge");
    }

    // ── Gate card payload reading ─────────────────────────────────────────────────

    [Fact]
    public void GatePayload_UrlAndNumberAreReadOutOfTheWorkflowsOwnCardShape()
    {
        // The shape written by JobExecutionWorkflow.UpsertPrReviewCardAsync.
        var json = """
            {"prUrl":"https://github.com/ckchessmaster/homelab-manager/pull/1","prNumber":1,
             "headSha":"8b8796","authorPersonaId":"bee","round":1,"reviewerId":null,
             "source":"workflow:job_pr_gate"}
            """;

        PrReviewPollingService.TryReadGatePayload(json, out var url, out var number, out var author)
            .Should().BeTrue();
        url.Should().Be("https://github.com/ckchessmaster/homelab-manager/pull/1");
        number.Should().Be(1);
        author.Should().Be("bee");
    }

    [Fact]
    public void GatePayload_AuthorFallsBackThroughTheToolVariantsKeyOrder()
    {
        // Same precedence the review endpoints use for the two-man check: authorPersonaId wins,
        // then senderPersona/senderId/author; the value arrives normalized for persona lookup.
        PrReviewPollingService
            .TryReadGatePayload("""{"prNumber":1,"senderPersona":"@Bee"}""", out _, out _, out var a1)
            .Should().BeTrue();
        a1.Should().Be("bee");

        PrReviewPollingService
            .TryReadGatePayload("""{"prNumber":1,"senderId":"rook","author":"ignored"}""", out _, out _, out var a2)
            .Should().BeTrue();
        a2.Should().Be("rook");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"prNumber":0}""")]
    [InlineData("""{"prUrl":"","prNumber":null}""")]
    public void GatePayload_UnusableShapes_MeanNothingToPoll(string? json)
    {
        PrReviewPollingService.TryReadGatePayload(json, out var url, out var number, out var author)
            .Should().BeFalse();
        url.Should().BeNull();
        number.Should().BeNull();
        author.Should().BeNull();
    }

    [Fact]
    public void GatePayload_NumberOnly_CarriesTheSignalTargetAnyway()
    {
        PrReviewPollingService
            .TryReadGatePayload("""{"prNumber":42}""", out var url, out var number, out var author)
            .Should().BeTrue();
        url.Should().BeNull();
        number.Should().Be(42);
        author.Should().BeNull();
    }

    // ── Signal dressing ───────────────────────────────────────────────────────────

    [Fact]
    public void VerdictKey_DistinguishesReviewIdAndHead_SoANewReviewRefireIsAllowed()
    {
        var first = PrReviewPollingService.EvaluateReviews(
            [Review(1, "techlead", "APPROVED", at: Parked.AddMinutes(5))], Head, "bee-bot", Parked);
        var reissued = PrReviewPollingService.EvaluateReviews(
            [Review(2, "techlead", "APPROVED", at: Parked.AddMinutes(30))], Head, "bee-bot", Parked);
        var movedHead = PrReviewPollingService.EvaluateReviews(
            [Review(1, "techlead", "APPROVED", at: Parked.AddMinutes(5))], StaleHead, "bee-bot", Parked);

        first!.Key.Should().Be($"approved:1:{Head.ToLowerInvariant()}");
        reissued!.Key.Should().NotBe(first.Key);
        movedHead.Should().BeNull(); // and thus never reaches a key at all
    }

    [Fact]
    public void EmptyReviewBody_DeliversNullComments_TheWayTheRemediationBuilderExpects()
    {
        // BuildReviewRemediationTasks falls back to a generic task for null; an empty string must not
        // become a "comment" that parses into a zero-length task list.
        var verdict = PrReviewPollingService.EvaluateReviews(
            [Review(1, "techlead", "CHANGES_REQUESTED", body: "   ")], Head, "bee-bot", Parked);

        verdict!.Comments.Should().BeNull();
    }

    // ── Author-persona credential resolution (persona-credential deployments have no
    //    control-plane token; the account that opened the PR can always read its reviews) ──────

    private static AesCredentialEncryptionService Encryption() => new(new byte[32]);

    [Fact]
    public async Task AuthorToken_DecryptsThePersonasStoredPat()
    {
        var db = TestDbContextFactory.Create();
        var enc = Encryption();
        db.Personas.Add(new Persona
        {
            Id = "bee",
            Name = "Bee",
            DriverId = "claude",
            GitHubTokenEncrypted = enc.Encrypt("ghp_bee_pat_123"),
        });
        await db.SaveChangesAsync();

        // Input carries the @-prefixed/mixed-case shape the tool writes into the card.
        var token = await PrReviewPollingService.ResolvePersonaGithubTokenAsync(
            db, enc, "@Bee", NullLogger<PrReviewPollingService>.Instance, CancellationToken.None);

        token.Should().Be("ghp_bee_pat_123");
    }

    [Fact]
    public async Task AuthorToken_MissingPersonaOrMissingEncryption_FallsBackQuietly()
    {
        var db = TestDbContextFactory.Create();
        var enc = Encryption();
        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", DriverId = "claude" }); // no token stored
        await db.SaveChangesAsync();

        (await PrReviewPollingService.ResolvePersonaGithubTokenAsync(
            db, enc, "rook", NullLogger<PrReviewPollingService>.Instance, default)).Should().BeNull();
        (await PrReviewPollingService.ResolvePersonaGithubTokenAsync(
            db, enc, "nobody", NullLogger<PrReviewPollingService>.Instance, default)).Should().BeNull();
        (await PrReviewPollingService.ResolvePersonaGithubTokenAsync(
            db, null, "bee", NullLogger<PrReviewPollingService>.Instance, default)).Should().BeNull();
        (await PrReviewPollingService.ResolvePersonaGithubTokenAsync(
            db, enc, null, NullLogger<PrReviewPollingService>.Instance, default)).Should().BeNull();
    }
}
