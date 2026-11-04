using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

/// <summary>
/// The wire shape of one project supporting document.
/// </summary>
/// <remarks>
/// Lives in Core next to <see cref="ProjectDocument"/> for the same reason
/// <see cref="JobAttachmentDto"/> does: it is a contract shared by the Api that produces it and the
/// worker/CLI code that reads it, and putting it on the controller would make the Api a dependency of
/// any consumer that just wants to name a document.
/// <para>
/// <see cref="Content"/> is populated only for a text document and only on the single-document read.
/// The list endpoint leaves it null: a library of 50 pasted specs is not something the dashboard should
/// have to receive to render 50 titles, and the prompt index excerpts from the row rather than from
/// this DTO.
/// </para>
/// </remarks>
/// <param name="Kind">Serializes as its numeric value, because no
/// <c>JsonStringEnumConverter</c> is registered on the API and every other enum on this contract
/// (<c>WorkspaceType</c>, <c>ProjectJobStatus</c>) already crosses the wire that way. The dashboard
/// mirrors it as a TS enum rather than a string so both sides keep one mapping.</param>
/// <param name="DownloadUrl">Relative content URL, built by
/// <see cref="Services.ProjectDocumentIndexFormatter.BuildContentUrl"/> so the index, the API and the
/// stored row cannot disagree about where a document lives. Same meaning as on
/// <see cref="SessionAttachmentDto"/> and <see cref="JobAttachmentDto"/>: the plain content endpoint,
/// which serves renderable types inline.</param>
/// <param name="ForceDownloadUrl">
/// The same endpoint with <c>?download=true</c>, which forces an <c>attachment</c> disposition. Separate
/// field because an <c>&lt;img src&gt;</c> and a "save this file" anchor want different dispositions, and
/// a client that appends the query itself is a client that can get the parameter name wrong.
/// </param>
public record ProjectDocumentDto(
    string Id,
    string ProjectId,
    string Title,
    ProjectDocumentKind Kind,
    string? FileName,
    string? ContentType,
    long SizeBytes,
    string? S3Key,
    string DownloadUrl,
    string ForceDownloadUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Content = null)
{
    /// <summary>Projects a persisted row onto the wire shape, without its body.</summary>
    public static ProjectDocumentDto From(ProjectDocument document, bool includeContent = false)
    {
        var url = Services.ProjectDocumentIndexFormatter.BuildContentUrl(document.ProjectId, document.Id);

        return new ProjectDocumentDto(
            Id: document.Id.ToString("N"),
            ProjectId: document.ProjectId,
            Title: document.Title,
            Kind: document.Kind,
            FileName: document.FileName,
            ContentType: document.ContentType,
            SizeBytes: document.SizeBytes,
            S3Key: document.S3Key,
            DownloadUrl: url,
            ForceDownloadUrl: url + "?download=true",
            CreatedAt: document.CreatedAt,
            UpdatedAt: document.UpdatedAt,
            Content: includeContent && document.Kind == ProjectDocumentKind.Text ? document.Content : null);
    }
}
