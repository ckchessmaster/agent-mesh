using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

public class JobTurnDispatcherTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task DispatchAsync_PropagatesResolvedWorkingDirectoryAndRepoMetadata_ToMetadataAndSignalRPayload()
    {
        using var db = TestDbContextFactory.Create();
        var (hubContext, capturedPayloads) = CreateCapturingHub();

        var project = new Project
        {
            Id = "billing",
            Name = "Billing Service",
            Description = "Handles invoicing and subscriptions",
            WorkspaceType = WorkspaceType.Git,
            GitRepositoryUrl = "https://github.com/org/billing-core.git",
            GitBaseBranch = "main",
            RepositoryName = "billing-core",
            WorkingDirectory = string.Empty
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Add retry backoff",
            GitBranchName = "mesh/job-retry"
        };
        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            Role = "Senior Engineer",
            DriverId = "agy",
            GitAuthorName = "Astra",
            GitAuthorEmail = "astra@agent-mesh"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        var dispatcher = new JobTurnDispatcher(db, hubContext);
        var result = await dispatcher.DispatchAsync(new JobTurnRequest(
            Job: job,
            Project: project,
            Persona: persona,
            Prompt: "Implement exponential backoff",
            Sender: "operator",
            TaskId: "task-123"));

        var expectedDir = $"{WorkspacePathResolver.ReposRoot}/billing-core";

        // 1. Session MetadataJson carries workingDirectory and repo metadata
        using var metaDoc = JsonDocument.Parse(result.Session.MetadataJson!);
        var meta = metaDoc.RootElement;
        meta.GetProperty("workingDirectory").GetString().Should().Be(expectedDir);
        meta.GetProperty("repositoryName").GetString().Should().Be("billing-core");
        meta.GetProperty("projectId").GetString().Should().Be("billing");
        meta.GetProperty("projectName").GetString().Should().Be("Billing Service");
        meta.GetProperty("projectDescription").GetString().Should().Be("Handles invoicing and subscriptions");
        meta.GetProperty("workspaceType").GetString().Should().Be("Git");
        meta.GetProperty("gitBranch").GetString().Should().Be("mesh/job-retry");
        meta.GetProperty("gitUrl").GetString().Should().Be("https://github.com/org/billing-core.git");
        meta.GetProperty("gitBaseBranch").GetString().Should().Be("main");
        meta.GetProperty("workspaceGitRemote").GetString().Should().Be("https://github.com/org/billing-core.git");
        meta.GetProperty("workspaceGitRef").GetString().Should().Be("mesh/job-retry");
        meta.GetProperty("gitAuthorName").GetString().Should().Be("Astra");
        meta.GetProperty("gitAuthorEmail").GetString().Should().Be("astra@agent-mesh");
        meta.GetProperty("taskId").GetString().Should().Be("task-123");

        // 2. SignalR PromptReceived payload deserializes cleanly into PromptReceivedEvent
        capturedPayloads.Should().NotBeEmpty();
        var wireJson = JsonSerializer.Serialize(capturedPayloads[0], JsonOpts);
        var ev = JsonSerializer.Deserialize<PromptReceivedEvent>(wireJson, JsonOpts);
        ev.Should().NotBeNull();
        ev!.WorkingDirectory.Should().Be(expectedDir);
        ev.RepositoryName.Should().Be("billing-core");
        ev.ProjectId.Should().Be("billing");
        ev.ProjectName.Should().Be("Billing Service");
        ev.ProjectDescription.Should().Be("Handles invoicing and subscriptions");
        ev.WorkspaceType.Should().Be("Git");
        ev.GitBranch.Should().Be("mesh/job-retry");
        ev.GitUrl.Should().Be("https://github.com/org/billing-core.git");
        ev.GitBaseBranch.Should().Be("main");
        ev.WorkspaceGitRemote.Should().Be("https://github.com/org/billing-core.git");
        ev.GitAuthorName.Should().Be("Astra");
        ev.GitAuthorEmail.Should().Be("astra@agent-mesh");
    }

    [Fact]
    public async Task DispatchAsync_PrefersExplicitProjectWorkingDirectory_OverRepositoryNameConvention()
    {
        using var db = TestDbContextFactory.Create();
        var (hubContext, capturedPayloads) = CreateCapturingHub();

        var project = new Project
        {
            Id = "mono-api",
            Name = "Monorepo API",
            WorkspaceType = WorkspaceType.Git,
            RepositoryName = "monorepo",
            WorkingDirectory = "/workspace/repos/monorepo/services/api/"
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Fix health check",
            GitBranchName = "mesh/job-health"
        };
        var persona = new Persona
        {
            Id = "rook",
            Name = "Rook",
            Role = "Engineer",
            DriverId = "agy"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        var dispatcher = new JobTurnDispatcher(db, hubContext);
        var result = await dispatcher.DispatchAsync(new JobTurnRequest(job, project, persona, "Check health", "operator"));

        using var metaDoc = JsonDocument.Parse(result.Session.MetadataJson!);
        metaDoc.RootElement.GetProperty("workingDirectory").GetString().Should().Be("/workspace/repos/monorepo/services/api");
        metaDoc.RootElement.GetProperty("repositoryName").GetString().Should().Be("monorepo");

        var wireJson = JsonSerializer.Serialize(capturedPayloads[0], JsonOpts);
        var ev = JsonSerializer.Deserialize<PromptReceivedEvent>(wireJson, JsonOpts);
        ev!.WorkingDirectory.Should().Be("/workspace/repos/monorepo/services/api");
        ev.RepositoryName.Should().Be("monorepo");
    }

    [Fact]
    public async Task DispatchAsync_PrefersJobWorkingDirectoryOverride_WhenSpecified()
    {
        using var db = TestDbContextFactory.Create();
        var (hubContext, capturedPayloads) = CreateCapturingHub();

        var project = new Project
        {
            Id = "proj-override",
            Name = "Override Project",
            WorkspaceType = WorkspaceType.Git,
            RepositoryName = "proj-override",
            WorkingDirectory = "/workspace/repos/proj-override"
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Run in isolated worktree",
            GitBranchName = "mesh/job-wt",
            WorkingDirectory = "/workspace/repos/worktrees/job-wt/"
        };
        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            Role = "Engineer",
            DriverId = "agy"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        var dispatcher = new JobTurnDispatcher(db, hubContext);
        var result = await dispatcher.DispatchAsync(new JobTurnRequest(job, project, persona, "Go", "operator"));

        using var metaDoc = JsonDocument.Parse(result.Session.MetadataJson!);
        metaDoc.RootElement.GetProperty("workingDirectory").GetString().Should().Be("/workspace/repos/worktrees/job-wt");

        var wireJson = JsonSerializer.Serialize(capturedPayloads[0], JsonOpts);
        var ev = JsonSerializer.Deserialize<PromptReceivedEvent>(wireJson, JsonOpts);
        ev!.WorkingDirectory.Should().Be("/workspace/repos/worktrees/job-wt");
    }

    [Fact]
    public async Task DispatchAsync_DeliversDecryptedPersonaMcpConfigsToTargetedGroups_WithoutLeakingSecretsToAllOrMetadata()
    {
        using var db = TestDbContextFactory.Create();
        var encryption = new AesCredentialEncryptionService(
            SHA256.HashData(Encoding.UTF8.GetBytes("dispatcher-mcp-test-key-32-bytes")));

        var allPayloads = new List<object>();
        var groupPayloads = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        var allProxy = new Mock<IClientProxy>();
        allProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) =>
            {
                if (method == "PromptReceived" && args.Length > 0 && args[0] is not null)
                {
                    allPayloads.Add(args[0]!);
                }
            })
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(allProxy.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns((string groupName) =>
        {
            var groupProxy = new Mock<IClientProxy>();
            groupProxy
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object?[], CancellationToken>((method, args, _) =>
                {
                    if (method == "PromptReceived" && args.Length > 0 && args[0] is not null)
                    {
                        if (!groupPayloads.TryGetValue(groupName, out var list))
                        {
                            list = new List<object>();
                            groupPayloads[groupName] = list;
                        }
                        list.Add(args[0]!);
                    }
                })
                .Returns(Task.CompletedTask);
            return groupProxy.Object;
        });

        var hub = new Mock<IHubContext<AgentMeshHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var project = new Project
        {
            Id = "billing",
            Name = "Billing",
            WorkspaceType = WorkspaceType.Git,
            RepositoryName = "billing"
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Wire MCP",
            GitBranchName = "mesh/job-mcp"
        };
        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            Role = "Senior Engineer",
            DriverId = "agy"
        };
        var githubServer = new McpServer
        {
            Id = "github",
            Name = "GitHub",
            Transport = McpTransportType.Stdio,
            Command = "npx",
            ArgsJson = "[\"-y\",\"@modelcontextprotocol/server-github\"]",
            EnvEncrypted = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
            {
                ["GITHUB_TOKEN"] = "ghp_catalog_secret_0001",
                ["GITHUB_HOST"] = "github.com"
            }),
            Enabled = true
        };
        var disabledServer = new McpServer
        {
            Id = "disabled-srv",
            Name = "Disabled",
            Transport = McpTransportType.Http,
            Url = "https://disabled.example.com/mcp",
            Enabled = false
        };

        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.Personas.Add(persona);
        db.McpServers.AddRange(githubServer, disabledServer);
        db.PersonaMcpServers.AddRange(
            new PersonaMcpServer
            {
                PersonaId = "astra",
                McpServerId = "github",
                Enabled = true,
                EnvOverridesEncrypted = McpSecretProtector.EncryptDictionary(encryption, new Dictionary<string, string>
                {
                    ["GITHUB_TOKEN"] = "ghp_astra_secret_9999"
                }),
                AllowedToolsJson = "[\"get_issue\"]",
                BlockedToolsJson = "[\"delete_repo\"]"
            },
            new PersonaMcpServer
            {
                PersonaId = "astra",
                McpServerId = "disabled-srv",
                Enabled = true
            });
        await db.SaveChangesAsync();

        var dispatcher = new JobTurnDispatcher(db, hub.Object, workspaces: null, containerManager: null, encryptionService: encryption);
        var result = await dispatcher.DispatchAsync(new JobTurnRequest(job, project, persona, "Run task", "operator"));

        // 1. MetadataJson persisted in DB must never contain decrypted secrets
        result.Session.MetadataJson.Should().NotContain("ghp_astra_secret_9999");
        result.Session.MetadataJson.Should().NotContain("ghp_catalog_secret_0001");

        // 2. Targeted runner groups receive decrypted, merged MCP server configs
        groupPayloads.Should().ContainKey("persona-astra");
        groupPayloads.Should().ContainKey("agent-agy");

        foreach (var targetGroup in new[] { "persona-astra", "agent-agy" })
        {
            var wireJson = JsonSerializer.Serialize(groupPayloads[targetGroup][0], JsonOpts);
            var ev = JsonSerializer.Deserialize<PromptReceivedEvent>(wireJson, JsonOpts);
            ev.Should().NotBeNull();
            ev!.McpServers.Should().ContainSingle();
            var srv = ev.McpServers![0];
            srv.Id.Should().Be("github");
            srv.Transport.Should().Be(McpTransportType.Stdio);
            srv.Command.Should().Be("npx");
            srv.Args.Should().Equal("-y", "@modelcontextprotocol/server-github");
            srv.Env.Should().Equal(new Dictionary<string, string>
            {
                ["GITHUB_TOKEN"] = "ghp_astra_secret_9999",
                ["GITHUB_HOST"] = "github.com"
            });
            srv.AllowedTools.Should().Equal("get_issue");
            srv.BlockedTools.Should().Equal("delete_repo");
        }

        // 3. Clients.All must not receive decrypted MCP secrets
        allPayloads.Should().ContainSingle();
        var broadcastJson = JsonSerializer.Serialize(allPayloads[0], JsonOpts);
        broadcastJson.Should().NotContain("ghp_astra_secret_9999");
        broadcastJson.Should().NotContain("ghp_catalog_secret_0001");
        var broadcastEv = JsonSerializer.Deserialize<PromptReceivedEvent>(broadcastJson, JsonOpts);
        broadcastEv!.McpServers.Should().BeNull();
    }

    private static (IHubContext<AgentMeshHub> Hub, List<object> CapturedPayloads) CreateCapturingHub()
    {
        var captured = new List<object>();
        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) =>
            {
                if (method == "PromptReceived" && args.Length > 0 && args[0] is not null)
                {
                    captured.Add(args[0]!);
                }
            })
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(proxy.Object);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);

        var hub = new Mock<IHubContext<AgentMeshHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return (hub.Object, captured);
    }
}
