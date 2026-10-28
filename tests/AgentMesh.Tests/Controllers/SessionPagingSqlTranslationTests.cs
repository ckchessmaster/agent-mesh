using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The controller unit tests run on the in-memory provider, which executes anything and therefore
/// cannot catch a query that fails to translate on PostgreSQL. Session paging is exactly the kind of
/// thing that breaks there: an aggregate carrying a sort node, or `Skip/Take` without a deterministic
/// `ORDER BY`, are both hazards.
///
/// `ToQueryString()` renders the command text through the Npgsql provider without opening a
/// connection, so these tests pin the real SQL shape with no database present.
/// </summary>
public class SessionPagingSqlTranslationTests
{
    private static AgentMeshDbContext Postgres() =>
        new(new DbContextOptionsBuilder<AgentMeshDbContext>()
            // Deliberately unreachable: nothing connects here, we only ask EF to render SQL.
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=translation-only;Username=x;Password=x")
            .Options);

    /// <summary>
    /// The exact queryable `CountAsync()` runs against in both controllers: filtered, never ordered.
    /// </summary>
    private static IQueryable<AgentSession> CountSource(AgentMeshDbContext db, string ownerId) =>
        db.AgentSessions.AsNoTracking().Where(s => s.PersonaId == ownerId);

    [Fact]
    public void CountSource_RendersOnPostgres_WithoutCarryingASortNode()
    {
        using var db = Postgres();

        var sql = CountSource(db, "rook").ToQueryString();

        sql.Should().Contain("SELECT").And.Contain("FROM agent_sessions");
        // The controllers count off the unordered queryable, so no sort can leak into the aggregate.
        sql.Should().NotContain("ORDER BY");
    }

    [Fact]
    public void PagedQuery_EmitsLimitOffsetWithDeterministicDescendingSort()
    {
        using var db = Postgres();

        var sql = CountSource(db, "rook")
            .OrderByDescending(s => s.UpdatedAt)
            .Skip(5)
            .Take(10)
            .Select(s => new { s.Id, s.Title })
            .ToQueryString();

        sql.Should().Contain("LIMIT").And.Contain("OFFSET");
        // OFFSET without ORDER BY would return a non-deterministic page, so rows could be skipped or
        // repeated between successive "Show all" clicks.
        sql.Should().MatchRegex("ORDER BY[^;]*\"UpdatedAt\" DESC");
    }

    [Fact]
    public void UnpagedQuery_OmitsLimitOffsetSoTheCliStillSeesEverything()
    {
        using var db = Postgres();

        var sql = CountSource(db, "rook")
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new { s.Id, s.Title })
            .ToQueryString();

        sql.Should().NotContain("LIMIT").And.NotContain("OFFSET");
        sql.Should().MatchRegex("ORDER BY[^;]*\"UpdatedAt\" DESC");
    }

    [Fact]
    public void DriverOwnerFilter_TranslatesAndPagesOnPostgres()
    {
        using var db = Postgres();

        // The agents endpoint ORs two owner columns; make sure that still renders with paging.
        var sql = db.AgentSessions
            .AsNoTracking()
            .Where(s => s.DriverId == "agy" || s.CreatorAgentId == "agy")
            .OrderByDescending(s => s.UpdatedAt)
            .Skip(3)
            .Take(5)
            .Select(s => new { s.Id })
            .ToQueryString();

        sql.Should().Contain(" OR ").And.Contain("LIMIT").And.Contain("OFFSET");
        sql.Should().MatchRegex("ORDER BY[^;]*\"UpdatedAt\" DESC");
    }
}
