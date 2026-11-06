using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// Reads, writes and merges the artifact manifest stored on a job row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this lives in Core.</strong> Two projects need to name a job artifact. The Api's job read
/// path (<c>ProjectJobsController</c>) renders the manifest for the Cockpit; the Api's session write
/// path (<c>SessionsController.SyncArtifact</c>) mirrors a newly synced session artifact into the job
/// that owns the turn. The second one is the reason for the split: if the merge rules were written on
/// <c>ProjectJobsController</c>, the session path would have to reach into a controller to keep the two
/// in step, and the mirror would be whatever the controller author happened to expose. Putting the
/// rules here means both callers get the same keying, the same URL shape, and the same ordering, and
/// neither owns them. The project document library made the same move for the same reason — see
/// <see cref="ProjectDocumentIndexFormatter"/>.
/// </para>
/// <para>
/// <strong>Why the manifest is a column and not a scan.</strong> The alternative is to derive a job's
/// artifacts on read by walking its turn sessions' <c>metadata_json</c> and unioning them. That is what
/// <see cref="ReadSessionArtifacts"/> does as a fallback, and it is why the fallback is bounded: a job
/// is long-lived and accumulates a turn session per chat message, so the derivation is O(turns) rows
/// parsed per read, on the endpoint the Cockpit hits every time you switch jobs. The column makes the
/// steady-state read one row. The derivation still runs, but as a backfill for rows written before the
/// column existed and for artifacts synced while the job row was unreachable — see
/// <see cref="MergeIntoManifest"/> for why the merge is id-by-name rather than replace.
/// </para>
/// <para>
/// <strong>Why entries are keyed by name, case-insensitively.</strong> A session manifest is keyed the
/// same way (<c>SessionsController.SyncArtifact</c> replaces the entry with the matching name), so an
/// agent that re-syncs <c>plan.md</c> after editing it updates one row in both places. Keying the mirror
/// by id instead would append a second entry for the same file on every re-sync and the panel would fill
/// with near-duplicates. The cost is that two different turn sessions that both produce <c>plan.md</c>
/// collapse to one entry — which matches what the session side already does, and is what the file on disk
/// does too, since both write to the same artifact directory name. "Collapse" does not mean "whatever the
/// backfill happened to read last": the freshest entry wins the fields that describe where the bytes live,
/// per <see cref="Merge"/>, so a later read of an older turn cannot repoint the survivor at stale bytes.
/// </para>
/// </remarks>
public static class JobArtifactManifest
{
    /// <summary>Manifest options. Case-insensitive on read so hand-written or PascalCase payloads
    /// (the shape <c>JsonSerializer</c> produces without a naming policy) still deserialise.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The empty manifest, used when a row holds null or unparseable JSON.</summary>
    public static List<JobArtifactDto> Empty => new();

    /// <summary>
    /// Parses a job's stored manifest. Never throws: a corrupt or legacy payload reads as empty, which
    /// is the same degradation <c>SessionsController.ExtractArtifacts</c> chose.
    /// </summary>
    public static List<JobArtifactDto> Read(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return Empty;
        try
        {
            var trimmed = manifestJson.TrimStart();
            if (trimmed.StartsWith('['))
            {
                return JsonSerializer.Deserialize<List<JobArtifactDto>>(trimmed, Options) ?? Empty;
            }

            // A jsonb column with a '{}' default: an object, not an array. Both the bare "{}" of a
            // never-written manifest and an envelope form are handled here rather than throwing.
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            if (!TryGetProperty(doc.RootElement, "artifacts", out var arr)) return Empty;
            if (arr.ValueKind != JsonValueKind.Array) return Empty;
            return JsonSerializer.Deserialize<List<JobArtifactDto>>(arr.GetRawText(), Options) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Serialises a manifest for storage. Always an array, so the column stays queryable as
    /// <c>jsonb</c> and an empty job reads as <c>[]</c> rather than <c>null</c>.</summary>
    public static string Write(IEnumerable<JobArtifactDto> artifacts) =>
        JsonSerializer.Serialize(artifacts.ToList(), Options);

    /// <summary>
    /// Folds <paramref name="incoming"/> into <paramref name="existing"/>, keyed on name
    /// (case-insensitive), and returns the merged list sorted newest-first for display.
    /// </summary>
    /// <remarks>
    /// Merge rather than replace, because the two writers see different slices of the truth: the mirror
    /// pass on read knows about every turn session it could resolve, while the sync-time mirror knows
    /// only about the artifact just written. A replace from either side would delete entries the other
    /// side legitimately added. Existing entries keep their <see cref="JobArtifactDto.Id"/> when an
    /// incoming entry updates them, so a selection in the Cockpit survives a refresh.
    /// <para>
    /// <strong>Freshness decides who owns a field.</strong> The read-time backfill walks a job's turn
    /// sessions in <c>CreatedAt</c> ascending order and folds every artifact it finds, so an entry can
    /// arrive from a session that is <em>older</em> than the one that last wrote it — and an older turn
    /// that re-synced a file late is normal here, not an edge case (a persona resumed on turn 1 while
    /// turn 2 already rewrote <c>plan.md</c>). An unconditional overwrite would then repoint
    /// <see cref="S3Key"/> and <see cref="SourceSessionId"/> at the stale location and the panel would
    /// serve, or fail to serve, the wrong bytes. So the incoming entry may only replace fields it is at
    /// least as fresh as; an older one may only fill gaps the stored entry left empty. See
    /// <see cref="Pick{T}"/> for the per-field rule and <see cref="ResolveDownloadUrl"/> for the one field
    /// that is derived rather than picked.
    /// </para>
    /// </remarks>
    public static List<JobArtifactDto> Merge(
        IEnumerable<JobArtifactDto> existing,
        IEnumerable<JobArtifactDto> incoming)
    {
        var merged = existing.ToList();

        foreach (var item in incoming)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;

            var index = merged.FindIndex(a =>
                string.Equals(a.Name, item.Name, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                merged.Add(item);
                continue;
            }

            var current = merged[index];
            var incomingOwnsFields = item.UpdatedAt >= current.UpdatedAt;

            merged[index] = current with
            {
                // Keep the original identity so the panel's selection is stable across re-syncs.
                Summary = Pick(item.Summary, current.Summary, incomingOwnsFields, IsEmpty),
                ContentType = Pick(item.ContentType, current.ContentType, incomingOwnsFields, IsEmpty),
                SizeBytes = Pick(item.SizeBytes, current.SizeBytes, incomingOwnsFields, IsUnknownSize),
                // The moment this file first appeared in the job is a fact about the job, not about the
                // entry that happened to be folded in first, so the earliest known value wins either way.
                CreatedAt = current.CreatedAt == default ? item.CreatedAt : current.CreatedAt,
                // Never rewound: an older incoming entry cannot move the clock backwards, and an
                // incoming entry with no timestamp of its own cannot claim one either.
                UpdatedAt = Pick(item.UpdatedAt, current.UpdatedAt, incomingOwnsFields, IsUnset),
                // A list-shaped incoming entry carries no body; do not blank a body we already have.
                Content = Pick(item.Content, current.Content, incomingOwnsFields, IsEmpty),
                S3Key = Pick(item.S3Key, current.S3Key, incomingOwnsFields, IsEmpty),
                // Rebuilt from the identity that survives the merge rather than copied: the incoming
                // entry's own URL addresses <em>its</em> id, and a second turn session re-syncing
                // "plan.md" mints a fresh one (SessionsController.SyncArtifact). Copying it would store
                // a URL for an id this entry does not have, which the content endpoint cannot resolve.
                DownloadUrl = ResolveDownloadUrl(current, item, incomingOwnsFields),
                SourceSessionId = Pick(
                    item.SourceSessionId, current.SourceSessionId, incomingOwnsFields, IsEmptyGuid)
            };
        }

        return SortForDisplay(merged);
    }

    /// <summary>
    /// Chooses the value one merged field ends up with.
    /// </summary>
    /// <param name="incoming">The value the entry being folded in carries.</param>
    /// <param name="existing">The value already stored on the job row.</param>
    /// <param name="incomingOwnsFields">
    /// True when the incoming entry is at least as fresh as the stored one, so it may replace what is
    /// there. False means the stored entry wins everywhere it already has a value, and the incoming one
    /// is reduced to filling gaps — which is still worth doing, because a stale record of a size or a
    /// summary beats no record of it.
    /// </param>
    /// <param name="isEmpty">The field's own notion of "carries no information".</param>
    private static T Pick<T>(T incoming, T existing, bool incomingOwnsFields, Func<T, bool> isEmpty) =>
        incomingOwnsFields
            ? isEmpty(incoming) ? existing : incoming
            : isEmpty(existing) ? incoming : existing;

    /// <summary>
    /// The content URL of a merged entry, always addressing the id the merge kept.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than picked from either side, because neither stored URL is necessarily the one the
    /// surviving identity needs: a second turn session re-syncing <c>plan.md</c> is minted a fresh
    /// session-side artifact id (<c>SessionsController.SyncArtifact</c>) while this merge deliberately keeps
    /// the original identity, so copying the incoming URL would store a link to an id this entry does not
    /// have. <c>FindArtifact</c> then resolves neither that id nor a guid-shaped name and the panel's
    /// download link and <c>&lt;img src&gt;</c> 404. The detail endpoint already re-derives this same URL
    /// from the entry it returns, so canonicalising it here keeps the list and the detail in agreement.
    /// </remarks>
    private static string? ResolveDownloadUrl(
        JobArtifactDto current,
        JobArtifactDto item,
        bool incomingOwnsFields)
    {
        if (current.JobId != Guid.Empty)
        {
            var id = string.IsNullOrWhiteSpace(current.Id) ? current.Name : current.Id;
            return BuildContentUrl(current.JobId, id);
        }

        // A stored entry with no job of its own (hand-written or pre-migration row): there is no correct
        // URL to build, so fall back to whichever side carries one.
        var kept = Pick(item.DownloadUrl, current.DownloadUrl, incomingOwnsFields, IsEmpty);
        return string.IsNullOrWhiteSpace(kept) ? null : kept;
    }

    private static bool IsEmpty(string? value) => string.IsNullOrWhiteSpace(value);
    private static bool IsEmptyGuid(Guid? value) => !value.HasValue || value.Value == Guid.Empty;
    private static bool IsUnknownSize(long value) => value <= 0;
    private static bool IsUnset(DateTimeOffset value) => value == default;

    /// <summary>
    /// Newest first, then by name so the order does not depend on wall-clock ties.
    /// </summary>
    /// <remarks>
    /// Deterministic ordering matters here for the same reason it matters in
    /// <see cref="ProjectDocumentIndexFormatter"/>: the Cockpit re-fetches this list on job switch and on
    /// every SignalR artifact event, and an order that flips between two identical payloads makes the
    /// panel jump.
    /// </remarks>
    public static List<JobArtifactDto> SortForDisplay(List<JobArtifactDto> artifacts) =>
        artifacts
            .OrderByDescending(a => a.UpdatedAt)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Rewrites the stored manifest with <paramref name="incoming"/> folded in, returning the merged
    /// list. This is the only mutator callers should use; it keeps serialisation in one place.
    /// </summary>
    public static List<JobArtifactDto> MergeIntoManifest(
        string? storedJson,
        IEnumerable<JobArtifactDto> incoming,
        out string updatedJson)
    {
        var merged = Merge(Read(storedJson), incoming);
        updatedJson = Write(merged);
        return merged;
    }

    /// <summary>
    /// Maps a session artifact onto the job shape, repointing its download URL at the job endpoint.
    /// </summary>
    /// <remarks>
    /// The URL is rebuilt rather than copied. A session artifact's own <c>downloadUrl</c> points at
    /// <c>/api/sessions/{sessionId}/artifacts/{id}/content</c>, which reads that session's manifest and
    /// its directory. Serving a job artifact through it would work only while the source session keeps
    /// the same id and manifest, and would make the job panel's behaviour depend on a row the job does
    /// not own. The job endpoint resolves the bytes itself (job directory first, then the source
    /// session's directory, then S3), so the mirror stays readable if the session is deleted.
    /// </remarks>
    public static JobArtifactDto FromSessionArtifact(
        Guid jobId,
        Guid sourceSessionId,
        string id,
        string name,
        string? summary,
        string contentType,
        long sizeBytes,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? s3Key) =>
        new(
            Id: id,
            JobId: jobId,
            SourceSessionId: sourceSessionId,
            Name: name,
            Summary: summary,
            ContentType: contentType,
            SizeBytes: sizeBytes,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            Content: null,
            S3Key: s3Key,
            DownloadUrl: BuildContentUrl(jobId, id));

    /// <summary>The content URL for one artifact of one job.</summary>
    public static string BuildContentUrl(Guid jobId, string artifactId) =>
        $"/api/project-jobs/{jobId}/artifacts/{Uri.EscapeDataString(artifactId)}/content";

    /// <summary>
    /// Reads the artifact list out of a session's <c>metadata_json</c>.
    /// </summary>
    /// <remarks>
    /// Duplicated from <c>SessionsController.ExtractArtifacts</c> in shape but not in code path: that
    /// method returns the Api's <c>SessionArtifactDto</c>, which Core cannot reference. This one reads
    /// the same JSON into the Core shape. The property-name probe mirrors the session side, which
    /// accepts both <c>artifacts</c> and <c>Artifacts</c> because payloads written before a naming policy
    /// existed are still in databases.
    /// </remarks>
    /// <param name="owningSessionId">
    /// The session whose <c>metadata_json</c> is being read. When supplied it is used as the artifact's
    /// source, in preference to any <c>sessionId</c> the entry itself carries.
    /// <para>
    /// The containing row is authoritative for "where do these bytes live": it is the manifest being read,
    /// and the file was written into that session's artifact directory. An entry's own <c>sessionId</c> is
    /// redundant metadata that in practice always agrees; when it does not, trusting it would point the
    /// content endpoint at a directory that does not hold the file. Falling back to it keeps this method
    /// usable by a caller that has the JSON but not the row.
    /// </para>
    /// </param>
    public static List<JobArtifactDto> ReadSessionArtifacts(
        string? sessionMetadataJson,
        Guid jobId,
        Guid? owningSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionMetadataJson)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(sessionMetadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            if (!TryGetProperty(doc.RootElement, "artifacts", out var arr)) return Empty;
            if (arr.ValueKind != JsonValueKind.Array) return Empty;

            var result = new List<JobArtifactDto>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var name = GetString(el, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var id = GetString(el, "id");
                if (string.IsNullOrWhiteSpace(id)) id = name;

                result.Add(new JobArtifactDto(
                    Id: id,
                    JobId: jobId,
                    SourceSessionId: owningSessionId ?? ReadGuid(el, "sessionId"),
                    Name: name,
                    Summary: GetString(el, "summary"),
                    ContentType: GetString(el, "contentType") ?? "text/plain",
                    SizeBytes: TryGetProperty(el, "sizeBytes", out var sizeEl) &&
                               sizeEl.ValueKind == JsonValueKind.Number &&
                               sizeEl.TryGetInt64(out var size)
                        ? size
                        : 0,
                    CreatedAt: GetDate(el, "createdAt"),
                    UpdatedAt: GetDate(el, "updatedAt"),
                    Content: null,
                    S3Key: GetString(el, "s3Key"),
                    DownloadUrl: BuildContentUrl(jobId, id)));
            }

            return result;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Case-insensitive property probe (handles camelCase and PascalCase payloads).</summary>
    private static bool TryGetProperty(JsonElement el, string name, out JsonElement value)
    {
        if (el.TryGetProperty(name, out value)) return true;

        foreach (var prop in el.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement el, string name) =>
        TryGetProperty(el, name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Reads a guid-valued property, returning null when absent or unparseable.</summary>
    private static Guid? ReadGuid(JsonElement el, string name) =>
        TryGetProperty(el, name, out var v) &&
        v.ValueKind == JsonValueKind.String &&
        Guid.TryParse(v.GetString(), out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset GetDate(JsonElement el, string name) =>
        TryGetProperty(el, name, out var v) &&
        v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
