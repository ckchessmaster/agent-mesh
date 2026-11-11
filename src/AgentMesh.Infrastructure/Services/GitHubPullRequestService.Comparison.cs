using System.Text;
﻿using AgentMesh.Core.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Infrastructure.Services;

/// <summary>
/// The remote half of <c>GitHubPullRequestService</c>: asking GitHub what a pushed branch changed.
/// </summary>
/// <remarks>
/// A partial class rather than a new service because the three things this needs — the credential
/// resolver (PAT or GitHub App installation token, with the same precedence and the same refusal on a
/// half-configured App), the repository resolver, and the named <see cref="HttpClient"/> with its
/// versioned headers — are already correct here, and a second implementation of any of them is a
/// second place where a credential can leak into a log or a URL.
/// <para>
/// <strong>Why the comparison endpoint and not the pull-request files endpoint.</strong> The PR
/// endpoint only exists once somebody opens a pull request, and the Changes panel has to work during
/// the turn — before any PR, for a job that never opens one, and for a job whose branch was pushed
/// without the platform's knowledge. <c>GET /repos/{owner}/{repo}/compare/{base}...{head}</c> answers
/// for any two refs, reports its own <c>merge_base_commit</c> (so the remote answer names the same
/// fork point the local reader computes with <c>git merge-base</c>), and returns the per-file
/// additions/deletions the panel renders.
/// </para>
/// <para>
/// <strong>Committed work only, and it says so.</strong> A remote read cannot see an uncommitted
/// tree; that is not a limitation to paper over but the reason the local source is preferred and why
/// <see cref="JobChangeSourceInfo.CommittedOnly"/> exists on the response.
/// </para>
/// <para>
/// <strong>No simulation path.</strong> <see cref="PushBranchAndCreatePrAsync"/> fabricates PR #999
/// when no credentials are configured, which is defensible for an action an agent can retry. Inventing
/// a change set is not: the panel would show files that do not exist, and nobody downstream would ever
/// find out. Missing credentials here are a reason code with an explanation.
/// </para>
/// </remarks>
public partial class GitHubPullRequestService
{
    /// <summary>
    /// Reads the comparison for two refs.
    /// </summary>
    /// <param name="headRef">The job's branch.</param>
    /// <param name="baseRef">The project's base branch.</param>
    /// <param name="repositoryOverride">
    /// <c>owner/repo</c> or a repository URL, when the caller knows better than configuration. A job's
    /// recorded remote wins here, because a multi-repo project's jobs do not all live in the configured
    /// repository and diffing the wrong one would be a confident wrong answer.
    /// </param>
    /// <remarks>
    /// The base is listed first, which is what makes GitHub compute <c>merge_base_commit</c> as
    /// "where the head branched off the base" and report only the head's own commits — the same
    /// symmetric fork-point semantics as <c>git log base..head</c> on the local side. Reversing the
    /// pair would report the base's commits as the job's work.
    /// </remarks>
    public async Task<JobChangeRemoteRead> ReadBranchComparisonAsync(
        string headRef,
        string? baseRef,
        string? repositoryOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(headRef))
        {
            return JobChangeRemoteRead.Fail(JobChangeUnavailableReasons.NoBranch, "No branch to compare.");
        }

        var options = ReadOptions();

        // The credential is resolved first and refused outright when absent, before any repository
        // guesswork: a request without a token is a 404 from GitHub for a private repository and a
        // rate-limited 403 for a public one, and both would read as "the branch does not exist" rather
        // than as "this deployment has no GitHub credentials configured", which is the actual fact the
        // operator needs.
        var token = await ResolveTokenAsync(options, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            return JobChangeRemoteRead.Fail(JobChangeUnavailableReasons.RemoteUnavailable,
                "No GitHub credentials are configured (GitHub:Token, or the App id/key/installation " +
                "triple), so the pushed branch cannot be read.");
        }

        // The two-string overload returns a nullable tuple (a miss is "could not resolve", not
        // "resolved to empty"), so it is unwrapped rather than deconstructed — the deconstruction the
        // push path uses targets its own (string?, string?) overload with a non-nullable tuple.
        var target = ResolveRepository(repositoryOverride, options.Repository);
        if (target is not { } resolved)
        {
            return JobChangeRemoteRead.Fail(JobChangeUnavailableReasons.RemoteUnavailable,
                "Could not determine the repository to compare in. Pass it through the job's git " +
                "remote, or set GitHub:Repository in configuration as 'owner/repo'.");
        }

        var owner = resolved.Owner;
        var repo = resolved.Repo;

        var head = Uri.EscapeDataString(headRef.Trim());
        var baseSegment = string.IsNullOrWhiteSpace(baseRef) ? "main" : baseRef.Trim();

        // Try the configured base, then main and master: a project row's base branch is operator input
        // and can outlive a rename of the repository's default branch, and the panel should still work
        // in that case rather than reporting nothing.
        foreach (var candidate in new[] { baseSegment, "main", "master" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = $"/repos/{owner}/{repo}/compare/{Uri.EscapeDataString(candidate)}...{head}";
            var response = await SendAsync(
                HttpMethod.Get,
                path,
                new StringContent(string.Empty, Encoding.UTF8, "application/json"),
                token,
                ct);

            using (response)
            {
                var body = await SafeBodyAsync(response, ct);

                if (!response.IsSuccessStatusCode)
                {
                    // 404 on this endpoint is overwhelmingly "one of the two refs does not exist" —
                    // most often a branch that was never pushed. Everything else (401/403/5xx) is a
                    // credentials or availability problem, and the two need different next actions.
                    var reason = response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? JobChangeUnavailableReasons.RemoteFailed
                        : JobChangeUnavailableReasons.RemoteUnavailable;

                    var detail = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.NotFound =>
                            $"'{headRef}' or the base '{candidate}' is not a ref in {owner}/{repo} " +
                            "(the branch may never have been pushed).",
                        System.Net.HttpStatusCode.Forbidden when body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) =>
                            "GitHub is rate-limiting this deployment's API requests.",
                        System.Net.HttpStatusCode.Unauthorized =>
                            "GitHub rejected the configured credentials.",
                        _ => Truncate(Redact(Sanitize(body), token), 200),
                    };

                    if (candidate.Equals(baseSegment, StringComparison.OrdinalIgnoreCase))
                    {
                        // The caller's own base failed: report it unless a fallback ref might work.
                        var mayRetry = !string.Equals(candidate, "main", StringComparison.OrdinalIgnoreCase) &&
                                       !string.Equals(candidate, "master", StringComparison.OrdinalIgnoreCase);
                        if (!mayRetry)
                        {
                            return JobChangeRemoteRead.Fail(reason, detail);
                        }
                    }

                    continue;
                }

                var files = GitChangesParser.ReadGitHubFiles(body);
                var commits = GitChangesParser.ReadGitHubCommits(body, JobChangesPolicy.CommitListLimit);
                var mergeBase = GitChangesParser.ReadGitHubMergeBase(body);
                var aheadBehind = GitChangesParser.ReadGitHubAheadBehind(body);

                if (mergeBase is null && files.Count == 0 && commits.Count == 0)
                {
                    // A payload with no fork point and no content is GitHub's "comparison cannot be
                    // computed" shape. Reporting it as an empty change set would claim the branch
                    // changed nothing, which is a different and much more misleading statement than
                    // "I could not compute this".
                    return JobChangeRemoteRead.Fail(JobChangeUnavailableReasons.RemoteFailed,
                        $"GitHub returned a comparison for {owner}/{repo} with no usable content.");
                }

                var notes = GitChangesParser.ReadComparisonNotes(body);

                var (joined, stats, truncated) = JobChangeMerge.Join(
                    files, Array.Empty<ChangedFileEntry>());

                // The remote knows a path exists on the branch but holds no bytes for it, so a patch
                // has to come from GitHub too: the panel's per-file diff is served from this same
                // comparison payload, which carries a `patch` field per file.
                return JobChangeRemoteRead.Ok(new JobChangeSetDto(
                    JobId: Guid.Empty,
                    Branch: headRef.Trim(),
                    BaseBranch: candidate,
                    HeadSha: commits.Count > 0 ? commits[commits.Count - 1].Sha : head,
                    BaseSha: mergeBase,
                    Stats: stats,
                    Files: joined,
                    Source: new JobChangeSourceInfo(
                        JobChangesSources.GitHubCompare,
                        ReasonCode: null,
                        Message: $"GitHub's comparison of {owner}/{repo} '{candidate}' to '{headRef}'.",
                        Degraded: false,
                        CommittedOnly: true,
                        Truncated: truncated,
                        Repository: $"{owner}/{repo}"),
                    Commits: commits,
                    HasUncommittedChanges: false,
                    UncommittedCount: 0,
                    GeneratedAt: DateTimeOffset.UtcNow,
                    Notes: notes.Count > 0 ? notes : null), body);
            }
        }

        return JobChangeRemoteRead.Fail(JobChangeUnavailableReasons.RemoteFailed,
            $"GitHub could not compare '{headRef}' against any known base branch in {owner}/{repo}.");
    }

    /// <summary>
    /// The result of a remote comparison read, carrying the raw payload so a caller can extract
    /// per-file patches without a second request.
    /// </summary>
    public sealed record JobChangeRemoteRead(JobChangeSetDto? ChangeSet, string? RawPayload, string? ReasonCode, string? Reason)
    {
        public static JobChangeRemoteRead Ok(JobChangeSetDto changeSet, string rawPayload) =>
            new(changeSet, rawPayload, null, null);

        public static JobChangeRemoteRead Fail(string reasonCode, string reason) =>
            new(null, null, reasonCode, reason);
    }
}
