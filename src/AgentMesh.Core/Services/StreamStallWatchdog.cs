namespace AgentMesh.Core.Services;

/// <summary>
/// Result of one bounded read: either a line, the end of the stream, or proof of silence.
/// </summary>
/// <param name="Line">
/// The line that was read, or <see langword="null"/> for end-of-stream when
/// <see cref="Stalled"/> is also <see langword="false"/>.
/// </param>
/// <param name="Stalled">
/// <see langword="true"/> when no line arrived within the idle limit. The read that eventually produced
/// nothing is still outstanding; the caller is expected to tear the producer down.
/// </param>
public readonly record struct StallReadResult(string? Line, bool Stalled)
{
    /// <summary>True when a real line was delivered.</summary>
    public bool GotLine => !Stalled && Line is not null;
}

/// <summary>
/// Bounds a stream read that would otherwise wait forever on a producer that has stopped producing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> A subprocess driver adapter waits on
/// <c>proc.StandardOutput.ReadLineAsync(ct)</c>. That await is only cancellable through
/// <paramref name="ct"/>, and the token a driver is handed is the runner's shutdown token — nothing
/// cancels it when the child process wedges mid-command. The result is a turn that is neither running
/// nor finished: the driver holds the tool call it never returned, the adapter holds the read, and the
/// session sits in a live status until a human notices. This is the difference between "no output for
/// four minutes" (ordinary, the model is thinking or a build is compiling) and "no output ever again"
/// (the turn must be settled), which is exactly what a plain read cannot tell you.
/// </para>
/// <para>
/// <strong>Why the timeout does not cancel the read.</strong> Cancelling <c>ReadLineAsync</c> on a
/// <see cref="System.IO.StreamReader"/> can leave the reader mid-frame, so subsequent reads would be
/// wrong rather than merely late. The abandoned read is instead detached and its fault observed: the
/// caller kills the process, the pipe breaks, and the task finishes as EOF or an
/// <see cref="IOException"/> that nobody is required to look at.
/// </para>
/// <para>
/// The idle clock restarts on every delivered line, so a chatty stream is never cut off, and a
/// genuinely long silent command is cut off once rather than on a fixed total budget.
/// </para>
/// </remarks>
public static class StreamStallWatchdog
{
    /// <summary>
    /// Reads one line, giving up after <paramref name="idleLimit"/> of silence.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> fired. Distinct from a stall on purpose: an operator- or platform-issued
    /// stop must not be reported to the model as a wedged driver.
    /// </exception>
    public static async Task<StallReadResult> ReadLineAsync(
        TextReader reader,
        TimeSpan idleLimit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (idleLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleLimit), "An idle limit must be positive; use a very large one to mean 'effectively unbounded'.");
        }

        ct.ThrowIfCancellationRequested();

        var read = reader.ReadLineAsync(ct).AsTask();

        // No linked cancellation on this delay: on timeout the delay is abandoned and the garbage
        // collector reclaims the timer. A CancellationTokenSource per read on a stream that emits
        // thousands of lines per turn is a per-line allocation for no benefit.
        var silence = Task.Delay(idleLimit, CancellationToken.None);
        var finished = await Task.WhenAny(read, silence).ConfigureAwait(false);

        if (finished != read)
        {
            ObserveQuietly(read);
            return new StallReadResult(null, Stalled: true);
        }

        ObserveQuietly(silence);
        return new StallReadResult(await read.ConfigureAwait(false), Stalled: false);
    }

    /// <summary>
    /// Reads lines until end-of-stream or a stall, handing each line to <paramref name="onLine"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the stream reached its end, <see langword="false"/> when it stalled.
    /// </returns>
    public static async Task<bool> PumpAsync(
        TextReader reader,
        TimeSpan idleLimit,
        Func<string, Task> onLine,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        while (true)
        {
            var result = await ReadLineAsync(reader, idleLimit, ct).ConfigureAwait(false);
            if (result.Stalled) return false;
            if (result.Line is null) return true;
            await onLine(result.Line).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prevents an unobserved exception on a read that was abandoned by a stall from reaching the
    /// finalizer, where it would surface as a process-level <c>UnobservedTaskException</c>.
    /// </summary>
    private static void ObserveQuietly(Task orphan) =>
        _ = orphan.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
