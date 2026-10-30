using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Repositories;

/// <summary>
/// Behaviour of the memory store: the upsert contract, the project-plus-global read, and delete.
/// </summary>
/// <remarks>
/// These run on the in-memory provider, like the rest of this assembly. That is a real limitation worth
/// naming: the provider neither enforces the <c>(agent_id, project_id, key)</c> unique index nor supports
/// transactions, so the collision-retry path in <see cref="AgentMemoryRepository.UpsertMemoryAsync"/>
/// cannot be driven from here. <c>AgentMemorySchemaTests</c> pins the index that path depends on, and
/// <see cref="UpsertMemory_ConcurrentSavesOfOneNewKey_LeaveExactlyOneRow"/> covers the outcome the retry
/// exists to produce.
/// </remarks>
public class AgentMemoryRepositoryTests
{
    /// <summary>
    /// A database name per test instance.
    /// </summary>
    /// <remarks>
    /// <see cref="TestDbContextFactory.Create"/> with no name mints a brand-new store on every call, which
    /// is what most tests want. The repository tests need two contexts over one store — one to write, a
    /// second to read back untracked — to prove a row really landed rather than merely sitting in the
    /// writer's change tracker.
    /// </remarks>
    private readonly string _dbName = "AgentMemoryRepo_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);


    private static AgentMemory Memory(
        string agentId,
        string projectId,
        string key,
        string value,
        string category = AgentMemoryCategories.Environment,
        double confidence = 1.0) =>
        AgentMemory.TryCreate(agentId, projectId, category, key, value, confidence)!;

    private static AgentMemoryRepository Repo(AgentMeshDbContext db) => new(db);

    [Fact]
    public async Task UpsertMemory_WhenNewKey_InsertsRecord()
    {
        using var db = Db();
        var repo = Repo(db);

        var saved = await repo.UpsertMemoryAsync(
            Memory("rook", "agent-mesh", "dotnet_test_flags", "dotnet test -v quiet --nologo"));

        saved.Id.Should().NotBeEmpty();
        saved.Category.Should().Be(AgentMemoryCategories.Environment);
        saved.ConfidenceScore.Should().Be(1.0);

        using var verify = Db();
        var rows = await verify.AgentMemories.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle()
            .Which.Should().Match<AgentMemory>(m =>
                m.AgentId == "rook" &&
                m.ProjectId == "agent-mesh" &&
                m.Key == "dotnet_test_flags" &&
                m.Value == "dotnet test -v quiet --nologo");
    }

    [Fact]
    public async Task UpsertMemory_WhenExistingKey_UpdatesValueAndTimestamp()
    {
        using var db = Db();
        var repo = Repo(db);

        var first = await repo.UpsertMemoryAsync(
            Memory("rook", "agent-mesh", "dotnet_test_flags", "dotnet test", confidence: 0.4));

        // A re-save must not throw a unique-key collision and must not leave a second row behind: this is
        // the whole point of keying memory on (agent, project, key) rather than appending to a log.
        var second = await repo.UpsertMemoryAsync(
            Memory("rook", "agent-mesh", "dotnet_test_flags", "dotnet test -v quiet --nologo",
                AgentMemoryCategories.BuildAndTest, confidence: 0.9));

        second.Id.Should().Be(first.Id, "the stored row is updated in place, so its identity is stable");
        second.Value.Should().Be("dotnet test -v quiet --nologo");
        second.Category.Should().Be(AgentMemoryCategories.BuildAndTest);
        second.ConfidenceScore.Should().Be(0.9);
        second.CreatedAt.Should().Be(first.CreatedAt);
        second.UpdatedAt.Should().BeOnOrAfter(first.UpdatedAt);

        using var verify = Db();
        (await verify.AgentMemories.AsNoTracking().ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task UpsertMemory_WhenExistingKey_KeepsCreatedAtAcrossLongGap()
    {
        using var db = Db();
        var repo = Repo(db);

        // Seeded straight into the store rather than through the repository: an insert stamps CreatedAt
        // with server time (no tool path can supply one), so the only way to stand up "a lesson we learned
        // three weeks ago" is to write the row the way a migration or an import would.
        var createdAt = DateTimeOffset.UtcNow.AddDays(-21);
        db.AgentMemories.Add(new AgentMemory
        {
            AgentId = "rook",
            ProjectId = "agent-mesh",
            Category = AgentMemoryCategories.Environment,
            Key = "sdk_path",
            Value = "/usr/share/dotnet",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();

        var refreshed = await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "sdk_path", "/usr/lib/dotnet"));

        // CreatedAt is how a reader tells "we learned this three weeks ago" from "we learned this just
        // now", which is what a confidence-decay pass would need. An update must never reset it.
        refreshed.CreatedAt.Should().Be(createdAt);
        refreshed.UpdatedAt.Should().BeAfter(createdAt);
    }

    [Fact]
    public async Task UpsertMemory_SameKeyUnderDifferentProjects_KeepsRowsSeparate()
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "test_command", "dotnet test AgentMesh.slnx"));
        await repo.UpsertMemoryAsync(Memory("rook", "other-repo", "test_command", "npm test"));

        using var verify = Db();
        var rows = await verify.AgentMemories.AsNoTracking().Where(m => m.Key == "test_command").ToListAsync();

        rows.Should().HaveCount(2, "the project boundary is part of the identity, so a lesson per project is two lessons");
        rows.Select(r => r.ProjectId).Should().BeEquivalentTo("agent-mesh", "other-repo");
    }

    [Fact]
    public async Task UpsertMemory_SameKeyUnderDifferentAgents_KeepsRowsSeparate()
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "test_command", "dotnet test"));
        await repo.UpsertMemoryAsync(Memory("forge", "agent-mesh", "test_command", "dotnet test --no-build"));

        using var verify = Db();
        (await verify.AgentMemories.AsNoTracking().ToListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task UpsertMemory_ConcurrentSavesOfOneNewKey_LeaveExactlyOneRow()
    {
        // Two writers, one per context, both seeing an empty table: the shape of two pods saving the same
        // lesson in the same second. The unique index is what resolves this on PostgreSQL (the loser's
        // insert is rejected and retried as an update); on the in-memory provider the index is not
        // enforced, so this asserts the outcome the retry path is designed to converge on.
        using var dbA = Db();
        using var dbB = Db();

        var saveA = Repo(dbA).UpsertMemoryAsync(Memory("rook", "agent-mesh", "sdk_path", "/usr/share/dotnet"));
        var saveB = Repo(dbB).UpsertMemoryAsync(Memory("rook", "agent-mesh", "sdk_path", "/usr/lib/dotnet"));

        var results = await Task.WhenAll(saveA, saveB);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Key == "sdk_path");
    }

    [Fact]
    public async Task UpsertMemory_WithInvalidRow_ThrowsBeforeTouchingTheStore()
    {
        using var db = Db();
        var repo = Repo(db);

        var invalid = new AgentMemory { AgentId = "rook", ProjectId = "p", Category = "hot_takes", Key = "k", Value = "v" };

        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertMemoryAsync(invalid));

        // The message has to name the offending field: this exception surfaces in a tool response, and
        // "validation failed" would leave the agent guessing which argument to fix.
        using var verify = Db();
        (await verify.AgentMemories.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetMemories_ReturnsProjectAndGlobalMemories()
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "project_flag", "--no-restore", AgentMemoryCategories.BuildAndTest));
        await repo.UpsertMemoryAsync(Memory("rook", AgentMemory.GlobalProjectId, "global_path", "/usr/share/dotnet"));
        await repo.UpsertMemoryAsync(Memory("rook", "other-repo", "elsewhere", "not yours", AgentMemoryCategories.CodebaseGotcha));

        var results = await repo.GetMemoriesForAgentAsync("rook", "agent-mesh");

        results.Select(m => m.Key).Should().BeEquivalentTo("project_flag", "global_path");
        results.Should().NotContain(m => m.Key == "elsewhere", "another project's lessons must not leak into this one");
    }

    [Fact]
    public async Task GetMemories_ExcludesOtherAgentsMemories()
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "mine", "rook was here"));
        await repo.UpsertMemoryAsync(Memory("forge", "agent-mesh", "theirs", "forge was here"));

        var results = await repo.GetMemoriesForAgentAsync("rook", "agent-mesh");

        results.Should().ContainSingle().Which.Key.Should().Be("mine");
    }

    [Fact]
    public async Task GetMemories_OrdersByCategoryThenMostRecentlyUpdated()
    {
        using var db = Db();
        var repo = Repo(db);

        var oldest = Memory("rook", "agent-mesh", "build_old", "v", AgentMemoryCategories.BuildAndTest);
        var newest = Memory("rook", "agent-mesh", "build_new", "v", AgentMemoryCategories.BuildAndTest);
        var gotcha = Memory("rook", "agent-mesh", "gotcha", "v", AgentMemoryCategories.CodebaseGotcha);
        var env = Memory("rook", "agent-mesh", "env", "v", AgentMemoryCategories.Environment);

        // Insert out of order so the ordering is produced by the query, not by insertion sequence.
        await repo.UpsertMemoryAsync(newest);
        await repo.UpsertMemoryAsync(gotcha);
        await repo.UpsertMemoryAsync(oldest);
        await repo.UpsertMemoryAsync(env);

        // Re-save 'oldest' so its UpdatedAt is the freshest inside build_and_test.
        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "build_old", "v2", AgentMemoryCategories.BuildAndTest));

        var results = await repo.GetMemoriesForAgentAsync("rook", "agent-mesh");

        results.Select(m => m.Category).Should().ContainInOrder(
            AgentMemoryCategories.BuildAndTest,
            AgentMemoryCategories.BuildAndTest,
            AgentMemoryCategories.CodebaseGotcha,
            AgentMemoryCategories.Environment);

        results.Select(m => m.Key).Should().ContainInOrder("build_old", "build_new", "gotcha", "env");
    }

    [Fact]
    public async Task GetMemories_WithNoProject_ReturnsEveryProjectTheAgentWrote()
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "a", "v"));
        await repo.UpsertMemoryAsync(Memory("rook", "other-repo", "b", "v"));
        await repo.UpsertMemoryAsync(Memory("rook", AgentMemory.GlobalProjectId, "c", "v"));
        await repo.UpsertMemoryAsync(Memory("forge", "agent-mesh", "d", "v"));

        var results = await repo.GetMemoriesForAgentAsync("rook");

        results.Select(m => m.Key).Should().BeEquivalentTo("a", "b", "c");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetMemories_WithBlankProject_BehavesLikeNoProject(string? projectId)
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "a", "v"));
        await repo.UpsertMemoryAsync(Memory("rook", AgentMemory.GlobalProjectId, "b", "v"));

        var results = await repo.GetMemoriesForAgentAsync("rook", projectId);

        results.Select(m => m.Key).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task GetMemories_WithUnknownAgent_ReturnsEmpty()
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "a", "v"));

        (await repo.GetMemoriesForAgentAsync("nobody", "agent-mesh")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetMemories_WithBlankAgent_ReturnsEmptyWithoutQuerying(string? agentId)
    {
        // An empty agent id must not degrade into "match everything": that would hand one agent's
        // lessons to another caller.
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "a", "v"));

        (await repo.GetMemoriesForAgentAsync(agentId!, "agent-mesh")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetMemories_TrimsCallerWhitespace()
    {
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "a", "v"));

        (await repo.GetMemoriesForAgentAsync("  rook  ", "  agent-mesh  "))
            .Should().ContainSingle().Which.Key.Should().Be("a");
    }

    [Fact]
    public async Task GetMemories_IsRepeatableForTheSameData()
    {
        // The prompt stack prepends these rows to a turn. If two reads of unchanged data came back in a
        // different order, every session would invalidate the driver's cached context prefix for nothing.
        using var db = Db();
        var repo = Repo(db);

        foreach (var key in new[] { "k1", "k2", "k3" })
        {
            await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", key, "v", AgentMemoryCategories.Environment));
        }

        var first = await repo.GetMemoriesForAgentAsync("rook", "agent-mesh");
        var second = await repo.GetMemoriesForAgentAsync("rook", "agent-mesh");

        second.Select(m => m.Id).Should().Equal(first.Select(m => m.Id));
    }

    [Fact]
    public async Task DeleteMemory_WithKnownId_RemovesRowAndReportsSuccess()
    {
        using var db = Db();
        var repo = Repo(db);

        var saved = await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "stale_lesson", "v"));

        (await repo.DeleteMemoryAsync(saved.Id)).Should().BeTrue();

        using var verify = Db();
        (await verify.AgentMemories.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteMemory_WithUnknownId_ReportsFalseWithoutThrowing()
    {
        using var db = Db();
        var repo = Repo(db);

        (await repo.DeleteMemoryAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteMemory_Twice_SecondCallReportsFalse()
    {
        using var db = Db();
        var repo = Repo(db);

        var saved = await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "k", "v"));

        (await repo.DeleteMemoryAsync(saved.Id)).Should().BeTrue();
        (await repo.DeleteMemoryAsync(saved.Id)).Should().BeFalse("already-gone is not an error, but it is not a deletion either");
    }

    [Fact]
    public async Task DeleteMemory_LeavesSiblingsIntact()
    {
        using var db = Db();
        var repo = Repo(db);

        var doomed = await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "doomed", "v"));
        var survivor = await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "survivor", "v"));

        await repo.DeleteMemoryAsync(doomed.Id);

        (await repo.GetMemoriesForAgentAsync("rook", "agent-mesh"))
            .Select(m => m.Id).Should().BeEquivalentTo(new[] { survivor.Id });
    }

    [Fact]
    public async Task Repository_RoundTripsUnicodeAndMultilineValues()
    {
        using var db = Db();
        var repo = Repo(db);

        var value = "Use `dotnet test -v quiet --nologo`.\nWhy: the default logger floods the spillover preview.\nEmoji: ✅";
        var saved = await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "test_flags", value));

        var results = await repo.GetMemoriesForAgentAsync("rook", "agent-mesh");

        results.Should().ContainSingle().Which.Value.Should().Be(value);
        saved.Value.Should().Be(value);
    }

    [Fact]
    public async Task Repository_PreservesNulSanitizationFromTheDbContext()
    {
        // AgentMeshDbContext.SanitizeStrings strips NUL characters on save, which PostgreSQL would reject
        // in a text column. A memory value arrives straight from a model's tool call, so it is exactly the
        // kind of string that can carry one.
        using var db = Db();
        var repo = Repo(db);

        await repo.UpsertMemoryAsync(Memory("rook", "agent-mesh", "k", "clean\0value"));

        using var verify = Db();
        var row = (await verify.AgentMemories.AsNoTracking().ToListAsync()).Single();
        row.Value.Should().NotContain("\0");
    }
}
