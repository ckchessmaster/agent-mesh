using System.Text.Json;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;

namespace AgentMesh.Api.Services;

/// <summary>Configuration for <see cref="PrReviewPollingService"/>.</summary>
public class PrReviewPollingOptions
{
    public const string SectionName = "PrReviewPolling";

    /// <summary>
    /// Master switch. Defaults on: a deployment without GitHub credentials configured spends each
    /// tick on one cheap card query and nothing else, and the read path reports
    /// <c>no_credentials</c> at Debug level rather than spamming warnings.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often parked gates are checked for GitHub-side review decisions.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Read CI results for a parked gate's head commit and report a failing build as the
    /// request-changes review it functionally is. On by default; switch off where CI is not the
    /// platform's to read (self-hosted runners behind a network policy the API pod cannot reach),
    /// because a poller that cannot reach CI is silent, and silent is what it was before.
    /// </summary>
    public bool CheckCi { get; set; } = true;

    /// <summary>
    /// How long after a check finished its failure may still be reported for a gate that parked
    /// afterwards. Floor of 5 minutes: the common case is CI going red while the persona is still
    /// writing its summary and the park landing a minute later, and a failure that predates the park
    /// by less than a couple of minutes is this park's business, not history.
    /// </summary>
    public int CiFailureLookbackMinutes { get; set; } = 45;

    /// <summary>Clamped <see cref="CiFailureLookbackMinutes"/>; see that property for the floor.</summary>
    public TimeSpan CiFailureLookback => TimeSpan.FromMinutes(Math.Max(5, CiFailureLookbackMinutes));

    /// <summary>Floor of 10s: two GETs per parked PR per tick should never become a hammer.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(10, IntervalSeconds));
}

/// <summary>
/// Delivers review decisions made on the GitHub PR page into the workflow parked on the review gate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is and what it is not.</strong> The gate releases on a
/// <see cref="PrReviewSignal"/>, and until now the only producers were the two review endpoints —
/// so a human who reviewed where reviewers actually work (github.com) was invisible and the job
/// stayed parked until the seven-day timeout. This service adds a third producer: it finds
/// <see cref="TimelineCardType.PrReview"/> cards the workflow parked in
/// <see cref="TimelineCardStatus.PendingInput"/>, reads the PR's reviews from GitHub, and when they
/// state a decision the gate should honour, sends the same signal class the REST path sends. The
/// workflow code, its two-man history records, the approved-SHA merge freeze and the cards' state
/// machine all stay exactly as they were; the poller cannot introduce a merge path the endpoints
/// did not already have, because it goes through the same router.
/// </para>
/// <para>
/// <strong>Why reviews bind to the current head, and why an approval on a moved head is silence
/// rather than a signal.</strong> A GitHub approval is recorded against a commit. If the author
/// pushed since, the approval does not cover what is on the branch now, and delivering it would make
/// the workflow merge code nobody reviewed — or, since the merge guard would reject the stale SHA
/// with <c>sha_moved</c>, re-park the gate and have the poller fire the same stale approval again
/// until the clock guard below starts suppressing it. Waiting for a fresh review of the current head
/// is both the honest reading of GitHub's own review semantics and the loop-free one.
/// </para>
/// <para>
/// <strong>The duplicate problem, and the two guards that answer it.</strong> Between delivering a
/// decision and the workflow moving the card off <c>PendingInput</c>, the next tick would otherwise
/// re-read the same reviews and re-fire. And after a changes-requested round whose remediation pushed
/// nothing, the same <c>CHANGES_REQUESTED</c> still matches the same head and would re-fire forever.
/// The first guard is an in-memory delivered-key per job (decision kind + review id + head), which
/// covers a live process; the second is durable: a review whose <c>submitted_at</c> predates the
/// card's current park (<c>UpdatedAt</c>) by more than the skew tolerance is one this park was
/// caused by, not one waiting to be seen. The park time comes from the card row the workflow just
/// wrote, so the guard survives an API restart — which is precisely when the in-memory key is gone.
/// A restart landing in the narrow window where the guard's tolerance and a same-head review could
/// double-fire costs one extra remediation round; the alternative (never re-firing) loses genuine
/// decisions, and losing those is the bug this service exists to fix.
/// </para>
/// <para>
/// <strong>Identity mapping is deliberately narrow.</strong> The REST path verifies a reviewer against
/// persona records; the poller sees only GitHub logins. What it can verify is the one comparison that
/// matters for the two-man rule — <em>login ≠ the account that opened this PR</em> — and it stamps
/// accepted reviewers as <c>github:&lt;login&gt;</c> so the workflow history says where the decision
/// actually came from. A reviewer whose GitHub account is the author persona under a different name
/// is not detectable from here, and is documented rather than papered over.
/// </para>
/// <para>
/// <strong>CI is the other thing a parked gate waits on, and it is read the same way.</strong> A build
/// that failed while the persona was still writing its summary used to sit on the PR page informing
/// nobody: the author had stopped working, the reviewer had not been asked yet, and the job waited for
/// a human to notice and type into a review box what GitHub had already said unambiguously. So the
/// poller reads the head commit's check runs and statuses too, and delivers a failure through the same
/// <see cref="PrReviewSignal"/> class with <c>IsApproved: false</c>, which makes the workflow take its
/// existing request-changes branch: review round incremented, one remediation task per failing check,
/// the author sent back to fix the build, and the gate re-parked on the new head. Nothing in the
/// workflow was forked or extended to accept a machine reviewer, which is the point — a reviewer that
/// is not a person still cannot invent a merge path.
/// <para>
/// <strong>The head commit is the epoch, and that is what stops the loop.</strong> A human reviewer
/// stops talking when they are satisfied; CI fails a commit forever. Delivering on "CI is red" would
/// therefore re-fire every minute for as long as the gate stays parked, and re-firing means a fresh
/// remediation round each time. So a failure is reported once per head commit per job, and the record
/// of what has been reported is deliberately <em>not</em> dropped when the gate briefly leaves
/// <c>PendingInput</c> during remediation — dropping it there is precisely what would turn one broken
/// build into a round every five minutes. A new report requires a new commit, which gives the loop the
/// same property a human review has: the author has to do something before the reviewer can say
/// anything. Reporting is also bounded in time (<see cref="PrReviewPollingOptions.CiFailureLookback"/>)
/// so a failure that predates the current park by hours is not resurrected by an operator merging
/// through it deliberately.
/// </para>
/// <para>
/// <strong>A green build approves nothing.</strong> CI passing is not the second pair of eyes the
/// merge gate exists to obtain, and delivering it as an approval would let a machine authorise its own
/// author's code and merge it. Green CI therefore produces no signal at all: it stops being an obstacle
/// and leaves the decision exactly where it was. Red CI only ever pushes the job backwards, which is
/// the one direction an automated reviewer is safe to move a job in.
/// </para>
/// <para>
/// <strong>A PR merged by hand counts.</strong> If a human merges the pull request directly on
/// GitHub, that is a strictly stronger act than approving it: the code they reviewed is in the base
/// branch. The poller delivers an approval for the merged head; the merge activity reads the PR,
/// sees <c>merged</c>, and returns its documented already-merged success, so the workflow completes
/// through its normal path instead of timing out over work that is genuinely landed. A PR merely
/// <em>closed</em> without merging gets left alone — there is no safe signal for it, and the seven-day
/// halt with the PR's own URL in the message is a better operator experience than inventing a
/// remediation round against a pull request its author just threw away.
/// </para>
/// <para>
/// <strong>Multi-instance safety.</strong> Each API replica runs this loop. A review two replicas
/// both fire delivers the signal twice, and the gate absorbs it: it clears <c>_prReview</c> on park
/// and on consume, so a duplicate arriving between gates is discarded rather than deferred into a
/// later round. The in-memory keys are per-process, which is fine for the same reason the guard in
/// the workflow already is: correctness does not depend on them, only on not double-firing.
/// </para>
/// </remarks>
public class PrReviewPollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<PrReviewPollingOptions> options,
    ILogger<PrReviewPollingService> logger) : BackgroundService
{
    /// <summary>
    /// Clock slack allowed between GitHub's <c>submitted_at</c> and this host's park timestamp before
    /// a review is considered to predate the park. Generous in one direction only: skew of tens of
    /// seconds is normal, and the tolerance is what keeps a genuine review made seconds after a park
    /// from being suppressed by a slow GitHub clock.
    /// </summary>
    internal static readonly TimeSpan ParkSkewTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Job id → the key of the last decision delivered for it. Cleared when the job stops showing a
    /// parked gate, so a later round's fresh review is deliverable again.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _deliveredKeys = new();

    /// <summary>
    /// Job id → the head commit whose CI failure was last reported. Kept when a gate momentarily stops
    /// showing as parked (that is what remediation looks like from out here) and dropped only when the
    /// job reaches a terminal status, because this map is what makes "the author has to push before CI
    /// can complain again" true. See the class remarks for why a CI failure cannot use the same
    /// lifecycle as <see cref="_deliveredKeys"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _ciReportedHeads = new();

    /// <summary>
    /// Job id → the last verdict key whose non-delivery we logged. Delivery stays retryable (a
    /// workflow that was mid-restart when the review landed must still receive it), but a job with
    /// permanently no workflow would otherwise log the same Information line once a minute forever.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _loggedUndeliverableKeys = new();

    /// <summary>
    /// Job id → the credential-skip reason already reported for it. A gate this poller cannot read
    /// GitHub for is deaf, and deaf is exactly what an operator debugging a merged-but-unfinished
    /// job needs to be told — once, at Information, rather than once a minute or never.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _loggedPollSkipReasons = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("PR review polling disabled (PrReviewPolling:Enabled=false)");
            return;
        }

        var interval = options.Value.Interval;
        logger.LogInformation("PR review poller started (sweep every {Interval}s)", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A tick that throws (database blip, malformed card) must not kill the loop; a dead
                // poller silently recreates exactly the invisible-review bug this service fixes.
                logger.LogError(ex, "PR review poll tick failed; retrying next tick");
            }
        }

        logger.LogInformation("PR review poller stopped");
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
        var github = scope.ServiceProvider.GetRequiredService<IGitHubPullRequestService>();
        var router = scope.ServiceProvider.GetRequiredService<IWorkflowSignalRouter>();
        // Optional by contract: API hosts that register the persona credential store decrypt author
        // PATs there; without it the poller still runs wherever GitHub:Token is configured.
        var encryption = scope.ServiceProvider.GetService<ICredentialEncryptionService>();

        // Newest first; the walk below keeps only the first card per job, which is the gate the
        // workflow most recently parked.
        var cards = await db.JobTimelineCards
            .AsNoTracking()
            .Where(c => c.CardType == TimelineCardType.PrReview &&
                        c.Status == TimelineCardStatus.PendingInput)
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        // `open_pull_request` writes a PendingInput ledger card beside the workflow's own gate card,
        // and only the gate card moves when the workflow merges — so a merged job can leave a stale
        // pending card behind. A job whose row is Completed/Archived is definitionally not waiting on
        // a review, and polling its leftover card would mean re-reading GitHub every tick and
        // reporting NoRunningWorkflow for a job that finished days ago.
        var candidateJobIds = cards
            .Where(c => Guid.TryParse(c.JobId, out _))
            .Select(c => Guid.Parse(c.JobId))
            .Distinct()
            .ToList();

        var terminalJobs = new HashSet<Guid>(await db.ProjectJobs
            .AsNoTracking()
            .Where(j => candidateJobIds.Contains(j.Id) &&
                        (j.Status == ProjectJobStatus.Completed || j.Status == ProjectJobStatus.Archived))
            .Select(j => j.Id)
            .ToListAsync(ct));

        // A job the workflow never moved off the gate because it was completed or archived out from
        // under a parked gate leaves its card saying "Awaiting Review" forever: the terminal skip
        // below is correct for signal delivery but wrong for card lifecycle, and the stale card is
        // exactly the kind of badge that outlives the truth it was written from (the merged-PR job
        // this service finally released still showed its gate pending days later).
        await ResolveTerminalGateCardsAsync(db, terminalJobs, logger, ct);

        var parkedJobs = new HashSet<Guid>();

        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();

            if (!Guid.TryParse(card.JobId, out var jobId) || !parkedJobs.Add(jobId) ||
                terminalJobs.Contains(jobId))
            {
                continue;
            }

            if (!TryReadGatePayload(card.StructuredPayloadJson, out var prUrl, out var prNumber, out var authorPersona))
            {
                // The gate can park before a PR is registered (the workflow retries opening it); a
                // card with no pull request in it simply has nothing on GitHub to have been reviewed.
                continue;
            }

            var prRef = !string.IsNullOrWhiteSpace(prUrl)
                ? prUrl!
                : prNumber!.Value.ToString(CultureInfo.InvariantCulture);

            // Read as the PR's own author persona: in persona-credential deployments the control
            // plane has no GitHub token, but the account that opened the PR can always see its own
            // reviews. Configured credentials remain the fallback when the persona carries none.
            var authorToken = await ResolvePersonaGithubTokenAsync(db, encryption, authorPersona, logger, ct);

            var snapshot = await github.ReadPullRequestReviewStateAsync(prRef, tokenOverride: authorToken, ct: ct);

            if (!snapshot.Success)
            {
                if (string.Equals(snapshot.FailureReason, "no_credentials", StringComparison.Ordinal))
                {
                    // Was a Debug line, which meant the most common way a parked gate goes deaf — the
                    // author persona carries no GitHub token and the deployment configures none — was
                    // invisible precisely when an operator was trying to work out why a merged PR had
                    // not completed the job. Reported once per job now: the every-minute re-fire is
                    // what dedupe is for, not a downgrade to silence.
                    if (_loggedPollSkipReasons.TryAdd(jobId, "no_credentials"))
                    {
                        logger.LogInformation(
                            "PR review poll for job {JobId} cannot read GitHub ({Pr}): no credentials " +
                            "resolve for this gate (author persona '{Author}', no configured token). " +
                            "GitHub-side decisions will not reach the workflow until a token is " +
                            "attached to the persona or GitHub:Token is configured.",
                            jobId, prRef, authorPersona ?? "unknown");
                    }
                }
                else
                {
                    logger.LogWarning(
                        "Could not read review state for job {JobId} ({Pr}): {Message} ({Reason})",
                        jobId, prRef, snapshot.Message, snapshot.FailureReason);
                }

                continue;
            }

            var verdict = DecideVerdict(jobId, snapshot, card.UpdatedAt, logger);

            // CI is asked only when nothing a person said has already decided this gate. Not out of
            // politeness: a reviewer that fires first can consume the park, and a human's
            // request-changes carries detail no build log has.
            if (verdict is null &&
                options.Value.CheckCi &&
                string.Equals(snapshot.PrState, "open", StringComparison.OrdinalIgnoreCase))
            {
                verdict = await ReadCiVerdictAsync(
                    jobId, github, prRef, snapshot.HeadSha, authorToken,
                    card.UpdatedAt, options.Value, ct);
            }

            if (verdict is null)
            {
                continue;
            }

            if (verdict.CiHeadSha is { } verdictHead)
            {
                // One report per commit. Deliberately not the same map as review keys: those are
                // dropped the moment a gate stops being parked, and a CI failure delivered from a
                // dropped key would start a fresh remediation round every few minutes against a build
                // that is merely still broken.
                if (string.Equals(_ciReportedHeads.GetValueOrDefault(jobId), verdictHead,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            else if (_deliveredKeys.TryGetValue(jobId, out var already) && already == verdict.Key)
            {
                continue;
            }

            var signal = new PrReviewSignal(
                ReviewerId: verdict.ReviewerId,
                IsApproved: verdict.IsApproved,
                ReviewComments: verdict.Comments,
                Timestamp: verdict.At ?? DateTimeOffset.UtcNow,
                PrNumber: prNumber,
                ApprovedHeadSha: verdict.ApprovedHeadSha);

            var delivery = await router.SendPrReviewAsync(jobId, signal, ct);

            switch (delivery.Status)
            {
                case SignalDeliveryStatus.Delivered:
                    // Recorded only on success: a Rejected or NoRunningWorkflow tick must stay
                    // retryable, or a workflow that was mid-restart when the review landed never
                    // hears about it and the gate sits out its seven days.
                    _deliveredKeys[jobId] = verdict.Key;

                    if (verdict.CiHeadSha is { } reportedHead)
                    {
                        _ciReportedHeads[jobId] = reportedHead;
                        logger.LogInformation(
                            "Delivered CI failure for job {JobId} on {Head} (PR {Pr}) into workflow " +
                            "{WorkflowId}; the gate is taking its request-changes round: {Checks}",
                            jobId, reportedHead, prRef, delivery.WorkflowId, verdict.Comments);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Delivered {Decision} review for job {JobId} from {Reviewer} " +
                            "(PR {Pr}, review {ReviewId}) into workflow {WorkflowId}",
                            verdict.IsApproved ? "approving" : "changes-requested",
                            jobId, verdict.ReviewerId, prRef, verdict.ReviewId, delivery.WorkflowId);
                    }
                    break;

                case SignalDeliveryStatus.NoRunningWorkflow:
                    logger.LogInformation(
                        "Job {JobId} has a review to deliver ({Pr}: {Decision}) but no running " +
                        "workflow gate — likely a non-Temporal job; the review endpoints still apply. " +
                        "Detail: {Detail}",
                        jobId, prRef, verdict.IsApproved ? "approved" : "changes requested",
                        delivery.Detail);
                    break;

                default:
                    logger.LogWarning(
                        "Review signal for job {JobId} was not delivered: {Status} {Detail}",
                        jobId, delivery.Status, delivery.Detail);
                    break;
            }
        }

        // Forget jobs whose gate has moved on (merged, rejected into a new round, or resolved) so the
        // dictionary does not grow with finished jobs and a genuinely new round can deliver again.
        foreach (var jobId in _deliveredKeys.Keys)
        {
            if (!parkedJobs.Contains(jobId))
            {
                _deliveredKeys.TryRemove(jobId, out _);
            }
        }

        foreach (var jobId in _loggedPollSkipReasons.Keys)
        {
            // Same lifecycle as _deliveredKeys: a gate that stopped being parked (resolved, terminal,
            // or credentials appeared and reads now succeed) can report a fresh skip reason later.
            if (!parkedJobs.Contains(jobId))
            {
                _loggedPollSkipReasons.TryRemove(jobId, out _);
            }
        }

        foreach (var jobId in _ciReportedHeads.Keys)
        {
            // Terminal only, on purpose — see _ciReportedHeads. A job that finished can never park a
            // gate again, so its entry has nothing left to suppress; an unfinished one still has.
            if (terminalJobs.Contains(jobId))
            {
                _ciReportedHeads.TryRemove(jobId, out _);
            }
        }
    }

    /// <summary>
    /// Closes PR-review gate cards a terminal job left <see cref="TimelineCardStatus.PendingInput"/>.
    /// </summary>
    /// <remarks>
    /// The workflow moves its gate card when it merges or re-parks; a job completed or archived out
    /// from under a parked gate — the <c>/complete</c> path, or an archive that never touched the
    /// ledger — leaves the card pending forever. This service then skips the job as terminal every
    /// tick without ever resolving the card, and the attention queue keeps advertising a review
    /// nobody is waiting for. A job whose row says Completed/Archived is definitionally not awaiting
    /// review, which is the same reading the terminal skip already applies to delivery; this applies
    /// it to lifecycle. Idempotent by query: resolved cards stop matching, so the write happens once.
    /// </remarks>
    /// <returns>The number of cards closed, for logging and tests.</returns>
    internal static async Task<int> ResolveTerminalGateCardsAsync(
        AgentMeshDbContext db,
        HashSet<Guid> terminalJobIds,
        ILogger logger,
        CancellationToken ct)
    {
        if (terminalJobIds.Count == 0)
        {
            return 0;
        }

        var terminalIdStrings = terminalJobIds.Select(id => id.ToString()).ToList();
        var stale = await db.JobTimelineCards
            .Where(c => c.CardType == TimelineCardType.PrReview &&
                        c.Status == TimelineCardStatus.PendingInput &&
                        terminalIdStrings.Contains(c.JobId))
            .ToListAsync(ct);

        foreach (var card in stale)
        {
            card.Status = TimelineCardStatus.Resolved;
            card.UpdatedAt = DateTimeOffset.UtcNow;
            card.StructuredPayloadJson = WithPayloadNote(card.StructuredPayloadJson,
                "Job reached a terminal status with this gate still pending; the review poller closed " +
                "the card because a completed or archived job is not waiting on a review.");
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Closed {Count} PR-review gate card(s) left PendingInput on terminal jobs: {Jobs}",
                stale.Count, string.Join(", ", stale.Select(c => c.JobId).Distinct()));
        }

        return stale.Count;
    }

    /// <summary>
    /// Sets <c>note</c> on a gate card's structured payload, preserving every other field.
    /// </summary>
    /// <remarks>
    /// Best-effort on purpose: a card whose payload will not parse gets its status moved and its
    /// payload left exactly as found. A malformed jsonb blob must not throw a tick down and take
    /// every other parked gate's poll — and therefore every other job's review delivery — with it.
    /// </remarks>
    internal static string? WithPayloadNote(string? payloadJson, string note)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return payloadJson;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return payloadJson;
            }

            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                fields[prop.Name] = prop.Value.Clone();
            }

            fields["note"] = note;
            return JsonSerializer.Serialize(fields);
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }

    /// <summary>
    /// Turns a review snapshot into the decision to deliver, or null for "say nothing yet".
    /// </summary>
    /// <remarks>
    /// The rules, in order of consequence:
    /// <list type="number">
    /// <item>Latest review per login wins, across every state. A login whose latest is
    /// <c>DISMISSED</c> or <c>COMMENTED</c> holds no verdict — that is GitHub's own reading (a
    /// dismissed approval does not unblock a PR), and it is what stops a push that dismissed an old
    /// approval from being delivered as continued consent.</item>
    /// <item>The PR author's login never counts. GitHub already refuses an author's *approval* of
    /// their own PR, but it does accept their <c>CHANGES_REQUESTED</c>, and an approval arriving
    /// through an account that renamed or transferred in later should not slip past the two-man rule
    /// either.</item>
    /// <item>Any latest-state <c>CHANGES_REQUESTED</c> wins outright over approvals. A PR with one
    /// approval and one request-changes is, on GitHub and in this loop, not ready.</item>
    /// <item>Approvals must match the current head SHA — an approval of a superseded commit is
    /// silence (see the class remarks).</item>
    /// <item>A review older than the current park by more than <see cref="ParkSkewTolerance"/> is
    /// one this park already acted on, so it is not delivered again. This is the durable half of
    /// duplicate suppression. An undated review passes the guard rather than being suppressed: a
    /// timestamp GitHub could not give must not silently discard a real decision, and the
    /// in-memory key plus head-binding carry the duplicate load for that (vanishingly rare) case.</item>
    /// </list>
    /// </remarks>
    internal static PrGateVerdict? EvaluateReviews(
        IReadOnlyList<GitHubPullReview> reviews,
        string? currentHeadSha,
        string? authorLogin,
        DateTimeOffset parkedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(currentHeadSha))
        {
            // No head to bind a decision to: an approval we cannot freeze to a commit is the exact
            // thing the merge guard exists to refuse.
            return null;
        }

        var latestByLogin = new Dictionary<string, GitHubPullReview>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in reviews)
        {
            if (string.IsNullOrWhiteSpace(review.Login)) continue;
            if (!latestByLogin.TryGetValue(review.Login, out var held) ||
                NewnessKey(review).CompareTo(NewnessKey(held)) > 0)
            {
                latestByLogin[review.Login] = review;
            }
        }

        var decisions = latestByLogin.Values
            .Where(r => !string.Equals(r.Login, authorLogin, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.State.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) ||
                        r.State.Equals("CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase))
            .Where(r => r.SubmittedAt is null || r.SubmittedAt > parkedAtUtc - ParkSkewTolerance)
            .ToList();

        var requested = decisions
            .Where(r => r.State.Equals("CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(NewnessKey)
            .FirstOrDefault();

        if (requested is not null)
        {
            // Changes requested are not bound to the head: the point is the feedback, and whether the
            // author pushed since the reviewer wrote it does not make the comments less true. The
            // park-time guard above already stopped the re-fire case.
            return new PrGateVerdict(
                IsApproved: false,
                ReviewerId: ReviewerTag(requested.Login),
                Comments: string.IsNullOrWhiteSpace(requested.Body) ? null : requested.Body!.Trim(),
                At: requested.SubmittedAt,
                ReviewId: requested.Id,
                ApprovedHeadSha: null,
                Key: $"changes:{requested.Id}");
        }

        var approval = decisions
            .Where(r => r.State.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(r.CommitSha) &&
                        string.Equals(r.CommitSha!.Trim(), currentHeadSha.Trim(),
                            StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(NewnessKey)
            .FirstOrDefault();

        if (approval is null)
        {
            return null;
        }

        return new PrGateVerdict(
            IsApproved: true,
            ReviewerId: ReviewerTag(approval.Login),
            Comments: string.IsNullOrWhiteSpace(approval.Body) ? null : approval.Body!.Trim(),
            At: approval.SubmittedAt,
            ReviewId: approval.Id,
            // The head as read live from GitHub, not the review's commit: they matched above, and
            // sending the live value keeps the merge guard's comparison identical to a REST
            // reviewer's (who approves "whatever is head right now").
            ApprovedHeadSha: currentHeadSha.Trim(),
            Key: $"approved:{approval.Id}:{currentHeadSha.Trim().ToLowerInvariant()}");
    }

    /// <summary>
    /// A manually merged PR is consent beyond approval: the reviewer put the code in the base branch
    /// themselves. Delivering it as an approval makes the merge activity take its already-merged
    /// success path and the job complete normally.
    /// </summary>
    internal static PrGateVerdict? EvaluateMergedPullRequest(
        GitHubPrReviewSnapshot snapshot,
        DateTimeOffset parkedAtUtc)
    {
        if (snapshot.MergedAt is not { } mergedAt || mergedAt <= parkedAtUtc - ParkSkewTolerance)
        {
            // Either no merge timestamp to date the event, or the merge predates the current park —
            // a park caused by something else (sha_moved after this very merge attempt, say) must not
            // be re-released by the same merged state it already reacted to.
            return null;
        }

        var reviewer = string.IsNullOrWhiteSpace(snapshot.MergedByLogin)
            ? "github:merge"
            : ReviewerTag(snapshot.MergedByLogin);

        return new PrGateVerdict(
            IsApproved: true,
            ReviewerId: reviewer,
            Comments: $"Pull request was merged directly on GitHub{(snapshot.MergeCommitSha is null ? "." : $" as {snapshot.MergeCommitSha}.")}",
            At: mergedAt,
            ReviewId: 0,
            ApprovedHeadSha: snapshot.HeadSha,
            Key: $"merged:{snapshot.MergeCommitSha ?? snapshot.HeadSha}");
    }

    /// <summary>
    /// Composes the verdicts: an open PR is judged by its reviews, a merged one by the merge, and
    /// anything else (a closed-without-merge PR) is deliberately left alone. Also where the read's
    /// failure reasons get logged once per tick with the job context attached.
    /// </summary>
    private static PrGateVerdict? DecideVerdict(
        Guid jobId,
        GitHubPrReviewSnapshot snapshot,
        DateTimeOffset parkedAtUtc,
        ILogger log)
    {
        var state = snapshot.PrState?.ToLowerInvariant();

        return state switch
        {
            "open" => EvaluateReviews(
                snapshot.Reviews, snapshot.HeadSha, snapshot.AuthorLogin, parkedAtUtc),
            "merged" => EvaluateMergedPullRequest(snapshot, parkedAtUtc),
            "closed" => SuppressClosed(log, jobId),
            _ => null,
        };
    }

    private static PrGateVerdict? SuppressClosed(ILogger log, Guid jobId)
    {
        // Debug rather than warning every minute: closing a job PR without merging is usually the
        // operator deciding the job is dead, and the workflow's own halt message at the timeout
        // carries the PR URL for whoever is left holding it.
        log.LogDebug(
            "Job {JobId}'s gate PR was closed on GitHub without merging; leaving the workflow parked " +
            "until its review timeout.", jobId);
        return null;
    }

    /// <summary>
    /// The reviewer identity a CI failure is delivered under. Namespaced like a person's
    /// <c>github:&lt;login&gt;</c> tag so the timeline card and the remediation prompt say where the
    /// request came from, and so an operator can tell a machine's round from a human's at a glance.
    /// </summary>
    internal const string CiReviewerId = "github:ci";

    /// <summary>
    /// Reads the head commit's CI and reports it if it is red, or null for "say nothing".
    /// </summary>
    /// <remarks>
    /// Reads with the same persona credential the reviews were read with — the author of the PR can see
    /// its checks — and reuses the head SHA the review read already returned rather than fetching the
    /// pull request a second time, so a tick costs the same three GETs per parked gate it did before.
    /// A CI read that fails never escalates to the reviews' verdict: an unreachable check API is not
    /// evidence that a build broke, and halting a healthy job over a network blip is a worse bug than
    /// waiting one more tick.
    /// </remarks>
    private async Task<PrGateVerdict?> ReadCiVerdictAsync(
        Guid jobId,
        IGitHubPullRequestService github,
        string prRef,
        string? headSha,
        string? authorToken,
        DateTimeOffset parkedAtUtc,
        PrReviewPollingOptions pollingOptions,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(headSha))
        {
            // No head to read a build result for. An empty pull request has no CI verdict, and
            // inventing one is how a job gets sent into a remediation round for a commit that does
            // not exist.
            return null;
        }

        var ci = await github.ReadPullRequestCiStateAsync(
            prRef,
            tokenOverride: authorToken,
            headShaOverride: headSha.Trim(),
            ct: ct);

        if (!ci.Success)
        {
            if (string.Equals(ci.FailureReason, "no_credentials", StringComparison.Ordinal) ||
                string.Equals(ci.FailureReason, "not_supported", StringComparison.Ordinal))
            {
                logger.LogDebug("CI poll for job {JobId} skipped: {Message}", jobId, ci.Message);
            }
            else
            {
                logger.LogWarning(
                    "Could not read CI state for job {JobId} ({Pr}): {Message} ({Reason})",
                    jobId, prRef, ci.Message, ci.FailureReason);
            }

            return null;
        }

        var verdict = EvaluateCiFailures(
            ci, prRef, parkedAtUtc, DateTimeOffset.UtcNow,
            pollingOptions.CiFailureLookback, ParkSkewTolerance);

        if (verdict is null && CiIsRed(ci))
        {
            logger.LogDebug(
                "Job {JobId}'s pull request {Pr} reports failing checks on {Head} that predate this " +
                "park and outlive the CI lookback; leaving the gate to its reviewers.",
                jobId, prRef, Short(headSha));
        }

        return verdict;
    }

    /// <summary>
    /// Turns a CI snapshot into the verdict to deliver, or null for "say nothing yet".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, and what each one is protecting:
    /// <list type="number">
    /// <item><strong>Only an explicit <c>failure</c> counts.</strong> A check run's other
    /// non-green endings — <c>cancelled</c>, <c>timed_out</c>, <c>action_required</c>, <c>stale</c>,
    /// <c>neutral</c>, <c>skipped</c> — say more about the runner, or about a human who stopped the
    /// run, than about the code, and acting on them sends a persona to rewrite files that are
    /// probably fine. The same reasoning excludes a commit status of <c>error</c>, which in the
    /// integrations that still post one usually means the publisher itself broke.</item>
    /// <item><strong>Nothing that is still running is a failure.</strong> Checks are almost always
    /// mid-flight when a gate parks, and firing on <c>queued</c> would report a red build for a
    /// commit nobody has finished testing. Only a finished check's conclusion is read, which is also
    /// what makes a long-running suite harmless.</item>
    /// <item><strong>The failure must belong to this park.</strong> Either it completed after the
    /// gate parked (modulo the review skew tolerance, which this shares so both halves of the poller
    /// agree on what GitHub's clock is worth), or it completed inside
    /// <see cref="PrReviewPollingOptions.CiFailureLookback"/> — the common case of CI going red while
    /// the persona was still summarising its own work. Anything older is a failure a human has been
    /// looking at for hours, possibly one they chose to merge through, and an operator's own decision
    /// is not this poller's to overrule.</item>
    /// <item><strong>Green says nothing at all.</strong> Returning no verdict for a passing build is
    /// what keeps CI out of the approving business; see the class remarks.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Failures are reported newest-first and undated ones last: a persona reading "the build is red"
    /// benefits from seeing the check that most recently spoke first, and one GitHub could not date is
    /// still a failure and must not be dropped for want of a timestamp.
    /// </para>
    /// </remarks>
    internal static PrGateVerdict? EvaluateCiFailures(
        GitHubCiSnapshot ci,
        string? prUrl,
        DateTimeOffset parkedAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan lookback,
        TimeSpan skew)
    {
        if (!ci.Success || string.IsNullOrWhiteSpace(ci.HeadSha))
        {
            return null;
        }

        var failures = FindCiFailures(ci.Checks);
        if (failures.Count == 0)
        {
            return null;
        }

        var reportable = failures
            .Where(f => f.CompletedAt is null ||
                        f.CompletedAt > parkedAtUtc - skew ||
                        f.CompletedAt > nowUtc - lookback)
            .OrderByDescending(NewnessKey)
            .ToList();

        if (reportable.Count == 0)
        {
            return null;
        }

        var head = ci.HeadSha!.Trim().ToLowerInvariant();
        var names = reportable
            .Select(CheckLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PrGateVerdict(
            IsApproved: false,
            ReviewerId: CiReviewerId,
            Comments: BuildCiFailureNotes(head, prUrl, reportable, ci.Checks),
            At: reportable
                .Select(f => f.CompletedAt)
                .FirstOrDefault(t => t is not null)
                .GetValueOrDefault(nowUtc),
            // No GitHub review id exists for a build result; 0 is the honest absence rather than a
            // borrowed number that would collide with a real review's key.
            ReviewId: 0,
            ApprovedHeadSha: null,
            Key: $"ci:{head}:{string.Join(",", names.OrderBy(n => n, StringComparer.Ordinal))}",
            CiHeadSha: head);
    }

    /// <summary>Whether a snapshot reports anything worth a remediation round.</summary>
    /// <remarks>
    /// Ignores the freshness rules on purpose: the caller uses this to tell "the build is red and this
    /// poller chose not to say so again" from "the build is green", which are different things to log
    /// and different things for an operator troubleshooting a parked gate to be told.
    /// </remarks>
    internal static bool CiIsRed(GitHubCiSnapshot ci) => FindCiFailures(ci.Checks).Count > 0;

    /// <summary>
    /// The checks on this list that reported a code failure, in the order GitHub gave them.
    /// </summary>
    private static List<GitHubCiCheck> FindCiFailures(IReadOnlyList<GitHubCiCheck> checks) =>
        checks
            .Where(c => string.Equals(c.State, "failure", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.Conclusion, "failure", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static DateTimeOffset NewnessKey(GitHubCiCheck check) => check.CompletedAt ?? DateTimeOffset.MinValue;

    private static string CheckLabel(GitHubCiCheck check) =>
        string.IsNullOrWhiteSpace(check.Name)
            ? (check.Kind == GitHubCiCheckKind.CommitStatus ? "a commit status" : "an unnamed check run")
            : check.Name!.Trim();

    /// <summary>How many failing checks are itemised before the rest are summarised.</summary>
    private const int CiFailuresListed = 15;

    /// <summary>
    /// The feedback text a CI failure delivers as a review's comments.
    /// </summary>
    /// <remarks>
    /// Shaped for the reader that will actually get it. <c>BuildReviewRemediationTasks</c> turns each
    /// bullet into one checklist item and ignores prose, so every failing check gets its own task, while
    /// the framing lines that tell the persona what to do when it is done land in the round prompt
    /// without becoming tasks of their own. The trailing instruction matters: with "the gate re-parks on
    /// the new commit" the author knows the loop it is in, and a fix that is committed but never pushed
    /// would leave everyone waiting on a build of code that no longer exists on GitHub.
    /// <para>
    /// The log URLs are kept off the bullets on purpose, and this is the shape the first production round
    /// proved necessary. That activity reads a file path out of each bullet with a "first thing containing
    /// a dot" pattern, and a GitHub Actions log URL always has one, so a bullet carrying its URL produced
    /// a task whose target file was <c>//github.com</c> — an author told to edit a hostname. Names alone
    /// resolve to <c>workspace</c>, which is the honest answer for a build failure, and resolve to the
    /// check's own path when a suite is named after one, which is the better answer.
    /// </para>
    /// <para>
    /// The URLs therefore go in a block quoted with <c>&gt;</c>. A quote was chosen over indentation or a
    /// plain line because the task builder trims each line before testing it for a list marker, so
    /// indentation protects nothing, and a check named <c>1. Build</c> would turn its own log line into a
    /// second task. The quote marker is not one the builder recognises, so no check name can promote a
    /// log line into work.
    /// </para>
    /// </remarks>
    internal static string BuildCiFailureNotes(
        string headSha,
        string? prUrl,
        IReadOnlyList<GitHubCiCheck> failures,
        IReadOnlyList<GitHubCiCheck> allChecks)
    {
        var listed = failures.Take(CiFailuresListed).ToList();
        var lines = new List<string>(listed.Count + 4);

        var where = string.IsNullOrWhiteSpace(prUrl) ? "the job pull request" : prUrl;
        lines.Add($"CI is failing on the current head ({Short(headSha)}) of {where}:");
        lines.Add(string.Empty);

        foreach (var failure in listed)
        {
            lines.Add($"- {CheckLabel(failure)} — failure");
        }

        var logLines = listed
            .Where(f => !string.IsNullOrWhiteSpace(f.HtmlUrl))
            .Select(f => $"> {CheckLabel(f)} — {f.HtmlUrl!.Trim()}")
            .ToList();

        if (logLines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Logs for the checks above:");
            lines.AddRange(logLines);
        }

        if (failures.Count > listed.Count)
        {
            var rest = failures.Skip(CiFailuresListed).Select(CheckLabel);
            lines.Add(string.Empty);
            lines.Add($"Also failing, not listed individually: {string.Join(", ", rest)}.");
        }

        var running = allChecks
            .Where(c => !string.Equals(c.State, "failure", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(c.Conclusion, "failure", StringComparison.OrdinalIgnoreCase) &&
                        !IsSettled(c))
            .Select(CheckLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(CiFailuresListed)
            .ToList();

        if (running.Count > 0)
        {
            // Named rather than left out, so a persona fixing one red check does not conclude the whole
            // suite is finished and re-request review on a commit that has not been tested yet.
            lines.Add(string.Empty);
            lines.Add($"Still running on this commit: {string.Join(", ", running)}.");
        }

        lines.Add(string.Empty);
        lines.Add("Fix the failing checks, run the same verification locally, commit to the job branch " +
                  "and push — the review gate re-parks on the new commit once CI has re-run.");

        return string.Join("\n", lines);
    }

    /// <summary>Whether a check has finished, by either API's vocabulary.</summary>
    private static bool IsSettled(GitHubCiCheck check) =>
        string.Equals(check.Status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(check.Status, "success", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(check.State, "success", StringComparison.OrdinalIgnoreCase);

    private static string Short(string value) => value.Length > 8 ? value[..8] : value;

    private static string ReviewerTag(string? login) =>
        "github:" + (string.IsNullOrWhiteSpace(login) ? "unknown" : login.Trim().TrimStart('@').ToLowerInvariant());

    /// <summary>
    /// Sort key for "which review is this login's latest word": timestamp first, GitHub's monotonic
    /// review id as tie-breaker, and an undated review always older than any dated one — a review
    /// GitHub could not timestamp is not the fresher evidence of intent.
    /// </summary>
    private static (DateTimeOffset At, long Id) NewnessKey(GitHubPullReview review) =>
        (review.SubmittedAt ?? DateTimeOffset.MinValue, review.Id);

    /// <summary>
    /// Reads <c>prUrl</c>/<c>prNumber</c>/<c>authorPersonaId</c> out of a gate card's structured
    /// payload. The payload is written by <c>UpsertPrReviewCardAsync</c> in the workflow (and by the
    /// review tool for non-workflow jobs, which uses sibling author keys); unknown shapes yield false,
    /// which the caller treats as "nothing to poll", never as "no reviews". The author is the persona
    /// whose stored PAT is used to read the reviews, so the same key precedence the review endpoints
    /// use to enforce the two-man rule applies here.
    /// </summary>
    internal static bool TryReadGatePayload(
        string? payloadJson,
        out string? prUrl,
        out int? prNumber,
        out string? authorPersona)
    {
        prUrl = null;
        prNumber = null;
        authorPersona = null;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (doc.RootElement.TryGetProperty("prUrl", out var url) &&
                url.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(url.GetString()))
            {
                prUrl = url.GetString()!.Trim();
            }

            if (doc.RootElement.TryGetProperty("prNumber", out var number) &&
                number.ValueKind == JsonValueKind.Number &&
                number.TryGetInt32(out var parsed) && parsed > 0)
            {
                prNumber = parsed;
            }

            foreach (var authorKey in new[] { "authorPersonaId", "senderPersona", "senderId", "author" })
            {
                if (doc.RootElement.TryGetProperty(authorKey, out var prop) &&
                    prop.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(prop.GetString()))
                {
                    authorPersona = prop.GetString()!.Trim().TrimStart('@').ToLowerInvariant();
                    break;
                }
            }

            return prUrl is not null || prNumber is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The persona's decrypted GitHub PAT, or null. Never logs the token; a persona id that is not in
    /// the store or carries no token is an ordinary fallthrough (configured credentials back it), not
    /// an error — the poller runs against jobs whose authors may predate per-persona credentials.
    /// </summary>
    internal static async Task<string?> ResolvePersonaGithubTokenAsync(
        AgentMeshDbContext db,
        ICredentialEncryptionService? encryption,
        string? authorPersona,
        ILogger logger,
        CancellationToken ct)
    {
        if (encryption is null || string.IsNullOrWhiteSpace(authorPersona)) return null;

        try
        {
            var normalized = authorPersona.Trim().TrimStart('@').ToLowerInvariant();
            var encrypted = await db.Personas.AsNoTracking()
                .Where(p => p.Id == normalized)
                .Select(p => p.GitHubTokenEncrypted)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(encrypted)) return null;

            var decrypted = encryption.Decrypt(encrypted);
            return string.IsNullOrWhiteSpace(decrypted) ? null : decrypted.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same posture as the worker's lookup: a credential-store problem degrades to the
            // configured-credential path, and the read's own failure reason reports the outcome.
            logger.LogWarning(ex,
                "Could not resolve a GitHub token for persona '{Persona}'; trying configured credentials.",
                authorPersona);
            return null;
        }
    }
}

/// <summary>
/// A review decision the poller is ready to deliver, already dressed as the gate expects.
/// </summary>
/// <param name="Key">
/// Identity of this decision for duplicate suppression: decision kind, review id, and — for
/// approvals — the head it binds to, because an approval of a superseded commit must become
/// deliverable again only when it is re-issued, not when the poller re-reads it.
/// </param>
/// <param name="CiHeadSha">
/// Set only by a CI verdict: the commit the failure was read from, which is the epoch duplicate
/// suppression is keyed on for a reviewer that would otherwise repeat itself forever. Null for every
/// verdict that came from a person.
/// </param>
public sealed record PrGateVerdict(
    bool IsApproved,
    string ReviewerId,
    string? Comments,
    DateTimeOffset? At,
    long ReviewId,
    string? ApprovedHeadSha,
    string Key,
    string? CiHeadSha = null);
