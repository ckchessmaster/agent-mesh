using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Infrastructure.Data;

/// <summary>
/// The reads every caller needs on <see cref="ProjectDocument"/>, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why an extension on the DbContext rather than a service.</strong> There are five call sites
/// that want a project's document index — the worker's <c>PromptStackBuilder</c> (Layer 3) and four
/// legacy envelope composers in the Api (<c>ProjectJobsController</c> ×2, <c>JobTurnDispatcher</c>,
/// <c>SessionsController</c>) — and all five already hold an <see cref="AgentMeshDbContext"/>. A
/// registered service would mean touching five constructors and five DI graphs to add an abstraction two
/// queries deep; an extension method composes with the <c>db.ProjectDocuments</c> DbSet every caller can
/// already see. It also keeps the Api and the Worker on one implementation without either referencing the
/// other, which is the property that stops the two prompt paths drifting.
/// </para>
/// <para>
/// <strong>Two families of read, split by failure policy.</strong>
/// <list type="bullet">
///   <item>
///   <c>LoadForProjectAsync</c>, <c>LoadOneAsync</c> and <c>CountForProjectAsync</c> throw. They back the
///   API's endpoints, where a database failure must surface as an error the operator can act on rather
///   than an empty list that looks like an empty library.
///   </item>
///   <item>
///   The <c>*Safe*</c> methods swallow and return nothing. They back prompt assembly, which has a
///   standing rule: a subsystem that is unavailable degrades one layer, it does not fail the turn. That
///   rule is enforced here rather than at the call sites because four of the five prompt callers have no
///   try/catch of their own — the legacy envelope is a string interpolation inside a message-creation
///   path. A document table that does not exist yet (the API deployed a build ahead of the migration,
///   which is the order a rolling deploy produces) would otherwise turn every job turn into a 500.
///   </item>
/// </list>
/// The one exception in both families is cancellation: an <see cref="OperationCanceledException"/>
/// propagates, because a turn that was asked to stop must stop, and swallowing it would make
/// cancellation look like a slow query. This matches <c>PromptStackBuilder</c>'s handling exactly.
/// </para>
/// <para>
/// <strong>Why the prompt-side reads are bounded.</strong> A text document's body is a <c>text</c> column
/// capped at <see cref="ProjectDocument.MaxTextContentChars"/> characters, and a project may hold
/// <see cref="ProjectDocument.MaxDocumentsPerProject"/> documents. An unbounded library read on the turn
/// path is therefore a user-reachable multi-megagram fetch, paid on every turn of every job in the
/// project. The prompt-side reads take a ceiling and the renderer caps at the same number, so the worst
/// case is proportional to what is actually printed.
/// </para>
/// </remarks>
public static class ProjectDocumentQueries
{
    /// <summary>
    /// A project's documents, ordered the way the list endpoint and the prompt index both want them.
    /// </summary>
    /// <remarks>
    /// Ordering is part of this method's contract, not a detail of it: the formatter re-orders
    /// defensively, but a caller that reads 50 rows to render 25 needs the database's order to be the
    /// same order the cap keeps, or the index would show one set of documents in the API and a different
    /// set in the prompt. Title-then-id is the tie-break that stays stable across edits, because
    /// <c>UpdatedAt</c> changes whenever a document is touched and would reshuffle the cached prefix.
    /// <para>
    /// Throws. The prompt path goes through <see cref="LoadIndexPageSafeAsync"/> instead.
    /// </para>
    /// </remarks>
    /// <param name="take">
    /// Optional ceiling on rows returned. Pass
    /// <see cref="ProjectDocumentIndexFormatter.MaxRenderedDocuments"/> when the result is only going to
    /// be rendered into a prompt, so a large library does not pull bodies the renderer will never reach.
    /// </param>
    public static async Task<List<ProjectDocument>> LoadForProjectAsync(
        this AgentMeshDbContext db,
        string projectId,
        int? take = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var normalized = Normalize(projectId);
        if (normalized.Length == 0) return [];

        var query = db.ProjectDocuments
            .AsNoTracking()
            .Where(d => d.ProjectId == normalized)
            .OrderBy(d => d.Title)
            .ThenBy(d => d.Id);

        return take is > 0
            ? await query.Take(take.Value).ToListAsync(ct)
            : await query.ToListAsync(ct);
    }

    /// <summary>
    /// How many documents a project carries.
    /// </summary>
    /// <remarks>
    /// Two callers, both of which need the number rather than the rows: the endpoint that enforces
    /// <see cref="ProjectDocument.MaxDocumentsPerProject"/> before any byte is written, and the index
    /// that has to say how many documents it omitted.
    /// </remarks>
    public static async Task<int> CountForProjectAsync(
        this AgentMeshDbContext db,
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var normalized = Normalize(projectId);
        if (normalized.Length == 0) return 0;

        return await db.ProjectDocuments.CountAsync(d => d.ProjectId == normalized, ct);
    }

    /// <summary>
    /// One document, but only if it belongs to this project.
    /// </summary>
    /// <remarks>
    /// The ownership check is in the query rather than done after the read on purpose. A lookup by id
    /// alone would let a caller enumerate another project's document ids and read their titles,
    /// filenames, and S3 keys through a URL that looks like it is scoped by project. Filtering here makes
    /// "not yours" and "does not exist" the same 404, which is the only answer that does not leak.
    /// </remarks>
    public static async Task<ProjectDocument?> LoadOneAsync(
        this AgentMeshDbContext db,
        string projectId,
        Guid documentId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var normalized = Normalize(projectId);
        if (normalized.Length == 0) return null;

        return await db.ProjectDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == normalized, ct);
    }

    /// <summary>
    /// A page of the library plus the size of the whole library.
    /// </summary>
    /// <remarks>
    /// The total travels with the page because the renderer needs it to say how many documents it
    /// omitted. A bounded read alone cannot distinguish "this is the whole library" from "this is the
    /// first 25 of 50", and an index that silently shows a prefix teaches an agent to conclude that a
    /// spec it cannot see does not exist.
    /// </remarks>
    public sealed record IndexPage(IReadOnlyList<ProjectDocument> Documents, int TotalCount)
    {
        /// <summary>The page projected onto the shapes the formatter renders.</summary>
        public IReadOnlyList<ProjectDocumentEntry> ToEntries() =>
            ProjectDocumentIndexFormatter.ToEntries(Documents);
    }

    /// <summary>
    /// The first <paramref name="take"/> documents in title order, plus the project's total.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two queries at worst, and the second is a <c>COUNT</c> over the same <c>(project_id, title)</c>
    /// index the first seeks on, across at most
    /// <see cref="ProjectDocument.MaxDocumentsPerProject"/> rows. The alternative — reading the whole
    /// library to render a prefix of it — is the one that has to be justified: see the class remarks on
    /// why an unbounded read on the turn path is user-reachable. Bounding the read and paying one indexed
    /// count keeps the worst case proportional to what is rendered.
    /// </para>
    /// <para>
    /// The count is skipped when the page came back short, because a short page <em>is</em> proof the
    /// page is complete: there is nothing left to count. That keeps an empty or small project's prompt
    /// assembly at one query, which is the overwhelmingly common case.
    /// </para>
    /// </remarks>
    public static async Task<IndexPage> LoadIndexPageSafeAsync(
        this AgentMeshDbContext db,
        string projectId,
        int take,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            var documents = await db.LoadForProjectAsync(projectId, take, ct);
            var total = documents.Count < take
                ? documents.Count
                : await db.CountForProjectAsync(projectId, ct);

            return new IndexPage(documents, total);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new IndexPage([], 0);
        }
    }

    /// <summary>
    /// The uploaded (file) documents of a project, in title order, bounded by <paramref name="take"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtered in the query rather than after the read. Staging wants the first N <em>files</em> by title
    /// across the whole library, and a caller that read the first N documents of all kinds would miss a
    /// <c>spec.pdf</c> that sorts after twenty pasted notes — the document an agent most needs would be
    /// the one never handed to it.
    /// </para>
    /// <para>
    /// The <c>kind</c> predicate is not in the index, so this is a scan of one project's rows (at most
    /// <see cref="ProjectDocument.MaxDocumentsPerProject"/>) followed by a bounded sort. That is the right
    /// shape: the alternative is a partial index on <c>(project_id, title) where kind = 1</c>, which is a
    /// migration to optimise a 50-row scan.
    /// </para>
    /// </remarks>
    public static async Task<List<ProjectDocument>> LoadFilesForStagingSafeAsync(
        this AgentMeshDbContext db,
        string projectId,
        int take,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            var normalized = Normalize(projectId);
            if (normalized.Length == 0 || take <= 0) return [];

            return await db.ProjectDocuments
                .AsNoTracking()
                .Where(d => d.ProjectId == normalized && d.Kind == ProjectDocumentKind.File)
                .OrderBy(d => d.Title)
                .ThenBy(d => d.Id)
                .Take(take)
                .ToListAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Project ids are slugs stored lowercase; every caller normalizes before comparing.
    /// </summary>
    /// <remarks>
    /// Matches <c>ProjectsController</c>/<c>ProjectJobsController</c>, which both do
    /// <c>id.Trim().ToLowerInvariant()</c> before a lookup. Doing it here means a caller that forgets
    /// still gets the right rows rather than an empty library — the failure mode of a missed normalize
    /// in a prompt path is silently missing context, which nobody notices until an agent ignores a spec.
    /// </remarks>
    private static string Normalize(string? projectId) => projectId?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// True for the failures that mean "the library is unavailable" rather than "this code is wrong".
    /// </summary>
    /// <remarks>
    /// The same narrow list <c>PromptStackBuilder.IsRecoverable</c> uses, and the same reasoning: a
    /// blanket catch would turn a real defect — an NRE in the renderer, a mis-mapped entity — into a
    /// silently short prompt that every agent works around forever. These are the failures a healthy
    /// process produces against an unhealthy or not-yet-migrated database.
    /// </remarks>
    private static bool IsRecoverable(Exception ex) =>
        ex is not OperationCanceledException &&
        ex is DbUpdateException or InvalidOperationException or KeyNotFoundException;
}
