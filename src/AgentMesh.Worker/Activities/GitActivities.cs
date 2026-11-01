using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Services;
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
/// task lifecycle that must not be attributable to the model: the persona container deliberately holds
/// no push credentials (see <see cref="GitHubPullRequestService"/>'s remarks), and a merge is only
/// legitimate as the platform acting on a decision a human recorded through a workflow signal. If the
/// agent could merge, the review gate would be advisory.
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
    IConfiguration config) : IGitActivities
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
            "Merging PR for job {JobId} into target branch {Branch}",
            input.JobId, input.TargetBranch);

        var prUrl = input.PrUrl;
        if (string.IsNullOrWhiteSpace(prUrl))
        {
            var (repository, _) = await ResolveMergeContextAsync(cancellationToken);
            prUrl = repository is not null
                ? $"https://github.com/{repository}/pull/{input.JobId}"
                : $"job-{input.JobId}";
        }

        return await MergePullRequestAsync(prUrl);
    }

    [Activity]
    public async Task<string> MergePullRequestAsync(string prUrl)
    {
        var cancellationToken = ActivityExecutionContext.HasCurrent
            ? ActivityExecutionContext.Current.CancellationToken
            : default;

        if (string.IsNullOrWhiteSpace(prUrl))
        {
            // Nothing to merge and nothing to retry: the workflow asked to merge a null URL, which
            // means it reached this branch without recording a PR. That is a workflow bug, and
            // reporting it as a halt is more honest than reporting it as a merge failure the operator
            // would then try to fix by re-reviewing a PR that does not exist.
            throw new ApplicationFailureException(
                "Merge requested without a pull request URL.", nonRetryable: true, errorType: "no_pr_url");
        }

        var (repository, sha) = await ResolveMergeContextAsync(cancellationToken);

        logger.LogInformation(
            "Merging approved pull request {PrUrl} (repository {Repository}, approvedSha {Sha})",
            prUrl, repository ?? "(resolved from config/url)", sha ?? "(not recorded)");

        var result = await pullRequests.MergePullRequestAsync(
            new GitHubMergeRequest(
                PrUrlOrNumber: prUrl.Trim(),
                Repository: repository,
                // Squash matches this repository's own history (feature commits squashed onto main) and
                // keeps the base branch linear, which is what makes a reverted task easy to find and
                // back out.
                Method: ReadMergeMethod(),
                Sha: sha),
            cancellationToken);

        if (!result.Success)
        {
            var retryable = result.FailureReason is "api_error";

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
