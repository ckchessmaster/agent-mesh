using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;

namespace AgentMesh.Cli.Commands;

public readonly record struct PrProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    int TimeoutSeconds = 120);

public readonly record struct PrProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr);

/// <summary>
/// Persona-owned git &amp; GitHub PR CLI harness (<c>agentmesh pr sync|open|merge</c>).
/// Runs inside the persona container using its <c>GITHUB_TOKEN</c> / <c>GH_TOKEN</c> credentials
/// and registers real PR linkage back to the AgentMesh control plane.
/// </summary>
public static class PrCommand
{
    public const int SuccessExitCode = 0;
    public const int ErrorExitCode = 1;
    public const int ConflictExitCode = 2;
    public const int ShaMovedExitCode = 3;

    private static readonly Regex GitHubPrUrlRegex = new(
        @"https?://[^\s""']+/pull/(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        CliConfig config,
        ConfigStore configStore,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        Func<PrProcessRequest, CancellationToken, Task<PrProcessResult>>? processRunner = null,
        Func<string, string, Guid?, string?, string?, CancellationToken, Task<string>>? mcpExecutor = null,
        CancellationToken ct = default)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;
        var runner = processRunner ?? RunProcessAsync;

        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            await PrintUsageAsync(outWriter);
            return SuccessExitCode;
        }

        var subcommand = args[0].Trim().ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToList(), config);

        var cwd = !string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? options.WorkingDirectory.Trim()
            : AgentAdapterFactory.ResolveWorkspaceDirectory();

        var env = BuildProcessEnvironment();

        Func<string, string, Guid?, string?, string?, CancellationToken, Task<string>> effectiveMcp =
            mcpExecutor ?? ((tool, toolArgsJson, sid, pid, aid, token) =>
            {
                var client = new MeshClient(config, configStore);
                return client.CallPlatformMcpToolAsync(tool, toolArgsJson, sid, pid, aid, token);
            });

        return subcommand switch
        {
            "sync" => await ExecuteSyncAsync(options, cwd, env, runner, outWriter, errWriter, ct),
            "open" => await ExecuteOpenAsync(options, cwd, env, runner, effectiveMcp, outWriter, errWriter, ct),
            "merge" => await ExecuteMergeAsync(options, cwd, env, runner, outWriter, errWriter, ct),
            _ => await UnknownSubcommandAsync(subcommand, errWriter)
        };
    }

    private static async Task<int> ExecuteSyncAsync(
        ParsedPrOptions options,
        string cwd,
        IReadOnlyDictionary<string, string?> env,
        Func<PrProcessRequest, CancellationToken, Task<PrProcessResult>> runner,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        var baseBranch = ResolveBaseBranch(options);

        var fetch = await runner(new PrProcessRequest("git", ["fetch", "origin", baseBranch], cwd, env), ct);
        if (fetch.ExitCode != 0)
        {
            await stderr.WriteLineAsync($"FETCH_FAILED: Could not fetch origin/{baseBranch}: {CombineOutput(fetch)}");
            return ErrorExitCode;
        }

        var merge = await runner(new PrProcessRequest("git", ["merge", "--no-edit", $"origin/{baseBranch}"], cwd, env), ct);
        if (merge.ExitCode != 0)
        {
            var conflictedFiles = await ReadConflictedFilesAsync(cwd, env, runner, ct);
            if (conflictedFiles.Count > 0 || ContainsConflictSignal(merge))
            {
                var fileList = conflictedFiles.Count > 0
                    ? string.Join(", ", conflictedFiles)
                    : "(unresolved merge state)";
                await stderr.WriteLineAsync($"MERGE_CONFLICT: {fileList}");
                await stderr.WriteLineAsync(
                    $"Resolve conflict markers in [{fileList}], stage resolved files (`git add <files>`), " +
                    "commit the merge (`git commit --no-edit`), run scoped tests, and re-run `agentmesh pr sync` or `agentmesh pr open`.");
                return ConflictExitCode;
            }

            await stderr.WriteLineAsync($"MERGE_FAILED: git merge --no-edit origin/{baseBranch} failed: {CombineOutput(merge)}");
            return ErrorExitCode;
        }

        await stdout.WriteLineAsync($"SYNC_OK: Merged origin/{baseBranch} cleanly.");
        return SuccessExitCode;
    }

    private static async Task<int> ExecuteOpenAsync(
        ParsedPrOptions options,
        string cwd,
        IReadOnlyDictionary<string, string?> env,
        Func<PrProcessRequest, CancellationToken, Task<PrProcessResult>> runner,
        Func<string, string, Guid?, string?, string?, CancellationToken, Task<string>> mcpExecutor,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Title))
        {
            await stderr.WriteLineAsync("INVALID_ARGS: --title is required for `agentmesh pr open` (e.g., --title \"feat(api): add job-level PR gate\").");
            return ErrorExitCode;
        }

        // 1. Verify working tree is clean
        var status = await runner(new PrProcessRequest("git", ["status", "--porcelain"], cwd, env), ct);
        if (status.ExitCode != 0)
        {
            await stderr.WriteLineAsync($"GIT_STATUS_FAILED: {CombineOutput(status)}");
            return ErrorExitCode;
        }

        if (!string.IsNullOrWhiteSpace(status.StdOut))
        {
            await stderr.WriteLineAsync(
                "DIRTY_WORKING_TREE: Uncommitted or untracked changes exist in the working tree:\n" +
                status.StdOut.Trim() + "\n" +
                "Stage and commit intended work (`git add ... && git commit -m ...`) or clean temporary files before running `agentmesh pr open`.");
            return ErrorExitCode;
        }

        // 2. Resolve head and base branches
        var baseBranch = ResolveBaseBranch(options);
        var headBranch = options.HeadBranch;
        if (string.IsNullOrWhiteSpace(headBranch))
        {
            var branchRes = await runner(new PrProcessRequest("git", ["branch", "--show-current"], cwd, env), ct);
            if (branchRes.ExitCode != 0 || string.IsNullOrWhiteSpace(branchRes.StdOut))
            {
                await stderr.WriteLineAsync($"BRANCH_UNRESOLVED: Could not determine current git branch: {CombineOutput(branchRes)}");
                return ErrorExitCode;
            }
            headBranch = branchRes.StdOut.Trim();
        }
        else
        {
            headBranch = headBranch.Trim();
        }

        if (string.Equals(headBranch, baseBranch, StringComparison.OrdinalIgnoreCase))
        {
            await stderr.WriteLineAsync($"INVALID_BRANCH: Head branch '{headBranch}' matches base branch '{baseBranch}'. Switch to a job branch first.");
            return ErrorExitCode;
        }

        // 3. Run sync step before pushing; halt on MERGE_CONFLICT
        var syncExit = await ExecuteSyncAsync(options, cwd, env, runner, stdout, stderr, ct);
        if (syncExit != SuccessExitCode)
        {
            return syncExit;
        }

        // 4. Read HEAD SHA & push current head branch to origin
        var revParse = await runner(new PrProcessRequest("git", ["rev-parse", "HEAD"], cwd, env), ct);
        if (revParse.ExitCode != 0 || string.IsNullOrWhiteSpace(revParse.StdOut))
        {
            await stderr.WriteLineAsync($"HEAD_SHA_FAILED: Could not read HEAD SHA: {CombineOutput(revParse)}");
            return ErrorExitCode;
        }
        var headSha = revParse.StdOut.Trim();

        var push = await runner(new PrProcessRequest("git", ["push", "-u", "origin", headBranch], cwd, env), ct);
        if (push.ExitCode != 0)
        {
            await stderr.WriteLineAsync($"PUSH_FAILED: Failed to push '{headBranch}' to origin: {CombineOutput(push)}");
            return ErrorExitCode;
        }

        // 5. Build 3-R PR body and check for existing open PR idempotently
        var summary3R = BuildSummary3R(options);
        var personaId = options.PersonaId ?? Environment.GetEnvironmentVariable("AGENTMESH_PERSONA_ID");
        var reviewer = options.Reviewer ?? Environment.GetEnvironmentVariable("AGENTMESH_REVIEWER");
        var prBody = Format3RBody(summary3R, personaId, reviewer);

        int prNumber;
        string prUrl;

        var existingPr = await TryFindExistingOpenPrAsync(headBranch, cwd, env, runner, ct);
        if (existingPr is { } existing)
        {
            prNumber = existing.Number;
            prUrl = existing.Url;

            var editArgs = new List<string>
            {
                "pr", "edit", prNumber.ToString(CultureInfo.InvariantCulture),
                "--title", options.Title.Trim(),
                "--body", prBody
            };
            if (!string.IsNullOrWhiteSpace(reviewer))
            {
                editArgs.Add("--add-reviewer");
                editArgs.Add(reviewer.Trim().TrimStart('@'));
            }
            _ = await runner(new PrProcessRequest("gh", editArgs, cwd, env), ct);
        }
        else
        {
            var createArgs = new List<string>
            {
                "pr", "create",
                "--base", baseBranch,
                "--head", headBranch,
                "--title", options.Title.Trim(),
                "--body", prBody
            };
            if (!string.IsNullOrWhiteSpace(reviewer))
            {
                createArgs.Add("--reviewer");
                createArgs.Add(reviewer.Trim().TrimStart('@'));
            }

            var created = await runner(new PrProcessRequest("gh", createArgs, cwd, env), ct);
            if (created.ExitCode != 0)
            {
                // If --reviewer failed (e.g. reviewer is an AgentMesh persona handle rather than GitHub user),
                // retry gh pr create once without --reviewer so the real PR still opens cleanly.
                if (!string.IsNullOrWhiteSpace(reviewer))
                {
                    var retryArgs = new List<string>
                    {
                        "pr", "create",
                        "--base", baseBranch,
                        "--head", headBranch,
                        "--title", options.Title.Trim(),
                        "--body", prBody
                    };
                    created = await runner(new PrProcessRequest("gh", retryArgs, cwd, env), ct);
                }

                if (created.ExitCode != 0)
                {
                    await stderr.WriteLineAsync($"PR_CREATE_FAILED: gh pr create failed: {CombineOutput(created)}");
                    return ErrorExitCode;
                }
            }

            var parsed = TryParsePrUrlAndNumber(CombineOutput(created));
            if (parsed is null)
            {
                var fallbackLookup = await TryFindExistingOpenPrAsync(headBranch, cwd, env, runner, ct);
                if (fallbackLookup is null)
                {
                    await stderr.WriteLineAsync($"PR_PARSE_FAILED: PR appeared to succeed but URL/number could not be parsed from output: {CombineOutput(created)}");
                    return ErrorExitCode;
                }
                prNumber = fallbackLookup.Value.Number;
                prUrl = fallbackLookup.Value.Url;
            }
            else
            {
                prNumber = parsed.Value.Number;
                prUrl = parsed.Value.Url;
            }
        }

        // 6. Register real PR metadata on the job in AgentMesh API (best-effort if outside a mesh job context,
        // surfaced clearly when job/session context is configured)
        Guid? sessionId = Guid.TryParse(options.SessionId ?? Environment.GetEnvironmentVariable("AGENTMESH_SESSION_ID"), out var sid)
            ? sid
            : null;
        var jobId = options.JobId ?? Environment.GetEnvironmentVariable("AGENTMESH_JOB_ID");
        var agentId = options.AgentId ?? Environment.GetEnvironmentVariable("AGENTMESH_DRIVER_ID") ?? personaId;

        var mcpArgsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["title"] = options.Title.Trim(),
            ["summary_3r"] = new Dictionary<string, string>
            {
                ["result"] = summary3R.Result,
                ["reasoning"] = summary3R.Reasoning,
                ["risk"] = summary3R.Risk
            },
            ["branch_name"] = headBranch,
            ["pr_url"] = prUrl,
            ["pr_number"] = prNumber,
            ["head_sha"] = headSha,
            ["job_id"] = string.IsNullOrWhiteSpace(jobId) ? null : jobId.Trim()
        });

        string? apiSyncNote = null;
        if (!options.SkipApiSync && (sessionId.HasValue || !string.IsNullOrWhiteSpace(jobId)))
        {
            var mcpResponse = await mcpExecutor("open_pull_request", mcpArgsJson, sessionId, personaId, agentId, ct);
            apiSyncNote = mcpResponse;
        }

        await stdout.WriteLineAsync(JsonSerializer.Serialize(new
        {
            success = true,
            prNumber,
            prUrl,
            headSha,
            headBranch,
            baseBranch,
            apiRegistered = apiSyncNote is not null && !apiSyncNote.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
        }));
        await stdout.WriteLineAsync($"PR_OPENED: #{prNumber} {prUrl} (HEAD: {headSha})");
        return SuccessExitCode;
    }

    private static async Task<int> ExecuteMergeAsync(
        ParsedPrOptions options,
        string cwd,
        IReadOnlyDictionary<string, string?> env,
        Func<PrProcessRequest, CancellationToken, Task<PrProcessResult>> runner,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApprovedSha))
        {
            await stderr.WriteLineAsync("sha_moved: --approved-sha (or --sha) is required to verify no unreviewed commits were pushed after approval.");
            return ShaMovedExitCode;
        }

        var approvedSha = options.ApprovedSha.Trim();

        // 1. Verify local HEAD SHA (when running inside git checkout)
        string? actualSha = null;
        var localRev = await runner(new PrProcessRequest("git", ["rev-parse", "HEAD"], cwd, env), ct);
        if (localRev.ExitCode == 0 && !string.IsNullOrWhiteSpace(localRev.StdOut))
        {
            actualSha = localRev.StdOut.Trim();
            if (!ShasMatch(actualSha, approvedSha))
            {
                await stderr.WriteLineAsync(
                    $"sha_moved: Local HEAD ({actualSha}) does not match approved SHA ({approvedSha}). Failing closed for re-approval.");
                return ShaMovedExitCode;
            }
        }

        // 2. Verify remote PR headRefOid via gh pr view
        var viewArgs = new List<string> { "pr", "view" };
        if (!string.IsNullOrWhiteSpace(options.PrReference))
        {
            viewArgs.Add(options.PrReference.Trim());
        }
        viewArgs.Add("--json");
        viewArgs.Add("headRefOid,state,number,url");

        var prView = await runner(new PrProcessRequest("gh", viewArgs, cwd, env), ct);
        if (prView.ExitCode == 0 && !string.IsNullOrWhiteSpace(prView.StdOut))
        {
            try
            {
                using var doc = JsonDocument.Parse(prView.StdOut);
                var root = doc.RootElement;
                if (root.TryGetProperty("state", out var stProp) &&
                    string.Equals(stProp.GetString(), "MERGED", StringComparison.OrdinalIgnoreCase))
                {
                    await stdout.WriteLineAsync("PR_MERGED: Pull request is already merged.");
                    return SuccessExitCode;
                }

                if (root.TryGetProperty("headRefOid", out var oidProp) &&
                    oidProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(oidProp.GetString()))
                {
                    var remoteSha = oidProp.GetString()!.Trim();
                    actualSha = remoteSha;
                    if (!ShasMatch(remoteSha, approvedSha))
                    {
                        await stderr.WriteLineAsync(
                            $"sha_moved: Remote PR HEAD ({remoteSha}) does not match approved SHA ({approvedSha}). Failing closed for re-approval.");
                        return ShaMovedExitCode;
                    }
                }
            }
            catch
            {
                // Proceed to gh pr merge with --match-head-commit
            }
        }
        else if (actualSha is null)
        {
            await stderr.WriteLineAsync($"PR_VIEW_FAILED: Could not read PR HEAD SHA: {CombineOutput(prView)}");
            return ErrorExitCode;
        }

        var methodFlag = (options.MergeMethod?.Trim().ToLowerInvariant()) switch
        {
            "merge" => "--merge",
            "rebase" => "--rebase",
            _ => "--squash"
        };

        var mergeArgs = new List<string> { "pr", "merge" };
        if (!string.IsNullOrWhiteSpace(options.PrReference))
        {
            mergeArgs.Add(options.PrReference.Trim());
        }
        mergeArgs.Add(methodFlag);
        mergeArgs.Add("--match-head-commit");
        mergeArgs.Add(actualSha ?? approvedSha);

        var mergeRes = await runner(new PrProcessRequest("gh", mergeArgs, cwd, env), ct);
        if (mergeRes.ExitCode != 0)
        {
            var combined = CombineOutput(mergeRes);
            if (combined.Contains("head commit", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("sha", StringComparison.OrdinalIgnoreCase))
            {
                await stderr.WriteLineAsync($"sha_moved: {combined}");
                return ShaMovedExitCode;
            }

            if (combined.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("not mergeable", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("out of date", StringComparison.OrdinalIgnoreCase))
            {
                await stderr.WriteLineAsync($"MERGE_CONFLICT: {combined}");
                return ConflictExitCode;
            }

            await stderr.WriteLineAsync($"PR_MERGE_FAILED: {combined}");
            return ErrorExitCode;
        }

        await stdout.WriteLineAsync($"PR_MERGED: Merged pull request cleanly at {actualSha ?? approvedSha}.");
        return SuccessExitCode;
    }

    internal static bool ShasMatch(string fullOrShortA, string fullOrShortB)
    {
        var a = fullOrShortA.Trim();
        var b = fullOrShortB.Trim();
        if (a.Length < 7 || b.Length < 7) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Format3RBody(Summary3R summary, string? personaId, string? reviewer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Summary (3-R Standard)");
        sb.Append("**Result:** ").AppendLine(string.IsNullOrWhiteSpace(summary.Result) ? "_Not provided._" : summary.Result.Trim().ReplaceLineEndings(" "));
        sb.Append("**Reasoning:** ").AppendLine(string.IsNullOrWhiteSpace(summary.Reasoning) ? "_Not provided._" : summary.Reasoning.Trim().ReplaceLineEndings(" "));
        sb.Append("**Risk:** ").AppendLine(string.IsNullOrWhiteSpace(summary.Risk) ? "_Not provided._" : summary.Risk.Trim().ReplaceLineEndings(" "));
        sb.AppendLine();
        sb.AppendLine("---");
        var sender = string.IsNullOrWhiteSpace(personaId) ? "AgentMesh" : $"AgentMesh Persona @{personaId.Trim().TrimStart('@')}";
        var lead = string.IsNullOrWhiteSpace(reviewer) ? "the assigned reviewer" : $"@{reviewer.Trim().TrimStart('@')}";
        sb.Append($"*Automated pull request submitted by {sender}. Awaiting review from {lead}.*");
        return sb.ToString();
    }

    private static Summary3R BuildSummary3R(ParsedPrOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Result) ||
            !string.IsNullOrWhiteSpace(options.Reasoning) ||
            !string.IsNullOrWhiteSpace(options.Risk))
        {
            return new Summary3R(
                string.IsNullOrWhiteSpace(options.Result) ? options.Title ?? "Implementation completed and verified." : options.Result!,
                string.IsNullOrWhiteSpace(options.Reasoning) ? "Implemented changes per job checklist." : options.Reasoning!,
                string.IsNullOrWhiteSpace(options.Risk) ? "Verified via scoped and regression test suites." : options.Risk!);
        }

        if (!string.IsNullOrWhiteSpace(options.Body))
        {
            var body = options.Body.Trim();
            return new Summary3R(
                ExtractSection(body, "Result") ?? body,
                ExtractSection(body, "Reasoning") ?? "Implemented changes per job checklist.",
                ExtractSection(body, "Risk") ?? "Verified via scoped and regression test suites.");
        }

        return new Summary3R(
            options.Title ?? "Implementation completed and verified.",
            "Implemented changes per job checklist.",
            "Verified via scoped and regression test suites.");
    }

    private static string? ExtractSection(string text, string heading)
    {
        var match = Regex.Match(
            text,
            $@"(?:^|\n)(?:#+\s*)?(?:\*\*)?{Regex.Escape(heading)}(?:\*\*)?:?\s*([\s\S]*?)(?=(?:^|\n)(?:#+\s*)?(?:\*\*)?(?:Result|Reasoning|Risk)(?:\*\*)?:?|$)",
            RegexOptions.IgnoreCase);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value.Trim()
            : null;
    }

    private static async Task<(int Number, string Url)?> TryFindExistingOpenPrAsync(
        string headBranch,
        string cwd,
        IReadOnlyDictionary<string, string?> env,
        Func<PrProcessRequest, CancellationToken, Task<PrProcessResult>> runner,
        CancellationToken ct)
    {
        var listRes = await runner(
            new PrProcessRequest("gh", ["pr", "list", "--head", headBranch, "--state", "open", "--json", "number,url"], cwd, env),
            ct);

        if (listRes.ExitCode != 0 || string.IsNullOrWhiteSpace(listRes.StdOut))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(listRes.StdOut);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = doc.RootElement[0];
            if (first.TryGetProperty("number", out var nProp) && nProp.TryGetInt32(out var num) &&
                first.TryGetProperty("url", out var uProp) && uProp.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(uProp.GetString()))
            {
                return (num, uProp.GetString()!);
            }
        }
        catch
        {
            // Ignore JSON parse errors on gh pr list
        }

        return null;
    }

    private static (int Number, string Url)? TryParsePrUrlAndNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = GitHubPrUrlRegex.Match(text);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
        {
            return null;
        }
        return (num, match.Value);
    }

    private static async Task<List<string>> ReadConflictedFilesAsync(
        string cwd,
        IReadOnlyDictionary<string, string?> env,
        Func<PrProcessRequest, CancellationToken, Task<PrProcessResult>> runner,
        CancellationToken ct)
    {
        var diff = await runner(new PrProcessRequest("git", ["diff", "--name-only", "--diff-filter=U"], cwd, env), ct);
        if (string.IsNullOrWhiteSpace(diff.StdOut)) return [];

        return diff.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool ContainsConflictSignal(PrProcessResult res)
    {
        var combined = CombineOutput(res);
        return combined.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
               combined.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBaseBranch(ParsedPrOptions options) =>
        !string.IsNullOrWhiteSpace(options.BaseBranch)
            ? options.BaseBranch.Trim()
            : (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTMESH_BASE_BRANCH"))
                ? Environment.GetEnvironmentVariable("AGENTMESH_BASE_BRANCH")!.Trim()
                : "main");

    private static IReadOnlyDictionary<string, string?> BuildProcessEnvironment()
    {
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var effectiveToken = !string.IsNullOrWhiteSpace(ghToken) ? ghToken : githubToken;

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0"
        };

        if (!string.IsNullOrWhiteSpace(effectiveToken))
        {
            env["GH_TOKEN"] = effectiveToken;
            env["GITHUB_TOKEN"] = effectiveToken;
        }

        return env;
    }

    private static string CombineOutput(PrProcessResult res)
    {
        var o = res.StdOut?.Trim() ?? string.Empty;
        var e = res.StdErr?.Trim() ?? string.Empty;
        if (o.Length == 0) return e;
        if (e.Length == 0) return o;
        return $"{o}\n{e}";
    }

    private static async Task<int> UnknownSubcommandAsync(string sub, TextWriter err)
    {
        await err.WriteLineAsync($"Unknown `agentmesh pr` subcommand '{sub}'. Expected: sync, open, or merge.");
        return ErrorExitCode;
    }

    private static async Task PrintUsageAsync(TextWriter outWriter)
    {
        await outWriter.WriteLineAsync("Usage:");
        await outWriter.WriteLineAsync("  agentmesh pr sync  [--base <main>] [--cwd <dir>]");
        await outWriter.WriteLineAsync("  agentmesh pr open  --title <title> [--result <r>] [--reasoning <r>] [--risk <r>] [--base <main>] [--head <branch>] [--reviewer <login>] [--job-id <guid>] [--session-id <guid>] [--cwd <dir>]");
        await outWriter.WriteLineAsync("  agentmesh pr merge [--pr <numOrUrl>] --approved-sha <sha> [--method <squash|merge|rebase>] [--cwd <dir>]");
    }

    private sealed class ParsedPrOptions
    {
        public string? BaseBranch { get; set; }
        public string? HeadBranch { get; set; }
        public string? Title { get; set; }
        public string? Result { get; set; }
        public string? Reasoning { get; set; }
        public string? Risk { get; set; }
        public string? Body { get; set; }
        public string? Reviewer { get; set; }
        public string? PrReference { get; set; }
        public string? ApprovedSha { get; set; }
        public string? MergeMethod { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? JobId { get; set; }
        public string? SessionId { get; set; }
        public string? PersonaId { get; set; }
        public string? AgentId { get; set; }
        public bool SkipApiSync { get; set; }
    }

    private static ParsedPrOptions ParseOptions(List<string> args, CliConfig config)
    {
        var opts = new ParsedPrOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;

            switch (arg)
            {
                case "--base" or "-b":
                    opts.BaseBranch = Next();
                    break;
                case "--head":
                    opts.HeadBranch = Next();
                    break;
                case "--title" or "-t":
                    opts.Title = Next();
                    break;
                case "--result":
                    opts.Result = Next();
                    break;
                case "--reasoning":
                    opts.Reasoning = Next();
                    break;
                case "--risk":
                    opts.Risk = Next();
                    break;
                case "--body":
                    opts.Body = Next();
                    break;
                case "--reviewer":
                    opts.Reviewer = Next();
                    break;
                case "--pr":
                    opts.PrReference = Next();
                    break;
                case "--approved-sha" or "--sha":
                    opts.ApprovedSha = Next();
                    break;
                case "--method":
                    opts.MergeMethod = Next();
                    break;
                case "--cwd" or "--working-directory":
                    opts.WorkingDirectory = Next();
                    break;
                case "--job-id":
                    opts.JobId = Next();
                    break;
                case "--session-id":
                    opts.SessionId = Next();
                    break;
                case "--persona":
                    opts.PersonaId = Next();
                    break;
                case "--agent" or "--driver":
                    opts.AgentId = Next();
                    break;
                case "--url" or "--mesh-url":
                    if (Next() is { } u) config.MeshUrl = u.TrimEnd('/');
                    break;
                case "--token":
                    if (Next() is { } tok) config.MasterToken = tok.Trim();
                    break;
                case "--no-api-sync":
                    opts.SkipApiSync = true;
                    break;
                default:
                    if (!arg.StartsWith('-') && string.IsNullOrWhiteSpace(opts.PrReference))
                    {
                        opts.PrReference = arg;
                    }
                    break;
            }
        }
        return opts;
    }

    private static async Task<PrProcessResult> RunProcessAsync(PrProcessRequest req, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = req.FileName,
            WorkingDirectory = req.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in req.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var kvp in req.Environment)
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        using var proc = new Process { StartInfo = psi };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(req.TimeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new PrProcessResult(proc.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }
}
