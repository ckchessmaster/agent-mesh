using System.Globalization;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// The single entry point for running a shell command inside the agent sandbox. Both the CLI runner (an
/// interactive agent's <c>run_command</c> tool call) and the Temporal worker activity call this, so the
/// timeout policy, the teardown ladder, and the spillover thresholds are identical in every runtime.
/// </summary>
/// <remarks>
/// Responsibilities kept deliberately narrow:
/// <list type="bullet">
/// <item><see cref="SandboxedProcessRunner"/> owns process lifetime and teardown.</item>
/// <item><see cref="CommandOutputSpillover"/> owns what the model gets to see.</item>
/// <item>This type owns the <em>contract</em>: clamping untrusted input, and turning a raw outcome into the
/// documented exit protocol.</item>
/// </list>
/// </remarks>
public static class SandboxShell
{
    /// <summary>
    /// True when this host can launch each command in its own POSIX process group, which is the mechanism
    /// that lets a timeout reach backgrounded forks (a hung <c>node</c> test worker holding a port) rather
    /// than only the shell that spawned them.
    /// </summary>
    /// <remarks>
    /// Requires <c>setsid</c> from util-linux. When it is absent the runner degrades to <c>pkill -P</c> plus
    /// <c>Kill(entireProcessTree)</c>, which still reaps direct descendants but cannot signal a detached
    /// group. Surfaced publicly so a host can log the weaker guarantee at startup, and so tests can state
    /// which contract they are exercising instead of silently assuming the stronger one.
    /// </remarks>
    public static bool SupportsProcessGroupIsolation => SandboxedProcessRunner.SupportsProcessGroupIsolation;

    /// <summary>Diagnostics: the resolved <c>setsid</c> path, or null when unavailable.</summary>
    public static string? ResolvedSetsidPath => SandboxedProcessRunner.ResolvedSetsidPath;
    /// <summary>
    /// Runs <paramref name="args"/> and returns the payload to hand back to the model.
    /// </summary>
    /// <param name="args">Command, optional working directory, optional (untrusted) timeout.</param>
    /// <param name="ct">External cancellation. Propagates as <see cref="OperationCanceledException"/>.</param>
    /// <param name="onOutputChunk">Optional streaming sink for the dashboard's live tail.</param>
    /// <param name="log">Diagnostics sink.</param>
    /// <param name="abortToken">Internal "stop this command" signal; reported as an abort, not a failure.</param>
    /// <param name="executionId">
    /// Stable id used to name the spillover file. Pass the session/command pair when you have one so the log
    /// path is predictable across retries; a fresh guid is used otherwise.
    /// </param>
    /// <param name="onStarted">
    /// Invoked right after the process launches with the live handle and its group-leader pid, for hosts
    /// that want to register the command so an operator can abort it.
    /// </param>
    /// <param name="trustedTimeout">
    /// A wall-clock limit supplied by in-process code rather than by a tool call, honoured verbatim (floored
    /// at 1s) instead of clamped. Exists so an internal caller can deliberately run a 2-second probe without
    /// the untrusted-input clamp silently turning it into 5.
    /// </param>
    public static async Task<CommandExecutionResult> RunAsync(
        RunCommandArgs args,
        CancellationToken ct,
        Func<string, Task>? onOutputChunk = null,
        Action<string>? log = null,
        CancellationToken abortToken = default,
        string? executionId = null,
        Action<System.Diagnostics.Process, int>? onStarted = null,
        TimeSpan? trustedTimeout = null)
    {
        if (args == null || string.IsNullOrWhiteSpace(args.Command))
        {
            return new CommandExecutionResult(false, 127, "Error: 'command' argument is required.");
        }

        // The timeout arrives from a model's tool call, so it is clamped here rather than trusted. A value
        // of 0 or a negative would otherwise cancel the run before the process is even started.
        //
        // `trustedTimeout` is the escape hatch for in-process callers (a probe, a test, a health check) that
        // need a budget outside [5,600]. The clamp exists to protect the sandbox from a model, not to
        // protect the sandbox from its own code.
        var timeoutSeconds = trustedTimeout.HasValue
            ? Math.Max(1, (int)Math.Round(trustedTimeout.Value.TotalSeconds))
            : CommandExecutionPolicy.ClampTimeoutSeconds(args.TimeoutSeconds);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        var workingDirectory = ResolveWorkingDirectory(args.WorkingDirectory);

        var id = string.IsNullOrWhiteSpace(executionId)
            ? Guid.NewGuid().ToString("N")
            : executionId;

        var outcome = await SandboxedProcessRunner.RunAsync(
            args.Command,
            workingDirectory,
            timeout,
            ct,
            onOutputChunk,
            log,
            abortToken,
            onStarted);

        return await FormatAsync(args.Command, outcome, timeoutSeconds, id);
    }

    /// <summary>
    /// Tears down a running command's whole process tree. Exposed for the dashboard's "stop command" path,
    /// which holds a <see cref="Process"/> handle but not the teardown ladder.
    /// </summary>
    /// <param name="proc">The live process.</param>
    /// <param name="processGroupLeaderPid">
    /// The group leader pid from <see cref="RunAsync"/>'s outcome, or 0 when unknown. Pass 0 unless you have
    /// proved the pid leads its own group: signalling a group you do not own can take down the caller.
    /// </param>
    /// <param name="log">Diagnostics sink.</param>
    public static void KillProcessTree(
        System.Diagnostics.Process proc,
        int processGroupLeaderPid = 0,
        Action<string>? log = null)
        => SandboxedProcessRunner.KillTree(proc, processGroupLeaderPid, log);

    /// <summary>
    /// Turns a raw outcome into the documented payload. Split out from <see cref="RunAsync"/> so the exit
    /// protocol and the spillover formatting are unit-testable without spawning a process.
    /// </summary>
    internal static async Task<CommandExecutionResult> FormatAsync(
        string command,
        SandboxRunOutcome outcome,
        int timeoutSeconds,
        string executionId)
    {
        var combined = CombineStreams(outcome.StdOut, outcome.StdErr);

        if (outcome.TimedOut)
        {
            // Spillover still applies to a timed-out run: a hang that spewed 5,000 lines before wedging is
            // exactly the case that would otherwise flood the context window.
            var spill = await CommandOutputSpillover.ApplyAsync(combined, executionId, CancellationToken.None);

            var header = $"CommandTimedOut: Execution exceeded limit of {timeoutSeconds}s. " +
                $"Process group {TerminatedTargetOf(outcome)} terminated.";

            var body = string.IsNullOrWhiteSpace(spill.Text)
                ? "(no output captured before timeout)"
                : spill.Text;

            var output = $"{header}\nPartial output captured before timeout:\n{body}";

            return new CommandExecutionResult(
                Success: false,
                ExitCode: CommandExecutionPolicy.TimeoutExitCode,
                Output: output,
                LogFilePath: spill.LogFilePath,
                TimedOut: true,
                ProcessGroupId: outcome.ProcessGroupId);
        }

        if (outcome.Aborted)
        {
            var spill = await CommandOutputSpillover.ApplyAsync(combined, executionId, CancellationToken.None);

            var body = string.IsNullOrWhiteSpace(spill.Text)
                ? "(no output captured before abort)"
                : spill.Text;

            return new CommandExecutionResult(
                Success: false,
                ExitCode: CommandExecutionPolicy.AbortedExitCode,
                Output: $"🛑 Command aborted by operator.\nPartial output captured before abort:\n{body}",
                LogFilePath: spill.LogFilePath,
                TimedOut: false,
                Aborted: true,
                ProcessGroupId: outcome.ProcessGroupId);
        }

        var normal = await CommandOutputSpillover.ApplyAsync(combined, executionId, CancellationToken.None);

        return new CommandExecutionResult(
            Success: outcome.ExitCode == 0,
            ExitCode: outcome.ExitCode,
            Output: normal.Text,
            LogFilePath: normal.LogFilePath,
            TimedOut: false,
            ProcessGroupId: outcome.ProcessGroupId);
    }

    /// <summary>
    /// The identifier to name in the timeout diagnostic.
    /// </summary>
    /// <remarks>
    /// Normally the process-group id, because that is what we signalled. On the no-<c>setsid</c> fallback
    /// path there is no dedicated group, and printing "Process group 0 terminated" would claim a teardown
    /// that never happened — so we name the pid instead, which is what stage 3 actually killed.
    /// </remarks>
    private static string TerminatedTargetOf(SandboxRunOutcome outcome)
        => outcome.ProcessGroupId > 1
            ? outcome.ProcessGroupId.ToString(CultureInfo.InvariantCulture)
            : $"(no dedicated group; pid {outcome.ProcessId.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>
    /// Merges the two streams for spillover accounting. stdout leads so a piped command's payload stays
    /// contiguous and its line numbers match what the reader sees in the log file.
    /// </summary>
    /// <remarks>
    /// Interleaving is not preserved. That is a deliberate trade: stdout and stderr are separate OS pipes
    /// and any "merged" order is already an approximation. Grouping them keeps compiler errors (stderr)
    /// together at the end of the tail window, which is where a reader looks for them.
    /// </remarks>
    internal static string CombineStreams(string stdOut, string stdErr)
    {
        stdOut ??= string.Empty;
        stdErr ??= string.Empty;

        var outText = stdOut.Trim();
        var errText = stdErr.Trim();

        if (outText.Length == 0) return errText;
        if (errText.Length == 0) return outText;
        return outText + Environment.NewLine + errText;
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            return workingDirectory;
        }

        // ProcessStartInfo.WorkingDirectory must point at a real directory or Start() throws before the
        // command has a chance to fail on its own merits.
        return Directory.Exists("/workspace") ? "/workspace" : Path.GetTempPath();
    }
}
