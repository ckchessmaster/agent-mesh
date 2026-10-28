using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Models;

/// <summary>
/// A persona needs a row in `agents` purely so its bearer token can be authenticated
/// (ApiKeyAuthenticationHandler matches on agents.api_key_hash). Those rows are not drivers and
/// must never be listed as such: doing so showed every driver twice on the drivers tab, with the
/// persona's name and role.
/// </summary>
public class AgentKindTests
{
    private static readonly Dictionary<string, string> PersonaOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rook"] = "agy",
        ["sorrn"] = "copilot"
    };

    [Fact]
    public void Detects_RunnerIdentity_FromExplicitMarker()
    {
        var agent = new Agent
        {
            Id = "rook",
            Name = "Rook",
            Role = "Software Engineer",
            CapabilitiesJson = """{"personaId":"rook","role":"persona-runner"}"""
        };

        // Even with no persona map available (e.g. a cheap single-row lookup), the marker is enough.
        AgentKind.IsPersonaRunnerIdentity(agent, new Dictionary<string, string>()).Should().BeTrue();
    }

    [Fact]
    public void Detects_Legacy_RunnerIdentity_FromPersonaOwnership()
    {
        // Rows created before the marker existed carry nothing but a persona-shaped name and role.
        var agent = new Agent { Id = "rook", Name = "Rook", Role = "Software Engineer", CapabilitiesJson = "[]" };

        AgentKind.IsPersonaRunnerIdentity(agent, PersonaOwners).Should().BeTrue();
    }

    [Fact]
    public void RealDrivers_AreNotFlagged()
    {
        var driver = new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver", CapabilitiesJson = "[]" };

        AgentKind.IsPersonaRunnerIdentity(driver, PersonaOwners).Should().BeFalse();
    }

    [Fact]
    public void PersonaRunningDirectlyOnADriverOfTheSameName_IsNotFlagged()
    {
        // `agy-architect` persona whose driver is `agy-architect`: one row is legitimate here, and
        // hiding it would leave the operator with no driver to start sessions on.
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["agy"] = "agy" };
        var agent = new Agent { Id = "agy", Name = "Antigravity CLI", Role = "Driver" };

        AgentKind.IsPersonaRunnerIdentity(agent, owners).Should().BeFalse();
    }

    [Fact]
    public void Detection_IsCaseInsensitive()
    {
        var agent = new Agent { Id = "ROOK", Name = "Rook", Role = "Software Engineer" };

        AgentKind.IsPersonaRunnerIdentity(agent, PersonaOwners).Should().BeTrue();
    }

    [Fact]
    public void CorruptCapabilitiesJson_DoesNotThrow_AndFallsBackToOwnership()
    {
        var agent = new Agent { Id = "rook", Name = "Rook", CapabilitiesJson = "{not json" };

        AgentKind.IsPersonaRunnerIdentity(agent, PersonaOwners).Should().BeTrue();
        AgentKind.HasRunnerMarker("{not json").Should().BeFalse();
    }

    [Fact]
    public void BuildRunnerIdentityCapabilities_MarksTheRow_AndRecordsThePersona()
    {
        var json = AgentKind.BuildRunnerIdentityCapabilities("Rook", "AGY", null);

        AgentKind.HasRunnerMarker(json).Should().BeTrue();

        // Ids are normalised so the heuristic tier agrees with the marker tier, and the persona
        // link is recorded so the row can be traced back to the persona it authenticates.
        json.Should().Contain("\"personaId\":\"rook\"");
        json.Should().Contain("\"driverId\":\"agy\"");
    }

    [Fact]
    public void BuildRunnerIdentityCapabilities_PreservesExistingKeys()
    {
        // A runner identity can be promoted to a real driver later; that must not lose its config.
        var existing = """{"adapter":"openai","endpoint":"http://ollama:11434/v1"}""";

        var json = AgentKind.BuildRunnerIdentityCapabilities("rook", "agy", existing);

        AgentKind.HasRunnerMarker(json).Should().BeTrue();
        json.Should().Contain("\"adapter\":\"openai\"");
        json.Should().Contain("ollama");
    }

    [Fact]
    public void BuildRunnerIdentityCapabilities_PreservesABareCapabilityArray()
    {
        // `Agent.CapabilitiesJson` defaults to "[]" and DriverCapabilities.Parse accepts a bare
        // array as the capability list, so a non-empty array is live data, not a placeholder.
        // Stamping the marker used to deserialise it into a dictionary, throw, and fall back to a
        // patch-only blob - silently deleting the capabilities it was meant to preserve.
        var existing = "[\"mcp\",\"shell\"]";

        var json = AgentKind.BuildRunnerIdentityCapabilities("rook", "agy", existing);

        AgentKind.HasRunnerMarker(json).Should().BeTrue();

        // The capabilities must still be readable by the parser that the rest of the system uses.
        AgentMesh.Api.Services.DriverCapabilities.Parse(json).Capabilities
            .Should().BeEquivalentTo(new[] { "mcp", "shell" },
                because: "marking a row as a runner identity must not destroy its declared capabilities");
    }

    [Fact]
    public void BuildRunnerIdentityCapabilities_KeepsAnEmptyArrayBlobMarked_AndReadable()
    {
        var json = AgentKind.BuildRunnerIdentityCapabilities("rook", "agy", "[]");

        AgentKind.HasRunnerMarker(json).Should().BeTrue();
        AgentMesh.Api.Services.DriverCapabilities.Parse(json).Capabilities.Should().BeEmpty();
    }

    [Fact]
    public void BuildRunnerIdentityCapabilities_IsIdempotent_WhenStampedTwice()
    {
        // The container managers backfill the marker on every token sync, so re-stamping must not
        // nest the previous result or drop keys added in between.
        var once = AgentKind.BuildRunnerIdentityCapabilities("rook", "agy", """["mcp"]""");
        var twice = AgentKind.BuildRunnerIdentityCapabilities("rook", "agy", once);

        AgentKind.HasRunnerMarker(twice).Should().BeTrue();
        AgentMesh.Api.Services.DriverCapabilities.Parse(twice).Capabilities
            .Should().BeEquivalentTo(new[] { "mcp" });
    }

    [Fact]
    public void ADriverThatMerelyReferencesAPersona_IsNotTreatedAsARunnerIdentity()
    {
        // Drivers list the personas they host, so a `personaId` key alone must not be read as the
        // runner marker - otherwise hiding rows would hide real drivers.
        AgentKind.HasRunnerMarker("""{"adapter":"agy","personaId":"rook"}""").Should().BeFalse();
    }

    [Fact]
    public void MarkerRoundTrips_ThroughTheSameHelperTheContainerManagersUse()
    {
        // Guards the contract between the Infrastructure writers and the API reader: what the
        // container manager stamps must be what GetAgents recognises.
        var json = AgentKind.BuildRunnerIdentityCapabilities("sorrn", "copilot", null);
        var row = new Agent { Id = "sorrn", Name = "Sorrn", Role = "Technical Architect", CapabilitiesJson = json };

        AgentKind.IsPersonaRunnerIdentity(row, new Dictionary<string, string>()).Should().BeTrue();
    }
}
