using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Commands;
using AgentMesh.Cli.Models;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

/// <summary>
/// The per-turn identity a driver's shell needs to reach the control plane.
/// </summary>
/// <remarks>
/// <para>
/// <c>agentmesh task</c> and <c>agentmesh message</c> place a call using the session and job in their
/// environment, which the runner stamps when it launches the driver. This is also why the context could not
/// live in a static MCP registration: one persona container serves many jobs, so the identity changes every
/// turn, and a container configured for one job would attribute the next job's checklist to the wrong place.
/// </para>
/// <para>
/// A blank value is left unset rather than exported empty, because "absent" and "empty" mean different
/// things to the hint the verbs print on failure — and because a stale inherited value must never outlive
/// the turn it came from.
/// </para>
/// </remarks>
public class MeshTurnContextTests
{
    private static PromptReceivedEvent Turn(
        Guid? sessionId = null,
        string? jobId = "job-77",
        string? personaId = "astra",
        string? driverId = "agy-default",
        string? agentId = "rook") => new()
    {
        SessionId = sessionId ?? Guid.NewGuid(),
        JobId = jobId,
        PersonaId = personaId,
        DriverId = driverId,
        AgentId = agentId
    };

    [Fact]
    public void TurnContext_ExportsEveryNameTheControlPlaneVerbsRead()
    {
        var sessionId = Guid.NewGuid();
        var context = AgentAdapterFactory.MeshTurnContext(Turn(sessionId)).ToDictionary(pair => pair.Key, pair => pair.Value);

        context[PlatformToolCommand.SessionIdEnvVar].Should().Be(sessionId.ToString());
        context[PlatformToolCommand.JobIdEnvVar].Should().Be("job-77");
        context[PlatformToolCommand.PersonaIdEnvVar].Should().Be("astra");
        context[PlatformToolCommand.AgentIdEnvVar].Should().Be("agy-default");
    }

    [Fact]
    public void TurnContext_KeepsTheLegacyAgentNameThatPredatesDriverIdentity()
    {
        var context = AgentAdapterFactory.MeshTurnContext(Turn()).ToDictionary(pair => pair.Key, pair => pair.Value);

        context["AGENTMESH_AGENT_ID"].Should().Be("agy-default",
            "operator scripts written before 2.6 read this name, and both must agree or one silently lies");
    }

    [Fact]
    public void TurnContext_PrefersTheCallingDriversOwnIdentityOverTheDispatchAgent()
    {
        var context = AgentAdapterFactory.MeshTurnContext(Turn(driverId: " "), agentId: "agy-primary")
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        context[PlatformToolCommand.AgentIdEnvVar].Should().Be("agy-primary");
    }

    [Fact]
    public void TurnContext_OmitsWhatTheTurnDoesNotKnow()
    {
        var context = AgentAdapterFactory.MeshTurnContext(
            Turn(sessionId: Guid.Empty, jobId: "   ", personaId: null, driverId: null, agentId: null));

        context.Select(pair => pair.Key).Should().BeEmpty("an empty value reads as a misconfiguration, not as an unknown");
    }

    [Fact]
    public void ApplyTurnContext_WritesIntoTheChildProcessEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin" };
        var promptEvent = Turn();

        AgentAdapterFactory.ApplyMeshTurnContext(env, promptEvent);

        env.Should().ContainKeys(
            PlatformToolCommand.SessionIdEnvVar,
            PlatformToolCommand.JobIdEnvVar,
            PlatformToolCommand.PersonaIdEnvVar,
            PlatformToolCommand.AgentIdEnvVar);
        env["PATH"].Should().Be("/usr/bin", "stamping context must not replace the rest of the environment");
        env[PlatformToolCommand.JobIdEnvVar].Should().Be(promptEvent.JobId);
    }

    [Fact]
    public void ContextPrefix_IsUsableAsAShellAssignmentAndQuotesAnApostrophe()
    {
        var prefix = AgentAdapterFactory.BuildMeshTurnContextPrefix(Turn(personaId: "maria o'brien"));

        prefix.Should().StartWith($"{PlatformToolCommand.SessionIdEnvVar}='");
        prefix.Should().Contain($"{PlatformToolCommand.PersonaIdEnvVar}='maria o'\\''brien'");
    }

    [Fact]
    public void ContextPrefix_IsEmptyWhenThereIsNothingToExport()
    {
        AgentAdapterFactory.BuildMeshTurnContextPrefix(
                Turn(sessionId: Guid.Empty, jobId: null, personaId: null, driverId: null, agentId: null))
            .Should().BeEmpty("a caller concatenates the prefix unconditionally");
    }
}
