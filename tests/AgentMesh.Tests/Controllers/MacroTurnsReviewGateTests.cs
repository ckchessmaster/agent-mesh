using System.Security.Claims;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The two-man review gate on <c>POST /api/project-jobs/{jobId}/reviews</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this class exists separately from the workflow tests.</strong>
/// <c>TaskExecutionWorkflowTests</c> proves the workflow releases its pause when a review signal
/// arrives; nothing there can prove the signal that reaches the workflow was authorised. The merge
/// decision is the one place in this feature where code lands on a base branch, so the gate — not the
/// plumbing — is the thing worth pinning.
/// </para>
/// <para>
/// <strong>The asymmetry these tests are most careful about.</strong> A job with no
/// <c>open_pull_request</c> ledger row returns "cannot prove a self-review", and the controller treats
/// that as <em>not blocked</em> rather than as <em>definitely safe</em>: refusing every review whose PR
/// was opened before this feature shipped (or by a path that does not write the ledger) would make the
/// gate unusable, and an unusable gate gets routed around. The mirror case — an author match — must be
/// a hard 403. Both directions are asserted below, because a test that only pins the 403 is compatible
/// with the guard having been inverted to "always refuse".
/// </para>
/// </remarks>
public class MacroTurnsReviewGateTests
{
    private const string AuthorPersona = "forge";
    private const string ReviewerPersona = "sentry";

    // ──────────────────────────────────────────────────────────────────────────
    // The author is read from the ledger, never from the request
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The spec's named case: a review from the persona that opened the PR is refused.
    /// </summary>
    [Fact]
    public async Task SubmitReview_FromThePrAuthor_IsRefusedWith403()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);

        var controller = CreateController(db, router.Object);
        SetUser(controller, ReviewerPersona);

        var result = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: true, ReviewerId: AuthorPersona), CancellationToken.None);

        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        // The decisive half: a refusal that still signals would merge anyway. Strict mock behaviour
        // makes any call throw, but assert it explicitly so the failure names the right thing.
        router.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A caller cannot dodge the gate by naming a different author: the author comes from the ledger
    /// row, so the request carries no author field to lie about.
    /// </summary>
    [Fact]
    public async Task SubmitReview_CannotBeBypassedByClaimingADifferentAuthor()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);
        var controller = CreateController(db, router.Object);
        SetUser(controller, AuthorPersona);

        // The authenticated principal *is* the author, and no reviewerId is supplied to override it.
        var result = await controller.SubmitReview(jobId, new SubmitPrReviewRequest(Approved: true), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Subject
            .StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        router.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A reviewer one character off from the author is refused as unverifiable rather than accepted as
    /// "different enough".
    /// </summary>
    [Fact]
    public async Task SubmitReview_FromAnUnassignedLookalikeReviewer_IsRefusedAsUnknown()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);
        var controller = CreateController(db, router.Object);
        SetUser(controller, ReviewerPersona);

        // "forg" — a typo of the author's id, and not a persona on the project.
        var result = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: true, ReviewerId: "forg"), CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Extract(bad.Value, "error").Should().Be("unknown_reviewer");
        router.VerifyNoOtherCalls();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The cases that must pass
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>A different, assigned persona approves and the decision reaches the workflow.</summary>
    [Fact]
    public async Task SubmitReview_FromADifferentAssignedPersona_IsDelivered()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        PrReviewSignal? captured = null;
        var router = new Mock<IWorkflowSignalRouter>();
        router
            .Setup(r => r.SendPrReviewAsync(
                jobId,
                It.IsAny<PrReviewSignal>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, PrReviewSignal, CancellationToken>((_, s, _) => captured = s)
            .ReturnsAsync(new SignalDelivery(WorkflowSignalRouter.WorkflowIdFor(jobId),
                SignalDeliveryStatus.Delivered));

        var controller = CreateController(db, router.Object);
        SetUser(controller, "some-operator-principal");

        var result = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(
                Approved: true, Comments: "LGTM", ReviewerId: ReviewerPersona.ToUpperInvariant()),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        Extract(ok.Value, "delivered").Should().Be(true);
        Extract(ok.Value, "decision").Should().Be("approved");

        // The reviewer is normalised to lowercase before it hits the wire, so the workflow's own
        // author comparison cannot be defeated by case variation.
        captured!.ReviewerId.Should().Be(ReviewerPersona);
        captured.IsApproved.Should().BeTrue();
        captured.ReviewComments.Should().Be("LGTM");
    }

    /// <summary>
    /// The human operator may approve a persona's PR even though they are not a persona on the project.
    /// </summary>
    /// <remarks>
    /// This is the escape hatch that keeps the gate from being a dead end: if only assigned personas
    /// could approve, a single-persona project could never merge anything.
    /// </remarks>
    [Fact]
    public async Task SubmitReview_FromTheOperator_IsDeliveredEvenThoughNotAnAssignedPersona()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        var router = RouteDelivered(jobId);
        var controller = CreateController(db, router.Object);
        SetUser(controller, "ignored-claim");

        var result = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: false, Comments: "Rename this", ReviewerId: "OPERATOR"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        router.Verify(r => r.SendPrReviewAsync(
            jobId,
            It.Is<PrReviewSignal>(s => s.ReviewerId == "operator" && !s.IsApproved),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// With no ledger row the author is unknowable, and the gate does not invent a refusal.
    /// </summary>
    [Fact]
    public async Task SubmitReview_WhenNoPrLedgerRowExists_IsNotBlockedOnAnUnknownAuthor()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: false);

        var router = RouteDelivered(jobId);
        var controller = CreateController(db, router.Object);
        SetUser(controller, ReviewerPersona);

        var result = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: true, ReviewerId: ReviewerPersona), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>
    /// A newer persona message whose payload is not a PR linkage must not mask the PR row.
    /// </summary>
    /// <remarks>
    /// Reproduced defect, not a hypothetical: the job ledger is append-only and the persona keeps
    /// talking after it opens a PR — an attachment post, a status note, anything that writes
    /// <c>structured_payload</c>. "The most recent persona row" is therefore usually <em>not</em> the
    /// PR row, and reading the author from it yields "unknown", which the gate treats as not-blocked.
    /// The result is that the author can approve its own PR as soon as it sends any later message,
    /// which is the exact bypass the guard exists to close.
    /// </remarks>
    [Fact]
    public async Task SubmitReview_AfterANonPrPersonaMessage_StillFindsThePrAuthor()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        // The author posts something with a structured payload that is not a PR linkage, 5 minutes
        // after opening the PR. Shape matches SessionsController.MergeAttachmentsIntoStructuredPayload.
        db.JobMessages.Add(new JobMessage
        {
            JobId = jobId,
            SenderType = "persona",
            SenderId = AuthorPersona,
            Content = "Attaching the trace",
            StructuredPayloadJson = """{"attachments":[{"name":"trace.txt"}]}""",
            CreatedAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5),
        });
        await db.SaveChangesAsync();

        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);
        var controller = CreateController(db, router.Object);
        SetUser(controller, ReviewerPersona);

        var result = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: true, ReviewerId: AuthorPersona), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Subject
            .StatusCode.Should().Be(StatusCodes.Status403Forbidden,
            "the author of the PR is still the author even after it posts a later message; falling " +
            "back to 'author unknown' here silently disables the self-review guard");
        router.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A reviewer that resolves to nobody at all is a 400, not a silent anonymous approval.
    /// </summary>
    [Fact]
    public async Task SubmitReview_WithNoResolvableReviewer_IsRefused()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);
        var controller = CreateController(db, router.Object);
        // No principal, and no reviewerId in the body.
        controller.ControllerContext.HttpContext = new DefaultHttpContext();

        var result = await controller.SubmitReview(jobId, new SubmitPrReviewRequest(Approved: true), CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Extract(bad.Value, "error").Should().BeOfType<string>()
            .Which.Should().Contain("Reviewer identity could not be resolved");
        router.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A decision that does not reach a workflow is reported as a conflict, never as a success.
    /// </summary>
    /// <remarks>
    /// The failure this pins is the one an operator cannot see from the UI: a 200 on an undelivered
    /// approval tells the reviewer their code is merging while nothing is merging.
    /// </remarks>
    [Fact]
    public async Task SubmitReview_WhenTheWorkflowIsNotListening_ReportsConflictNotSuccess()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: true);

        var router = new Mock<IWorkflowSignalRouter>();
        router
            .Setup(r => r.SendPrReviewAsync(
                jobId, It.IsAny<PrReviewSignal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalDelivery(
                WorkflowSignalRouter.WorkflowIdFor(jobId),
                SignalDeliveryStatus.NoRunningWorkflow,
                "No macro task workflow is running for this job."));

        var controller = CreateController(db, router.Object);
        SetUser(controller, ReviewerPersona);

        var result = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: true, ReviewerId: ReviewerPersona), CancellationToken.None);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        Extract(conflict.Value, "error").Should().Be("review_not_delivered");

        // The anonymous body declares `delivery.Status`, so the property is "Status" and it is still
        // the enum at this layer (the string conversion happens in the JSON formatter, which a
        // direct controller call bypasses).
        Extract(conflict.Value, "Status").Should().Be(SignalDeliveryStatus.NoRunningWorkflow);
    }

    /// <summary>An unknown job is a 404 before any of the review logic runs.</summary>
    [Fact]
    public async Task SubmitReview_ForAnUnknownJob_IsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);

        var controller = CreateController(db, router.Object);
        SetUser(controller, ReviewerPersona);

        var result = await controller.SubmitReview(
            Guid.NewGuid(),
            new SubmitPrReviewRequest(Approved: true, ReviewerId: ReviewerPersona), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        router.VerifyNoOtherCalls();
    }

    /// <summary>
    /// With two PRs on one job, the number in the request picks which one was reviewed.
    /// </summary>
    [Fact]
    public async Task SubmitReview_WithTwoOpenPrs_UsesTheNumberedRowsAuthor()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = await SeedJobAsync(db, withPrLedgerRow: false);

        // PR 41 was opened by the author; PR 42 by somebody else, and it is the more recent row.
        db.JobMessages.AddRange(
            PrMessage(jobId, AuthorPersona, prNumber: 41, age: TimeSpan.FromMinutes(30)),
            PrMessage(jobId, "other-agent", prNumber: 42, age: TimeSpan.FromMinutes(5)));
        await db.SaveChangesAsync();

        var router = RouteDelivered(jobId);
        var controller = CreateController(db, router.Object);
        SetUser(controller, ReviewerPersona);

        // Approving PR 42 as "other-agent" would be a self-review of that PR — but the reviewer is
        // naming PR 41, whose author really is 'forge'.
        var wrongPr = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: true, PrNumber: 42, ReviewerId: "other-agent"), CancellationToken.None);
        wrongPr.Should().BeOfType<ObjectResult>().Subject
            .StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        // Nothing reached the workflow on the refusal, so the second call is the only delivery.
        router.Verify(r => r.SendPrReviewAsync(
            It.IsAny<Guid>(), It.IsAny<PrReviewSignal>(), It.IsAny<CancellationToken>()), Times.Never);

        var rightPr = await controller.SubmitReview(
            jobId,
            new SubmitPrReviewRequest(Approved: true, PrNumber: 41, ReviewerId: ReviewerPersona), CancellationToken.None);
        rightPr.Should().BeOfType<OkObjectResult>();

        // And the signal names the PR that was actually reviewed, not the newest row on the job.
        router.Verify(r => r.SendPrReviewAsync(
            jobId,
            It.Is<PrReviewSignal>(s => s.PrNumber == 41),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Wiring
    // ──────────────────────────────────────────────────────────────────────────

    private static Mock<IWorkflowSignalRouter> RouteDelivered(Guid jobId)
    {
        var router = new Mock<IWorkflowSignalRouter>();
        router
            .Setup(r => r.SendPrReviewAsync(
                jobId, It.IsAny<PrReviewSignal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalDelivery(
                WorkflowSignalRouter.WorkflowIdFor(jobId), SignalDeliveryStatus.Delivered));
        return router;
    }

    private static MacroTurnsController CreateController(
        AgentMeshDbContext db,
        IWorkflowSignalRouter router) => new(db, router)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static void SetUser(MacroTurnsController controller, string nameIdentifier)
    {
        controller.ControllerContext.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, nameIdentifier),
            }, "TestAuth")),
        };
    }

    private static async Task<Guid> SeedJobAsync(AgentMeshDbContext db, bool withPrLedgerRow)
    {
        var projectId = "app-" + Guid.NewGuid().ToString("N")[..8];

        db.Projects.Add(new Project { Id = projectId, Name = "App" });
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = projectId, PersonaId = ReviewerPersona });

        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = projectId,
            Title = "Orchestrated task",
            TasksJson = "[]",
        });

        if (withPrLedgerRow)
        {
            db.JobMessages.Add(PrMessage(jobId, AuthorPersona, prNumber: 41, age: TimeSpan.FromMinutes(10)));
        }

        await db.SaveChangesAsync();
        return jobId;
    }

    /// <summary>
    /// A ledger row shaped the way <c>open_pull_request</c> writes one: a persona message whose
    /// structured payload carries <c>prUrl</c> and <c>prNumber</c>.
    /// </summary>
    private static JobMessage PrMessage(Guid jobId, string senderId, int prNumber, TimeSpan age) => new()
    {
        JobId = jobId,
        SenderType = "persona",
        SenderId = senderId,
        Content = $"Opened PR #{prNumber}",
        StructuredPayloadJson =
            $$"""{"prUrl":"https://github.com/o/r/pull/{{prNumber}}","prNumber":{{prNumber}}}""",
        CreatedAt = DateTimeOffset.UtcNow - age,
    };

    /// <summary>
    /// Reads one property out of an anonymous response body.
    /// </summary>
    /// <remarks>
    /// Reflection rather than a typed DTO: these bodies are deliberately shape-only (no DTO is declared
    /// for them, for the reason recorded on <c>OpenPullRequestResult</c>'s removal), and adding one to
    /// the production assembly purely to satisfy a test would reintroduce exactly the dead-code
    /// half-contract that was deleted.
    /// </remarks>
    private static object? Extract(object? body, string property)
    {
        body.Should().NotBeNull("the response must carry a body for the caller to act on");
        var value = body!.GetType().GetProperty(property)?.GetValue(body);
        value.Should().NotBeNull($"response body should expose '{property}'");
        return value;
    }
}
