using System.Globalization;
using System.Text;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// What the harness observed about a workspace before it prompted the model.
/// </summary>
/// <remarks>
/// Every field is a measurement, not a claim. That distinction is the reason this type exists: an
/// agent's own summary of its state is written from memory, and after a restart the memory is gone.
/// </remarks>
/// <param name="WorkingDirectory">The directory that was inspected.</param>
/// <param name="Inspected">
/// True when the probe ran and produced a usable answer. False means the note must not be rendered
/// at all — see <see cref="FailureReason"/>.
/// </param>
/// <param name="IsGitRepository">False for a non-repo workspace, which is legal but changes the note.</param>
/// <param name="Branch">
/// Current branch, or empty on a detached HEAD. Empty is not "unknown": <see cref="DetachedHead"/>
/// distinguishes the two, and conflating them would tell an agent to create a branch it is already on.
/// </param>
/// <param name="DetachedHead">True when HEAD is not on a branch.</param>
/// <param name="UpstreamRef">
/// The tracking ref (<c>origin/feat-x</c>) when one is configured. Null means no upstream, which the
/// note reports as "not pushed" rather than inventing <c>origin/{branch}</c> — the spec's literal
/// <c>(Tracking: origin/{branch})</c> is a guess, and a wrong one here tells an agent to push to a
/// remote branch that does not exist and to read a nonexistent diff.
/// </param>
/// <param name="ModifiedFilesCount">
/// Exact count from <c>git status --porcelain</c>. Exact even when <see cref="ModifiedFiles"/> is
/// truncated, which is the whole reason the two are separate values.
/// </param>
/// <param name="ModifiedFiles">
/// Up to <see cref="MaxReportedFiles"/> paths. Null and empty differ the same way they do in the
/// persisted checkpoint: null means the section was never produced (probe died before it), empty
/// means the tree was inspected and found clean.
/// </param>
/// <param name="ModifiedFilesTruncated">True when the list is a sample of a longer one.</param>
/// <param name="HeadCommitOneline">Output of <c>git log -1 --oneline</c>, null before the first commit.</param>
/// <param name="StashEntries">Stash list, newest first.</param>
/// <param name="FailureReason">Why <see cref="Inspected"/> is false.</param>
/// <param name="RawOutput">The probe's combined output, for diagnostics and tests.</param>
public record WorkspaceReconciliationReport(
    string WorkingDirectory,
    bool Inspected,
    bool IsGitRepository = false,
    string Branch = "",
    bool DetachedHead = false,
    string? UpstreamRef = null,
    int ModifiedFilesCount = 0,
    IReadOnlyList<string>? ModifiedFiles = null,
    bool ModifiedFilesTruncated = false,
    string? HeadCommitOneline = null,
    IReadOnlyList<string>? StashEntries = null,
    string? FailureReason = null,
    string? RawOutput = null)
{
    /// <summary>
    /// Paths named in the note before it stops being a useful read.
    /// </summary>
    /// <remarks>
    /// 30, not "as many as exist", because the probe's output travels through
    /// <see cref="SandboxShell"/>, which spills anything over
    /// <see cref="CommandExecutionPolicy.SpillLineThreshold"/> lines and hands back a head/tail
    /// preview. A 200-file list would therefore be *truncated by the spillover engine* rather than by
    /// us, and the middle of it would vanish silently — the preview keeps the first 20 and last 30
    /// lines, so a file list would lose its middle entries with no marker saying so. Budgeting the
    /// whole probe (branch, upstream, counts, head, stashes, plus 30 paths) to stay under 60 lines
    /// means we control the truncation, report it in the note, and keep the exact count regardless.
    /// </remarks>
    public const int MaxReportedFiles = 30;

    /// <summary>Stash entries named before the note stops being a useful read.</summary>
    public const int MaxReportedStashes = 5;

    /// <summary>A report for a workspace that could not be inspected.</summary>
    public static WorkspaceReconciliationReport Failed(string workingDirectory, string reason, string? raw = null) =>
        new(workingDirectory, Inspected: false, FailureReason: reason, RawOutput: raw);

    /// <summary>True when the tree has uncommitted work.</summary>
    public bool HasUncommittedWork => ModifiedFilesCount > 0;

    /// <summary>Paths, or empty when none were reported. Never null for callers to null-test.</summary>
    public IReadOnlyList<string> FilesOrEmpty => ModifiedFiles ?? [];

    /// <summary>Stash entries, or empty when there are none.</summary>
    public IReadOnlyList<string> StashesOrEmpty => StashEntries ?? [];

    /// <summary>True when the agent has work parked in a stash it may have forgotten about.</summary>
    public bool HasStash => StashEntries is { Count: > 0 };

    /// <summary>
    /// True when the workspace looks like it was left mid-task: dirty tree, or a stash, or a branch
    /// that is not the base.
    /// </summary>
    /// <remarks>
    /// Drives whether the note is framed as "resumed, here is your state" or "clean slate". A clean
    /// workspace does not need the anti-exploration instruction, and printing it anyway teaches the
    /// agent to distrust the note in the cases where it matters.
    /// </remarks>
    public bool LooksMidTask =>
        IsGitRepository && (HasUncommittedWork || HasStash || DetachedHead);
}

/// <summary>
/// Inspects a workspace's git state so the agent does not have to.
/// </summary>
public interface IWorkspaceReconciler
{
    /// <summary>
    /// Runs the pre-turn inspection in <paramref name="workspacePath"/>.
    /// </summary>
    /// <remarks>
    /// Never throws for a workspace that merely looks odd (not a repo, empty repo, missing git
    /// binary, no read permission). Those come back with
    /// <see cref="WorkspaceReconciliationReport.Inspected"/> false, or as a partially filled report,
    /// because a reconciliation failure must not prevent the turn from running — an agent that
    /// explores for two turns is a smaller loss than a task that makes no progress at all.
    /// </remarks>
    Task<WorkspaceReconciliationReport> ReconcileAsync(string workspacePath, CancellationToken ct = default);
}

/// <summary>
/// Formats the <c>[AUTOMATIC ENVIRONMENT RECOVERY NOTE]</c> block.
/// </summary>
/// <remarks>
/// Split from the reconciler because the two are verified differently: the reconciler needs a real
/// repository and a shell, the formatter is pure string work that every branch of is worth asserting
/// on directly. It also lets the CLI runner reuse the exact same note without the worker.
/// </remarks>
public static class EnvironmentRecoveryNote
{
    /// <summary>The literal header the job spec fixes. Tests and the DoD both match on this string.</summary>
    public const string Header = "[AUTOMATIC ENVIRONMENT RECOVERY NOTE]";

    /// <summary>
    /// Builds the note, or returns null when there is nothing true to say.
    /// </summary>
    /// <remarks>
    /// Returning null rather than a note full of "unknown" is the load-bearing decision here. A note
    /// that asserts nothing still occupies the same position in the prompt and still ends with
    /// "do not spend turns running git status" — so the agent skips the very commands that would
    /// have told it what the note could not. A confidently useless header is worse than no header,
    /// because it converts an unknown into a lie the agent is instructed to trust.
    /// </remarks>
    public static string? Format(WorkspaceReconciliationReport? report)
    {
        if (report is null || !report.Inspected) return null;

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');

        if (!report.IsGitRepository)
        {
            // Still worth printing: "this is not a git repository" is a fact the agent cannot get
            // from `git status` without first being confused by its exit code, and it forecloses an
            // entire category of exploration (git log, git diff, branch switching).
            sb.Append("The harness pre-inspected this workspace:\n");
            sb.Append("* Working Directory: ").Append(report.WorkingDirectory).Append('\n');
            sb.Append("* Not a git repository — there is no branch, HEAD commit, or stash to inspect.\n");
            sb.Append("Do not spend turns running git status, git log, or pwd to discover this state.");
            return sb.ToString();
        }

        sb.Append("Session resumed/restarted. The workspace has been pre-inspected by the harness:\n");

        sb.Append("* Working Directory: ").Append(report.WorkingDirectory).Append('\n');

        // Branch line. The tracking ref is only appended when one actually exists — see
        // WorkspaceReconciliationReport.UpstreamRef for why the spec's unconditional origin/{branch}
        // was rejected.
        sb.Append("* Active Branch: ");
        if (report.DetachedHead)
        {
            sb.Append("(detached HEAD)");
        }
        else if (!string.IsNullOrWhiteSpace(report.Branch))
        {
            sb.Append(report.Branch);
            if (!string.IsNullOrWhiteSpace(report.UpstreamRef))
            {
                sb.Append(" (Tracking: ").Append(report.UpstreamRef).Append(')');
            }
            else
            {
                sb.Append(" (no upstream configured; not yet pushed)");
            }
        }
        else
        {
            // An empty branch name with no detached-HEAD marker means the probe could not read it.
            // Say exactly that rather than "(detached HEAD)", which would send the agent to look for
            // a commit it is not on.
            sb.Append("unknown (git branch --show-current returned nothing)");
        }

        sb.Append('\n');

        sb.Append("* Working Tree: ");
        if (report.ModifiedFilesCount == 0)
        {
            sb.Append("clean (no uncommitted files)");
        }
        else
        {
            // "paths", not "files": a porcelain entry can name a directory (an untracked tree with no
            // tracked content inside it yet) or a rename pair, and calling those "files" is the same
            // category of overclaim that -uall was added to avoid.
            sb.Append(report.ModifiedFilesCount)
              .Append(report.ModifiedFilesCount == 1 ? " uncommitted path" : " uncommitted paths");

            var files = report.FilesOrEmpty;
            if (files.Count > 0)
            {
                sb.Append(" (").Append(string.Join(", ", files));
                if (report.ModifiedFilesTruncated)
                {
                    sb.Append(", … ")
                      .Append((report.ModifiedFilesCount - files.Count).ToString(CultureInfo.InvariantCulture))
                      .Append(" more — run git status for the full list");
                }
                sb.Append(')');
            }
        }
        sb.Append('\n');

        sb.Append("* Stash: ");
        var stashes = report.StashesOrEmpty;
        if (stashes.Count == 0)
        {
            sb.Append("none");
        }
        else
        {
            sb.Append(stashes.Count == 1 ? "1 entry: " : $"{stashes.Count.ToString(CultureInfo.InvariantCulture)} entries: ");
            sb.Append(string.Join(" | ", stashes.Take(WorkspaceReconciliationReport.MaxReportedStashes)));
            if (stashes.Count > WorkspaceReconciliationReport.MaxReportedStashes)
            {
                sb.Append(" …");
            }
        }
        sb.Append('\n');

        sb.Append("* Head Commit: ")
          .Append(string.IsNullOrWhiteSpace(report.HeadCommitOneline)
              ? "none (no commits yet on this branch)"
              : report.HeadCommitOneline)
          .Append('\n');

        // The instruction. Scoped to the state actually reported: a clean tree does not need "do not
        // check for uncommitted work", and telling an agent not to explore when the note could not
        // establish the branch would be the confidently-useless case described above.
        sb.Append("Do not spend turns running git status, git log, or pwd to discover this state.");

        if (report.HasStash)
        {
            sb.Append("\nThere is work in the stash: check it before reimplementing anything that looks missing.");
        }

        if (report.HasUncommittedWork)
        {
            sb.Append("\nThe working tree is dirty: do not reset, clean, or check out over it.");
        }

        return sb.ToString().TrimEnd();
    }
}
