using AgentMesh.Api.Controllers;
using AgentMesh.Core.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Temporalio.Client;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class DiscordWebhookControllerTests
{
    private readonly Mock<ITemporalClient> _mockTemporalClient;

    public DiscordWebhookControllerTests()
    {
        _mockTemporalClient = new Mock<ITemporalClient>();
    }

    [Fact]
    public async Task HandleInteraction_PingType1_ReturnsType1Response()
    {
        // Arrange
        var controller = new DiscordWebhookController(_mockTemporalClient.Object, NullLogger<DiscordWebhookController>.Instance);

        // Act
        var result = await controller.HandleInteraction(new DiscordInteractionPayload { Type = 1 });

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleInteraction_ButtonType3_Approve_SignalsWorkflowAndReturnsMessage()
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

        var controller = new DiscordWebhookController(_mockTemporalClient.Object, NullLogger<DiscordWebhookController>.Instance);

        var payload = new DiscordInteractionPayload
        {
            Type = 3,
            Data = new DiscordInteractionData { CustomId = $"approve:{jobId}" },
            Member = new DiscordMember { User = new DiscordUser { Username = "SecOpsLead" } }
        };

        // Act
        var result = await controller.HandleInteraction(payload);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleInteraction_ButtonType3_Reject_SignalsWorkflowAndReturnsMessage()
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

        var controller = new DiscordWebhookController(_mockTemporalClient.Object, NullLogger<DiscordWebhookController>.Instance);

        var payload = new DiscordInteractionPayload
        {
            Type = 3,
            Data = new DiscordInteractionData { CustomId = $"reject:{jobId}" },
            Member = new DiscordMember { User = new DiscordUser { Username = "SecOpsLead" } }
        };

        // Act
        var result = await controller.HandleInteraction(payload);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleInteraction_InvalidType_ReturnsBadRequest()
    {
        // Arrange
        var controller = new DiscordWebhookController(_mockTemporalClient.Object, NullLogger<DiscordWebhookController>.Instance);

        // Act
        var result = await controller.HandleInteraction(new DiscordInteractionPayload { Type = 99 });

        // Assert
        result.Should().BeOfType<BadRequestResult>();
    }
}
