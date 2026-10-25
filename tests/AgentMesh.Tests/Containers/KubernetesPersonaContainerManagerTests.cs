using AgentMesh.Infrastructure.Containers;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Containers;

public class KubernetesPersonaContainerManagerTests
{
    [Theory]
    [InlineData("architect", "agentmesh-persona-architect-workspace")]
    [InlineData("System-Architect", "agentmesh-persona-system-architect-workspace")]
    [InlineData("  DevOps-Lead  ", "agentmesh-persona-devops-lead-workspace")]
    public void GetPvcName_NormalizesPersonaId_AndAppendsSuffix(string personaId, string expectedPvcName)
    {
        var pvcName = KubernetesPersonaContainerManager.GetPvcName(personaId);
        pvcName.Should().Be(expectedPvcName);
    }

    [Theory]
    [InlineData("architect", "agentmesh-persona-architect")]
    [InlineData("System-Architect", "agentmesh-persona-system-architect")]
    public void GetDeploymentName_NormalizesPersonaId(string personaId, string expectedDeploymentName)
    {
        var depName = KubernetesPersonaContainerManager.GetDeploymentName(personaId);
        depName.Should().Be(expectedDeploymentName);
    }
}
