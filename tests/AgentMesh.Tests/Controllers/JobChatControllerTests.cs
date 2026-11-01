using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
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
    public async Task JobChat_SignalWithStart_LaunchesWorkflow()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Autonomous Job",
            GitBranchName = "feat/test",
            AssignedAgentId = "implementer-1",
            ApprovalPolicy = PlanApprovalPolicy.LeadOrHuman,
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        WorkflowOptions? capturedOptions = null;

        var mockHandle = new Mock<WorkflowHandle<JobExecutionWorkflow, JobExecutionResult>>(
            _mockTemporalClient.Object,
            $"job-{jobId}",
            (string?)null,
            (string?)null,
            (string?)null);

        _mockTemporalClient
            .Setup(c => c.StartWorkflowAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<JobExecutionWorkflow, Task<JobExecutionResult>>>>(),
                It.IsAny<WorkflowOptions>()))
            .Callback<System.Linq.Expressions.Expression<Func<JobExecutionWorkflow, Task<JobExecutionResult>>>, WorkflowOptions>(
                (expr, opts) =>
                {
                    capturedOptions = opts;
                })
            .ReturnsAsync(mockHandle.Object);

        var controller = new JobChatController(
            db,
            _mockTemporalClient.Object,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
        );

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "developer"),
            new Claim("agent_id", "operator")
        ], "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        var request = new PostMessageRequest("Please build the new feature.", null);

        // Act
        var result = await controller.PostChatMessage(jobId.ToString(), request);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        // Verify temporal workflow was started with SignalWithStart configuration
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Id.Should().Be($"job-{jobId}");
        capturedOptions.TaskQueue.Should().Be("job-execution-queue");
        capturedOptions.StartSignal.Should().Be("ReceiveChatMessage");
        capturedOptions.StartSignalArgs.Should().NotBeNull();
        capturedOptions.StartSignalArgs!.Should().HaveCount(1);

        var firstArg = capturedOptions.StartSignalArgs!.First();
        firstArg.Should().BeOfType<ChatMessageSignal>();
        var chatSignal = (ChatMessageSignal)firstArg!;
        chatSignal.Content.Should().Be("Please build the new feature.");
    }

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

        var controller = new JobChatController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
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

        var controller = new JobChatController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
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

        var controller = new JobChatController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
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

        var controller = new JobChatController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
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

        var controller = new JobChatController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
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

        var controller = new JobChatController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
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

        var controller = new JobChatController(
            db,
            null,
            _mockHubContext.Object,
            NullLogger<JobChatController>.Instance
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
}
