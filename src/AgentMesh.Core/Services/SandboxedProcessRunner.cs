using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// Raw outcome of a sandboxed process run, before the exit protocol and log spillover are applied.
/// </summary>
internal readonly record struct SandboxRunOutcome(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Aborted,
    int ProcessGroupId,
    int ProcessId,
    TimeSpan Elapsed);

/// <summary>
/// Launches a shell command in its own POSIX process group, enforces a wall-clock limit, and guarantees
/// that a killed run leaves no orphans behind.
/// </summary>
/// <remarks>
/// Why this lives in Core rather than the worker: the CLI runner (the path an interactive agent's
/// <c>run_command</c> tool call actually takes) and the Temporal worker activity are two separate hosts that
/// must enforce the <em>same</em> limits. Duplicating teardown logic would let them drift, and drift in a
/// kill path surfaces as leaked processes rather than as a compile error.
///
/// The teardown ladder, in order of reliability:
/// <list type="bullet">
/// <item><c>kill -TERM -- -&lt;pgid&gt;</c>, grace period, then <c>kill -KILL -- -&lt;pgid&gt;</c>. The
/// leading <c>-</c> on the pid is the whole mechanism: it signals every member of the group, which is the
/// only way to reach a backgrounded fork whose immediate parent already exited and reparented it to init.</item>
/// <item><c>pkill -9 -P &lt;pid&gt;</c> for the no-setsid fallback path.</item>
/// <item><see cref="Process.Kill(bool)"/>(entireProcessTree: true). It walks <em>descendants</em>, so it
/// misses anything already reparented — necessary but not sufficient.</item>
/// </list>
///
/// Four hazards, each measured on the target container rather than assumed:
/// <list type="number">
/// <item><c>/bin/kill -TERM -123</c> (no <c>--</c>) exits 0 and kills <em>nothing</em>: util-linux parses
/// <c>-TERM</c> as a combined option-and-operand. The <c>--</c> separator is mandatory, not cosmetic, and
/// the failure is silent.</item>
/// <item>A child that failed to become a group leader still sits in its parent's group. In a container where
/// the worker process itself is pgid 1, "kill my group" would mean "kill the worker". Group teardown is
/// therefore enabled only after <c>/proc/&lt;pid&gt;/stat</c> confirms <c>pgid == pid</c>.</item>
/// <item>Reads on a SIGKILLed pipe raise <see cref="IOException"/> rather than returning EOF, so the stream
/// readers treat a broken pipe as "keep what you have" instead of losing the partial capture.</item>
/// <item>A <see cref="CancellationTokenRegistration"/> callback runs on a thread-pool thread, so a caller
/// that resumes from <c>OperationCanceledException</c> can observe the run as "finished" while the kill is
/// still in flight. Teardown completion is therefore signalled explicitly and awaited.</item>
/// </list>
/// </remarks>
internal static class SandboxedProcessRunner
{
    private static readonly string? SetsidPath = ResolveSetsid();

    /// <summary>
    /// True when this host can run each command in its own process group, which is what makes group-level
    /// teardown of backgrounded forks possible.
    /// </summary>
    /// <remarks>
    /// Exposed rather than kept private because the guarantee is environment-dependent: an image without
    /// util-linux silently loses the strongest teardown stage, and a caller (or a test) needs to be able to
    /// state which contract it is actually exercising instead of assuming one.
    /// </remarks>
    internal static bool SupportsProcessGroupIsolation => IsPosix() && SetsidPath != null;

    /// <summary>Diagnostics only: the resolved <c>setsid</c> path, or null.</summary>
    internal static string? ResolvedSetsidPath => SetsidPath;

    /// <summary>
    /// Runs <paramref name="command"/> under <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// A timeout is reported as a result (<see cref="SandboxRunOutcome.TimedOut"/>), never as an exception:
    /// an agent that cannot see the partial stream has nothing to reason about. External cancellation
    /// propagates as <see cref="OperationCanceledException"/> so the caller's turn is cancelled honestly.
    /// </remarks>
    /// <param name="command">Command line handed to <c>bash -c</c>.</param>
    /// <param name="workingDirectory">Existing working directory.</param>
    /// <param name="timeout">Wall-clock limit. Callers must pass an already-clamped value.</param>
    /// <param name="ct">External cancellation (operator stop, host shutdown).</param>
    /// <param name="onOutputChunk">Optional streaming sink for the dashboard's live tail.</param>
    /// <param name="log">Diagnostics sink.</param>
    /// <param name="abortToken">
    /// Optional internal cancellation source (the operator "stop this command" button). Cancelling it is
    /// reported as <see cref="SandboxRunOutcome.Aborted"/> rather than propagated, because the command was
    /// meant to stop and the caller still needs the partial output.
    /// </param>
    /// <param name="onStarted">
    /// Invoked immediately after <c>Start()</c> with the live handle and the group-leader pid (0 when the
    /// child does not lead its own group). Lets a host register the command for an operator abort without
    /// the engine knowing anything about registries.
    /// </param>
    internal static async Task<SandboxRunOutcome> RunAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        Func<string, Task>? onOutputChunk = null,
        Action<string>? log = null,
        CancellationToken abortToken = default,
        Action<Process, int>? onStarted = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var isWindows = !IsPosix();

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isWindows)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else if (SetsidPath != null)
        {
            // `setsid --wait` puts the shell in a new session whose pid is also its pgid, and --wait makes
            // setsid exit with the child's status so real exit codes still propagate. Verified here:
            // `ps -o pid,pgid,sid` reports all three equal, and `exit 7` surfaces as 7.
            psi.FileName = SetsidPath;
            psi.ArgumentList.Add("--wait");
            psi.ArgumentList.Add("/bin/bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            // Minimal image without util-linux: teardown degrades to pkill -P + Kill(entireProcessTree),
            // which still reaches direct children but cannot signal a detached group.
            psi.FileName = "/bin/bash";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        using var proc = new Process { StartInfo = psi };
        var stdOutSb = new StringBuilder();
        var stdErrSb = new StringBuilder();
        var groupLeaderPid = 0;

        // One linked source for all three reasons a run can end early. CancelAfter gives us the wall-clock
        // limit without a competing Task.Delay that would keep a timer alive after a normal exit.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct, abortToken);

        // Signalled by the teardown callback so the awaiting caller can prove the group is dead before it
        // reports the run as finished. Without this, "returned in 2s" and "left no orphans" can both be
        // true at different instants, and the orphan check in a test would be a coin flip.
        var teardown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Registered before Start(): cancellation arriving mid-launch must still tear the tree down.
        using var reg = runCts.Token.Register(() =>
        {
            try
            {
                KillTree(proc, groupLeaderPid, log);
            }
            finally
            {
                try { teardown.TrySetResult(true); } catch { }
            }
        });

        runCts.CancelAfter(timeout);
        proc.Start();

        // Resolve the group leader immediately after Start(): the value is fixed for the process lifetime,
        // and the guard it provides (never signal a group we do not lead) is the reason this is safe at all.
        if (!isWindows)
        {
            groupLeaderPid = await ReadOwnProcessGroupIfLeaderAsync(proc.Id);
        }

        if (onStarted != null)
        {
            try
            {
                onStarted(proc, groupLeaderPid);
            }
            catch (Exception ex)
            {
                // A host that fails to file its own bookkeeping must not lose a running command.
                log?.Invoke($"onStarted callback threw: {ex.Message}");
            }
        }

        async Task ReadStreamAsync(StreamReader reader, StringBuilder sb)
        {
            var buffer = new char[2048];
            while (!runCts.Token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), runCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // The group was SIGKILLed mid-read, so the pipe breaks. Whatever we captured stands.
                    break;
                }

                if (read <= 0) break;

                var chunk = new string(buffer, 0, read);
                lock (sb)
                {
                    sb.Append(chunk);
                }

                if (onOutputChunk != null)
                {
                    try { await onOutputChunk(chunk); } catch { }
                }
            }
        }

        var stdOutTask = ReadStreamAsync(proc.StandardOutput, stdOutSb);
        var stdErrTask = ReadStreamAsync(proc.StandardError, stdErrSb);
        var readers = Task.WhenAll(stdOutTask, stdErrTask);

        var timedOut = false;
        var aborted = false;
        var externallyCancelled = false;

        try
        {
            await proc.WaitForExitAsync(runCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Three ways this token fires. Order matters: a run that hits its limit and is aborted in the
            // same instant should report the timeout, because that is the failure the agent can act on.
            timedOut = !ct.IsCancellationRequested && !abortToken.IsCancellationRequested;
            aborted = !timedOut && abortToken.IsCancellationRequested;
            externallyCancelled = !timedOut && !aborted;

            // The registration callback does the killing on a thread-pool thread; wait for it to land so
            // the outcome we return describes a dead tree, not a kill that is merely in flight.
            if (!await WaitForTeardownAsync(teardown, TimeSpan.FromSeconds(6)))
            {
                log?.Invoke($"Teardown did not signal completion for pid {proc.Id}; forcing one more pass.");
            }

            // Run one more pass with the *resolved* group pid. The callback above may have fired inside the
            // window between Start() and the pgid read, in which case it could only reach descendants.
            // KillTree is idempotent and returns as soon as the group is provably empty, so this costs one
            // pgrep in the normal case.
            KillTree(proc, groupLeaderPid, log);
        }

        if (timedOut || aborted || externallyCancelled)
        {
            // Drain whatever the kernel had already buffered: the partial stream is the diagnostic the agent
            // needs to reason about *where* the run stalled. Bounded so a wedged pipe reader cannot hold the
            // turn open past the kill.
            var winner = await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(2)));
            if (winner != readers)
            {
                log?.Invoke("Stream readers did not drain within 2s after teardown; returning partial capture.");
            }

            if (externallyCancelled)
            {
                // The caller's turn was cancelled: propagate honestly rather than inventing a result.
                throw new OperationCanceledException(ct);
            }

            return new SandboxRunOutcome(
                timedOut ? CommandExecutionPolicy.TimeoutExitCode : 0,
                Snapshot(stdOutSb),
                Snapshot(stdErrSb),
                timedOut,
                aborted,
                groupLeaderPid,
                proc.Id,
                DateTimeOffset.UtcNow - startedAt);
        }

        await readers;

        return new SandboxRunOutcome(
            proc.ExitCode,
            Snapshot(stdOutSb),
            Snapshot(stdErrSb),
            false,
            false,
            groupLeaderPid,
            proc.Id,
            DateTimeOffset.UtcNow - startedAt);
    }

    private static async Task<bool> WaitForTeardownAsync(TaskCompletionSource<bool> teardown, TimeSpan bound)
    {
        try
        {
            using var cts = new CancellationTokenSource(bound);
            var completed = await Task.WhenAny(teardown.Task, Task.Delay(bound, cts.Token));
            cts.Cancel(); // do not leak the timer for the life of the parent token
            return completed == teardown.Task;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the full teardown ladder. Idempotent, safe to call from a cancellation callback, and never
    /// throws: a teardown path that throws is worse than one that leaks.
    /// </summary>
    internal static void KillTree(
        Process proc,
        int groupLeaderPid,
        Action<string>? log = null)
    {
        var pid = 0;
        try { pid = proc.Id; } catch { }

        try
        {
            // A dead parent tells us nothing about its forks: the group may still hold backgrounded
            // children, so we must not early-return just because HasExited is true.
            if (proc.HasExited && groupLeaderPid <= 1) return;
        }
        catch { }

        if (IsPosix() && pid > 1)
        {
            // Stage 1: the group, but only once we proved the child leads one. This is the stage that
            // catches backgrounded forks, and the guard is what stops it from killing the host.
            if (groupLeaderPid > 1)
            {
                SignalGroup(groupLeaderPid, "-TERM");

                var deadline = Environment.TickCount64
                    + (long)CommandExecutionPolicy.TermGrace.TotalMilliseconds;

                while (Environment.TickCount64 < deadline)
                {
                    if (!ProcessGroupExists(groupLeaderPid)) return;
                    Thread.Sleep(50);
                }

                if (ProcessGroupExists(groupLeaderPid))
                {
                    log?.Invoke($"Process group {groupLeaderPid} ignored SIGTERM within " +
                        $"{CommandExecutionPolicy.TermGrace.TotalSeconds:F0}s; escalating to SIGKILL.");
                    SignalGroup(groupLeaderPid, "-KILL");
                }

                if (!ProcessGroupExists(groupLeaderPid)) return;
            }

            // Stage 2: direct children by ppid, for the fallback path where there is no dedicated group.
            SignalDirectChildren(pid);
        }

        // Stage 3: .NET's own tree walk. Catches descendants still attached, and always reaps the handle we
        // own so the caller's `using var proc` cannot block on an unwaited child.
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Kill(entireProcessTree) failed for pid {pid}: {ex.Message}");
        }

        if (IsPosix() && pid > 1)
        {
            SignalPid(pid, "-KILL");
        }

        try { proc.StandardInput?.Close(); } catch { }
    }

    private static string Snapshot(StringBuilder sb)
    {
        lock (sb)
        {
            return sb.ToString().Trim();
        }
    }

    private static bool IsPosix() => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string? ResolveSetsid()
    {
        if (!IsPosix()) return null;

        foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
        {
            if (File.Exists(candidate)) return candidate;
        }

        // PATH lookup, for images that ship util-linux somewhere unusual.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), "setsid");
                if (File.Exists(full)) return full;
            }
            catch
            {
                // A malformed PATH entry must not take the runner down.
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the child's process group, returning the pid only once it is provably its own group leader.
    /// </summary>
    /// <remarks>
    /// <c>setsid</c> calls <c>setsid()</c> in the child after <c>fork</c>, so a single read taken the instant
    /// <c>Start()</c> returns can still show the <em>parent's</em> group. Measured on this container, the
    /// window is 0.3–5.5 ms. Polling is bounded and exits at the first read that confirms leadership, so the
    /// common case costs one file read.
    ///
    /// If the window elapses without confirmation we return 0, which disables stage 1 and leaves teardown to
    /// <c>pkill -P</c> plus <c>Kill(entireProcessTree)</c>. That is the correct failure direction: guessing a
    /// pgid we did not confirm risks signalling the worker's own group, and in a container where the worker
    /// is pgid 1 that is a self-kill.
    /// </remarks>
    private static async Task<int> ReadOwnProcessGroupIfLeaderAsync(int pid)
    {
        var deadline = Environment.TickCount64 + 50;

        while (true)
        {
            var pgid = ReadProcessGroup(pid);

            if (pgid == pid) return pid;

            // The child is already gone (a fast `echo`), or /proc is unreadable: there is no group to own.
            if (pgid == ProcessGone) return 0;

            if (Environment.TickCount64 >= deadline) return 0;

            await Task.Delay(1);
        }
    }

    private const int ProcessGone = -1;

    /// <summary>
    /// Reads <c>/proc/&lt;pid&gt;/stat</c> and returns the process group id, or <see cref="ProcessGone"/>.
    /// </summary>
    /// <remarks>
    /// The <c>comm</c> field is parenthesised and may itself contain spaces and parentheses, so the parse
    /// anchors on the <em>last</em> ')' instead of splitting the raw line.
    /// </remarks>
    private static int ReadProcessGroup(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var closeParen = stat.LastIndexOf(')');
            if (closeParen < 0 || closeParen + 2 >= stat.Length) return ProcessGone;

            var fields = stat[(closeParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Field order after "comm)": state=0 ppid=1 pgid=2 sid=3
            if (fields.Length < 3) return ProcessGone;
            if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgid)) return ProcessGone;

            return pgid;
        }
        catch
        {
            return ProcessGone;
        }
    }

    /// <summary>
    /// Sends <paramref name="signal"/> to every process in <paramref name="pgid"/>.
    /// </summary>
    /// <remarks>
    /// The <c>--</c> is load-bearing: without it, util-linux reads <c>-TERM</c> as an option-with-value and
    /// exits 0 having killed nothing. A negative operand after <c>--</c> is unambiguously a process group.
    /// </remarks>
    private static bool SignalGroup(int pgid, string signal)
        => RunKill(signal, "--", "-" + pgid.ToString(CultureInfo.InvariantCulture));

    private static bool SignalPid(int pid, string signal)
        => RunKill(signal, "--", pid.ToString(CultureInfo.InvariantCulture));

    private static void SignalDirectChildren(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("pkill")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-9");
            psi.ArgumentList.Add("-P");
            psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));

            using var pkill = Process.Start(psi);
            pkill?.WaitForExit(500);
        }
        catch
        {
            // pkill absent: stage 3 still covers direct descendants.
        }
    }

    private static bool RunKill(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/kill")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var killProc = Process.Start(psi);
            if (killProc == null) return false;

            killProc.WaitForExit(1000);
            return killProc.HasExited && killProc.ExitCode == 0;
        }
        catch
        {
            // ESRCH ("No such process") is the success case for a group that already exited.
            return false;
        }
    }

    /// <summary>
    /// True when any live process still belongs to <paramref name="pgid"/>. Lets the TERM/KILL grace loop
    /// exit as soon as the group is empty instead of always burning the full 2 seconds.
    /// </summary>
    private static bool ProcessGroupExists(int pgid)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pgrep")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add(pgid.ToString(CultureInfo.InvariantCulture));

            using var pgrep = Process.Start(psi);
            if (pgrep == null) return true; // cannot prove it empty, so assume it is not

            pgrep.WaitForExit(1000);
            if (!pgrep.HasExited) return true;

            // pgrep(1): 0 = matches, 1 = no matches, >=2 = error. Only "no matches" counts as clean.
            return pgrep.ExitCode == 0 || pgrep.ExitCode >= 2;
        }
        catch
        {
            // pgrep missing: assume the group exists so we still escalate to SIGKILL.
            return true;
        }
    }
}
