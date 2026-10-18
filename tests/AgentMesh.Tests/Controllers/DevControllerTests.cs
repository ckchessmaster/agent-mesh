using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class DevControllerTests
{
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;

    public DevControllerTests()
    {
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
    }

    [Fact]
    public async Task SeedDemo_PopulatesDemoAgentSessionAndTrajectoryEvents_ThenResetMeshPurgesAll()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new DevController(db, _mockHubContext.Object);

        // Act 1: Seed Demo
        var seedResult = await controller.SeedDemo();

        // Assert 1: Seeded
        seedResult.Should().BeOfType<OkObjectResult>();
        db.Agents.Should().ContainSingle(a => a.Id == "agy-core");
        db.AgentSessions.Should().ContainSingle(s => s.CreatorAgentId == "agy-core");
        db.SessionMessages.Should().HaveCount(8);
        db.SessionMessages.Select(m => m.EventType).Should().Contain(new[]
        {
            "prompt", "thought", "tool_call", "tool_result", "response"
        });

        // Act 2: Reset Mesh
        var resetResult = await controller.ResetMesh();

        // Assert 2: Purged
        resetResult.Should().BeOfType<OkObjectResult>();
        db.Agents.Should().BeEmpty();
        db.AgentSessions.Should().BeEmpty();
        db.SessionMessages.Should().BeEmpty();
    }
}
