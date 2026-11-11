using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// The precedence rules, limits and reason codes for "what did this job change?".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these rules live in Core and the reads live in the API.</strong> The precedence and the
/// limits are a contract that the dashboard, the endpoints and the tests all have to agree on, and the
/// dashboard cannot reference the Api project. The actual reads (<c>git</c> in a checkout, GitHub's
/// comparison for a pushed branch, the stored working checkpoint) are API-side because only that
/// process decides which sources it can attempt. This type therefore owns the numbers and the
/// vocabulary, and the fold that applies them
/// (<see cref="JobChangesService.Compose"/> in <c>AgentMesh.Api.Services</c>).
/// </para>
/// <para>
/// <strong>The order is local → pushed → checkpoint.</strong> A local read is strictly more
/// informative than a remote one: it sees work that is neither committed nor pushed, which is the
/// state an agent's work is in most of the time. A pushed branch is strictly more informative than a
/// checkpoint, which names paths but has no line stats. Preferring a cached remote answer over an
/// exact local one would trade a real fact for a stale one, and the panel would look healthy while
/// reporting last week's change.
/// </para>
/// <para>
/// <strong>Every degraded answer says it is degraded.</strong> A response with
/// <see cref="JobChangeSourceInfo.Kind"/> set and <see cref="JobChangeSourceInfo.CommittedOnly"/> or
/// <see cref="JobChangeSourceInfo.Degraded"/> true is the difference between "the platform shows diffs"
/// and "the platform sometimes shows diffs and tells you which kind it showed". The reason codes are
/// machine-readable so the UI can word each case distinctly instead of printing one generic shrug.
/// </para>
/// </remarks>
public static class JobChangesPolicy
{
    /// <summary>Paths in a list response when the caller does not ask for a number.</summary>
    public const int DefaultFileListLimit = 500;

    /// <summary>Hard ceiling on paths in one response, whatever the caller asks for.</summary>
    public const int MaxFileListLimit = 2000;

    /// <summary>Commit subjects in one response.</summary>
    public const int CommitListLimit = 100;

    /// <summary>
    /// Sample size for the list of uncommitted paths.
    /// </summary>
    /// <remarks>
    /// A summary, not a file browser: <see cref="JobChangeSetDto.UncommittedCount"/> carries the exact
    /// number, so capping the sample costs no information the reader needs.
    /// </remarks>
    public const int UncommittedPathLimit = 50;

    /// <summary>Jobs answered by one summary call, so a project list cannot ask for unbounded work.</summary>
    public const int MaxSummaryBatch = 50;

    /// <summary>Default ceiling on one patch body, in bytes.</summary>
    public const int DefaultPatchByteLimit = 512 * 1024;

    /// <summary>
    /// Hard ceiling on a configured patch limit, however the operator sets it.
    /// </summary>
    /// <remarks>
    /// A patch is a string that a browser panel puts into the DOM. Beyond a few megabytes the reader
    /// has no way to review it and the tab has no way to stay responsive, so the knob can only be
    /// turned down from here, not up.
    /// </remarks>
    public const int MaxPatchByteLimit = 4 * 1024 * 1024;

    /// <summary>
    /// How long a successful change set may be reused for the same reader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panel fetches the list when a job is opened, then fetches a patch per file click, and a
    /// whole-change-set patch on demand. Without any reuse, opening one job with twelve changed files
    /// costs sixty <c>git</c> invocations in a live checkout. Ten seconds is short enough that a
    /// reviewer who waits for an agent's next turn sees new work immediately, and long enough to
    /// collapse one browsing session into one set of reads.
    /// </para>
    /// <para>
    /// <c>?refresh=true</c> bypasses it entirely, and a caller that pins
    /// <see cref="JobChangeSetDto.BaseSha"/>/
    /// <see cref="JobChangeSetDto.HeadSha"/> is served only a change set computed from that exact pair
    /// (see <see cref="CacheKey"/>). A cache entry can therefore never contradict the SHAs printed
    /// above the diff, which is the invariant that makes the numbers defensible.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan SuccessCacheLifetime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a failed read is remembered.
    /// </summary>
    /// <remarks>
    /// Short on purpose, and shorter than the success lifetime: a checkout mid-<c>git-add</c> can
    /// refuse for one second, and a user who hits refresh should get a real answer rather than a
    /// cached "git is busy". It exists at all because a source that is missing outright (no checkout,
    /// no credentials) would otherwise be re-probed on every keystroke.
    /// </remarks>
    public static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Ceiling on distinct change sets held in memory per API replica.
    /// </summary>
    /// <remarks>
    /// Bounded because the API is a long-lived server process serving many jobs, and an unbounded
    /// dictionary keyed by job is a slow memory leak dressed up as a cache. Eviction is oldest-first by
    /// insertion, which is fine for a ten-second cache: a stale-by-seconds entry that gets evicted
    /// early costs one recomputation, while an unbounded one costs a restart.
    /// <para>
    /// This is a per-replica cache, which is correct because every entry is keyed by the identity of
    /// the thing being measured (workspace root plus the resolved SHAs), not by which replica computed
    /// it. A <c>?refresh=true</c> request that lands on a different replica simply recomputes; nothing
    /// depends on the two agreeing within the cache lifetime, and the SHAs in the response tell the
    /// reader which commit they are looking at.
    /// </para>
    /// </remarks>
    public const int CacheEntryLimit = 64;

    /// <summary>
    /// The cache key for one measurement: the checkout, the branch pair, and the caller's pin.
    /// </summary>
    /// <remarks>
    /// <see cref="JobChangeSourceInfo"/> is <em>not</em> part of the key, and a remote comparison is
    /// kept in a separate namespace from a local read (the <c>local|</c>/<c>remote|</c> prefix
    /// <c>kind</c> argument): the two describe different states of the same branch — one includes
    /// uncommitted work, one cannot — and sharing a key between them would let a remote answer satisfy
    /// a local request.
    /// </remarks>
    public static string CacheKey(
        string kind,
        string workspaceRootOrRepository,
        string branch,
        string? baseBranch,
        string? pinnedBaseSha,
        string? pinnedHeadSha,
        int contextLines,
        string? path)
    {
        var scope = kind == JobChangesSources.LocalGit ? "local" : kind == JobChangesSources.GitHubCompare ? "remote" : "other";

        return string.Join('|',
            scope,
            workspaceRootOrRepository.Length == 0 ? "-" : workspaceRootOrRepository,
            branch.Length == 0 ? "-" : branch,
            string.IsNullOrWhiteSpace(baseBranch) ? "-" : baseBranch,
            string.IsNullOrWhiteSpace(pinnedBaseSha) ? "-" : pinnedBaseSha,
            string.IsNullOrWhiteSpace(pinnedHeadSha) ? "-" : pinnedHeadSha,
            contextLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(path) ? "ALL" : path);
    }

    /// <summary>Clamps a caller's file-count request into <see cref="DefaultFileListLimit"/>.</summary>
    public static int ResolveFileLimit(int? requested) =>
        requested is null or <= 0
            ? DefaultFileListLimit
            : Math.Clamp(requested.Value, 1, MaxFileListLimit);

    /// <summary>
    /// The empty answer, with the reason nothing could be read.
    /// </summary>
    /// <remarks>
    /// <c>200</c> with this shape is a legitimate answer for a job that has not run; a
    /// <c>404</c>/<c>500</c> for that case is what makes a panel show a red error box instead of
    /// "nothing yet, and here is why". When the job itself does not exist the endpoint does return
    /// <c>404</c> — that is a different fact and it belongs on the route, not in here.
    /// </remarks>
    public static JobChangeSetDto Unavailable(
        Guid jobId,
        string? branch,
        string? baseBranch,
        string reasonCode,
        string message,
        bool degraded = false,
        IReadOnlyList<string>? notes = null) =>
        new(jobId, branch, baseBranch, null, null,
            new JobChangeStats(0, 0, 0),
            Array.Empty<JobChangedFileDto>(),
            new JobChangeSourceInfo(null, reasonCode, message, Degraded: degraded),
            Array.Empty<JobChangeCommitDto>(),
            HasUncommittedChanges: false,
            UncommittedCount: 0,
            GeneratedAt: DateTimeOffset.UtcNow,
            Notes: notes is { Count: > 0 } ? notes : null);
}
