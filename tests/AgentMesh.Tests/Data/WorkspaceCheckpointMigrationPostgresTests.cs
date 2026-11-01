using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AgentMesh.Tests.Data;

/// <summary>
/// The migration under review, executed against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this file exists when <see cref="WorkspaceCheckpointSchemaTests"/> already pins the
/// shape.</strong> That file reads the EF <em>model</em> through the Npgsql provider without opening a
/// connection. It catches type and default drift between the model and the snapshot, and it cannot do
/// anything else, because it never runs the SQL. <c>AddMultiRepoPathsAndTaskCheckpoints</c> is
/// hand-written guarded DDL — every statement is <c>Sql(...)</c>, not <c>AddColumn(...)</c> — so the
/// model and the migration are two separate descriptions of the same intent, and nothing in the
/// model-based suite can prove the DDL agrees with the model, or that it executes at all. Four
/// properties are only observable against a live server:
/// </para>
/// <list type="number">
/// <item><description>
/// The DDL <em>runs</em>. A guarded <c>DO $$ ... $$</c> block with a typo in it is invisible to the
/// model; it is a startup crash in production, because <c>Program.cs</c> calls
/// <c>MigrateAsync()</c> before the API serves a single request.
/// </description></item>
/// <item><description>
/// The guards hold on re-apply. The whole reason the DDL is guarded is that an operator may re-run it
/// against a database a previous attempt left half-built. That scenario is reproduced here by deleting
/// the history row and letting EF re-execute <c>Up()</c> through the real product path.
/// </description></item>
/// <item><description>
/// Legacy rows survive the re-key. <c>job_workspace_states</c> moves its primary key from
/// <c>(job_id)</c> to <c>(job_id, task_id)</c> while the table has rows in it. Whether those rows keep
/// their <c>git_ref</c>/<c>head_commit</c>/lease values, and whether they land on the
/// <c>'_unassigned'</c> sentinel, is a statement about data, not about a model.
/// </description></item>
/// <item><description>
/// <c>Down()</c> refuses rather than guesses. Its guard is a <c>RAISE EXCEPTION</c> in a PL/pgSQL
/// block. Against the in-memory or model-only view it does not exist.
/// </description></item>
/// </list>
/// <para>
/// <strong>Scope.</strong> This class targets the migration, not the repository. The behavioural
/// Postgres coverage for the checkpoint read/write path stays in
/// <see cref="Repositories.WorkspaceStateRepositoryTests"/>.
/// </para>
/// <para>
/// Runs inside <c>PostgresScratch</c> — see <see cref="PostgresFactAttribute"/> for why every class
/// that touches the scratch database has to share one non-parallel collection.
/// </para>
/// </remarks>
[Collection(PostgresScratchCollection.Name)]
public class WorkspaceCheckpointMigrationPostgresTests : IAsyncLifetime
{
    /// <summary>The migration under review.</summary>
    private const string TargetMigration = "20261030171746_AddMultiRepoPathsAndTaskCheckpoints";

    /// <summary>
    /// The head production sits at today, verified against the live
    /// <c>__EFMigrationsHistory</c> rather than assumed. Migrating to here first is what makes the
    /// upgrade path in these tests the same one the deployment will take.
    /// </summary>
    private const string ProductionHead = "20261030051800_AddAgentMemoriesTable";

    private readonly ITestOutputHelper _output;
    private string? _connection;
    private string? _unavailableReason;

    public WorkspaceCheckpointMigrationPostgresTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            _unavailableReason = $"'{PostgresFactAttribute.ConnectionVariable}' is not set";
            return;
        }

        _connection = connection;

        // Start from nothing. The sibling Postgres classes in this collection leave the scratch
        // database at whatever head they reached, and a migration test that inherits a half-built
        // schema proves nothing about a cold install.
        try
        {
            await using var db = NewDb();
            await db.Database.EnsureDeletedAsync();
        }
        catch (Exception ex)
        {
            // A scratch database that does not exist yet is the normal case, not a failure.
            _output.WriteLine("EnsureDeleted on setup did not complete: " + ex.Message);
        }

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_connection is null) return;

        try
        {
            await using var db = NewDb();
            await db.Database.EnsureDeletedAsync();
        }
        catch (Exception ex)
        {
            _output.WriteLine("Could not drop the scratch database: " + ex.Message);
        }

        await Task.CompletedTask;
    }

    private AgentMeshDbContext NewDb()
    {
        _connection.Should().NotBeNull("the fixture only reaches this point when the connection is set");
        return new AgentMeshDbContext(new DbContextOptionsBuilder<AgentMeshDbContext>()
            .UseNpgsql(_connection!)
            .UseSnakeCaseNamingConvention()
            .Options);
    }

    private void RequirePostgres() => _connection.Should().NotBeNull(
        "$" + PostgresFactAttribute.ConnectionVariable + " is set but PostgreSQL could not be reached; " +
        "the run asked for this coverage and did not get it. Cause: " + (_unavailableReason ?? "unknown"));

    /// <summary>
    /// The DDL executes, and the schema it produces agrees with the EF model that
    /// <see cref="WorkspaceCheckpointSchemaTests"/> asserts against.
    /// </summary>
    /// <remarks>
    /// This is the blocking item from the PR description: "the guarded <c>IF NOT EXISTS</c> form and the
    /// model↔DDL agreement are asserted against the Npgsql model, which catches type/default drift but
    /// cannot execute the SQL." Here the SQL runs, and the resulting catalog is compared against the
    /// two deliberate deviations from the job spec so a future edit to either side is caught.
    /// </remarks>
    [PostgresFact]
    public async Task Migration_AppliesToAFreshDatabase_AndTheDdlMatchesTheModel()
    {
        RequirePostgres();

        await using (var db = NewDb())
        {
            (await db.Database.GetPendingMigrationsAsync()).Should().Contain(TargetMigration,
                "a fresh scratch database must actually have the migration under review pending, " +
                "otherwise this test would pass without ever executing it");

            await db.Database.MigrateAsync();
        }

        await using var conn = await OpenAsync();

        // Deviation (1): working_directory defaults to '' so a new project derives its own path,
        // rather than to the spec's literal, which would birth every new project inside agent-mesh's
        // checkout.
        var workingDirectoryDefault = await ScalarAsync(conn, """
            select column_default from information_schema.columns
            where table_schema = 'public' and table_name = 'projects' and column_name = 'working_directory'
            """);
        workingDirectoryDefault.Should().NotBeNull("the column must exist in the executed DDL");
        workingDirectoryDefault.Should().StartWith("''::",
            "the deviation from the job spec is deliberate: a literal default would point a new " +
            "repository_name='payments-api' project at agent-mesh's checkout, and every command would " +
            "succeed against the wrong tree with nothing reporting an error");

        var repositoryNameDefault = await ScalarAsync(conn, """
            select column_default from information_schema.columns
            where table_schema = 'public' and table_name = 'projects' and column_name = 'repository_name'
            """);
        repositoryNameDefault.Should().Be("'agent-mesh'::character varying",
            "existing rows must resolve to the repository they have always meant, so no project's " +
            "path changes as a side effect of the column being added");

        // Deviation (2): the key is composite, which is what makes a per-task checkpoint representable.
        var keyColumns = await ScalarAsync(conn, """
            select string_agg(a.attname, ',' order by k.ordinality)
            from pg_constraint con
            join pg_class rel on rel.oid = con.conrelid
            join unnest(con.conkey) with ordinality as k(attnum, ordinality) on true
            join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum
            where con.conname = 'pk_job_workspace_states' and rel.relname = 'job_workspace_states'
            """);
        keyColumns.Should().Be("job_id,task_id",
            "the checkpoint describes one task's working tree; a job-only key lets a save overwrite " +
            "another task's head commit");

        var indexDef = await ScalarAsync(conn, """
            select indexdef from pg_indexes
            where schemaname = 'public' and indexname = 'ix_job_workspace_states_checkpoint_recency'
            """);
        indexDef.Should().NotBeNull("the recency index is what stops a restarted runner sorting the " +
            "whole job's history to find its last task");
        // Case-folded because Postgres renders the ordering keyword uppercase in pg_indexes.indexdef
        // while the migration writes it in capitals and the assertion reads more clearly in lower.
        indexDef!.Replace("\"", "").ToLowerInvariant().Should().Contain("job_id, checkpoint_updated_at desc",
            "the lookup is newest-first per job, so the index has to match that order");

        // The checkpoint columns must be nullable: "no checkpoint" has to be representable as all-null
        // rather than as an empty row, because HasCheckpoint keys off checkpoint_updated_at being set.
        foreach (var column in new[]
        {
            "checkpoint_branch", "checkpoint_status", "checkpoint_summary",
            "checkpoint_modified_files_json", "checkpoint_active_stash", "checkpoint_head_commit",
            "checkpoint_focus", "checkpoint_turn_session_id", "checkpoint_updated_at",
        })
        {
            var nullable = await ScalarAsync(conn, """
                select is_nullable from information_schema.columns
                where table_schema = 'public' and table_name = 'job_workspace_states' and column_name = @c
                """, ("c", column));
            nullable.Should().Be("YES", $"{column} must be nullable; a lease-only row has no checkpoint");
        }

        // NULL means "we never inspected the tree"; '[]' would mean "it is clean" and an agent that
        // believes that runs `git checkout .` on work that exists only in its stash.
        var modifiedFiles = await ScalarAsync(conn, """
            select data_type || '|' || coalesce(column_default, '<no-default>')
            from information_schema.columns
            where table_schema = 'public' and table_name = 'job_workspace_states'
              and column_name = 'checkpoint_modified_files_json'
            """);
        modifiedFiles.Should().Be("jsonb|<no-default>",
            "defaulting to '[]' would let a lease-only checkpoint assert a clean working tree");
    }

    /// <summary>
    /// Re-running the migration against an already-migrated database does not throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scenario the guards exist for, stated in the migration's own remarks: this project applies
    /// migrations both from <c>MigrateAsync()</c> at startup and by an operator against a database a
    /// previous attempt left half-built. If a prior attempt committed the <c>ADD CONSTRAINT</c> and then
    /// died on the index, the retry has to survive.
    /// </para>
    /// <para>
    /// Reproduced through the product path rather than by regex-parsing the migration source: the
    /// history row is deleted and EF is asked to migrate again, so the exact <c>Up()</c> SQL the
    /// operator would re-run is what executes.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Migration_ReAppliedToAnAlreadyMigratedDatabase_DoesNotThrow()
    {
        RequirePostgres();

        await using (var db = NewDb())
        {
            await db.Database.MigrateAsync();
        }

        // Simulate the half-built state: the DDL is applied, the history row is not.
        await using (var conn = await OpenAsync())
        {
            var deleted = await NonQueryAsync(conn, """
                delete from public."__EFMigrationsHistory" where migration_id = @m
                """, ("m", TargetMigration));
            deleted.Should().Be(1, "the target migration must be recorded before this test can un-record it");
        }

        await using (var db = NewDb())
        {
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            pending.Should().Contain(TargetMigration, "un-recording the history row must make it pending again");

            // The assertion is "does not throw". An unguarded ALTER TABLE ADD COLUMN would abort here,
            // turning a recoverable state into one that needs __EFMigrationsHistory edited by hand.
            var act = () => db.Database.MigrateAsync();
            await act.Should().NotThrowAsync(
                "the guarded DDL must be re-appliable to a database that already has it, which is the " +
                "stated reason every statement in Up() carries a guard");
        }

        // And the schema is still right afterwards — a guard that silently no-ops the PK rebuild would
        // also "not throw".
        await using (var check = await OpenAsync())
        {
            var keyColumns = await ScalarAsync(check, """
                select string_agg(a.attname, ',' order by k.ordinality)
                from pg_constraint con
                join pg_class rel on rel.oid = con.conrelid
                join unnest(con.conkey) with ordinality as k(attnum, ordinality) on true
                join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum
                where con.conname = 'pk_job_workspace_states' and rel.relname = 'job_workspace_states'
                """);
            keyColumns.Should().Be("job_id,task_id", "re-applying must not degrade the composite key");

            var indexCount = await ScalarAsync(check, """
                select count(*)::text from pg_indexes
                where schemaname = 'public' and indexname = 'ix_job_workspace_states_checkpoint_recency'
                """);
            indexCount.Should().Be("1", "CREATE INDEX IF NOT EXISTS must not leave a duplicate or drop the index");
        }
    }

    /// <summary>
    /// The upgrade path production will actually take: legacy rows present, key moved under them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-key happens while the table has rows in it, and two things have to be true at once: the
    /// existing data must survive untouched, and the new composite key must accept a second row per job.
    /// Neither is visible in the model.
    /// </para>
    /// <para>
    /// Seeded through raw SQL in the pre-migration column set, because the point is that these rows
    /// were written by the <em>old</em> shape. Writing them through the current entity model would seed
    /// <c>task_id</c> and the checkpoint columns that do not exist yet at that head.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Migration_FromTheProductionHead_PreservesLegacyRowsAndBackfillsThePath()
    {
        RequirePostgres();

        await using (var db = NewDb())
        {
            await db.Database.MigrateAsync(ProductionHead);
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Contain(ProductionHead,
                "the chain up to the production head must be applied");
            applied.Should().NotContain(TargetMigration,
                "the migration under review must still be pending, or this test would not be testing " +
                "an upgrade at all");
            applied[^1].Should().Be(ProductionHead,
                "the test must start where production actually is, or it is not the upgrade path");
        }

        var legacyJobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        var legacyProjectId = Guid.NewGuid().ToString("N");

        await using (var conn = await OpenAsync())
        {
            await NonQueryAsync(conn, """
                insert into public.projects
                    (id, name, description, git_repository_url, git_base_branch,
                     project_instructions, created_at, updated_at)
                values (@id, 'Legacy Project', 'pre-migration row',
                        'https://example.invalid/x.git', 'main', '', now(), now())
                """, ("id", legacyProjectId));

            foreach (var (jobId, gitRef, head) in new[]
            {
                (legacyJobId, "mesh/job-legacy-a", new string('a', 40)),
                (otherJobId, "mesh/job-legacy-b", new string('b', 40)),
            })
            {
                await NonQueryAsync(conn, """
                    insert into public.job_workspace_states
                        (job_id, project_id, git_remote_url, git_ref, head_commit, content_hash,
                         lease_holder_agent_id, lease_acquired_at, updated_at)
                    values (@job, @proj, 'https://example.invalid/x.git', @gitref, @head,
                            repeat('c', 64), 'agy-rook', now(), now())
                    """,
                    ("job", jobId), ("proj", legacyProjectId), ("gitref", gitRef), ("head", head));
            }
        }

        await using (var db = NewDb())
        {
            await db.Database.MigrateAsync(TargetMigration);
        }

        await using (var check = await OpenAsync())
        {
            // The backfill the migration promises for legacy rows.
            var backfilled = await ScalarAsync(check, """
                select working_directory from public.projects where id = @id
                """, ("id", legacyProjectId));
            backfilled.Should().Be("/workspace/repos/agent-mesh",
                "the spec's stated outcome still holds for existing data: legacy projects resolve to " +
                "the same path they always did");

            var repoName = await ScalarAsync(check, """
                select repository_name from public.projects where id = @id
                """, ("id", legacyProjectId));
            repoName.Should().Be("agent-mesh",
                "no existing project's resolved path may change as a side effect of the column being added");

            // The sentinel: converted rows are job-level state that was never about a checklist item.
            var sentinelCount = await ScalarAsync(check, """
                select count(*)::text from public.job_workspace_states where task_id = '_unassigned'
                """);
            sentinelCount.Should().Be("2",
                "legacy rows must land on the sentinel, which is what JobWorkspaceService treats as the " +
                "job-level fallback");

            // The data itself. A re-key that rebuilds the table can silently drop columns.
            var refs = await ScalarAsync(check, """
                select string_agg(coalesce(git_ref, '<null>'), ',' order by git_ref)
                from public.job_workspace_states
                """);
            refs.Should().Be("mesh/job-legacy-a,mesh/job-legacy-b",
                "a branch name lost during the re-key is a workspace that cannot be rehydrated");

            var intact = await ScalarAsync(check, """
                select count(*)::text from public.job_workspace_states
                where head_commit in (repeat('a', 40), repeat('b', 40))
                  and content_hash = repeat('c', 64)
                  and lease_holder_agent_id = 'agy-rook'
                """);
            intact.Should().Be("2", "head commit, content hash and lease holder must survive the re-key");

            // The capability the re-key exists to buy: two task rows for one job.
            await NonQueryAsync(check, """
                insert into public.job_workspace_states
                    (job_id, task_id, project_id, git_ref, head_commit, content_hash, updated_at,
                     checkpoint_branch, checkpoint_head_commit, checkpoint_updated_at, checkpoint_status)
                values (@job, @task, @proj, 'mesh/job-legacy-a', 'cc', 'dd', now(),
                        'mesh/job-legacy-a', repeat('e', 40), now(), 'clean')
                """,
                ("job", legacyJobId), ("task", Guid.NewGuid().ToString("N")), ("proj", legacyProjectId));

            var perJob = await ScalarAsync(check, """
                select count(*)::text from public.job_workspace_states where job_id = @j
                """, ("j", legacyJobId));
            perJob.Should().Be("2", "one job must be able to hold two task rows — that is the re-key's purpose");

            // ...and the old job-only shape must now be rejected, by the constraint the DDL created.
            var duplicate = await Assert.ThrowsAsync<PostgresException>(async () =>
                await NonQueryAsync(check, """
                    insert into public.job_workspace_states
                        (job_id, task_id, project_id, git_ref, head_commit, content_hash, updated_at)
                    values (@job, '_unassigned', @proj, 'dup', 'c', 'd', now())
                    """, ("job", legacyJobId), ("proj", legacyProjectId)));
            duplicate.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation,
                "the composite PK created by the DDL must actually reject a duplicate (job_id, task_id)");

            // The query a restarted runner makes, and the index that has to serve it.
            var recency = await ScalarAsync(check, """
                select task_id from public.job_workspace_states
                where job_id = @j and checkpoint_updated_at is not null
                order by checkpoint_updated_at desc limit 1
                """, ("j", legacyJobId));
            recency.Should().NotBeNull("the recency lookup has to return the task the agent was last on");

            // Honest scope: with a handful of rows the planner seq-scans because that is genuinely
            // cheaper. What is assertable is that the index can serve this predicate and ordering, so
            // take the seq path away and check the plan names the index.
            await NonQueryAsync(check, "set enable_seqscan = off");
            var plan = await PlanAsync(check, """
                select task_id from public.job_workspace_states
                where job_id = @j and checkpoint_updated_at is not null
                order by checkpoint_updated_at desc limit 5
                """, ("j", legacyJobId));
            await NonQueryAsync(check, "set enable_seqscan = on");

            _output.WriteLine(plan);
            plan.Should().Contain("ix_job_workspace_states_checkpoint_recency",
                "the recency index must be able to serve the runner's newest-task-per-job lookup; " +
                "asserted with seq-scan disabled because at this row count the planner is right to " +
                "seq-scan, and that is not a defect in the index");
        }
    }

    /// <summary>
    /// <c>Down()</c> refuses to downgrade a database where a job holds two task rows.
    /// </summary>
    /// <remarks>
    /// Restoring the single-column primary key is only sound if no job actually acquired a second row.
    /// A blind downgrade would silently pick one task's state to represent the whole job — the exact
    /// stale-state lie the feature exists to eliminate — so the migration raises and names the count
    /// instead. That refusal is a <c>RAISE EXCEPTION</c> inside PL/pgSQL and has no model-level
    /// observable whatsoever.
    /// </remarks>
    [PostgresFact]
    public async Task DownMigration_WhenAJobHoldsTwoTaskRows_RefusesRatherThanGuessing()
    {
        RequirePostgres();

        await using (var db = NewDb())
        {
            await db.Database.MigrateAsync();
        }

        var jobId = Guid.NewGuid();
        var projectId = Guid.NewGuid().ToString("N");
        await using (var conn = await OpenAsync())
        {
            await NonQueryAsync(conn, """
                insert into public.projects
                    (id, name, description, git_repository_url, git_base_branch,
                     project_instructions, created_at, updated_at)
                values (@id, 'Downgrade Project', '', 'https://example.invalid/x.git', 'main', '',
                        now(), now())
                """, ("id", projectId));

            // Two task rows for one job: the shape Down() refuses to collapse.
            for (var i = 0; i < 2; i++)
            {
                // task_id is varchar(64); a trimmed GUID is 32, so two distinct ones fit with room left.
                await NonQueryAsync(conn, """
                    insert into public.job_workspace_states
                        (job_id, task_id, project_id, git_ref, head_commit, content_hash, updated_at)
                    values (@job, @task, @proj, 'mesh/x', 'h', 'c', now())
                    """,
                    ("job", jobId),
                    ("task", Guid.NewGuid().ToString("N")),
                    ("proj", projectId));
            }
        }

        await using (var db = NewDb())
        {
            var act = () => db.Database.MigrateAsync(ProductionHead);

            var failure = (await act.Should().ThrowAsync<Exception>(
                "Down() must refuse rather than pick one task's state to represent the whole job")).Which;

            var inner = failure;
            var messages = new List<string>();
            while (inner is not null)
            {
                messages.Add(inner.Message);
                inner = inner.InnerException;
            }

            // The guard's own text, not just "some exception": a generic Npgsql failure would otherwise
            // satisfy the assertion for the wrong reason.
            string.Join(" | ", messages).Should().Contain("cannot downgrade")
                .And.Contain("more than one task row",
                    "the refusal must name why it stopped, since the operator is the one who has to act on it");
        }

        // And the refusal left the schema intact — a partial rollback that dropped the checkpoint
        // columns before raising would be worse than the guess it prevents.
        await using (var check = await OpenAsync())
        {
            var keyColumns = await ScalarAsync(check, """
                select string_agg(a.attname, ',' order by k.ordinality)
                from pg_constraint con
                join pg_class rel on rel.oid = con.conrelid
                join unnest(con.conkey) with ordinality as k(attnum, ordinality) on true
                join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum
                where con.conname = 'pk_job_workspace_states' and rel.relname = 'job_workspace_states'
                """);
            keyColumns.Should().Be("job_id,task_id",
                "the refused downgrade must leave the composite key in place, not half-removed");

            var taskRows = await ScalarAsync(check, """
                select count(*)::text from public.job_workspace_states where job_id = @j
                """, ("j", jobId));
            taskRows.Should().Be("2", "the refused downgrade must not have collapsed either task row");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_connection!);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<string?> ScalarAsync(NpgsqlConnection conn, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : result.ToString();
    }

    private static async Task<int> NonQueryAsync(NpgsqlConnection conn, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The planner's chosen plan for a query, as text.
    /// </summary>
    /// <remarks>
    /// <c>EXPLAIN</c> returns a result set rather than a scalar, and <c>FORMAT JSON</c> hands back one
    /// row holding the whole tree — so the tree is walked for every <c>Index Name</c> the planner
    /// settled on. Text format would work too, but it is locale- and version-sensitive; the JSON
    /// document is the stable surface.
    /// </remarks>
    private static async Task<string> PlanAsync(NpgsqlConnection conn, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand("explain (format json) " + sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);

        var json = await cmd.ExecuteScalarAsync();
        json.Should().NotBeNull("EXPLAIN always returns a row");

        using var doc = System.Text.Json.JsonDocument.Parse(json!.ToString()!);
        var names = new List<string>();
        CollectIndexNames(doc.RootElement, names);
        return string.Join(", ", names);
    }

    private static void CollectIndexNames(System.Text.Json.JsonElement node, List<string> names)
    {
        switch (node.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals("Index Name"u8) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        names.Add(property.Value.GetString()!);
                    CollectIndexNames(property.Value, names);
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) CollectIndexNames(item, names);
                break;
        }
    }
}
