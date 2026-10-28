using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The dashboard's persona/driver cards collapse their session list and lazily expand it. The global
/// `GET /api/sessions` window is capped at 100 rows, so the per-owner endpoints are the only way to
/// see the whole history. These tests pin the contract that expansion relies on: an exact total for
/// the owner, opt-in paging, and an unchanged bare-array body for existing CLI consumers.
/// </summary>
public class SessionListPagingTests
{
    private readonly Mock<IHubContext<AgentMeshHub>> _hub = new();
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();

    public SessionListPagingTests()
    {
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.All).Returns(proxy.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        _hub.Setup(h => h.Clients).Returns(clients.Object);
    }

    private static AgentSession Session(string title, DateTimeOffset updatedAt, string? personaId, string driverId) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = "active",
            MetadataJson = "{}",
            PersonaId = personaId,
            DriverId = driverId,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };

    private (PersonasController Personas, AgentsController Agents) Controllers(AgentMeshDbContext db)
    {
        var personas = new PersonasController(db);
        var agents = new AgentsController(db, _apiKeyService, _hub.Object);

        // Response headers only exist when the action runs inside an HTTP context; the dashboard
        // reads X-Total-Count there, while direct unit calls must still work without one.
        foreach (var c in new ControllerBase[] { personas, agents })
        {
            c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        }

        return (personas, agents);
    }

    private static async Task<List<SessionSummaryDto>> ItemsAsync(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeAssignableTo<IEnumerable<SessionSummaryDto>>().Subject.ToList();
    }

    private async Task<(PersonasController Personas, AgentsController Agents, AgentMeshDbContext Db)> SeedAsync(int count)
    {
        var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "agy", Name = "Antigravity CLI" });
        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", Role = "Engineer", PersonaPrompt = "", DriverId = "agy" });

        var baseTime = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < count; i++)
        {
            db.AgentSessions.Add(Session($"S{i:00}", baseTime.AddMinutes(i), "rook", "agy"));
        }
        await db.SaveChangesAsync();

        var (personas, agents) = Controllers(db);
        return (personas, agents, db);
    }

    [Fact]
    public async Task GetPersonaSessions_WithoutPaging_ReturnsEverySession_AndReportsExactTotal()
    {
        var (personas, _, db) = await SeedAsync(12);
        using (db)
        {
            var items = await ItemsAsync(await personas.GetPersonaSessions("rook"));

            items.Should().HaveCount(12, "the CLI polls this endpoint un-paged and must keep seeing everything");
            personas.HttpContext!.Response.Headers["X-Total-Count"].ToString().Should().Be("12");
        }
    }

    [Fact]
    public async Task GetPersonaSessions_WithTake_SlicesNewestFirst_ButStillReportsFullTotal()
    {
        var (personas, _, db) = await SeedAsync(12);
        using (db)
        {
            var items = await ItemsAsync(await personas.GetPersonaSessions("rook", take: 5));

            items.Should().HaveCount(5);
            items.Select(s => s.Title).Should().Equal("S11", "S10", "S09", "S08", "S07");
            personas.HttpContext!.Response.Headers["X-Total-Count"].ToString().Should().Be("12");
        }
    }

    [Fact]
    public async Task GetPersonaSessions_WithTakeAndSkip_WalksTheWholeHistoryWithoutGaps()
    {
        var (personas, _, db) = await SeedAsync(12);
        using (db)
        {
            var page1 = await ItemsAsync(await personas.GetPersonaSessions("rook", take: 5, skip: 0));
            var page2 = await ItemsAsync(await personas.GetPersonaSessions("rook", take: 5, skip: 5));
            var page3 = await ItemsAsync(await personas.GetPersonaSessions("rook", take: 5, skip: 10));

            page1.Should().HaveCount(5);
            page2.Should().HaveCount(5);
            page3.Should().HaveCount(2, "the tail page returns whatever is left of the owner's history");

            var all = page1.Concat(page2).Concat(page3).Select(s => s.Id).ToList();
            all.Should().OnlyHaveUniqueItems();
            all.Should().HaveCount(12);
        }
    }

    [Fact]
    public async Task GetPersonaSessions_TotalCountExcludesOtherPersonas()
    {
        var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "agy", Name = "Antigravity CLI" });
        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", Role = "Engineer", PersonaPrompt = "", DriverId = "agy" });
        db.Personas.Add(new Persona { Id = "astra", Name = "Astra", Role = "Lead", PersonaPrompt = "", DriverId = "agy" });

        var baseTime = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 7; i++) db.AgentSessions.Add(Session($"R{i}", baseTime.AddMinutes(i), "rook", "agy"));
        for (var i = 0; i < 3; i++) db.AgentSessions.Add(Session($"A{i}", baseTime.AddMinutes(i), "astra", "agy"));
        await db.SaveChangesAsync();

        var (personas, _) = Controllers(db);
        using (db)
        {
            var items = await ItemsAsync(await personas.GetPersonaSessions("rook"));
            items.Should().HaveCount(7);
            personas.HttpContext!.Response.Headers["X-Total-Count"].ToString().Should().Be("7");
        }
    }

    [Fact]
    public async Task GetAgentSessions_WithoutPaging_ReturnsEverySession_AndReportsExactTotal()
    {
        var (_, agents, db) = await SeedAsync(12);
        using (db)
        {
            var items = await ItemsAsync(await agents.GetAgentSessions("agy"));
            items.Should().HaveCount(12);
            agents.HttpContext!.Response.Headers["X-Total-Count"].ToString().Should().Be("12");
        }
    }

    [Fact]
    public async Task GetAgentSessions_WithTake_SlicesNewestFirst_ButStillReportsFullTotal()
    {
        var (_, agents, db) = await SeedAsync(12);
        using (db)
        {
            var items = await ItemsAsync(await agents.GetAgentSessions("agy", take: 3, skip: 9));

            items.Select(s => s.Title).Should().Equal("S02", "S01", "S00");
            agents.HttpContext!.Response.Headers["X-Total-Count"].ToString().Should().Be("12");
        }
    }

    [Fact]
    public async Task GetPersonaSessions_NonHttpUnitCall_DoesNotThrowWhenNoResponseAvailable()
    {
        var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "agy", Name = "Antigravity CLI" });
        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", Role = "Engineer", PersonaPrompt = "", DriverId = "agy" });
        db.AgentSessions.Add(Session("S0", DateTimeOffset.UtcNow, "rook", "agy"));
        await db.SaveChangesAsync();

        // No ControllerContext at all: unit callers (and any future background caller) must not blow up.
        var personas = new PersonasController(db);
        using (db)
        {
            var items = await ItemsAsync(await personas.GetPersonaSessions("rook"));
            items.Should().ContainSingle();
        }
    }
}
