using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
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
}
