using AgentMesh.Api.Controllers;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;

namespace AgentMesh.Api.Services;

/// <summary>
/// Copies a project's uploaded documents into the turn session's attachment directory so the agent can
/// open them with <c>read_file</c> instead of fetching them over HTTP, and renders the project preamble
/// that tells it they are there.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why staging at all, when the prompt already names a download URL.</strong> The index tells an
/// agent a document exists; it does not put the bytes in front of it. An agent that has to fetch a file
/// needs a working API URL, credentials, and a network path from inside a sandboxed runner — and the
/// common case is a spec or a screenshot that is already sitting on the same disk. Staging turns "read
/// the API contract" into one <c>read_file</c> call, and for images it is what lets the CLI hand the file
/// to a multimodal model as a visual input rather than describing a filename.
/// </para>
/// <para>
/// <strong>Why the turn session's directory and not the workspace.</strong>
/// <c>SessionsController.GetAttachmentDirectory(turnSessionId)</c> is where the job-attachment path
/// already puts staged files (<c>JobTurnDispatcher.StageAttachments</c>,
/// <c>ProjectJobsController.PostJobMessage</c>), and the CLI resolves staged files relative to
/// <c>.agentmesh/attachments/{id}_{fileName}</c> (<c>AgentAdapters.FormatAttachedFilesBlock</c>). Writing
/// anywhere else would mean inventing a second location the runner does not look in.
/// </para>
/// <para>
/// <strong>Why only file documents are staged.</strong> A text document has no bytes on disk — its body
/// is a column. Materialising one into a file would mean writing a derived artifact per turn, in a
/// directory the delete path does not know about, to duplicate content the prompt already carries as an
/// excerpt and the content endpoint already serves. So the rule is one sentence: uploads are staged,
/// pasted text is referenced.
/// </para>
/// <para>
/// <strong>Why a failure here never fails the turn.</strong> Staging is an optimisation on top of the
/// index. A source file that is not on this pod (uploaded to a different replica) leaves the document
/// exactly where it was before this feature: listed in the index, fetchable by URL. Throwing would make a
/// missing local copy take down a job turn, which is a worse failure than a longer path to the file.
/// </para>
/// </remarks>
public static class ProjectDocumentStaging
{
    /// <summary>
    /// What one turn needs from a project's library: the attachment list to hand the runner, and the
    /// project preamble to put in front of the prompt.
    /// </summary>
    /// <remarks>
    /// <see cref="Attachments"/> is what the runner is handed and <see cref="ProjectPreamble"/> is what the
    /// prompt says. Both are built from one pair of bounded reads, which is what keeps the index and the
    /// staged files from being computed off two different snapshots of the library.
    /// </remarks>
    public sealed record Prepared(
        IReadOnlyList<JobAttachmentDto> Attachments,
        string ProjectPreamble);

    /// <summary>
    /// Loads a project's library once, stages its uploaded documents into the turn session, and renders
    /// the guidelines-plus-index preamble for the prompt.
    /// </summary>
    /// <param name="existing">
    /// Attachments already destined for this turn (the message's own, plus the active task's). Staged
    /// documents are appended to these, deduplicated by id, so a document a user also attached to the
    /// message is not listed twice.
    /// </param>
    /// <param name="projectInstructions">
    /// The project's free-text instructions, emitted as the <c>[Project Guidelines: …]</c> line the
    /// legacy envelope already writes. Pass <c>null</c> to get the document index alone.
    /// </param>
    /// <param name="composePreamble">
    /// Pass <c>false</c> when the caller already has a layered prompt stack whose Layer 3 carries the
    /// index, so the envelope text is not built and then discarded. The staging still runs: the files are
    /// useful on both paths, while the preamble is only ever rendered by one of them.
    /// </param>
    public static async Task<Prepared> PrepareAsync(
        AgentMeshDbContext db,
        string projectId,
        Guid turnSessionId,
        IReadOnlyList<JobAttachmentDto>? existing = null,
        string? projectInstructions = null,
        bool composePreamble = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Two bounded reads, not one unbounded one, and each is bounded by the cap of the thing it feeds:
        // the index by MaxRenderedDocuments, the staging by MaxAttachmentsPerMessage. A single unbounded
        // load would pull every text body in the library (up to
        // ProjectDocument.MaxTextContentChars characters each) on every turn of every job, to render at
        // most 25 index lines and copy at most 10 files.
        //
        // They are not merged into one read because a filter-then-take cannot serve both: the index wants
        // the first 25 documents of any kind, staging wants the first 10 files across all kinds, and a
        // project whose earliest-titled documents are pasted notes needs spec.pdf further down the list
        // to still be staged. See LoadFilesForStagingSafeAsync.
        //
        // Both degrade to empty rather than throwing (LoadIndexPageSafeAsync /
        // LoadFilesForStagingSafeAsync own that policy), so a library that cannot be read costs this turn
        // its documents and nothing else.
        var indexPage = composePreamble
            ? await db.LoadIndexPageSafeAsync(
                projectId, ProjectDocumentIndexFormatter.MaxRenderedDocuments, ct)
            : null;

        var files = await db.LoadFilesForStagingSafeAsync(
            projectId, SessionsController.MaxAttachmentsPerMessage, ct);

        var attachments = StageFiles(files, projectId, turnSessionId, existing);

        var preamble = indexPage is null
            ? string.Empty
            : ProjectDocumentIndexFormatter.FormatProjectEnvelope(
                projectInstructions, indexPage.ToEntries(), projectId, indexPage.TotalCount);

        return new Prepared(attachments, preamble);
    }

    /// <summary>
    /// Stages a project's uploaded documents and returns the turn's combined attachment list.
    /// </summary>
    /// <remarks>
    /// The preamble-free form, for a caller that already has its project context from somewhere else (the
    /// layered stack, or a preamble it composed earlier in the same method).
    /// </remarks>
    public static async Task<List<JobAttachmentDto>> StageFileDocumentsAsync(
        AgentMeshDbContext db,
        string projectId,
        Guid turnSessionId,
        IReadOnlyList<JobAttachmentDto>? existing = null,
        CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(
            db, projectId, turnSessionId, existing, composePreamble: false, ct: ct);

        return prepared.Attachments.ToList();
    }

    /// <summary>
    /// Copies the file documents into the turn session's attachment directory and appends them to the
    /// turn's attachment list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the staged documents join the attachment payload.</strong> The payload is what the CLI
    /// renders as <c>[Attached Files in Workspace]</c>, naming each file's relative path. Staging bytes
    /// without telling the agent where they are would leave the file discoverable only by an agent that
    /// thinks to run <c>ls</c> — the exact behaviour the platform spends a whole prompt layer telling
    /// agents not to do. There is precedent for the merge in this same code path: the active task's
    /// attachments are already folded into a turn's attachment list at
    /// <c>ProjectJobsController.PostJobMessage</c> even though the user did not attach them to that
    /// message.
    /// </para>
    /// <para>
    /// <strong>Why the count is capped.</strong> <see cref="SessionsController.MaxAttachmentsPerMessage"/>
    /// is the ceiling on what one turn's payload may carry, and it is what the CLI's block and the
    /// dashboard's chips are sized for. A project is allowed
    /// <see cref="ProjectDocument.MaxDocumentsPerProject"/> documents, so an uncapped stage would put 50
    /// files in front of every turn of every job in the project.
    /// </para>
    /// <para>
    /// <strong>What the cap does and does not guarantee.</strong> The staged set is the head of the
    /// file documents in title order, and the index lists the head of <em>all</em> documents in the same
    /// title order — so the two orderings agree on the relative position of every file, but the two sets
    /// are not identical: a project whose earliest-titled documents are all pasted text will have an index
    /// that lists text and a staged set of files that appear further down. That is fine rather than
    /// confusing, because each section names what it contains: the index gives the URL for what it lists,
    /// and the CLI's attachment block gives the local path for what was copied. Neither ever claims
    /// something the other does not have.
    /// </para>
    /// <para>
    /// Synchronous, and deliberately so: the source is a local path lookup and the copy is a
    /// <see cref="System.IO.File"/> call, neither of which has anything to await. Keeping it sync lets
    /// <see cref="PrepareAsync"/> feed one load to both the render and the copy with no second await
    /// between them.
    /// </para>
    /// </remarks>
    private static List<JobAttachmentDto> StageFiles(
        IReadOnlyList<ProjectDocument> files,
        string projectId,
        Guid turnSessionId,
        IReadOnlyList<JobAttachmentDto>? existing)
    {
        // Already filtered to file documents with a filename, already in title order, already capped:
        // LoadFilesForStagingSafeAsync does that in the query so the ceiling applies across the whole
        // library rather than to whatever the first page happened to contain.
        var attachments = new List<JobAttachmentDto>(existing ?? []);
        if (files.Count == 0) return attachments;

        // GetDocumentDirectory creates the directory it names, so it is only asked for once there is
        // something to look for. A project whose rows all point at another pod's files must not leave an
        // empty artifacts directory behind on every turn.
        var sourceDir = ProjectsController.GetDocumentDirectory(projectId);

        foreach (var document in files)
        {
            if (attachments.Any(a => string.Equals(a.Id, document.Id.ToString("N"), StringComparison.Ordinal)))
            {
                // Already coming from the message or the task; copying it again would list it twice.
                continue;
            }

            var safeName = SessionsController.SanitizeFileName(document.FileName!);
            var source = ProjectsController.FindLocalDocumentFile(sourceDir, document.Id, safeName);
            if (source is null) continue;

            // Resolved here rather than once above the loop because GetAttachmentDirectory also creates:
            // asking for it before knowing there is a file to copy would mint an empty attachment
            // directory for a turn that stages nothing. It is the single source of truth for where staged
            // files live, so the alternative — recomputing the path — is the drift this avoids.
            var targetDir = SessionsController.GetAttachmentDirectory(turnSessionId);
            var destination = Path.Combine(targetDir, $"{document.Id:N}_{safeName}");
            try
            {
                System.IO.File.Copy(source, destination, overwrite: true);
            }
            catch
            {
                // Best-effort, exactly as StageAttachments does: an unreadable source or a full disk
                // costs this turn one staged file, not the turn.
                continue;
            }

            attachments.Add(new JobAttachmentDto(
                Id: document.Id.ToString("N"),
                JobId: turnSessionId,
                FileName: safeName,
                ContentType: string.IsNullOrWhiteSpace(document.ContentType)
                    ? SessionsController.GuessContentTypeFromExtension(safeName)
                    : document.ContentType,
                SizeBytes: document.SizeBytes,
                S3Key: document.S3Key ?? string.Empty,
                DownloadUrl: ProjectDocumentIndexFormatter.BuildContentUrl(projectId, document.Id),
                CreatedAt: document.CreatedAt));
        }

        return attachments;
    }
}
