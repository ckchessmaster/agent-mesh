using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// The status vocabulary a job's change set uses, in the words the panel shows.
/// </summary>
/// <remarks>
/// <para>
/// Git's porcelain letters are the input, these strings are the output, and
/// <c>GitChangesParser</c> is the only translator. The panel colours a row from this value, so the set
/// is closed on purpose: a status git can produce that has no mapping is passed through verbatim
/// rather than coerced to <c>modified</c>, which keeps an unexpected letter visible and diagnosable
/// instead of quietly plausible.
/// </para>
/// <para>
/// <see cref="Untracked"/> is the one value that no <c>git diff</c> will ever report — an untracked
/// file is not in the index, so no tree comparison can see it. It arrives from
/// <c>git status --porcelain</c>, and it exists here because "the agent created this file and has not
/// committed it" is a state a reviewer needs to see distinguished from "the agent committed this
/// file".
/// </para>
/// </remarks>
public static class JobFileChangeKinds
{
    public const string Added = "added";
    public const string Modified = "modified";
    public const string Deleted = "deleted";
    public const string Renamed = "renamed";
    public const string Copied = "copied";
    public const string TypeChanged = "typeChanged";
    public const string Untracked = "untracked";
    public const string Ignored = "ignored";
}

/// <summary>
/// One path as reported by one plumbing command, before the status list and the stats list are joined.
/// </summary>
/// <remarks>
/// A struct-of-optional-fields rather than two parallel lists because the join is positional and the
/// commands do not agree on what they name: <c>--name-status</c> owns the status,
/// <c>--numstat</c> owns the counts, and a rename's pair must survive the join or the row renders as a
/// deletion of a file that was actually moved.
/// </remarks>
/// <param name="Path">Repository-relative path, normalized by <c>GitChangesParser.NormalizeRelativePath</c>.</param>
/// <param name="Status">A <see cref="JobFileChangeKinds"/> value, or null when this record carries only stats.</param>
/// <param name="PreviousPath">The preimage for a rename or copy.</param>
/// <param name="Additions">Lines added, or null when the source cannot count them.</param>
/// <param name="Deletions">Lines deleted, or null when the source cannot count them.</param>
/// <param name="Binary">True when git refused to count because the content is binary.</param>
public readonly record struct ChangedFileEntry(
    string Path,
    string? Status = null,
    string? PreviousPath = null,
    int? Additions = null,
    int? Deletions = null,
    bool Binary = false);

/// <summary>
/// Joins and reduces the plumbing payloads into the shape the API returns.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The status list leads and the stats list is joined onto it.</strong> Not the reverse:
/// <c>--numstat</c> does not report a status, and a file that appears only in the stats list means the
/// two commands disagreed, which is worth showing as a row with counts and an inferred status rather
/// than dropping. Dropping it would understate <c>filesChanged</c> — the number a reviewer uses to
/// decide how carefully to read.
/// </para>
/// <para>
/// <strong>Truncation never touches the totals.</strong> When the file list is capped,
/// <see cref="JobChangeStats.Additions"/> and <see cref="JobChangeStats.Deletions"/> are still summed
/// over every path, because the line totals are the answer to "how big is this change" and a capped
/// list is only an answer to "show me some files". A capped total would be the misleading version of
/// the same number.
/// </para>
/// </remarks>
public static class JobChangeMerge
{
    /// <summary>
    /// Combines one status payload with one stats payload into rows, sorted and capped.
    /// </summary>
    /// <param name="statusEntries">Rows from <c>--name-status -z</c> (or porcelain). May be empty.</param>
    /// <param name="statEntries">Rows from <c>--numstat -z</c>. May be empty.</param>
    /// <param name="limit">Maximum rows to return; 0 or less means no cap.</param>
    /// <param name="patchAvailable">
    /// False when the source describes paths without being able to diff them — which is only the
    /// working-checkpoint fallback. A row that offers no patch still belongs in the list, because
    /// "these files changed" is true and useful even when "here is how" is not available.
    /// </param>
    public static (IReadOnlyList<JobChangedFileDto> Files, JobChangeStats Stats, bool Truncated) Join(
        IReadOnlyList<ChangedFileEntry> statusEntries,
        IReadOnlyList<ChangedFileEntry> statEntries,
        int limit = 0,
        bool patchAvailable = true)
    {
        var statsByPath = new Dictionary<string, ChangedFileEntry>(StringComparer.Ordinal);
        foreach (var stat in statEntries)
        {
            if (stat.Path.Length == 0) continue;
            statsByPath[stat.Path] = stat;
        }

        var rows = new List<JobChangedFileDto>(Math.Max(statusEntries.Count, statEntries.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in statusEntries)
        {
            if (entry.Path.Length == 0 || !seen.Add(entry.Path)) continue;

            // ChangedFileEntry is a value type, so a miss leaves `stat` at its default rather than
            // null. `found` is what distinguishes "no stats row for this path" from "a stats row whose
            // counts are null", which matters because the first is an unknown and the second is a
            // binary file — and only the second should suppress a row's counts to null.
            var found = statsByPath.TryGetValue(entry.Path, out var statValue);
            ChangedFileEntry? stat = found ? statValue : null;

            var additions = entry.Additions ?? stat?.Additions;
            var deletions = entry.Deletions ?? stat?.Deletions;
            var binary = entry.Binary || stat?.Binary == true;

            rows.Add(new JobChangedFileDto(
                Path: entry.Path,
                Status: entry.Status ?? JobFileChangeKinds.Modified,
                PreviousPath: entry.PreviousPath ?? stat?.PreviousPath,
                Additions: binary ? null : additions,
                Deletions: binary ? null : deletions,
                Binary: binary,
                // Every status has a patch when the source can produce one: a deletion diffs to a
                // removal, an untracked file diffs against /dev/null, and a rename detected with no
                // content edits diffs to an empty body that the viewer renders as "no changes". None
                // of those is a reason to offer no link at all.
                PatchAvailable: patchAvailable));
        }

        // Paths the stats payload saw but the status payload did not: kept, with counts and an
        // inferred status, so the totals still match what the reader will see on GitHub.
        foreach (var stat in statEntries)
        {
            if (stat.Path.Length == 0 || !seen.Add(stat.Path)) continue;

            rows.Add(new JobChangedFileDto(
                Path: stat.Path,
                Status: stat.PreviousPath is null ? JobFileChangeKinds.Modified : JobFileChangeKinds.Renamed,
                PreviousPath: stat.PreviousPath,
                Additions: stat.Binary ? null : stat.Additions,
                Deletions: stat.Binary ? null : stat.Deletions,
                Binary: stat.Binary,
                PatchAvailable: patchAvailable));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var totalAdditions = Sum(rows, r => r.Additions);
        var totalDeletions = Sum(rows, r => r.Deletions);

        var truncated = limit > 0 && rows.Count > limit;
        var returned = truncated ? rows.GetRange(0, limit) : rows;

        return (returned,
            new JobChangeStats(rows.Count, totalAdditions, totalDeletions, truncated ? rows.Count - returned.Count : 0),
            truncated);
    }

    /// <summary>Sums a nullable column, treating null as "not countable" rather than as zero.</summary>
    public static int Sum<T>(IEnumerable<T> rows, Func<T, int?> selector)
    {
        var total = 0;
        foreach (var value in rows.Select(selector))
        {
            if (value.HasValue) total += value.Value;
        }

        return total;
    }

    /// <summary>
    /// Drops rows whose path escapes the repository, defensively.
    /// </summary>
    /// <remarks>
    /// git will not print such a path for a tracked file, but the path is joined onto a workspace root
    /// before a file is read, so the filter stays even though the parser is expected to be the only
    /// producer. Cheap, and it means a parser regression cannot become a file-read primitive.
    /// </remarks>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        if (path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal)) return false;
        if (Path.IsPathRooted(path)) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment == ".." || segment == ".") return false;
            if (segment.Length == 0) continue;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }

        return true;
    }
}
