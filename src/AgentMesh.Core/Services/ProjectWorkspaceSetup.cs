using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

public enum ProjectWorkspaceSetupOutcome
{
    AlreadyInitialized,
    Cloned,
    Initialized,
    DirectoryPrepared,
    FallbackUsed,
    Failed
}

public sealed record ProjectWorkspaceSetupRequest(
    string? ProjectId = null,
    string? ProjectName = null,
    string? ProjectDescription = null,
    string? RepositoryName = null,
    string? WorkingDirectory = null,
    WorkspaceType WorkspaceType = WorkspaceType.Git,
    string? GitRepositoryUrl = null,
    string? GitBaseBranch = null,
    string? FallbackDirectory = null);

public sealed record ProjectWorkspaceSetupResult(
    bool Success,
    string TargetDirectory,
    string EffectiveDirectory,
    ProjectWorkspaceSetupOutcome Outcome,
    bool UsedFallback,
    bool CreatedFiles,
    string Message,
    IReadOnlyList<string> Warnings)
{
    public static ProjectWorkspaceSetupResult Succeeded(
        string targetDirectory,
        string effectiveDirectory,
        ProjectWorkspaceSetupOutcome outcome,
        bool createdFiles,
        string message,
        bool usedFallback = false,
        IReadOnlyList<string>? warnings = null) =>
        new(
            Success: true,
            TargetDirectory: targetDirectory,
            EffectiveDirectory: effectiveDirectory,
            Outcome: outcome,
            UsedFallback: usedFallback,
            CreatedFiles: createdFiles,
            Message: message,
            Warnings: warnings ?? Array.Empty<string>());

    public static ProjectWorkspaceSetupResult Failure(
        string targetDirectory,
        string message,
        IReadOnlyList<string>? warnings = null) =>
        new(
            Success: false,
            TargetDirectory: targetDirectory,
            EffectiveDirectory: string.Empty,
            Outcome: ProjectWorkspaceSetupOutcome.Failed,
            UsedFallback: false,
            CreatedFiles: false,
            Message: message,
            Warnings: warnings ?? Array.Empty<string>());
}

internal sealed record GitSetupCommand(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds = 30);

internal sealed record GitSetupResult(
    int ExitCode,
    string StdOut,
    string StdErr);

public interface IProjectWorkspaceSetup
{
    Task<ProjectWorkspaceSetupResult> EnsureWorkspaceAsync(
        ProjectWorkspaceSetupRequest request,
        Action<string>? log = null,
        CancellationToken ct = default);

    Task<ProjectWorkspaceSetupResult> EnsureWorkspaceAsync(
        Project project,
        string? fallbackDirectory = null,
        Action<string>? log = null,
        CancellationToken ct = default);
}

/// <summary>
/// Idempotent project workspace initializer shared by the API and CLI runner.
/// Ensures a project's working directory exists with a standard repository layout
/// (cloning a remote git repository when configured, or initializing a local repository
/// with <c>src/</c>, <c>tests/</c>, <c>docs/</c>, <c>README.md</c>, and <c>.gitignore</c>),
/// and falls back cleanly when the primary target cannot be prepared.
/// </summary>
public sealed class ProjectWorkspaceSetup : IProjectWorkspaceSetup
{
    private const int CloneTimeoutSeconds = 60;
    private const int LocalGitTimeoutSeconds = 15;
    private const string DefaultCommitAuthorName = "AgentMesh";
    private const string DefaultCommitAuthorEmail = "agentmesh@local";

    private static readonly string[] StandardDirectories = ["src", "tests", "docs"];

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.Ordinal);

    private readonly Func<GitSetupCommand, CancellationToken, Task<GitSetupResult>> _gitRunner;
    private readonly string _reposRoot;

    public ProjectWorkspaceSetup()
        : this(null, null)
    {
    }

    internal ProjectWorkspaceSetup(
        Func<GitSetupCommand, CancellationToken, Task<GitSetupResult>>? gitRunner,
        string? reposRootOverride = null)
    {
        _gitRunner = gitRunner ?? RunGitProcessAsync;
        _reposRoot = string.IsNullOrWhiteSpace(reposRootOverride)
            ? WorkspacePathResolver.ReposRoot
            : WorkspacePathResolver.Normalize(reposRootOverride);
    }

    public Task<ProjectWorkspaceSetupResult> EnsureWorkspaceAsync(
        Project project,
        string? fallbackDirectory = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var request = new ProjectWorkspaceSetupRequest(
            ProjectId: project.Id,
            ProjectName: project.Name,
            ProjectDescription: project.Description,
            RepositoryName: project.RepositoryName,
            WorkingDirectory: project.WorkingDirectory,
            WorkspaceType: project.WorkspaceType,
            GitRepositoryUrl: project.GitRepositoryUrl,
            GitBaseBranch: project.GitBaseBranch,
            FallbackDirectory: fallbackDirectory);

        return EnsureWorkspaceAsync(request, log, ct);
    }

    public async Task<ProjectWorkspaceSetupResult> EnsureWorkspaceAsync(
        ProjectWorkspaceSetupRequest request,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        var (validPath, targetDirectory, pathError) = ResolveTargetDirectory(request);
        if (!validPath || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return TryFallback(
                request.FallbackDirectory,
                targetDirectory ?? string.Empty,
                pathError ?? "Invalid project workspace path.",
                warnings,
                log);
        }

        var pathLock = PathLocks.GetOrAdd(targetDirectory, _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(ct);
        try
        {
            return await EnsureWorkspaceCoreAsync(request, targetDirectory, warnings, log, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var reason = $"Failed to set up project workspace '{targetDirectory}': {ex.Message}";
            log?.Invoke(reason);
            return TryFallback(request.FallbackDirectory, targetDirectory, reason, warnings, log);
        }
        finally
        {
            pathLock.Release();
        }
    }

    private async Task<ProjectWorkspaceSetupResult> EnsureWorkspaceCoreAsync(
        ProjectWorkspaceSetupRequest request,
        string targetDirectory,
        List<string> warnings,
        Action<string>? log,
        CancellationToken ct)
    {
        if (request.WorkspaceType == WorkspaceType.S3Archive)
        {
            return EnsureNonGitWorkspace(request, targetDirectory, warnings, log);
        }

        // Fast path: directory is already a git repository root.
        if (Directory.Exists(targetDirectory) && HasGitMetadata(targetDirectory))
        {
            var mismatch = await RemoteMismatchReasonAsync(targetDirectory, request, warnings, log, ct);
            if (mismatch is not null)
            {
                return ProjectWorkspaceSetupResult.Failure(targetDirectory, mismatch, warnings);
            }

            log?.Invoke($"Project workspace already initialized at '{targetDirectory}'.");
            return ProjectWorkspaceSetupResult.Succeeded(
                targetDirectory,
                targetDirectory,
                ProjectWorkspaceSetupOutcome.AlreadyInitialized,
                createdFiles: false,
                message: $"Workspace already initialized at '{targetDirectory}'.",
                warnings: warnings);
        }

        // Monorepo / subdirectory checkout: non-empty directory inside an ancestor git repository.
        if (Directory.Exists(targetDirectory) &&
            IsDirectoryNonEmpty(targetDirectory) &&
            IsInsideMonorepoCheckout(targetDirectory))
        {
            var mismatch = await RemoteMismatchReasonAsync(targetDirectory, request, warnings, log, ct);
            if (mismatch is not null)
            {
                return ProjectWorkspaceSetupResult.Failure(targetDirectory, mismatch, warnings);
            }

            log?.Invoke($"Project workspace '{targetDirectory}' is part of an existing repository checkout.");
            return ProjectWorkspaceSetupResult.Succeeded(
                targetDirectory,
                targetDirectory,
                ProjectWorkspaceSetupOutcome.AlreadyInitialized,
                createdFiles: false,
                message: $"Workspace '{targetDirectory}' is already part of an existing git repository.",
                warnings: warnings);
        }

        var hasRemoteUrl = !string.IsNullOrWhiteSpace(request.GitRepositoryUrl);
        if (hasRemoteUrl)
        {
            return await SetupFromRemoteAsync(request, targetDirectory, warnings, log, ct);
        }

        return await InitializeLocalGitAsync(request, targetDirectory, warnings, log, ct);
    }

    private ProjectWorkspaceSetupResult EnsureNonGitWorkspace(
        ProjectWorkspaceSetupRequest request,
        string targetDirectory,
        List<string> warnings,
        Action<string>? log)
    {
        var existedBefore = Directory.Exists(targetDirectory);
        var wasEmpty = !existedBefore || !IsDirectoryNonEmpty(targetDirectory);

        Directory.CreateDirectory(targetDirectory);

        var createdFiles = false;
        if (wasEmpty)
        {
            createdFiles = EnsureStandardStructure(targetDirectory, request, includeGitKeep: false);
        }

        var outcome = createdFiles
            ? ProjectWorkspaceSetupOutcome.DirectoryPrepared
            : ProjectWorkspaceSetupOutcome.AlreadyInitialized;

        var msg = createdFiles
            ? $"Prepared standard workspace structure at '{targetDirectory}'."
            : $"Workspace directory already prepared at '{targetDirectory}'.";

        log?.Invoke(msg);
        return ProjectWorkspaceSetupResult.Succeeded(
            targetDirectory,
            targetDirectory,
            outcome,
            createdFiles,
            msg,
            warnings: warnings);
    }

    /// <summary>
    /// Why an existing checkout must not be reused, or null when it is the repository the project names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reusing a directory that already has <c>.git</c> is the fast path, and it used to be
    /// unconditional — "is a repository" was treated as "is this project's repository". When a project
    /// row still carries the pre-multi-repo <c>repository_name</c> default
    /// (<see cref="Project.DefaultRepositoryName"/>, which is also this deployment's own repository),
    /// the conventional path resolves onto whichever other checkout happens to be mounted in the same
    /// container, and every command the agent runs — <c>git status</c>, <c>git checkout -b</c>, the
    /// commit at the end — succeeds against the wrong tree. The job then carries commits, a branch and
    /// eventually a pull request against a repository the project was never about, and nothing in the
    /// platform reports an error. That is exactly the failure
    /// <see cref="WorkspacePathResolver"/> was written to prevent, reintroduced through the door the
    /// fast path left open.
    /// </para>
    /// <para>
    /// A mismatch is returned as a hard failure rather than routed through
    /// <see cref="TryFallback"/>: a fallback to a scratch directory at least produces an honest
    /// "there is no code here", whereas continuing inside a real checkout of a different repository
    /// produces work that cannot be merged and looks like work while it happens.
    /// </para>
    /// <para>
    /// An unreadable or absent <c>origin</c> warns instead of failing. A workspace initialized locally
    /// cannot be a checkout of some other repository, and refusing to run there would break projects
    /// whose remote was configured after the fact or has never been pushed.
    /// </para>
    /// </remarks>
    private async Task<string?> RemoteMismatchReasonAsync(
        string targetDirectory,
        ProjectWorkspaceSetupRequest request,
        List<string> warnings,
        Action<string>? log,
        CancellationToken ct)
    {
        var expected = request.GitRepositoryUrl?.Trim();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return null;
        }

        var origin = await _gitRunner(
            new GitSetupCommand(targetDirectory, ["remote", "get-url", "origin"], LocalGitTimeoutSeconds),
            ct);

        var originUrl = origin.StdOut.Trim();
        if (origin.ExitCode != 0 || originUrl.Length == 0)
        {
            var unreadable = $"Could not read the 'origin' remote of '{targetDirectory}' to confirm it is " +
                             $"'{expected}': {FirstNonEmpty(origin.StdErr, origin.StdOut, "git remote get-url returned nothing")}";
            warnings.Add(unreadable);
            log?.Invoke(unreadable);
            return null;
        }

        if (WorkspacePathResolver.AreRemotesEquivalent(originUrl, expected))
        {
            return null;
        }

        var mismatch = $"'{targetDirectory}' is a checkout of '{originUrl}', not the repository project " +
                       $"'{FirstNonEmpty(request.ProjectId, request.ProjectName)}' is bound to ('{expected}'). " +
                       "Refusing to run there: set this project's repository name or working directory to " +
                       "the intended checkout, or point its git remote at the remote it actually uses.";
        log?.Invoke(mismatch);
        return mismatch;
    }

    private async Task<ProjectWorkspaceSetupResult> SetupFromRemoteAsync(
        ProjectWorkspaceSetupRequest request,
        string targetDirectory,
        List<string> warnings,
        Action<string>? log,
        CancellationToken ct)
    {
        var remoteUrl = request.GitRepositoryUrl!.Trim();
        if (!IsSafeGitRemoteUrl(remoteUrl))
        {
            return TryFallback(
                request.FallbackDirectory,
                targetDirectory,
                $"Unsafe git repository URL '{remoteUrl}'.",
                warnings,
                log);
        }

        var baseBranch = ResolveSafeBaseBranch(request.GitBaseBranch, warnings);

        if (Directory.Exists(targetDirectory) && IsDirectoryNonEmpty(targetDirectory))
        {
            var reason = $"Cannot clone '{remoteUrl}' into non-empty non-git directory '{targetDirectory}'.";
            log?.Invoke(reason);
            return TryFallback(request.FallbackDirectory, targetDirectory, reason, warnings, log);
        }

        var existedBefore = Directory.Exists(targetDirectory);
        var parentDir = Path.GetDirectoryName(targetDirectory);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var cloneWorkingDir = !string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir)
            ? parentDir
            : Directory.GetCurrentDirectory();

        log?.Invoke($"Cloning '{remoteUrl}' into '{targetDirectory}' (branch: {baseBranch})...");

        var cloneResult = await _gitRunner(
            new GitSetupCommand(
                cloneWorkingDir,
                ["clone", "--branch", baseBranch, "--", remoteUrl, targetDirectory],
                CloneTimeoutSeconds),
            ct);

        if (cloneResult.ExitCode != 0)
        {
            // Clean partial clone artifacts before trying fallback clone without --branch.
            CleanupFailedCloneDirectory(targetDirectory, existedBefore);

            cloneResult = await _gitRunner(
                new GitSetupCommand(
                    cloneWorkingDir,
                    ["clone", "--", remoteUrl, targetDirectory],
                    CloneTimeoutSeconds),
                ct);
        }

        if (cloneResult.ExitCode != 0)
        {
            CleanupFailedCloneDirectory(targetDirectory, existedBefore);
            var err = FirstNonEmpty(cloneResult.StdErr, cloneResult.StdOut, $"exit code {cloneResult.ExitCode}");
            var reason = $"git clone failed for '{remoteUrl}': {err}";
            log?.Invoke(reason);
            return TryFallback(request.FallbackDirectory, targetDirectory, reason, warnings, log);
        }

        // If the remote repository was empty (unborn HEAD), initialize the base branch and standard structure.
        var createdFiles = false;
        var headCheck = await _gitRunner(
            new GitSetupCommand(targetDirectory, ["rev-parse", "--verify", "HEAD"], LocalGitTimeoutSeconds),
            ct);

        if (headCheck.ExitCode != 0)
        {
            await EnsureBaseBranchOnUnbornHeadAsync(targetDirectory, baseBranch, warnings, ct);
            createdFiles = EnsureStandardStructure(targetDirectory, request, includeGitKeep: true);
            await StageAndCommitInitialStructureAsync(targetDirectory, warnings, ct);
        }

        log?.Invoke($"Cloned project repository into '{targetDirectory}'.");
        return ProjectWorkspaceSetupResult.Succeeded(
            targetDirectory,
            targetDirectory,
            ProjectWorkspaceSetupOutcome.Cloned,
            createdFiles,
            $"Cloned repository into '{targetDirectory}'.",
            warnings: warnings);
    }

    private async Task<ProjectWorkspaceSetupResult> InitializeLocalGitAsync(
        ProjectWorkspaceSetupRequest request,
        string targetDirectory,
        List<string> warnings,
        Action<string>? log,
        CancellationToken ct)
    {
        var baseBranch = ResolveSafeBaseBranch(request.GitBaseBranch, warnings);
        var existedBefore = Directory.Exists(targetDirectory);

        Directory.CreateDirectory(targetDirectory);

        var initResult = await _gitRunner(
            new GitSetupCommand(targetDirectory, ["init", "-b", baseBranch], LocalGitTimeoutSeconds),
            ct);

        if (initResult.ExitCode != 0)
        {
            // Older git versions may not support `git init -b`.
            initResult = await _gitRunner(
                new GitSetupCommand(targetDirectory, ["init"], LocalGitTimeoutSeconds),
                ct);

            if (initResult.ExitCode != 0)
            {
                if (!existedBefore)
                {
                    TryDeleteEmptyDirectory(targetDirectory);
                }

                var err = FirstNonEmpty(initResult.StdErr, initResult.StdOut, $"exit code {initResult.ExitCode}");
                var reason = $"git init failed in '{targetDirectory}': {err}";
                log?.Invoke(reason);
                return TryFallback(request.FallbackDirectory, targetDirectory, reason, warnings, log);
            }

            await EnsureBaseBranchOnUnbornHeadAsync(targetDirectory, baseBranch, warnings, ct);
        }

        var createdFiles = EnsureStandardStructure(targetDirectory, request, includeGitKeep: true);

        var headCheck = await _gitRunner(
            new GitSetupCommand(targetDirectory, ["rev-parse", "--verify", "HEAD"], LocalGitTimeoutSeconds),
            ct);

        if (headCheck.ExitCode != 0)
        {
            await StageAndCommitInitialStructureAsync(targetDirectory, warnings, ct);
        }

        log?.Invoke($"Initialized standard git repository at '{targetDirectory}' (branch: {baseBranch}).");
        return ProjectWorkspaceSetupResult.Succeeded(
            targetDirectory,
            targetDirectory,
            ProjectWorkspaceSetupOutcome.Initialized,
            createdFiles,
            $"Initialized git repository at '{targetDirectory}' on branch '{baseBranch}'.",
            warnings: warnings);
    }

    private async Task EnsureBaseBranchOnUnbornHeadAsync(
        string targetDirectory,
        string baseBranch,
        List<string> warnings,
        CancellationToken ct)
    {
        var symRef = await _gitRunner(
            new GitSetupCommand(
                targetDirectory,
                ["symbolic-ref", "HEAD", $"refs/heads/{baseBranch}"],
                LocalGitTimeoutSeconds),
            ct);

        if (symRef.ExitCode != 0)
        {
            var err = FirstNonEmpty(symRef.StdErr, symRef.StdOut, $"exit code {symRef.ExitCode}");
            warnings.Add($"Could not set initial branch to '{baseBranch}': {err}");
        }
    }

    private async Task StageAndCommitInitialStructureAsync(
        string targetDirectory,
        List<string> warnings,
        CancellationToken ct)
    {
        var status = await _gitRunner(
            new GitSetupCommand(targetDirectory, ["status", "--porcelain"], LocalGitTimeoutSeconds),
            ct);

        if (status.ExitCode != 0 || string.IsNullOrWhiteSpace(status.StdOut))
        {
            return;
        }

        var add = await _gitRunner(
            new GitSetupCommand(targetDirectory, ["add", "-A"], LocalGitTimeoutSeconds),
            ct);
        if (add.ExitCode != 0)
        {
            warnings.Add($"Could not stage initial repository files: {FirstNonEmpty(add.StdErr, add.StdOut, "git add failed")}");
            return;
        }

        var commit = await _gitRunner(
            new GitSetupCommand(
                targetDirectory,
                [
                    "-c", $"user.name={DefaultCommitAuthorName}",
                    "-c", $"user.email={DefaultCommitAuthorEmail}",
                    "commit", "-m", "chore: initialize standard repository structure"
                ],
                LocalGitTimeoutSeconds),
            ct);

        if (commit.ExitCode != 0)
        {
            warnings.Add($"Could not create initial commit: {FirstNonEmpty(commit.StdErr, commit.StdOut, "git commit failed")}");
        }
    }

    private (bool Valid, string? Path, string? Error) ResolveTargetDirectory(ProjectWorkspaceSetupRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            var trimmed = request.WorkingDirectory.Trim();
            if (!WorkspacePathResolver.IsSafeWorkingDirectory(trimmed))
            {
                return (false, null, $"Unsafe working directory '{trimmed}'.");
            }

            var normalized = WorkspacePathResolver.Normalize(trimmed);
            if (IsProtectedRootPath(normalized))
            {
                return (false, null, $"Refusing to initialize workspace directly at protected root '{normalized}'.");
            }

            return (true, normalized, null);
        }

        var candidateName = !string.IsNullOrWhiteSpace(request.RepositoryName)
            ? request.RepositoryName.Trim()
            : request.ProjectId?.Trim();

        if (string.IsNullOrWhiteSpace(candidateName))
        {
            return (false, null, "No WorkingDirectory, RepositoryName, or ProjectId specified for project setup.");
        }

        if (!WorkspacePathResolver.IsSafeRepositoryName(candidateName))
        {
            return (false, null, $"Unsafe repository name '{candidateName}'.");
        }

        var path = WorkspacePathResolver.Normalize($"{_reposRoot}/{candidateName}");
        return (true, path, null);
    }

    private bool IsProtectedRootPath(string normalizedPath)
    {
        if (string.Equals(normalizedPath, "/", StringComparison.Ordinal)) return true;
        if (string.Equals(normalizedPath, _reposRoot, StringComparison.Ordinal)) return true;
        if (string.Equals(normalizedPath, WorkspacePathResolver.ReposRoot, StringComparison.Ordinal)) return true;
        return false;
    }

    private static ProjectWorkspaceSetupResult TryFallback(
        string? fallbackDirectory,
        string targetDirectory,
        string reason,
        List<string> warnings,
        Action<string>? log)
    {
        if (!string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            var normalizedFallback = WorkspacePathResolver.Normalize(fallbackDirectory.Trim());
            if (WorkspacePathResolver.IsSafeWorkingDirectory(normalizedFallback))
            {
                try
                {
                    Directory.CreateDirectory(normalizedFallback);
                    warnings.Add(reason);
                    var msg = $"{reason} Falling back to '{normalizedFallback}'.";
                    log?.Invoke(msg);

                    return ProjectWorkspaceSetupResult.Succeeded(
                        targetDirectory,
                        normalizedFallback,
                        ProjectWorkspaceSetupOutcome.FallbackUsed,
                        createdFiles: false,
                        message: msg,
                        usedFallback: true,
                        warnings: warnings);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Fallback directory '{normalizedFallback}' could not be prepared: {ex.Message}");
                }
            }
            else
            {
                warnings.Add($"Fallback directory '{normalizedFallback}' is not a valid absolute path.");
            }
        }

        return ProjectWorkspaceSetupResult.Failure(targetDirectory, reason, warnings);
    }

    private static bool EnsureStandardStructure(
        string targetDirectory,
        ProjectWorkspaceSetupRequest request,
        bool includeGitKeep)
    {
        var createdAny = false;

        foreach (var dirName in StandardDirectories)
        {
            var dirPath = Path.Combine(targetDirectory, dirName);
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
                createdAny = true;
            }

            if (includeGitKeep && !IsDirectoryNonEmpty(dirPath))
            {
                var keepPath = Path.Combine(dirPath, ".gitkeep");
                if (!File.Exists(keepPath))
                {
                    File.WriteAllText(keepPath, string.Empty, Encoding.UTF8);
                    createdAny = true;
                }
            }
        }

        var readmePath = Path.Combine(targetDirectory, "README.md");
        if (!File.Exists(readmePath))
        {
            var title = FirstNonEmpty(
                request.ProjectName,
                request.RepositoryName,
                request.ProjectId,
                Path.GetFileName(targetDirectory),
                "Project");

            var description = !string.IsNullOrWhiteSpace(request.ProjectDescription)
                ? request.ProjectDescription.Trim()
                : "Project workspace initialized by AgentMesh.";

            var readme =
                $"# {title}\n\n" +
                $"{description}\n\n" +
                "## Repository Structure\n\n" +
                "- `src/` — Application and library source code\n" +
                "- `tests/` — Automated test suites\n" +
                "- `docs/` — Architecture notes and specifications\n";

            File.WriteAllText(readmePath, readme, Encoding.UTF8);
            createdAny = true;
        }

        var gitignorePath = Path.Combine(targetDirectory, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            const string gitignore =
                "# AgentMesh\n" +
                ".agentmesh/\n\n" +
                "# OS & Editor\n" +
                ".DS_Store\n" +
                "Thumbs.db\n" +
                "*.swp\n" +
                "*.swo\n" +
                "*~\n\n" +
                "# Environment & Logs\n" +
                ".env\n" +
                ".env.local\n" +
                "*.log\n\n" +
                "# Build & Dependencies\n" +
                "bin/\n" +
                "obj/\n" +
                "dist/\n" +
                "build/\n" +
                "out/\n" +
                "node_modules/\n" +
                "__pycache__/\n" +
                "*.pyc\n" +
                ".venv/\n";

            File.WriteAllText(gitignorePath, gitignore, Encoding.UTF8);
            createdAny = true;
        }

        return createdAny;
    }

    private static bool HasGitMetadata(string directory)
    {
        var dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static bool IsDirectoryNonEmpty(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        using var enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        return enumerator.MoveNext();
    }

    private bool IsInsideMonorepoCheckout(string targetDirectory)
    {
        var current = Directory.GetParent(targetDirectory);
        while (current != null)
        {
            var normalized = WorkspacePathResolver.Normalize(current.FullName);

            // Stop at container/repos boundaries: /workspace having .git must never cause
            // /workspace/repos/<project> to look like a monorepo subdirectory.
            if (normalized == "/" ||
                normalized == "/workspace" ||
                normalized == _reposRoot ||
                normalized == WorkspacePathResolver.ReposRoot)
            {
                break;
            }

            if (HasGitMetadata(current.FullName))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static void CleanupFailedCloneDirectory(string targetDirectory, bool existedBefore)
    {
        try
        {
            if (!Directory.Exists(targetDirectory)) return;

            if (!existedBefore)
            {
                Directory.Delete(targetDirectory, recursive: true);
                return;
            }

            // Directory existed (empty) before clone; remove only if git left partial .git state.
            var dotGit = Path.Combine(targetDirectory, ".git");
            if (Directory.Exists(dotGit))
            {
                Directory.Delete(dotGit, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDeleteEmptyDirectory(string targetDirectory)
    {
        try
        {
            if (Directory.Exists(targetDirectory) && !IsDirectoryNonEmpty(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: false);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool IsSafeGitRemoteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith('-')) return false;
        return url.IndexOfAny(['\r', '\n', '\0']) < 0;
    }

    private static string ResolveSafeBaseBranch(string? branch, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(branch)) return "main";

        var trimmed = branch.Trim();
        if (trimmed.StartsWith('-') ||
            trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.IndexOfAny([' ', '\t', '\r', '\n', '\0']) >= 0)
        {
            warnings.Add($"Invalid git base branch '{trimmed}'; defaulting to 'main'.");
            return "main";
        }

        return trimmed;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static async Task<GitSetupResult> RunGitProcessAsync(
        GitSetupCommand command,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Never block waiting on an interactive credential prompt inside a daemon or API worker.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        foreach (var arg in command.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            if (!process.Start())
            {
                return new GitSetupResult(-1, string.Empty, "Failed to start git process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds > 0 ? command.TimeoutSeconds : LocalGitTimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new GitSetupResult(-1, stdout.ToString(), $"git {command.Arguments.FirstOrDefault()} timed out.");
            }

            return new GitSetupResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GitSetupResult(-1, string.Empty, ex.Message);
        }
    }
}
