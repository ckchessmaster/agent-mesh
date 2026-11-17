using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Core;

/// <summary>
/// The thresholds that decide when a turn counts as dead, and the ordering between them.
/// </summary>
/// <remarks>
/// These assertions look tautological ("110 minutes is 110 minutes") but they are not pinning the
/// numbers: they are pinning the <em>ordering</em>. The worker, the API sweeper, and the CLI's driver
/// stream read independently and ship in three different images, so a future edit that tightens the
/// sweeper below the worker's ceiling would let the platform write <c>stopped</c> over a live agent
/// mid-command. That is a worse failure than the phantom-running bug the sweeper exists to fix, and
/// nothing but this ordering catches it.
/// </remarks>
public class TurnStallPolicyTests
{
    [Fact]
    public void DriverStreamBound_FiresBeforeTheWorkerCeiling()
    {
        TurnStallPolicy.DriverStreamIdleLimit.Should()
            .BeLessThan(TurnStallPolicy.MaxObservedTurn,
                "a wedged subprocess driver should be reaped long before the worker gives up on the turn; " +
                "if the bound moved above the ceiling, every driver stall would wait out the whole 110 minutes.");
    }

    [Fact]
    public void OrphanedTurnAfter_SitsAboveTheWorkerCeiling()
    {
        TurnStallPolicy.OrphanedTurnAfter.Should()
            .BeGreaterThan(TurnStallPolicy.MaxObservedTurn,
                "the sweeper must never settle a turn the worker is still observing. The worker polls on " +
                "an interval and then runs post-turn activities, so it can legitimately conclude slightly " +
                "after its own ceiling.");

        TurnStallPolicy.OrphanedTurnAfter.Should()
            .Be(TurnStallPolicy.MaxObservedTurn + TurnStallPolicy.OrphanGrace,
                "the orphan threshold is defined as the ceiling plus grace, not as an independent guess.");
    }

    [Fact]
    public void DriverBound_SitsBelowTheWorkerCeiling_ByAComfortableMargin()
    {
        // The driver bound has to clear an ordinary silent build (dotnet test, npm ci) and still leave
        // the worker room to observe the turn it belongs to. Two-thirds of the ceiling is the margin
        // this policy chose; anything tighter and a slow CI box starts killing live turns.
        TurnStallPolicy.DriverStreamIdleLimit.Should()
            .BeLessThanOrEqualTo(TurnStallPolicy.MaxObservedTurn / 2);
    }

    [Fact]
    public void LiveTurnStatuses_AreTheStatusesThatLieWhenAbandoned()
    {
        TurnStallPolicy.LiveTurnStatuses.Should().Contain(new[] { "thinking", "busy" });

        // "active" is a session parked between turns, and "waiting_for_input" is a turn deliberately left
        // open for a human. Sweeping either would erase a correct state, so they must stay out.
        TurnStallPolicy.LiveTurnStatuses.Should().NotContain(new[] { "active", "waiting_for_input", "stopped", "error" });
    }

    [Fact]
    public void SettledStatus_IsItselfNotLive()
    {
        // If the status we settle into were also a live status, the sweeper would settle the same session
        // on every cycle forever, appending a notice row each time.
        TurnStallPolicy.LiveTurnStatuses.Should().NotContain(TurnStallPolicy.SettledStatus);
    }

    [Fact]
    public void SettleNoticePrefix_IsBracketedAndStable()
    {
        TurnStallPolicy.SettleNoticePrefix.Should().StartWith("[").And.EndWith("]");

        // The dashboard and every future "was this row written by the platform?" filter keys off this
        // literal. Changing it silently orphans those consumers.
        TurnStallPolicy.SettleNoticePrefix.Should().Be("[turn-stalled]");
    }

    [Fact]
    public void EnvVarOverride_HonoursALargerValue()
    {
        TurnStallPolicy.ResolveDriverStreamIdleLimit("45").Should().Be(TimeSpan.FromMinutes(45));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("0.5")]
    public void EnvVarOverride_FallsBackToTheDefault(string? raw)
    {
        // Sub-minute values are rejected rather than clamped up to one minute. An operator who types 0
        // usually means "no limit", which is precisely the failure mode this bound exists to remove; a
        // bound of a few seconds would kill ordinary turns. Silently honouring either intent would be
        // the worse answer, so the default wins and the override is a no-op.
        TurnStallPolicy.ResolveDriverStreamIdleLimit(raw).Should().Be(TurnStallPolicy.DriverStreamIdleLimit);
    }

    [Fact]
    public void EnvVarOverride_ParsesInvariantly()
    {
        // Persona containers run under whatever culture the base image happens to set. A comma decimal
        // must not make "2.5" unparseable and silently restore the default on a machine that asked for
        // two and a half minutes.
        using (new CultureScope("fr-FR"))
        {
            TurnStallPolicy.ResolveDriverStreamIdleLimit("2.5").Should().Be(TimeSpan.FromMinutes(2.5));
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous =
            Thread.CurrentThread.CurrentCulture;

        public CultureScope(string name) =>
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => Thread.CurrentThread.CurrentCulture = _previous;
    }
}
