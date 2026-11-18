using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentMesh.Tests.Services;

public class McpResolutionTests
{
    private static ICredentialEncryptionService CreateEncryption() =>
        new AesCredentialEncryptionService(SHA256.HashData(Encoding.UTF8.GetBytes("mcp-resolution-test-key-32-bytes")));

    [Fact]
    public async Task ResolveForPersonaAsync_DecryptsAndMergesSecrets_AppliesToolFilters_AndSkipsDisabled()
    {
        await using var db = TestDbContextFactory.Create();
        var encryption = CreateEncryption();

        db.Personas.Add(new Persona
        {
            Id = "astra",
            Name = "Astra",
            DriverId = "agy",
            PersonaPrompt = "Senior Engineer"
        });

        db.McpServers.AddRange(
            new McpServer
            {
                Id = "github",
                Name = "GitHub",
                Transport = McpTransportType.Stdio,
                Command = "npx",
                ArgsJson = "[\"-y\",\"@modelcontextprotocol/server-github\"]",
                EnvEncrypted = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
                {
                    ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_catalog_default_0001",
                    ["GITHUB_API_URL"] = "https://api.github.com"
                }),
                Enabled = true,
                TimeoutSeconds = 45
            },
            new McpServer
            {
                Id = "context7",
                Name = "Context7",
                Transport = McpTransportType.Http,
                Url = "https://mcp.context7.com/mcp",
                HeadersEncrypted = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer catalog_token",
                    ["X-Client"] = "agentmesh"
                }),
                Enabled = true,
                TimeoutSeconds = 20
            },
            new McpServer
            {
                Id = "disabled-global",
                Name = "Disabled Global",
                Transport = McpTransportType.Sse,
                Url = "https://disabled.example.com/sse",
                Enabled = false
            },
            new McpServer
            {
                Id = "disabled-local",
                Name = "Disabled Local",
                Transport = McpTransportType.Stdio,
                Command = "uvx",
                Enabled = true
            },
            new McpServer
            {
                Id = "broken-stdio",
                Name = "Broken Stdio",
                Transport = McpTransportType.Stdio,
                Command = "   ",
                Enabled = true
            });

        db.PersonaMcpServers.AddRange(
            new PersonaMcpServer
            {
                PersonaId = "astra",
                McpServerId = "github",
                Enabled = true,
                EnvOverridesEncrypted = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
                {
                    ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_astra_override_9999",
                    ["EXTRA_VAR"] = "override-val"
                }),
                AllowedToolsJson = "[\"get_issue\",\"create_pull_request\"]",
                BlockedToolsJson = "[\"delete_repository\"]"
            },
            new PersonaMcpServer
            {
                PersonaId = "astra",
                McpServerId = "context7",
                Enabled = true,
                HeadersOverridesEncrypted = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer astra_override_token"
                }),
                AllowedToolsJson = "[]",
                BlockedToolsJson = null
            },
            new PersonaMcpServer
            {
                PersonaId = "astra",
                McpServerId = "disabled-global",
                Enabled = true
            },
            new PersonaMcpServer
            {
                PersonaId = "astra",
                McpServerId = "disabled-local",
                Enabled = false
            },
            new PersonaMcpServer
            {
                PersonaId = "astra",
                McpServerId = "broken-stdio",
                Enabled = true
            });

        await db.SaveChangesAsync();

        var resolver = new PersonaMcpResolver(db, encryption);
        var resolved = await resolver.ResolveForPersonaAsync("  ASTRA ");

        resolved.Should().HaveCount(2);
        // Deterministic ordering by Name: Context7 before GitHub
        resolved.Select(r => r.Id).Should().Equal("context7", "github");

        var ctx7 = resolved[0];
        ctx7.Transport.Should().Be(McpTransportType.Http);
        ctx7.Url.Should().Be("https://mcp.context7.com/mcp");
        ctx7.Command.Should().BeNull();
        ctx7.Args.Should().BeEmpty();
        ctx7.TimeoutSeconds.Should().Be(20);
        ctx7.Headers.Should().Equal(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer astra_override_token",
            ["X-Client"] = "agentmesh"
        });
        ctx7.AllowedTools.Should().NotBeNull().And.BeEmpty();
        ctx7.BlockedTools.Should().BeNull();

        var gh = resolved[1];
        gh.Transport.Should().Be(McpTransportType.Stdio);
        gh.Command.Should().Be("npx");
        gh.Args.Should().Equal("-y", "@modelcontextprotocol/server-github");
        gh.Url.Should().BeNull();
        gh.TimeoutSeconds.Should().Be(45);
        gh.Env.Should().Equal(new Dictionary<string, string>
        {
            ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_astra_override_9999",
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["EXTRA_VAR"] = "override-val"
        });
        gh.AllowedTools.Should().Equal("get_issue", "create_pull_request");
        gh.BlockedTools.Should().Equal("delete_repository");
    }

    [Fact]
    public async Task ResolveForPersonaAsync_WhenPersonaIdBlankOrMissing_ReturnsEmpty()
    {
        await using var db = TestDbContextFactory.Create();
        var resolver = new PersonaMcpResolver(db, CreateEncryption());

        (await resolver.ResolveForPersonaAsync(null)).Should().BeEmpty();
        (await resolver.ResolveForPersonaAsync("   ")).Should().BeEmpty();
        (await resolver.ResolveForPersonaAsync("unknown")).Should().BeEmpty();
    }

    [Fact]
    public async Task PersonasController_GetResolvedPersonaMcpServers_ReturnsDecryptedConfigs_AndEnforcesDriverScope()
    {
        await using var db = TestDbContextFactory.Create();
        var encryption = CreateEncryption();

        db.Personas.Add(new Persona
        {
            Id = "astra",
            Name = "Astra",
            DriverId = "agy",
            PersonaPrompt = "Prompt"
        });
        db.McpServers.Add(new McpServer
        {
            Id = "sentry",
            Name = "Sentry",
            Transport = McpTransportType.Sse,
            Url = "https://mcp.sentry.io/sse",
            HeadersEncrypted = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer sentry_secret_1234"
            }),
            Enabled = true
        });
        db.PersonaMcpServers.Add(new PersonaMcpServer
        {
            PersonaId = "astra",
            McpServerId = "sentry",
            Enabled = true
        });
        await db.SaveChangesAsync();

        var controller = new PersonasController(db, encryptionService: encryption);

        // Missing persona -> 404
        (await controller.GetResolvedPersonaMcpServers("nonexistent", CancellationToken.None))
            .Should().BeOfType<NotFoundObjectResult>();

        // Mismatched Driver token -> Forbid
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim("AgentId", "claude"),
                        new Claim(ClaimTypes.Role, "Agent"),
                        new Claim(ClaimTypes.Role, "Driver")
                    },
                    authenticationType: "ApiKey"))
            }
        };
        (await controller.GetResolvedPersonaMcpServers("astra", CancellationToken.None))
            .Should().BeOfType<ForbidResult>();

        // Matching Driver token -> Ok with decrypted secrets
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("AgentId", "agy"),
                new Claim(ClaimTypes.Role, "Agent"),
                new Claim(ClaimTypes.Role, "Driver")
            },
            authenticationType: "ApiKey"));

        var ok = (await controller.GetResolvedPersonaMcpServers("astra", CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;
        var configs = ok.Value.Should().BeAssignableTo<List<ResolvedMcpServerConfig>>().Subject;
        configs.Should().ContainSingle();
        configs[0].Id.Should().Be("sentry");
        configs[0].Transport.Should().Be(McpTransportType.Sse);
        configs[0].Headers["Authorization"].Should().Be("Bearer sentry_secret_1234");
    }
}
