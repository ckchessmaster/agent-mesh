using System.Security.Claims;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Temporalio.Client;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class ApprovalControllerTests
{
    private readonly Mock<ITemporalClient> _mockTemporalClient;
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;

    public ApprovalControllerTests()
    {
        _mockTemporalClient = new Mock<ITemporalClient>();
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
    }

    [Fact]
    public async Task SubmitApproval_CallsWorkflowHandleSignal_AndBroadcastsSignalR()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var mockHandle = new Mock<WorkflowHandle<AgentTaskWorkflow>>(
            _mockTemporalClient.Object,
            $"job-{jobId}",
            (string?)null,
            (string?)null,
            (string?)null);

        _mockTemporalClient
            .Setup(c => c.GetWorkflowHandle<AgentTaskWorkflow>($"job-{jobId}", null))
            .Returns(mockHandle.Object);

        var controller = new ApprovalController(
            _mockTemporalClient.Object,
            _mockHubContext.Object,
            NullLogger<ApprovalController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "LeadDevReviewer")
                    }, "TestAuth"))
                }
            }
        };

        // Act
        var req = new ApprovalDecisionRequest(Approved: true, Reason: "Verified bounds");
        var result = await controller.SubmitApproval(jobId, req);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();

        _mockClients.Verify(c => c.All, Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("JobApprovalUpdated", It.Is<object[]>(args => args.Length == 1), default),
            Times.Once);
    }
}
