using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
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

        // Verify that immediate acknowledgment response from persona was posted
        var ackMsg = await db.JobMessages.FirstOrDefaultAsync(m => m.JobId == job.Id && m.SenderType == "persona" && m.TurnSessionId == turnSession.Id);
        ackMsg.Should().NotBeNull();
        ackMsg!.SenderId.Should().Be("astra");
        ackMsg.SenderName.Should().Be("Astra");
        ackMsg.Content.Should().Be("I'm on it — starting work on this now.");
    }

    [Fact]
    public async Task PostJobMessage_WithPendingTasks_IncludesActiveTaskInAckAndPromptContext()
    {
        using var db = TestDbContextFactory.Create();

        var driver = new Agent { Id = "agy", Name = "Antigravity", RuntimeType = RuntimeType.CliAgy, TrustTier = TrustTier.SeniorManager };
        var persona = new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "agy", PersonaPrompt = "You are Astra." };
        var project = new Project
        {
            Id = "proj-tasks",
            Name = "Task Sequence Project",
            WorkspaceType = WorkspaceType.Git
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-tasks",
            Title = "Sequential Feature Job",
            GitBranchName = "mesh/job-seq",
            TasksJson = System.Text.Json.JsonSerializer.Serialize(new List<JobTaskDto>
            {
                new() { Id = Guid.NewGuid(), Title = "First Step Completed", IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow },
                new() { Id = Guid.NewGuid(), Title = "Second Step Active", IsCompleted = false },
                new() { Id = Guid.NewGuid(), Title = "Third Step Queued", IsCompleted = false }
            })
        };
        db.Agents.Add(driver);
        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "proj-tasks", PersonaId = "astra" });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var msgReq = new PostJobMessageRequest(
            Content: "@astra please work on the tasks"
        );

        var result = await controller.PostJobMessage(job.Id, msgReq);
        result.Should().BeOfType<OkObjectResult>();

        // Verify acknowledgment message includes active task title
        var ackMsg = await db.JobMessages.FirstOrDefaultAsync(m => m.JobId == job.Id && m.SenderType == "persona");
        ackMsg.Should().NotBeNull();
        ackMsg!.Content.Should().Be("I'm on it — starting work on \"Second Step Active\" now.");

        // Verify prompt content includes sequential checklist and active task instruction
        var promptMsg = await db.SessionMessages.FirstOrDefaultAsync(m => m.EventType == "prompt");
        promptMsg.Should().NotBeNull();
        promptMsg!.Content.Should().Contain("[Job Tasks & Sequential Checklist]:");
        promptMsg.Content.Should().Contain("[x] First Step Completed");
        promptMsg.Content.Should().Contain("[ ] (CURRENT ACTIVE FOCUS) Second Step Active");
        promptMsg.Content.Should().Contain("[ ] (Queued) Third Step Queued");
        promptMsg.Content.Should().Contain("[Current Target Task]: \"Second Step Active\"");
        promptMsg.Content.Should().Contain("[Sequential Execution Rule]");
    }

    [Fact]
    public async Task PostJobMessage_WithDiscussionRequiredTask_SetsDiscussionAckAndPromptDirective()
    {
        using var db = TestDbContextFactory.Create();

        var driver = new Agent { Id = "agy", Name = "Antigravity", RuntimeType = RuntimeType.CliAgy, TrustTier = TrustTier.SeniorManager };
        var persona = new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "agy", PersonaPrompt = "You are Astra." };
        var project = new Project
        {
            Id = "proj-discuss",
            Name = "Task Discuss Project",
            WorkspaceType = WorkspaceType.Git
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-discuss",
            Title = "Discussion Feature Job",
            GitBranchName = "mesh/job-discuss",
            TasksJson = System.Text.Json.JsonSerializer.Serialize(new List<JobTaskDto>
            {
                new() { Id = Guid.NewGuid(), Title = "First Step Completed", IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow },
                new() { Id = Guid.NewGuid(), Title = "Second Step Needs Discussion", IsCompleted = false, RequiresDiscussion = true },
                new() { Id = Guid.NewGuid(), Title = "Third Step Queued Discuss", IsCompleted = false, RequiresDiscussion = true }
            })
        };
        db.Agents.Add(driver);
        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "proj-discuss", PersonaId = "astra" });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var msgReq = new PostJobMessageRequest(
            Content: "@astra please work on the tasks"
        );

        var result = await controller.PostJobMessage(job.Id, msgReq);
        result.Should().BeOfType<OkObjectResult>();

        // Verify acknowledgment message includes discussion-specific phrasing
        var ackMsg = await db.JobMessages.FirstOrDefaultAsync(m => m.JobId == job.Id && m.SenderType == "persona");
        ackMsg.Should().NotBeNull();
        ackMsg!.Content.Should().Be("I'm on it — starting discussion on \"Second Step Needs Discussion\" before coding.");

        // Verify prompt content includes discussion tags, rule, and [READY_TO_PROCEED] instruction
        var promptMsg = await db.SessionMessages.FirstOrDefaultAsync(m => m.EventType == "prompt");
        promptMsg.Should().NotBeNull();
        promptMsg!.Content.Should().Contain("[Job Tasks & Sequential Checklist]:");
        promptMsg.Content.Should().Contain("[x] First Step Completed");
        promptMsg.Content.Should().Contain("[ ] (CURRENT ACTIVE FOCUS - DISCUSSION REQUIRED) Second Step Needs Discussion");
        promptMsg.Content.Should().Contain("[ ] (Queued - Discuss First) Third Step Queued Discuss");
        promptMsg.Content.Should().Contain("[Current Target Task]: \"Second Step Needs Discussion\"");
        promptMsg.Content.Should().Contain("[Discussion Required Rule]");
        promptMsg.Content.Should().Contain("[READY_TO_PROCEED]");
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
    public async Task PostJobMessage_WithAttachments_AttachesPayloadAndPropagatesToTurnSession()
    {
        using var db = TestDbContextFactory.Create();

        var driver = new Agent { Id = "agy", Name = "Antigravity", RuntimeType = RuntimeType.CliAgy, TrustTier = TrustTier.SeniorManager };
        var persona = new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "agy", PersonaPrompt = "You are Astra." };
        var project = new Project
        {
            Id = "proj-mesh",
            Name = "Mesh Project",
            WorkspaceType = WorkspaceType.Git,
            ProjectInstructions = "Guidelines"
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-mesh",
            Title = "Attachment Turn Test",
            GitBranchName = "mesh/job-attach"
        };
        db.Agents.Add(driver);
        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "proj-mesh", PersonaId = "astra" });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        var attachment = new JobAttachmentDto(
            Id: "att-12345",
            JobId: job.Id,
            FileName: "architecture.pdf",
            ContentType: "application/pdf",
            SizeBytes: 1024,
            S3Key: $"jobs/{job.Id}/attachments/att-12345/architecture.pdf",
            DownloadUrl: $"/api/project-jobs/{job.Id}/attachments/att-12345/content",
            CreatedAt: DateTimeOffset.UtcNow
        );

        var msgReq = new PostJobMessageRequest(
            Content: "@astra please inspect the design document",
            Attachments: new List<JobAttachmentDto> { attachment }
        );

        var result = await controller.PostJobMessage(job.Id, msgReq);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var msgDto = ok.Value.Should().BeOfType<JobMessageDto>().Subject;

        msgDto.Content.Should().Be("@astra please inspect the design document");
        msgDto.Mentions.Should().Contain("astra");
        msgDto.TurnSessionId.Should().NotBeNull();
        msgDto.Attachments.Should().NotBeNull();
        msgDto.Attachments.Should().HaveCount(1);
        msgDto.Attachments![0].FileName.Should().Be("architecture.pdf");
        msgDto.StructuredPayloadJson.Should().Contain("architecture.pdf");

        // Verify turn session metadata contains attachments
        var turnSession = await db.AgentSessions.FindAsync(msgDto.TurnSessionId);
        turnSession.Should().NotBeNull();
        turnSession!.MetadataJson.Should().Contain("architecture.pdf");

        // Verify session message prompt also has structured payload with attachments
        var promptMsg = await db.SessionMessages.FirstOrDefaultAsync(m => m.SessionId == turnSession.Id && m.EventType == "prompt");
        promptMsg.Should().NotBeNull();
        promptMsg!.StructuredPayloadJson.Should().Contain("architecture.pdf");
    }

    [Fact]
    public async Task PostJobMessage_WithPersonaMention_BroadcastsSessionCreatedSignalREvent()
    {
        using var db = TestDbContextFactory.Create();

        var driver = new Agent { Id = "agy", Name = "Antigravity", RuntimeType = RuntimeType.CliAgy, TrustTier = TrustTier.SeniorManager };
        var persona = new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "agy", PersonaPrompt = "You are Astra." };
        var project = new Project
        {
            Id = "proj-mesh-signalr",
            Name = "SignalR Test Project",
            WorkspaceType = WorkspaceType.Git
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "proj-mesh-signalr",
            Title = "Trajectory Test Job",
            GitBranchName = "mesh/job-traj"
        };
        db.Agents.Add(driver);
        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "proj-mesh-signalr", PersonaId = "astra" });
        await db.SaveChangesAsync();

        var mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new ProjectJobsController(db, mockHubContext.Object);

        var msgReq = new PostJobMessageRequest(
            Content: "@astra please execute the trajectory turn"
        );

        var result = await controller.PostJobMessage(job.Id, msgReq);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var msgDto = ok.Value.Should().BeOfType<JobMessageDto>().Subject;

        // Verify SessionCreated event was broadcast via SignalR
        mockClientProxy.Verify(
            c => c.SendCoreAsync(
                "SessionCreated",
                It.Is<object?[]>(args => args.Length == 1 && args[0] is SessionSummaryDto && ((SessionSummaryDto)args[0]!).Id == msgDto.TurnSessionId),
                default),
            Times.Once);
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
    public async Task PostJobMessage_OnArchivedJob_ReturnsBadRequest()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "archived-chat", Name = "Archived Chat" };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "archived-chat",
            Title = "Archived Job",
            Status = ProjectJobStatus.Archived
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var msgReq = new PostJobMessageRequest(Content: "Hello to archived job");

        var result = await controller.PostJobMessage(job.Id, msgReq);
        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.ToString().Should().Contain("archived");
    }

    [Fact]
    public async Task PostJobMessage_WhenPersonaAtMaxConcurrentJobs_EnqueuesTurnAndPostsConversationalNotice()
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
            Id = "concurrency-proj",
            Name = "Concurrency Project",
            DefaultLeadPersonaId = "astra"
        };
        var job1 = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "concurrency-proj",
            Title = "First Active Job",
            Status = ProjectJobStatus.Active
        };
        var job2 = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "concurrency-proj",
            Title = "Second Job To Queue",
            Status = ProjectJobStatus.Active,
            Priority = ProjectJobPriority.High
        };

        // Active session for Astra on job1
        var activeSessionJob1 = new AgentSession
        {
            Id = Guid.NewGuid(),
            PersonaId = "astra",
            DriverId = "agy",
            Status = "thinking",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { jobId = job1.Id.ToString() })
        };

        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "concurrency-proj", PersonaId = "astra" });
        db.ProjectJobs.AddRange(job1, job2);
        db.AgentSessions.Add(activeSessionJob1);
        await db.SaveChangesAsync();

        var mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new ProjectJobsController(db, mockHubContext.Object);

        // Post message on job2 targeting @astra
        var msgReq = new PostJobMessageRequest(Content: "@astra please work on this second job");
        var res = await controller.PostJobMessage(job2.Id, msgReq);

        var okRes = res.Should().BeOfType<OkObjectResult>().Subject;
        var msgDto = okRes.Value.Should().BeOfType<JobMessageDto>().Subject;
        msgDto.Content.Should().Be("@astra please work on this second job");

        // Verify turn was enqueued in job2
        var updatedJob2 = await db.ProjectJobs.FindAsync(job2.Id);
        var queuedTurns = ProjectJobsController.ParseQueuedTurns(updatedJob2!.QueuedTurnsJson);
        queuedTurns.Should().HaveCount(1);
        queuedTurns[0].TargetPersonaId.Should().Be("astra");

        // Verify conversational reply was added
        var job2Messages = await db.JobMessages.Where(m => m.JobId == job2.Id).OrderBy(m => m.CreatedAt).ToListAsync();
        job2Messages.Should().HaveCount(2);
        var busyReply = job2Messages.Last();
        busyReply.SenderType.Should().Be("persona");
        busyReply.SenderId.Should().Be("astra");
        busyReply.Content.Should().Contain("I'm currently busy working on another job (\"First Active Job\")");
        busyReply.Content.Should().Contain("queued this up and will start as soon as I'm done");

        // Now job1 finishes its turn: dispatch queued turns for astra
        activeSessionJob1.Status = "active";
        await db.SaveChangesAsync();

        await ProjectJobsController.CheckAndDispatchQueuedJobTurnsAsync(db, mockHubContext.Object, "astra");

        // Verify job2 queued turn was dequeued
        var dispatchedJob2 = await db.ProjectJobs.FindAsync(job2.Id);
        var remainingQueuedTurns = ProjectJobsController.ParseQueuedTurns(dispatchedJob2!.QueuedTurnsJson);
        remainingQueuedTurns.Should().BeEmpty();

        // Verify a new session was created for job2
        var job2Sessions = await db.AgentSessions.Where(s => s.PersonaId == "astra" && s.Id != activeSessionJob1.Id).ToListAsync();
        job2Sessions.Should().HaveCount(1);
        job2Sessions[0].Status.Should().Be("thinking");
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
            Id = "spence-ai-rook",
            Name = "Rook",
            Role = "Software Engineer",
            DriverId = "spence-ai",
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

        // Verify system cancellation message was posted
        var messages = await db.JobMessages.Where(m => m.JobId == job.Id).ToListAsync();
        messages.Should().HaveCount(1);
        messages[0].SenderType.Should().Be("system");
        messages[0].Content.Should().Contain("Queued turn for @Rook was cancelled.");

        // Verify hub broadcasts were made
        mockClientProxy.Verify(
            p => p.SendCoreAsync("JobMessageCreated", It.IsAny<object[]>(), default),
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

        db.Personas.Add(new Persona { Id = "rook", Name = "Rook", Role = "Engineer", DriverId = "spence-ai", PersonaPrompt = "Code" });
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        var res = await controller.CancelQueuedTurns(job.Id, "rook");
        var ok = res.Should().BeOfType<OkObjectResult>().Subject;

        // Bulk cancel must emit ONE audit message, not one per turn (the fan-out
        // equivalent produced N messages and raced on QueuedTurnsJson).
        var messages = await db.JobMessages.Where(m => m.JobId == job.Id).ToListAsync();
        messages.Should().HaveCount(1);
        messages[0].SenderType.Should().Be("system");
        messages[0].Content.Should().Contain("3 queued turn(s)");
        // Display name, not raw persona id, matching the single-cancel message format.
        messages[0].Content.Should().Contain("@Rook");

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

        (await db.JobMessages.CountAsync(m => m.JobId == job.Id)).Should().Be(0);
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
        (await db.JobMessages.CountAsync(m => m.JobId == job.Id)).Should().Be(0);
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

        var audit = await db.JobMessages.Where(m => m.JobId == job.Id && m.SenderType == "system").ToListAsync();
        audit.Should().HaveCount(1);
        audit[0].Content.Should().Contain("Fix auto-scroll");
        audit[0].Content.Should().Contain("@Astra");
        audit[0].Content.Should().Contain("@Soren");
        audit[0].Content.Should().Contain("1 queued turn");

        // Soren has a free slot, so the handed-over turn runs for the new assignee at once.
        // Without this the turn would only drain when some *other* Soren session finished —
        // and an idle Soren has none, so the work would strand in the queue permanently.
        var persistedTurns = ProjectJobsController.ParseQueuedTurns((await db.ProjectJobs.FindAsync(job.Id))!.QueuedTurnsJson);
        persistedTurns.Should().BeEmpty();

        var sorenSession = await db.AgentSessions.SingleAsync(s => s.PersonaId == "soren");
        sorenSession.Status.Should().Be("thinking");
        var sorenPrompt = await db.SessionMessages.SingleAsync(m => m.SessionId == sorenSession.Id && m.EventType == "prompt");
        sorenPrompt.Content.Should().Contain("please fix the scroll behaviour");
        sorenPrompt.Content.Should().Contain("[Current Target Task]: \"Fix auto-scroll\"");

        var sorenAck = await db.JobMessages.SingleAsync(m => m.JobId == job.Id && m.SenderId == "soren");
        sorenAck.Content.Should().Be("I'm on it — starting work on \"Fix auto-scroll\" now.");

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

        var audit = await db.JobMessages.SingleAsync(m => m.SenderType == "system");
        audit.Content.Should().Contain("@Astra").And.Contain("@Soren");
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
        (await db.JobMessages.CountAsync(m => m.JobId == job.Id && m.SenderType == "system")).Should().Be(0);
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
    public async Task PostJobMessage_WithoutMention_RoutesToAssigneeOfTargetTask()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-route");
        project.DefaultLeadPersonaId = "soren";
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Assigned to astra", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Routing Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        var res = await controller.PostJobMessage(job.Id, new PostJobMessageRequest(Content: "get this done please"));
        res.Should().BeOfType<OkObjectResult>();

        var turnSession = await db.AgentSessions.SingleAsync(s => s.Status == "thinking");
        turnSession.PersonaId.Should().Be("astra");
        var ack = await db.JobMessages.SingleAsync(m => m.SenderType == "persona");
        ack.SenderId.Should().Be("astra");
        ack.Content.Should().Be("I'm on it — starting work on \"Assigned to astra\" now.");
    }

    [Fact]
    public async Task PostJobMessage_ExplicitMentionStillWinsOverTaskAssignee()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-mention");
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Assigned to astra", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Mention Wins Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(Content: "@soren can you take this one instead?"));

        (await db.AgentSessions.SingleAsync(s => s.Status == "thinking")).PersonaId.Should().Be("soren");
    }

    [Fact]
    public async Task PostJobMessage_NamesTheTaskTheMentionedPersonaActuallyOwns()
    {
        // Two people own different tasks in one job. The ack must name the responder's own
        // task, not the first pending row in the list — that mismatch is what made the
        // operator see "starting work on X" while the agent was doing Y.
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-target");
        var astraTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Astra review task", AssignedPersonaId = "astra" };
        var sorenTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Soren implementation task", AssignedPersonaId = "soren" };
        var job = MakeAssignmentJob(project.Id, "Split Targets Job", new List<JobTaskDto> { astraTask, sorenTask });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(Content: "@soren start on yours"));

        var ack = await db.JobMessages.SingleAsync(m => m.SenderType == "persona");
        ack.SenderId.Should().Be("soren");
        ack.Content.Should().Be("I'm on it — starting work on \"Soren implementation task\" now.");

        var prompt = await db.SessionMessages.SingleAsync(m => m.EventType == "prompt");
        prompt.Content.Should().Contain("[Current Target Task]: \"Soren implementation task\"");
        prompt.Content.Should().Contain("[ ] (CURRENT ACTIVE FOCUS) Soren implementation task");
        prompt.Content.Should().Contain("[ ] (Queued) Astra review task [Assigned: @Astra]");
    }

    [Fact]
    public async Task PostJobMessage_DoesNotClaimAnUnownedTaskWhenEveryPendingTaskBelongsToSomeoneElse()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-noclaim");
        var astraTask = new JobTaskDto { Id = Guid.NewGuid(), Title = "Astra only task", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "No Claim Job", new List<JobTaskDto> { astraTask });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(Content: "@soren quick question before you start"));

        var ack = await db.JobMessages.SingleAsync(m => m.SenderType == "persona");
        ack.SenderId.Should().Be("soren");
        ack.Content.Should().NotContain("Astra only task");
        ack.Content.Should().Be("I'm on it — looking at this now.");
    }

    [Fact]
    public async Task PromptContext_NamesTheAssigneeAndTellsUnassignedPersonasNotToStart()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-warn");
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "Locked to astra", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "Warn Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(Content: "@soren are you able to look at this?"));

        var prompt = await db.SessionMessages.SingleAsync(m => m.EventType == "prompt");
        prompt.Content.Should().Contain("[Assigned: @Astra]");
        prompt.Content.Should().Contain("[Task Ownership Rule]");
        prompt.Content.Should().Contain("assigned to other team members (@Astra)");
    }

    [Fact]
    public async Task PromptContext_LeavesChecklistUnchangedWhenNothingIsAssigned()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-assign-plain");
        var job = MakeAssignmentJob(project.Id, "Plain Job", new List<JobTaskDto>
        {
            new() { Id = Guid.NewGuid(), Title = "Unassigned task" }
        });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);
        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(Content: "@astra go"));

        var prompt = await db.SessionMessages.SingleAsync(m => m.EventType == "prompt");
        prompt.Content.Should().Contain("[ ] (CURRENT ACTIVE FOCUS) Unassigned task");
        prompt.Content.Should().NotContain("[Assigned:");
        prompt.Content.Should().NotContain("[Task Ownership Rule]");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Durable-workflow routing and turn ownership
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A persona reply releases a parked workflow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter here used to be <c>SenderType == "user"</c>, on the reasoning that a persona message is
    /// agent-to-agent chatter. That was right about chatter and fatal about escalations: an orchestrated
    /// agent blocks with <c>requires_reply: true, target_persona: "astra"</c>, and the only party who can
    /// answer is another persona — whose reply arrives with <c>SenderType = "persona"</c>. So the
    /// escalation could never be answered and the task sat for the full seven-day gate. A cross-persona
    /// review request, which is the normal shape of two-man review in this product, was a guaranteed
    /// deadlock.
    /// </para>
    /// <para>
    /// Seeded with no personas assigned to the project, so the endpoint's own dispatch branch is skipped
    /// and the assertion isolates the routing call. Assigning a persona here would let the test pass
    /// through the legacy dispatcher instead.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_RoutesAPersonaReplyIntoTheWorkflow()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project { Id = "proj-route", Name = "Route", WorkspaceType = WorkspaceType.Git });
        var job = MakeAssignmentJob("proj-route", "Escalation Job", new List<JobTaskDto>());
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var router = new RecordingRouter();
        var controller = NewRoutingController(db, router);

        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(
            Content: "Sign-off: approved, merge it.",
            SenderType: "persona",
            SenderId: "astra"));

        router.ChatSends.Should().ContainSingle(
            "a persona is the only party who can answer an agent's escalation; dropping the reply is " +
            "what left parked tasks wedged for seven days");
        router.ChatSends[0].signal.SenderId.Should().Be("astra");
        router.ChatSends[0].signal.Content.Should().Contain("approved");
    }

    /// <summary>
    /// A human reply is still routed — the relaxation must not cost the original case.
    /// </summary>
    [Fact]
    public async Task PostJobMessage_StillRoutesAHumanReply()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project { Id = "proj-route", Name = "Route", WorkspaceType = WorkspaceType.Git });
        var job = MakeAssignmentJob("proj-route", "Human Reply Job", new List<JobTaskDto>());
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var router = new RecordingRouter();
        var controller = NewRoutingController(db, router);

        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(
            Content: "Use OIDC.", SenderType: "user", SenderId: "operator"));

        router.ChatSends.Should().ContainSingle();
        router.ChatSends[0].signal.SenderId.Should().Be("operator");
    }

    /// <summary>
    /// Generated bookkeeping is still excluded.
    /// </summary>
    /// <remarks>
    /// <c>system</c> rows are notices nobody addressed to anyone (handoff records, completion markers).
    /// Releasing a turn on one would be pure noise, and the ack the endpoint itself writes for a persona
    /// dispatch is exactly such a row — so routing them would make a single human message wake the
    /// workflow twice.
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_DoesNotRouteASystemMessage()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project { Id = "proj-route", Name = "Route", WorkspaceType = WorkspaceType.Git });
        var job = MakeAssignmentJob("proj-route", "System Message Job", new List<JobTaskDto>());
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var router = new RecordingRouter();
        var controller = NewRoutingController(db, router);

        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(
            Content: "Job handed off from astra to sorren.",
            SenderType: "system",
            SenderId: "system"));

        router.ChatSends.Should().BeEmpty(
            "system rows are generated bookkeeping, not a reply anybody is waiting on");
    }

    /// <summary>
    /// A job under workflow management does not also acquire a legacy SignalR runner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ownership rule the feature's documentation claimed and this endpoint did not enforce. The
    /// dispatch branch keys off "a persona was mentioned and no session id was supplied", and an
    /// unaddressed human reply resolves the target to the next pending task's assignee — on an
    /// orchestrated job, exactly the persona the workflow is driving. So a plain "yes, use OIDC" both
    /// released the workflow's gate AND queued a second agent on the same working tree: two agents, one
    /// branch, edits clobbering each other and pushes rejected for reasons nobody could reproduce.
    /// </para>
    /// <para>
    /// Asserted as "no session was created and nothing was queued" rather than by watching for a dispatch
    /// call, because the dispatch here is a SignalR send plus a row insert — the rows are what a later
    /// reader would find, and they are what an operator would blame.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_WhenTheWorkflowIsDriving_DoesNotDispatchALegacyRunner()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-own");
        var task = new JobTaskDto
        {
            Id = Guid.NewGuid(),
            Title = "Implement the gate",
            AssignedPersonaId = "astra",
        };
        var job = MakeAssignmentJob(project.Id, "Orchestrated Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var router = new RecordingRouter { WorkflowDriving = true };
        var controller = NewRoutingController(db, router);

        // Unaddressed: targetPersonaId resolves to the pending task's assignee, which is the persona the
        // workflow is driving. This is the exact shape that used to double-drive.
        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(
            Content: "yes, use OIDC", SenderType: "user", SenderId: "operator"));

        router.ChatSends.Should().ContainSingle(
            "the reply still has to reach the parked workflow — suppression is about the runner, not the signal");

        (await db.AgentSessions.AnyAsync()).Should().BeFalse(
            "a workflow-driven job must not also start a SignalR turn: two agents on one branch is the " +
            "failure this guard exists to prevent");
        (await db.JobMessages.AnyAsync(m => m.SenderType == "persona")).Should().BeFalse(
            "the 'I'm on it' ack comes from the legacy dispatch path; emitting it while the workflow " +
            "owns the turn would tell the operator an agent started that did not");

        var reloaded = await db.ProjectJobs.SingleAsync(j => j.Id == job.Id);
        ParseQueued(reloaded.QueuedTurnsJson).Should().BeEmpty(
            "and the reply must not be parked in the legacy queue either, where a runner would claim it");
    }

    /// <summary>
    /// When the workflow is not driving, the legacy path is untouched.
    /// </summary>
    /// <remarks>
    /// The counterpart that keeps the guard honest. Without it, "always suppress" would pass the test
    /// above while silently disabling every non-orchestrated job in the product — including every job
    /// that exists today, since orchestration is opt-in per task.
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_WhenNoWorkflowIsDriving_StillDispatchesTheLegacyRunner()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-own");
        var task = new JobTaskDto
        {
            Id = Guid.NewGuid(),
            Title = "Implement the gate",
            AssignedPersonaId = "astra",
        };
        var job = MakeAssignmentJob(project.Id, "Plain Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var router = new RecordingRouter { WorkflowDriving = false };
        var controller = NewRoutingController(db, router);

        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(
            Content: "yes, use OIDC", SenderType: "user", SenderId: "operator"));

        (await db.AgentSessions.AnyAsync(s => s.PersonaId == "astra")).Should().BeTrue(
            "the pre-orchestration behaviour must survive intact for a job nobody is orchestrating");
    }

    /// <summary>
    /// A router that cannot answer the ownership probe must not suppress the dispatch.
    /// </summary>
    /// <remarks>
    /// Fails toward dispatch, deliberately. The real router swallows its own probe errors and returns
    /// false; a fake that throws models the caller being handed an exception anyway (a cancellation
    /// racing the request). Either way the answer must be "dispatch as if not orchestrated": a control-plane
    /// outage that silently swallows chat replies is worse than the double-drive it avoids, because a
    /// double-drive is visible and recoverable while a dropped turn is neither.
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_WhenTheOwnershipProbeThrows_StillDispatches()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-own");
        var task = new JobTaskDto
        {
            Id = Guid.NewGuid(),
            Title = "Implement the gate",
            AssignedPersonaId = "astra",
        };
        var job = MakeAssignmentJob(project.Id, "Outage Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = NewRoutingController(db, new ThrowingOwnershipRouter());

        var result = await controller.PostJobMessage(job.Id, new PostJobMessageRequest(
            Content: "yes, use OIDC", SenderType: "user", SenderId: "operator"));

        result.Should().BeOfType<OkObjectResult>(
            "a Temporal outage must not turn into a failed message post");
        (await db.AgentSessions.AnyAsync(s => s.PersonaId == "astra")).Should().BeTrue(
            "and it must not silently swallow the turn either");
    }

    /// <summary>
    /// With no router wired, the endpoint behaves exactly as it did before orchestration existed.
    /// </summary>
    [Fact]
    public async Task PostJobMessage_WithoutARouter_DispatchesAndRoutesNothing()
    {
        var (db, project, _, _) = BuildAssignmentContext("proj-own");
        var task = new JobTaskDto { Id = Guid.NewGuid(), Title = "T", AssignedPersonaId = "astra" };
        var job = MakeAssignmentJob(project.Id, "No Router Job", new List<JobTaskDto> { task });
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db);

        await controller.PostJobMessage(job.Id, new PostJobMessageRequest(
            Content: "yes, use OIDC", SenderType: "user", SenderId: "operator"));

        (await db.AgentSessions.AnyAsync(s => s.PersonaId == "astra")).Should().BeTrue();
    }

    // ── Harness for the routing tests ────────────────────────────────────────────

    /// <summary>
    /// Builds the controller with a router, leaving the other optional parameters null.
    /// </summary>
    /// <remarks>
    /// Positional through <c>workflows</c> because the constructor's later parameters are optional; a
    /// named argument keeps that ordering dependency visible instead of silent.
    /// </remarks>
    private static ProjectJobsController NewRoutingController(
        AgentMeshDbContext db,
        AgentMesh.Api.Services.IWorkflowSignalRouter router) =>
        new(db, hubContext: null, storageService: null, workflows: router);

    private static List<QueuedJobTurnDto> ParseQueued(string json) =>
        ProjectJobsController.ParseQueuedTurns(json);

    private sealed class RecordingRouter : AgentMesh.Api.Services.IWorkflowSignalRouter
    {
        public List<(Guid jobId, AgentMesh.Core.Workflows.ChatMessageSignal signal)> ChatSends { get; } = [];

        /// <summary>What the ownership probe answers.</summary>
        public bool WorkflowDriving { get; set; }

        public Task<AgentMesh.Api.Services.SignalDelivery> SendChatMessageAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.ChatMessageSignal signal,
            CancellationToken ct = default)
        {
            ChatSends.Add((jobId, signal));
            return Task.FromResult(new AgentMesh.Api.Services.SignalDelivery(
                "task-execution-" + jobId.ToString("N"),
                AgentMesh.Api.Services.SignalDeliveryStatus.Delivered));
        }

        public Task<AgentMesh.Api.Services.SignalDelivery> SendPrReviewAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.PrReviewSignal signal,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentMesh.Api.Services.SignalDelivery(
                "task-execution-" + jobId.ToString("N"),
                AgentMesh.Api.Services.SignalDeliveryStatus.Delivered));

        public Task<AgentMesh.Api.Services.WorkflowStartResult> StartTaskWorkflowAsync(
            AgentMesh.Core.Workflows.TaskExecutionInput input, CancellationToken ct = default) =>
            Task.FromResult(new AgentMesh.Api.Services.WorkflowStartResult(true, "test-workflow"));

        public Task<AgentMesh.Core.Workflows.TaskStatusView?> GetStatusAsync(
            Guid jobId, string? taskId, CancellationToken ct = default) =>
            Task.FromResult<AgentMesh.Core.Workflows.TaskStatusView?>(null);

        public Task<bool> IsWorkflowDrivingAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult(WorkflowDriving);
    }

    private sealed class ThrowingOwnershipRouter : AgentMesh.Api.Services.IWorkflowSignalRouter
    {
        public Task<AgentMesh.Api.Services.SignalDelivery> SendChatMessageAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.ChatMessageSignal signal,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentMesh.Api.Services.SignalDelivery(
                "task-execution-" + jobId.ToString("N"),
                AgentMesh.Api.Services.SignalDeliveryStatus.NoRunningWorkflow));

        public Task<AgentMesh.Api.Services.SignalDelivery> SendPrReviewAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.PrReviewSignal signal,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentMesh.Api.Services.SignalDelivery(
                "task-execution-" + jobId.ToString("N"),
                AgentMesh.Api.Services.SignalDeliveryStatus.NoRunningWorkflow));

        public Task<AgentMesh.Api.Services.WorkflowStartResult> StartTaskWorkflowAsync(
            AgentMesh.Core.Workflows.TaskExecutionInput input, CancellationToken ct = default) =>
            Task.FromResult(new AgentMesh.Api.Services.WorkflowStartResult(true, "test-workflow"));

        public Task<AgentMesh.Core.Workflows.TaskStatusView?> GetStatusAsync(
            Guid jobId, string? taskId, CancellationToken ct = default) =>
            Task.FromResult<AgentMesh.Core.Workflows.TaskStatusView?>(null);

        public Task<bool> IsWorkflowDrivingAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromException<bool>(new InvalidOperationException("simulated Temporal outage"));
    }
}
