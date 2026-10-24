using AgentMesh.Api.Hubs;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Hubs;

public class AgentMeshHubTests
{
    private readonly Mock<HubCallerContext> _mockContext;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;

    public AgentMeshHubTests()
    {
        _mockContext = new Mock<HubCallerContext>();
        _mockContext.Setup(c => c.ConnectionId).Returns("conn-123");

        _mockGroups = new Mock<IGroupManager>();
        _mockClients = new Mock<IHubCallerClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);

        AgentMeshHub.ClearConnections();
    }

    [Fact]
    public async Task RegisterPersonaRunner_RegistersPersonaConnection_AndAddsToGroup()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var hub = new AgentMeshHub(db)
        {
            Context = _mockContext.Object,
            Groups = _mockGroups.Object,
            Clients = _mockClients.Object
        };

        // Act
        await hub.RegisterPersonaRunner("system-architect", "agy");

        // Assert
        AgentMeshHub.IsPersonaConnected("system-architect").Should().BeTrue();
        AgentMeshHub.GetConnectedPersonaIds().Should().Contain("system-architect");

        _mockGroups.Verify(g => g.AddToGroupAsync("conn-123", "persona-system-architect", default), Times.Once);
        _mockGroups.Verify(g => g.AddToGroupAsync("conn-123", "agent-agy", default), Times.Once);

        // Act - disconnect
        await hub.OnDisconnectedAsync(null);

        // Assert - cleaned up
        AgentMeshHub.IsPersonaConnected("system-architect").Should().BeFalse();
        AgentMeshHub.GetConnectedPersonaIds().Should().NotContain("system-architect");
    }

    [Fact]
    public async Task RegisterPersonaRunner_CaseInsensitiveMatching()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var hub = new AgentMeshHub(db)
        {
            Context = _mockContext.Object,
            Groups = _mockGroups.Object,
            Clients = _mockClients.Object
        };

        // Act
        await hub.RegisterPersonaRunner("System-Architect");

        // Assert
        AgentMeshHub.IsPersonaConnected("system-architect").Should().BeTrue();
        AgentMeshHub.IsPersonaConnected("SYSTEM-ARCHITECT").Should().BeTrue();

        await hub.OnDisconnectedAsync(null);
    }
}
