using System.Linq.Expressions;
using System.Text.Json;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Services;

/// <summary>
/// The stable wire names for the kinds of thing that can park a job on a human.
/// </summary>
/// <remarks>
/// These are strings on the wire rather than a serialized enum because the set is a contract with
/// clients that persist them (the Cockpit's notification seen-set keys on
/// <c>"{jobId}:{type}"</c> across page reloads). Adding a kind is additive; renaming one is a
/// breaking change to every stored key, so the values below are frozen.
/// </remarks>
public static class AttentionTypes
{
    /// <summary>A proposed checklist waiting for plan approval.</summary>
    public const string PlanApproval = "planApproval";

    /// <summary>Discovery questions waiting for answers.</summary>
    public const string DiscoveryQuestions = "discoveryQuestions";

    /// <summary>A turn that was interrupted mid-flight and needs a resume/retry/realign decision.</summary>
    public const string InterruptedTurn = "interruptedTurn";

    /// <summary>An open pull request waiting for a reviewer.</summary>
    public const string PrReview = "prReview";

    /// <summary>An agent parked its turn asking the operator to reply to it.</summary>
    public const string Escalation = "escalation";
}

/// <summary>
/// One unit of work waiting on the operator, aggregated across every project.
/// </summary>
/// <param name="Id">
/// Stable identity for the wait: <c>"{jobId}:{type}"</c>. A job contributes at most one item, so
/// this is the dedupe key a client can persist (notifications must not re-fire for the same wait on
/// every poll).
/// </param>
/// <param name="Type">One of <see cref="AttentionTypes"/>.</param>
/// <param name="Title">Short human label, safe to render in a banner.</param>
/// <param name="Detail">
/// The question, the reason, or the escalation body — truncated. May be null when the source row
/// carries no prose beyond <paramref name="Title"/>.
/// </param>
/// <param name="JobId">Job as a string; timeline cards key on the string form.</param>
/// <param name="PrUrl">
/// Populated for <see cref="AttentionTypes.PrReview"/> when the review card carries the linkage — that is
/// how <c>open_pull_request</c> tells the gate card which pull request to click through to.
/// </param>
/// <param name="Since">When the wait started, i.e. the age the operator sees as "waiting 3h".</param>
public sealed record AttentionItemDto(
    string Id,
    string Type,
    string Title,
    string? Detail,
    string JobId,
    string JobTitle,
    string ProjectId,
    string? ProjectName,
    Guid? CardId,
    string? PrUrl,
    int? PrNumber,
    DateTimeOffset Since);

/// <summary>The body of <c>GET /api/attention</c>.</summary>
public sealed record AttentionRollupDto(
    IReadOnlyList<AttentionItemDto> Items,
    int Count,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Answers "what needs me right now?" without the caller having to enumerate projects.
/// </summary>
public interface IAttentionQueryService
{
    Task<AttentionRollupDto> QueryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The one item <paramref name="jobId"/> is contributing to the rollup, or null when it is waiting
    /// on nobody. Same fold, same precedence, same prose as <see cref="QueryAsync"/> — see
    /// <see cref="AttentionQueryService.QueryJobAsync"/>.
    /// </summary>
    Task<AttentionItemDto?> QueryJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The classifier's kinds. The enum value is a precedence ranking used when one source reports two
/// waits at once — two cards in <see cref="TimelineCardStatus.PendingInput"/>, say — where the one an
/// operator can act on fastest wins.
/// </summary>
/// <remarks>
/// <para>
/// A job can genuinely be in two waits at once — an escalation posted before a plan card, say. The
/// notification surface wants one row per job (a bell that lists the same job three times is noise),
/// so the rollup picks a winner instead of a list. The loser is not lost: it is on the job's own
/// timeline, which is one click away.
/// </para>
/// <para>
/// Across sources the ranking is <em>not</em> what decides, because a high-precedence kind read from
/// a stale row would bury a fresher, real wait. There, a heartbeat that names a wait wins outright; an
/// unanswered escalation comes next; a pending card is consulted only when there is no heartbeat to
/// read at all — see <c>BuildItem</c>. The ranking above still decides the card-versus-escalation tie.
/// </para>
/// </remarks>
internal enum AttentionKind
{
    PlanApproval = 0,
    DiscoveryQuestions = 1,
    InterruptedTurn = 2,
    PrReview = 3,
    Escalation = 4
}

internal static class AttentionKindExtensions
{
    /// <summary>The frozen wire spelling for <paramref name="kind"/>.</summary>
    public static string ToWireName(this AttentionKind kind) => kind switch
    {
        AttentionKind.PlanApproval => AttentionTypes.PlanApproval,
        AttentionKind.DiscoveryQuestions => AttentionTypes.DiscoveryQuestions,
        AttentionKind.InterruptedTurn => AttentionTypes.InterruptedTurn,
        AttentionKind.PrReview => AttentionTypes.PrReview,
        AttentionKind.Escalation => AttentionTypes.Escalation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

/// <summary>
/// Reads the two places a "waiting on a human" state is recorded and folds them into one list.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a fold over two sources.</strong> There is no single column that says
/// "blocked on the operator", and no cross-project job-list endpoint either —
/// <c>GET /api/project-jobs</c> is scoped to one project. The state is spread across:
/// </para>
/// <list type="number">
/// <item>
/// <description><see cref="ProjectJob.ProgressHeartbeat"/> — free text written by the workflow and
/// the MCP tools. This is the only signal that survives when a gate is opened without a card (the
/// <c>manage_task set_checklist</c> path in <see cref="PlatformMcpTools"/> sets the heartbeat but
/// creates no card; the card is created by the orchestrated activity in
/// <c>TimelineActivities</c>).</description>
/// </item>
/// <item>
/// <description><see cref="JobTimelineCard"/> rows — the structured questions and the proposed
/// checklist, the parked escalation, and the pull-request gate. Every one of the five wait kinds has a
/// card type now: <see cref="TimelineCardType.PlanApproval"/> and
/// <see cref="TimelineCardType.DiscoveryQa"/> in
/// <see cref="TimelineCardStatus.PendingInput"/> carry the gates,
/// <see cref="TimelineCardType.Escalation3R"/> in <see cref="TimelineCardStatus.PendingInput"/> is what
/// <c>post_job_message</c> with <c>requires_reply</c> writes, and
/// <see cref="TimelineCardType.PrReview"/> in <see cref="TimelineCardStatus.PendingInput"/> is the review
/// gate <c>open_pull_request</c> attaches its linkage to.</description>
/// </item>
/// </list>
/// <para>
/// <strong>Why not the job ledger.</strong> This service used to read <c>job_messages</c> for the
/// escalation and pull-request kinds, on the reasoning that the <c>Escalation3R</c> and
/// <c>PrReview</c> card types existed in the enum but nothing wrote them. The ledger is gone: the same
/// platform change that removed it made those two card types the record of those two waits, with the
/// same <c>requiresReply</c>/<c>targetPersona</c>/<c>prUrl</c>/<c>prNumber</c> payload keys. Reading
/// cards also removes the one thing the fold could not do reliably before — decide whether a human has
/// answered an escalation — because the reply now flips the card out of
/// <see cref="TimelineCardStatus.PendingInput"/> (see the comments route on
/// <c>JobTimelineController</c>) and the wait simply stops being a pending card.
/// </para>
/// <para>
/// <strong>Deliberate drift from <c>ProjectJobsController.DeriveLifecycleStatus</c>.</strong> That
/// helper maps a heartbeat onto a single display label for the job list ("AwaitingApproval",
/// "AwaitingReview", "InProgress") and the frontend's union type is built around those strings. It
/// does not know about discovery waits, interrupts, or unanswered escalations, and changing its
/// output would change the job-list contract. This service therefore classifies heartbeats on its
/// own and reads it only as precedent for the substring style. If the two ever need to agree, the
/// right move is to make this the single classifier and have that helper project from it — not to
/// have this call that helper.
/// </para>
/// <para>
/// <strong>Fail-open on prose.</strong> The heartbeat is free text, so recognition is a small set of
/// substrings written by code in this repo (see <see cref="ClassifyHeartbeat"/>). A heartbeat that
/// matches nothing produces no item: a wrong notification is worse than a missing one, because the
/// job's own timeline still shows the wait. The reverse failure — inventing a wait — cannot happen
/// here.
/// </para>
/// <para>
/// <strong>Cost.</strong> Three indexed reads regardless of project count (jobs by status, cards by
/// job id, projects by id), then an in-memory fold. Nothing per project, nothing per job, no N+1.
/// The status filter keeps the driving set small: completed and archived jobs leave the rollup
/// entirely, which is also why resolved waits disappear without needing a "resolved" write.
/// </para>
/// </remarks>
public sealed class AttentionQueryService(
    AgentMeshDbContext db,
    ILogger<AttentionQueryService> logger) : IAttentionQueryService
{
    /// <summary>Banner-sized cap on <see cref="AttentionItemDto.Detail"/>.</summary>
    internal const int DetailCharLimit = 240;

    /// <summary>
    /// The jobs that can be waiting on someone. <see cref="ProjectJobStatus.Completed"/> and
    /// <see cref="ProjectJobStatus.Archived"/> jobs are done waiting by definition, and dropping them
    /// here is what makes resolution implicit: an approval flips the status or clears the heartbeat
    /// and the item vanishes on the next read, with no "resolved" write anywhere.
    /// </summary>
    /// <remarks>
    /// These three reads are written as explicit expression trees rather than inline lambdas so the
    /// shape can be pinned against the real provider: see
    /// <c>AttentionRollupSqlTranslationTests</c>, which renders them through
    /// <c>TestDbContextFactory.Postgres()</c>. The InMemory provider used by every other test in that
    /// file evaluates anything on the client, so without this handle a predicate that only Npgsql
    /// rejects would first show up as a 500 in production.
    /// </remarks>
    internal static Expression<Func<ProjectJob, bool>> OpenJobsPredicate { get; } =
        j => j.Status == ProjectJobStatus.Active || j.Status == ProjectJobStatus.Pending;

    /// <summary>
    /// The human-gate cards for a set of jobs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Explicit <c>||</c> comparisons rather than <c>array.Contains(c.CardType)</c>: this predicate has
    /// to survive translation to SQL, and the five-way form is unambiguous in both providers.
    /// </para>
    /// <para>
    /// Status is deliberately <em>not</em> filtered here, because the five types do not share one
    /// "this is a live wait" status: the gates and the escalation are <c>PendingInput</c>, a
    /// <see cref="TimelineCardType.TurnInterrupted"/> card is written <see cref="TimelineCardStatus.Active"/>
    /// by the worker and is used for enrichment only (the heartbeat decides whether the interrupt is still
    /// live), and filtering in SQL would mean a status test per type in a query that runs on every poll.
    /// The callers that care filter in memory — see <see cref="ClassifyPendingCard"/> and
    /// <see cref="FindCard"/>.
    /// </para>
    /// </remarks>
    internal static Expression<Func<JobTimelineCard, bool>> CardPredicate(List<string> jobIds)
        => c => jobIds.Contains(c.JobId)
                && (c.CardType == TimelineCardType.PlanApproval
                    || c.CardType == TimelineCardType.DiscoveryQa
                    || c.CardType == TimelineCardType.TurnInterrupted
                    || c.CardType == TimelineCardType.Escalation3R
                    || c.CardType == TimelineCardType.PrReview);

    public async Task<AttentionRollupDto> QueryAsync(CancellationToken cancellationToken = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;

        var jobs = await db.ProjectJobs.AsNoTracking()
            .Where(OpenJobsPredicate)
            .Select(j => new JobRow(j.Id, j.ProjectId, j.Title, j.ProgressHeartbeat, j.UpdatedAt))
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return new AttentionRollupDto([], 0, generatedAt);
        }

        // Cards key on the string form of the job id: that is the only spelling any writer uses for
        // JobTimelineCard.JobId, so no Guid index is needed once the ledger left the fold.
        var jobKeyByGuid = jobs.ToDictionary(j => j.Id, j => j.Id.ToString());
        var jobIds = jobKeyByGuid.Values.ToList();

        var cardLookup = await LoadCardsAsync(jobIds, cancellationToken);
        var projectNames = await LoadProjectNamesAsync(
            jobs.Select(j => j.ProjectId).Distinct(StringComparer.Ordinal).ToList(), cancellationToken);

        var items = new List<AttentionItemDto>(jobs.Count);
        foreach (var job in jobs)
        {
            var key = jobKeyByGuid[job.Id];
            cardLookup.TryGetValue(key, out var cards);
            projectNames.TryGetValue(job.ProjectId, out var projectName);

            var item = BuildItem(job, key, cards ?? [], projectName);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        // Oldest wait first: that is the one that has been blocked longest, and the order is stable
        // across polls so a bell's list does not reshuffle itself between refreshes.
        var ordered = items
            .OrderBy(i => i.Since)
            .ThenBy(i => i.JobTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogDebug("Attention rollup: {ItemCount} job(s) waiting on a human out of {JobCount} open",
            ordered.Count, jobs.Count);

        return new AttentionRollupDto(ordered, ordered.Count, generatedAt);
    }

    /// <summary>
    /// The single-job form of <see cref="QueryAsync"/>, for the write sites that have to announce or
    /// clear one job's wait without re-reading every other open job in the system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It shares everything that decides an answer with the rollup — <see cref="OpenJobsPredicate"/>,
    /// <see cref="CardPredicate"/>, <see cref="ClassifyHeartbeat"/> and <c>BuildItem</c> — and differs
    /// only in the driving set: one job id instead of all open ones. That is deliberate. The push events
    /// carry the same <see cref="AttentionItemDto"/> a client later gets from <c>GET /api/attention</c>,
    /// including <see cref="AttentionItemDto.Id"/>, which is the client's dedupe key: if the two paths
    /// classified independently, the same wait would arrive under two ids and a notification would fire
    /// twice for it, or a banner would never clear.
    /// </para>
    /// <para>
    /// <strong>Why a completed job answers null.</strong> The status filter is applied before anything
    /// else, so approving a plan or finishing a job produces null from the same call that produced an
    /// item a moment earlier — which is how <see cref="AttentionEvents.Resolved"/> gets raised without
    /// any code path having to write a "resolved" fact.
    /// </para>
    /// <para>
    /// Cost is two indexed reads (job by primary key, its cards) plus the project name, both of them the
    /// queries above narrowed to one id.
    /// </para>
    /// </remarks>
    public async Task<AttentionItemDto?> QueryJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await db.ProjectJobs.AsNoTracking()
            .Where(OpenJobsPredicate)
            .Where(j => j.Id == jobId)
            .Select(j => new JobRow(j.Id, j.ProjectId, j.Title, j.ProgressHeartbeat, j.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return null;
        }

        var key = job.Id.ToString();

        var cards = (await LoadCardsAsync([key], cancellationToken)).TryGetValue(key, out var rows)
            ? rows
            : [];

        var projectNames = await LoadProjectNamesAsync([job.ProjectId], cancellationToken);
        projectNames.TryGetValue(job.ProjectId, out var projectName);

        return BuildItem(job, key, cards, projectName);
    }

    private async Task<Dictionary<string, List<CardRow>>> LoadCardsAsync(
        List<string> jobIds,
        CancellationToken cancellationToken)
    {
        var cards = await db.JobTimelineCards.AsNoTracking()
            .Where(CardPredicate(jobIds))
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.JobId,
                c.CardType,
                c.Status,
                c.Title,
                c.StructuredPayloadJson,
                c.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return cards
            .GroupBy(c => c.JobId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new CardRow(c.Id, c.CardType, c.Status, c.Title,
                    c.StructuredPayloadJson, c.CreatedAt)).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, string>> LoadProjectNamesAsync(
        List<string> projectIds,
        CancellationToken cancellationToken)
    {
        var projects = await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        return projects.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves one job's signals into the single item an operator should be told about, or null
    /// when nothing is waiting.
    /// </summary>
    private static AttentionItemDto? BuildItem(
        JobRow job,
        string jobKey,
        IReadOnlyList<CardRow> cards,
        string? projectName)
    {
        // The heartbeat is the authority on whether a wait is still open: it is rewritten by whichever
        // code opened the gate, and a parked job writes nothing further, so a heartbeat that reads
        // "Executing task #3" means the agent is working — even if a PendingInput card from an earlier
        // phase was never resolved (nothing flips a TurnInterrupted card to a terminal status, and a
        // rejected plan leaves the old row behind while the revision is generated). Gate cards are
        // consulted only when there is no heartbeat at all, which is the safety net for a gate recorded
        // as a card and nothing else.
        //
        // An escalation card sits outside that gate rather than inside it, because post_job_message with
        // requires_reply parks the session without touching the heartbeat unless the job is in discovery:
        // the job would otherwise look busy while it waits for exactly the reply being asked for.
        AttentionKind? winner = ClassifyHeartbeat(job.ProgressHeartbeat);
        if (winner is null && string.IsNullOrWhiteSpace(job.ProgressHeartbeat))
        {
            winner = ClassifyPendingCard(cards);
        }

        var escalation = FindEscalationCard(cards);
        winner ??= escalation is not null ? AttentionKind.Escalation : null;

        if (winner is null)
        {
            return null;
        }

        // A structured card is preferred over the heartbeat when both describe the same wait, because
        // the card carries the questions or checklist the operator actually has to read.
        var card = winner.Value switch
        {
            AttentionKind.PlanApproval => FindCard(cards, TimelineCardType.PlanApproval, TimelineCardStatus.PendingInput),
            AttentionKind.DiscoveryQuestions => FindCard(cards, TimelineCardType.DiscoveryQa, TimelineCardStatus.PendingInput),
            AttentionKind.InterruptedTurn => FindCard(cards, TimelineCardType.TurnInterrupted, TimelineCardStatus.Active)
                                             ?? FindCard(cards, TimelineCardType.TurnInterrupted, TimelineCardStatus.PendingInput),
            AttentionKind.Escalation => escalation,
            AttentionKind.PrReview => FindCard(cards, TimelineCardType.PrReview, TimelineCardStatus.PendingInput),
            _ => null
        };

        var (title, detail) = Describe(winner.Value, job, card);

        // The pull-request linkage lives on the gate card, written by open_pull_request through
        // PrReviewCardPayload.Merge — the same reader the review poller uses, so the bell cannot offer a
        // click-through target that the poller does not consider the current pull request.
        var (prNumber, prUrl) = winner.Value == AttentionKind.PrReview && card is not null
            ? PrReviewCardPayload.ReadReference(card.StructuredPayloadJson)
            : ((int?)null, (string?)null);

        return new AttentionItemDto(
            Id: $"{jobKey}:{winner.Value.ToWireName()}",
            Type: winner.Value.ToWireName(),
            Title: title,
            Detail: detail,
            JobId: jobKey,
            JobTitle: job.Title,
            ProjectId: job.ProjectId,
            ProjectName: projectName,
            CardId: card?.Id,
            PrUrl: prUrl,
            PrNumber: prNumber,
            // Every kind now has a card on the path that created the wait, so the card's own creation
            // time is the honest "waiting since"; the job row is the fallback for a heartbeat-only gate.
            Since: card?.CreatedAt ?? job.UpdatedAt);
    }

    /// <summary>
    /// The pending human-gate cards for a job, mapped to a kind. Null when no card is waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TimelineCardType.TurnInterrupted"/> is excluded on purpose: the worker writes it as
    /// <see cref="TimelineCardStatus.Active"/>, and the interrupt is also recorded on the heartbeat.
    /// Treating an Active interrupt card as a gate here would keep a job in the inbox after the
    /// operator resumed it, because nothing ever flips that card to a terminal status. The heartbeat
    /// is the live signal; the card is only decoration once it is.
    /// </para>
    /// <para>
    /// <see cref="TimelineCardType.Escalation3R"/> and <see cref="TimelineCardType.PrReview"/> are
    /// excluded for a different reason: neither is a heartbeat-empty-only signal. An escalation is raised
    /// by <c>post_job_message</c> while the job is mid-task, so it is resolved outside this ranking —
    /// see <see cref="FindEscalationCard"/> — and a pending review card outlives the review it was
    /// opened for (only the gate card moves when the workflow merges, per
    /// <c>PrReviewPollingService</c>), so announcing one on card evidence alone would ring the bell for
    /// a job that is back to implementing. The heartbeat naming the review is the live signal there.
    /// </para>
    /// </remarks>
    private static AttentionKind? ClassifyPendingCard(IReadOnlyList<CardRow> cards)
    {
        AttentionKind? best = null;

        foreach (var card in cards)
        {
            if (card.Status != TimelineCardStatus.PendingInput)
            {
                continue;
            }

            var kind = card.CardType switch
            {
                TimelineCardType.PlanApproval => AttentionKind.PlanApproval,
                TimelineCardType.DiscoveryQa => AttentionKind.DiscoveryQuestions,
                _ => (AttentionKind?)null
            };

            if (kind is not null && (best is null || kind < best))
            {
                best = kind;
            }
        }

        return best;
    }

    private static CardRow? FindCard(
        IReadOnlyList<CardRow> cards,
        TimelineCardType type,
        TimelineCardStatus status)
        // Cards are loaded newest-first, so the first match is the current one — a job that went
        // through a rejected plan and a revision shows the revision.
        => cards.FirstOrDefault(c => c.CardType == type && c.Status == status);

    /// <summary>
    /// The escalation card, if any, that is waiting on the <em>operator</em> right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three writers produce these: <c>post_job_message</c> with <c>requires_reply</c> (the parked turn
    /// that used to be a ledger row), <c>SessionsController</c> forwarding an agent's
    /// <c>ask_question</c> into a job thread, and the worker's error pause in
    /// <c>HttpAgentTurnRunner</c>. All three write <see cref="TimelineCardStatus.PendingInput"/>, and all
    /// three flip or resolve that status when the human answers on the card — which is why there is no
    /// "has a human replied since" query left in this file.
    /// </para>
    /// <para>
    /// A card addressed to another persona is skipped: that is an internal consult which parks the
    /// other agent, and pinging a human about it is noise. Cards are newest-first, so the first match is
    /// the live ask.
    /// </para>
    /// </remarks>
    private static CardRow? FindEscalationCard(IReadOnlyList<CardRow> cards)
        => cards.FirstOrDefault(c => c.CardType == TimelineCardType.Escalation3R
                                     && c.Status == TimelineCardStatus.PendingInput
                                     && EscalationAwaitsOperator(c.StructuredPayloadJson));

    /// <summary>
    /// Maps a job's free-text heartbeat onto a wait kind, or null when the job is working.
    /// </summary>
    /// <remarks>
    /// The literals below are the exact strings written by
    /// <c>JobExecutionWorkflow</c> (<c>"Awaiting discovery Q&amp;A answers"</c>,
    /// <c>"Awaiting plan approval"</c>), <c>TimelineActivities</c> (the same two, plus
    /// <c>$"Turn interrupted: {reason}"</c>), <c>PlatformMcpTools</c>
    /// (<c>$"Awaiting plan approval{note}"</c>, <c>"Awaiting user input on planning questions"</c>,
    /// <c>$"Awaiting input from @{target} on planning questions"</c>,
    /// <c>$"Awaiting pull request review (PR #{n})"</c>) and <c>HttpAgentTurnRunner</c>. Substring
    /// matching is used rather than equality because the writers decorate these prefixes with notes,
    /// reasons and PR numbers.
    /// </remarks>
    internal static AttentionKind? ClassifyHeartbeat(string? heartbeat)
    {
        if (string.IsNullOrWhiteSpace(heartbeat))
        {
            return null;
        }

        var hb = heartbeat.Trim();

        // Order matters: a job can carry a decorated heartbeat that mentions an earlier phase
        // ("Plan approved. Executing task #2"), and the plan gate is the older, already-cleared one.
        if (Contains(hb, "Awaiting plan approval"))
        {
            return AttentionKind.PlanApproval;
        }

        if (Contains(hb, "Awaiting discovery Q&A") || Contains(hb, "planning questions"))
        {
            // "Awaiting input from @astra on planning questions" is an agent-to-agent consult: the
            // other persona is the one who has to answer, and pinging a human about it is noise.
            return HeartbeatTargetsPersona(hb) ? null : AttentionKind.DiscoveryQuestions;
        }

        if (Contains(hb, "Turn interrupted"))
        {
            return AttentionKind.InterruptedTurn;
        }

        if (Contains(hb, "Awaiting pull request review") || Contains(hb, "Awaiting PR review"))
        {
            return AttentionKind.PrReview;
        }

        return null;
    }

    /// <summary>
    /// True for the <c>"Awaiting input from @{target} on planning questions"</c> form when
    /// <c>{target}</c> is a persona rather than the operator.
    /// </summary>
    private static bool HeartbeatTargetsPersona(string heartbeat)
    {
        const string prefix = "Awaiting input from @";
        if (!heartbeat.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var target = heartbeat[prefix.Length..]
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim('.', ',', ':', ';') ?? string.Empty;

        return target.Length > 0
               && !target.Equals(PlatformMcpTools.OperatorPersonaId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the operator-facing label and body for the winning kind.
    /// </summary>
    /// <remarks>
    /// Every branch has to work with the heartbeat alone, because that is the only source on the
    /// card-less paths (the MCP <c>manage_task set_checklist</c> gate, the MCP discovery-questions
    /// park). Where a card exists its payload supplies the specifics; where it does not, the
    /// heartbeat is the best available prose.
    /// </remarks>
    private static (string Title, string? Detail) Describe(
        AttentionKind kind,
        JobRow job,
        CardRow? card)
    {
        var heartbeat = job.ProgressHeartbeat?.Trim();

        return kind switch
        {
            AttentionKind.PlanApproval => (
                card?.Title ?? "Plan awaiting approval",
                Truncate(ReadPayloadStrings(card?.StructuredPayloadJson, "tasks", "title", "reasoning")
                         ?? heartbeat)),

            AttentionKind.DiscoveryQuestions => (
                card?.Title ?? "Discovery questions awaiting answers",
                Truncate(ReadPayloadStrings(card?.StructuredPayloadJson, "questions", "question", "summary")
                         ?? heartbeat)),

            AttentionKind.InterruptedTurn => (
                card?.Title ?? Truncate(heartbeat, 80) ?? "Turn interrupted",
                Truncate(ReadPayloadStrings(card?.StructuredPayloadJson, null, null, "reason", "requestedBy")
                         ?? heartbeat)),

            AttentionKind.PrReview => (
                "Pull request awaiting review",
                Truncate(heartbeat
                         ?? ReadPayloadStrings(card?.StructuredPayloadJson, null, null, "note", "summary")
                         ?? card?.Title)),

            // The card body is the 3-R prose the agent wrote, so it becomes the banner text; the card's
            // own title ("Escalation from @rook to @operator") is the fallback when a writer stores no
            // content, and the heartbeat is the last resort for a card whose payload will not parse.
            AttentionKind.Escalation => (
                Truncate(FirstLine(EscalationBody(card)), 80) ?? card?.Title ?? "Escalation awaiting your reply",
                Truncate(EscalationBody(card)) ?? Truncate(card?.Title) ?? Truncate(heartbeat)),

            _ => ("Attention needed", Truncate(heartbeat))
        };
    }

    /// <summary>
    /// Reads the human-readable strings out of a card payload without deserialising it into a
    /// workflow contract.
    /// </summary>
    /// <param name="payloadJson">The card's <c>structured_payload_json</c>.</param>
    /// <param name="arrayProperty">
    /// The array to enumerate (<c>tasks</c>, <c>questions</c>), or null for a flat payload.
    /// </param>
    /// <param name="arrayItemProperty">The string to pull out of each element.</param>
    /// <param name="fallbackProperties">
    /// Top-level strings used when the array is absent or empty, in reading order. More than one is
    /// joined, which is how an interrupt payload becomes "reason · requested by".
    /// </param>
    /// <returns>
    /// The joined prose, or null when the payload is missing, unparseable, or carries none of the
    /// expected keys — which is why the caller always keeps a heartbeat fallback.
    /// </returns>
    /// <remarks>
    /// Read as raw JSON rather than via <c>PlanApprovalPayload</c>/<c>DiscoveryPayload</c> on purpose:
    /// those live in <c>AgentMesh.Core.Workflows</c> and are the workflow's wire contract. Coupling a
    /// read model to them means a workflow contract change breaks the inbox, and the payload for a
    /// card written by an older worker is not guaranteed to round-trip. Unknown keys are simply
    /// skipped, so the rollup degrades to the heartbeat instead of throwing.
    /// </remarks>
    internal static string? ReadPayloadStrings(
        string? payloadJson,
        string? arrayProperty,
        string? arrayItemProperty,
        params string[] fallbackProperties)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (arrayProperty is not null && arrayItemProperty is not null
                && doc.RootElement.TryGetProperty(arrayProperty, out var array)
                && array.ValueKind == JsonValueKind.Array)
            {
                var parts = array.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.Object
                                     && item.TryGetProperty(arrayItemProperty, out var value)
                                     && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(4)
                    .ToList();

                if (parts.Count > 0)
                {
                    return string.Join("; ", parts);
                }
            }

            var scalars = fallbackProperties
                .Select(property => doc.RootElement.TryGetProperty(property, out var element)
                                    && element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            return scalars.Count > 0 ? string.Join(" · ", scalars) : null;
        }
        catch (JsonException)
        {
            // A corrupt payload must not take the endpoint down; the heartbeat fallback covers it.
            return null;
        }
    }

    /// <summary>
    /// True unless a card payload says the ask is addressed to some persona other than the operator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the inverse of the rule it replaces. On the ledger the payload <em>was</em> the request —
    /// a row is just a row unless its payload says <c>requiresReply</c> — so absence had to mean "not a
    /// wait" and the classifier failed closed. On a card the wait is already recorded structurally, by
    /// <see cref="TimelineCardStatus.PendingInput"/> on an <see cref="TimelineCardType.Escalation3R"/>
    /// row, and the payload only says <em>who</em> has to answer: <c>post_job_message</c> writes
    /// <c>targetPersona</c> beside <c>requiresReply</c>, and the worker's error pause writes
    /// <c>"operator"</c> explicitly. An unreadable or partial payload must therefore not swallow a real
    /// park; only a target that names somebody else suppresses the item.
    /// </para>
    /// <para>
    /// A card addressed to another persona is an internal consult: it parks that agent, not the operator,
    /// and a desktop notification about it is noise.
    /// </para>
    /// </remarks>
    internal static bool EscalationAwaitsOperator(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            var target = TryReadString(doc.RootElement, "targetPersona");
            if (!string.IsNullOrWhiteSpace(target)
                && !target.Trim().TrimStart('@')
                    .Equals(PlatformMcpTools.OperatorPersonaId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // An explicit "requiresReply": false is a writer saying this card is not a request at all —
            // an FYI addressed to the operator. Only the explicit false counts; a missing key does not.
            return !TryReadBool(doc.RootElement, "requiresReply", out var requiresReply) || requiresReply;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// The prose an escalation card carries for the human: the 3-R body, the forwarded question, or the
    /// error the agent paused on.
    /// </summary>
    /// <remarks>
    /// <c>post_job_message</c> renders the body as <c>"@operator &lt;content&gt;"</c> so a human scanning
    /// the timeline sees that a message is addressed to them. In the bell that mention is redundant —
    /// only operator-facing waits get here at all — so it is dropped to keep the banner prose.
    /// </remarks>
    private static string? EscalationBody(CardRow? card)
    {
        var content = ReadPayloadStrings(card?.StructuredPayloadJson, null, null, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('@'))
        {
            var rest = trimmed[1..];
            var cut = rest.IndexOfAny([' ', '\t', '\r', '\n']);
            trimmed = cut < 0 ? string.Empty : rest[cut..].TrimStart();
        }

        return trimmed.Length > 0 ? trimmed : null;
    }

    private static bool TryReadBool(JsonElement obj, string property, out bool value)
    {
        value = false;
        if (!obj.TryGetProperty(property, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static string? TryReadString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? FirstLine(string? text)
        => text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

    /// <summary>Collapses the line breaks and caps the length so a banner stays a banner.</summary>
    internal static string? Truncate(string? text, int limit = DetailCharLimit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // A 3-R escalation is multi-paragraph prose; a notification body is one line. TrimEntries also
        // swallows the \r of a CRLF ledger body, which would otherwise render as a stray box glyph.
        var flat = string.Join(' ',
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (flat.Length <= limit)
        {
            return flat;
        }

        return string.Concat(flat.AsSpan(0, Math.Max(1, limit - 1)), "…");
    }

    internal sealed record JobRow(
        Guid Id,
        string ProjectId,
        string Title,
        string? ProgressHeartbeat,
        DateTimeOffset UpdatedAt);

    internal sealed record CardRow(
        Guid Id,
        TimelineCardType CardType,
        TimelineCardStatus Status,
        string Title,
        string StructuredPayloadJson,
        DateTimeOffset CreatedAt);
}
