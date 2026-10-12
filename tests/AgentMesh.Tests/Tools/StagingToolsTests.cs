using System.Text.Json;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Temporalio.Client;
using Xunit;

namespace AgentMesh.Tests.Tools;

public class StagingToolsTests
{
    private readonly Mock<ITemporalClient> _mockTemporalClient;
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;

    public StagingToolsTests()
    {
        _mockTemporalClient = new Mock<ITemporalClient>();
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
    }

    [Fact]
    public async Task StageAgentIntent_StartsTemporalWorkflow_AndBroadcastsJobStaged()
    {
        // Arrange
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Agent-Id"] = "gemini-spark";
        httpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);

        var stagingTools = new StagingTools(
            _mockTemporalClient.Object,
            NullLogger<StagingTools>.Instance,
            httpContextAccessor.Object,
            _mockHubContext.Object);

        var sessionId = Guid.NewGuid().ToString();

        // Act
        var resultJson = await stagingTools.StageAgentIntent(
            targetService: "platform_ops",
            actionVerb: "stage_journal_entry",
            payloadJson: "{\"title\":\"Test Entry\"}",
            sessionId: sessionId,
            ct: default);

        // Assert
        resultJson.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("staged");
        root.GetProperty("job_id").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("workflow_id").GetString().Should().StartWith("job-");

        _mockClients.Verify(c => c.All, Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("JobStaged", It.Is<object[]>(args => args.Length == 1), default),
            Times.Once);
    }
}
