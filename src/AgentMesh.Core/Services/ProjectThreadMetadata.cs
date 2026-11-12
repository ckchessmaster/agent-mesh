using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMesh.Core.Services;

/// <summary>
/// The closed set of values the <c>workspaceAccess</c> metadata/wire key can take.
/// </summary>
/// <remarks>
/// <para>
/// Strings rather than an enum, matching <see cref="JobChangesSources"/>: these values ride session
/// metadata and the dispatch payload to the dashboard and the CLI runner, and a renumbered enum is a
/// silent breaking change on that path.
/// </para>
/// <para>
/// <strong>Resolution is explicitly opt-in.</strong> A thread is read-only only when the value is
/// literally <see cref="ReadOnly"/> (case-insensitive). Missing, empty, or unrecognised values resolve
/// to today's behaviour (<see cref="ReadWrite"/>) so an old API dispatch reaching a new runner, or a
/// session row written before this key existed, is no worse off than it is today. The writer that
/// creates a project Q&amp;A thread (the project-threads API) is responsible for stamping
/// <see cref="ReadOnly"/> at creation; every later dispatch re-reads the same metadata, so the marker
/// is inherited by all follow-up turns without a migration.
/// </para>
/// </remarks>
public static class WorkspaceAccessKinds
{
    /// <summary>
    /// Inspection only: the runner may read and search the workspace but must refuse anything that
    /// creates, edits, deletes, builds, tests, commits or pushes. Enforced by
    /// <see cref="ReadOnlyCommandPolicy"/>.
    /// </summary>
    public const string ReadOnly = "readOnly";

    /// <summary>The default, and the absence of the key: the workspace may be modified as before.</summary>
    public const string ReadWrite = "readWrite";
}

/// <summary>
/// The metadata contract for project Q&amp;A threads: the key names, and the only readers/writers
/// allowed to interpret them.
/// </summary>
/// <remarks>
/// <para>
/// A project thread is a free-floating <c>AgentSession</c> row that carries <c>projectId</c> and no
/// <c>jobId</c> in <c>MetadataJson</c>, plus an explicit <c>workspaceAccess</c> value. Job-dispatched
/// turns already stamp <c>workspaceScope: "job"</c> (see <c>JobTurnDispatcher</c>) and always carry a
/// <c>jobId</c>, so job turns and project threads are distinguishable from metadata alone — which is
/// what lets the API gate tool refusals (and the CLI gate its tool list) without a schema change.
/// </para>
/// <para>
/// Every read here is total: malformed or null metadata parses to "no keys" rather than throwing,
/// because this runs on the read path of session lists and dispatch payloads where a legacy row with
/// junk JSON must not take down a GET. Writers that need to reject bad JSON reject it at their own
/// write boundary.
/// </para>
/// </remarks>
public static class ProjectThreadMetadata
{
    /// <summary>
    /// The metadata/wire key carrying the <see cref="WorkspaceAccessKinds"/> value. Written once at
    /// thread creation, re-read on every dispatch, and surfaced on the poll payload as the runner's
    /// read-only switch.
    /// </summary>
    public const string WorkspaceAccessKey = "workspaceAccess";

    /// <summary>The metadata key carrying the owning project id (same spelling the resolvers already read).</summary>
    public const string ProjectIdKey = "projectId";

    /// <summary>The metadata key carrying the owning job id. A project thread must not have one.</summary>
    public const string JobIdKey = "jobId";

    /// <summary>
    /// Reads <see cref="WorkspaceAccessKey"/> from a metadata JSON blob, or null when the key is
    /// absent, non-string, or the blob is malformed.
    /// </summary>
    public static string? ReadWorkspaceAccess(string? metadataJson) => ReadString(metadataJson, WorkspaceAccessKey);

    /// <summary>Reads <see cref="ProjectIdKey"/> from a metadata JSON blob (see <see cref="ReadWorkspaceAccess"/>).</summary>
    public static string? ReadProjectId(string? metadataJson) => ReadString(metadataJson, ProjectIdKey);

    /// <summary>Reads <see cref="JobIdKey"/> from a metadata JSON blob (see <see cref="ReadWorkspaceAccess"/>).</summary>
    public static string? ReadJobId(string? metadataJson) => ReadString(metadataJson, JobIdKey);

    /// <summary>
    /// True only for the literal <see cref="WorkspaceAccessKinds.ReadOnly"/> value, case-insensitively.
    /// Everything else — null, blank, unknown spellings — is not read-only. Fail-open on the value
    /// space is deliberate: see the remarks on <see cref="WorkspaceAccessKinds"/>.
    /// </summary>
    public static bool IsReadOnlyAccessValue(string? accessValue) =>
        !string.IsNullOrWhiteSpace(accessValue) &&
        accessValue.Trim().Equals(WorkspaceAccessKinds.ReadOnly, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this session's metadata marks its workspace read-only.</summary>
    public static bool IsReadOnlyWorkspace(string? metadataJson) => IsReadOnlyAccessValue(ReadWorkspaceAccess(metadataJson));

    /// <summary>
    /// True for a project Q&amp;A thread: the metadata carries a project id and no job id. This is the
    /// "ask a question about a project without creating a job" shape, and it is what makes the thread
    /// jobless on the wire.
    /// </summary>
    public static bool IsProjectThread(string? metadataJson) =>
        !string.IsNullOrWhiteSpace(ReadProjectId(metadataJson)) &&
        string.IsNullOrWhiteSpace(ReadJobId(metadataJson));

    /// <summary>
    /// True when enforcement applies: a project thread whose workspace access resolves to read-only.
    /// Callers that already know they are holding a thread row (the API's thread endpoints) may check
    /// <see cref="IsReadOnlyWorkspace"/> alone; this conjunction is the safe default for code that sees
    /// arbitrary session metadata.
    /// </summary>
    public static bool IsReadOnlyProjectThread(string? metadataJson) =>
        IsProjectThread(metadataJson) && IsReadOnlyWorkspace(metadataJson);

    /// <summary>
    /// Returns <paramref name="metadataJson"/> with <see cref="WorkspaceAccessKey"/> set to
    /// <paramref name="accessValue"/>, preserving every other key. Passing null or blank removes the
    /// key (back to the implicit default).
    /// </summary>
    /// <remarks>
    /// Malformed input JSON is replaced by a fresh object containing only the written key rather than
    /// throwing: the writer's contract is "the access marker survives", and silently dropping junk keys
    /// nobody can parse anyway is the same behaviour the rest of the metadata writers settled on.
    /// Serialization is camelCase-insensitive round-trip: existing property names are kept verbatim.
    /// </remarks>
    public static string WithWorkspaceAccess(string? metadataJson, string? accessValue)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(metadataJson)
                ? new JsonObject()
                : JsonSerializer.Deserialize<JsonObject>(metadataJson) ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        if (string.IsNullOrWhiteSpace(accessValue))
        {
            root.Remove(WorkspaceAccessKey);
        }
        else
        {
            root[WorkspaceAccessKey] = accessValue.Trim();
        }

        return root.ToJsonString();
    }

    private static string? ReadString(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Case-insensitive to match the rest of the metadata readers (older rows were written
                // with mixed casing by different call sites).
                if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var val = prop.Value.GetString();
                    return string.IsNullOrWhiteSpace(val) ? null : val;
                }
                return null;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
