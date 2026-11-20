using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The endpoint that retires an escalation card without replying to it.
/// </summary>
/// <remarks>
/// <para>
/// The incident this exists for: a halted job's "Agent paused on error" escalation stayed in
/// Needs-Attention forever because the only documented exit was a comment, the reply had no live
/// workflow to receive it, and nothing ever flipped the card to a terminal status. These tests pin
/// the three properties that make the new exit trustworthy — it really clears the wait, it never
/// fakes a decision on a workflow's gate, and pressing it twice is a no-op rather than an error.
/// </para>
/// <para>
/// Gate refusal is the load-bearing one: <c>PlanApproval</c>/<c>PrReview</c>/<c>DiscoveryQa</c> cards
/// are what a running workflow blocks on, and marking one Resolved behind the workflow's back would
/// leave a job parked on a card the ledger claims is finished — a worse lie than the stale bell.
/// </para>
/// </remarks>
public class ResolveEscalationEndpointTests
{
    private static (AgentMeshDbContext Db, string JobId, Guid CardId) Seed(
        TimelineCardType cardType = TimelineCardType.Escalation3R,
        TimelineCardStatus status = TimelineCardStatus.PendingInput)
    {
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var cardId = Guid.NewGuid();

        db.JobTimelineCards.Add(new JobTimelineCard
        {
            Id = cardId,
            JobId = jobId.ToString(),
            CardType = cardType,
            Status = status,
            Title = "Agent @test paused on error",
            StructuredPayloadJson = "{\"content\":\"boom\",\"requiresReply\":true}"
        });
        db.SaveChanges();

        return (db, jobId.ToString(), cardId);
    }

    // The temporal client, hub, router and notifier are all unused by this route (the attention
    // refresh is a null-safe extension); the production container wires the real ones and
    // ApiServiceRegistrationTests pins that.
    private static JobTimelineController Build(AgentMeshDbContext db) =>
        new(db, null!, hubContext: null, workflows: null);

    [Fact]
    public async Task Resolve_EscalationCard_IsRetiredWithSystemComment()
    {
        var (db, jobId, cardId) = Seed();
        var controller = Build(db);

        var result = await controller.ResolveCard(jobId, cardId, new ResolveCardRequest("Fixed out-of-band."), default);

        result.Should().BeOfType<OkObjectResult>();
        var card = await db.JobTimelineCards.AsNoTracking().SingleAsync(c => c.Id == cardId);
        card.Status.Should().Be(TimelineCardStatus.Resolved);

        // The audit row is the difference between "cleared because handled" and "cleared because
        // whoever clicked wanted the bell to stop": the note names the reason on the card forever.
        var comment = await db.JobCardComments.AsNoTracking().SingleAsync(c => c.CardId == cardId);
        comment.SenderType.Should().Be("system");
        comment.Content.Should().Contain("operator").And.Contain("Fixed out-of-band.");
    }

    [Theory]
    [InlineData(TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput)]
    [InlineData(TimelineCardType.InternalConsultation, TimelineCardStatus.PendingInput)]
    [InlineData(TimelineCardType.TurnInterrupted, TimelineCardStatus.Active)]
    public async Task Resolve_HumanWaitCards_AreAllRetirable(TimelineCardType cardType, TimelineCardStatus status)
    {
        // Every card shape the attention rollup can ring for — worker error escalations,
        // consultations, and interrupts whose run has since closed — needs this exit.
        var (db, jobId, cardId) = Seed(cardType, status);
        var controller = Build(db);

        var result = await controller.ResolveCard(jobId, cardId, new ResolveCardRequest(), default);

        result.Should().BeOfType<OkObjectResult>();
        (await db.JobTimelineCards.SingleAsync(c => c.Id == cardId)).Status
            .Should().Be(TimelineCardStatus.Resolved);
    }

    [Theory]
    [InlineData(TimelineCardType.PlanApproval)]
    [InlineData(TimelineCardType.PrReview)]
    [InlineData(TimelineCardType.DiscoveryQa)]
    public async Task Resolve_WorkflowGate_IsRefused(TimelineCardType gateType)
    {
        var (db, jobId, cardId) = Seed(gateType, TimelineCardStatus.PendingInput);
        var controller = Build(db);

        var result = await controller.ResolveCard(jobId, cardId, new ResolveCardRequest(), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        // Refused means untouched: no status flip, no comment.
        (await db.JobTimelineCards.SingleAsync(c => c.Id == cardId)).Status
            .Should().Be(TimelineCardStatus.PendingInput);
        (await db.JobCardComments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Resolve_UnknownCard_IsNotFound()
    {
        var (db, jobId, _) = Seed();
        var controller = Build(db);

        var result = await controller.ResolveCard(jobId, Guid.NewGuid(), new ResolveCardRequest(), default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Resolve_AlreadyRetired_IsIdempotentNoReopen()
    {
        var (db, jobId, cardId) = Seed(TimelineCardType.Escalation3R, TimelineCardStatus.Resolved);
        var controller = Build(db);

        var result = await controller.ResolveCard(jobId, cardId, new ResolveCardRequest("double click"), default);

        result.Should().BeOfType<OkObjectResult>(
            "a retried or duplicate resolve against genuinely-done work must not fail loudly");
        (await db.JobCardComments.CountAsync()).Should().Be(0,
            "the second press records nothing — the first press's comment is the whole audit trail");
    }
}
