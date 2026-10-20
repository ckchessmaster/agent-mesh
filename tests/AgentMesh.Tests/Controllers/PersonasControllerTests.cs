using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class PersonasControllerTests
{
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;

    public PersonasControllerTests()
    {
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
    }

    [Fact]
    public async Task CreatePersona_ValidRequest_CreatesPersonaAndAutoCreatesDriver()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new PersonasController(db);

        var req = new CreatePersonaRequest(
            Name: "Agy Architect",
            DriverId: "agy",
            Id: "agy-architect",
            Role: "Principal Systems Architect",
            PersonaPrompt: "Design distributed microservices.",
            DefaultModel: "gemini-4-argon",
            DefaultEffort: "high",
            DefaultMode: "plan"
        );

        // Act
        var result = await controller.CreatePersona(req);

        // Assert
        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<PersonaDto>().Subject;

        dto.Id.Should().Be("agy-architect");
        dto.Name.Should().Be("Agy Architect");
        dto.Role.Should().Be("Principal Systems Architect");
        dto.DriverId.Should().Be("agy");
        dto.DefaultModel.Should().Be("gemini-4-argon");
        dto.DefaultEffort.Should().Be("high");
        dto.DefaultMode.Should().Be("plan");

        // Verify Driver was auto-created in db.Agents
        var driver = await db.Agents.FindAsync("agy");
        driver.Should().NotBeNull();
        driver!.Name.Should().Be("Antigravity CLI");
    }

    [Fact]
    public async Task GetPersonas_FiltersByDriverId_AndDriverIncludesPersonas()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var personasController = new PersonasController(db);
        var agentsController = new AgentsController(db, _apiKeyService);

        await personasController.CreatePersona(new CreatePersonaRequest("Agy Reviewer", "agy", "agy-reviewer", "Security Reviewer", DefaultModel: "gemini-4-argon"));
        await personasController.CreatePersona(new CreatePersonaRequest("Agy Coder", "agy", "agy-coder", "Backend Engineer", DefaultModel: "gpt-oss-120b"));
        await personasController.CreatePersona(new CreatePersonaRequest("Copilot Helper", "copilot", "copilot-helper", "Shell Assistant"));

        // Act
        var agyPersonasRes = await personasController.GetPersonas(driverId: "agy");
        var agyDriverRes = await agentsController.GetAgent("agy");

        // Assert
        var agyPersonas = agyPersonasRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<PersonaDto>>().Subject.ToList();
        agyPersonas.Should().HaveCount(2);
        agyPersonas.Select(p => p.Id).Should().Contain(["agy-reviewer", "agy-coder"]);

        var agyDriver = agyDriverRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AgentDto>().Subject;
        agyDriver.Personas.Should().NotBeNull();
        agyDriver.Personas!.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreatePersonaSession_SnapshotsPersonaDefaults_AndLinksBothPersonaAndDriver()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var personasController = new PersonasController(db);
        var agentsController = new AgentsController(db, _apiKeyService);
        var sessionsController = new SessionsController(db, _mockHubContext.Object);

        await personasController.CreatePersona(new CreatePersonaRequest(
            Name: "Agy Security Auditor",
            DriverId: "agy",
            Id: "sec-auditor",
            Role: "AppSec Engineer",
            PersonaPrompt: "Audit all auth flows.",
            DefaultModel: "gemini-4-argon",
            DefaultEffort: "high",
            DefaultMode: "plan",
            DefaultWorkingDirectory: "/workspace/sec"
        ));

        // Act - create session without explicit overrides so it inherits persona defaults
        var createRes = await personasController.CreatePersonaSession("sec-auditor", new CreateAgentSessionRequest(
            Title: "Audit OIDC Flow",
            InitialPrompt: "Check token validation"
        ));

        // Assert
        var created = createRes.Should().BeOfType<CreatedAtActionResult>().Subject;
        var session = created.Value.Should().BeOfType<AgentSession>().Subject;

        session.PersonaId.Should().Be("sec-auditor");
        session.DriverId.Should().Be("agy");
        session.CreatorAgentId.Should().Be("agy");

        using var doc = JsonDocument.Parse(session.MetadataJson);
        doc.RootElement.GetProperty("model").GetString().Should().Be("gemini-4-argon");
        doc.RootElement.GetProperty("effort").GetString().Should().Be("high");
        doc.RootElement.GetProperty("mode").GetString().Should().Be("plan");
        doc.RootElement.GetProperty("workingDirectory").GetString().Should().Be("/workspace/sec");
        doc.RootElement.GetProperty("personaId").GetString().Should().Be("sec-auditor");
        doc.RootElement.GetProperty("personaName").GetString().Should().Be("Agy Security Auditor");

        // Verify session appears at Persona level
        var personaSessionsRes = await personasController.GetPersonaSessions("sec-auditor");
        var personaSessions = personaSessionsRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<SessionSummaryDto>>().Subject.ToList();
        personaSessions.Should().ContainSingle(s => s.Id == session.Id && s.PersonaId == "sec-auditor" && s.DriverId == "agy");

        // Verify session appears at Driver level
        var driverSessionsRes = await agentsController.GetAgentSessions("agy");
        var driverSessions = driverSessionsRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<SessionSummaryDto>>().Subject.ToList();
        driverSessions.Should().ContainSingle(s => s.Id == session.Id && s.PersonaId == "sec-auditor" && s.DriverId == "agy");

        // Verify SessionsController filtering by personaId and driverId
        var byPersona = (await sessionsController.GetSessions(personaId: "sec-auditor")).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<SessionSummaryDto>>().Subject.ToList();
        byPersona.Should().ContainSingle(s => s.Id == session.Id);

        var byDriver = (await sessionsController.GetSessions(driverId: "agy")).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<SessionSummaryDto>>().Subject.ToList();
        byDriver.Should().ContainSingle(s => s.Id == session.Id);
    }

    [Fact]
    public async Task UpdatePersona_ChangingDriver_LeavesExistingSessionDriverIntact()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var personasController = new PersonasController(db);

        await personasController.CreatePersona(new CreatePersonaRequest(
            Name: "Multi Runtime Reviewer",
            DriverId: "agy",
            Id: "multi-reviewer"
        ));

        var sessionRes = await personasController.CreatePersonaSession("multi-reviewer", new CreateAgentSessionRequest("Initial Agy Session"));
        var existingSession = sessionRes.Should().BeOfType<CreatedAtActionResult>().Subject
            .Value.Should().BeOfType<AgentSession>().Subject;

        // Act - switch persona's driver from agy to copilot
        await personasController.UpdatePersona("multi-reviewer", new UpdatePersonaRequest(
            DriverId: "copilot",
            DefaultModel: "gpt-6.1-sol"
        ));

        var newSessionRes = await personasController.CreatePersonaSession("multi-reviewer", new CreateAgentSessionRequest("New Copilot Session"));
        var newSession = newSessionRes.Should().BeOfType<CreatedAtActionResult>().Subject
            .Value.Should().BeOfType<AgentSession>().Subject;

        // Assert - existing session keeps agy, new session uses copilot
        var reloadedOld = await db.AgentSessions.FindAsync(existingSession.Id);
        reloadedOld!.DriverId.Should().Be("agy");
        newSession.DriverId.Should().Be("copilot");
    }

    [Fact]
    public async Task DeletePersona_CascadesAndDeletesAssociatedSessions()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var personasController = new PersonasController(db);

        await personasController.CreatePersona(new CreatePersonaRequest("Temp Persona", "agy", "temp-persona"));
        var sRes = await personasController.CreatePersonaSession("temp-persona", new CreateAgentSessionRequest("Temp Session", "Hello"));
        var createdSession = sRes.Should().BeOfType<CreatedAtActionResult>().Subject.Value.Should().BeOfType<AgentSession>().Subject;

        // Act
        var delRes = await personasController.DeletePersona("temp-persona");

        // Assert
        delRes.Should().BeOfType<OkObjectResult>();
        (await db.Personas.FindAsync("temp-persona")).Should().BeNull();
        (await db.AgentSessions.FindAsync(createdSession.Id)).Should().BeNull();
    }
}
