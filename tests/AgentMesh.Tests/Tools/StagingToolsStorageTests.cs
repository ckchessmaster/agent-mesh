using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Temporalio.Client;
using Xunit;

namespace AgentMesh.Tests.Tools;

public class StagingToolsStorageTests
{
    private readonly Mock<ITemporalClient> _mockTemporalClient;
    private readonly Mock<IStorageService> _mockStorage;
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly IConfiguration _config;

    public StagingToolsStorageTests()
    {
        _mockTemporalClient = new Mock<ITemporalClient>();
        _mockStorage = new Mock<IStorageService>();
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:S3:ThresholdBytes"] = "1024" // Low threshold for test
            })
            .Build();
    }

    [Fact]
    public async Task StageAgentIntent_SmallPayload_DoesNotOffloadToS3()
    {
        // Arrange
        var staging = new StagingTools(
            _mockTemporalClient.Object,
            NullLogger<StagingTools>.Instance,
            null,
            _mockHubContext.Object,
            _mockStorage.Object,
            _config);

        var smallPayload = "{\"small\":\"data\"}";

        // Act
        var resultJson = await staging.StageAgentIntent("github", "apply_diff", smallPayload, Guid.NewGuid().ToString(), default);

        // Assert
        using var doc = JsonDocument.Parse(resultJson);
        doc.RootElement.GetProperty("status").GetString().Should().Be("staged");
        doc.RootElement.GetProperty("payload_s3_uri").ValueKind.Should().Be(JsonValueKind.Null);

        _mockStorage.Verify(s => s.UploadTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task StageAgentIntent_LargePayload_OffloadsToSeaweedFS()
    {
        // Arrange
        _mockStorage.Setup(s => s.UploadTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync("s3://agent-mesh-artifacts/jobs/test-job/payload.json");

        var staging = new StagingTools(
            _mockTemporalClient.Object,
            NullLogger<StagingTools>.Instance,
            null,
            _mockHubContext.Object,
            _mockStorage.Object,
            _config);

        // Exceeds 1024 bytes threshold
        var largePayload = new string('x', 2048);

        // Act
        var resultJson = await staging.StageAgentIntent("github", "apply_diff", largePayload, Guid.NewGuid().ToString(), default);

        // Assert
        using var doc = JsonDocument.Parse(resultJson);
        doc.RootElement.GetProperty("status").GetString().Should().Be("staged");
        doc.RootElement.GetProperty("payload_s3_uri").GetString().Should().Be("s3://agent-mesh-artifacts/jobs/test-job/payload.json");

        _mockStorage.Verify(s => s.UploadTextAsync(It.Is<string>(k => k.StartsWith("jobs/")), largePayload, "application/json", default), Times.Once);
    }
}
