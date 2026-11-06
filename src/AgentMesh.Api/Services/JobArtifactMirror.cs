using AgentMesh.Api.Hubs;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Api.Services;

/// <summary>
/// Keeps a job's artifact manifest in step with the artifacts its turn sessions produce.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is.</strong> Artifacts are produced against a session, never against a job: the
/// runner calls <c>POST /api/sessions/{id}/artifacts</c> with the turn session's id, because that is the
/// conversation it is inside. A job is the unit the operator looks at, though, and one job spans many
/// turn sessions — one per dispatched chat message, across however many personas take turns on it. This
/// class is the single place that answer is converted into the other: it resolves which job owns a
/// session and folds that session's artifacts into the job's manifest.
/// </para>
/// <para>
/// <strong>Two entry points, one set of rules.</strong> The write path (<see cref="MirrorAsync"/>) runs
/// when an artifact is synced, so the job panel updates live during a turn. The read path
/// (<see cref="RefreshAsync"/>) runs when the panel is opened, and additionally backfills artifacts that
/// exist but were never mirrored — files written before the manifest column existed, artifacts synced
/// while the job row was unreachable, and loose files sitting in the job's own artifact directory. Both
/// go through <see cref="JobArtifactManifest.Merge"/>, so the keying and ordering rules cannot drift
/// between "what a sync writes" and "what a read shows".
/// </para>
/// <para>
/// <strong>Why the mirror is not the only source of truth.</strong> A mirrored entry is a pointer: the
/// bytes stay in the session's artifact directory (and in S3). That is why the content endpoint resolves
/// from the source session's directory rather than a job-owned copy, and why a job keeps serving its
/// artifacts after a session row is deleted only for as long as the files or the S3 object survive. The
/// alternative — copying bytes into a job-owned directory on every sync — would double storage and make
/// the two copies disagree the first time a session re-synced an edited file.
/// </para>
/// <para>
/// <strong>Concurrency.</strong> Both writers are read-modify-write on the job row, so two personas
/// syncing artifacts into the same job at the same moment can lose one of the two updates — and the
/// multi-persona job is the normal case here, not an edge. That is tolerable because of what the manifest
/// is: an index over data that lives elsewhere (the session's <c>metadata_json</c> and the file on disk),
/// never the only copy of anything. A lost entry is re-derived by the next <see cref="RefreshAsync"/>,
/// which reads the sessions again, so the list converges on the truth within one panel open. Making the
/// fold atomic would need either a row lock or an optimistic-concurrency token on
/// <c>project_jobs.ArtifactsJson</c>; both are real options, and neither is worth the write contention on
/// a column whose contents are reconstructible. The same reasoning is why the session side has shipped
/// with the identical race.
/// </para>
/// <para>
/// <strong>Failure posture.</strong> Mirroring is best-effort. A job manifest that fails to update must
/// not fail the session artifact sync that triggered it: the agent's own artifact write is the durable
/// event, and <see cref="RefreshAsync"/> will pick the artifact up on the next panel open. So
/// <see cref="MirrorAsync"/> swallows and logs, and only <see cref="RefreshAsync"/> — a read the operator
/// asked for — surfaces storage errors.
/// </para>
/// </remarks>
public static class JobArtifactMirror
{
    /// <summary>
    /// Mirrors one freshly synced session artifact into the job that owns the session, if any.
    /// </summary>
    /// <param name="hubContext">Nullable because the job endpoints and the tests construct controllers
    /// without a hub; a null hub means the manifest still updates, only no live push.</param>
    /// <param name="isNew">Whether the session-side sync created the artifact or replaced it. Decides
    /// which SignalR event name the Cockpit sees, matching the session-side convention.</param>
    /// <returns>The job the artifact was mirrored into, or null when the session belongs to no job (an
    /// interactive session, which is the majority of them) or the mirror failed.</returns>
    public static async Task<ProjectJob?> MirrorAsync(
        AgentMeshDbContext db,
        IHubContext<AgentMeshHub>? hubContext,
        AgentSession session,
        string artifactId,
        string name,
        string? summary,
        string contentType,
        long sizeBytes,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? s3Key,
        bool isNew,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        try
        {
            var jobId = ResolveJobId(session, db);
            if (jobId == null) return null;

            var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == jobId.Value, ct);
            if (job == null)
            {
                // A turn session can outlive its job row (cascade deletes, or a job deleted while a turn
                // was in flight). Nothing to mirror into; the session still has the artifact.
                return null;
            }

            var entry = JobArtifactManifest.FromSessionArtifact(
                jobId: job.Id,
                sourceSessionId: session.Id,
                id: artifactId,
                name: name,
                summary: summary,
                contentType: contentType,
                sizeBytes: sizeBytes,
                createdAt: createdAt,
                updatedAt: updatedAt,
                s3Key: s3Key);

            var mirrored = WriteMerged(job.ArtifactsJson, new[] { entry }, out var json);
            job.ArtifactsJson = json;
            // Bumping UpdatedAt here is deliberate and correct: an artifact is being produced right now,
            // so the job is active. PersonaIdleReaperService reads job.UpdatedAt as "this persona has
            // recent work" before restarting a runner for an update, and a long turn whose only visible
            // progress is artifact output would otherwise look idle. The read path below does NOT touch
            // this column, for the opposite reason: viewing a job is not activity.
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (hubContext != null)
            {
                // Only the artifact event is broadcast, deliberately not "JobUpdated": the Cockpit's
                // JobUpdated handler replaces the whole job row from the payload it is given
                // (App.tsx applyJobFromServer), so sending anything other than a ProjectJobDto would blank
                // the job out of the list. The artifact events carry a single JobArtifactDto, which is what
                // the panel folds into its own artifact state.
                //
                // The broadcast is the merged entry rather than the one just constructed, so a re-sync that
                // kept its original id pushes that id — the one the panel already holds — instead of
                // teaching the client a second key for the same file.
                var eventName = isNew ? "JobArtifactCreated" : "JobArtifactUpdated";
                var broadcast = mirrored.FirstOrDefault(a =>
                    string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (broadcast != null)
                {
                    await hubContext.Clients.All.SendAsync(eventName, broadcast, ct);
                }
            }

            return job;
        }
        catch (Exception ex)
        {
            // See the failure posture in the type remarks: never fail a session artifact sync because of
            // a job-side mirror. The read path recovers it.
            logger?.LogWarning(ex,
                "Could not mirror artifact {ArtifactName} of session {SessionId} into its job.",
                name, session.Id);
            return null;
        }
    }

    /// <summary>
    /// Backfills and returns a job's full artifact list: everything its turn sessions recorded, plus any
    /// loose file in the job's own artifact directory, folded into the stored manifest and persisted.
    /// </summary>
    /// <remarks>
    /// Called on every list read, and on a detail or content read only when the stored manifest does not
    /// already name the requested artifact — <c>ProjectJobsController.GetJobArtifact</c> and
    /// <c>GetJobArtifactContent</c> try <see cref="JobArtifactManifest.Read"/> first, because the Cockpit
    /// pairs one list fetch with a detail (or an <c>&lt;img src&gt;</c>) milliseconds later and this method
    /// is the expensive half of that exchange.
    /// <para>
    /// Persisting on a read is deliberate and matches what the session endpoint already does
    /// (<c>SessionsController.GetArtifacts</c> writes discovered disk files back into the session
    /// manifest). The alternative is a background sweep, which would need a new scheduler and would leave
    /// the panel wrong until it ran. The write is idempotent: a second call with nothing new produces the
    /// same JSON and, because the merge keeps ids and timestamps, does not dirty the row.
    /// </remarks>
    public static async Task<List<JobArtifactDto>> RefreshAsync(
        AgentMeshDbContext db,
        Guid jobId,
        CancellationToken ct = default)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job == null) return JobArtifactManifest.Empty;

        var discovered = new List<JobArtifactDto>();

        foreach (var session in await TurnSessionsAsync(db, jobId, ct))
        {
            // The session row is passed alongside its own JSON: the artifact bytes live in that session's
            // directory, and the content endpoint needs the id to find them.
            discovered.AddRange(
                JobArtifactManifest.ReadSessionArtifacts(session.MetadataJson, jobId, session.Id));
        }

        discovered.AddRange(DiskArtifacts(jobId));

        var merged = WriteMerged(job.ArtifactsJson, discovered, out var json);

        // Only save when this method's own fold changed something.
        //
        // Not an optimization against redundant UPDATEs: EF's snapshot tracking already suppresses those,
        // since assigning a value equal to the original never marks the entry Modified. Verified by
        // mutation — removing this guard left the change tracker observing no write at all, which is why
        // the accompanying test asserts read determinism instead of trying to observe this branch.
        //
        // The guard is kept because SaveChangesAsync flushes everything staged in the request's context,
        // not just this column. Today no caller stages anything before calling here, so that is latent
        // rather than live; it means a read endpoint only ever commits what the read itself discovered,
        // even if a future caller reaches this with pending changes of its own.
        if (!string.Equals(job.ArtifactsJson, json, StringComparison.Ordinal))
        {
            job.ArtifactsJson = json;
            await db.SaveChangesAsync(ct);
        }

        return merged;
    }

    /// <summary>
    /// The turn sessions belonging to a job.
    /// </summary>
    /// <remarks>
    /// Two sources, unioned, because neither alone is complete:
    /// <list type="number">
    /// <item><description>
    /// <c>job_messages.turn_session_id</c> — indexed on <c>job_id</c>, and the authoritative link for every
    /// turn that posted a chat message, which is every turn that finished. This is the bulk of a job's
    /// history and it costs one indexed lookup.
    /// </description></item>
    /// <item><description>
    /// The job's <em>active</em> turn sessions, matched on their <c>metadata-&gt;jobId</c> envelope — which
    /// catches a turn in flight, one that has produced artifacts but not yet posted a job message.
    /// </description></item>
    /// </list>
    /// <para>
    /// <strong>Why the second lookup is filtered by status before the metadata match.</strong> The obvious
    /// query — every session whose metadata mentions the job id — is a sequential scan with a substring
    /// search over <c>agent_sessions</c>, on an endpoint the Cockpit hits every time you switch jobs and
    /// every time an artifact event lands. It would get slower for the whole deployment forever as the
    /// table grew, and no index helps a <c>LIKE '%guid%'</c>. Filtering on <c>status</c> first uses the
    /// existing <c>ix_agent_sessions_status</c> and bounds the scan to the handful of turns actually
    /// running, which is exactly the shape the three concurrency-limit sites already use
    /// (<c>ProjectJobsController</c> ×2 and the reset path). A completed turn is always reachable through
    /// <c>job_messages</c>, so nothing is lost by not scanning for it in metadata.
    /// </para>
    /// <para>
    /// The substring match is on the lowercase hyphenated guid, the only form the writers emit
    /// (<c>job.Id.ToString()</c>); <c>ToLower()</c> on the column makes it insensitive regardless.
    /// </para>
    /// </remarks>
    private static async Task<List<AgentSession>> TurnSessionsAsync(
        AgentMeshDbContext db,
        Guid jobId,
        CancellationToken ct)
    {
        var linkedIds = await db.JobMessages
            .AsNoTracking()
            .Where(m => m.JobId == jobId && m.TurnSessionId != null)
            .Select(m => m.TurnSessionId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var needle = jobId.ToString().ToLowerInvariant();

        // Materialise the (small, index-bounded) active set, then match the envelope in memory: EF cannot
        // translate a jsonb property read here without a value converter on the column, and the codebase
        // already resolves jobId out of metadata in C# (ProjectJobsController.ExtractJobId).
        var activeSessions = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.Status == "thinking" || s.Status == "busy")
            .ToListAsync(ct);

        var activeIds = activeSessions
            .Where(s => s.MetadataJson != null &&
                        s.MetadataJson.ToLowerInvariant().Contains(needle))
            .Select(s => s.Id);

        var sessionIds = linkedIds.Concat(activeIds).Distinct().ToList();
        if (sessionIds.Count == 0) return new List<AgentSession>();

        return await db.AgentSessions
            .AsNoTracking()
            .Where(s => sessionIds.Contains(s.Id))
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Files sitting in the job's own artifact directory that no manifest entry names yet.
    /// </summary>
    /// <remarks>
    /// The job directory is where a runner may drop output addressed to the job rather than to one of its
    /// turns, and it is the fallback the attachment path already reads from
    /// (<c>ProjectJobsController.GetAttachmentContent</c>). Scanning it here means a file an agent wrote
    /// there shows up in the panel without the agent having to call anything. The scan is top-directory
    /// only, which also keeps the <c>attachments/</c> subdirectory — user uploads, a different concept
    /// with its own endpoints — out of the artifact list.
    /// </remarks>
    private static List<JobArtifactDto> DiskArtifacts(Guid jobId)
    {
        var result = new List<JobArtifactDto>();
        var dir = ResolveArtifactDirectory(jobId);
        if (!Directory.Exists(dir)) return result;

        var now = DateTimeOffset.UtcNow;
        foreach (var path in Directory.GetFiles(dir))
        {
            var info = new FileInfo(path);
            if (info.Length == 0) continue;

            var name = info.Name;
            result.Add(new JobArtifactDto(
                Id: name,
                JobId: jobId,
                SourceSessionId: null,
                Name: name,
                Summary: null,
                ContentType: GuessContentType(name),
                SizeBytes: info.Length,
                CreatedAt: info.CreationTimeUtc,
                UpdatedAt: info.LastWriteTimeUtc,
                Content: null,
                S3Key: $"jobs/{jobId}/artifacts/{name}",
                DownloadUrl: JobArtifactManifest.BuildContentUrl(jobId, name)));
        }

        return result;
    }

    /// <summary>
    /// Resolves the job a session belongs to.
    /// </summary>
    /// <remarks>
    /// Prefers the session's own metadata (set when the turn was dispatched —
    /// <c>JobTurnDispatcher</c>, <c>ProjectJobsController</c> ×2, <c>SessionsController</c> handoff), then
    /// falls back to the job message that carries this turn, which is the only link that survives a
    /// session created without the envelope (an operator re-attaching a session to a job chat).
    /// </remarks>
    public static Guid? ResolveJobId(AgentSession session, AgentMeshDbContext? db = null)
    {
        var fromMetadata = Controllers.ProjectJobsController.ExtractJobId(session.MetadataJson);
        if (fromMetadata != null) return fromMetadata;

        if (db == null) return null;

        try
        {
            var msg = db.JobMessages
                .AsNoTracking()
                .FirstOrDefault(m => m.TurnSessionId == session.Id);
            return msg?.JobId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Locates the bytes for one artifact of one job: job directory, then the source session's directory,
    /// then the S3 key recorded on the entry.
    /// </summary>
    /// <remarks>
    /// Ordered most-specific-first for the same reason the attachment path is
    /// (<c>ProjectJobsController.GetAttachmentContent</c>): a local file is the freshest copy, and S3 is
    /// the fallback for a job whose runner is on another host. Returns null when none of the three has it,
    /// which the caller turns into a 404.
    /// </remarks>
    public static async Task<Stream?> OpenContentStreamAsync(
        JobArtifactDto? artifact,
        Guid jobId,
        string name,
        IStorageService? storageService,
        CancellationToken ct = default)
    {
        // Path.GetFileName here, and not only at the call sites: this is the one place that turns a
        // manifest-supplied or request-supplied name into a filesystem path, and a manifest entry written
        // by an older API (or a hand-edited row) containing "../" would otherwise escape the artifact
        // directory. The session side applies the same strip at write time (SyncArtifact) and here.
        var safeName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safeName)) return null;

        var jobPath = Path.Combine(ResolveArtifactDirectory(jobId), safeName);
        if (File.Exists(jobPath))
        {
            return new FileStream(jobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        if (artifact?.SourceSessionId is Guid sessionId)
        {
            // Path only, never SessionsController.GetArtifactDirectory(sessionId): that helper creates the
            // directory, and a read of a job whose source session was deleted would resurrect an empty
            // directory tree for a session that no longer exists.
            var sessionDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentmesh",
                "artifacts",
                sessionId.ToString());
            var sessionPath = Path.Combine(sessionDir, safeName);
            if (File.Exists(sessionPath))
            {
                return new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
        }

        if (storageService != null && !string.IsNullOrWhiteSpace(artifact?.S3Key))
        {
            try
            {
                return await storageService.DownloadStreamAsync(artifact.S3Key, ct);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The job-level artifact directory: <c>~/.agentmesh/artifacts/jobs/{jobId}</c>, created on demand.
    /// </summary>
    /// <remarks>
    /// Sits directly above <c>ProjectJobsController.GetAttachmentDirectory</c> (<c>.../jobs/{jobId}/
    /// attachments</c>) so a job's files are together on disk, and is created on demand the way the
    /// session and attachment directories are.
    /// </remarks>
    public static string GetArtifactDirectory(Guid jobId)
    {
        var basePath = ResolveArtifactDirectory(jobId);
        Directory.CreateDirectory(basePath);
        return basePath;
    }

    /// <summary>
    /// The job-level artifact path without creating it.
    /// </summary>
    /// <remarks>
    /// The read path uses this instead of <see cref="GetArtifactDirectory"/>: a GET must not leave a
    /// directory behind for every job it is asked about. Listing a job's artifacts, or resolving a
    /// content stream that turns out not to exist, would otherwise litter
    /// <c>~/.agentmesh/artifacts/jobs/</c> with an empty directory per job ever viewed — the same reason
    /// the session scan checks <c>Directory.Exists</c> before enumerating.
    /// </remarks>
    public static string ResolveArtifactDirectory(Guid jobId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentmesh",
            "artifacts",
            "jobs",
            jobId.ToString());

    /// <summary>
    /// Folds <paramref name="incoming"/> into <paramref name="stored"/>, returning the merged list and the
    /// JSON to persist. One helper so the two writers cannot disagree about serialisation.
    /// </summary>
    private static List<JobArtifactDto> WriteMerged(
        string stored,
        IEnumerable<JobArtifactDto> incoming,
        out string json)
    {
        var merged = JobArtifactManifest.Merge(JobArtifactManifest.Read(stored), incoming);
        json = JobArtifactManifest.Write(merged);
        return merged;
    }

    private static string GuessContentType(string fileName) =>
        Controllers.SessionsController.GuessContentTypeFromExtension(fileName);
}
