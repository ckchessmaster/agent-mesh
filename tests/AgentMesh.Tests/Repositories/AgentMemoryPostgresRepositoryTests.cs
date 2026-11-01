using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AgentMesh.Tests.Repositories;

/// <summary>
/// The one guarantee the in-memory provider cannot give: the unique index really rejects a duplicate
/// <c>(agent_id, project_id, key)</c>, and the repository really turns that rejection into an update.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentMemoryRepository.UpsertMemoryAsync"/> does find-then-update rather than a native
/// <c>ON CONFLICT</c>, which leaves a window: two writers can both miss the lookup and both try to insert.
/// The unique index is what closes it — the loser gets SQLSTATE 23505, which the repository catches and
/// retries as an update. Nothing in the in-memory suite exercises that path, because that provider neither
/// enforces indexes nor raises <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
/// </para>
/// <para>
/// So these tests run against a real PostgreSQL, and are skipped when one is not configured — see
/// <see cref="PostgresFactAttribute"/> for why a skip is the right call and why it has to be decided at
/// discovery time. When the variable <em>is</em> set but the store cannot be reached, these fail rather
/// than skip: the operator asked for this coverage and did not get it, which is not something a green run
/// should be allowed to hide.
/// </para>
/// <para>
/// Point it at a scratch database with <c>AGENTMESH_TEST_POSTGRES_CONNECTION</c>, e.g.
/// <c>Host=127.0.0.1;Database=agentmesh_memorytest;Username=postgres;Password=postgres</c>. The database is
/// created and deleted by the fixture, so it must be a name dedicated to this purpose.
/// </para>
/// </remarks>
[Collection(PostgresScratchCollection.Name)]
public class AgentMemoryPostgresRepositoryTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private AgentMeshDbContext? _db;
    private string? _unavailableReason;

    public AgentMemoryPostgresRepositoryTests(ITestOutputHelper output) => _output = output;

    private AgentMeshDbContext Db => _db ?? throw new InvalidOperationException("context was not initialized");

    private AgentMemoryRepository Repo() => new(Db);

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            _unavailableReason = $"'{PostgresFactAttribute.ConnectionVariable}' is not set";
            return;
        }

        try
        {
            _db = new AgentMeshDbContext(new DbContextOptionsBuilder<AgentMeshDbContext>()
                .UseNpgsql(connection)
                .UseSnakeCaseNamingConvention()
                .Options);

            // Migrate, not EnsureCreated. Two reasons, and the second is the one that matters:
            // EnsureCreated builds straight from the model and never consults the migrations, so every test
            // in this class would pass against a schema the deployed database does not have. Running the
            // real chain means a broken or missing AddAgentMemoriesTable fails the behavioural tests too,
            // not just the two that name it.
            await _db.Database.EnsureDeletedAsync();
            await _db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            _unavailableReason = $"{ex.GetType().Name}: {ex.Message}";
            _db?.Dispose();
            _db = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_db is null)
        {
            return;
        }

        try
        {
            await _db.Database.EnsureDeletedAsync();
        }
        catch (Exception ex)
        {
            _output.WriteLine("Could not drop the scratch database: " + ex.Message);
        }

        _db.Dispose();
    }

    private void RequirePostgres()
    {
        // Reaching this with no context means the connection variable was set (the attribute would have
        // skipped the test otherwise) and the store named in it could not be used.
        // That is a failure, not a skip: the run asked for this coverage and did not get it.
        _db.Should().NotBeNull(
            "$" + PostgresFactAttribute.ConnectionVariable + " is set but PostgreSQL could not be reached; " +
            "the run asked for this coverage and did not get it. Cause: " + (_unavailableReason ?? "unknown"));
    }

    private static AgentMemory Memory(
        string agentId,
        string projectId,
        string key,
        string value,
        string category = AgentMemoryCategories.Environment) =>
        AgentMemory.TryCreate(agentId, projectId, category, key, value)!;

    [PostgresFact]
    public async Task UniqueIndex_RejectsADirectDuplicateOfAgentProjectKey()
    {
        // The precondition for every other test here, and the assertion the in-memory suite structurally
        // cannot make: if this index is ever dropped, concurrent saves start forking lessons.
        RequirePostgres();

        await Repo().UpsertMemoryAsync(Memory("pg-rook", "pg-project", "sdk_path", "/usr/share/dotnet"));

        var duplicate = Memory("pg-rook", "pg-project", "sdk_path", "/usr/lib/dotnet");
        Db.AgentMemories.Add(duplicate);

        var act = () => Db.SaveChangesAsync();

        var failure = (await act.Should().ThrowAsync<DbUpdateException>()).Subject.FirstOrDefault();

        // The violation must name the index the repository's retry path keys its diagnosis on. Asserting
        // the index name rather than only "a DbUpdateException happened" is what catches an unrelated
        // constraint (or a missing table) being mistaken for the collision this test is about.
        var message = (failure?.InnerException?.Message ?? string.Empty) + " " + (failure?.Message ?? string.Empty);
        message.Should().Contain("uq_agent_project_key");

        Db.Entry(duplicate).State = EntityState.Detached;
    }

    [PostgresFact]
    public async Task UpsertMemory_WhenExistingKey_UpdatesInPlaceWithoutCollision()
    {
        RequirePostgres();

        var first = await Repo().UpsertMemoryAsync(Memory("pg-rook", "pg-project", "sdk_path", "/usr/share/dotnet"));
        var second = await Repo().UpsertMemoryAsync(Memory("pg-rook", "pg-project", "sdk_path", "/usr/lib/dotnet"));

        second.Id.Should().Be(first.Id);
        second.CreatedAt.Should().Be(first.CreatedAt);
        second.UpdatedAt.Should().BeOnOrAfter(first.UpdatedAt);

        // Read through a second context so the assertion is about the committed row, not the tracker.
        await using var verify = new AgentMeshDbContext(new DbContextOptionsBuilder<AgentMeshDbContext>()
            .UseNpgsql(Db.Database.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options);

        var rows = await verify.AgentMemories.AsNoTracking()
            .Where(m => m.AgentId == "pg-rook" && m.ProjectId == "pg-project").ToListAsync();

        rows.Should().ContainSingle()
            .Which.Should().Match<AgentMemory>(m =>
                m.Id == first.Id && m.Value == "/usr/lib/dotnet" && m.CreatedAt == first.CreatedAt);
    }

    [PostgresFact]
    public async Task UpsertMemory_ConcurrentSavesOfOneNewKey_LeaveOneRow()
    {
        // The race the retry path exists for: several contexts, all seeing an empty table, all inserting.
        // One wins; the rest take 23505 and re-try as updates. The observable contract is that the lesson
        // is stored exactly once and every caller gets a usable row back.
        //
        // Eight writers rather than two, because the retry only runs if a collision actually happens. With
        // two, a fast first writer can commit before the second even queries, and the test would pass
        // without ever exercising the branch it exists to cover. Eight simultaneous inserts against an
        // empty table make a loser a certainty: PostgreSQL blocks each later inserter on the index until
        // the first commits, then rejects it.
        RequirePostgres();

        const int racers = 8;
        var connection = Db.Database.GetConnectionString();

        // One context per racer: EF's DbContext is not thread-safe, so sharing one across concurrent saves
        // would test the tracker's locking rather than the index.
        var contexts = Enumerable.Range(0, racers).Select(_ => NewContext(connection)).ToArray();

        try
        {
            var saves = contexts
                .Select((ctx, i) => new AgentMemoryRepository(ctx)
                    .UpsertMemoryAsync(Memory("pg-race", "pg-project", "sdk_path", $"/usr/lib/dotnet-{i}")))
                .ToArray();

            var results = await Task.WhenAll(saves);

            results.Should().HaveCount(racers, "no racer may be turned away by a collision");
            results.Should().OnlyContain(r => r.Key == "sdk_path");
            results.Select(r => r.Id).Distinct().Should().HaveCount(1,
                "every racer ends up on the one stored row, not on a private copy of it");

            await using var verify = NewContext(connection);
            var rows = await verify.AgentMemories.AsNoTracking()
                .Where(m => m.AgentId == "pg-race" && m.Key == "sdk_path").ToListAsync();

            rows.Should().ContainSingle();
            results.Should().Contain(r => r.Id == rows[0].Id);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    [PostgresFact]
    public async Task GetMemories_ReturnsProjectAndGlobalMemories_OnPostgres()
    {
        RequirePostgres();

        await Repo().UpsertMemoryAsync(Memory("pg-rook", "pg-project", "project_flag", "v", AgentMemoryCategories.BuildAndTest));
        await Repo().UpsertMemoryAsync(Memory("pg-rook", AgentMemory.GlobalProjectId, "global_flag", "v"));
        await Repo().UpsertMemoryAsync(Memory("pg-rook", "pg-other", "elsewhere", "v", AgentMemoryCategories.CodebaseGotcha));
        await Repo().UpsertMemoryAsync(Memory("pg-forge", "pg-project", "other_agent", "v"));

        var results = await Repo().GetMemoriesForAgentAsync("pg-rook", "pg-project");

        results.Select(m => m.Key).Should().BeEquivalentTo("project_flag", "global_flag");
    }

    [PostgresFact]
    public async Task GetMemories_TranslatesAndOrdersOnPostgres()
    {
        // The in-memory provider executes anything, so it cannot catch a query that fails to translate.
        // This one carries the OR-plus-ORDER-BY shape that prompt assembly runs on every session start.
        RequirePostgres();

        await Repo().UpsertMemoryAsync(Memory("pg-order", "pg-project", "b_old", "v", AgentMemoryCategories.BuildAndTest));
        await Repo().UpsertMemoryAsync(Memory("pg-order", "pg-project", "a_new", "v", AgentMemoryCategories.BuildAndTest));
        await Repo().UpsertMemoryAsync(Memory("pg-order", "pg-project", "z_env", "v", AgentMemoryCategories.Environment));

        var results = await Repo().GetMemoriesForAgentAsync("pg-order", "pg-project");

        results.Select(m => m.Category).Should().ContainInOrder(
            AgentMemoryCategories.BuildAndTest, AgentMemoryCategories.BuildAndTest, AgentMemoryCategories.Environment);
        results.Select(m => m.Key).Should().ContainInOrder("a_new", "b_old", "z_env");
    }

    [PostgresFact]
    public async Task GetMemories_QueryUsesTheLookupIndex()
    {
        // The read is on the session-start critical path. If it ever stops using idx_agent_memories_lookup
        // it degrades into a sequential scan over every agent's lessons, which is the kind of regression
        // that shows up as a slow dashboard rather than a failing test.
        RequirePostgres();

        await Repo().UpsertMemoryAsync(Memory("pg-plan", "pg-project", "k", "v"));

        var sql = Db.AgentMemories
            .Where(m => m.AgentId == "pg-plan" && (m.ProjectId == "pg-project" || m.ProjectId == AgentMemory.GlobalProjectId))
            .OrderBy(m => m.Category).ThenByDescending(m => m.UpdatedAt).ThenBy(m => m.Id)
            .ToQueryString();

        // Pinned on the SQL shape rather than on EXPLAIN output: the plan depends on table statistics, and
        // an empty scratch table would make PostgreSQL choose a scan regardless of what indexes exist.
        sql.Should().Contain("agent_id");

        // EF rewrites `project_id = @y OR project_id = '*'` into an IN list. That is the property worth
        // asserting, and stronger than the OR the repository writes: a single index-friendly predicate,
        // one round trip, and the global bucket included by construction rather than by a second query.
        sql.Should().Contain("project_id IN");
        sql.Should().Match(s => s.Contains("pg-plan") && s.Contains("'*'"),
            "the agent's own row set and the global bucket must both be in the one statement");
        sql.Should().MatchRegex("ORDER BY[^;]*category[^;]*updated_at DESC[^;]*id");
        sql.Should().NotContain("LIMIT", "the read is bounded by the agent's own memory count, not a page");
    }

    [PostgresFact]
    public async Task DeleteMemory_RemovesTheRowOnPostgres()
    {
        RequirePostgres();

        var saved = await Repo().UpsertMemoryAsync(Memory("pg-del", "pg-project", "k", "v"));

        (await Repo().DeleteMemoryAsync(saved.Id)).Should().BeTrue();
        (await Repo().DeleteMemoryAsync(saved.Id)).Should().BeFalse();

        await using var verify = NewContext(Db.Database.GetConnectionString());
        (await verify.AgentMemories.AsNoTracking().Where(m => m.AgentId == "pg-del").ToListAsync()).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Postgres_RejectsValuesTheDomainWouldHaveCaughtFirst()
    {
        // Documents why validation lives in the domain: these are the errors the database raises, and they
        // arrive as opaque provider exceptions with no field names. TryCreate is what lets the tool answer
        // "your key is too long" instead of "22001: value too long for type character varying(128)".
        RequirePostgres();

        await AssertColumnRejects("key", new string('k', AgentMemory.MaxKeyLength + 1));
        await AssertColumnRejects("agent_id", new string('a', AgentMemory.MaxAgentIdLength + 1));
        await AssertColumnRejects("category", new string('c', AgentMemory.MaxCategoryLength + 1));
    }

    private async Task AssertColumnRejects(string column, string overlongValue)
    {
        var memory = Memory("pg-limits", "pg-project", "k", "v");
        switch (column)
        {
            case "key": memory.Key = overlongValue; break;
            case "agent_id": memory.AgentId = overlongValue; break;
            case "category": memory.Category = overlongValue; break;
        }

        Db.AgentMemories.Add(memory);

        var act = () => Db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>($"'{column}' is limited at the database and must reject an over-long value");

        Db.Entry(memory).State = EntityState.Detached;
    }

    [PostgresFact]
    public async Task Migration_AddAgentMemoriesTable_AppliesToAFreshDatabase()
    {
        // The DoD asks for the migration to apply cleanly against the test PostgreSQL instance, and this is
        // the only place that claim can be checked: EnsureCreated (used by the fixture) builds from the
        // model and would pass even if the migration itself were broken or missing.
        //
        // Migrate() runs every migration in order, so this also proves AddAgentMemoriesTable coexists with
        // the eleven that precede it, and that the hand-written DDL leaves the table where the model says
        // it should be.
        RequirePostgres();

        // The fixture already ran the full chain, so reaching here means every migration applied to a
        // database that was empty a moment earlier.
        await using var migrated = NewContext(Db.Database.GetConnectionString());

        var history = await migrated.Database.SqlQueryRaw<string>(
            "SELECT migration_id AS Value FROM \"__EFMigrationsHistory\" ORDER BY migration_id").ToListAsync();

        history.Should().Contain(id => id.EndsWith("AddAgentMemoriesTable"));

        // The repository must work against the migrated schema, not merely against a created one: a
        // mismatch between migration DDL and model would surface here as a missing column or index.
        var saved = await new AgentMemoryRepository(migrated)
            .UpsertMemoryAsync(Memory("pg-migrate", "pg-project", "k", "v"));

        (await migrated.AgentMemories.AsNoTracking().Where(m => m.AgentId == "pg-migrate").ToListAsync())
            .Should().ContainSingle().Which.Id.Should().Be(saved.Id);

        // The unique index has to exist in the migrated database too; the repository's retry path is keyed
        // on the violation it raises.
        var indexNames = await migrated.Database.SqlQueryRaw<string>(
            "SELECT indexname AS Value FROM pg_indexes WHERE tablename = 'agent_memories'").ToListAsync();

        indexNames.Should().Contain(new[] { "pk_agent_memories", "uq_agent_project_key", "idx_agent_memories_lookup" });
    }

    [PostgresFact]
    public async Task Migration_IsIdempotent_WhenReappliedToADatabaseThatHasTheTable()
    {
        // The reason the DDL is guarded: Program.cs runs MigrateAsync() at startup, so a database left
        // half-built by a failed attempt must be recoverable by simply restarting the API. An unguarded
        // CREATE TABLE aborts the migration transaction there, and escaping that means editing
        // __EFMigrationsHistory by hand.
        RequirePostgres();

        // Simulate the half-built state against the schema the fixture just migrated: the table is present,
        // the history row is not.
        await Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"__EFMigrationsHistory\" WHERE migration_id LIKE '%AddAgentMemoriesTable'");

        await using var replying = NewContext(Db.Database.GetConnectionString());
        await replying.Database.MigrateAsync();

        // Re-applied without error, and the schema is still the one the model expects.
        var saved = await new AgentMemoryRepository(replying)
            .UpsertMemoryAsync(Memory("pg-idempotent", "pg-project", "k", "v"));
        saved.Id.Should().NotBeEmpty();

        var history = await replying.Database.SqlQueryRaw<string>(
            "SELECT migration_id AS Value FROM \"__EFMigrationsHistory\" WHERE migration_id LIKE '%AddAgentMemoriesTable'").ToListAsync();
        history.Should().ContainSingle();
    }

    private static AgentMeshDbContext NewContext(string? connection) =>
        new(new DbContextOptionsBuilder<AgentMeshDbContext>()
            .UseNpgsql(connection)
            .UseSnakeCaseNamingConvention()
            .Options);
}
