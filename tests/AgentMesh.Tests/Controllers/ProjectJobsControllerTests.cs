using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
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
        inDb!.Status.Should().Be(ProjectJobStatus.Pending);
        dto.Status.Should().Be(ProjectJobStatus.Pending);
    }

    [Fact]
    public async Task CreateJob_WithAssignedAgentId_SavesAndExposesAssignedAgentId()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project
        {
            Id = "core-app",
            Name = "Core App",
            WorkspaceType = WorkspaceType.Git,
            DefaultLeadPersonaId = "lead-persona"
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        // 1. When explicit AssignedAgentId is supplied
        var req1 = new CreateProjectJobRequest(
            Title: "Build Auth Provider",
            AssignedAgentId: "astra"
        );
        var result1 = await controller.CreateJob("core-app", req1);
        var dto1 = (result1.Should().BeOfType<CreatedAtActionResult>().Subject.Value as ProjectJobDto)!;
        dto1.AssignedAgentId.Should().Be("astra");

        var inDb1 = await db.ProjectJobs.FindAsync(dto1.Id);
        inDb1!.AssignedAgentId.Should().Be("astra");

        // 2. When AssignedAgentId is omitted, falls back to DefaultLeadPersonaId
        var req2 = new CreateProjectJobRequest(Title: "Fix UI Glitch");
        var result2 = await controller.CreateJob("core-app", req2);
        var dto2 = (result2.Should().BeOfType<CreatedAtActionResult>().Subject.Value as ProjectJobDto)!;
        dto2.AssignedAgentId.Should().Be("lead-persona");

        var inDb2 = await db.ProjectJobs.FindAsync(dto2.Id);
        inDb2!.AssignedAgentId.Should().Be("lead-persona");
    }

    [Fact]
    public async Task UpdateJob_ChangingAssignedAgentId_UpdatesJob()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "app", Name = "App" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "app",
            Title = "Job 1",
            AssignedAgentId = "lead-persona",
            TasksJson = "[]"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        var updateRes = await controller.UpdateJob(job.Id, new UpdateProjectJobRequest(
            AssignedAgentId: "astra"
        ));

        var updated = (updateRes.Should().BeOfType<OkObjectResult>().Subject.Value as ProjectJobDto)!;
        updated.AssignedAgentId.Should().Be("astra");

        var inDb = await db.ProjectJobs.FindAsync(job.Id);
        inDb!.AssignedAgentId.Should().Be("astra");

        // Reassign to null / empty string
        var clearRes = await controller.UpdateJob(job.Id, new UpdateProjectJobRequest(
            AssignedAgentId: ""
        ));
        var cleared = (clearRes.Should().BeOfType<OkObjectResult>().Subject.Value as ProjectJobDto)!;
        cleared.AssignedAgentId.Should().BeNull();
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
        var addRes = await controller.AddJobTask(job.Id, new CreateJobTaskRequest("New feature", RequiresDiscussion: true));
        var added = (addRes.Should().BeOfType<OkObjectResult>().Subject.Value as JobTaskDto)!;
        added.Title.Should().Be("New feature");
        added.IsCompleted.Should().BeFalse();
        added.RequiresDiscussion.Should().BeTrue();

        // 2. Toggle Task
        var updateRes = await controller.UpdateJobTask(job.Id, added.Id, new UpdateJobTaskRequest(IsCompleted: true, RequiresDiscussion: false));
        var updated = (updateRes.Should().BeOfType<OkObjectResult>().Subject.Value as JobTaskDto)!;
        updated.IsCompleted.Should().BeTrue();
        updated.CompletedAt.Should().NotBeNull();
        updated.RequiresDiscussion.Should().BeFalse();

        // 3. Delete Task
        var delRes = await controller.DeleteJobTask(job.Id, added.Id);
        delRes.Should().BeOfType<OkObjectResult>();

        var tasksRes = await controller.GetJobTasks(job.Id);
        var tasksList = (tasksRes.Should().BeOfType<OkObjectResult>().Subject.Value as List<JobTaskDto>)!;
        tasksList.Should().BeEmpty();
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

        dto.Status.Should().Be(ProjectJobStatus.Archived);
        dto.ResultSummary.Should().Be("Everything is done and tests pass.");

        var inDb = await db.ProjectJobs.FindAsync(job.Id);
        inDb!.Status.Should().Be(ProjectJobStatus.Archived);
        inDb.ResultSummary.Should().Be("Everything is done and tests pass.");
    }

    [Fact]
    public async Task UploadJobAttachments_SavesFileAndReturnsDto()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "test-proj", Name = "Test Project" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "test-proj",
            Title = "Attachment Job"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockStorage = new Mock<IStorageService>();
        mockStorage
            .Setup(s => s.UploadStreamAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, Stream _, string _, CancellationToken _) => $"s3://agent-mesh-artifacts/{key}");

        var controller = new ProjectJobsController(db, storageService: mockStorage.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var pngStream = new MemoryStream(pngBytes);
        var formFile = new FormFile(pngStream, 0, pngBytes.Length, "files", "spec-diagram.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
        var formFiles = new FormFileCollection { formFile };

        var uploadResult = await controller.UploadJobAttachments(job.Id, formFiles);
        var ok = uploadResult.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<List<JobAttachmentDto>>().Subject;

        list.Should().HaveCount(1);
        var att = list[0];
        att.JobId.Should().Be(job.Id);
        att.FileName.Should().Be("spec-diagram.png");
        att.ContentType.Should().Be("image/png");
        att.SizeBytes.Should().Be(pngBytes.Length);
        att.DownloadUrl.Should().Be($"/api/project-jobs/{job.Id}/attachments/{att.Id}/content");

        // Verify storage upload was attempted
        mockStorage.Verify(
            s => s.UploadStreamAsync(
                It.Is<string>(k => k.EndsWith("spec-diagram.png")),
                It.IsAny<Stream>(),
                "image/png",
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify GetAttachmentContent can stream it back
        var contentResult = await controller.GetAttachmentContent(job.Id, att.Id, download: false);
        var fileStreamResult = contentResult.Should().BeOfType<FileStreamResult>().Subject;
        fileStreamResult.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task UploadJobAttachments_DisallowedExtension_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "test-proj", Name = "Test Project" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "test-proj",
            Title = "Attachment Job"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var exeBytes = new byte[] { 0x4D, 0x5A };
        var exeStream = new MemoryStream(exeBytes);
        var formFile = new FormFile(exeStream, 0, exeBytes.Length, "files", "malicious.exe")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/x-msdownload"
        };
        var formFiles = new FormFileCollection { formFile };

        var uploadResult = await controller.UploadJobAttachments(job.Id, formFiles);
        uploadResult.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ArchiveJob_And_ReopenJob_UpdatesStatusAndBroadcastsSignalR()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "archive-test", Name = "Archive Project" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "archive-test",
            Title = "Job To Archive",
            Status = ProjectJobStatus.Active
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new ProjectJobsController(db, mockHubContext.Object);

        // 1. Archive
        var archiveRes = await controller.ArchiveJob(job.Id);
        var okArchive = archiveRes.Should().BeOfType<OkObjectResult>().Subject;
        var dtoArchive = okArchive.Value.Should().BeOfType<ProjectJobDto>().Subject;
        dtoArchive.Status.Should().Be(ProjectJobStatus.Archived);

        var inDbArchived = await db.ProjectJobs.FindAsync(job.Id);
        inDbArchived!.Status.Should().Be(ProjectJobStatus.Archived);

        mockClientProxy.Verify(
            c => c.SendCoreAsync("JobUpdated", It.IsAny<object?[]>(), default),
            Times.Once);

        // 2. Reopen
        var reopenRes = await controller.ReopenJob(job.Id);
        var okReopen = reopenRes.Should().BeOfType<OkObjectResult>().Subject;
        var dtoReopen = okReopen.Value.Should().BeOfType<ProjectJobDto>().Subject;
        dtoReopen.Status.Should().Be(ProjectJobStatus.Active);

        var inDbReopened = await db.ProjectJobs.FindAsync(job.Id);
        inDbReopened!.Status.Should().Be(ProjectJobStatus.Active);
    }

    [Fact]
    public async Task CheckAndDispatchQueuedJobTurns_DispatchesHigherPriorityJobBeforeLowerPriority()
    {
        using var db = TestDbContextFactory.Create();

        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            Role = "Tech Lead",
            DriverId = "agy",
            MaxConcurrentJobs = 1
        };
        var project = new Project
        {
            Id = "prio-proj",
            Name = "Priority Project"
        };

        var jobLow = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "prio-proj",
            Title = "Low Priority Job",
            Status = ProjectJobStatus.Active,
            Priority = ProjectJobPriority.Low
        };
        var jobUrgent = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "prio-proj",
            Title = "Urgent Priority Job",
            Status = ProjectJobStatus.Active,
            Priority = ProjectJobPriority.Urgent
        };

        // Queue Low priority job turn first
        var turnLow = new QueuedJobTurnDto(
            Id: Guid.NewGuid(),
            JobId: jobLow.Id,
            ProjectId: "prio-proj",
            TargetPersonaId: "astra",
            SenderType: "user",
            SenderId: "user",
            SenderName: "Operator",
            Content: "Low priority work",
            Mentions: new List<string> { "astra" },
            Attachments: null,
            Priority: ProjectJobPriority.Low,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10)
        );
        jobLow.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { turnLow });

        // Queue Urgent priority job turn later
        var turnUrgent = new QueuedJobTurnDto(
            Id: Guid.NewGuid(),
            JobId: jobUrgent.Id,
            ProjectId: "prio-proj",
            TargetPersonaId: "astra",
            SenderType: "user",
            SenderId: "user",
            SenderName: "Operator",
            Content: "Urgent priority work",
            Mentions: new List<string> { "astra" },
            Attachments: null,
            Priority: ProjectJobPriority.Urgent,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5)
        );
        jobUrgent.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { turnUrgent });

        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.AddRange(jobLow, jobUrgent);
        await db.SaveChangesAsync();

        var mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        // Since MaxConcurrentJobs is 1 and active jobs = 0, dispatching should pick Urgent first!
        await ProjectJobsController.CheckAndDispatchQueuedJobTurnsAsync(db, mockHubContext.Object, "astra");

        // Verify Urgent job turn was dequeued
        var updatedUrgent = await db.ProjectJobs.FindAsync(jobUrgent.Id);
        ProjectJobsController.ParseQueuedTurns(updatedUrgent!.QueuedTurnsJson).Should().BeEmpty();

        // Low priority job turn should still be queued
        var updatedLow = await db.ProjectJobs.FindAsync(jobLow.Id);
        ProjectJobsController.ParseQueuedTurns(updatedLow!.QueuedTurnsJson).Should().HaveCount(1);

        // Only 1 session was created (for Urgent job)
        var createdSessions = await db.AgentSessions.Where(s => s.PersonaId == "astra").ToListAsync();
        createdSessions.Should().HaveCount(1);
        createdSessions[0].Title.Should().Contain("Urgent Priority Job");
    }

    [Fact]
    public async Task JobTasks_SupportAttachments_AndUploadDeleteCycle()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-task-att", Name = "Task Att Project" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-task-att",
            Title = "Task Attachments Job"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        var initialAtt = new JobAttachmentDto(
            Id: "att-init-1",
            JobId: job.Id,
            FileName: "wireframe.png",
            ContentType: "image/png",
            SizeBytes: 1024,
            S3Key: $"jobs/{job.Id}/attachments/att-init-1/wireframe.png",
            DownloadUrl: $"/api/project-jobs/{job.Id}/attachments/att-init-1/content",
            CreatedAt: DateTimeOffset.UtcNow
        );

        // 1. Add Task with Attachment
        var addRes = await controller.AddJobTask(job.Id, new CreateJobTaskRequest(
            Title: "Build wireframe UI",
            Attachments: new List<JobAttachmentDto> { initialAtt }
        ));
        var added = (addRes.Should().BeOfType<OkObjectResult>().Subject.Value as JobTaskDto)!;
        added.Attachments.Should().NotBeNull();
        added.Attachments.Should().HaveCount(1);
        added.Attachments![0].FileName.Should().Be("wireframe.png");

        // 2. Upload Task Attachment via dedicated endpoint
        var txtBytes = System.Text.Encoding.UTF8.GetBytes("task specification notes");
        var txtStream = new MemoryStream(txtBytes);
        var formFile = new FormFile(txtStream, 0, txtBytes.Length, "files", "notes.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };
        var uploadRes = await controller.UploadTaskAttachments(job.Id, added.Id, new FormFileCollection { formFile });
        var uploadedList = (uploadRes.Should().BeOfType<OkObjectResult>().Subject.Value as List<JobAttachmentDto>)!;
        uploadedList.Should().HaveCount(1);
        uploadedList[0].FileName.Should().Be("notes.txt");

        // Verify task in DB has both attachments
        var tasksRes = await controller.GetJobTasks(job.Id);
        var tasksList = (tasksRes.Should().BeOfType<OkObjectResult>().Subject.Value as List<JobTaskDto>)!;
        var taskInDb = tasksList.First(t => t.Id == added.Id);
        taskInDb.Attachments.Should().HaveCount(2);

        // 3. Delete Task Attachment
        var delAttRes = await controller.DeleteTaskAttachment(job.Id, added.Id, initialAtt.Id);
        delAttRes.Should().BeOfType<OkObjectResult>();

        var updatedTasks = ( (await controller.GetJobTasks(job.Id)).Should().BeOfType<OkObjectResult>().Subject.Value as List<JobTaskDto> )!;
        var taskAfterDel = updatedTasks.First(t => t.Id == added.Id);
        taskAfterDel.Attachments.Should().HaveCount(1);
        taskAfterDel.Attachments![0].FileName.Should().Be("notes.txt");
    }

    [Fact]
    public async Task GetProjectJobs_WithIncludeArchivedFalse_ExcludesArchivedJobs()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-arch", Name = "Archive Filter Project" };
        var activeJob = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-arch",
            Title = "Active Job",
            Status = ProjectJobStatus.Active
        };
        var archivedJob = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-arch",
            Title = "Archived Job",
            Status = ProjectJobStatus.Archived
        };
        db.Projects.Add(project);
        db.ProjectJobs.AddRange(activeJob, archivedJob);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        // Default (or includeArchived: true) returns all
        var allJobsRes = await controller.GetProjectJobs("proj-arch", includeArchived: true);
        var allJobs = (allJobsRes.Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectJobDto>)!;
        allJobs.Should().HaveCount(2);

        // includeArchived: false excludes archived
        var activeOnlyRes = await controller.GetProjectJobs("proj-arch", includeArchived: false);
        var activeOnly = (activeOnlyRes.Should().BeOfType<OkObjectResult>().Subject.Value as List<ProjectJobDto>)!;
        activeOnly.Should().HaveCount(1);
        activeOnly[0].Id.Should().Be(activeJob.Id);
    }

    [Fact]
    public async Task CancelQueuedTurn_RemovesTurn_CreatesSystemMessage_AndBroadcasts()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-queued", Name = "Queued Turn Project" };
        var persona = new Persona
        {
            Id = "mock-driver-engineer",
            Name = "Rook",
            Role = "Software Engineer",
            DriverId = "mock-driver",
            PersonaPrompt = "Code"
        };

        var turnId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var queuedTurn = new QueuedJobTurnDto(
            Id: turnId,
            JobId: jobId,
            ProjectId: project.Id,
            TargetPersonaId: persona.Id,
            SenderType: "user",
            SenderId: null,
            SenderName: null,
            Content: "Refactor timeout logic",
            Mentions: new List<string> { persona.Id },
            Attachments: null,
            Priority: ProjectJobPriority.Normal,
            CreatedAt: DateTimeOffset.UtcNow
        );

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Bug Hunt",
            GitBranchName = "mesh/bug-hunt",
            Priority = ProjectJobPriority.Normal,
            QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { queuedTurn })
        };

        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new ProjectJobsController(db, mockHubContext.Object);

        var res = await controller.CancelQueuedTurn(job.Id, turnId);
        res.Should().BeOfType<OkObjectResult>();

        // Verify turn was removed from job
        var updatedJob = await db.ProjectJobs.FindAsync(job.Id);
        var turns = ProjectJobsController.ParseQueuedTurns(updatedJob!.QueuedTurnsJson);
        turns.Should().BeEmpty();

        // Verify timeline cancellation card was posted
        var cards = await db.JobTimelineCards.Where(c => c.JobId == job.Id.ToString()).ToListAsync();
        cards.Should().HaveCount(1);
        cards[0].StructuredPayloadJson.Should().Contain("Queued turn for @Rook was cancelled.");

        // Verify hub broadcasts were made
        mockClientProxy.Verify(
            p => p.SendCoreAsync("ReceiveTimelineCardUpdated", It.IsAny<object[]>(), default),
            Times.Once);
        mockClientProxy.Verify(
            p => p.SendCoreAsync("JobUpdated", It.IsAny<object[]>(), default),
            Times.Once);
    }

    [Fact]
    public async Task CancelQueuedTurn_ReturnsNotFound_WhenTurnDoesNotExist()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-queued-404", Name = "Queued Project" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Bug Hunt",
            GitBranchName = "mesh/bug-hunt",
            QueuedTurnsJson = "[]"
        };

        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.CancelQueuedTurn(job.Id, Guid.NewGuid());
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task CancelQueuedTurns_CancelsAllForPersona_WithSingleSystemMessage_AndReturnsJobDto()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-bulk", Name = "Bulk Project" };
        var jobId = Guid.NewGuid();

        QueuedJobTurnDto Mk(string personaId, string content) => new(
            Id: Guid.NewGuid(),
            JobId: jobId,
            ProjectId: project.Id,
            TargetPersonaId: personaId,
            SenderType: "user",
            SenderId: null,
            SenderName: null,
            Content: content,
            Mentions: new List<string> { personaId },
            Attachments: null,
            Priority: ProjectJobPriority.Normal,
            CreatedAt: DateTimeOffset.UtcNow
        );

        var rookTurns = new List<QueuedJobTurnDto> { Mk("rook", "one"), Mk("rook", "two"), Mk("rook", "three") };
        var others = new List<QueuedJobTurnDto> { Mk("soren", "keep me") };
        var all = rookTurns.Concat(others).ToList();

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Bug Hunt",
            GitBranchName = "mesh/bug-hunt",
            QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(all)
        };

        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", Role = "Engineer", DriverId = "mock-driver", PersonaPrompt = "Code" });
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        var res = await controller.CancelQueuedTurns(job.Id, "rook");
        var ok = res.Should().BeOfType<OkObjectResult>().Subject;

        var cards = await db.JobTimelineCards.Where(c => c.JobId == job.Id.ToString()).ToListAsync();
        cards.Should().HaveCount(1);
        cards[0].StructuredPayloadJson.Should().Contain("3 queued turn(s)");
        cards[0].StructuredPayloadJson.Should().Contain("@Rook");

        var remaining = ProjectJobsController.ParseQueuedTurns(job.QueuedTurnsJson);
        remaining.Should().HaveCount(1);
        remaining[0].Content.Should().Be("keep me");

        // The dashboard relies on this DTO to sync authoritative queue state.
        ok.Value.Should().NotBeNull();
        var props = ok.Value!.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(ok.Value));
        props.Should().ContainKey("job");
        props["job"].Should().BeOfType<ProjectJobDto>();
        props["count"].Should().Be(3);
        ((ProjectJobDto)props["job"]!).QueuedTurns.Should().HaveCount(1);
    }

    [Fact]
    public async Task CancelQueuedTurns_ReturnsZeroCount_WhenNothingQueued()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-empty-queue", Name = "Empty Queue Project" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Bug Hunt",
            GitBranchName = "mesh/bug-hunt",
            QueuedTurnsJson = "[]"
        };

        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.CancelQueuedTurns(job.Id);
        res.Should().BeOfType<OkObjectResult>();

        (await db.JobTimelineCards.CountAsync(c => c.JobId == job.Id.ToString())).Should().Be(0);
    }

    [Fact]
    public async Task CancelQueuedTurn_ReturnsNotFound_WhenJobDoesNotExist()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectJobsController(db);
        var res = await controller.CancelQueuedTurn(Guid.NewGuid(), Guid.NewGuid());
        res.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task CancelQueuedTurns_FiltersByPersona_WhenPersonaIdSpecified()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-multi-queued", Name = "Multi Queued Project" };
        var jobId = Guid.NewGuid();

        var turnRook = new QueuedJobTurnDto(
            Id: Guid.NewGuid(),
            JobId: jobId,
            ProjectId: project.Id,
            TargetPersonaId: "rook",
            SenderType: "user",
            SenderId: null,
            SenderName: null,
            Content: "Task for rook",
            Mentions: new List<string> { "rook" },
            Attachments: null,
            Priority: ProjectJobPriority.Normal,
            CreatedAt: DateTimeOffset.UtcNow
        );

        var turnSoren = new QueuedJobTurnDto(
            Id: Guid.NewGuid(),
            JobId: jobId,
            ProjectId: project.Id,
            TargetPersonaId: "soren",
            SenderType: "user",
            SenderId: null,
            SenderName: null,
            Content: "Task for soren",
            Mentions: new List<string> { "soren" },
            Attachments: null,
            Priority: ProjectJobPriority.Normal,
            CreatedAt: DateTimeOffset.UtcNow
        );

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Bug Hunt",
            GitBranchName = "mesh/bug-hunt",
            QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { turnRook, turnSoren })
        };

        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        // Cancel only rook's queued turn
        var res = await controller.CancelQueuedTurns(job.Id, "rook");
        res.Should().BeOfType<OkObjectResult>();

        var updatedJob = await db.ProjectJobs.FindAsync(job.Id);
        var remainingTurns = ProjectJobsController.ParseQueuedTurns(updatedJob!.QueuedTurnsJson);
        remainingTurns.Should().HaveCount(1);
        remainingTurns[0].TargetPersonaId.Should().Be("soren");
    }

    // ---------------------------------------------------------------------
    // Task assignment / reassignment
    // ---------------------------------------------------------------------

    private static ProjectJob MakeAssignmentJob(
        string projectId,
        string title,
        List<JobTaskDto> tasks,
        List<QueuedJobTurnDto>? queuedTurns = null,
        ProjectJobPriority priority = ProjectJobPriority.Normal)
    {
        return new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            Status = ProjectJobStatus.Active,
            Priority = priority,
            TasksJson = System.Text.Json.JsonSerializer.Serialize(tasks),
            QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(queuedTurns ?? new List<QueuedJobTurnDto>())
        };
    }

    private static QueuedJobTurnDto MakeQueuedTurn(Guid jobId, string projectId, string personaId, string content) =>
        new(
            Id: Guid.NewGuid(),
            JobId: jobId,
            ProjectId: projectId,
            TargetPersonaId: personaId,
            SenderType: "user",
            SenderId: "operator",
            SenderName: "Operator",
            Content: content,
            Mentions: new List<string> { personaId },
            Attachments: null,
            Priority: ProjectJobPriority.Normal,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

    private static (AgentMeshDbContext Db, Project Project, Persona Astra, Persona Soren) BuildAssignmentContext(
        string projectId,
        int? astraMaxConcurrentJobs = null,
        int? sorenMaxConcurrentJobs = null)
    {
        var db = TestDbContextFactory.Create();
        var astra = new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "agy", MaxConcurrentJobs = astraMaxConcurrentJobs };
        var soren = new Persona { Id = "soren", Name = "Soren", Role = "Engineer", DriverId = "agy", MaxConcurrentJobs = sorenMaxConcurrentJobs };
        var project = new Project { Id = projectId, Name = "Assignment Project", WorkspaceType = WorkspaceType.Git };
        db.Agents.Add(new Agent { Id = "agy", Name = "Antigravity", RuntimeType = RuntimeType.CliAgy, TrustTier = TrustTier.SeniorManager });
        db.Personas.AddRange(astra, soren);
        db.Projects.Add(project);
        db.ProjectPersonas.AddRange(
            new ProjectPersona { ProjectId = projectId, PersonaId = "astra" },
            new ProjectPersona { ProjectId = projectId, PersonaId = "soren" });
        return (db, project, astra, soren);
    }

    [Fact]
    public async Task AssignJobTask_SetsAssigneeAndReturnsServerAuthoritativeJob()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-basic");
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Wire up assignee picker" };
        var job = MakeAssignmentJob(project.Id, "Assign Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, task.Id, new AssignJobTaskRequest("astra"));

        var ok = res.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<AssignJobTaskResult>().Subject;
        payload.Task.AssignedPersonaId.Should().Be("astra");
        payload.ToPersonaName.Should().Be("Astra");
        payload.FromPersonaId.Should().BeNull();
        payload.Job.Should().NotBeNull();
        payload.Job.Tasks.Single().AssignedPersonaId.Should().Be("astra");

        var persisted = ProjectJobsController.ParseTasks((await db.ProjectJobs.FindAsync(job.Id))!.TasksJson);
        persisted.Single().AssignedPersonaId.Should().Be("astra");
    }

    [Fact]
    public async Task AssignJobTask_NormalizesCaseAndAcceptsEmptyStringToUnassign()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-unassign");
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Unassign me", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Unassign Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        var upper = await controller.AssignJobTask(job.Id, task.Id, new AssignJobTaskRequest("  ASTRA "));
        ((AssignJobTaskResult)upper.Should().BeOfType<OkObjectResult>().Subject.Value!).ToPersonaId.Should().Be("astra");

        var cleared = await controller.AssignJobTask(job.Id, task.Id, new AssignJobTaskRequest(""));
        var clearedResult = (AssignJobTaskResult)cleared.Should().BeOfType<OkObjectResult>().Subject.Value!;
        clearedResult.ToPersonaId.Should().BeNull();
        clearedResult.ToPersonaName.Should().BeNull();

        var persisted = ProjectJobsController.ParseTasks((await db.ProjectJobs.FindAsync(job.Id))!.TasksJson);
        persisted.Single().AssignedPersonaId.Should().BeNull();
    }

    [Fact]
    public async Task AssignJobTask_UnknownPersona_ReturnsBadRequestAndLeavesTaskUntouched()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-unknown");
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Ghost assignee" };
        var job = MakeAssignmentJob(project.Id, "Unknown Assignee Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, task.Id, new AssignJobTaskRequest("nobody"));

        res.Should().BeOfType<BadRequestObjectResult>();

        var persisted = ProjectJobsController.ParseTasks((await db.ProjectJobs.FindAsync(job.Id))!.TasksJson);
        persisted.Single().AssignedPersonaId.Should().BeNull();
        (await db.JobTimelineCards.CountAsync(c => c.JobId == job.Id.ToString())).Should().Be(0);
    }

    [Fact]
    public async Task AssignJobTask_PersonaNotOnProjectTeam_ReturnsBadRequest()
    {
        // Routing only considers team members, so an outsider assignee would be unreachable.
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-noteam");
        db.Personas.Add(new Persona { Id = "stranger", Name = "Stranger", Role = "Guest", DriverId = "agy" });
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Unreachable assignee" };
        var job = MakeAssignmentJob(project.Id, "No Team Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, task.Id, new AssignJobTaskRequest("stranger"));

        res.Should().BeOfType<BadRequestObjectResult>();
        ProjectJobsController.ParseTasks((await db.ProjectJobs.FindAsync(job.Id))!.TasksJson)
            .Single().AssignedPersonaId.Should().BeNull();
    }

    [Fact]
    public async Task AssignJobTask_WhenProjectHasNoTeamDefined_AllowsAnyExistingPersona()
    {
        // Mirrors the dashboard, which falls back to listing every persona when a project has
        // no team yet; refusing to assign there would lock the operator out of a fresh project.
        var db = TestDbContextFactory.Create();
        db.Personas.Add(new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "agy" });
        var project = new Project { Id = "proj-noteam", Name = "No Team Project", WorkspaceType = WorkspaceType.Git };
        db.Projects.Add(project);
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Bootstrap task" };
        var job = MakeAssignmentJob(project.Id, "Bootstrap Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, task.Id, new AssignJobTaskRequest("astra"));

        ((AssignJobTaskResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!).ToPersonaId.Should().Be("astra");
    }

    [Fact]
    public async Task AssignJobTask_UnknownTask_ReturnsNotFound()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-badtask");
        var job = MakeAssignmentJob(project.Id, "No Task Job", new List<JobTaskDto>
        {
            new() { Id = Guid.NewGuid(), Title = "Real task" }
        });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, Guid.NewGuid(), new AssignJobTaskRequest("astra"));

        res.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task AssignJobTask_ReassigningFocusTaskHandsQueuedTurnsToNewAssigneeAndAudits()
    {
        // Astra is at its concurrency limit, so the operator's follow-up is parked in the
        // job queue. Reassigning the task must move that parked work, otherwise the turn
        // runs for the persona that no longer owns it.
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-move", astraMaxConcurrentJobs: 1);
        var focusTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Fix auto-scroll", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Reassign Job", new List<JobTaskDto> { focusTask });
        var queuedTurn = MakeQueuedTurn(job.Id, project.Id, "astra", "please fix the scroll behaviour");
        job.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { queuedTurn });

        var otherBusyJob = MakeAssignmentJob(project.Id, "Astra Busy Job", new List<JobTaskDto>());
        db.ProjectJobs.AddRange(job, otherBusyJob);
        db.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            PersonaId = "astra",
            DriverId = "agy",
            Status = "thinking",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { jobId = otherBusyJob.Id.ToString() })
        });
        await db.SaveChangesAsync();

        var mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new ProjectJobsController(db, mockHubContext.Object);
        var res = await controller.AssignJobTask(job.Id, focusTask.Id, new AssignJobTaskRequest("soren"));
        var result = (AssignJobTaskResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!;

        result.ToPersonaId.Should().Be("soren");
        result.FromPersonaId.Should().Be("astra");
        result.IsTargetTask.Should().BeTrue();
        result.MovedQueuedTurnCount.Should().Be(1);
        result.FromPersonaName.Should().Be("Astra");
        result.ToPersonaName.Should().Be("Soren");
        result.DispatchedToNewAssignee.Should().BeTrue();
        // The DTO handed back must match what was persisted, so open cockpits converge.
        result.Job.QueuedTurns.Should().BeEmpty();
        result.Job.Tasks.Single().AssignedPersonaId.Should().Be("soren");

        var jobCards = await db.JobTimelineCards.Where(c => c.JobId == job.Id.ToString()).ToListAsync();
        var audit = jobCards.Single(c => c.CardType == TimelineCardType.InternalConsultation);
        audit.StructuredPayloadJson.Should().Contain("Fix auto-scroll");
        audit.StructuredPayloadJson.Should().Contain("@Astra");
        audit.StructuredPayloadJson.Should().Contain("@Soren");
        audit.StructuredPayloadJson.Should().Contain("1 queued turn");

        var persistedTurns = ProjectJobsController.ParseQueuedTurns((await db.ProjectJobs.FindAsync(job.Id))!.QueuedTurnsJson);
        persistedTurns.Should().BeEmpty();

        var sorenSession = await db.AgentSessions.SingleAsync(s => s.PersonaId == "soren");
        sorenSession.Status.Should().Be("thinking");
        var sorenPrompt = await db.SessionMessages.SingleAsync(m => m.SessionId == sorenSession.Id && m.EventType == "prompt");
        sorenPrompt.Content.Should().Contain("please fix the scroll behaviour");
        sorenPrompt.Content.Should().Contain("[Current Target Task]: \"Fix auto-scroll\"");

        var sorenAck = jobCards.Single(c => c.CardType == TimelineCardType.TaskStep);
        System.Text.Json.JsonDocument.Parse(sorenAck.StructuredPayloadJson)
            .RootElement.GetProperty("content").GetString()
            .Should().Be("I'm on it — starting work on \"Fix auto-scroll\" now.");

        // Astra must not be offered the work it just lost.
        await ProjectJobsController.CheckAndDispatchQueuedJobTurnsAsync(db, mockHubContext.Object, "astra");
        await db.Entry(sorenSession).ReloadAsync();
        sorenSession.Status.Should().Be("thinking");
        (await db.AgentSessions.CountAsync(s => s.PersonaId == "astra" && s.Status == "thinking")).Should().Be(1);
    }

    [Fact]
    public async Task AssignJobTask_WhenNewAssigneeIsAlsoBusy_HandedOverTurnStaysQueuedForThem()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-bothbusy", astraMaxConcurrentJobs: 1, sorenMaxConcurrentJobs: 1);
        var focusTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Handover while busy", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Both Busy Job", new List<JobTaskDto> { focusTask });
        var queuedTurn = MakeQueuedTurn(job.Id, project.Id, "astra", "queued work for the focus task");
        job.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { queuedTurn });

        var astraBusyJob = MakeAssignmentJob(project.Id, "Astra Busy Job", new List<JobTaskDto>());
        var sorenBusyJob = MakeAssignmentJob(project.Id, "Soren Busy Job", new List<JobTaskDto>());
        db.ProjectJobs.AddRange(job, astraBusyJob, sorenBusyJob);
        db.AgentSessions.AddRange(
            new AgentSession
            {
                Id = Guid.NewGuid(), PersonaId = "astra", DriverId = "agy", Status = "thinking",
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { jobId = astraBusyJob.Id.ToString() })
            },
            new AgentSession
            {
                Id = Guid.NewGuid(), PersonaId = "soren", DriverId = "agy", Status = "thinking",
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { jobId = sorenBusyJob.Id.ToString() })
            });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, focusTask.Id, new AssignJobTaskRequest("soren"));
        var result = (AssignJobTaskResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!;

        result.MovedQueuedTurnCount.Should().Be(1);
        result.DispatchedToNewAssignee.Should().BeFalse();

        var persistedTurns = ProjectJobsController.ParseQueuedTurns((await db.ProjectJobs.FindAsync(job.Id))!.QueuedTurnsJson);
        persistedTurns.Should().ContainSingle(t => t.TargetPersonaId == "soren");
        (await db.AgentSessions.CountAsync(s => s.PersonaId == "soren")).Should().Be(1);
    }

    [Fact]
    public async Task AssignJobTask_ReportsDispatchedByTurnIdNotByEmptyQueue()
    {
        // Soren also has his own work parked on this job. After the handover the queue is
        // still non-empty, so an "is the queue empty?" check would report a handover that
        // actually succeeded as a failure.
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-dispatchflag", sorenMaxConcurrentJobs: 1);
        var focusTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Dispatch check", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Dispatch Flag Job", new List<JobTaskDto> { focusTask });

        // Same job, so priority ties and the dispatcher drains by CreatedAt: making the
        // handed-over turn the older one means it runs while Soren's own newer message stays
        // parked, leaving a non-empty queue behind a successful handover.
        var astraTurn = MakeQueuedTurn(job.Id, project.Id, "astra", "handed over to soren");
        var sorenTurn = MakeQueuedTurn(job.Id, project.Id, "soren", "soren's own newer message");
        sorenTurn = sorenTurn with { CreatedAt = astraTurn.CreatedAt.AddMinutes(1) };
        job.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { astraTurn, sorenTurn });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, focusTask.Id, new AssignJobTaskRequest("soren"));
        var result = (AssignJobTaskResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!;

        result.MovedQueuedTurnCount.Should().Be(1);
        result.DispatchedToNewAssignee.Should().BeTrue();

        // Soren is single-slot: the handed-over turn ran, his own newer message is still
        // parked, and the queue is therefore NOT empty despite the handover succeeding.
        var remaining = ProjectJobsController.ParseQueuedTurns((await db.ProjectJobs.FindAsync(job.Id))!.QueuedTurnsJson);
        remaining.Should().ContainSingle();
        remaining[0].Id.Should().Be(sorenTurn.Id);
        (await db.AgentSessions.CountAsync(s => s.PersonaId == "soren")).Should().Be(1);
    }

    [Fact]
    public async Task AssignJobTask_AssigningAnUnassignedTaskWithParkedTurnsMovesThem()
    {
        // The "tell an agent to grab it" flow leaves a task unlabelled but someone queued to
        // run it. Redirecting the work must move that parked message, or the grabber still
        // executes it and the operator's new choice is ignored.
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-grab");
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Grabbed task" };
        var job = MakeAssignmentJob(project.Id, "Grab Job", new List<JobTaskDto> { task });
        var queuedTurn = MakeQueuedTurn(job.Id, project.Id, "astra", "you grab the unassigned task");
        job.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { queuedTurn });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, task.Id, new AssignJobTaskRequest("soren"));
        var result = (AssignJobTaskResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!;

        result.FromPersonaId.Should().BeNull();
        result.FromPersonaName.Should().Be("Astra");
        result.IsTargetTask.Should().BeTrue();
        result.MovedQueuedTurnCount.Should().Be(1);
        ProjectJobsController.ParseQueuedTurns((await db.ProjectJobs.FindAsync(job.Id))!.QueuedTurnsJson)
            .Should().BeEmpty();

        var audit = await db.JobTimelineCards.SingleAsync(c => c.CardType == TimelineCardType.InternalConsultation);
        audit.StructuredPayloadJson.Should().Contain("@Astra").And.Contain("@Soren");
    }

    [Fact]
    public async Task AssignJobTask_TakeQueuedTurnsFalse_LeavesPreviousAssigneesQueueInPlace()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-keep");
        var focusTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Keep the queue", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Keep Queue Job", new List<JobTaskDto> { focusTask });
        var queuedTurn = MakeQueuedTurn(job.Id, project.Id, "astra", "queued for astra");
        job.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { queuedTurn });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, focusTask.Id, new AssignJobTaskRequest("soren", TakeQueuedTurns: false));
        var result = (AssignJobTaskResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!;

        result.ToPersonaId.Should().Be("soren");
        result.MovedQueuedTurnCount.Should().Be(0);
        ProjectJobsController.ParseQueuedTurns((await db.ProjectJobs.FindAsync(job.Id))!.QueuedTurnsJson)
            .Should().ContainSingle(t => t.TargetPersonaId == "astra");
        (await db.JobTimelineCards.CountAsync(c => c.JobId == job.Id.ToString())).Should().Be(0);
        (await db.AgentSessions.CountAsync(s => s.PersonaId == "soren")).Should().Be(0);
    }

    [Fact]
    public async Task AssignJobTask_ReassigningANonFocusTaskDoesNotStealTheFocusOwnersQueuedTurns()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-nonfocus");
        var focusTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Focus task", AssignedPersonaId = "astra" };
        var laterTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Later task", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Non Focus Job", new List<JobTaskDto> { focusTask, laterTask });
        var queuedTurn = MakeQueuedTurn(job.Id, project.Id, "astra", "work on the focus task");
        job.QueuedTurnsJson = System.Text.Json.JsonSerializer.Serialize(new List<QueuedJobTurnDto> { queuedTurn });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.AssignJobTask(job.Id, laterTask.Id, new AssignJobTaskRequest("soren"));
        var result = (AssignJobTaskResult)res.Should().BeOfType<OkObjectResult>().Subject.Value!;

        result.IsTargetTask.Should().BeFalse();
        result.MovedQueuedTurnCount.Should().Be(0);
        ProjectJobsController.ParseQueuedTurns((await db.ProjectJobs.FindAsync(job.Id))!.QueuedTurnsJson)
            .Should().ContainSingle(t => t.TargetPersonaId == "astra");
    }

    [Fact]
    public async Task ResetJob_ResetsJobToPending_ClearsHeartbeat_AndStopsDanglingSessions()
    {
        using var db = TestDbContextFactory.Create();

        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-reset",
            Title = "Stuck Job",
            Status = ProjectJobStatus.Active,
            CurrentTaskId = "stage-1-discovery",
            ProgressHeartbeat = "Exploring codebase and generating checklist"
        };
        db.ProjectJobs.Add(job);

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Turn Session",
            Status = "thinking",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { jobId = job.Id.ToString() })
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var result = await controller.ResetJob(job.Id, temporalClient: null);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectJobDto>().Subject;

        dto.Status.Should().Be(ProjectJobStatus.Pending);
        dto.CurrentTaskId.Should().BeNull();
        dto.ProgressHeartbeat.Should().BeNull();

        var updatedJob = await db.ProjectJobs.FindAsync(job.Id);
        updatedJob!.Status.Should().Be(ProjectJobStatus.Pending);
        updatedJob.CurrentTaskId.Should().BeNull();
        updatedJob.ProgressHeartbeat.Should().BeNull();

        var updatedSession = await db.AgentSessions.FindAsync(session.Id);
        updatedSession!.Status.Should().Be("stopped");
    }

    [Fact]
    public async Task StartJob_WhenTemporalUnavailable_DispatchesDirectFallbackTurn()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project
        {
            Id = "proj-start-fallback",
            Name = "Project Start Fallback",
            WorkspaceType = WorkspaceType.Git,
            DefaultLeadPersonaId = "astra"
        };
        db.Projects.Add(project);

        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            DriverId = "agy",
            Role = "Tech Lead"
        };
        db.Personas.Add(persona);

        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Implement Feature X",
            Description = "Feature X details",
            Status = ProjectJobStatus.Pending
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockDispatcher = new Mock<JobTurnDispatcher>(db, null, null, null);
        mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<JobTurnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchedJobTurn(new AgentSession(), new SessionMessage(), new Agent()));

        var controller = new ProjectJobsController(db, turns: mockDispatcher.Object);
        var result = await controller.StartJob(job.Id, temporalClient: null);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectJobDto>().Subject;
        dto.Status.Should().Be(ProjectJobStatus.Active);
        dto.CurrentTaskId.Should().Be("stage-1-discovery");
        dto.AssignedAgentId.Should().Be("astra");

        var updatedJob = await db.ProjectJobs.FindAsync(job.Id);
        updatedJob!.Status.Should().Be(ProjectJobStatus.Active);
        updatedJob.AssignedAgentId.Should().Be("astra");

        mockDispatcher.Verify(d => d.DispatchAsync(
            It.Is<JobTurnRequest>(r => r.Job.Id == job.Id && r.TaskId == "stage-1-discovery" && r.Persona.Id == "astra"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartJob_ResolvesAssignedPersona_WhenAssignedAgentIdIsNull()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project
        {
            Id = "proj-resolve-persona",
            Name = "Project Resolve Persona",
            WorkspaceType = WorkspaceType.Git
        };
        db.Projects.Add(project);

        var persona = new Persona
        {
            Id = "rook",
            Name = "Rook",
            DriverId = "agy",
            Role = "Security Reviewer"
        };
        db.Personas.Add(persona);

        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "Audit Dependencies",
            Status = ProjectJobStatus.Pending,
            AssignedAgentId = null
        };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var mockDispatcher = new Mock<JobTurnDispatcher>(db, null, null, null);
        mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<JobTurnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchedJobTurn(new AgentSession(), new SessionMessage(), new Agent()));

        var controller = new ProjectJobsController(db, turns: mockDispatcher.Object);
        var result = await controller.StartJob(job.Id, temporalClient: null);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectJobDto>().Subject;
        dto.AssignedAgentId.Should().Be("rook");

        var updatedJob = await db.ProjectJobs.FindAsync(job.Id);
        updatedJob!.AssignedAgentId.Should().Be("rook");
    }
}
