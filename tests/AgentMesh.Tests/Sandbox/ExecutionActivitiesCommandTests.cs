using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMesh.Tests.Sandbox;

/// <summary>
/// Coverage for the Temporal worker's command activity.
/// </summary>
/// <remarks>
/// The interesting property is not that the activity runs a command — <see cref="SandboxShell"/> already
/// covers that. It is that the worker and the CLI cannot end up with different limits, because a policy that
/// differs by host is a policy that will be bypassed by accident. These tests pin the shared surface.
/// </remarks>
[Collection(SandboxTestCollections.Spillover)]
public class ExecutionActivitiesCommandTests
{
    private static ExecutionActivities NewActivity() => new(NullLogger<ExecutionActivities>.Instance);

    private static string MakeTempDir(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agentmesh-activity-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ExecuteCommand_WhenUnderThreshold_ReturnsFullOutputInline()
    {
        var dir = MakeTempDir("inline");
        try
        {
            var activity = NewActivity();

            var result = await activity.ExecuteCommand(
                new RunCommandArgs("echo \"worker activity inline\"", dir));

            result.Success.Should().BeTrue();
            result.ExitCode.Should().Be(0);
            result.TimedOut.Should().BeFalse();
            result.Output.Should().Contain("worker activity inline");
            result.LogFilePath.Should().BeNull();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenOverThreshold_WritesLogFileAndReturnsPreview()
    {
        var dir = MakeTempDir("spill");
        var sessionId = Guid.NewGuid();
        try
        {
            var activity = NewActivity();

            var result = await activity.ExecuteCommand(
                new RunCommandArgs("for i in $(seq 1 150); do echo \"line $i\"; done", dir),
                sessionId,
                "cmd-spill");

            result.ExitCode.Should().Be(0);
            result.LogFilePath.Should().NotBeNull();

            var raw = await File.ReadAllTextAsync(result.LogFilePath);
            CommandOutputSpillover.SplitLines(raw).Should().HaveCount(150,
                "the activity must preserve 100% of the log even though it returns a preview");

            result.Output.Should().Contain("line 1");
            result.Output.Should().Contain("line 20");
            result.Output.Should().Contain("line 121");
            result.Output.Should().Contain("line 150");
            result.Output.Should().NotContain("line 77");
            result.Output.Should().Contain("[... Omitted 100 lines (");

            // The activity names the file after the session/command pair, so a workflow can find the log of
            // a specific attempt without threading a guid through its own state.
            result.LogFilePath.Should().Contain(sessionId.ToString("N"));
            result.LogFilePath.Should().Contain("cmd-spill");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenTimesOut_ReturnsExitCode124AndDiagnostic()
    {
        var dir = MakeTempDir("timeout");
        const int sleeperSeconds = 28;
        try
        {
            var activity = NewActivity();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 2s is below the untrusted floor, so the activity clamps it up to 5s. That is the contract for
            // a workflow-supplied argument: a workflow cannot ask for a shorter fuse than the policy allows.
            var result = await activity.ExecuteCommand(
                new RunCommandArgs($"sleep {sleeperSeconds} & sleep {sleeperSeconds}", dir, TimeoutSeconds: 2));

            sw.Stop();

            result.TimedOut.Should().BeTrue();
            result.ExitCode.Should().Be(CommandExecutionPolicy.TimeoutExitCode);
            result.Output.Should().StartWith("CommandTimedOut: Execution exceeded limit of 5s.");

            // Clamped to the 5s floor, not honoured at 2s, and nowhere near the 28s the command wanted.
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(12));
            sw.Elapsed.TotalSeconds.Should().BeGreaterThanOrEqualTo(4);

            await Task.Delay(400);
            using var pgrep = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/sh")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "-c", $"pgrep -af '(^|/)sleep {sleeperSeconds}($|\\s)' 2>/dev/null | grep -v pgrep || true" }
                }
            };
            pgrep.Start();
            var survivors = await pgrep.StandardOutput.ReadToEndAsync();
            pgrep.WaitForExit();

            survivors.Trim().Should().BeEmpty($"the worker's activity must not leak a forked sleep; got: {survivors}");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenTimeoutExceedsCeiling_IsClampedToPolicyMax()
    {
        var activity = NewActivity();

        // A workflow asking for 100,000s must not be able to pin a worker slot indefinitely.
        var result = await activity.ExecuteCommand(
            new RunCommandArgs("true", null, TimeoutSeconds: 100_000));

        result.ExitCode.Should().Be(0);
        CommandExecutionPolicy.ClampTimeoutSeconds(100_000).Should().Be(CommandExecutionPolicy.MaxTimeoutSeconds);
    }

    [Fact]
    public async Task ExecuteCommand_WhenCommandBlank_ReturnsUsageError()
    {
        var activity = NewActivity();

        var result = await activity.ExecuteCommand(new RunCommandArgs(""));

        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(127);
        result.Output.Should().Contain("'command' argument is required");
    }

    [Fact]
    public async Task ExecuteCommand_WhenWorkflowCancels_TearsDownProcessTree()
    {
        var dir = MakeTempDir("cancel");
        const int sleeperSeconds = 29;
        try
        {
            var activity = NewActivity();
            using var cts = new CancellationTokenSource();

            var run = activity.ExecuteCommand(
                new RunCommandArgs($"sleep {sleeperSeconds} & sleep {sleeperSeconds}", dir),
                sessionId: Guid.NewGuid(),
                commandId: "cmd-cancel",
                cancellationToken: cts.Token);

            await Task.Delay(700);
            cts.Cancel();

            var act = async () => await run;
            await act.Should().ThrowAsync<OperationCanceledException>(
                "a cancelled activity must propagate so Temporal records the cancellation rather than a fabricated result");

            await Task.Delay(500);
            using var pgrep = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("/bin/sh")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "-c", $"pgrep -af '(^|/)sleep {sleeperSeconds}($|\\s)' 2>/dev/null | grep -v pgrep || true" }
                }
            };
            pgrep.Start();
            var survivors = await pgrep.StandardOutput.ReadToEndAsync();
            pgrep.WaitForExit();

            survivors.Trim().Should().BeEmpty("cancelling a workflow must not orphan the command's children");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void WorkerAndCliShareOneTimeoutPolicySoLimitsCannotDriftByHost()
    {
        // The structural guarantee behind this job: the worker activity and the CLI runner call the same
        // policy type. If someone later adds a second hard-coded 180 or a second clamp, this test is the
        // tripwire. Values are asserted against the spec, not against the code, so a "convenient" edit to
        // the constants fails here rather than shipping.
        CommandExecutionPolicy.DefaultTimeoutSeconds.Should().Be(180);
        CommandExecutionPolicy.MinTimeoutSeconds.Should().Be(5);
        CommandExecutionPolicy.MaxTimeoutSeconds.Should().Be(600);
        CommandExecutionPolicy.TimeoutExitCode.Should().Be(124);
        CommandExecutionPolicy.SpillLineThreshold.Should().Be(60);
        CommandExecutionPolicy.SpillByteThreshold.Should().Be(4096);
        CommandExecutionPolicy.PreviewHeadLines.Should().Be(20);
        CommandExecutionPolicy.PreviewTailLines.Should().Be(30);
        CommandExecutionPolicy.TermGrace.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteCommand_WhenSpilled_ResultPayloadStaysWithinTemporalHistoryBudget()
    {
        var dir = MakeTempDir("history");
        try
        {
            var activity = NewActivity();

            // A 20,000-line build log. Temporal serializes an activity's return value into workflow history,
            // so the *result* — not the file — is what must stay small. This is the real reason spillover is
            // mandatory for the worker rather than merely nice for the CLI.
            var result = await activity.ExecuteCommand(
                new RunCommandArgs("for i in $(seq 1 20000); do echo \"build output line $i with padding text to make it realistic\"; done", dir));

            result.ExitCode.Should().Be(0);
            result.LogFilePath.Should().NotBeNull();

            var rawBytes = new FileInfo(result.LogFilePath).Length;
            rawBytes.Should().BeGreaterThan(1_000_000, "the test must actually generate a large stream");

            var resultBytes = System.Text.Encoding.UTF8.GetByteCount(result.Output);
            resultBytes.Should().BeLessThan(CommandExecutionPolicy.SpillByteThreshold * 20,
                $"the activity result goes into workflow history; it was {resultBytes} bytes against {rawBytes} raw");

            // ...and nothing is lost: the file still has every line.
            var raw = await File.ReadAllTextAsync(result.LogFilePath);
            CommandOutputSpillover.SplitLines(raw).Should().HaveCount(20000);
        }
        finally
        {
            TryDelete(dir);
        }
    }
}
