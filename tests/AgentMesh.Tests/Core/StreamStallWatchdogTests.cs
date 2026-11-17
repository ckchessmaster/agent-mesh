using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Core;

/// <summary>
/// The bounded stream read that turns "the driver stopped printing" into an event instead of a hang.
/// </summary>
/// <remarks>
/// <para>
/// These are the four behaviours the incident demanded, and each one is a distinct way the fix could be
/// wrong in production: a stall that is mistaken for cancellation (an operator's stop gets reported to
/// the model as a wedged driver), a cancellation that is mistaken for a stall (the reverse), an idle
/// clock that is really a total budget (long-but-healthy streams get killed), and a cancelled read that
/// leaves the reader mid-frame (subsequent lines corrupted).
/// </para>
/// <para>
/// Timeouts here are tens of milliseconds rather than the production twenty minutes. The ratios are what
/// matter: every test keeps the delay-per-read comfortably under the idle limit, or deliberately over it.
/// </para>
/// </remarks>
public class StreamStallWatchdogTests
{
    private static readonly TimeSpan StallAfter = TimeSpan.FromMilliseconds(40);

    [Fact]
    public async Task ReadLineAsync_DeliversALine_AndReportsNoStall()
    {
        using var stream = new FakeDriverStream(["first line"]);

        var result = await StreamStallWatchdog.ReadLineAsync(stream, StallAfter, CancellationToken.None);

        result.GotLine.Should().BeTrue();
        result.Line.Should().Be("first line");
        result.Stalled.Should().BeFalse();
    }

    [Fact]
    public async Task ReadLineAsync_ReportsAStall_WhenTheProducerGoesQuiet()
    {
        // A driver that is thinking, or running a build, prints nothing for a while. What distinguishes
        // the incident is that it printed nothing *for ever*, and a plain read cannot tell those apart.
        using var stream = new FakeDriverStream([], stallAfterScript: true);
        var started = DateTimeOffset.UtcNow;

        var result = await StreamStallWatchdog.ReadLineAsync(stream, StallAfter, CancellationToken.None);

        result.Stalled.Should().BeTrue();
        result.Line.Should().BeNull();
        result.GotLine.Should().BeFalse();

        // It waited roughly the whole window rather than returning instantly. Not asserted exactly:
        // Task.Delay is a timer, and a timer can fire a millisecond early on a loaded CI box, which
        // would make a precise bound a flaky test instead of a meaningful one.
        (DateTimeOffset.UtcNow - started).Should().BeGreaterThan(StallAfter - TimeSpan.FromMilliseconds(15));
    }

    [Fact]
    public async Task ReadLineAsync_ReportsEndOfStream_RatherThanAStall_WhenTheProducerCloses()
    {
        using var stream = new FakeDriverStream([null]);

        var result = await StreamStallWatchdog.ReadLineAsync(stream, StallAfter, CancellationToken.None);

        // Both cases yield a null line. Only one of them means "the driver died holding a tool call", so
        // callers must be able to tell EOF from stall, and a closed stream must not cost an idle window.
        result.Line.Should().BeNull();
        result.Stalled.Should().BeFalse();
    }

    [Fact]
    public async Task ReadLineAsync_RestartsTheIdleClock_OnEveryLine()
    {
        // Six silent reads that each finish inside the window, for a total elapsed time well past it. A
        // watchdog built on a fixed total budget would cut this stream off around line three, killing a
        // healthy driver mid-turn — the exact class of bug this whole change must not introduce.
        var lines = Enumerable.Repeat((string?)"tick", 6).ToList();
        using var stream = new FakeDriverStream(lines, delayPerRead: TimeSpan.FromMilliseconds(15));
        var idle = TimeSpan.FromMilliseconds(60);

        var seen = new List<string>();
        var completed = await StreamStallWatchdog.PumpAsync(
            stream, idle, line => { seen.Add(line); return Task.CompletedTask; }, CancellationToken.None);

        completed.Should().BeTrue("the stream ended on its own; nothing stalled");
        seen.Should().HaveCount(6);
    }

    [Fact]
    public async Task ReadLineAsync_ThrowsForCancellation_RatherThanReportingAStall()
    {
        using var stream = new FakeDriverStream([], stallAfterScript: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        var act = async () => await StreamStallWatchdog.ReadLineAsync(stream, TimeSpan.FromSeconds(30), cts.Token);

        // A platform- or operator-issued stop must not be written into the transcript as "the driver
        // wedged". Those are different facts, and the CLI's adapter error paths treat them differently.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadLineAsync_ThrowsForAnAlreadyCancelledToken_WithoutReading()
    {
        using var stream = new FakeDriverStream(["never delivered"]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await StreamStallWatchdog.ReadLineAsync(stream, StallAfter, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stream.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadLineAsync_RejectsANonPositiveIdleLimit(int milliseconds)
    {
        using var stream = new FakeDriverStream(["x"]);

        var act = async () => await StreamStallWatchdog.ReadLineAsync(
            stream, TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None);

        // Passing TimeSpan.Zero to mean "no timeout" would disable the guard entirely, silently. That is
        // the configuration that produced the original incident, so it has to be a loud mistake.
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadLineAsync_RejectsANullReader()
    {
        var act = async () => await StreamStallWatchdog.ReadLineAsync(
            null!, StallAfter, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PumpAsync_HandsOverEveryLineUpToTheStall_ThenReportsIt()
    {
        using var stream = new FakeDriverStream(["one", "two"], stallAfterScript: true);

        var seen = new List<string>();
        var reachedEnd = await StreamStallWatchdog.PumpAsync(
            stream, StallAfter, line => { seen.Add(line); return Task.CompletedTask; }, CancellationToken.None);

        reachedEnd.Should().BeFalse();
        seen.Should().Equal("one", "two");
    }

    [Fact]
    public async Task PumpAsync_StopsAtEndOfStream_WithoutCallingTheHandlerWithNull()
    {
        using var stream = new FakeDriverStream(["only"]);

        var seen = new List<string>();
        var reachedEnd = await StreamStallWatchdog.PumpAsync(
            stream, StallAfter, line => { seen.Add(line); return Task.CompletedTask; }, CancellationToken.None);

        reachedEnd.Should().BeTrue();
        seen.Should().Equal("only");
    }

    [Fact]
    public async Task PumpAsync_PropagatesAHandlerFault()
    {
        using var stream = new FakeDriverStream(["one"]);

        var act = async () => await StreamStallWatchdog.PumpAsync(
            stream, StallAfter, _ => throw new InvalidOperationException("adapter mapping failed"),
            CancellationToken.None);

        // A handler that throws is a bug in the adapter, not a stalled stream. Swallowing it here would
        // turn a mapping defect into a mysterious twenty-minute stall on every turn.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PumpAsync_RejectsANullHandler()
    {
        using var stream = new FakeDriverStream(["x"]);

        var act = async () => await StreamStallWatchdog.PumpAsync(stream, StallAfter, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// A stand-in for a subprocess driver's stdout pipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="System.IO.StringReader"/> cannot model the case that matters: a read that never
    /// returns. Overriding <see cref="TextReader.ReadLineAsync(CancellationToken)"/> reproduces the real
    /// shape exactly, because that is the call the driver adapters make on <c>proc.StandardOutput</c>.
    /// </para>
    /// <para>
    /// The cancellation token is honoured on the stalling read so the reader can be disposed while a read
    /// is outstanding — which is the normal situation after a stall is reported, since the caller is
    /// expected to kill the child process.
    /// </para>
    /// </remarks>
    private sealed class FakeDriverStream : TextReader
    {
        private readonly List<string?> _script;
        private readonly TimeSpan _delayPerRead;
        private readonly bool _stallAfterScript;
        private readonly CancellationTokenSource _disposed = new();
        private int _index;

        public FakeDriverStream(
            IEnumerable<string?> script,
            TimeSpan? delayPerRead = null,
            bool stallAfterScript = false)
        {
            _script = script.ToList();
            _delayPerRead = delayPerRead ?? TimeSpan.Zero;
            _stallAfterScript = stallAfterScript;
        }

        /// <summary>How many reads the watchdog actually issued, for the "never read" assertions.</summary>
        public int Reads => _index;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_index < _script.Count)
            {
                var line = _script[_index];
                _index++;

                if (_delayPerRead > TimeSpan.Zero)
                {
                    await Task.Delay(_delayPerRead, cancellationToken);
                }
                return line;
            }

            if (_stallAfterScript)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            }

            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed.Cancel();
                _disposed.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
