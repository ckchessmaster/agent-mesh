using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class ProjectJobsControllerTests
{
    [Fact]
    public async Task CreateJob_CreatesJobWithBranchAndTasks()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project
        {
            Id = "core-app",
            Name = "Core App",
            WorkspaceType = WorkspaceType.Git,
            GitBaseBranch = "main"
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var req = new CreateProjectJobRequest(
            Title: "Build Auth Provider",
            Description: "Implement JWT flow",
            InitialTasks: new List<string> { "Task 1", "Task 2" }
        );

        var result = await controller.CreateJob("core-app", req);
        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<ProjectJobDto>().Subject;

        dto.Title.Should().Be("Build Auth Provider");
        dto.ProjectId.Should().Be("core-app");
        dto.GitBranchName.Should().StartWith("mesh/job-");
        dto.Tasks.Should().HaveCount(2);
        dto.Tasks[0].Title.Should().Be("Task 1");
        dto.Tasks[0].IsCompleted.Should().BeFalse();

        var inDb = await db.ProjectJobs.FindAsync(dto.Id);
        inDb.Should().NotBeNull();
        inDb!.Status.Should().Be(ProjectJobStatus.Active);
    }

    [Fact]
    public async Task TaskManagement_CanAddToggleAndDeleteTasks()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "app", Name = "App" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "app",
            Title = "Job 1",
            TasksJson = "[]"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        // 1. Add Task
        var addRes = await controller.AddJobTask(job.Id, new CreateJobTaskRequest("New feature"));
        var added = (addRes.Should().BeOfType<OkObjectResult>().Subject.Value as JobTaskDto)!;
        added.Title.Should().Be("New feature");
        added.IsCompleted.Should().BeFalse();

        // 2. Toggle Task
        var updateRes = await controller.UpdateJobTask(job.Id, added.Id, new UpdateJobTaskRequest(IsCompleted: true));
        var updated = (updateRes.Should().BeOfType<OkObjectResult>().Subject.Value as JobTaskDto)!;
        updated.IsCompleted.Should().BeTrue();
        updated.CompletedAt.Should().NotBeNull();

        // 3. Delete Task
        var delRes = await controller.DeleteJobTask(job.Id, added.Id);
        delRes.Should().BeOfType<OkObjectResult>();

        var tasksRes = await controller.GetJobTasks(job.Id);
        var tasksList = (tasksRes.Should().BeOfType<OkObjectResult>().Subject.Value as List<JobTaskDto>)!;
        tasksList.Should().BeEmpty();
    }

    [Fact]
    public async Task PostJobMessage_WithPersonaMention_InitiatesTurnSession()
    {
        using var db = TestDbContextFactory.Create();

        var driver = new Agent { Id = "agy", Name = "Antigravity", RuntimeType = RuntimeType.CliAgy, TrustTier = TrustTier.SeniorManager };
        var persona = new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "agy", PersonaPrompt = "You are Astra." };
        var project = new Project
        {
            Id = "proj-chat",
            Name = "Chat Project",
            WorkspaceType = WorkspaceType.Git,
            ProjectInstructions = "Tech Lead instructions"
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-chat",
            Title = "Refactor Database",
            GitBranchName = "mesh/job-refactor"
        };
        db.Agents.Add(driver);
        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "proj-chat", PersonaId = "astra" });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var msgReq = new PostJobMessageRequest(
            Content: "@astra please refactor the database schema"
        );

        var result = await controller.PostJobMessage(job.Id, msgReq);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var msgDto = ok.Value.Should().BeOfType<JobMessageDto>().Subject;

        msgDto.Content.Should().Be("@astra please refactor the database schema");
        msgDto.Mentions.Should().Contain("astra");
        msgDto.TurnSessionId.Should().NotBeNull();

        // Verify that the TurnSession was created
        var turnSession = await db.AgentSessions.FindAsync(msgDto.TurnSessionId);
        turnSession.Should().NotBeNull();
        turnSession!.PersonaId.Should().Be("astra");
        turnSession.MetadataJson.Should().Contain("proj-chat");
        turnSession.MetadataJson.Should().Contain("mesh/job-refactor");

        // Verify initial prompt message created in that session
        var promptMsg = await db.SessionMessages.FirstOrDefaultAsync(m => m.SessionId == turnSession.Id && m.EventType == "prompt");
        promptMsg.Should().NotBeNull();
        promptMsg!.Content.Should().Contain("[Project Context: Project \"Chat Project\"");
        promptMsg.Content.Should().Contain("please refactor the database schema");
    }

    [Fact]
    public async Task CompleteJob_SetsStatusToCompleted()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "p-comp", Name = "Comp" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "p-comp",
            Title = "Feature X",
            Status = ProjectJobStatus.Active
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var result = await controller.CompleteJob(job.Id, new CompleteJobRequest("Everything is done and tests pass."));
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectJobDto>().Subject;

        dto.Status.Should().Be(ProjectJobStatus.Completed);
        dto.ResultSummary.Should().Be("Everything is done and tests pass.");

        var inDb = await db.ProjectJobs.FindAsync(job.Id);
        inDb!.Status.Should().Be(ProjectJobStatus.Completed);
        inDb.ResultSummary.Should().Be("Everything is done and tests pass.");
    }
}
