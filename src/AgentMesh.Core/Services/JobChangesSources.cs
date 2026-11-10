namespace AgentMesh.Core.Services;

/// <summary>The closed set of sources a job change set can come from.</summary>
/// <remarks>
/// Strings rather than an enum: these values cross to the dashboard and back in responses, and a
/// renumbered enum is a silent breaking change. See <see cref="Models.JobChangeSourceInfo"/>.
/// </remarks>
public static class JobChangesSources
{
    /// <summary>
    /// <c>git diff</c> against a checkout this process can see — the most informative source, because
    /// it includes work that is not committed and not pushed.
    /// </summary>
    public const string LocalGit = "localGit";

    /// <summary>GitHub's comparison for the pushed branch: committed work only, remote truth.</summary>
    public const string GitHubCompare = "gitHubCompare";

    /// <summary>
    /// The working checkpoint the harness wrote at the last turn boundary: paths the runner measured
    /// with <c>git status --porcelain</c>, with no line stats.
    /// </summary>
    public const string Checkpoint = "checkpoint";
}

/// <summary>Why a source was skipped. Machine-readable so the UI can word each case distinctly.</summary>
public static class JobChangeUnavailableReasons
{
    /// <summary>No checkout for this job is visible to this process — a statement about topology, not a fault.</summary>
    public const string NoWorkspace = "no_workspace";

    /// <summary>A checkout exists but is on a different branch, so its diff would describe other work.</summary>
    public const string BranchMismatch = "branch_mismatch";

    /// <summary>The job has no branch name.</summary>
    public const string NoBranch = "no_branch";

    /// <summary>The job's branch is not present in the checkout (never fetched, or deleted upstream).</summary>
    public const string UnknownRef = "unknown_ref";

    /// <summary>git could not be executed, or the directory is not a repository.</summary>
    public const string GitUnavailable = "git_unavailable";

    /// <summary>No credentials or no resolvable repository, so the remote comparison is impossible.</summary>
    public const string RemoteUnavailable = "remote_unavailable";

    /// <summary>The remote refused the comparison — commonly a branch that was never pushed.</summary>
    public const string RemoteFailed = "remote_failed";

    /// <summary>Nothing was found from any source; most often a job that has not run yet.</summary>
    public const string NoChanges = "no_changes";

    /// <summary>The requested path is not part of this job's change set.</summary>
    public const string PathNotInChangeSet = "path_not_in_change_set";
}

/// <summary>
/// A source could not answer, carrying the machine-readable reason the response should show.
/// </summary>
/// <remarks>
/// An exception rather than a null return because "this source cannot answer" is decided deep inside a
/// probe (a ref that does not resolve, a git that timed out) and has to reach the composition layer
/// with its reason code intact. A null would lose the code and force the caller to invent one; a
/// result-object return type would push an error channel through every helper in the reader for a
/// condition only one layer acts on. The composition layer converts it into a note and moves to the
/// next source, so it never escapes as a 500 on a read endpoint.
/// </remarks>
public sealed class JobChangeUnavailableException(string reasonCode, string message) : Exception(message)
{
    /// <summary>
    /// A code from <see cref="JobChangeUnavailableReasons"/>. Kept as a plain string rather than
    /// constrained to the known set: a caller that invents a more specific code than the list carries
    /// should still get a working panel, and a closed set here would make the reason codes a breaking
    /// change every time a new failure mode is described.
    /// </summary>
    public string ReasonCode { get; } =
        string.IsNullOrWhiteSpace(reasonCode) ? JobChangeUnavailableReasons.NoChanges : reasonCode;
}
