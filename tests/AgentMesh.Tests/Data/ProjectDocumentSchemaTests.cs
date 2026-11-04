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
/// The database shape the project document library depends on, asserted against the EF model rather
/// than against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this pins that the controller tests cannot.</strong> The API tests run on the in-memory
/// provider, which enforces no indexes, applies no column defaults, and accepts any string length. So a
/// <c>title</c> widened past 255, a <c>content</c> column that silently became nullable, or the loss of
/// the <c>(project_id, title)</c> index is invisible to every behavioural test in the assembly — and the
/// last of those is the one that turns the per-turn prompt index into a sort over the whole table.
/// </para>
/// <para>
/// The model is read through the Npgsql provider without opening a connection, the same technique
/// <see cref="AgentMemorySchemaTests"/> uses, which yields the real column types PostgreSQL would get.
/// </para>
/// <para>
/// <strong>Why the migration's operation type is asserted.</strong> The guards only work because the DDL
/// is hand-written SQL. A generated <see cref="CreateTableOperation"/> would emit an unguarded
/// <c>CREATE TABLE</c> that aborts the whole migration transaction against a half-built database, so
/// "every operation is SQL" is the contract that keeps the guard in place, not a stylistic preference.
/// </para>
/// </remarks>
public class ProjectDocumentSchemaTests
{
    private const string TableName = "project_documents";
    private const string MigrationId = "20261104134515_AddProjectDocumentsTable";

    private static IEntityType DocumentEntity()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(ProjectDocument));

        entity.Should().NotBeNull($"the model must map {nameof(ProjectDocument)}");
        return entity!;
    }

    private static IProperty Property(string columnName)
    {
        var entity = DocumentEntity();
        var property = entity.GetProperties().FirstOrDefault(p =>
            string.Equals(p.GetColumnName(), columnName, StringComparison.Ordinal));

        property.Should().NotBeNull($"'{columnName}' must be mapped on {TableName}");
        return property!;
    }

    [Fact]
    public void ProjectDocuments_IsMappedOnTheDbContext()
    {
        using var db = TestDbContextFactory.Create();

        db.ProjectDocuments.Should().NotBeNull();
        db.Model.FindEntityType(typeof(ProjectDocument)).Should().NotBeNull();
    }

    [Fact]
    public void Entity_MapsToTheSpecifiedTableName()
    {
        DocumentEntity().GetTableName().Should().Be(TableName);
    }

    [Fact]
    public void Table_ColumnsMatchTheSpecifiedSchema()
    {
        DocumentEntity().GetProperties()
            .Select(p => p.GetColumnName())
            .Should()
            .BeEquivalentTo(
                "id", "project_id", "title", "kind", "content", "file_name",
                "content_type", "size_bytes", "s3key", "created_at", "updated_at");
    }

    [Theory]
    [InlineData("id", "uuid")]
    [InlineData("project_id", "character varying(64)")]
    [InlineData("title", "character varying(255)")]
    [InlineData("kind", "integer")]
    [InlineData("content", "text")]
    [InlineData("file_name", "character varying(255)")]
    [InlineData("content_type", "character varying(128)")]
    [InlineData("size_bytes", "bigint")]
    [InlineData("s3key", "character varying(512)")]
    [InlineData("created_at", "timestamp with time zone")]
    [InlineData("updated_at", "timestamp with time zone")]
    public void Table_ColumnTypesMatchTheSpecifiedSchema(string column, string expectedType)
    {
        Property(column).GetColumnType().Should().Be(expectedType);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("project_id")]
    [InlineData("title")]
    [InlineData("kind")]
    [InlineData("content")]
    [InlineData("size_bytes")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    public void Table_EveryRequiredColumnIsNotNull(string column)
    {
        // A nullable project_id would let a row escape every library read (WHERE project_id = @x never
        // matches NULL), and a nullable content would push a null check into the text-document render
        // path on every turn.
        Property(column).IsColumnNullable().Should().BeFalse($"'{column}' is NOT NULL in the specified schema");
    }

    [Theory]
    [InlineData("file_name")]
    [InlineData("content_type")]
    [InlineData("s3key")]
    public void Table_TheThreeFileOnlyColumnsAreNullable(string column)
    {
        // A pasted text document has no filename, no media type, and no object key. Making these
        // NOT NULL would force a text document to invent a filename, and the index would then advertise
        // a file that does not exist.
        Property(column).IsColumnNullable().Should().BeTrue(
            $"{column} describes an upload only; a text document must be storable with it unset");
    }

    [Theory]
    [InlineData("project_id", 64)]
    [InlineData("title", 255)]
    [InlineData("file_name", 255)]
    [InlineData("content_type", 128)]
    [InlineData("s3key", 512)]
    public void Table_ColumnLengthsMatchTheSpecifiedSchema(string column, int maxLength)
    {
        Property(column).GetMaxLength().Should().Be(maxLength);
    }

    [Fact]
    public void Table_ColumnDefaultsMatchTheSpecifiedSchema()
    {
        // The id default is what lets a caller insert without generating one, and the timestamp defaults
        // are what keep a raw SQL backfill from writing a row with an unset UpdatedAt.
        Property("id").GetDefaultValueSql().Should().Be("gen_random_uuid()");

        // Read back through the value converter, so the model reports the CLR value rather than the
        // integer the DDL will contain. The literal `DEFAULT 0` in the SQL is asserted separately in
        // AddProjectDocumentsTableMigration_DdlMatchesTheModelItClaimsToCreate — this half proves the
        // model's default is Text, that half proves PostgreSQL gets 0.
        Property("kind").GetDefaultValue().Should().Be(ProjectDocumentKind.Text);
        Property("content").GetDefaultValue().Should().Be(string.Empty);
        Property("size_bytes").GetDefaultValue().Should().Be(0L);
        Property("created_at").GetDefaultValueSql().Should().Be("CURRENT_TIMESTAMP");
        Property("updated_at").GetDefaultValueSql().Should().Be("CURRENT_TIMESTAMP");
    }

    [Fact]
    public void Table_HasPrimaryKeyOnId()
    {
        var key = DocumentEntity().FindPrimaryKey();

        key.Should().NotBeNull();
        key!.Properties.Should().ContainSingle().Which.GetColumnName().Should().Be("id");
    }

    [Fact]
    public void Table_HasLibraryIndexOnProjectAndTitle()
    {
        // Every read of this table is one project's whole set ordered by title: the list endpoint, the
        // count that enforces MaxDocumentsPerProject, and the prompt index. Losing this index does not
        // change any result — it changes the cost of a query that runs on every turn of every job.
        var library = DocumentEntity().GetIndexes()
            .SingleOrDefault(i => !i.IsUnique &&
                                  i.Properties.Select(p => p.GetColumnName())
                                      .SequenceEqual(new[] { "project_id", "title" }));

        library.Should().NotBeNull(
            "the library is always read as one project's set ordered by title, which is what makes the " +
            "prompt index byte-stable; without the index that read is a sort over the whole table");
        library!.GetDatabaseName().Should().Be("ix_project_documents_project_title");
    }

    [Fact]
    public void Table_HasExactlyTheOneIndexTheReadPathsNeed()
    {
        // The single-document read is a point lookup on the primary key plus a project_id filter, so a
        // second index would be pure write-time cost. Asserting the exact set is what stops a future
        // "optimisation" from adding one nobody asked for.
        DocumentEntity().GetIndexes().Select(i => i.GetDatabaseName()).Should().BeEquivalentTo(
            "ix_project_documents_project_title");
    }

    [Fact]
    public void Table_HasNoForeignKeyToProjects()
    {
        // Matches every other project-scoped table here (project_jobs, project_personas, agent_memories)
        // and the explicit child-deletion contract in DeleteProject. A constraint on this table alone
        // would change what DeleteProject is allowed to do while the other three stayed unconstrained.
        DocumentEntity().GetForeignKeys().Should().BeEmpty(
            "this schema keeps project_id as a plain indexed column by convention; adding a constraint " +
            "here would make DeleteProject's behaviour differ per child table");
    }

    [Fact]
    public void Project_Entity_HasNoNavigationToTheLibrary()
    {
        // The reason a Project load can never pull the library: there is no navigation and no
        // relationship in either direction, so EF has no path from projects to project_documents. A
        // navigation would make every project list query capable of dragging a `text` column with it, and
        // this context keeps no project child collections for exactly that reason.
        using var db = TestDbContextFactory.Postgres();
        var project = db.Model.FindEntityType(typeof(Project))!;

        project.GetNavigations().Should().BeEmpty(
            "projects is read by list queries; a navigation to a table with a text column would make the " +
            "library reachable from a call site that never asked for it");

        project.GetReferencingForeignKeys().Should().BeEmpty(
            "project_documents points at projects by convention-less id column, so no FK exists to cascade from");
    }

    [Fact]
    public void Kind_IsStoredAsItsNumericValue()
    {
        // Asserted because the storage form is what the DDL says (integer) and what a raw SQL query
        // would compare against. Switching to a string conversion silently changes the column type and
        // breaks the migration this test pins.
        Property("kind").GetColumnType().Should().Be("integer");
        Property("kind").GetProviderClrType().Should().Be(typeof(int));
    }

    [Fact]
    public void Title_ColumnIsWideEnoughForASanitisedFileName()
    {
        // An upload defaults its title to its filename. If the column were narrower than the filename
        // column, a legal 255-character upload would fail on insert with a truncation error rather than
        // with a 400 — the exact class of bug a width check between two columns catches.
        Property("title").GetMaxLength().Should().Be(Property("file_name").GetMaxLength());
    }

    [Fact]
    public void AddProjectDocumentsTableMigration_CreatesTheTableIdempotently()
    {
        // This project runs MigrateAsync() at startup, so a half-applied migration must be re-runnable
        // rather than fatal. Asserted on the emitted operations, which is what the migration sends.
        var sql = string.Join('\n', UpOperations().OfType<SqlOperation>().Select(o => o.Sql));

        sql.Should().Contain("CREATE TABLE IF NOT EXISTS");
        sql.Should().Contain("CREATE INDEX IF NOT EXISTS");
        sql.Should().Contain(TableName);
        sql.Should().Contain("ix_project_documents_project_title");
        sql.Should().Contain("gen_random_uuid()");
        sql.Should().Contain("CONSTRAINT pk_project_documents PRIMARY KEY (id)");
    }

    [Fact]
    public void AddProjectDocumentsTableMigration_DdlMatchesTheModelItClaimsToCreate()
    {
        // The one risk unique to a hand-written migration: the DDL and the model can disagree and both
        // look fine, because the snapshot is what EF diffs against while the SQL is what runs. Every
        // column the model declares must appear in the CREATE TABLE with its type and nullability.
        var sql = string.Join('\n', UpOperations().OfType<SqlOperation>().Select(o => o.Sql));
        var createTable = sql.Split(';').First(s => s.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase));

        foreach (var property in DocumentEntity().GetProperties())
        {
            var column = property.GetColumnName();
            var type = property.GetColumnType();

            createTable.Should().Contain(column,
                $"{column} is on the model but not in the migration's CREATE TABLE — the deployed " +
                "database would lack a column the code writes to");
            createTable.Should().Contain(type,
                $"{column} is declared as {type} on the model but the migration creates something else");

            if (property.IsColumnNullable())
            {
                createTable.Should().Contain($"{column} {type} NULL",
                    $"{column} is nullable on the model but NOT NULL in the DDL");
            }
            else
            {
                createTable.Should().Contain($"{column} {type} NOT NULL",
                    $"{column} is NOT NULL on the model but nullable in the DDL");
            }
        }
    }

    [Fact]
    public void AddProjectDocumentsTableMigration_TouchesNoOtherTable()
    {
        var sql = string.Join('\n', UpOperations().OfType<SqlOperation>().Select(o => o.Sql));

        sql.Should().NotBeEmpty("the migration must emit DDL");
        SplitStatements(sql).Should().OnlyContain(
            statement => ReferencesOnlyDocuments(statement),
            $"every statement in this migration should target '{TableName}' and nothing else");
    }

    [Fact]
    public void AddProjectDocumentsTableMigration_EmitsNoNonSqlOperations()
    {
        UpOperations().Should().OnlyContain(operation => operation is SqlOperation);
        DownOperations().Should().OnlyContain(operation => operation is SqlOperation);
    }

    [Fact]
    public void AddProjectDocumentsTableMigration_DownDropsTheTable()
    {
        var sql = string.Join('\n', DownOperations().OfType<SqlOperation>().Select(o => o.Sql));

        sql.Should().Contain("DROP TABLE IF EXISTS");
        sql.Should().Contain(TableName);
    }

    /// <summary>
    /// The migration currently at the tip of the sequence.
    /// </summary>
    /// <remarks>
    /// The tripwire from <see cref="AgentMemorySchemaTests"/> applied to this table: pinning the tip
    /// means the next migration stacked on top shows up as a failure to re-check rather than a silent
    /// assumption that the snapshot is current.
    /// </remarks>
    private const string TipMigrationId = MigrationId;

    [Fact]
    public void MigrationSequence_KeepsMigrationsInOrder()
    {
        using var db = TestDbContextFactory.Postgres();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        migrations.Should().Contain(MigrationId);

        var documentsIndex = migrations.IndexOf(MigrationId);
        var tipIndex = migrations.IndexOf(TipMigrationId);

        tipIndex.Should().Be(migrations.Count - 1,
            $"{TipMigrationId} is the expected tip; if a new migration was added, update TipMigrationId " +
            "and re-check that project_documents' position is still correct");

        // The dependency the ordering actually protects: project_documents is scoped by project_id and
        // has no FK, so nothing in the DDL enforces that projects exists first. The application-level
        // contract (upload into a project that is there) does, and it only holds if the table is created
        // after the projects table it points at.
        migrations.IndexOf("20261020184553_AddProjectsAndJobs")
            .Should().BeLessThan(documentsIndex,
                "project_documents is scoped by project_id, so projects must be created first");

        migrations.Should().HaveCount(tipIndex + 1);
    }

    [Fact]
    public void ModelSnapshot_AgreesWithTheEntityConfiguration()
    {
        // Guards the failure mode where someone edits ProjectDocumentConfiguration and generates no
        // migration: the snapshot is what MigrateAsync diffs against, so a stale snapshot means the
        // deployed database quietly lacks the column the code writes to.
        using var db = TestDbContextFactory.Postgres();
        var mapped = db.Model.FindEntityType(typeof(ProjectDocument))!;

        var builder = new ModelBuilder();
        new ProjectDocumentConfiguration().Configure(builder.Entity<ProjectDocument>());
        var expected = builder.Model.FindEntityType(typeof(ProjectDocument))!;

        foreach (var expectedProperty in expected.GetProperties())
        {
            var actual = mapped.FindProperty(expectedProperty.Name);

            actual.Should().NotBeNull($"{nameof(ProjectDocument)}.{expectedProperty.Name} must be mapped");
            actual!.GetMaxLength().Should().Be(expectedProperty.GetMaxLength(),
                $"column width for {expectedProperty.Name} drifted between configuration and model");
            actual.IsNullable.Should().Be(expectedProperty.IsNullable,
                $"nullability for {expectedProperty.Name} drifted between configuration and model");

            // Column *type* is deliberately not compared here. The bare ModelBuilder above has no
            // database provider, so every one of its properties reports a null store type while the
            // Npgsql-mapped model reports the resolved one ("uuid", "text", …) — the comparison would
            // fail on every column and prove nothing. Types are pinned against literals by
            // Table_ColumnTypesMatchTheSpecifiedSchema, and against the executed DDL by
            // AddProjectDocumentsTableMigration_DdlMatchesTheModelItClaimsToCreate.
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
        var migration = new AddProjectDocumentsTable();

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
    /// True when a DDL statement names <c>project_documents</c> and no other table this context maps.
    /// </summary>
    private static bool ReferencesOnlyDocuments(string statement)
    {
        if (!statement.Contains(TableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var otherTables = new[]
        {
            "agents", "personas", "agent_sessions", "session_messages", "agent_jobs",
            "projects", "project_personas", "project_jobs", "job_messages",
            "job_handoffs", "job_workspace_states", "agent_memories",
        };

        // Word-boundary matching so 'project_documents' is not read as a hit for 'project_personas' and
        // a shared prefix does not match everything.
        return !otherTables.Any(other =>
            System.Text.RegularExpressions.Regex.IsMatch(
                statement,
                $@"(?<![a-z_]){System.Text.RegularExpressions.Regex.Escape(other)}(?![a-z_])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }
}
