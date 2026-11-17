using AgentMesh.Infrastructure.Containers;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Containers;

/// <summary>
/// What the platform does when a persona's workspace claim and the configured size disagree.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, an existing claim was left exactly as it was, so raising
/// <c>config.personaRunner.storage.size</c> in the chart changed nothing for a running deployment and read
/// as a knob that did not work. The cases here therefore concentrate on the one failure mode that would be
/// worse than doing nothing: patching a claim that did not need patching. A persona's workspace is
/// reconciled on every dispatched turn, so a comparison that treats equal sizes as different would issue a
/// write per turn, forever.
/// </para>
/// <para>
/// The other half is direction. A bound claim cannot shrink, so a smaller configured value has to be
/// reported and left alone; attempting it would fail at the API server on every persona start and cost the
/// persona its workspace startup for an operation Kubernetes has no way to perform.
/// </para>
/// </remarks>
public class PersonaWorkspaceResizeTests
{
    private const int Gi = 1024 * 1024 * 1024;

    private static KubernetesPersonaContainerManager.WorkspaceResize Plan(
        string? current, string? configured) =>
        KubernetesPersonaContainerManager.PlanWorkspaceResize(current, configured);

    // ── Growing ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10Gi", "20Gi")]
    [InlineData("10Gi", "102400Mi")]
    [InlineData("10Gi", "1Ti")]
    [InlineData("10Gi", "10737418241")]
    [InlineData("512Mi", "1Gi")]
    public void A_largerConfiguredSizeRaisesTheClaim(string current, string configured) =>
        Plan(current, configured).Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.Grow);

    // ── Equal, however it is written ────────────────────────────────────────────────

    [Theory]
    [InlineData("10Gi", "10Gi")]
    [InlineData("10Gi", "10240Mi")]
    [InlineData("10Gi", "10737418240")]
    [InlineData("1Ti", "1024Gi")]
    [InlineData("1024Gi", "1Ti")]
    [InlineData(" 10Gi ", "10Gi")]
    public void TheSameSizeWrittenDifferentlyIsLeftAlone(string current, string configured)
    {
        // The claim reports its size in whatever form it was stored, and canonicalising text is where a
        // naive comparison silently turns a no-op into a write on every turn.
        Plan(current, configured).Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.None);
    }

    [Fact]
    public void BinaryAndDecimalSuffixesAreNotInterchangeable()
    {
        // 10G is 10,000,000,000 bytes and 10Gi is 10,737,418,240, so asking for 10G against a 10Gi claim
        // is a shrink. Reading both as "ten gigabytes" would issue a patch Kubernetes rejects.
        Plan("10Gi", "10G").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.ShrinkRequested);
        Plan("1Gi", "1G").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.ShrinkRequested);
        Plan("10G", "10Gi").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.Grow);
    }

    // ── Never shrinking ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("20Gi", "10Gi")]
    [InlineData("1Ti", "1023Gi")]
    public void A_smallerConfiguredSizeIsReportedRatherThanAttempted(string current, string configured) =>
        Plan(current, configured).Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.ShrinkRequested);

    [Fact]
    public void ABareDecimalSizeIsReadableAndComparable()
    {
        // Twenty gigabytes decimal is smaller than the same nominal figure in binary units, and both are
        // legitimate input. Only the strings that are not quantities at all are config errors.
        Plan("10Gi", "20G").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.Grow);
        // 20Gi is 21,474,836,480 bytes and 21G is 21,000,000,000, so the nominally larger figure is the
        // smaller request. This is precisely the mistake a text comparison would make.
        Plan("20Gi", "21G").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.ShrinkRequested);
    }

    [Fact]
    public void ASmallerValueIsNeverGrownEvenThoughItIsValidInput()
    {
        // Guards the direction of the only comparison in the feature: reversing it would delete nothing
        // but would fail loudly and constantly, which is the worse way to be wrong.
        Plan("20Gi", "10Gi").Should().NotBe(KubernetesPersonaContainerManager.WorkspaceResize.Grow);
    }

    // ── Unreadable configuration ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10GiB")]
    [InlineData("abc")]
    [InlineData("10Gig")]
    [InlineData("10 GiB")]
    [InlineData("0")]
    [InlineData("-5Gi")]
    [InlineData("999999999999999999999999999999")]
    public void AnUnusableConfiguredSizeIsReportedAsAConfigProblem(string? configured)
    {
        // `0` parses cleanly as a quantity, and treating it as a legitimate request would describe
        // shrinking every workspace to nothing. Anything that cannot become a positive byte count is a
        // value the operator should be told to fix, not a size.
        Plan("10Gi", configured).Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.ConfiguredUnreadable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void AClaimWhoseSizeCannotBeReadIsLeftAlone(string? current)
    {
        // Not patching blind: this runs where a persona's workspace comes up, and a claim of unknown size
        // is a claim whose growth cannot be confirmed as an increase.
        Plan(current, "20Gi").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.None);
    }

    [Fact]
    public void AnUnreadableConfigWinsOverAnUnreadableClaim()
    {
        // The config error is the actionable one: fixing it is what makes the claim readable in practice.
        Plan(null, "abc").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.ConfiguredUnreadable);
    }

    // ── The quantity reader itself ──────────────────────────────────────────────────

    [Theory]
    [InlineData("10Gi", 10L * Gi)]
    [InlineData("1Mi", 1048576)]
    [InlineData("1k", 1000)]
    [InlineData("1M", 1000000)]
    [InlineData("1e3", 1000)]
    [InlineData("1.5Gi", 1610612736)]
    [InlineData("5120", 5120)]
    public void StorageQuantitiesParseToBytes(string quantity, long expected) =>
        KubernetesPersonaContainerManager.TryReadWorkspaceBytes(quantity).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("10Xi")]
    [InlineData("0")]
    [InlineData("-1")]
    public void AnythingThatIsNotAPositiveSizeReadsAsNothing(string? quantity) =>
        KubernetesPersonaContainerManager.TryReadWorkspaceBytes(quantity).Should().BeNull();

    [Fact]
    public void ParsingNeverThrowsOnInputTheApiServerWouldReject()
    {
        // The value comes from a chart, so a typo is a certainty over the life of a deployment, and this
        // runs on the path that starts a persona.
        var act = () =>
        {
            KubernetesPersonaContainerManager.TryReadWorkspaceBytes("!!!");
            KubernetesPersonaContainerManager.TryReadWorkspaceBytes("1e999999");
            KubernetesPersonaContainerManager.PlanWorkspaceResize("{{{", "}");
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void TheComparisonIsMadeInBytesRatherThanAsText()
    {
        // The text ordering that makes this worth pinning: "1Ti" sorts before "2Gi" character-wise.
        string.CompareOrdinal("1Ti", "2Gi").Should().BeNegative();
        Plan("2Gi", "1Ti").Should().Be(KubernetesPersonaContainerManager.WorkspaceResize.Grow);
    }
}
