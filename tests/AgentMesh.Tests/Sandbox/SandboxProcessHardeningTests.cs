using System.Diagnostics;
using System.Text;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Sandbox;

/// <summary>
/// Integration coverage for the sandbox hardening work: wall-clock timeouts, process-tree teardown, and
/// non-destructive log spillover.
/// </summary>
/// <remarks>
/// These tests spawn real processes, so they are deliberately the only class in the assembly that writes to
/// the shared <c>/tmp/agentmesh/runs</c> directory. xUnit parallelises across classes, and a second class
/// spilling concurrently would race <c>last_run.log</c> — the artifact the spec asks us to assert on.
///
/// Process assertions use a per-test unique sleep duration rather than a fixed one: a leaked <c>sleep 30</c>
/// from an earlier failed run would otherwise satisfy (or poison) a later test's orphan check.
/// </remarks>
[Collection(SandboxTestCollections.Spillover)]
public class SandboxProcessHardeningTests
{
    private static readonly bool Posix = !OperatingSystem.IsWindows();

    private static string MakeTempDir(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agentmesh-sandbox-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// Counts live processes whose command line contains <paramref name="marker"/>.
    /// </summary>
    /// <remarks>
    /// <c>pgrep -f</c> rather than <c>pgrep</c>: the marker lives in the argument, and the process we care
    /// about is a <c>sleep</c> fork whose name alone says nothing. The <c>[x]</c> bracket trick keeps this
    /// very command line from matching its own search.
    /// </remarks>
    private static async Task<List<string>> FindProcessesMatching(string marker)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        // Anchor to the command/binary invocation so orchestrator processes (e.g. agy, dotnet) that
        // happen to contain the marker in their prompt or arguments are not falsely identified as survivors.
        psi.ArgumentList.Add($"pgrep -af '(^|/){EscapeForSingleQuotes(marker)}($|\\s)' 2>/dev/null || true");

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            // Drop the probe's own shell and the pgrep child, which contain the marker literally.
            .Where(l => !l.Contains("pgrep", StringComparison.Ordinal)
                     && !l.Contains("/bin/sh -c", StringComparison.Ordinal))
            .ToList();
    }

    private static string EscapeForSingleQuotes(string value)
        => value.Replace("'", @"'\''");

    /// <summary>
    /// Members still attached to a process group, by pid. The strongest available orphan assertion: it asks
    /// the kernel who is left in the group we killed, rather than pattern-matching command lines.
    /// </summary>
    private static async Task<List<string>> FindGroupMembers(int pgid)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"ps -eo pid=,pgid=,stat= | awk -v p={pgid} '$2==p && $3 !~ /Z/ {{print $1}}'");

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    // ------------------------------------------------------------------
    // Environment contract
    // ------------------------------------------------------------------

    [Fact]
    public void Sandbox_HostProvidesSetsidSoProcessGroupIsolationIsAvailable()
    {
        // A canary, not a behaviour test. The strong teardown guarantee below (killing backgrounded forks
        // and nested grandchildren) is only mechanically possible when each run gets its own process group,
        // which needs `setsid` from util-linux.
        //
        // Without it the runner degrades on purpose -- signalling a group the child does not own could take
        // down the worker itself, and in a container where the worker is pgid 1 that is a self-kill. But a
        // degraded host should fail HERE, with a sentence that names the missing package, rather than
        // surfacing as three confusing "ProcessGroupId expected greater than 1, found 0" failures in tests
        // that are really about orphans.
        //
        // The worker and API images are Debian-based (aspnet:10.0), where util-linux is Essential, so this
        // holds in every deployed runtime.
        SandboxShell.SupportsProcessGroupIsolation.Should().BeTrue(
            because: $"process-group teardown requires setsid; resolved path was '{SandboxShell.ResolvedSetsidPath ?? "(not found)"}'. " +
                     "Install util-linux in the persona/worker image, or expect orphaned test workers to survive timeouts.");
    }

    // ------------------------------------------------------------------
    // B. Non-destructive log spillover
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExecuteCommand_WhenUnderThreshold_ReturnsFullOutputInline()
    {
        var dir = MakeTempDir("inline");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs("echo \"hello spillover under-threshold\"", dir),
                CancellationToken.None,
                executionId: executionId);

            result.Success.Should().BeTrue();
            result.ExitCode.Should().Be(0);
            result.TimedOut.Should().BeFalse();
            result.Output.Should().Contain("hello spillover under-threshold");

            // The point of the threshold: a small result must not cost a file write or an omission banner.
            result.LogFilePath.Should().BeNull();
            result.Output.Should().NotContain("Omitted");
            result.Output.Should().NotContain("Hint:");

            File.Exists(Path.Combine(CommandOutputSpillover.RunsDirectory, $"run_{executionId}.log"))
                .Should().BeFalse("a sub-threshold run must not touch the spillover directory");
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
        var executionId = Guid.NewGuid().ToString("N");
        const int totalLines = 150;

        try
        {
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs($"for i in $(seq 1 {totalLines}); do echo \"line $i\"; done", dir),
                CancellationToken.None,
                executionId: executionId);

            result.Success.Should().BeTrue();
            result.ExitCode.Should().Be(0);

            // --- the file holds 100% of the stream ---
            result.LogFilePath.Should().NotBeNull();
            result.LogFilePath.Should().Be(Path.Combine(CommandOutputSpillover.RunsDirectory, $"run_{executionId}.log"));
            File.Exists(result.LogFilePath).Should().BeTrue();

            var fileLines = CommandOutputSpillover.SplitLines(await File.ReadAllTextAsync(result.LogFilePath));
            fileLines.Should().HaveCount(totalLines, "spillover is non-destructive: nothing may be lost");
            fileLines.Should().ContainInOrder(
                Enumerable.Range(1, totalLines).Select(i => $"line {i}"));

            // --- last_run.log is refreshed and resolves to a real, complete log ---
            var lastRun = Path.Combine(CommandOutputSpillover.RunsDirectory, "last_run.log");
            File.Exists(lastRun).Should().BeTrue();
            var lastRunLines = CommandOutputSpillover.SplitLines(await File.ReadAllTextAsync(lastRun));
            lastRunLines.Should().HaveCount(totalLines);

            // --- the returned preview is head 20 + banner + tail 30 + hint ---
            var previewLines = CommandOutputSpillover.SplitLines(result.Output);

            previewLines.Should().Contain("line 1");
            previewLines.Should().Contain("line 20");
            previewLines.Should().Contain("line 121");
            previewLines.Should().Contain($"line {totalLines}");

            // The middle is what gets omitted, and it must actually be absent from the payload.
            previewLines.Should().NotContain("line 21");
            previewLines.Should().NotContain("line 120");

            // Head window is exactly lines 1..20 and tail exactly 121..150, so the banner must account for
            // precisely the 100 lines in between.
            previewLines.Should().Contain(l => l.StartsWith("line 20", StringComparison.Ordinal));
            previewLines.Should().Contain(l => l.StartsWith("line 121", StringComparison.Ordinal));
            result.Output.Should().Contain("[... Omitted 100 lines (");
            result.Output.Should().Contain("bytes). Full output saved to ");
            result.Output.Should().Contain(result.LogFilePath);
            result.Output.Should().Contain("Hint: The complete output was saved to ");
            result.Output.Should().Contain("Use 'grep', 'sed', or 'read_file'");

            // The preview must be the smaller artifact, or the mechanism is pointless.
            System.Text.Encoding.UTF8.GetByteCount(result.Output)
                .Should().BeLessThan(System.Text.Encoding.UTF8.GetByteCount(await File.ReadAllTextAsync(result.LogFilePath)));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenByteThresholdExceededButLineCountLow_StillSpills()
    {
        var dir = MakeTempDir("bytes");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            // 10 lines x 600 chars: under the 60-line threshold, well over the 4 KB one. Proves the two
            // thresholds are OR'd, not AND'd — a long single-line compiler dump is a real flooding case.
            var payload = string.Join('\n', Enumerable.Range(0, 10).Select(_ => new string('x', 600)));
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs($"printf '%s\\n' '{payload}'", dir),
                CancellationToken.None,
                executionId: executionId);

            result.ExitCode.Should().Be(0);
            result.LogFilePath.Should().NotBeNull();

            var fileBytes = new FileInfo(result.LogFilePath).Length;
            fileBytes.Should().BeGreaterThan(CommandExecutionPolicy.SpillByteThreshold);

            // Fewer lines than head+tail combined: there is nothing to omit, so the payload is the whole
            // stream plus the pointer. Truncating here would be lossy for no benefit.
            result.Output.Should().NotContain("Omitted");
            result.Output.Should().Contain("Hint: The complete output was saved to ");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenExactlyAtThresholds_DoesNotSpill()
    {
        var dir = MakeTempDir("edge");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            // Exactly 60 lines of 2 chars ("1 " .. "60") plus newlines stays under 4 KB, so neither
            // threshold is *exceeded*. The spec says "exceeds", and off-by-one drift here would either
            // start spilling ordinary output or stop spilling when it should.
            var cmd = "for i in $(seq 1 60); do printf '%s\\n' \"$i \"; done";
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs(cmd, dir),
                CancellationToken.None,
                executionId: executionId);

            result.ExitCode.Should().Be(0);
            result.LogFilePath.Should().BeNull();
            result.Output.Should().NotContain("Omitted");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------
    // A. Execution timeout & process-tree teardown
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExecuteCommand_WhenTimesOut_KillsProcessTreeAndReturnsExitCode124()
    {
        var dir = MakeTempDir("timeout");
        var executionId = Guid.NewGuid().ToString("N");

        // A unique sleep duration is the marker. It must NOT be injected as a `# comment`: in bash the `#`
        // only starts a comment at the start of a word, so `sleep 21 # x & sleep 21` swallows the `&` and
        // the backgrounded fork silently never happens — the test would then "pass" while proving nothing.
        const int sleeperSeconds = 21;

        try
        {
            // The spec's exact shape: a backgrounded fork plus a foreground sleep, so killing only the
            // shell would leave the fork alive and re-parented to init.
            var command = $"sleep {sleeperSeconds} & sleep {sleeperSeconds}";

            var sw = Stopwatch.StartNew();
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs(command, dir),
                CancellationToken.None,
                executionId: executionId,
                // 2s is below the untrusted-input floor of 5s on purpose: the clamp protects the sandbox
                // from a model's tool call, not the sandbox from its own code.
                trustedTimeout: TimeSpan.FromSeconds(2));
            sw.Stop();

            result.TimedOut.Should().BeTrue();
            result.ExitCode.Should().Be(CommandExecutionPolicy.TimeoutExitCode);
            result.ExitCode.Should().Be(124, "the GNU timeout(1) convention is part of the published contract");
            result.Success.Should().BeFalse();

            // Returned promptly: the limit plus the TERM grace, not the full 21s.
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8),
                because: "a timeout that is noticed 21 seconds late is not a timeout");
            sw.Elapsed.TotalSeconds.Should().BeGreaterThanOrEqualTo(1.5);

            // --- the documented diagnostic shape ---
            result.Output.Should().StartWith("CommandTimedOut: Execution exceeded limit of 2s.");
            result.Output.Should().Contain("terminated.");
            result.Output.Should().Contain("Partial output captured before timeout:");

            // --- no orphans ---
            result.ProcessGroupId.Should().BeGreaterThan(1,
                "the child must have been launched in its own process group for group teardown to be possible");

            await Task.Delay(300); // let the kernel finish the reaping
            var survivors = await FindProcessesMatching($"sleep {sleeperSeconds}");
            survivors.Should().BeEmpty(
                $"killing the tree must not leave a backgrounded fork alive; found: {string.Join(" | ", survivors)}");

            var groupMembers = await FindGroupMembers(result.ProcessGroupId);
            groupMembers.Should().BeEmpty("the process group we terminated must be empty");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenTimesOut_KillsTheExactForkedChildPidThatWasRecordedBeforeTheKill()
    {
        var dir = MakeTempDir("exactpid");
        var executionId = Guid.NewGuid().ToString("N");
        var pidFile = Path.Combine(dir, "pids.txt");

        // Long enough to outlive the 2s limit by a wide margin, and unique enough that a leftover process
        // from another test cannot be mistaken for ours.
        const int sleeperSeconds = 27;

        try
        {
            // The command records the pids it actually created *before* hanging. That lets the assertion
            // name the specific processes that must be dead, instead of pattern-matching `pgrep` output and
            // risking a vacuous pass when the fork never happened at all.
            var command = string.Join("; ",
                $"echo $$ > '{pidFile}'",
                $"sleep {sleeperSeconds} & echo $! >> '{pidFile}'",
                $"sleep {sleeperSeconds}");

            var result = await SandboxShell.RunAsync(
                new RunCommandArgs(command, dir),
                CancellationToken.None,
                executionId: executionId,
                trustedTimeout: TimeSpan.FromSeconds(2));

            result.ExitCode.Should().Be(124);

            File.Exists(pidFile).Should().BeTrue("the command must have reached the point where it forked");

            var recorded = (await File.ReadAllTextAsync(pidFile))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .ToList();

            // shell pid + at least one backgrounded fork.
            recorded.Should().HaveCountGreaterThanOrEqualTo(2,
                "the test must actually be exercising a forked child, not just the shell itself");

            var forkPids = recorded.Skip(1).ToList();

            await Task.Delay(400);

            foreach (var forkPid in forkPids)
            {
                // /proc/<pid> is the ground truth for "this exact process is gone". A zombie would still
                // have a /proc entry, so check the state too.
                if (File.Exists($"/proc/{forkPid}/stat"))
                {
                    var stat = await File.ReadAllTextAsync($"/proc/{forkPid}/stat");
                    var state = stat.Split(' ')[2];
                    state.Should().NotBe("Z", $"pid {forkPid} was left as a zombie by the teardown");

                    Assert.Fail($"pid {forkPid} (a fork the command created) survived the timeout teardown");
                }
            }

            result.ProcessGroupId.Should().BeGreaterThan(1);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenTimesOut_ReturnsPartialOutputCapturedBeforeKill()
    {
        var dir = MakeTempDir("partial");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            // Emits, then wedges. Discarding the partial stream (the old behaviour) left the agent with a
            // one-line message and no idea where the run stalled.
            var command = "echo 'building project Alpha'; echo 'compiling 42 files'; sleep 22";

            var result = await SandboxShell.RunAsync(
                new RunCommandArgs(command, dir),
                CancellationToken.None,
                executionId: executionId,
                trustedTimeout: TimeSpan.FromSeconds(2));

            result.TimedOut.Should().BeTrue();
            result.ExitCode.Should().Be(124);
            result.Output.Should().Contain("building project Alpha");
            result.Output.Should().Contain("compiling 42 files");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenCancelledByCaller_TearsDownProcessGroup()
    {
        var dir = MakeTempDir("cancel");
        var executionId = Guid.NewGuid().ToString("N");
        const int sleeperSeconds = 23;

        try
        {
            using var cts = new CancellationTokenSource();

            var run = SandboxShell.RunAsync(
                new RunCommandArgs($"sleep {sleeperSeconds} & sleep {sleeperSeconds}", dir),
                cts.Token,
                executionId: executionId,
                trustedTimeout: TimeSpan.FromSeconds(30));

            await Task.Delay(700);
            cts.Cancel();

            // External cancellation propagates: the caller's turn was cancelled, so inventing a result
            // would be a lie. What must not happen is a leaked process.
            var act = async () => await run;
            await act.Should().ThrowAsync<OperationCanceledException>();

            await Task.Delay(500);
            var survivors = await FindProcessesMatching($"sleep {sleeperSeconds}");
            survivors.Should().BeEmpty(
                $"cancelling the caller token must kill the tree too; found: {string.Join(" | ", survivors)}");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenOperatorAborts_ReturnsAbortedDiagnosticAndKillsTree()
    {
        var dir = MakeTempDir("abort");
        var executionId = Guid.NewGuid().ToString("N");
        const int sleeperSeconds = 24;

        try
        {
            using var abortCts = new CancellationTokenSource();

            var run = SandboxShell.RunAsync(
                new RunCommandArgs($"sleep {sleeperSeconds} & sleep {sleeperSeconds}", dir),
                CancellationToken.None,
                abortToken: abortCts.Token,
                executionId: executionId,
                trustedTimeout: TimeSpan.FromSeconds(30));

            await Task.Delay(700);
            abortCts.Cancel();

            var result = await run;

            // An abort is not a failure to be propagated: the operator asked for it, and the agent still
            // needs the partial output.
            result.ExitCode.Should().Be(CommandExecutionPolicy.AbortedExitCode);
            result.Output.Should().Contain("aborted by operator");
            result.TimedOut.Should().BeFalse();

            await Task.Delay(500);
            var survivors = await FindProcessesMatching($"sleep {sleeperSeconds}");
            survivors.Should().BeEmpty();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenCommandExitsNonZero_PreservesRealExitCodeAndSpillsErrors()
    {
        var dir = MakeTempDir("nonzero");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs("echo 'first error CS1002: ; expected' >&2; exit 7", dir),
                CancellationToken.None,
                executionId: executionId);

            // Regression guard for the setsid wrapper: `--wait` must propagate the child's status, or
            // every failing build would look like a success.
            result.ExitCode.Should().Be(7);
            result.Success.Should().BeFalse();
            result.TimedOut.Should().BeFalse();
            result.Output.Should().Contain("CS1002");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenCommandForksSurvivingGrandchild_LimitStillReapsIt()
    {
        var dir = MakeTempDir("grandchild");
        var executionId = Guid.NewGuid().ToString("N");
        const int sleeperSeconds = 25;

        try
        {
            // Two levels of nesting: the shell forks a subshell which forks the sleeper. Kill(entireProcessTree)
            // walks descendants, so this case alone does not isolate the group-kill mechanism — but it must
            // still be reaped, and it is the shape of a real test runner (bash -> node -> worker).
            var command = $"(sleep {sleeperSeconds} &) ; sleep {sleeperSeconds}";

            var result = await SandboxShell.RunAsync(
                new RunCommandArgs(command, dir),
                CancellationToken.None,
                executionId: executionId,
                trustedTimeout: TimeSpan.FromSeconds(2));

            result.ExitCode.Should().Be(124);

            await Task.Delay(600);
            var survivors = await FindProcessesMatching($"sleep {sleeperSeconds}");
            survivors.Should().BeEmpty(
                $"nested forks must not survive the run; found: {string.Join(" | ", survivors)}. " +
                "If this failed on a host where " +
                $"{nameof(SandboxShell)}.{nameof(SandboxShell.SupportsProcessGroupIsolation)} is false, " +
                "the canary test Sandbox_HostProvidesSetsidSoProcessGroupIsolationIsAvailable should have " +
                "failed first: without setsid there is no dedicated group to signal, so a grandchild that " +
                "was already re-parented is unreachable by design.");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ------------------------------------------------------------------
    // Policy surface (fast, no processes)
    // ------------------------------------------------------------------

    [Fact]
    public void ClampTimeoutSeconds_MapsUntrustedInputOntoThePolicyRange()
    {
        CommandExecutionPolicy.DefaultTimeoutSeconds.Should().Be(180);
        CommandExecutionPolicy.MinTimeoutSeconds.Should().Be(5);
        CommandExecutionPolicy.MaxTimeoutSeconds.Should().Be(600);
        CommandExecutionPolicy.TimeoutExitCode.Should().Be(124);

        CommandExecutionPolicy.ClampTimeoutSeconds(null).Should().Be(180);
        CommandExecutionPolicy.ClampTimeoutSeconds(0).Should().Be(180, "a zero budget would cancel before launch");
        CommandExecutionPolicy.ClampTimeoutSeconds(-45).Should().Be(180);
        CommandExecutionPolicy.ClampTimeoutSeconds(2).Should().Be(5, "the floor protects against a self-cancelling tool call");
        CommandExecutionPolicy.ClampTimeoutSeconds(30).Should().Be(30);
        CommandExecutionPolicy.ClampTimeoutSeconds(180).Should().Be(180);
        CommandExecutionPolicy.ClampTimeoutSeconds(600).Should().Be(600);
        CommandExecutionPolicy.ClampTimeoutSeconds(601).Should().Be(600, "the ceiling bounds how long a worker slot can be held");
        CommandExecutionPolicy.ClampTimeoutSeconds(int.MaxValue).Should().Be(600);
    }

    [Fact]
    public async Task ExecuteCommand_WhenTimeoutBelowFloor_IsClampedUpAndStillSucceeds()
    {
        var dir = MakeTempDir("clamp");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            // A model asking for 1s is not honoured literally (that would fail almost any real command);
            // it is raised to the 5s floor. The command finishes in well under that, so it must succeed.
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs("echo quick", dir, TimeoutSeconds: 1),
                CancellationToken.None,
                executionId: executionId);

            result.ExitCode.Should().Be(0);
            result.TimedOut.Should().BeFalse();
            result.Output.Should().Contain("quick");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenUnderlyingCommandHangs_DoesNotLeakTimersOrStreamsOnSuccessPath()
    {
        var dir = MakeTempDir("regression");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            // The Definition-of-Done regression check: ordinary short commands must complete normally and
            // promptly, with no premature termination from the new 180s default.
            for (var i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                var result = await SandboxShell.RunAsync(
                    new RunCommandArgs($"echo pass-{i}", dir),
                    CancellationToken.None,
                    executionId: $"{executionId}-{i}");
                sw.Stop();

                result.ExitCode.Should().Be(0, $"iteration {i}");
                result.Output.Should().Contain($"pass-{i}");
                sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), $"iteration {i} was cut short");
            }
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenStderrOnly_PreservesErrorTextForTheModel()
    {
        var dir = MakeTempDir("stderr");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            var result = await SandboxShell.RunAsync(
                new RunCommandArgs("echo 'npm ERR! code ELIFECYCLE' >&2; exit 1", dir),
                CancellationToken.None,
                executionId: executionId);

            result.ExitCode.Should().Be(1);
            result.Success.Should().BeFalse();
            result.Output.Should().Contain("ELIFECYCLE");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task ExecuteCommand_WhenNoCommandSupplied_ReturnsUsageErrorWithoutSpawning()
    {
        var result = await SandboxShell.RunAsync(
            new RunCommandArgs("   "),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(127);
        result.Output.Should().Contain("'command' argument is required");
        await Task.CompletedTask;
    }

    [Fact]
    public void BuildPreview_WhenLinesArePathologicallyLong_KeepsPayloadBoundedButNeverLosesThePointer()
    {
        // The line windows bound a preview made of ordinary lines. A minified bundle, a base64 blob or a
        // JSON API response can be a megabyte across four lines and still fit inside "20 head + 30 tail",
        // so without a byte budget the mechanism that exists to protect the context window returns the
        // whole megabyte.
        var blob = string.Join('\n', Enumerable.Range(0, 4).Select(_ => new string('q', 400_000)));

        var preview = CommandOutputSpillover.BuildPreview(blob, "/tmp/agentmesh/runs/run_blob.log", out _, out _);

        Encoding.UTF8.GetByteCount(preview)
            .Should().BeLessThanOrEqualTo(CommandExecutionPolicy.PreviewByteBudget + 512,
                "a preview must not be able to flood the context window regardless of line shape");

        // The invariant that matters most when we truncate: the reader must always be told where the full
        // bytes live. A preview that lost this would turn a context problem into a debugging dead end.
        preview.Should().Contain("/tmp/agentmesh/runs/run_blob.log");
        preview.Should().Contain("Hint: The complete output was saved to ");
    }

    [Fact]
    public void BuildPreview_WhenBudgetedForLongLines_KeepsEveryWindowLineAndClampsWithinIt()
    {
        // Preferred degradation: 50 short-ish lines beat 2 clamped ones, because the reader still sees the
        // shape of the run. This is the pass that fires when lines are long but the count is normal.
        //
        // 60 lines, not 50: the head (20) and tail (30) windows overlap completely at 50 lines, so there is
        // no middle to omit and the banner is never emitted -- which is correct behaviour, but not what this
        // test is about.
        var lines = Enumerable.Range(1, 60).Select(i => $"line{i}:" + new string('z', 5_000)).ToList();
        var raw = string.Join('\n', lines);

        var preview = CommandOutputSpillover.BuildPreview(raw, "/tmp/agentmesh/runs/run_long.log", out var omitted, out _);

        omitted.Should().Be(10, "60 lines minus a 20-line head and a 30-line tail");
        Encoding.UTF8.GetByteCount(preview).Should().BeLessThan(Encoding.UTF8.GetByteCount(raw));

        // Every window line survives, merely shortened: the reader can still tell which steps ran.
        preview.Should().Contain("line1:");
        preview.Should().Contain("line20:");
        preview.Should().Contain("line31:");
        preview.Should().Contain("line60:");
        preview.Should().Contain("[line truncated]", "over-long lines are clamped rather than dropped");

        // The banner and the hint name the log path; clamping them would corrupt the pointer.
        preview.Should().Contain("Full output saved to /tmp/agentmesh/runs/run_long.log");
        preview.Should().Contain("Hint: The complete output was saved to /tmp/agentmesh/runs/run_long.log");
    }

    [Fact]
    public void BuildPreview_WhenWithinBudget_IsNotClampedAtAll()
    {
        // The backstop must stay invisible for ordinary output: clamping a normal 120-char error line would
        // be a new, self-inflicted form of silent loss.
        var line = "Program.cs(12,5): error CS0246: The type or namespace 'Widget' could not be found (are you missing a using directive or an assembly reference?)";
        var raw = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"{i}: {line}")) + "\n";

        var preview = CommandOutputSpillover.BuildPreview(raw, "/tmp/x.log", out var omitted, out _);

        omitted.Should().Be(50);
        preview.Should().NotContain("[line truncated]");
        preview.Should().NotContain("preview shortened");
        preview.Should().Contain(line);
    }

    [Fact]
    public void BuildPreview_ClampsOnCharacterBoundariesForMultibyteOutput()
    {
        // A byte-indexed cut through a multi-byte sequence yields U+FFFD, which is exactly the corruption
        // this feature is meant to avoid. Test output is full of it: ✓, ✗, →, and box-drawing glyphs.
        var raw = string.Join('\n', Enumerable.Range(0, 4).Select(_ => "✓ 编译错误 → ✗ " + new string('ü', 200_000)));

        var preview = CommandOutputSpillover.BuildPreview(raw, "/tmp/x.log", out _, out _);

        preview.Should().NotContain("\uFFFD", "clamping must not split a multi-byte character");
        preview.Should().Contain("✓");
        preview.Should().Contain("编译错误");
    }

    [Fact]
    public async Task ExecuteCommand_WhenSpilled_PreviewIsAlwaysSmallerThanTheFile()
    {
        var dir = MakeTempDir("bounded");
        var executionId = Guid.NewGuid().ToString("N");
        try
        {
            // 4 lines x 300 KB: trips the byte threshold, fits the line windows. The worst case for a
            // line-count-only strategy.
            var cmd = "for i in 1 2 3 4; do printf '%s\\n' \"$(head -c 300000 /dev/zero | tr '\\0' 'w')\"; done";

            var result = await SandboxShell.RunAsync(
                new RunCommandArgs(cmd, dir),
                CancellationToken.None,
                executionId: executionId);

            result.ExitCode.Should().Be(0);
            result.LogFilePath.Should().NotBeNull();

            var fileBytes = new FileInfo(result.LogFilePath).Length;
            var previewBytes = Encoding.UTF8.GetByteCount(result.Output);

            fileBytes.Should().BeGreaterThan(1_000_000);
            ((long)previewBytes).Should().BeLessThan(fileBytes / 10,
                "the whole point of spillover is that the payload is the small artifact");
            result.Output.Should().Contain("Hint: The complete output was saved to ");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void BuildPreview_WhenLogFileCouldNotBeWritten_NeverPointsAtAPathThatDoesNotExist()
    {
        // The failure mode this guards: the disk is full, the write throws, and the payload still tells the
        // model "the complete output was saved to /tmp/...". The agent then burns tool calls grepping a file
        // that was never created. Telling it the truth costs one line; lying costs a debugging dead end.
        var raw = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}")) + "\n";

        var preview = CommandOutputSpillover.BuildPreview(raw, null, out var omitted, out _);

        omitted.Should().Be(150);
        preview.Should().Contain("could not be written");
        preview.Should().NotContain("Hint: The complete output was saved to",
            because: "there is no file to inspect, so the hint would send the agent to grep a phantom path");

        // The preview must still be useful without a pointer.
        preview.Should().Contain("line 1");
        preview.Should().Contain("line 200");
    }

    [Fact]
    public void SplitLines_DoesNotCountATrailingNewlineAsAnEmptyLine()
    {
        // The banner's line arithmetic is only trustworthy if the counter matches what a reader would state
        // while looking at the file.
        CommandOutputSpillover.SplitLines("a\nb\n").Should().HaveCount(2);
        CommandOutputSpillover.SplitLines("a\nb").Should().HaveCount(2);
        CommandOutputSpillover.SplitLines("a\r\nb\r\n").Should().HaveCount(2);
        CommandOutputSpillover.SplitLines("\n").Should().BeEmpty();
        CommandOutputSpillover.SplitLines("").Should().BeEmpty();
    }

    [Fact]
    public void BuildPreview_OmitsExactlyTheMiddleAndAccountsForEveryByte()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"l{i}").ToList();
        var raw = string.Join('\n', lines) + "\n";

        var preview = CommandOutputSpillover.BuildPreview(raw, "/tmp/x.log", out var omittedLines, out var omittedBytes);

        omittedLines.Should().Be(50, "100 lines minus a 20-line head and a 30-line tail");
        preview.Should().Contain("l1\n");
        preview.Should().Contain("l20");
        preview.Should().NotContain("l21");
        preview.Should().Contain("l71");
        preview.Should().Contain("l100");
        preview.Should().Contain("[... Omitted 50 lines (");

        // head + omitted + tail must reconstruct the file's size, or the banner is lying about what it hid.
        var totalBytes = System.Text.Encoding.UTF8.GetByteCount(raw);
        var headBytes = System.Text.Encoding.UTF8.GetByteCount(string.Join('\n', lines.Take(20)));
        var tailBytes = System.Text.Encoding.UTF8.GetByteCount(string.Join('\n', lines.Skip(70)));
        omittedBytes.Should().Be(totalBytes - headBytes - tailBytes);
    }
}
