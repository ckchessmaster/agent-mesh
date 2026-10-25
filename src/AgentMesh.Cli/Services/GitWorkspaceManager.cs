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

        try
        {
            var (_, currentBranch, _) = await RunGitAsync(directory, "branch --show-current");
            if (string.Equals(currentBranch.Trim(), branchName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"Already on job branch '{branchName}'");
                return;
            }

            var (code, _, errStr) = await RunGitAsync(directory, $"checkout -B {branchName.Trim()}");
            if (code == 0)
            {
                log?.Invoke($"Checked out job branch '{branchName}'");
            }
            else
            {
                log?.Invoke($"Warning: git checkout -B {branchName} failed: {errStr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning: Failed to prepare branch '{branchName}': {ex.Message}");
        }
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

            var authorName = !string.IsNullOrWhiteSpace(customAuthorName)
                ? customAuthorName.Trim()
                : (!string.IsNullOrWhiteSpace(personaName) ? personaName : personaId);

            var authorEmail = !string.IsNullOrWhiteSpace(customAuthorEmail)
                ? customAuthorEmail.Trim()
                : $"{personaId.ToLowerInvariant().Replace(' ', '-')}-bot@agentmesh.local";

            var summary = prompt.Trim().Split('\n')[0];
            if (summary.Length > 60) summary = summary[..60] + "...";
            var commitMsg = $"feat(job): turn by @{personaId} - {summary}";

            var (commitCode, _, commitErr) = await RunGitAsync(
                directory,
                $"commit -m \"{EscapeForCommandLine(commitMsg)}\" --author=\"{EscapeForCommandLine(authorName)} <{authorEmail}>\"");

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
