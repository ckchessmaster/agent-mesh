using System.Diagnostics;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Services;

public class ProjectWorkspaceSetupTests : IDisposable
{
    private readonly string _tempRoot;

    public ProjectWorkspaceSetupTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("agentmesh-setup-tests-").FullName;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp directories (e.g. git pack files).
        }
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_LocalGitProject_CreatesStandardStructureAndInitialCommit()
    {
        var reposRoot = Path.Combine(_tempRoot, "repos");
        var setup = new ProjectWorkspaceSetup(gitRunner: null, reposRootOverride: reposRoot);

        var project = new Project
        {
            Id = "payments-api",
            Name = "Payments API",
            Description = "Handles payment intents and settlement.",
            RepositoryName = "payments-api",
            WorkingDirectory = string.Empty,
            WorkspaceType = WorkspaceType.Git,
            GitBaseBranch = "main"
        };

        var result = await setup.EnsureWorkspaceAsync(project);

        var expectedDir = WorkspacePathResolver.Normalize(Path.Combine(reposRoot, "payments-api"));
        result.Success.Should().BeTrue(result.Message);
        result.TargetDirectory.Should().Be(expectedDir);
        result.EffectiveDirectory.Should().Be(expectedDir);
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Initialized);
        result.UsedFallback.Should().BeFalse();
        result.CreatedFiles.Should().BeTrue();

        Directory.Exists(Path.Combine(expectedDir, ".git")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedDir, "src")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedDir, "tests")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedDir, "docs")).Should().BeTrue();
        File.Exists(Path.Combine(expectedDir, "src", ".gitkeep")).Should().BeTrue();
        File.Exists(Path.Combine(expectedDir, "tests", ".gitkeep")).Should().BeTrue();
        File.Exists(Path.Combine(expectedDir, "docs", ".gitkeep")).Should().BeTrue();

        var readme = await File.ReadAllTextAsync(Path.Combine(expectedDir, "README.md"));
        readme.Should().Contain("# Payments API");
        readme.Should().Contain("Handles payment intents and settlement.");

        var gitignore = await File.ReadAllTextAsync(Path.Combine(expectedDir, ".gitignore"));
        gitignore.Should().Contain(".agentmesh/");

        var (headExit, headSha, _) = await RunGitAsync(expectedDir, "rev-parse", "--verify", "HEAD");
        headExit.Should().Be(0);
        headSha.Should().NotBeNullOrWhiteSpace();

        var (branchExit, branchName, _) = await RunGitAsync(expectedDir, "branch", "--show-current");
        branchExit.Should().Be(0);
        branchName.Should().Be("main");

        var (statusExit, statusOut, _) = await RunGitAsync(expectedDir, "status", "--porcelain");
        statusExit.Should().Be(0);
        statusOut.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_WhenAlreadyInitialized_IsIdempotentAndPreservesFiles()
    {
        var workDir = Path.Combine(_tempRoot, "existing-project");
        var setup = new ProjectWorkspaceSetup();

        var request = new ProjectWorkspaceSetupRequest(
            ProjectId: "existing-project",
            ProjectName: "Existing Project",
            WorkingDirectory: workDir,
            WorkspaceType: WorkspaceType.Git,
            GitBaseBranch: "develop");

        var first = await setup.EnsureWorkspaceAsync(request);
        first.Success.Should().BeTrue(first.Message);
        first.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Initialized);

        var (_, firstHead, _) = await RunGitAsync(workDir, "rev-parse", "HEAD");
        var readmePath = Path.Combine(workDir, "README.md");
        await File.WriteAllTextAsync(readmePath, "# Custom README\nDo not overwrite.\n");

        var second = await setup.EnsureWorkspaceAsync(request);
        second.Success.Should().BeTrue(second.Message);
        second.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
        second.CreatedFiles.Should().BeFalse();
        second.UsedFallback.Should().BeFalse();

        var (_, secondHead, _) = await RunGitAsync(workDir, "rev-parse", "HEAD");
        secondHead.Should().Be(firstHead);
        (await File.ReadAllTextAsync(readmePath)).Should().Be("# Custom README\nDo not overwrite.\n");
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_WhenGitRepoAlreadyExistsWithoutStandardLayout_DoesNotMutateWorkingTree()
    {
        var workDir = Path.Combine(_tempRoot, "preexisting-git");
        Directory.CreateDirectory(workDir);
        await RunGitAsync(workDir, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(workDir, "app.txt"), "hello\n");
        await RunGitAsync(workDir, "add", "-A");
        await RunGitAsync(workDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "seed");

        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectName: "Preexisting",
            WorkingDirectory: workDir,
            WorkspaceType: WorkspaceType.Git));

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
        result.CreatedFiles.Should().BeFalse();
        File.Exists(Path.Combine(workDir, "README.md")).Should().BeFalse();

        var (_, statusOut, _) = await RunGitAsync(workDir, "status", "--porcelain");
        statusOut.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_MonorepoSubdirectory_DoesNotCreateNestedGitRepo()
    {
        var reposRoot = Path.Combine(_tempRoot, "repos");
        var monorepoDir = Path.Combine(reposRoot, "monorepo");
        var serviceDir = Path.Combine(monorepoDir, "services", "api");
        Directory.CreateDirectory(serviceDir);

        await RunGitAsync(monorepoDir, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(serviceDir, "Program.cs"), "// api\n");
        await RunGitAsync(monorepoDir, "add", "-A");
        await RunGitAsync(monorepoDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "init monorepo");

        var setup = new ProjectWorkspaceSetup(gitRunner: null, reposRootOverride: reposRoot);
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            RepositoryName: "monorepo",
            WorkingDirectory: serviceDir,
            WorkspaceType: WorkspaceType.Git));

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
        Directory.Exists(Path.Combine(serviceDir, ".git")).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_EmptySubdirInsideOuterGitRepo_StillInitializesOwnGitRepo()
    {
        // Mirrors /workspace being a git repo while /workspace/repos/{project} is an empty subdir.
        var outerRoot = Path.Combine(_tempRoot, "outer");
        var reposRoot = Path.Combine(outerRoot, "repos");
        var projectDir = Path.Combine(reposRoot, "fresh-project");
        Directory.CreateDirectory(projectDir);

        await RunGitAsync(outerRoot, "init", "-b", "main");

        var setup = new ProjectWorkspaceSetup(gitRunner: null, reposRootOverride: reposRoot);
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectId: "fresh-project",
            ProjectName: "Fresh Project",
            RepositoryName: "fresh-project",
            WorkspaceType: WorkspaceType.Git));

        result.Success.Should().BeTrue(result.Message);
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Initialized);
        Directory.Exists(Path.Combine(projectDir, ".git")).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_WithPopulatedRemoteUrl_ClonesAndIsIdempotent()
    {
        var bareRepo = Path.Combine(_tempRoot, "remote.git");
        var seedDir = Path.Combine(_tempRoot, "seed");
        var targetDir = Path.Combine(_tempRoot, "cloned-project");
        Directory.CreateDirectory(bareRepo);
        Directory.CreateDirectory(seedDir);

        await RunGitAsync(bareRepo, "init", "--bare");
        await RunGitAsync(seedDir, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "seed.txt"), "from remote\n");
        await RunGitAsync(seedDir, "add", "-A");
        await RunGitAsync(seedDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "initial remote commit");
        await RunGitAsync(seedDir, "remote", "add", "origin", bareRepo);
        await RunGitAsync(seedDir, "push", "origin", "main");

        var setup = new ProjectWorkspaceSetup();
        var request = new ProjectWorkspaceSetupRequest(
            ProjectName: "Cloned Project",
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: bareRepo,
            GitBaseBranch: "main");

        var first = await setup.EnsureWorkspaceAsync(request);
        first.Success.Should().BeTrue(first.Message);
        first.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Cloned);
        first.CreatedFiles.Should().BeFalse();
        File.Exists(Path.Combine(targetDir, "seed.txt")).Should().BeTrue();

        var second = await setup.EnsureWorkspaceAsync(request);
        second.Success.Should().BeTrue(second.Message);
        second.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_WithEmptyRemoteUrl_ClonesAndSeedsStandardStructure()
    {
        var bareRepo = Path.Combine(_tempRoot, "empty-remote.git");
        var targetDir = Path.Combine(_tempRoot, "empty-cloned");
        Directory.CreateDirectory(bareRepo);
        await RunGitAsync(bareRepo, "init", "--bare");

        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectName: "Empty Remote Repo",
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: bareRepo,
            GitBaseBranch: "main"));

        result.Success.Should().BeTrue(result.Message);
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Cloned);
        result.CreatedFiles.Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "README.md")).Should().BeTrue();
        Directory.Exists(Path.Combine(targetDir, "src")).Should().BeTrue();

        var (headExit, _, _) = await RunGitAsync(targetDir, "rev-parse", "--verify", "HEAD");
        headExit.Should().Be(0);
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_S3ArchiveWorkspace_CreatesStandardDirectoriesWithoutGit()
    {
        var targetDir = Path.Combine(_tempRoot, "s3-workspace");
        var setup = new ProjectWorkspaceSetup();

        var request = new ProjectWorkspaceSetupRequest(
            ProjectName: "S3 Docs Project",
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.S3Archive);

        var first = await setup.EnsureWorkspaceAsync(request);
        first.Success.Should().BeTrue(first.Message);
        first.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.DirectoryPrepared);
        first.CreatedFiles.Should().BeTrue();
        Directory.Exists(Path.Combine(targetDir, ".git")).Should().BeFalse();
        Directory.Exists(Path.Combine(targetDir, "src")).Should().BeTrue();
        Directory.Exists(Path.Combine(targetDir, "tests")).Should().BeTrue();
        Directory.Exists(Path.Combine(targetDir, "docs")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, ".gitignore")).Should().BeTrue();

        var second = await setup.EnsureWorkspaceAsync(request);
        second.Success.Should().BeTrue();
        second.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
        second.CreatedFiles.Should().BeFalse();
    }

    [Theory]
    [InlineData("../../etc", null)]
    [InlineData("a/b", null)]
    [InlineData(null, "relative/path")]
    [InlineData(null, "/")]
    [InlineData(null, "/workspace/repos")]
    public async Task EnsureWorkspaceAsync_UnsafeOrProtectedPath_FailsWithoutFallback(
        string? repoName,
        string? workingDir)
    {
        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            RepositoryName: repoName,
            WorkingDirectory: workingDir));

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Failed);
        result.UsedFallback.Should().BeFalse();
        result.EffectiveDirectory.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_WhenCloneFailsAndFallbackProvided_UsesFallbackAndLeavesTargetClean()
    {
        var targetDir = Path.Combine(_tempRoot, "broken-clone-target");
        var fallbackDir = Path.Combine(_tempRoot, "fallback-workspace");
        var setup = new ProjectWorkspaceSetup();

        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectName: "Broken Remote",
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: Path.Combine(_tempRoot, "does-not-exist.git"),
            FallbackDirectory: fallbackDir));

        result.Success.Should().BeTrue();
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.FallbackUsed);
        result.UsedFallback.Should().BeTrue();
        result.EffectiveDirectory.Should().Be(WorkspacePathResolver.Normalize(fallbackDir));
        result.Warnings.Should().NotBeEmpty();
        Directory.Exists(fallbackDir).Should().BeTrue();
        Directory.Exists(targetDir).Should().BeFalse("a failed clone must not leave a broken target directory behind");
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_UnsafeGitRemoteUrl_IsRejected()
    {
        var targetDir = Path.Combine(_tempRoot, "unsafe-url-target");
        var setup = new ProjectWorkspaceSetup();

        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: "--upload-pack=touch /tmp/pwned"));

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Failed);
        Directory.Exists(targetDir).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_ConcurrentCallsForSameDirectory_SerializeCleanly()
    {
        var workDir = Path.Combine(_tempRoot, "concurrent-project");
        var setup = new ProjectWorkspaceSetup();
        var request = new ProjectWorkspaceSetupRequest(
            ProjectName: "Concurrent Project",
            WorkingDirectory: workDir,
            WorkspaceType: WorkspaceType.Git);

        var results = await Task.WhenAll(
            setup.EnsureWorkspaceAsync(request),
            setup.EnsureWorkspaceAsync(request));

        results.Should().OnlyContain(r => r.Success);
        results.Select(r => r.Outcome).Should().BeEquivalentTo(
        [
            ProjectWorkspaceSetupOutcome.Initialized,
            ProjectWorkspaceSetupOutcome.AlreadyInitialized
        ]);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Remote identity: an existing checkout must be the project's own repository.
    //
    // A project whose repository_name still carries the pre-multi-repo placeholder resolves onto
    // whichever checkout is mounted at /workspace/repos/agent-mesh — in this deployment, the platform's
    // own repository. Before this check the fast path accepted any directory containing .git, so the job
    // branched and committed against the wrong remote while every command reported success.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureWorkspaceAsync_ExistingCheckoutOfADifferentRemote_FailsAndLeavesItAlone()
    {
        var remoteA = await CreateSeededBareRepoAsync("remote-a.git");
        var remoteB = await CreateSeededBareRepoAsync("remote-b.git");
        var parent = Path.Combine(_tempRoot, "repos");
        var targetDir = Path.Combine(parent, "budget-app");
        Directory.CreateDirectory(parent);
        await RunGitAsync(parent, "clone", "--", remoteA, targetDir);

        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectId: "budget-app",
            ProjectName: "Budget App",
            RepositoryName: "budget-app",
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: remoteB,
            GitBaseBranch: "main"));

        result.Success.Should().BeFalse(result.Message);
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.Failed);
        result.UsedFallback.Should().BeFalse();
        result.Message.Should().Contain("is a checkout of").And.Contain(remoteB);

        // Refusing must be a no-op on the tree it refused: no branch, no commit, no dirty files.
        var (headExit, headSha, _) = await RunGitAsync(targetDir, "rev-parse", "HEAD");
        headExit.Should().Be(0);
        headSha.Should().NotBeEmpty();
        var (_, porcelain, _) = await RunGitAsync(targetDir, "status", "--porcelain");
        porcelain.Should().BeEmpty();
        var (_, branches, _) = await RunGitAsync(targetDir, "branch", "--format=%(refname:short)");
        branches.Should().Be("main");
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_ExistingCheckoutMatchesRemoteSpelledDifferently_IsReused()
    {
        var remote = await CreateSeededBareRepoAsync("remote-match.git");
        var targetDir = Path.Combine(_tempRoot, "cloned-match");
        await RunGitAsync(_tempRoot, "clone", "--", remote, targetDir);

        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectId: "budget-app",
            WorkingDirectory: targetDir + "/",
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: remote + "/",
            GitBaseBranch: "main"));

        result.Success.Should().BeTrue(result.Message);
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_ExistingCheckoutWithNoConfiguredRemote_IsReused()
    {
        var remote = await CreateSeededBareRepoAsync("remote-nourl.git");
        var targetDir = Path.Combine(_tempRoot, "cloned-nourl");
        await RunGitAsync(_tempRoot, "clone", "--", remote, targetDir);

        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectId: "local-only",
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.Git));

        result.Success.Should().BeTrue(result.Message);
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_ExistingCheckoutWithNoOriginRemote_WarnsInsteadOfFailing()
    {
        // A locally initialized repository cannot be a checkout of some other project's remote, and
        // projects whose remote was configured after the fact must keep working. So: warn, do not fail.
        var targetDir = Path.Combine(_tempRoot, "local-only-noorigin");
        Directory.CreateDirectory(targetDir);
        await RunGitAsync(targetDir, "-c", "init.defaultBranch=main", "init");

        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectId: "local-only",
            WorkingDirectory: targetDir,
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: Path.Combine(_tempRoot, "not-configured-yet.git")));

        result.Success.Should().BeTrue(result.Message);
        result.Outcome.Should().Be(ProjectWorkspaceSetupOutcome.AlreadyInitialized);
        result.Warnings.Should().ContainSingle(w => w.Contains("origin"));
    }

    [Fact]
    public async Task EnsureWorkspaceAsync_MismatchFoundInMonorepoSubdirectoryCheckout_Fails()
    {
        var remoteA = await CreateSeededBareRepoAsync("monorepo-a.git");
        var monorepoDir = Path.Combine(_tempRoot, "monorepo");
        var serviceDir = Path.Combine(monorepoDir, "services", "api");
        await RunGitAsync(_tempRoot, "clone", "--", remoteA, monorepoDir);
        Directory.CreateDirectory(serviceDir);
        await File.WriteAllTextAsync(Path.Combine(serviceDir, "Program.cs"), "// api\n");
        await RunGitAsync(monorepoDir, "add", "-A");
        await RunGitAsync(monorepoDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "add api service");

        var setup = new ProjectWorkspaceSetup();
        var result = await setup.EnsureWorkspaceAsync(new ProjectWorkspaceSetupRequest(
            ProjectId: "other-project",
            WorkingDirectory: serviceDir,
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: await CreateSeededBareRepoAsync("monorepo-b.git")));

        result.Success.Should().BeFalse(result.Message);
        result.Message.Should().Contain("is a checkout of");
    }

    private async Task<string> CreateSeededBareRepoAsync(string name)
    {
        var bareRepo = Path.Combine(_tempRoot, name);
        var seedDir = Path.Combine(_tempRoot, $"{name}-seed");
        Directory.CreateDirectory(bareRepo);
        Directory.CreateDirectory(seedDir);

        await RunGitAsync(bareRepo, "-c", "init.defaultBranch=main", "init", "--bare");
        await RunGitAsync(seedDir, "-c", "init.defaultBranch=main", "init");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "seed.txt"), $"seed for {name}\n");
        await RunGitAsync(seedDir, "add", "-A");
        await RunGitAsync(seedDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "initial");
        await RunGitAsync(seedDir, "remote", "add", "origin", bareRepo);
        await RunGitAsync(seedDir, "push", "origin", "main");

        return bareRepo;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string workingDirectory,
        params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout.Trim(), stderr.Trim());
    }
}
