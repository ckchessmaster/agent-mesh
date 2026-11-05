using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
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

/// <summary>
/// The job artifact read endpoints and the session-to-job mirror.
/// </summary>
/// <remarks>
/// <para>
/// Each test uses a fresh <see cref="Guid"/> for the job and session, because the artifact paths are
/// derived from those ids under <c>~/.agentmesh/artifacts/</c> and the disk scan is part of the read path
/// — a shared id would let one test's files appear in another's assertions. Anything a test writes to disk
/// is removed in <see cref="Dispose"/>.
/// </para>
/// <para>
/// The InMemory provider is deliberate for the aggregation logic; it cannot express jsonb or the
/// <c>metadata-&gt;&gt;'jobId'</c> operator, which is why the session lookup is driven through
/// <c>job_messages.turn_session_id</c> here and the SQL shape of the active-turn filter is not asserted
/// (see <c>SessionPagingSqlTranslationTests</c> for how SQL shape is pinned in this repo when it matters).
/// </para>
/// </remarks>
public sealed class ProjectJobsControllerArtifactTests : IDisposable
{
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly List<string> _tempPaths = new();

    public ProjectJobsControllerArtifactTests()
    {
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a test fixture directory.
            }
        }
    }

    private static string SessionDir(Guid sessionId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentmesh", "artifacts", sessionId.ToString());

    private static string JobDir(Guid jobId) => JobArtifactMirror.ResolveArtifactDirectory(jobId);

    private static string SessionMetadata(Guid jobId) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["jobId"] = jobId.ToString(),
        ["personaId"] = "rook"
    });

    private static string SessionMetadataWithArtifacts(Guid jobId, params (string Id, string Name, string ContentType, long Size)[] artifacts)
    {
        var list = artifacts.Select(a => new Dictionary<string, object?>
        {
            ["id"] = a.Id,
            ["name"] = a.Name,
            ["contentType"] = a.ContentType,
            ["sizeBytes"] = a.Size,
            ["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-30),
            ["updatedAt"] = DateTimeOffset.UtcNow.AddMinutes(-5),
            ["s3Key"] = $"sessions/x/artifacts/{a.Id}/{a.Name}"
        }).Cast<object?>().ToList();

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jobId"] = jobId.ToString(),
            ["artifacts"] = list
        });
    }

    private string WriteJobFile(Guid jobId, string name, string content)
    {
        var dir = JobDir(jobId);
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);

        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        _tempPaths.Add(path);
        return path;
    }

    /// <summary>
    /// Unwraps an OkObjectResult to its typed payload, failing with the actual result type when the
    /// action returned something else (a 404 or 500 is a far better message than a cast exception).
    /// </summary>
    private static T Ok<T>(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return (T)ok.Value!;
    }

    /// <summary>
    /// The controller with only its required dependency. The hub, storage, workflow and logger
    /// parameters are optional by design so the artifact endpoints stay testable without a cluster;
    /// the storage-dependent assertions inject via <c>[FromServices]</c> instead.
    /// </summary>
    private static ProjectJobsController Controller(
        AgentMesh.Infrastructure.Data.AgentMeshDbContext db) => new(db);

    [Fact]
    public async Task GetJobArtifacts_WhenJobUnknown_ReturnsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var controller = Controller(db);

        var result = await controller.GetJobArtifacts(Guid.NewGuid());

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetJobArtifacts_WhenJobHasNoArtifacts_ReturnsEmptyList()
    {
        using var db = TestDbContextFactory.Create();
        var job = new ProjectJob { Id = Guid.NewGuid(), ProjectId = "core-app", Title = "Empty" };
        db.ProjectJobs.Add(job);
        await db.SaveChangesAsync();

        var result = await Controller(db).GetJobArtifacts(job.Id);

        Ok<List<JobArtifactDto>>(result).Should().BeEmpty();
    }

    [Fact]
    public async Task GetJobArtifacts_AggregatesArtifactsFromTurnSessions_AndPersistsThem()
    {
        // Arrange: a job whose turn session recorded two artifacts, linked by a job message — the shape a
        // job has after a turn ran and posted its reply.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var session = new AgentSession
        {
            Id = sessionId,
            Title = "Job Turn",
            MetadataJson = SessionMetadataWithArtifacts(
                jobId, ("a1", "plan.md", "text/markdown", 120), ("a2", "diagram.mmd", "text/plain", 40))
        };
        db.AgentSessions.Add(session);
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        db.JobMessages.Add(new JobMessage
        {
            JobId = jobId,
            TurnSessionId = sessionId,
            Content = "turn reply",
            SenderType = "persona"
        });
        await db.SaveChangesAsync();

        // Act
        var result = await Controller(db).GetJobArtifacts(jobId);

        // Assert
        var artifacts = Ok<List<JobArtifactDto>>(result);
        artifacts.Select(a => a.Name).Should().BeEquivalentTo("plan.md", "diagram.mmd");
        artifacts.Should().OnlyContain(a => a.JobId == jobId);
        artifacts.Should().OnlyContain(a => a.SourceSessionId == sessionId);
        artifacts.Should().OnlyContain(a => a.DownloadUrl != null &&
                                            a.DownloadUrl!.Contains($"/api/project-jobs/{jobId}/artifacts/"));

        // The fold is persisted, so the next open is a single-row read rather than a re-derivation.
        var stored = JobArtifactManifest.Read((await db.ProjectJobs.FirstAsync(j => j.Id == jobId)).ArtifactsJson);
        stored.Select(a => a.Name).Should().BeEquivalentTo("plan.md", "diagram.mmd");
    }

    [Fact]
    public async Task GetJobArtifacts_IsIdempotent_AcrossRepeatedReads()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            MetadataJson = SessionMetadataWithArtifacts(jobId, ("a1", "plan.md", "text/markdown", 120))
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        db.JobMessages.Add(new JobMessage { JobId = jobId, TurnSessionId = sessionId, Content = "x" });
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var first = Ok<List<JobArtifactDto>>(await controller.GetJobArtifacts(jobId));
        var second = Ok<List<JobArtifactDto>>(await controller.GetJobArtifacts(jobId));

        second.Should().HaveCount(first.Count);
        second.Select(a => a.Id).Should().BeEquivalentTo(first.Select(a => a.Id));
    }

    [Fact]
    public async Task GetJobArtifacts_PicksUpLooseFileInJobArtifactDirectory()
    {
        // An agent that writes straight into the job's artifact directory — no session sync — still shows
        // up, which is what makes the feature work in practice rather than only for adapters that call the
        // artifact endpoint.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        WriteJobFile(jobId, "notes.md", "# loose notes");

        var artifacts = Ok<List<JobArtifactDto>>(await Controller(db).GetJobArtifacts(jobId));

        var entry = artifacts.Single(a => a.Name == "notes.md");
        entry.ContentType.Should().Be("text/markdown");
        entry.SizeBytes.Should().BeGreaterThan(0);
        entry.SourceSessionId.Should().BeNull();
        entry.DownloadUrl.Should().Be($"/api/project-jobs/{jobId}/artifacts/notes.md/content");
    }

    [Fact]
    public async Task GetJobArtifacts_DoesNotListJobAttachments()
    {
        // Attachments are user uploads with their own endpoints and their own UI. They live one level
        // below the artifact directory, and the scan must stay top-directory-only so the two concepts do
        // not merge in the panel.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        var attachDir = Path.Combine(JobDir(jobId), "attachments");
        Directory.CreateDirectory(attachDir);
        _tempPaths.Add(JobDir(jobId));
        var attachFile = Path.Combine(attachDir, "upload.png");
        File.WriteAllText(attachFile, "bytes");
        _tempPaths.Add(attachFile);

        var artifacts = Ok<List<JobArtifactDto>>(await Controller(db).GetJobArtifacts(jobId));

        artifacts.Should().NotContain(a => a.Name == "upload.png");
        artifacts.Should().NotContain(a => a.Name == "attachments");
    }

    [Fact]
    public async Task GetJobArtifacts_WhenJobNeverHadArtifacts_DoesNotCreateItsDirectory()
    {
        // A GET must not leave a directory behind for every job it is asked about.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Pristine" });
        await db.SaveChangesAsync();

        var dir = JobDir(jobId);
        Directory.Exists(dir).Should().BeFalse("precondition: this job has never had artifacts");

        await Controller(db).GetJobArtifacts(jobId);

        Directory.Exists(dir).Should().BeFalse("listing artifacts must not create the artifact directory");
    }

    [Fact]
    public async Task GetJobArtifact_InlinesTextBody_AndOmitsBodyFromTheList()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        WriteJobFile(jobId, "plan.md", "# Architecture\nkeep it simple");

        var list = Ok<List<JobArtifactDto>>(await Controller(db).GetJobArtifacts(jobId));
        list.Single().Content.Should().BeNull("the list endpoint must not ship bodies");

        var detail = await Controller(db).GetJobArtifact(jobId, "plan.md");

        var dto = detail.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<JobArtifactDto>().Subject;
        dto.Name.Should().Be("plan.md");
        dto.Content.Should().Contain("keep it simple");
    }

    [Fact]
    public async Task GetJobArtifact_ResolvesByManifestIdOrByName()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            MetadataJson = SessionMetadataWithArtifacts(jobId, ("a1", "plan.md", "text/markdown", 5))
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        db.JobMessages.Add(new JobMessage { JobId = jobId, TurnSessionId = sessionId, Content = "x" });
        await db.SaveChangesAsync();
        WriteJobFile(jobId, "plan.md", "# by id or name");

        var controller = Controller(db);

        // The panel addresses by id; a caller that only knows the filename still resolves.
        var byId = await controller.GetJobArtifact(jobId, "a1");
        var byName = await controller.GetJobArtifact(jobId, "plan.md");

        ((JobArtifactDto)byId.Should().BeOfType<OkObjectResult>().Subject.Value!).Id.Should().Be("a1");
        ((JobArtifactDto)byName.Should().BeOfType<OkObjectResult>().Subject.Value!).Id.Should().Be("a1");
    }

    [Fact]
    public async Task GetJobArtifact_WhenUnknown_ReturnsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        (await Controller(db).GetJobArtifact(jobId, "ghost.md")).Should().BeOfType<NotFoundObjectResult>();
        (await Controller(db).GetJobArtifact(Guid.NewGuid(), "plan.md")).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetJobArtifactContent_ServesBytesAndForcesDownloadOnRequest()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();
        WriteJobFile(jobId, "diagram.png", "fake-png-bytes");

        var controller = Controller(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var inline = await controller.GetJobArtifactContent(jobId, "diagram.png", download: false);
        var file = inline.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("image/png");
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline");

        var forced = await controller.GetJobArtifactContent(jobId, "diagram.png", download: true);
        forced.Should().BeOfType<FileStreamResult>();
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("attachment");
    }

    [Fact]
    public async Task GetJobArtifactContent_FallsBackToSourceSessionDirectory()
    {
        // The mirror stores a pointer, not a copy: the bytes stay in the session that produced them, and
        // the job endpoint has to reach them there.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            MetadataJson = SessionMetadataWithArtifacts(jobId, ("a1", "plan.md", "text/markdown", 9))
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        db.JobMessages.Add(new JobMessage { JobId = jobId, TurnSessionId = sessionId, Content = "x" });
        await db.SaveChangesAsync();

        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        var file = Path.Combine(dir, "plan.md");
        File.WriteAllText(file, "# lives in the session directory");
        _tempPaths.Add(file);

        var result = await Controller(db).GetJobArtifactContent(jobId, "a1", download: false);

        var streamResult = result.Should().BeOfType<FileStreamResult>().Subject;
        using var reader = new StreamReader(streamResult.FileStream!);
        (await reader.ReadToEndAsync()).Should().Contain("lives in the session directory");
    }

    [Fact]
    public async Task GetJobArtifactContent_WhenBytesMissing_ReturnsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            // Recorded in a manifest, but the file is gone (deleted session directory, no S3 configured).
            MetadataJson = SessionMetadataWithArtifacts(jobId, ("a1", "gone.md", "text/markdown", 9))
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        db.JobMessages.Add(new JobMessage { JobId = jobId, TurnSessionId = sessionId, Content = "x" });
        await db.SaveChangesAsync();

        (await Controller(db).GetJobArtifactContent(jobId, "a1"))
            .Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetJobArtifactContent_WhenNameEscapesTheDirectory_DoesNotServeForeignFiles()
    {
        // artifactId reaches a filesystem path. A caller supplying "../" must not be able to read outside
        // the job's artifact directory.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        var controller = Controller(db);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var traversal = await controller.GetJobArtifactContent(jobId, "../../../../etc/passwd");

        traversal.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SyncArtifact_WhenSessionBelongsToJob_MirrorsIntoTheJobManifest()
    {
        // The production path: an agent syncs an artifact against its turn session, and the job picks it up
        // without the agent knowing a job exists.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = "Job Turn",
            MetadataJson = SessionMetadata(jobId)
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var sync = await controller.SyncArtifact(sessionId, new SyncArtifactRequest(
            Name: "architecture_plan.md",
            Content: "# Architecture Plan",
            Summary: "the plan",
            ContentType: "text/markdown"));

        sync.Should().BeOfType<OkObjectResult>();

        var job = await db.ProjectJobs.FirstAsync(j => j.Id == jobId);
        var mirrored = JobArtifactManifest.Read(job.ArtifactsJson);
        var entry = mirrored.Should().ContainSingle().Subject;
        entry.Name.Should().Be("architecture_plan.md");
        entry.JobId.Should().Be(jobId);
        entry.SourceSessionId.Should().Be(sessionId);
        entry.Summary.Should().Be("the plan");
        entry.DownloadUrl.Should().Be($"/api/project-jobs/{jobId}/artifacts/{entry.Id}/content");

        // And the job endpoint serves what the mirror recorded, end to end.
        var listed = Ok<List<JobArtifactDto>>(await Controller(db).GetJobArtifacts(jobId));
        listed.Should().ContainSingle(a => a.Name == "architecture_plan.md");

        _tempPaths.Add(SessionDir(sessionId));
    }

    [Fact]
    public async Task SyncArtifact_WhenReSynced_UpdatesTheMirroredEntryInsteadOfDuplicating()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            MetadataJson = SessionMetadata(jobId)
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        await controller.SyncArtifact(sessionId, new SyncArtifactRequest("plan.md", "v1", null, "text/markdown"));
        var firstId = JobArtifactManifest.Read((await db.ProjectJobs.FirstAsync(j => j.Id == jobId)).ArtifactsJson)
            .Single().Id;

        await controller.SyncArtifact(sessionId, new SyncArtifactRequest("plan.md", "v2 longer body", null, "text/markdown"));

        var job = await db.ProjectJobs.FirstAsync(j => j.Id == jobId);
        var mirrored = JobArtifactManifest.Read(job.ArtifactsJson);
        mirrored.Should().ContainSingle();
        // Identity survives the re-sync, so an open panel keeps its selection.
        mirrored.Single().Id.Should().Be(firstId);
        mirrored.Single().SizeBytes.Should().BeGreaterThan(2);

        _tempPaths.Add(SessionDir(sessionId));
    }

    [Fact]
    public async Task SyncArtifact_WhenSessionIsNotAJobTurn_LeavesNoJobBehindAndDoesNotThrow()
    {
        // The majority of sessions are interactive. Mirroring must be a no-op for them, and must not fail
        // the sync the agent asked for.
        using var db = TestDbContextFactory.Create();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession { Id = sessionId, Title = "Interactive", MetadataJson = "{}" });
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var sync = await controller.SyncArtifact(sessionId, new SyncArtifactRequest("note.md", "hi"));

        sync.Should().BeOfType<OkObjectResult>();
        db.ProjectJobs.Should().BeEmpty();

        _tempPaths.Add(SessionDir(sessionId));
    }

    [Fact]
    public async Task SyncArtifact_ResolvesTheJobThroughTheJobMessageWhenMetadataLacksJobId()
    {
        // The fallback link: a session created without the job envelope, but whose turn posted into a job
        // chat, still mirrors.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession { Id = sessionId, MetadataJson = "{}" });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        db.JobMessages.Add(new JobMessage
        {
            JobId = jobId,
            TurnSessionId = sessionId,
            Content = "turn reply",
            SenderType = "persona"
        });
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        await controller.SyncArtifact(sessionId, new SyncArtifactRequest("linked.md", "body"));

        var job = await db.ProjectJobs.FirstAsync(j => j.Id == jobId);
        JobArtifactManifest.Read(job.ArtifactsJson).Should().ContainSingle(a => a.Name == "linked.md");

        _tempPaths.Add(SessionDir(sessionId));
    }

    [Fact]
    public void ResolveJobId_WhenSessionHasNoJobLink_ReturnsNull()
    {
        using var db = TestDbContextFactory.Create();

        JobArtifactMirror.ResolveJobId(new AgentSession { Id = Guid.NewGuid(), MetadataJson = "{}" }, db)
            .Should().BeNull();
        JobArtifactMirror.ResolveJobId(new AgentSession { Id = Guid.NewGuid(), MetadataJson = null }, db)
            .Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_WhenJobUnknown_ReturnsEmptyWithoutThrowing()
    {
        using var db = TestDbContextFactory.Create();

        (await JobArtifactMirror.RefreshAsync(db, Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public void ResolveJobId_FallsBackToTheJobMessageLink()
    {
        // The mirror's own resolution path, asserted directly: a session whose envelope lacks jobId is
        // still a job's turn if a job message names it, and without this fallback a re-sync of that
        // turn's artifacts would silently stop updating the job.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.JobMessages.Add(new JobMessage
        {
            JobId = jobId,
            TurnSessionId = sessionId,
            Content = "turn reply",
            SenderType = "persona"
        });
        db.SaveChanges();

        JobArtifactMirror.ResolveJobId(
            new AgentSession { Id = sessionId, MetadataJson = "{}" }, db).Should().Be(jobId);
    }

    [Fact]
    public void ResolveJobId_PrefersTheSessionEnvelopeOverTheMessageLink()
    {
        using var db = TestDbContextFactory.Create();
        var envelopeJob = Guid.NewGuid();
        var messageJob = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.JobMessages.Add(new JobMessage
        {
            JobId = messageJob,
            TurnSessionId = sessionId,
            Content = "x",
            SenderType = "persona"
        });
        db.SaveChanges();

        JobArtifactMirror.ResolveJobId(
            new AgentSession { Id = sessionId, MetadataJson = SessionMetadata(envelopeJob) }, db)
            .Should().Be(envelopeJob);
    }

    [Fact]
    public async Task RefreshAsync_RecoversAnArtifactTheMirrorLostToARacingWrite()
    {
        // The concurrency argument in JobArtifactMirror's remarks rests on one claim: the manifest is an
        // index over data that lives elsewhere, so a lost write converges rather than disappearing. This
        // asserts that claim directly — a job row whose manifest is missing an entry that its turn
        // session still records comes back complete on the next read, with no re-sync from the agent.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            MetadataJson = SessionMetadataWithArtifacts(
                jobId, ("a1", "kept.md", "text/markdown", 5), ("a2", "lost.md", "text/markdown", 6))
        });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "core-app",
            Title = "Build it",
            // Simulates the lost update: only one of the two artifacts survived the fold.
            ArtifactsJson = JobArtifactManifest.Write(new[]
            {
                new JobArtifactDto("a1", jobId, sessionId, "kept.md", null, "text/markdown", 5,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null,
                    JobArtifactManifest.BuildContentUrl(jobId, "a1"))
            })
        });
        db.JobMessages.Add(new JobMessage { JobId = jobId, TurnSessionId = sessionId, Content = "x" });
        await db.SaveChangesAsync();

        var artifacts = await JobArtifactMirror.RefreshAsync(db, jobId);

        artifacts.Select(a => a.Name).Should().BeEquivalentTo("kept.md", "lost.md");

        // And the recovery is persisted, not just returned: the next read is a plain index read again.
        var stored = JobArtifactManifest.Read((await db.ProjectJobs.FirstAsync(j => j.Id == jobId)).ArtifactsJson);
        stored.Select(a => a.Name).Should().Contain("lost.md");
    }

    [Fact]
    public async Task RefreshAsync_IsDeterministic_TheStoredManifestIsByteStableAcrossReads()
    {
        // The manifest is persisted on read, so re-reading must not churn it.
        //
        // Two properties, and they are protected by different mechanisms:
        //
        // 1. The persisted bytes are stable, because Merge keys on name and keeps the stored entry's
        //    identity. A discovery step producing different ids per pass would still not rewrite the row,
        //    so this assertion alone is not load-bearing (confirmed by mutation).
        //
        // 2. The ids address the artifact — the load-bearing one. The session-side equivalent
        //    (SessionsController.GetArtifacts) mints a fresh Guid for every file it discovers; copying
        //    that here would change each disk entry's id and therefore its downloadUrl on every read,
        //    breaking a held selection and 404ing an image whose src used the previous id. Keying disk
        //    entries by filename is what fails loudly when that behaviour is copied in.
        //
        // Asserted on the persisted string with the tracker cleared between reads, so the second read
        // re-fetches from the store rather than reusing the first read's object.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            MetadataJson = SessionMetadataWithArtifacts(
                jobId, ("a1", "plan.md", "text/markdown", 5), ("a2", "diagram.mmd", "text/plain", 7))
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        db.JobMessages.Add(new JobMessage { JobId = jobId, TurnSessionId = sessionId, Content = "x" });
        await db.SaveChangesAsync();

        WriteJobFile(jobId, "loose-notes.md", "# written straight to the job directory");

        await JobArtifactMirror.RefreshAsync(db, jobId);
        await db.SaveChangesAsync();
        var first = (await db.ProjectJobs.FirstAsync(j => j.Id == jobId)).ArtifactsJson;

        db.ChangeTracker.Clear();
        await JobArtifactMirror.RefreshAsync(db, jobId);
        await db.SaveChangesAsync();
        var second = (await db.ProjectJobs.FirstAsync(j => j.Id == jobId)).ArtifactsJson;

        second.Should().Be(first);

        // Entries keep the ids that address them — the session's own ids, and filenames for disk files —
        // rather than freshly minted ones. This is the property that makes a re-read addressable by a
        // selection the panel already holds.
        JobArtifactManifest.Read(second).Select(a => a.Id)
            .Should().BeEquivalentTo("a1", "a2", "loose-notes.md");
    }

    [Fact]
    public async Task RefreshAsync_PicksUpAnActiveTurnSessionByItsJobEnvelope()
    {
        // A turn in flight has produced artifacts but not yet posted a job message, so the
        // job_messages link does not exist yet. The active-session lookup is what makes the panel live
        // during a turn rather than after it.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            Status = "thinking",
            MetadataJson = SessionMetadataWithArtifacts(jobId, ("a1", "wip.md", "text/markdown", 3))
        });
        // An unrelated active session must not leak its artifacts into this job.
        db.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            Status = "thinking",
            MetadataJson = SessionMetadataWithArtifacts(Guid.NewGuid(), ("a9", "other.md", "text/markdown", 3))
        });
        db.ProjectJobs.Add(new ProjectJob { Id = jobId, ProjectId = "core-app", Title = "Build it" });
        await db.SaveChangesAsync();

        var artifacts = await JobArtifactMirror.RefreshAsync(db, jobId);

        artifacts.Should().ContainSingle(a => a.Name == "wip.md");
    }
}
