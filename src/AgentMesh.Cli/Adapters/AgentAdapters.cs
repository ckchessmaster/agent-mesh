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

    public static bool IsExecutableOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (File.Exists(command)) return true;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "", ".exe", ".cmd", ".bat", ".ps1" }
            : new[] { "" };

        foreach (var dir in paths)
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(dir, command + ext);
                if (File.Exists(fullPath)) return true;
            }
        }

        return false;
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
    public string AdapterType => "agy";

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = Environment.CurrentDirectory;

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[Antigravity CLI @{preset.Id}] Processing prompt from {promptEvent.Sender}: \"{promptEvent.Prompt}\"\nWorkspace: {cwd}"
        });

        // Determine diagnostic tool to run based on prompt
        var diagnosticCmd = SelectWorkspaceCommand(promptEvent.Prompt);
        var toolArgs = JsonSerializer.Serialize(new { command = diagnosticCmd, cwd }, new JsonSerializerOptions { WriteIndented = true });

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "tool_call",
            ToolName = "run_command",
            Content = $"Executing workspace inspection: {diagnosticCmd}",
            ToolArgsJson = toolArgs
        });

        var (exitCode, stdOut, stdErr) = await AgentAdapterFactory.RunShellCommandAsync(diagnosticCmd, cwd, ct);
        var combinedOutput = string.IsNullOrWhiteSpace(stdOut) ? stdErr : stdOut;
        if (string.IsNullOrWhiteSpace(combinedOutput))
        {
            combinedOutput = "(Command completed with no output)";
        }

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "tool_result",
            ToolName = "run_command",
            Content = $"Exit code {exitCode}",
            ToolResultJson = combinedOutput.Length > 4000 ? combinedOutput[..4000] + "\n... (truncated)" : combinedOutput
        });

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "response",
            Content = $"**Agent `@{preset.Id}` ({preset.Name}) completed execution.**\n\n" +
                      $"- **Prompt**: `{promptEvent.Prompt}`\n" +
                      $"- **Executed**: `{diagnosticCmd}` (exit code `{exitCode}`)\n\n" +
                      $"```\n{(combinedOutput.Length > 1500 ? combinedOutput[..1500] + "\n..." : combinedOutput)}\n```"
        });
    }

    internal static string SelectWorkspaceCommand(string prompt)
    {
        var trimmed = prompt.Trim();
        if (trimmed.StartsWith("$ "))
        {
            return trimmed[2..].Trim();
        }
        if (trimmed.StartsWith("run ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[4..].Trim();
        }

        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("git") || lower.Contains("status") || lower.Contains("branch") || lower.Contains("commit"))
        {
            return "git status -sb && git log -n 3 --oneline";
        }
        if (lower.Contains("test") || lower.Contains("build"))
        {
            return "dotnet --version && git status -sb";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "dir /b"
            : "ls -la";
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
        var cwd = Environment.CurrentDirectory;
        var hasClaudeBinary = AgentAdapterFactory.IsExecutableOnPath(preset.Command ?? "claude");

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[Claude Code @{preset.Id}] Analyzing request: \"{promptEvent.Prompt}\" in {cwd}"
        });

        if (hasClaudeBinary)
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "tool_call",
                ToolName = "claude_cli",
                Content = "Invoking claude -p headless execution",
                ToolArgsJson = JsonSerializer.Serialize(new { command = "claude", args = new[] { "-p", promptEvent.Prompt } })
            });

            var escapedPrompt = promptEvent.Prompt.Replace("\"", "\\\"");
            var (exitCode, stdOut, stdErr) = await AgentAdapterFactory.RunShellCommandAsync($"claude -p \"{escapedPrompt}\"", cwd, ct);
            var output = !string.IsNullOrWhiteSpace(stdOut) ? stdOut : stdErr;

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "tool_result",
                ToolName = "claude_cli",
                Content = $"claude exited with code {exitCode}",
                ToolResultJson = output
            });

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = string.IsNullOrWhiteSpace(output)
                    ? $"Claude CLI completed with exit code {exitCode}."
                    : output
            });
            return;
        }

        // Fallback workspace inspection when claude binary is not installed locally
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
        var cwd = Environment.CurrentDirectory;

        // If user configured a custom command, run it and parse JSONL or plain text output
        if (!string.IsNullOrWhiteSpace(preset.Command))
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

            // Write prompt JSON to stdin and close (ignore Broken Pipe if process doesn't read stdin)
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

        // Default workspace execution when no custom command is provided
        var agyAdapter = new AgyAdapter();
        await agyAdapter.ExecutePromptAsync(preset, promptEvent, onEvent, ct);
    }
}
