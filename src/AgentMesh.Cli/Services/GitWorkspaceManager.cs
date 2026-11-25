using System.Diagnostics;
using System.Text;

namespace AgentMesh.Cli.Services;

public static class GitWorkspaceManager
{
    public static async Task<bool> IsGitRepositoryAsync(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        var (exitCode, stdout, _) = await RunGitAsync(directory, "rev-parse --is-inside-work-tree");
        return exitCode == 0 && stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task PrepareJobBranchAsync(string directory, string branchName, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(branchName) || !await IsGitRepositoryAsync(directory))
            return;

        var targetBranch = branchName.Trim();

        try
        {
            var (_, currentBranch, _) = await RunGitAsync(directory, "branch --show-current");
            if (string.Equals(currentBranch.Trim(), targetBranch, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"Already on job branch '{targetBranch}'");
                return;
            }

            // If the job branch already exists locally, switch to it without `-B` so existing
            // commits on that job branch are never overwritten by whatever HEAD is checked out.
            var (localRefCode, _, _) = await RunGitAsync(directory, $"show-ref --verify --quiet refs/heads/{targetBranch}");
            if (localRefCode == 0)
            {
                var (switchCode, _, switchErr) = await RunGitAsync(directory, $"checkout {targetBranch}");
                if (switchCode == 0)
                {
                    log?.Invoke($"Switched to existing job branch '{targetBranch}'");
                }
                else
                {
                    log?.Invoke($"Warning: git checkout {targetBranch} failed: {switchErr.Trim()}");
                }
                return;
            }

            // For a brand-new job branch, refresh origin (best-effort) and anchor to the remote's
            // default branch (or origin/main / origin/master) rather than inheriting a stale prior
            // job branch's HEAD (which causes duplicate-commit merge conflicts after squash merges).
            string? baseRef = null;
            var (remoteCode, remotesOut, _) = await RunGitAsync(directory, "remote");
            if (remoteCode == 0 && remotesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(r => string.Equals(r.Trim(), "origin", StringComparison.OrdinalIgnoreCase)))
            {
                await RunGitAsync(directory, "fetch origin --prune");

                // If the job branch already exists on origin, track it directly.
                var (remoteBranchCode, _, _) = await RunGitAsync(directory, $"show-ref --verify --quiet refs/remotes/origin/{targetBranch}");
                if (remoteBranchCode == 0)
                {
                    var (trackCode, _, trackErr) = await RunGitAsync(directory, $"checkout -b {targetBranch} origin/{targetBranch}");
                    if (trackCode == 0)
                    {
                        log?.Invoke($"Checked out remote job branch '{targetBranch}' from origin/{targetBranch}");
                        return;
                    }
                    log?.Invoke($"Warning: git checkout -b {targetBranch} origin/{targetBranch} failed: {trackErr.Trim()}");
                }

                baseRef = await ResolveOriginDefaultRefAsync(directory);
            }

            var createArgs = !string.IsNullOrWhiteSpace(baseRef)
                ? $"checkout -b {targetBranch} {baseRef}"
                : $"checkout -b {targetBranch}";

            var (code, _, errStr) = await RunGitAsync(directory, createArgs);
            if (code == 0)
            {
                log?.Invoke(!string.IsNullOrWhiteSpace(baseRef)
                    ? $"Created job branch '{targetBranch}' from '{baseRef}'"
                    : $"Checked out job branch '{targetBranch}'");
            }
            else
            {
                log?.Invoke($"Warning: git {createArgs} failed: {errStr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning: Failed to prepare branch '{targetBranch}': {ex.Message}");
        }
    }

    private static async Task<string?> ResolveOriginDefaultRefAsync(string directory)
    {
        var (symCode, symOut, _) = await RunGitAsync(directory, "symbolic-ref --quiet --short refs/remotes/origin/HEAD");
        if (symCode == 0 && !string.IsNullOrWhiteSpace(symOut))
        {
            return symOut.Trim();
        }

        foreach (var candidate in new[] { "origin/main", "origin/master" })
        {
            var (refCode, _, _) = await RunGitAsync(directory, $"show-ref --verify --quiet refs/remotes/{candidate}");
            if (refCode == 0) return candidate;
        }

        return null;
    }

    public static async Task CommitTurnChangesAsync(
        string directory,
        string branchName,
        string personaId,
        string? personaName,
        string prompt,
        Action<string>? log = null,
        string? customAuthorName = null,
        string? customAuthorEmail = null)
    {
        if (string.IsNullOrWhiteSpace(branchName) || !await IsGitRepositoryAsync(directory))
            return;

        try
        {
            var (statusCode, statusOut, _) = await RunGitAsync(directory, "status --porcelain");
            if (statusCode != 0 || string.IsNullOrWhiteSpace(statusOut.Trim()))
            {
                return; // Clean workspace, nothing to commit
            }

            var (addCode, _, addErr) = await RunGitAsync(directory, "add -A");
            if (addCode != 0)
            {
                log?.Invoke($"Warning: git add -A failed: {addErr.Trim()}");
                return;
            }

            var (commitCode, _, commitErr) = await RunGitAsync(
                directory,
                BuildCommitArguments(personaId, personaName, prompt, customAuthorName, customAuthorEmail, out var authorName, out var authorEmail));

            if (commitCode == 0)
            {
                log?.Invoke($"Committed turn changes to '{branchName}' by @{personaId} ({authorName} <{authorEmail}>)");
            }
            else
            {
                log?.Invoke($"Warning: git commit failed: {commitErr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning: Failed to commit turn changes: {ex.Message}");
        }
    }

    internal static string BuildCommitArguments(
        string? personaId,
        string? personaName,
        string? prompt,
        string? customAuthorName,
        string? customAuthorEmail,
        out string authorName,
        out string authorEmail)
    {
        var normalizedPersonaId = (personaId ?? string.Empty).Trim();

        authorName = !string.IsNullOrWhiteSpace(customAuthorName)
            ? customAuthorName.Trim()
            : (!string.IsNullOrWhiteSpace(personaName) ? personaName.Trim() : normalizedPersonaId);
        if (string.IsNullOrWhiteSpace(authorName))
        {
            authorName = "AgentMesh";
        }

        authorEmail = !string.IsNullOrWhiteSpace(customAuthorEmail)
            ? customAuthorEmail.Trim()
            : $"{normalizedPersonaId.ToLowerInvariant().Replace(' ', '-')}-bot@agentmesh.local";
        if (string.IsNullOrWhiteSpace(authorEmail) || authorEmail.StartsWith('-') || !authorEmail.Contains('@'))
        {
            authorEmail = "agentmesh-bot@agentmesh.local";
        }

        var normalizedPrompt = (prompt ?? string.Empty).Trim();
        var summary = normalizedPrompt.Split('\n')[0];
        if (summary.Length > 60) summary = summary[..60] + "...";
        var actorLabel = string.IsNullOrWhiteSpace(normalizedPersonaId) ? "agentmesh" : normalizedPersonaId;
        var commitMsg = $"feat(job): turn by @{actorLabel} - {summary}";

        var escapedAuthorName = EscapeForCommandLine(authorName);
        var escapedAuthorEmail = EscapeForCommandLine(authorEmail);
        return $"-c user.name=\"{escapedAuthorName}\" -c user.email=\"{escapedAuthorEmail}\" commit -m \"{EscapeForCommandLine(commitMsg)}\" --author=\"{escapedAuthorName} <{escapedAuthorEmail}>\"";
    }

    private static string EscapeForCommandLine(string str) =>
        str.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(string workingDir, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await proc.WaitForExitAsync(timeoutCts.Token);

            return (proc.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
