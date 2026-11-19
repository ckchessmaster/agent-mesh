using System.Text.Json;
using AgentMesh.Cli.Commands;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Cli;

public class PrCommandTests
{
    private static CliConfig NewConfig() => new() { MeshUrl = "http://localhost:5000", MasterToken = "test-token" };

    [Fact]
    public async Task Sync_WhenCleanMerge_FetchesAndMergesBaseBranch()
    {
        var calls = new List<(string File, string Args)>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PrCommand.ExecuteAsync(
            ["sync", "--base", "main", "--cwd", "/tmp"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            processRunner: (req, _) =>
            {
                calls.Add((req.FileName, string.Join(' ', req.Arguments)));
                return Task.FromResult(new PrProcessResult(0, "Already up to date.", ""));
            });

        code.Should().Be(PrCommand.SuccessExitCode);
        calls.Should().Equal(
            ("git", "fetch origin main"),
            ("git", "merge --no-edit origin/main"));
        stdout.ToString().Should().Contain("SYNC_OK");
    }

    [Fact]
    public async Task Sync_WhenMergeConflicts_EmitsStructuredMergeConflictListAndReturnsConflictExitCode()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PrCommand.ExecuteAsync(
            ["sync", "--base", "main", "--cwd", "/tmp"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            processRunner: (req, _) =>
            {
                var cmd = $"{req.FileName} {string.Join(' ', req.Arguments)}";
                return Task.FromResult(cmd switch
                {
                    "git fetch origin main" => new PrProcessResult(0, "", ""),
                    "git merge --no-edit origin/main" => new PrProcessResult(1, "CONFLICT (content): Merge conflict in src/Api/Program.cs", ""),
                    "git diff --name-only --diff-filter=U" => new PrProcessResult(0, "src/Api/Program.cs\nsrc/Core/Job.cs\n", ""),
                    _ => new PrProcessResult(0, "", "")
                });
            });

        code.Should().Be(PrCommand.ConflictExitCode);
        stderr.ToString().Should().Contain("MERGE_CONFLICT: src/Api/Program.cs, src/Core/Job.cs");
    }

    [Fact]
    public async Task Open_WhenWorkingTreeIsDirty_FailsFastBeforeSyncingOrPushing()
    {
        var calls = new List<string>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PrCommand.ExecuteAsync(
            ["open", "--title", "feat: job pr", "--cwd", "/tmp"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            processRunner: (req, _) =>
            {
                var cmd = $"{req.FileName} {string.Join(' ', req.Arguments)}";
                calls.Add(cmd);
                return Task.FromResult(new PrProcessResult(0, " M src/DirtyFile.cs\n", ""));
            });

        code.Should().Be(PrCommand.ErrorExitCode);
        stderr.ToString().Should().Contain("DIRTY_WORKING_TREE");
        calls.Should().Equal("git status --porcelain");
    }

    [Fact]
    public async Task Open_CleanTree_SyncsPushesCreates3RPrAndRegistersMetadataInApi()
    {
        var calls = new List<(string File, IReadOnlyList<string> Args)>();
        string? capturedMcpTool = null;
        string? capturedMcpArgs = null;
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PrCommand.ExecuteAsync(
            [
                "open",
                "--title", "feat(workflow): job-level PR gate",
                "--result", "Implemented job-level PR gate and CLI harness.",
                "--reasoning", "Moves git operations into the persona container.",
                "--risk", "Low; verified by unit tests.",
                "--reviewer", "astra",
                "--job-id", jobId.ToString(),
                "--session-id", sessionId.ToString(),
                "--persona", "rook",
                "--cwd", "/tmp"
            ],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            processRunner: (req, _) =>
            {
                calls.Add((req.FileName, req.Arguments));
                var cmd = $"{req.FileName} {string.Join(' ', req.Arguments)}";
                if (cmd == "git status --porcelain") return Task.FromResult(new PrProcessResult(0, "", ""));
                if (cmd == "git branch --show-current") return Task.FromResult(new PrProcessResult(0, "mesh/job-8888\n", ""));
                if (cmd == "git fetch origin main") return Task.FromResult(new PrProcessResult(0, "", ""));
                if (cmd == "git merge --no-edit origin/main") return Task.FromResult(new PrProcessResult(0, "Already up to date.", ""));
                if (cmd == "git rev-parse HEAD") return Task.FromResult(new PrProcessResult(0, "9f8e7d6c5b4a3210\n", ""));
                if (cmd == "git push -u origin mesh/job-8888") return Task.FromResult(new PrProcessResult(0, "", ""));
                if (cmd.StartsWith("gh pr list", StringComparison.Ordinal)) return Task.FromResult(new PrProcessResult(0, "[]", ""));
                if (cmd.StartsWith("gh pr create", StringComparison.Ordinal))
                    return Task.FromResult(new PrProcessResult(0, "https://github.com/ckchessmaster/agent-mesh/pull/77\n", ""));
                return Task.FromResult(new PrProcessResult(0, "", ""));
            },
            mcpExecutor: (tool, argsJson, sid, pid, _, _) =>
            {
                capturedMcpTool = tool;
                capturedMcpArgs = argsJson;
                sid.Should().Be(sessionId);
                pid.Should().Be("rook");
                return Task.FromResult("{\"success\":true}");
            });

        code.Should().Be(PrCommand.SuccessExitCode, stderr.ToString());
        stdout.ToString().Should().Contain("PR_OPENED: #77 https://github.com/ckchessmaster/agent-mesh/pull/77 (HEAD: 9f8e7d6c5b4a3210)");

        // Verify 3-R template formatting passed to gh pr create
        var createCall = calls.Single(c => c.File == "gh" && c.Args.Count > 1 && c.Args[1] == "create");
        var bodyIdx = createCall.Args.ToList().IndexOf("--body") + 1;
        createCall.Args[bodyIdx].Should().Contain("## Summary (3-R Standard)")
            .And.Contain("**Result:** Implemented job-level PR gate and CLI harness.")
            .And.Contain("**Reasoning:** Moves git operations into the persona container.")
            .And.Contain("**Risk:** Low; verified by unit tests.");

        capturedMcpTool.Should().Be("open_pull_request");
        using var mcpDoc = JsonDocument.Parse(capturedMcpArgs!);
        mcpDoc.RootElement.GetProperty("pr_number").GetInt32().Should().Be(77);
        mcpDoc.RootElement.GetProperty("pr_url").GetString().Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/77");
        mcpDoc.RootElement.GetProperty("head_sha").GetString().Should().Be("9f8e7d6c5b4a3210");
    }

    [Fact]
    public async Task Open_WhenOpenPrAlreadyExistsForHeadBranch_UpdatesAndReturnsExistingPrIdempotently()
    {
        var calls = new List<string>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PrCommand.ExecuteAsync(
            ["open", "--title", "feat: update pr", "--head", "mesh/job-88", "--no-api-sync", "--cwd", "/tmp"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            processRunner: (req, _) =>
            {
                var cmd = $"{req.FileName} {string.Join(' ', req.Arguments)}";
                calls.Add(cmd);
                if (cmd == "git status --porcelain") return Task.FromResult(new PrProcessResult(0, "", ""));
                if (cmd == "git rev-parse HEAD") return Task.FromResult(new PrProcessResult(0, "abcdef1234567890\n", ""));
                if (cmd.StartsWith("gh pr list", StringComparison.Ordinal))
                    return Task.FromResult(new PrProcessResult(0, """[{"number":88,"url":"https://github.com/ckchessmaster/agent-mesh/pull/88"}]""", ""));
                return Task.FromResult(new PrProcessResult(0, "", ""));
            });

        code.Should().Be(PrCommand.SuccessExitCode);
        calls.Should().NotContain(c => c.StartsWith("gh pr create", StringComparison.Ordinal));
        calls.Should().Contain(c => c.StartsWith("gh pr edit 88", StringComparison.Ordinal));
        stdout.ToString().Should().Contain("PR_OPENED: #88 https://github.com/ckchessmaster/agent-mesh/pull/88");
    }

    [Fact]
    public async Task Merge_WhenHeadShaMovedAfterApproval_FailsClosedWithShaMoved()
    {
        var calls = new List<string>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PrCommand.ExecuteAsync(
            ["merge", "--pr", "77", "--approved-sha", "1111111aaaaaaa", "--cwd", "/tmp"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            processRunner: (req, _) =>
            {
                var cmd = $"{req.FileName} {string.Join(' ', req.Arguments)}";
                calls.Add(cmd);
                if (cmd == "git rev-parse HEAD") return Task.FromResult(new PrProcessResult(0, "2222222bbbbbbb\n", ""));
                return Task.FromResult(new PrProcessResult(0, "", ""));
            });

        code.Should().Be(PrCommand.ShaMovedExitCode);
        stderr.ToString().Should().Contain("sha_moved");
        calls.Should().NotContain(c => c.StartsWith("gh pr merge", StringComparison.Ordinal),
            "merge must never execute if HEAD moved after approval");
    }

    [Fact]
    public async Task Merge_WhenApprovedShaMatchesHead_ExecutesGhPrMergeWithMatchHeadCommit()
    {
        var calls = new List<string>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PrCommand.ExecuteAsync(
            ["merge", "--pr", "77", "--approved-sha", "abcdef1234567890", "--method", "squash", "--cwd", "/tmp"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            processRunner: (req, _) =>
            {
                var cmd = $"{req.FileName} {string.Join(' ', req.Arguments)}";
                calls.Add(cmd);
                if (cmd == "git rev-parse HEAD") return Task.FromResult(new PrProcessResult(0, "abcdef1234567890\n", ""));
                if (cmd.StartsWith("gh pr view", StringComparison.Ordinal))
                    return Task.FromResult(new PrProcessResult(0, """{"headRefOid":"abcdef1234567890","state":"OPEN","number":77,"url":"https://github.com/o/r/pull/77"}""", ""));
                return Task.FromResult(new PrProcessResult(0, "Merged!", ""));
            });

        code.Should().Be(PrCommand.SuccessExitCode);
        calls.Should().Contain("gh pr merge 77 --squash --match-head-commit abcdef1234567890");
        stdout.ToString().Should().Contain("PR_MERGED");
    }

    [Fact]
    public void BuildProcessEnvironment_UsesAgentMeshGitHubTokenOrPersistedPat_WhenGhTokenIsStrippedByCopilotAdapter()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"pr-cmd-pat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempHome, ".agentmesh"));
        File.WriteAllText(Path.Combine(tempHome, ".agentmesh", "github-pat"), "ghp_fromPersistedPatFile\n");

        try
        {
            var fromAgentMeshEnv = PrCommand.BuildProcessEnvironment(
                key => key switch
                {
                    "AGENTMESH_GITHUB_TOKEN" => "ghp_personaRepoToken",
                    "COPILOT_GITHUB_TOKEN" => "gho_emuCopilotToken",
                    _ => null
                },
                tempHome);

            fromAgentMeshEnv["GH_TOKEN"].Should().Be("ghp_personaRepoToken");
            fromAgentMeshEnv["GITHUB_TOKEN"].Should().Be("ghp_personaRepoToken");

            var fromPersistedFile = PrCommand.BuildProcessEnvironment(_ => null, tempHome);
            fromPersistedFile["GH_TOKEN"].Should().Be("ghp_fromPersistedPatFile");
            fromPersistedFile["GITHUB_TOKEN"].Should().Be("ghp_fromPersistedPatFile");
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }
}
