using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class JobsControllerTests
{
    [Fact]
    public async Task GetJobs_ReturnsAllJobs_OrderedByCreatedAtDesc()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var agent = new Agent { Id = "test-agent", Name = "Test Agent", RuntimeType = RuntimeType.McpCloud };
        var session = new AgentSession { Id = Guid.NewGuid(), Title = "Session 1" };
        db.Agents.Add(agent);
        db.AgentSessions.Add(session);

        var job1 = new AgentJob
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RequestedByAgentId = agent.Id,
            TargetService = "github",
            ActionVerb = "apply_diff",
            PayloadJson = "{}",
            ApprovalStatus = ApprovalStatus.PendingHuman,
            ExecutionStatus = ExecutionStatus.Staged,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var job2 = new AgentJob
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RequestedByAgentId = agent.Id,
            TargetService = "platform_ops",
            ActionVerb = "stage_journal_entry",
            PayloadJson = "{}",
            ApprovalStatus = ApprovalStatus.AutoApproved,
            ExecutionStatus = ExecutionStatus.Completed,
            CreatedAt = DateTime.UtcNow
        };

        db.AgentJobs.AddRange(job1, job2);
        await db.SaveChangesAsync();

        var controller = new JobsController(db);

        // Act
        var result = await controller.GetJobs(null);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedJobs = okResult.Value.Should().BeAssignableTo<IEnumerable<AgentJob>>().Subject.ToList();

        returnedJobs.Should().HaveCount(2);
        returnedJobs.First().Id.Should().Be(job2.Id); // job2 is newer
    }

    [Fact]
    public async Task GetJobs_WithPendingHumanFilter_ReturnsOnlyPendingJobs()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var agent = new Agent { Id = "test-agent", Name = "Test Agent", RuntimeType = RuntimeType.McpCloud };
        var session = new AgentSession { Id = Guid.NewGuid(), Title = "Session 1" };
        db.Agents.Add(agent);
        db.AgentSessions.Add(session);

        var pendingJob = new AgentJob
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RequestedByAgentId = agent.Id,
            TargetService = "github",
            ActionVerb = "apply_diff",
            PayloadJson = "{}",
            ApprovalStatus = ApprovalStatus.PendingHuman
        };

        var approvedJob = new AgentJob
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RequestedByAgentId = agent.Id,
            TargetService = "platform_ops",
            ActionVerb = "stage_journal_entry",
            PayloadJson = "{}",
            ApprovalStatus = ApprovalStatus.Approved
        };

        db.AgentJobs.AddRange(pendingJob, approvedJob);
        await db.SaveChangesAsync();

        var controller = new JobsController(db);

        // Act
        var result = await controller.GetJobs("PendingHuman");

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedJobs = okResult.Value.Should().BeAssignableTo<IEnumerable<AgentJob>>().Subject.ToList();

        returnedJobs.Should().HaveCount(1);
        returnedJobs.Single().Id.Should().Be(pendingJob.Id);
    }

    [Fact]
    public async Task GetJob_ExistingId_ReturnsOkWithJob()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var agent = new Agent { Id = "test-agent", Name = "Test Agent", RuntimeType = RuntimeType.McpCloud };
        var session = new AgentSession { Id = Guid.NewGuid(), Title = "Session 1" };
        db.Agents.Add(agent);
        db.AgentSessions.Add(session);

        var job = new AgentJob
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RequestedByAgentId = agent.Id,
            TargetService = "github",
            ActionVerb = "apply_diff",
            PayloadJson = "{}",
            ApprovalStatus = ApprovalStatus.PendingHuman
        };
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobsController(db);

        // Act
        var result = await controller.GetJob(job.Id);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var returnedJob = okResult.Value.Should().BeOfType<AgentJob>().Subject;
        returnedJob.Id.Should().Be(job.Id);
        returnedJob.RequestedByAgent.Should().NotBeNull();
        returnedJob.RequestedByAgent!.Name.Should().Be("Test Agent");
    }

    [Fact]
    public async Task GetJob_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new JobsController(db);

        // Act
        var result = await controller.GetJob(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }
}
