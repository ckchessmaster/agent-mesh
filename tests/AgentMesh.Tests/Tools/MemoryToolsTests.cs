using System.Text.Json;
using AgentMesh.Api.Services;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Tools;

/// <summary>
/// The <c>save_memory</c> tool contract: what it accepts, what it refuses, and what it says when it does
/// either.
/// </summary>
/// <remarks>
/// Driven through the tool class directly with a real repository over the in-memory provider, so the
/// assertions cover the whole write path (validation, scope resolution, persistence) rather than a mocked
/// call. <c>SaveMemoryToolEndToEndTests</c> covers the MCP transport on top of this.
/// </remarks>
public class MemoryToolsTests
{
    private static readonly MemoryCallScope ProjectScope =
        new("rook", "agent-mesh", "test claim", "test header");

    private readonly AgentMeshDbContext _db = TestDbContextFactory.Create();

    private MemoryTools Tool(MemoryCallScope? scope = null) =>
        new(
            new AgentMemoryRepository(_db),
            new StubScopeResolver(scope ?? ProjectScope),
            NullLogger<MemoryTools>.Instance);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The tool under a specific scope, backed by its own store so the test can assert "nothing was
    /// written" without another test's rows showing up.
    /// </summary>
    private static MemoryTools ToolReturning(MemoryCallScope scope) => new(
        new AgentMemoryRepository(TestDbContextFactory.Create()),
        new StubScopeResolver(scope),
        NullLogger<MemoryTools>.Instance);

    [Fact]
    public async Task SaveMemory_WithValidProjectScopedArgs_PersistsAndReportsSuccess()
    {
        var result = Parse(await Tool().SaveMemory("build_and_test", "dotnet_test_flags", "dotnet test -v quiet --nologo"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("category").GetString().Should().Be(AgentMemoryCategories.BuildAndTest);
        result.GetProperty("agentId").GetString().Should().Be("rook");
        result.GetProperty("projectId").GetString().Should().Be("agent-mesh");
        result.GetProperty("isGlobal").GetBoolean().Should().BeFalse();
        result.GetProperty("message").GetString()
            .Should().Be("Memory saved under key 'dotnet_test_flags' for project 'agent-mesh'. It will be injected into future sessions.");

        var stored = await new AgentMemoryRepository(_db).GetMemoriesForAgentAsync("rook", "agent-mesh");
        stored.Should().ContainSingle()
            .Which.Value.Should().Be("dotnet test -v quiet --nologo");
    }

    [Fact]
    public async Task SaveMemory_WithIsGlobal_StoresUnderTheSentinelProject()
    {
        var result = Parse(await Tool().SaveMemory(
            "environment", "dotnet_root", "/usr/share/dotnet", is_global: true));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("projectId").GetString().Should().Be(AgentMemory.GlobalProjectId);
        result.GetProperty("isGlobal").GetBoolean().Should().BeTrue();
        result.GetProperty("resolvedFrom").GetProperty("projectId").GetString().Should().Be("is_global=true");

        var stored = await new AgentMemoryRepository(_db).GetMemoriesForAgentAsync("rook", "agent-mesh");
        stored.Should().ContainSingle(m => m.IsGlobal,
            "a global lesson must be visible to a project-scoped read");
    }

    [Fact]
    public async Task SaveMemory_WithoutIsGlobal_UsesTheResolvedProject()
    {
        var result = Parse(await Tool().SaveMemory("environment", "k", "v"));

        result.GetProperty("isGlobal").GetBoolean().Should().BeFalse();
        result.GetProperty("projectId").GetString().Should().Be("agent-mesh");
    }

    [Fact]
    public async Task SaveMemory_ReSavingTheSameKey_UpdatesInPlaceAndEchoesTheStoredId()
    {
        var tool = Tool();

        var first = Parse(await tool.SaveMemory("environment", "sdk_path", "/usr/share/dotnet"));
        var second = Parse(await tool.SaveMemory("codebase_gotcha", "sdk_path", "/usr/lib/dotnet"));

        second.GetProperty("success").GetBoolean().Should().BeTrue(
            "refining a lesson re-saves the same key; a collision error here would teach agents to invent new keys and fork the memory");
        second.GetProperty("memoryId").GetString().Should().Be(first.GetProperty("memoryId").GetString());

        var stored = await new AgentMemoryRepository(_db).GetMemoriesForAgentAsync("rook", "agent-mesh");
        stored.Should().ContainSingle();
        stored[0].Value.Should().Be("/usr/lib/dotnet");
        stored[0].Category.Should().Be(AgentMemoryCategories.CodebaseGotcha);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("build_and_test")]
    [InlineData("codebase_gotcha")]
    public async Task SaveMemory_AcceptsEveryAdvertisedCategory(string category)
    {
        var result = Parse(await Tool().SaveMemory(category, "k", "v"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("category").GetString().Should().Be(category);
    }

    [Theory]
    [InlineData("BUILD_AND_TEST")]
    [InlineData(" Codebase_Gotcha ")]
    public async Task SaveMemory_NormalizesCategoryCasingAndWhitespace(string category)
    {
        var result = Parse(await Tool().SaveMemory(category, "k", "v"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("category").GetString().Should().Be(category.Trim().ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("misc")]
    [InlineData("environmental")]
    public async Task SaveMemory_RejectsUnknownCategory_ListingTheAllowedValues(string category)
    {
        var result = Parse(await Tool().SaveMemory(category, "k", "v"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("invalid_category");
        result.GetProperty("message").GetString().Should().Contain("'environment', 'build_and_test', 'codebase_gotcha'");
        result.GetProperty("details").GetProperty("allowedCategories").GetArrayLength().Should().Be(3);

        // The rejection must be a tool result, not a protocol error: a model that gets an opaque failure
        // retries the same bad argument, one that gets the list fixes it.
        (await new AgentMemoryRepository(_db).GetMemoriesForAgentAsync("rook", "agent-mesh")).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveMemory_RejectsBlankKey()
    {
        var result = Parse(await Tool().SaveMemory("environment", "   ", "v"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("invalid_memory");
        result.GetProperty("message").GetString().Should().Contain("Key");
    }

    [Fact]
    public async Task SaveMemory_RejectsBlankValue()
    {
        var result = Parse(await Tool().SaveMemory("environment", "k", "   "));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("invalid_memory");
        result.GetProperty("message").GetString().Should().Contain("Value");
    }

    [Fact]
    public async Task SaveMemory_RejectsOverlongKey_NamingTheLimit()
    {
        var result = Parse(await Tool().SaveMemory("environment", new string('k', AgentMemory.MaxKeyLength + 1), "v"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("message").GetString().Should().Contain(AgentMemory.MaxKeyLength.ToString());
    }

    [Fact]
    public async Task SaveMemory_RejectsAnonymousCaller_WithoutWriting()
    {
        var tool = ToolReturning(new MemoryCallScope(null, "agent-mesh", "unauthenticated", "test header"));

        var result = Parse(await tool.SaveMemory("environment", "k", "v"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("unauthorized");
        result.GetProperty("message").GetString().Should().Contain("X-Agent-Id");
    }

    [Fact]
    public async Task SaveMemory_RejectsUnresolvableProject_AndTellsTheCallerHowToFixIt()
    {
        // The alternative is silently filing a project lesson under '*', which makes it visible to every
        // project forever. The error names both headers and the is_global escape hatch.
        var tool = ToolReturning(new MemoryCallScope("rook", null, "test claim", "no project context"));

        var result = Parse(await tool.SaveMemory("environment", "k", "v"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("project_context_missing");
        var message = result.GetProperty("message").GetString();
        message.Should().Contain("X-Project-Id").And.Contain("X-Session-Id").And.Contain("is_global");
    }

    [Fact]
    public async Task SaveMemory_ReportsWhereIdentityWasResolvedFrom()
    {
        // A mis-scoped memory is otherwise invisible: the agent sees "saved" and moves on. Echoing the
        // resolution source is what makes "why is this in the wrong project?" answerable from the log.
        var scope = new MemoryCallScope("rook", "agent-mesh", "X-Agent-Id header", "X-Project-Id header");

        var result = Parse(await Tool(scope).SaveMemory("environment", "k", "v"));

        result.GetProperty("resolvedFrom").GetProperty("agentId").GetString().Should().Be("X-Agent-Id header");
        result.GetProperty("resolvedFrom").GetProperty("projectId").GetString().Should().Be("X-Project-Id header");
    }

    [Fact]
    public async Task SaveMemory_WhenStorageFails_ReturnsAnErrorPayloadInsteadOfThrowing()
    {
        // A memory write is best-effort from the agent's point of view. Letting the exception escape costs
        // the model its turn; a structured failure lets it fall back to reporting the finding in prose.
        var failing = new Mock<IAgentMemoryRepository>();
        failing.Setup(r => r.UpsertMemoryAsync(It.IsAny<AgentMemory>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));

        var tool = new MemoryTools(
            failing.Object,
            new StubScopeResolver(ProjectScope),
            NullLogger<MemoryTools>.Instance);

        var result = Parse(await tool.SaveMemory("environment", "k", "v"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Be("storage_unavailable");
        result.GetProperty("message").GetString().Should().Contain("connection refused");
    }

    [Fact]
    public async Task SaveMemory_PropagatesCancellation()
    {
        // An aborted turn must still cancel: swallowing OperationCanceledException would keep the runner
        // alive inside a turn the caller has already given up on.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var failing = new Mock<IAgentMemoryRepository>();
        failing.Setup(r => r.UpsertMemoryAsync(It.IsAny<AgentMemory>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var tool = new MemoryTools(
            failing.Object,
            new StubScopeResolver(ProjectScope),
            NullLogger<MemoryTools>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.SaveMemory("environment", "k", "v", ct: cts.Token));
    }

    [Fact]
    public async Task SaveMemory_TrimsArgumentsBeforeStoringThem()
    {
        // ' k ' and 'k' must not become two rows: the key is the identity, and a whitespace fork would put
        // both versions into the prompt stack.
        var result = Parse(await Tool().SaveMemory("environment", "  dotnet_test_flags  ", "  dotnet test -v quiet  "));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("message").GetString().Should().Contain("'dotnet_test_flags'");

        var stored = await new AgentMemoryRepository(_db).GetMemoriesForAgentAsync("rook", "agent-mesh");
        stored.Should().ContainSingle().Which.Key.Should().Be("dotnet_test_flags");
    }

    [Fact]
    public async Task SaveMemory_UsesTheCallerIdentity_NotTheKeyContent()
    {
        // The agent id comes from the resolved scope, never from an argument: a tool parameter named
        // agentId would let any caller write into another agent's memory.
        var result = Parse(await Tool().SaveMemory("environment", "k", "v"));

        result.GetProperty("agentId").GetString().Should().Be("rook");
        var stored = await new AgentMemoryRepository(_db).GetMemoriesForAgentAsync("rook", "agent-mesh");
        stored.Should().ContainSingle().Which.AgentId.Should().Be("rook");
    }

    [Fact]
    public async Task SaveMemory_ReturnsCamelCaseFieldNamesForTheDashboardAndClients()
    {
        var result = Parse(await Tool().SaveMemory("environment", "k", "v"));

        result.EnumerateObject().Select(p => p.Name).Should().Contain(new[]
        {
            "success", "message", "memoryId", "category", "agentId", "projectId", "isGlobal",
            "confidenceScore", "updatedAt", "resolvedFrom",
        });
    }

    private sealed class StubScopeResolver(MemoryCallScope scope) : IMemoryContextResolver
    {
        public Task<MemoryCallScope> ResolveAsync(bool? isGlobal, CancellationToken ct = default) =>
            Task.FromResult(isGlobal == true
                ? scope with { ProjectId = AgentMemory.GlobalProjectId, ProjectIdSource = "is_global=true" }
                : scope);
    }
}
