using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Data.Configurations;
using AgentMesh.Infrastructure.Migrations;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace AgentMesh.Tests.Data;

/// <summary>
/// The database shape <see cref="AgentMesh.Infrastructure.Repositories.AgentMemoryRepository"/> depends
/// on, asserted against the EF model rather than against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// <para>
/// The repository's upsert is only idempotent because the unique index exists: two concurrent saves of a
/// brand-new key both miss the find-then-update lookup, and it is the index that turns the loser's insert
/// into a catchable violation instead of a duplicate row. The in-memory provider used everywhere else in
/// this assembly does not enforce indexes, so that guarantee has to be pinned where it is declared — on
/// the model.
/// </para>
/// <para>
/// The model is read through the Npgsql provider without opening a connection, the same technique
/// <c>SessionPagingSqlTranslationTests</c> uses. That yields the real column types and index definitions
/// PostgreSQL would get, which is what catches a dropped <c>IsUnique()</c>, a widened column, or a renamed
/// index — all invisible to the behavioural tests.
/// </para>
/// </remarks>
public class AgentMemorySchemaTests
{
    private const string TableName = "agent_memories";
    private const string MigrationId = "20261030051800_AddAgentMemoriesTable";

    private static IEntityType MemoryEntity()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(AgentMemory));

        entity.Should().NotBeNull($"the model must map {nameof(AgentMemory)}");
        return entity!;
    }

    private static IProperty Property(string columnName)
    {
        var entity = MemoryEntity();
        var property = entity.GetProperties().FirstOrDefault(p =>
            string.Equals(p.GetColumnName(), columnName, StringComparison.Ordinal));

        property.Should().NotBeNull($"'{columnName}' must be mapped on {TableName}");
        return property!;
    }

    [Fact]
    public void AgentMemories_IsMappedOnTheDbContext()
    {
        using var db = TestDbContextFactory.Create();

        db.AgentMemories.Should().NotBeNull();
        db.Model.FindEntityType(typeof(AgentMemory)).Should().NotBeNull();
    }

    [Fact]
    public void Entity_MapsToTheSpecifiedTableName()
    {
        MemoryEntity().GetTableName().Should().Be(TableName);
    }

    [Fact]
    public void Table_ColumnsMatchTheSpecifiedSchema()
    {
        MemoryEntity().GetProperties()
            .Select(p => p.GetColumnName())
            .Should()
            .BeEquivalentTo(
                "id", "agent_id", "project_id", "category", "key", "value",
                "confidence_score", "created_at", "updated_at");
    }

    [Theory]
    [InlineData("id", "uuid")]
    [InlineData("agent_id", "character varying(64)")]
    [InlineData("project_id", "character varying(64)")]
    [InlineData("category", "character varying(32)")]
    [InlineData("key", "character varying(128)")]
    [InlineData("value", "text")]
    [InlineData("confidence_score", "double precision")]
    [InlineData("created_at", "timestamp with time zone")]
    [InlineData("updated_at", "timestamp with time zone")]
    public void Table_ColumnTypesMatchTheSpecifiedSchema(string column, string expectedType)
    {
        Property(column).GetColumnType().Should().Be(expectedType);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("agent_id")]
    [InlineData("project_id")]
    [InlineData("category")]
    [InlineData("key")]
    [InlineData("value")]
    [InlineData("confidence_score")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    public void Table_EveryColumnIsNotNull(string column)
    {
        // A nullable key column would let a row escape the unique index entirely: NULLs never collide.
        Property(column).IsColumnNullable().Should().BeFalse($"every column is NOT NULL in the specified schema");
    }

    [Theory]
    [InlineData("agent_id", 64)]
    [InlineData("project_id", 64)]
    [InlineData("category", 32)]
    [InlineData("key", 128)]
    public void Table_ColumnLengthsMatchTheSpecifiedSchema(string column, int maxLength)
    {
        Property(column).GetMaxLength().Should().Be(maxLength);
    }

    [Fact]
    public void Table_ColumnDefaultsMatchTheSpecifiedSchema()
    {
        Property("id").GetDefaultValueSql().Should().Be("gen_random_uuid()");
        Property("project_id").GetDefaultValue().Should().Be(AgentMemory.GlobalProjectId);
        Property("confidence_score").GetDefaultValue().Should().Be(1.0d);
        Property("created_at").GetDefaultValueSql().Should().Be("CURRENT_TIMESTAMP");
        Property("updated_at").GetDefaultValueSql().Should().Be("CURRENT_TIMESTAMP");
    }

    [Fact]
    public void Table_HasPrimaryKeyOnId()
    {
        var key = MemoryEntity().FindPrimaryKey();

        key.Should().NotBeNull();
        key!.Properties.Should().ContainSingle().Which.GetColumnName().Should().Be("id");
    }

    [Fact]
    public void Table_HasUniqueIndexOnAgentProjectKey()
    {
        // This is the assertion that protects the upsert. Remove IsUnique() from the configuration and
        // concurrent saves of the same new key start producing duplicate lessons.
        var unique = MemoryEntity().GetIndexes()
            .SingleOrDefault(i => i.IsUnique && i.Properties.Select(p => p.GetColumnName()).SequenceEqual(
                new[] { "agent_id", "project_id", "key" }));

        unique.Should().NotBeNull(
            "the upsert contract is enforced by a unique index on (agent_id, project_id, key); the repository's collision retry depends on it");
        unique!.GetDatabaseName().Should().Be("uq_agent_project_key");
    }

    [Fact]
    public void Table_HasLookupIndexOnAgentAndProject()
    {
        // The read path is `WHERE agent_id = @x AND (project_id = @y OR project_id = '*')`. The OR stops
        // the planner from seeking the unique index's project_id column, so this narrower index is what
        // the prompt-assembly sweep uses.
        var lookup = MemoryEntity().GetIndexes()
            .SingleOrDefault(i => !i.IsUnique && i.Properties.Select(p => p.GetColumnName()).SequenceEqual(
                new[] { "agent_id", "project_id" }));

        lookup.Should().NotBeNull("prompt assembly reads an agent's memories on every session start");
        lookup!.GetDatabaseName().Should().Be("idx_agent_memories_lookup");
    }

    [Fact]
    public void Table_HasExactlyTheTwoIndexesTheReadAndWritePathsNeed()
    {
        MemoryEntity().GetIndexes().Select(i => i.GetDatabaseName()).Should().BeEquivalentTo(
            "uq_agent_project_key", "idx_agent_memories_lookup");
    }

    [Fact]
    public void Category_ColumnIsWideEnoughForEveryDeclaredBucket()
    {
        var maxLength = Property("category").GetMaxLength();

        // A bucket that does not fit is a runtime truncation error, not a compile error, so the column
        // width and the constant list have to be checked against each other.
        maxLength.Should().NotBeNull();
        AgentMemoryCategories.All.Should().OnlyContain(
            category => category.Length <= maxLength!,
            $"adding a category longer than {maxLength} characters needs the column widened first");
    }

    [Fact]
    public void AddAgentMemoriesTableMigration_CreatesTheTableIdempotently()
    {
        // The spec asks for an idempotent migration: this project runs MigrateAsync() at startup, so a
        // half-applied migration must be re-runnable rather than fatal. Asserted on the emitted
        // operations, which is what the migration actually sends.
        var sql = string.Join('\n', UpOperations().OfType<SqlOperation>().Select(o => o.Sql));

        sql.Should().Contain("CREATE TABLE IF NOT EXISTS");
        sql.Should().Contain("CREATE UNIQUE INDEX IF NOT EXISTS");
        sql.Should().Contain("CREATE INDEX IF NOT EXISTS");
        sql.Should().Contain("uq_agent_project_key");
        sql.Should().Contain("idx_agent_memories_lookup");
        sql.Should().Contain("gen_random_uuid()");
        sql.Should().Contain("DEFAULT '*'");
    }

    [Fact]
    public void AddAgentMemoriesTableMigration_TouchesNoOtherTable()
    {
        // A migration that quietly starts altering a second table is the one an idempotency guard on the
        // first table would not catch.
        var sql = string.Join('\n', UpOperations().OfType<SqlOperation>().Select(o => o.Sql));

        sql.Should().NotBeEmpty("the migration must emit DDL");
        SplitStatements(sql).Should().OnlyContain(
            statement => ReferencesOnlyMemories(statement),
            $"every statement in this migration should target '{TableName}' and nothing else");
    }

    [Fact]
    public void AddAgentMemoriesTableMigration_EmitsNoNonSqlOperations()
    {
        // The guards only work because the DDL is hand-written SQL. A generated CreateTableOperation would
        // bypass them, so the migration's operation type is part of the contract.
        UpOperations().Should().OnlyContain(operation => operation is SqlOperation);
        DownOperations().Should().OnlyContain(operation => operation is SqlOperation);
    }

    [Fact]
    public void AddAgentMemoriesTableMigration_DownDropsTheTable()
    {
        var sql = string.Join('\n', DownOperations().OfType<SqlOperation>().Select(o => o.Sql));

        sql.Should().Contain("DROP TABLE IF EXISTS");
        sql.Should().Contain(TableName);
    }

    /// <summary>
    /// The migration currently at the tip of the sequence.
    /// </summary>
    /// <remarks>
    /// <see cref="TipMigrationId"/> is the tripwire's current value, updated by whoever stacks the next
    /// migration. See <see cref="MigrationSequence_KeepsMigrationsInOrder"/>.
    /// </remarks>
    private const string TipMigrationId = "20261030171746_AddMultiRepoPathsAndTaskCheckpoints";

    [Fact]
    public void MigrationSequence_KeepsMigrationsInOrder()
    {
        // Migrations apply in id order. Pinning the tip means a later migration stacked on top of this one
        // shows up as a failure to re-check rather than a silent assumption that the snapshot is current.
        //
        // The tripwire fired when AddMultiRepoPathsAndTaskCheckpoints was added, and re-checking it is
        // what produced the assertions below: agent_memories is no longer the tip, so "is the tip" is no
        // longer the invariant worth holding. What is still worth holding is that it is present, that it
        // is ordered after the tables its foreign keys read, and that exactly one migration sits above it
        // — so a fifth migration trips this test again rather than sliding in unnoticed.
        using var db = TestDbContextFactory.Postgres();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        migrations.Should().Contain(MigrationId);
        migrations.Should().Contain(TipMigrationId);

        var agentMemoriesIndex = migrations.IndexOf(MigrationId);
        var tipIndex = migrations.IndexOf(TipMigrationId);

        tipIndex.Should().Be(migrations.Count - 1,
            $"{TipMigrationId} is the expected tip; if a new migration was added, update TipMigrationId " +
            "and re-check that agent_memories' position is still correct");
        agentMemoriesIndex.Should().BeLessThan(tipIndex,
            "the memories table predates the multi-repo migration and must not be re-ordered after it");

        // The dependency that ordering actually protects: agent_memories' read path and the checkpoint's
        // create path both resolve a project, so projects must exist first.
        migrations.IndexOf("20261020184553_AddProjectsAndJobs")
            .Should().BeLessThan(agentMemoriesIndex,
                "agent_memories is scoped by project, so the projects table must be created first");

        migrations.Should().HaveCount(tipIndex + 1);
    }

    [Fact]
    public void ModelSnapshot_AgreesWithTheEntityConfiguration()
    {
        // Guards the failure mode where someone edits AgentMemoryConfiguration and generates no migration:
        // the snapshot is what MigrateAsync diffs against, so a stale snapshot means the deployed database
        // quietly lacks the column the code writes to.
        using var db = TestDbContextFactory.Postgres();
        var mapped = db.Model.FindEntityType(typeof(AgentMemory))!;

        var builder = new ModelBuilder();
        new AgentMemoryConfiguration().Configure(builder.Entity<AgentMemory>());
        var expected = builder.Model.FindEntityType(typeof(AgentMemory))!;

        foreach (var expectedProperty in expected.GetProperties())
        {
            var actual = mapped.FindProperty(expectedProperty.Name);

            actual.Should().NotBeNull($"{nameof(AgentMemory)}.{expectedProperty.Name} must be mapped");
            actual!.GetMaxLength().Should().Be(expectedProperty.GetMaxLength(),
                $"column width for {expectedProperty.Name} drifted between configuration and model");
            actual.IsNullable.Should().Be(expectedProperty.IsNullable,
                $"nullability for {expectedProperty.Name} drifted between configuration and model");
        }

        expected.GetIndexes().Select(i => (i.Properties.Select(p => p.Name).ToArray(), i.IsUnique))
            .Should().BeEquivalentTo(
                mapped.GetIndexes().Select(i => (i.Properties.Select(p => p.Name).ToArray(), i.IsUnique)),
                "indexes in the model snapshot must match the configuration");
    }

    private static IReadOnlyList<MigrationOperation> UpOperations() => Operations(up: true);

    private static IReadOnlyList<MigrationOperation> DownOperations() => Operations(up: false);

    private static IReadOnlyList<MigrationOperation> Operations(bool up)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var migration = new AddAgentMemoriesTable();

        // Up/Down are protected by design (the runner calls them); invoke through the base type so the
        // production migration needs no test-only seam.
        var method = typeof(Migration).GetMethod(up ? "Up" : "Down",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        method.Invoke(migration, new object[] { builder });
        return builder.Operations;
    }

    private static IEnumerable<string> SplitStatements(string sql) =>
        sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// True when a DDL statement names <c>agent_memories</c> and no other table this context maps.
    /// </summary>
    private static bool ReferencesOnlyMemories(string statement)
    {
        if (!statement.Contains(TableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var otherTables = new[]
        {
            "agents", "personas", "agent_sessions", "session_messages", "agent_jobs",
            "projects", "project_personas", "project_jobs", "job_messages",
            "job_handoffs", "job_workspace_states",
        };

        // Word-boundary matching so 'agent_memories' is not read as a hit for 'agent_sessions' and a
        // shared prefix does not match everything.
        return !otherTables.Any(other =>
            System.Text.RegularExpressions.Regex.IsMatch(
                statement,
                $@"(?<![a-z_]){System.Text.RegularExpressions.Regex.Escape(other)}(?![a-z_])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }
}
