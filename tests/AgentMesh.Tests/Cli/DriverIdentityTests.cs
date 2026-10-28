using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Cli;

/// <summary>
/// A persona runner registers against its *driver*, and registration pushes the preset's
/// Id/Name/Role onto the driver row. The old code copied the persona's name and role into the
/// preset, so running @rook on @agy renamed the driver to "Rook (@agy)" with role "Software
/// Engineer" - which is exactly what the drivers tab then displayed.
/// </summary>
public class DriverIdentityTests
{
    private static RemoteAgentDto Driver(string name, string role) =>
        new("agy", name, role, "online", 0, null, null);

    [Fact]
    public void ApplyTo_KeepsTheDriversNameAndRole_NotThePersonas()
    {
        var preset = new AgentPresetConfig { Id = "agy", Name = "agy", Role = "Autonomous Agent" };

        DriverIdentity.ApplyTo(preset, "agy", Driver("Antigravity CLI", "Driver"), personaRole: "Software Engineer");

        preset.Name.Should().Be("Antigravity CLI");
        preset.Role.Should().Be("Driver");
    }

    [Fact]
    public void ApplyTo_RepairsADriverAlreadyRenamedByAPersonaRunner()
    {
        // The row in the DB is already polluted from a previous run; re-registering must heal it
        // without a data migration.
        var preset = new AgentPresetConfig { Id = "agy", Name = "agy", Role = "Autonomous Agent" };

        DriverIdentity.ApplyTo(preset, "agy", Driver("Rook (@agy)", "Software Engineer"), personaRole: "Software Engineer");

        preset.Name.Should().Be("agy", because: "the cloned name is not the driver's identity");
        preset.Role.Should().Be("Driver",
            because: "the driver's role equals the hosted persona's role, so it was copied, not chosen");
    }

    [Fact]
    public void ApplyTo_DoesNotKeepTheLegacyPlaceholderRole()
    {
        var preset = new AgentPresetConfig { Id = "agy", Name = "Antigravity CLI", Role = "Autonomous Agent" };

        DriverIdentity.ApplyTo(preset, "agy", Driver("Antigravity CLI", "Autonomous Agent"));

        preset.Role.Should().Be("Driver");
    }

    [Fact]
    public void ApplyTo_FallsBackToTheDriverId_WhenTheMeshIsUnreachable()
    {
        var preset = new AgentPresetConfig { Id = "agy", Name = "Rook (@agy)", Role = "Software Engineer" };

        DriverIdentity.ApplyTo(preset, "agy", remoteDriver: null, personaRole: "Software Engineer");

        preset.Name.Should().Be("agy");
        preset.Role.Should().Be("Driver");
    }

    [Fact]
    public void ApplyTo_PreservesAnOperatorChosenName_WhenTheServerHasNone()
    {
        // `agentmesh add my-runner --name "Work Laptop"` must not be flattened to the bare id.
        var preset = new AgentPresetConfig { Id = "my-runner", Name = "Work Laptop", Role = "Autonomous Agent" };

        DriverIdentity.ApplyTo(preset, "my-runner", remoteDriver: null);

        preset.Name.Should().Be("Work Laptop");
    }

    [Fact]
    public void ApplyTo_PreservesAnExplicitDriverRole()
    {
        var preset = new AgentPresetConfig { Id = "agy", Name = "agy", Role = "Autonomous Agent" };

        DriverIdentity.ApplyTo(preset, "agy", Driver("Antigravity CLI", "Primary Dev Box"), personaRole: "Software Engineer");

        preset.Role.Should().Be("Primary Dev Box",
            because: "an operator may deliberately label a driver; only placeholders and persona copies are replaced");
    }

    [Fact]
    public void ApplyTo_RejectsAClonedLocalRole()
    {
        // A local preset poisoned by an older CLI, with no remote to correct it.
        var preset = new AgentPresetConfig { Id = "agy", Name = "agy", Role = "Rook (@agy)" };

        DriverIdentity.ApplyTo(preset, "agy", remoteDriver: null);

        preset.Role.Should().Be("Driver");
    }

    [Fact]
    public void ApplyTo_LeavesADriverRoleAlone_WhenNoPersonaIsHosted()
    {
        // A plain driver runner (no persona) must never have its role rewritten just because the
        // value looks persona-ish; there is no persona to have copied it from.
        var preset = new AgentPresetConfig { Id = "agy", Name = "agy", Role = "Autonomous Agent" };

        DriverIdentity.ApplyTo(preset, "agy", Driver("Antigravity CLI", "Release Runner"), personaRole: null);

        preset.Role.Should().Be("Release Runner");
    }

    [Theory]
    [InlineData(null, "", "agy", "agy")]
    [InlineData(null, "Rook (@agy)", "agy", "agy")]
    [InlineData("  ", "Rook (@agy)", "agy", "agy")]
    [InlineData("Antigravity CLI", "Rook (@agy)", "agy", "Antigravity CLI")]
    [InlineData("Rook (@agy)", "Work Laptop", "agy", "Work Laptop")]
    [InlineData("Rook (@agy)", "Rook (@agy)", "agy", "agy")]
    public void ResolveName_PrefersRealNames_OverClonedOnes(
        string? remoteName, string? localName, string driverId, string expected)
    {
        DriverIdentity.ResolveName(remoteName, localName, driverId).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null, null, "Driver")]
    [InlineData("Autonomous Agent", "Autonomous Agent", null, "Driver")]
    [InlineData("Software Engineer", "Driver", "Software Engineer", "Driver")]
    [InlineData("Software Engineer", "Driver", null, "Software Engineer")]
    [InlineData(null, "Primary Dev Box", "Software Engineer", "Primary Dev Box")]
    [InlineData(null, "Rook (@agy)", null, "Driver")]
    [InlineData("Release Runner", "Software Engineer", "Software Engineer", "Release Runner")]
    public void ResolveRole_ReplacesPlaceholdersAndPersonaCopies(
        string? remoteRole, string? localRole, string? personaRole, string expected)
    {
        DriverIdentity.ResolveRole(remoteRole, localRole, personaRole).Should().Be(expected);
    }
}
