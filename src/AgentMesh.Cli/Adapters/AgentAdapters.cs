using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Cli.Adapters;

public interface IAgentAdapter
{
    string AdapterType { get; }
    Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct);
}

public static class AgentAdapterFactory
{
    public static IAgentAdapter Create(AgentPresetConfig preset)
    {
        return preset.AdapterType.ToLowerInvariant() switch
        {
            "claude" => new ClaudeAdapter(),
            "copilot" => new CopilotAdapter(),
            "agy" => new AgyAdapter(),
            "openai" or "openai-compatible" or "ollama" or "vllm" => new OpenAiAdapter(),
            _ => new GenericCommandAdapter()
        };
    }

    public static string? ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (File.Exists(command)) return Path.GetFullPath(command);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidateDirs = new List<string>();

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidateDirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        if (!string.IsNullOrWhiteSpace(home))
        {
            candidateDirs.Add(Path.Combine(home, ".local", "bin"));
            candidateDirs.Add(Path.Combine(home, ".gemini", "antigravity-cli", "bin"));
        }

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "", ".exe", ".cmd", ".bat", ".ps1" }
            : new[] { "" };

        foreach (var dir in candidateDirs)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var fullPath = Path.Combine(dir, command + ext);
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch
                {
                    // Ignore invalid path segments
                }
            }
        }

        return null;
    }

    public static bool IsExecutableOnPath(string command) =>
        ResolveExecutablePath(command) != null;

    public static string ResolveWorkspaceDirectory(PromptReceivedEvent? promptEvent = null)
    {
        if (!string.IsNullOrWhiteSpace(promptEvent?.WorkingDirectory))
        {
            return WorkspacePathResolver.Normalize(promptEvent.WorkingDirectory.Trim());
        }

        var repoOrProject = !string.IsNullOrWhiteSpace(promptEvent?.RepositoryName)
            ? promptEvent!.RepositoryName
            : promptEvent?.ProjectId;
        if (!string.IsNullOrWhiteSpace(repoOrProject) &&
            (Directory.Exists("/workspace") || string.Equals(Environment.CurrentDirectory, "/workspace", StringComparison.Ordinal)))
        {
            return WorkspacePathResolver.FromRepositoryName(repoOrProject);
        }

        var envRepo = Environment.GetEnvironmentVariable("AGENTMESH_REPOSITORY_NAME")
                      ?? Environment.GetEnvironmentVariable("AGENTMESH_PROJECT_ID");
        if (!string.IsNullOrWhiteSpace(envRepo) && Directory.Exists("/workspace"))
        {
            return WorkspacePathResolver.FromRepositoryName(envRepo);
        }

        var cwd = Environment.CurrentDirectory;
        if (string.Equals(cwd, "/workspace", StringComparison.Ordinal) ||
            string.Equals(cwd, "/workspace/repo", StringComparison.Ordinal))
        {
            if (Directory.Exists(WorkspacePathResolver.ReposRoot))
            {
                try
                {
                    var subdirs = Directory.GetDirectories(WorkspacePathResolver.ReposRoot);
                    if (subdirs.Length == 1)
                    {
                        return WorkspacePathResolver.Normalize(subdirs[0]);
                    }
                    var defaultRepoDir = Path.Combine(WorkspacePathResolver.ReposRoot, Project.DefaultRepositoryName);
                    if (Directory.Exists(defaultRepoDir))
                    {
                        return WorkspacePathResolver.Normalize(defaultRepoDir);
                    }
                }
                catch { }
            }
        }

        // If user launched the binary from inside dist/cli/linux-x64, walk up to repo root if applicable
        var normalized = cwd.Replace('\\', '/');
        var distIdx = normalized.IndexOf("/dist/cli", StringComparison.OrdinalIgnoreCase);
        if (distIdx > 0)
        {
            var repoRoot = cwd[..distIdx];
            if (Directory.Exists(repoRoot))
            {
                return repoRoot;
            }
        }
        return cwd;
    }

    public static bool TryParseJsonEventLine(string line, out TrajectoryEventPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var tProp)
                ? tProp.GetString()
                : root.TryGetProperty("eventType", out var etProp)
                    ? etProp.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(type)) return false;

            var content = root.TryGetProperty("content", out var cProp)
                ? cProp.GetString() ?? string.Empty
                : string.Empty;

            var toolName = root.TryGetProperty("name", out var nProp)
                ? nProp.GetString()
                : root.TryGetProperty("toolName", out var tnProp)
                    ? tnProp.GetString()
                    : null;

            string? toolArgs = null;
            if (root.TryGetProperty("args", out var aProp))
            {
                toolArgs = aProp.ValueKind == JsonValueKind.String ? aProp.GetString() : aProp.GetRawText();
            }
            else if (root.TryGetProperty("toolArgsJson", out var taProp))
            {
                toolArgs = taProp.ValueKind == JsonValueKind.String ? taProp.GetString() : taProp.GetRawText();
            }

            string? toolResult = null;
            if (root.TryGetProperty("result", out var rProp))
            {
                toolResult = rProp.ValueKind == JsonValueKind.String ? rProp.GetString() : rProp.GetRawText();
            }
            else if (root.TryGetProperty("toolResultJson", out var trProp))
            {
                toolResult = trProp.ValueKind == JsonValueKind.String ? trProp.GetString() : trProp.GetRawText();
            }

            payload = new TrajectoryEventPayload
            {
                EventType = type.ToLowerInvariant(),
                Content = content,
                ToolName = toolName,
                ToolArgsJson = toolArgs,
                ToolResultJson = toolResult
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tears down a process and everything it spawned.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="SandboxShell.KillProcessTree"/> so the solution has exactly one teardown
    /// ladder. The previous local implementation relied on <c>Kill(entireProcessTree)</c> plus
    /// <c>pkill -P</c>, which cannot reach a fork whose parent already exited and was reparented to init —
    /// the exact shape of a hung <c>node</c> test worker still holding a port.
    /// </remarks>
    public static void KillProcessTree(Process proc) => SandboxShell.KillProcessTree(proc);

    /// <summary>
    /// Tears down a process, signalling its whole process group when the caller knows the group leader.
    /// </summary>
    /// <param name="processGroupLeaderPid">
    /// The group-leader pid reported by <see cref="RunCommandAsync"/>, or 0 when unknown. Signalling a group
    /// the caller does not own can take down the worker itself, so 0 deliberately degrades to the
    /// descendant walk rather than guessing.
    /// </param>
    public static void KillProcessTree(Process proc, int processGroupLeaderPid)
        => SandboxShell.KillProcessTree(proc, processGroupLeaderPid);

    /// <summary>
    /// Runs a shell command and returns the hardened result: clamped timeout, process-group teardown on
    /// kill, and non-destructive log spillover.
    /// </summary>
    /// <remarks>
    /// This is the real entry point for a driver's <c>run_command</c> tool call. The engine itself lives in
    /// <see cref="SandboxShell"/> (Core) because the Temporal worker activity must enforce the same limits;
    /// keeping the policy here would let the CLI and the worker drift apart.
    /// </remarks>
    /// <param name="command">Command line for the shell.</param>
    /// <param name="workingDirectory">Existing working directory.</param>
    /// <param name="ct">Turn/host cancellation. Propagates as <see cref="OperationCanceledException"/>.</param>
    /// <param name="timeout">
    /// Legacy internal override. Ignored when <paramref name="timeoutSeconds"/> is supplied; prefer the
    /// latter so every caller goes through the same clamp.
    /// </param>
    /// <param name="sessionId">Session that owns the command; enables the dashboard's per-command stop.</param>
    /// <param name="commandId">Correlation id for the live tail and the stop button.</param>
    /// <param name="onOutputChunk">Streaming sink for the dashboard.</param>
    /// <param name="timeoutSeconds">Untrusted per-invocation timeout from the tool call; clamped by policy.</param>
    /// <param name="trustedTimeout">
    /// Wall-clock limit from in-process code, honoured verbatim instead of clamped. Use for probes and tests
    /// that need a budget outside the untrusted-input range [5,600].
    /// </param>
    /// <param name="log">
    /// Optional sink for teardown diagnostics (SIGTERM escalation, undrained streams). Defaults to
    /// <see cref="System.Diagnostics.Debug"/> because the static adapter factory has no
    /// <see cref="Microsoft.Extensions.Logging.ILogger"/> to hand it, and writing to the console would
    /// corrupt the Spectre.Console TUI.
    /// </param>
    public static async Task<CommandExecutionResult> RunCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken ct,
        TimeSpan? timeout = null,
        Guid? sessionId = null,
        string? commandId = null,
        Func<string, Task>? onOutputChunk = null,
        int? timeoutSeconds = null,
        bool trustedTimeout = false,
        Action<string>? log = null)
    {
        // Precedence: an explicit per-call timeout wins, then a legacy TimeSpan, then the policy default.
        int? requested = timeoutSeconds
            ?? (timeout.HasValue ? (int)Math.Round(timeout.Value.TotalSeconds) : (int?)null);

        // A trusted caller's TimeSpan bypasses the clamp; a value arriving from a tool call does not.
        var args = new RunCommandArgs(
            Command: command,
            WorkingDirectory: workingDirectory,
            TimeoutSeconds: requested);

        var effectiveTrustedTimeout = trustedTimeout ? timeout : null;

        var executionId = sessionId.HasValue
            ? $"{sessionId.Value:N}-{(string.IsNullOrWhiteSpace(commandId) ? "adhoc" : commandId)}"
            : Guid.NewGuid().ToString("N");

        // The operator "stop this command" button cancels this source. It is deliberately *not* the turn
        // token: stopping one command must not cancel the agent's whole turn.
        using var abortCts = new CancellationTokenSource();

        // Assigned from inside the onStarted callback (the group pid does not exist until Start() returns)
        // and disposed here, so the registry entry cannot outlive the call.
        IDisposable? registration = null;

        try
        {
            return await SandboxShell.RunAsync(
                args,
                ct,
                onOutputChunk,
                log: log ?? (message => System.Diagnostics.Debug.WriteLine($"[sandbox] {message}")),
                abortToken: abortCts.Token,
                executionId: executionId,
                trustedTimeout: effectiveTrustedTimeout,
                onStarted: sessionId.HasValue
                    ? (proc, groupLeaderPid) =>
                    {
                        registration = ActiveCommandRegistry.Register(
                            sessionId.Value, commandId, proc, abortCts, groupLeaderPid);
                    }
                    : null);
        }
        finally
        {
            registration?.Dispose();
        }
    }

    /// <summary>
    /// Compatibility shim for adapters that consume <c>(exit, stdout, stderr)</c>.
    /// </summary>
    /// <remarks>
    /// The spillover preview is returned in <c>StdOut</c> so existing callers keep working; the timeout and
    /// abort diagnostics go to <c>StdErr</c>, which is where the adapters already look when a command fails.
    /// Callers that need <see cref="CommandExecutionResult.LogFilePath"/> should use
    /// <see cref="RunCommandAsync"/> directly.
    ///
    /// A <see cref="TimeSpan"/> passed here is treated as trusted: it comes from in-process code (a driver
    /// adapter or a test), not from a model's tool call, so it is honoured verbatim rather than clamped to
    /// the [5,600] untrusted-input range.
    /// </remarks>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunShellCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken ct,
        TimeSpan? timeout = null,
        Guid? sessionId = null,
        string? commandId = null,
        Func<string, Task>? onOutputChunk = null)
    {
        var result = await RunCommandAsync(
            command, workingDirectory, ct, timeout, sessionId, commandId, onOutputChunk,
            trustedTimeout: true);

        // Route on the flags, not on the exit code: 124 is also a legitimate exit status for a command that
        // internally invokes `timeout(1)`, and misclassifying one of those as our own teardown would move
        // real output onto the stderr channel.
        if (result.TimedOut || result.Aborted)
        {
            // The diagnostic is the payload; keep it on stderr so "command failed" paths surface it.
            return (result.ExitCode, string.Empty, result.Output);
        }

        return (result.ExitCode, result.Output, string.Empty);
    }

    public record ExtractedQuestion(string Question, List<string> Options, bool IsMultiSelect);

    public static string FormatAttachedFilesBlock(List<AttachmentItem>? attachments)
    {
        if (attachments == null || attachments.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Attached Files in Workspace]");
        sb.AppendLine("The user attached the following file(s) to this message. They have been staged into the workspace (text files can be inspected using `read_file`, and image attachments are also provided directly to multimodal vision models as visual inputs). Note: If running on a text-only model without vision, do not attempt to script OCR or decode image binaries; ask the user for details if needed:");
        foreach (var att in attachments)
        {
            var path = !string.IsNullOrWhiteSpace(att.LocalFilePath)
                ? att.LocalFilePath
                : Path.Combine(".agentmesh", "attachments", $"{att.Id}_{att.FileName}");
            sb.AppendLine($"- {att.FileName} ({att.ContentType}, {att.SizeBytes} bytes): {path}");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    public static string BuildAgentMeshPromptEnvelope(
        string userPrompt,
        string agentId,
        string? mode,
        string? personaId = null,
        string? personaName = null,
        string? personaRole = null,
        string? personaPrompt = null,
        Guid? sessionId = null,
        List<AttachmentItem>? attachments = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(personaId) || !string.IsNullOrWhiteSpace(personaName))
        {
            var pDisplay = !string.IsNullOrWhiteSpace(personaName) ? personaName : personaId;
            var roleClause = !string.IsNullOrWhiteSpace(personaRole) ? $", Role: {personaRole}" : "";
            sb.AppendLine($"[AgentMesh System Context: You are running on driver @{agentId} as Persona \"{pDisplay}\" (@{personaId ?? agentId}{roleClause}) connected to the interactive AgentMesh Cockpit.]");
        }
        else
        {
            sb.AppendLine($"[AgentMesh System Context: You are running as driver @{agentId} connected to the interactive AgentMesh Cockpit.]");
        }

        if (sessionId.HasValue)
        {
            sb.AppendLine($"[Active Session ID: {sessionId.Value}]");
        }

        if (!string.IsNullOrWhiteSpace(personaId))
        {
            sb.AppendLine($"[Session History Tooling: You can inspect past sessions for this persona anytime using the MCP tool 'get_persona_sessions' with personaId='{personaId}', or 'get_session' to inspect previous session transcripts and artifacts.]");
        }

        if (!string.IsNullOrWhiteSpace(personaPrompt))
        {
            sb.AppendLine($"[Persona System Instructions: {personaPrompt.Trim()}]");
        }

        if (!string.IsNullOrWhiteSpace(mode))
        {
            sb.AppendLine($"[Execution Mode: {mode}]");
        }
        sb.AppendLine("[Interactive Human-in-the-Loop Instructions: You are running in an interactive session. If the user asks you to ask them questions, or if you need clarifications or decisions before proceeding, ask your questions directly. Format questions as a numbered list (e.g. 1. Question? 2. Question?), or output a ```ask_question JSON block with questions array. Do NOT state that this is a non-interactive session or that you cannot see replies. The user will be presented with interactive UI cards to answer your questions and will send their responses in the next turn.]");
        sb.AppendLine();
        var attachedBlock = FormatAttachedFilesBlock(attachments);
        if (!string.IsNullOrWhiteSpace(attachedBlock))
        {
            sb.Append(attachedBlock);
        }
        sb.AppendLine(userPrompt);
        return sb.ToString();
    }

    public static List<ExtractedQuestion> ExtractQuestionsFromOutput(string text)
    {
        var questions = new List<ExtractedQuestion>();
        if (string.IsNullOrWhiteSpace(text)) return questions;

        // Check for ```ask_question or ```json:ask_question block
        var jsonBlockMatch = Regex.Match(text, @"```(?:json:)?ask_question\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (jsonBlockMatch.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonBlockMatch.Groups[1].Value);
                if (doc.RootElement.TryGetProperty("questions", out var qArray) && qArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var qElem in qArray.EnumerateArray())
                    {
                        var qText = qElem.TryGetProperty("question", out var qt) ? qt.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(qText))
                        {
                            var opts = new List<string>();
                            if (qElem.TryGetProperty("options", out var opArr) && opArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var o in opArr.EnumerateArray())
                                {
                                    var optStr = o.GetString();
                                    if (!string.IsNullOrWhiteSpace(optStr)) opts.Add(optStr);
                                }
                            }
                            var multi = qElem.TryGetProperty("is_multi_select", out var ms) && ms.GetBoolean();
                            questions.Add(new ExtractedQuestion(qText, opts, multi));
                        }
                    }
                    if (questions.Count > 0) return questions;
                }
            }
            catch { }
        }

        // Check for numbered question lines: e.g. "1. What is ...?" or "1) How ...?" or "- Question?"
        var numberedMatches = Regex.Matches(text, @"^\s*(?:\d+[\.\)]|[-*])\s+(.+?\?)", RegexOptions.Multiline);
        if (numberedMatches.Count > 0)
        {
            foreach (Match m in numberedMatches)
            {
                var qText = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(qText) && qText.Length > 5)
                {
                    var extractedOpts = new List<string>();
                    var parenMatch = Regex.Match(qText, @"\(([^)]+)\)");
                    if (parenMatch.Success)
                    {
                        var rawChoices = Regex.Split(parenMatch.Groups[1].Value, @"[,/]| or ", RegexOptions.IgnoreCase);
                        foreach (var c in rawChoices)
                        {
                            var trimmed = Regex.Replace(c.Trim(), @"^(?:etc|e\.g\.)\.?$", "", RegexOptions.IgnoreCase).Trim();
                            if (trimmed.Length > 1 && trimmed.Length < 35 && !extractedOpts.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                            {
                                extractedOpts.Add(trimmed);
                            }
                        }
                    }

                    if (extractedOpts.Count == 0 && qText.Contains(" or ", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleaned = qText.TrimEnd('?').Trim();
                        var orParts = Regex.Split(cleaned, @"\s+or\s+", RegexOptions.IgnoreCase);
                        if (orParts.Length >= 2 && orParts.Length <= 4)
                        {
                            foreach (var p in orParts)
                            {
                                var trimmed = p.Trim();
                                if (trimmed.Length > 2 && trimmed.Length < 35 && !extractedOpts.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                                {
                                    extractedOpts.Add(trimmed);
                                }
                            }
                        }
                    }

                    questions.Add(new ExtractedQuestion(qText, extractedOpts, false));
                }
            }
        }

        return questions;
    }
}

/// <summary>
/// A live shell command, so the dashboard's stop button can reach the exact process it wants gone.
/// </summary>
/// <param name="ProcessGroupLeaderPid">
/// The pid to signal *as a process group* when the command leads its own group, else 0. Carrying it here is
/// what lets an operator abort reach backgrounded forks instead of only the shell that spawned them.
/// </param>
public record CommandExecutionHandle(
    Guid SessionId,
    string? CommandId,
    Process Process,
    CancellationTokenSource Cts,
    DateTimeOffset StartedAt,
    StringBuilder? LiveOutput = null,
    int ProcessGroupLeaderPid = 0);

public static class ActiveCommandRegistry
{
    private static readonly ConcurrentDictionary<string, CommandExecutionHandle> ActiveCommands = new();

    public static IDisposable Register(Guid sessionId, string? commandId, Process proc, CancellationTokenSource cts)
        => Register(sessionId, commandId, proc, cts, 0);

    /// <param name="processGroupLeaderPid">
    /// The group leader pid, or 0 when the process does not lead its own group. Supplied by the caller
    /// rather than read here because it only exists once the process has started.
    /// </param>
    public static IDisposable Register(Guid sessionId, string? commandId, Process proc, CancellationTokenSource cts, int processGroupLeaderPid)
    {
        var outputSb = new StringBuilder();
        var handle = new CommandExecutionHandle(sessionId, commandId, proc, cts, DateTimeOffset.UtcNow, outputSb, processGroupLeaderPid);
        var key = $"{sessionId}:{(string.IsNullOrWhiteSpace(commandId) ? "current" : commandId)}";
        ActiveCommands[key] = handle;
        ActiveCommands[$"{sessionId}:current"] = handle;

        return new RegistrationDisposable(() =>
        {
            ActiveCommands.TryRemove(key, out _);
            ActiveCommands.TryRemove($"{sessionId}:current", out _);
        });
    }

    public static void AppendOutput(Guid sessionId, string? commandId, string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        var key = $"{sessionId}:{(string.IsNullOrWhiteSpace(commandId) ? "current" : commandId)}";
        if (ActiveCommands.TryGetValue(key, out var handle) && handle.LiveOutput != null)
        {
            lock (handle.LiveOutput)
            {
                handle.LiveOutput.Append(chunk);
                if (handle.LiveOutput.Length > 200_000)
                {
                    handle.LiveOutput.Remove(0, 50_000);
                }
            }
        }
    }

    public static string? GetLiveOutput(Guid sessionId, string? commandId = null)
    {
        var key = $"{sessionId}:{(string.IsNullOrWhiteSpace(commandId) ? "current" : commandId)}";
        if (ActiveCommands.TryGetValue(key, out var handle) && handle.LiveOutput != null)
        {
            lock (handle.LiveOutput)
            {
                return handle.LiveOutput.ToString();
            }
        }
        return null;
    }

    public static bool TryStopCommand(Guid sessionId, string? commandId = null)
    {
        CommandExecutionHandle? handle = null;
        if (!string.IsNullOrWhiteSpace(commandId) && ActiveCommands.TryGetValue($"{sessionId}:{commandId}", out handle))
        {
        }
        else if (ActiveCommands.TryGetValue($"{sessionId}:current", out handle))
        {
        }

        if (handle != null)
        {
            try
            {
                handle.Cts?.Cancel();
            }
            catch { }

            if (handle.Process != null)
            {
                // Pass the group leader so an operator abort reaches backgrounded forks, not just the shell.
                AgentAdapterFactory.KillProcessTree(handle.Process, handle.ProcessGroupLeaderPid);
            }
            return true;
        }

        return false;
    }

    private sealed class RegistrationDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}

public static class ActiveAgyProcessRegistry
{
    private static readonly ConcurrentDictionary<Guid, Process> Processes = new();

    public static IDisposable Register(Guid sessionId, Process proc)
    {
        Processes[sessionId] = proc;
        return new RegistrationDisposable(() => Processes.TryRemove(sessionId, out _));
    }

    public static bool TryKillChildren(Guid sessionId)
    {
        if (Processes.TryGetValue(sessionId, out var proc) && !proc.HasExited)
        {
            var pid = 0;
            try { pid = proc.Id; } catch { }
            if (pid > 0 && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using var pkill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "pkill",
                        ArgumentList = { "-TERM", "-P", pid.ToString() },
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    pkill?.WaitForExit(500);
                    return true;
                }
                catch { }
            }
        }
        return false;
    }

    private sealed class RegistrationDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}

public class AgyAdapter : IAgentAdapter, IDescribesOwnCapabilities
{
    private static readonly ConcurrentDictionary<Guid, string> SessionConversations = new();
    private static readonly object MappingLock = new();

    public static string? GetSessionConversationId(Guid sessionId)
    {
        if (sessionId == Guid.Empty) return null;
        if (SessionConversations.TryGetValue(sessionId, out var convId) && !string.IsNullOrWhiteSpace(convId))
        {
            return convId;
        }

        LoadPersistedSessionConversations();

        return SessionConversations.TryGetValue(sessionId, out convId) && !string.IsNullOrWhiteSpace(convId) ? convId : null;
    }

    public static void SetSessionConversationId(Guid sessionId, string convId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(convId)) return;
        SessionConversations[sessionId] = convId.Trim();
        PersistSessionConversations();
    }

    private static List<string> GetCandidateMappingFilePaths()
    {
        var list = new List<string>();
        if (Directory.Exists("/workspace"))
        {
            list.Add("/workspace/.agentmesh/session_conversations.json");
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(Path.Combine(userProfile, ".agentmesh", "session_conversations.json"));
        }
        return list;
    }

    private static void LoadPersistedSessionConversations()
    {
        lock (MappingLock)
        {
            var paths = GetCandidateMappingFilePaths();
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict == null) continue;
                    foreach (var (k, v) in dict)
                    {
                        if (Guid.TryParse(k, out var sid) && !string.IsNullOrWhiteSpace(v))
                        {
                            SessionConversations.TryAdd(sid, v.Trim());
                        }
                    }
                }
                catch { }
            }
        }
    }

    private static void PersistSessionConversations()
    {
        lock (MappingLock)
        {
            try
            {
                var dict = SessionConversations.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                var paths = GetCandidateMappingFilePaths();
                foreach (var path in paths)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        var tempFile = path + ".tmp";
                        File.WriteAllText(tempFile, json);
                        File.Move(tempFile, path, overwrite: true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    public static void EnsureBrainRestoredFromWorkspaceState(string convId)
    {
        if (string.IsNullOrWhiteSpace(convId)) return;
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var brainTarget = Path.Combine(userHome, ".gemini", "antigravity-cli", "brain", convId);
            var brainBackup = Path.Combine("/workspace", ".agentmesh", "state", ".gemini", "antigravity-cli", "brain", convId);

            if (!Directory.Exists(brainTarget) && Directory.Exists(brainBackup))
            {
                CopyDirectoryRecursive(brainBackup, brainTarget);
            }

            var dbTarget = Path.Combine(userHome, ".gemini", "antigravity-cli", "conversation_summaries.db");
            var dbBackup = Path.Combine("/workspace", ".agentmesh", "state", ".gemini", "antigravity-cli", "conversation_summaries.db");
            if (!File.Exists(dbTarget) && File.Exists(dbBackup))
            {
                var targetDir = Path.GetDirectoryName(dbTarget);
                if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                File.Copy(dbBackup, dbTarget, overwrite: true);
            }
        }
        catch { }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, dest);
        }
    }

    private static readonly HashSet<string> SupportedBrainArtifactExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".pdf"
    };

    public static async Task SyncBrainArtifactsAsync(Guid sessionId, string convId, MeshClient? client, CancellationToken ct = default)
    {
        if (client == null || string.IsNullOrWhiteSpace(convId)) return;
        try
        {
            var brainDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "brain", convId);
            if (!Directory.Exists(brainDir)) return;

            var files = Directory.GetFiles(brainDir, "*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                var fInfo = new FileInfo(file);
                if (fInfo.Name.StartsWith('.') || fInfo.Name.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = fInfo.Extension.ToLowerInvariant();
                if (!SupportedBrainArtifactExtensions.Contains(ext)) continue;

                string? summary = null;
                var metaFile = file + ".metadata.json";
                if (File.Exists(metaFile))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(metaFile, ct));
                        if (doc.RootElement.TryGetProperty("summary", out var sProp))
                        {
                            summary = sProp.GetString();
                        }
                    }
                    catch { }
                }

                var contentType = ext switch
                {
                    ".md" => "text/markdown",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".svg" => "image/svg+xml",
                    ".pdf" => "application/pdf",
                    _ => "application/octet-stream"
                };

                if (ext is ".md" or ".svg")
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    await client.SyncSessionArtifactAsync(sessionId, fInfo.Name, content, summary, contentType, ct);
                }
                else
                {
                    var bytes = await File.ReadAllBytesAsync(file, ct);
                    var base64 = Convert.ToBase64String(bytes);
                    await client.SyncSessionArtifactAsync(sessionId, fInfo.Name, string.Empty, summary, contentType, ct, base64);
                }
            }
        }
        catch { }
    }

    public string AdapterType => "agy";

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = AgentAdapterFactory.ResolveWorkspaceDirectory(promptEvent);

        var agyPath = (string.Equals(preset.AdapterType, "agy", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(preset.Command))
            ? (AgentAdapterFactory.ResolveExecutablePath(preset.Command) ?? AgentAdapterFactory.ResolveExecutablePath("agy"))
            : AgentAdapterFactory.ResolveExecutablePath("agy");

        if (agyPath != null)
        {
            await ExecuteRealAgyStreamAsync(agyPath, cwd, preset, promptEvent, onEvent, ct);
            return;
        }

        // Fallback only if agy binary is not installed on the system
        await ExecuteFallbackShellAsync(cwd, preset, promptEvent, onEvent, ct);
    }

    public static int GetMaxTranscriptStep(string transcriptPath)
    {
        if (!File.Exists(transcriptPath)) return -1;
        var max = -1;
        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("step_index", out var sProp))
                    {
                        var s = sProp.GetInt32();
                        if (s > max) max = s;
                    }
                }
                catch { }
            }
        }
        catch { }
        return max;
    }

    public static string? GetLatestThoughtFromTranscript(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return null;
        try
        {
            var lines = new List<string>();
            using (var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                string? l;
                while ((l = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(l)) lines.Add(l);
                }
            }

            lines.Reverse();
            foreach (var line in lines.Take(40))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("thinking", out var thinkingProp) && thinkingProp.ValueKind == JsonValueKind.String)
                    {
                        var thinking = thinkingProp.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(thinking))
                        {
                            var clean = Regex.Replace(thinking, @"\s+", " ");
                            return clean.Length > 250 ? clean[..247] + "..." : clean;
                        }
                    }
                    if (root.TryGetProperty("content", out var contentProp) &&
                        contentProp.ValueKind == JsonValueKind.String &&
                        root.TryGetProperty("type", out var typeProp) &&
                        typeProp.GetString() == "PLANNER_RESPONSE")
                    {
                        var content = contentProp.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            var clean = Regex.Replace(content, @"\s+", " ");
                            return clean.Length > 250 ? clean[..247] + "..." : clean;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    public record AskQuestionDetection(TrajectoryEventPayload ToolCallEvent, string? PriorResponseContent)
    {
        public string EventType => ToolCallEvent.EventType;
        public string? ToolName => ToolCallEvent.ToolName;
        public string? Content => ToolCallEvent.Content;
        public string? ToolArgsJson => ToolCallEvent.ToolArgsJson;
    }

    private static string? TryReadStepContentFromFullTranscript(string fullPath, int targetStepIndex)
    {
        if (!File.Exists(fullPath)) return null;
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("step_index", out var sProp) && sProp.GetInt32() == targetStepIndex)
                    {
                        if (doc.RootElement.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                        {
                            return cProp.GetString();
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    public static AskQuestionDetection? CheckForAskQuestionToolCall(string transcriptPath, ref int lastProcessedStep)
    {
        if (!File.Exists(transcriptPath)) return null;

        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("step_index", out var sProp))
                    {
                        var stepIdx = sProp.GetInt32();
                        if (stepIdx > lastProcessedStep)
                        {
                            if (root.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var tc in tcProp.EnumerateArray())
                                {
                                    var name = tc.TryGetProperty("name", out var n) ? n.GetString() : null;
                                    if (string.Equals(name, "ask_question", StringComparison.OrdinalIgnoreCase))
                                    {
                                        lastProcessedStep = stepIdx;
                                        string? argsJson = null;
                                        var summary = "Asking question";
                                        if (tc.TryGetProperty("args", out var aProp))
                                        {
                                            argsJson = aProp.ValueKind == JsonValueKind.String ? aProp.GetString() : aProp.GetRawText();
                                            if (aProp.ValueKind == JsonValueKind.Object)
                                            {
                                                if (aProp.TryGetProperty("toolSummary", out var ts))
                                                {
                                                    var s = ts.GetString()?.Trim('\"');
                                                    if (!string.IsNullOrWhiteSpace(s)) summary = s;
                                                }
                                                else if (aProp.TryGetProperty("toolAction", out var ta))
                                                {
                                                    var a = ta.GetString()?.Trim('\"');
                                                    if (!string.IsNullOrWhiteSpace(a)) summary = a;
                                                }
                                            }
                                        }

                                        string? priorContent = null;
                                        if (root.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                                        {
                                            priorContent = cProp.GetString();
                                        }

                                        if (root.TryGetProperty("truncated_fields", out var tfProp) && tfProp.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var tf in tfProp.EnumerateArray())
                                            {
                                                if (tf.GetString() == "content")
                                                {
                                                    var fullPath = transcriptPath.Replace("transcript.jsonl", "transcript_full.jsonl");
                                                    var fullContent = TryReadStepContentFromFullTranscript(fullPath, stepIdx);
                                                    if (!string.IsNullOrWhiteSpace(fullContent))
                                                    {
                                                        priorContent = fullContent;
                                                    }
                                                    break;
                                                }
                                            }
                                        }

                                        return new AskQuestionDetection(
                                            new TrajectoryEventPayload
                                            {
                                                EventType = "tool_call",
                                                ToolName = "ask_question",
                                                Content = summary,
                                                ToolArgsJson = argsJson
                                            },
                                            priorContent
                                        );
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Adds the model/effort/mode selection flags to an <c>agy</c> invocation.
    /// </summary>
    /// <returns>The model id actually passed to the binary, or null when none was.</returns>
    /// <remarks>
    /// Extracted from <see cref="ExecuteRealAgyStreamAsync"/> so the argument contract is testable
    /// without spawning the CLI. The rules encoded here are the ones the capability surface has to
    /// advertise against: a value the picker offers but this method drops or rewrites is a lie.
    /// <list type="bullet">
    /// <item>
    /// <c>--effort</c> takes the set <c>agy --help</c> documents
    /// (<c>low|medium|high|xhigh|max</c>) and forwards it verbatim. xhigh/max used to be rewritten
    /// down to "high" while the picker advertised all five, so a user who chose <c>max</c> silently
    /// got <c>high</c> and the transcript reported <c>max</c>.
    /// </item>
    /// <item>
    /// An effort outside that set is dropped, not mapped to a neighbour: omitting the flag lets agy
    /// apply its own default, whereas substituting fabricates a choice the user never made.
    /// </item>
    /// <item>
    /// There is deliberately no context-size flag. <c>agy --help</c> exposes none, which is why the
    /// capability surface reports <c>supportsContextSelection: false</c> for this driver and the
    /// dashboard hides the picker.
    /// </item>
    /// </list>
    /// </remarks>
    /// <inheritdoc />
    public Task<DriverCapabilityDeclaration> DescribeCapabilitiesAsync(
        AgentPresetConfig preset,
        CancellationToken ct = default)
        => DriverCapabilityDescriber.DescribeAgyAsync(preset, ct);

    internal static string? AddAgySelectionArguments(ProcessStartInfo psi, PromptReceivedEvent promptEvent)
    {
        string? resolvedModel = null;
        if (!string.IsNullOrWhiteSpace(promptEvent.Model))
        {
            var rawModel = promptEvent.Model.Trim();

            // Retired model ids: agy no longer accepts them, so they are rewritten to the current
            // equivalent rather than failing the run. This is a compatibility mapping on ids the
            // driver rejects, not a substitution for missing telemetry.
            resolvedModel = rawModel.Equals("claude-3-7-sonnet", StringComparison.OrdinalIgnoreCase) ||
                            rawModel.Equals("claude-3-5-sonnet", StringComparison.OrdinalIgnoreCase)
                ? "claude-sonnet-5-5"
                : rawModel;

            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(resolvedModel);
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Effort))
        {
            var rawEffort = promptEvent.Effort.Trim().ToLowerInvariant();
            if (DriverEffortContracts.Supports(DriverAdapters.Agy, rawEffort))
            {
                psi.ArgumentList.Add("--effort");
                psi.ArgumentList.Add(rawEffort);
            }
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Mode))
        {
            psi.ArgumentList.Add("--mode");
            psi.ArgumentList.Add(promptEvent.Mode.Trim());
        }

        return resolvedModel;
    }

    private static async Task ExecuteRealAgyStreamAsync(
        string agyExecutable,
        string cwd,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = agyExecutable,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        string? activeConvId = null;
        if (!string.IsNullOrWhiteSpace(promptEvent.ConversationId))
        {
            activeConvId = promptEvent.ConversationId.Trim();
            SetSessionConversationId(promptEvent.SessionId, activeConvId);
        }
        else if (GetSessionConversationId(promptEvent.SessionId) is string existingConvId &&
                 !string.IsNullOrWhiteSpace(existingConvId))
        {
            activeConvId = existingConvId;
        }

        if (!string.IsNullOrWhiteSpace(activeConvId))
        {
            EnsureBrainRestoredFromWorkspaceState(activeConvId);
            psi.ArgumentList.Add("--conversation");
            psi.ArgumentList.Add(activeConvId);
        }

        string? resolvedModel = AddAgySelectionArguments(psi, promptEvent);

        var attachedBlock = AgentAdapterFactory.FormatAttachedFilesBlock(promptEvent.Attachments);
        var agyPromptText = string.IsNullOrWhiteSpace(attachedBlock)
            ? promptEvent.Prompt
            : $"{attachedBlock}{promptEvent.Prompt}";
        if (string.IsNullOrWhiteSpace(activeConvId) &&
            (!string.IsNullOrWhiteSpace(promptEvent.PersonaPrompt) || !string.IsNullOrWhiteSpace(promptEvent.PersonaName)))
        {
            var sb = new StringBuilder();
            var pDisplay = !string.IsNullOrWhiteSpace(promptEvent.PersonaName) ? promptEvent.PersonaName : promptEvent.PersonaId;
            var rolePart = !string.IsNullOrWhiteSpace(promptEvent.PersonaRole) ? $" ({promptEvent.PersonaRole})" : "";
            sb.AppendLine($"[Persona Context: You are acting as \"{pDisplay}\"{rolePart} on driver @{preset.Id}.]");
            sb.AppendLine($"[Active Session ID: {promptEvent.SessionId}]");
            if (!string.IsNullOrWhiteSpace(promptEvent.PersonaId))
            {
                sb.AppendLine($"[Session History Tooling: You can inspect past sessions for this persona anytime using the MCP tool 'get_persona_sessions' with personaId='{promptEvent.PersonaId}', or 'get_session' to inspect previous session transcripts and artifacts.]");
            }
            if (!string.IsNullOrWhiteSpace(promptEvent.PersonaPrompt))
            {
                sb.AppendLine($"[Persona Instructions: {promptEvent.PersonaPrompt.Trim()}]");
            }
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(attachedBlock))
            {
                sb.Append(attachedBlock);
            }
            sb.Append(promptEvent.Prompt);
            agyPromptText = sb.ToString();
        }

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(agyPromptText);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        string? transcriptFile = !string.IsNullOrWhiteSpace(activeConvId)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "brain", activeConvId, ".system_generated", "logs", "transcript.jsonl")
            : null;
        var lastTranscriptStep = transcriptFile != null ? GetMaxTranscriptStep(transcriptFile) : -1;

        using var proc = new Process { StartInfo = psi };
        using var agyReg = ActiveAgyProcessRegistry.Register(promptEvent.SessionId, proc);
        proc.Start();
        using var reg = ct.Register(() => AgentAdapterFactory.KillProcessTree(proc));

        if (ct.IsCancellationRequested)
        {
            AgentAdapterFactory.KillProcessTree(proc);
            throw new OperationCanceledException(ct);
        }

        var streamedResponseBuilder = new StringBuilder();
        var emittedFinalResult = false;
        long lastInputTokens = 0, lastOutputTokens = 0, lastThinkingTokens = 0, lastTotalTokens = 0;
        double lastDuration = 0;

        var isPastDiscovery = (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Plan approved", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("Plan approved", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Executing task", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("Executing task", StringComparison.OrdinalIgnoreCase) == true);

        var isDiscovery = !isPastDiscovery && (
            (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("stage-1-discovery", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("stage-1-discovery", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("[STAGE 1: DISCOVERY", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Awaiting plan approval", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("Awaiting plan approval", StringComparison.OrdinalIgnoreCase) == true));
        var maxSteps = OpenAiAdapter.ResolveMaxSteps(preset, isDiscovery);
        var agyStepCount = 0;
        var agyReadOnlyStreak = 0;
        var agyWarnedBudget = false;
        var agyFilesInspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agyFilesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agyCommandsRun = new List<string>();
        string? agyLastActionSummary = null;
        string? agyLatestThought = null;

        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    AgentAdapterFactory.KillProcessTree(proc);
                    throw new OperationCanceledException(ct);
                }

                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;

                if (ct.IsCancellationRequested)
                {
                    AgentAdapterFactory.KillProcessTree(proc);
                    throw new OperationCanceledException(ct);
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var evProp)) continue;

                    var evName = evProp.GetString();

                    if (evName == "init")
                    {
                        if (root.TryGetProperty("conversation_id", out var convProp))
                        {
                            var convId = convProp.GetString();
                            if (!string.IsNullOrWhiteSpace(convId))
                            {
                                activeConvId = convId;
                                SetSessionConversationId(promptEvent.SessionId, convId);
                                transcriptFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "brain", convId, ".system_generated", "logs", "transcript.jsonl");
                                if (lastTranscriptStep < 0)
                                {
                                    lastTranscriptStep = GetMaxTranscriptStep(transcriptFile);
                                }
                                try
                                {
                                    await onEvent(new TrajectoryEventPayload
                                    {
                                        EventType = "system",
                                        Content = $"Conversation initialized ({convId[..Math.Min(8, convId.Length)]}...)",
                                        StructuredPayloadJson = JsonSerializer.Serialize(new { conversationId = convId })
                                    });
                                }
                                catch { }
                            }
                        }
                    }
                    else if (evName == "step_update" && root.TryGetProperty("step_update", out var step))
                    {
                        // Check if an interactive question was called and recorded in the transcript
                        if (!string.IsNullOrWhiteSpace(transcriptFile))
                        {
                            var askQ = CheckForAskQuestionToolCall(transcriptFile, ref lastTranscriptStep);
                            if (askQ != null)
                            {
                                var priorText = askQ.PriorResponseContent;
                                if (string.IsNullOrWhiteSpace(priorText) && streamedResponseBuilder.Length > 0)
                                {
                                    priorText = streamedResponseBuilder.ToString().Trim();
                                }
                                if (!string.IsNullOrWhiteSpace(priorText))
                                {
                                    await onEvent(new TrajectoryEventPayload
                                    {
                                        EventType = "response",
                                        Content = priorText
                                    });
                                }
                                await onEvent(askQ.ToolCallEvent);
                                AgentAdapterFactory.KillProcessTree(proc);
                                return;
                            }
                        }
                        var stepType = step.TryGetProperty("step_type", out var stProp) ? stProp.GetString() : null;
                        var state = step.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;

                        if (stepType == "agent_response")
                        {
                            if (step.TryGetProperty("text_delta", out var tdProp))
                            {
                                var delta = tdProp.GetString();
                                if (!string.IsNullOrEmpty(delta))
                                {
                                    streamedResponseBuilder.Append(delta);
                                }
                            }

                            if (step.TryGetProperty("thought", out var thProp) && thProp.ValueKind == JsonValueKind.String)
                            {
                                var th = thProp.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(th)) agyLatestThought = th;
                            }
                            else if (step.TryGetProperty("reasoning", out var rsProp) && rsProp.ValueKind == JsonValueKind.String)
                            {
                                var rs = rsProp.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(rs)) agyLatestThought = rs;
                            }

                            if (state == "DONE" && step.TryGetProperty("usage", out var usageProp))
                            {
                                var thinkingTokens = usageProp.TryGetProperty("thinking_tokens", out var ttProp) ? ttProp.GetInt32() : 0;
                                var duration = step.TryGetProperty("duration_seconds", out var durProp) ? durProp.GetDouble() : 0;

                                if (usageProp.TryGetProperty("input_tokens", out var ip)) lastInputTokens = ip.GetInt64();
                                if (usageProp.TryGetProperty("output_tokens", out var op)) lastOutputTokens = op.GetInt64();
                                if (usageProp.TryGetProperty("thinking_tokens", out var tp)) lastThinkingTokens = tp.GetInt64();
                                if (usageProp.TryGetProperty("total_tokens", out var totp)) lastTotalTokens = totp.GetInt64();
                                if (duration > 0) lastDuration = duration;

                                // Emit thought indicator when the model completed a reasoning step before calling a tool
                                if (thinkingTokens > 0 && streamedResponseBuilder.Length == 0)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    await onEvent(new TrajectoryEventPayload
                                    {
                                        EventType = "thought",
                                        Content = $"Model reasoned ({thinkingTokens} thinking tokens in {duration:F1}s)"
                                    });
                                }
                            }
                        }
                        else if (stepType == "tool")
                        {
                            var toolName = step.TryGetProperty("tool_name", out var tnProp) ? tnProp.GetString() ?? "tool" : "tool";
                            string? argsJson = null;
                            string? outputText = null;
                            var summary = $"Executing {toolName}";

                            if (step.TryGetProperty("tool_info", out var tiProp))
                            {
                                JsonElement paramsElem = default;
                                bool hasParams = false;
                                if (tiProp.TryGetProperty("parameters", out var p1)) { paramsElem = p1; hasParams = true; }
                                else if (tiProp.TryGetProperty("arguments", out var p2)) { paramsElem = p2; hasParams = true; }
                                else if (tiProp.TryGetProperty("args", out var p3)) { paramsElem = p3; hasParams = true; }
                                else if (tiProp.TryGetProperty("input", out var p4)) { paramsElem = p4; hasParams = true; }

                                if (hasParams)
                                {
                                    argsJson = paramsElem.ValueKind == JsonValueKind.String
                                        ? paramsElem.GetString()
                                        : paramsElem.GetRawText();

                                    var cmdVal = OpenAiAdapter.GetPropertyString(paramsElem, "command", "CommandLine", "commandLine", "cmd", "script", "shell_command");
                                    if (!string.IsNullOrWhiteSpace(cmdVal))
                                    {
                                        summary = $"Running command: {(cmdVal.Length > 60 ? cmdVal[..57] + "..." : cmdVal)}";
                                        agyCommandsRun.Add(cmdVal);
                                    }
                                    else
                                    {
                                        var pVal = OpenAiAdapter.GetPropertyString(paramsElem, "path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath", "target_file", "source_file", "uri");
                                        if (!string.IsNullOrWhiteSpace(pVal))
                                        {
                                            var fileName = Path.GetFileName(pVal);
                                            var tnLower = toolName.ToLowerInvariant();
                                            if (tnLower.Contains("write") || tnLower.Contains("edit") || tnLower.Contains("replace") || tnLower.Contains("modify") || tnLower.Contains("patch"))
                                            {
                                                agyFilesModified.Add(pVal);
                                                summary = $"Modifying file: {fileName}";
                                            }
                                            else
                                            {
                                                agyFilesInspected.Add(pVal);
                                                summary = $"Inspecting file: {fileName}";
                                            }
                                        }
                                    }
                                }
                                if (tiProp.TryGetProperty("output", out var outProp))
                                {
                                    outputText = outProp.ValueKind == JsonValueKind.String
                                        ? outProp.GetString()
                                        : outProp.GetRawText();
                                }
                            }
                            agyLastActionSummary = summary;

                            ct.ThrowIfCancellationRequested();
                            if (state == "ACTIVE")
                            {
                                await onEvent(new TrajectoryEventPayload
                                {
                                    EventType = "tool_call",
                                    ToolName = toolName,
                                    Content = summary,
                                    ToolArgsJson = argsJson
                                });
                            }
                            else if (state == "DONE")
                            {
                                var duration = step.TryGetProperty("duration_seconds", out var durProp) ? durProp.GetDouble() : 0;
                                await onEvent(new TrajectoryEventPayload
                                {
                                    EventType = "tool_result",
                                    ToolName = toolName,
                                    Content = $"Completed in {duration:F2}s",
                                    ToolResultJson = outputText
                                });

                                bool isReadOnly = OpenAiAdapter.IsReadOnlyTool(toolName, argsJson);
                                if (isReadOnly)
                                {
                                    agyReadOnlyStreak++;
                                    if (agyReadOnlyStreak % 2 == 0)
                                    {
                                        agyStepCount++;
                                    }
                                }
                                else
                                {
                                    agyReadOnlyStreak = 0;
                                    agyStepCount++;
                                }

                                if (!agyWarnedBudget && agyStepCount >= (int)(maxSteps * 0.8) && maxSteps > 5)
                                {
                                    agyWarnedBudget = true;
                                    await onEvent(new TrajectoryEventPayload
                                    {
                                        EventType = "thought",
                                        Content = $"[@{preset.Id}] [Turn Step Budget Notice] Approaching step budget ({agyStepCount}/{maxSteps}). Please wrap up current verification, update task status via manage_task, and conclude turn."
                                    });
                                }

                                if (agyStepCount >= maxSteps)
                                {
                                    await onEvent(new TrajectoryEventPayload
                                    {
                                        EventType = "thought",
                                        Content = $"[@{preset.Id}] Reached progress checkpoint ({maxSteps} steps). Concluding turn to roll over."
                                    });
                                    AgentAdapterFactory.KillProcessTree(proc);

                                    var detailsSb = new StringBuilder();
                                    detailsSb.AppendLine($"🔄 **Progress Checkpoint ({maxSteps} steps)**:\n");
                                    var agentTag = !string.IsNullOrWhiteSpace(promptEvent.PersonaId) ? promptEvent.PersonaId : preset.Id;
                                    var personaDisplay = !string.IsNullOrWhiteSpace(promptEvent.PersonaName) ? $" ({promptEvent.PersonaName})" : "";
                                    detailsSb.AppendLine($"Agent `@{agentTag}`{personaDisplay} reached the {maxSteps}-step progress limit for this turn and is rolling over.\n");

                                    var goal = OpenAiAdapter.ExtractGoal(promptEvent);
                                    if (!string.IsNullOrWhiteSpace(goal))
                                    {
                                        detailsSb.AppendLine($"**Active Goal**: {goal}\n");
                                    }

                                    if (!string.IsNullOrWhiteSpace(agyLastActionSummary))
                                    {
                                        detailsSb.AppendLine($"**Current Activity**: {agyLastActionSummary}");
                                    }

                                    var thought = agyLatestThought;
                                    if (string.IsNullOrWhiteSpace(thought) && !string.IsNullOrWhiteSpace(transcriptFile))
                                    {
                                        thought = GetLatestThoughtFromTranscript(transcriptFile);
                                    }

                                    var partialText = streamedResponseBuilder.ToString().Trim();
                                    if (!string.IsNullOrWhiteSpace(partialText))
                                    {
                                        var clippedText = partialText.Length > 300 ? partialText[..297] + "..." : partialText;
                                        detailsSb.AppendLine($"**Agent Output**:\n{clippedText}\n");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(thought))
                                    {
                                        detailsSb.AppendLine($"**Recent Thinking**: \"{thought}\"\n");
                                    }

                                    detailsSb.AppendLine("**Turn Progress & Workspace Activity**:");
                                    detailsSb.AppendLine($"- **Steps**: {agyStepCount} completed");
                                    if (agyFilesModified.Count > 0)
                                    {
                                        var modFiles = string.Join(", ", agyFilesModified.Take(8).Select(f => $"`{Path.GetFileName(f)}`"));
                                        var extraMod = agyFilesModified.Count > 8 ? $" (+{agyFilesModified.Count - 8} more)" : "";
                                        detailsSb.AppendLine($"- **Files Modified** ({agyFilesModified.Count}): {modFiles}{extraMod}");
                                    }
                                    if (agyFilesInspected.Count > 0)
                                    {
                                        var inspFiles = string.Join(", ", agyFilesInspected.Take(8).Select(f => $"`{Path.GetFileName(f)}`"));
                                        var extra = agyFilesInspected.Count > 8 ? $" (+{agyFilesInspected.Count - 8} more)" : "";
                                        detailsSb.AppendLine($"- **Files Inspected** ({agyFilesInspected.Count}): {inspFiles}{extra}");
                                    }
                                    if (agyCommandsRun.Count > 0)
                                    {
                                        var distinctCmds = agyCommandsRun.Distinct().ToList();
                                        var cmds = string.Join(", ", distinctCmds.Take(5).Select(c => $"`{(c.Length > 45 ? c[..42] + "..." : c)}`"));
                                        var extraCmds = distinctCmds.Count > 5 ? $" (+{distinctCmds.Count - 5} more)" : "";
                                        detailsSb.AppendLine($"- **Commands Run** ({distinctCmds.Count}): {cmds}{extraCmds}");
                                    }

                                    detailsSb.Append("\n*Continuing to next turn autonomously. To redirect or interrupt, reply in this chat.*");

                                    await onEvent(new TrajectoryEventPayload
                                    {
                                        EventType = "response",
                                        Content = detailsSb.ToString()
                                    });
                                    return;
                                }
                            }
                        }
                    }
                    else if (evName == "result" && root.TryGetProperty("result", out var resElem))
                    {
                        var responseText = resElem.TryGetProperty("response", out var respProp)
                            ? respProp.GetString()?.Trim()
                            : null;

                        if (string.IsNullOrWhiteSpace(responseText))
                        {
                            responseText = streamedResponseBuilder.ToString().Trim();
                        }

                        if (resElem.TryGetProperty("duration_seconds", out var rDur)) lastDuration = rDur.GetDouble();
                        if (resElem.TryGetProperty("usage", out var rUsage))
                        {
                            if (rUsage.TryGetProperty("input_tokens", out var ip)) lastInputTokens = ip.GetInt64();
                            if (rUsage.TryGetProperty("output_tokens", out var op)) lastOutputTokens = op.GetInt64();
                            if (rUsage.TryGetProperty("thinking_tokens", out var tp)) lastThinkingTokens = tp.GetInt64();
                            if (rUsage.TryGetProperty("total_tokens", out var totp)) lastTotalTokens = totp.GetInt64();
                        }

                        if (lastTotalTokens == 0 && (lastInputTokens > 0 || lastOutputTokens > 0))
                        {
                            lastTotalTokens = lastInputTokens + lastOutputTokens;
                        }

                        string? structuredJson = null;
                        var effectiveModel = resolvedModel ?? promptEvent.Model;
                        if (lastTotalTokens > 0 || lastDuration > 0 || !string.IsNullOrWhiteSpace(effectiveModel))
                        {
                            structuredJson = JsonSerializer.Serialize(new
                            {
                                model = effectiveModel,
                                effort = promptEvent.Effort,
                                mode = promptEvent.Mode,
                                // null, not promptEvent.ContextSize: agy has no context-size flag
                                // and nothing above forwards one, so echoing the value back here
                                // claimed a window the binary was never told about. That is how a
                                // stale "500k" badge ended up pinned to agy sessions.
                                contextSize = (string?)null,
                                conversationId = activeConvId,
                                usage = new
                                {
                                    inputTokens = lastInputTokens,
                                    outputTokens = lastOutputTokens,
                                    thinkingTokens = lastThinkingTokens,
                                    totalTokens = lastTotalTokens,
                                    durationSeconds = lastDuration
                                }
                            });
                        }

                        ct.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(responseText))
                        {
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "response",
                                Content = responseText,
                                StructuredPayloadJson = structuredJson
                            });
                            emittedFinalResult = true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Non-JSON line fallback
                    streamedResponseBuilder.AppendLine(line);
                }
            }

            if (!string.IsNullOrWhiteSpace(transcriptFile))
            {
                var askQ = CheckForAskQuestionToolCall(transcriptFile, ref lastTranscriptStep);
                if (askQ != null)
                {
                    var priorText = askQ.PriorResponseContent;
                    if (string.IsNullOrWhiteSpace(priorText) && streamedResponseBuilder.Length > 0)
                    {
                        priorText = streamedResponseBuilder.ToString().Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(priorText))
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = priorText
                        });
                    }
                    await onEvent(askQ.ToolCallEvent);
                    AgentAdapterFactory.KillProcessTree(proc);
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();
            var stdErr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            ct.ThrowIfCancellationRequested();

            if (!emittedFinalResult)
            {
                var fallbackText = streamedResponseBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(fallbackText) && !string.IsNullOrWhiteSpace(stdErr))
                {
                    fallbackText = stdErr.Trim();
                }
                if (string.IsNullOrWhiteSpace(fallbackText))
                {
                    fallbackText = $"Agent `@{preset.Id}` finished execution (exit code {proc.ExitCode}).";
                }

                if (lastTotalTokens == 0 && (lastInputTokens > 0 || lastOutputTokens > 0))
                {
                    lastTotalTokens = lastInputTokens + lastOutputTokens;
                }

                string? structuredJson = null;
                var fallbackEffectiveModel = resolvedModel ?? promptEvent.Model;
                if (lastTotalTokens > 0 || lastDuration > 0 || !string.IsNullOrWhiteSpace(fallbackEffectiveModel))
                {
                    structuredJson = JsonSerializer.Serialize(new
                    {
                        model = fallbackEffectiveModel,
                        effort = promptEvent.Effort,
                        mode = promptEvent.Mode,
                        contextSize = (string?)null, // see the note on the primary path above
                        conversationId = activeConvId,
                        usage = new
                        {
                            inputTokens = lastInputTokens,
                            outputTokens = lastOutputTokens,
                            thinkingTokens = lastThinkingTokens,
                            totalTokens = lastTotalTokens,
                            durationSeconds = lastDuration
                        }
                    });
                }

                ct.ThrowIfCancellationRequested();
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "response",
                    Content = fallbackText,
                    StructuredPayloadJson = structuredJson
                });
            }
        }
        catch (OperationCanceledException)
        {
            AgentAdapterFactory.KillProcessTree(proc);
            throw;
        }
    }

    private static async Task ExecuteFallbackShellAsync(
        string cwd,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[@{preset.Id}] Processing prompt from {promptEvent.Sender}: \"{promptEvent.Prompt}\" in {cwd}"
        });

        var diagnosticCmd = SelectWorkspaceCommand(promptEvent.Prompt);
        var (exitCode, stdOut, stdErr) = await AgentAdapterFactory.RunShellCommandAsync(diagnosticCmd, cwd, ct);
        ct.ThrowIfCancellationRequested();
        var combinedOutput = string.IsNullOrWhiteSpace(stdOut) ? stdErr : stdOut;

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "response",
            Content = string.IsNullOrWhiteSpace(combinedOutput)
                ? $"Command `{diagnosticCmd}` completed (exit code {exitCode})."
                : combinedOutput
        });
    }

    internal static string SelectWorkspaceCommand(string prompt)
    {
        var trimmed = prompt.Trim();
        if (trimmed.StartsWith("$ ")) return trimmed[2..].Trim();
        if (trimmed.StartsWith("run ", StringComparison.OrdinalIgnoreCase)) return trimmed[4..].Trim();
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dir /b" : "ls -la";
    }
}

public class ClaudeAdapter : IAgentAdapter
{
    public string AdapterType => "claude";

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = AgentAdapterFactory.ResolveWorkspaceDirectory(promptEvent);
        var claudePath = AgentAdapterFactory.ResolveExecutablePath(preset.Command ?? "claude");

        if (claudePath != null)
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "thought",
                Content = $"[Claude Code @{preset.Id}] Executing prompt via {claudePath}..."
            });

            var promptEnvelope = AgentAdapterFactory.BuildAgentMeshPromptEnvelope(
                promptEvent.Prompt,
                preset.Id,
                promptEvent.Mode,
                promptEvent.PersonaId,
                promptEvent.PersonaName,
                promptEvent.PersonaRole,
                promptEvent.PersonaPrompt,
                promptEvent.SessionId,
                promptEvent.Attachments);
            var escapedPrompt = promptEnvelope.Replace("\"", "\\\"");
            var autocompactArg = !string.IsNullOrWhiteSpace(promptEvent.ContextSize)
                ? $" --autocompact \"{promptEvent.ContextSize.Trim()}\""
                : "";
            var isPastDiscovery = (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Plan approved", StringComparison.OrdinalIgnoreCase) == true)
                || (promptEvent.Prompt?.Contains("Plan approved", StringComparison.OrdinalIgnoreCase) == true)
                || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Executing task", StringComparison.OrdinalIgnoreCase) == true)
                || (promptEvent.Prompt?.Contains("Executing task", StringComparison.OrdinalIgnoreCase) == true);

            var isDiscovery = !isPastDiscovery && (
                (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("stage-1-discovery", StringComparison.OrdinalIgnoreCase) == true)
                || (promptEvent.Prompt?.Contains("stage-1-discovery", StringComparison.OrdinalIgnoreCase) == true)
                || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("[STAGE 1: DISCOVERY", StringComparison.OrdinalIgnoreCase) == true)
                || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Awaiting plan approval", StringComparison.OrdinalIgnoreCase) == true)
                || (promptEvent.Prompt?.Contains("Awaiting plan approval", StringComparison.OrdinalIgnoreCase) == true));
            var maxSteps = OpenAiAdapter.ResolveMaxSteps(preset, isDiscovery);
            var maxStepsArg = $" --max-steps {maxSteps}";
            var (exitCode, stdOut, stdErr) = await AgentAdapterFactory.RunShellCommandAsync($"\"{claudePath}\"{autocompactArg}{maxStepsArg} -p \"{escapedPrompt}\"", cwd, ct);
            ct.ThrowIfCancellationRequested();
            var output = !string.IsNullOrWhiteSpace(stdOut) ? stdOut : stdErr;

            var questions = AgentAdapterFactory.ExtractQuestionsFromOutput(output);
            if (questions.Count > 0)
            {
                var questionsJson = JsonSerializer.Serialize(new
                {
                    questions = questions.Select(q => new
                    {
                        question = q.Question,
                        options = q.Options,
                        is_multi_select = q.IsMultiSelect
                    })
                });

                var cleanedOutput = Regex.Replace(output, @"\(?Note:?\s*I can't see your replies in this non-interactive session\.?.*?(\)|$)", "", RegexOptions.IgnoreCase).Trim();

                var claudePayloadJson = JsonSerializer.Serialize(new
                {
                    model = promptEvent.Model ?? "claude-3-7-sonnet",
                    effort = promptEvent.Effort,
                    mode = promptEvent.Mode,
                    contextSize = promptEvent.ContextSize
                });

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "response",
                    Content = string.IsNullOrWhiteSpace(cleanedOutput) ? output : cleanedOutput,
                    StructuredPayloadJson = claudePayloadJson
                });

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_call",
                    ToolName = "ask_question",
                    Content = $"Asking {questions.Count} question{(questions.Count == 1 ? "" : "s")}",
                    ToolArgsJson = questionsJson
                });
                return;
            }

            var defaultClaudePayloadJson = JsonSerializer.Serialize(new
            {
                model = promptEvent.Model ?? "claude-3-7-sonnet",
                effort = promptEvent.Effort,
                mode = promptEvent.Mode,
                contextSize = promptEvent.ContextSize
            });

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = string.IsNullOrWhiteSpace(output)
                    ? $"Claude CLI completed with exit code {exitCode}."
                    : output,
                StructuredPayloadJson = defaultClaudePayloadJson
            });
            return;
        }

        var fallback = new AgyAdapter();
        await fallback.ExecutePromptAsync(preset, promptEvent, onEvent, ct);
    }
}

public class CopilotAdapter : IAgentAdapter, IDescribesOwnCapabilities
{
    public string AdapterType => "copilot";

    public Task<DriverCapabilityDeclaration> DescribeCapabilitiesAsync(
        AgentPresetConfig preset,
        CancellationToken ct = default)
        => DriverCapabilityDescriber.DescribeCopilotAsync(preset, ct);

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = AgentAdapterFactory.ResolveWorkspaceDirectory(promptEvent);
        string? copilotPath = null;
        if (!string.IsNullOrWhiteSpace(preset.Command) && !string.Equals(preset.Command, "gh", StringComparison.OrdinalIgnoreCase))
        {
            copilotPath = AgentAdapterFactory.ResolveExecutablePath(preset.Command);
        }
        copilotPath ??= AgentAdapterFactory.ResolveExecutablePath("copilot");

        var ghPath = copilotPath == null ? AgentAdapterFactory.ResolveExecutablePath("gh") : null;

        if (copilotPath != null || ghPath != null)
        {
            var isViaGh = copilotPath == null && ghPath != null;
            var executablePath = copilotPath ?? ghPath!;

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "thought",
                Content = $"[GitHub Copilot CLI @{preset.Id}] Running prompt via {(isViaGh ? "gh copilot" : executablePath)}..."
            });

            await ExecuteCopilotInvocationAsync(
                executablePath,
                isViaGh,
                cwd,
                preset,
                promptEvent,
                onEvent,
                ct);
            return;
        }

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "error",
            Content = $"[GitHub Copilot CLI @{preset.Id}] GitHub Copilot CLI ('copilot') is not installed on this system."
        });

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "response",
            Content = $"**Error: GitHub Copilot CLI (`copilot`) is not installed.**\n\nPlease install it using:\n```bash\nnpm install -g @github/copilot\n```"
        });
    }

    private static async Task ExecuteCopilotInvocationAsync(
        string executablePath,
        bool isViaGh,
        string cwd,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var promptEnvelope = AgentAdapterFactory.BuildAgentMeshPromptEnvelope(
            promptEvent.Prompt,
            preset.Id,
            promptEvent.Mode,
            promptEvent.PersonaId,
            promptEvent.PersonaName,
            promptEvent.PersonaRole,
            promptEvent.PersonaPrompt,
            promptEvent.SessionId,
            promptEvent.Attachments);

        var (exitCode, stdout, stderr, runSucceeded) = await RunCopilotSubprocessAsync(
            executablePath,
            isViaGh,
            cwd,
            preset,
            promptEvent,
            promptEnvelope,
            useJsonFormat: true,
            onEvent,
            ct);

        if (!runSucceeded && (stderr.Contains("unexpected argument '--output-format'") || stderr.Contains("unknown option '--output-format'")))
        {
            await RunCopilotSubprocessAsync(
                executablePath,
                isViaGh,
                cwd,
                preset,
                promptEvent,
                promptEnvelope,
                useJsonFormat: false,
                onEvent,
                ct);
        }
    }

    internal static ProcessStartInfo BuildCopilotProcessStartInfo(
        string executablePath,
        bool isViaGh,
        string cwd,
        PromptReceivedEvent promptEvent,
        string promptEnvelope,
        bool useJsonFormat)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isViaGh)
        {
            psi.ArgumentList.Add("copilot");
            psi.ArgumentList.Add("--");
        }

        if (AgyAdapter.GetSessionConversationId(promptEvent.SessionId) is string existingConvId &&
            !string.IsNullOrWhiteSpace(existingConvId))
        {
            psi.ArgumentList.Add($"--resume={existingConvId}");
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(promptEvent.Model.Trim());
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Mode))
        {
            var modeVal = promptEvent.Mode.Trim().ToLowerInvariant();
            if (modeVal == "plan")
            {
                psi.ArgumentList.Add("--mode");
                psi.ArgumentList.Add("plan");
            }
            else if (modeVal is "auto" or "autopilot")
            {
                psi.ArgumentList.Add("--mode");
                psi.ArgumentList.Add("autopilot");
            }
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.ContextSize))
        {
            var ctxVal = promptEvent.ContextSize.Trim().ToLowerInvariant();
            var copilotContextTier = ctxVal is "long_context" or "500k" or "1m" or "2m"
                ? "long_context"
                : "default";
            psi.ArgumentList.Add("--context");
            psi.ArgumentList.Add(copilotContextTier);
        }

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(promptEnvelope);
        psi.ArgumentList.Add("--allow-all");
        psi.ArgumentList.Add("--no-color");

        if (useJsonFormat)
        {
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--stream");
            psi.ArgumentList.Add("on");
        }

        IsolateCopilotEnvironment(psi.Environment);

        return psi;
    }

    internal static void IsolateCopilotEnvironment(IDictionary<string, string?> environment, string? homeDirectory = null)
    {
        environment.TryGetValue(CopilotCredentialHelper.PersonaGitHubTokenEnvVar, out var personaGhToken);
        environment.TryGetValue("GH_TOKEN", out var ghToken);
        environment.TryGetValue("GITHUB_TOKEN", out var githubToken);

        var effectivePersonaGitHubToken = !string.IsNullOrWhiteSpace(personaGhToken)
            ? personaGhToken
            : (!string.IsNullOrWhiteSpace(ghToken) ? ghToken : githubToken);

        if (!string.IsNullOrWhiteSpace(effectivePersonaGitHubToken) &&
            string.IsNullOrWhiteSpace(personaGhToken))
        {
            environment[CopilotCredentialHelper.PersonaGitHubTokenEnvVar] = effectivePersonaGitHubToken.Trim();
        }

        // Copilot CLI checks COPILOT_GITHUB_TOKEN, GH_TOKEN, and GITHUB_TOKEN ahead of
        // ~/.copilot/config.json and exits immediately if any of them contains a classic PAT (ghp_).
        // Keep GitHub repository PAT auth isolated from Copilot CLI auth.
        environment.Remove("GH_TOKEN");
        environment.Remove("GITHUB_TOKEN");

        environment.TryGetValue("COPILOT_GITHUB_TOKEN", out var rawCopilotEnv);
        var sanitizedCopilotEnv = CopilotCredentialHelper.SanitizeCopilotEnvToken(
            rawCopilotEnv,
            effectivePersonaGitHubToken);

        if (sanitizedCopilotEnv == null)
        {
            environment.Remove("COPILOT_GITHUB_TOKEN");

            if (environment.TryGetValue(CopilotCredentialHelper.CopilotOAuthTokenEnvVar, out var oauthTok) &&
                CopilotCredentialHelper.IsSupportedCopilotToken(oauthTok))
            {
                var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var hasFileLogin = false;
                if (!string.IsNullOrWhiteSpace(home))
                {
                    var configPath = Path.Combine(home, ".copilot", "config.json");
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            hasFileLogin = CopilotCredentialHelper.HasActiveCopilotConfigLogin(File.ReadAllText(configPath));
                        }
                        catch { }
                    }
                }

                if (!hasFileLogin)
                {
                    environment["COPILOT_GITHUB_TOKEN"] = oauthTok!.Trim();
                }
            }
        }
        else
        {
            environment["COPILOT_GITHUB_TOKEN"] = sanitizedCopilotEnv;
        }
    }

    internal const int MaxRawFallbackPreviewChars = 16_384;
    private const int MaxToolResultPayloadChars = 32_768;

    internal static async Task<(int ExitCode, string StdOut, string StdErr, bool Succeeded)> RunCopilotSubprocessAsync(
        string executablePath,
        bool isViaGh,
        string cwd,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        string promptEnvelope,
        bool useJsonFormat,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var psi = BuildCopilotProcessStartInfo(executablePath, isViaGh, cwd, promptEvent, promptEnvelope, useJsonFormat);

        using var proc = new Process { StartInfo = psi };
        using var reg = ct.Register(() => AgentAdapterFactory.KillProcessTree(proc));
        proc.Start();
        proc.StandardInput.Close();

        var streamedResponseBuilder = new StringBuilder();
        var rawStdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();
        var lastActivity = Stopwatch.GetTimestamp();
        var lastHeartbeatTime = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        string? copilotSessionId = null;
        string? terminalUsageJson = null;
        string? taskCompleteSummary = null;

        var readStdOutTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                lastActivity = Stopwatch.GetTimestamp();
                lock (rawStdOutBuilder)
                {
                    rawStdOutBuilder.AppendLine(line);
                }

                if (TryParseCopilotJsonLine(line, out var evType, out var evContent, out var toolName, out var toolArgs, out var toolRes, out var sid, out var usage, out var parsedTaskSummary))
                {
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        copilotSessionId = sid;
                        AgyAdapter.SetSessionConversationId(promptEvent.SessionId, sid);
                    }
                    if (!string.IsNullOrWhiteSpace(usage))
                    {
                        terminalUsageJson = usage;
                    }
                    if (!string.IsNullOrWhiteSpace(parsedTaskSummary))
                    {
                        taskCompleteSummary = parsedTaskSummary.Trim();
                    }

                    if (evType == "thought" && !string.IsNullOrWhiteSpace(evContent))
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = evContent.Trim()
                        });
                    }
                    else if (evType == "tool_call" && !string.IsNullOrWhiteSpace(toolName))
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "tool_call",
                            ToolName = toolName,
                            ToolArgsJson = toolArgs,
                            Content = evContent ?? $"Calling {toolName}..."
                        });
                    }
                    else if (evType == "tool_result")
                    {
                        var boundedToolResult = BoundPreviewTail(toolRes, MaxToolResultPayloadChars);
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "tool_result",
                            ToolName = toolName,
                            ToolResultJson = boundedToolResult,
                            Content = TruncateLine(toolRes, 120)
                        });
                    }
                    else if (evType == "message" && !string.IsNullOrWhiteSpace(evContent))
                    {
                        lock (streamedResponseBuilder)
                        {
                            streamedResponseBuilder.Append(evContent);
                        }
                    }
                    else if (evType == "result" && !string.IsNullOrWhiteSpace(evContent))
                    {
                        lock (streamedResponseBuilder)
                        {
                            if (streamedResponseBuilder.Length == 0)
                            {
                                streamedResponseBuilder.Append(evContent);
                            }
                        }
                    }
                }
                else
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Running ", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Executing ", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Reading ", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Writing ", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Searching ", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Inspecting ", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("> ", StringComparison.Ordinal))
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = trimmed
                        });
                    }

                    lock (streamedResponseBuilder)
                    {
                        streamedResponseBuilder.AppendLine(line);
                    }
                }
            }
        }, ct);

        var readStdErrTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                lastActivity = Stopwatch.GetTimestamp();
                lock (stdErrBuilder)
                {
                    stdErrBuilder.AppendLine(line);
                }
            }
        }, ct);

        try
        {
            while (!proc.HasExited)
            {
                await Task.WhenAny(Task.Delay(1000, ct), proc.WaitForExitAsync(ct));
                if (proc.HasExited) break;

                var now = Stopwatch.GetTimestamp();
                var silentDuration = Stopwatch.GetElapsedTime(lastActivity);
                var sinceLastHeartbeat = Stopwatch.GetElapsedTime(lastHeartbeatTime);

                // Only emit heartbeat if Copilot has produced NO output for at least 60 seconds
                if (silentDuration >= TimeSpan.FromSeconds(60) && sinceLastHeartbeat >= TimeSpan.FromSeconds(60))
                {
                    lastHeartbeatTime = now;
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = $"[GitHub Copilot CLI @{preset.Id}] Still working... ({sw.Elapsed.TotalSeconds:F0}s elapsed)"
                    });
                }
            }

            await Task.WhenAll(readStdOutTask, readStdErrTask);
            ct.ThrowIfCancellationRequested();

            string rawStdOut;
            lock (rawStdOutBuilder) { rawStdOut = rawStdOutBuilder.ToString().Trim(); }
            string stdErr;
            lock (stdErrBuilder) { stdErr = stdErrBuilder.ToString().Trim(); }

            if (proc.ExitCode != 0 && (stdErr.Contains("unexpected argument '--output-format'") || stdErr.Contains("unknown option '--output-format'")))
            {
                return (proc.ExitCode, rawStdOut, stdErr, false);
            }

            string streamedOutput;
            lock (streamedResponseBuilder) { streamedOutput = streamedResponseBuilder.ToString().Trim(); }
            var output = ResolveFinalCopilotOutput(streamedOutput, taskCompleteSummary, rawStdOut, stdErr);

            // Check if Copilot asked questions
            var questions = AgentAdapterFactory.ExtractQuestionsFromOutput(output);
            if (questions.Count > 0)
            {
                var questionsJson = JsonSerializer.Serialize(new
                {
                    questions = questions.Select(q => new
                    {
                        question = q.Question,
                        options = q.Options,
                        is_multi_select = q.IsMultiSelect
                    })
                });

                var cleanedOutput = Regex.Replace(output, @"\(?Note:?\s*I can't see your replies in this non-interactive session\.?.*?(\)|$)", "", RegexOptions.IgnoreCase).Trim();

                var copilotPayloadJson = BuildPayloadJson(promptEvent, copilotSessionId, terminalUsageJson);

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "response",
                    Content = string.IsNullOrWhiteSpace(cleanedOutput) ? output : cleanedOutput,
                    StructuredPayloadJson = copilotPayloadJson
                });

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_call",
                    ToolName = "ask_question",
                    Content = $"Asking {questions.Count} question{(questions.Count == 1 ? "" : "s")}",
                    ToolArgsJson = questionsJson
                });
                return (proc.ExitCode, rawStdOut, stdErr, true);
            }

            var defaultCopilotPayloadJson = BuildPayloadJson(promptEvent, copilotSessionId, terminalUsageJson);

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = string.IsNullOrWhiteSpace(output)
                    ? $"Copilot CLI finished (exit code {proc.ExitCode})."
                    : output,
                StructuredPayloadJson = defaultCopilotPayloadJson
            });

            return (proc.ExitCode, rawStdOut, stdErr, true);
        }
        catch (OperationCanceledException)
        {
            AgentAdapterFactory.KillProcessTree(proc);
            throw;
        }
    }

    internal static string ResolveFinalCopilotOutput(
        string? streamedResponse,
        string? taskCompleteSummary,
        string? rawStdOut,
        string? stdErr,
        int maxFallbackChars = MaxRawFallbackPreviewChars)
    {
        if (!string.IsNullOrWhiteSpace(streamedResponse))
        {
            return streamedResponse.Trim();
        }

        if (!string.IsNullOrWhiteSpace(taskCompleteSummary))
        {
            return taskCompleteSummary.Trim();
        }

        // Extract non-JSON plain-text lines from rawStdOut first; never emit unbounded multi-MB JSONL streams.
        if (!string.IsNullOrWhiteSpace(rawStdOut))
        {
            var plainLines = new List<string>();
            foreach (var rawLine in rawStdOut.Split('\n'))
            {
                var t = rawLine.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith('{') && t.EndsWith('}')) continue;
                plainLines.Add(rawLine.TrimEnd('\r'));
            }

            if (plainLines.Count > 0)
            {
                return BoundPreviewTail(string.Join("\n", plainLines).Trim(), maxFallbackChars)!;
            }
        }

        var fallback = !string.IsNullOrWhiteSpace(stdErr) ? stdErr.Trim() : (rawStdOut?.Trim() ?? string.Empty);
        return BoundPreviewTail(fallback, maxFallbackChars) ?? string.Empty;
    }

    internal static string? BoundPreviewTail(string? text, int maxChars = MaxRawFallbackPreviewChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        const string prefix = "[... Earlier output omitted ...]\n";
        if (maxChars <= prefix.Length)
        {
            return text[^maxChars..];
        }

        var tailLen = maxChars - prefix.Length;
        return prefix + text[^tailLen..];
    }

    internal static bool TryParseCopilotJsonLine(
        string line,
        out string? eventType,
        out string? content,
        out string? toolName,
        out string? toolArgsJson,
        out string? toolResultJson,
        out string? sessionId,
        out string? usageJson) =>
        TryParseCopilotJsonLine(
            line,
            out eventType,
            out content,
            out toolName,
            out toolArgsJson,
            out toolResultJson,
            out sessionId,
            out usageJson,
            out _);

    internal static bool TryParseCopilotJsonLine(
        string line,
        out string? eventType,
        out string? content,
        out string? toolName,
        out string? toolArgsJson,
        out string? toolResultJson,
        out string? sessionId,
        out string? usageJson,
        out string? taskCompleteSummary)
    {
        eventType = null;
        content = null;
        toolName = null;
        toolArgsJson = null;
        toolResultJson = null;
        sessionId = null;
        usageJson = null;
        taskCompleteSummary = null;

        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var rawType = root.TryGetProperty("type", out var tProp)
                ? tProp.GetString()
                : (root.TryGetProperty("event", out var evProp)
                    ? evProp.GetString()
                    : (root.TryGetProperty("eventType", out var etProp) ? etProp.GetString() : null));

            if (string.IsNullOrWhiteSpace(rawType)) return false;

            var data = root.TryGetProperty("data", out var dProp) ? dProp : root;

            if (root.TryGetProperty("sessionId", out var sidRoot) && sidRoot.ValueKind == JsonValueKind.String)
            {
                sessionId = sidRoot.GetString();
            }
            else if (data.TryGetProperty("sessionId", out var sidData) && sidData.ValueKind == JsonValueKind.String)
            {
                sessionId = sidData.GetString();
            }

            if (root.TryGetProperty("usage", out var uRoot))
            {
                usageJson = uRoot.GetRawText();
            }
            else if (data.TryGetProperty("usage", out var uData))
            {
                usageJson = uData.GetRawText();
            }

            var lower = rawType.ToLowerInvariant();

            if (lower == "session.start")
            {
                eventType = "session.start";
                return true;
            }

            if (lower is "session.task_complete" or "task_complete" or "task.complete")
            {
                eventType = "task_complete";
                taskCompleteSummary = ExtractTaskCompleteSummary(data) ?? ExtractTaskCompleteSummary(root);
                content = taskCompleteSummary;
                return true;
            }

            if (lower is "assistant.reasoning" or "thought" or "reasoning")
            {
                eventType = "thought";
                content = data.TryGetProperty("content", out var c) ? c.GetString() : (data.TryGetProperty("text", out var t) ? t.GetString() : null);
                return !string.IsNullOrWhiteSpace(content);
            }

            // Evaluate tool_result BEFORE tool_call so events like "tool.execution_complete"
            // map to tool_result rather than matching "execution" in tool_call!
            if (lower.Contains("tool") && (lower.Contains("result") || lower.Contains("output") || lower.Contains("finish") || lower.Contains("done") || lower.Contains("complete")))
            {
                eventType = "tool_result";
                toolName = ExtractToolName(data);
                if (data.TryGetProperty("result", out var r) || data.TryGetProperty("output", out r) || data.TryGetProperty("content", out r) || data.TryGetProperty("stdout", out r))
                {
                    toolResultJson = r.ValueKind == JsonValueKind.String ? r.GetString() : r.GetRawText();
                }

                if (IsTaskCompleteToolName(toolName))
                {
                    taskCompleteSummary = ExtractTaskCompleteSummary(data) ?? ExtractTaskCompleteSummaryFromJson(toolResultJson);
                }

                content = TruncateLine(toolResultJson, 120);
                return true;
            }

            if (lower.Contains("tool") && (lower.Contains("call") || lower.Contains("execution") || lower.Contains("start") || lower.Contains("use")))
            {
                eventType = "tool_call";
                toolName = ExtractToolName(data) ?? "tool";
                if (data.TryGetProperty("args", out var a) || data.TryGetProperty("arguments", out a) || data.TryGetProperty("input", out a))
                {
                    toolArgsJson = a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText();
                }

                if (IsTaskCompleteToolName(toolName))
                {
                    taskCompleteSummary = ExtractTaskCompleteSummary(data) ?? ExtractTaskCompleteSummaryFromJson(toolArgsJson);
                }

                content = FormatToolCallSummary(toolName, toolArgsJson);
                return true;
            }

            if (lower is "assistant.message" or "message" or "content.delta" or "assistant.turn")
            {
                eventType = "message";
                content = data.TryGetProperty("content", out var c) ? c.GetString() : (data.TryGetProperty("text", out var t) ? t.GetString() : (data.TryGetProperty("delta", out var d) ? d.GetString() : null));
                return !string.IsNullOrWhiteSpace(content);
            }

            if (lower is "result" or "workflow.result")
            {
                eventType = "result";
                if (data.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                {
                    content = res.GetString();
                }
                else if (data.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    content = c.GetString();
                }
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractToolName(JsonElement data)
    {
        if (data.TryGetProperty("toolName", out var tnProp) && tnProp.ValueKind == JsonValueKind.String)
            return tnProp.GetString();
        if (data.TryGetProperty("tool", out var tProp) && tProp.ValueKind == JsonValueKind.String)
            return tProp.GetString();
        if (data.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
            return nProp.GetString();
        return null;
    }

    private static bool IsTaskCompleteToolName(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) &&
        (string.Equals(toolName, "task_complete", StringComparison.OrdinalIgnoreCase) ||
         toolName.EndsWith(".task_complete", StringComparison.OrdinalIgnoreCase) ||
         toolName.EndsWith("/task_complete", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractTaskCompleteSummary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty("summary", out var sProp) && sProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sProp.GetString()))
        {
            return sProp.GetString();
        }

        if (element.TryGetProperty("arguments", out var argsProp))
        {
            var fromArgs = argsProp.ValueKind == JsonValueKind.String
                ? ExtractTaskCompleteSummaryFromJson(argsProp.GetString())
                : ExtractTaskCompleteSummary(argsProp);
            if (!string.IsNullOrWhiteSpace(fromArgs)) return fromArgs;
        }

        if (element.TryGetProperty("args", out var aProp))
        {
            var fromArgs = aProp.ValueKind == JsonValueKind.String
                ? ExtractTaskCompleteSummaryFromJson(aProp.GetString())
                : ExtractTaskCompleteSummary(aProp);
            if (!string.IsNullOrWhiteSpace(fromArgs)) return fromArgs;
        }

        if (element.TryGetProperty("result", out var resProp))
        {
            if (resProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(resProp.GetString()))
                return resProp.GetString();
            if (resProp.ValueKind == JsonValueKind.Object)
            {
                if (resProp.TryGetProperty("summary", out var rsProp) && rsProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rsProp.GetString()))
                    return rsProp.GetString();
                if (resProp.TryGetProperty("content", out var rcProp) && rcProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rcProp.GetString()))
                    return rcProp.GetString();
            }
        }

        if (element.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cProp.GetString()))
        {
            return cProp.GetString();
        }

        return null;
    }

    private static string? ExtractTaskCompleteSummaryFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var trimmed = json.Trim();
        if (!trimmed.StartsWith('{')) return trimmed;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return ExtractTaskCompleteSummary(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatToolCallSummary(string? toolName, string? toolArgsJson)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return "Calling tool...";
        if (string.IsNullOrWhiteSpace(toolArgsJson)) return $"Calling {toolName}...";

        try
        {
            using var doc = JsonDocument.Parse(toolArgsJson);
            var root = doc.RootElement;
            if (string.Equals(toolName, "shell", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("command", out var cmd))
            {
                return $"Running shell: {cmd.GetString()}";
            }
            if (string.Equals(toolName, "read", StringComparison.OrdinalIgnoreCase) && (root.TryGetProperty("path", out var rPath) || root.TryGetProperty("file", out rPath)))
            {
                return $"Reading file: {rPath.GetString()}";
            }
            if (string.Equals(toolName, "write", StringComparison.OrdinalIgnoreCase) && (root.TryGetProperty("path", out var wPath) || root.TryGetProperty("file", out wPath)))
            {
                return $"Writing file: {wPath.GetString()}";
            }
        }
        catch { }

        return $"Calling {toolName}...";
    }

    private static string TruncateLine(string? input, int maxLen)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var clean = Regex.Replace(input, @"\s+", " ").Trim();
        return clean.Length <= maxLen ? clean : clean[..Math.Max(0, maxLen - 3)] + "...";
    }

    private static string BuildPayloadJson(PromptReceivedEvent promptEvent, string? copilotSessionId, string? terminalUsageJson)
    {
        JsonElement? usageElem = null;
        if (!string.IsNullOrWhiteSpace(terminalUsageJson))
        {
            try
            {
                using var uDoc = JsonDocument.Parse(terminalUsageJson);
                usageElem = uDoc.RootElement.Clone();
            }
            catch { }
        }

        if (usageElem.HasValue)
        {
            return JsonSerializer.Serialize(new
            {
                model = promptEvent.Model ?? "gpt-4o",
                tier = promptEvent.Tier,
                mode = promptEvent.Mode,
                contextSize = promptEvent.ContextSize,
                conversationId = copilotSessionId,
                usage = usageElem.Value
            });
        }

        return JsonSerializer.Serialize(new
        {
            model = promptEvent.Model ?? "gpt-4o",
            tier = promptEvent.Tier,
            mode = promptEvent.Mode,
            contextSize = promptEvent.ContextSize,
            conversationId = copilotSessionId
        });
    }
}

public class GenericCommandAdapter : IAgentAdapter
{
    public string AdapterType => "generic";

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = AgentAdapterFactory.ResolveWorkspaceDirectory(promptEvent);

        // If user configured a custom command (not empty and not default 'agy'), run it and parse JSONL or plain text output
        if (!string.IsNullOrWhiteSpace(preset.Command) &&
            !string.Equals(preset.Command, "agy", StringComparison.OrdinalIgnoreCase))
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "thought",
                Content = $"[@{preset.Id}] Dispatching prompt to command: {preset.Command} {preset.Arguments}".Trim()
            });

            var fullCommand = string.IsNullOrWhiteSpace(preset.Arguments)
                ? preset.Command
                : $"{preset.Command} {preset.Arguments}";

            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (isWindows)
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(fullCommand);
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(fullCommand);
            }

            psi.Environment["AGENTMESH_PROMPT"] = promptEvent.Prompt;
            psi.Environment["AGENTMESH_SESSION_ID"] = promptEvent.SessionId.ToString();
            psi.Environment["AGENTMESH_AGENT_ID"] = preset.Id;

            using var proc = new Process { StartInfo = psi };
            using var reg = ct.Register(() => AgentAdapterFactory.KillProcessTree(proc));
            proc.Start();

            try
            {
                var stdinPayload = JsonSerializer.Serialize(promptEvent);
                await proc.StandardInput.WriteLineAsync(stdinPayload);
                proc.StandardInput.Close();
            }
            catch (IOException)
            {
                // Process already exited without reading stdin
            }

            var plainTextBuffer = new StringBuilder();
            var emittedFinalResponse = false;

            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var line = await proc.StandardOutput.ReadLineAsync(ct);
                    if (line == null) break;
                    ct.ThrowIfCancellationRequested();

                    if (AgentAdapterFactory.TryParseJsonEventLine(line, out var parsed) && parsed != null)
                    {
                        await onEvent(parsed);
                        if (parsed.EventType == "response")
                        {
                            emittedFinalResponse = true;
                        }
                    }
                    else
                    {
                        plainTextBuffer.AppendLine(line);
                    }
                }

                ct.ThrowIfCancellationRequested();
                var stdErr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                ct.ThrowIfCancellationRequested();

                if (!emittedFinalResponse)
                {
                    var finalContent = plainTextBuffer.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(finalContent) && !string.IsNullOrWhiteSpace(stdErr))
                    {
                        finalContent = stdErr.Trim();
                    }
                    if (string.IsNullOrWhiteSpace(finalContent))
                    {
                        finalContent = $"Command `{fullCommand}` finished with exit code {proc.ExitCode}.";
                    }

                    ct.ThrowIfCancellationRequested();

                    var questions = AgentAdapterFactory.ExtractQuestionsFromOutput(finalContent);
                    if (questions.Count > 0)
                    {
                        var questionsJson = JsonSerializer.Serialize(new
                        {
                            questions = questions.Select(q => new
                            {
                                question = q.Question,
                                options = q.Options,
                                is_multi_select = q.IsMultiSelect
                            })
                        });

                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = finalContent
                        });

                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "tool_call",
                            ToolName = "ask_question",
                            Content = $"Asking {questions.Count} question{(questions.Count == 1 ? "" : "s")}",
                            ToolArgsJson = questionsJson
                        });
                        return;
                    }

                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = finalContent
                    });
                }

                return;
            }
            catch (OperationCanceledException)
            {
                AgentAdapterFactory.KillProcessTree(proc);
                throw;
            }
        }

        // Delegate to real Antigravity CLI adapter
        var agyAdapter = new AgyAdapter();
        await agyAdapter.ExecutePromptAsync(preset, promptEvent, onEvent, ct);
    }
}
