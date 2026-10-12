using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class AgentsControllerTests
{
    [Fact]
    public async Task GetAgents_ReturnsAllAgents_OrderedByName()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var a1 = new Agent { Id = "z-agent", Name = "Zeta Agent", TrustTier = TrustTier.Intern, RuntimeType = RuntimeType.LocalOllama };
        var a2 = new Agent { Id = "a-agent", Name = "Alpha Agent", TrustTier = TrustTier.LeadHuman, RuntimeType = RuntimeType.CliAgy };
        db.Agents.AddRange(a1, a2);
        await db.SaveChangesAsync();

        var controller = new AgentsController(db);

        // Act
        var result = await controller.GetAgents();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var agents = okResult.Value.Should().BeAssignableTo<IEnumerable<Agent>>().Subject.ToList();

        agents.Should().HaveCount(2);
        agents[0].Name.Should().Be("Alpha Agent");
        agents[1].Name.Should().Be("Zeta Agent");
    }

    [Fact]
    public async Task GetAgent_ExistingId_ReturnsOkWithAgent()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var agent = new Agent { Id = "spark-1", Name = "Spark Agent", TrustTier = TrustTier.SeniorManager, RuntimeType = RuntimeType.McpCloud };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var controller = new AgentsController(db);

        // Act
        var result = await controller.GetAgent("spark-1");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<Agent>().Subject;
        returned.Id.Should().Be("spark-1");
    }

    [Fact]
    public async Task GetAgent_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new AgentsController(db);

        // Act
        var result = await controller.GetAgent("non-existent-agent");

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }
}
