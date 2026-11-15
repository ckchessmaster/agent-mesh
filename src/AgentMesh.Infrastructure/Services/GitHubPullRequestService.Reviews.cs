using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Services;

/// <summary>
/// The review-reading half of <c>GitHubPullRequestService</c>: asking GitHub what a pull request's
/// reviewers have actually decided.
/// </summary>
/// <remarks>
/// <para>
/// A partial class for the same reason <see cref="ReadBranchComparisonAsync"/> is one: the credential
/// resolver, the repository resolver and the versioned HTTP client already live in this type, and a
/// second copy of any of them is a second place a token can leak.
/// </para>
/// <para>
/// <strong>Why this read exists at all.</strong> The review gate used to be released only by an HTTP
/// call into the platform (the two review endpoints). That made a review submitted where reviewers
/// naturally work — the GitHub PR page itself — invisible: the workflow would sit parked until the
/// seven-day timeout even though a human had plainly approved. This is the read that lets a poller
/// notice the GitHub-side decision and deliver it as the same <c>PrReviewSignal</c> the endpoints
/// send, so the gate's merge, two-man and SHA-freeze semantics are unchanged no matter where the
/// review came from.
/// </para>
/// <para>
/// <strong>Reads prefer the URL over configuration, which is the reverse of merge.</strong> A merge
/// wants the repository the credentials and the operator's <c>GitHub:Repository</c> point at, because
/// "which repo do we land this in" is a deployment decision. A review read answers "what did people
/// say on <em>this</em> pull request", and the URL recorded on the gate's card is the only unambiguous
/// locator — the same PR number in a different repository is a different pull request, and reading
/// its reviews would be a confident wrong answer about someone else's code.
/// </para>
/// <para>
/// <strong>Committed review states only, and the caller decides what they mean.</strong> This returns
/// every submitted review (<c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c>,
/// <c>DISMISSED</c>) with the commit each one was made against. It deliberately does not collapse
/// them into a verdict: "approval counts only for the commit it saw" needs the current head SHA and
/// the per-reviewer latest-state rule, and that policy lives in the poller where it can be tested
/// without an HTTP stub.
/// </para>
/// </remarks>
public partial class GitHubPullRequestService
{
    /// <summary>How many submitted reviews one read returns. One job's PR never approaches this.</summary>
    private const int ReviewsPerPage = 100;

    /// <summary>
    /// Reads a pull request's current state and the review decisions recorded on it.
    /// </summary>
    /// <param name="prUrlOrNumber">
    /// The PR URL or number. A URL also locates the repository, which is why the poller can pass the
    /// gate card's <c>prUrl</c> through without resolving the project first.
    /// </param>
    /// <param name="repositoryOverride">
    /// <c>owner/repo</c> when the caller knows better. Optional; a bare number with neither this nor
    /// <c>GitHub:Repository</c> is refused rather than guessed.
    /// </param>
    public async Task<GitHubPrReviewSnapshot> ReadPullRequestReviewStateAsync(
        string prUrlOrNumber,
        string? repositoryOverride = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prUrlOrNumber))
        {
            return GitHubPrReviewSnapshot.Failure("invalid_request",
                "A pull request URL or number is required to read its reviews.");
        }

        var (refOwner, refRepo, refNumber) = ParsePullRequestReference(prUrlOrNumber);

        if (refNumber is not { } prNumber)
        {
            return GitHubPrReviewSnapshot.Failure("invalid_pr_reference",
                $"'{Truncate(Sanitize(prUrlOrNumber), 120)}' is neither a pull request URL nor a number.");
        }

        // URL before configuration — see the class remarks for why this is the opposite of the merge path.
        var options = ReadOptions();
        (string Owner, string Repo)? fromUrl =
            refOwner is not null && refRepo is not null ? (refOwner, refRepo) : null;
        var repo = ResolveRepository(repositoryOverride, null)
                   ?? fromUrl
                   ?? ResolveRepository(null, options.Repository);

        if (repo is not { } target)
        {
            return GitHubPrReviewSnapshot.Failure("no_repository",
                $"Cannot determine the repository for pull request #{prNumber}: the reference carried no " +
                "owner/repo, no repository override was passed, and GitHub:Repository is not configured.");
        }

        var token = await ResolveTokenAsync(options, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            return GitHubPrReviewSnapshot.Failure("no_credentials",
                "No GitHub credentials are configured, so pull request reviews cannot be read.");
        }

        var prResponse = await SendAsync(
            HttpMethod.Get,
            $"/repos/{target.Owner}/{target.Repo}/pulls/{prNumber}",
            EmptyJsonContent(),
            token,
            ct);

        var prBody = await SafeBodyAsync(prResponse, ct);

        if (!prResponse.IsSuccessStatusCode)
        {
            return GitHubPrReviewSnapshot.Failure(
                prResponse.StatusCode == System.Net.HttpStatusCode.NotFound ? "not_found" : "api_error",
                $"Could not read pull request #{prNumber} from {target.Owner}/{target.Repo}: " +
                $"HTTP {(int)prResponse.StatusCode}.");
        }

        var summary = TryReadPullRequestSummary(prBody);

        if (summary.State is null)
        {
            return GitHubPrReviewSnapshot.Failure("api_error",
                $"Pull request #{prNumber} came back without a readable state from " +
                $"{target.Owner}/{target.Repo}.");
        }

        var reviewsResponse = await SendAsync(
            HttpMethod.Get,
            $"/repos/{target.Owner}/{target.Repo}/pulls/{prNumber}/reviews?per_page={ReviewsPerPage}",
            EmptyJsonContent(),
            token,
            ct);

        var reviewsBody = await SafeBodyAsync(reviewsResponse, ct);

        if (!reviewsResponse.IsSuccessStatusCode)
        {
            // Report the failure rather than a PR with zero reviews: an empty list here would read as
            // "nobody has said anything yet", which is exactly the answer a rate-limited call fakes.
            return GitHubPrReviewSnapshot.Failure(
                reviewsResponse.StatusCode == System.Net.HttpStatusCode.NotFound ? "not_found" : "api_error",
                $"Could not read reviews for pull request #{prNumber} from {target.Owner}/{target.Repo}: " +
                $"HTTP {(int)reviewsResponse.StatusCode}.");
        }

        var reviews = TryReadReviews(reviewsBody);

        if (reviews.Count >= ReviewsPerPage)
        {
            logger.LogWarning(
                "Reviews for {Owner}/{Repo}#{Pr} filled one full page ({PerPage}); older reviews beyond " +
                "the first page were not read.",
                target.Owner, target.Repo, prNumber, ReviewsPerPage);
        }

        return new GitHubPrReviewSnapshot(
            Success: true,
            PrState: summary.State,
            AuthorLogin: summary.AuthorLogin,
            HeadSha: summary.HeadSha,
            MergedByLogin: summary.MergedByLogin,
            MergedAt: summary.MergedAt,
            MergeCommitSha: summary.MergeCommitSha,
            Reviews: reviews);
    }

    /// <summary>
    /// Everything the review poller needs from one pull-request payload.
    /// </summary>
    /// <remarks>
    /// The author login is not a nicety: it is the GitHub-side half of the two-man rule. A review from
    /// the account that opened the PR is not a second pair of eyes, whoever the persona behind it is.
    /// </remarks>
    internal static (
        string? State,
        string? HeadSha,
        string? AuthorLogin,
        string? MergedByLogin,
        DateTimeOffset? MergedAt,
        string? MergeCommitSha) TryReadPullRequestSummary(string? json)
    {
        var (state, headSha, mergeCommitSha) = TryReadPullRequestState(json);

        string? authorLogin = null;
        string? mergedByLogin = null;
        DateTimeOffset? mergedAt = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return (state, headSha, authorLogin, mergedByLogin, mergedAt, mergeCommitSha);

                authorLogin = ReadNestedLogin(doc.RootElement, "user");
                mergedByLogin = ReadNestedLogin(doc.RootElement, "merged_by");
                mergedAt = ReadNestedDate(doc.RootElement, "merged_at");
            }
            catch (JsonException)
            {
                // The three extras are context, not preconditions: a payload that parses well enough
                // for state and head still answers the poller's main question.
            }
        }

        return (state, headSha, authorLogin, mergedByLogin, mergedAt, mergeCommitSha);
    }

    /// <summary>
    /// Parses the <c>GET /pulls/{n}/reviews</c> array into submitted review records.
    /// </summary>
    /// <remarks>
    /// Tolerant by design and it must stay that way: an item missing <c>state</c> or <c>id</c> is
    /// skipped rather than failing the read, because one unexpected field in one entry must not cost
    /// the poller the reviews that did parse. Timestamps are whatever GitHub emitted; an unparseable
    /// one becomes null and the caller falls back to ordering by review id, which GitHub assigns
    /// monotonically per pull request.
    /// </remarks>
    internal static IReadOnlyList<GitHubPullReview> TryReadReviews(string? json)
    {
        var results = new List<GitHubPullReview>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("state", out var stateProp) || stateProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var state = stateProp.GetString();
                if (string.IsNullOrWhiteSpace(state)) continue;

                long id = 0;
                if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                {
                    id = idProp.GetInt64();
                }

                results.Add(new GitHubPullReview(
                    Id: id,
                    Login: ReadNestedLogin(item, "user"),
                    State: state,
                    CommitSha: ReadString(item, "commit_sha"),
                    Body: item.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
                        ? bodyProp.GetString()
                        : null,
                    SubmittedAt: ReadNestedDate(item, "submitted_at")));
            }
        }
        catch (JsonException)
        {
            // Not JSON at all (an HTML error page through a proxy, say): return what nothing was read,
            // which the caller distinguishes from "read and empty" only by the snapshot's Success flag.
        }

        return results;
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedLogin(JsonElement root, string objectProperty) =>
        root.TryGetProperty(objectProperty, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, "login")
            : null;

    private static DateTimeOffset? ReadNestedDate(JsonElement root, string property)
    {
        var raw = ReadString(root, property);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static StringContent EmptyJsonContent() =>
        new(string.Empty, Encoding.UTF8, "application/json");
}

/// <summary>One submitted review on a pull request, as GitHub recorded it.</summary>
/// <param name="Id">
/// GitHub's review id. Monotonic per pull request, which makes it the tie-breaker when timestamps are
/// missing or equal.
/// </param>
/// <param name="Login">
/// The account that submitted it. Compared against the PR author's login to keep the two-man rule on
/// the GitHub side; null means the entry carried no usable user object and the poller will drop it.
/// </param>
/// <param name="State">
/// One of <c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c>, <c>DISMISSED</c>,
/// <c>PENDING</c>. Carried verbatim rather than as an enum so a state GitHub adds later degrades to
/// "not a decision we act on" instead of a parse failure.
/// </param>
/// <param name="CommitSha">
/// The head commit the reviewer was looking at. An approval counts only while this still matches the
/// PR's current head — the same freeze the merge guard enforces from the other side.
/// </param>
/// <param name="SubmittedAt">
/// GitHub's clock, UTC when parseable. Null orders as oldest (id still breaks ties) but is not
/// suppressed by the poller's park-time guard — losing a real decision is worse than risking one
/// duplicate.
/// </param>
public sealed record GitHubPullReview(
    long Id,
    string? Login,
    string State,
    string? CommitSha,
    string? Body,
    DateTimeOffset? SubmittedAt);

/// <summary>The result of reading a pull request's state and reviews.</summary>
/// <param name="Success">
/// False means nothing here may be acted on — in particular, a failed reviews read must not be
/// presented as an empty review list, because "nobody has reviewed yet" is a real answer and a lie
/// about it is worse than no answer.
/// </param>
/// <param name="PrState">GitHub's <c>open</c> / <c>closed</c> / <c>merged</c>.</param>
/// <param name="AuthorLogin">The account that opened the PR — the login the two-man rule excludes.</param>
/// <param name="MergedAt">Set when the PR was merged, including a manual merge on github.com.</param>
public sealed record GitHubPrReviewSnapshot(
    bool Success,
    string? PrState,
    string? AuthorLogin,
    string? HeadSha,
    string? MergedByLogin,
    DateTimeOffset? MergedAt,
    string? MergeCommitSha,
    IReadOnlyList<GitHubPullReview> Reviews,
    string? FailureReason = null,
    string? Message = null)
{
    internal static GitHubPrReviewSnapshot Failure(string reason, string message) =>
        new(false, null, null, null, null, null, null, Array.Empty<GitHubPullReview>(), reason, message);
}
