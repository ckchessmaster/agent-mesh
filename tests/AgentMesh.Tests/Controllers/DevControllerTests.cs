using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
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
    public async Task ResetMesh_PurgesAllAgentsSessionsAndEvents()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var agent = new Agent { Id = "agy-core", Name = "Agy Lead", Role = "Architect" };
        var session = new AgentSession { Id = Guid.NewGuid(), Title = "Test Session", CreatorAgentId = "agy-core" };
        var message = new SessionMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            SenderAgentId = "agy-core",
            EventType = "thought",
            Content = "Reasoning..."
        };

        db.Agents.Add(agent);
        db.AgentSessions.Add(session);
        db.SessionMessages.Add(message);
        await db.SaveChangesAsync();

        var controller = new DevController(db, _mockHubContext.Object);

        // Act
        var resetResult = await controller.ResetMesh();

        // Assert
        resetResult.Should().BeOfType<OkObjectResult>();
        db.Agents.Should().BeEmpty();
        db.AgentSessions.Should().BeEmpty();
        db.SessionMessages.Should().BeEmpty();
    }
}
