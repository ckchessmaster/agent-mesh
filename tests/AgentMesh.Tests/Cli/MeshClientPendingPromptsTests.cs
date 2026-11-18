using System.Net;
using System.Text;
using System.Text.Json;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Cli;

public class MeshClientPendingPromptsTests : IDisposable
{
    private readonly string _configPath;

    public MeshClientPendingPromptsTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"agentmesh-meshclient-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }
    }

    [Fact]
    public async Task GetPendingPromptsForAgentAsync_PropagatesWorkingDirectoryAndRepoMetadata_WithoutExtraProjectCall()
    {
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var metadataJson = JsonSerializer.Serialize(new
        {
            jobId = Guid.NewGuid().ToString(),
            projectId = "billing",
            projectName = "Billing Service",
            projectDescription = "Invoicing core",
            repositoryName = "billing-core",
            workingDirectory = "/workspace/repos/billing-core/",
            workspaceType = "Git",
            gitBranch = "mesh/job-123",
            gitUrl = "https://github.com/org/billing-core.git",
            gitBaseBranch = "main",
            gitAuthorName = "Astra",
            gitAuthorEmail = "astra@agent-mesh"
        });

        var projectRequests = 0;
        var client = CreateClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agents/agy/sessions", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = sessionId, status = "thinking", personaId = "astra", personaName = "Astra", metadataJson }
                });
            }
            if (path.EndsWith($"/api/sessions/{sessionId}/events", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = messageId, eventType = "prompt", content = "Fix bug", senderAgentId = "operator", createdAt = DateTimeOffset.UtcNow }
                });
            }
            if (path.StartsWith("/api/projects/", StringComparison.Ordinal))
            {
                projectRequests++;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var prompts = await client.GetPendingPromptsForAgentAsync("agy", "agent-token");

        prompts.Should().ContainSingle();
        var p = prompts[0];
        p.WorkingDirectory.Should().Be("/workspace/repos/billing-core");
        p.ProjectId.Should().Be("billing");
        p.ProjectName.Should().Be("Billing Service");
        p.ProjectDescription.Should().Be("Invoicing core");
        p.RepositoryName.Should().Be("billing-core");
        p.WorkspaceType.Should().Be("Git");
        p.GitBranch.Should().Be("mesh/job-123");
        p.GitUrl.Should().Be("https://github.com/org/billing-core.git");
        p.WorkspaceGitRemote.Should().Be("https://github.com/org/billing-core.git");
        p.GitBaseBranch.Should().Be("main");
        p.GitAuthorName.Should().Be("Astra");
        p.GitAuthorEmail.Should().Be("astra@agent-mesh");
        projectRequests.Should().Be(0, "session metadata already carries all project and repo fields");
    }

    [Fact]
    public async Task GetPendingPromptsForPersonaAsync_BackfillsFromProjectEndpointOnce_WhenMetadataLacksWorkingDirectory()
    {
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();
        var msg1 = Guid.NewGuid();
        var msg2 = Guid.NewGuid();
        var metadataJson = JsonSerializer.Serialize(new
        {
            jobId = Guid.NewGuid().ToString(),
            projectId = "payments",
            gitBranch = "mesh/job-pay"
        });

        var projectRequests = 0;
        var client = CreateClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/personas/astra/sessions", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = session1, status = "thinking", driverId = "agy", personaId = "astra", personaName = "Astra", metadataJson },
                    new { id = session2, status = "thinking", driverId = "agy", personaId = "astra", personaName = "Astra", metadataJson }
                });
            }
            if (path.EndsWith($"/api/sessions/{session1}/events", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = msg1, eventType = "prompt", content = "Turn 1", senderAgentId = "operator", createdAt = DateTimeOffset.UtcNow }
                });
            }
            if (path.EndsWith($"/api/sessions/{session2}/events", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = msg2, eventType = "prompt", content = "Turn 2", senderAgentId = "operator", createdAt = DateTimeOffset.UtcNow }
                });
            }
            if (path.EndsWith("/api/projects/payments", StringComparison.Ordinal))
            {
                projectRequests++;
                return JsonResponse(new
                {
                    id = "payments",
                    name = "Payments API",
                    description = "Payment processing",
                    workspaceType = (int)WorkspaceType.Git,
                    gitRepositoryUrl = "https://github.com/org/payments-api.git",
                    gitBaseBranch = "main",
                    repositoryName = "payments-api",
                    workingDirectory = "",
                    resolvedWorkingDirectory = "/workspace/repos/payments-api"
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var prompts = await client.GetPendingPromptsForPersonaAsync("astra", "agent-token");

        prompts.Should().HaveCount(2);
        projectRequests.Should().Be(1, "project metadata should be cached across sessions in the same poll");
        foreach (var p in prompts)
        {
            p.WorkingDirectory.Should().Be("/workspace/repos/payments-api");
            p.ProjectId.Should().Be("payments");
            p.ProjectName.Should().Be("Payments API");
            p.ProjectDescription.Should().Be("Payment processing");
            p.RepositoryName.Should().Be("payments-api");
            p.WorkspaceType.Should().Be("Git");
            p.GitUrl.Should().Be("https://github.com/org/payments-api.git");
            p.WorkspaceGitRemote.Should().Be("https://github.com/org/payments-api.git");
            p.GitBaseBranch.Should().Be("main");
        }
    }

    [Fact]
    public async Task GetPendingPromptsForAgentAsync_DerivesWorkingDirectoryFromRepositoryName_WhenProjectEndpointFails()
    {
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var metadataJson = JsonSerializer.Serialize(new
        {
            projectId = "legacy-app",
            repositoryName = "legacy-svc",
            workspaceGitRemote = "https://github.com/org/legacy-svc.git"
        });

        var client = CreateClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agents/agy/sessions", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = sessionId, status = "thinking", metadataJson }
                });
            }
            if (path.EndsWith($"/api/sessions/{sessionId}/events", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = messageId, eventType = "prompt", content = "Inspect legacy", senderAgentId = "operator", createdAt = DateTimeOffset.UtcNow }
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var prompts = await client.GetPendingPromptsForAgentAsync("agy", null);

        prompts.Should().ContainSingle();
        prompts[0].WorkingDirectory.Should().Be($"{WorkspacePathResolver.ReposRoot}/legacy-svc");
        prompts[0].RepositoryName.Should().Be("legacy-svc");
        prompts[0].GitUrl.Should().Be("https://github.com/org/legacy-svc.git");
        prompts[0].WorkspaceGitRemote.Should().Be("https://github.com/org/legacy-svc.git");
    }

    [Fact]
    public async Task GetProjectAsync_DeserializesNumericAndStringWorkspaceTypes()
    {
        var client = CreateClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/projects/s3-proj", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    id = "s3-proj",
                    name = "S3 Project",
                    description = "Archive project",
                    workspaceType = 1,
                    gitBaseBranch = "main",
                    repositoryName = "s3-repo",
                    workingDirectory = "",
                    resolvedWorkingDirectory = "/workspace/repos/s3-repo"
                });
            }
            if (path.EndsWith("/api/projects/git-proj", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    id = "git-proj",
                    name = "Git Project",
                    description = "Git repo",
                    workspaceType = "Git",
                    gitRepositoryUrl = "https://github.com/org/git-repo.git",
                    gitBaseBranch = "develop",
                    repositoryName = "git-repo",
                    workingDirectory = "/workspace/repos/custom-git",
                    resolvedWorkingDirectory = "/workspace/repos/custom-git"
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var s3 = await client.GetProjectAsync("s3-proj");
        var git = await client.GetProjectAsync("git-proj");
        var missing = await client.GetProjectAsync("unknown");

        s3.Should().NotBeNull();
        s3!.WorkspaceType.Should().Be(WorkspaceType.S3Archive);
        s3.ResolvedWorkingDirectory.Should().Be("/workspace/repos/s3-repo");

        git.Should().NotBeNull();
        git!.WorkspaceType.Should().Be(WorkspaceType.Git);
        git.GitRepositoryUrl.Should().Be("https://github.com/org/git-repo.git");
        git.GitBaseBranch.Should().Be("develop");
        git.ResolvedWorkingDirectory.Should().Be("/workspace/repos/custom-git");

        missing.Should().BeNull();
    }

    [Fact]
    public async Task GetResolvedPersonaMcpServersAsync_And_GetPendingPromptsForPersonaAsync_DeliverAndCacheMcpConfigs()
    {
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();
        var msg1 = Guid.NewGuid();
        var msg2 = Guid.NewGuid();
        var metadataJson = JsonSerializer.Serialize(new
        {
            jobId = Guid.NewGuid().ToString(),
            projectId = "payments",
            workingDirectory = "/workspace/repos/payments"
        });

        var mcpRequests = 0;
        string? capturedAuth = null;
        string? capturedAgentId = null;

        var client = CreateClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/personas/astra/sessions", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = session1, status = "thinking", driverId = "agy", personaId = "astra", personaName = "Astra", metadataJson },
                    new { id = session2, status = "thinking", driverId = "agy", personaId = "astra", personaName = "Astra", metadataJson }
                });
            }
            if (path.EndsWith($"/api/sessions/{session1}/events", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = msg1, eventType = "prompt", content = "Turn 1", senderAgentId = "operator", createdAt = DateTimeOffset.UtcNow }
                });
            }
            if (path.EndsWith($"/api/sessions/{session2}/events", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new { id = msg2, eventType = "prompt", content = "Turn 2", senderAgentId = "operator", createdAt = DateTimeOffset.UtcNow }
                });
            }
            if (path.EndsWith("/api/personas/astra/mcp-servers/resolved", StringComparison.Ordinal))
            {
                mcpRequests++;
                capturedAuth = req.Headers.Authorization?.ToString();
                capturedAgentId = req.Headers.TryGetValues("X-Agent-Id", out var vals) ? vals.FirstOrDefault() : null;
                return JsonResponse(new object[]
                {
                    new
                    {
                        id = "github",
                        name = "GitHub",
                        transport = "Stdio",
                        command = "npx",
                        args = new[] { "-y", "@modelcontextprotocol/server-github" },
                        env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_resolved_1234" },
                        headers = new Dictionary<string, string>(),
                        timeoutSeconds = 45,
                        allowedTools = new[] { "get_issue" },
                        blockedTools = new[] { "delete_repo" }
                    },
                    new
                    {
                        id = "context7",
                        name = "Context7",
                        transport = "Http",
                        url = "https://mcp.context7.com/mcp",
                        env = new Dictionary<string, string>(),
                        headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ctx7_secret" },
                        timeoutSeconds = 30
                    }
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var prompts = await client.GetPendingPromptsForPersonaAsync("astra", "mesh_tok_runner");

        prompts.Should().HaveCount(2);
        mcpRequests.Should().Be(1, "resolved MCP config should be fetched once per persona per poll cycle");
        capturedAuth.Should().Be("Bearer mesh_tok_runner");
        capturedAgentId.Should().Be("agy");

        foreach (var p in prompts)
        {
            p.McpServers.Should().HaveCount(2);
            p.McpServers![0].Id.Should().Be("github");
            p.McpServers[0].Transport.Should().Be(McpTransportType.Stdio);
            p.McpServers[0].Env["GITHUB_TOKEN"].Should().Be("ghp_resolved_1234");
            p.McpServers[0].AllowedTools.Should().Equal("get_issue");
            p.McpServers[0].BlockedTools.Should().Equal("delete_repo");

            p.McpServers[1].Id.Should().Be("context7");
            p.McpServers[1].Transport.Should().Be(McpTransportType.Http);
            p.McpServers[1].Headers["Authorization"].Should().Be("Bearer ctx7_secret");
        }

        // Direct fetch returns null on error/404 and does not throw
        var missing = await client.GetResolvedPersonaMcpServersAsync("unknown-persona", "mesh_tok_runner");
        missing.Should().BeNull();
    }

    [Fact]
    public void AgentRunnerInstance_ExtractJsonMcpServers_ParsesArrayAndDistinguishesNullVsEmpty()
    {
        using var absentDoc = JsonDocument.Parse("""{"sessionId":"00000000-0000-0000-0000-000000000001"}""");
        AgentRunnerInstance.ExtractJsonMcpServers(absentDoc.RootElement).Should().BeNull();

        using var emptyDoc = JsonDocument.Parse("""{"mcpServers":[]}""");
        AgentRunnerInstance.ExtractJsonMcpServers(emptyDoc.RootElement).Should().NotBeNull().And.BeEmpty();

        using var populatedDoc = JsonDocument.Parse("""
        {
          "mcpServers": [
            {
              "id": "context7",
              "name": "Context7",
              "transport": "Http",
              "url": "https://mcp.context7.com/mcp",
              "headers": { "X-Api-Key": "secret-val" },
              "timeoutSeconds": 25
            }
          ]
        }
        """);
        var parsed = AgentRunnerInstance.ExtractJsonMcpServers(populatedDoc.RootElement);
        parsed.Should().ContainSingle();
        parsed![0].Id.Should().Be("context7");
        parsed[0].Transport.Should().Be(McpTransportType.Http);
        parsed[0].Headers["X-Api-Key"].Should().Be("secret-val");
        parsed[0].TimeoutSeconds.Should().Be(25);
    }

    private MeshClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var store = new ConfigStore(_configPath);
        var config = new CliConfig
        {
            MeshUrl = "http://localhost:5000",
            MasterToken = "test-master-token"
        };
        var httpClient = new HttpClient(new StubHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        return new MeshClient(config, store, httpClient);
    }

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
