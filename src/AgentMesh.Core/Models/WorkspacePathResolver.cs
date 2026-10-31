namespace AgentMesh.Core.Models;

/// <summary>
/// The single definition of "where does a project's repository live on disk".
/// </summary>
/// <remarks>
/// <para>
/// Lives in Core because three hosts need the identical answer and two of them run in different
/// processes: the API resolves it when it starts a workflow and stamps it into
/// <see cref="AgentMesh.Core.Workflows.TaskExecutionInput"/>, the worker resolves it again when a
/// shell command arrives without a directory, and the CLI runner resolves it when it prepares a
/// checkout. If those three disagree, an agent's <c>git status</c> reports a clean tree for a
/// repository it is not standing in — a failure with no error message anywhere.
/// </para>
/// <para>
/// The convention is <c>/workspace/repos/{repo_name}</c> rather than the old
/// <c>/workspace/repo</c> because a single fixed directory cannot hold more than one repository: a
/// multi-repo job either overwrites the first checkout with the second or fails on a non-empty
/// target, and in both cases the first project's work is silently gone.
/// </para>
/// </remarks>
public static class WorkspacePathResolver
{
    /// <summary>The parent directory every conventional checkout lives under.</summary>
    public const string ReposRoot = "/workspace/repos";

    /// <summary>
    /// Builds the checkout path for a repository name: <c>/workspace/repos/{repoName}</c>.
    /// </summary>
    /// <remarks>
    /// A null/blank name yields <see cref="ReposRoot"/> itself rather than throwing, because this
    /// runs on the read path of every project row and a legacy row with an empty name must not take
    /// down a GET. Callers that need to reject a bad name should use
    /// <see cref="IsSafeRepositoryName"/> at the write boundary, where the caller can be told.
    /// </remarks>
    public static string FromRepositoryName(string? repoName) =>
        string.IsNullOrWhiteSpace(repoName)
            ? ReposRoot
            : $"{ReposRoot}/{repoName.Trim()}";

    /// <summary>
    /// Resolves a project's working directory: the explicit override when present, otherwise the
    /// conventional path. See <see cref="Project.GetResolvedWorkingDirectory"/>.
    /// </summary>
    public static string Resolve(string? repositoryName, string? workingDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(workingDirectory)
            ? FromRepositoryName(repositoryName)
            : workingDirectory.Trim();

        return Normalize(candidate);
    }

    /// <summary>
    /// Strips trailing separators and converts backslashes to forward slashes.
    /// </summary>
    /// <remarks>
    /// Backslash conversion is deliberately conservative: it only applies to absolute POSIX paths
    /// (leading <c>/</c>), so a Windows path like <c>C:\repos\api</c> is left alone rather than
    /// mangled into <c>C:/repos/api</c> — which would be correct on Windows but is a change of
    /// meaning this function cannot verify. The production target is Linux containers, where the
    /// leading-slash case is the only one that occurs.
    /// </remarks>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var result = path.Trim();
        if (result.StartsWith('/'))
        {
            result = result.Replace('\\', '/');
        }

        // Keep a lone "/" as root; stripping it would produce "" and hand the caller back a
        // relative path, which resolves against the worker's own directory.
        while (result.Length > 1 && result.EndsWith('/'))
        {
            result = result[..^1];
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="repoName"/> is safe to embed in a path.
    /// </summary>
    /// <remarks>
    /// Rejects traversal (<c>..</c>), separators, and control characters. The name arrives from an
    /// API request body, so without this check <c>repository_name = "../../etc"</c> would resolve to
    /// <c>/workspace/repos/../../etc</c> and every agent command would run as root of an arbitrary
    /// directory. Allowed set is intentionally narrow: repository names in practice are
    /// alphanumeric plus <c>.-_</c>, and nothing in that set needs the characters we exclude.
    /// </remarks>
    public static bool IsSafeRepositoryName(string? repoName)
    {
        if (string.IsNullOrWhiteSpace(repoName)) return false;
        if (repoName.Length > MaxRepositoryNameLength) return false;
        if (repoName != repoName.Trim()) return false;
        if (repoName is "." or "..") return false;

        foreach (var c in repoName)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';
            if (!ok) return false;
        }

        // A name that merely contains a dot is fine ("agent-mesh.git"); one that is *only* dots is
        // a path component that means "up".
        return repoName.Trim('.') != string.Empty;
    }

    /// <summary>
    /// True when <paramref name="workingDirectory"/> may be stored as an explicit override.
    /// </summary>
    /// <remarks>
    /// Requires an absolute path: a relative override would be resolved against whatever directory
    /// the worker happens to start in, which is exactly the ambiguity the column exists to remove.
    /// Control characters are rejected because the value is later embedded in a shell command line.
    /// </remarks>
    public static bool IsSafeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return true; // blank means "use the convention"
        if (workingDirectory.Length > MaxWorkingDirectoryLength) return false;
        if (workingDirectory != workingDirectory.Trim()) return false;
        if (!workingDirectory.StartsWith('/') && !ContainsWindowsDriveRoot(workingDirectory)) return false;

        return workingDirectory.IndexOfAny(ControlChars) < 0;
    }

    /// <summary>Matches <c>C:\</c> / <c>C:/</c> style roots without taking a dependency on Windows APIs.</summary>
    private static bool ContainsWindowsDriveRoot(string path) =>
        path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) &&
        path[1] == ':' &&
        (path[2] == '/' || path[2] == '\\');

    private static readonly char[] ControlChars = ['\n', '\r', '\0', '\t', '\a', '\b', '\v', '\f'];

    /// <summary>Column width guard, matched to <c>projects.repository_name varchar(128)</c>.</summary>
    public const int MaxRepositoryNameLength = 128;

    /// <summary>Column width guard, matched to <c>projects.working_directory varchar(256)</c>.</summary>
    public const int MaxWorkingDirectoryLength = 256;
}
