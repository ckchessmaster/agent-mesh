using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// Repro for the three driver-tab defects (jobs 3/4/5 of the misc-fixes batch):
///   1. The usage panel renders invented quota numbers when the provider probe cannot run.
///   2. Persona runners mint a second row in `agents` per persona, so every driver shows up twice.
///   3. Drivers carry a persona-style `role` and get renamed to the persona that runs on them.
/// </summary>
public class DriverIdentityReproTests
{
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();

    [Fact]
    public async Task GetAgents_DoesNotListPersonaShadowRows_AsDrivers()
    {
        // Arrange: a real driver, plus the shadow row the container manager used to mint for the
        // persona that runs on it (same persona name, same persona role).
        using var db = TestDbContextFactory.Create();
        db.Agents.AddRange(
            new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver", RuntimeType = RuntimeType.CliAgy },
            new Agent
            {
                Id = "rook",
                Name = "Rook",
                Role = "Software Engineer",
                RuntimeType = RuntimeType.CliAgy,
                CapabilitiesJson = """{"personaId":"rook","role":"persona-runner"}"""
            });
        db.Personas.Add(new Persona
        {
            Id = "rook",
            Name = "Rook",
            Role = "Software Engineer",
            DriverId = "agy"
        });
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService);

        // Act
        var result = await controller.GetAgents();

        // Assert
        var drivers = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<AgentDto>>().Subject.ToList();

        drivers.Select(d => d.Id).Should().BeEquivalentTo(new[] { "agy" },
            because: "@rook is a persona, not a driver; it only exists in `agents` to hold a runner token");
    }

    [Fact]
    public async Task RegisterAgent_WithoutRole_DoesNotAssignAPersonaStyleRole()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new AgentsController(db, _apiKeyService);

        await controller.RegisterAgent(new RegisterAgentRequest(
            Id: "ollama-local",
            Name: "Local Ollama Server"));

        var row = await db.Agents.FindAsync("ollama-local");
        row.Should().NotBeNull();
        row!.Role.Should().Be("Driver",
            because: "role is a persona concept; drivers should only carry a placeholder for legacy consumers");
    }

    [Fact]
    public async Task CreatePersona_AutoCreatedDriver_ShouldNotCarryPersonaRoleOrSyntheticPrefix()
    {
        using var db = TestDbContextFactory.Create();
        var personasController = new PersonasController(db);

        await personasController.CreatePersona(new CreatePersonaRequest(
            Name: "Rook",
            DriverId: "mystery-runner",
            Id: "rook"));

        var driver = await db.Agents.FindAsync("mystery-runner");
        driver.Should().NotBeNull();
        driver!.Role.Should().Be("Driver");
        driver.Name.Should().NotStartWith("Driver ",
            because: "the card renders @id and the name side by side, so the prefix is duplicated noise");
    }

    [Fact]
    public async Task GetAgents_HidesLegacyRunnerRows_ThatCarryNoMarker()
    {
        // This is the state of an existing deployment: the shadow rows were created before any
        // marker existed, so they can only be recognised by joining against `personas`.
        using var db = TestDbContextFactory.Create();
        db.Agents.AddRange(
            new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver", RuntimeType = RuntimeType.CliAgy },
            new Agent { Id = "rook", Name = "Rook", Role = "Software Engineer", RuntimeType = RuntimeType.CliAgy },
            new Agent { Id = "sorrn", Name = "Sorrn", Role = "Technical Architect", RuntimeType = RuntimeType.CliAgy });
        db.Personas.AddRange(
            new Persona { Id = "rook", Name = "Rook", Role = "Software Engineer", DriverId = "agy" },
            new Persona { Id = "sorrn", Name = "Sorrn", Role = "Technical Architect", DriverId = "agy" });
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService);

        var drivers = (await controller.GetAgents()).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<AgentDto>>().Subject.ToList();

        drivers.Select(d => d.Id).Should().BeEquivalentTo(new[] { "agy" },
            because: "both shadow rows are personas hosted by @agy, which is exactly the duplicate the operator reported");

        // ...and the personas are still reachable, listed under the driver that hosts them.
        var agy = drivers.Single();
        agy.Personas.Should().NotBeNull();
        agy.Personas!.Select(p => p.Id).Should().BeEquivalentTo(new[] { "rook", "sorrn" });
    }

    [Fact]
    public async Task GetAgents_CanStillListRunnerIdentities_OnDemand()
    {
        // Escape hatch for tooling that needs the full identity table (token issuance, support).
        using var db = TestDbContextFactory.Create();
        db.Agents.AddRange(
            new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver" },
            new Agent { Id = "rook", Name = "Rook", Role = "Software Engineer", CapabilitiesJson = """{"role":"persona-runner"}""" });
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService);

        var all = (await controller.GetAgents(includeRunnerIdentities: true)).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<AgentDto>>().Subject.ToList();

        all.Select(d => d.Id).Should().BeEquivalentTo(new[] { "agy", "rook" });
    }

    [Fact]
    public async Task GetAgent_StillResolvesARunnerIdentityById()
    {
        // Hiding the row from the list must not break auth or per-agent lookups: the persona still
        // calls the API with its own token and expects to resolve itself.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "rook",
            Name = "Rook",
            Role = "Software Engineer",
            CapabilitiesJson = """{"role":"persona-runner","personaId":"rook"}"""
        });
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService);

        var result = await controller.GetAgent("rook");
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAgentUsage_WithoutProviderIntrospection_ReturnsNoQuotaRatherThanInventedNumbers()
    {
        // Arrange: an id that used to trip the "agy" heuristic and return the hardcoded
        // 88% / weekly / 5h fallback panel whenever the binary was not callable.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy",
            Name = "Antigravity CLI",
            Role = "Driver",
            RuntimeType = RuntimeType.CliAgy
        });
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService);

        // Act
        var result = await controller.GetAgentUsage("agy", refresh: true);

        // Assert
        var usage = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AgentUsageResponse>().Subject;

        usage.ProviderQuota.Should().BeNull(
            because: "a fabricated quota panel is indistinguishable from a real one in the UI");
    }

    [Fact]
    public async Task GetAgentUsage_QueriesTheInjectedProbe_AndForwardsRefresh()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy",
            Name = "Antigravity CLI",
            Role = "Driver",
            RuntimeType = RuntimeType.CliAgy,
            CapabilitiesJson = """{"adapter":"agy"}"""
        });
        await db.SaveChangesAsync();

        var probe = new RecordingQuotaProbe();
        var controller = new AgentsController(db, _apiKeyService, hubContext: null, quotaProbe: probe);

        await controller.GetAgentUsage("agy", refresh: false);
        probe.Requests.Should().ContainSingle(r =>
            r.DriverId == "agy" && r.Adapter == "agy" && !r.ForceRefresh);

        await controller.GetAgentUsage("agy", refresh: true);
        probe.Requests.Last().ForceRefresh.Should().BeTrue(
            because: "an explicit user refresh must bypass the probe's TTL cache");
    }

    [Fact]
    public async Task GetAgentUsage_MapsARealReadingOntoTheResponse()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver", RuntimeType = RuntimeType.CliAgy });
        await db.SaveChangesAsync();

        var observed = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        var probe = new RecordingQuotaProbe
        {
            Reading = new DriverQuotaReading(
                Provider: "Google Antigravity",
                PlanName: "Antigravity Pro",
                Source: "agy:/usage",
                ObservedAt: observed,
                PrimaryRemainingFraction: 0.62,
                Groups: new[]
                {
                    new DriverQuotaGroup("Gemini", "Google models", new[]
                    {
                        new DriverQuotaBucket("gemini-weekly", "Weekly Limit", null, "weekly", 0.62, observed.AddDays(5))
                    })
                })
        };

        var controller = new AgentsController(db, _apiKeyService, hubContext: null, quotaProbe: probe);

        var usage = (await controller.GetAgentUsage("agy", refresh: false)).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AgentUsageResponse>().Subject;

        var quota = usage.ProviderQuota;
        quota.Should().NotBeNull();
        quota!.Provider.Should().Be("Google Antigravity");
        quota.PlanName.Should().Be("Antigravity Pro");
        quota.PrimaryRemainingFraction.Should().BeApproximately(0.62, 0.0001);
        quota.Source.Should().Be("agy:/usage");
        quota.ObservedAt.Should().Be(observed);
        quota.Groups.Should().ContainSingle()
            .Which.Buckets.Should().ContainSingle()
            .Which.ResetTime.Should().Be(observed.AddDays(5).ToString("u"));
    }

    [Fact]
    public async Task GetAgentUsage_ReportsMeshTrackedTokens_EvenWhenQuotaIsUnavailable()
    {
        // Token accounting comes from session metadata this mesh recorded itself, so it stays
        // truthful regardless of whether the provider exposed quota.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "copilot", Name = "GitHub Copilot CLI", Role = "Driver", RuntimeType = RuntimeType.CliAgy });
        db.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            DriverId = "copilot",
            Title = "s",
            Status = "completed",
            MetadataJson = """{"cumulativeUsage":{"inputTokens":1200,"outputTokens":300,"totalTokens":1500,"totalDurationSeconds":12.5}}"""
        });
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, _apiKeyService, hubContext: null, quotaProbe: NullDriverQuotaProbe.Instance);

        var usage = (await controller.GetAgentUsage("copilot", refresh: false)).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AgentUsageResponse>().Subject;

        usage.ProviderQuota.Should().BeNull();
        usage.LifetimeTokens.TotalTokens.Should().Be(1500);
        usage.SessionCount.Should().Be(1);
    }

    [Fact]
    public void Program_RegistersTheQuotaProbeSoTheControllerActuallyReceivesIt()
    {
        // Guards the wiring, not just the logic. `IDriverQuotaProbe` is an *optional* controller
        // dependency, so if the registration is ever dropped the controller silently falls back to
        // NullDriverQuotaProbe: quota vanishes in production while every unit test still passes.
        var program = File.ReadAllText(FindRepoFile("src/AgentMesh.Api/Program.cs"));

        program.Should().Contain("AddSingleton<IDriverQuotaProbe>",
            because: "the drivers tab only shows quota if a probe is resolved from DI");
        program.Should().Contain("AddSingleton<AgyDriverQuotaProbe>");
    }

    [Fact]
    public async Task AgyProbe_DegradesToNoQuota_WhenTheBinaryIsAbsentOnTheApiHost()
    {
        // The production probe, end to end through the controller, on a host with no signed-in CLI.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy",
            Name = "Antigravity CLI",
            Role = "Driver",
            RuntimeType = RuntimeType.CliAgy,
            CapabilitiesJson = """{"adapter":"agy"}"""
        });
        await db.SaveChangesAsync();

        var unreachable = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => null,                       // no agy on PATH
            (_, _, _, _) => Task.FromResult<string?>("must not run"));

        var controller = new AgentsController(db, _apiKeyService, hubContext: null, quotaProbe: unreachable);

        var usage = (await controller.GetAgentUsage("agy", refresh: false)).Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<AgentUsageResponse>().Subject;

        usage.ProviderQuota.Should().BeNull();
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgentMesh.slnx")))
        {
            dir = dir.Parent;
        }

        var full = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), relativePath);
        return File.Exists(full) ? full : throw new FileNotFoundException($"could not locate {relativePath} from test assembly", full);
    }

    private sealed class RecordingQuotaProbe : IDriverQuotaProbe
    {
        public List<DriverQuotaProbeRequest> Requests { get; } = new();
        public DriverQuotaReading? Reading { get; init; }

        public Task<DriverQuotaReading?> ProbeAsync(DriverQuotaProbeRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Reading);
        }
    }
}
