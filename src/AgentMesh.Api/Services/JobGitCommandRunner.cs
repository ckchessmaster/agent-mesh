using System.Diagnostics;
using System.Text;

namespace AgentMesh.Api.Services;

/// <summary>One <c>git</c> invocation's captured output.</summary>
/// <param name="ExitCode">
/// The process exit code, or <c>-1</c> when git could not be started or was killed at the timeout.
/// </param>
/// <param name="StdOut">
/// Captured stdout decoded as UTF-8, byte-exact. NUL bytes survive, which is what the <c>-z</c>
/// payloads depend on: a line-based capture would split a filename containing a newline in half.
/// </param>
/// <param name="StdErr">Captured stderr, used only in log and note text after sanitizing.</param>
/// <param name="TimedOut">True when the run hit its wall-clock limit.</param>
/// <param name="OutputTruncated">
/// True when stdout exceeded <see cref="JobGitCommandRunner.MaxOutputBytes"/>. The payload is then
/// incomplete and must not be parsed: a truncated <c>--numstat</c> list reads as a smaller change,
/// which is the one wrong answer that looks right.
/// </param>
public readonly record struct JobGitResult(
    int ExitCode, string StdOut, string StdErr, bool TimedOut = false, bool OutputTruncated = false)
{
    public bool Success => ExitCode == 0 && !TimedOut && !OutputTruncated;

    /// <summary>
    /// A successful run whose stdout is <paramref name="stdout"/>.
    /// </summary>
    /// <remarks>
    /// A named constructor rather than <c>new JobGitResult(0, x, "")</c> at each call site because the
    /// meaningful part of a stubbed git answer is its payload, and a literal <c>0</c> in the first
    /// position reads as an exit code a reader has to remember the meaning of. Test fixtures and the
    /// production "no such ref" path both use it, which is also why it lives on the record rather than
    /// in a test helper: the reader's own failure answers are the same shape.
    /// </remarks>
    public static JobGitResult Ok(string stdout, int exitCode = 0, string stderr = "") =>
        new(exitCode, stdout, stderr);

    /// <summary>A failed run, with <paramref name="message"/> in the slot <see cref="ExplainFailure"/> reads.</summary>
    public static JobGitResult Fail(string message, string stdout = "") =>
        new(128, stdout, message);

    /// <summary>One-line explanation for a note, safe to show and safe to log.</summary>
    public string ExplainFailure()
    {
        if (TimedOut) return "the git command timed out";
        if (OutputTruncated) return "git produced more output than this endpoint will read";

        var text = FirstLine(Sanitize(StdErr.Length > 0 ? StdErr : StdOut));
        return text.Length == 0 ? $"git exited with code {ExitCode}" : text;
    }

    private static string FirstLine(string value) =>
        value.Split('\n')[0].Trim();

    /// <summary>
    /// Strips control characters and any <c>user:pass@host</c> credential git echoed back in a remote
    /// URL.
    /// </summary>
    /// <remarks>
    /// git's stderr names the remote it contacted, and a remote configured with a token in its URL
    /// would otherwise reach the job chat and the browser. The same hazard <c>GitHubPullRequestService</c>
    /// guards on the push path; this half covers the shape that can appear from a repository's own
    /// configuration, which this process never holds as a secret it knows the value of.
    /// </remarks>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var text = System.Text.RegularExpressions.Regex
            .Replace(value, @"(?<=://)[^\s/@:]+:[^\s@/]+@", "[redacted]@");

        var buffer = new char[text.Length];
        var length = 0;

        foreach (var c in text)
        {
            if (c is '\0' or '\r') continue;
            // Keep \n and \t; drop the rest of C0/C1 so one line of stderr cannot smuggle terminal
            // escape sequences into a chat message.
            if (c < 0x20 && c != '\n' && c != '\t') continue;
            if (c is >= '\u0080' and <= '\u009f') continue;
            buffer[length++] = c;
        }

        return new string(buffer, 0, length).Trim();
    }
}

/// <summary>Runs one <c>git</c> command and returns its captured output.</summary>
/// <remarks>
/// A seam rather than a static call so the change reader is testable on captured payloads without git
/// on PATH, and so the only code in this feature that spawns a process is in one place.
/// </remarks>
public interface IJobGitCommandRunner
{
    Task<JobGitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default);
}

/// <summary>
/// The real runner: <c>git</c> in a child process, read-only by the caller's argument lists, with hard
/// limits and no shell.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The directory is a runner's live checkout, so this class must never write.</strong> The
/// commands <see cref="JobChangesGitSource"/> builds are <c>diff</c>, <c>status</c>, <c>log</c>,
/// <c>rev-list</c>, <c>merge-base</c> and <c>rev-parse --verify</c>; none can mutate the tree. The
/// allowlist lives with the argument builders and every method here takes an argument vector the
/// caller assembled, which keeps "add a write to the change reader" a deliberate act rather than an
/// accident of someone passing <c>checkout</c> through a helper.
/// </para>
/// <para>
/// <strong>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>.</strong> Nothing is
/// re-parsed by a shell, so a branch name containing a space, a quote or a backtick stays one
/// argument. Same reasoning as the push path in <c>GitHubPullRequestService</c>.
/// </para>
/// <para>
/// <strong>Streams are drained by hand, not by <c>OutputDataReceived</c>.</strong> The event-based
/// reader is line-based, and reconstructing a <c>-z</c> payload from lines means re-appending newlines
/// that the payload's own bytes happen to contain — a filename with a newline in it would come back
/// mangled by a parser that looks perfectly tested. Reading the stream directly is byte-exact and
/// makes the size cap a plain byte count. The drain never stops early even past the cap, because a
/// child blocked on a full pipe is a hung request rather than a truncated answer.
/// </para>
/// <para>
/// <strong>Environment.</strong> The anti-hang trio (<c>GIT_TERMINAL_PROMPT=0</c>,
/// <c>GCM_INTERACTIVE=never</c>, an <c>*_ASKPASS</c> that echoes) turns a credential prompt into an
/// immediate failure instead of a request that never returns. <c>GIT_PAGER=cat</c> and
/// <c>--no-pager</c> cover the case where git decides to page for a non-tty.
/// <c>core.fsmonitor=false</c> is passed because a repository that enabled fsmonitor would otherwise
/// spawn a long-lived daemon from a control-plane read, which is a resource leak on a replica that
/// will be asked this question a lot.
/// </para>
/// <para>
/// <strong><c>--no-ext-diff</c> and <c>--no-textconv</c> are not cosmetic.</strong> A checked-in
/// <c>.gitattributes</c> can name an external diff or textconv driver, and that driver would then run
/// as a child process of the API, from repository content, on a request path. On a mesh that clones
/// repositories nobody has audited, that is a remote-code-execution primitive handed to anyone who can
/// push a branch. Suppressing both costs nothing here: the panel wants git's own unified diff.
/// </para>
/// <para>
/// <strong>What is deliberately not overridden:</strong> <c>HOME</c>, <c>GIT_DIR</c>,
/// <c>GIT_INDEX_FILE</c> and the repository's own local config. The question being answered is about
/// this checkout as the runner has it, and scrubbing those would answer a question about a different
/// repository while still reporting success.
/// </para>
/// </remarks>
public sealed class JobGitCommandRunner(
    ILogger<JobGitCommandRunner>? logger = null,
    int timeoutSeconds = 0) : IJobGitCommandRunner
{
    /// <summary>
    /// Per-invocation ceiling: generous for a <c>status</c> call in a monorepo, short enough that a
    /// wedged git cannot stall a request thread. Configure with
    /// <c>Platform:JobChangesGitTimeoutSeconds</c>.
    /// </summary>
    public const int DefaultTimeoutSeconds = 20;

    /// <summary>
    /// Ceiling on retained stdout, roughly a <c>--numstat</c> payload for 200k paths.
    /// </summary>
    /// <remarks>
    /// Whole-change-set patches are capped separately and much lower, by
    /// <see cref="JobChangesOptions.MaxPatchBytes"/>; this bound exists so a pathological repository
    /// cannot make one panel render allocate hundreds of megabytes in the API.
    /// </remarks>
    public const int MaxOutputBytes = 24 * 1024 * 1024;

    /// <summary>Cap on retained stderr, which is only ever used to write a one-line note.</summary>
    public const int MaxErrorBytes = 64 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly int _timeoutSeconds = timeoutSeconds > 0 ? Math.Clamp(timeoutSeconds, 1, 300) : DefaultTimeoutSeconds;

    public async Task<JobGitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new JobGitResult(-1, string.Empty, "no working directory was supplied");
        }

        if (!Directory.Exists(workingDirectory))
        {
            return new JobGitResult(-1, string.Empty, $"the directory '{workingDirectory}' does not exist");
        }

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // `-c` overrides must precede the subcommand.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.fsmonitor=false");
        psi.ArgumentList.Add("core.quotePath=false");
        foreach (var argument in arguments)
        {
            if (string.IsNullOrEmpty(argument)) continue;
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_ASKPASS"] = "echo";
        psi.Environment["SSH_ASKPASS"] = "echo";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_FLUSH"] = "1";

        try
        {
            using var process = new Process { StartInfo = psi };

            if (!process.Start())
            {
                return new JobGitResult(-1, string.Empty, "failed to start git");
            }

            // An empty, closed stdin is the cheapest guarantee that nothing git decides to ask will
            // ever be answered by a terminal that is not there.
            try { process.StandardInput.Close(); } catch { /* already gone */ }

            var readStdout = DrainAsync(process.StandardOutput.BaseStream, MaxOutputBytes, ct);
            var readStderr = DrainAsync(process.StandardError.BaseStream, MaxErrorBytes, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                logger?.LogWarning("git {Command} timed out after {Seconds}s in {Directory}",
                    arguments.Count > 0 ? arguments[0] : "(none)", _timeoutSeconds, workingDirectory);
            }

            // The reads finish when the pipes close, which Kill also causes. A cancelled caller is
            // allowed to see these fail: nothing is left running either way, because the child's
            // streams close when the process is disposed.
            var stdout = await readStdout;
            var stderr = await readStderr;

            if (timedOut)
            {
                return new JobGitResult(-1, stdout.Text, stderr.Text, TimedOut: true);
            }

            if (stdout.Truncated)
            {
                logger?.LogWarning(
                    "git {Command} output exceeded {Limit} bytes in {Directory}; the payload was discarded",
                    arguments.Count > 0 ? arguments[0] : "(none)", MaxOutputBytes, workingDirectory);
            }

            return new JobGitResult(process.ExitCode, stdout.Text, stderr.Text,
                OutputTruncated: stdout.Truncated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing git binary surfaces as an InvalidOperationException from Process.Start; a
            // permissions problem as a Win32Exception. Both belong in a note, not in a 500: the panel
            // has a remote fallback and will use it.
            logger?.LogDebug(ex, "git {Command} could not run in {Directory}",
                arguments.Count > 0 ? arguments[0] : "(none)", workingDirectory);
            return new JobGitResult(-1, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a stream to the end, retaining at most <paramref name="cap"/> bytes.
    /// </summary>
    /// <remarks>
    /// Decoding is done once over the retained prefix rather than per read, because a UTF-8 sequence
    /// straddling a read boundary would otherwise decode to two replacement characters — and a
    /// mangled filename in a diff list is worse than a truncated flag. The byte cap is therefore
    /// approximate by at most one buffered read, which is fine: it bounds memory, not semantics.
    /// </remarks>
    private static async Task<(string Text, bool Truncated)> DrainAsync(
        Stream stream,
        int cap,
        CancellationToken ct)
    {
        var retained = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0L;
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) break;

            total += read;
            if (retained.Length < cap)
            {
                var room = (int)Math.Min(cap - retained.Length, read);
                retained.Write(buffer, 0, room);
                if (room < read) truncated = true;
            }
            else
            {
                truncated = true;
            }
        }

        return (Utf8NoBom.GetString(retained.ToArray()), truncated);
    }
}
