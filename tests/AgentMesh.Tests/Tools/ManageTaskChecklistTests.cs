using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Tools;

public class ManageTaskChecklistTests
{
    private readonly Mock<IHubContext<AgentMeshHub>> _hub = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly Mock<IGitHubPullRequestService> _pullRequests = new();
    private readonly Mock<IPlatformCallContextResolver> _resolver = new();

    public ManageTaskChecklistTests()
    {
        _clients.Setup(c => c.All).Returns(_clientProxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        _hub.Setup(h => h.Clients).Returns(_clients.Object);

        _clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static AgentMeshDbContext CreateInMemoryDb() => TestDbContextFactory.Create();

    private PlatformMcpTools BuildTools(AgentMeshDbContext db, Guid jobId)
    {
        _resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformCallContext(
                AgentId: "driver-implementer",
                AgentIdSource: "test",
                PersonaId: "implementer",
                PersonaIdSource: "test",
                SessionId: Guid.NewGuid(),
                SessionIdSource: "test",
                JobId: jobId,
                JobIdSource: "test",
                ProjectId: "proj-1",
                GitBranch: "main",
                GitRemoteUrl: null,
                GitBaseBranch: null,
                WorkspaceDirectory: "/workspace"));

        return new PlatformMcpTools(
            db,
            _resolver.Object,
            _pullRequests.Object,
            _hub.Object,
            NullLogger<PlatformMcpTools>.Instance,
            null,
            null
        );
    }

    [Fact]
    public async Task ManageTask_SetChecklist_EnforcesTaskCountCeiling()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Job Decomposition Test",
            AssignedAgentId = "implementer",
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var tools = BuildTools(db, jobId);

        // Attempt to register 7 tasks (exceeds max ceiling of 6)
        var sevenTasks = Enumerable.Range(1, 7)
            .Select(i => new ChecklistTaskItem($"Task {i}", $"src/File{i}.cs", $"test-{i}"))
            .ToList();

        var args = new ManageTaskArgs(
            Action: "set_checklist",
            Tasks: sevenTasks,
            ProgressNote: "Proposed 7 tasks"
        );

        // Act
        var rawResult = await tools.ManageTaskAsync(args, jobId.ToString());
        using var doc = JsonDocument.Parse(rawResult);
        var root = doc.RootElement;

        // Assert: Must reject with task_count_exceeded error
        root.GetProperty("error").GetString().Should().Be("task_count_exceeded");
        root.GetProperty("message").GetString().Should().Contain("consolidate tasks into 3 to 6 atomic");
    }

    [Fact]
    public async Task ManageTask_SetChecklist_EnforcesTaskCountMinimum()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Job Decomposition Test",
            AssignedAgentId = "implementer",
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var tools = BuildTools(db, jobId);

        // Attempt to register 2 tasks (below minimum of 3)
        var twoTasks = Enumerable.Range(1, 2)
            .Select(i => new ChecklistTaskItem($"Task {i}", $"src/File{i}.cs", $"test-{i}"))
            .ToList();

        var args = new ManageTaskArgs(
            Action: "set_checklist",
            Tasks: twoTasks,
            ProgressNote: "Proposed 2 tasks"
        );

        // Act
        var rawResult = await tools.ManageTaskAsync(args, jobId.ToString());
        using var doc = JsonDocument.Parse(rawResult);
        var root = doc.RootElement;

        // Assert: Must reject with task_count_below_minimum error
        root.GetProperty("error").GetString().Should().Be("task_count_below_minimum");
        root.GetProperty("message").GetString().Should().Contain("requires a minimum of 3 tasks");
    }

    [Fact]
    public async Task ManageTask_SetChecklist_RegistersTasksAndUpdatesCurrentTaskIdAndHeartbeat()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Valid Job Decomposition",
            AssignedAgentId = "implementer",
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var tools = BuildTools(db, jobId);

        // Register 4 tasks (valid: within 3 to 6 range)
        var fourTasks = Enumerable.Range(1, 4)
            .Select(i => new ChecklistTaskItem($"Task {i}", $"src/File{i}.cs", $"verify-{i}"))
            .ToList();

        var args = new ManageTaskArgs(
            Action: "set_checklist",
            Tasks: fourTasks,
            ProgressNote: "Decomposition complete. 4 tasks created."
        );

        // Act
        var rawResult = await tools.ManageTaskAsync(args, jobId.ToString());
        using var doc = JsonDocument.Parse(rawResult);
        var root = doc.RootElement;

        // Assert
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("taskCount").GetInt32().Should().Be(4);

        // Verify persisted state in db
        var savedJob = await db.ProjectJobs.FindAsync(jobId);
        savedJob.Should().NotBeNull();
        savedJob!.ProgressHeartbeat.Should().Be("Decomposition complete. 4 tasks created.");
        savedJob.CurrentTaskId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpdateTaskStatus_RejectsDuringDiscovery_EvenIfCallerSendsDifferentTaskId()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        // Job has no established checklist and is in discovery phase
        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Discovery Job",
            AssignedAgentId = "implementer",
            ProgressHeartbeat = "Exploring codebase and generating checklist",
            TasksJson = "[]",
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        // Context has an arbitrary task id, NOT "stage-1-discovery"
        _resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformCallContext(
                AgentId: "driver-implementer",
                AgentIdSource: "test",
                PersonaId: "implementer",
                PersonaIdSource: "test",
                SessionId: Guid.NewGuid(),
                SessionIdSource: "test",
                JobId: jobId,
                JobIdSource: "test",
                ProjectId: "proj-1",
                GitBranch: "main",
                GitRemoteUrl: null,
                GitBaseBranch: null,
                WorkspaceDirectory: "/workspace",
                TaskId: "custom-task-id-123")); // Note: not stage-1-discovery

        var tools = new PlatformMcpTools(
            db,
            _resolver.Object,
            _pullRequests.Object,
            _hub.Object,
            NullLogger<PlatformMcpTools>.Instance,
            null,
            null
        );

        var args = new ManageTaskArgs(
            Action: "update_task_status",
            TaskId: "custom-task-id-123",
            Status: "in_progress"
        );

        // Act
        var rawResult = await tools.ManageTaskAsync(args, jobId.ToString());
        using var doc = JsonDocument.Parse(rawResult);
        var root = doc.RootElement;

        // Assert: Must reject with discovery_phase_read_only based on server-side job state
        root.GetProperty("error").GetString().Should().Be("discovery_phase_read_only");
    }

    [Fact]
    public async Task ManageTask_SetChecklist_ReturnsError_WhenWorkflowSignalIsRejected()
    {
        // Arrange
        using var db = CreateInMemoryDb();
        var jobId = Guid.NewGuid();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = "proj-1",
            Title = "Valid Job Decomposition",
            AssignedAgentId = "implementer",
            Status = AgentMesh.Core.Enums.ProjectJobStatus.Active
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockWorkflows = new Mock<IWorkflowSignalRouter>();
        mockWorkflows
            .Setup(w => w.SendSetChecklistAsync(jobId, It.IsAny<List<ChecklistTaskItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalDelivery($"job-{jobId:D}", SignalDeliveryStatus.Rejected, "Workflow execution terminated"));

        _resolver
            .Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformCallContext(
                AgentId: "driver-implementer",
                AgentIdSource: "test",
                PersonaId: "implementer",
                PersonaIdSource: "test",
                SessionId: Guid.NewGuid(),
                SessionIdSource: "test",
                JobId: jobId,
                JobIdSource: "test",
                ProjectId: "proj-1",
                GitBranch: "main",
                GitRemoteUrl: null,
                GitBaseBranch: null,
                WorkspaceDirectory: "/workspace"));

        var tools = new PlatformMcpTools(
            db,
            _resolver.Object,
            _pullRequests.Object,
            _hub.Object,
            NullLogger<PlatformMcpTools>.Instance,
            null,
            mockWorkflows.Object
        );

        var tasks = Enumerable.Range(1, 3)
            .Select(i => new ChecklistTaskItem($"Task {i}", $"src/File{i}.cs", $"verify-{i}"))
            .ToList();

        var args = new ManageTaskArgs(
            Action: "set_checklist",
            Tasks: tasks,
            ProgressNote: "Proposed 3 tasks"
        );

        // Act
        var rawResult = await tools.ManageTaskAsync(args, jobId.ToString());
        using var doc = JsonDocument.Parse(rawResult);
        var root = doc.RootElement;

        // Assert: Must return workflow_signal_rejected instead of silently succeeding
        root.GetProperty("error").GetString().Should().Be("workflow_signal_rejected");
        root.GetProperty("message").GetString().Should().Contain("Workflow execution terminated");
    }
}
