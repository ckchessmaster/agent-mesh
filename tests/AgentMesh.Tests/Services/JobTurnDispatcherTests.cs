using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
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
