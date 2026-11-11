using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Services;

namespace AgentMesh.Api.Services;

/// <summary>
/// Reads a job's change set and patches from a checkout this process can see, using <c>git</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The base is the fork point, always.</strong> <c>git diff base...head</c> (three dots)
/// compares from the merge base; this class resolves that merge base once
/// (<see cref="ResolvePairAsync"/>) and then uses two-dot form everywhere else, so the list, the
/// stats, the commit roll-up and every patch provably describe the same pair — and the pair is
/// reported as <see cref="JobChangeSetDto.BaseSha"/> so a later patch request can pin it. Diffing
/// against the base branch's <em>tip</em> instead is how a job branch that has been alive for a week
/// shows a reviewer 400 files of other people's merged work as its own change: a confident lie about
/// blast radius, which is worse than showing nothing.
/// </para>
/// <para>
/// <strong>Refs are validated before they reach an argument vector.</strong> Two separate reasons.
/// A rev specification placed in an argument position can look like an option (<c>--output=x</c>), and
/// <c>:path</c>/<c>^rev</c>/<c>@{...}</c> forms resolve to objects that are not a branch at all, so
/// the response would mislabel a blob as "the branch". And a branch name typed into the New Job modal
/// that happens to begin with a dash would otherwise silently become a git option. Neither is a
/// security incident at this trust level — an authenticated caller with a job id can already ask for a
/// diff — but a refusal with <see cref="JobChangeUnavailableReasons.UnknownRef"/> is a comprehensible
/// panel state while an arbitrary git option is an undefined one. <c>_logger</c> records the refusal.
/// </para>
/// <para>
/// <strong>Uncommitted work is included and labelled.</strong> Two states exist in a runner's tree
/// that no committed comparison can show: edits to tracked files that are not committed, and files new
/// enough to be absent from the index. The first arrives from <c>git status --porcelain</c> merged over
/// the committed rows; the second additionally gets a patch on demand via
/// <c>git diff --no-index -- /dev/null &lt;path&gt;</c>, since no tree comparison will ever list an
/// untracked file. Listing only committed work is what made the pre-existing checkpoint file list feel
/// nearly right and not actually right: it names files, this names hunks.
/// </para>
/// <para>
/// <strong>When the checkout is on another branch, this source refuses.</strong> The alternative —
/// diffing <c>HEAD</c> and labelling the result with the job's branch — is the failure the workspace
/// resolver documents for the PR tool: commands succeed against the wrong tree and no error surfaces
/// anywhere. Refusal with <see cref="JobChangeUnavailableReasons.BranchMismatch"/> lets the caller fall
/// through to the pushed branch, which is at least a statement about the right work.
/// </para>
/// <para>
/// <strong>Committed stats and a worktree status are two different measurements, and they are kept
/// apart on purpose.</strong> Line counts come from <c>--numstat</c> between the fork point and
/// <c>HEAD</c>; the uncommitted indicator comes from <c>status</c>. Producing worktree-inclusive line
/// totals would need a second full diff of a live tree per panel open, and a total that silently mixes
/// "committed" and "uncommitted" lines cannot be reconciled against the PR the reader opens next to it.
/// </para>
/// </remarks>
public sealed class JobChangesGitSource(
    IJobGitCommandRunner git,
    ILogger<JobChangesGitSource>? logger = null)
{
    /// <summary>
    /// The git subcommands this class will run, and the only ones it will run.
    /// </summary>
    /// <remarks>
    /// Read-only by construction, enforced in <see cref="RunAsync"/>. The directory being inspected is
    /// a runner's live checkout mid-turn: a <c>checkout</c>, <c>clean</c>, <c>add</c> or
    /// <c>stash</c> issued from a control-plane read would destroy work in progress with nothing to
    /// undo it. Listing the permitted commands here means widening that set is a visible edit in
    /// review rather than a consequence of someone passing a verb through a helper.
    /// </remarks>
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.Ordinal)
    {
        "diff", "status", "log", "rev-parse", "merge-base",
    };


    /// <summary>git's empty-tree object id, present in every repository by construction.</summary>
    /// <remarks>
    /// Used as the base when the job branch shares no history with any candidate base (a shallow clone
    /// or an unrelated import), which makes "every file is added" a computed answer rather than a
    /// guess, and reports <see cref="JobChangeSetDto.BaseSha"/> as that tree so the panel can say so.
    /// </remarks>
    public const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>What <c>--no-index</c> names the empty side of a one-sided comparison.</summary>
    private const string DevNull = "/dev/null";

    private readonly ILogger? _logger = logger;

    /// <summary>
    /// The change set for <paramref name="branch"/> against the fork point, read from
    /// <paramref name="workspaceRoot"/>.
    /// </summary>
    /// <param name="workspaceRoot">The checkout to read.</param>
    /// <param name="workspaceProvenance">Which locator rule picked it, for the source note.</param>
    /// <param name="branch">The job's branch.</param>
    /// <param name="baseBranch">The project's base branch, or null to try the usual candidates.</param>
    /// <param name="options">Limits and diff knobs.</param>
    /// <param name="ct">Request cancellation.</param>
    /// <exception cref="JobChangeUnavailableException">
    /// This checkout cannot answer: the ref is absent, the tree is on a different branch, or git would
    /// not run. The reason code is what the response carries forward.
    /// </exception>
    public async Task<JobChangeSetDto> ReadAsync(
        string workspaceRoot,
        string workspaceProvenance,
        string branch,
        string? baseBranch,
        JobChangesOptions options,
        CancellationToken ct = default)
    {
        var headRef = RequireRef(branch);
        var pair = await ResolvePairAsync(workspaceRoot, headRef, baseBranch, ct);

        var nameStatus = await RunAsync(workspaceRoot,
            DiffArgs("--name-status", "-z", pair.BaseSha, pair.HeadSha), ct);
        var numstat = await RunAsync(workspaceRoot,
            DiffArgs("--numstat", "-z", pair.BaseSha, pair.HeadSha), ct);

        // `git diff` exits 1 when it found differences and 0 when it did not; an empty payload with
        // exit 0 is "this branch changed nothing", which is an answer. Anything else is a failure.
        // Exit 128 in particular means a bad rev, which ResolvePairAsync has already ruled out.
        if (nameStatus.OutputTruncated || numstat.OutputTruncated)
        {
            throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                "This checkout's diff is larger than the control plane reads; the pushed branch is the smaller question.");
        }

        if (!nameStatus.Success && nameStatus.ExitCode != 1)
        {
            throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                $"git diff failed: {nameStatus.ExplainFailure()}");
        }

        var committed = GitChangesParser.ReadNameStatus(nameStatus.StdOut);
        var stats = GitChangesParser.ReadNumstat(numstat.StdOut);

        var statusResult = await RunAsync(workspaceRoot,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-color"], ct);

        var uncommitted = statusResult.Success
            ? GitChangesParser.ReadPorcelainStatus(statusResult.StdOut).ToList()
            : new List<ChangedFileEntry>();

        if (!statusResult.Success)
        {
            _logger?.LogDebug("git status in {Root} failed: {Detail}",
                workspaceRoot, statusResult.ExplainFailure());
        }

        // Committed rows first; an uncommitted row for the same path overrides the status, because the
        // tree's state is the more current fact and the reader is looking at what exists now.
        var byPath = new Dictionary<string, ChangedFileEntry>(StringComparer.Ordinal);
        foreach (var entry in committed)
        {
            byPath[entry.Path] = entry;
        }

        foreach (var entry in uncommitted)
        {
            if (byPath.TryGetValue(entry.Path, out var existing))
            {
                byPath[entry.Path] = existing with
                {
                    Status = entry.Status ?? existing.Status,
                    PreviousPath = existing.PreviousPath ?? entry.PreviousPath,
                };
            }
            else
            {
                byPath[entry.Path] = entry;
            }
        }

        var (files, fileStats, truncated) = JobChangeMerge.Join(
            byPath.Values.ToList(), stats, options.ResolveFileLimit(null));

        var commits = await ReadCommitsAsync(workspaceRoot, pair.BaseSha, pair.HeadSha, ct);

        var notes = new List<string>(pair.Notes);
        if (uncommitted.Count > 0)
        {
            notes.Add($"{uncommitted.Count} path(s) carry changes that are not committed on the branch.");
        }

        return new JobChangeSetDto(
            JobId: Guid.Empty,
            Branch: branch,
            BaseBranch: pair.BaseRef,
            HeadSha: pair.HeadSha,
            BaseSha: pair.BaseSha,
            Stats: fileStats,
            Files: files,
            Source: new JobChangeSourceInfo(
                JobChangesSources.LocalGit,
                ReasonCode: null,
                Message: $"git diff in the job's checkout ({workspaceProvenance}).",
                Degraded: false,
                CommittedOnly: false,
                Truncated: truncated),
            Commits: commits,
            HasUncommittedChanges: uncommitted.Count > 0,
            UncommittedPaths: uncommitted
                .Select(e => e.Path)
                .Distinct(StringComparer.Ordinal)
                .Take(JobChangesPolicy.UncommittedPathLimit)
                .ToList(),
            UncommittedCount: uncommitted.Count,
            GeneratedAt: DateTimeOffset.UtcNow,
            Notes: notes.Count > 0 ? notes : null);
    }

    /// <summary>
    /// One file's patch, or the whole change set's, from <paramref name="workspaceRoot"/>.
    /// </summary>
    /// <param name="path">The repository-relative path, or null for the whole change set.</param>
    /// <param name="previousPath">
    /// A rename's preimage. It must be passed as a second pathspec: verified against git 2.43, a
    /// per-file diff asked for by the destination alone prints the whole file as additions
    /// (<c>new file mode</c>, 60 lines for a two-line edit), while passing both prints
    /// <c>similarity index 95%</c> plus the real hunks. The list already carries the preimage, so
    /// forwarding it is free and turns a misleading patch into the correct one.
    /// </param>
    /// <param name="untracked">
    /// True when the path is absent from the index, which routes through <c>diff --no-index</c>: the
    /// only command that can produce a patch for a file no tree comparison will list. Its exit code is
    /// 1 by design when a difference is found, so success is judged by the payload rather than by the
    /// exit code.
    /// </param>
    /// <param name="baseSha">
    /// The fork point from the caller's list response. Honouring it is what keeps a patch comparable to
    /// the counts that were rendered a moment earlier; omitting it re-resolves, which can differ if the
    /// base branch moved in between.
    /// </param>
    /// <param name="headSha">The head side from the same list response.</param>
    /// <param name="maxPatchBytes">Truncation bound for the returned body.</param>
    public async Task<JobChangePatchDto> ReadPatchAsync(
        string workspaceRoot,
        string workspaceProvenance,
        string branch,
        string? baseBranch,
        string? path,
        string? previousPath,
        bool untracked,
        int contextLines,
        string? baseSha,
        string? headSha,
        int maxPatchBytes,
        CancellationToken ct = default)
    {
        var context = $"-U{contextLines}";

        if (untracked && path is not null)
        {
            var relative = RequireRelativePath(path);

            // `--` is what makes a filename beginning with a dash a filename rather than an option, and
            // it is required here because the path is caller-supplied.
            var oneSided = await RunAsync(workspaceRoot,
                ["diff", "--no-index", "--no-ext-diff", "--no-textconv", context, "--no-color",
                    "--", DevNull, relative], ct);

            var untrackedBody = JobGitResult.Sanitize(oneSided.StdOut);

            if (oneSided.ExitCode != 0 && oneSided.ExitCode != 1 && untrackedBody.Length == 0)
            {
                throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                    $"git could not read the untracked file: {oneSided.ExplainFailure()}");
            }

            return new JobChangePatchDto(
                JobId: Guid.Empty, Branch: branch, BaseBranch: baseBranch,
                HeadSha: headSha, BaseSha: baseSha,
                Path: relative, PreviousPath: null, Status: JobFileChangeKinds.Untracked,
                Binary: untrackedBody.Length == 0 || IsBinaryPatch(untrackedBody),
                Empty: untrackedBody.Length == 0,
                Patch: StripPatchNoise(untrackedBody, maxPatchBytes, out var untrackedTruncated),
                Truncated: untrackedTruncated,
                ContextLines: contextLines,
                Source: LocalSource(workspaceProvenance));
        }

        // The pathspec and the ref are validated before any process is spawned: rejecting a path that
        // could escape the repository costs nothing, while rejecting it after five git invocations
        // means a malformed request still pays for a full fork-point resolution in someone's live
        // checkout.
        var headRef = RequireRef(branch);
        // Bound to non-null locals rather than re-deriving below: `path is not null` does not make
        // `target` non-null in the compiler's eyes, and a `!` here would be a claim the reader has to
        // trust instead of a fact it can see in the two lines above.
        string? target = null;
        string? from = null;

        if (path is not null)
        {
            target = RequireRelativePath(path);
            from = string.IsNullOrWhiteSpace(previousPath) ? null : RequireRelativePath(previousPath);
        }

        var pair = await ResolvePairAsync(workspaceRoot, headRef, baseBranch, ct,
            pinnedHeadSha: headSha, pinnedBaseSha: baseSha);

        if (string.IsNullOrWhiteSpace(path))
        {
            var whole = await RunAsync(workspaceRoot,
                DiffArgs("--find-copies-harder", context, pair.BaseSha, pair.HeadSha), ct);

            if (whole.OutputTruncated)
            {
                throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                    "The whole-branch diff is larger than the control plane reads. Ask for one file.");
            }

            if (!whole.Success && whole.ExitCode != 1)
            {
                throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                    $"git diff failed: {whole.ExplainFailure()}");
            }

            var wholeBody = StripPatchNoise(JobGitResult.Sanitize(whole.StdOut), maxPatchBytes, out var wholeTruncated);

            return new JobChangePatchDto(
                JobId: Guid.Empty, Branch: branch, BaseBranch: pair.BaseRef,
                HeadSha: pair.HeadSha, BaseSha: pair.BaseSha,
                Path: null, PreviousPath: null, Status: null,
                Binary: false, Empty: wholeBody.Length == 0,
                Patch: wholeBody, Truncated: wholeTruncated,
                ContextLines: contextLines,
                Source: LocalSource(workspaceProvenance));
        }

        // `target` is non-null here: the null-path case returned the whole-change-set patch above.
        if (target is null)
        {
            throw new JobChangeUnavailableException(JobChangeUnavailableReasons.PathNotInChangeSet,
                "A file path is required for a per-file patch.");
        }

        var args = DiffArgs("--find-copies-harder", context, pair.BaseSha, pair.HeadSha);
        args.Add("--");
        args.Add(target);
        if (from is not null && !string.Equals(from, target, StringComparison.Ordinal))
        {
            args.Add(from);
        }

        var single = await RunAsync(workspaceRoot, args, ct);

        if (single.OutputTruncated)
        {
            throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                "This file's diff is larger than the control plane reads.");
        }

        if (!single.Success && single.ExitCode != 1)
        {
            throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                $"git diff failed: {single.ExplainFailure()}");
        }

        var patch = JobGitResult.Sanitize(single.StdOut);
        var isBinary = IsBinaryPatch(patch);
        var body = StripPatchNoise(isBinary ? string.Empty : patch, maxPatchBytes, out var truncated);

        return new JobChangePatchDto(
            JobId: Guid.Empty, Branch: branch, BaseBranch: pair.BaseRef,
            HeadSha: pair.HeadSha, BaseSha: pair.BaseSha,
            Path: target, PreviousPath: from, Status: null,
            Binary: isBinary, Empty: body.Length == 0,
            Patch: body, Truncated: truncated,
            ContextLines: contextLines,
            Source: LocalSource(workspaceProvenance));
    }

    /// <summary>
    /// The (base, head) commit pair every read in this class uses, resolved once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The head must be the job's branch, and the checkout must know it.</strong> A checkout
    /// that has no such ref cannot answer, and saying <c>unknown_ref</c> is what lets the caller use the
    /// pushed branch instead. The same check on <c>branch --show-current</c> is what refuses to answer
    /// from another branch's <c>HEAD</c> — see the class remarks.
    /// </para>
    /// <para>
    /// <strong>Base candidates, best first:</strong> <c>origin/&lt;base&gt;</c> before the local
    /// <c>&lt;base&gt;</c>. A runner's local base branch is exactly as current as its last fetch, while
    /// its remote-tracking ref is what it fetched for this turn; taking a fork point against a stale
    /// local base yields a <em>later</em> commit than the true fork point, which silently drops real
    /// changes from the diff — a wrong answer with no error, worth one extra <c>rev-parse</c> to avoid.
    /// The configured base is then tried before <c>main</c>/<c>master</c>, and a repository with no
    /// common ancestor falls back to <see cref="EmptyTreeSha"/> with a note that says so.
    /// </para>
    /// </remarks>
    internal async Task<(string HeadSha, string BaseSha, string BaseRef, IReadOnlyList<string> Notes)> ResolvePairAsync(
        string workspaceRoot,
        string headRef,
        string? baseBranch,
        CancellationToken ct,
        string? pinnedHeadSha = null,
        string? pinnedBaseSha = null)
    {
        var notes = new List<string>();

        var headSha = IsSha(pinnedHeadSha)
            ? pinnedHeadSha!
            : await ResolveRefAsync(workspaceRoot, headRef, ct)
              ?? throw new JobChangeUnavailableException(JobChangeUnavailableReasons.UnknownRef,
                    $"This checkout has no branch '{headRef}' — it was never fetched here, or it was deleted upstream.");

        // A pinned pair is honoured without re-deriving the fork point: the caller already knows which
        // two commits its file counts came from, and re-resolving could silently answer a different
        // question than the one whose numbers are on screen.
        if (IsSha(pinnedBaseSha) && IsSha(pinnedHeadSha))
        {
            return (headSha, pinnedBaseSha!, "(pinned by caller)", notes);
        }

        foreach (var candidate in BaseCandidateRefs(baseBranch))
        {
            var resolved = await ResolveRefAsync(workspaceRoot, candidate, ct);
            if (resolved is null) continue;

            var mergeBase = await MergeBaseAsync(workspaceRoot, resolved, headSha, ct);
            if (mergeBase != null)
            {
                return (headSha, mergeBase, candidate, notes);
            }

            notes.Add($"'{candidate}' shares no history with '{headRef}', so every file is listed as added.");
            return (headSha, EmptyTreeSha, candidate + " (no common ancestor)", notes);
        }

        throw new JobChangeUnavailableException(JobChangeUnavailableReasons.UnknownRef,
            "No base branch could be resolved in this checkout, so there is nothing to compare against.");
    }

    /// <summary>
    /// The refs to try as the base, best first, with the configured base tried under two names.
    /// </summary>
    internal static IReadOnlyList<string> BaseCandidateRefs(string? baseBranch)
    {
        var names = new List<string>();
        var configured = string.IsNullOrWhiteSpace(baseBranch) ? null : baseBranch.Trim();

        if (configured != null && TryRef(configured) != null)
        {
            names.Add($"origin/{configured}");
            names.Add(configured);
        }

        foreach (var fallback in new[] { "main", "master" })
        {
            names.Add($"origin/{fallback}");
            names.Add(fallback);
        }

        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<IReadOnlyList<JobChangeCommitDto>> ReadCommitsAsync(
        string workspaceRoot, string baseSha, string headSha, CancellationToken ct)
    {
        var result = await RunAsync(workspaceRoot,
            GitChangesParser.CommitLogArguments($"{baseSha}..{headSha}", JobChangesPolicy.CommitListLimit), ct);

        if (!result.Success)
        {
            // The commit roll-up is the least important part of the response and its failure says
            // nothing about the file list, so it degrades to empty rather than to an error.
            _logger?.LogDebug("git log for {Base}..{Head} failed: {Detail}",
                Shorten(baseSha), Shorten(headSha), result.ExplainFailure());
            return Array.Empty<JobChangeCommitDto>();
        }

        return GitChangesParser.ReadCommitRecords(result.StdOut);
    }

    private async Task<string?> ResolveRefAsync(string workspaceRoot, string refName, CancellationToken ct)
    {
        var result = await RunAsync(workspaceRoot,
            ["rev-parse", "--verify", "--quiet", $"{refName}{{commit}}"], ct);

        if (!result.Success) return null;

        var sha = result.StdOut.Trim();
        return IsSha(sha) ? sha : null;
    }

    private async Task<string?> MergeBaseAsync(string workspaceRoot, string left, string right, CancellationToken ct)
    {
        var result = await RunAsync(workspaceRoot, ["merge-base", left, right], ct);
        if (!result.Success) return null;

        var sha = result.StdOut.Split('\n')[0].Trim();
        return IsSha(sha) ? sha : null;
    }

    private static List<string> DiffArgs(params string[] extras)
    {
        var args = new List<string> { "diff", "--find-renames", "--no-ext-diff", "--no-textconv", "--no-color" };

        // `--find-renames` before the revs, `--` never needed for the revs themselves because they are
        // SHAs by the time they get here.
        foreach (var extra in extras)
        {
            if (!string.IsNullOrEmpty(extra)) args.Add(extra);
        }

        return args;
    }

    private static JobChangeSourceInfo LocalSource(string workspaceProvenance) =>
        new(JobChangesSources.LocalGit, null, $"git diff in the job's checkout ({workspaceProvenance}).");

    /// <summary>True when git replaced a patch with its "Binary files … differ" line.</summary>
    private static bool IsBinaryPatch(string patch) =>
        patch.Contains("Binary files ", StringComparison.Ordinal) ||
        patch.Contains("GIT binary patch", StringComparison.Ordinal);

    /// <summary>
    /// Trims a patch to the byte limit and drops git's index-line noise from a whole-branch body.
    /// </summary>
    /// <remarks>
    /// Truncation cuts on a hunk boundary rather than mid-line where possible: a patch that ends inside
    /// a hunk renders as a corrupt diff in most viewers, while one that ends at a <c>diff --git</c>
    /// boundary renders as a partial file list with the truncation flag explaining why. Cutting at the
    /// last <c>diff --git</c> before the limit is the difference between "here is most of the change"
    /// and "here is something a diff parser rejects".
    /// </remarks>
    internal static string StripPatchNoise(string patch, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (patch.Length == 0 || maxBytes <= 0) return patch;

        if (patch.Length <= maxBytes) return patch;

        truncated = true;

        // Scan the whole patch for record starts and keep the last one that begins inside the window.
        // Searching for the needle inside the truncated prefix instead is wrong by up to one header's
        // length: a cut that lands four characters into the next "diff --git" line has no complete
        // needle left of the cut to find, so it falls back to a hard cut mid-line — which is exactly the
        // corrupt-diff output this method exists to avoid.
        // Walking every boundary rather than stopping at the first: the loop's exit condition has to
        // be "this start is past the window", while "skipping index 0" is a *separate* rule, because a
        // cut at 0 would return an empty patch and read as "this file changed nothing". Conflating the
        // two by testing `at > 0` as the loop condition makes the walk end before it starts — which is
        // exactly what it did, so the "cut on a boundary" rule never fired and a patch ending four
        // characters into the next header was returned as though it were a valid partial diff.
        const string boundary = "diff --git ";

        var lastBoundary = 0;
        for (var at = patch.IndexOf(boundary, StringComparison.Ordinal);
             at >= 0;
             at = patch.IndexOf(boundary, at + 1, StringComparison.Ordinal))
        {
            if (at > maxBytes) break; // the previous boundary was the last usable one
            if (at == 0) continue;    // a cut at 0 is an empty patch, not a partial one
            lastBoundary = at;
        }

        // If even the first file's body exceeds the limit there is no boundary to cut on, so fall back
        // to a hard cut: an empty result would read as "this file changed nothing", which is the one
        // wrong answer that looks right.
        return lastBoundary > 0 ? patch[..lastBoundary].TrimEnd('\n') : patch[..maxBytes];
    }

    /// <summary>
    /// Validates a ref name, or null when it cannot be used as one.
    /// </summary>
    /// <remarks>
    /// Rules that matter: no leading dash and no embedded <c>--</c> (option injection), no
    /// <c>:</c>/<c>?</c>/<c>*</c>/<c>[</c> (a rev can be a glob or a blob path), no <c>^</c>/<c>~</c>
    /// (rev traversal the response would mislabel), no <c>@{</c> (a reflog selector resolves to an
    /// object the caller did not ask for), no <c>..</c> (a range is two revs), no whitespace or control
    /// characters, and a length ceiling. A rejected name is reported as an unusable ref: it is not an
    /// attack, it is a job whose branch name cannot be diffed, and the message says which.
    /// </remarks>
    internal static string? TryRef(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var value = candidate.Trim();
        if (value.Length is 0 or > 200) return null;
        if (value[0] is '-' or '~' or '^' or '@' or '+' or '.') return null;
        if (value.EndsWith('/') || value.EndsWith('.') || value.EndsWith(".lock", StringComparison.Ordinal)) return null;

        if (value.Contains("--", StringComparison.Ordinal) ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal) ||
            value.Contains("/: ", StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        return value.IndexOfAny([':', '?', '*', '[', ']', '^', '~', ' ', '\t', '\n', '\r', '\0']) >= 0
            ? null
            : value;
    }

    private static string RequireRef(string value) =>
        TryRef(value) ?? throw new JobChangeUnavailableException(
            JobChangeUnavailableReasons.UnknownRef,
            $"'{JobGitResult.Sanitize(value)}' cannot be used as a branch name for a diff.");

    /// <summary>
    /// Validates a repository-relative path before it becomes a pathspec.
    /// </summary>
    /// <remarks>
    /// A pathspec is still an argument to a process running in someone's checkout, so a
    /// <c>..</c> segment is rejected outright rather than normalized away: git resolves pathspecs
    /// relative to the repository root and would happily match a tracked file outside the intended
    /// directory, which is a change report about someone else's work.
    /// </remarks>
    internal static string RequireRelativePath(string value)
    {
        var normalized = GitChangesParser.NormalizeRelativePath(value);

        if (normalized.Length == 0 || !JobChangeMerge.IsSafeRelativePath(normalized))
        {
            throw new JobChangeUnavailableException(JobChangeUnavailableReasons.PathNotInChangeSet,
                $"'{JobGitResult.Sanitize(value)}' is not a path inside the repository.");
        }

        return normalized;
    }

    private static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static string Shorten(string sha) => sha[..Math.Min(8, sha.Length)];

    /// <summary>
    /// True when <paramref name="command"/> is one the change reader may run.
    /// </summary>
    /// <remarks>
    /// Exposed as a predicate rather than left inside <see cref="RunAsync"/> so the guard is testable.
    /// It is defence-in-depth: every argument vector this class builds is already read-only, and the
    /// enumeration test would fail if a write verb were ever added, so nothing reachable today can
    /// observe the guard. That is exactly why it needed a name — an unobservable safety check that no
    /// test can reach is a comment with a <c>Contains</c> call in it, and a regression in
    /// <see cref="AllowedCommands"/> (a typo, a well-meant "while we're here" addition) would have been
    /// invisible. One internal method makes the invariant assertable without widening the public API.
    /// </remarks>
    internal static bool IsAllowedCommand(string? command) =>
        !string.IsNullOrEmpty(command) && AllowedCommands.Contains(command);

    private async Task<JobGitResult> RunAsync(string workspaceRoot, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var command = arguments.Count > 0 ? arguments[0] : string.Empty;

        if (!IsAllowedCommand(command))
        {
            // A programming error, not a runtime condition: it must never be reachable from user input,
            // and reaching it means someone added a call site without deciding whether the change
            // reader is allowed to run that command at all.
            _logger?.LogError("Refusing to run git {Command} from the change reader", command);
            throw new JobChangeUnavailableException(JobChangeUnavailableReasons.GitUnavailable,
                $"'{(command.Length > 0 ? command : "(none)")}' is not a command the change reader may run.");
        }

        var result = await git.RunAsync(workspaceRoot, arguments, ct);

        if (result.TimedOut || result.OutputTruncated)
        {
            _logger?.LogWarning("git {Command} in {Root} returned {Reason}",
                command, workspaceRoot, result.TimedOut ? "a timeout" : "an oversized payload");
        }

        return result;
    }
}
