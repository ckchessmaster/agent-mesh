using AgentMesh.Worker.Activities;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Activities;

/// <summary>
/// What the worker writes into the transcript when it stops observing a turn.
/// </summary>
/// <remarks>
/// <para>
/// This notice is the whole difference between an incident that took an evening to diagnose and one that
/// diagnoses itself. Until now the runner gave up on a wedged turn and left no trace of it anywhere a
/// human reads: the session stayed <c>thinking</c>, the job kept its "Executing task" heartbeat, and
/// finding out what had actually happened meant correlating Temporal history against transcript rows.
/// </para>
/// <para>
/// The two things the notice has to get right, then, are <em>what was last seen</em> (so the operator
/// knows which process to go kill) and <em>what to do next</em> (so they do not open the Temporal
/// console). Guessing at a cause is not part of it: quoting the last row is a measurement.
/// </para>
/// </remarks>
public class TurnAbandonNoticeTests
{
    private static HttpAgentTurnRunner.TurnState Snapshot(
        string? lastMessageType = null,
        string? lastMessageContent = null,
        DateTimeOffset? lastActivityAt = null) =>
        new(
            SessionStatus: "thinking",
            ToolCalls: [],
            JobMessages: [],
            LastActivityAt: lastActivityAt,
            LastSessionMessageContent: lastMessageContent,
            LastSessionMessageType: lastMessageType);

    // ── FormatElapsed ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(60, "1m 00s")]
    [InlineData(125, "2m 05s")]
    [InlineData(3600, "1h 00m 00s")]
    [InlineData(8045, "2h 14m 05s")]
    public void FormatElapsed_ReadsLikeASentence(int seconds, string expected) =>
        HttpAgentTurnRunner.FormatElapsed(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void FormatElapsed_DropsLeadingZeroUnits()
    {
        // "0h 04m 12s" reads like a truncated number and makes an operator wonder whether an hour is
        // hiding in there. Silence of four minutes is four minutes.
        HttpAgentTurnRunner.FormatElapsed(TimeSpan.FromSeconds(252)).Should().Be("4m 12s");
    }

    [Fact]
    public void FormatElapsed_ClocksBeforeNow_ReadAsZero()
    {
        // LastActivityAt comes from the database and clocks in this fleet are not guaranteed to agree.
        // A negative span must not render as "-3h -12m", which would read as a bug in the notice rather
        // than as the clock skew it is.
        HttpAgentTurnRunner.FormatElapsed(TimeSpan.FromMinutes(-200)).Should().Be("0s");
    }

    // ── BuildAbandonNotice ───────────────────────────────────────────────────

    [Fact]
    public void Notice_QuotesTheLastTranscriptRowSoTheWedgeIsNameable()
    {
        // This is the shape the incident actually had: a tool_call with no result after it, which means
        // a command that never returned. Naming the row is what lets the next person go straight to the
        // process instead of re-deriving this from scratch.
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot(
            "tool_call",
            "git -C /workspace diff --stat origin/main..HEAD && dotnet test /workspace/AgentMesh.slnx"));

        notice.Should().Contain("tool_call");
        notice.Should().Contain("dotnet test /workspace/AgentMesh.slnx");
    }

    [Fact]
    public void Notice_SaysNothingWasRecorded_WhenTheTurnProducedNoRowsAtAll()
    {
        // "the last row was a thinking" would be a lie when the driver never emitted anything, and that
        // is a different failure (the persona container never started) needing a different fix.
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot());

        notice.Should().Contain("No transcript row was recorded");
    }

    [Fact]
    public void Notice_KeepsASilentGapOutOfTheSentence_WhenNothingRecordedAnActivity()
    {
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot("thought", "planning the next step"));

        notice.Should().NotContain("Silent for");
    }

    [Fact]
    public void Notice_ReportsHowLongItHasBeenQuiet()
    {
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot(
            "tool_call", "npm ci", DateTimeOffset.UtcNow.AddMinutes(-14)));

        // The distinction between "quiet for nine minutes" and "quiet for six hours" decides whether
        // anyone needs to be woken up, and the runner is the only thing holding that number.
        notice.Should().MatchRegex(@"Silent for \d+m \d{2}s");
    }

    [Fact]
    public void Notice_CollapsesAndClipsAWholeStackTrailingMessage()
    {
        var longContent = "fatal: " + string.Join("\n", Enumerable.Range(0, 40).Select(i => $"  frame {i} " + new string('x', 30)));

        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot("error", longContent));

        // Transcript notices are rendered inline in the cockpit. A multi-kilobyte dump would bury the
        // sentence that matters, and newlines would break the one-line rendering.
        notice.Should().NotContain("\n");
        notice.Should().Contain("…", "the quoted row is clipped rather than spliced into the sentence");
        notice.Length.Should().BeLessThan(600);
        notice.Should().EndWith("`.", "the instruction to recover always comes last, whatever the row said");
    }

    [Fact]
    public void Notice_TellsTheOperatorHowToRecover()
    {
        // The entire point of the endpoint is that nobody has to learn the Temporal UI to unstick a job.
        // A notice that explains the problem without naming the repair still sends them to cluster tooling.
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot("tool_call", "dotnet test"));

        notice.Should().Contain("POST /api/project-jobs/<jobId>/recover");
    }

    [Fact]
    public void Notice_StatesTheWindowItGaveUpOn()
    {
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot("thought", "thinking"));

        // Without the number, "the runner stopped observing it" is unactionable: the operator cannot
        // tell whether the ceiling is too tight for this workload or the agent is genuinely dead.
        notice.Should().Contain("110 minutes");
    }

    [Fact]
    public void Notice_NeverClaimsACauseItDidNotObserve()
    {
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot("tool_call", "dotnet build"));

        // It did not see the process die, and saying "crashed" would have sent the last investigation
        // looking for an exit code that did not exist. Everything here is a quotation or a measurement.
        foreach (var claim in new[] { "crashed", "oom", "killed", "segfault", "timed out" })
        {
            notice.ToLowerInvariant().Should().NotContain(claim);
        }
    }

    [Fact]
    public void Notice_IsASingleLineEndingInFullStop()
    {
        var notice = HttpAgentTurnRunner.BuildAbandonNotice(Snapshot("response", "done"));

        notice.Should().NotContain("\n\n");
        notice.Should().EndWith(".");
    }
}
