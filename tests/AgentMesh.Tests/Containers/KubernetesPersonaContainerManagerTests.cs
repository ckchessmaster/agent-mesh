using AgentMesh.Core.Models;
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

    [Fact]
    public void BuildSidecarContainer_WhenDisabled_ReturnsNull()
    {
        var sidecar = new DriverSidecarConfig { Enabled = false };
        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "astra");
        container.Should().BeNull();
    }

    [Fact]
    public void BuildSidecarContainer_WhenTailscale_SetsTsKubeSecretEmptyString_ToPreventRbacChecks()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            AuthKey = "tskey-auth-12345",
            Hostname = "mesh-astra"
        };

        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "astra");

        container.Should().NotBeNull();
        container!.Name.Should().Be("driver-sidecar");
        container.Image.Should().Be("tailscale/tailscale:v1.78.1");

        // Verify TS_KUBE_SECRET is explicitly empty string to avoid Kube Secret/Events RBAC requirement
        var kubeSecretVar = container.Env.FirstOrDefault(e => e.Name == "TS_KUBE_SECRET");
        kubeSecretVar.Should().NotBeNull();
        kubeSecretVar!.Value.Should().Be("");

        var authKeyVar = container.Env.FirstOrDefault(e => e.Name == "TS_AUTHKEY");
        authKeyVar.Should().NotBeNull();
        authKeyVar!.Value.Should().Be("tskey-auth-12345");

        var extraArgsVar = container.Env.FirstOrDefault(e => e.Name == "TS_EXTRA_ARGS");
        extraArgsVar.Should().NotBeNull();
        extraArgsVar!.Value.Should().NotContain("--ephemeral");
        extraArgsVar.Value.Should().Contain("--hostname=mesh-astra");

        var acceptDnsVar = container.Env.FirstOrDefault(e => e.Name == "TS_ACCEPT_DNS");
        acceptDnsVar.Should().NotBeNull();
        acceptDnsVar!.Value.Should().Be("true");
    }

    [Fact]
    public void BuildSidecarContainer_WhenTailscale_AllowsOverridingTsKubeSecret_ViaEnvironmentVariables()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["TS_KUBE_SECRET"] = "custom-tailscale-secret",
                ["TS_STATE_DIR"] = "/var/lib/tailscale"
            }
        };

        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "astra");

        container.Should().NotBeNull();
        var kubeSecretVar = container!.Env.FirstOrDefault(e => e.Name == "TS_KUBE_SECRET");
        kubeSecretVar.Should().NotBeNull();
        kubeSecretVar!.Value.Should().Be("custom-tailscale-secret");

        var stateDirVar = container.Env.FirstOrDefault(e => e.Name == "TS_STATE_DIR");
        stateDirVar.Should().NotBeNull();
        stateDirVar!.Value.Should().Be("/var/lib/tailscale");
    }

    [Fact]
    public void BuildSidecarContainer_WhenTailscale_TunMode_AddsNetAdminAndDevTunMount()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            UserspaceMode = false
        };

        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "astra");

        container.Should().NotBeNull();
        container!.SecurityContext.Should().NotBeNull();
        container.SecurityContext!.Capabilities.Add.Should().Contain("NET_ADMIN");
        container.VolumeMounts.Should().ContainSingle(vm => vm.Name == "tun-dev" && vm.MountPath == "/dev/net/tun");
    }

    [Fact]
    public void BuildSidecarContainer_WhenTailscale_UserspaceMode_OmitsNetAdminAndSetsUserspaceTrue()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            UserspaceMode = true
        };

        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "astra");

        container.Should().NotBeNull();
        container!.SecurityContext.Should().BeNull();
        container.VolumeMounts.Should().BeNull();

        var userspaceVar = container.Env.FirstOrDefault(e => e.Name == "TS_USERSPACE");
        userspaceVar.Should().NotBeNull();
        userspaceVar!.Value.Should().Be("true");
    }

    [Fact]
    public void BuildSidecarContainer_WhenTailscale_WithSecretRef_ConfiguresSecretKeyRef()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            SecretRefName = "my-tailscale-secret",
            SecretRefKey = "CUSTOM_AUTHKEY"
        };

        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "astra");

        container.Should().NotBeNull();
        var authKeyVar = container!.Env.FirstOrDefault(e => e.Name == "TS_AUTHKEY");
        authKeyVar.Should().NotBeNull();
        authKeyVar!.ValueFrom.Should().NotBeNull();
        authKeyVar.ValueFrom!.SecretKeyRef.Should().NotBeNull();
        authKeyVar.ValueFrom.SecretKeyRef!.Name.Should().Be("my-tailscale-secret");
        authKeyVar.ValueFrom.SecretKeyRef.Key.Should().Be("CUSTOM_AUTHKEY");
    }

    [Fact]
    public void BuildSidecarContainer_WhenCustomType_ConfiguresCustomContainerAndEnv()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "custom",
            Image = "custom-wireguard:latest",
            Command = "/start.sh",
            Arguments = new List<string> { "--verbose" },
            EnvironmentVariables = new Dictionary<string, string> { ["WG_PORT"] = "51820" }
        };

        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "astra");

        container.Should().NotBeNull();
        container!.Image.Should().Be("custom-wireguard:latest");
        container.Command.Should().ContainSingle(c => c == "/start.sh");
        container.Args.Should().ContainSingle(a => a == "--verbose");
        container.Env.Should().ContainSingle(e => e.Name == "WG_PORT" && e.Value == "51820");
    }

    [Fact]
    public void DriverSidecarConfig_PreservesCompanionSettings()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            CompanionSetup = "which aip >/dev/null 2>&1 || curl -fsSL https://fire.taild419e9.ts.net:12443/cli/install.sh | sh",
            CompanionCommand = "aip proxy",
            CompanionPort = 11435
        };

        sidecar.CompanionSetup.Should().Be("which aip >/dev/null 2>&1 || curl -fsSL https://fire.taild419e9.ts.net:12443/cli/install.sh | sh");
        sidecar.CompanionCommand.Should().Be("aip proxy");
        sidecar.CompanionPort.Should().Be(11435);
    }

    [Theory]
    [InlineData("--ephemeral", "")]
    [InlineData("-ephemeral", "")]
    [InlineData("--ephemeral=true", "")]
    [InlineData("-ephemeral=false", "")]
    [InlineData("--ephemeral --accept-routes", "--accept-routes")]
    [InlineData("--accept-routes -ephemeral --advertise-exit-node", "--accept-routes --advertise-exit-node")]
    [InlineData("--hostname=test --ephemeral --ssh", "--hostname=test --ssh")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void CleanTailscaleExtraArgs_StripsEphemeralFlags(string? input, string expected)
    {
        var result = DriverSidecarConfig.CleanTailscaleExtraArgs(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void BuildSidecarContainer_WhenExtraArgsContainsEphemeral_SanitizesIt()
    {
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            Hostname = "mesh-test",
            ExtraArgs = "--ephemeral --accept-routes"
        };

        var container = KubernetesPersonaContainerManager.BuildSidecarContainer(sidecar, "test");
        container.Should().NotBeNull();

        var extraArgsVar = container!.Env.FirstOrDefault(e => e.Name == "TS_EXTRA_ARGS");
        extraArgsVar.Should().NotBeNull();
        extraArgsVar!.Value.Should().NotContain("--ephemeral");
        extraArgsVar.Value.Should().Contain("--hostname=mesh-test");
        extraArgsVar.Value.Should().Contain("--accept-routes");
    }
}
