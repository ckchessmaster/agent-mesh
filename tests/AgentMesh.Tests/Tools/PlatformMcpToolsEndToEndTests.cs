using System.Net.Http.Headers;
using System.Text.Json;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using StackExchange.Redis;
using Temporalio.Client;
using Xunit;

namespace AgentMesh.Tests.Tools;

/// <summary>
/// The three platform tools over the real <c>/mcp</c> endpoint: JSON-RPC in, rows out.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests construct <see cref="AgentMesh.Api.Tools.PlatformMcpTools"/> directly, which
/// cannot catch the two ways this feature fails in production:
/// </para>
/// <list type="number">
/// <item><description>
/// The tools not being registered on the server. The Definition of Done is that <c>/mcp</c> lists
/// <c>post_job_message</c>, <c>manage_task</c>, and <c>open_pull_request</c>; that is a claim about
/// <c>Program.cs</c>, and only a request against the real endpoint can verify it.
/// </description></item>
/// <item><description>
/// An argument name that no longer binds. The MCP library derives the published schema from the C#
/// parameter names and binds the incoming JSON by them, so renaming <c>target_persona</c> to
/// <c>targetPersona</c> on either side is silent until an agent's message loses its recipient — or
/// the call fails with a missing-argument error the agent cannot fix.
/// </description></item>
/// </list>
/// <para>
/// The wire names are asserted against the job spec's snake_case contract explicitly, including the
/// nested <c>summary_3r</c> object, because that is the contract an agent codes against and the one
/// nothing else in the build would notice breaking.
/// </para>
/// </remarks>
public class PlatformMcpToolsEndToEndTests : IClassFixture<WebApplicationFactory<AgentMesh.Api.Program>>
{
    // Hoisted, not inlined into UseInMemoryDatabase: that lambda runs on every context creation, so
    // a Guid.NewGuid() there gives each scope its own empty database and a request cannot see what
    // the test seeded.
    private readonly string _dbName = "PlatformToolsE2E_" + Guid.NewGuid();

    private readonly WebApplicationFactory<AgentMesh.Api.Program> _factory;

    public PlatformMcpToolsEndToEndTests(WebApplicationFactory<AgentMesh.Api.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Swap the provider. Removing DbContextOptions<...> alone is not enough on EF Core 10:
                // AddDbContext composes IDbContextOptionsConfiguration<T> registrations, so the
                // Aspire-registered UseNpgsql configuration survives and both providers end up on one
                // DbContextOptions, which EF rejects at query time rather than at registration.
                var stale = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AgentMeshDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(AgentMeshDbContext) ||
                    d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration") ||
                    d.ServiceType.Namespace?.StartsWith("Npgsql") == true).ToList();
                foreach (var d in stale) services.Remove(d);

                services.AddDbContext<AgentMeshDbContext>(opts => opts.UseInMemoryDatabase(_dbName));

                var mockStorage = new Mock<IStorageService>();
                mockStorage.Setup(s => s.EnsureBucketExistsAsync(default)).Returns(Task.CompletedTask);
                services.AddSingleton(mockStorage.Object);

                var mockRedis = new Mock<IConnectionMultiplexer>();
                mockRedis.Setup(r => r.IsConnected).Returns(true);
                var mockDb = new Mock<IDatabase>();
                mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
                mockDb.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ReturnsAsync(TimeSpan.FromMilliseconds(2));
                services.AddSingleton(mockRedis.Object);

                services.AddSingleton(new Mock<ITemporalClient>().Object);

                // The PR service is replaced rather than stubbed at the HTTP layer. These tests are
                // about the MCP surface — registration, schema, binding, and what the tool writes —
                // and GitHubPullRequestServiceTests already covers the transport. Leaving the real
                // one in place would make every PR test depend on whether the host has a token.
                services.AddScoped<IGitHubPullRequestService>(_ => _stubbedPullRequestService);
            });
        });
    }

    private static readonly StubPullRequestService _stubbedPullRequestService = new();

    // ── Definition of Done: tool discovery ────────────────────────────────────────

    [Fact]
    public async Task McpEndpoint_ListsAllThreePlatformToolsAlongsideTheExistingOnes()
    {
        var (_, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token));

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        names.Should().Contain("post_job_message")
            .And.Contain("manage_task")
            .And.Contain("open_pull_request");

        // The pre-existing tools must survive the registration; a WithTools<>() typo that replaced a
        // line instead of adding one would otherwise look like success.
        names.Should().Contain("save_memory").And.Contain("register_agent").And.Contain("create_session");
    }

    [Fact]
    public async Task PostJobMessage_PublishesTheSpecifiedWireSchema()
    {
        var schema = await SchemaOf("post_job_message");

        using var root = JsonDocument.Parse(schema.GetRawText());

        root.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("content", "target_persona", "requires_reply", "job_id");

        // The three required names come straight from the job spec. A camelCase drift here is the
        // silent failure: the tool still works for a caller that guesses right and fails for one
        // that reads the schema the server published.
        root.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("content", "target_persona", "requires_reply");

        // 'job_id' is the optional escape hatch and must not be required, or a runner with no
        // reason to know its job id cannot use the tool at all.
        ScalarTypeOf(root.RootElement.GetProperty("properties").GetProperty("job_id"))
            .Should().Be("string");
    }

    [Fact]
    public async Task ManageTask_PublishesTheSpecifiedWireSchema()
    {
        var schema = await SchemaOf("manage_task");

        using var root = JsonDocument.Parse(schema.GetRawText());
        var properties = root.RootElement.GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("action", "task_id", "status", "notes", "job_id");

        root.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("action");

        // The two closed vocabularies are advertised as enums so a capable client never sends a bad
        // value, while the handler still validates and answers with the allowed list. Both are
        // injected by PlatformToolSchema at tools/list time, because the handler takes them as
        // strings precisely so a bad value produces an actionable error.
        properties.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("get_details", "set_status", "list_all");

        properties.GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("todo", "in_progress", "awaiting_review", "completed");

        // The enum patch must not disturb the rest of the property. Losing the description would
        // cost the caller the explanation of when to use which action.
        properties.GetProperty("action").GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        ScalarTypeOf(properties.GetProperty("action")).Should().Be("string");
        ScalarTypeOf(properties.GetProperty("status")).Should().Be("string");
    }

    [Fact]
    public async Task OpenPullRequest_PublishesTheNestedSummarySchemaUnderTheSnakeCaseName()
    {
        var schema = await SchemaOf("open_pull_request");

        using var root = JsonDocument.Parse(schema.GetRawText());
        var properties = root.RootElement.GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("title", "summary_3r", "branch_name", "job_id");

        root.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("title", "summary_3r");

        // This is the assertion the whole nested-object decision rests on. ModelContextProtocol
        // 2.2.0 derives the property name from the C# parameter name, so the parameter must be
        // spelled summary_3r; a parameter named summary3R publishes "summary3R" and every client
        // written against the spec fails to bind.
        var summary = properties.GetProperty("summary_3r");
        summary.GetProperty("type").GetString().Should().Be("object");
        // The nested record's own members are camelCase, which is the library's serialization of
        // Summary3R's properties. They are pinned here because a client building the object has to
        // use exactly these keys.
        summary.GetProperty("properties").EnumerateObject().Select(p2 => p2.Name)
            .Should().Contain("result").And.Contain("reasoning").And.Contain("risk");
        summary.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("result", "reasoning", "risk");
        summary.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("result", "reasoning", "risk");
    }

    // ── End-to-end calls ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PostJobMessage_ThroughTheEndpoint_WritesTheLedgerAndParksTheSession()
    {
        var (agentId, token) = await RegisterAgentAsync();
        var jobId = await SeedJobAsync(agentId, tasks: null, personaId: "e2e-lead");

        var sessionId = Guid.NewGuid();
        await SeedTurnSessionAsync(sessionId, jobId, agentId, personaId: "e2e-lead");

        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, sessionId: sessionId));

        var result = await client.CallToolAsync("post_job_message", new Dictionary<string, object?>
        {
            ["content"] = "Blocked on the migration order. Need a decision.",
            ["target_persona"] = "e2e-lead",
            ["requires_reply"] = true,
        });

        NotProtocolError(result);
        var payload = JsonDocument.Parse(Text(result)).RootElement;
        payload.GetProperty("success").GetBoolean().Should().BeTrue(payload.ToString());
        payload.GetProperty("requiresReply").GetBoolean().Should().BeTrue();

        // Read back through a fresh scope: the point is that the row is in the store the Cockpit's
        // HTTP endpoints will query, not merely in the object the tool mutated.
        var stored = await ReadBackAsync(db => db.JobMessages.Where(m => m.JobId == jobId).ToListAsync());
        var message = stored.Should().ContainSingle().Subject;
        message.Content.Should().Be("@e2e-lead Blocked on the migration order. Need a decision.");
        message.SenderId.Should().Be("e2e-lead");
        message.TurnSessionId.Should().Be(sessionId);

        var session = await ReadBackAsync(db => db.AgentSessions.SingleAsync(s => s.Id == sessionId));
        session.Status.Should().Be("waiting_for_input");
    }

    [Fact]
    public async Task ManageTask_ThroughTheEndpoint_TransitionsTheTaskInTheStoredBlob()
    {
        var (agentId, token) = await RegisterAgentAsync();
        var task = new JobTaskDto { Title = "Wire format tests", AssignedPersonaId = "e2e-lead" };
        var jobId = await SeedJobAsync(agentId, [task], personaId: "e2e-lead");

        var sessionId = Guid.NewGuid();
        await SeedTurnSessionAsync(sessionId, jobId, agentId, personaId: "e2e-lead");

        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, sessionId: sessionId));

        var listed = await client.CallToolAsync("manage_task", new Dictionary<string, object?>
        {
            ["action"] = "list_all",
        });
        NotProtocolError(listed);

        var listedTasks = JsonDocument.Parse(Text(listed)).RootElement.GetProperty("tasks")
            .EnumerateArray().ToList();
        listedTasks.Should().ContainSingle();
        listedTasks[0].GetProperty("id").GetString().Should().Be(task.Id.ToString());
        listedTasks[0].GetProperty("status").GetString().Should().Be("todo");

        var updated = await client.CallToolAsync("manage_task", new Dictionary<string, object?>
        {
            ["action"] = "set_status",
            ["task_id"] = task.Id.ToString(),
            ["status"] = "in_progress",
            ["notes"] = "started",
        });
        NotProtocolError(updated);
        JsonDocument.Parse(Text(updated)).RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var persisted = await ReadBackAsync(db => db.ProjectJobs.SingleAsync(j => j.Id == jobId));
        var storedTask = ProjectJobsControllerProbe.Parse(persisted.TasksJson).Single();
        storedTask.Status.Should().Be("in_progress");
        storedTask.StatusNotes.Should().Be("started");
    }

    [Fact]
    public async Task ManageTask_ThroughTheEndpoint_RejectsABadStatusAsAPayloadNotAProtocolError()
    {
        var (agentId, token) = await RegisterAgentAsync();
        var task = new JobTaskDto { Title = "Wire format tests", AssignedPersonaId = "e2e-lead" };
        var jobId = await SeedJobAsync(agentId, [task], personaId: "e2e-lead");

        var sessionId = Guid.NewGuid();
        await SeedTurnSessionAsync(sessionId, jobId, agentId, personaId: "e2e-lead");

        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, sessionId: sessionId));

        var result = await client.CallToolAsync("manage_task", new Dictionary<string, object?>
        {
            ["action"] = "set_status",
            ["task_id"] = task.Id.ToString(),
            ["status"] = "done",
        });

        // The distinction the caller cares about: a protocol error costs the model its turn and it
        // retries the same bad value. A success:false payload keeps it in the conversation with the
        // allowed list in hand.
        result.IsError.Should().NotBeTrue();

        var payload = JsonDocument.Parse(Text(result)).RootElement;
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("error").GetString().Should().Be("invalid_status");
        payload.GetProperty("details").GetProperty("allowedStatuses").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public async Task OpenPullRequest_ThroughTheEndpoint_BindsTheNestedSummaryAndReturnsThePrNumber()
    {
        var (agentId, token) = await RegisterAgentAsync();
        var jobId = await SeedJobAsync(agentId, tasks: null, personaId: "e2e-lead", branch: "feat/e2e-branch");

        var sessionId = Guid.NewGuid();
        await SeedTurnSessionAsync(sessionId, jobId, agentId, personaId: "e2e-lead");

        _stubbedPullRequestService.Capture();

        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, sessionId: sessionId));

        var result = await client.CallToolAsync("open_pull_request", new Dictionary<string, object?>
        {
            ["title"] = "feat(api): platform mcp tools",
            ["summary_3r"] = new Dictionary<string, object?>
            {
                ["result"] = "Three tools registered; 130 tests green.",
                ["reasoning"] = "Reused the existing hub event names.",
                ["risk"] = "Reviewer login mapping is an assumption.",
            },
        });

        NotProtocolError(result);
        var payload = JsonDocument.Parse(Text(result)).RootElement;
        payload.GetProperty("success").GetBoolean().Should().BeTrue(payload.ToString());
        payload.GetProperty("pr_number").GetInt32().Should().Be(4242);
        payload.GetProperty("pr_url").GetString().Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/4242");
        payload.GetProperty("status").GetString().Should().Be("awaiting_review");

        // The nested object survived JSON-RPC -> schema binding -> the service request. This is the
        // single assertion that proves summary_3r binds at all.
        var sent = _stubbedPullRequestService.LastRequest;
        sent.Should().NotBeNull("the tool never reached the PR service");
        sent!.Title.Should().Be("feat(api): platform mcp tools");
        sent.Summary.Result.Should().Be("Three tools registered; 130 tests green.");
        sent.Summary.Reasoning.Should().Be("Reused the existing hub event names.");
        sent.Summary.Risk.Should().Be("Reviewer login mapping is an assumption.");
        sent.HeadBranch.Should().Be("feat/e2e-branch");
        sent.RepositoryOwner.Should().Be("ckchessmaster");
        sent.RepositoryName.Should().Be("agent-mesh");
    }

    [Fact]
    public async Task OpenPullRequest_ThroughTheEndpoint_ReportsAFailureAsAPayload()
    {
        var (agentId, token) = await RegisterAgentAsync();
        var jobId = await SeedJobAsync(agentId, tasks: null, personaId: "e2e-lead", branch: "feat/e2e-branch");

        var sessionId = Guid.NewGuid();
        await SeedTurnSessionAsync(sessionId, jobId, agentId, personaId: "e2e-lead");

        _stubbedPullRequestService.FailWith("no_working_directory", "No job workspace found.");

        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, sessionId: sessionId));

        var result = await client.CallToolAsync("open_pull_request", new Dictionary<string, object?>
        {
            ["title"] = "feat(api): x",
            ["summary_3r"] = new Dictionary<string, object?> { ["result"] = "r", ["reasoning"] = "r", ["risk"] = "r" },
        });

        result.IsError.Should().NotBeTrue();
        var payload = JsonDocument.Parse(Text(result)).RootElement;
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("error").GetString().Should().Be("no_working_directory");

        (await ReadBackAsync(db => db.JobMessages.CountAsync(m => m.JobId == jobId))).Should().Be(0);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────

    private async Task<JsonElement> SchemaOf(string toolName)
    {
        var (_, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token));

        var tool = (await client.ListToolsAsync()).SingleOrDefault(t => t.Name == toolName);
        tool.Should().NotBeNull($"the control plane must expose {toolName} alongside the session tools");
        tool!.Description.Should().NotBeNullOrWhiteSpace(
            "a tool with no description is a name an agent cannot decide to call");

        return tool.JsonSchema;
    }

    private async Task<(string AgentId, string Token)> RegisterAgentAsync()
    {
        var agentId = "e2e-agent-" + Guid.NewGuid().ToString("N")[..12];
        var rawToken = new ApiKeyService().GenerateToken();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
        db.Agents.Add(new Agent
        {
            Id = agentId,
            Name = agentId,
            Role = "E2E",
            Status = "online",
            ApiKeyHash = new ApiKeyService().HashToken(rawToken),
        });
        await db.SaveChangesAsync();

        return (agentId, rawToken);
    }

    private async Task<Guid> SeedJobAsync(
        string projectIdSlug,
        List<JobTaskDto>? tasks,
        string personaId,
        string branch = "mesh/job-e2e")
    {
        var projectId = "e2e-proj-" + Guid.NewGuid().ToString("N")[..10];
        var jobId = Guid.NewGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "E2E Project",
            GitRepositoryUrl = "https://github.com/ckchessmaster/agent-mesh.git",
            GitBaseBranch = "main",
            DefaultLeadPersonaId = personaId,
        });

        db.Personas.Add(new Persona { Id = personaId, Name = personaId, DriverId = projectIdSlug });
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = projectId, PersonaId = personaId });

        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = projectId,
            Title = "Platform tools E2E",
            GitBranchName = branch,
            TasksJson = tasks is null ? "[]" : JsonSerializer.Serialize(tasks),
        });

        await db.SaveChangesAsync();
        return jobId;
    }

    private async Task SeedTurnSessionAsync(Guid sessionId, Guid jobId, string driverId, string personaId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

        var job = await db.ProjectJobs.SingleAsync(j => j.Id == jobId);
        var project = await db.Projects.SingleAsync(p => p.Id == job.ProjectId);

        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = $"Job Turn: {job.Title} (@{personaId})",
            Status = "thinking",
            PersonaId = personaId,
            DriverId = driverId,
            MetadataJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jobId"] = jobId.ToString(),
                ["projectId"] = project.Id,
                ["gitBranch"] = job.GitBranchName,
                ["gitBaseBranch"] = project.GitBaseBranch,
                ["workspaceGitRemote"] = project.GitRepositoryUrl,
            }),
        });

        await db.SaveChangesAsync();
    }

    private HttpClient CreateAuthenticatedClient(string token, Guid? sessionId = null)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (sessionId is not null)
        {
            http.DefaultRequestHeaders.Add("X-Session-Id", sessionId.Value.ToString());
        }

        return http;
    }

    /// <summary>
    /// A real MCP client over the test server's in-process pipeline.
    /// </summary>
    /// <remarks>
    /// The endpoint is absolute as far as the client is concerned; the factory's handler
    /// short-circuits into the test server, so nothing listens on that port and no socket is opened.
    /// </remarks>
    private static async Task<McpClient> ConnectAsync(HttpClient http)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, loggerFactory: null!, ownsHttpClient: false);

        return await McpClient.CreateAsync(transport);
    }

    /// <summary>
    /// The scalar type name of a schema property, unwrapping the union a nullable parameter gets.
    /// </summary>
    /// <remarks>
    /// ModelContextProtocol 2.2.0 publishes an optional <c>string?</c> parameter as
    /// <c>{"type": ["string","null"], "default": null}</c> — the correct JSON Schema way to say
    /// "may be absent". The shipped <c>save_memory.is_global</c> parameter has the same shape, so
    /// this is the library's convention rather than a defect to work around; the tests assert the
    /// scalar half of it.
    /// </remarks>
    private static string ScalarTypeOf(JsonElement property)
    {
        var type = property.GetProperty("type");
        return type.ValueKind == JsonValueKind.String
            ? type.GetString()!
            : type.EnumerateArray().First(e => e.GetString() != "null").GetString()!;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>
    /// Asserts the call did not fail at the protocol layer.
    /// </summary>
    /// <remarks>
    /// <c>IsError</c> is a nullable flag the library leaves unset on success, so this is "not true"
    /// rather than "false". A refusal the tool reports as a <c>success:false</c> payload must still
    /// be a protocol success, which is exactly what this checks.
    /// </remarks>
    private static void NotProtocolError(CallToolResult result) =>
        result.IsError.Should().NotBeTrue(string.Concat(
            result.Content.OfType<TextContentBlock>().Select(c => c.Text)));

    private async Task<T> ReadBackAsync<T>(Func<AgentMeshDbContext, Task<T>> read)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>());
    }

    /// <summary>
    /// Stands in for the GitHub service so the MCP surface can be exercised without credentials.
    /// </summary>
    private sealed class StubPullRequestService : IGitHubPullRequestService
    {
        private GitHubPullRequestResult _next = new(true, 4242,
            "https://github.com/ckchessmaster/agent-mesh/pull/4242",
            "feat/e2e-branch", "main",
            "PR #4242 opened: https://github.com/ckchessmaster/agent-mesh/pull/4242. Assigned to @e2e-lead for review.");

        public GitHubPullRequestRequest? LastRequest { get; private set; }

        public void Capture() => LastRequest = null;

        public void FailWith(string reason, string message) =>
            _next = new GitHubPullRequestResult(false, null, null, null, null, message, FailureReason: reason);

        public Task<GitHubPullRequestResult> PushBranchAndCreatePrAsync(
            GitHubPullRequestRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(_next);
        }

        /// <summary>
        /// Merge is asserted by <c>GitActivitiesTests</c> against a dedicated fake, not here: this
        /// harness exists to drive the MCP tool surface, and no MCP tool merges. The stub implements it
        /// only because <see cref="IGitHubPullRequestService"/> now declares the member — a PR opened
        /// through <c>open_pull_request</c> and merged through a Temporal review signal are two
        /// different trust decisions on two different code paths.
        /// </summary>
        public Task<GitHubMergeResult> MergePullRequestAsync(
            GitHubMergeRequest request,
            CancellationToken ct = default)
        {
            LastMergeRequest = request;
            return Task.FromResult(_nextMerge);
        }

        public GitHubMergeRequest? LastMergeRequest { get; private set; }

        private GitHubMergeResult _nextMerge = new(true, "Merged pull request #4242 via squash (commit abc1234).");

        public void MergeFailsWith(string reason, string message) =>
            _nextMerge = new GitHubMergeResult(false, message, reason);
    }

    /// <summary>
    /// Mirrors the controller's task-blob parser so the assertion is against the same deserialization
    /// the Cockpit's endpoints perform.
    /// </summary>
    private static class ProjectJobsControllerProbe
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static List<JobTaskDto> Parse(string tasksJson) =>
            JsonSerializer.Deserialize<List<JobTaskDto>>(tasksJson, Options) ?? new();
    }
}
