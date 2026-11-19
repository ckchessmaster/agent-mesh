using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The writes that make an abandoned turn honest, and the guards on who may be rewritten.
/// </summary>
/// <remarks>
/// The behaviour under test produced the original incident: a session sat in <c>thinking</c> for
/// thirteen hours because nothing ever wrote a terminal status. So the tests below are not only "does it
/// settle" but "does it settle <em>legibly</em>" (a transcript row saying why, not just a status flip)
/// and, just as importantly, "does it leave alone every session it must not touch" — a sweeper that
/// rewrites a completed turn or a human-parked one would be a worse bug than the one it fixes.
/// </remarks>
public class TurnStallSettlerTests
{
    private readonly AgentMeshDbContext _db = TestDbContextFactory.Create();

    private AgentSession AddSession(string status, DateTimeOffset updatedAt, string? metadata = null)
    {
        var session = new AgentSession
        {
            Title = "Standardized project setup",
            Status = status,
            UpdatedAt = updatedAt,
            CreatorAgentId = "astra",
            MetadataJson = metadata ?? "{}"
        };
        _db.AgentSessions.Add(session);
        _db.SaveChanges();
        return session;
    }

    // ── SettleOneAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SettleOneAsync_MarksTheSessionStopped()
    {
        var session = AddSession("thinking", DateTimeOffset.UtcNow.AddMinutes(-140));

        var settled = await TurnStallSettler.SettleOneAsync(_db, session.Id, "the ceiling expired", "temporal-worker");

        settled.Should().NotBeNull();
        settled!.Status.Should().Be(TurnStallPolicy.SettledStatus);
        (await Reload(session.Id)).Status.Should().Be("stopped");
    }

    [Fact]
    public async Task SettleOneAsync_AppendsATranscriptRowExplainingWhy()
    {
        // The status flip alone is indistinguishable from an operator pressing Stop. The row is what lets
        // the next investigation start from "the platform settled this at T because X" instead of
        // re-deriving it from scratch, and it is what the resuming agent reads as context.
        var session = AddSession("busy", DateTimeOffset.UtcNow.AddMinutes(-140));

        await TurnStallSettler.SettleOneAsync(_db, session.Id, "worker stopped observing after 110 minutes", "temporal-worker");

        var row = (await _db.SessionMessages.Where(m => m.SessionId == session.Id).ToListAsync()).Single();
        row.EventType.Should().Be("system");
        row.SenderAgentId.Should().Be("temporal-worker");
        row.Content.Should().StartWith(TurnStallPolicy.SettleNoticePrefix);
        row.Content.Should().Contain("worker stopped observing after 110 minutes");
    }

    [Fact]
    public async Task SettleOneAsync_TimestampsTheNoticeItself()
    {
        var session = AddSession("thinking", DateTimeOffset.UtcNow.AddMinutes(-140));
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await TurnStallSettler.SettleOneAsync(_db, session.Id, "quiet too long", "platform-sweeper");

        var content = (await _db.SessionMessages.Where(m => m.SessionId == session.Id).ToListAsync()).Single().Content;
        content.Should().MatchRegex(@"Settled at \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z\.$",
            "a notice without an instant cannot be lined up against the transcript to see what the agent " +
            "was last doing, and callers each supply only the reason");
        content.Should().NotContain("\n");

        var row = (await _db.SessionMessages.Where(m => m.SessionId == session.Id).ToListAsync()).Single();
        row.CreatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task SettleOneAsync_UsesAFallbackSentence_WhenTheReasonIsBlank()
    {
        var session = AddSession("thinking", DateTimeOffset.UtcNow.AddMinutes(-140));

        await TurnStallSettler.SettleOneAsync(_db, session.Id, "   ", "platform-sweeper");

        var content = (await _db.SessionMessages.Where(m => m.SessionId == session.Id).ToListAsync()).Single().Content;
        content.Should().Contain("owner stopped advancing it");
    }

    [Fact]
    public async Task SettleOneAsync_TargetsTheSessionCreator_WhenKnown()
    {
        var session = AddSession("thinking", DateTimeOffset.UtcNow.AddMinutes(-140));

        await TurnStallSettler.SettleOneAsync(_db, session.Id, "quiet", "platform-sweeper");

        var row = (await _db.SessionMessages.Where(m => m.SessionId == session.Id).ToListAsync()).Single();
        row.TargetAgentId.Should().Be("astra");
    }

    [Theory]
    [InlineData("active")]
    [InlineData("waiting_for_input")]
    [InlineData("stopped")]
    [InlineData("error")]
    [InlineData("")]
    [InlineData(null)]
    public async Task SettleOneAsync_RefusesToRewrite_ANonLiveStatus(string? status)
    {
        // A concluded turn's status is a fact about what happened. This is a repair, not an override: an
        // operator's Stop, a completed response, and a human-parked review are all answers that must
        // survive a sweep.
        var session = AddSession(status ?? "active", DateTimeOffset.UtcNow.AddMinutes(-500));

        var settled = await TurnStallSettler.SettleOneAsync(_db, session.Id, "quiet", "platform-sweeper");

        settled.Should().BeNull();
        (await Reload(session.Id)).Status.Should().Be(status ?? "active");
        (await _db.SessionMessages.Where(m => m.SessionId == session.Id).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SettleOneAsync_ReturnsNull_ForAnUnknownSession()
    {
        var settled = await TurnStallSettler.SettleOneAsync(_db, Guid.NewGuid(), "quiet", "platform-sweeper");

        settled.Should().BeNull();
    }

    // ── SettleOrphansAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task SettleOrphansAsync_SettlesOnlyQuietLiveSessions()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-140);
        var orphaned = AddSession("thinking", stale);
        var alsoOrphaned = AddSession("busy", stale);
        var liveButQuiet = AddSession("active", stale);
        var recentThinking = AddSession("thinking", DateTimeOffset.UtcNow.AddMinutes(-3));

        var settled = await TurnStallSettler.SettleOrphansAsync(
            _db, DateTimeOffset.UtcNow - TurnStallPolicy.OrphanedTurnAfter, "swept", "platform-sweeper");

        settled.Select(s => s.Id).Should().BeEquivalentTo(new[] { orphaned.Id, alsoOrphaned.Id });
        (await Reload(liveButQuiet.Id)).Status.Should().Be("active", "a parked session is not an abandoned turn");
        (await Reload(recentThinking.Id)).Status.Should().Be("thinking", "a turn inside the window is still live");
    }

    [Fact]
    public async Task SettleOrphansAsync_WritesOneNoticeRow_PerSettledSession()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-200);
        AddSession("thinking", stale);
        AddSession("busy", stale);

        var settled = await TurnStallSettler.SettleOrphansAsync(
            _db, DateTimeOffset.UtcNow - TurnStallPolicy.OrphanedTurnAfter, "swept", "platform-sweeper");

        settled.Should().HaveCount(2);
        (await _db.SessionMessages.ToListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task SettleOrphansAsync_ReturnsEmpty_AndWritesNothing_WhenNothingQualifies()
    {
        AddSession("thinking", DateTimeOffset.UtcNow);

        var settled = await TurnStallSettler.SettleOrphansAsync(
            _db, DateTimeOffset.UtcNow - TurnStallPolicy.OrphanedTurnAfter, "swept", "platform-sweeper");

        settled.Should().BeEmpty();
        (await _db.SessionMessages.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SettleOrphansAsync_HonoursTheFilter_SoARecoveryStaysInsideOneJob()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-200);
        var jobId = Guid.NewGuid();
        var mine = AddSession("thinking", stale, JobMetadata(jobId));
        AddSession("thinking", stale, JobMetadata(Guid.NewGuid()));

        var settled = await TurnStallSettler.SettleOrphansAsync(
            _db,
            DateTimeOffset.UtcNow - TurnStallPolicy.OrphanedTurnAfter,
            "operator recovered",
            "operator-recovery",
            filter: s => TurnStallSettler.TryGetJobIdFromMetadata(s.MetadataJson, out var id) && id == jobId);

        // Recovery is taken in a hurry on one wedged job. Without the filter it would settle every
        // orphan in the cluster, including another persona's legitimately-running turn.
        settled.Select(s => s.Id).Should().Equal(mine.Id);
    }

    // ── MarkJobStalledAsync ──────────────────────────────────────────────────

    private ProjectJob AddJob(ProjectJobStatus status, string? heartbeat = "Executing task 3/5: add the metrics route")
    {
        var job = new ProjectJob
        {
            ProjectId = "proj-1",
            Title = "Standardized project setup",
            Status = status,
            ProgressHeartbeat = heartbeat,
            GitBranchName = "mesh/job-1"
        };
        _db.ProjectJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    [Fact]
    public async Task MarkJobStalledAsync_PrefixesTheHeartbeat_SoTheCockpitCanOfferRecovery()
    {
        var job = AddJob(ProjectJobStatus.Active);

        var marked = await TurnStallSettler.MarkJobStalledAsync(_db, job.Id, "the 110-minute ceiling expired");

        marked.Should().BeTrue();
        var reloaded = await _db.ProjectJobs.FirstAsync(j => j.Id == job.Id);
        reloaded.ProgressHeartbeat.Should().StartWith(TurnStallSettler.StalledHeartbeatPrefix);
        reloaded.ProgressHeartbeat.Should().Contain("110-minute ceiling expired");
        TurnStallSettler.IsStalledHeartbeat(reloaded.ProgressHeartbeat).Should().BeTrue();
    }

    [Fact]
    public async Task MarkJobStalledAsync_KeepsTheJobActive_AndLeavesTheChecklistAlone()
    {
        // Stalled is deliberately a heartbeat marker and not a ProjectJobStatus. Inventing a status would
        // change the lifecycle contract for every consumer, and recovery has to be possible without
        // re-activating anything.
        var job = AddJob(ProjectJobStatus.Active);

        await TurnStallSettler.MarkJobStalledAsync(_db, job.Id, "wedged driver");

        var reloaded = await _db.ProjectJobs.FirstAsync(j => j.Id == job.Id);
        reloaded.Status.Should().Be(ProjectJobStatus.Active);
    }

    [Theory]
    [InlineData(ProjectJobStatus.Pending)]
    [InlineData(ProjectJobStatus.Completed)]
    [InlineData(ProjectJobStatus.Archived)]
    public async Task MarkJobStalledAsync_OnlyMarksActiveJobs(ProjectJobStatus status)
    {
        var job = AddJob(status, heartbeat: null);

        var marked = await TurnStallSettler.MarkJobStalledAsync(_db, job.Id, "wedged driver");

        marked.Should().BeFalse();
        (await _db.ProjectJobs.FirstAsync(j => j.Id == job.Id)).ProgressHeartbeat.Should().BeNull();
    }

    [Fact]
    public async Task MarkJobStalledAsync_ReturnsFalse_ForAnUnknownJob()
    {
        (await TurnStallSettler.MarkJobStalledAsync(_db, Guid.NewGuid(), "reason")).Should().BeFalse();
    }

    [Fact]
    public async Task MarkJobStalledAsync_KeepsTheMarkerOnOneLine()
    {
        // The heartbeat is rendered in a single-line badge. A multi-line reason would break the layout,
        // so only the first line is kept, and it is clipped so one long sentence cannot push the badge
        // into a scroll.
        var job = AddJob(ProjectJobStatus.Active, heartbeat: null);

        await TurnStallSettler.MarkJobStalledAsync(_db, job.Id, "first line\nsecond line that nobody should see");

        var heartbeat = (await _db.ProjectJobs.FirstAsync(j => j.Id == job.Id)).ProgressHeartbeat!;
        heartbeat.Should().NotContain("\n");
        heartbeat.Should().Contain("first line");
        heartbeat.Should().NotContain("second line");
    }

    [Fact]
    public async Task MarkJobStalledAsync_ClipsAVeryLongReason()
    {
        var job = AddJob(ProjectJobStatus.Active, heartbeat: null);

        await TurnStallSettler.MarkJobStalledAsync(_db, job.Id, new string('x', 900));

        var heartbeat = (await _db.ProjectJobs.FirstAsync(j => j.Id == job.Id)).ProgressHeartbeat!;
        heartbeat.Length.Should().BeLessThan(TurnStallSettler.StalledHeartbeatPrefix.Length + 230);
        heartbeat.Should().EndWith("…");
    }

    [Fact]
    public async Task MarkJobStalledAsync_SaysSomething_SoTheBadgeIsNeverEmpty()
    {
        var job = AddJob(ProjectJobStatus.Active, heartbeat: null);

        await TurnStallSettler.MarkJobStalledAsync(_db, job.Id, "  ");

        (await _db.ProjectJobs.FirstAsync(j => j.Id == job.Id))
            .ProgressHeartbeat.Should().Be($"{TurnStallSettler.StalledHeartbeatPrefix} its turn stopped advancing");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Executing task 2/5", false)]
    [InlineData("Recovering: resuming at task 4/5", false)]
    [InlineData("Stalled: ceiling expired", true)]
    [InlineData("stalled: lower case still counts", true)]
    public void IsStalledHeartbeat_ReadsTheMarker(string? heartbeat, bool expected) =>
        TurnStallSettler.IsStalledHeartbeat(heartbeat).Should().Be(expected);

    // ── metadata + liveness helpers ──────────────────────────────────────────

    [Fact]
    public void TryGetJobIdFromMetadata_OutParamIsTheParsedJob()
    {
        var jobId = Guid.NewGuid();

        TurnStallSettler.TryGetJobIdFromMetadata(JobMetadata(jobId), out var parsed).Should().BeTrue();
        parsed.Should().Be(jobId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]{not an object}")]
    [InlineData("{ unterminated")]
    [InlineData(@"{""jobId"":null}")]
    [InlineData(@"{""jobId"":""not-a-guid""}")]
    [InlineData(@"{""jobId"":""00000000-0000-0000-0000-000000000000""}")]
    [InlineData(@"{""otherJob"":""e9a1b2c3-0000-4000-8000-000000000001""}")]
    public void TryGetJobIdFromMetadata_SaysNo_RatherThanGuessing(string? metadata)
    {
        // A plain chat session with no job has no jobId, and that must mean "there is no job to mark
        // stalled" rather than "mark some job". Tolerating garbage here would let one bad row drag an
        // unrelated job into a stalled badge.
        TurnStallSettler.TryGetJobIdFromMetadata(metadata, out var parsed).Should().BeFalse();
        parsed.Should().Be(Guid.Empty);
    }

    [Theory]
    [InlineData("thinking", true)]
    [InlineData("Thinking", true)]
    [InlineData("busy", true)]
    [InlineData("active", false)]
    [InlineData("waiting_for_input", false)]
    [InlineData("stopped", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsLive_AnswersOnlyForStatusesAnAgentMayStillHOLD(string? status, bool expected) =>
        TurnStallSettler.IsLive(status).Should().Be(expected);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string JobMetadata(Guid jobId) => $$"""{"jobId":"{{jobId}}","taskId":"t-1"}""";

    private async Task<AgentSession> Reload(Guid sessionId) =>
        await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId);
}
