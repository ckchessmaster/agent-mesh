using AgentMesh.Core.Services;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace AgentMesh.Tests.Core;

/// <summary>
/// The identity of a pull-request review card, and the merge that lets its two writers share one row.
/// </summary>
/// <remarks>
/// Two code paths write this card — the workflow's review gate and the CLI's <c>pr open</c> — and each
/// used to deduplicate against its own idea of "the card for this PR", which is why every job that
/// reached Stage 4 showed the same review twice. These helpers are the shared answer, so the cases that
/// matter are the ones where matching would collapse two genuinely different things (over-eager) and
/// the ones where not matching would leave the duplicate on screen (over-cautious).
/// </remarks>
public class PrReviewCardPayloadTests
{
    private const string Url = "https://github.com/ckchessmaster/agent-mesh/pull/93";

    // ── Reading the reference ───────────────────────────────────────────────────────

    [Fact]
    public void BothIdentifiersAreReadWhenThePayloadCarriesThem()
    {
        var payload = $$"""{"prNumber":93,"prUrl":"{{Url}}","round":2}""";

        PrReviewCardPayload.ReadReference(payload).Should().Be((93, Url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void AnythingThatIsNotAnObjectWithIdentifiersYieldsNoReference(string? payload)
    {
        // Two producers write these payloads and a hot poll loop reads them; a malformed card is a
        // card with no identity, never an exception on the way to a review gate.
        PrReviewCardPayload.ReadReference(payload).Should().Be(((int?)null, null));
    }

    [Theory]
    [InlineData("""{"prNumber":0}""")]
    [InlineData("""{"prNumber":-4}""")]
    [InlineData("""{"prNumber":"93"}""")]
    [InlineData("""{"prNumber":null,"prUrl":"  "}""")]
    public void ZeroNegativeAndNonNumericPullRequestNumbersAreNotIdentity(string payload)
    {
        // A card parked before a pull request exists carries null here; carrying 0 or a string is a
        // writer that got its types wrong, and matching on it would merge unrelated reviews.
        PrReviewCardPayload.ReadReference(payload).Should().Be(((int?)null, null));
    }

    [Fact]
    public void TheStoredReferenceIsNormalisedSoTheTwoWritersCanMeet()
    {
        var withSlash = $$"""{"prUrl":"{{Url}}/"}""";

        PrReviewCardPayload.ReadReference(withSlash).Url.Should().Be(Url);
    }

    // ── Deciding what "the same pull request" means ─────────────────────────────────

    [Fact]
    public void MatchingNumbersIdentifyTheSamePullRequestEvenIfOnlyOneSideKnowsTheUrl()
    {
        var gate = PrReviewCardPayload.ReadReference("""{"prNumber":93,"headSha":"abc"}""");
        var cli = PrReviewCardPayload.ReadReference($$"""{"prNumber":93,"prUrl":"{{Url}}"}""");

        // This is the pairing that was missing in production: the ledger card and the gate card for one
        // pull request, unable to see each other, each posting its own.
        PrReviewCardPayload.IsSamePullRequest(gate, cli).Should().BeTrue();
        PrReviewCardPayload.IsSamePullRequest(cli, gate).Should().BeTrue();
    }

    [Fact]
    public void DifferentNumbersAreDifferentPullRequestsEvenOnTheSameUrl()
    {
        var ninetyThree = PrReviewCardPayload.ReadReference($$"""{"prNumber":93,"prUrl":"{{Url}}"}""");
        var ninetyFour = PrReviewCardPayload.ReadReference("""{"prNumber":94,"prUrl":"https://github.com/ckchessmaster/agent-mesh/pull/93"}""");

        // The number wins whenever both sides have one; a URL someone pasted onto the wrong card must
        // not fuse two reviews.
        PrReviewCardPayload.IsSamePullRequest(ninetyThree, ninetyFour).Should().BeFalse();
    }

    [Fact]
    public void UrlsIdentifyTheSamePullRequestWhenANumberIsMissingOnOneSide()
    {
        var gate = PrReviewCardPayload.ReadReference("""{"prUrl":"https://github.com/ckchessmaster/agent-mesh/pull/93/"}""");
        var cli = PrReviewCardPayload.ReadReference("""{"prUrl":"https://github.com/CKCHESSMASTER/agent-mesh/pull/93"}""");

        // GitHub serves the same pull request with or without a trailing slash, and case-insensitively
        // for owner/repo, so comparing the strings verbatim is what left duplicates behind.
        PrReviewCardPayload.IsSamePullRequest(gate, cli).Should().BeTrue();

        var elsewhere = PrReviewCardPayload.ReadReference("""{"prUrl":"https://github.com/ckchessmaster/agent-mesh/pull/94"}""");
        PrReviewCardPayload.IsSamePullRequest(gate, elsewhere).Should().BeFalse();
    }

    [Fact]
    public void ACardWithNoIdentifierMatchesNothing()
    {
        var anonymous = PrReviewCardPayload.ReadReference("""{"status":"parked"}""");
        var real = PrReviewCardPayload.ReadReference($$"""{"prNumber":93,"prUrl":"{{Url}}"}""");

        // The gate can park before a pull request exists. Adopting aggressively there would let a later,
        // unrelated park claim a card it was never meant to own, so absence means "create me a card".
        PrReviewCardPayload.IsSamePullRequest(anonymous, real).Should().BeFalse();
        PrReviewCardPayload.IsSamePullRequest(anonymous, anonymous).Should().BeFalse();
    }

    // ── Merging two writers onto one payload ────────────────────────────────────────

    [Fact]
    public void AStatusWriteFromTheGateDoesNotDeleteTheSummaryTheCliRecorded()
    {
        const string fromCli = """{"prNumber":93,"prUrl":"https://github.com/x/y/pull/93","content":"PR #93 opened","mentions":["bee"]}""";
        const string fromGate = """{"prNumber":93,"prUrl":"https://github.com/x/y/pull/93","round":1,"reviewerId":null}""";

        var merged = JsonDocument.Parse(PrReviewCardPayload.Merge(fromCli, fromGate)).RootElement;

        // The regression this prevents is invisible in the database and obvious on screen: the card
        // loses the pull-request summary it had been showing, because the gate's payload never mentioned
        // a field it does not own.
        merged.GetProperty("content").GetString().Should().Be("PR #93 opened");
        merged.GetProperty("round").GetInt32().Should().Be(1);
    }

    [Fact]
    public void TheIncomingValueWinsForAKeyBothSidesCarry()
    {
        var merged = JsonDocument.Parse(PrReviewCardPayload.Merge(
            """{"headSha":"aaaa","round":1}""",
            """{"headSha":"bbbb"}""")).RootElement;

        merged.GetProperty("headSha").GetString().Should().Be("bbbb");
        merged.GetProperty("round").GetInt32().Should().Be(1);
    }

    [Fact]
    public void AnOverlaidNullIsStillAStatementRatherThanAnOmission()
    {
        var merged = JsonDocument.Parse(PrReviewCardPayload.Merge(
            """{"reviewerId":"bee"}""",
            """{"reviewerId":null}""")).RootElement;

        // A writer that says "there is no reviewer on this round" means it; preserving the old value
        // would keep a stale name on a card that has moved on.
        merged.GetProperty("reviewerId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void AWholeObjectKeyIsReplacedRatherThanDeepMerged()
    {
        var merged = JsonDocument.Parse(PrReviewCardPayload.Merge(
            """{"gate":{"round":1,"seen":true}}""",
            """{"gate":{"round":2}}""")).RootElement;

        // Shallow by design: two producers that disagree about a nested shape need last-write-wins, not
        // a union of structures neither of them can parse.
        merged.GetProperty("gate").TryGetProperty("seen", out _).Should().BeFalse();
        merged.GetProperty("gate").GetProperty("round").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData(null, """{"a":1}""")]
    [InlineData("", """{"a":1}""")]
    [InlineData("not json", """{"a":1}""")]
    [InlineData("""{"a":1}""", "not json")]
    public void AMalformedSideCostsTheMergeNeverTheWrite(string? existing, string? incoming)
    {
        // A payload that cannot be parsed must never be written over one that can, and a legacy row of
        // junk must never be able to veto the write that would repair it.
        var merged = PrReviewCardPayload.Merge(existing, incoming);

        merged.Should().NotBeNullOrWhiteSpace();
        JsonDocument.Parse(merged).RootElement.GetProperty("a").GetInt32().Should().Be(1);
    }

    [Fact]
    public void AnEmptyIncomingPayloadLeavesTheExistingRowAlone()
    {
        const string existing = """{"content":"kept"}""";

        // The activity skips a blank payload before it ever gets here, but the helper is public and the
        // other caller should not have to re-derive that.
        PrReviewCardPayload.Merge(existing, null).Should().Be(existing);
    }

    [Fact]
    public void MergingIsRepeatableAndDoesNotAliasTheNodes()
    {
        var first = PrReviewCardPayload.Merge("""{"a":1,"n":{"deep":true}}""", """{"b":2}""");
        var second = PrReviewCardPayload.Merge(first, """{"c":3}""");

        var root = JsonDocument.Parse(second).RootElement;
        root.GetProperty("a").GetInt32().Should().Be(1);
        root.GetProperty("b").GetInt32().Should().Be(2);
        root.GetProperty("c").GetInt32().Should().Be(3);
        root.GetProperty("n").GetProperty("deep").GetBoolean().Should().BeTrue();
    }
}
