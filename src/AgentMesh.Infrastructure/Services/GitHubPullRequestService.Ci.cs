using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Services;

/// <summary>
/// The continuous-integration half of <c>GitHubPullRequestService</c>: asking GitHub whether the
/// commits on a pull request have finished building and testing, and what they decided.
/// </summary>
/// <remarks>
/// <para>
/// A partial class for the same reason the review read is one: the credential resolver, the repository
/// resolver and the versioned HTTP client already live in this type, and a second copy of any of them
/// is a second place a token can leak.
/// </para>
/// <para>
/// <strong>Why this read exists.</strong> A job parks its pull request on a human review gate, and
/// until now nothing in the platform looked at what CI said about that head commit. A red build sat
/// in front of a persona that had already stopped working, waiting for a person to notice and write
/// down in a review box what GitHub had already stated unambiguously. This read lets the gate poller
/// deliver a CI failure through the same channel as a request-changes review, so the author persona is
/// sent to fix the build instead of waiting to be told.
/// </para>
/// <para>
/// <strong>Both check APIs are read, because both are in use.</strong> GitHub Actions publishes check
/// runs; older integrations and most external status publishers report a commit status. Reading only
/// the first would call a repository with Actions wired up "no CI", and reading only the second would
/// miss every workflow this repository actually runs. The two are returned as one list and the caller
/// decides what they mean — this file does not reduce them to a verdict, because "failure that predates
/// the current review park" is policy that belongs where it can be tested without an HTTP stub.
/// </para>
/// <para>
/// <strong><c>filter=latest</c> is the whole reason a re-run is not a failure.</strong> Asking for
/// every attempt would report a check that failed, was re-run by a human, and then passed as still
/// failing, and the loop would send a persona to fix a build that is green. Latest-attempt-per-suite is
/// what a reviewer reading the PR page actually sees.
/// </para>
/// <para>
/// <strong>States are carried verbatim, and a failed read is never an empty list.</strong> Conclusions
/// and statuses arrive as GitHub's strings so a value added later degrades to "not a failure we act
/// on" instead of a parse error. If the check-run read fails, the read fails: an empty list would be
/// read as "this commit has no CI", which is a confident wrong answer and the difference between
/// waiting politely and halting work that was never broken. The combined-status read is the exception —
/// it is strictly additional evidence, and losing it to a 404 on a commit with no legacy statuses must
/// not blind the poller to the check runs that did come back.
/// </para>
/// </remarks>
public partial class GitHubPullRequestService
{
    /// <summary>Check runs for one commit. A pull request's head never approaches this.</summary>
    private const int CheckRunsPerPage = 100;

    /// <summary>
    /// Reads what CI has decided about a pull request's head commit.
    /// </summary>
    /// <param name="prUrlOrNumber">
    /// The PR URL or number. A URL also locates the repository, which is what lets the gate poller pass
    /// its card's <c>prUrl</c> straight through.
    /// </param>
    /// <param name="repositoryOverride">
    /// <c>owner/repo</c> when the caller knows better. Optional; a bare number with neither this nor
    /// <c>GitHub:Repository</c> is refused rather than guessed.
    /// </param>
    /// <param name="headShaOverride">
    /// The commit to ask about, when the caller already read the pull request for another reason. The
    /// head is never taken on trust from a card: CI for a superseded commit says nothing about whether
    /// the code on the branch now is good, and asking about the live head is also what makes a pushed
    /// fix stop the previous failure from being reported again.
    /// </param>
    public async Task<GitHubCiSnapshot> ReadPullRequestCiStateAsync(
        string prUrlOrNumber,
        string? repositoryOverride = null,
        string? tokenOverride = null,
        string? headShaOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prUrlOrNumber))
        {
            return GitHubCiSnapshot.Failure("invalid_request",
                "A pull request URL or number is required to read its CI state.");
        }

        var (refOwner, refRepo, refNumber) = ParsePullRequestReference(prUrlOrNumber);

        if (refNumber is not { } prNumber)
        {
            return GitHubCiSnapshot.Failure("invalid_pr_reference",
                $"'{Truncate(Sanitize(prUrlOrNumber), 120)}' is neither a pull request URL nor a number.");
        }

        var options = ReadOptions();
        (string Owner, string Repo)? fromUrl =
            refOwner is not null && refRepo is not null ? (refOwner, refRepo) : null;
        var repo = ResolveRepository(repositoryOverride, null)
                   ?? fromUrl
                   ?? ResolveRepository(null, options.Repository);

        if (repo is not { } target)
        {
            return GitHubCiSnapshot.Failure("no_repository",
                $"Cannot determine the repository for pull request #{prNumber}: the reference carried no " +
                "owner/repo, no repository override was passed, and GitHub:Repository is not configured.");
        }

        var token = !string.IsNullOrWhiteSpace(tokenOverride) ? tokenOverride.Trim() : null;
        if (token is null)
        {
            token = await ResolveTokenAsync(options, ct);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return GitHubCiSnapshot.Failure("no_credentials",
                "No GitHub credentials available for this CI read: no persona token was passed and none " +
                "is configured, so check results cannot be read.");
        }

        var headSha = string.IsNullOrWhiteSpace(headShaOverride) ? null : headShaOverride.Trim();

        if (headSha is null)
        {
            var prResponse = await SendAsync(
                HttpMethod.Get,
                $"/repos/{target.Owner}/{target.Repo}/pulls/{prNumber}",
                EmptyJsonContent(),
                token,
                ct);

            var prBody = await SafeBodyAsync(prResponse, ct);

            if (!prResponse.IsSuccessStatusCode)
            {
                return GitHubCiSnapshot.Failure(
                    prResponse.StatusCode == System.Net.HttpStatusCode.NotFound ? "not_found" : "api_error",
                    $"Could not read pull request #{prNumber} from {target.Owner}/{target.Repo}: " +
                    $"HTTP {(int)prResponse.StatusCode}.");
            }

            var (_, prHeadSha, _) = TryReadPullRequestState(prBody);
            headSha = string.IsNullOrWhiteSpace(prHeadSha) ? null : prHeadSha.Trim();

            if (headSha is null)
            {
                return GitHubCiSnapshot.Failure("api_error",
                    $"Pull request #{prNumber} came back without a head commit from " +
                    $"{target.Owner}/{target.Repo}, so its CI state cannot be read.");
            }
        }

        var checks = new List<GitHubCiCheck>();

        var runsResponse = await SendAsync(
            HttpMethod.Get,
            $"/repos/{target.Owner}/{target.Repo}/commits/{Uri.EscapeDataString(headSha)}" +
            $"/check-runs?per_page={CheckRunsPerPage}&filter=latest",
            EmptyJsonContent(),
            token,
            ct);

        var runsBody = await SafeBodyAsync(runsResponse, ct);

        if (!runsResponse.IsSuccessStatusCode)
        {
            return GitHubCiSnapshot.Failure(
                runsResponse.StatusCode == System.Net.HttpStatusCode.NotFound ? "not_found" : "api_error",
                $"Could not read check runs for {ShortSha(headSha)} on {target.Owner}/{target.Repo}: " +
                $"HTTP {(int)runsResponse.StatusCode}.");
        }

        checks.AddRange(TryReadCheckRuns(runsBody));

        if (checks.Count(r => r.Kind == GitHubCiCheckKind.CheckRun) >= CheckRunsPerPage)
        {
            logger.LogWarning(
                "Check runs for {Owner}/{Repo}@{Sha} filled one full page ({PerPage}); older check " +
                "suites beyond the first page were not read.",
                target.Owner, target.Repo, ShortSha(headSha), CheckRunsPerPage);
        }

        // Additional evidence only, so a failure here is not a failure of the read. Repositories whose
        // CI is Actions alone commonly answer this with statuses nobody ever posted, and that must not
        // cost the poller the check runs it did read.
        var statusResponse = await SendAsync(
            HttpMethod.Get,
            $"/repos/{target.Owner}/{target.Repo}/commits/{Uri.EscapeDataString(headSha)}/status",
            EmptyJsonContent(),
            token,
            ct);

        if (statusResponse.IsSuccessStatusCode)
        {
            checks.AddRange(TryReadCommitStatuses(await SafeBodyAsync(statusResponse, ct)));
        }
        else
        {
            logger.LogDebug(
                "Combined commit status for {Owner}/{Repo}@{Sha} was not readable (HTTP {Status}); " +
                "acting on {RunCount} check run(s) alone.",
                target.Owner, target.Repo, ShortSha(headSha), (int)statusResponse.StatusCode, checks.Count);
        }

        return new GitHubCiSnapshot(true, headSha, checks);
    }

    /// <summary>
    /// Parses the <c>GET /commits/{ref}/check-runs</c> payload into check records.
    /// </summary>
    /// <remarks>
    /// Tolerant by design, for the reason the review parser gives: one malformed entry in one suite must
    /// not cost the poller every other check on the commit. An entry with no name is still kept, because
    /// a nameless failing check is a real failure and dropping it would be a silent green light; the
    /// caller labels it when it composes the feedback.
    /// </remarks>
    internal static IReadOnlyList<GitHubCiCheck> TryReadCheckRuns(string? json)
    {
        var results = new List<GitHubCiCheck>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("check_runs", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var status = ReadString(item, "status") ?? "completed";
                var conclusion = ReadString(item, "conclusion");

                results.Add(new GitHubCiCheck(
                    Kind: GitHubCiCheckKind.CheckRun,
                    Name: ReadString(item, "name"),
                    Status: status,
                    Conclusion: conclusion,
                    // A check run that has not finished reports no conclusion; reporting its status here
                    // keeps "still running" and "finished without a conclusion" distinguishable upstream.
                    State: conclusion ?? status,
                    HtmlUrl: ReadString(item, "html_url") ?? ReadString(item, "details_url"),
                    CompletedAt: ReadNestedDate(item, "completed_at")));
            }
        }
        catch (JsonException)
        {
            // Whatever parsed into `results` before the failure is still the truth about this commit.
        }

        return results;
    }

    /// <summary>
    /// Parses the <c>GET /commits/{ref}/status</c> payload's individual statuses.
    /// </summary>
    /// <remarks>
    /// The payload's top-level <c>state</c> is deliberately ignored: it is GitHub's rollup, and a
    /// caller that wants a rollup can compute one from the same entries the poller is acting on. Keeping
    /// only <c>statuses[]</c> means one failing context names itself in the feedback instead of the
    /// rollup saying merely "failure" about six of them.
    /// </remarks>
    internal static IReadOnlyList<GitHubCiCheck> TryReadCommitStatuses(string? json)
    {
        var results = new List<GitHubCiCheck>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("statuses", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var state = ReadString(item, "state");
                if (string.IsNullOrWhiteSpace(state)) continue;

                results.Add(new GitHubCiCheck(
                    Kind: GitHubCiCheckKind.CommitStatus,
                    Name: ReadString(item, "context"),
                    // Commit statuses have no separate status/conclusion pair: the state is both, and a
                    // `pending` here means exactly what an unfinished check run means upstream.
                    Status: state,
                    Conclusion: null,
                    State: state,
                    HtmlUrl: ReadString(item, "target_url"),
                    CompletedAt: state.Equals("pending", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : ReadNestedDate(item, "updated_at")));
            }
        }
        catch (JsonException)
        {
            // Same posture as the check-run parser: partial truth beats none.
        }

        return results;
    }

    // Dates come from ReadNestedDate (declared beside the review parsers): it already applies the
    // invariant UTC conversion this class's callers depend on, and a second parser would be a second
    // place to get the conversion wrong.
    private static string ShortSha(string sha) =>
        sha.Length > 8 ? sha[..8] : sha;
}

/// <summary>Which of GitHub's two check-reporting APIs produced a record.</summary>
public static class GitHubCiCheckKind
{
    /// <summary>A GitHub Actions (or other Checks API) check run.</summary>
    public const string CheckRun = "check_run";

    /// <summary>A legacy commit status posted by an integration.</summary>
    public const string CommitStatus = "commit_status";
}

/// <summary>
/// One CI result reported against a pull request's head commit, exactly as GitHub stated it.
/// </summary>
/// <param name="Kind">
/// <see cref="GitHubCiCheckKind.CheckRun"/> or <see cref="GitHubCiCheckKind.CommitStatus"/>. Kept as a
/// string constant rather than an enum because callers branch on it in a poll loop and a new kind should
/// degrade to unrecognised, not to a crash.
/// </param>
/// <param name="Name">
/// The check or status context. Nullable because GitHub reports unnamed check runs for some apps; the
/// poller still counts them as failures and gives them a placeholder name in the feedback.
/// </param>
/// <param name="Status">
/// Check runs: <c>queued</c>, <c>in_progress</c>, <c>completed</c>. Commit statuses: <c>pending</c>,
/// <c>success</c>, <c>failure</c>, <c>error</c>. Carried verbatim.
/// </param>
/// <param name="Conclusion">
/// A finished check run's verdict (<c>failure</c>, <c>success</c>, <c>cancelled</c>, <c>skipped</c>,
/// <c>neutral</c>, <c>timed_out</c>, <c>action_required</c>, <c>stale</c>), or null while it runs.
/// </param>
/// <param name="State">
/// The record's effective state for display: a check run's conclusion once it has one, otherwise its
/// status; a commit status is its state.
/// </param>
/// <param name="HtmlUrl">
/// Where a human goes to read the log. Carried into the review feedback so a persona can be told which
/// URL to look at, and so the cockpit link survives after the run scrolls off the checks tab.
/// </param>
/// <param name="CompletedAt">
/// When the result landed, UTC when parseable, null when GitHub did not date it. Null is treated as
/// "undateable, so undated results are judged by the freshness rule" rather than as ancient.
/// </param>
public sealed record GitHubCiCheck(
    string Kind,
    string? Name,
    string Status,
    string? Conclusion,
    string? State,
    string? HtmlUrl,
    DateTimeOffset? CompletedAt);

/// <summary>The result of reading a pull request head's CI state.</summary>
/// <param name="Success">
/// False means nothing here may be acted on. In particular it must not be read as "CI is green": the
/// absence of evidence that a build broke is not evidence that it passed.
/// </param>
/// <param name="HeadSha">The commit every returned check belongs to.</param>
public sealed record GitHubCiSnapshot(
    bool Success,
    string? HeadSha,
    IReadOnlyList<GitHubCiCheck> Checks,
    string? FailureReason = null,
    string? Message = null)
{
    internal static GitHubCiSnapshot Failure(string reason, string message) =>
        new(false, null, Array.Empty<GitHubCiCheck>(), reason, message);
}
