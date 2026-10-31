using System.Globalization;
using System.Text;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// Default <see cref="IWorkspaceReconciler"/>: one bounded shell probe, parsed into a report.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a single probe instead of the spec's four commands.</strong> The sketch lists
/// <c>git branch --show-current</c>, <c>git status --short</c>, <c>git log -1 --oneline</c> and
/// <c>git stash list</c> as separate invocations. Four <see cref="SandboxShell"/> calls means four
/// process spawns, four setsid/teardown ladders and four spillover evaluations, on the critical path
/// of every single turn, to read state that one <c>bash</c> script collects in a fork of
/// <c>git</c> per section anyway. It also makes partial failure the normal case: three commands
/// succeed, one times out, and the note is assembled from a mixture the reader cannot tell apart
/// from a workspace that genuinely looks that way. One probe has one exit status and one budget.
/// </para>
/// <para>
/// <strong>Why the output is section-delimited rather than parsed from human-readable git prose.</strong>
/// <c>git status --short</c> output is already machine-oriented, but <c>git log -1 --oneline</c> can
/// contain any character a commit message can, including newlines and text that looks like a section
/// marker. Sections are therefore emitted on lines we control, and each section is read as
/// "everything until the next marker", so commit text cannot forge a boundary.
/// </para>
/// <para>
/// <strong>Why the probe never fails the turn.</strong> Every abnormal exit is mapped to a report
/// that says what could not be learned. The alternative — throwing — propagates out of the activity,
/// Temporal retries it, and the retry re-runs the whole turn. A workspace we cannot inspect is a
/// prompt-quality problem, not an execution fault, and it is handled by omitting the note.
/// </para>
/// <para>
/// <paramref name="log"/> is an <see cref="Action{T}"/> sink rather than an <c>ILogger</c> because
/// Core declares no logging package (<see cref="SandboxShell"/> takes the same shape). Naming the
/// dependency here would pull Microsoft.Extensions.Logging into every consumer of Core, including
/// the CLI, for one warning line.
/// </para>
/// </remarks>
public class ShellWorkspaceReconciler(
    Func<string, string, CancellationToken, Task<CommandExecutionResult>>? shellRunner = null,
    Action<string>? log = null) : IWorkspaceReconciler
{
    /// <summary>
    /// Wall-clock budget for the probe.
    /// </summary>
    /// <remarks>
    /// Passed as <see cref="SandboxShell"/>'s <c>trustedTimeout</c>, so it is honoured verbatim
    /// rather than clamped into <see cref="CommandExecutionPolicy.MinTimeoutSeconds"/>'s range.
    /// <para>
    /// 10 seconds is generous for four <c>git</c> invocations on a warm cache and tight enough to be
    /// worth the trade: a repository on a dead NFS mount would otherwise stall every turn's prompt
    /// assembly for the 180-second default, and the harness would look hung rather than slow. A probe
    /// that exceeds this yields no note, and the agent does what it does today — discovers its own
    /// state. That is the pre-feature behaviour, which is an acceptable floor and not a regression.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(10);

    /// <summary>Exit code the probe returns when the directory does not exist or is not enterable.</summary>
    public const int MissingDirectoryExitCode = 42;

    /// <summary>Exit code the probe returns when the directory is not inside a git work tree.</summary>
    public const int NotAGitRepositoryExitCode = 43;

    /// <summary>Exit code the probe returns when the <c>git</c> binary is absent.</summary>
    public const int GitNotInstalledExitCode = 44;

    private readonly Func<string, string, CancellationToken, Task<CommandExecutionResult>> _shellRunner =
        shellRunner ?? RunInSandboxAsync;

    public async Task<WorkspaceReconciliationReport> ReconcileAsync(
        string workspacePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return WorkspaceReconciliationReport.Failed(workspacePath ?? string.Empty, "no working directory resolved");
        }

        var path = WorkspacePathResolver.Normalize(workspacePath);
        var script = BuildProbeScript(path);

        CommandExecutionResult result;
        try
        {
            result = await _shellRunner(script, path, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // The turn is being torn down; there is nothing to report and nothing to salvage.
        }
        catch (Exception ex)
        {
            // A shell host that cannot spawn at all is a different failure from a workspace that
            // cannot be inspected, and it is worth a distinct log line: the first means the worker is
            // broken, the second means the checkout is.
            log?.Invoke(
                $"Workspace probe could not run in {path}: {ex.GetType().Name}: {ex.Message}. " +
                "A shell host that cannot spawn is a different failure from a workspace that cannot " +
                "be inspected: the first means the worker is broken, the second means the checkout is.");
            return WorkspaceReconciliationReport.Failed(path, $"probe could not run: {ex.GetType().Name}");
        }

        return Parse(path, result);
    }

    /// <summary>
    /// The probe script, collecting every field in one shell.
    /// </summary>
    /// <remarks>
    /// Static and internal so a test can assert the shape of what gets executed — in particular that
    /// the path is quoted, and that the file list is capped before it reaches the spillover engine.
    /// <para>
    /// The path is embedded inside single quotes with each <c>'</c> replaced by the
    /// <c>'\''</c> idiom. That is the only construct that is safe inside single quotes in POSIX
    /// shells, and it is required rather than defensive: <c>working_directory</c> is operator-supplied
    /// text, and a path containing a quote would otherwise terminate the literal and turn the rest of
    /// the value into shell syntax executed inside the agent's sandbox.
    /// </para>
    /// <para>
    /// <c>cd --</c> uses the end-of-options marker so a path beginning with <c>-</c> is treated as a
    /// path rather than as a <c>cd</c> flag.
    /// </para>
    /// </remarks>
    internal static string BuildProbeScript(string workspacePath)
    {
        var quoted = "'" + workspacePath.Replace("'", @"'\''") + "'";
        var fileCap = WorkspaceReconciliationReport.MaxReportedFiles.ToString(CultureInfo.InvariantCulture);
        var stashCap = WorkspaceReconciliationReport.MaxReportedStashes.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("cd -- ").Append(quoted).Append(" 2>/dev/null || exit ").Append(MissingDirectoryExitCode).Append('\n');
        sb.Append("command -v git >/dev/null 2>&1 || exit ").Append(GitNotInstalledExitCode).Append('\n');
        sb.Append("git rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit ").Append(NotAGitRepositoryExitCode).Append('\n');

        // HEAD type before branch: `git branch --show-current` prints nothing for a detached HEAD and
        // exits 0, so an empty branch name is ambiguous between "detached" and "could not read".
        // Symbolic-ref is the discriminator, and it is cheap.
        AppendSection(sb, SectionHeadType);
        sb.Append("if git symbolic-ref -q HEAD >/dev/null 2>&1; then echo branch; else echo detached; fi\n");

        AppendSection(sb, SectionBranch);
        sb.Append("git branch --show-current 2>/dev/null\n");

        // The real upstream, not a synthesized origin/{branch}. `@{u}` is what `git push`/`git pull`
        // would actually use, so the note describes the same relationship the tools do.
        AppendSection(sb, SectionUpstream);
        sb.Append("git rev-parse --abbrev-ref --symbolic-full-name @{u} 2>/dev/null\n");

        // `-uall` (--untracked-files=all) rather than the default. Measured: with the default, a
        // repository holding 42 new files under a previously-untracked src/ tree reports
        // `?? src/` — one entry. The note would then say "1 uncommitted file (?? src/)", which is
        // wrong twice over: the count is off by 41, and the path named is a directory the agent did not
        // create. An agent told it has one uncommitted file behaves completely differently from one told
        // it has 42 new files in a tree it is mid-way through generating, and the second is the truth.
        //
        // The cost is that git descends into fully-untracked directories instead of collapsing them, so
        // a repository with an un-ignored node_modules produces a large count. That is handled by the
        // cap below and by the note's wording ("paths", with the elided count stated) rather than by
        // going back to a form that under-reports real work.
        AppendSection(sb, SectionCount);
        sb.Append("git status --porcelain --untracked-files=all 2>/dev/null | wc -l | tr -d ' '\n");

        AppendSection(sb, SectionFiles);
        sb.Append("git status --porcelain --untracked-files=all 2>/dev/null | head -n ").Append(fileCap).Append('\n');

        AppendSection(sb, SectionStash);
        sb.Append("git stash list 2>/dev/null | head -n ").Append(stashCap).Append('\n');

        AppendSection(sb, SectionHead);
        sb.Append("git log -1 --oneline 2>/dev/null\n");

        // Sentinel so a truncated probe (timeout mid-script) is distinguishable from a workspace
        // whose last section happened to be empty.
        AppendSection(sb, SectionDone);
        return sb.ToString();
    }

    private const string SectionHeadType = "@@HEADTYPE@@";
    private const string SectionBranch = "@@BRANCH@@";
    private const string SectionUpstream = "@@UPSTREAM@@";
    private const string SectionCount = "@@COUNT@@";
    private const string SectionFiles = "@@FILES@@";
    private const string SectionStash = "@@STASH@@";
    private const string SectionHead = "@@HEAD@@";
    private const string SectionDone = "@@DONE@@";

    /// <summary>
    /// Emits a section marker on its own line.
    /// </summary>
    /// <remarks>
    /// Plain <c>echo</c> with the marker single-quoted: the markers are compile-time literals
    /// containing no shell metacharacters, so there is nothing for quoting to defend against here, and
    /// the quotes make that explicit to a reader who is checking whether this line is injectable.
    /// <see cref="SplitIntoSections"/> honours a marker only when it is the entire line, which is what
    /// <c>echo</c> guarantees.
    /// </remarks>
    private static void AppendSection(StringBuilder sb, string marker) =>
        sb.Append("echo '").Append(marker).Append("'\n");

    /// <summary>
    /// Turns a probe result into a report. Internal so parsing is unit-testable without a shell.
    /// </summary>
    internal static WorkspaceReconciliationReport Parse(string workspacePath, CommandExecutionResult result)
    {
        var output = result.Output ?? string.Empty;

        switch (result.ExitCode)
        {
            case MissingDirectoryExitCode:
                return WorkspaceReconciliationReport.Failed(
                    workspacePath, "working directory does not exist or is not enterable", output);

            case GitNotInstalledExitCode:
                return WorkspaceReconciliationReport.Failed(workspacePath, "git is not installed on this host", output);

            case NotAGitRepositoryExitCode:
                // Inspected=true: "not a repository" is a positive finding, and the note is worth
                // printing for it. This is the one non-repo case we can assert with confidence.
                return new WorkspaceReconciliationReport(
                    workspacePath,
                    Inspected: true,
                    IsGitRepository: false,
                    RawOutput: output);
        }

        if (!result.Success && !output.Contains(SectionBranch, StringComparison.Ordinal))
        {
            // No markers at all means the script never got to the first section. A non-zero exit with
            // markers present is normal (a git subcommand can fail while the rest succeeded), so the
            // presence of the markers — not the exit code — decides whether there is a report here.
            return WorkspaceReconciliationReport.Failed(
                workspacePath,
                result.TimedOut
                    ? $"workspace probe timed out after {ProbeBudget.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s"
                    : $"workspace probe exited {result.ExitCode.ToString(CultureInfo.InvariantCulture)}",
                output);
        }

        var sections = SplitIntoSections(output);

        var headType = sections.GetValueOrDefault(SectionHeadType) ?? string.Empty;
        var detached = headType.Equals("detached", StringComparison.OrdinalIgnoreCase);
        var branch = sections.GetValueOrDefault(SectionBranch) ?? string.Empty;

        var countRaw = sections.GetValueOrDefault(SectionCount) ?? string.Empty;
        var fileLines = SplitLines(sections.GetValueOrDefault(SectionFiles));

        // Prefer the reported count; fall back to the sample size when `wc` was unavailable. The
        // fallback is only reachable on a host without coreutils, and it under-reports past the
        // sample cap — which is why the note also prints how many paths it elided.
        var count = int.TryParse(countRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fileLines.Count;

        var truncated = count > fileLines.Count;

        return new WorkspaceReconciliationReport(
            workspacePath,
            Inspected: true,
            IsGitRepository: true,
            Branch: branch,
            DetachedHead: detached,
            UpstreamRef: NullIfBlank(sections.GetValueOrDefault(SectionUpstream)),
            ModifiedFilesCount: count,
            ModifiedFiles: fileLines,
            ModifiedFilesTruncated: truncated,
            HeadCommitOneline: NullIfBlank(sections.GetValueOrDefault(SectionHead)),
            StashEntries: SplitLines(sections.GetValueOrDefault(SectionStash)),
            RawOutput: output);
    }

    /// <summary>
    /// Splits probe output into marker → payload, reading each payload as "everything up to the next
    /// marker we recognize".
    /// </summary>
    /// <remarks>
    /// Line-based rather than regex-based, and it scans for the known marker set instead of any
    /// <c>@@…@@</c> token, so commit text or a filename that happens to contain <c>@@</c> cannot open
    /// a section. A marker is only honoured when it is the entire line, which is how the probe emits
    /// them.
    /// </remarks>
    internal static Dictionary<string, string> SplitIntoSections(string output)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            SectionHeadType, SectionBranch, SectionUpstream, SectionCount,
            SectionFiles, SectionStash, SectionHead, SectionDone,
        };

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var buffer = new List<string>();

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (known.Contains(line))
            {
                if (current is not null && !result.ContainsKey(current))
                {
                    result[current] = string.Join("\n", buffer).Trim();
                }

                current = line;
                buffer.Clear();
                continue;
            }

            if (current is not null) buffer.Add(rawLine);
        }

        if (current is not null && !result.ContainsKey(current))
        {
            result[current] = string.Join("\n", buffer).Trim();
        }

        return result;
    }

    private static IReadOnlyList<string> SplitLines(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return [];

        return section
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The production shell host: <see cref="SandboxShell"/>, with the probe's own budget.
    /// </summary>
    /// <remarks>
    /// <c>trustedTimeout</c> rather than <c>TimeoutSeconds</c>: the probe budget is shorter than
    /// <see cref="CommandExecutionPolicy.MinTimeoutSeconds"/>, and the clamp exists to protect the
    /// sandbox from a model-supplied number. This is in-process code, which is exactly the case the
    /// escape hatch was added for.
    /// </remarks>
    private static async Task<CommandExecutionResult> RunInSandboxAsync(
        string script,
        string workingDirectory,
        CancellationToken ct)
    {
        var args = new RunCommandArgs(Command: script, WorkingDirectory: workingDirectory);

        return await SandboxShell.RunAsync(
            args,
            ct,
            executionId: "workspace-reconcile",
            trustedTimeout: ProbeBudget);
    }
}
