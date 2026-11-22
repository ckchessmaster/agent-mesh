using System.Text;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
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

public class ProjectJobsControllerJobAttachmentTests : IDisposable
{
    private readonly List<Guid> _jobDirsToCleanup = new();

    private Guid TrackJob(Guid jobId)
    {
        _jobDirsToCleanup.Add(jobId);
        return jobId;
    }

    public void Dispose()
    {
        foreach (var jobId in _jobDirsToCleanup)
        {
            try
            {
                var jobRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".agentmesh",
                    "artifacts",
                    "jobs",
                    jobId.ToString());
                if (Directory.Exists(jobRoot))
                {
                    Directory.Delete(jobRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup
            }
        }
    }

    private static (Mock<IHubContext<AgentMeshHub>> Hub, List<ProjectJobDto> BroadcastJobs) CreateCapturingHub()
    {
        var broadcastJobs = new List<ProjectJobDto>();
        var mockHub = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        var mockProxy = new Mock<IClientProxy>();

        mockProxy
            .Setup(p => p.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) =>
            {
                if (method == "JobUpdated" && args is { Length: > 0 } && args[0] is ProjectJobDto dto)
                {
                    broadcastJobs.Add(dto);
                }
            })
            .Returns(Task.CompletedTask);

        mockClients.Setup(c => c.All).Returns(mockProxy.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockProxy.Object);
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

        return (mockHub, broadcastJobs);
    }

    private static IFormFile CreateFormFile(string fileName, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    [Fact]
    public async Task UploadJobAttachments_PersistsManifestAccumulatesAcrossUploads_SurfacesInGetEndpoints_AndBroadcastsJobUpdated()
    {
        using var db = TestDbContextFactory.Create();
        var (mockHub, broadcastJobs) = CreateCapturingHub();

        var jobId = TrackJob(Guid.NewGuid());
        db.Projects.Add(new Project { Id = "attach-proj", Name = "Attachment Project" });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "attach-proj",
            Title = "Support pasted screenshots"
        });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db, mockHub.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var firstBatch = new FormFileCollection
        {
            CreateFormFile("pasted-screenshot.png", "image/png", pngBytes)
        };

        var upload1 = await controller.UploadJobAttachments(jobId, firstBatch);
        var uploaded1 = upload1.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<JobAttachmentDto>>().Subject;
        uploaded1.Should().ContainSingle();

        // Anytime mid-job upload appends rather than overwriting
        var mdBytes = Encoding.UTF8.GetBytes("# Repro steps\n1. Open modal\n2. Paste image");
        var secondBatch = new FormFileCollection
        {
            CreateFormFile("repro.md", "text/markdown", mdBytes)
        };

        var upload2 = await controller.UploadJobAttachments(jobId, secondBatch);
        var uploaded2 = upload2.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<JobAttachmentDto>>().Subject;
        uploaded2.Should().ContainSingle();

        // Persisted in database row
        var persistedJob = await db.ProjectJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        var parsedFromDb = ProjectJobsController.ParseAttachments(persistedJob.AttachmentsJson);
        parsedFromDb.Should().HaveCount(2);
        parsedFromDb.Select(a => a.FileName).Should().ContainInOrder("pasted-screenshot.png", "repro.md");

        // Surfaced in GET /api/project-jobs/{id}/attachments
        var listResult = await controller.GetJobAttachments(jobId);
        var listed = listResult.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<JobAttachmentDto>>().Subject;
        listed.Should().HaveCount(2);
        listed.Select(a => a.Id).Should().Equal(uploaded1[0].Id, uploaded2[0].Id);

        // Surfaced in GET /api/project-jobs/{id} (ProjectJobDto.Attachments)
        var getJobResult = await controller.GetJob(jobId);
        var jobDto = getJobResult.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ProjectJobDto>().Subject;
        jobDto.Attachments.Should().NotBeNull().And.HaveCount(2);

        // SignalR JobUpdated broadcast on both uploads with updated manifest
        broadcastJobs.Should().HaveCount(2);
        broadcastJobs[0].Attachments.Should().ContainSingle(a => a.FileName == "pasted-screenshot.png");
        broadcastJobs[1].Attachments.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteJobAttachment_RemovesFromManifest_CleansLocalCopy_BroadcastsJobUpdated_AndReturns404WhenMissing()
    {
        using var db = TestDbContextFactory.Create();
        var (mockHub, broadcastJobs) = CreateCapturingHub();

        var jobId = TrackJob(Guid.NewGuid());
        db.Projects.Add(new Project { Id = "del-proj", Name = "Delete Attachment Project" });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "del-proj",
            Title = "Delete screenshot test"
        });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db, mockHub.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var files = new FormFileCollection
        {
            CreateFormFile("remove-me.png", "image/png", [0x89, 0x50, 0x4E, 0x47]),
            CreateFormFile("keep-me.txt", "text/plain", Encoding.UTF8.GetBytes("keep"))
        };

        var uploadRes = await controller.UploadJobAttachments(jobId, files);
        var uploaded = uploadRes.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<JobAttachmentDto>>().Subject;
        var toDelete = uploaded[0];
        var toKeep = uploaded[1];

        broadcastJobs.Clear();

        var deleteRes = await controller.DeleteJobAttachment(jobId, toDelete.Id);
        deleteRes.Should().BeOfType<OkObjectResult>();

        var remaining = ProjectJobsController.ParseAttachments(
            (await db.ProjectJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).AttachmentsJson);
        remaining.Should().ContainSingle().Which.Id.Should().Be(toKeep.Id);

        broadcastJobs.Should().ContainSingle();
        broadcastJobs[0].Attachments.Should().ContainSingle(a => a.Id == toKeep.Id);

        // Deleted attachment content is no longer served locally
        var deletedContent = await controller.GetAttachmentContent(jobId, toDelete.Id);
        deletedContent.Should().BeOfType<NotFoundObjectResult>();

        // Kept attachment still streams cleanly
        await using var keptStreamResult = (await controller.GetAttachmentContent(jobId, toKeep.Id))
            .Should().BeOfType<FileStreamResult>().Subject.FileStream;
        using var reader = new StreamReader(keptStreamResult);
        (await reader.ReadToEndAsync()).Should().Be("keep");

        // Deleting again returns 404
        var secondDelete = await controller.DeleteJobAttachment(jobId, toDelete.Id);
        secondDelete.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAttachmentContent_WhenPodLocalFileMissing_UsesManifestS3KeyFileNameAndContentType()
    {
        using var db = TestDbContextFactory.Create();

        var jobId = TrackJob(Guid.NewGuid());
        db.Projects.Add(new Project { Id = "s3-proj", Name = "S3 Fallback Project" });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "s3-proj",
            Title = "S3 fallback job"
        });
        await db.SaveChangesAsync();

        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        string? uploadedS3Key = null;

        var mockStorage = new Mock<IStorageService>();
        mockStorage
            .Setup(s => s.UploadStreamAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, CancellationToken>((key, _, _, _) => uploadedS3Key = key)
            .ReturnsAsync((string key, Stream _, string _, CancellationToken _) => $"s3://bucket/{key}");
        mockStorage
            .Setup(s => s.DownloadStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                key == uploadedS3Key
                    ? new MemoryStream(pngBytes)
                    : throw new FileNotFoundException($"Unexpected S3 key: {key}"));

        var controller = new ProjectJobsController(db, storageService: mockStorage.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var uploadRes = await controller.UploadJobAttachments(
            jobId,
            new FormFileCollection { CreateFormFile("cockpit-paste.png", "image/png", pngBytes) });
        var att = uploadRes.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<List<JobAttachmentDto>>().Subject.Single();

        // Simulate pod restart / different API replica by deleting pod-local directory
        var localDir = ProjectJobsController.GetAttachmentDirectory(jobId);
        Directory.Delete(localDir, recursive: true);

        var contentRes = await controller.GetAttachmentContent(jobId, att.Id, download: false);
        var streamResult = contentRes.Should().BeOfType<FileStreamResult>().Subject;
        streamResult.ContentType.Should().Be("image/png");
        controller.Response.Headers.ContentDisposition.ToString()
            .Should().Contain("inline").And.Contain("cockpit-paste.png");

        mockStorage.Verify(
            s => s.DownloadStreamAsync(att.S3Key, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateJob_WithInitialAttachments_NormalizesJobIdAndPersistsManifest()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project { Id = "init-proj", Name = "Initial Attachments Project" });
        await db.SaveChangesAsync();

        var controller = new ProjectJobsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var seedAtt = new JobAttachmentDto(
            Id: "seedatt01",
            JobId: Guid.Empty,
            FileName: "wireframe.png",
            ContentType: "image/png",
            SizeBytes: 4096,
            S3Key: "jobs/temp/wireframe.png",
            DownloadUrl: "/api/project-jobs/temp/attachments/seedatt01/content",
            CreatedAt: DateTimeOffset.UtcNow);

        var createRes = await controller.CreateJob("init-proj", new CreateProjectJobRequest(
            Title: "Build wireframe UI",
            Description: "Match attached wireframe",
            Attachments: [seedAtt]));

        var created = createRes.Should().BeOfType<CreatedAtActionResult>().Subject.Value
            .Should().BeOfType<ProjectJobDto>().Subject;
        TrackJob(created.Id);

        created.Attachments.Should().ContainSingle();
        created.Attachments![0].Id.Should().Be("seedatt01");
        created.Attachments[0].JobId.Should().Be(created.Id);
    }
}
