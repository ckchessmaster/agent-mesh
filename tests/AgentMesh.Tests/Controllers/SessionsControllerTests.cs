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
            mode: "plan",
            tier: "intelligence");

        var (workDir, model, effort, mode, tier) = SessionsController.ExtractSessionSettings(merged);

        workDir.Should().Be("/tmp/custom-workdir");
        model.Should().Be("gemini-2.5-pro");
        effort.Should().Be("high");
        mode.Should().Be("plan");
        tier.Should().Be("intelligence");
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

    [Fact]
    public async Task StopSession_SetsStatusActive_AndBroadcastsStopExecution()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Busy Session",
            Status = "thinking",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var result = await controller.StopSession(session.Id);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        session.Status.Should().Be("active");

        // Broadcasted StopExecution signal
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("StopExecution", It.IsAny<object[]>(), default),
            Times.AtLeastOnce);

        // Saved audit message
        db.SessionMessages.Should().ContainSingle(m => m.SessionId == session.Id && m.Content.Contains("stopped by operator"));
    }

    [Fact]
    public async Task StopCommand_BroadcastsStopCommand_AndAddsAuditMessage()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Command Session",
            Status = "thinking",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var result = await controller.StopCommand(session.Id, "cmd-99");

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        _mockClientProxy.Verify(
            p => p.SendCoreAsync("StopCommand", It.IsAny<object[]>(), default),
            Times.AtLeastOnce);

        db.SessionMessages.Should().ContainSingle(m => m.SessionId == session.Id && m.Content.Contains("cmd-99"));
    }

    [Fact]
    public async Task EnqueuePrompt_AddsToQueue_WhenNotSendNow()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Queue Test Session",
            Status = "thinking",
            CreatorAgentId = "agy",
            MetadataJson = "{}"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var req = new EnqueuePromptRequest("Follow up question", SendNow: false);
        var result = await controller.EnqueuePrompt(session.Id, req);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        var queue = SessionsController.ExtractPromptQueue(session.MetadataJson);
        queue.Should().HaveCount(1);
        queue[0].Prompt.Should().Be("Follow up question");

        // Broadcasted QueueUpdated
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("QueueUpdated", It.IsAny<object[]>(), default),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task EnqueuePrompt_DispatchesImmediately_WhenSendNow()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Send Now Session",
            Status = "thinking",
            CreatorAgentId = "copilot",
            MetadataJson = "{}"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var req = new EnqueuePromptRequest("Urgent override instruction", SendNow: true);
        var result = await controller.EnqueuePrompt(session.Id, req);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        session.Status.Should().Be("thinking");

        // Broadcasted PromptReceived
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("PromptReceived", It.IsAny<object[]>(), default),
            Times.AtLeastOnce);

        db.SessionMessages.Should().Contain(m => m.Content == "Urgent override instruction");
    }

    [Fact]
    public async Task PostMessage_AutoAdvancesQueuedPrompt_WhenTurnCompletes()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var queuedItem = new QueuedPromptDto(
            Id: "q-1",
            Prompt: "Next queued task",
            Sender: "operator",
            CreatedAt: DateTimeOffset.UtcNow
        );
        var initialMetadata = SessionsController.UpdatePromptQueueMetadata("{}", new List<QueuedPromptDto> { queuedItem });

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Auto-advance Session",
            Status = "thinking",
            CreatorAgentId = "agy",
            MetadataJson = initialMetadata
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act: agent completes turn by posting response event
        var req = new PostMessageRequest("Turn 1 complete response", EventType: "response", SenderAgentId: "agy");
        var result = await controller.PostMessage(session.Id, req);

        // Assert
        result.Should().BeOfType<CreatedAtActionResult>();

        // Queue should now be empty (item was auto-dispatched)
        var queue = SessionsController.ExtractPromptQueue(session.MetadataJson);
        queue.Should().BeEmpty();

        // Dispatched PromptReceived for the queued prompt
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("PromptReceived", It.IsAny<object[]>(), default),
            Times.AtLeastOnce);

        // New prompt message created in session
        db.SessionMessages.Should().Contain(m => m.Content == "Next queued task" && m.EventType == "prompt");
    }
}

