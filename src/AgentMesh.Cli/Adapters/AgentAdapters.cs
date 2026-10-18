using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentMesh.Cli.Models;

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

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunShellCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken ct)
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
        proc.Start();

        var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (proc.ExitCode, stdOut.Trim(), stdErr.Trim());
    }
}

public class AgyAdapter : IAgentAdapter
{
    private static readonly ConcurrentDictionary<Guid, string> SessionConversations = new();

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

        if (SessionConversations.TryGetValue(promptEvent.SessionId, out var existingConvId) &&
            !string.IsNullOrWhiteSpace(existingConvId))
        {
            psi.ArgumentList.Add("--conversation");
            psi.ArgumentList.Add(existingConvId);
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(promptEvent.Model.Trim());
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Effort))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(promptEvent.Effort.Trim());
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Mode))
        {
            psi.ArgumentList.Add("--mode");
            psi.ArgumentList.Add(promptEvent.Mode.Trim());
        }

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(promptEvent.Prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var streamedResponseBuilder = new StringBuilder();
        var emittedFinalResult = false;
        long lastInputTokens = 0, lastOutputTokens = 0, lastThinkingTokens = 0, lastTotalTokens = 0;
        double lastDuration = 0;

        while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
        {
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
                            SessionConversations[promptEvent.SessionId] = convId;
                        }
                    }
                }
                else if (evName == "step_update" && root.TryGetProperty("step_update", out var step))
                {
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
                    if (lastTotalTokens > 0 || lastDuration > 0)
                    {
                        structuredJson = JsonSerializer.Serialize(new
                        {
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
            catch
            {
                // Non-JSON line fallback
                streamedResponseBuilder.AppendLine(line);
            }
        }

        var stdErr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

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
            if (lastTotalTokens > 0 || lastDuration > 0)
            {
                structuredJson = JsonSerializer.Serialize(new
                {
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

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = fallbackText,
                StructuredPayloadJson = structuredJson
            });
        }
    }

    private static async Task ExecuteFallbackShellAsync(
        string cwd,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[@{preset.Id}] Processing prompt from {promptEvent.Sender}: \"{promptEvent.Prompt}\" in {cwd}"
        });

        var diagnosticCmd = SelectWorkspaceCommand(promptEvent.Prompt);
        var (exitCode, stdOut, stdErr) = await AgentAdapterFactory.RunShellCommandAsync(diagnosticCmd, cwd, ct);
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

            var escapedPrompt = promptEvent.Prompt.Replace("\"", "\\\"");
            var (exitCode, stdOut, stdErr) = await AgentAdapterFactory.RunShellCommandAsync($"\"{claudePath}\" -p \"{escapedPrompt}\"", cwd, ct);
            var output = !string.IsNullOrWhiteSpace(stdOut) ? stdOut : stdErr;

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = string.IsNullOrWhiteSpace(output)
                    ? $"Claude CLI completed with exit code {exitCode}."
                    : output
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

            if (!string.IsNullOrWhiteSpace(promptEvent.Model))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(promptEvent.Model.Trim());
            }

            if (!string.IsNullOrWhiteSpace(promptEvent.Mode))
            {
                psi.ArgumentList.Add("--mode");
                psi.ArgumentList.Add(promptEvent.Mode.Trim());
            }

            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(promptEvent.Prompt);
            psi.ArgumentList.Add("--allow-all");
            psi.ArgumentList.Add("--no-color");

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdOut = await proc.StandardOutput.ReadToEndAsync(ct);
            var stdErr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            var output = !string.IsNullOrWhiteSpace(stdOut) ? stdOut.Trim() : stdErr.Trim();
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = string.IsNullOrWhiteSpace(output)
                    ? $"Copilot CLI finished (exit code {proc.ExitCode})."
                    : output
            });
            return;
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

            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
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

            var stdErr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

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

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "response",
                    Content = finalContent
                });
            }

            return;
        }

        // Delegate to real Antigravity CLI adapter
        var agyAdapter = new AgyAdapter();
        await agyAdapter.ExecutePromptAsync(preset, promptEvent, onEvent, ct);
    }
}
