using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Auth;

public class DexOidcConfigurationTests
{
    [Fact]
    public void DexConfig_ContainsRequiredCorsAndClientSettings()
    {
        // Arrange: locate src/AgentMesh.AppHost/dex/config.yaml by finding solution root
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgentMesh.slnx")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName ?? Directory.GetCurrentDirectory();
        var dexConfigPath = Path.Combine(repoRoot, "src", "AgentMesh.AppHost", "dex", "config.yaml");

        File.Exists(dexConfigPath).Should().BeTrue($"Dex config should exist at {dexConfigPath}");

        var yaml = File.ReadAllText(dexConfigPath);

        // Assert: issuer must match the expected authority
        yaml.Should().Contain("issuer: http://localhost:8085/dex");

        // Assert: CORS allowedOrigins must include localhost:3000 or *
        yaml.Should().Contain("allowedOrigins:");
        yaml.Should().Contain("http://localhost:3000");

        // Assert: client must be agentmesh-dashboard with PKCE / public flag
        yaml.Should().Contain("id: agentmesh-dashboard");
        yaml.Should().Contain("http://localhost:3000/auth/callback");
        yaml.Should().Contain("public: true");

        // Assert: admin user exists
        yaml.Should().Contain("email: \"admin@agentmesh.local\"");
        yaml.Should().Contain("skipApprovalScreen: true");
    }
}
