using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Activities;

/// <summary>
/// The discovery transcript query, rendered through the Npgsql provider.
/// <para>
/// <strong>Why this file exists.</strong> <c>ExecuteDiscoveryQaActivityAsync</c> used to find the job's
/// sessions with <c>s.MetadataJson.Contains(jobId)</c> inside a <c>where</c>. <c>agent_sessions.metadata</c>
/// is a jsonb column, PostgreSQL defines no <c>LIKE</c> for jsonb, and the server answered
/// <c>42883: operator does not exist: jsonb ~~ jsonb</c>. The activity caught that exception the same way it
/// catches a turn that produced no questions, so every job went straight from discovery to plan approval
/// while the questions the agent had already written sat unread in the transcript.
/// </para>
/// <para>
/// Nothing in the suite could see it, because the in-memory provider the activity tests run on translates
/// any LINQ you hand it. These tests render the real command text with no connection open, so the shape
/// PostgreSQL would receive is what gets asserted. See
/// <see cref="Controllers.SessionPagingSqlTranslationTests"/> for the same technique on session paging.
/// </para>
/// </summary>
public class DiscoveryTranscriptSqlTranslationTests
{
    private static readonly List<Guid> SessionIds = [Guid.NewGuid(), Guid.NewGuid()];

    private static AgentMeshDbContext Postgres() =>
        new(new DbContextOptionsBuilder<AgentMeshDbContext>()
            // Deliberately unreachable: nothing connects here, we only ask EF to render SQL.
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=translation-only;Username=x;Password=x")
            .Options);

    [Fact]
    public void TranscriptSource_NarrowsOnSessionIds_WithoutJoiningTheJsonbMetadataColumn()
    {
        using var db = Postgres();

        var sql = TimelineActivities.TranscriptSource(db, SessionIds)
            .Where(m => m.EventType == "response")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Content)
            .Take(10)
            .ToQueryString();

        sql.Should().Contain("FROM session_messages");
        // The job is named by id list, never by metadata, so no predicate can land on the jsonb column.
        sql.Should().NotContain("agent_sessions").And.NotContain("metadata");
        // "jsonb ~~ text" is the exact operator the server rejected; a LIKE anywhere near metadata means
        // the filter crept back into SQL.
        sql.Should().NotContain("LIKE");
        // An id list must travel as one bound array: interpolating the ids would make every render a
        // distinct command text and hand the plan cache a new query per job.
        sql.Should().MatchRegex(@"""SessionId""\s*=\s*ANY\s*\(@sessionIds\)");
    }

    [Fact]
    public void AskQuestionAndResponseReads_TranslateWithoutTouchingMetadata()
    {
        using var db = Postgres();

        // Take(1) stands in for FirstOrDefaultAsync: it renders the same single-row shape without
        // needing a server, which is the only thing a translation test can ask for.
        var askQuestion = TimelineActivities.TranscriptSource(db, SessionIds)
            .Where(m => m.ToolName == "ask_question")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.ToolArgsJson)
            .Take(1)
            .ToQueryString();
        var response = TimelineActivities.TranscriptSource(db, SessionIds)
            .Where(m => m.EventType == "response")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Content)
            .Take(1)
            .ToQueryString();

        foreach (var sql in new[] { askQuestion, response })
        {
            sql.Should().Contain("FROM session_messages");
            sql.Should().Contain("LIMIT");
            sql.Should().NotContain("metadata").And.NotContain("LIKE");
        }
    }

    [Fact]
    public void MetadataJson_IsJsonbSoLikeIsNotAnOptionOnIt()
    {
        using var db = Postgres();

        var column = db.Model.FindEntityType(typeof(AgentSession))!
            .FindProperty(nameof(AgentSession.MetadataJson))!
            .GetColumnType();

        // The whole trap: a string property over a jsonb column looks text-filterable in LINQ and is not
        // in SQL. If this ever becomes text, the in-memory match below can go back into the query.
        column.Should().Be("jsonb");
    }

    [Fact]
    public void MetadataContains_RendersALikeOnJsonb_WhichIsWhyTheFilterLivesInMemory()
    {
        using var db = Postgres();
        var jobId = Guid.NewGuid();

        var sql = db.AgentSessions.AsNoTracking()
            .Where(s => s.MetadataJson.Contains(jobId.ToString()))
            .Select(s => s.Id)
            .ToQueryString();

        // Pinned as a hazard, not endorsed as a shape: this is the command the server rejected, and it is
        // recorded here so the next reader can see the failure text without running it against a database.
        sql.Should().Contain("metadata").And.Contain("LIKE");
    }
}
