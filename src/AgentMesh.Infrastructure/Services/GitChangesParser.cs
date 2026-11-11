using System.Text;
using System.Text.Json;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Infrastructure.Services;

/// <summary>
/// Reads <c>git</c> plumbing output into typed records, using no line breaks as delimiters.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the parser is separate from the reader.</strong> This is the part of the feature whose
/// mistakes would be silent. A plumbing error — a rename whose second path is swallowed, a quoted
/// path left quoted — yields a change set that renders perfectly and names the wrong files. So every
/// parsing test runs against captured bytes (spaces, tabs and newlines inside a filename, octal
/// escapes, binary stats, a rename's path pair, an empty payload) rather than against a real
/// repository: a fixture repo would need git on PATH, would drift with its version, and could not
/// produce the adversarial names that decide whether the field boundaries are real.
/// </para>
/// <para>
/// <strong>NUL-delimited, not line-delimited.</strong> <c>--name-status -z</c>, <c>--numstat -z</c>
/// and <c>status --porcelain -z</c> separate paths with NUL bytes and disable git's C-style quoting.
/// A line-based parser must undo that quoting and still cannot represent a filename containing a
/// newline; <c>-z</c> can, and deleting the quoting step deletes its bugs too.
/// </para>
/// <para>
/// <strong>The record strides differ per command and were verified against git 2.43, not assumed.</strong>
/// <list type="bullet">
/// <item><description>
/// <c>--name-status -z --find-renames</c>: <c>M</c> NUL <c>path</c> NUL for an ordinary change, but
/// <c>R097</c> NUL <c>from</c> NUL <c>to</c> NUL for a rename. <strong>The similarity score is part of
/// the status field, not its own field</strong> — verified against git 2.43's bytes and pinned by
/// <c>GitOutputContractTests</c>. Assuming a separate score field costs two things at once: the
/// rename's own destination is read from the wrong slot, and the stride is off by one, so every
/// record after it is misread and a status letter is filed as a filename. That is why the stride is
/// derived from the status <em>letter</em> alone and the digits after it are ignored.
/// </description></item>
/// <item><description>
/// <c>--numstat -z --find-renames</c>: <c>added</c> TAB <c>deleted</c> TAB <c>path</c> NUL, but for a
/// rename the third column is empty and the pair follows as two more NUL-separated fields. This is
/// why <see cref="ReadNumstat"/> walks with a variable stride too, and why it keys a rename record on
/// its destination: that is the path <see cref="ReadNameStatus"/> reports, and the two lists are
/// joined on it.
/// </description></item>
/// <item><description>
/// <c>status --porcelain=v1 -z</c>: <c>XY&lt;space&gt;path</c> NUL, with a rename's source following
/// as its own field — destination first, the reverse of the diff's order.
/// </description></item>
/// <item><description>
/// <c>diff --no-index --name-status -z -- /dev/null &lt;path&gt;</c>: a plain <c>A</c> NUL
/// <c>path</c> NUL, which is what lets an untracked file be described with the same vocabulary as a
/// tracked one. Its exit code is 1 by design (a difference was found), which is why the caller's
/// success test is payload-based rather than exit-code-based.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public static class GitChangesParser
{
    /// <summary>The literal git writes in both count columns of <c>--numstat</c> for a binary file.</summary>
    public const string BinaryStatMarker = "-";

    /// <summary>What <c>--no-index</c> calls the empty side of a one-sided comparison.</summary>
    public const string NoIndexEmptySide = "/dev/null";

    /// <summary>
    /// Parses <c>git diff --find-renames --name-status -z</c> into paths with their status.
    /// </summary>
    /// <remarks>
    /// Git's letters map onto the words the panel shows. An unrecognised letter is passed through
    /// verbatim instead of being coerced to <c>modified</c>: git adds statuses over time and a merge
    /// diff produces <c>U</c>, so a row showing a raw letter is diagnosable while a row showing a
    /// plausible wrong word is not.
    /// </remarks>
    public static IReadOnlyList<ChangedFileEntry> ReadNameStatus(string? payload)
    {
        var entries = new List<ChangedFileEntry>();
        if (string.IsNullOrEmpty(payload)) return entries;

        var fields = payload.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var status = fields[i];
            if (string.IsNullOrEmpty(status)) continue; // the payload's trailing NUL

            var letter = status[0];
            string path;
            string? previous = null;

            if (letter is 'R' or 'C')
            {
                // [from, to] follow the status: the similarity score is *inside* the status field
                // ("R097"), not its own field. A truncated tail is dropped rather than guessed at.
                if (i + 2 >= fields.Length) break;
                previous = CleanPrevious(fields[i + 1]);
                path = NormalizeRelativePath(fields[i + 2]);
                i += 2;
            }
            else
            {
                if (i + 1 >= fields.Length) break;
                path = NormalizeRelativePath(fields[i + 1]);
                i += 1;
            }

            if (path.Length == 0) continue;
            entries.Add(new ChangedFileEntry(path, MapStatusLetter(letter, status), previous));
        }

        return entries;
    }

    /// <summary>
    /// Parses <c>git diff --find-renames --numstat -z</c> into per-file line counts.
    /// </summary>
    /// <remarks>
    /// A <c>-</c> in a count column is preserved as <see cref="ChangedFileEntry.Binary"/> with null
    /// counts rather than as zero: "this is a PNG" and "this changed by nothing" are different facts,
    /// and a panel that adds zeros is reporting a total that no longer matches GitHub's.
    /// <para>
    /// Unparseable counts (a git build that changes the column format) become nulls, and the caller
    /// reports "no stats" rather than a confident zero.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChangedFileEntry> ReadNumstat(string? payload)
    {
        var entries = new List<ChangedFileEntry>();
        if (string.IsNullOrEmpty(payload)) return entries;

        var fields = payload.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var record = fields[i];
            if (string.IsNullOrEmpty(record)) continue;

            // Split into at most three parts: a path may contain tabs, and git does not escape them
            // in -z mode. Everything after the second tab therefore belongs to the path.
            var first = record.IndexOf('\t');
            if (first < 0) continue;
            var second = record.IndexOf('\t', first + 1);
            if (second < 0) continue;

            var added = ParseStat(record[..first]);
            var deleted = ParseStat(record[(first + 1)..second]);
            var tail = record[(second + 1)..];

            string path;
            string? previous = null;

            if (tail.Length == 0)
            {
                // A rename or copy: the preimage and postimage are the next two fields.
                if (i + 2 >= fields.Length) break;
                previous = CleanPrevious(fields[i + 1]);
                path = NormalizeRelativePath(fields[i + 2]);
                i += 2;
            }
            else
            {
                path = NormalizeRelativePath(tail);
            }

            if (path.Length == 0) continue;

            entries.Add(new ChangedFileEntry(
                path,
                Status: null,
                PreviousPath: previous,
                Additions: added < 0 ? null : added,
                Deletions: deleted < 0 ? null : deleted,
                Binary: added < 0 || deleted < 0));
        }

        return entries;
    }

    /// <summary>
    /// Parses <c>git status --porcelain=v1 -z --untracked-files=all</c>.
    /// </summary>
    /// <remarks>
    /// Feeds the "work in this tree that is not on the branch yet" indicator and the path list an
    /// untracked-file patch is requested for. Porcelain's rename record is
    /// <c>XY&lt;space&gt;destination</c> NUL <c>source</c> NUL — the source is a trailing field, the
    /// opposite of the diff's order, which is exactly the asymmetry worth absorbing once.
    /// </remarks>
    public static IReadOnlyList<ChangedFileEntry> ReadPorcelainStatus(string? payload)
    {
        var entries = new List<ChangedFileEntry>();
        if (string.IsNullOrEmpty(payload)) return entries;

        var fields = payload.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var record = fields[i];
            if (record.Length < 4) continue; // needs "XY<space>at-least-one-character"

            var index = record[0];
            var worktree = record[1];
            var path = NormalizeRelativePath(record.Substring(3));
            if (path.Length == 0) continue;

            string? previous = null;
            if (index is 'R' or 'C' || worktree is 'R' or 'C')
            {
                if (i + 1 < fields.Length)
                {
                    previous = CleanPrevious(fields[i + 1]);
                    i++;
                }
            }

            entries.Add(new ChangedFileEntry(path, MapPorcelain(index, worktree), previous));
        }

        return entries;
    }

    /// <summary>
    /// The <c>git log --format</c> specification that <see cref="ReadCommitRecords"/> decodes:
    /// <c>%x1f</c> between fields, <c>%x1e</c> between records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Declared next to the parser on purpose.</strong> The format and its decoder are one
    /// contract expressed in two places (a command string and an index list), and they sat in
    /// different projects behind nothing but a documentation note. That is a latent defect class: a
    /// call to <c>ReadCommitRecords</c> that passes some other ad-hoc format — which is now free to
    /// happen, because the value was a public constant anyone could ignore — decodes author names into
    /// the subject field with no type error and no failure, only a panel full of subtly wrong rows.
    /// Making the parser own the format makes that pair unrepresentable.
    /// </para>
    /// <para>
    /// <strong>No <c>%h</c>.</strong> The abbreviation is derived from the full sha on the decode side:
    /// <c>core.abbrev</c> is user-configurable and auto-scales with object count, so git's short form
    /// has no stable width to promise a table cell, and a field emitted but not read would be a second
    /// source of truth for one value.
    /// </para>
    /// <para>
    /// <see cref="CommitLogArguments"/> is the only way to obtain a payload this method understands,
    /// and it is a <c>git log</c> command so a test can prove that command and this parser agree by
    /// running the real thing.
    /// </para>
    /// </remarks>
    public const string CommitLogFormat = "%H%x1f%ct%x1f%an%x1f%ae%x1f%s%x1e";

    /// <summary>
    /// Builds the <c>git log</c> arguments that produce a payload <see cref="ReadCommitRecords"/>
    /// decodes, newest-last, capped, excluding merges.
    /// </summary>
    /// <remarks>
    /// Merge commits are excluded because a merge's "changes" are the conflicts it resolved, not work,
    /// and a job's commit list that counts ten merges alongside three real commits tells a reviewer the
    /// branch did three times the work it did. The cap is the caller's, not a constant, so the reader's
    /// policy stays in the reader.
    /// </remarks>
    public static IReadOnlyList<string> CommitLogArguments(string range, int limit)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            throw new ArgumentException("A commit range is required.", nameof(range));
        }

        return limit > 0
            ? ["log", "--reverse", "--no-merges", $"--format={CommitLogFormat}", $"-{limit}", range]
            : ["log", "--reverse", "--no-merges", $"--format={CommitLogFormat}", range];
    }

    /// <summary>
    /// Parses <c>git log --format</c> output written with <c>%x1f</c> between fields and
    /// <c>%x1e</c> between records.
    /// </summary>
    /// <remarks>
    /// Those delimiters rather than a printable separator because a commit subject may contain any
    /// printable string — including whatever separator was chosen — and a subject with a newline in it
    /// is routine. Neither control character can appear in a log entry's own text, which is what makes
    /// the split unambiguous.
    /// </remarks>
    public static IReadOnlyList<JobChangeCommitDto> ReadCommitRecords(string? payload)
    {
        var commits = new List<JobChangeCommitDto>();
        if (string.IsNullOrEmpty(payload)) return commits;

        foreach (var record in payload.Split('\u001e'))
        {
            var trimmed = record.Trim('\n', '\r');
            if (trimmed.Length == 0) continue;

            var fields = trimmed.Split('\u001f');
            var sha = fields[0].Trim();
            if (sha.Length == 0) continue;

            commits.Add(new JobChangeCommitDto(
                Sha: sha,
                // Abbreviated from the full sha; see CommitLogFormat for why git's own %h is not asked
                // for. The result is stable across clones, which a width git chooses is not.
                ShortSha: sha.Length >= 7 ? sha[..7] : sha,
                // First line only. A commit body routinely continues onto a second line, and the
                // reader renders this into one table cell — the same rule the GitHub commit parser
                // applies, so a job's commits look identical whichever source described them.
                // Indices follow CommitLogFormat — 0 sha, 1 commit unix time, 2 author name,
                // 3 author email, 4 subject — declared eleven lines above so they cannot drift.
                // Reading past the end of a record yields null rather than throwing, so a hand-built
                // payload degrades to an empty subject instead of a 500 on a read endpoint.
                Subject: fields.Length > 4 ? FirstLine(fields[4]) : string.Empty,
                AuthorName: NullIfEmpty(fields.Length > 2 ? fields[2].Trim() : null),
                AuthorEmail: NullIfEmpty(fields.Length > 3 ? fields[3].Trim() : null),
                CommittedAt: fields.Length > 1 && long.TryParse(fields[1].Trim(), out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).ToUniversalTime()
                    : null));
        }

        return commits;
    }

    /// <summary>
    /// Normalizes one path from git's output into a repository-relative, forward-slash path.
    /// </summary>
    /// <remarks>
    /// Handles the shapes git emits: a plain path; a C-style quoted path with octal escapes, which
    /// <c>-z</c> disables but which must not be mistaken for a literal filename if a caller reuses
    /// this parser without <c>core.quotePath=false</c>; and <c>/dev/null</c>, which is a placeholder
    /// and not a path. Empty and <c>.</c> segments are dropped.
    /// <para>
    /// A <c>..</c> segment is <em>preserved</em> rather than folded away, because this path is later
    /// joined onto a workspace root to read a file: a consumer that sees <c>a/../../etc/passwd</c> can
    /// reject it, while one handed the already-folded <c>etc/passwd</c> has no way to know it was ever
    /// an escape attempt.
    /// </para>
    /// </remarks>
    public static string NormalizeRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = UnescapeCString(value[1..^1]);
        }

        if (value.Length == 0 || value == NoIndexEmptySide || value == "NUL") return string.Empty;

        value = value.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        value = value.TrimStart('/');

        if (value.Length == 0) return string.Empty;

        var kept = new List<string>();
        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            kept.Add(segment); // including ".." — see the remarks
        }

        return string.Join('/', kept);
    }

    /// <summary>
    /// Decodes git's C-style escapes: <c>\n</c>, <c>\t</c>, <c>\\</c>, <c>\"</c> and <c>\ooo</c>
    /// octal byte sequences.
    /// </summary>
    /// <remarks>
    /// Octal escapes are buffered as <em>bytes</em> and UTF-8 decoded as one run, because git escapes
    /// the bytes of a UTF-8 filename rather than its characters — turning each escape into its own
    /// character is what renders an accented filename as mojibake.
    /// </remarks>
    internal static string UnescapeCString(string value)
    {
        var result = new StringBuilder(value.Length);
        var pending = new List<byte>();

        void Flush()
        {
            if (pending.Count == 0) return;
            result.Append(Encoding.UTF8.GetString(pending.ToArray()));
            pending.Clear();
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                Flush();
                result.Append(c);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n': Flush(); result.Append('\n'); break;
                case 't': Flush(); result.Append('\t'); break;
                case 'r': Flush(); result.Append('\r'); break;
                case 'b': Flush(); result.Append('\b'); break;
                case 'f': Flush(); result.Append('\f'); break;
                case 'v': Flush(); result.Append('\v'); break;
                case '"': Flush(); result.Append('"'); break;
                case '\\': Flush(); result.Append('\\'); break;
                case '\'': Flush(); result.Append('\''); break;
                case >= '0' and <= '7':
                {
                    var octal = next.ToString();
                    while (octal.Length < 3 && i + 1 < value.Length && value[i + 1] is >= '0' and <= '7')
                    {
                        octal += value[++i];
                    }

                    // Base-8 accumulation rather than Convert.ToByte(octal, 8): no exception on a
                    // malformed tail, and a three-digit octal can never exceed 0o377 (255), so the
                    // cast cannot silently wrap. git only ever emits \ooo for a byte < 0o400.
                    var decoded = 0;
                    var valid = octal.Length is > 0 and <= 3;
                    foreach (var digit in octal)
                    {
                        if (digit is < '0' or > '7') { valid = false; break; }
                        decoded = (decoded * 8) + (digit - '0');
                    }

                    if (valid && decoded <= byte.MaxValue)
                    {
                        pending.Add((byte)decoded);
                    }
                    else
                    {
                        Flush();
                        result.Append('\\').Append(octal);
                    }

                    break;
                }
                default:
                    Flush();
                    result.Append('\\').Append(next);
                    break;
            }
        }

        Flush();
        return result.ToString();
    }

    /// <summary>A rename's original path, or null when git used a placeholder or the field is blank.</summary>
    private static string? CleanPrevious(string? raw)
    {
        var normalized = NormalizeRelativePath(raw);
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// Maps the porcelain status letter, ignoring any similarity score appended to it.
    /// </summary>
    /// <remarks>
    /// The raw value is returned verbatim for a letter this switch does not know, digits and all:
    /// <c>Q097</c> is a strange-looking row a reader can investigate, while coercing it to
    /// <c>modified</c> is a plausible row nobody would question.
    /// </remarks>
    private static string MapStatusLetter(char letter, string rawStatus) =>
        letter switch
        {
            'A' => JobFileChangeKinds.Added,
            'M' => JobFileChangeKinds.Modified,
            'D' => JobFileChangeKinds.Deleted,
            'R' => JobFileChangeKinds.Renamed,
            'C' => JobFileChangeKinds.Copied,
            'T' => JobFileChangeKinds.TypeChanged,
            '?' => JobFileChangeKinds.Untracked,
            _ => rawStatus,
        };

    /// <summary>
    /// Collapses porcelain's index and worktree columns into one status.
    /// </summary>
    /// <remarks>
    /// The worktree column wins: the reader's question is what the tree looks like now, and the two
    /// columns disagree routinely while a file is half-staged mid-turn. A deterministic pick beats an
    /// arbitrary one because whoever is debugging the panel will be comparing it to
    /// <c>git status</c>.
    /// </remarks>
    private static string MapPorcelain(char index, char worktree)
    {
        if (index == '?' && worktree == '?') return JobFileChangeKinds.Untracked;
        if (index == '!' && worktree == '!') return JobFileChangeKinds.Ignored;

        foreach (var candidate in new[] { worktree, index })
        {
            var mapped = candidate switch
            {
                'R' => JobFileChangeKinds.Renamed,
                'C' => JobFileChangeKinds.Copied,
                'D' => JobFileChangeKinds.Deleted,
                'A' => JobFileChangeKinds.Added,
                'T' => JobFileChangeKinds.TypeChanged,
                'M' => JobFileChangeKinds.Modified,
                'U' => "U", // unmerged: raw on purpose, see ReadNameStatus
                _ => null,
            };

            if (mapped != null) return mapped;
        }

        return JobFileChangeKinds.Modified;
    }

    /// <summary>Reads one numstat column: a count, <c>-1</c> for binary, or null if unparseable.</summary>
    private static int? ParseStat(string value)
    {
        var text = value.Trim();
        if (text == BinaryStatMarker) return -1;
        return int.TryParse(text, out var parsed) && parsed >= 0 ? parsed : null;
    }

    /// <summary>
    /// Parses the <c>files[]</c> array of GitHub's compare or pull-request payload.
    /// </summary>
    /// <remarks>
    /// GitHub reports a rename as <c>previous_filename</c> plus the current <c>filename</c>, and its
    /// <c>status</c> values (<c>renamed</c>, <c>added</c>, …) are already the words the panel shows —
    /// but its <c>changes</c> field is the <em>sum</em> of additions and deletions, which is why the
    /// per-side <c>additions</c>/<c>deletions</c> fields are read rather than derived. A file GitHub
    /// marks <c>changes</c> with no per-side counts is reported as uncountable rather than as an even
    /// split, because inventing the split would produce totals that do not match the PR page a reader
    /// has open next to ours.
    /// <para>
    /// Missing or unparseable JSON yields an empty list rather than throwing: a response shape change
    /// upstream should degrade the panel to "the remote answered nothing", which the caller reports as
    /// a reason, and not to an unhandled exception on a request path.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChangedFileEntry> ReadGitHubFiles(string? json)
    {
        var entries = new List<ChangedFileEntry>();
        if (string.IsNullOrWhiteSpace(json)) return entries;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetPropertyCaseInsensitive(document.RootElement, "files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (var file in files.EnumerateArray())
            {
                var path = ReadString(file, "filename");
                if (path.Length == 0) continue;

                var previous = ReadString(file, "previous_filename");
                var status = ReadString(file, "status");

                entries.Add(new ChangedFileEntry(
                    NormalizeRelativePath(path),
                    MapGitHubStatus(status),
                    previous.Length == 0 ? null : NormalizeRelativePath(previous),
                    Additions: ReadInt(file, "additions"),
                    Deletions: ReadInt(file, "deletions"),
                    Binary: ReadBool(file, "binary") ?? LooksBinary(file, status)));
            }
        }
        catch (JsonException)
        {
            return Array.Empty<ChangedFileEntry>();
        }

        return entries;
    }

    /// <summary>
    /// Parses the <c>commits[]</c> array of GitHub's compare payload.
    /// </summary>
    /// <remarks>
    /// GitHub nests the author inside <c>commit.author</c> (name, email, date) while the commit SHA is
    /// a top-level <c>sha</c> — the shape is a translation of git's own object model, and reading the
    /// flat fields instead would silently produce a list of commits with no authors and no dates.
    /// </remarks>
    public static IReadOnlyList<JobChangeCommitDto> ReadGitHubCommits(string? json, int limit = 0)
    {
        var commits = new List<JobChangeCommitDto>();
        if (string.IsNullOrWhiteSpace(json)) return commits;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetPropertyCaseInsensitive(document.RootElement, "commits", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return commits;
            }

            foreach (var item in items.EnumerateArray())
            {
                var sha = ReadString(item, "sha");
                if (sha.Length == 0) continue;

                var hasCommit = TryGetPropertyCaseInsensitive(item, "commit", out var commit);
                var authorName = hasCommit && TryGetPropertyCaseInsensitive(commit, "author", out var author)
                    ? ReadString(author, "name")
                    : string.Empty;
                var authorEmail = hasCommit && TryGetPropertyCaseInsensitive(commit, "author", out var emailHolder)
                    ? ReadString(emailHolder, "email")
                    : string.Empty;
                var subject = hasCommit ? ReadString(commit, "message") : string.Empty;

                // GitHub's compare payload puts the committer's identity in commit.committer, and the
                // author's in commit.author; the date the panel sorts by is the author date.
                var date = hasCommit && TryGetPropertyCaseInsensitive(commit, "author", out var authorNode)
                    ? ReadDate(authorNode)
                    : null;

                commits.Add(new JobChangeCommitDto(
                    Sha: sha,
                    ShortSha: sha.Length >= 7 ? sha[..7] : sha,
                    // A commit message is multi-line; the panel shows the subject, so take the first
                    // line rather than rendering a paragraph in a table cell.
                    Subject: FirstLine(subject),
                    AuthorName: authorName.Length == 0 ? null : authorName,
                    AuthorEmail: authorEmail.Length == 0 ? null : authorEmail,
                    CommittedAt: date));

                if (limit > 0 && commits.Count >= limit) break;
            }
        }
        catch (JsonException)
        {
            return Array.Empty<JobChangeCommitDto>();
        }

        return commits;
    }

    /// <summary>
    /// Reads GitHub's <c>merge_base_commit.sha</c> from a compare payload.
    /// </summary>
    /// <remarks>
    /// The API reports the fork point it used, which is what makes the remote answer comparable to the
    /// local one: the local reader resolves the same merge base with <c>git merge-base</c> and prints
    /// it as <see cref="JobChangeSetDto.BaseSha"/>. When the field is absent (a payload for a
    /// nonexistent comparison carries <c>compare_url</c> and empty arrays) this returns null and the
    /// caller reports the read as unavailable rather than as "no changes".
    /// </remarks>
    public static string? ReadGitHubMergeBase(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetPropertyCaseInsensitive(document.RootElement, "merge_base_commit", out var mergeBase))
            {
                return null;
            }

            var sha = ReadString(mergeBase, "sha");
            return sha.Length == 0 ? null : sha;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The status GitHub reports for the head side of a compare, or null when the payload does not say.
    /// </summary>
    /// <remarks>
    /// <c>merge_base_commit</c> carries no status; the head commit's <c>status</c> field is
    /// <c>ahead</c>/<c>behind</c> relative to the base and is what tells a reader whether the branch is
    /// only ahead (their work plus an up-to-date base) or has also fallen behind (the base moved, so
    /// the fork-point comparison is doing real work and the "commits" list is the interesting part).
    /// </remarks>
    public static string? ReadGitHubComparisonStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var status = ReadString(document.RootElement, "status");
            return status.Length == 0 ? null : status;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a compare payload's total ahead/behind counts, for a note about a branch that fell behind.
    /// </summary>
    public static (int? Ahead, int? Behind) ReadGitHubAheadBehind(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetPropertyCaseInsensitive(document.RootElement, "ahead_by", out var ahead) ||
                ahead.ValueKind != JsonValueKind.Number)
            {
                return (null, null);
            }

            var behind = TryGetPropertyCaseInsensitive(document.RootElement, "behind_by", out var b) &&
                         b.ValueKind == JsonValueKind.Number
                ? b.GetInt32()
                : (int?)null;

            return (ahead.GetInt32(), behind);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Maps GitHub's status words onto the panel's vocabulary, passing unknown values through.</summary>
    public static string MapGitHubStatus(string status) => status.Trim().ToLowerInvariant() switch
    {
        "added" => JobFileChangeKinds.Added,
        "modified" => JobFileChangeKinds.Modified,
        "removed" or "deleted" => JobFileChangeKinds.Deleted,
        "renamed" => JobFileChangeKinds.Renamed,
        "copied" => JobFileChangeKinds.Copied,
        "changed" => JobFileChangeKinds.Modified,
        "unchanged" => "unchanged",
        var other when other.Length > 0 => other,
        _ => JobFileChangeKinds.Modified,
    };

    private static bool LooksBinary(JsonElement file, string status)
    {
        // GitHub does not flag a binary file directly; it reports `changes` with no per-side counts
        // for one (and `additions`/`deletions` present for text). A 0/0 pair on a `changed` file whose
        // `changes` is also 0 is the signature of a binary or empty blob, which is what the panel needs
        // to say "no line stats for this file" instead of "this file changed by nothing".
        var additions = ReadInt(file, "additions");
        var deletions = ReadInt(file, "deletions");
        var changes = ReadInt(file, "changes");

        if (additions is null || deletions is null) return true;
        return additions == 0 && deletions == 0 && changes == 0 && status.Equals("modified", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string value)
    {
        var index = value.IndexOf('\n');
        var line = index < 0 ? value : value[..index];
        return line.TrimEnd('\r').Trim();
    }

    private static DateTimeOffset? ReadDate(JsonElement element)
    {
        if (!TryGetPropertyCaseInsensitive(element, "date", out var date)) return null;

        if (date.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(date.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static string ReadString(JsonElement element, string name) =>
        TryGetPropertyCaseInsensitive(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? ReadInt(JsonElement element, string name) =>
        TryGetPropertyCaseInsensitive(element, name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed) && parsed >= 0
            ? parsed
            : null;

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!TryGetPropertyCaseInsensitive(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>
    /// Case-insensitive property lookup, because GitHub's payloads are camelCase while a proxied or
    /// stubbed response can carry whatever the author typed, and a mapper that reads only one spelling
    /// renders an empty list with no error — the failure every DTO mapper in this repo guards against.
    /// </summary>
    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The caveats a reader needs to be told about a comparison payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is a pure function of the payload rather than inline prose in the reader.</strong>
    /// A branch that has fallen behind its base is the case where a reader is most likely to conclude
    /// the panel is wrong — "these files did not change in my commit" — when it is in fact reporting
    /// the fork-point comparison correctly. The note has to travel with the change set wherever it
    /// goes, including through a caller that fetched the payload elsewhere, so the rule and its
    /// wording live in one place that both the remote source and the tests can call.
    /// </para>
    /// <para>
    /// An unknown <c>behind_by</c> yields no note rather than a reassuring one: silence is honest here,
    /// while inventing "up to date" for a field GitHub did not send would be a claim about the branch.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ReadComparisonNotes(string? json)
    {
        var (ahead, behind) = ReadGitHubAheadBehind(json);

        var notes = new List<string>();

        if (behind is > 0)
        {
            notes.Add($"The branch is {behind.Value} commit(s) behind the base; the diff is taken from " +
                      "the fork point, so the base\'s newer work is not included.");
        }

        if (ahead == 0 && behind == 0)
        {
            notes.Add("The branch and the base point at the same commit, so this branch has no changes of its own.");
        }

        return notes;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
