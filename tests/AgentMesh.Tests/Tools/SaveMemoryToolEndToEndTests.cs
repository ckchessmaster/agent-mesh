using System.Net.Http.Headers;
using System.Text.Json;
using AgentMesh.Api.Services;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
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
/// <c>save_memory</c> over the real MCP endpoint: JSON-RPC in, a persisted row out.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests construct <c>MemoryTools</c> directly, which cannot catch the two ways this feature
/// fails in production: the tool not being registered on the server at all, and an argument name the
/// client sends not binding to the parameter the method declares. Argument binding is done by the MCP
/// library from the JSON payload, so a rename on either side is silent until an agent's write is dropped.
/// </para>
/// <para>
/// These run against <see cref="AgentMesh.Api.Program"/> through <c>WebApplicationFactory</c> with the
/// database swapped for the in-memory provider, following the host-override recipe established by
/// <c>CapabilitiesWireFormatTests</c>.
/// </para>
/// </remarks>
public class SaveMemoryToolEndToEndTests : IClassFixture<WebApplicationFactory<AgentMesh.Api.Program>>
{
    private readonly WebApplicationFactory<AgentMesh.Api.Program> _factory;

    // Hoisted, not inlined into UseInMemoryDatabase: that lambda runs on every context creation, so a
    // Guid.NewGuid() there gives each scope its own empty database and the request cannot see what the
    // test seeded.
    private readonly string _dbName = "SaveMemoryE2E_" + Guid.NewGuid();

    public SaveMemoryToolEndToEndTests(WebApplicationFactory<AgentMesh.Api.Program> factory)
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
            });
        });
    }

    /// <summary>
    /// Registers a throwaway agent and returns its bearer token.
    /// </summary>
    /// <remarks>
    /// A real agent token rather than the master dev key: the master key's acceptance depends on
    /// <c>Auth:StaticApiKey</c> and <c>AGENTMESH_TOKEN</c>, and the latter is an ambient environment
    /// variable in some hosts. Hashing a token onto an agent row is the path production actually uses, and
    /// it authenticates through the claim the resolver is supposed to read.
    /// </remarks>
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

    private HttpClient CreateAuthenticatedClient(string token, string? projectId = null, Guid? sessionId = null)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (projectId is not null)
        {
            http.DefaultRequestHeaders.Add("X-Project-Id", projectId);
        }
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
    /// The endpoint is absolute as far as the client is concerned; the factory's handler short-circuits into
    /// the test server, so nothing listens on that port and no socket is opened.
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

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>
    /// Asserts the call did not fail at the protocol layer.
    /// </summary>
    /// <remarks>
    /// <c>IsError</c> is a nullable flag the library leaves unset on success, so this is "not true" rather
    /// than "false". The distinction matters for the assertions below: a refusal the tool reports as a
    /// <c>success:false</c> payload must still be a protocol success, which is exactly what this checks.
    /// </remarks>
    private static void ShouldNotBeProtocolError(CallToolResult result, string because) =>
        result.IsError.Should().NotBe(true, because + ", got: " + Text(result));

    /// <summary>
    /// Reads the store the request wrote to, through its own DI scope.
    /// </summary>
    /// <remarks>
    /// The context is scoped and the request's instance is gone by the time the call returns, but every
    /// context in this fixture points at the same in-memory store, so a fresh scope sees the committed
    /// rows. That is the point: this proves the write left the request's change tracker.
    /// </remarks>
    private async Task<T> ReadBackAsync<T>(Func<AgentMeshDbContext, Task<T>> read)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>());
    }

    [Fact]
    public async Task SaveMemory_IsAdvertisedOnTheMcpEndpoint_WithTheSpecifiedSchema()
    {
        var (_, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token));
        var tools = await client.ListToolsAsync();

        var saveMemory = tools.SingleOrDefault(t => t.Name == "save_memory");
        saveMemory.Should().NotBeNull("the control plane must expose save_memory alongside the session tools");
        saveMemory!.Description.Should().NotBeNullOrWhiteSpace();

        using var schema = JsonDocument.Parse(saveMemory.JsonSchema.ToString());
        var root = schema.RootElement;

        root.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("category", "key", "value");

        var properties = root.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "category", "key", "value", "is_global");

        properties.GetProperty("category").GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        properties.GetProperty("key").GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        properties.GetProperty("value").GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        properties.GetProperty("is_global").GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();

        // The enum is what stops a capable client from ever sending a bad bucket. It is patched onto the
        // generated schema by MemoryToolSchema, so this asserts the filter really ran on this endpoint.
        properties.GetProperty("category").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Equal(AgentMemoryCategories.All);
    }

    [Fact]
    public async Task SaveMemoryTool_ExecutesSuccessfullyViaMcp()
    {
        var (agentId, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, projectId: "agent-mesh"));

        var result = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "build_and_test",
            ["key"] = "dotnet_test_flags",
            ["value"] = "dotnet test -v quiet --nologo",
        });

        ShouldNotBeProtocolError(result, "the tool reports failures as payloads");
        using var payload = JsonDocument.Parse(Text(result));

        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("projectId").GetString().Should().Be("agent-mesh");
        payload.RootElement.GetProperty("message").GetString()
            .Should().Be($"Memory saved under key 'dotnet_test_flags' for project 'agent-mesh'. It will be injected into future sessions.");

        var memoryId = Guid.Parse(payload.RootElement.GetProperty("memoryId").GetString()!);

        var stored = await ReadBackAsync(db =>
            db.AgentMemories.AsNoTracking().Where(m => m.AgentId == agentId).ToListAsync());

        stored.Should().ContainSingle()
            .Which.Should().Match<AgentMemory>(m =>
                m.Id == memoryId &&
                m.ProjectId == "agent-mesh" &&
                m.Category == AgentMemoryCategories.BuildAndTest &&
                m.Key == "dotnet_test_flags" &&
                m.Value == "dotnet test -v quiet --nologo");
    }

    [Fact]
    public async Task SaveMemory_TwoCallsOnOneKey_CommitToTheDatabaseOnce()
    {
        // The spec's "commits to the database" check, run twice: the second call is the one that would
        // surface a unique-key collision over the wire if the upsert were really an insert.
        var (agentId, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, projectId: "agent-mesh"));

        var first = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "environment", ["key"] = "sdk_path", ["value"] = "/usr/share/dotnet",
        });
        var second = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "environment", ["key"] = "sdk_path", ["value"] = "/usr/lib/dotnet",
        });

        ShouldNotBeProtocolError(first, "the first save should succeed");
        ShouldNotBeProtocolError(second, "the re-save must not surface a unique-key collision");

        using var payload = JsonDocument.Parse(Text(second));
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var stored = await ReadBackAsync(db => db.AgentMemories.AsNoTracking()
            .Where(m => m.AgentId == agentId && m.Key == "sdk_path").ToListAsync());

        stored.Should().ContainSingle().Which.Value.Should().Be("/usr/lib/dotnet");
    }

    [Fact]
    public async Task SaveMemory_GlobalFlag_StoresUnderTheSentinel()
    {
        var (agentId, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, projectId: "agent-mesh"));

        var result = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "environment",
            ["key"] = "glibc_version",
            ["value"] = "runner image is glibc 2.39; do not link against 2.40-only symbols",
            ["is_global"] = true,
        });

        ShouldNotBeProtocolError(result, "a global save should succeed");

        var stored = await ReadBackAsync(db =>
            db.AgentMemories.AsNoTracking().Where(m => m.AgentId == agentId).ToListAsync());
        stored.Should().ContainSingle().Which.ProjectId.Should().Be(AgentMemory.GlobalProjectId);
    }

    [Fact]
    public async Task SaveMemory_WithoutProjectContext_RefusesAndSaysHowToSupplyOne()
    {
        // No X-Project-Id and no session: the write has nowhere to go. Silently choosing '*' would publish
        // a project-specific lesson to every project, which no later re-save can undo.
        var (agentId, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token));

        var result = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "codebase_gotcha", ["key"] = "k", ["value"] = "v",
        });

        ShouldNotBeProtocolError(result, "a refusal is a well-formed tool result, not a protocol failure");

        using var payload = JsonDocument.Parse(Text(result));
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("error").GetString().Should().Be("project_context_missing");
        payload.RootElement.GetProperty("message").GetString().Should().Contain("X-Project-Id");

        (await ReadBackAsync(db =>
            db.AgentMemories.AsNoTracking().Where(m => m.AgentId == agentId).ToListAsync())).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveMemory_WithUnknownCategory_RefusesWithTheAllowedList()
    {
        var (_, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, projectId: "agent-mesh"));

        var result = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "hot_takes", ["key"] = "k", ["value"] = "v",
        });

        ShouldNotBeProtocolError(result, "the caller is a model mid-turn; an opaque protocol error wastes the turn");

        using var payload = JsonDocument.Parse(Text(result));
        payload.RootElement.GetProperty("error").GetString().Should().Be("invalid_category");
        payload.RootElement.GetProperty("details").GetProperty("allowedCategories").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal(AgentMemoryCategories.All);
    }

    [Fact]
    public async Task SaveMemory_ResolvesProjectFromTheTurnSession()
    {
        // The path a persona container takes mid-job: it knows its session id, and JobTurnDispatcher
        // stamped projectId into that session's metadata. Without this, every runner would have to be told
        // its project slug out of band.
        var (agentId, token) = await RegisterAgentAsync();
        var sessionId = Guid.NewGuid();

        await ReadBackAsync(async db =>
        {
            db.AgentSessions.Add(new AgentSession
            {
                Id = sessionId,
                Title = "Job Turn: memory e2e",
                CreatorAgentId = agentId,
                MetadataJson = """{"projectId":"from-turn-session","personaId":"rook"}""",
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await using var client = await ConnectAsync(CreateAuthenticatedClient(token, sessionId: sessionId));
        var result = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "environment", ["key"] = "k", ["value"] = "v",
        });

        ShouldNotBeProtocolError(result, "the session-scoped save should succeed");
        using var payload = JsonDocument.Parse(Text(result));
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("projectId").GetString().Should().Be("from-turn-session");
    }

    [Fact]
    public async Task SaveMemory_WithoutCredentials_RefusesAndWritesNothing()
    {
        // The MCP endpoint itself is anonymous by design (the other tools self-report unauthorized, see
        // SessionTools.GetAuthenticatedAgentId), so the guard lives in the tool. What must never happen is
        // an anonymous caller landing rows that later get injected into someone else's prompt.
        var http = _factory.CreateClient();
        await using var client = await ConnectAsync(http);

        var result = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "environment", ["key"] = "sneaky", ["value"] = "v", ["is_global"] = true,
        });

        using var payload = JsonDocument.Parse(Text(result));
        payload.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("error").GetString().Should().Be("unauthorized");

        (await ReadBackAsync(db =>
            db.AgentMemories.AsNoTracking().Where(m => m.Key == "sneaky").ToListAsync())).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveMemory_UsesTokenIdentity_NotTheControllableHeader()
    {
        // X-Agent-Id is a plain header; anyone can set it. If it won over the verified token, one agent
        // could author lessons into another agent's memory and have them show up in that agent's prompts.
        var (victim, token) = await RegisterAgentAsync();
        var attackerId = "e2e-attacker-" + Guid.NewGuid().ToString("N")[..8];

        var http = CreateAuthenticatedClient(token, projectId: "agent-mesh");
        http.DefaultRequestHeaders.Add("X-Agent-Id", attackerId);

        await using var client = await ConnectAsync(http);
        var result = await client.CallToolAsync("save_memory", new Dictionary<string, object?>
        {
            ["category"] = "environment", ["key"] = "k", ["value"] = "v",
        });

        ShouldNotBeProtocolError(result, "the save should succeed under the token's identity");
        using var payload = JsonDocument.Parse(Text(result));
        payload.RootElement.GetProperty("agentId").GetString().Should().Be(victim);

        (await ReadBackAsync(db =>
            db.AgentMemories.AsNoTracking().Where(m => m.AgentId == attackerId).ToListAsync())).Should().BeEmpty();
        (await ReadBackAsync(db =>
            db.AgentMemories.AsNoTracking().Where(m => m.AgentId == victim).ToListAsync())).Should().ContainSingle();
    }

    [Fact]
    public async Task SaveMemory_LeavesOtherToolsRegistered()
    {
        // Registering a tool type adds to the collection rather than replacing it, but the wiring is a
        // one-line chain in Program.cs and dropping the session tools would be a silent regression.
        var (_, token) = await RegisterAgentAsync();
        await using var client = await ConnectAsync(CreateAuthenticatedClient(token));
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        names.Should().Contain("save_memory");
        names.Should().Contain("create_session");
        names.Should().Contain("append_thought");
        names.Should().Contain("stage_agent_intent");
    }

    [Fact]
    public async Task MemoryWiring_ResolvesTheWholeChainFromTheContainer()
    {
        // The tool is activated by the MCP server through the container, so a missing registration fails at
        // the first tool call in production rather than at startup. Resolving it here turns that into a
        // test failure: repository -> resolver -> tool, all three must be constructible in one scope.
        await using var scope = _factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IAgentMemoryRepository>()
            .Should().BeOfType<AgentMemoryRepository>();
        scope.ServiceProvider.GetRequiredService<IMemoryContextResolver>()
            .Should().BeOfType<MemoryContextResolver>();

        // Scoped, not singleton: the repository holds the per-request DbContext, and a singleton would pin
        // one change tracker across concurrent requests.
        using var secondScope = _factory.Services.CreateScope();
        secondScope.ServiceProvider.GetRequiredService<IAgentMemoryRepository>()
            .Should().NotBeSameAs(scope.ServiceProvider.GetRequiredService<IAgentMemoryRepository>());

        // MemoryTools is not itself a registered service: the MCP library builds it per invocation with
        // ActivatorUtilities, filling its constructor from the request scope. Doing the same here proves the
        // constructor's dependencies are all resolvable, which is the failure this test exists to catch.
        var tool = ActivatorUtilities.CreateInstance<MemoryTools>(scope.ServiceProvider);

        using var result = JsonDocument.Parse(await tool.SaveMemory("environment", "k", "v"));
        result.RootElement.GetProperty("success").GetBoolean().Should().BeFalse(
            "a container-resolved tool has no project header on the ambient request, and must say so rather than guess");
        result.RootElement.GetProperty("error").GetString().Should().Be("unauthorized");
    }
}
