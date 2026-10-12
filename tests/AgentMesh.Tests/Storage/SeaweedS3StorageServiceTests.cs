using System.Text;
using AgentMesh.Infrastructure.Storage;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Storage;

public class SeaweedS3StorageServiceTests
{
    private readonly Mock<IAmazonS3> _mockS3;
    private readonly IConfiguration _config;

    public SeaweedS3StorageServiceTests()
    {
        _mockS3 = new Mock<IAmazonS3>();
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:S3:BucketName"] = "agent-mesh-artifacts",
                ["Storage:S3:ServiceUrl"] = "http://localhost:8333"
            })
            .Build();
    }

    [Fact]
    public async Task UploadTextAsync_UploadsToS3_AndReturnsS3Uri()
    {
        // Arrange
        _mockS3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), default))
            .ReturnsAsync(new PutObjectResponse());

        var storage = new SeaweedS3StorageService(_mockS3.Object, _config, NullLogger<SeaweedS3StorageService>.Instance);

        // Act
        var uri = await storage.UploadTextAsync("jobs/test-1/payload.json", "{\"key\":\"val\"}");

        // Assert
        uri.Should().Be("s3://agent-mesh-artifacts/jobs/test-1/payload.json");
        _mockS3.Verify(s => s.PutObjectAsync(It.Is<PutObjectRequest>(r =>
            r.BucketName == "agent-mesh-artifacts" &&
            r.Key == "jobs/test-1/payload.json" &&
            r.ContentBody == "{\"key\":\"val\"}"), default), Times.Once);
    }

    [Fact]
    public async Task DownloadTextAsync_ReadsFromS3ObjectStream()
    {
        // Arrange
        var content = "{\"diff\":\"large patch data\"}";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        _mockS3.Setup(s => s.GetObjectAsync("agent-mesh-artifacts", "jobs/test-1/diff.patch", default))
            .ReturnsAsync(new GetObjectResponse { ResponseStream = stream });

        var storage = new SeaweedS3StorageService(_mockS3.Object, _config, NullLogger<SeaweedS3StorageService>.Instance);

        // Act
        var downloaded = await storage.DownloadTextAsync("s3://agent-mesh-artifacts/jobs/test-1/diff.patch");

        // Assert
        downloaded.Should().Be(content);
    }

    [Fact]
    public async Task GetPresignedUrlAsync_DelegatesToS3Client()
    {
        // Arrange
        _mockS3.Setup(s => s.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
            .Returns("http://localhost:8333/agent-mesh-artifacts/jobs/test-1/payload.json?X-Amz-Signature=xyz");

        var storage = new SeaweedS3StorageService(_mockS3.Object, _config, NullLogger<SeaweedS3StorageService>.Instance);

        // Act
        var url = await storage.GetPresignedUrlAsync("jobs/test-1/payload.json", TimeSpan.FromMinutes(15));

        // Assert
        url.Should().Contain("X-Amz-Signature=xyz");
        _mockS3.Verify(s => s.GetPreSignedURL(It.Is<GetPreSignedUrlRequest>(r =>
            r.BucketName == "agent-mesh-artifacts" &&
            r.Key == "jobs/test-1/payload.json")), Times.Once);
    }
}
