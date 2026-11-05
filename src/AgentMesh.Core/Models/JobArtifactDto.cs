using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

/// <summary>
/// One artifact produced during a job, as the Cockpit and the API see it.
/// </summary>
/// <remarks>
/// This is the job-side twin of <c>SessionArtifactDto</c>, and it deliberately keeps the same field
/// names and the same explicit <see cref="JsonPropertyName"/> attributes rather than reusing that
/// record. Two reasons:
/// <list type="number">
/// <item><description>
/// <c>SessionArtifactDto</c> lives on <c>SessionsController</c> in the Api project. Anything that
/// wants to name an artifact — the job read path, the session write path that mirrors into a job, and
/// the tests for both — would have to depend on the Api to reference it. The project library hit the
/// same wall and moved its formatter into Core for the same reason (see
/// <see cref="Services.ProjectDocumentIndexFormatter"/>).
/// </description></item>
/// <item><description>
/// The identity is different. A session artifact belongs to one session; a job artifact is a mirror of
/// something a session produced, so it carries <see cref="SourceSessionId"/> to say which turn session
/// it came from, and <see cref="JobId"/> instead of <see cref="SourceSessionId"/> alone. A job with
/// three turns from two personas can produce two files with the same name, and a reader has to be able
/// to tell them apart.
/// </description></item>
/// </list>
/// <para>
/// The mirror is keyed on <see cref="Name"/> (case-insensitive), the same key the session manifest
/// uses, so a re-sync of <c>plan.md</c> updates the entry instead of appending a second one.
/// </para>
/// <para>
/// <see cref="Content"/> is populated only on the single-artifact read, never in the list: the list is
/// polled by the Cockpit on every job switch, and a job whose turns produced a 2 MB transcript should
/// not have to ship that transcript to render a filename. Same rule as
/// <see cref="ProjectDocumentDto.Content"/>.
/// </para>
/// </remarks>
/// <param name="Id">Stable identity of the mirrored artifact. Reused across re-syncs of the same
/// name so the Cockpit's selection survives a refresh.</param>
/// <param name="SourceSessionId">The turn session that produced the artifact, or null for an artifact
/// written straight to the job (a disk file found in the job's artifact directory).</param>
/// <param name="DownloadUrl">Relative content URL served by
/// <c>GET /api/project-jobs/{jobId}/artifacts/{artifactId}/content</c>. Built by
/// <see cref="Services.JobArtifactManifest.BuildContentUrl"/> so the mirror stored in the row and the
/// endpoint that serves it cannot disagree.</param>
public record JobArtifactDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("sourceSessionId")] Guid? SourceSessionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("s3Key")] string? S3Key = null,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl = null
);
