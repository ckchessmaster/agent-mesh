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

    /// <summary>
    /// Reduces two spellings of a git remote to one comparable identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Which repository is this?" is a question about the remote, and the platform stores remotes in
    /// whatever form the operator pasted (<c>https://host/o/r</c>, <c>https://host/o/r.git</c>,
    /// <c>git@host:o/r.git</c>, <c>ssh://git@host:2222/o/r</c>) while <c>git remote get-url</c> answers
    /// in the form the clone produced. Compared literally those differ, so the comparison happens here
    /// once, and both the write boundary and the workspace initializer ask the same question.
    /// </para>
    /// <para>
    /// What survives the reduction is <c>host/path</c> with the scheme, userinfo, port, trailing
    /// separators and <c>.git</c> suffix removed, and the host lowercased. Credentials are dropped
    /// deliberately: <c>https://user:token@host/o/r</c> and <c>https://host/o/r</c> name the same
    /// repository, and a comparison that kept the token would report a mismatch on the deployment that
    /// stores a token in the remote. The path keeps its case because repository paths are
    /// case-sensitive on every host that matters here.
    /// </para>
    /// <para>
    /// A local path (<c>/srv/git/repo.git</c>) has no host, so it normalizes to itself with the suffix
    /// removed. That is what makes the comparison usable against bare repositories in tests.
    /// </para>
    /// </remarks>
    public static string NormalizeRemoteUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return string.Empty;

        var value = remoteUrl.Trim();
        string host;
        string path;

        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            // An explicit scheme always puts an authority before the first slash, even when that
            // authority carries credentials ("https://user:token@host/o/r").
            value = value[(schemeIndex + 3)..];
            var authorityEnd = value.IndexOf('/');
            host = authorityEnd >= 0 ? value[..authorityEnd] : value;
            path = authorityEnd >= 0 ? value[(authorityEnd + 1)..] : string.Empty;
            host = StripPort(host);
        }
        else
        {
            var colon = value.IndexOf(':');
            var slash = value.IndexOf('/');

            // scp-like syntax ("git@host:o/r.git", "host:2222/o/r") separates host from path with a
            // colon before any slash. A single-letter "host" is a Windows drive letter, which has no
            // host at all, so it takes the local-path branch below.
            if (colon > 1 && (slash < 0 || colon < slash))
            {
                host = StripPort(value[..colon]);
                path = value[(colon + 1)..];
                var portPath = path.IndexOf('/');
                var portSegment = portPath >= 0 ? path[..portPath] : path;
                if (portPath >= 0 && portSegment.Length > 0 && portSegment.All(char.IsAsciiDigit))
                {
                    // "host:2222/o/r" is the same tree as "host/o/r": the digits are a port.
                    path = path[(portPath + 1)..];
                }
            }
            else
            {
                // No host: a local path or path-only remote. Kept verbatim, so a bare repository on
                // disk compares against its own spelling.
                host = string.Empty;
                path = value;
            }
        }

        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            host = host[(at + 1)..];
        }

        host = host.TrimEnd('/').ToLowerInvariant();
        path = TrimRemotePath(path);

        if (host.Length == 0) return path;
        if (path.Length == 0) return host;
        return $"{host}/{path}";
    }

    /// <summary>
    /// Drops a <c>:port</c> suffix from an authority, which is transport detail rather than identity.
    /// </summary>
    private static string StripPort(string authority)
    {
        var colon = authority.LastIndexOf(':');
        if (colon <= 0 || colon == authority.Length - 1) return authority;

        var port = authority[(colon + 1)..];
        return port.All(char.IsAsciiDigit) ? authority[..colon] : authority;
    }

    /// <summary>
    /// True when both remotes name the same repository, compared by
    /// <see cref="NormalizeRemoteUrl"/>. False only when both sides are known and differ.
    /// </summary>
    /// <remarks>
    /// Two empty inputs are <em>not</em> equivalent: "unknown" must never satisfy a check that is
    /// supposed to protect against the wrong tree, so callers test for a blank remote before asking.
    /// </remarks>
    public static bool AreRemotesEquivalent(string? left, string? right)
    {
        var a = NormalizeRemoteUrl(left);
        var b = NormalizeRemoteUrl(right);
        if (a.Length == 0 || b.Length == 0) return false;
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>
    /// The directory name a remote implies: the last path segment, without <c>.git</c>.
    /// </summary>
    /// <remarks>
    /// Returns null when the remote carries no usable name or the name would be unsafe as a path
    /// component (<see cref="IsSafeRepositoryName"/>), so callers fall back to their own convention
    /// rather than inventing a directory nobody can clean up.
    /// </remarks>
    public static string? TryDeriveRepositoryNameFromRemote(string? remoteUrl)
    {
        var normalized = NormalizeRemoteUrl(remoteUrl);
        if (normalized.Length == 0) return null;

        // Without a path there is no repository to name: "https://github.com" is a host, and a
        // directory called "github.com" would be a guess nobody could clean up.
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0) return null;

        var candidate = normalized[(lastSlash + 1)..];

        return IsSafeRepositoryName(candidate) ? candidate : null;
    }

    private static string TrimRemotePath(string path)
    {
        var result = path.Trim('/');
        if (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^4];
        }

        return result.Trim('/');
    }

    private static readonly char[] ControlChars = ['\n', '\r', '\0', '\t', '\a', '\b', '\v', '\f'];

    /// <summary>Column width guard, matched to <c>projects.repository_name varchar(128)</c>.</summary>
    public const int MaxRepositoryNameLength = 128;

    /// <summary>Column width guard, matched to <c>projects.working_directory varchar(256)</c>.</summary>
    public const int MaxWorkingDirectoryLength = 256;
}
