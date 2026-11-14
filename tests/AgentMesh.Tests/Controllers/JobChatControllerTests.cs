using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Worker.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Temporalio.Client;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class JobChatControllerTests
{
    private readonly Mock<ITemporalClient> _mockTemporalClient = new();
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext = new();
    private readonly Mock<IHubClients> _mockClients = new();
    private readonly Mock<IClientProxy> _mockClientProxy = new();

    public JobChatControllerTests()
    {
        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
    }

    private static AgentMeshDbContext CreateInMemoryDb() => TestDbContextFactory.Create();

    [Fact]
    public async Task PostReview_RejectsSelfReviewByImplementer()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Autonomous Job",
            AssignedAgentId = "implementer-bot",
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // Implementer attempts to approve their own PR
        var selfReviewer = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "implementer-bot"),
            new Claim("agent_id", "implementer-bot")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = selfReviewer }
        };

        var request = new SubmitJobPrReviewRequest(ReviewerId: "implementer-bot", IsApproved: true, ReviewComments: "Self approval", PrNumber: 101);

        // Act
        var result = await controller.SubmitReview(jobId.ToString(), request);

        // Assert: Self review must be rejected with 403 Forbidden
        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        forbidden.Value.ToString().Should().Contain("self_review_not_allowed");
    }

    [Fact]
    public async Task PostReview_WhenAssignedAgentIdIsNull_ResolvesAuthorFromPrLinkage_AndRejectsSelfReviewWhileAllowingSecondEyes()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Autonomous Job Without AssignedAgentId",
            AssignedAgentId = null,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);

        // 1. Implementer opens PR and writes PR linkage to job timeline
        var prLinkage = new
        {
            prNumber = 101,
            prUrl = "https://github.com/org/repo/pull/101",
            authorPersonaId = "implementer-bot",
            senderPersona = "implementer-bot",
            source = "mcp:open_pull_request"
        };
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = jobId.ToString(),
            CardType = TimelineCardType.PrReview,
            Status = TimelineCardStatus.PendingInput,
            Title = "PR #101",
            StructuredPayloadJson = JsonSerializer.Serialize(prLinkage),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        // 2. Later, another persona (e.g. tech lead) posts a timeline card WITHOUT PR linkage
        db.JobTimelineCards.Add(new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = jobId.ToString(),
            CardType = TimelineCardType.InternalConsultation,
            Status = TimelineCardStatus.Active,
            Title = "Review note (@astra)",
            StructuredPayloadJson = "{\"content\":\"Reviewing this PR now.\",\"authorPersonaId\":\"astra\"}",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        });

        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // Case A: The implementer attempts to review their PR -> must be rejected as self review
        var implementerUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "implementer-bot"),
            new Claim("agent_id", "implementer-bot")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = implementerUser }
        };

        var selfReviewRequest = new SubmitJobPrReviewRequest(ReviewerId: "implementer-bot", IsApproved: true, ReviewComments: "Self approval", PrNumber: 101);
        var selfReviewResult = await controller.SubmitReview(jobId.ToString(), selfReviewRequest);

        var forbidden = selfReviewResult.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        forbidden.Value.ToString().Should().Contain("self_review_not_allowed");

        // Case B: The commenter (Astra / Tech Lead) submits the review -> must NOT be treated as author, allowed to approve
        var techLeadUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "astra"),
            new Claim("agent_id", "astra"),
            new Claim(ClaimTypes.Role, "tech_lead")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = techLeadUser }
        };

        var leadReviewRequest = new SubmitJobPrReviewRequest(ReviewerId: "astra", IsApproved: true, ReviewComments: "LGTM!", PrNumber: 101);
        var leadReviewResult = await controller.SubmitReview(jobId.ToString(), leadReviewRequest);

        leadReviewResult.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ApprovePlan_RejectsTechLead_WhenPolicyIsHumanOnly()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Mission Critical Job",
            ApprovalPolicy = PlanApprovalPolicy.HumanOnly,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // Tech lead attempts to approve a HumanOnly plan
        var techLead = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "astra"),
            new Claim("agent_id", "astra"),
            new Claim(ClaimTypes.Role, "tech_lead")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = techLead }
        };

        // Even if client attempts to assert approverRole = "operator", role is derived from ClaimsPrincipal
        var request = new ApprovePlanRequest(ApproverId: "astra", ApproverRole: "operator", Comments: "Plan LGTM from tech lead");

        // Act
        var result = await controller.ApprovePlan(jobId.ToString(), request);

        // Assert: 403 Forbidden under HumanOnly policy
        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(403);
        forbidden.Value.ToString().Should().Contain("human_only_policy");
    }

    [Fact]
    public async Task ApprovePlan_Returns401_WhenCallerIdentityIsMissing()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Mission Critical Job",
            ApprovalPolicy = PlanApprovalPolicy.HumanOnly,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // Authenticated principal without any identity claims
        var unidentifiableUser = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = unidentifiableUser }
        };

        var request = new ApprovePlanRequest(ApproverId: null, ApproverRole: null, Comments: "Sneaky approval");

        // Act
        var result = await controller.ApprovePlan(jobId.ToString(), request);

        // Assert: 401 Unauthorized
        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ApprovePlan_RejectsCallerClaimingOperatorId_WithoutOperatorRole_WhenPolicyIsHumanOnly()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Mission Critical Job",
            ApprovalPolicy = PlanApprovalPolicy.HumanOnly,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // Caller claims agent_id is "operator" but has no Operator role claim
        var impersonatingUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "operator"),
            new Claim("agent_id", "operator")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = impersonatingUser }
        };

        var request = new ApprovePlanRequest(ApproverId: "operator", ApproverRole: "operator", Comments: "I claim to be operator");

        // Act
        var result = await controller.ApprovePlan(jobId.ToString(), request);

        // Assert: 403 Forbidden under HumanOnly policy (cannot claim operator role from ID alone)
        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        forbidden.Value.ToString().Should().Contain("human_only_policy");
    }

    [Fact]
    public async Task ApprovePlan_RejectsAssignedImplementer_SelfApproval()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Lead Job",
            AssignedAgentId = "tech-lead-1",
            ApprovalPolicy = PlanApprovalPolicy.LeadOrHuman,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // Implementer is also a tech lead
        var implementerLead = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "tech-lead-1"),
            new Claim("agent_id", "tech-lead-1"),
            new Claim(ClaimTypes.Role, "tech_lead")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = implementerLead }
        };

        var request = new ApprovePlanRequest(ApproverId: "tech-lead-1", Comments: "Self approval from assigned lead");

        // Act
        var result = await controller.ApprovePlan(jobId.ToString(), request);

        // Assert: 403 Forbidden (implementer cannot approve their own plan)
        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        forbidden.Value.ToString().Should().Contain("self_approval_not_allowed");
    }

    [Fact]
    public async Task SubmitReview_RejectsSelfReview_WhenAuthorIsUnset_AndReviewerIsAgent()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Autonomous Job Without Explicit Author",
            AssignedAgentId = null,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // An agent attempts to review PR when author is unrecorded
        var agentReviewer = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "any-agent"),
            new Claim("agent_id", "any-agent")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = agentReviewer }
        };

        var request = new SubmitJobPrReviewRequest(ReviewerId: "any-agent", IsApproved: true, ReviewComments: "Agent review", PrNumber: 42);

        // Act
        var result = await controller.SubmitReview(jobId.ToString(), request);

        // Assert: 403 Forbidden (unverifiable author requires operator sign-off)
        var forbidden = result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        forbidden.Value.ToString().Should().Contain("unverifiable_author");
    }

    [Fact]
    public async Task SubmitReview_AllowsOperator_WhenAuthorIsUnset()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Autonomous Job Without Explicit Author",
            AssignedAgentId = null,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobReviewsController>.Instance
        );

        // Operator reviews PR when author is unrecorded
        var operatorUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "human-operator"),
            new Claim(ClaimTypes.Role, "Operator")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = operatorUser }
        };

        var request = new SubmitJobPrReviewRequest(ReviewerId: "human-operator", IsApproved: true, ReviewComments: "Operator approval", PrNumber: 42);

        // Act
        var result = await controller.SubmitReview(jobId.ToString(), request);

        // Assert: 200 OK
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ApprovePlan_WhenTemporalIsNull_DispatchesImplementationTurn()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var project = new Project
        {
            Id = "proj-1",
            Name = "Approval Test Project",
            WorkspaceType = AgentMesh.Core.Enums.WorkspaceType.Git
        };
        db.Projects.Add(project);

        var persona = new Persona
        {
            Id = "rook",
            Name = "Rook",
            Role = "Implementer"
        };
        db.Personas.Add(persona);

        var tasks = new List<JobTaskDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Implement Feature X",
                IsCompleted = false
            }
        };

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Mission Critical Job",
            ApprovalPolicy = PlanApprovalPolicy.HumanOnly,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active,
            CurrentTaskId = "stage-1-discovery",
            ProgressHeartbeat = "Awaiting plan approval (Stage 2 gate)",
            AssignedAgentId = "rook",
            TasksJson = JsonSerializer.Serialize(tasks)
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockDispatcher = new Mock<JobTurnDispatcher>(db, null, null, null);
        mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<JobTurnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchedJobTurn(new AgentSession(), new SessionMessage(), new Agent()));

        var controller = new JobReviewsController(
            db,
            temporalClient: null,
            hubContext: _mockHubContext.Object,
            logger: NullLogger<JobReviewsController>.Instance,
            turnDispatcher: mockDispatcher.Object,
            workflows: null
        );

        var operatorUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "human-operator"),
            new Claim(ClaimTypes.Role, "Operator")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = operatorUser }
        };

        var request = new ApprovePlanRequest(ApproverId: "human-operator", ApproverRole: "operator", Comments: "LGTM! Ship it.");

        // Act
        var result = await controller.ApprovePlan(jobId.ToString(), request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        // Verify job state updated
        var updatedJob = await db.ProjectJobs.FindAsync(jobId);
        updatedJob.Should().NotBeNull();
        updatedJob!.ProgressHeartbeat.Should().Be("Plan approved. Starting implementation");
        updatedJob.CurrentTaskId.Should().Be(tasks[0].Id.ToString("D"));

        // Verify implementation turn was dispatched
        mockDispatcher.Verify(d => d.DispatchAsync(
            It.Is<JobTurnRequest>(r =>
                r.Job.Id == jobId &&
                r.Persona.Id == "rook" &&
                r.TaskId == tasks[0].Id.ToString("D") &&
                r.Prompt.Contains("Implement Feature X")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApprovePlan_AllowsAuthenticatedHumanWithEmail_UnderHumanOnlyPolicy()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var project = new Project
        {
            Id = "proj-email-auth",
            Name = "Email Auth Project",
            WorkspaceType = AgentMesh.Core.Enums.WorkspaceType.Git
        };
        db.Projects.Add(project);

        var persona = new Persona
        {
            Id = "rook",
            Name = "Rook",
            Role = "Implementer"
        };
        db.Personas.Add(persona);

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Human Only Plan Approval Job",
            ApprovalPolicy = PlanApprovalPolicy.HumanOnly,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active,
            CurrentTaskId = "stage-1-discovery",
            ProgressHeartbeat = "Awaiting plan approval (Stage 2 gate)",
            AssignedAgentId = "rook"
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobReviewsController(
            db,
            temporalClient: null,
            hubContext: _mockHubContext.Object,
            logger: NullLogger<JobReviewsController>.Instance,
            turnDispatcher: null,
            workflows: null
        );

        // User logged in via OIDC with email cdkingston@gmail.com
        var humanUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "cdkingston@gmail.com"),
            new Claim(ClaimTypes.Email, "cdkingston@gmail.com"),
            new Claim(ClaimTypes.Name, "cdkingston")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = humanUser }
        };

        var request = new ApprovePlanRequest(ApproverId: "cdkingston@gmail.com", ApproverRole: "operator", Comments: "Plan approved by operator");

        // Act
        var result = await controller.ApprovePlan(jobId.ToString(), request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        var updatedJob = await db.ProjectJobs.FindAsync(jobId);
        updatedJob!.ProgressHeartbeat.Should().Be("Plan approved. Starting implementation");
    }

    [Fact]
    public async Task ApprovePlan_WhenTemporalThrows_ProceedsWithDirectDispatchWithout502()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var project = new Project
        {
            Id = "proj-fallback",
            Name = "Fallback Test Project",
            WorkspaceType = AgentMesh.Core.Enums.WorkspaceType.Git
        };
        db.Projects.Add(project);

        var persona = new Persona
        {
            Id = "rook",
            Name = "Rook",
            Role = "Implementer"
        };
        db.Personas.Add(persona);

        var tasks = new List<JobTaskDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Implement Feature Y",
                IsCompleted = false
            }
        };

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Mission Critical Job With Temporal Down",
            ApprovalPolicy = PlanApprovalPolicy.HumanOnly,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active,
            CurrentTaskId = "stage-1-discovery",
            ProgressHeartbeat = "Awaiting plan approval (Stage 2 gate)",
            AssignedAgentId = "rook",
            TasksJson = JsonSerializer.Serialize(tasks)
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockDispatcher = new Mock<JobTurnDispatcher>(db, null, null, null);
        mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<JobTurnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchedJobTurn(new AgentSession(), new SessionMessage(), new Agent()));

        var mockRouter = new Mock<IWorkflowSignalRouter>();
        mockRouter
            .Setup(r => r.SendPlanApprovalAsync(It.IsAny<Guid>(), It.IsAny<PlanApprovedSignal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalDelivery($"job-{jobId:D}", SignalDeliveryStatus.NoRunningWorkflow));
        mockRouter
            .Setup(r => r.IsWorkflowDrivingAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Note: non-null temporalClient present (like production)
        var controller = new JobReviewsController(
            db,
            temporalClient: _mockTemporalClient.Object,
            hubContext: _mockHubContext.Object,
            logger: NullLogger<JobReviewsController>.Instance,
            turnDispatcher: mockDispatcher.Object,
            workflows: mockRouter.Object
        );

        var operatorUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "human-operator"),
            new Claim(ClaimTypes.Role, "Operator")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = operatorUser }
        };

        var request = new ApprovePlanRequest(ApproverId: "human-operator", ApproverRole: "operator");

        // Act
        var result = await controller.ApprovePlan(jobId.ToString(), request);

        // Assert: 200 OK (not 502)
        result.Should().BeOfType<OkObjectResult>();

        // Verify implementation turn was dispatched directly
        mockDispatcher.Verify(d => d.DispatchAsync(
            It.Is<JobTurnRequest>(r =>
                r.Job.Id == jobId &&
                r.Persona.Id == "rook" &&
                r.TaskId == tasks[0].Id.ToString("D")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
