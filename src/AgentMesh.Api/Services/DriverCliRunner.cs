using System.Diagnostics;

namespace AgentMesh.Api.Services;

/// <summary>
/// Runs a driver CLI on the API host and returns stdout, or <c>null</c> for "no answer".
/// </summary>
/// <remarks>
/// Extracted from <see cref="AgyDriverQuotaProbe"/> so the quota and capability probes share one
/// definition of failure. Both must treat a non-zero exit, a timeout, or a missing binary as "the
/// provider did not answer" rather than as an exception to paper over - that distinction is the
/// whole reason the quota panel stopped rendering fabricated meters.
/// </remarks>
internal static class DriverCliRunner
{
    /// <summary>
    /// Hard ceiling on a single invocation. Callers pass a token they deliberately do *not* bind
    /// to the HTTP request, so the timeout is the only thing standing between a stalled CLI and a
    /// request that never returns.
    /// </summary>
    internal static async Task<string?> RunAsync(
        string executable,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start()) return null;
        }
        catch
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        await stderrTask;

        // Unauthenticated / unsupported builds exit non-zero with a login prompt on stderr.
        if (process.ExitCode != 0) return null;

        return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
    }
}
