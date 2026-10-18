using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class AgentsControllerTests
{
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();

    [Fact]
    public async Task GetAgents_ReturnsAllAgents_OrderedByName()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var a1 = new Agent { Id = "z-agent", Name = "Zeta Agent", TrustTier = TrustTier.Intern, RuntimeType = RuntimeType.LocalOllama };
        var a2 = new Agent { Id = "a-agent", Name = "Alpha Agent", TrustTier = TrustTier.LeadHuman, RuntimeType = RuntimeType.CliAgy };
        db.Agents.AddRange(a1, a2);
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService);

        // Act
        var result = await controller.GetAgents();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var agents = okResult.Value.Should().BeAssignableTo<IEnumerable<AgentDto>>().Subject.ToList();

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

        var controller = new AgentsController(db, _apiKeyService);

        // Act
        var result = await controller.GetAgent("spark-1");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = okResult.Value.Should().BeOfType<AgentDto>().Subject;
        returned.Id.Should().Be("spark-1");
    }

    [Fact]
    public async Task GetAgent_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new AgentsController(db, _apiKeyService);

        // Act
        var result = await controller.GetAgent("non-existent-agent");

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task RegisterAgent_ValidRequest_CreatesAgentAndReturnsToken()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new AgentsController(db, _apiKeyService);
        var req = new RegisterAgentRequest("agy", "Antigravity CLI", RuntimeType.CliAgy, TrustTier.LeadHuman, "Persona", "Memory");

        // Act
        var result = await controller.RegisterAgent(req);

        // Assert
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.ActionName.Should().Be(nameof(AgentsController.GetAgent));

        var saved = await db.Agents.FindAsync("agy");
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Antigravity CLI");
        saved.ApiKeyHash.Should().NotBeNullOrWhiteSpace();
        saved.ApiKeyHash.Should().NotStartWith("mesh_tok_"); // must be hashed!
    }

    [Fact]
    public async Task RegisterAgent_DuplicateId_ReturnsConflict()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new AgentsController(db, _apiKeyService);
        var req = new RegisterAgentRequest("agy", "Antigravity CLI", RuntimeType.CliAgy, TrustTier.LeadHuman, "Persona", "Memory");

        await controller.RegisterAgent(req);

        // Act
        var result = await controller.RegisterAgent(req);

        // Assert
        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task GetAgentUsage_AggregatesLifetimeTokens_AcrossSessions()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var agent = new Agent { Id = "agy-test", Name = "Agy Test", TrustTier = TrustTier.SeniorManager, RuntimeType = RuntimeType.CliAgy };
        db.Agents.Add(agent);

        var s1 = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Session 1",
            CreatorAgentId = "agy-test",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                cumulativeUsage = new
                {
                    inputTokens = 1000L,
                    outputTokens = 200L,
                    thinkingTokens = 50L,
                    totalTokens = 1250L,
                    totalDurationSeconds = 2.5
                }
            })
        };
        var s2 = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Session 2",
            CreatorAgentId = "agy-test",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                cumulativeUsage = new
                {
                    inputTokens = 3000L,
                    outputTokens = 400L,
                    thinkingTokens = 100L,
                    totalTokens = 3500L,
                    totalDurationSeconds = 4.0
                }
            })
        };
        db.AgentSessions.AddRange(s1, s2);
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService);

        // Act
        var result = await controller.GetAgentUsage("agy-test");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var usage = okResult.Value.Should().BeOfType<AgentUsageResponse>().Subject;

        usage.AgentId.Should().Be("agy-test");
        usage.SessionCount.Should().Be(2);
        usage.LifetimeTokens.InputTokens.Should().Be(4000);
        usage.LifetimeTokens.OutputTokens.Should().Be(600);
        usage.LifetimeTokens.ThinkingTokens.Should().Be(150);
        usage.LifetimeTokens.TotalTokens.Should().Be(4750);
        usage.LifetimeTokens.TotalDurationSeconds.Should().Be(6.5);
    }
}
