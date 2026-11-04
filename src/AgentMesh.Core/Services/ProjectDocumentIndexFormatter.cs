using System.Text;

namespace AgentMesh.Core.Services;

/// <summary>
/// Renders a project's supporting-document library into the bounded text block an agent reads.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is one class and not two.</strong> There are two prompt paths in this codebase.
/// The current one composes the layered stack in
/// <see cref="AgentMesh.Core.Services"/>-adjacent worker code (<c>PromptStackBuilder.ProjectPrompt</c>,
/// Layer 3); the legacy one concatenates a <c>[Project Context] / [Project Guidelines]</c> envelope at
/// four call sites in the Api (<c>ProjectJobsController</c> ×2, <c>JobTurnDispatcher</c>,
/// <c>SessionsController</c>). Both are live: <c>JobTurnDispatcher</c> picks the layered stack when the
/// worker composed one and the flat envelope when it did not, so which shape an agent sees depends on
/// deployment order. If the document index were written into each path separately, the two would drift
/// the first time someone edited one — and the drift would be invisible until an agent on one path
/// started ignoring a spec that an agent on the other path read. Both paths call
/// <see cref="FormatDocumentIndex"/> for the index and <see cref="FormatProjectEnvelope"/> for the
/// legacy preamble, so the text an agent sees about its documents is one implementation.
/// </para>
/// <para>
/// <strong>Why an index and not the documents themselves.</strong> A project library is capped at
/// <see cref="ProjectDocument.MaxDocumentsPerProject"/> documents of up to
/// <see cref="ProjectDocument.MaxDocumentSizeBytes"/> bytes each. Inlining the bodies would put a
/// multi-megabyte block in the cached prefix of every turn of every job in the project, which is the
/// opposite of what the layering exists to do. So the prompt carries a table of contents — title, kind,
/// filename, id, download URL — plus a short excerpt for the text documents, and the agent fetches the
/// one it actually needs.
/// </para>
/// <para>
/// <strong>Why the block is byte-stable.</strong> Layer 3 sits in the cached prefix, so anything that
/// changes per turn invalidates everything below it. Two properties of a naive render would break that:
/// <c>CreatedAt</c>/<c>UpdatedAt</c> timestamps (a document edit would reshuffle the block and, worse,
/// make the ordering depend on wall-clock time) and an unordered database read. So the order here is
/// <c>Title</c> then <c>Id</c> — both stable across edits — and no timestamp is emitted. The cost is
/// that the index cannot say "added 5 minutes ago"; the benefit is that uploading a document invalidates
/// the cache exactly once instead of on every read of every job.
/// </para>
/// <para>
/// <strong>Why nothing here throws.</strong> Prompt assembly degrades one layer rather than failing a
/// turn, and this formatter is part of that contract: a null list, a null title, or a document with no
/// filename renders as a shorter block, never as an exception.
/// </para>
/// </remarks>
public static class ProjectDocumentIndexFormatter
{
    /// <summary>
    /// How many documents one prompt block lists.
    /// </summary>
    /// <remarks>
    /// Below <see cref="ProjectDocument.MaxDocumentsPerProject"/> on purpose: the library cap is what
    /// the API enforces on write, this is what the prompt can carry. When a project exceeds it the block
    /// says so and names the count it dropped, because an agent that believes it saw the whole library
    /// will not go looking for the spec that is not listed.
    /// </remarks>
    public const int MaxRenderedDocuments = 25;

    /// <summary>
    /// How much of a text document's body is quoted inline.
    /// </summary>
    /// <remarks>
    /// Enough to tell "API contract v2" from "meeting notes" without opening either, and small enough
    /// that 25 entries cannot dominate the layer. File documents get no excerpt — their bytes are not in
    /// the row, and fetching them here would move the whole cost into the prompt.
    /// </remarks>
    public const int MaxExcerptChars = 400;

    /// <summary>Section header, shared by both prompt paths so a test can assert on one string.</summary>
    public const string SectionHeader = "[PROJECT SUPPORTING DOCUMENTS]";

    /// <summary>
    /// The path an agent (or the dashboard) fetches a document's bytes from.
    /// </summary>
    /// <remarks>
    /// Built here rather than at each call site because it is also stored on the row and returned over
    /// the API: three places composing the same URL string is three ways to get one wrong.
    /// </remarks>
    public static string BuildContentUrl(string projectId, Guid documentId) =>
        BuildContentUrl(projectId, documentId.ToString("N"));

    /// <summary>
    /// The string-id overload used by the index formatter, whose entries carry an already-formatted id.
    /// </summary>
    /// <remarks>
    /// The id is escaped rather than interpolated because it reaches this method from a route value on
    /// the read path, and a URL built by concatenation is where a crafted id becomes a path traversal or
    /// a second segment. <c>Guid.ToString("N")</c> can never need escaping; a caller-supplied string can.
    /// </remarks>
    public static string BuildContentUrl(string projectId, string? documentId)
    {
        var id = documentId?.Trim() ?? string.Empty;
        if (Guid.TryParse(id, out var parsed))
        {
            id = parsed.ToString("N");
        }

        return $"/api/projects/{Uri.EscapeDataString(projectId?.Trim() ?? string.Empty)}/documents/" +
               $"{Uri.EscapeDataString(id)}/content";
    }

    /// <summary>
    /// The index block for a library, or <c>null</c> when there is nothing to list.
    /// </summary>
    /// <remarks>
    /// Returning <c>null</c> rather than an empty string is what lets both callers omit the section
    /// entirely for a project with no documents: a header announcing an empty library is noise the agent
    /// has to read to discover there is nothing to read.
    /// <para>
    /// <paramref name="totalCount"/> is the size of the whole library when the caller passed a bounded
    /// page. It defaults to the list it is given, which is correct for a caller that loaded everything
    /// and is the safe default: a caller that forgot to pass a total gets an index that claims to be
    /// complete, which is the failure mode the parameter exists to prevent, so it is at least visible as
    /// a wrong claim rather than a silently missing one.
    /// </para>
    /// </remarks>
    public static string? FormatDocumentIndex(
        IReadOnlyCollection<ProjectDocumentEntry>? documents,
        string projectId,
        int? totalCount = null)
    {
        if (documents is null || documents.Count == 0) return null;

        // Ordered here, not in the query, for the same reason the roster is: the cache property belongs
        // to the renderer. A caller that loads rows another way still gets byte-identical output.
        var ordered = documents
            .Where(d => d is not null)
            .OrderBy(d => d.Title ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        // The caller's total wins; a total smaller than the page it handed over is a caller bug and would
        // make the block claim it dropped rows it actually printed.
        var total = Math.Max(totalCount ?? ordered.Count, ordered.Count);

        var sb = new StringBuilder();
        sb.Append(SectionHeader).Append('\n');
        sb.Append("Documents attached to this project. Fetch one by its URL before relying on it; the ")
          .Append("excerpt is a label, not the content.\n");

        var rendered = 0;
        foreach (var doc in ordered)
        {
            if (rendered >= MaxRenderedDocuments) break;
            rendered++;

            sb.Append("* ").Append(Describe(doc, projectId)).Append('\n');
        }

        if (total > rendered)
        {
            sb.Append("… ").Append(total - rendered)
              .Append(" more document(s) not listed (GET /api/projects/")
              .Append(Uri.EscapeDataString(projectId?.Trim() ?? string.Empty))
              .Append("/documents for the full library)\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// One index line: <c>Title — kind, filename, id, size, URL</c> plus an excerpt for text documents.
    /// </summary>
    internal static string Describe(ProjectDocumentEntry doc, string projectId)
    {
        var title = string.IsNullOrWhiteSpace(doc.Title) ? "Untitled document" : doc.Title.Trim();

        var sb = new StringBuilder();
        sb.Append(title).Append(" — ").Append(doc.KindLabel);

        if (!string.IsNullOrWhiteSpace(doc.FileName))
        {
            sb.Append(", file: ").Append(doc.FileName!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(doc.ContentType))
        {
            sb.Append(", type: ").Append(doc.ContentType!.Trim());
        }

        if (doc.SizeBytes > 0)
        {
            sb.Append(", ").Append(FormatSize(doc.SizeBytes));
        }

        sb.Append(", doc id: ").Append(doc.Id);
        sb.Append(" → ").Append(BuildContentUrl(projectId, doc.Id));

        var excerpt = doc.Excerpt(MaxExcerptChars);
        if (!string.IsNullOrEmpty(excerpt))
        {
            sb.Append("\n  excerpt: ").Append(excerpt);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The legacy envelope's project preamble: guidelines plus the document index.
    /// </summary>
    /// <remarks>
    /// Replaces the <c>[Project Guidelines: …]</c> ternary at the four flat-envelope sites. Those sites
    /// still compose their own <c>[Project Context]</c> and <c>[Job Context]</c> lines, because the
    /// branch and job text differ per call; what they must not each re-implement is how the project's
    /// own instructions and library are described.
    /// <para>
    /// Returns an empty string when the project has neither instructions nor documents, which is what
    /// keeps today's prompts byte-identical for a project with an empty library — the property the
    /// existing envelope tests assert on.
    /// </para>
    /// </remarks>
    public static string FormatProjectEnvelope(
        string? projectInstructions,
        IReadOnlyCollection<ProjectDocumentEntry>? documents,
        string projectId,
        int? totalCount = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(projectInstructions))
        {
            sb.Append("[Project Guidelines: ").Append(projectInstructions.Trim()).Append("]\n");
        }

        var index = FormatDocumentIndex(documents, projectId, totalCount);
        if (index is not null)
        {
            sb.Append(index);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a persisted row onto the entry the formatter renders.
    /// </summary>
    /// <remarks>
    /// A projection rather than the entity so the formatter stays usable from a path that has the rows
    /// in hand (the Api, which loads them for staging) and from one that has only a summary.
    /// </remarks>
    public static ProjectDocumentEntry ToEntry(AgentMesh.Core.Models.ProjectDocument document) =>
        new(
            Id: document.Id.ToString("N"),
            Title: document.Title ?? string.Empty,
            KindLabel: document.Kind == AgentMesh.Core.Models.ProjectDocumentKind.File ? "file" : "text",
            FileName: document.FileName,
            ContentType: document.ContentType,
            SizeBytes: document.Kind == AgentMesh.Core.Models.ProjectDocumentKind.File
                ? document.SizeBytes
                : document.Content?.Length ?? 0,
            Content: document.Kind == AgentMesh.Core.Models.ProjectDocumentKind.Text ? document.Content : null);

    public static IReadOnlyList<ProjectDocumentEntry> ToEntries(
        IEnumerable<AgentMesh.Core.Models.ProjectDocument>? documents)
    {
        if (documents is null) return [];

        var list = new List<ProjectDocumentEntry>();
        foreach (var document in documents)
        {
            if (document is null) continue;
            list.Add(ToEntry(document));
        }

        return list;
    }

    /// <summary>
    /// Collapses a body into a single-line excerpt of at most <paramref name="maxChars"/>.
    /// </summary>
    /// <remarks>
    /// Whitespace is collapsed because the index is a list of one-line entries: a pasted document with
    /// hard returns would otherwise break the entry across lines and read as several documents. Newlines
    /// become <c>' '</c> rather than being stripped so words at a line break do not fuse.
    /// </remarks>
    internal static string? ExcerptOf(string? content, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var collapsed = new StringBuilder(content.Length);
        var lastWasSpace = false;
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch))
            {
                // A run of whitespace collapses to a single space. The Length guard is what drops a
                // leading run (nothing has been written yet, so there is nothing to separate); lastWasSpace
                // is what drops the second and third characters of a run, so "\n\n" and "   " each cost one
                // space rather than two.
                if (!lastWasSpace && collapsed.Length > 0) collapsed.Append(' ');
                lastWasSpace = true;
                continue;
            }

            collapsed.Append(ch);
            lastWasSpace = false;
        }

        var text = collapsed.ToString().Trim();
        if (text.Length == 0) return null;

        if (maxChars > 0 && text.Length > maxChars)
        {
            // Cut on a word boundary so the excerpt does not end mid-identifier, then mark that the rest
            // exists. Trimming the trailing space after the cut keeps the marker flush against a word.
            var cut = text[..maxChars];
            var lastSpace = cut.LastIndexOf(' ');
            if (lastSpace > maxChars / 2) cut = cut[..lastSpace];
            text = cut.TrimEnd() + AgentMesh.Core.Models.ProjectDocument.TruncationMarker;
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Human-readable byte count, e.g. <c>1.4 MB</c>.
    /// </summary>
    /// <remarks>
    /// An agent deciding whether to fetch a document needs an order of magnitude, not a byte count:
    /// <c>1482391</c> costs a comparison it has to do anyway.
    /// </remarks>
    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>
/// One document as the index sees it. Deliberately free of EF and of the entity so the formatter can be
/// exercised without a database and reused by a caller that holds a summary rather than a row.
/// </summary>
/// <param name="Id">Document id in the <c>N</c> format used in URLs.</param>
/// <param name="Content">
/// The body for a text document, <c>null</c> for a file. Only a text body can be excerpted, because
/// only a text body is in the row.
/// </param>
public sealed record ProjectDocumentEntry(
    string Id,
    string Title,
    string KindLabel,
    string? FileName = null,
    string? ContentType = null,
    long SizeBytes = 0,
    string? Content = null)
{
    /// <summary>The inline excerpt, or <c>null</c> when there is no text body to excerpt.</summary>
    public string? Excerpt(int maxChars) =>
        string.IsNullOrEmpty(Content) ? null : ProjectDocumentIndexFormatter.ExcerptOf(Content, maxChars);
}
