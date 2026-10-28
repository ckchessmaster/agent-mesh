using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Containers;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Containers;

/// <summary>
/// The persona container managers mint an `agents` row for both the driver and the persona, so a
/// persona's bearer token has something to authenticate against. Those persona rows are not
/// drivers, and stamping them as such is what made the drivers tab list every driver twice - once
/// as the driver, once as a persona-named clone wearing the persona's role.
/// </summary>
public class PersonaRunnerIdentityProvisioningTests
{
    private const string Hash = "sha256:deadbeef";

    [Theory]
    [InlineData("Docker")]
    [InlineData("Kubernetes")]
    public async Task PersonaRow_IsStampedAsRunnerIdentity_NotAsADriver(string runtime)
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver" });
        await db.SaveChangesAsync();

        await SyncAsync(runtime, db, driverId: "agy", personaId: "rook",
            personaName: "Rook", personaRole: "Software Engineer");

        var personaRow = await db.Agents.FindAsync("rook");
        personaRow.Should().NotBeNull("the persona still needs a row so its token can authenticate");

        AgentKind.HasRunnerMarker(personaRow!.CapabilitiesJson).Should().BeTrue(
            because: "this is the marker GetAgents uses to keep the row off the drivers tab");
        personaRow.CapabilitiesJson.Should().Contain("\"personaId\":\"rook\"");

        // The driver row must be untouched: no rename, no role churn.
        var driver = await db.Agents.FindAsync("agy");
        driver!.Name.Should().Be("Antigravity CLI");
        driver.Role.Should().Be("Driver");
    }

    [Theory]
    [InlineData("Docker")]
    [InlineData("Kubernetes")]
    public async Task DriverRow_IsCreatedWithNeutralRole_WhenMissing(string runtime)
    {
        using var db = TestDbContextFactory.Create();

        await SyncAsync(runtime, db, driverId: "mystery", personaId: "sorrn",
            personaName: "Sorrn", personaRole: "Technical Architect");

        var driver = await db.Agents.FindAsync("mystery");
        driver.Should().NotBeNull();
        driver!.Role.Should().Be("Driver");
        AgentKind.HasRunnerMarker(driver.CapabilitiesJson).Should().BeFalse(
            because: "the driver itself is a real driver and must stay visible on the tab");

        var persona = await db.Agents.FindAsync("sorrn");
        persona!.Role.Should().Be("Technical Architect",
            because: "a runner identity mirrors the persona it authenticates, and it is hidden anyway");
    }

    [Theory]
    [InlineData("Docker")]
    [InlineData("Kubernetes")]
    public async Task ExistingRows_OnlyGetTheirTokenHashUpdated(string runtime)
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.AddRange(
            new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver", ApiKeyHash = "old" },
            new Agent { Id = "rook", Name = "Rook", Role = "Software Engineer", ApiKeyHash = "old" });
        await db.SaveChangesAsync();

        await SyncAsync(runtime, db, driverId: "agy", personaId: "rook",
            personaName: "Rook", personaRole: "Software Engineer");

        (await db.Agents.FindAsync("agy"))!.ApiKeyHash.Should().Be(Hash);
        (await db.Agents.FindAsync("rook"))!.ApiKeyHash.Should().Be(Hash);
    }

    [Fact]
    public async Task LegacyPersonaRow_BacksFilledWithMarker_WithoutADataMigration()
    {
        // Rows created before the marker existed carry nothing identifying them as runner
        // identities. Re-running provisioning (which happens on every container start) heals them,
        // so no EF migration or manual SQL is required for existing deployments.
        using var db = TestDbContextFactory.Create();
        db.Agents.AddRange(
            new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver" },
            new Agent { Id = "rook", Name = "Rook", Role = "Software Engineer", CapabilitiesJson = "[]" });
        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", Role = "Software Engineer", DriverId = "agy" });
        await db.SaveChangesAsync();

        await DockerPersonaContainerManager.SyncAgentTokenHashAsync(
            db, "agy", "rook", "Rook", "Software Engineer", Hash, CancellationToken.None);

        var personaRow = await db.Agents.FindAsync("rook");
        AgentKind.IsPersonaRunnerIdentity(personaRow!, new Dictionary<string, string>()).Should().BeTrue();
    }

    [Fact]
    public async Task Backfill_DoesNotMarkADriver_AsARunnerIdentity()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver", CapabilitiesJson = "[]" });
        await db.SaveChangesAsync();

        await DockerPersonaContainerManager.SyncAgentTokenHashAsync(
            db, "agy", "agy", "Antigravity CLI", "Driver", Hash, CancellationToken.None);

        var driver = await db.Agents.FindAsync("agy");
        AgentKind.HasRunnerMarker(driver!.CapabilitiesJson).Should().BeFalse(
            because: "driverId == personaId means there is no separate persona row to mark");
    }

    private static Task SyncAsync(
        string runtime,
        AgentMeshDbContext db,
        string driverId,
        string personaId,
        string? personaName,
        string? personaRole) => runtime switch
        {
            "Docker" => DockerPersonaContainerManager.SyncAgentTokenHashAsync(
                db, driverId, personaId, personaName, personaRole, Hash, CancellationToken.None),
            "Kubernetes" => KubernetesPersonaContainerManager.SyncAgentTokenHashAsync(
                db, driverId, personaId, personaName, personaRole, Hash, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(runtime))
        };
}
