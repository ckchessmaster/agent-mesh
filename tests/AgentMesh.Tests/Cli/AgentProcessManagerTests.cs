using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Cli;

public class AgentProcessManagerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _configPath;

    public AgentProcessManagerTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("agentmesh-apm-tests-").FullName;
        _configPath = Path.Combine(_tempRoot, "cli-config.json");
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
    public async Task PrepareTurnWorkspaceAsync_LocalGitProject_InitializesStandardStructureAndIsIdempotent()
    {
        var projectDir = Path.Combine(_tempRoot, "repos", "payments-api");
        var fallbackDir = Path.Combine(_tempRoot, "fallback");
        var logs = new List<string>();
        var runner = CreateRunner(
            logs: logs,
            fallbackDirectoryOverride: fallbackDir);

        var ev = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "generic",
            ProjectId = "payments",
            ProjectName = "Payments API",
            ProjectDescription = "Handles settlement.",
            RepositoryName = "payments-api",
            WorkingDirectory = projectDir + "/",
            WorkspaceType = nameof(WorkspaceType.Git),
            GitBaseBranch = "main"
        };

        var first = await runner.PrepareTurnWorkspaceAsync(ev, CancellationToken.None);

        var expectedDir = WorkspacePathResolver.Normalize(projectDir);
        first.UsedFallback.Should().BeFalse();
        first.WorkingDirectory.Should().Be(expectedDir);
        Directory.Exists(Path.Combine(expectedDir, ".git")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedDir, "src")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedDir, "tests")).Should().BeTrue();
        Directory.Exists(Path.Combine(expectedDir, "docs")).Should().BeTrue();
        File.Exists(Path.Combine(expectedDir, ".gitignore")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(expectedDir, "README.md"))).Should().Contain("# Payments API");

        var (_, firstHead, _) = await RunGitAsync(expectedDir, "rev-parse", "HEAD");
        firstHead.Should().NotBeNullOrWhiteSpace();

        // Customize README and run setup a second time to verify idempotency.
        await File.WriteAllTextAsync(Path.Combine(expectedDir, "README.md"), "# Custom Payments\n");
        var second = await runner.PrepareTurnWorkspaceAsync(ev, CancellationToken.None);

        second.UsedFallback.Should().BeFalse();
        second.WorkingDirectory.Should().Be(expectedDir);
        (await File.ReadAllTextAsync(Path.Combine(expectedDir, "README.md"))).Should().Be("# Custom Payments\n");
        var (_, secondHead, _) = await RunGitAsync(expectedDir, "rev-parse", "HEAD");
        secondHead.Should().Be(firstHead);
    }

    [Fact]
    public async Task PrepareTurnWorkspaceAsync_RemoteGitProject_ClonesAndIsIdempotent()
    {
        var bareRepo = Path.Combine(_tempRoot, "remote.git");
        var seedDir = Path.Combine(_tempRoot, "seed");
        var targetDir = Path.Combine(_tempRoot, "cloned-project");
        Directory.CreateDirectory(bareRepo);
        Directory.CreateDirectory(seedDir);

        (await RunGitAsync(bareRepo, "init", "--bare")).ExitCode.Should().Be(0);
        (await RunGitAsync(seedDir, "init", "-b", "main")).ExitCode.Should().Be(0);
        await File.WriteAllTextAsync(Path.Combine(seedDir, "README.md"), "# Remote Project\n");
        (await RunGitAsync(seedDir, "add", "-A")).ExitCode.Should().Be(0);
        (await RunGitAsync(seedDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "initial")).ExitCode.Should().Be(0);
        (await RunGitAsync(seedDir, "remote", "add", "origin", bareRepo)).ExitCode.Should().Be(0);
        (await RunGitAsync(seedDir, "push", "-u", "origin", "main")).ExitCode.Should().Be(0);

        var runner = CreateRunner(fallbackDirectoryOverride: Path.Combine(_tempRoot, "fallback"));
        var ev = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "generic",
            ProjectId = "remote-proj",
            RepositoryName = "remote-proj",
            WorkingDirectory = targetDir,
            WorkspaceType = nameof(WorkspaceType.Git),
            GitUrl = bareRepo,
            GitBaseBranch = "main"
        };

        var first = await runner.PrepareTurnWorkspaceAsync(ev, CancellationToken.None);
        first.UsedFallback.Should().BeFalse();
        first.WorkingDirectory.Should().Be(WorkspacePathResolver.Normalize(targetDir));
        File.Exists(Path.Combine(targetDir, "README.md")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(targetDir, "README.md"))).Should().Be("# Remote Project\n");

        var second = await runner.PrepareTurnWorkspaceAsync(ev, CancellationToken.None);
        second.UsedFallback.Should().BeFalse();
        second.WorkingDirectory.Should().Be(WorkspacePathResolver.Normalize(targetDir));
    }

    [Fact]
    public async Task PrepareTurnWorkspaceAsync_MissingProjectFields_BackfillsFromMeshClientBeforeSetup()
    {
        var resolvedDir = Path.Combine(_tempRoot, "repos", "billing-core");
        var projectRequests = 0;

        var runner = CreateRunner(
            httpHandler: req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/api/projects/billing", StringComparison.Ordinal))
                {
                    projectRequests++;
                    return JsonResponse(new
                    {
                        id = "billing",
                        name = "Billing Core",
                        description = "Invoices and ledger.",
                        workspaceType = "Git",
                        gitBaseBranch = "main",
                        repositoryName = "billing-core",
                        workingDirectory = "",
                        resolvedWorkingDirectory = resolvedDir
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
            fallbackDirectoryOverride: Path.Combine(_tempRoot, "fallback"));

        var ev = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "generic",
            ProjectId = "billing"
        };

        var resolution = await runner.PrepareTurnWorkspaceAsync(ev, CancellationToken.None);

        projectRequests.Should().Be(1);
        resolution.UsedFallback.Should().BeFalse();
        resolution.WorkingDirectory.Should().Be(WorkspacePathResolver.Normalize(resolvedDir));
        Directory.Exists(Path.Combine(resolvedDir, ".git")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(resolvedDir, "README.md"))).Should().Contain("# Billing Core");
    }

    [Fact]
    public async Task PrepareTurnWorkspaceAsync_WhenTargetDirectoryFailsSetup_FallsBackToExistingDirectoryAndLogsWarning()
    {
        var blockingFile = Path.Combine(_tempRoot, "blocking-file");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        var fallbackDir = Path.Combine(_tempRoot, "fallback-workspace");
        var logs = new List<string>();

        var runner = CreateRunner(
            logs: logs,
            fallbackDirectoryOverride: fallbackDir);

        var ev = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "generic",
            ProjectId = "broken-proj",
            RepositoryName = "broken-proj",
            WorkingDirectory = blockingFile,
            WorkspaceType = nameof(WorkspaceType.Git)
        };

        var resolution = await runner.PrepareTurnWorkspaceAsync(ev, CancellationToken.None);

        resolution.UsedFallback.Should().BeTrue();
        resolution.WorkingDirectory.Should().Be(WorkspacePathResolver.Normalize(fallbackDir));
        Directory.Exists(resolution.WorkingDirectory).Should().BeTrue();
        logs.Should().Contain(m => m.Contains("falling back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PrepareTurnWorkspaceAsync_WhenPrimaryFallbackAlsoFails_FallsBackToGuaranteedDirectory()
    {
        var blockingTarget = Path.Combine(_tempRoot, "blocking-target");
        var blockingFallback = Path.Combine(_tempRoot, "blocking-fallback");
        await File.WriteAllTextAsync(blockingTarget, "file");
        await File.WriteAllTextAsync(blockingFallback, "file");
        var logs = new List<string>();

        var runner = CreateRunner(
            logs: logs,
            fallbackDirectoryOverride: blockingFallback);

        var ev = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "generic",
            ProjectId = "double-fail",
            RepositoryName = "double-fail",
            WorkingDirectory = blockingTarget,
            WorkspaceType = nameof(WorkspaceType.Git)
        };

        var resolution = await runner.PrepareTurnWorkspaceAsync(ev, CancellationToken.None);

        resolution.UsedFallback.Should().BeTrue();
        Directory.Exists(resolution.WorkingDirectory).Should().BeTrue();
        resolution.WorkingDirectory.Should().NotBe(blockingTarget);
        resolution.WorkingDirectory.Should().NotBe(blockingFallback);
        logs.Should().Contain(m => m.Contains("falling back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PrepareTurnWorkspaceAsync_NonProjectSession_DoesNotInitializeProjectStructure_AndFallsBackWhenInvalid()
    {
        var adHocDir = Path.Combine(_tempRoot, "adhoc-session");
        var fallbackDir = Path.Combine(_tempRoot, "fallback-adhoc");
        var blockingFile = Path.Combine(_tempRoot, "not-a-dir");
        await File.WriteAllTextAsync(blockingFile, "file");

        var logs = new List<string>();
        var runner = CreateRunner(
            logs: logs,
            fallbackDirectoryOverride: fallbackDir);

        // Valid non-project directory should be created without initializing git/src/tests/docs.
        var validEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "generic",
            WorkingDirectory = adHocDir
        };

        var validRes = await runner.PrepareTurnWorkspaceAsync(validEv, CancellationToken.None);
        validRes.UsedFallback.Should().BeFalse();
        validRes.WorkingDirectory.Should().Be(WorkspacePathResolver.Normalize(adHocDir));
        Directory.Exists(adHocDir).Should().BeTrue();
        Directory.Exists(Path.Combine(adHocDir, ".git")).Should().BeFalse();
        Directory.Exists(Path.Combine(adHocDir, "src")).Should().BeFalse();

        // Uncreatable non-project directory should fall back cleanly.
        var brokenEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "generic",
            WorkingDirectory = blockingFile
        };

        var brokenRes = await runner.PrepareTurnWorkspaceAsync(brokenEv, CancellationToken.None);
        brokenRes.UsedFallback.Should().BeTrue();
        brokenRes.WorkingDirectory.Should().Be(WorkspacePathResolver.Normalize(fallbackDir));
        Directory.Exists(brokenRes.WorkingDirectory).Should().BeTrue();
        logs.Should().Contain(m => m.Contains("falling back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandlePromptAsync_ProjectTurn_InitializesRepoPreparesBranchExecutesAndCommits()
    {
        var projectDir = Path.Combine(_tempRoot, "repos", "orders-api");
        var fallbackDir = Path.Combine(_tempRoot, "fallback");
        var postedEvents = new List<string>();
        var sessionId = Guid.NewGuid();

        var isWindows = OperatingSystem.IsWindows();
        var preset = new AgentPresetConfig
        {
            Id = "test-bot",
            Name = "Test Bot",
            AdapterType = "generic",
            Command = isWindows
                ? "cd > turn-output.txt && cd"
                : "pwd > turn-output.txt && pwd"
        };

        var runner = CreateRunner(
            preset: preset,
            httpHandler: req =>
            {
                if (req.Method == HttpMethod.Post &&
                    req.RequestUri!.AbsolutePath.EndsWith($"/api/sessions/{sessionId}/events", StringComparison.Ordinal))
                {
                    var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                    postedEvents.Add(body);
                    return JsonResponse(new { ok = true });
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            fallbackDirectoryOverride: fallbackDir);

        var ev = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "test-bot",
            PersonaId = "astra",
            PersonaName = "Astra",
            ProjectId = "orders",
            ProjectName = "Orders API",
            RepositoryName = "orders-api",
            WorkingDirectory = projectDir,
            WorkspaceType = nameof(WorkspaceType.Git),
            GitBaseBranch = "main",
            GitBranch = "mesh/job-orders-1",
            Prompt = "Record workspace path"
        };

        await runner.HandlePromptAsync(ev, CancellationToken.None);

        var normalizedProjectDir = WorkspacePathResolver.Normalize(projectDir);
        ev.WorkingDirectory.Should().Be(normalizedProjectDir);
        Directory.Exists(Path.Combine(normalizedProjectDir, ".git")).Should().BeTrue();
        Directory.Exists(Path.Combine(normalizedProjectDir, "src")).Should().BeTrue();
        File.Exists(Path.Combine(normalizedProjectDir, "turn-output.txt")).Should().BeTrue();

        var recordedCwd = (await File.ReadAllTextAsync(Path.Combine(normalizedProjectDir, "turn-output.txt"))).Trim();
        Path.GetFullPath(recordedCwd).Should().Be(Path.GetFullPath(normalizedProjectDir));

        var (_, currentBranch, _) = await RunGitAsync(normalizedProjectDir, "branch", "--show-current");
        currentBranch.Should().Be("mesh/job-orders-1");

        var (_, statusOut, _) = await RunGitAsync(normalizedProjectDir, "status", "--porcelain");
        statusOut.Should().BeEmpty("turn changes should have been committed onto the job branch");

        var (_, logOut, _) = await RunGitAsync(normalizedProjectDir, "log", "-1", "--pretty=%s");
        logOut.Should().Contain("feat(job): turn by @astra");

        postedEvents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandlePromptAsync_WhenWorkspaceFallsBack_SkipsGitBranchMutationOnFallbackAndStillExecutes()
    {
        var blockingTarget = Path.Combine(_tempRoot, "blocking-project-target");
        await File.WriteAllTextAsync(blockingTarget, "file");

        // Initialize fallbackDir as an independent git repo on 'main' (mirroring /workspace in a container).
        var fallbackDir = Path.Combine(_tempRoot, "existing-workspace-repo");
        Directory.CreateDirectory(fallbackDir);
        (await RunGitAsync(fallbackDir, "init", "-b", "main")).ExitCode.Should().Be(0);
        await File.WriteAllTextAsync(Path.Combine(fallbackDir, "host.txt"), "host repo\n");
        (await RunGitAsync(fallbackDir, "add", "-A")).ExitCode.Should().Be(0);
        (await RunGitAsync(fallbackDir, "-c", "user.name=Test", "-c", "user.email=test@local", "commit", "-m", "host init")).ExitCode.Should().Be(0);

        var sessionId = Guid.NewGuid();
        var postedEvents = new List<string>();
        var preset = new AgentPresetConfig
        {
            Id = "test-bot",
            Name = "Test Bot",
            AdapterType = "generic",
            Command = OperatingSystem.IsWindows() ? "cd" : "pwd"
        };

        var runner = CreateRunner(
            preset: preset,
            httpHandler: req =>
            {
                if (req.Method == HttpMethod.Post &&
                    req.RequestUri!.AbsolutePath.EndsWith($"/api/sessions/{sessionId}/events", StringComparison.Ordinal))
                {
                    var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                    postedEvents.Add(body);
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            fallbackDirectoryOverride: fallbackDir);

        var ev = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "test-bot",
            ProjectId = "broken",
            RepositoryName = "broken",
            WorkingDirectory = blockingTarget,
            WorkspaceType = nameof(WorkspaceType.Git),
            GitBranch = "mesh/job-should-not-checkout",
            Prompt = "Check cwd"
        };

        await runner.HandlePromptAsync(ev, CancellationToken.None);

        ev.WorkingDirectory.Should().Be(WorkspacePathResolver.Normalize(fallbackDir));
        var (_, currentBranch, _) = await RunGitAsync(fallbackDir, "branch", "--show-current");
        currentBranch.Should().Be("main", "fallback execution must not switch branches in an unrelated repository");
        postedEvents.Should().Contain(b => b.Contains(Path.GetFileName(fallbackDir), StringComparison.Ordinal));
    }

    private AgentRunnerInstance CreateRunner(
        AgentPresetConfig? preset = null,
        Func<HttpRequestMessage, HttpResponseMessage>? httpHandler = null,
        List<string>? logs = null,
        string? fallbackDirectoryOverride = null)
    {
        var store = new ConfigStore(_configPath);
        var config = new CliConfig
        {
            MeshUrl = "http://localhost:5000",
            MasterToken = "test-master-token"
        };
        var httpClient = new HttpClient(new StubHandler(httpHandler ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound))))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        var meshClient = new MeshClient(config, store, httpClient);
        var effectivePreset = preset ?? new AgentPresetConfig
        {
            Id = "generic",
            Name = "Generic",
            AdapterType = "generic"
        };

        return new AgentRunnerInstance(
            effectivePreset,
            meshClient,
            config.MeshUrl,
            (_, msg) => logs?.Add(msg),
            () => { },
            fallbackDirectoryOverride: fallbackDirectoryOverride);
    }

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string workingDir,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
