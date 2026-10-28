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

    [Fact]
    public async Task PostTrajectory_AskQuestion_SetsStatusToWaitingForInput()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Interactive Session",
            Status = "thinking",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act: agent emits ask_question tool_call
        var req = new PostMessageRequest(
            "Asking user for confirmation",
            EventType: "tool_call",
            ToolName: "ask_question",
            ToolArgsJson: "{\"questions\":[{\"question\":\"Confirm deploy?\",\"options\":[\"Yes\",\"No\"]}]}",
            SenderAgentId: "agy"
        );
        var result = await controller.PostMessage(session.Id, req);

        // Assert
        result.Should().BeOfType<CreatedAtActionResult>();
        session.Status.Should().Be("waiting_for_input");
    }

    [Fact]
    public async Task SubmitAnswer_PersistsToolResultAndPrompt_AndDispatchesPromptReceived()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Interactive Session",
            Status = "waiting_for_input",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);

        var toolCallMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "agy",
            EventType = "tool_call",
            ToolName = "ask_question",
            Content = "Which database?",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(toolCallMsg);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var req = new SubmitAnswerRequest(
            ToolCallId: toolCallMsg.Id,
            Answers: new List<QuestionAnswerDto>
            {
                new(
                    QuestionIndex: 0,
                    Question: "Which database?",
                    SelectedOptions: new List<string> { "PostgreSQL" },
                    CustomText: null
                )
            }
        );
        var result = await controller.SubmitAnswer(session.Id, req);

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        session.Status.Should().Be("thinking");

        // Tool result message created
        db.SessionMessages.Should().Contain(m =>
            m.EventType == "tool_result" &&
            m.ToolName == "ask_question" &&
            m.Content.Contains("PostgreSQL"));

        // Prompt message created
        db.SessionMessages.Should().Contain(m =>
            m.EventType == "prompt" &&
            m.Content.Contains("PostgreSQL"));

        // PromptReceived SignalR broadcast to agent-agy
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("PromptReceived", It.IsAny<object[]>(), default),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopSession_RecordsStoppedAtInMetadata_AndDropsSubsequentAgentEvents()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Active Session",
            Status = "thinking",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act 1: Stop session
        var stopResult = await controller.StopSession(session.Id);
        stopResult.Should().BeOfType<OkObjectResult>();

        session.Status.Should().Be("active");
        var stoppedAt = SessionsController.ExtractStoppedAt(session.MetadataJson);
        stoppedAt.Should().NotBeNull();

        // Act 2: Trailing agent event arrives after stop
        var trailingEvent = new PostMessageRequest("Late model thought", SenderAgentId: "agy", EventType: "thought");
        var dropResult = await controller.PostMessage(session.Id, trailingEvent);
        var dropOk = dropResult.Should().BeOfType<OkObjectResult>().Subject;
        dropOk.Value!.ToString().Should().Contain("dropped_due_to_stop");

        // DB should NOT have recorded the trailing thought
        db.SessionMessages.Should().NotContain(m => m.Content == "Late model thought");

        // Act 3: Operator sends new prompt - should clear stoppedAt and be accepted
        var newPrompt = new PostMessageRequest("New prompt from operator", SenderAgentId: "operator", TargetAgentId: "agy", EventType: "prompt");
        var promptResult = await controller.PostMessage(session.Id, newPrompt);
        promptResult.Should().BeOfType<CreatedAtActionResult>();

        var clearedStoppedAt = SessionsController.ExtractStoppedAt(session.MetadataJson);
        clearedStoppedAt.Should().BeNull();
        db.SessionMessages.Should().Contain(m => m.Content == "New prompt from operator");
    }

    [Fact]
    public async Task SyncArtifact_PersistsArtifact_AndCanBeRetrieved()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Title = "Artifact Test Session",
            Status = "active",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act 1: Sync a new markdown artifact
        var syncReq = new SyncArtifactRequest(
            Name: "architecture_plan.md",
            Content: "# Architecture Plan\n\n```mermaid\ngraph TD\nA-->B\n```",
            Summary: "System architecture and flow diagram",
            ContentType: "text/markdown"
        );
        var syncResult = await controller.SyncArtifact(session.Id, syncReq);

        // Assert 1: Sync successful and persisted
        var syncOk = syncResult.Should().BeOfType<OkObjectResult>().Subject;
        var artifactDto = syncOk.Value.Should().BeOfType<SessionArtifactDto>().Subject;
        artifactDto.Name.Should().Be("architecture_plan.md");
        artifactDto.Summary.Should().Be("System architecture and flow diagram");
        artifactDto.Content.Should().Contain("# Architecture Plan");

        // Act 2: List artifacts for session
        var listResult = await controller.GetArtifacts(session.Id);
        var listOk = listResult.Should().BeOfType<OkObjectResult>().Subject;
        var artifacts = listOk.Value.Should().BeAssignableTo<IEnumerable<SessionArtifactDto>>().Subject.ToList();
        artifacts.Should().ContainSingle(a => a.Name == "architecture_plan.md");

        // Act 3: Get specific artifact by name
        var getResult = await controller.GetArtifact(session.Id, "architecture_plan.md");
        var getOk = getResult.Should().BeOfType<OkObjectResult>().Subject;
        var fetchedDto = getOk.Value.Should().BeOfType<SessionArtifactDto>().Subject;
        fetchedDto.Content.Should().Contain("graph TD");
    }

    [Fact]
    public async Task PostMessage_MeshEffortNoArgs_InterceptedAsSystemCommand_WithoutForwardingToAgent()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Mesh Command Test",
            Status = "active",
            CreatorAgentId = "agy",
            MetadataJson = SessionsController.MergeSessionMetadata(null, null, "gemini-3.8-flash", "medium", "auto")
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act: User submits "/mesh effort"
        var req = new PostMessageRequest(
            Content: "/mesh effort",
            SenderAgentId: "operator",
            EventType: "prompt"
        );
        var result = await controller.PostMessage(session.Id, req);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        // System message persisted
        var sysMsg = db.SessionMessages.FirstOrDefault(m => m.SessionId == session.Id && m.EventType == "system");
        sysMsg.Should().NotBeNull();
        sysMsg!.Content.Should().Contain("Reasoning Effort Configuration");
        sysMsg.Content.Should().Contain("medium");

        // Should NOT have created a prompt message
        db.SessionMessages.Should().NotContain(m => m.EventType == "prompt");

        // Should NOT have sent PromptReceived
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("PromptReceived", It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Fact]
    public async Task PostMessage_BackslashMeshEffortWithArg_UpdatesEffort_AndReturnsConfirmation()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Mesh Effort Test",
            Status = "active",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act: User submits "\mesh effort max"
        var req = new PostMessageRequest(
            Content: "\\mesh effort max",
            SenderAgentId: "operator",
            EventType: "prompt"
        );
        var result = await controller.PostMessage(session.Id, req);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        var (_, _, effort, _, _) = SessionsController.ExtractSessionSettings(session.MetadataJson);
        effort.Should().Be("max");

        var sysMsg = db.SessionMessages.FirstOrDefault(m => m.SessionId == session.Id && m.EventType == "system");
        sysMsg.Should().NotBeNull();
        sysMsg!.Content.Should().Contain("max");

        _mockClientProxy.Verify(
            p => p.SendCoreAsync("PromptReceived", It.IsAny<object[]>(), default),
            Times.Never);
    }

    [Fact]
    public async Task StopSession_LaggingTurnEventsDropEvenAfterNewPromptTurnBegins()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Lagging Event Test",
            Status = "thinking",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Step 1: Stop session at T_stop
        var stopTime = DateTimeOffset.UtcNow;
        await controller.StopSession(session.Id);

        // Step 2: New prompt arrives shortly after stop
        var promptReq = new PostMessageRequest(
            Content: "New task for model",
            SenderAgentId: "operator",
            EventType: "prompt"
        );
        var promptResult = await controller.PostMessage(session.Id, promptReq);
        promptResult.Should().BeOfType<CreatedAtActionResult>();
        session.Status.Should().Be("thinking");

        // Step 3: A lagging event from the aborted turn arrives (with creation timestamp from before the stop)
        var laggingThought = new PostMessageRequest(
            Content: "Lagging model thought from aborted turn",
            SenderAgentId: "agy",
            EventType: "thought",
            CreatedAt: stopTime.AddMilliseconds(-100)
        );
        var lagResult = await controller.PostMessage(session.Id, laggingThought);
        var lagOk = lagResult.Should().BeOfType<OkObjectResult>().Subject;
        lagOk.Value!.ToString().Should().Contain("dropped_due_to_stop");

        // DB should NOT have recorded the lagging thought
        db.SessionMessages.Should().NotContain(m => m.Content == "Lagging model thought from aborted turn");
    }

    [Fact]
    public async Task DeleteSession_ExistingSession_RemovesFromDatabase_AndBroadcastsSessionDeleted()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Session to Delete",
            DriverId = "agy",
            PersonaId = "astra"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var result = await controller.DeleteSession(session.Id);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        db.AgentSessions.Should().NotContain(s => s.Id == session.Id);

        _mockClients.Verify(c => c.All, Times.AtLeastOnce);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("SessionDeleted", It.Is<object[]>(args => args.Length == 1), default),
            Times.Once);
    }

    [Fact]
    public async Task DeleteSession_NonExistentSession_ReturnsNotFound()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act
        var result = await controller.DeleteSession(Guid.NewGuid());

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task UploadAttachments_ValidImageAndMarkdown_UploadsToS3_PersistsMetadata_AndStreamsContent()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Attachment Upload Test",
            Status = "active",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var mockStorage = new Mock<AgentMesh.Core.Services.IStorageService>();
        mockStorage
            .Setup(s => s.UploadStreamAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, Stream _, string _, CancellationToken _) => $"s3://agent-mesh-artifacts/{key}");

        var controller = new SessionsController(db, _mockHubContext.Object, mockStorage.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var pngStream = new MemoryStream(pngBytes);
        var formFile = new FormFile(pngStream, 0, pngBytes.Length, "files", "architecture.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
        var formFiles = new FormFileCollection { formFile };

        // Act: Upload attachment
        var uploadResult = await controller.UploadAttachments(session.Id, formFiles);

        // Assert: Upload succeeded & S3 UploadStreamAsync called
        var okResult = uploadResult.Should().BeOfType<OkObjectResult>().Subject;
        var uploadedList = okResult.Value.Should().BeAssignableTo<List<SessionAttachmentDto>>().Subject;
        uploadedList.Should().HaveCount(1);
        var att = uploadedList[0];
        att.FileName.Should().Be("architecture.png");
        att.ContentType.Should().Be("image/png");
        att.SizeBytes.Should().Be(pngBytes.Length);
        att.S3Key.Should().StartWith($"sessions/{session.Id}/attachments/{att.Id}/architecture.png");
        att.DownloadUrl.Should().Be($"/api/sessions/{session.Id}/attachments/{att.Id}/content");

        mockStorage.Verify(
            s => s.UploadStreamAsync(
                It.Is<string>(k => k.EndsWith("/architecture.png")),
                It.IsAny<Stream>(),
                "image/png",
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Act: Stream content back via GetAttachmentContent (using S3 stream)
        mockStorage
            .Setup(s => s.DownloadStreamAsync(att.S3Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(pngBytes));

        var contentResult = await controller.GetAttachmentContent(session.Id, att.Id, download: false);
        var fileResult = contentResult.Should().BeOfType<FileStreamResult>().Subject;
        fileResult.ContentType.Should().Be("image/png");
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline;");
    }

    [Fact]
    public async Task UploadAttachments_DisallowedExtension_ReturnsBadRequest()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Invalid Attachment Test",
            Status = "active",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);
        var badBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        var formFile = new FormFile(new MemoryStream(badBytes), 0, badBytes.Length, "files", "malware.exe")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/x-msdownload"
        };
        var formFiles = new FormFileCollection { formFile };

        // Act
        var result = await controller.UploadAttachments(session.Id, formFiles);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PostMessage_And_EnqueuePrompt_WithAttachmentsAndEmptyText_UsesDefaultDirectiveAndPersistsAttachments()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Attachment-Only Prompt Test",
            Status = "active",
            CreatorAgentId = "agy"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);
        var attachment = new SessionAttachmentDto(
            Id: "att123",
            SessionId: session.Id,
            FileName: "screenshot.png",
            ContentType: "image/png",
            SizeBytes: 1024,
            S3Key: $"sessions/{session.Id}/attachments/att123/screenshot.png",
            DownloadUrl: $"/api/sessions/{session.Id}/attachments/att123/content",
            CreatedAt: DateTimeOffset.UtcNow
        );

        // Act 1: PostMessage with empty Content but non-empty Attachments
        var postResult = await controller.PostMessage(session.Id, new PostMessageRequest(
            Content: "",
            SenderAgentId: "operator",
            EventType: "prompt",
            Attachments: new List<SessionAttachmentDto> { attachment }
        ));

        // Assert 1: Default prompt supplied & attachment persisted in StructuredPayloadJson with camelCase properties
        var created = postResult.Should().BeOfType<CreatedAtActionResult>().Subject;
        var msgDto = created.Value.Should().BeOfType<SessionMessageDto>().Subject;
        msgDto.Content.Should().Be(SessionsController.DefaultAttachmentPrompt);
        msgDto.StructuredPayloadJson.Should().Contain("screenshot.png");
        msgDto.StructuredPayloadJson.Should().Contain("\"fileName\":\"screenshot.png\"");
        msgDto.StructuredPayloadJson.Should().Contain("\"id\":\"att123\"");
        msgDto.StructuredPayloadJson.Should().Contain("\"downloadUrl\":");

        // Act 2: Session is now 'thinking'; EnqueuePrompt with empty Prompt and an attachment queues item
        var queueResult = await controller.EnqueuePrompt(session.Id, new EnqueuePromptRequest(
            Prompt: "   ",
            SenderAgentId: "operator",
            SendNow: false,
            Attachments: new List<SessionAttachmentDto> { attachment }
        ));
        queueResult.Should().BeOfType<OkObjectResult>();
        var queue = SessionsController.ExtractPromptQueue(session.MetadataJson);
        queue.Should().HaveCount(1);
        queue[0].Prompt.Should().Be(SessionsController.DefaultAttachmentPrompt);
        queue[0].Attachments.Should().NotBeNull();
        queue[0].Attachments!.Should().ContainSingle(a => a.FileName == "screenshot.png");
    }

    [Fact]
    public async Task RunnerStaging_And_PromptInjection_StagesFileIntoWorkspaceAndFormatsBlock()
    {
        // Arrange
        var tempWorkDir = Path.Combine(Path.GetTempPath(), $"agentmesh-test-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkDir);
        try
        {
            var sessionId = Guid.NewGuid();
            var promptEv = new AgentMesh.Cli.Models.PromptReceivedEvent
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid(),
                AgentId = "agy",
                Prompt = "Inspect this diagram",
                WorkingDirectory = tempWorkDir,
                Attachments = new List<AgentMesh.Cli.Models.AttachmentItem>
                {
                    new()
                    {
                        Id = "att999",
                        SessionId = sessionId,
                        FileName = "diagram.png",
                        ContentType = "image/png",
                        SizeBytes = 64
                    }
                }
            };

            var mockMeshClient = new Mock<AgentMesh.Cli.Services.MeshClient>(new AgentMesh.Cli.Models.CliConfig(), new AgentMesh.Cli.Services.ConfigStore(), null);
            mockMeshClient
                .Setup(m => m.DownloadSessionAttachmentAsync(sessionId, "att999", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string _, string destPath, CancellationToken _) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.WriteAllBytes(destPath, new byte[] { 1, 2, 3, 4 });
                    return true;
                });

            // Act
            var stagedPaths = await AgentMesh.Cli.Services.AgentRunnerInstance.StagePromptAttachmentsAsync(
                promptEv,
                tempWorkDir,
                mockMeshClient.Object);

            // Assert
            stagedPaths.Should().HaveCount(1);
            stagedPaths[0].Should().EndWith(Path.Combine(".agentmesh", "attachments", "att999_diagram.png"));
            File.Exists(stagedPaths[0]).Should().BeTrue();
            promptEv.Attachments[0].LocalFilePath.Should().Be(stagedPaths[0]);

            var formattedBlock = AgentMesh.Cli.Adapters.AgentAdapterFactory.FormatAttachedFilesBlock(promptEv.Attachments);
            formattedBlock.Should().Contain("[Attached Files in Workspace]");
            formattedBlock.Should().Contain("diagram.png (image/png, 64 bytes)");
            formattedBlock.Should().Contain(stagedPaths[0]);
        }
        finally
        {
            if (Directory.Exists(tempWorkDir))
            {
                Directory.Delete(tempWorkDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PostMessage_AskQuestionToolCall_ForwardsToJobMessages_AndAnswerForwardsBack()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();

        var project = new Project
        {
            Id = "proj-qa",
            Name = "QA Project"
        };
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = "QA Job",
            GitBranchName = "mesh/job-qa"
        };
        var persona = new Persona
        {
            Id = "persona-qa",
            Name = "QA Persona",
            DriverId = "agy"
        };
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Job Turn Session",
            Status = "thinking",
            PersonaId = persona.Id,
            CreatorAgentId = "agy",
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId = job.Id.ToString(),
                projectId = project.Id
            })
        };

        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.Personas.Add(persona);
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        var questionsPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            questions = new[]
            {
                new { question = "Which DB?", options = new[] { "PG", "SQLite" }, is_multi_select = false }
            }
        });

        // Act 1: Persona posts ask_question tool_call event
        var toolCallRes = await controller.PostMessage(session.Id, new PostMessageRequest(
            Content: "Asking database preference",
            SenderAgentId: "agy",
            EventType: "tool_call",
            ToolName: "ask_question",
            ToolArgsJson: questionsPayload,
            StructuredPayloadJson: questionsPayload
        ));

        // Assert 1: Session transitions to waiting_for_input and JobMessage is created
        toolCallRes.Should().BeOfType<CreatedAtActionResult>();
        session.Status.Should().Be("waiting_for_input");

        var jobMsgs = db.JobMessages.Where(m => m.JobId == job.Id).ToList();
        jobMsgs.Should().ContainSingle();
        var qMsg = jobMsgs[0];
        qMsg.SenderType.Should().Be("persona");
        qMsg.SenderId.Should().Be(persona.Id);
        qMsg.TurnSessionId.Should().Be(session.Id);
        qMsg.StructuredPayloadJson.Should().Contain("Which DB?");

        // Act 2: Human submits answer to the question
        var answerRes = await controller.SubmitAnswer(session.Id, new SubmitAnswerRequest(
            ToolCallId: qMsg.Id,
            Answers: new List<QuestionAnswerDto>
            {
                new(0, "Which DB?", new List<string> { "PG" }, "")
            }
        ));

        // Assert 2: Session transitions back to thinking, answer is forwarded to JobMessages
        answerRes.Should().BeOfType<OkObjectResult>();
        session.Status.Should().Be("thinking");

        var allJobMsgs = db.JobMessages.Where(m => m.JobId == job.Id).OrderBy(m => m.CreatedAt).ToList();
        allJobMsgs.Should().HaveCount(2);
        var aMsg = allJobMsgs[1];
        aMsg.SenderType.Should().Be("user");
        aMsg.Content.Should().Contain("User answered question:");
        aMsg.Content.Should().Contain("Which DB? -> A: PG");
        aMsg.TurnSessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task PostMessage_WithNullBytesInToolResultOrContent_StripsNullBytesAndPersistsSuccessfully()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession { Id = Guid.NewGuid(), Title = "Sanitize Session", Status = "thinking" };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var controller = new SessionsController(db, _mockHubContext.Object);

        // Act: post event with null bytes in multiple string fields
        var req = new PostMessageRequest(
            Content: "Output\0content\0test",
            SenderAgentId: "rook",
            TargetAgentId: "operator",
            EventType: "tool_result",
            ToolName: "read_file\0",
            ToolArgsJson: "{\"path\":\0\"test.png\"}",
            ToolResultJson: "Binary\0Data\0Line1\nLine2"
        );

        var result = await controller.PostMessage(session.Id, req);

        // Assert
        result.Should().BeOfType<CreatedAtActionResult>();
        var msg = db.SessionMessages.FirstOrDefault(m => m.SessionId == session.Id && m.EventType == "tool_result");
        msg.Should().NotBeNull();
        msg!.Content.Should().Be("Outputcontenttest");
        msg.ToolName.Should().Be("read_file");
        msg.ToolArgsJson.Should().Be("{\"path\":\"test.png\"}");
        msg.ToolResultJson.Should().Be("BinaryDataLine1\nLine2");
        msg.ToolResultJson.Should().NotContain("\0");
    }

    [Fact]
    public async Task AgentMeshDbContext_SanitizesNullBytes_OnAnyEntityStringProperty()
    {
        using var db = TestDbContextFactory.Create();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Corrupt\0Title\0WithNulls",
            MetadataJson = "{\"key\": \"val\0ue\"}"
        };

        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();

        var saved = await db.AgentSessions.FindAsync(session.Id);
        saved.Should().NotBeNull();
        saved!.Title.Should().Be("CorruptTitleWithNulls");
        saved.MetadataJson.Should().Be("{\"key\": \"value\"}");
        saved.Title.Should().NotContain("\0");
        saved.MetadataJson.Should().NotContain("\0");
    }
}


