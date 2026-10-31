using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentMesh.Tests.Sandbox;

/// <summary>
/// Where a sandboxed command runs when nobody says.
/// </summary>
/// <remarks>
/// <para>
/// The spec's requirement is that a command with no working directory defaults to the task's resolved
/// checkout (<c>/workspace/repos/{repo_name}</c>) rather than to a single hardcoded path. The
/// observable is <c>pwd</c>'s output, so these tests run real commands in real temporary directories —
/// a stubbed shell would only prove the code passed a string somewhere.
/// </para>
/// <para>
/// <see cref="WorkspacePathResolver"/> itself is covered by
/// <see cref="WorkspacePathResolverTests"/>; this class covers the activity's precedence chain, which
/// is where the interesting failure modes live (a configured path that no longer exists, a caller that
/// names a directory on purpose).
/// </para>
/// </remarks>
public class ShellDefaultWorkingDirectoryTests
{
    /// <summary>
    /// The spec's named test: a command with no override runs in the resolved project path.
    /// </summary>
    [Fact]
    public async Task ShellEngine_DefaultsToResolvedProjectPath()
    {
        var repoDir = Directory.CreateTempSubdirectory("agentmesh-repo-").FullName;
        try
        {
            var activity = new ExecutionActivities(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionActivities>.Instance);

            var result = await activity.ExecuteCommand(
                new RunCommandArgs("pwd"),
                taskWorkspacePath: repoDir);

            result.Success.Should().BeTrue(result.Output);

            // Resolved through the OS rather than string-compared: macOS and some CI images expose
            // /tmp through a symlink, so an exact match against the created path would fail on a host
            // where the code is correct.
            Realpath(result.Output).Should().Be(Realpath(repoDir),
                "a command that names no directory must run in the task's checkout");
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    /// <summary>
    /// The resolved path is the one the project row produces, end to end.
    /// </summary>
    /// <remarks>
    /// The chain the spec asks for is <c>projects.repository_name</c> →
    /// <c>GetResolvedWorkingDirectory()</c> → <c>TaskExecutionInput.WorkspacePath</c> → this default.
    /// Only the last link is exercised by the activity, so this pins the first two against the third by
    /// resolving a real project and asserting the shape the activity then receives.
    /// </remarks>
    [Fact]
    public void ProjectPathResolution_ProducesTheMultiRepoLayout()
    {
        new Project { Id = "agent-mesh", RepositoryName = "agent-mesh", WorkingDirectory = "" }
            .GetResolvedWorkingDirectory().Should().Be("/workspace/repos/agent-mesh");

        new Project { Id = "payments", RepositoryName = "payments-api", WorkingDirectory = "" }
            .GetResolvedWorkingDirectory().Should().Be("/workspace/repos/payments-api");

        // An explicit override survives, which is what monorepos need.
        new Project { Id = "mono", RepositoryName = "monorepo", WorkingDirectory = "/workspace/repos/monorepo/services/api" }
            .GetResolvedWorkingDirectory().Should().Be("/workspace/repos/monorepo/services/api");
    }

    /// <summary>
    /// A command that names a directory on purpose is not overridden by the task path.
    /// </summary>
    /// <remarks>
    /// The precedence matters: a build script that must run in a sibling checkout, or the reconciler
    /// probing a path it was handed, has to be able to say so. Silently preferring the task path would
    /// make that unrepresentable.
    /// </remarks>
    [Fact]
    public async Task ExplicitWorkingDirectory_WinsOverTheTaskWorkspace()
    {
        var taskDir = Directory.CreateTempSubdirectory("agentmesh-task-").FullName;
        var explicitDir = Directory.CreateTempSubdirectory("agentmesh-explicit-").FullName;
        try
        {
            var activity = new ExecutionActivities(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionActivities>.Instance);

            var result = await activity.ExecuteCommand(
                new RunCommandArgs("pwd", explicitDir),
                taskWorkspacePath: taskDir);

            Realpath(result.Output).Should().Be(Realpath(explicitDir));
            Realpath(result.Output).Should().NotBe(Realpath(taskDir));
        }
        finally
        {
            Directory.Delete(taskDir, recursive: true);
            Directory.Delete(explicitDir, recursive: true);
        }
    }

    /// <summary>
    /// A task workspace that does not exist falls through instead of throwing.
    /// </summary>
    /// <remarks>
    /// <c>ProcessStartInfo.WorkingDirectory</c> throws before the command can fail on its own merits, so
    /// a stale <c>projects.working_directory</c> would surface as an opaque activity exception rather
    /// than as a command that ran and reported what it found. Falling through is the diagnosable
    /// behaviour, and the activity logs the miss.
    /// </remarks>
    [Fact]
    public async Task MissingTaskWorkspace_FallsBackRatherThanThrowing()
    {
        var activity = new ExecutionActivities(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionActivities>.Instance);

        var result = await activity.ExecuteCommand(
            new RunCommandArgs("pwd"),
            taskWorkspacePath: "/workspace/repos/this-repo-was-deleted-yesterday");

        result.Success.Should().BeTrue("the command must still run somewhere");
        result.Output.Should().NotContain("this-repo-was-deleted-yesterday");
    }

    /// <summary>
    /// A configured workspace root is preferred over the generic fallback.
    /// </summary>
    [Fact]
    public async Task ConfiguredWorkspaceRoot_IsUsedWhenNoTaskPathIsGiven()
    {
        var configured = Directory.CreateTempSubdirectory("agentmesh-configured-").FullName;
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sandbox:WorkspaceRoot"] = configured,
                })
                .Build();

            var activity = new ExecutionActivities(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionActivities>.Instance,
                config: config);

            var result = await activity.ExecuteCommand(new RunCommandArgs("pwd"));

            Realpath(result.Output).Should().Be(Realpath(configured));
        }
        finally
        {
            Directory.Delete(configured, recursive: true);
        }
    }

    /// <summary>
    /// A configured root that does not exist is not used.
    /// </summary>
    [Fact]
    public async Task ConfiguredWorkspaceRoot_WhenMissing_IsIgnored()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sandbox:WorkspaceRoot"] = "/nope/gone",
            })
            .Build();

        var activity = new ExecutionActivities(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionActivities>.Instance,
            config: config);

        var result = await activity.ExecuteCommand(new RunCommandArgs("pwd"));

        result.Success.Should().BeTrue();
        result.Output.Should().NotContain("/nope/gone");
    }

    /// <summary>
    /// The task path is normalized before use.
    /// </summary>
    /// <remarks>
    /// A trailing slash is the common authoring mistake in a config value, and it becomes <c>//</c> when
    /// a path is concatenated onto the result. Normalizing here means the activity's directory and the
    /// assembler's directory compare equal rather than differing by one character.
    /// </remarks>
    [Fact]
    public async Task TaskWorkspacePath_IsNormalized()
    {
        var dir = Directory.CreateTempSubdirectory("agentmesh-norm-").FullName;
        try
        {
            var activity = new ExecutionActivities(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionActivities>.Instance);

            var result = await activity.ExecuteCommand(
                new RunCommandArgs("pwd"),
                taskWorkspacePath: dir + "/   ");

            Realpath(result.Output).Should().Be(Realpath(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Realpath(string? output)
    {
        var trimmed = (output ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;

        // The last line, in case the command emitted anything before it.
        var path = trimmed.Split('\n')[^1].Trim();
        return Path.GetFullPath(path);
    }
}

/// <summary>
/// The single definition of "where does a project's repository live".
/// </summary>
public class WorkspacePathResolverTests
{
    [Theory]
    [InlineData("agent-mesh", "/workspace/repos/agent-mesh")]
    [InlineData("payments-api", "/workspace/repos/payments-api")]
    [InlineData("  spaced  ", "/workspace/repos/spaced")]
    [InlineData("", "/workspace/repos")]
    [InlineData(null, "/workspace/repos")]
    public void FromRepositoryName_AppliesTheConvention(string? name, string expected) =>
        WorkspacePathResolver.FromRepositoryName(name).Should().Be(expected);

    [Theory]
    // Blank override derives from the name.
    [InlineData("payments", "", "/workspace/repos/payments")]
    [InlineData("payments", null, "/workspace/repos/payments")]
    [InlineData("payments", "   ", "/workspace/repos/payments")]
    // A real override wins.
    [InlineData("payments", "/srv/work/payments", "/srv/work/payments")]
    // Trailing separators are stripped so an override compares equal to a derived path.
    [InlineData("payments", "/workspace/repos/payments/", "/workspace/repos/payments")]
    [InlineData("payments", "/workspace/repos/payments///", "/workspace/repos/payments")]
    // A lone root is preserved rather than collapsed to "", which would become a relative path.
    [InlineData("x", "/", "/")]
    public void Resolve_HonoursOverrideAndConvention(
        string repo, string? workingDir, string expected) =>
        WorkspacePathResolver.Resolve(repo, workingDir).Should().Be(expected);

    [Fact]
    public void Resolve_KeepsAWindowsStylePathIntact()
    {
        // Backslash normalization applies only to absolute POSIX paths; converting C:\repos\api to
        // C:/repos/api would be a change of meaning this function cannot verify.
        WorkspacePathResolver.Resolve("api", @"C:\repos\api").Should().Be(@"C:\repos\api");
    }

    [Theory]
    [InlineData("agent-mesh", true)]
    [InlineData("agent.mesh", true)]
    [InlineData("agent_mesh", true)]
    [InlineData("repo123", true)]
    // Traversal: the name is embedded in a path, so this is the injection case.
    [InlineData("..", false)]
    [InlineData("../evil", false)]
    [InlineData("../../etc", false)]
    [InlineData("a/../b", false)]
    [InlineData(".", false)]
    [InlineData("...", false)]
    // Separators would escape the repos root.
    [InlineData("a/b", false)]
    [InlineData("/etc", false)]
    [InlineData("a\\b", false)]
    // Shell metacharacters and whitespace.
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    [InlineData("a|b", false)]
    [InlineData("a$(id)", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("  padded  ", false)]
    public void IsSafeRepositoryName_RejectsAnythingThatEscapesThePath(string? name, bool expected) =>
        WorkspacePathResolver.IsSafeRepositoryName(name).Should().Be(expected);

    [Fact]
    public void IsSafeRepositoryName_EnforcesTheColumnWidth()
    {
        WorkspacePathResolver.IsSafeRepositoryName(new string('a', WorkspacePathResolver.MaxRepositoryNameLength))
            .Should().BeTrue();
        WorkspacePathResolver.IsSafeRepositoryName(new string('a', WorkspacePathResolver.MaxRepositoryNameLength + 1))
            .Should().BeFalse("the column is varchar(128) and a longer name would be a database error");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("/workspace/repos/api", true)]
    [InlineData("/workspace/repos/monorepo/services/api", true)]
    [InlineData(@"C:\repos\api", true)]
    [InlineData("relative/path", false)]
    [InlineData("has space", false)]
    [InlineData("has\nnewline", false)]
    [InlineData("  /leading-space", false)]
    [InlineData("/trailing-space  ", false)]
    public void IsSafeWorkingDirectory_RequiresAnAbsolutePath(string? value, bool expected) =>
        WorkspacePathResolver.IsSafeWorkingDirectory(value).Should().Be(expected);

    [Fact]
    public void IsSafeWorkingDirectory_EnforcesTheColumnWidth()
    {
        var atLimit = "/" + new string('a', WorkspacePathResolver.MaxWorkingDirectoryLength - 1);
        WorkspacePathResolver.IsSafeWorkingDirectory(atLimit).Should().BeTrue();
        WorkspacePathResolver.IsSafeWorkingDirectory(atLimit + "a").Should().BeFalse();
    }

    /// <summary>
    /// A hostile repository name cannot escape the repos root.
    /// </summary>
    /// <remarks>
    /// The name arrives from an API request body. Without the guard, <c>repository_name =
    /// "../../etc"</c> resolves to <c>/workspace/repos/../../etc</c> and every agent command runs as
    /// root of an arbitrary directory — with no error, because that directory exists and is writable.
    /// </remarks>
    [Fact]
    public void TraversalStyleRepositoryName_IsRejectedAtTheWriteBoundary()
    {
        var hostile = "../../etc";

        WorkspacePathResolver.IsSafeRepositoryName(hostile).Should().BeFalse();

        // And if it somehow reached Resolve anyway (a row written before the guard existed), the
        // resolution is still visible as a traversal rather than silently normalized.
        WorkspacePathResolver.Resolve(hostile, null).Should().Be("/workspace/repos/../../etc");
    }
}
