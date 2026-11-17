using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Infrastructure.Services;

/// <summary>
/// Writes the terminal state for a turn whose owner is gone.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is one place.</strong> Three callers need the same two writes: the worker when its
/// observation ceiling expires, the API sweeper when a session has outlived every owner it could have
/// had, and the operator-facing recovery endpoint when a human decides a job should move on. The status
/// alone is not enough — a session that silently changes from "thinking" to "stopped" is
/// indistinguishable from an operator cancel, so every settle also appends a transcript row saying why.
/// Without that row the next investigation starts from scratch, which is the cost this path exists to
/// avoid.
/// </para>
/// <para>
/// <strong>Why a live status is required.</strong> Both entry points re-check that the session is still
/// in a live status before writing. A turn that has already concluded, parked on
/// <c>waiting_for_input</c>, or been stopped by an operator must not be rewritten: its status is a fact
/// about what happened, and this is a repair, not an override.
/// </para>
/// <para>
/// <strong>Why a wrong settle is survivable.</strong> If a turn that is in fact alive gets settled, the
/// next event the runner posts overwrites the status from that event's type. Recovery is automatic,
/// which is what makes it acceptable to settle on a timer instead of proving the owner is dead.
/// </para>
/// </remarks>
public static class TurnStallSettler
{
    /// <summary>
    /// Settles one session by id, whatever its silence, provided it is still live.
    /// </summary>
    /// <remarks>
    /// Used by the turn runner, which does not need an age to decide: it has just watched the turn reach
    /// the ceiling on its own observation window, and that is the evidence.
    /// </remarks>
    /// <returns>The settled session, or <see langword="null"/> when it was not found or not live.</returns>
    public static async Task<AgentSession?> SettleOneAsync(
        AgentMeshDbContext db,
        Guid sessionId,
        string notice,
        string senderAgentId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || !IsLive(session.Status)) return null;

        var settledAt = DateTimeOffset.UtcNow;
        ApplySettle(db, session, notice, senderAgentId, settledAt);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Settles every live session that has been quiet since <paramref name="cutoff"/>.
    /// </summary>
    /// <param name="cutoff">
    /// A session is an orphan when its <see cref="AgentSession.UpdatedAt"/> is at or before this instant.
    /// Callers derive it from <see cref="TurnStallPolicy.OrphanedTurnAfter"/> rather than inventing a
    /// number, so the sweeper can never fire before the worker has given up on the turn.
    /// </param>
    /// <param name="filter">
    /// Narrows the sweep without changing what counts as orphaned — the recovery endpoint uses it to stay
    /// inside one job. The filter runs in process, after the indexed time-window query.
    /// </param>
    /// <returns>The sessions that were settled.</returns>
    public static async Task<IReadOnlyList<AgentSession>> SettleOrphansAsync(
        AgentMeshDbContext db,
        DateTimeOffset cutoff,
        string notice,
        string senderAgentId,
        Func<AgentSession, bool>? filter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var candidates = await db.AgentSessions
            .Where(s => TurnStallPolicy.LiveTurnStatuses.Contains(s.Status) && s.UpdatedAt <= cutoff)
            .ToListAsync(ct);

        if (filter is not null)
        {
            candidates = candidates.Where(filter).ToList();
        }
        if (candidates.Count == 0) return [];

        var settledAt = DateTimeOffset.UtcNow;
        foreach (var session in candidates)
        {
            ApplySettle(db, session, notice, senderAgentId, settledAt);
        }
        await db.SaveChangesAsync(ct);
        return candidates;
    }

    /// <summary>
    /// Records on the job itself that its turn was settled without an owner, so the cockpit can offer a
    /// recovery without asking Temporal what is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a heartbeat string and not a status value.</strong> <c>ProjectJobStatus</c> has no
    /// failure member, and inventing one is a schema-and-semantics change to every consumer of a job's
    /// lifecycle for a state that is transient by definition: a recovered job is immediately active again.
    /// The heartbeat already exists to say "here is what this job is doing right now", and the lifecycle
    /// derivation in the API already reads it, so a stalled job becomes visible through a path the
    /// dashboard is already on rather than a new one.
    /// </para>
    /// <para>
    /// A job that is no longer active is left alone: if an operator completed or archived the job while
    /// the settle was in flight, their decision is the newer fact.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the job row was marked.</returns>
    public static async Task<bool> MarkJobStalledAsync(
        AgentMeshDbContext db,
        Guid jobId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != Core.Enums.ProjectJobStatus.Active) return false;

        var text = string.IsNullOrWhiteSpace(reason) ? "its turn stopped advancing" : reason.Trim();
        var firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length > 220) firstLine = firstLine[..220].TrimEnd() + "…";

        job.ProgressHeartbeat = $"{StalledHeartbeatPrefix} {firstLine}";
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The marker <see cref="MarkJobStalledAsync"/> writes, and the string the API's lifecycle derivation
    /// looks for. Public because the dashboard's recovery affordance and the API both read it.
    /// </summary>
    public const string StalledHeartbeatPrefix = "Stalled:";

    /// <summary>True when a job's heartbeat says its turn was settled without an owner.</summary>
    public static bool IsStalledHeartbeat(string? heartbeat) =>
        !string.IsNullOrWhiteSpace(heartbeat) &&
        heartbeat.StartsWith(StalledHeartbeatPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the job a turn session belongs to out of the metadata the dispatcher stamped on it.
    /// </summary>
    /// <remarks>
    /// The association lives in <c>MetadataJson</c> rather than a column because the dispatcher already
    /// writes <c>jobId</c> there for every job turn, and adding a column to satisfy a repair path would
    /// mean a migration for something that only ever reads. Non-job sessions (a cockpit chat with no job)
    /// have no <c>jobId</c> and return <see langword="false"/>, which is the correct answer: there is no
    /// job to mark stalled.
    /// </remarks>
    public static bool TryGetJobIdFromMetadata(string? metadataJson, out Guid jobId)
    {
        jobId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("jobId", out var el)) return false;
            var raw = el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : el.ToString();
            return Guid.TryParse(raw, out jobId) && jobId != Guid.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>True when a status means an agent may still be working on the session.</summary>
    public static bool IsLive(string? status) =>
        !string.IsNullOrWhiteSpace(status) &&
        TurnStallPolicy.LiveTurnStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mutates the tracked session and queues its transcript row, without saving.
    /// </summary>
    /// <remarks>
    /// Split out so the batch path issues one <c>SaveChanges</c> for N sessions rather than N, and so the
    /// status and the notice cannot be applied half-way by a caller that forgets one of the two.
    /// </remarks>
    private static void ApplySettle(
        AgentMeshDbContext db,
        AgentSession session,
        string notice,
        string senderAgentId,
        DateTimeOffset settledAt)
    {
        session.Status = TurnStallPolicy.SettledStatus;
        session.UpdatedAt = settledAt;

        // The notice is a transcript row rather than only a log line because the transcript is what the
        // dashboard and the next turn both read. An agent that resumes this session should be able to
        // see that the platform settled the previous turn, and why.
        db.SessionMessages.Add(new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = senderAgentId,
            TargetAgentId = string.IsNullOrWhiteSpace(session.CreatorAgentId) ? null : session.CreatorAgentId,
            EventType = "system",
            Content = BuildNotice(notice, settledAt),
            CreatedAt = settledAt
        });
    }

    /// <summary>
    /// Stamps the reason with the settle instant.
    /// </summary>
    /// <remarks>
    /// The caller supplies the reason it knows ("the 110-minute observation ceiling expired", "an
    /// operator recovered the job") and the wall-clock instant is appended here, because the two are
    /// written by different code paths and a notice without a timestamp cannot be compared against the
    /// transcript to work out what the agent was last seen doing.
    /// </remarks>
    private static string BuildNotice(string reason, DateTimeOffset settledAt)
    {
        var text = string.IsNullOrWhiteSpace(reason)
            ? "The platform settled this turn because its owner stopped advancing it."
            : reason.Trim();

        return $"{TurnStallPolicy.SettleNoticePrefix} {text} Settled at {settledAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z.";
    }
}
