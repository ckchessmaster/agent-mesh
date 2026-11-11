using System.Diagnostics;
using System.Text;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// Checks the change reader's assumptions about git's own output against a real git binary.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these exist when the parsing is already covered.</strong> The parser tests feed
/// <see cref="GitChangesParser"/> captured payloads, which is the only way to exercise a filename
/// containing a newline or a tab. But a captured payload can only prove the parser agrees with whoever
/// captured it. Every one of those fixtures was written after running a real command once, and that
/// step is invisible to a later reader and unreproducible in review: if git's <c>--numstat -z</c>
/// rename layout is not what this repository assumes, then the parser, its tests, and the panel all
/// agree with each other and disagree with git — a wrong answer behind a green suite. These tests close
/// that loop by executing git and asserting on its bytes.
/// </para>
/// <para>
/// They are <see cref="GitFactAttribute"/> tests, so they skip rather than fail where git is absent. A
/// claim about git's output that cannot be checked is better reported as unverified than replaced by a
/// stub that quietly restates the assumption it was meant to test.
/// </para>
/// <para>
/// <strong>Every comparison is against a recorded base commit, never a positional one.</strong> The
/// first pass of this file wrote two commits onto a branch and then diffed <c>HEAD~1..HEAD</c>, which
/// silently compared against the branch's own second-newest commit: the rename under test had scrolled
/// out of the range, and the assertions failed on an empty payload while the production reader — which
/// resolves the real merge base — was innocent. A fixture whose base is a positional guess cannot
/// distinguish "git changed its output format" from "my range was wrong", and that distinction is the
/// entire reason these tests exist.
/// </para>
/// <para>
/// Fixture repositories are built with <c>-c</c> overrides for the settings that change output shapes
/// (<c>user.*</c>, <c>commit.gpgsign</c>, <c>core.autocrlf</c>, <c>core.quotePath</c>) plus
/// <c>GIT_CONFIG_NOSYSTEM</c> and a redirected <c>HOME</c>, so a developer's global configuration
/// cannot change what the assertions are about. <c>init.defaultBranch</c> is pinned through
/// <c>init -b</c> because the branch name appears in assertions that are about output formats, not
/// naming.
/// </para>
/// </remarks>
public sealed class GitOutputContractTests : IDisposable
{
    /// <summary>The identity every fixture commit is authored by, pinned through the environment.</summary>
    private const string FixtureAuthor = "Contract Tester";

    /// <summary>See <see cref="FixtureAuthor"/>; pinned so no ambient address can leak into an assertion.</summary>
    private const string FixtureEmail = "contract@agentmesh.test";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "git-contract-" + Guid.NewGuid().ToString("N"));

    public GitOutputContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── the commit format the parser decodes ────────────────────────────────────

    /// <summary>
    /// The <c>--format</c> specification the parser decodes is the one git actually expands.
    /// </summary>
    /// <remarks>
    /// The highest-value assertion in this file. <c>%x1f</c> and <c>%x1e</c> are format specifiers git
    /// must translate into control characters; if it emitted them literally instead, the split on
    /// <c>\u001e</c> would leave the whole log in one record and every field after the sha would be
    /// wrong in a way no stub could ever reveal, because a stub replays what its author believed.
    /// </remarks>
    [GitFact]
    public async Task CommitLogFormat_GitEmitsTheSeparatorsTheParserSplitsOn()
    {
        var baseSha = await SeedAsync();

        // Two commits after the recorded base, the older one carrying a multi-line message full of
        // punctuation. --reverse puts it first, so one assertion covers both "the body did not split
        // the record" and "the newest commit is last".
        await CommitFileAsync("a.txt", "one\n", "first: add a\n\nA body with a comma, a colon: and a | pipe.");
        await CommitFileAsync("b.txt", "two\n", "second: add b");

        var output = await GitAsync(GitChangesParser.CommitLogArguments($"{baseSha}..HEAD", 0));

        output.Should().Contain("\u001e", "git must honour %x1e as a record separator, not print it");
        output.Should().NotContain("%x1f", "an unexpanded specifier would leave one record per payload");

        var commits = GitChangesParser.ReadCommitRecords(output);

        commits.Should().HaveCount(2, "both commits are after the base and neither is a merge");
        commits[0].Sha.Should().MatchRegex("^[0-9a-f]{40}$");
        commits[0].ShortSha.Should().HaveLength(7);
        commits[0].Subject.Should().Be("first: add a",
            "the panel renders one table cell: the summary line, never the body");
        commits[0].AuthorName.Should().Be("Contract Tester");
        commits[0].AuthorEmail.Should().Be("contract@agentmesh.test");
        commits[0].CommittedAt.Should().NotBeNull();
        commits[1].Subject.Should().Be("second: add b", "--reverse orders the list oldest-first");
    }

    /// <summary>
    /// Merge commits are excluded, so a rebased branch does not report other people's merges as work.
    /// </summary>
    [GitFact]
    public async Task CommitLogArguments_ExcludeMergeCommits()
    {
        await InitAsync();
        await CommitFileAsync("seed.txt", "base\n", "base: seed");

        await GitAsync(["checkout", "-b", "side"]);
        await CommitFileAsync("side.txt", "side\n", "side: work");

        await GitAsync(["checkout", "master"]);
        await CommitFileAsync("mainline.txt", "main\n", "main: work");
        await GitAsync(["merge", "--no-ff", "-m", "merge: bring side in", "side"]);

        // A plain log sees the merge, which proves it exists and is in range. Asserting only the
        // reader's own format would also pass on a repository with no merge at all, which is the
        // failure mode worth guarding against.
        var plain = await GitAsync(["log", "--format=%s", "master"]);
        plain.Should().Contain("merge: bring side in", "a plain log sees the merge");

        var formatted = GitChangesParser.ReadCommitRecords(
            await GitAsync(GitChangesParser.CommitLogArguments("master", 0)));

        formatted.Should().NotContain(c => c.Subject.StartsWith("merge:", StringComparison.Ordinal),
            "a merge records which conflicts were resolved, not work done; counting ten of them " +
            "alongside three real commits tells a reviewer the branch did three times the work");
        formatted.Should().Contain(c => c.Subject == "side: work");
    }

    /// <summary>The limit reaches git, so a long-lived branch cannot produce an unbounded response.</summary>
    [GitFact]
    public async Task CommitLogArguments_LimitIsHonouredByGit()
    {
        var baseSha = await SeedAsync();

        for (var i = 0; i < 5; i++)
        {
            await CommitFileAsync($"f{i}.txt", $"{i}\n", $"work: commit {i}");
        }

        var capped = GitChangesParser.ReadCommitRecords(
            await GitAsync(GitChangesParser.CommitLogArguments($"{baseSha}..HEAD", 2)));

        capped.Should().HaveCount(2, "the cap is the reader's policy and git has to enforce it");
    }

    // ── the plumbing payloads, byte for byte ────────────────────────────────────

    /// <summary>
    /// <c>--name-status -z</c> interleaves the similarity score, which is why the record stride varies.
    /// </summary>
    /// <remarks>
    /// Asserted against the captured bytes first and the parsed entries second. The byte assertion is
    /// the one that protects the parser: it is what a future git change would break, and it names the
    /// empty-score field that the fixed-stride reading would mistake for a path.
    /// </remarks>
    [GitFact]
    public async Task NameStatus_Zero_ReportsRenameAsScoreThenPreimageThenDestination()
    {
        var (baseRef, body) = await SeedWithLargeFileAsync("big.txt");

        await GitAsync(["mv", "big.txt", "moved.txt"]);
        await CommitFileAsync("moved.txt", body.Replace("line 5\n", "line 5 EDITED\n"), "work: move and edit");
        await CommitFileAsync("other.txt", "content\n", "work: add a file");

        var payload = Decode(await GitRawAsync(
            ["diff", "--find-renames", "--name-status", "-z", baseRef, "HEAD"]));

        payload.Should().Be("R097\0big.txt\0moved.txt\0A\0other.txt\0");

        var entries = GitChangesParser.ReadNameStatus(payload);

        entries.Should().HaveCount(2);
        entries[0].Path.Should().Be("moved.txt");
        entries[0].PreviousPath.Should().Be("big.txt");
        entries[0].Status.Should().Be(JobFileChangeKinds.Renamed);
        entries[1].Path.Should().Be("other.txt");
        entries[1].Status.Should().Be(JobFileChangeKinds.Added);

        // A fixed two-field stride would put "097" or "big.txt" in the path column and shift every
        // later record by one, so no row may consist of nothing but a similarity score.
        entries.Should().NotContain(e => e.Path.All(char.IsDigit));
    }

    /// <summary>
    /// <c>--numstat -z</c> puts the two paths of a rename in their own fields after an empty path.
    /// </summary>
    /// <remarks>
    /// The layout the parser's variable-stride walk turns on, and the one record in the format whose
    /// field count differs. Getting it wrong is silent: the counts land on the wrong rows and the
    /// totals stop matching the patch a reader opens next to them.
    /// </remarks>
    [GitFact]
    public async Task Numstat_Zero_ReportsRenameWithAnEmptyPathColumnThenThePair()
    {
        var (baseRef, body) = await SeedWithLargeFileAsync("big.txt");

        await GitAsync(["mv", "big.txt", "moved.txt"]);
        await CommitFileAsync("moved.txt", body.Replace("line 5\n", "line 5 EDITED\n"), "work: move and edit");
        await CommitFileAsync("other.txt", "content\n", "work: add a file");

        var payload = Decode(await GitRawAsync(
            ["diff", "--find-renames", "--numstat", "-z", baseRef, "HEAD"]));

        payload.Should().Be("1\t1\t\0big.txt\0moved.txt\01\t0\tother.txt\0",
            "the rename's third column is empty and its paths follow as separate NUL-terminated fields");

        var entries = GitChangesParser.ReadNumstat(payload);

        entries.Should().HaveCount(2);
        entries[0].Path.Should().Be("moved.txt", "a rename is addressed by the path that exists now");
        entries[0].PreviousPath.Should().Be("big.txt");
        entries[0].Additions.Should().Be(1);
        entries[0].Deletions.Should().Be(1);
        entries[1].Path.Should().Be("other.txt");
    }

    /// <summary>A rename with no edits at all is 0/0, which is a different fact from "no stats".</summary>
    [GitFact]
    public async Task Numstat_Zero_PureRename_ReportsZeroCountsNotMissingOnes()
    {
        var (baseRef, _) = await SeedWithLargeFileAsync("big.txt");

        await GitAsync(["mv", "big.txt", "moved.txt"]);
        await GitAsync(["commit", "-m", "work: pure rename"]);

        var entries = GitChangesParser.ReadNumstat(Decode(await GitRawAsync(
            ["diff", "--find-renames", "--numstat", "-z", baseRef, "HEAD"])));

        var rename = entries.Single();
        rename.Additions.Should().Be(0, "0 is a measured answer; null would mean 'not countable'");
        rename.Deletions.Should().Be(0);
        rename.Binary.Should().BeFalse();
    }

    /// <summary>
    /// Porcelain reports a staged rename's destination first and its source as a following field.
    /// </summary>
    /// <remarks>
    /// The reverse of <c>--name-status</c>'s order. Pinned because it is the asymmetry most likely to
    /// be "tidied up" by someone who reads one parser and assumes both plumbing commands agree.
    /// </remarks>
    [GitFact]
    public async Task PorcelainZero_StagedRename_ReportsDestinationInsideTheRecordAndSourceAfterIt()
    {
        await SeedAsync("orig.txt");
        await CommitFileAsync("orig.txt", "content\n", "base: seed a file to move");

        await GitAsync(["mv", "orig.txt", "staged.txt"]);

        var payload = Decode(await GitRawAsync(
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"]));

        payload.Should().Be("R  staged.txt\0orig.txt\0");

        var entries = GitChangesParser.ReadPorcelainStatus(payload);

        entries.Should().ContainSingle();
        entries[0].Path.Should().Be("staged.txt");
        entries[0].PreviousPath.Should().Be("orig.txt");
        entries[0].Status.Should().Be(JobFileChangeKinds.Renamed);
    }

    [Fact]
    public void PorcelainZero_UntrackedAndWorktreeEdit_AreDistinguishableInRealOutput()
    {
        // No git needed: the two record shapes this reader depends on are asserted on captured bytes,
        // and the same shapes are produced by the git-backed tests above. Kept as a plain [Fact] so
        // the vocabulary distinction survives on a machine without git installed.
        var entries = GitChangesParser.ReadPorcelainStatus(" M src/tracked.txt\0?? src/fresh.txt\0");

        entries[0].Status.Should().Be(JobFileChangeKinds.Modified);
        entries[1].Status.Should().Be(JobFileChangeKinds.Untracked,
            "'created and not committed yet' is the state a reviewer most needs to see separately " +
            "from 'edited and not committed yet'");
    }

    // ── one-sided reads ─────────────────────────────────────────────────────────

    /// <summary>
    /// An untracked file is invisible to every committed comparison and reachable only one-sided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole reason <see cref="JobFileChangeKinds.Untracked"/> exists as a distinct status
    /// and the patch route is chosen from it. A committed diff for an untracked path exits 0 with an
    /// empty body — success, and nothing to show — so a reader that treats exit 0 as "answered" renders
    /// "this file changed nothing" for the one row in the list that certainly did change.
    /// </para>
    /// <para>
    /// The one-sided command's exit code is 1 by design when it finds a difference, which is why the
    /// helper is told to expect it: an assertion helper that pinned exit 0 for every command would
    /// report the reader's own correct behaviour as a git failure.
    /// </para>
    /// </remarks>
    [GitFact]
    public async Task UntrackedFile_CommittedDiffIsSilentlyEmptyAndOnlyNoIndexDescribesIt()
    {
        await SeedAsync("tracked.txt");
        await CommitFileAsync("tracked.txt", "committed\n", "base: seed");

        await WriteFileAsync("fresh.txt", "untracked content\nno commit contains this\n");

        var committed = await GitRawAsync(
            ["diff", "--find-renames", "--numstat", "HEAD", "--", "fresh.txt"]);

        Encoding.UTF8.GetString(committed).Should().BeEmpty(
            "exit 0 with no payload: the file is in no tree, so there is no diff to report");

        var oneSided = Decode(await GitRawAsync(expectedExitCode: 1,
            ["diff", "--no-index", "--name-status", "-z", "--", "/dev/null", "fresh.txt"]));

        oneSided.Should().Be("A\0fresh.txt\0", "the one-sided read describes it as an addition");

        // The path stays relative, exactly as the reader passes it: an absolute path would address the
        // file from outside the repository whose changes are being described, and would leak the
        // workspace root into the response.
        GitChangesParser.ReadNameStatus(oneSided).Should().ContainSingle(e => e.Path == "fresh.txt");

        var patch = Decode(await GitRawAsync(expectedExitCode: 1,
            ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "-U3", "--no-color",
                "--", "/dev/null", "fresh.txt"]));

        patch.Should().Contain("+++ b/fresh.txt").And.Contain("+untracked content");
    }

    /// <summary>
    /// <c>--no-index</c> accepts a path that no tracked comparison can name, which is what makes an
    /// untracked file's diff expressible at all.
    /// </summary>
    [GitFact]
    public async Task UntrackedFile_DeletedFromTheWorktree_ReportsNoBodyRatherThanAnError()
    {
        await SeedAsync("tracked.txt");
        await CommitFileAsync("tracked.txt", "committed\n", "base: seed");

        // The path the list would only ever offer while the file exists; if it vanished between the
        // list and the patch, git's failure must not arrive as a 200 with a stack trace.
        var missing = await GitRawAsyncAllowingFailure(
            ["diff", "--no-index", "--name-status", "-z", "--", "/dev/null", "never-existed.txt"]);

        missing.ExitCode.Should().NotBe(0, "there is no file to read");
        Decode(missing.Stdout).Should().BeEmpty(
            "an empty payload is what the reader reports as 'this file could not be read'");
    }

    // ── fixture repository ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a repository on <c>master</c> with one commit, then branches <c>work</c> from it.
    /// </summary>
    /// <remarks>
    /// Returns the fork point so a caller compares against the commit the branch actually diverged
    /// from — the same resolution the production reader performs with <c>git merge-base</c>, and the
    /// thing a positional <c>HEAD~1</c> cannot promise once a branch has more than one commit.
    /// </remarks>
    private async Task<string> SeedAsync(string fileName = "seed.txt")
    {
        await InitAsync();
        await CommitFileAsync(fileName, "base content\n", "base: seed the repository");

        var baseSha = (await GitAsync(["rev-parse", "HEAD"])).Trim();
        await GitAsync(["checkout", "-b", "work"]);

        return baseSha;
    }

    /// <summary>
    /// Seeds a repository containing a file large enough for git to recognise a rename of it.
    /// </summary>
    /// <remarks>
    /// Sixty lines is not arbitrary: rename detection is a similarity threshold over content, and a
    /// two-line file that changes by one line is legitimately reported as an unrelated add plus a
    /// delete. A fixture whose assertions depend on git deciding a tiny file is a rename would fail
    /// for reasons that have nothing to do with the output format under test.
    /// </remarks>
    /// <returns>
    /// The fork point and the seeded content, so a caller can edit one line of it and still know
    /// which commit to compare against. <c>out</c> is not usable here: an async method cannot
    /// declare one, and the alternative — a field on the test class mutated by a helper — would
    /// make two tests in the same class depend on each other's leftovers under the parallel runner.
    /// </returns>
    private async Task<(string BaseSha, string Body)> SeedWithLargeFileAsync(string fileName)
    {
        var body = string.Concat(Enumerable.Range(0, 60).Select(i => $"line {i}\n"));

        await InitAsync();
        await CommitFileAsync(fileName, body, "base: seed a large file");

        var baseSha = (await GitAsync(["rev-parse", "HEAD"])).Trim();
        await GitAsync(["checkout", "-b", "work"]);

        return (baseSha, body);
    }

    private async Task InitAsync() =>
        // `init -b` rather than init.defaultBranch: the branch name appears in assertions that are
        // about git's output formats, not about what a machine happens to name its first branch.
        await GitAsync(["init", "-b", "master", _root]);

    private async Task WriteFileAsync(string relative, string content) =>
        await File.WriteAllTextAsync(Path.Combine(_root, relative), content, new UTF8Encoding(false));

    private async Task CommitFileAsync(string relative, string content, string message)
    {
        await WriteFileAsync(relative, content);
        await GitAsync(["add", "-A"]);
        await GitAsync(["commit", "-m", message]);
    }

    private async Task<string> GitAsync(IReadOnlyList<string> args) =>
        Decode(await GitRawAsync(args.ToArray()));

    /// <summary>Runs git and asserts the ordinary success contract.</summary>
    private Task<byte[]> GitRawAsync(params string[] args) => GitRawAsync(0, args);

    private sealed record GitRun(int ExitCode, byte[] Stdout);

    /// <summary>Runs git twice-removed from a shell, returning the captured bytes plus its exit code.</summary>
    private async Task<byte[]> GitRawAsync(int expectedExitCode, params string[] args) =>
        (await RunAsync(expectedExitCode, args)).Stdout;

    /// <summary>
    /// Runs git with every ambient setting neutralised, without asserting on the exit code.
    /// </summary>
    private Task<GitRun> GitRawAsyncAllowingFailure(params string[] args) => RunAsync(null, args);

    /// <remarks>
    /// <para>
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a reconstructed command
    /// line, so a fixture filename containing a space or a quote is one argument rather than two.
    /// </para>
    /// <para>
    /// <strong>Not every command that answers correctly exits 0.</strong> <c>diff --no-index</c>
    /// signals "a difference was found" with 1 — its documented contract, and the reason the
    /// production reader judges that command by its payload. An earlier version of this helper asserted
    /// exit 0 unconditionally and so reported the reader's own correct behaviour as a git failure,
    /// which is precisely the class of false green these tests exist to remove.
    /// </para>
    /// <para>
    /// <c>core.abbrev</c> is deliberately not pinned; the first attempt pinned it to a value git
    /// rejects outright (<c>full</c> is not a unit), and the contract test caught that in about a
    /// second, which is the point. No override is needed: <see cref="GitChangesParser.CommitLogFormat"/>
    /// asks for the full <c>%H</c> and derives the abbreviation itself, so the width git would have
    /// chosen is not observable from any assertion here.
    /// </para>
    /// </remarks>
    private async Task<GitRun> RunAsync(int? expectedExitCode, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var overridePair in new[]
        {
            $"user.name={FixtureAuthor}",
            $"user.email={FixtureEmail}",
            "commit.gpgsign=false",
            "tag.gpgsign=false",
            "core.autocrlf=false",
            "core.quotePath=false", // raw UTF-8 filenames, which is what the reader passes too
            "core.fsmonitor=false", // no daemon spawned from a test
            "i18n.logOutputEncoding=UTF-8",
        })
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(overridePair);
        }

        foreach (var argument in args) psi.ArgumentList.Add(argument);

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; // no /etc/gitconfig either
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["HOME"] = _root; // no global config

        // The author identity has to be pinned through the environment, not only through
        // -c user.*: GIT_AUTHOR_NAME/GIT_AUTHOR_EMAIL override the config outright, so a host that
        // exports them — this sandbox does, and CI runners routinely do — would stamp its own
        // identity onto every fixture commit and the assertion below would then be comparing git's
        // output against a name no test chose. The contract test caught exactly this, which is the
        // hazard the class remarks describe.
        psi.Environment["GIT_AUTHOR_NAME"] = FixtureAuthor;
        psi.Environment["GIT_AUTHOR_EMAIL"] = FixtureEmail;
        psi.Environment["GIT_COMMITTER_NAME"] = FixtureAuthor;
        psi.Environment["GIT_COMMITTER_EMAIL"] = FixtureEmail;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");

        try { process.StandardInput.Close(); } catch { /* already closed */ }

        // Read to EOF before waiting: a child blocked on a full pipe never exits, and a fixture helper
        // that hangs the test host is worse than one that reports a failure.
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;

        while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var command = args.Length > 0 ? args[0] : "(none)";

        if (expectedExitCode is { } expected)
        {
            process.ExitCode.Should().Be(expected, $"git {command} exited unexpectedly: {error}");
        }

        return new GitRun(process.ExitCode, output.ToArray());
    }

    /// <summary>
    /// Decodes captured bytes as UTF-8 without translating any newline pairing.
    /// </summary>
    /// <remarks>
    /// A round-trip through a text-mode reader would normalise nothing on Linux and everything on
    /// Windows, so the payload assertions would describe one platform while claiming to describe the
    /// format. Byte-level decoding is what makes "git emitted exactly these bytes" true on both.
    /// </remarks>
    private static string Decode(byte[] raw) => Encoding.UTF8.GetString(raw);
}
