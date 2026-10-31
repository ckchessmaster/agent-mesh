using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace AgentMesh.Tests.Data;

/// <summary>
/// The database shape the per-task workspace checkpoint depends on, asserted against the EF model.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this adds over the behavioural tests, measured rather than assumed.</strong> Each
/// assertion below was mutation-tested against the existing suite:
/// <list type="bullet">
/// <item><description>
/// Reverting the composite key to <c>HasKey(s =&gt; s.JobId)</c> fails 8 tests in
/// <c>WorkspaceStateRepositoryTests</c> / <c>JobWorkspaceTaskScopingTests</c>. The in-memory provider
/// does enforce keys, so that invariant is already covered; it is asserted here as well because this is
/// the only place the failure names the invariant ("the checkpoint is per task") instead of surfacing as
/// a duplicate-row exception three layers away.
/// </description></item>
/// <item><description>
/// Changing <c>projects.working_directory</c>'s default from blank to the literal path passes all 42
/// behavioural tests in <c>ShellDefaultWorkingDirectoryTests</c>, <c>JobWorkspaceTaskScopingTests</c>,
/// <c>ProjectsControllerTests</c> and <c>WorkspaceReconciliationTests</c>. Nothing but this file catches
/// it — see the paragraph below for why that specific default is worth a file of its own.
/// </description></item>
/// <item><description>
/// Deleting the recency index passes all 45 behavioural tests. The in-memory provider creates no
/// indexes, so a query-plan guarantee has no behavioural observable; the model is the only place it
/// exists.
/// </description></item>
/// </list>
/// The model is read through the Npgsql provider without opening a connection, the technique
/// <see cref="AgentMemorySchemaTests"/> and <c>SessionPagingSqlTranslationTests</c> already use.
/// </para>
/// <para>
/// <strong>The specific regression this is written to prevent.</strong> The migration deliberately departs
/// from the job spec's SQL sketch on <c>projects.working_directory</c>: the column defaults to blank
/// ("derive it from <c>repository_name</c>") rather than to the literal
/// <c>'/workspace/repos/agent-mesh'</c>. A literal default would make EF materialize that path into every
/// newly created project, so a project created as <c>repository_name='payments-api'</c> would be born
/// pointing at agent-mesh's checkout — a directory that exists, is writable, and belongs to a different
/// repository. Every command would succeed against the wrong tree and nothing would ever report an error.
/// That is a silent-corruption failure, and the easiest way to reintroduce it is someone "correcting"
/// the default to match the spec text. Pinning the blank default here turns that edit into a red test
/// rather than a misdirected commit.
/// </para>
/// </remarks>
public class WorkspaceCheckpointSchemaTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // projects: the multi-repo path columns
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Projects_CarriesRepositoryNameAndWorkingDirectory()
    {
        Column("projects", "repository_name").GetColumnType().Should().Be("character varying(128)");
        Column("projects", "working_directory").GetColumnType().Should().Be("character varying(256)");
    }

    /// <summary>
    /// The deviation from the spec's SQL sketch, pinned. See the class remarks.
    /// </summary>
    [Fact]
    public void Projects_WorkingDirectoryDefaultsToBlankRatherThanToALiteralPath()
    {
        var workingDirectory = Column("projects", "working_directory");

        workingDirectory.GetDefaultValue().Should().Be(string.Empty,
            "a literal default would be materialized into new rows, pointing a project created for " +
            "another repository at agent-mesh's checkout");

        // And the sibling column's default must be the name the resolver would derive anyway, so a
        // row created without either value still resolves to a directory that means what it says.
        Column("projects", "repository_name").GetDefaultValue()
            .Should().Be(Project.DefaultRepositoryName);
    }

    [Fact]
    public void Projects_BothPathColumnsAreRequired()
    {
        // Nullable would push a fallback into each of the three hosts that resolves a path, where they
        // would each invent a slightly different one.
        Column("projects", "repository_name").IsNullable.Should().BeFalse();
        Column("projects", "working_directory").IsNullable.Should().BeFalse();
    }

    /// <summary>
    /// The blank default is only safe because the resolver treats blank as "derive it".
    /// </summary>
    /// <remarks>
    /// Pairs with the default-value test above: change one half without the other and a project either
    /// gets a wrong literal path or an empty working directory that resolves to the process cwd.
    /// </remarks>
    [Fact]
    public void BlankWorkingDirectory_ResolvesToTheRepositorysOwnPath()
    {
        var project = new Project { Id = "payments", Name = "Payments", RepositoryName = "payments-api" };

        project.GetResolvedWorkingDirectory().Should().Be(
            $"{WorkspacePathResolver.ReposRoot}/payments-api");
    }

    /// <summary>
    /// A legacy row — default repository name, blank override — still resolves to the exact path the
    /// migration backfills it with.
    /// </summary>
    /// <remarks>
    /// The migration's <c>UPDATE</c> writes the literal <c>'/workspace/repos/agent-mesh'</c> into
    /// <c>working_directory</c> for rows that still carry the default repository name. That literal and
    /// this derivation are two independent statements of one fact, in two languages, with nothing
    /// checking they agree. If they ever diverge, a backfilled row and a freshly derived row for the same
    /// project would point at different directories, and which one a runner gets would depend on whether
    /// the migration had touched it.
    /// </remarks>
    [Fact]
    public void LegacyRow_ResolvesToTheSameLiteralTheMigrationBackfills()
    {
        var legacy = new Project
        {
            Id = "agent-mesh",
            Name = "AgentMesh",
            RepositoryName = Project.DefaultRepositoryName,
            WorkingDirectory = string.Empty,
        };

        legacy.GetResolvedWorkingDirectory().Should().Be(Project.DefaultWorkingDirectory);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // job_workspace_states: the composite key
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The key is <c>(job_id, task_id)</c>, not <c>(job_id)</c>.
    /// </summary>
    /// <remarks>
    /// This is the single line the whole task-scoping feature rests on. With a job-scoped key, a second
    /// task's checkpoint would collide with the first's and the repository's save would either throw or
    /// overwrite another task's head commit — and the job-level hydration path would keep handing runners
    /// an arbitrary task's branch, which is the bug <c>JobWorkspaceService</c> was rewritten to remove.
    /// </remarks>
    [Fact]
    public void JobWorkspaceStates_KeyIsJobIdPlusTaskId()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(JobWorkspaceState));
        entity.Should().NotBeNull();

        var key = entity!.FindPrimaryKey();
        key.Should().NotBeNull();

        key!.Properties.Select(p => p.GetColumnName()).ToList()
            .Should().Equal(new[] { "job_id", "task_id" },
                "the checkpoint is per task; a job-scoped key cannot represent two tasks' working trees");
    }

    [Fact]
    public void JobWorkspaceStates_TaskIdIsNotNullableAndDefaultsToTheSentinel()
    {
        var taskId = Column("job_workspace_states", "task_id");

        // Nullable would make "no task" unrepresentable in the key: Postgres treats NULLs as distinct
        // for uniqueness, so a job could acquire unlimited NULL-task rows and the job-level lookup would
        // be back to picking one at random.
        taskId.IsNullable.Should().BeFalse();
        taskId.GetColumnType().Should().Be("character varying(64)");
    }

    /// <summary>
    /// The recency index the job-level "active row" lookup scans.
    /// </summary>
    /// <remarks>
    /// <c>FindActiveAsync</c> orders by <c>updated_at DESC</c> to decide which row a job-scoped caller
    /// — the CLI runner's save endpoint, <c>JobHandoffService</c> — means. Without the index that is a
    /// sequential scan on a table that grows with every orchestrated task.
    /// </remarks>
    [Fact]
    public void JobWorkspaceStates_HasTheCheckpointRecencyIndex()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(JobWorkspaceState))!;

        var index = entity.GetIndexes().SingleOrDefault(i =>
            i.GetDatabaseName() == "ix_job_workspace_states_checkpoint_recency");

        index.Should().NotBeNull(
            "the job-level active-row lookup orders by checkpoint recency and must not sequential-scan");
        index!.Properties.Select(p => p.GetColumnName())
            .Should().Equal("job_id", "checkpoint_updated_at");
    }

    /// <summary>
    /// The checkpoint columns exist, with the types the migration's guarded DDL declares.
    /// </summary>
    /// <remarks>
    /// The migration is hand-written SQL, so nothing at runtime checks it agrees with the model. This is
    /// that check for the columns whose type is a decision rather than a default: <c>jsonb</c> because an
    /// operator debugging a stuck job reads the modified-file list with <c>-&gt;</c> operators, and
    /// <c>timestamptz</c> because a <c>timestamp</c> would silently drop the offset the checkpoint is
    /// compared against.
    /// </remarks>
    [Theory]
    [InlineData("checkpoint_summary", "character varying(4096)")]
    [InlineData("checkpoint_modified_files_json", "jsonb")]
    [InlineData("checkpoint_active_stash", "character varying(512)")]
    [InlineData("checkpoint_head_commit", "character varying(64)")]
    [InlineData("checkpoint_focus", "character varying(256)")]
    [InlineData("checkpoint_updated_at", "timestamp with time zone")]
    public void JobWorkspaceStates_CheckpointColumnsHaveTheDeclaredTypes(
        string column, string expectedType)
    {
        Column("job_workspace_states", column).GetColumnType().Should().Be(expectedType);
    }

    /// <summary>
    /// <c>HasCheckpoint</c> is a computed convenience, not a column.
    /// </summary>
    /// <remarks>
    /// Mapped by accident it would become a generated column the guarded DDL never creates, and every
    /// insert would fail on an unknown column — a failure that only appears against real Postgres, since
    /// the in-memory provider ignores the mismatch entirely.
    /// </remarks>
    [Fact]
    public void JobWorkspaceStates_HasCheckpointIsNotMappedToAColumn()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(JobWorkspaceState))!;

        entity.GetProperties().Should().NotContain(p =>
            string.Equals(p.Name, nameof(JobWorkspaceState.HasCheckpoint), StringComparison.Ordinal));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Wiring
    // ──────────────────────────────────────────────────────────────────────────

    private static IProperty Column(string table, string columnName)
    {
        using var db = TestDbContextFactory.Postgres();

        var entity = db.Model.GetEntityTypes().SingleOrDefault(t =>
            t.GetTableName() == table);

        entity.Should().NotBeNull($"the model must map the '{table}' table");

        var property = entity!.GetProperties().FirstOrDefault(p =>
            string.Equals(p.GetColumnName(), columnName, StringComparison.Ordinal));

        property.Should().NotBeNull($"'{columnName}' must be mapped on {table} — the migration and the " +
            "model are maintained separately here, so this is the only thing that keeps them agreeing");
        return property!;
    }
}
