using AgentMesh.Core.Activities;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace AgentMesh.Tests.Activities;

/// <summary>
/// That a job's pull request gets one review card, whichever of its two writers arrives first.
/// </summary>
/// <remarks>
/// <para>
/// The review gate posts a card when it parks, and <c>pr open</c> posts a card recording the pull
/// request's linkage. They describe the same pull request, and until they shared a notion of identity
/// each one created its own row: every job that reached Stage 4 showed the operator two identical
/// "awaiting review" cards, and the gate poller had to guess which row's timestamp was the real park.
/// </para>
/// <para>
/// The adoption lives inside <see cref="TimelineActivities.CreateCardAsync"/> rather than in the
/// workflow, and that placement is the constraint these tests exist to keep: the workflow's Temporal
/// command sequence must not change, or a run parked mid-review across a deploy would replay against a
/// history its own code no longer produces. Only the effect on the table may differ.
/// </para>
/// </remarks>
public class PullRequestReviewCardIdentityTests
{
    private const string PrUrl = "https://github.com/ckchessmaster/agent-mesh/pull/93";

    private static TimelineActivities Activities(AgentMeshDbContext db) =>
        new(db, NullLogger<TimelineActivities>.Instance);

    private static CreateTimelineCardInput Card(
        string jobId,
        string? taskId,
        TimelineCardType cardType,
        string title,
        string payload) =>
        new(jobId, taskId, cardType, TimelineCardStatus.PendingInput, title, payload);

    private static string Json(params (string Key, string Value)[] fields) =>
        "{" + string.Join(",", fields.Select(f => $"\"{f.Key}\":{f.Value}")) + "}";

    /// <summary>
    /// A job row and a fresh in-memory store. Cards are added by each test so the pull-request identity
    /// under test is always visible in the case that asserts on it.
    /// </summary>
    private static async Task<(AgentMeshDbContext Db, Guid JobId)> SeedAsync()
    {
        var db = TestDbContextFactory.Create();
        var job = new ProjectJob
        {
            ProjectId = "agent-mesh",
            Title = "Standardized project setup",
            Status = ProjectJobStatus.Active,
            GitBranchName = "mesh/job-pr-card",
            TasksJson = "[]",
            ProgressHeartbeat = "Executing task: stage-4-pr-open-0"
        };

        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        return (db, job.Id);
    }

    private static async Task AddAsync(AgentMeshDbContext db, JobTimelineCard card)
    {
        db.JobTimelineCards.Add(card);
        await db.SaveChangesAsync();
    }

    private static JobTimelineCard Existing(
        Guid jobId,
        string? taskId,
        string payload,
        string title = "PR #93: feat(projects): workspace setup",
        TimelineCardStatus status = TimelineCardStatus.PendingInput) =>
        new()
        {
            JobId = jobId.ToString(),
            TaskId = taskId,
            CardType = TimelineCardType.PrReview,
            Status = status,
            Title = title,
            StructuredPayloadJson = payload,
            CreatedAt = new DateTimeOffset(2026, 11, 17, 18, 17, 41, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 11, 17, 18, 17, 41, TimeSpan.Zero)
        };

    // ── One card per pull request ───────────────────────────────────────────────────

    [Fact]
    public async Task TheGateAdoptsTheCardTheCliAlreadyPostedInsteadOfAddingASecondOne()
    {
        // The production shape, replayed: `pr open` posts its ledger card at 18:17:41, and the workflow
        // parks its gate two seconds later and would post a second one beside it.
        var (db, jobId) = await SeedAsync();
        var cliCard = Existing(jobId, "stage-4-pr-open-0",
            Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}\""),
                 ("content", "\"PR #93 opened: awaiting review from @bee.\"")));
        await AddAsync(db, cliCard);

        var gatePayload = Json(
            ("prUrl", $"\"{PrUrl}\""), ("prNumber", "93"), ("round", "1"), ("headSha", "\"70a1842457cc\""));

        var created = await Activities(db).CreateCardAsync(new CreateTimelineCardInput(
            jobId.ToString(), null, TimelineCardType.PrReview,
            TimelineCardStatus.PendingInput, "Awaiting Review: PR #93", gatePayload));

        created.Id.Should().Be(cliCard.Id);
        created.Title.Should().Be("Awaiting Review: PR #93");
        (await db.JobTimelineCards.CountAsync(c => c.CardType == TimelineCardType.PrReview))
            .Should().Be(1);

        // Both writers' fields survive on the single row: the number, round and head the gate owns, and
        // the summary only the CLI ever writes.
        var payload = JsonDocument.Parse(created.StructuredPayloadJson).RootElement;
        payload.GetProperty("round").GetInt32().Should().Be(1);
        payload.GetProperty("headSha").GetString().Should().Be("70a1842457cc");
        payload.GetProperty("content").GetString().Should().StartWith("PR #93 opened");
    }

    [Fact]
    public async Task AReRegisteredPullRequestUpdatesTheSameCardAcrossReviewRounds()
    {
        var (db, jobId) = await SeedAsync();

        var gate = Existing(jobId, null,
            Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}\""), ("round", "1")),
            title: "Awaiting Review: PR #93");
        await AddAsync(db, gate);
        var firstPark = gate.UpdatedAt;

        // Round 2: the CLI re-registers the same pull request after remediation.
        var cli = await Activities(db).CreateCardAsync(Card(
            jobId.ToString(), "stage-4-pr-open-1", TimelineCardType.PrReview,
            "PR #93: feat(projects): workspace setup",
            Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}\""), ("content", "\"re-registered after round 1\""))));

        cli.Id.Should().Be(gate.Id);
        (await db.JobTimelineCards.CountAsync(c => c.CardType == TimelineCardType.PrReview)).Should().Be(1);
        // Captured before the call: the activity returns the very instance it updated, so asserting
        // against `gate.UpdatedAt` would be comparing the row to itself.
        cli.UpdatedAt.Should().BeAfter(firstPark);
    }

    [Fact]
    public async Task MatchingFallsBackToTheUrlWhenTheGateParkedBeforeItKnewTheNumber()
    {
        var (db, jobId) = await SeedAsync();

        var gate = Existing(jobId, null, Json(("prUrl", $"\"{PrUrl}\""), ("round", "1")),
            title: "Awaiting Review: Job Pull Request");
        await AddAsync(db, gate);

        var cli = await Activities(db).CreateCardAsync(Card(
            jobId.ToString(), "stage-4-pr-open-0", TimelineCardType.PrReview, "PR #93: workspace setup",
            Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}/\""))));

        // The gate's payload has no number at all, so number-matching alone could never have collapsed
        // this pair; the trailing slash is what the CLI's own normalisation does not promise.
        cli.Id.Should().Be(gate.Id);
    }

    [Fact]
    public async Task ANumberlessGateCardTakesTheTaskIdTheCliKnows()
    {
        var (db, jobId) = await SeedAsync();

        var gate = Existing(jobId, null, Json(("prUrl", $"\"{PrUrl}\"")));
        await AddAsync(db, gate);

        await Activities(db).CreateCardAsync(Card(
            jobId.ToString(), "stage-4-pr-open-0", TimelineCardType.PrReview, "PR #93: workspace setup",
            Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}\""))));

        // The checklist renders a card under its task, and the gate parks without one; leaving it empty
        // would strand the review card outside the round it belongs to.
        (await db.JobTimelineCards.SingleAsync(c => c.Id == gate.Id)).TaskId.Should().Be("stage-4-pr-open-0");
    }

    // ── What must NOT be collapsed ──────────────────────────────────────────────────

    [Fact]
    public async Task ADifferentPullRequestIsADifferentCard()
    {
        var (db, jobId) = await SeedAsync();

        await AddAsync(db, Existing(jobId, "stage-4-pr-open-0",
            Json(("prNumber", "93"), ("prUrl", "\"https://github.com/ckchessmaster/agent-mesh/pull/93\""))));

        await Activities(db).CreateCardAsync(Card(
            jobId.ToString(), "stage-4-pr-open-0", TimelineCardType.PrReview, "PR #94: workspace setup",
            Json(("prNumber", "94"), ("prUrl", "\"https://github.com/ckchessmaster/agent-mesh/pull/94\""))));

        // An author who closes a PR and opens another gets a second card; fusing them would hide that the
        // first review was thrown away.
        (await db.JobTimelineCards.CountAsync(c => c.CardType == TimelineCardType.PrReview)).Should().Be(2);
    }

    [Fact]
    public async Task AGateWithNoPullRequestYetStillGetsItsOwnCard()
    {
        var (db, jobId) = await SeedAsync();

        await AddAsync(db, Existing(jobId, null, Json(("round", "1"))));

        await Activities(db).CreateCardAsync(new CreateTimelineCardInput(
            jobId.ToString(), null, TimelineCardType.PrReview, TimelineCardStatus.PendingInput,
            "Awaiting Review: Job Pull Request", Json(("round", "1"))));

        // Nothing identifies either card, and two unnamed parks are more likely to be two parks than to
        // be the same one — guessing here would merge unrelated reviews.
        (await db.JobTimelineCards.CountAsync(c => c.CardType == TimelineCardType.PrReview)).Should().Be(2);
    }

    [Fact]
    public async Task OtherCardTypesAreStillCreatedEveryTime()
    {
        var (db, jobId) = await SeedAsync();

        var activities = Activities(db);
        var payload = Json(("taskIndex", "2"));

        var first = await activities.CreateCardAsync(
            Card(jobId.ToString(), "task-2", TimelineCardType.TaskStep, "Task 2", payload));
        var second = await activities.CreateCardAsync(
            Card(jobId.ToString(), "task-2", TimelineCardType.TaskStep, "Task 2 (turn update)", payload));

        // The dedupe is scoped to the one card type with two writers. Applying it to turn steps would
        // silently swallow the history the cockpit is built from.
        second.Id.Should().NotBe(first.Id);
        (await db.JobTimelineCards.CountAsync(c => c.CardType == TimelineCardType.TaskStep)).Should().Be(2);
    }

    [Fact]
    public async Task AdoptionStillRecordsTheHeartbeatTheCallerWouldHaveWritten()
    {
        var (db, jobId) = await SeedAsync();

        var cli = Existing(jobId, "stage-4-pr-open-0", Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}\"")));
        await AddAsync(db, cli);

        await Activities(db).CreateCardAsync(new CreateTimelineCardInput(
            jobId.ToString(), null, TimelineCardType.PrReview, TimelineCardStatus.PendingInput,
            "Awaiting Review: PR #93", Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}\""))));

        // Adoption has to leave the job reporting the same thing it would have with two cards, or
        // collapsing the duplicate would quietly change what the cockpit's lifecycle badge derives.
        var stored = await db.JobTimelineCards.SingleAsync(c => c.Id == cli.Id);
        stored.Status.Should().Be(TimelineCardStatus.PendingInput);
        stored.Title.Should().Be("Awaiting Review: PR #93");
        (await db.ProjectJobs.SingleAsync(j => j.Id == jobId)).ProgressHeartbeat
            .Should().Be("Awaiting Review: PR #93");
    }

    // ── Payload merging on status writes ────────────────────────────────────────────

    [Fact]
    public async Task AReviewStatusWriteKeepsThePullRequestSummaryTheCliRecorded()
    {
        var (db, jobId) = await SeedAsync();

        var card = Existing(jobId, "stage-4-pr-open-0",
            Json(("prNumber", "93"), ("prUrl", $"\"{PrUrl}\""), ("content", "\"PR #93 opened\"")));
        await AddAsync(db, card);

        await Activities(db).UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
            card.Id, TimelineCardStatus.Rejected,
            Json(("prNumber", "93"), ("round", "2"), ("reviewerId", "\"github:ci\"")),
            "Changes Requested: PR #93"));

        var payload = JsonDocument.Parse(
            (await db.JobTimelineCards.SingleAsync(c => c.Id == card.Id)).StructuredPayloadJson).RootElement;

        // The visible regression this prevents: the card had been showing the PR summary, and a status
        // write that never mentioned that field deletes it.
        payload.GetProperty("content").GetString().Should().Be("PR #93 opened");
        payload.GetProperty("reviewerId").GetString().Should().Be("github:ci");
        payload.GetProperty("round").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task OtherCardTypesKeepReplacingTheirPayloadOnAStatusWrite()
    {
        var (db, jobId) = await SeedAsync();

        var card = Existing(jobId, "task-1", Json(("stale", "\"kept\"")), title: "Task 1");
        card.CardType = TimelineCardType.TaskStep;
        await AddAsync(db, card);

        await Activities(db).UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
            card.Id, TimelineCardStatus.Resolved, Json(("status", "\"completed\"")), "Task 1 done"));

        // Union semantics are only safe for the card with two known writers. Every other type's callers
        // already assume a status write states the whole payload.
        var payload = JsonDocument.Parse(
            (await db.JobTimelineCards.SingleAsync(c => c.Id == card.Id)).StructuredPayloadJson).RootElement;
        payload.TryGetProperty("stale", out _).Should().BeFalse();
        payload.GetProperty("status").GetString().Should().Be("completed");
    }
}
