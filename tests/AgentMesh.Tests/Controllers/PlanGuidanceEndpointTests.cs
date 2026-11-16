using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The endpoint that lets an operator talk to the agent without deciding the plan gate.
/// </summary>
/// <remarks>
/// Its whole job is to be honest about delivery. The message is written to the card before anything is
/// signalled, so the failure an operator must never see is "accepted" followed by silence: a 202 that no
/// workflow ever read leaves a job that looks instructed and is not. These tests pin the opposite — the
/// comment always survives, and a non-delivery says so in the response.
/// </remarks>
public class PlanGuidanceEndpointTests
{
    private static (AgentMeshDbContext Db, string JobId, Guid CardId) Seed(
        TimelineCardType cardType = TimelineCardType.PlanApproval,
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
            Title = "Plan Approval",
            StructuredPayloadJson = "{\"tasks\":[],\"reasoning\":\"r\"}"
        });
        db.SaveChanges();

        return (db, jobId.ToString(), cardId);
    }

    private static JobTimelineController Build(AgentMeshDbContext db, IWorkflowSignalRouter? router) =>
        // The temporal client is only reached by the decision route's signal call, which every case
        // here either refuses before it or replaces with the router.
        new(db, null!, hubContext: null, workflows: router);

    private static Mock<IWorkflowSignalRouter> Router(SignalDeliveryStatus status, string? detail = null)
    {
        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);
        router.Setup(r => r.SendPlanGuidanceAsync(It.IsAny<Guid>(), It.IsAny<PlanGuidanceSignal>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SignalDelivery("job-x", status, detail));
        return router;
    }

    private static JsonNode AsNode(object? value) =>
        JsonSerializer.SerializeToNode(value)!;

    [Fact]
    public async Task Guidance_WithoutContent_IsRefused()
    {
        var (db, jobId, cardId) = Seed();
        var controller = Build(db, Router(SignalDeliveryStatus.Delivered).Object);

        var result = await controller.SubmitPlanGuidance(jobId, cardId, new PlanGuidanceRequest("   "), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.JobCardComments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Guidance_OnAnUnknownCard_IsNotFound()
    {
        var (db, jobId, _) = Seed();
        var controller = Build(db, Router(SignalDeliveryStatus.Delivered).Object);

        var result = await controller.SubmitPlanGuidance(jobId, Guid.NewGuid(), new PlanGuidanceRequest("freeze billing"), default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Guidance_OnACardThatIsNotAPlanGate_IsRefused()
    {
        var (db, jobId, cardId) = Seed(cardType: TimelineCardType.DiscoveryQa);
        var controller = Build(db, Router(SignalDeliveryStatus.Delivered).Object);

        var result = await controller.SubmitPlanGuidance(jobId, cardId, new PlanGuidanceRequest("freeze billing"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        AsNode(((BadRequestObjectResult)result).Value)["error"]!.GetValue<string>()
            .Should().Contain("DiscoveryQa");
    }

    [Fact]
    public async Task Guidance_OnADecidedGate_IsRefused()
    {
        var (db, jobId, cardId) = Seed(status: TimelineCardStatus.Approved);
        var controller = Build(db, Router(SignalDeliveryStatus.Delivered).Object);

        var result = await controller.SubmitPlanGuidance(jobId, cardId, new PlanGuidanceRequest("freeze billing"), default);

        result.Should().BeOfType<ConflictObjectResult>();
        (await db.JobCardComments.CountAsync()).Should().Be(0, "a decided gate has nothing downstream that would read it");
    }

    [Fact]
    public async Task Guidance_RoutesThroughTheWorkflowAndCarriesTheCommentId()
    {
        var (db, jobId, cardId) = Seed();
        PlanGuidanceSignal? captured = null;
        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);
        router.Setup(r => r.SendPlanGuidanceAsync(It.IsAny<Guid>(), It.IsAny<PlanGuidanceSignal>(), It.IsAny<CancellationToken>()))
              .Callback<Guid, PlanGuidanceSignal, CancellationToken>((_, signal, _) => captured = signal)
              .ReturnsAsync(new SignalDelivery("job-1", SignalDeliveryStatus.Delivered));

        var result = await Build(db, router.Object)
            .SubmitPlanGuidance(jobId, cardId, new PlanGuidanceRequest("  The billing module is frozen.  "), default);

        (result as AcceptedResult).Should().NotBeNull();
        captured.Should().NotBeNull();
        captured!.CardId.Should().Be(cardId);
        captured.SenderId.Should().Be("operator");
        captured.Content.Should().Be("The billing module is frozen.", "the agent should not see stray whitespace");

        // The comment id is what makes the signal idempotent: the workflow drops a repeat of an id it
        // has already woken on, so a retried POST cannot buy a second decomposition round.
        captured.MessageId.Should().NotBeNullOrWhiteSpace();
        (await db.JobCardComments.SingleAsync()).Id.ToString().Should().Be(captured.MessageId);
        AsNode(((AcceptedResult)result).Value!)["delivered"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Guidance_ThatReachesNoWorkflow_StillPersistsTheMessage_AndSaysNoAgentWasTold()
    {
        var (db, jobId, cardId) = Seed();
        var router = Router(SignalDeliveryStatus.NoRunningWorkflow, "No running job workflow is holding a plan gate.");

        var result = await Build(db, router.Object)
            .SubmitPlanGuidance(jobId, cardId, new PlanGuidanceRequest("freeze billing"), default);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var body = AsNode(conflict.Value)!;
        body["error"]!.GetValue<string>().Should().Contain("no agent has been told");
        body["delivered"]!.GetValue<bool>().Should().BeFalse();
        body["commentId"]!.GetValue<Guid>().Should().NotBeEmpty();

        (await db.JobCardComments.SingleAsync()).Content.Should().Be("freeze billing",
            "an operator's typed instruction must not be lost because the workflow had moved on");
    }

    [Fact]
    public async Task Guidance_WithoutAConfiguredRouter_ReportsUnavailableRatherThanSuccess()
    {
        var (db, jobId, cardId) = Seed();

        var result = await Build(db, router: null)
            .SubmitPlanGuidance(jobId, cardId, new PlanGuidanceRequest("freeze billing"), default);

        var status = result.Should().BeOfType<ObjectResult>().Subject.StatusCode;
        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        (await db.JobCardComments.SingleAsync()).Content.Should().Be("freeze billing");
    }

    [Fact]
    public async Task Guidance_OnASupersededGate_StillReachesTheAgent()
    {
        // Guidance sent while Stage 1B is mid-revision has nowhere to land except the next round, and the
        // card it was written against is already Resolved. Refusing it would be refusing a real
        // instruction over a bookkeeping detail.
        var (db, jobId, cardId) = Seed(status: TimelineCardStatus.Resolved);

        var result = await Build(db, Router(SignalDeliveryStatus.Delivered).Object)
            .SubmitPlanGuidance(jobId, cardId, new PlanGuidanceRequest("and keep the CLI contract stable"), default);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task PlanDecision_OnAGateThatWasAlreadyAnswered_IsRefusedBeforeSignalling()
    {
        var (db, jobId, cardId) = Seed(status: TimelineCardStatus.Resolved);
        var router = Router(SignalDeliveryStatus.Delivered);

        var result = await Build(db, router.Object)
            .SubmitPlanDecision(jobId, cardId, new PlanDecisionPayload(true, null, "operator"), default);

        result.Should().BeOfType<ConflictObjectResult>();
        router.VerifyNoOtherCalls();
        var card = await db.JobTimelineCards.SingleAsync(c => c.Id == cardId);
        card.Status.Should().Be(TimelineCardStatus.Resolved, "a stale approval must not rewrite a superseded gate");
    }
}
