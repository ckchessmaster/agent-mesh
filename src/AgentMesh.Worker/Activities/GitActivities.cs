using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace AgentMesh.Worker.Activities;

/// <summary>
/// Git/PR activities the macro workflow drives after an external decision.
/// </summary>
/// <remarks>
/// <para>
/// The worker performs the merge rather than the agent because the merge is the one action in the
/// task lifecycle that must not be attributable to the model: a merge is only legitimate as the
/// platform acting on a decision a human recorded through a workflow signal. If the agent could
/// merge, the review gate would be advisory. The <em>credential</em> signing the merge is a separate
/// question from <em>who decided</em> to merge — see <see cref="ResolveAuthorGithubTokenAsync"/>.
/// </para>
/// <para>
/// Registered as its own activity type rather than as another method on
/// <see cref="ExecutionActivities"/> because it depends on the GitHub service and the API's
/// configuration surface, and Job A's shell tests construct <c>ExecutionActivities</c> with a bare
/// logger. Keeping the dependency sets separate is what lets each be built by the tests that exercise
/// it.
/// </para>
/// </remarks>
public class GitActivities(
    ILogger<GitActivities> logger,
    IGitHubPullRequestService pullRequests,
    IConfiguration config,
    // Both optional-additive: absent (older three-arg construction, test hosts without the persona
    // store wired) the merge falls back to deployment-configured credentials, which is exactly the
    // behaviour before persona-identity merges existed. The worker host supplies both.
    AgentMeshDbContext? db = null,
    ICredentialEncryptionService? encryption = null) : IGitActivities
{
    /// <summary>
    /// Merges an approved pull request.
    /// </summary>
    /// <remarks>
    /// Registered name: <c>MergePullRequest</c>.
    /// <para>
    /// <strong>Idempotency is the contract.</strong> Temporal retries an activity whose
    /// <c>StartToCloseTimeout</c> elapses, and a merge that GitHub already honoured before the timeout
    /// would otherwise be re-attempted against a merged PR. The service reads the PR first and reports
    /// "already merged" as success with <see cref="GitHubMergeResult.AlreadyMerged"/> set, so a retry
    /// completes rather than failing the task after its work already landed.
    /// </para>
    /// <para>
    /// <strong>Failures are split into retryable and not.</strong> A 5xx or a rate limit should be
    /// retried by the workflow's policy; a rejected merge because the head moved after approval must
    /// not be, because no number of retries will make GitHub accept an unreviewed commit. The
    /// distinction is carried by <see cref="ApplicationFailureException"/>'s non-retryable flag rather
    /// than by returning a string the workflow would have to pattern-match.
    /// </para>
    /// </remarks>
    /// <returns>A summary naming the merged PR, for the workflow's result.</returns>
    [Activity("MergeJobPullRequest")]
    public async Task<string> MergeJobPullRequestAsync(MergePullRequestInput input)
    {
        var cancellationToken = ActivityExecutionContext.HasCurrent
            ? ActivityExecutionContext.Current.CancellationToken
            : default;

        logger.LogInformation(
            "Merging PR for job {JobId} into target branch {Branch} (approvedSha={ApprovedSha})",
            input.JobId, input.TargetBranch, input.ApprovedHeadSha ?? "(none)");

        var prUrl = input.PrUrl;
        if (string.IsNullOrWhiteSpace(prUrl))
        {
            throw new ApplicationFailureException(
                "Merge requested without a pull request URL.", nonRetryable: true, errorType: "no_pr_url");
        }

        var authorToken = await ResolveAuthorGithubTokenAsync(input.JobId, cancellationToken);

        return await MergePullRequestInternalAsync(
            prUrl, input.ApprovedHeadSha, cancellationToken, authorToken);
    }

    [Activity]
    public async Task<string> MergePullRequestAsync(string prUrl)
    {
        var cancellationToken = ActivityExecutionContext.HasCurrent
            ? ActivityExecutionContext.Current.CancellationToken
            : default;

        return await MergePullRequestInternalAsync(prUrl, approvedHeadSha: null, cancellationToken);
    }

    /// <summary>
    /// The job's author persona GitHub PAT — the credential that opened the PR — or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the author's identity signs a platform decision.</strong> The merge is a human's
    /// decision (recorded through the review gate) and a machine's execution; nothing says the
    /// execution must be billed to a GitHub account the human owns. This deployment's GitHub
    /// identities <em>are</em> the personas' — the PR was opened by the persona's credential, the
    /// PR's head branch belongs to that account, and GitHub allows an author to merge its own pull
    /// request. Falling back to the configured control-plane token keeps every existing deployment
    /// working unchanged; when neither exists the service refuses loudly, as it always has.
    /// </para>
    /// <para>
    /// <strong>The credential is read fresh, never replayed.</strong> Temporal activities run real IO
    /// and are allowed to observe the present; the approved SHA comes from the workflow's recorded
    /// signal (immutable history), but <em>which persona</em> authored the job is a lookup, and if it
    /// changed mid-activity (it cannot — the job is parked), re-reading it could only converge to the
    /// current assignee, which is the safer identity, not a less safe one.
    /// </para>
    /// <para>
    /// Resolution order: project job row first (the job-level workflow's id space), then the agent-job
    /// table (the older runner path). A missing job row, missing assignee, or a persona with no stored
    /// token all yield null — the silent-but-correct answer is to fall through to configured
    /// credentials, and the no_credentials failure the service then reports names both halves of the
    /// gap so an operator can tell "persona has no PAT" from "cluster has no token".
    /// </para>
    /// </remarks>
    private async Task<string?> ResolveAuthorGithubTokenAsync(string jobId, CancellationToken ct)
    {
        if (db is null || encryption is null) return null;
        if (!Guid.TryParse(jobId, out var parsed)) return null;

        try
        {
            var authorId =
                (await db.ProjectJobs.AsNoTracking()
                    .Where(j => j.Id == parsed)
                    .Select(j => j.AssignedAgentId)
                    .FirstOrDefaultAsync(ct))
                ?? (await db.AgentJobs.AsNoTracking()
                    .Where(j => j.Id == parsed)
                    .Select(j => j.AssignedAgentId)
                    .FirstOrDefaultAsync(ct));

            if (string.IsNullOrWhiteSpace(authorId)) return null;

            var normalized = authorId.Trim().TrimStart('@').ToLowerInvariant();
            var encrypted = await db.Personas.AsNoTracking()
                .Where(p => p.Id == normalized)
                .Select(p => p.GitHubTokenEncrypted)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(encrypted))
            {
                logger.LogInformation(
                    "Job {JobId} author persona '{Persona}' has no stored GitHub token; " +
                    "falling back to configured GitHub credentials.", jobId, normalized);
                return null;
            }

            var decrypted = encryption.Decrypt(encrypted);
            return string.IsNullOrWhiteSpace(decrypted) ? null : decrypted.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A credential-store blip must not mask the merge into a hard failure here: the
            // service's own no_credentials refusal (or configured-token success) is the honest
            // next step, and it carries the operator-facing message.
            logger.LogWarning(ex,
                "Resolving the author persona GitHub token for job {JobId} failed; " +
                "falling back to configured GitHub credentials.", jobId);
            return null;
        }
    }

    internal static TimeSpan ChecksInProgressPollInterval { get; set; } = TimeSpan.FromSeconds(15);
    internal static TimeSpan ChecksInProgressMaxWait { get; set; } = TimeSpan.FromMinutes(12);

    private async Task<string> MergePullRequestInternalAsync(
        string prUrl,
        string? approvedHeadSha,
        CancellationToken cancellationToken,
        string? authorToken = null)
    {
        if (string.IsNullOrWhiteSpace(prUrl))
        {
            throw new ApplicationFailureException(
                "Merge requested without a pull request URL.", nonRetryable: true, errorType: "no_pr_url");
        }

        var (repository, contextSha) = await ResolveMergeContextAsync(cancellationToken);
        var effectiveSha = !string.IsNullOrWhiteSpace(approvedHeadSha) ? approvedHeadSha.Trim() : contextSha;

        logger.LogInformation(
            "Merging approved pull request {PrUrl} (repository {Repository}, approvedSha {Sha})",
            prUrl, repository ?? "(resolved from config/url)", effectiveSha ?? "(not recorded)");

        var mergeRequest = new GitHubMergeRequest(
            PrUrlOrNumber: prUrl.Trim(),
            Repository: repository,
            Method: ReadMergeMethod(),
            Sha: effectiveSha,
            Token: authorToken);

        var startedAt = DateTimeOffset.UtcNow;
        var result = await pullRequests.MergePullRequestAsync(mergeRequest, cancellationToken);

        // When an operator approves a PR right after it opens, GitHub's required CI checks are often
        // still in progress and return HTTP 405. Poll while checks remain in progress so the approved
        // merge completes automatically once CI turns green, rather than halting the job.
        while (!result.Success &&
               string.Equals(result.FailureReason, "checks_in_progress", StringComparison.OrdinalIgnoreCase) &&
               DateTimeOffset.UtcNow - startedAt < ChecksInProgressMaxWait)
        {
            logger.LogInformation(
                "Merge of {PrUrl} waiting on in-progress required status checks; retrying in {DelaySeconds}s ({Message})",
                prUrl, (int)ChecksInProgressPollInterval.TotalSeconds, result.Message);

            if (ActivityExecutionContext.HasCurrent)
            {
                ActivityExecutionContext.Current.Heartbeat($"Waiting for required status checks on {prUrl}");
            }

            await Task.Delay(ChecksInProgressPollInterval, cancellationToken);
            result = await pullRequests.MergePullRequestAsync(mergeRequest, cancellationToken);
        }

        if (!result.Success)
        {
            var retryable = result.FailureReason is "api_error" or "checks_in_progress";

            logger.LogWarning(
                "Merge of {PrUrl} failed ({Reason}, retryable={Retryable}): {Message}",
                prUrl, result.FailureReason ?? "unspecified", retryable, result.Message);

            // Retryable failures propagate as ordinary exceptions so the workflow's RetryPolicy
            // applies. Non-retryable ones become a non-retryable ApplicationFailure, which the workflow
            // catches and turns into a halted task with the reason intact — the operator sees "the head
            // moved after approval, re-review required" instead of a workflow stuck retrying.
            if (retryable)
            {
                throw new InvalidOperationException(
                    $"Merge of {prUrl} failed transiently ({result.FailureReason}): {result.Message}");
            }

            throw new ApplicationFailureException(
                result.Message,
                nonRetryable: true,
                errorType: result.FailureReason ?? "merge_failed");
        }

        logger.LogInformation("Merge complete: {Message}", result.Message);
        return result.Message;
    }

    /// <summary>
    /// The repository and the reviewed head SHA, read from the job's durable workspace state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repository is resolved from state rather than trusted from the PR URL because the URL is
    /// what the agent's tool call produced and the state is what the platform recorded from an actual
    /// push. When they disagree, merging the URL's repository would merge into a repo the job was never
    /// working in.
    /// </para>
    /// <para>
    /// The head SHA is the reviewer's approval target. It is only available if a turn recorded it, and
    /// today nothing writes <c>checkpoint_head_commit</c> at the moment of PR creation — so this
    /// returns null in the common case and the service logs the gap rather than pretending the
    /// guarantee holds. Reporting a false assurance here would be worse than admitting the check is
    /// not yet wired: an operator reading "approved SHA enforced" would not re-check the branch.
    /// </para>
    /// </remarks>
    private Task<(string? Repository, string? Sha)> ResolveMergeContextAsync(CancellationToken ct)
    {
        // The activity carries only the PR URL (the spec's signature), so the repository comes from
        // configuration or, failing that, from the URL itself inside the service. Reading the job's
        // workspace state instead would require a job id this activity deliberately does not take: a
        // workflow that replays history must not re-derive *which* PR was approved from mutable task
        // state, because the state may have moved on since the review signal was recorded.
        //
        // The approved SHA is likewise not available yet — nothing writes checkpoint_head_commit at the
        // moment of PR creation. Returning null makes the service log the gap rather than claim the
        // approve-then-freeze guarantee holds. Reporting a false assurance would be worse than
        // admitting the check is not wired: an operator reading "approved SHA enforced" would not
        // re-check the branch.
        var configured = config["GitHub:Repository"] ?? config["GitHub:Repo"];
        return Task.FromResult<(string?, string?)>(
            string.IsNullOrWhiteSpace(configured) ? (null, null) : (configured.Trim(), null));
    }

    private GitHubMergeMethod ReadMergeMethod()
    {
        var configured = config["GitHub:MergeMethod"];

        return configured?.Trim().ToLowerInvariant() switch
        {
            "merge" => GitHubMergeMethod.Merge,
            "rebase" => GitHubMergeMethod.Rebase,
            // Squash is the default; an unset or unrecognized value falls to it rather than throwing,
            // because a typo in a config key must not halt a task whose review already passed.
            _ => GitHubMergeMethod.Squash,
        };
    }
}
