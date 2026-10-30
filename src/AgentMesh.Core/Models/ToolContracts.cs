namespace AgentMesh.Core.Models;

/// <summary>
/// Arguments for a sandboxed shell execution.
/// </summary>
/// <param name="Command">The exact command line handed to <c>/bin/bash -c</c> (or <c>cmd.exe /c</c>).</param>
/// <param name="WorkingDirectory">Optional working directory; defaults to the caller's workspace root.</param>
/// <param name="TimeoutSeconds">
/// Optional per-invocation wall-clock limit. Untrusted input (it arrives from a model's tool call), so it is
/// always passed through <see cref="CommandExecutionPolicy.ClampTimeoutSeconds"/> before use. Null means
/// "use <see cref="CommandExecutionPolicy.DefaultTimeoutSeconds"/>".
/// </param>
public record RunCommandArgs(
    string Command,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null
);

/// <summary>
/// The outcome of a sandboxed shell execution, after log spillover has been applied.
/// </summary>
/// <param name="Success">True only when the command exited 0 and was not killed by a timeout.</param>
/// <param name="ExitCode">
/// The child's exit code, or <see cref="CommandExecutionPolicy.TimeoutExitCode"/> (124, the GNU
/// <c>timeout(1)</c> convention) when the wall-clock limit fired.
/// </param>
/// <param name="Output">
/// The text safe to hand back to a model: the raw output when under the spillover threshold, otherwise the
/// head/tail preview with an omission banner and inspection hint. Never lossy — the full stream is on disk
/// at <paramref name="LogFilePath"/> whenever this is a preview.
/// </param>
/// <param name="LogFilePath">
/// Absolute path to the file holding 100% of the raw stream, or null when the output was small enough to
/// stay inline and no file was written.
/// </param>
/// <param name="TimedOut">True when execution was cut short by the timeout rather than by the command finishing.</param>
/// <param name="Aborted">
/// True when an operator stopped the command. Distinct from <paramref name="TimedOut"/> because the two need
/// different follow-up: a timeout means "the command is too slow, narrow the test filter", an abort means
/// "a human intervened, stop and wait". Also the only reliable way to tell an abort apart from a command
/// that happened to exit with the same status on its own.
/// </param>
/// <param name="ProcessGroupId">
/// The POSIX process-group id the command ran in, or 0 when it did not lead its own group. Surfaced because
/// the timeout diagnostic quotes it ("Process group {pid} terminated") and because an operator abort needs
/// exactly this number to signal the same set of processes.
/// </param>
public record CommandExecutionResult(
    bool Success,
    int ExitCode,
    string Output,
    string? LogFilePath = null,
    bool TimedOut = false,
    bool Aborted = false,
    int ProcessGroupId = 0
);

/// <summary>
/// Single source of truth for sandbox execution limits. Both the CLI runner (the path an interactive agent
/// actually takes) and the Temporal worker activity read these, so a limit can never drift between runtimes.
/// </summary>
public static class CommandExecutionPolicy
{
    /// <summary>Wall-clock limit applied when a caller does not ask for one.</summary>
    public const int DefaultTimeoutSeconds = 180;

    /// <summary>Floor for a model-supplied timeout; stops a tool call asking for e.g. 0s from self-cancelling.</summary>
    public const int MinTimeoutSeconds = 5;

    /// <summary>Ceiling for a model-supplied timeout; bounds how long a worker slot can be held hostage.</summary>
    public const int MaxTimeoutSeconds = 600;

    /// <summary>GNU <c>timeout(1)</c> exit status. Distinguishes "we killed it" from "the command failed".</summary>
    public const int TimeoutExitCode = 124;

    /// <summary>POSIX signal number + 128: the conventional exit status for a SIGKILLed shell.</summary>
    public const int AbortedExitCode = -1;

    /// <summary>Spill to disk once the combined stream exceeds this many lines.</summary>
    public const int SpillLineThreshold = 60;

    /// <summary>Spill to disk once the combined stream exceeds this many bytes.</summary>
    public const int SpillByteThreshold = 4096;

    /// <summary>Leading lines kept in a preview — where build/test banners and the first error usually live.</summary>
    public const int PreviewHeadLines = 20;

    /// <summary>
    /// Trailing lines kept in a preview — where the summary ("Tests failed: 3"), stack trace and exit
    /// diagnostics live. Asymmetric on purpose: the tail is where a run explains itself.
    /// </summary>
    public const int PreviewTailLines = 30;

    /// <summary>
    /// Hard ceiling on the size of a preview, independent of how many lines it spans.
    /// </summary>
    /// <remarks>
    /// The line windows alone do not bound the payload: a minified JS bundle, a base64 blob or a JSON API
    /// response can be 1 MB across four lines. Such a stream trips the byte threshold and gets spilled, yet
    /// still fits inside "20 head + 30 tail" lines — so returning it whole would flood the context window
    /// that spillover exists to protect. This budget is the backstop for that case.
    ///
    /// Set to 4x <see cref="SpillByteThreshold"/>: comfortably above any ordinary preview, and small enough
    /// that a pathological single-line dump still costs ~16 KB of context rather than megabytes.
    /// </remarks>
    public const int PreviewByteBudget = 16_384;

    /// <summary>Grace period between SIGTERM and SIGKILL for a stubborn process group.</summary>
    public static readonly TimeSpan TermGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maps an optional, untrusted timeout onto the policy range. Null, zero and out-of-range values all
    /// collapse to a safe number rather than throwing: a bad tool-call argument should degrade, not kill a turn.
    /// </summary>
    public static int ClampTimeoutSeconds(int? requestedSeconds)
    {
        if (!requestedSeconds.HasValue) return DefaultTimeoutSeconds;

        var value = requestedSeconds.Value;
        if (value <= 0) return DefaultTimeoutSeconds;

        return Math.Clamp(value, MinTimeoutSeconds, MaxTimeoutSeconds);
    }
}
