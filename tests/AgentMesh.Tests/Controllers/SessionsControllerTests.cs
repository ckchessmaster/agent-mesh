using System.Security.Claims;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class SessionsControllerTests
{
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<IGroupManager> _mockGroups;

    public SessionsControllerTests()
    {
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockGroups = new Mock<IGroupManager>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _mockHubContext.Setup(h => h.Groups).Returns(_mockGroups.Object);
    }

    [Fact]
    public async Task CreateSession_PersistsSession_AndBroadcastsSignalR()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var req = new CreateSessionRequest("Autonomous Ops Session", "{\"goal\":\"deploy\"}");
        var result = await controller.CreateSession(req);

        // Assert
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var session = createdResult.Value.Should().BeOfType<AgentSession>().Subject;
        session.Title.Should().Be("Autonomous Ops Session");
        session.Status.Should().Be("active");

        db.AgentSessions.Should().ContainSingle(s => s.Id == session.Id);

        _mockClients.Verify(c => c.All, Times.Once);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("SessionCreated", It.Is<object[]>(args => args.Length == 1), default),
            Times.Once);
    }

    [Fact]
    public async Task PostMessage_PersistsMessage_AndBroadcastsToGroupAndAll()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession { Id = Guid.NewGuid(), Title = "Active Session" };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "operator-user")
                    }, "TestAuth"))
                }
            }
        };

        // Act
        var req = new PostMessageRequest(
            Content: "Proceed with staging step 3",
            SenderAgentId: "gemini-spark",
            TargetAgentId: "worker-agent");

        var result = await controller.PostMessage(session.Id, req);

        // Assert
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var msg = createdResult.Value.Should().BeOfType<SessionMessageDto>().Subject;
        msg.Content.Should().Be("Proceed with staging step 3");
        msg.SenderAgentId.Should().Be("gemini-spark");
        msg.TargetAgentId.Should().Be("worker-agent");

        db.SessionMessages.Should().ContainSingle(m => m.Id == msg.Id);

        // Broadcasted to session group and all
        _mockClients.Verify(c => c.Group($"session-{session.Id}"), Times.Once);
        _mockClients.Verify(c => c.All, Times.Once);
    }

    [Fact]
    public async Task GetMessages_ReturnsSessionMessages_OrderedByCreatedAt()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var sessionId = Guid.NewGuid();
        var session = new AgentSession { Id = sessionId, Title = "Session For Messages" };
        db.AgentSessions.Add(session);

        var m1 = new SessionMessage
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            SenderAgentId = "agent-1",
            Content = "First message",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2)
        };
        var m2 = new SessionMessage
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            SenderAgentId = "agent-2",
            Content = "Second message",
            CreatedAt = DateTime.UtcNow
        };
        db.SessionMessages.AddRange(m1, m2);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var result = await controller.GetMessages(sessionId);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var msgs = okResult.Value.Should().BeAssignableTo<IEnumerable<SessionMessageDto>>().Subject.ToList();
        msgs.Should().HaveCount(2);
        msgs[0].Content.Should().Be("First message");
        msgs[1].Content.Should().Be("Second message");
    }

    [Fact]
    public void MergeAndExtractSessionSettings_HandlesExplicitSettings_Correctly()
    {
        var merged = SessionsController.MergeSessionMetadata(
            existingJson: "{\"custom\":\"key\"}",
            workingDirectory: "/tmp/custom-workdir",
            model: "gemini-2.5-pro",
            effort: "high",
            mode: "plan");

        var (workDir, model, effort, mode) = SessionsController.ExtractSessionSettings(merged);

        workDir.Should().Be("/tmp/custom-workdir");
        model.Should().Be("gemini-2.5-pro");
        effort.Should().Be("high");
        mode.Should().Be("plan");
        merged.Should().Contain("\"custom\":\"key\"");
    }

    [Fact]
    public void AccumulateUsageMetadata_AggregatesTokensAndDuration_OverMultipleTurns()
    {
        var turn1Payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            usage = new
            {
                inputTokens = 1000,
                outputTokens = 150,
                thinkingTokens = 40,
                totalTokens = 1150,
                durationSeconds = 1.5
            }
        });

        var turn2Payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            usage = new
            {
                inputTokens = 500,
                outputTokens = 100,
                thinkingTokens = 20,
                totalTokens = 600,
                durationSeconds = 2.0
            }
        });

        var metaAfterTurn1 = SessionsController.AccumulateUsageMetadata("{}", turn1Payload);
        var metaAfterTurn2 = SessionsController.AccumulateUsageMetadata(metaAfterTurn1, turn2Payload);

        using var doc = System.Text.Json.JsonDocument.Parse(metaAfterTurn2);
        var cumulative = doc.RootElement.GetProperty("cumulativeUsage");

        cumulative.GetProperty("inputTokens").GetInt64().Should().Be(1500);
        cumulative.GetProperty("outputTokens").GetInt64().Should().Be(250);
        cumulative.GetProperty("thinkingTokens").GetInt64().Should().Be(60);
        cumulative.GetProperty("totalTokens").GetInt64().Should().Be(1750);
        cumulative.GetProperty("totalDurationSeconds").GetDouble().Should().BeApproximately(3.5, 0.01);
    }
}
