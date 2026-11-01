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
            _mockTemporalClient.Object,
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
            _mockTemporalClient.Object,
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
}
