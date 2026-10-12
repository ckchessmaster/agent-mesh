using System.Text;
using AgentMesh.Api.Controllers;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class JobsControllerArtifactsTests
{
    private readonly Mock<IStorageService> _mockStorage;

    public JobsControllerArtifactsTests()
    {
        _mockStorage = new Mock<IStorageService>();
    }

    [Fact]
    public async Task GetArtifact_WhenInlinePayload_ReturnsContentResult()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var job = new AgentJob
        {
            Id = Guid.NewGuid(),
            PayloadJson = "{\"inline\":\"payload\"}",
            PayloadS3Uri = null
        };
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new JobsController(db);

        // Act
        var result = await controller.GetArtifact(job.Id, "payload", _mockStorage.Object);

        // Assert
        var contentResult = result.Should().BeOfType<ContentResult>().Subject;
        contentResult.Content.Should().Be("{\"inline\":\"payload\"}");
        contentResult.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task GetArtifact_WhenOffloadedToS3_StreamsFromSeaweedFS()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var job = new AgentJob
        {
            Id = Guid.NewGuid(),
            PayloadJson = "{\"_offloaded\":true}",
            PayloadS3Uri = "s3://agent-mesh-artifacts/jobs/test-1/payload.json"
        };
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();

        var s3Content = "{\"large\":\"raw-data\"}";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(s3Content));
        _mockStorage.Setup(s => s.DownloadStreamAsync(job.PayloadS3Uri, default))
            .ReturnsAsync(stream);

        var controller = new JobsController(db);

        // Act
        var result = await controller.GetArtifact(job.Id, "payload", _mockStorage.Object);

        // Assert
        var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.FileDownloadName.Should().Be("payload");
        fileResult.ContentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task GetPresignedUrl_WhenOffloadedToS3_ReturnsPresignedLink()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var job = new AgentJob
        {
            Id = Guid.NewGuid(),
            PayloadS3Uri = "s3://agent-mesh-artifacts/jobs/test-1/payload.json"
        };
        db.AgentJobs.Add(job);
        await db.SaveChangesAsync();

        _mockStorage.Setup(s => s.GetPresignedUrlAsync(job.PayloadS3Uri, It.IsAny<TimeSpan>()))
            .ReturnsAsync("http://localhost:8333/agent-mesh-artifacts/jobs/test-1/payload.json?sig=abc");

        var controller = new JobsController(db);

        // Act
        var result = await controller.GetPresignedUrl(job.Id, "payload", _mockStorage.Object);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();
    }
}
