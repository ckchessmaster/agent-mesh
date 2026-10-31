using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Tools;

/// <summary>
/// The platform tools against a real database and a recorded SignalR hub.
/// </summary>
/// <remarks>
/// <para>
/// These construct <see cref="PlatformMcpTools"/> directly with the in-memory provider, which is
/// how the rest of the tool suite works. Two things that cannot be faked are deliberately real:
/// the database (so a persisted row is read back from the same store the Cockpit will query, not
/// from the object the tool happened to hold) and the hub recorder (so the event name and payload
/// the Cockpit subscribes to are asserted rather than assumed).
/// </para>
/// <para>
/// The context resolver is stubbed. Its own resolution rules are covered by
/// <see cref="PlatformCallContextResolverTests"/>; stubbing it here keeps each test asserting one
/// thing about the tool rather than re-testing header precedence on every case.
/// </para>
/// </remarks>
public class PlatformMcpToolsTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private readonly Mock<IHubContext<AgentMeshHub>> _hub = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly Mock<IGitHubPullRequestService> _pullRequests = new();
    private readonly List<SentEvent> _sent = new();

    public PlatformMcpToolsTests()
    {
        _clients.Setup(c => c.All).Returns(_clientProxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        _hub.Setup(h => h.Clients).Returns(_clients.Object);

        // IClientProxy.SendCoreAsync is what every SendAsync overload funnels into, so recording
        // here catches the event name and the payload argument for both the group and the broadcast
        // path without needing to know which overload the production code reached for.
        _clientProxy
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns<string, object?[], CancellationToken>((name, args, _) =>
            {
                _sent.Add(new SentEvent(name, args.Length > 0 ? args[0] : null));
                return Task.CompletedTask;
            });
    }

    // ── post_job_message ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PostJobMessage_WritesJobChatMessage_AndBroadcastsSignalR()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, tasks: null, personas: ["astra", "sorren"]);
        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", sessionId: Guid.NewGuid()));

        var json = await tools.PostJobMessage("Build is green, ready for review.", "astra", requires_reply: false);

        var result = Parse(json);
        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());

        // The spec named public.session_messages; the shared Job Chat the Cockpit renders is
        // job_messages, and that is the ledger this writes to. Asserting the row lands in the
        // table the reader actually reads is the whole point of the deviation.
        var stored = await ReadBackAsync(dbName, d => d.JobMessages.Where(m => m.JobId == job.Id).ToListAsync());
        var message = stored.Should().ContainSingle().Subject;

        message.SenderType.Should().Be("persona");
        message.SenderId.Should().Be("rook");
        message.Content.Should().Be("@astra Build is green, ready for review.");
        message.MentionsJson.Should().Contain("astra");
        message.StructuredPayloadJson.Should().NotBeNull();

        var payload = JsonDocument.Parse(message.StructuredPayloadJson!).RootElement;
        payload.GetProperty("requiresReply").GetBoolean().Should().BeFalse();
        payload.GetProperty("targetPersona").GetString().Should().Be("astra");

        // The event name the React Cockpit already subscribes to (App.tsx: JobMessageCreated).
        _sent.Should().Contain(e => e.Name == "JobMessageCreated");
        _sent.Single(e => e.Name == "JobMessageCreated").Payload!.GetType().Name
            .Should().Be("JobMessageDto",
                "the Cockpit's handler is typed against the controller's DTO; a different shape " +
                "renders an empty message row rather than failing loudly");
    }

    [Fact]
    public async Task PostJobMessage_LinkedToTheTurnSession_TheCockpitCanAttributeItToARunner()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra"]);
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(db, sessionId, job.Id, personaId: "rook");

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", sessionId: sessionId));
        await tools.PostJobMessage("Question about the schema.", "astra", requires_reply: false);

        var stored = await ReadBackAsync(dbName, d => d.JobMessages.SingleAsync(m => m.JobId == job.Id));
        stored.TurnSessionId.Should().Be(sessionId,
            "JobCockpit resolves the sending runner off turnSessionId; without it the message has no avatar");
    }

    [Fact]
    public async Task PostJobMessage_RequiresReply_ParksTheSessionAsWaitingForInput()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["operator"]);
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(db, sessionId, job.Id, personaId: "rook", status: "thinking");

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", sessionId: sessionId));

        var result = Parse(await tools.PostJobMessage(
            "Blocked on a schema decision. @sorren needs to weigh in.", "operator", requires_reply: true));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("requiresReply").GetBoolean().Should().BeTrue();
        result.GetProperty("sessionStatus").GetString().Should().Be(PlatformMcpTools.WaitingForInputStatus);

        var session = await ReadBackAsync(dbName, d => d.AgentSessions.SingleAsync(s => s.Id == sessionId));
        session.Status.Should().Be("waiting_for_input",
            "the Cockpit and Job D's wait gate both key off this exact string, not 'AwaitingInput'");

        _sent.Should().Contain(e => e.Name == "SessionUpdated",
            "the runner's status chip must flip without a page refresh");
    }

    // ── Durable-workflow routing (escalation release) ────────────────────────────
    //
    // An orchestrated agent parks in Temporal's history, not in a runner, so nothing polling the
    // session table will ever notice a reply. The other persona answers it through THIS tool — not
    // through the REST endpoint a human uses — so if routing lives only on the REST path, every
    // cross-persona escalation (the normal shape of two-man review here) is undeliverable and the task
    // sits for the full seven-day gate.

    [Fact]
    public async Task PostJobMessage_RoutesTheReplyIntoTheParkedWorkflow()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra", "rook"]);

        var router = new RecordingRouter();
        var tools = BuildTools(
            db, Context(jobId: job.Id, personaId: "astra", agentId: "astra-driver"), router);

        await tools.PostJobMessage("Use OIDC, not passwords. Here is the spec.", "rook",
            requires_reply: false);

        router.ChatSends.Should().HaveCount(1,
            "a persona-to-persona message is how an escalation actually gets answered; not routing it " +
            "leaves the workflow parked while the answer sits in the ledger");
        router.ChatSends[0].jobId.Should().Be(job.Id);
        router.ChatSends[0].signal.SenderId.Should().Be("astra",
            "the workflow's self-check compares against this id; sending the agent id or a blank would " +
            "either mis-attribute the answer or defeat the check");
        router.ChatSends[0].signal.Content.Should().Contain("OIDC");
    }

    /// <summary>
    /// Routing is not gated on <c>requires_reply</c>, because the flag cannot distinguish the cases.
    /// </summary>
    /// <remarks>
    /// The obvious rule — "route only messages that ask for a reply" — is backwards here. A
    /// <c>requires_reply: true</c> from the orchestrated persona is that persona <em>raising</em> an
    /// escalation, which must not release its own gate; a plain progress-free answer from another persona
    /// carries no flag at all, because <c>post_job_message</c> has no "in reply to" field. So the flag
    /// selects the wrong set in both directions. Everything is offered and the workflow decides, which is
    /// the only component that knows who the run belongs to.
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_RoutesEvenWithoutRequiresReply()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["sorren", "rook"]);

        var router = new RecordingRouter();
        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "sorren"), router);

        await tools.PostJobMessage("Architecture sign-off: approved, merge it.", "rook",
            requires_reply: false);

        router.ChatSends.Should().HaveCount(1,
            "the answer to an escalation carries no requires_reply flag; gating on it would drop the " +
            "only message that can release the gate");
    }

    /// <summary>
    /// A Temporal outage must not make a delivered message look undeliverable to the agent that sent it.
    /// </summary>
    /// <remarks>
    /// The message is committed and broadcast before routing runs. Surfacing a routing failure as a tool
    /// error would invite the agent to re-post, duplicating the ledger entry for a message the human
    /// already saw — and an agent that believes its escalation failed starts escalating harder.
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_WhenRoutingThrows_StillReportsSuccess()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra", "rook"]);

        var tools = BuildTools(
            db,
            Context(jobId: job.Id, personaId: "astra", sessionId: Guid.NewGuid()),
            new ThrowingRouter());

        var result = Parse(await tools.PostJobMessage(
            "Answering your escalation.", "rook", requires_reply: true));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("messageId").GetGuid().Should().NotBeEmpty();

        var stored = await ReadBackAsync(dbName, d => d.JobMessages.SingleAsync(m => m.JobId == job.Id));
        stored.Content.Should().Contain("Answering your escalation.");
    }

    /// <summary>
    /// The dedup key is the persisted row id, matching the REST path.
    /// </summary>
    /// <remarks>
    /// A retried MCP call re-sends the same id so the workflow drops the duplicate instead of rendering
    /// the same answer twice, which reads to the model as the human repeating themselves.
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_UsesThePersistedRowIdAsTheDedupKey()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra", "rook"]);

        var router = new RecordingRouter();
        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "astra"), router);

        var result = Parse(await tools.PostJobMessage("Dedup me.", "rook", requires_reply: false));

        var messageId = result.GetProperty("messageId").GetGuid();
        router.ChatSends.Should().ContainSingle();
        router.ChatSends[0].signal.MessageId.Should().Be(messageId.ToString(),
            "the signal must carry the id of the row that was actually written, or the workflow's " +
            "duplicate window cannot match a redelivery against it");
    }

    /// <summary>
    /// With no router wired, the tool behaves exactly as before orchestration existed.
    /// </summary>
    /// <remarks>
    /// Pins the optional-parameter contract: the ~60 tests in this file construct the tool without a
    /// router, and production registers one (pinned by <c>ApiServiceRegistrationTests</c>). A null here
    /// must be a silent no-op, not a null reference.
    /// </remarks>
    [Fact]
    public async Task PostJobMessage_WithoutARouter_StillPersistsAndBroadcasts()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra", "rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "astra"), workflows: null);

        var result = Parse(await tools.PostJobMessage("No router here.", "rook", requires_reply: false));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        _sent.Should().Contain(e => e.Name == "JobMessageCreated");
    }

    [Fact]
    public async Task PostJobMessage_WhenNotRequiresReply_AndWorkflowRouterPresent_SignalsWorkflow()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra", "rook"]);
        var routerMock = new Mock<IWorkflowSignalRouter>();
        routerMock.Setup(r => r.SendChatMessageAsync(job.Id, It.IsAny<ChatMessageSignal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalDelivery($"task-execution-{job.Id:N}", SignalDeliveryStatus.Delivered));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "astra"), workflows: routerMock.Object);
        var json = await tools.PostJobMessage("Architecture reviewed; all looks good.", "rook", requires_reply: false);

        var result = Parse(json);
        result.GetProperty("success").GetBoolean().Should().BeTrue();

        routerMock.Verify(r => r.SendChatMessageAsync(
            job.Id,
            It.Is<ChatMessageSignal>(s => s.SenderId == "astra" && s.Content.Contains("Architecture reviewed")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostJobMessage_WhenRequiresReply_SignalsWorkflow()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra", "rook"]);
        var routerMock = new Mock<IWorkflowSignalRouter>();
        routerMock.Setup(r => r.SendChatMessageAsync(job.Id, It.IsAny<ChatMessageSignal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalDelivery($"task-execution-{job.Id:N}", SignalDeliveryStatus.Delivered));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"), workflows: routerMock.Object);
        var json = await tools.PostJobMessage("Do we use postgres or sqlite?", "astra", requires_reply: true);

        var result = Parse(json);
        result.GetProperty("success").GetBoolean().Should().BeTrue();

        routerMock.Verify(r => r.SendChatMessageAsync(
            job.Id,
            It.Is<ChatMessageSignal>(s => s.SenderId == "rook" && s.Content.Contains("Do we use postgres")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostJobMessage_WithoutRequiresReply_LeavesTheSessionRunning()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra"]);
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(db, sessionId, job.Id, personaId: "rook", status: "thinking");

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", sessionId: sessionId));
        await tools.PostJobMessage("Progress update only.", "astra", requires_reply: false);

        var session = await ReadBackAsync(dbName, d => d.AgentSessions.SingleAsync(s => s.Id == sessionId));
        session.Status.Should().Be("thinking", "a progress note must not park a turn that is still working");

        _sent.Should().NotContain(e => e.Name == "SessionUpdated");
    }

    [Fact]
    public async Task PostJobMessage_TargetingTheOperator_IsAlwaysPermitted()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        // No ProjectPersona rows at all: a fresh deployment where an agent's first act is to ask a
        // human for help must still work.
        var job = await SeedJobAsync(db);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));
        var result = Parse(await tools.PostJobMessage("Who should review this?", "operator", requires_reply: true));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());
        result.GetProperty("targetPersona").GetString().Should().Be("operator");
    }

    [Fact]
    public async Task PostJobMessage_TargetingAnUnassignedPersona_IsRefusedWithTheValidOptions()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra", "sorren"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.PostJobMessage("Hey, are you there?", "vane", requires_reply: false));

        // The failure mode this guards: a message to a persona that is not on the project is
        // delivered to a table nobody polls. The agent believes it escalated.
        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("unknown_target_persona");
        result.GetProperty("message").GetString().Should().Contain("vane").And.Contain("astra").And.Contain("sorren");

        (await ReadBackAsync(dbName, d => d.JobMessages.CountAsync(m => m.JobId == job.Id))).Should().Be(0,
            "a refused message must not half-write itself into the ledger");
        _sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData("@astra please review the migration", "@astra please review the migration")]
    [InlineData("@Astra, can you look?", "@Astra, can you look?")]
    [InlineData("please review", "@astra please review")]
    [InlineData("  spaced out  ", "@astra spaced out")]
    public async Task PostJobMessage_AddsTheMentionOnceAndNeverDoublesIt(string content, string expected)
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra"]);
        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        await tools.PostJobMessage(content, "astra", requires_reply: false);

        var stored = await ReadBackAsync(dbName, d => d.JobMessages.SingleAsync(m => m.JobId == job.Id));
        stored.Content.Should().Be(expected);
        stored.Content.Should().NotMatch(@"^@astra @astra", "a doubled mention renders as noise in the chat");
    }

    [Fact]
    public async Task PostJobMessage_WithNoResolvableIdentity_IsRefused()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: null, agentId: null));

        var result = Parse(await tools.PostJobMessage("Hello?", "astra", requires_reply: false));

        result.GetProperty("error").GetString().Should().Be("unauthorized");
        var refusal = result.GetProperty("message").GetString();
        refusal.Should().Match(m => m.Contains("X-Agent-Id") || m.Contains("X-Persona-Id"),
            "the refusal must name a header the caller can actually set");
    }

    [Fact]
    public async Task PostJobMessage_WithBlankContent_IsRefusedBeforeAnyLookup()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tools = BuildTools(db, Context(jobId: Guid.NewGuid()));

        var result = Parse(await tools.PostJobMessage("   ", "astra", requires_reply: false));

        result.GetProperty("error").GetString().Should().Be("empty_content");
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task PostJobMessage_WhenTheJobCannotBeResolved_ExplainsEveryWayToSupplyIt()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tools = BuildTools(db, Context(jobId: null));

        var result = Parse(await tools.PostJobMessage("Hello?", "astra", requires_reply: false));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("job_context_missing");

        // The deadlock this message exists to prevent: an agent with no job context burning turns
        // guessing which header it forgot. All three routes are named in one response.
        var message = result.GetProperty("message").GetString();
        message.Should().Contain("X-Session-Id").And.Contain("X-Job-Id").And.Contain("job_id");
    }

    [Fact]
    public async Task PostJobMessage_WithAnExplicitJobId_UsesItAndReportsTheSource()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["astra"]);

        // The resolver knows nothing; the argument is the only source of the job.
        var tools = BuildTools(db, Context(jobId: null, personaId: "rook"));

        var result = Parse(await tools.PostJobMessage("Hi", "astra", false, job.Id.ToString()));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());
        result.GetProperty("resolvedFrom").GetProperty("job").GetString().Should().Be("argument:job_id");
    }

    [Fact]
    public async Task PostJobMessage_WithANonGuidJobId_IsRefused()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tools = BuildTools(db, Context(jobId: null, personaId: "rook"));

        var result = Parse(await tools.PostJobMessage("Hi", "astra", false, "not-a-guid"));

        result.GetProperty("error").GetString().Should().Be("invalid_job_id");
    }

    [Fact]
    public async Task PostJobMessage_WithAJobIdThatDoesNotExist_IsRefused()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tools = BuildTools(db, Context(jobId: null, personaId: "rook"));

        var result = Parse(await tools.PostJobMessage("Hi", "astra", false, Guid.NewGuid().ToString()));

        result.GetProperty("error").GetString().Should().Be("job_not_found");
    }

    // ── manage_task ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManageTask_ListAll_ReturnsTheSequentialChecklistWithStatuses()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new() { Title = "Contracts", AssignedPersonaId = "rook", Status = JobTaskStatus.Completed },
            new() { Title = "PR service", AssignedPersonaId = "rook", Status = JobTaskStatus.InProgress },
            new() { Title = "Tests", AssignedPersonaId = "vane" },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook", "vane"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("list_all"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        var listed = result.GetProperty("tasks").EnumerateArray().ToList();
        listed.Should().HaveCount(3);

        // position is 1-based because it is the number the agent sees in its own prompt checklist.
        listed[0].GetProperty("position").GetInt32().Should().Be(1);
        listed[0].GetProperty("status").GetString().Should().Be("completed");
        listed[1].GetProperty("status").GetString().Should().Be("in_progress");
        listed[2].GetProperty("status").GetString().Should().Be("todo",
            "a legacy task with no explicit status derives 'todo' from IsCompleted=false");
        listed[2].GetProperty("statusExplicit").GetBoolean().Should().BeFalse();
        listed[2].GetProperty("id").GetString().Should().Be(tasks[2].Id.ToString());
    }

    [Fact]
    public async Task ManageTask_GetDetails_ReturnsTitleAssigneeAndStatus()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new() { Title = "Contract definitions", AssignedPersonaId = "rook", Status = JobTaskStatus.InProgress },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("get_details", task_id: tasks[0].Id.ToString()));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        var task = result.GetProperty("task");
        task.GetProperty("title").GetString().Should().Be("Contract definitions");
        task.GetProperty("assignedPersonaId").GetString().Should().Be("rook");
        task.GetProperty("status").GetString().Should().Be("in_progress");
    }

    [Fact]
    public async Task ManageTask_SetStatus_UpdatesDatabase_AndEmitsEvent()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new() { Title = "PR service", AssignedPersonaId = "rook", Status = JobTaskStatus.InProgress },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask(
            "set_status", task_id: tasks[0].Id.ToString(), status: "awaiting_review", notes: "PR opened, awaiting Sorren"));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());
        result.GetProperty("previousStatus").GetString().Should().Be("in_progress");
        result.GetProperty("status").GetString().Should().Be("awaiting_review");

        var persisted = await ReadBackAsync(dbName, d => d.ProjectJobs.SingleAsync(j => j.Id == job.Id));
        var storedTask = ProjectJobsController.ParseTasks(persisted.TasksJson).Single();
        storedTask.Status.Should().Be("awaiting_review");
        storedTask.StatusNotes.Should().Be("PR opened, awaiting Sorren");
        storedTask.StatusChangedAt.Should().NotBeNull();
        storedTask.IsCompleted.Should().BeFalse("only 'completed' satisfies the checkbox the Cockpit draws");

        // The event name the Cockpit already handles (App.tsx: JobTaskUpdated), carrying the same
        // { jobId, tasks } shape the controller's task endpoints send.
        var broadcast = _sent.Single(e => e.Name == "JobTaskUpdated");
        JsonDocument.Parse(JsonSerializer.Serialize(broadcast.Payload)).RootElement
            .GetProperty("jobId").GetString().Should().Be(job.Id.ToString());
    }

    [Fact]
    public async Task ManageTask_SetStatusCompleted_AlsoSetsTheLegacyCompletedFlag()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new() { Title = "Tests", AssignedPersonaId = "rook", Status = JobTaskStatus.AwaitingReview },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));
        await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "completed");

        var persisted = await ReadBackAsync(dbName, d => d.ProjectJobs.SingleAsync(j => j.Id == job.Id));
        var storedTask = ProjectJobsController.ParseTasks(persisted.TasksJson).Single();

        // The React Cockpit renders IsCompleted, not Status. If the two could disagree, an agent
        // would report a task done while the checklist still showed it open.
        storedTask.IsCompleted.Should().BeTrue();
        storedTask.CompletedAt.Should().NotBeNull();
        storedTask.EffectiveStatus.Should().Be(JobTaskStatus.Completed);
    }

    [Fact]
    public async Task ManageTask_ReopeningAClearedTask_UnsetsTheCompletedFlag()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new()
            {
                Title = "Tests",
                AssignedPersonaId = "rook",
                Status = JobTaskStatus.Completed,
                IsCompleted = true,
                CompletedAt = DateTimeOffset.UtcNow,
            },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "in_progress"));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());

        var persisted = await ReadBackAsync(dbName, d => d.ProjectJobs.SingleAsync(j => j.Id == job.Id));
        var reopened = ProjectJobsController.ParseTasks(persisted.TasksJson).Single();
        reopened.IsCompleted.Should().BeFalse();
        reopened.CompletedAt.Should().BeNull("a reopened task that still reports CompletedAt is a lie about history");
    }

    [Fact]
    public async Task ManageTask_RejectsAnInvalidStatusEnum_AndListsTheAllowedValues()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto> { new() { Title = "PR service", AssignedPersonaId = "rook" } };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "shipped"));

        result.GetProperty("error").GetString().Should().Be("invalid_status");
        result.GetProperty("message").GetString().Should().Contain("shipped")
            .And.Contain("in_progress").And.Contain("awaiting_review");
        result.GetProperty("details").GetProperty("allowedStatuses").GetArrayLength().Should().Be(4);

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ManageTask_RejectsAnInvalidAction()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"]);
        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("delete_everything"));

        result.GetProperty("error").GetString().Should().Be("invalid_action");
        result.GetProperty("message").GetString().Should().Contain("get_details")
            .And.Contain("set_status").And.Contain("list_all");
    }

    [Fact]
    public async Task ManageTask_RejectsSkippingTheReviewGate_AndNamesTheLegalNextStates()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto> { new() { Title = "PR service", AssignedPersonaId = "rook" } };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "completed"));

        result.GetProperty("error").GetString().Should().Be("invalid_transition");
        result.GetProperty("details").GetProperty("currentStatus").GetString().Should().Be("todo");
        result.GetProperty("details").GetProperty("allowedNext").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("todo", "in_progress");

        var persisted = await ReadBackAsync(dbName, d => d.ProjectJobs.SingleAsync(j => j.Id == job.Id));
        ProjectJobsController.ParseTasks(persisted.TasksJson).Single().IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ManageTask_ReplayingTheSameStatus_IsAnIdempotentSuccess()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new() { Title = "PR service", AssignedPersonaId = "rook", Status = JobTaskStatus.InProgress },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "in_progress"));

        // A model that retries a call whose response it lost must not be told it failed, or it will
        // conclude the transition is blocked and start working around the state machine.
        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());
        result.GetProperty("previousStatus").GetString().Should().Be("in_progress");
    }

    [Fact]
    public async Task ManageTask_CannotMoveAnotherPersonasTask()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new() { Title = "SignalR contract review", AssignedPersonaId = "sorren", Status = JobTaskStatus.InProgress },
            new() { Title = "My own task", AssignedPersonaId = "rook" },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook", "sorren"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "completed"));

        result.GetProperty("error").GetString().Should().Be("task_owned_by_other_persona");
        result.GetProperty("message").GetString().Should().Contain("@sorren").And.Contain("@rook");

        // The refusal must point at the caller's own work, or the agent's next move is to guess.
        var suggested = result.GetProperty("details").GetProperty("yourTasks").EnumerateArray().ToList();
        suggested.Should().ContainSingle().Subject.GetProperty("title").GetString().Should().Be("My own task");

        var persisted = await ReadBackAsync(dbName, d => d.ProjectJobs.SingleAsync(j => j.Id == job.Id));
        ProjectJobsController.ParseTasks(persisted.TasksJson).First(t => t.Id == tasks[0].Id)
            .EffectiveStatus.Should().Be("in_progress");
    }

    [Fact]
    public async Task ManageTask_ClaimsAnUnassignedTaskForTheCaller()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto> { new() { Title = "Unowned chore" } };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "in_progress"));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());

        var persisted = await ReadBackAsync(dbName, d => d.ProjectJobs.SingleAsync(j => j.Id == job.Id));
        ProjectJobsController.ParseTasks(persisted.TasksJson).Single().AssignedPersonaId
            .Should().Be("rook", "claiming work by starting it is the intended way to take a task");
    }

    [Fact]
    public async Task ManageTask_WithNoTaskId_FallsBackToTheCallersFocusTask()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto>
        {
            new() { Title = "Someone else's", AssignedPersonaId = "sorren" },
            new() { Title = "Rook's focus", AssignedPersonaId = "rook" },
        };
        var job = await SeedJobAsync(db, tasks, personas: ["rook", "sorren"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", status: "in_progress"));

        // The control plane already tells the agent which task is its focus when it formats the
        // checklist into the prompt; honouring the same rule means the agent does not have to
        // thread an id it was never given.
        result.GetProperty("task").GetProperty("title").GetString().Should().Be("Rook's focus");
    }

    [Fact]
    public async Task ManageTask_WithAnUnknownTaskId_ListsWhatActuallyExists()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto> { new() { Title = "Real task", AssignedPersonaId = "rook" } };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var result = Parse(await tools.ManageTask("set_status", task_id: Guid.NewGuid().ToString(), status: "in_progress"));

        result.GetProperty("error").GetString().Should().Be("task_not_found");
        result.GetProperty("message").GetString().Should().Contain("Real task").And.Contain(tasks[0].Id.ToString());
    }

    [Fact]
    public async Task ManageTask_OnAJobWithNoTasks_SaysSoRatherThanReturningAnEmptyList()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, tasks: null, personas: ["rook"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook"));

        var listed = Parse(await tools.ManageTask("list_all"));
        listed.GetProperty("success").GetBoolean().Should().BeTrue();
        listed.GetProperty("taskCount").GetInt32().Should().Be(0);

        var detail = Parse(await tools.ManageTask("get_details"));
        detail.GetProperty("error").GetString().Should().Be("no_tasks");
    }

    [Fact]
    public async Task ManageTask_CannotMoveAnotherPersonasTaskEvenWhenTheCallerIsAnonymous()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto> { new() { Title = "Sorren's", AssignedPersonaId = "sorren" } };
        var job = await SeedJobAsync(db, tasks, personas: ["sorren"]);

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: null, agentId: null));

        var result = Parse(await tools.ManageTask("set_status", task_id: tasks[0].Id.ToString(), status: "completed"));

        result.GetProperty("error").GetString().Should().Be("caller_identity_missing");
    }

    // ── open_pull_request ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenPullRequest_Formats3RBody_AndReturnsPrDetails()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"], leadPersonaId: "astra");
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(db, sessionId, job.Id, personaId: "rook");

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(true, 42, "https://github.com/ckchessmaster/agent-mesh/pull/42",
                "feat/platform-mcp-tools", "main",
                "PR #42 opened: https://github.com/ckchessmaster/agent-mesh/pull/42. Assigned to @astra for review."));

        var tools = BuildTools(db, Context(
            jobId: job.Id, personaId: "rook", sessionId: sessionId,
            branch: "feat/platform-mcp-tools", remoteUrl: "https://github.com/ckchessmaster/agent-mesh.git"));

        var result = Parse(await tools.OpenPullRequest(
            "feat(api): platform mcp tools",
            new Summary3R("Three tools, 90 tests green.", "Reused the existing hub events.", "Reviewer login mapping."),
            branch_name: "feat/platform-mcp-tools"));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());
        result.GetProperty("pr_number").GetInt32().Should().Be(42);
        result.GetProperty("pr_url").GetString().Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/42");
        result.GetProperty("status").GetString().Should().Be("awaiting_review");

        var sent = _pullRequests.Invocations.Single().Arguments[0] as GitHubPullRequestRequest;
        sent!.Title.Should().Be("feat(api): platform mcp tools");
        sent.HeadBranch.Should().Be("feat/platform-mcp-tools");
        // The base comes from Project.GitBaseBranch, not a hardcoded 'main': a repo whose trunk is
        // named otherwise would otherwise get every agent's PR opened against the wrong branch.
        sent.BaseBranch.Should().Be("trunk-protect");
        sent.RepositoryOwner.Should().Be("ckchessmaster");
        sent.RepositoryName.Should().Be("agent-mesh");
        sent.Summary.Risk.Should().Be("Reviewer login mapping.");

        // The 3-R body is assembled by the service, which owns the template; the tool's job is to
        // hand it the three fields and the two identities that fill the attribution line.
        sent.SenderAgentId.Should().Be("rook");
        sent.TeamLeadAgentId.Should().Be("astra");
    }

    [Fact]
    public async Task OpenPullRequest_PostsTheConfirmationToTheJobChat_AndRecordsTheLinkage()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var tasks = new List<JobTaskDto> { new() { Title = "PR service", AssignedPersonaId = "rook", Status = JobTaskStatus.InProgress } };
        var job = await SeedJobAsync(db, tasks, personas: ["rook"], leadPersonaId: "astra");
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(db, sessionId, job.Id, personaId: "rook");

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(true, 42, "https://github.com/ckchessmaster/agent-mesh/pull/42",
                "feat/x", "main", "PR #42 opened: https://github.com/ckchessmaster/agent-mesh/pull/42. Assigned to @astra for review."));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", sessionId: sessionId, branch: "feat/x"));
        await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r"));

        var message = await ReadBackAsync(dbName, d => d.JobMessages.SingleAsync(m => m.JobId == job.Id));
        message.Content.Should().Contain("PR #42 opened");
        message.MentionsJson.Should().Contain("astra");
        message.TurnSessionId.Should().Be(sessionId);

        // The job and task rows have no PR column, so the durable linkage lives in the message
        // payload — which is what the Cockpit renders and a later turn re-reads.
        var linkage = JsonDocument.Parse(message.StructuredPayloadJson!).RootElement;
        linkage.GetProperty("prNumber").GetInt32().Should().Be(42);
        linkage.GetProperty("prUrl").GetString().Should().EndWith("/pull/42");
        linkage.GetProperty("simulated").GetBoolean().Should().BeFalse();

        var persistedJob = await ReadBackAsync(dbName, d => d.ProjectJobs.SingleAsync(j => j.Id == job.Id));
        ProjectJobsController.ParseTasks(persistedJob.TasksJson).Single().StatusNotes
            .Should().Contain("PR #42", "get_details must answer 'where is this work?'");

        _sent.Should().Contain(e => e.Name == "JobMessageCreated");
        _sent.Should().Contain(e => e.Name == "JobTaskUpdated");
    }

    [Fact]
    public async Task OpenPullRequest_WhenSimulated_SaysSoLoudlyAndStillReturnsTheNumber()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"]);

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(true, 999, "https://github.com/your-org/your-repo/pull/999",
                "feat/x", "main", "Simulated PR #999 (no GitHub token configured, so nothing was pushed).", Simulated: true));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: "feat/x"));

        var result = Parse(await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r")));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("pr_number").GetInt32().Should().Be(999);
        // An agent that believes it opened PR #999 will report a review that never arrives. The
        // flag and the nextStep text are what keep a local run honest.
        result.GetProperty("simulated").GetBoolean().Should().BeTrue();
        result.GetProperty("nextStep").GetString().Should().Contain("Nothing was pushed");
    }

    [Fact]
    public async Task OpenPullRequest_WhenThePushFails_ReturnsTheServiceReasonAndAnActionableHint()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"]);

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(false, null, null, null, null,
                "No job workspace found at '/root/.agentmesh/workspaces/jobs/x'.", FailureReason: "no_working_directory"));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: "feat/x"));

        var result = Parse(await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r")));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("no_working_directory");
        result.GetProperty("details").GetProperty("hint").GetString().Should().Contain("Save the workspace");

        (await ReadBackAsync(dbName, d => d.JobMessages.CountAsync(m => m.JobId == job.Id))).Should().Be(0,
            "a failed PR must not announce itself as opened");
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenPullRequest_ResolvesTheBranchFromTheJobWhenTheArgumentIsOmitted()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"]);
        job.GitBranchName = "mesh/job-a5b3ad3f";
        await db.SaveChangesAsync();

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(true, 5, "url", "mesh/job-a5b3ad3f", "main", "ok"));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: null));
        await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r"));

        var sent = (GitHubPullRequestRequest)_pullRequests.Invocations.Single().Arguments[0];
        sent.HeadBranch.Should().Be("mesh/job-a5b3ad3f");
        // The base comes from the project, not from a hardcoded 'main', so a repo whose trunk is
        // 'develop' does not get a PR against the wrong trunk.
        sent.BaseBranch.Should().Be("trunk-protect");
    }

    [Fact]
    public async Task OpenPullRequest_WithNoBranchAnywhere_IsRefused()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"]);
        job.GitBranchName = string.Empty;
        await db.SaveChangesAsync();

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: null));

        var result = Parse(await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r")));

        result.GetProperty("error").GetString().Should().Be("branch_unresolved");
        _pullRequests.Verify(
            s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OpenPullRequest_WithAnEmptyResultField_IsRefused()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"]);
        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: "feat/x"));

        var result = Parse(await tools.OpenPullRequest("feat(api): x", new Summary3R("  ", "r", "r")));

        result.GetProperty("error").GetString().Should().Be("invalid_summary");
    }

    [Fact]
    public async Task OpenPullRequest_WithoutAConfiguredLead_StillOpensAndReportsNoReviewer()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"], leadPersonaId: null);

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(true, 6, "url", "feat/x", "main", "PR #6 opened: url"));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: "feat/x"));

        var result = Parse(await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r")));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());
        var sent = (GitHubPullRequestRequest)_pullRequests.Invocations.Single().Arguments[0];
        result.TryGetProperty("reviewer", out _).Should().BeFalse(
            "the key is omitted rather than sent as null, so the agent sees 'no reviewer' " +
            "instead of an empty string it might pass back as a login");
        sent.ReviewerLogin.Should().BeNull();
    }

    [Fact]
    public async Task OpenPullRequest_MapsTheProjectLeadOntoAGitHubLoginThroughConfiguration()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"], leadPersonaId: "astra");

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(true, 1, "url", "feat/x", "main", "ok"));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: "feat/x"),
            configuration: new Dictionary<string, string?> { ["GitHub:PersonaLogins:astra"] = "astra-vane" });

        var result = Parse(await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r")));

        var sent = (GitHubPullRequestRequest)_pullRequests.Invocations.Single().Arguments[0];
        sent.ReviewerLogin.Should().Be("astra-vane");
        result.GetProperty("reviewer").GetString().Should().Be("astra-vane");
    }

    [Fact]
    public async Task OpenPullRequest_FallsBackToTheHostWideReviewerWhenTheProjectHasNoLead()
    {
        var dbName = "PlatformTools_" + Guid.NewGuid().ToString("N");
        using var db = NewDb(dbName);
        var job = await SeedJobAsync(db, personas: ["rook"], leadPersonaId: null);

        _pullRequests
            .Setup(s => s.PushBranchAndCreatePrAsync(It.IsAny<GitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequestResult(true, 1, "url", "feat/x", "main", "ok"));

        var tools = BuildTools(db, Context(jobId: job.Id, personaId: "rook", branch: "feat/x"),
            configuration: new Dictionary<string, string?> { ["GitHub:DefaultReviewer"] = "team-lead-bot" });

        await tools.OpenPullRequest("feat(api): x", new Summary3R("r", "r", "r"));

        var sent = (GitHubPullRequestRequest)_pullRequests.Invocations.Single().Arguments[0];
        sent.ReviewerLogin.Should().Be("team-lead-bot");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────

    private sealed record SentEvent(string Name, object? Payload);

    private PlatformMcpTools BuildTools(
        AgentMeshDbContext db,
        PlatformCallContext context,
        IReadOnlyDictionary<string, string?>? configuration = null) =>
        BuildTools(db, context, workflows: null, configuration);

    /// <summary>
    /// Builds the tool, optionally with a signal router.
    /// </summary>
    /// <remarks>
    /// The router parameter is optional on <c>PlatformMcpTools</c> for the same reason it is optional on
    /// <c>ProjectJobsController</c>: the ~60 tests here exercise ledger behaviour and must not be forced to
    /// know about Temporal. Overloading rather than editing every call site keeps that property while
    /// letting the routing tests supply a recording fake.
    /// </remarks>
    private PlatformMcpTools BuildTools(
        AgentMeshDbContext db,
        PlatformCallContext context,
        IWorkflowSignalRouter? workflows,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var resolver = new Mock<IPlatformCallContextResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);

        IConfiguration config = configuration is null
            ? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build()
            : new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();

        return new PlatformMcpTools(
            db,
            resolver.Object,
            _pullRequests.Object,
            _hub.Object,
            NullLogger<PlatformMcpTools>.Instance,
            config,
            workflows);
    }

    private static PlatformCallContext Context(
        Guid? jobId,
        string? personaId = "rook",
        string? agentId = "rook-driver",
        Guid? sessionId = null,
        string? branch = "feat/platform-mcp-tools",
        string? remoteUrl = "https://github.com/ckchessmaster/agent-mesh.git") =>
        new(
            AgentId: agentId,
            AgentIdSource: "test",
            PersonaId: personaId,
            PersonaIdSource: "test",
            SessionId: sessionId,
            SessionIdSource: "test",
            JobId: jobId,
            JobIdSource: jobId is null ? "absent" : "test",
            ProjectId: "agent-mesh",
            GitBranch: branch,
            GitRemoteUrl: remoteUrl,
            GitBaseBranch: null,
            WorkspaceDirectory: "/tmp/agentmesh-test-workspace");

    private static async Task<ProjectJob> SeedJobAsync(
        AgentMeshDbContext db,
        List<JobTaskDto>? tasks = null,
        string[]? personas = null,
        string? leadPersonaId = null)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == ProjectId.ToString()))
        {
            db.Projects.Add(new Project
            {
                Id = ProjectId.ToString(),
                Name = "Agent Mesh",
                GitRepositoryUrl = "https://github.com/ckchessmaster/agent-mesh.git",
                GitBaseBranch = "trunk-protect",
                DefaultLeadPersonaId = leadPersonaId,
            });
        }

        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId.ToString(),
            Title = "Platform Communication & PR Tools",
            GitBranchName = "mesh/job-2d039b82",
            TasksJson = tasks is null ? "[]" : JsonSerializer.Serialize(tasks),
        };
        db.ProjectJobs.Add(job);

        foreach (var persona in personas ?? Array.Empty<string>())
        {
            if (!await db.Personas.AnyAsync(p => p.Id == persona))
            {
                db.Personas.Add(new Persona { Id = persona, Name = persona, DriverId = "claude" });
            }
            db.ProjectPersonas.Add(new ProjectPersona { ProjectId = ProjectId.ToString(), PersonaId = persona });
        }

        if (leadPersonaId is not null)
        {
            if (!await db.Personas.AnyAsync(p => p.Id == leadPersonaId))
            {
                db.Personas.Add(new Persona { Id = leadPersonaId, Name = "Astra", DriverId = "claude" });
            }
        }

        await db.SaveChangesAsync();
        return job;
    }

    private static async Task SeedSessionAsync(
        AgentMeshDbContext db,
        Guid sessionId,
        Guid jobId,
        string personaId,
        string status = "thinking")
    {
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = $"Job Turn: platform tools (@{personaId})",
            Status = status,
            PersonaId = personaId,
            DriverId = "rook-driver",
            MetadataJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jobId"] = jobId.ToString(),
                ["projectId"] = ProjectId.ToString(),
                ["gitBranch"] = "feat/platform-mcp-tools",
                ["workspaceGitRemote"] = "https://github.com/ckchessmaster/agent-mesh.git",
            }),
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Create a context over a named in-memory store.
    /// </summary>
    /// <remarks>
    /// The name is hoisted by the caller rather than generated here: <c>UseInMemoryDatabase</c>
    /// shares a store per name, so one name means one database, and two contexts over it see each
    /// other's writes. That is what makes <see cref="ReadBackAsync"/> a real read-back.
    /// </remarks>
    private static AgentMeshDbContext NewDb(string name) => TestDbContextFactory.Create(name);

    /// <summary>
    /// Read through a second context over the same store.
    /// </summary>
    /// <remarks>
    /// The tool holds its own change tracker; asserting against that same instance can pass on an
    /// entity the tool merely mutated in memory. A fresh context is what proves the row reached the
    /// database — the same store the Cockpit's HTTP endpoints will query.
    /// </remarks>
    private static async Task<T> ReadBackAsync<T>(string dbName, Func<AgentMeshDbContext, Task<T>> read)
    {
        using var fresh = NewDb(dbName);
        return await read(fresh);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// A router that records what it was asked to deliver.
    /// </summary>
    /// <remarks>
    /// A hand-written fake rather than a <c>Mock&lt;IWorkflowSignalRouter&gt;</c> because the assertion is
    /// about the *payload* — which sender id and which dedup key reach the wire — and reading those off a
    /// recorded tuple is clearer than re-deriving them from <c>Invocations</c>. It also lets the same fake
    /// answer <c>IsWorkflowDrivingAsync</c> without a second setup expression.
    /// </remarks>
    private sealed class RecordingRouter : IWorkflowSignalRouter
    {
        public List<(Guid jobId, AgentMesh.Core.Workflows.ChatMessageSignal signal)> ChatSends { get; } = [];

        public Task<SignalDelivery> SendChatMessageAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.ChatMessageSignal signal,
            CancellationToken ct = default)
        {
            ChatSends.Add((jobId, signal));
            return Task.FromResult(new SignalDelivery(
                WorkflowSignalRouter.WorkflowIdFor(jobId), SignalDeliveryStatus.Delivered));
        }

        public Task<SignalDelivery> SendPrReviewAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.PrReviewSignal signal,
            CancellationToken ct = default) =>
            Task.FromResult(new SignalDelivery(
                WorkflowSignalRouter.WorkflowIdFor(jobId), SignalDeliveryStatus.Delivered));

        public Task<WorkflowStartResult> StartTaskWorkflowAsync(
            AgentMesh.Core.Workflows.TaskExecutionInput input, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowStartResult(true, "test-workflow"));

        public Task<AgentMesh.Core.Workflows.TaskStatusView?> GetStatusAsync(
            Guid jobId, string? taskId, CancellationToken ct = default) =>
            Task.FromResult<AgentMesh.Core.Workflows.TaskStatusView?>(null);

        public Task<bool> IsWorkflowDrivingAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    /// <summary>A router that fails the way an unreachable Temporal fails.</summary>
    private sealed class ThrowingRouter : IWorkflowSignalRouter
    {
        public Task<SignalDelivery> SendChatMessageAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.ChatMessageSignal signal,
            CancellationToken ct = default) =>
            Task.FromException<SignalDelivery>(
                new InvalidOperationException("simulated Temporal outage on the signal call"));

        public Task<SignalDelivery> SendPrReviewAsync(
            Guid jobId,
            AgentMesh.Core.Workflows.PrReviewSignal signal,
            CancellationToken ct = default) =>
            Task.FromException<SignalDelivery>(new InvalidOperationException("simulated outage"));

        public Task<WorkflowStartResult> StartTaskWorkflowAsync(
            AgentMesh.Core.Workflows.TaskExecutionInput input, CancellationToken ct = default) =>
            Task.FromException<WorkflowStartResult>(new InvalidOperationException("simulated outage"));

        public Task<AgentMesh.Core.Workflows.TaskStatusView?> GetStatusAsync(
            Guid jobId, string? taskId, CancellationToken ct = default) =>
            Task.FromException<AgentMesh.Core.Workflows.TaskStatusView?>(
                new InvalidOperationException("simulated outage"));

        public Task<bool> IsWorkflowDrivingAsync(Guid jobId, CancellationToken ct = default) =>
            Task.FromException<bool>(new InvalidOperationException("simulated outage"));
    }
}
