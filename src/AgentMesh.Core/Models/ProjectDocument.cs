namespace AgentMesh.Core.Models;

/// <summary>
/// What kind of supporting document a <see cref="ProjectDocument"/> row describes.
/// </summary>
/// <remarks>
/// The split exists because the two kinds are produced by different inputs and read back differently:
/// a <see cref="Text"/> document's body lives in the row and is served straight out of the database,
/// while a <see cref="File"/> document's body lives on disk (mirrored to S3) and the row only points at
/// it. A caller that wants to inline a document's content into a prompt can only do that for the first
/// kind, which is exactly the distinction the agent-facing index has to make.
/// </remarks>
public enum ProjectDocumentKind
{
    /// <summary>Pasted or typed prose. Body is <see cref="ProjectDocument.Content"/>.</summary>
    Text = 0,

    /// <summary>An uploaded document or image. Body is the file at <see cref="ProjectDocument.S3Key"/>.</summary>
    File = 1
}

/// <summary>
/// A supporting document attached to a <see cref="Project"/> — a spec, an API contract, a design
/// document, a screenshot — kept for the life of the project rather than for the life of one job or
/// one chat message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a table and not a jsonb column on <c>projects</c>.</strong> Sessions and jobs carry
/// their attachments inside <c>metadata_json</c> / <c>tasks_json</c>, and that shape is correct for
/// them: those arrays are rewritten as one unit by the code that owns them, and a message's attachment
/// dies with the message. A project library is the opposite on both counts. It is appended to and
/// deleted from one document at a time by concurrent callers, and every upload that rode in a single
/// jsonb column would rewrite the whole library — losing a concurrent writer's addition, re-serialising
/// every other document, and putting the project's entire document set inside one row's size ceiling.
/// Rows are also what make a document addressable: <c>GET /api/projects/{id}/documents/{docId}</c> and
/// a per-document delete need an identity, not an array scan.
/// </para>
/// <para>
/// <strong>Why the body of a file document is not in the row.</strong> The cap is
/// <see cref="MaxDocumentSizeBytes"/> per file, so a handful of documents is comfortably more than a
/// database row should hold, and the byte payload already has two homes (local disk and S3) that the
/// job-attachment path uses. The row stores the index and the pointer; the bytes stay where the
/// existing storage service puts them.
/// </para>
/// <para>
/// <strong>Why <see cref="ProjectId"/> has no foreign key.</strong> Every other table in this schema
/// that references a project (<c>project_jobs</c>, <c>project_personas</c>, <c>agent_memories</c>)
/// stores the id as a plain column with an index and no constraint, and <c>DeleteProject</c> deletes
/// its children explicitly. A constraint here would be the only one in the schema, would change what
/// <c>DeleteProject</c> is allowed to do, and would need the existing tables backfilled to be
/// consistent. Matching the convention keeps one deletion contract.
/// </para>
/// </remarks>
public class ProjectDocument
{
    /// <summary>Per-file ceiling, shared with session and job attachments so one number is the rule.</summary>
    public const long MaxDocumentSizeBytes = 25 * 1024 * 1024; // 25 MB, cf. SessionsController.MaxAttachmentSizeBytes

    /// <summary>
    /// How many documents one project may carry.
    /// </summary>
    /// <remarks>
    /// The per-message attachment cap is 10 because a message is read once; a project library is
    /// cumulative, so the ceiling that matters here is the one on the whole set. It is bounded because
    /// the agent-facing index renders every row (see
    /// <see cref="ProjectDocumentIndexFormatter.MaxRenderedDocuments"/>): an unbounded library would
    /// either silently truncate the index or grow the prompt without limit, and the operator deserves
    /// a 400 at upload time rather than either of those.
    /// </remarks>
    public const int MaxDocumentsPerProject = 50;

    /// <summary>
    /// Identity of the document.
    /// </summary>
    /// <remarks>
    /// A <see cref="Guid"/> rather than a slug: the id appears in a download URL and in the S3 key, and
    /// a caller-supplied slug in either position is a path-injection surface. The human-facing label is
    /// <see cref="Title"/>, which is never used to locate bytes.
    /// </remarks>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The owning project's slug id. No FK by convention — see the class remarks.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// The document's label, and the only part of a file document that reaches a prompt verbatim.
    /// </summary>
    /// <remarks>
    /// Defaults to the filename for uploads and is required for pasted text, because an index line
    /// reading "document 3" tells an agent nothing about whether it should fetch it.
    /// </remarks>
    public string Title { get; set; } = string.Empty;

    public ProjectDocumentKind Kind { get; set; } = ProjectDocumentKind.Text;

    /// <summary>
    /// The body of a <see cref="ProjectDocumentKind.Text"/> document; empty for uploads.
    /// </summary>
    /// <remarks>
    /// Capped by <see cref="MaxTextContentChars"/> rather than left as unbounded text, because this
    /// column is the one the agent index can inline. A 5 MB paste stored successfully and then rendered
    /// into every subsequent turn's prompt is a self-inflicted context overflow, so the limit is
    /// enforced on write and stated in the index line on read.
    /// </remarks>
    public string Content { get; set; } = string.Empty;

    /// <summary>Original filename of an upload, sanitised. Null for text documents.</summary>
    public string? FileName { get; set; }

    /// <summary>Media type used when serving bytes back. Null for text documents.</summary>
    public string? ContentType { get; set; }

    /// <summary>Byte size of an upload. Zero for text documents, whose size is <see cref="Content"/>.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// The S3 object key for an upload: <c>projects/{projectId}/documents/{docId}/{fileName}</c>.
    /// </summary>
    /// <remarks>
    /// Stored even though the local file is the first thing the read path tries. The job-attachment
    /// reader does the same, and for the same reason: the local disk is pod-local, so a document
    /// uploaded to pod A is only reachable from pod B through this key.
    /// </remarks>
    public string? S3Key { get; set; }

    /// <summary>
    /// The character ceiling on an inline text document.
    /// </summary>
    /// <remarks>
    /// 200,000 characters is roughly 50k tokens — large enough for a full API spec or a design doc,
    /// small enough that inlining one document cannot crowd out the task envelope and the sliding
    /// window, which are the sections a turn cannot work without.
    /// </remarks>
    public const int MaxTextContentChars = 200_000;

    /// <summary>
    /// Truncation marker used when a text body is rendered into a bounded index.
    /// </summary>
    public const string TruncationMarker = "… [truncated]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
