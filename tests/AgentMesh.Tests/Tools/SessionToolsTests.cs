using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Tools;

public class SessionToolsTests
{
    private readonly Mock<IApiKeyService> _mockApiKeyService;
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;

    public SessionToolsTests()
    {
        _mockApiKeyService = new Mock<IApiKeyService>();
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
    }

    [Fact]
    public async Task GetPersonaSessions_ReturnsOnlySessionsForRequestedPersona_WithArtifacts()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var sid1 = Guid.NewGuid();
        var sid2 = Guid.NewGuid();

        db.AgentSessions.AddRange(
            new AgentSession
            {
                Id = sid1,
                Title = "Astra Architecture Review",
                PersonaId = "astra",
                DriverId = "agy",
                Status = "active",
                MetadataJson = "{\"artifacts\":[{\"id\":\"art-1\",\"name\":\"arch-plan.md\",\"contentType\":\"text/markdown\",\"summary\":\"Architecture plan\"}]}"
            },
            new AgentSession
            {
                Id = sid2,
                Title = "Other Persona Task",
                PersonaId = "other-persona",
                DriverId = "copilot",
                Status = "completed"
            }
        );
        await db.SaveChangesAsync();

        var tools = new SessionTools(
            db,
            _mockApiKeyService.Object,
            NullLogger<SessionTools>.Instance,
            null,
            _mockHubContext.Object);

        // Act
        var resultJson = await tools.GetPersonaSessions("astra");

        // Assert
        using var doc = JsonDocument.Parse(resultJson);
        var array = doc.RootElement.EnumerateArray().ToList();

        array.Should().HaveCount(1);
        var item = array[0];
        item.GetProperty("id").GetString().Should().Be(sid1.ToString());
        item.GetProperty("title").GetString().Should().Be("Astra Architecture Review");
        item.GetProperty("personaId").GetString().Should().Be("astra");
        item.GetProperty("artifactCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetSession_IncludesExtractedArtifacts()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var sid = Guid.NewGuid();

        db.AgentSessions.Add(new AgentSession
        {
            Id = sid,
            Title = "Session With Artifacts",
            PersonaId = "astra",
            MetadataJson = "{\"artifacts\":[{\"id\":\"art-1\",\"name\":\"design.md\",\"contentType\":\"text/markdown\",\"summary\":\"Design doc\"}]}"
        });
        await db.SaveChangesAsync();

        var tools = new SessionTools(
            db,
            _mockApiKeyService.Object,
            NullLogger<SessionTools>.Instance,
            null,
            _mockHubContext.Object);

        // Act
        var resultJson = await tools.GetSession(sid.ToString());

        // Assert
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        root.GetProperty("id").GetString().Should().Be(sid.ToString());
        root.GetProperty("personaId").GetString().Should().Be("astra");
        root.GetProperty("artifacts").GetArrayLength().Should().Be(1);
        root.GetProperty("artifacts")[0].GetProperty("name").GetString().Should().Be("design.md");
    }

    [Fact]
    public async Task ListSessions_FiltersByPersonaId()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        db.AgentSessions.AddRange(
            new AgentSession { Id = Guid.NewGuid(), Title = "Astra 1", PersonaId = "astra" },
            new AgentSession { Id = Guid.NewGuid(), Title = "Astra 2", PersonaId = "astra" },
            new AgentSession { Id = Guid.NewGuid(), Title = "Spark 1", PersonaId = "spark" }
        );
        await db.SaveChangesAsync();

        var tools = new SessionTools(
            db,
            _mockApiKeyService.Object,
            NullLogger<SessionTools>.Instance,
            null,
            _mockHubContext.Object);

        // Act
        var resultJson = await tools.ListSessions(status: "all", personaId: "astra");

        // Assert
        using var doc = JsonDocument.Parse(resultJson);
        var array = doc.RootElement.EnumerateArray().ToList();
        array.Should().HaveCount(2);
        array.All(e => e.GetProperty("personaId").GetString() == "astra").Should().BeTrue();
    }
}
