using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// Pins the SQL the job artifact read path emits, against the real Npgsql provider.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour tests in <c>ProjectJobsControllerArtifactTests</c> run on the InMemory provider, which
/// accepts any queryable and enforces no indexes and no translation rules. That is fine for the merge
/// logic and blind to everything that only shows up in production: a query EF cannot translate throws at
/// the endpoint rather than in a unit test, and a query that translates to a sequential scan gets slower
/// for the whole deployment as the tables grow. Both failure modes live in the SQL, so the SQL is asserted
/// here, with no database present — the same technique <see cref="SessionPagingSqlTranslationTests"/>
/// established for the session list.
/// </para>
/// <para>
/// The connection string points at an unreachable host on purpose. <c>ToQueryString()</c> asks the
/// provider to render SQL and never opens a socket.
/// </para>
/// </remarks>
public class JobArtifactSqlTranslationTests
{
    /// <summary>
    /// The repo's own Npgsql harness. It applies <c>UseSnakeCaseNamingConvention</c> explicitly, which
    /// <see cref="AgentMeshDbContext.OnConfiguring"/> only does for an otherwise-unconfigured builder —
    /// without it the rendered SQL names columns that do not exist in the deployed database, and the
    /// assertions below would be pinning a shape production never emits.
    /// </summary>
    private static AgentMeshDbContext Postgres() => TestDbContextFactory.Postgres();

    /// <summary>
    /// The turn-session lookup the read path starts with, exactly as <c>JobArtifactMirror</c> builds it.
    /// </summary>
    /// <remarks>
    /// This is the query that decides whether opening the Artifacts panel is cheap forever or gets worse
    /// with every session ever created. It must be driven by the <c>job_id</c> index on
    /// <c>job_messages</c>, not by a scan of <c>agent_sessions</c>.
    /// </remarks>
    [Fact]
    public void TurnSessionLookup_RendersAgainstJobMessages_AndCarriesNoSessionScan()
    {
        using var db = Postgres();
        var jobId = Guid.NewGuid();

        var sql = db.JobMessages
            .AsNoTracking()
            .Where(m => m.JobId == jobId && m.TurnSessionId != null)
            .Select(m => m.TurnSessionId!.Value)
            .Distinct()
            .ToQueryString();

        sql.Should().Contain("FROM job_messages");
        sql.Should().Contain("WHERE");
        sql.Should().Contain("turn_session_id");
        sql.Should().Contain("DISTINCT");
        // The whole point of keying history off job_messages: this lookup must never touch the sessions
        // table, let alone sort it.
        sql.Should().NotContain("agent_sessions");
        sql.Should().NotContain("ORDER BY");
    }

    /// <summary>
    /// The active-turn lookup that catches a turn in flight, before its job message exists.
    /// </summary>
    /// <remarks>
    /// The bound that keeps this from becoming a full-table scan is the status predicate, which is
    /// indexed (<c>ix_agent_sessions_status</c>). Asserting the predicate survives translation is the
    /// cheap way to guarantee nobody "simplifies" the query into an unbounded metadata scan later: the
    /// scan form is what it looks like when that happens, and it fails here instead of in production.
    /// </remarks>
    [Fact]
    public void ActiveTurnLookup_FiltersOnStatusBeforeAnyMetadataMatch()
    {
        using var db = Postgres();

        var sql = db.AgentSessions
            .AsNoTracking()
            .Where(s => s.Status == "thinking" || s.Status == "busy")
            .Select(s => s.Id)
            .ToQueryString();

        sql.Should().Contain("FROM agent_sessions");
        sql.Should().Contain("status");
        sql.Should().Contain("thinking").And.Contain("busy");
        // No jsonb operator and no substring match reaches the database: the jobId envelope is resolved in
        // C# over this already-bounded set, the same way ProjectJobsController.ExtractJobId does.
        sql.Should().NotContain("->>");
        sql.Should().NotContain("LIKE");
        sql.Should().NotContain("metadata");
    }

    /// <summary>
    /// The id fetch that follows both lookups.
    /// </summary>
    [Fact]
    public void SessionFetchByCollectedIds_RendersAsParameterisedInList()
    {
        using var db = Postgres();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var sql = db.AgentSessions
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.CreatedAt)
            .ToQueryString();

        sql.Should().Contain("FROM agent_sessions");
        // Npgsql renders a Contains() as a parameterised array test rather than an expanded IN list. That
        // is the shape we want: one parameter, a plan that is cacheable across jobs, and a sargable
        // predicate the primary-key index can serve. Asserting it here means a change to a client-side
        // ToList-then-filter (which would fetch the whole table) fails loudly.
        sql.Should().Contain("= ANY (");
        sql.Should().Contain("@ids");
        // Deterministic order, so a re-read of an unchanged job returns the same list and the panel does
        // not reorder under the cursor.
        sql.Should().Contain("ORDER BY").And.Contain("created_at");
    }

    /// <summary>
    /// The new column must be readable and writable through the model, or the endpoints 500 on first use.
    /// </summary>
    [Fact]
    public void ProjectJobArtifactsColumn_IsMappedAsJsonbOnTheProjectJobsTable()
    {
        using var db = Postgres();

        var sql = db.ProjectJobs.AsNoTracking().Select(j => j.ArtifactsJson).ToQueryString();

        sql.Should().Contain("FROM project_jobs");
        // Named `artifacts`, matching agent_jobs.artifacts rather than the property's default. Npgsql
        // leaves an all-lowercase identifier unquoted, so the token is asserted bare and the exact name
        // is pinned through the model below.
        sql.Should().Contain("SELECT p.artifacts");

        var property = db.Model.FindEntityType(typeof(AgentMesh.Core.Models.ProjectJob))!
            .FindProperty(nameof(AgentMesh.Core.Models.ProjectJob.ArtifactsJson))!;
        property.GetColumnType().Should().Be("jsonb");
        property.IsNullable.Should().BeFalse();
        property.GetDefaultValue().Should().Be("[]");
    }

    /// <summary>
    /// The migration's DDL and the model must agree, or the next scaffolded migration diffs a column that
    /// is already correct and someone ships a redundant ALTER.
    /// </summary>
    [Fact]
    public void ModelSnapshot_MatchesTheConfiguredArtifactsColumn()
    {
        using var db = Postgres();

        var snapshot = File.ReadAllText(SnapshotPath());
        var artifactsBlock = snapshot[snapshot.IndexOf(
            "b.Property<string>(\"ArtifactsJson\")", StringComparison.Ordinal)..];
        artifactsBlock = artifactsBlock[..artifactsBlock.IndexOf(";\n", StringComparison.Ordinal)];

        artifactsBlock.Should().Contain(".HasColumnType(\"jsonb\")");
        artifactsBlock.Should().Contain(".HasDefaultValue(\"[]\")");
        artifactsBlock.Should().Contain(".HasColumnName(\"artifacts\")");

        // And the migration that creates it exists and targets the same column.
        var migration = Directory.GetFiles(MigrationsDir(), "*AddProjectJobArtifacts.cs")
            .Single(f => !f.EndsWith(".Designer.cs"));
        var ddl = File.ReadAllText(migration);
        ddl.Should().Contain("ADD COLUMN IF NOT EXISTS artifacts jsonb NOT NULL DEFAULT '[]'::jsonb");
        ddl.Should().Contain("DROP COLUMN IF EXISTS artifacts");
    }

    private static string MigrationsDir()
    {
        // Tests run from bin/, so walk up to the repo root rather than hardcoding a depth that breaks
        // when the target framework folder changes.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgentMesh.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test assembly must live inside the repo for path-based asserts");
        return Path.Combine(dir!.FullName, "src", "AgentMesh.Infrastructure", "Migrations");
    }

    private static string SnapshotPath() =>
        Path.Combine(MigrationsDir(), "AgentMeshDbContextModelSnapshot.cs");
}
