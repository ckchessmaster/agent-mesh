using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Workflows;

/// <summary>
/// Workspace reconciliation and the environment recovery note.
/// </summary>
/// <remarks>
/// Split into a probe half (against a real temporary git repository) and a formatter half (pure
/// string work). The probe is where the shell script can be wrong — a missing <c>cd --</c>, an
/// unquoted path, a <c>git</c> invocation that behaves differently on a detached HEAD — and none of
/// that is visible from a unit test of the formatter. The formatter is where the wording can be wrong,
/// and it has enough branches (clean tree, dirty tree, stash, detached, non-repo, truncated list) that
/// driving each through a real repository would make the suite slow and the failures ambiguous.
/// </remarks>
public class WorkspaceReconciliationTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // The spec's named test
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A dirty working tree produces a note that states the state, so the agent does not explore for
    /// it.
    /// </summary>
    [Fact]
    public async Task ReconcileWorkspace_InjectsStateHeader_AndSkipsToolExploration()
    {
        var repo = new TempGitRepository();
        await using var _ = repo;
        await repo.InitAsync();
        await repo.CommitAsync("initial commit", ("seed.txt", "seed"));
        await repo.CheckoutBranchAsync("feat/dirty-work");
        await repo.WriteAsync("src/changed.cs", "edited");
        await repo.WriteAsync("src/untracked.cs", "new file");
        await repo.StashAsync("wip: half-finished feature");
        // Re-dirty after stashing so both facts are true at once, which is the state an agent is most
        // likely to misread.
        await repo.WriteAsync("src/changed.cs", "edited again");

        var reconciler = new ShellWorkspaceReconciler(
            shellRunner: (script, cwd, ct) => repo.RunProbeAsync(script, cwd));

        var report = await reconciler.ReconcileAsync(repo.Path);
        var note = EnvironmentRecoveryNote.Format(report);

        report.Inspected.Should().BeTrue(report.FailureReason ?? "the probe should have succeeded");
        report.IsGitRepository.Should().BeTrue();
        note.Should().NotBeNull();

        // The header the Definition of Done matches on.
        note!.Should().Contain(EnvironmentRecoveryNote.Header);

        // Every fact the agent would otherwise spend a turn discovering.
        note.Should().Contain(repo.Path, "the agent must not need to run pwd");
        note.Should().Contain("feat/dirty-work", "the agent must not need to run git branch");
        note.Should().Contain("Head Commit:").And.Contain("initial commit",
            "the agent must not need to run git log");
        note.Should().Contain("Stash:").And.Contain("wip: half-finished feature",
            "an agent that does not know it stashed will reimplement the work and then hit a conflict");

        // The dirty-file count, from `git status --porcelain --untracked-files=all`.
        note.Should().MatchRegex(@"Working Tree: \d+ uncommitted paths? \(",
            "the count and the path list are the reason the agent skips git status");
        note.Should().Contain("src/changed.cs");

        // The instruction that makes the note worth its tokens.
        note.Should().Contain("Do not spend turns running git status, git log, or pwd");

        // The safety guidance that only appears when it is true.
        note.Should().Contain("do not reset, clean, or check out over it",
            "a dirty tree must be flagged as work at risk, not merely described");
        note.Should().Contain("check it before reimplementing anything that looks missing");
    }

    /// <summary>
    /// The prompt stack carries the note, and the note is what replaces exploratory tool calls.
    /// </summary>
    /// <remarks>
    /// The spec's DoD says a restarted turn must recover "without exploratory <c>pwd</c> or
    /// <c>git status</c> tool calls". The harness cannot prevent a model from calling a tool; what it
    /// can guarantee is that the answer is already in the prompt and that the prompt says so. This
    /// asserts the harness half of that contract, which is the half that is testable.
    /// </remarks>
    [Fact]
    public void ReconcileWorkspace_NoteIsRenderedIntoThePromptStack()
    {
        var report = new WorkspaceReconciliationReport(
            WorkingDirectory: "/workspace/repos/agent-mesh",
            Inspected: true,
            IsGitRepository: true,
            Branch: "feat/recovery",
            UpstreamRef: "origin/feat/recovery",
            ModifiedFilesCount: 2,
            ModifiedFiles: ["src/a.cs", "src/b.cs"],
            HeadCommitOneline: "a1b2c3d fix: the thing",
            StashEntries: ["stash@{0}: On feat/recovery: wip"]);

        var note = EnvironmentRecoveryNote.Format(report);
        note.Should().NotBeNull();

        note!.Should().Contain("Tracking: origin/feat/recovery",
            "the upstream is stated only because one actually exists");
        note.Should().Contain("2 uncommitted paths (src/a.cs, src/b.cs)");
        note.Should().Contain("a1b2c3d fix: the thing");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Probe: real repository, real shell
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_WhenNotAGitRepository_ReportsThatAsAFinding()
    {
        var dir = Directory.CreateTempSubdirectory("agentmesh-notarepo-").FullName;
        try
        {
            var reconciler = new ShellWorkspaceReconciler(
                shellRunner: ShellRunner.RunIn(dir));

            var report = await reconciler.ReconcileAsync(dir);

            report.Inspected.Should().BeTrue("not being a repository is a positive finding");
            report.IsGitRepository.Should().BeFalse();

            var note = EnvironmentRecoveryNote.Format(report);
            note.Should().NotBeNull()
                .And.Contain("Not a git repository")
                .And.Contain("no branch, HEAD commit, or stash");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenDirectoryMissing_ProducesNoNoteRatherThanAWrongOne()
    {
        var reconciler = new ShellWorkspaceReconciler(
            shellRunner: ShellRunner.RunIn("/workspace"));

        var report = await reconciler.ReconcileAsync("/workspace/definitely-not-here-42");

        report.Inspected.Should().BeFalse();
        report.FailureReason.Should().Contain("does not exist");

        // The load-bearing assertion: no note is rendered at all. A header full of "unknown" would
        // still tell the agent not to run git status, converting an unknown into a lie it is
        // instructed to trust.
        EnvironmentRecoveryNote.Format(report).Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_OnDetachedHead_DoesNotClaimABranch()
    {
        var repo = new TempGitRepository();
        await using var _ = repo;
        await repo.InitAsync();
        await repo.CommitAsync("first", ("a.txt", "a"));
        await repo.CommitAsync("second", ("b.txt", "b"));
        await repo.CheckoutDetachedAsync();

        var report = await new ShellWorkspaceReconciler(
            shellRunner: ShellRunner.RunIn(repo.Path)).ReconcileAsync(repo.Path);

        report.DetachedHead.Should().BeTrue();
        report.Branch.Should().BeEmpty();

        var note = EnvironmentRecoveryNote.Format(report);
        note.Should().Contain("detached HEAD");
        note.Should().NotContain("Active Branch: (no upstream",
            "a detached HEAD is not a branch with a missing upstream; conflating them sends the agent " +
            "to create a branch it is not on");
    }

    [Fact]
    public async Task Reconcile_WhenBranchHasNoUpstream_SaysSoInsteadOfInventingOrigin()
    {
        var repo = new TempGitRepository();
        await using var _ = repo;
        await repo.InitAsync();
        await repo.CommitAsync("initial", ("a.txt", "a"));
        await repo.CheckoutBranchAsync("feat/local-only");

        var report = await new ShellWorkspaceReconciler(
            shellRunner: ShellRunner.RunIn(repo.Path)).ReconcileAsync(repo.Path);

        // The spec's template hardcodes "(Tracking: origin/{branch})". This branch has no upstream, so
        // that string would name a remote branch that does not exist and invite the agent to push to it
        // or diff against it.
        report.UpstreamRef.Should().BeNull();
        EnvironmentRecoveryNote.Format(report).Should()
            .Contain("no upstream configured; not yet pushed")
            .And.NotContain("Tracking: origin/feat/local-only");
    }

    [Fact]
    public async Task Reconcile_CleanTree_SaysCleanRatherThanZeroFiles()
    {
        var repo = new TempGitRepository();
        await using var _ = repo;
        await repo.InitAsync();
        await repo.CommitAsync("initial", ("a.txt", "a"));

        var report = await new ShellWorkspaceReconciler(
            shellRunner: ShellRunner.RunIn(repo.Path)).ReconcileAsync(repo.Path);

        report.ModifiedFilesCount.Should().Be(0);
        report.HasUncommittedWork.Should().BeFalse();
        report.LooksMidTask.Should().BeFalse();

        EnvironmentRecoveryNote.Format(report).Should().Contain("clean (no uncommitted files)");
    }

    /// <summary>
    /// A commit message containing a forged section marker cannot forge a section boundary.
    /// </summary>
    /// <remarks>
    /// The probe emits markers on their own lines and reads each section as "everything until the next
    /// marker on its own line". A commit message is arbitrary text the agent can influence, and if a
    /// marker could be forged the parser would attribute later sections' content to an earlier one —
    /// which in the worst case makes a clean tree look dirty, or a dirty one look clean.
    /// </remarks>
    [Fact]
    public async Task Reconcile_CommitMessageContainingMarker_CannotForgeASection()
    {
        var repo = new TempGitRepository();
        await using var _ = repo;
        await repo.InitAsync();
        await repo.CommitAsync("initial", ("a.txt", "a"));
        await repo.CommitAsync("commit mentioning @@FILES@@ forged marker", ("b.txt", "b"));

        var report = await new ShellWorkspaceReconciler(
            shellRunner: ShellRunner.RunIn(repo.Path)).ReconcileAsync(repo.Path);

        report.HeadCommitOneline.Should().Contain("forged marker");
        report.ModifiedFiles.Should().BeEmpty("the forged marker is inside the commit line, not a section");
        report.ModifiedFilesCount.Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_TruncatesTheFileListButReportsTheExactCount()
    {
        var repo = new TempGitRepository();
        await using var _ = repo;
        await repo.InitAsync();
        await repo.CommitAsync("initial", ("seed.txt", "seed"));

        // More files than the note reports, to force the truncation path.
        for (var i = 0; i < WorkspaceReconciliationReport.MaxReportedFiles + 12; i++)
        {
            await repo.WriteAsync($"src/generated/file{i:D3}.cs", $"content {i}");
        }

        var report = await new ShellWorkspaceReconciler(
            shellRunner: ShellRunner.RunIn(repo.Path)).ReconcileAsync(repo.Path);

        report.ModifiedFilesCount.Should().Be(WorkspaceReconciliationReport.MaxReportedFiles + 12,
            "the count comes from `wc -l` over the whole status with --untracked-files=all, so it stays " +
            "exact even though the list is a sample. Without -uall this reports 1 (`?? src/`), because " +
            "git collapses a fully-untracked tree into its directory entry.");
        report.ModifiedFiles.Should().HaveCount(WorkspaceReconciliationReport.MaxReportedFiles);
        report.ModifiedFilesTruncated.Should().BeTrue();

        var note = EnvironmentRecoveryNote.Format(report);
        note.Should().Contain("12 more", "the note must say how much it elided");
        note.Should().Contain("run git status for the full list",
            "silently truncating would make the agent plan around an incomplete file set");
    }

    [Fact]
    public void ProbeScript_QuotesAPathContainingASingleQuote()
    {
        // working_directory is operator-supplied text that is embedded in a bash script. Without the
        // '\'' idiom, a path containing a quote terminates the literal and the remainder executes as
        // shell syntax inside the agent's sandbox.
        var script = ShellWorkspaceReconciler.BuildProbeScript("/tmp/we'ird; touch /tmp/pwned");

        script.Should().Contain(@"'/tmp/we'\''ird; touch /tmp/pwned'");
        script.Should().Contain("cd -- ");

        // The injected command must not be executable as a statement of its own.
        var cdLine = script.Split('\n')[0];
        cdLine.Should().StartWith("cd -- '");
        cdLine.IndexOf("; touch", StringComparison.Ordinal).Should().BeGreaterThan(-1,
            "the payload is present, but inside a single-quoted literal");
        cdLine.EndsWith("' 2>/dev/null || exit 42").Should().BeTrue(
            "the quote was re-opened before the redirect, so the payload stayed data");
    }

    [Fact]
    public void ProbeScript_CapsOutputBelowTheSpilloverThreshold()
    {
        // The probe runs through SandboxShell, which spills anything over SpillLineThreshold lines and
        // hands back a head/tail preview. A file list truncated by *that* mechanism would lose its
        // middle entries with no marker saying so, so the probe caps the list itself instead.
        var script = ShellWorkspaceReconciler.BuildProbeScript("/tmp/any");

        var fileCap = System.Text.RegularExpressions.Regex.Match(script, @"head -n (\d+)");
        fileCap.Success.Should().BeTrue();
        int.Parse(fileCap.Groups[1].Value).Should()
            .BeLessThan(CommandExecutionPolicy.SpillLineThreshold,
                "the sample must leave room for the other sections under the spillover line budget");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Formatter branches that a real repository cannot cheaply produce
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_WhenNothingWasInspected_ReturnsNull()
    {
        EnvironmentRecoveryNote.Format(null).Should().BeNull();
        EnvironmentRecoveryNote.Format(
            WorkspaceReconciliationReport.Failed("/tmp/x", "git is not installed on this host")).Should().BeNull();
    }

    [Fact]
    public void Format_WhenBranchUnreadable_SaysUnknownRatherThanDetached()
    {
        // An empty branch name with no detached marker means the probe could not read it. Reporting
        // "detached HEAD" would send the agent to look for a commit it is not standing on.
        var report = new WorkspaceReconciliationReport(
            "/tmp/repo", Inspected: true, IsGitRepository: true, Branch: "", DetachedHead: false);

        EnvironmentRecoveryNote.Format(report).Should()
            .Contain("unknown (git branch --show-current returned nothing)")
            .And.NotContain("detached");
    }

    [Fact]
    public void Format_WithNoCommitsYet_SaysNoneRatherThanLeavingTheFieldBlank()
    {
        var report = new WorkspaceReconciliationReport(
            "/tmp/repo", Inspected: true, IsGitRepository: true, Branch: "main", HeadCommitOneline: null);

        EnvironmentRecoveryNote.Format(report).Should().Contain("Head Commit: none (no commits yet");
    }

    [Fact]
    public void Format_CapsTheStashListButReportsTheTotal()
    {
        var stashes = Enumerable.Range(0, WorkspaceReconciliationReport.MaxReportedStashes + 3)
            .Select(i => $"stash@{{{i}}}: On main: wip {i}")
            .ToList();

        var report = new WorkspaceReconciliationReport(
            "/tmp/repo", Inspected: true, IsGitRepository: true, Branch: "main", StashEntries: stashes);

        var note = EnvironmentRecoveryNote.Format(report);
        note.Should().Contain("8 entries");

        // The elision marker belongs on the stash line, which is not the last line of the note (the
        // anti-exploration instruction and the stash warning follow it), so assert on that line rather
        // than on the note's tail.
        var stashLine = note!.Split('\n').Single(l => l.StartsWith("* Stash: ", StringComparison.Ordinal));
        stashLine.Should().EndWith("…", "the note must signal that it elided stash entries");
        stashLine.Should().NotContain("wip 7", "entries past the cap are not rendered");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the probe script through a real <c>bash</c> in a real directory.
    /// </summary>
    /// <remarks>
    /// Not <see cref="SandboxShell"/>: the sandbox applies log spillover, and a spillover preview of a
    /// section-delimited probe would make these tests assert on the spillover engine's truncation
    /// rather than on the reconciler's parsing. The production path is exercised by
    /// <see cref="ShellRunner.RunIn"/> matching the same exit-code protocol, and the spillover
    /// interaction is bounded separately by
    /// <see cref="ProbeScript_CapsOutputBelowTheSpilloverThreshold"/>.
    /// </remarks>
    private static class ShellRunner
    {
        public static Func<string, string, CancellationToken, Task<CommandExecutionResult>> RunIn(string cwd) =>
            async (script, workingDirectory, ct) =>
            {
                var dir = string.IsNullOrWhiteSpace(workingDirectory) ? cwd : workingDirectory;
                var result = await RunBashAsync(script, dir, ct);
                return result;
            };

        internal static async Task<CommandExecutionResult> RunBashAsync(string script, string dir, CancellationToken ct)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = System.IO.Directory.Exists(dir) ? dir : "/tmp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("cat | /bin/bash");

            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.StandardInput.WriteAsync(script.AsMemory(), ct);
            process.StandardInput.Close();

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var exit = process.ExitCode;
            var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();

            return new CommandExecutionResult(Success: exit == 0, ExitCode: exit, Output: combined);
        }
    }

    /// <summary>
    /// A throwaway git repository, created per test and deleted on dispose.
    /// </summary>
    /// <remarks>
    /// Real git rather than a fixture of canned <c>git</c> output, because the behaviour under test is
    /// what <c>git</c> actually prints: <c>branch --show-current</c> exiting 0 with empty output on a
    /// detached HEAD, <c>rev-parse @{u}</c> failing on a branch with no upstream, porcelain's two-column
    /// status prefixes. A canned fixture asserts that the parser agrees with whatever the author assumed
    /// git prints.
    /// </remarks>
    private sealed class TempGitRepository : IAsyncDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("agentmesh-repo-").FullName;

        public async Task InitAsync()
        {
            await GitAsync("init -q");
            await GitAsync("config user.email test@example.com");
            await GitAsync("config user.name Test");
            await GitAsync("config commit.gpgsign false");
            // A fixed initial branch name, so the default-branch assertion does not depend on the
            // developer's git version or their global init.defaultBranch setting.
            await GitAsync("config init.defaultBranch main");
        }

        public async Task WriteAsync(string relativePath, string content)
        {
            var full = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        public async Task CommitAsync(string message, params (string Path, string Content)[] files)
        {
            foreach (var (p, c) in files)
            {
                await WriteAsync(p, c);
            }

            await GitAsync("add -A");
            await GitAsync($"commit -q -m \"{message.Replace("\"", "'")}\"");
        }

        public Task CheckoutBranchAsync(string branch) => GitAsync($"checkout -q -b {branch}");

        public Task CheckoutDetachedAsync() => GitAsync("checkout -q --detach HEAD");

        public Task StashAsync(string message) =>
            GitAsync($"stash -q -u -m \"{message.Replace("\"", "'")}\"");

        /// <summary>
        /// Runs the reconciler's probe script inside this repository.
        /// </summary>
        public Task<CommandExecutionResult> RunProbeAsync(string script, string workingDirectory) =>
            ShellRunner.RunBashAsync(script, workingDirectory, CancellationToken.None);

        private async Task GitAsync(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"git {arguments}");

            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {arguments} exited {process.ExitCode}: {stderr.Trim()} (stdout: {stdout.Trim()})");
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over; the OS reclaims /tmp.
            }

            return ValueTask.CompletedTask;
        }
    }
}
