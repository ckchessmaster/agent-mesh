using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Sandbox;

/// <summary>
/// Coverage for the agent-facing <c>run_command</c> tool surface: the JSON schema a model sees, the
/// <c>timeout_seconds</c> argument, and the exact text of a spilling tool result.
/// </summary>
/// <remarks>
/// These assert the contract from the model's side. The engine tests prove the mechanism works; these prove
/// the mechanism is actually reachable through the tool call, which is the only way an agent can benefit
/// from it. Shares the spillover collection because it writes the same <c>last_run.log</c>.
/// </remarks>
[Collection(SandboxTestCollections.Spillover)]
public class RunCommandToolSurfaceTests
{
    private static string MakeTempDir(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agentmesh-tool-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void GetToolDefinitions_RunCommand_ExposesTimeoutSecondsParameter()
    {
        // If the parameter is not advertised in the schema, no model will ever pass it, and the feature is
        // dead on arrival regardless of how correct the engine is.
        var definitions = OpenAiAdapter.GetToolDefinitions();

        var runCommand = definitions
            .Select(d => JsonSerializer.SerializeToElement(d))
            .Single(e => e.GetProperty("function").GetProperty("name").GetString() == "run_command");

        var properties = runCommand.GetProperty("function").GetProperty("parameters").GetProperty("properties");

        properties.TryGetProperty("timeout_seconds", out var timeoutProp).Should().BeTrue(
            "the tool schema must advertise the override using the snake_case name the spec defines");

        timeoutProp.GetProperty("type").GetString().Should().Be("integer");

        // The description is the only guidance a model gets; it must state the range and the default.
        var description = timeoutProp.GetProperty("description").GetString();
        description.Should().NotBeNullOrWhiteSpace();
        description.Should().Contain("180");
        description.Should().Contain("600");

        // command stays the only required field: the timeout must remain optional.
        var required = runCommand.GetProperty("function").GetProperty("parameters").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Equal("command");
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_WhenOverThreshold_ReturnsPreviewBannerHintAndExitCode()
    {
        var dir = MakeTempDir("tool-spill");
        try
        {
            var args = JsonSerializer.Serialize(new { command = "for i in $(seq 1 150); do echo \"out $i\"; done" });

            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, dir, CancellationToken.None);

            // The three parts of the payload a model needs, in one string: the preview, the pointer, and
            // the exit status it already keys on.
            result.Should().Contain("out 1");
            result.Should().Contain("out 20");
            result.Should().Contain("out 121");
            result.Should().Contain("out 150");
            result.Should().NotContain("out 77");
            result.Should().Contain("[... Omitted 100 lines (");
            result.Should().Contain("Hint: The complete output was saved to ");
            result.Should().Contain("/tmp/agentmesh/runs/");
            result.Should().Contain("Exit Code: 0");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_HonoursTimeoutSecondsArgument()
    {
        var dir = MakeTempDir("tool-timeout");
        try
        {
            // 5s is the policy floor, so it survives the clamp and is honoured literally.
            var args = JsonSerializer.Serialize(new { command = "sleep 40", timeout_seconds = 5 });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, dir, CancellationToken.None);
            sw.Stop();

            result.Should().Contain("CommandTimedOut: Execution exceeded limit of 5s.");
            result.Should().Contain("Partial output captured before timeout:");
            result.Should().Contain("Exit Code: 124");

            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
                "the model asked for 5s and must not wait out the 40s sleep");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_WhenTimeoutSecondsAbsent_UsesPolicyDefault()
    {
        var dir = MakeTempDir("tool-default");
        try
        {
            // No timeout_seconds and no preset override => the 180s default. A short command must complete
            // normally: this is the Definition-of-Done regression check that the new default does not cut
            // ordinary work short.
            var args = JsonSerializer.Serialize(new { command = "echo 'no timeout supplied'" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, dir, CancellationToken.None);
            sw.Stop();

            result.Should().Contain("no timeout supplied");
            result.Should().Contain("Exit Code: 0");
            result.Should().NotContain("CommandTimedOut");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_WhenTimeoutSecondsZero_FallsBackToDefaultAndSucceeds()
    {
        var dir = MakeTempDir("tool-zero");
        try
        {
            // A model emitting timeout_seconds: 0 would, without the clamp, cancel the run before the
            // process started. The policy maps it to the default instead.
            var args = JsonSerializer.Serialize(new { command = "echo 'zero timeout'", timeout_seconds = 0 });

            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, dir, CancellationToken.None);

            result.Should().Contain("zero timeout");
            result.Should().Contain("Exit Code: 0");
            result.Should().NotContain("CommandTimedOut");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void ResolveCommandTimeoutSeconds_OnlyHonoursAnExplicitPresetValue()
    {
        // The HTTP handshake budget (ResolveTimeoutSeconds -> 600s fallback) must not become the command
        // budget, or the 180s sandbox default is unreachable in practice.
        OpenAiAdapter.ResolveCommandTimeoutSeconds(new AgentMesh.Cli.Models.AgentPresetConfig())
            .Should().BeNull("an unconfigured preset must defer to the sandbox default, not to 600");

        OpenAiAdapter.ResolveCommandTimeoutSeconds(new AgentMesh.Cli.Models.AgentPresetConfig { TimeoutSeconds = 0 })
            .Should().BeNull("a zero preset value is not a usable budget");

        OpenAiAdapter.ResolveCommandTimeoutSeconds(new AgentMesh.Cli.Models.AgentPresetConfig { TimeoutSeconds = 45 })
            .Should().Be(45, "an operator's explicit configuration is respected");

        // And the two helpers really are independent.
        OpenAiAdapter.ResolveTimeoutSeconds(new AgentMesh.Cli.Models.AgentPresetConfig())
            .Should().Be(600, "the endpoint handshake budget keeps its own, longer default");
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_WhenCommandFails_ReportsRealExitCodeAndErrorText()
    {
        var dir = MakeTempDir("tool-fail");
        try
        {
            var args = JsonSerializer.Serialize(new { command = "echo 'error CS0246: The type X is not found' >&2; exit 2" });

            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, dir, CancellationToken.None);

            result.Should().Contain("CS0246");
            result.Should().Contain("Exit Code: 2");
            result.Should().NotContain("CommandTimedOut");
        }
        finally
        {
            TryDelete(dir);
        }
    }
}
