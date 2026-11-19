using System.Text.Json;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

public class SubprocessMcpProvisionerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"subprocess-mcp-test-{Guid.NewGuid():N}");

    public SubprocessMcpProvisionerTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ProvisionForClaude_WritesSecureConfigFile_SupportsStdioHttpSse_AndEnforcesBlockedTools()
    {
        var servers = new List<ResolvedMcpServerConfig>
        {
            new()
            {
                Id = "github",
                Name = "GitHub",
                Transport = McpTransportType.Stdio,
                Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-github"],
                Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_claude_secret_123" },
                AllowedTools = ["search_code", "create_issue", "delete_repo"],
                BlockedTools = ["delete_repo"]
            },
            new()
            {
                Id = "context7",
                Name = "Context7",
                Transport = McpTransportType.Http,
                Url = "https://mcp.context7.com/mcp",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ctx_secret_456" }
            },
            new()
            {
                Id = "legacy-sse",
                Name = "Legacy SSE",
                Transport = McpTransportType.Sse,
                Url = "https://sse.example.com/sse",
                Headers = new Dictionary<string, string> { ["X-Api-Key"] = "sse_secret_789" },
                BlockedTools = ["drop_table"]
            },
            new()
            {
                Id = "empty-allowed",
                Name = "Empty Allowed",
                Transport = McpTransportType.Stdio,
                Command = "echo",
                AllowedTools = []
            }
        };

        string? configPath;
        using (var result = SubprocessMcpProvisioner.ProvisionForClaude(servers, mode: "auto", tempDirectory: _tempRoot))
        {
            result.ProvisionedServerCount.Should().Be(3);
            result.ConfigFilePath.Should().NotBeNullOrWhiteSpace();
            configPath = result.ConfigFilePath!;
            File.Exists(configPath).Should().BeTrue();

            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(configPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            result.ExtraArguments.Should().ContainInOrder("--mcp-config", configPath, "--strict-mcp-config");
            var disallowedIdx = result.ExtraArguments.ToList().IndexOf("--disallowedTools");
            disallowedIdx.Should().BeGreaterThanOrEqualTo(0);
            result.ExtraArguments[disallowedIdx + 1].Should().Contain("mcp__github__delete_repo");
            result.ExtraArguments[disallowedIdx + 1].Should().Contain("mcp__legacy-sse__drop_table");

            // Warnings must never expose decrypted credentials
            result.Warnings.Should().Contain(w => w.Contains("allowedTools") && !w.Contains("ghp_claude_secret_123"));
            result.Warnings.Should().Contain(w => w.Contains("no allowed tools"));
            string.Join("\n", result.Warnings).Should().NotContain("ghp_claude_secret_123");
            string.Join("\n", result.Warnings).Should().NotContain("ctx_secret_456");
            string.Join("\n", result.Warnings).Should().NotContain("sse_secret_789");

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var mcpServers = doc.RootElement.GetProperty("mcpServers");
            mcpServers.TryGetProperty("empty-allowed", out _).Should().BeFalse();

            var gh = mcpServers.GetProperty("github");
            gh.GetProperty("type").GetString().Should().Be("stdio");
            gh.GetProperty("command").GetString().Should().Be("npx");
            gh.GetProperty("args").EnumerateArray().Select(e => e.GetString()).Should().Equal("-y", "@modelcontextprotocol/server-github");
            gh.GetProperty("env").GetProperty("GITHUB_TOKEN").GetString().Should().Be("ghp_claude_secret_123");

            var ctx = mcpServers.GetProperty("context7");
            ctx.GetProperty("type").GetString().Should().Be("http");
            ctx.GetProperty("url").GetString().Should().Be("https://mcp.context7.com/mcp");
            ctx.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer ctx_secret_456");

            var sse = mcpServers.GetProperty("legacy-sse");
            sse.GetProperty("type").GetString().Should().Be("sse");
            sse.GetProperty("url").GetString().Should().Be("https://sse.example.com/sse");
            sse.GetProperty("headers").GetProperty("X-Api-Key").GetString().Should().Be("sse_secret_789");
        }

        File.Exists(configPath).Should().BeFalse("disposing Claude MCP provisioning must remove temp credentials");
    }

    [Fact]
    public void ProvisionForCopilot_WritesSecureAdditionalConfig_AppliesToolsAllowAndDenyFlags_AndCleansUp()
    {
        var servers = new List<ResolvedMcpServerConfig>
        {
            new()
            {
                Id = "github",
                Name = "GitHub",
                Transport = McpTransportType.Stdio,
                Command = "npx",
                Args = ["-y", "gh-mcp"],
                Env = new Dictionary<string, string> { ["TOKEN"] = "copilot_secret_env" },
                AllowedTools = ["list_prs", "create_pr", "delete_branch"],
                BlockedTools = ["delete_branch"]
            },
            new()
            {
                Id = "docs",
                Name = "Docs",
                Transport = McpTransportType.Http,
                Url = "https://docs.example.com/mcp",
                Headers = new Dictionary<string, string> { ["X-Auth"] = "copilot_secret_header" },
                BlockedTools = ["admin_reset"]
            }
        };

        string? configPath;
        using (var result = SubprocessMcpProvisioner.ProvisionForCopilot(servers, mode: "autopilot", tempDirectory: _tempRoot))
        {
            result.ProvisionedServerCount.Should().Be(2);
            configPath = result.ConfigFilePath!;
            File.Exists(configPath).Should().BeTrue();

            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(configPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            result.ExtraArguments.Should().ContainInOrder("--additional-mcp-config", $"@{configPath}");
            result.ExtraArguments.Should().ContainInOrder("--deny-tool", "github(delete_branch)");
            result.ExtraArguments.Should().ContainInOrder("--deny-tool", "docs(admin_reset)");

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var mcpServers = doc.RootElement.GetProperty("mcpServers");

            var gh = mcpServers.GetProperty("github");
            gh.GetProperty("type").GetString().Should().Be("local");
            gh.GetProperty("command").GetString().Should().Be("npx");
            gh.GetProperty("env").GetProperty("TOKEN").GetString().Should().Be("copilot_secret_env");
            gh.GetProperty("tools").EnumerateArray().Select(e => e.GetString()).Should().Equal("list_prs", "create_pr");

            var docs = mcpServers.GetProperty("docs");
            docs.GetProperty("type").GetString().Should().Be("http");
            docs.GetProperty("url").GetString().Should().Be("https://docs.example.com/mcp");
            docs.GetProperty("headers").GetProperty("X-Auth").GetString().Should().Be("copilot_secret_header");
            docs.GetProperty("tools").EnumerateArray().Select(e => e.GetString()).Should().Equal("*");
        }

        File.Exists(configPath).Should().BeFalse("disposing Copilot MCP provisioning must remove temp credentials");
    }

    [Fact]
    public void ProvisionForAgy_StagesAndRestoresMcpConfig_SupportsEnabledAndDisabledTools_AndSkipsSseWithWarning()
    {
        var fakeHome = Path.Combine(_tempRoot, "agy-home");
        var configDir = Path.Combine(fakeHome, ".gemini", "config");
        Directory.CreateDirectory(configDir);
        var mcpConfigPath = Path.Combine(configDir, "mcp_config.json");

        const string originalJson = """
            {
              "mcpServers": {
                "user-local": {
                  "command": "uvx",
                  "args": ["user-tool"],
                  "disabled": false
                }
              }
            }
            """;
        File.WriteAllText(mcpConfigPath, originalJson);

        var servers = new List<ResolvedMcpServerConfig>
        {
            new()
            {
                Id = "github",
                Name = "GitHub",
                Transport = McpTransportType.Stdio,
                Command = "npx",
                Args = ["-y", "gh-mcp"],
                Env = new Dictionary<string, string> { ["GH_MCP_PAT"] = "agy_secret_pat" },
                TimeoutSeconds = 45,
                AllowedTools = ["get_issue", "close_issue"],
                BlockedTools = ["close_issue"]
            },
            new()
            {
                Id = "context7",
                Name = "Context7",
                Transport = McpTransportType.Http,
                Url = "https://mcp.context7.com/mcp",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer agy_http_secret" },
                TimeoutSeconds = 20,
                BlockedTools = ["unsafe_op"]
            },
            new()
            {
                Id = "legacy-sse",
                Name = "Legacy SSE",
                Transport = McpTransportType.Sse,
                Url = "https://sse.example.com/sse"
            }
        };

        using (var result = SubprocessMcpProvisioner.ProvisionForAgy(servers, mode: "auto", homeDirectory: fakeHome))
        {
            result.ProvisionedServerCount.Should().Be(2);
            result.ConfigFilePath.Should().Be(mcpConfigPath);
            result.Warnings.Should().ContainSingle(w => w.Contains("SSE") && w.Contains("Legacy SSE"));
            string.Join("\n", result.Warnings).Should().NotContain("agy_secret_pat");
            string.Join("\n", result.Warnings).Should().NotContain("agy_http_secret");

            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(mcpConfigPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
            var mcpServers = doc.RootElement.GetProperty("mcpServers");
            mcpServers.TryGetProperty("user-local", out _).Should().BeTrue("pre-existing user server should be preserved during the turn");
            mcpServers.TryGetProperty("legacy-sse", out _).Should().BeFalse();

            var gh = mcpServers.GetProperty("github");
            gh.GetProperty("command").GetString().Should().Be("npx");
            gh.GetProperty("env").GetProperty("GH_MCP_PAT").GetString().Should().Be("agy_secret_pat");
            gh.GetProperty("timeoutSeconds").GetInt32().Should().Be(45);
            gh.GetProperty("disabled").GetBoolean().Should().BeFalse();
            gh.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).Should().Equal("get_issue");
            gh.GetProperty("disabledTools").EnumerateArray().Select(e => e.GetString()).Should().Equal("close_issue");

            var ctx = mcpServers.GetProperty("context7");
            ctx.GetProperty("serverUrl").GetString().Should().Be("https://mcp.context7.com/mcp");
            ctx.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer agy_http_secret");
            ctx.GetProperty("timeoutSeconds").GetInt32().Should().Be(20);
            ctx.GetProperty("disabledTools").EnumerateArray().Select(e => e.GetString()).Should().Equal("unsafe_op");
        }

        File.ReadAllText(mcpConfigPath).Should().Be(originalJson, "original mcp_config.json must be restored verbatim after turn");
    }

    [Fact]
    public void ProvisionForAgy_WhenNoConfigExistedBeforeAndOverlappingLeasesExist_DeletesOnlyAfterLastLeaseDisposes()
    {
        var fakeHome = Path.Combine(_tempRoot, "agy-clean-home");
        var mcpConfigPath = Path.Combine(fakeHome, ".gemini", "config", "mcp_config.json");

        var lease1 = SubprocessMcpProvisioner.ProvisionForAgy(
            [new ResolvedMcpServerConfig { Id = "srv-a", Name = "Srv A", Transport = McpTransportType.Stdio, Command = "cmd-a" }],
            mode: "auto",
            homeDirectory: fakeHome);

        var lease2 = SubprocessMcpProvisioner.ProvisionForAgy(
            [new ResolvedMcpServerConfig { Id = "srv-b", Name = "Srv B", Transport = McpTransportType.Stdio, Command = "cmd-b" }],
            mode: "auto",
            homeDirectory: fakeHome);

        using (var doc = JsonDocument.Parse(File.ReadAllText(mcpConfigPath)))
        {
            var mcpServers = doc.RootElement.GetProperty("mcpServers");
            mcpServers.TryGetProperty("srv-a", out _).Should().BeTrue();
            mcpServers.TryGetProperty("srv-b", out _).Should().BeTrue();
        }

        lease1.Dispose();
        File.Exists(mcpConfigPath).Should().BeTrue("lease2 is still active");
        using (var docAfter1 = JsonDocument.Parse(File.ReadAllText(mcpConfigPath)))
        {
            var mcpServers = docAfter1.RootElement.GetProperty("mcpServers");
            mcpServers.TryGetProperty("srv-a", out _).Should().BeFalse();
            mcpServers.TryGetProperty("srv-b", out _).Should().BeTrue();
        }

        lease2.Dispose();
        File.Exists(mcpConfigPath).Should().BeFalse("last lease disposal must delete ephemeral mcp_config.json when none existed before");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData(" DIRECT ")]
    public void DirectMode_SkipsProvisioningAcrossAllSubprocessDrivers(string mode)
    {
        var servers = new List<ResolvedMcpServerConfig>
        {
            new() { Id = "github", Name = "GitHub", Transport = McpTransportType.Stdio, Command = "npx" }
        };

        var fakeHome = Path.Combine(_tempRoot, "agy-direct-home");
        using var claude = SubprocessMcpProvisioner.ProvisionForClaude(servers, mode, _tempRoot);
        using var copilot = SubprocessMcpProvisioner.ProvisionForCopilot(servers, mode, _tempRoot);
        using var agy = SubprocessMcpProvisioner.ProvisionForAgy(servers, mode, fakeHome);

        claude.ProvisionedServerCount.Should().Be(0);
        claude.ExtraArguments.Should().BeEmpty();
        claude.ConfigFilePath.Should().BeNull();

        copilot.ProvisionedServerCount.Should().Be(0);
        copilot.ExtraArguments.Should().BeEmpty();
        copilot.ConfigFilePath.Should().BeNull();

        agy.ProvisionedServerCount.Should().Be(0);
        agy.ConfigFilePath.Should().BeNull();
        Directory.Exists(fakeHome).Should().BeFalse();
    }
}
