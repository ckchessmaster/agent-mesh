using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;

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

    public static string ResolveWorkspaceDirectory()
    {
        var cwd = Environment.CurrentDirectory;
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

    public static void KillProcessTree(Process proc)
    {
        try
        {
            var pid = 0;
            try { pid = proc.Id; } catch { }
            try { if (proc.HasExited) return; } catch { }

            // On Linux/macOS, kill child processes first so background scripts/daemons don't survive as orphans
            if (pid > 0 && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using var pkill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "pkill",
                        ArgumentList = { "-9", "-P", pid.ToString() },
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    pkill?.WaitForExit(300);
                }
                catch { }
            }

            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch { }

            if (pid > 0 && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using var killProc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "kill",
                        ArgumentList = { "-9", pid.ToString() },
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    killProc?.WaitForExit(300);
                }
                catch { }
            }

            try { proc.StandardInput?.Close(); } catch { }
        }
        catch { }
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunShellCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken ct,
        TimeSpan? timeout = null,
        Guid? sessionId = null,
        string? commandId = null)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isWindows)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        using var proc = new Process { StartInfo = psi };
        using var cmdCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cmdCts.Token);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(180);

        IDisposable? regDisposable = null;
        if (sessionId.HasValue)
        {
            regDisposable = ActiveCommandRegistry.Register(sessionId.Value, commandId, proc, cmdCts);
        }

        using var reg = linkedCts.Token.Register(() => KillProcessTree(proc));
        proc.Start();

        try
        {
            var stdOutTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stdErrTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);

            var waitTask = proc.WaitForExitAsync(linkedCts.Token);
            var timeoutTask = Task.Delay(effectiveTimeout, CancellationToken.None);

            var finished = await Task.WhenAny(waitTask, timeoutTask);
            linkedCts.Token.ThrowIfCancellationRequested();

            if (finished == timeoutTask && !proc.HasExited)
            {
                KillProcessTree(proc);
                return (-1, string.Empty, $"Error: Command execution timed out after {effectiveTimeout.TotalSeconds:F0}s.");
            }

            await waitTask;
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            linkedCts.Token.ThrowIfCancellationRequested();
            return (proc.ExitCode, stdOut.Trim(), stdErr.Trim());
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(proc);
            if (cmdCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return (-1, string.Empty, "🛑 Command aborted by operator.");
            }
            throw;
        }
        finally
        {
            regDisposable?.Dispose();
        }
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

public record CommandExecutionHandle(
    Guid SessionId,
    string? CommandId,
    Process Process,
    CancellationTokenSource Cts,
    DateTimeOffset StartedAt);

public static class ActiveCommandRegistry
{
    private static readonly ConcurrentDictionary<string, CommandExecutionHandle> ActiveCommands = new();

    public static IDisposable Register(Guid sessionId, string? commandId, Process proc, CancellationTokenSource cts)
    {
        var handle = new CommandExecutionHandle(sessionId, commandId, proc, cts, DateTimeOffset.UtcNow);
        var key = $"{sessionId}:{(string.IsNullOrWhiteSpace(commandId) ? "current" : commandId)}";
        ActiveCommands[key] = handle;
        ActiveCommands[$"{sessionId}:current"] = handle;

        return new RegistrationDisposable(() =>
        {
            ActiveCommands.TryRemove(key, out _);
            ActiveCommands.TryRemove($"{sessionId}:current", out _);
        });
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
                AgentAdapterFactory.KillProcessTree(handle.Process);
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

public static class SessionConversationStore
{
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentmesh",
        "session_conversations.json");
    private static readonly object FileLock = new();

    static SessionConversationStore()
    {
        LoadFromDisk();
    }

    private static void LoadFromDisk()
    {
        try
        {
            if (File.Exists(CacheFilePath))
            {
                var json = File.ReadAllText(CacheFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    foreach (var (k, v) in dict)
                    {
                        if (Guid.TryParse(k, out var sid) && !string.IsNullOrWhiteSpace(v))
                        {
                            Cache[sid] = v;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static void SaveToDisk()
    {
        try
        {
            lock (FileLock)
            {
                var dir = Path.GetDirectoryName(CacheFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var dict = Cache.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
                File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(dict));
            }
        }
        catch { }
    }

    public static string? Get(Guid sessionId)
    {
        return Cache.TryGetValue(sessionId, out var convId) ? convId : null;
    }

    public static void Set(Guid sessionId, string convId)
    {
        if (string.IsNullOrWhiteSpace(convId)) return;
        Cache[sessionId] = convId;
        SaveToDisk();
    }
}

public class AgyAdapter : IAgentAdapter
{
    private static readonly ConcurrentDictionary<Guid, string> SessionConversations = new();

    public static string? GetSessionConversationId(Guid sessionId)
    {
        return SessionConversationStore.Get(sessionId) ?? (SessionConversations.TryGetValue(sessionId, out var convId) ? convId : null);
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
        var cwd = !string.IsNullOrWhiteSpace(promptEvent.WorkingDirectory)
            ? promptEvent.WorkingDirectory
            : AgentAdapterFactory.ResolveWorkspaceDirectory();

        var agyPath = AgentAdapterFactory.ResolveExecutablePath(preset.Command ?? "agy")
                      ?? AgentAdapterFactory.ResolveExecutablePath("agy");

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
        var existingConvId = !string.IsNullOrWhiteSpace(promptEvent.ConversationId)
            ? promptEvent.ConversationId
            : GetSessionConversationId(promptEvent.SessionId);

        if (!string.IsNullOrWhiteSpace(existingConvId))
        {
            activeConvId = existingConvId;
            psi.ArgumentList.Add("--conversation");
            psi.ArgumentList.Add(existingConvId);
        }

        string? resolvedModel = null;
        if (!string.IsNullOrWhiteSpace(promptEvent.Model))
        {
            var rawModel = promptEvent.Model.Trim();
            if (rawModel.Equals("claude-3-7-sonnet", StringComparison.OrdinalIgnoreCase) ||
                rawModel.Equals("claude-3-5-sonnet", StringComparison.OrdinalIgnoreCase))
            {
                resolvedModel = "claude-sonnet-5-5";
            }
            else
            {
                resolvedModel = rawModel;
            }
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(resolvedModel);
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Effort))
        {
            var rawEffort = promptEvent.Effort.Trim();
            if (rawEffort.Equals("max", StringComparison.OrdinalIgnoreCase) ||
                rawEffort.Equals("xhigh", StringComparison.OrdinalIgnoreCase))
            {
                rawEffort = "high";
            }

            var rawModel = promptEvent.Model?.Trim() ?? "";
            var modelRejectsEffort = (rawModel.Equals("claude-3-7-sonnet", StringComparison.OrdinalIgnoreCase) ||
                                      rawModel.Equals("claude-3-5-sonnet", StringComparison.OrdinalIgnoreCase)) &&
                                     resolvedModel == rawModel;
            if (!modelRejectsEffort)
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
                                SessionConversations[promptEvent.SessionId] = convId;
                                SessionConversationStore.Set(promptEvent.SessionId, convId);
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
                                if (tiProp.TryGetProperty("parameters", out var paramsProp))
                                {
                                    argsJson = paramsProp.GetRawText();
                                    if (paramsProp.TryGetProperty("CommandLine", out var cmdProp))
                                    {
                                        summary = $"Running command: {cmdProp.GetString()}";
                                    }
                                    else if (paramsProp.TryGetProperty("AbsolutePath", out var pathProp))
                                    {
                                        summary = $"Inspecting file: {Path.GetFileName(pathProp.GetString())}";
                                    }
                                }
                                if (tiProp.TryGetProperty("output", out var outProp))
                                {
                                    outputText = outProp.ValueKind == JsonValueKind.String
                                        ? outProp.GetString()
                                        : outProp.GetRawText();
                                }
                            }

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
                                contextSize = promptEvent.ContextSize,
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
                        contextSize = promptEvent.ContextSize,
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
        var cwd = !string.IsNullOrWhiteSpace(promptEvent.WorkingDirectory)
            ? promptEvent.WorkingDirectory
            : AgentAdapterFactory.ResolveWorkspaceDirectory();
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
            var (exitCode, stdOut, stdErr) = await AgentAdapterFactory.RunShellCommandAsync($"\"{claudePath}\"{autocompactArg} -p \"{escapedPrompt}\"", cwd, ct);
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

public class CopilotAdapter : IAgentAdapter
{
    public string AdapterType => "copilot";

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = !string.IsNullOrWhiteSpace(promptEvent.WorkingDirectory)
            ? promptEvent.WorkingDirectory
            : AgentAdapterFactory.ResolveWorkspaceDirectory();
        var copilotPath = AgentAdapterFactory.ResolveExecutablePath("copilot");

        if (copilotPath != null)
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "thought",
                Content = $"[GitHub Copilot CLI @{preset.Id}] Running prompt via {copilotPath}..."
            });

            var psi = new ProcessStartInfo
            {
                FileName = copilotPath,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Session ID resumption for multi-turn conversations
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

            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(promptEnvelope);
            psi.ArgumentList.Add("--allow-all");
            psi.ArgumentList.Add("--no-color");

            using var proc = new Process { StartInfo = psi };
            using var reg = ct.Register(() => AgentAdapterFactory.KillProcessTree(proc));
            proc.Start();

            try
            {
                var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
                var stdErrTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                var stdOut = await stdOutTask;
                var stdErr = await stdErrTask;

                ct.ThrowIfCancellationRequested();

                var output = !string.IsNullOrWhiteSpace(stdOut) ? stdOut.Trim() : stdErr.Trim();

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

                    var copilotPayloadJson = JsonSerializer.Serialize(new
                    {
                        model = promptEvent.Model ?? "gpt-4o",
                        tier = promptEvent.Tier,
                        mode = promptEvent.Mode,
                        contextSize = promptEvent.ContextSize
                    });

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
                    return;
                }

                var defaultCopilotPayloadJson = JsonSerializer.Serialize(new
                {
                    model = promptEvent.Model ?? "gpt-4o",
                    tier = promptEvent.Tier,
                    mode = promptEvent.Mode,
                    contextSize = promptEvent.ContextSize
                });

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "response",
                    Content = string.IsNullOrWhiteSpace(output)
                        ? $"Copilot CLI finished (exit code {proc.ExitCode})."
                        : output,
                    StructuredPayloadJson = defaultCopilotPayloadJson
                });
                return;
            }
            catch (OperationCanceledException)
            {
                AgentAdapterFactory.KillProcessTree(proc);
                throw;
            }
        }

        var fallback = new AgyAdapter();
        await fallback.ExecutePromptAsync(preset, promptEvent, onEvent, ct);
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
        var cwd = !string.IsNullOrWhiteSpace(promptEvent.WorkingDirectory)
            ? promptEvent.WorkingDirectory
            : AgentAdapterFactory.ResolveWorkspaceDirectory();

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
