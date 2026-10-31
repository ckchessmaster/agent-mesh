using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Workflows;

public class HttpAgentTurnRunnerSessionResolutionTests
{
    private readonly string _dbName = "TurnRunnerResolutionDb_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    private static HttpAgentTurnRunner CreateRunner(
        AgentMeshDbContext db,
        IWorkspaceStateRepository checkpoints)
    {
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        var config = new ConfigurationBuilder().Build();

        return new HttpAgentTurnRunner(
            db,
            checkpoints,
            httpFactoryMock.Object,
            config,
            NullLogger<HttpAgentTurnRunner>.Instance);
    }

    private static AgentTurnRequest MakeRequest(
        Guid jobId,
        string taskId,
        string? turnSessionId = null)
    {
        var input = new TaskExecutionInput(
            JobId: jobId.ToString(),
            TaskId: taskId,
            AgentId: "driver-forge",
            ProjectId: "agent-mesh",
            WorkspacePath: "/workspace/repos/agent-mesh",
            TargetBranch: "main",
            PersonaId: "forge",
            TurnSessionId: turnSessionId);

        return new AgentTurnRequest(
            Input: input,
            Prompt: "Do the work",
            WorkspacePath: "/workspace/repos/agent-mesh",
            PersonaId: "forge",
            AgentId: "driver-forge",
            TurnSessionId: turnSessionId);
    }

    [Fact]
    public async Task ResolveExistingSessionAsync_WhenExplicitSessionIsCompleted_ReturnsNull()
    {
        using var db = Db();
        var completedSessionId = Guid.NewGuid();

        db.AgentSessions.Add(new AgentSession
        {
            Id = completedSessionId,
            Title = "Closed session",
            Status = "completed",
            DriverId = "driver-forge",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var repoMock = new Mock<IWorkspaceStateRepository>();
        repoMock.Setup(r => r.GetCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync((TaskWorkingCheckpoint?)null);

        var runner = CreateRunner(db, repoMock.Object);
        var request = MakeRequest(Guid.NewGuid(), "task-1", turnSessionId: completedSessionId.ToString());

        var resolved = await runner.ResolveExistingSessionAsync(request, CancellationToken.None);

        resolved.Should().BeNull("completed session must not be re-attached to; must trigger a fresh dispatch");
    }

    [Fact]
    public async Task ResolveExistingSessionAsync_WhenExplicitSessionIsRunning_ReturnsExplicitId()
    {
        using var db = Db();
        var liveSessionId = Guid.NewGuid();

        db.AgentSessions.Add(new AgentSession
        {
            Id = liveSessionId,
            Title = "Live session",
            Status = "running",
            DriverId = "driver-forge",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var repoMock = new Mock<IWorkspaceStateRepository>();
        repoMock.Setup(r => r.GetCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync((TaskWorkingCheckpoint?)null);

        var runner = CreateRunner(db, repoMock.Object);
        var request = MakeRequest(Guid.NewGuid(), "task-1", turnSessionId: liveSessionId.ToString());

        var resolved = await runner.ResolveExistingSessionAsync(request, CancellationToken.None);

        resolved.Should().Be(liveSessionId);
    }

    [Fact]
    public async Task ResolveExistingSessionAsync_WhenCheckpointHasLiveSession_PrefersCheckpointOverExplicitId()
    {
        using var db = Db();
        var checkpointSessionId = Guid.NewGuid();
        var explicitSessionId = Guid.NewGuid();

        db.AgentSessions.AddRange(
            new AgentSession
            {
                Id = checkpointSessionId,
                Title = "Checkpoint session",
                Status = "running",
                DriverId = "driver-forge",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new AgentSession
            {
                Id = explicitSessionId,
                Title = "Explicit session",
                Status = "running",
                DriverId = "driver-forge",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var jobId = Guid.NewGuid();
        var repoMock = new Mock<IWorkspaceStateRepository>();
        repoMock.Setup(r => r.GetCheckpointAsync(jobId.ToString(), "task-1", default))
            .ReturnsAsync(new TaskWorkingCheckpoint(
                JobId: jobId.ToString(),
                TaskId: "task-1",
                ActiveBranch: "main",
                Status: JobTaskStatus.InProgress,
                LastSummary: "summary",
                ModifiedFiles: [],
                ActiveStash: null,
                UpdatedAt: DateTimeOffset.UtcNow,
                TurnSessionId: checkpointSessionId.ToString()));

        var runner = CreateRunner(db, repoMock.Object);
        var request = MakeRequest(jobId, "task-1", turnSessionId: explicitSessionId.ToString());

        var resolved = await runner.ResolveExistingSessionAsync(request, CancellationToken.None);

        resolved.Should().Be(checkpointSessionId, "the checkpoint represents the freshest state of this task");
    }

    [Fact]
    public async Task ResolveExistingSessionAsync_WhenCheckpointIsTerminal_FallsBackToLiveExplicitSession()
    {
        using var db = Db();
        var checkpointSessionId = Guid.NewGuid();
        var explicitSessionId = Guid.NewGuid();

        db.AgentSessions.AddRange(
            new AgentSession
            {
                Id = checkpointSessionId,
                Title = "Checkpoint session",
                Status = "completed",
                DriverId = "driver-forge",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            },
            new AgentSession
            {
                Id = explicitSessionId,
                Title = "Explicit session",
                Status = "running",
                DriverId = "driver-forge",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var jobId = Guid.NewGuid();
        var repoMock = new Mock<IWorkspaceStateRepository>();
        repoMock.Setup(r => r.GetCheckpointAsync(jobId.ToString(), "task-1", default))
            .ReturnsAsync(new TaskWorkingCheckpoint(
                JobId: jobId.ToString(),
                TaskId: "task-1",
                ActiveBranch: "main",
                Status: JobTaskStatus.InProgress,
                LastSummary: "summary",
                ModifiedFiles: [],
                ActiveStash: null,
                UpdatedAt: DateTimeOffset.UtcNow,
                TurnSessionId: checkpointSessionId.ToString()));

        var runner = CreateRunner(db, repoMock.Object);
        var request = MakeRequest(jobId, "task-1", turnSessionId: explicitSessionId.ToString());

        var resolved = await runner.ResolveExistingSessionAsync(request, CancellationToken.None);

        resolved.Should().Be(explicitSessionId, "falls back to live explicit session if checkpoint session is completed");
    }

    [Fact]
    public async Task ResolveExistingSessionAsync_WhenBothAreTerminal_ReturnsNull()
    {
        using var db = Db();
        var checkpointSessionId = Guid.NewGuid();
        var explicitSessionId = Guid.NewGuid();

        db.AgentSessions.AddRange(
            new AgentSession
            {
                Id = checkpointSessionId,
                Title = "Checkpoint session",
                Status = "completed",
                DriverId = "driver-forge",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            },
            new AgentSession
            {
                Id = explicitSessionId,
                Title = "Explicit session",
                Status = "idle",
                DriverId = "driver-forge",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            });
        await db.SaveChangesAsync();

        var jobId = Guid.NewGuid();
        var repoMock = new Mock<IWorkspaceStateRepository>();
        repoMock.Setup(r => r.GetCheckpointAsync(jobId.ToString(), "task-1", default))
            .ReturnsAsync(new TaskWorkingCheckpoint(
                JobId: jobId.ToString(),
                TaskId: "task-1",
                ActiveBranch: "main",
                Status: JobTaskStatus.InProgress,
                LastSummary: "summary",
                ModifiedFiles: [],
                ActiveStash: null,
                UpdatedAt: DateTimeOffset.UtcNow,
                TurnSessionId: checkpointSessionId.ToString()));

        var runner = CreateRunner(db, repoMock.Object);
        var request = MakeRequest(jobId, "task-1", turnSessionId: explicitSessionId.ToString());

        var resolved = await runner.ResolveExistingSessionAsync(request, CancellationToken.None);

        resolved.Should().BeNull();
    }
}
