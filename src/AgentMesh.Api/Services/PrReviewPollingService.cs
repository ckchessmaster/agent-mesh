using System.Text.Json;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
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
    /// Job id → the last verdict key whose non-delivery we logged. Delivery stays retryable (a
    /// workflow that was mid-restart when the review landed must still receive it), but a job with
    /// permanently no workflow would otherwise log the same Information line once a minute forever.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _loggedUndeliverableKeys = new();

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

        var parkedJobs = new HashSet<Guid>();

        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();

            if (!Guid.TryParse(card.JobId, out var jobId) || !parkedJobs.Add(jobId) ||
                terminalJobs.Contains(jobId))
            {
                continue;
            }

            if (!TryReadGatePayload(card.StructuredPayloadJson, out var prUrl, out var prNumber))
            {
                // The gate can park before a PR is registered (the workflow retries opening it); a
                // card with no pull request in it simply has nothing on GitHub to have been reviewed.
                continue;
            }

            var prRef = !string.IsNullOrWhiteSpace(prUrl)
                ? prUrl!
                : prNumber!.Value.ToString(CultureInfo.InvariantCulture);

            var snapshot = await github.ReadPullRequestReviewStateAsync(prRef, ct: ct);

            if (!snapshot.Success)
            {
                if (string.Equals(snapshot.FailureReason, "no_credentials", StringComparison.Ordinal))
                {
                    logger.LogDebug(
                        "PR review poll for job {JobId} skipped: {Message}", jobId, snapshot.Message);
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
            if (verdict is null)
            {
                continue;
            }

            if (_deliveredKeys.TryGetValue(jobId, out var already) && already == verdict.Key)
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
                    logger.LogInformation(
                        "Delivered {Decision} review for job {JobId} from {Reviewer} " +
                        "(PR {Pr}, review {ReviewId}) into workflow {WorkflowId}",
                        verdict.IsApproved ? "approving" : "changes-requested",
                        jobId, verdict.ReviewerId, prRef, verdict.ReviewId, delivery.WorkflowId);
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
    /// Reads <c>prUrl</c>/<c>prNumber</c> out of a gate card's structured payload. The payload is
    /// written by <c>UpsertPrReviewCardAsync</c> in the workflow; unknown shapes yield false, which
    /// the caller treats as "nothing to poll", never as "no reviews".
    /// </summary>
    internal static bool TryReadGatePayload(string? payloadJson, out string? prUrl, out int? prNumber)
    {
        prUrl = null;
        prNumber = null;
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

            return prUrl is not null || prNumber is not null;
        }
        catch (JsonException)
        {
            return false;
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
public sealed record PrGateVerdict(
    bool IsApproved,
    string ReviewerId,
    string? Comments,
    DateTimeOffset? At,
    long ReviewId,
    string? ApprovedHeadSha,
    string Key);
