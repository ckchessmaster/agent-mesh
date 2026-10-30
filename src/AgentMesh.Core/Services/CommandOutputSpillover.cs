using System.Text;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Services;

/// <summary>
/// The decision <see cref="CommandOutputSpillover"/> makes for a given stream: either hand it back whole,
/// or hand back a preview and point at the file that holds the rest.
/// </summary>
/// <param name="Spilled">True when the stream crossed a threshold and a file was written.</param>
/// <param name="Text">Inline output when <paramref name="Spilled"/> is false; otherwise the preview payload.</param>
/// <param name="LogFilePath">The file holding 100% of the raw stream; null when nothing was spilled.</param>
/// <param name="TotalLines">Line count of the raw stream.</param>
/// <param name="TotalBytes">UTF-8 byte count of the raw stream.</param>
/// <param name="OmittedLines">Lines present in the file but absent from the preview.</param>
/// <param name="OmittedBytes">Bytes present in the file but absent from the preview.</param>
public readonly record struct SpilloverResult(
    bool Spilled,
    string Text,
    string? LogFilePath,
    int TotalLines,
    int TotalBytes,
    int OmittedLines,
    int OmittedBytes);

/// <summary>
/// Non-destructive log spillover: the model sees a preview, the disk keeps everything.
/// </summary>
/// <remarks>
/// The naive fix for context flooding is to cut the middle of the stream, which is exactly backwards for
/// build and test output: the first compiler error usually sits near the top and the failure summary
/// ("Tests failed: 3", "Build FAILED") is always at the bottom. Keeping a head window and a tail window
/// preserves both, and writing the untouched stream to a file means the omitted span is one <c>grep</c>
/// away rather than permanently gone.
///
/// Lives in Core rather than the worker because the CLI runner and the Temporal activity are two separate
/// callers of one policy; a second copy would let the thresholds drift between runtimes.
/// </remarks>
public static class CommandOutputSpillover
{
    /// <summary>
    /// Spill destination root. Overridable for tests and for sandboxes where <c>/tmp</c> is not writable;
    /// falls back to the per-user temp path if the configured root cannot be created.
    /// </summary>
    public static string RunsDirectory
    {
        get => _runsDirectory ?? Environment.GetEnvironmentVariable("AGENTMESH_RUNS_DIR") ?? DefaultRunsDirectory;
        set => _runsDirectory = value;
    }

    private static string? _runsDirectory;

    private const string LastRunFileName = "last_run.log";

    private static string DefaultRunsDirectory =>
        Path.Combine(Path.GetTempPath(), "agentmesh", "runs");

    /// <summary>
    /// True when <paramref name="combined"/> is large enough that returning it inline would cost more context
    /// than it is worth. The thresholds are strict ("exceeds"), matching the job spec: 61 lines or 4097 bytes
    /// spills; exactly 60 lines and 4096 bytes does not.
    /// </summary>
    public static bool ExceedsThreshold(string combined, out int lineCount, out int byteCount)
    {
        lineCount = CountLines(combined);
        byteCount = string.IsNullOrEmpty(combined) ? 0 : Encoding.UTF8.GetByteCount(combined);
        return lineCount > CommandExecutionPolicy.SpillLineThreshold
            || byteCount > CommandExecutionPolicy.SpillByteThreshold;
    }

    /// <summary>
    /// Writes the full stream to <c>{RunsDirectory}/run_{executionId}.log</c>, refreshes
    /// <c>{RunsDirectory}/last_run.log</c>, and returns the payload to show the model.
    /// </summary>
    /// <param name="combined">The raw, already-merged stdout+stderr stream. Never mutated.</param>
    /// <param name="executionId">Stable identifier used for the per-run file name.</param>
    /// <param name="ct">Cancellation token for the file writes.</param>
    public static async Task<SpilloverResult> ApplyAsync(
        string combined,
        string executionId,
        CancellationToken ct = default)
    {
        combined ??= string.Empty;

        var totalBytes = string.IsNullOrEmpty(combined) ? 0 : Encoding.UTF8.GetByteCount(combined);

        if (!ExceedsThreshold(combined, out var totalLines, out _))
        {
            return new SpilloverResult(false, combined, null, totalLines, totalBytes, 0, 0);
        }

        string? logPath;
        try
        {
            logPath = await WriteAsync(combined, executionId, ct);
        }
        catch (Exception)
        {
            // Spillover is a convenience, not a precondition. If the disk is full or read-only we must not
            // turn a successful command into a failed one: still shrink the payload, but say where the file
            // was supposed to be.
            logPath = null;
        }

        var preview = BuildPreview(combined, logPath, out var omittedLines, out var omittedBytes);

        return new SpilloverResult(
            Spilled: true,
            Text: preview,
            LogFilePath: logPath,
            TotalLines: totalLines,
            TotalBytes: totalBytes,
            OmittedLines: omittedLines,
            OmittedBytes: omittedBytes);
    }

    /// <summary>
    /// Builds the head/tail payload. Exposed so the windowing arithmetic is unit-testable without touching
    /// the filesystem.
    /// </summary>
    /// <param name="combined">The raw stream.</param>
    /// <param name="logFilePath">Path to quote in the banner; null when the write failed.</param>
    /// <param name="omittedLines">Receives the number of lines dropped from the middle.</param>
    /// <param name="omittedBytes">Receives the UTF-8 size of those dropped lines.</param>
    public static string BuildPreview(
        string combined,
        string? logFilePath,
        out int omittedLines,
        out int omittedBytes)
    {
        combined ??= string.Empty;
        var lines = SplitLines(combined);
        var headCount = CommandExecutionPolicy.PreviewHeadLines;
        var tailCount = CommandExecutionPolicy.PreviewTailLines;

        // When the write failed there is no file to point at, and telling the model to grep a path that
        // does not exist is worse than saying nothing: it sends the agent on a dead-end tool call.
        var hint = logFilePath == null
            ? string.Empty
            : $"\n\nHint: The complete output was saved to {logFilePath}. " +
              "Use 'grep', 'sed', or 'read_file' to inspect specific sections if the failure details above are insufficient.";

        // A stream short enough to fit inside both windows has nothing to omit — typically the byte
        // threshold tripping on a few very long lines rather than the line threshold. There is no middle to
        // cut, so the body is the whole stream; it still gets the pointer, because the caller's contract is
        // "if we spilled, say where".
        if (lines.Length <= headCount + tailCount)
        {
            omittedLines = 0;
            omittedBytes = 0;

            return EnforceByteBudget(combined + hint, hint, logFilePath);
        }

        var head = lines[..headCount];
        var tail = lines[^tailCount..];

        var headText = string.Join('\n', head);
        var tailText = string.Join('\n', tail);

        omittedLines = lines.Length - headCount - tailCount;

        // Derived from the real stream rather than from the rendered preview: "bytes not represented in the
        // payload" is the number the banner is claiming, and subtracting the two kept windows is the only
        // way to get it exactly (counting the middle's own separators and the newline that closed the file
        // is easy to get off by one, and the banner would then understate what it hid).
        omittedBytes = Math.Max(0,
            Encoding.UTF8.GetByteCount(combined)
            - Encoding.UTF8.GetByteCount(headText)
            - Encoding.UTF8.GetByteCount(tailText));

        var savedTo = logFilePath ?? "a log file that could not be written (disk full or read-only)";
        var banner = $"\n\n[... Omitted {omittedLines} lines ({omittedBytes} bytes). " +
            $"Full output saved to {savedTo} ...]";

        var body = headText + banner + "\n\n" + tailText;

        return EnforceByteBudget(body, hint, logFilePath);
    }

    /// <summary>
    /// Keeps the payload inside <see cref="CommandExecutionPolicy.PreviewByteBudget"/> without ever hiding
    /// the pointer to the full log.
    /// </summary>
    /// <remarks>
    /// The line windows bound a preview made of ordinary lines, but not one made of pathological lines: a
    /// minified bundle or a base64 blob can be a megabyte across four lines and still fit in "20 head + 30
    /// tail". Strategy, cheapest-first:
    /// <list type="number">
    /// <item>Clamp individual over-long lines, so all 50 window lines survive and the reader still sees the
    /// shape of the run.</item>
    /// <item>If that is still not enough, hard-truncate the body.</item>
    /// </list>
    /// The hint is re-appended after either step rather than budgeted inside the body, because a preview
    /// that lost "the full output is at &lt;path&gt;" would turn a context problem into a debugging dead end.
    /// </remarks>
    private static string EnforceByteBudget(string body, string hint, string? logFilePath)
    {
        var budget = CommandExecutionPolicy.PreviewByteBudget;
        var hintBytes = Encoding.UTF8.GetByteCount(hint);

        if (Encoding.UTF8.GetByteCount(body) + hintBytes <= budget)
        {
            return body + hint;
        }

        var bodyBudget = Math.Max(512, budget - hintBytes);
        var lines = SplitLines(body);

        // Pass 1: clamp the long lines. The banner and hint lines carry the file path and the omission
        // counts, so they are left intact; clamping them would corrupt the very pointer this exists to keep.
        var clamped = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            clamped.Add(IsStructuralLine(line, logFilePath) ? line : ClampLine(line, PerLineBudget(lines.Length, bodyBudget)));
        }

        var candidate = string.Join('\n', clamped);
        if (Encoding.UTF8.GetByteCount(candidate) <= bodyBudget)
        {
            return candidate + hint;
        }

        // Pass 2: the windows themselves are too big to keep intact. Truncate, but keep the head of the
        // body: the first error is more often near the top than the tail summary is near the bottom, and the
        // hint still points at everything.
        var shortened = logFilePath == null
            ? "\n\n[... preview shortened to fit the context budget; the full stream could not be saved ...]"
            : $"\n\n[... preview shortened to fit the context budget; every line remains in {logFilePath} ...]";

        return TruncateToBytes(candidate, Math.Max(0, bodyBudget - Encoding.UTF8.GetByteCount(shortened)))
            + shortened
            + hint;
    }

    /// <summary>
    /// Per-line allowance. Divides the budget across the window lines and leaves headroom for the clamp
    /// marker, floored so a 50-line window still shows something readable per line.
    /// </summary>
    private static int PerLineBudget(int lineCount, int bodyBudget)
        => Math.Max(120, bodyBudget / Math.Max(1, lineCount) - 64);

    private static bool IsStructuralLine(string line, string? logFilePath)
        => line.StartsWith("[... ", StringComparison.Ordinal)
        || line.StartsWith("Hint:", StringComparison.Ordinal)
        || (logFilePath != null && line.Contains(logFilePath, StringComparison.Ordinal));

    private static string ClampLine(string line, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(line) <= maxBytes) return line;

        const string marker = " …[line truncated]";
        var limit = Math.Max(0, maxBytes - Encoding.UTF8.GetByteCount(marker));

        // Walk by Unicode scalar (Rune), not by char or byte, so a surrogate pair or a multi-byte
        // sequence is never split into a replacement character.
        var bytes = 0;
        var builder = new StringBuilder(Math.Min(line.Length, limit));
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > limit) break;
            builder.Append(rune);
            bytes += size;
        }

        return builder.Append(marker).ToString();
    }

    /// <summary>
    /// Cuts to a UTF-8 byte budget without splitting a text element.
    /// </summary>
    private static string TruncateToBytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;

        var bytes = 0;
        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        foreach (var rune in value.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes) break;
            builder.Append(rune);
            bytes += size;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Convenience overload for callers that only need the text.
    /// </summary>
    public static string BuildPreview(string combined, string? logFilePath)
        => BuildPreview(combined, logFilePath, out _, out _);

    /// <summary>
    /// Writes <paramref name="combined"/> to <c>run_{executionId}.log</c> and mirrors it to
    /// <c>last_run.log</c>. Returns the per-run path.
    /// </summary>
    public static async Task<string> WriteAsync(string combined, string executionId, CancellationToken ct = default)
    {
        var dir = EnsureRunsDirectory();
        var safeId = SanitizeExecutionId(executionId);

        var runPath = Path.Combine(dir, $"run_{safeId}.log");
        await File.WriteAllTextAsync(runPath, combined ?? string.Empty, new UTF8Encoding(false), ct);

        var lastPath = MirrorToLastRun(dir, runPath);
        await EnsureLastRunResolvesAsync(lastPath, combined ?? string.Empty, ct);

        return runPath;
    }

    private static string MirrorToLastRun(string dir, string runPath)
    {
        var lastPath = Path.Combine(dir, LastRunFileName);
        try
        {
            if (File.Exists(lastPath) || Directory.Exists(lastPath))
            {
                File.Delete(lastPath);
            }

            // A symlink is free and atomic to re-point, and it means `tail -f last_run.log` follows the
            // newest run without the caller having to re-resolve anything.
            File.CreateSymbolicLink(lastPath, runPath);
        }
        catch (Exception)
        {
            // No symlink privilege (unprivileged container, Windows without dev mode): the copy below
            // keeps the contract intact.
        }

        return lastPath;
    }

    private static async Task EnsureLastRunResolvesAsync(string lastPath, string combined, CancellationToken ct)
    {
        try
        {
            // A working symlink already resolves to the bytes we just wrote; only pay for a full copy when
            // it does not.
            if (File.Exists(lastPath) && new FileInfo(lastPath).Length == Encoding.UTF8.GetByteCount(combined))
            {
                return;
            }
        }
        catch
        {
            // Treat an unreadable stat as "needs a copy".
        }

        try
        {
            await File.WriteAllTextAsync(lastPath, combined, new UTF8Encoding(false), ct);
        }
        catch
        {
            // last_run.log is best-effort; run_{id}.log is the durable artifact and already succeeded.
        }
    }

    /// <summary>
    /// Resolves a writable destination for this call only.
    /// </summary>
    /// <remarks>
    /// Deliberately does <em>not</em> cache the fallback into <see cref="_runsDirectory"/>. A transient
    /// ENOSPC or a momentarily read-only mount would otherwise pin every future log to the fallback path for
    /// the life of the process, and the operator would have to restart the worker to get the documented
    /// <c>/tmp/agentmesh/runs</c> location back. Each call re-attempts the configured root and degrades only
    /// for itself.
    /// </remarks>
    private static string EnsureRunsDirectory()
    {
        var dir = RunsDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            if (Directory.Exists(dir)) return dir;
        }
        catch
        {
            // fall through to the per-user temp path
        }

        var fallback = Path.Combine(Path.GetTempPath(), "agentmesh-runs");
        Directory.CreateDirectory(fallback); // if this also throws, ApplyAsync turns it into "no file pointer"
        return fallback;
    }

    private static string SanitizeExecutionId(string? executionId)
    {
        if (string.IsNullOrWhiteSpace(executionId))
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        }

        var chars = executionId.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' ? c : '_')
            .ToArray();

        var value = new string(chars);
        return value.Length <= 64 ? value : value[..64];
    }

    /// <summary>
    /// Splits on \n or \r\n and drops a single trailing newline's phantom empty entry, so "a\nb\n" is 2 lines
    /// rather than 3. That is the count a reader would state while looking at the file.
    /// </summary>
    public static string[] SplitLines(string value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<string>();

        var normalized = value.Replace("\r\n", "\n");
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        // "\n" alone is an empty stream, not a stream containing one blank line.
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        return normalized.Split('\n');
    }

    private static int CountLines(string value) => SplitLines(value).Length;
}
