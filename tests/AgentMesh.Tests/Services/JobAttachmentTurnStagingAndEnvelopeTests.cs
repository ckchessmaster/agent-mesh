using System.Text;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

public class JobAttachmentTurnStagingAndEnvelopeTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly List<Guid> _jobDirsToCleanup = new();
    private readonly List<Guid> _sessionDirsToCleanup = new();

    private Guid TrackJob(Guid id)
    {
        _jobDirsToCleanup.Add(id);
        return id;
    }

    private Guid TrackSession(Guid id)
    {
        _sessionDirsToCleanup.Add(id);
        return id;
    }

    public void Dispose()
    {
        foreach (var jobId in _jobDirsToCleanup)
        {
            TryDeleteDir(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentmesh", "artifacts", "jobs", jobId.ToString()));
        }

        foreach (var sessionId in _sessionDirsToCleanup)
        {
            TryDeleteDir(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentmesh", "artifacts", sessionId.ToString()));
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort teardown cleanup
        }
    }

    private static (IHubContext<AgentMeshHub> Hub, List<object?> PromptPayloads) CreateCapturingHub()
    {
        var payloads = new List<object?>();
        var mockHub = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockAllProxy = new Mock<IClientProxy>();
        var mockGroupProxy = new Mock<IClientProxy>();

        mockAllProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockGroupProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) =>
            {
                if (method == "PromptReceived" && args is { Length: > 0 })
                {
                    payloads.Add(args[0]);
                }
            })
            .Returns(Task.CompletedTask);

        mockClients.Setup(c => c.All).Returns(mockAllProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockGroupProxy.Object);
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        return (mockHub.Object, payloads);
    }

    private static JobAttachmentDto WriteJobAttachmentFile(Guid jobId, string attId, string fileName, string contentType, byte[] bytes)
    {
        var dir = ProjectJobsController.GetAttachmentDirectory(jobId);
        var safeName = SessionsController.SanitizeFileName(fileName);
        File.WriteAllBytes(Path.Combine(dir, $"{attId}_{safeName}"), bytes);

        return new JobAttachmentDto(
            Id: attId,
            JobId: jobId,
            FileName: safeName,
            ContentType: contentType,
            SizeBytes: bytes.Length,
            S3Key: $"jobs/{jobId}/attachments/{attId}/{safeName}",
            DownloadUrl: $"/api/project-jobs/{jobId}/attachments/{attId}/content",
            CreatedAt: DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task JobTurnDispatcher_DispatchAsync_StagesPersistedJobAttachments_WhenRequestAttachmentsNull()
    {
        using var db = TestDbContextFactory.Create();
        var (hub, payloads) = CreateCapturingHub();

        var jobId = TrackJob(Guid.NewGuid());
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03, 0x04 };
        var screenshot = WriteJobAttachmentFile(jobId, "jobshot01", "error-dialog.png", "image/png", pngBytes);

        var project = new Project
        {
            Id = "stage-job-att",
            Name = "Stage Job Attachment",
            WorkspaceType = WorkspaceType.Git,
            RepositoryName = "agent-mesh"
        };
        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Fix dialog alignment",
            Description = "See attached screenshot",
            GitBranchName = "mesh/job-dialog",
            AttachmentsJson = JsonSerializer.Serialize(new[] { screenshot }, JsonOpts)
        };
        var persona = new Persona
        {
            Id = "bee",
            Name = "Bee",
            Role = "Principal Engineer",
            DriverId = "copilot-driver"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        var dispatcher = new JobTurnDispatcher(db, hub);
        var dispatched = await dispatcher.DispatchAsync(new JobTurnRequest(
            Job: job,
            Project: project,
            Persona: persona,
            Prompt: "Inspect the UI bug.",
            Sender: "system",
            Attachments: null,
            TaskId: "stage-1-discovery"));

        TrackSession(dispatched.Session.Id);

        // 1. Staged onto turn session's attachment directory
        var stagedPath = Path.Combine(
            SessionsController.GetAttachmentDirectory(dispatched.Session.Id),
            "jobshot01_error-dialog.png");
        File.Exists(stagedPath).Should().BeTrue();
        File.ReadAllBytes(stagedPath).Should().Equal(pngBytes);

        // 2. Recorded on turnSession.MetadataJson and SessionMessage.StructuredPayloadJson
        using var metaDoc = JsonDocument.Parse(dispatched.Session.MetadataJson!);
        metaDoc.RootElement.GetProperty("attachments").GetArrayLength().Should().Be(1);
        dispatched.PromptMessage.StructuredPayloadJson.Should().Contain("jobshot01").And.Contain("error-dialog.png");
        dispatched.PromptMessage.Content.Should().Contain("[Job Attachments: error-dialog.png");

        // 3. Broadcast on SignalR PromptReceived so runner stages into .agentmesh/attachments/
        payloads.Should().NotBeEmpty();
        var wire = JsonSerializer.Deserialize<PromptReceivedEvent>(
            JsonSerializer.Serialize(payloads[0], JsonOpts), JsonOpts);
        wire.Should().NotBeNull();
        wire!.Attachments.Should().ContainSingle(a =>
            a.Id == "jobshot01" &&
            a.FileName == "error-dialog.png" &&
            a.DownloadUrl == $"/api/project-jobs/{jobId}/attachments/jobshot01/content");
    }

    [Fact]
    public async Task MacroTurnController_StartMacroTurn_MergesJobAndFocusTaskAttachments_DeduplicatedById()
    {
        using var db = TestDbContextFactory.Create();
        var (hub, payloads) = CreateCapturingHub();

        var jobId = TrackJob(Guid.NewGuid());
        var focusTaskId = Guid.NewGuid();
        var otherTaskId = Guid.NewGuid();

        var sharedAtt = WriteJobAttachmentFile(jobId, "shared01", "shared.png", "image/png", [1, 2, 3]);
        var jobOnlyAtt = WriteJobAttachmentFile(jobId, "jobonly02", "architecture.md", "text/markdown", Encoding.UTF8.GetBytes("# Arch"));
        var focusTaskAtt = WriteJobAttachmentFile(jobId, "taskonly03", "task-mock.png", "image/png", [4, 5, 6]);
        var otherTaskAtt = WriteJobAttachmentFile(jobId, "othertask04", "unrelated.png", "image/png", [7, 8, 9]);

        var project = new Project
        {
            Id = "macro-att",
            Name = "Macro Turn Attachment Project",
            WorkspaceType = WorkspaceType.Git,
            RepositoryName = "agent-mesh"
        };
        var tasks = new List<JobTaskDto>
        {
            new()
            {
                Id = focusTaskId,
                Title = "Implement modal",
                AssignedPersonaId = "bee",
                Attachments = [sharedAtt, focusTaskAtt]
            },
            new()
            {
                Id = otherTaskId,
                Title = "Later task",
                AssignedPersonaId = "bee",
                Attachments = [otherTaskAtt]
            }
        };
        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Modal attachment workflow",
            Status = ProjectJobStatus.Active,
            GitBranchName = "mesh/job-modal",
            TasksJson = JsonSerializer.Serialize(tasks, JsonOpts),
            AttachmentsJson = JsonSerializer.Serialize(new[] { sharedAtt, jobOnlyAtt }, JsonOpts)
        };
        var persona = new Persona
        {
            Id = "bee",
            Name = "Bee",
            Role = "Principal Engineer",
            DriverId = "copilot-driver"
        };
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        var dispatcher = new JobTurnDispatcher(db, hub);
        var macroController = new MacroTurnController(db, dispatcher, hubContext: hub)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await macroController.StartMacroTurn(
            jobId,
            new StartMacroTurnRequest(
                Prompt: "[TASK ENVELOPE & WORKING ENVIRONMENT]\nExecute focus task.",
                PersonaId: "bee",
                TaskId: focusTaskId.ToString()),
            CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<StartMacroTurnResponse>().Subject;
        TrackSession(response.TurnSessionId);

        payloads.Should().NotBeEmpty();
        var wire = JsonSerializer.Deserialize<PromptReceivedEvent>(
            JsonSerializer.Serialize(payloads[0], JsonOpts), JsonOpts);
        wire.Should().NotBeNull();
        wire!.Attachments.Should().NotBeNull();
        wire.Attachments!.Select(a => a.Id).Should().Equal("shared01", "jobonly02", "taskonly03");
        wire.Attachments.Select(a => a.Id).Should().NotContain("othertask04");
    }

    [Fact]
    public async Task CheckAndDispatchQueuedJobTurnsAsync_IncludesPersistedJobAttachments()
    {
        using var db = TestDbContextFactory.Create();
        var (hub, payloads) = CreateCapturingHub();

        var jobId = TrackJob(Guid.NewGuid());
        var jobAtt = WriteJobAttachmentFile(jobId, "queuedjob01", "pasted.png", "image/png", [0x89, 0x50, 0x4E, 0x47]);

        var project = new Project
        {
            Id = "queued-att",
            Name = "Queued Turn Project",
            WorkspaceType = WorkspaceType.Git,
            RepositoryName = "agent-mesh"
        };
        var persona = new Persona
        {
            Id = "bee",
            Name = "Bee",
            Role = "Principal Engineer",
            DriverId = "copilot-driver"
        };
        var queuedTurn = new QueuedJobTurnDto(
            Id: Guid.NewGuid(),
            JobId: jobId,
            ProjectId: project.Id,
            TargetPersonaId: "bee",
            SenderType: "operator",
            SenderId: "operator",
            SenderName: "Operator",
            Content: "Please check the pasted screenshot.",
            Mentions: ["bee"],
            Attachments: null,
            Priority: ProjectJobPriority.Normal,
            CreatedAt: DateTimeOffset.UtcNow);

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = project.Id,
            Title = "Queued screenshot check",
            Status = ProjectJobStatus.Active,
            GitBranchName = "mesh/job-queued",
            QueuedTurnsJson = JsonSerializer.Serialize(new[] { queuedTurn }, JsonOpts),
            AttachmentsJson = JsonSerializer.Serialize(new[] { jobAtt }, JsonOpts)
        };

        db.Projects.Add(project);
        db.Personas.Add(persona);
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        await ProjectJobsController.CheckAndDispatchQueuedJobTurnsAsync(db, hub, "bee");

        var session = db.AgentSessions.Single();
        TrackSession(session.Id);

        var stagedFile = Path.Combine(SessionsController.GetAttachmentDirectory(session.Id), "queuedjob01_pasted.png");
        File.Exists(stagedFile).Should().BeTrue();

        payloads.Should().NotBeEmpty();
        var wire = JsonSerializer.Deserialize<PromptReceivedEvent>(
            JsonSerializer.Serialize(payloads[0], JsonOpts), JsonOpts);
        wire!.Attachments.Should().ContainSingle(a => a.Id == "queuedjob01" && a.FileName == "pasted.png");
    }

    [Fact]
    public async Task PromptStackBuilder_RendersJobAttachmentsInLayer6EnvelopeOnly_PreservingCachedSystemPrefixByteIdentity()
    {
        var dbNameWithout = "PromptJobAtt_Empty_" + Guid.NewGuid();
        var dbNameWith = "PromptJobAtt_Populated_" + Guid.NewGuid();

        using var emptyDb = TestDbContextFactory.Create(dbNameWithout);
        using var populatedDb = TestDbContextFactory.Create(dbNameWith);

        var jobId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var taskId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

        static async Task SeedAsync(AgentMeshDbContext db, Guid jid, Guid tid, string attachmentsJson, List<JobAttachmentDto>? taskAttachments)
        {
            db.Projects.Add(new Project
            {
                Id = "agent-mesh",
                Name = "Agent Mesh",
                RepositoryName = "agent-mesh",
                GitBaseBranch = "main",
                ProjectInstructions = "Follow repository conventions."
            });
            db.Personas.Add(new Persona
            {
                Id = "bee",
                Name = "Bee",
                Role = "Principal Software Engineer",
                DriverId = "driver-bee",
                PersonaPrompt = "You are Bee.",
                TrustTier = TrustTier.Intern
            });
            db.Agents.Add(new Agent
            {
                Id = "driver-bee",
                Name = "Bee Driver",
                RuntimeType = RuntimeType.CliAgy,
                TrustTier = TrustTier.Intern
            });
            db.ProjectJobs.Add(new ProjectJob
            {
                Id = jid,
                ProjectId = "agent-mesh",
                Title = "Job Attachments",
                Description = "Support uploading and pasting screenshots",
                GitBranchName = "mesh/job-67fcb18a",
                Status = ProjectJobStatus.Active,
                AttachmentsJson = attachmentsJson,
                TasksJson = JsonSerializer.Serialize(new[]
                {
                    new JobTaskDto
                    {
                        Id = tid,
                        Title = "Wire prompt envelope",
                        AssignedPersonaId = "bee",
                        Attachments = taskAttachments
                    }
                }, JsonOpts)
            });
            await db.SaveChangesAsync();
        }

        await SeedAsync(emptyDb, jobId, taskId, "[]", null);

        var sharedScreenshot = new JobAttachmentDto(
            Id: "attshot01",
            JobId: jobId,
            FileName: "pasted-screenshot.png",
            ContentType: "image/png",
            SizeBytes: 15360,
            S3Key: $"jobs/{jobId}/attachments/attshot01/pasted-screenshot.png",
            DownloadUrl: $"/api/project-jobs/{jobId}/attachments/attshot01/content",
            CreatedAt: DateTimeOffset.UtcNow);
        var taskLog = new JobAttachmentDto(
            Id: "attlog02",
            JobId: jobId,
            FileName: "stacktrace.txt",
            ContentType: "text/plain",
            SizeBytes: 512,
            S3Key: $"jobs/{jobId}/attachments/attlog02/stacktrace.txt",
            DownloadUrl: $"/api/project-jobs/{jobId}/attachments/attlog02/content",
            CreatedAt: DateTimeOffset.UtcNow);

        await SeedAsync(
            populatedDb,
            jobId,
            taskId,
            JsonSerializer.Serialize(new[] { sharedScreenshot }, JsonOpts),
            [sharedScreenshot, taskLog]);

        static Task<AssembledPromptStack> BuildStackAsync(AgentMeshDbContext db, Guid jid, Guid tid)
        {
            var reconciler = new ShellWorkspaceReconciler(shellRunner: (_, _, _) =>
                Task.FromResult(new CommandExecutionResult(true, 0, """
                    @@HEADTYPE@@
                    branch
                    @@BRANCH@@
                    mesh/job-67fcb18a
                    @@UPSTREAM@@
                    origin/mesh/job-67fcb18a
                    @@COUNT@@
                    0
                    @@FILES@@
                    @@STASH@@
                    @@HEAD@@
                    9c41122 feat: add project_jobs.attachments
                    @@DONE@@
                    """)));
            var builder = new PromptStackBuilder(
                db,
                new WorkspaceStateRepository(db),
                reconciler,
                null,
                new AgentMemoryRepository(db));

            return builder.BuildPromptStackAsync(new PromptStackContext(
                AgentId: "driver-bee",
                ProjectId: "agent-mesh",
                JobId: jid.ToString(),
                TaskId: tid.ToString(),
                WorkspacePath: "/workspace/repos/agent-mesh",
                TargetBranch: "mesh/job-67fcb18a",
                UserPrompt: string.Empty,
                PersonaId: "bee"));
        }

        var stackWithout = await BuildStackAsync(emptyDb, jobId, taskId);
        var stackWith = await BuildStackAsync(populatedDb, jobId, taskId);

        // 1. Adding mid-job attachments MUST NOT perturb Layers 1–5 cached system prompt!
        stackWith.CachedSystemPrompt.Should().Be(stackWithout.CachedSystemPrompt);
        stackWithout.UserTaskEnvelope.Should().NotContain(PromptStackBuilder.JobAttachmentsHeader);

        // 2. Layer 6 UserTaskEnvelope renders [JOB ATTACHMENTS] with staged path + API download URL, deduplicated by ID
        stackWith.UserTaskEnvelope.Should().Contain(PromptStackBuilder.JobAttachmentsHeader);
        stackWith.UserTaskEnvelope.Should().Contain(
            "* pasted-screenshot.png (image/png, 15 KB) — staged: .agentmesh/attachments/attshot01_pasted-screenshot.png → " +
            $"/api/project-jobs/{jobId}/attachments/attshot01/content");
        stackWith.UserTaskEnvelope.Should().Contain(
            "* stacktrace.txt (text/plain, 512 B) — staged: .agentmesh/attachments/attlog02_stacktrace.txt → " +
            $"/api/project-jobs/{jobId}/attachments/attlog02/content");

        // Shared screenshot appears only once even though present on both job and focus task
        var occurrences = stackWith.UserTaskEnvelope.Split("attshot01_pasted-screenshot.png").Length - 1;
        occurrences.Should().Be(1);
    }
}
