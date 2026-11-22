using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using ModelContextProtocol.Client;

namespace AgentMesh.Cli.Adapters;

/// <summary>
/// Shared local workspace, platform control-plane, and persona MCP tool host used by HTTP
/// tool-loop adapters (<see cref="OpenAiAdapter"/> and <see cref="OpenRouterAdapter"/>).
/// </summary>
public static class AgentToolExecutor
{
    public static Func<Guid, string, string, Task>? CommandOutputBroadcaster { get; set; }

    /// <summary>
    /// Optional callback invoked when the agent calls a platform tool
    /// (<c>manage_task</c>, <c>post_job_message</c>, <c>open_pull_request</c>, <c>save_memory</c>, <c>list_memories</c>).
    /// Args: (toolName, argsJson, sessionId, personaId, agentId, ct).
    /// </summary>
    public static Func<string, string, Guid?, string?, string?, CancellationToken, Task<string>>? PlatformToolExecutor { get; set; }

    internal static Func<ResolvedMcpServerConfig, string?, IClientTransport?>? McpTransportFactoryForTest { get; set; }

    internal static void ResetMcpTransportFactoryForTest()
    {
        McpTransportFactoryForTest = null;
    }

    public static int? ResolveCommandTimeoutSeconds(AgentPresetConfig? preset)
    {
        if (preset?.TimeoutSeconds is int configured && configured > 0)
        {
            return configured;
        }

        return null;
    }

    public static string TruncateToolResult(string result, int maxChars = 50_000)
    {
        if (string.IsNullOrEmpty(result) || result.Length <= maxChars) return result;
        var headLength = maxChars / 2;
        var tailLength = maxChars / 3;
        var omitted = result.Length - (headLength + tailLength);
        return $"{result[..headLength]}\n\n[... Output truncated: {omitted} characters omitted to preserve context window ...]\n\n{result[^tailLength..]}";
    }

    public static async Task<McpClientManager> ConnectMcpServersAsync(
        IReadOnlyList<ResolvedMcpServerConfig> servers,
        AgentPresetConfig preset,
        string cwd,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var manager = await McpClientManager.CreateAsync(servers, cwd, McpTransportFactoryForTest, ct);
        foreach (var diag in manager.Diagnostics.Where(d => !d.Connected))
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "thought",
                Content = $"[@{preset.Id}] Warning: MCP server '{diag.ServerName}' ({diag.ServerId}) failed to connect: {diag.ErrorMessage}"
            });
        }

        var connectedCount = manager.Diagnostics.Count(d => d.Connected);
        if (connectedCount > 0 || manager.Tools.Count > 0)
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "thought",
                Content = $"[@{preset.Id}] Connected {connectedCount}/{manager.Diagnostics.Count} MCP server(s); bridged {manager.Tools.Count} MCP tool(s)."
            });
        }

        return manager;
    }

    public static async Task<string> ExecuteBridgedOrLocalToolAsync(
        string toolName,
        string argsJson,
        string cwd,
        McpClientManager? mcpManager,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        string commandId,
        bool isReadOnlyPhase,
        CancellationToken ct)
    {
        if (mcpManager != null && mcpManager.HasTool(toolName))
        {
            return await mcpManager.CallToolAsync(toolName, argsJson, ct);
        }

        var toolTimeout = ResolveCommandTimeoutSeconds(preset);
        return await ExecuteToolLocallyAsync(
            toolName,
            argsJson,
            cwd,
            ct,
            toolTimeout,
            promptEvent.SessionId,
            commandId,
            onOutputChunk: async (chunk) =>
            {
                if (CommandOutputBroadcaster != null)
                {
                    try { await CommandOutputBroadcaster(promptEvent.SessionId, commandId, chunk); } catch { }
                }
            },
            personaId: promptEvent.PersonaId,
            agentId: promptEvent.AgentId,
            isDiscoveryPhase: isReadOnlyPhase);
    }

    public static string BuildDefaultSystemPrompt(AgentPresetConfig preset, PromptReceivedEvent promptEvent, string cwd)
    {
        var sb = new StringBuilder();
        var displayName = !string.IsNullOrWhiteSpace(promptEvent.PersonaName)
            ? promptEvent.PersonaName
            : (!string.IsNullOrWhiteSpace(preset.Name) ? preset.Name : preset.Id);
        var role = !string.IsNullOrWhiteSpace(promptEvent.PersonaRole)
            ? promptEvent.PersonaRole
            : preset.Role;

        sb.AppendLine($"You are \"{displayName}\" ({role}), an autonomous AI engineering agent connected via AgentMesh.");
        sb.AppendLine($"Workspace directory: {cwd}");

        if (!string.IsNullOrWhiteSpace(promptEvent.PersonaPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("[Persona Instructions]");
            sb.AppendLine(promptEvent.PersonaPrompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Mode))
        {
            sb.AppendLine();
            sb.AppendLine($"[Execution Mode: {promptEvent.Mode}]");
            if (string.Equals(promptEvent.Mode, "plan", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("You are in PLAN mode. Explore the codebase and formulate a comprehensive implementation plan. Do NOT write or edit project files.");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[Autonomous Tool Usage & Environment Guidelines]");
        sb.AppendLine("You have access to autonomous tools to execute shell commands, read files, write files, and edit files in the workspace.");
        sb.AppendLine("- Use `run_command` to inspect the project, run tests, execute builds, and check git status.");
        sb.AppendLine("- Use `read_file` to inspect code and text files.");
        sb.AppendLine("- Use `write_file` or `edit_file` to implement solutions.");
        sb.AppendLine("- When you need the operator to clarify something or decide between options, call `ask_question` with one entry per question and its answer options. That is the only question channel they can see and answer: a question written into message text is never shown to them, and the job advances on your guess. Do not put questions in `post_job_message`.");
        sb.AppendLine("- With no function tools available, emit the same object as a fenced code block tagged `ask_question` containing `{\"questions\":[{\"question\":\"...\",\"options\":[\"...\"]}]}`; that block is the only accepted prose form.");
        sb.AppendLine("- For an escalation or a plan that needs a reply, use `post_job_message(requires_reply: true)`. For a question, `ask_question`.");
        sb.AppendLine("- Platform MCP Tools: `manage_task`, `post_job_message`, and `open_pull_request` are control plane tools. In Stage 1 Discovery, file edits and git mutations are blocked: use `manage_task(action: 'set_checklist')` to register tasks and `post_job_message(requires_reply: true)` to post the plan.");
        sb.AppendLine("- Environment Topology: You run in an isolated Linux container runner. The Control Plane API does not mount /workspace directly; state and diffs are reported through platform tools.");
        sb.AppendLine("- Vision / Image Handling: Attached image files are provided directly to your multimodal visual input when supported. You can inspect, analyze, and describe them visually. However, binary image files on disk cannot be read as plain text via `read_file`. Do NOT attempt to install OCR packages (e.g., tesseract), write custom Python decoders, or parse raw image bytes with shell commands.");
        sb.AppendLine("- Be concise and lead with working code and clear implementation.");

        return sb.ToString();
    }

    public static List<object> GetToolDefinitions() => GetToolDefinitions(extraTools: null);

    public static List<object> GetToolDefinitions(IEnumerable<object>? extraTools)
    {
        var tools = new List<object>
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "run_command",
                    description = "Execute a shell command in the workspace directory. Use this to run builds, tests, git commands, and inspection tools. Long output is preserved in full on disk and returned as a head/tail preview; the result names the log file to grep.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new { type = "string", description = "The exact shell command line string to execute." },
                            workingDirectory = new { type = "string", description = "Optional subdirectory or working directory. Defaults to workspace root." },
                            timeout_seconds = new { type = "integer", description = "Optional wall-clock limit in seconds (5-600, default 180). Use a larger value for full builds or test suites; the command's whole process tree is killed if it exceeds the limit." }
                        },
                        required = new[] { "command" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "read_file",
                    description = "Read the contents of a text file from the workspace. Supports reading files in parallel in a single response.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "The file path relative to workspace root or absolute path." },
                            startLine = new { type = "integer", description = "Optional 1-based start line." },
                            endLine = new { type = "integer", description = "Optional 1-based end line." }
                        },
                        required = new[] { "path" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "ask_question",
                    description = "Ask the human operator questions when you need clarification, decision input, or alignment before proceeding.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            questions = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        question = new { type = "string", description = "The question to ask." },
                                        options = new { type = "array", items = new { type = "string" }, description = "List of options for user to select from." },
                                        is_multi_select = new { type = "boolean", description = "Whether user can select multiple choices." }
                                    },
                                    required = new[] { "question" }
                                }
                            }
                        },
                        required = new[] { "questions" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "manage_task",
                    description = "Manage the living task checklist and task statuses for the active job. In Stage 1 Discovery, decompose the objective into atomic, verifiable tasks sized to the scope of the work (typically 3-6 tasks, or more/fewer as needed) and register them via action='set_checklist'. In Stage 3, update task status with action='update_task_status'.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new
                            {
                                type = "string",
                                description = "The action to perform: 'set_checklist' (to register checklist tasks), 'update_task_status', 'get_checklist', or 'get_details'.",
                                @enum = new[] { "set_checklist", "update_task_status", "get_checklist", "get_details", "set_status", "list_all" }
                            },
                            tasks = new
                            {
                                type = "array",
                                description = "List of atomic, verifiable tasks when action is 'set_checklist' (1..25 tasks; typically 3-6 for standard features).",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        title = new { type = "string", description = "Clear, actionable title of the task." },
                                        target_file = new { type = "string", description = "Target file or component path." },
                                        verification_method = new { type = "string", description = "Exact verification command (e.g. 'dotnet test', 'npm test')." }
                                    },
                                    required = new[] { "title" }
                                }
                            },
                            task_id = new { type = "string", description = "Identifier of the task being updated." },
                            status = new
                            {
                                type = "string",
                                description = "Target status when updating task: 'todo', 'in_progress', 'awaiting_review', or 'completed'.",
                                @enum = new[] { "todo", "in_progress", "awaiting_review", "completed" }
                            },
                            progress_note = new { type = "string", description = "1-sentence heartbeat summarizing the state change." },
                            notes = new { type = "string", description = "Optional context notes." }
                        },
                        required = new[] { "action" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "post_job_message",
                    description = "Post an update, clarifying question, or architectural escalation to the job's shared ledger. Set requires_reply=true to pause execution and await user/lead response.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            content = new { type = "string", description = "Markdown message body." },
                            target_persona = new { type = "string", description = "Recipient: 'operator' or assigned teammate persona ID." },
                            requires_reply = new { type = "boolean", description = "If true, halts turn and parks session awaiting response." },
                            job_id = new { type = "string", description = "Optional job GUID." }
                        },
                        required = new[] { "content" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "save_memory",
                    description = "Store persistent architectural notes, codebase quirks, or test workarounds for future sessions.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new
                            {
                                type = "string",
                                description = "Category of memory: 'environment' (paths, SDKs, ports, daemon sockets), 'build_and_test' (flags, mocks, test quirks), or 'codebase_gotcha' (domain rules, architectural constraints).",
                                @enum = new[] { "environment", "build_and_test", "codebase_gotcha" }
                            },
                            key = new { type = "string", description = "Unique identifier (e.g. ef-dbcontext-pooling-rule)." },
                            value = new { type = "string", description = "Concise documentation of the learned knowledge or guideline." },
                            is_global = new { type = "boolean", description = "True if this memory applies across all projects; false if specific to this project." }
                        },
                        required = new[] { "category", "key", "value" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "list_memories",
                    description = "List or search saved persistent memories (both project-scoped and global '*') for your persona so you can inspect or update stale environment entries without duplicating keys.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Optional case-insensitive substring filter matched against memory keys and values." },
                            category = new
                            {
                                type = "string",
                                description = "Optional category filter: 'environment', 'build_and_test', or 'codebase_gotcha'.",
                                @enum = new[] { "environment", "build_and_test", "codebase_gotcha" }
                            }
                        }
                    }
                }
            }
        };

        tools.Add(new
        {
            type = "function",
            function = new
            {
                name = "write_file",
                description = "Create a new file or completely overwrite an existing file with the given content.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "The file path relative to workspace root or absolute path." },
                        content = new { type = "string", description = "The full content to write to the file." }
                    },
                    required = new[] { "path", "content" }
                }
            }
        });

        tools.Add(new
        {
            type = "function",
            function = new
            {
                name = "edit_file",
                description = "Replace a target string within an existing file with replacement text. The target must occur exactly once — include surrounding lines to make it unique — or the edit is refused with the occurrence count, unless replace_all is set.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "The file path relative to workspace root or absolute path." },
                        target = new { type = "string", description = "The exact character sequence in the file to be replaced." },
                        replacement = new { type = "string", description = "The text to replace target with." },
                        replace_all = new { type = "boolean", description = "Set true only when every occurrence of the exact target string should be replaced. Default false: an ambiguous target is refused rather than silently replacing everywhere." }
                    },
                    required = new[] { "path", "target", "replacement" }
                }
            }
        });

        tools.Add(new
        {
            type = "function",
            function = new
            {
                name = "open_pull_request",
                description = "Register a real GitHub Pull Request opened via 'agentmesh pr open' (or submit the active branch for Stage 4 Two-Man Review Gate). Run 'agentmesh pr open' in your persona container first.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string", description = "Pull request title following Conventional Commits (e.g. 'feat(api): add tool X')." },
                        summary_3r = new
                        {
                            type = "object",
                            description = "The structured 3-R summary that becomes the PR body.",
                            properties = new
                            {
                                result = new { type = "string", description = "What was built and what verifies it (files modified, test commands, passing test counts)." },
                                reasoning = new { type = "string", description = "Why this approach was taken and key architectural trade-offs." },
                                risk = new { type = "string", description = "Potential risks, edge cases, regression risks, or operational caveats." }
                            },
                            required = new[] { "result", "reasoning", "risk" }
                        },
                        branch_name = new { type = "string", description = "The local task/job branch (optional; defaults to the job's git branch)." },
                        pr_url = new { type = "string", description = "Real GitHub Pull Request URL returned by 'agentmesh pr open'." },
                        pr_number = new { type = "integer", description = "Real GitHub Pull Request number returned by 'agentmesh pr open'." },
                        head_sha = new { type = "string", description = "Current HEAD commit SHA pushed for this PR." }
                    },
                    required = new[] { "title", "summary_3r" }
                }
            }
        });

        if (extraTools != null)
        {
            tools.AddRange(extraTools);
        }

        return tools;
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".tiff", ".tif", ".svg"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".tiff", ".tif",
        ".pdf", ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".iso", ".dmg",
        ".mp3", ".mp4", ".wav", ".ogg", ".avi", ".mov", ".webm", ".mkv",
        ".wasm", ".pyc", ".class", ".o", ".obj"
    };

    private static async Task<bool> IsBinaryFileAsync(string filePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath);
        if (BinaryExtensions.Contains(ext))
        {
            return true;
        }

        try
        {
            const int bufferSize = 8192;
            var buffer = new byte[bufferSize];
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
            for (var i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // If checking fails, proceed to normal read
        }

        return false;
    }

    internal static string ResolveDefaultToolWorkingDirectory(string? cwd)
    {
        if (!string.IsNullOrWhiteSpace(cwd) &&
            !string.Equals(cwd.Trim(), "/workspace", StringComparison.Ordinal) &&
            !string.Equals(cwd.Trim(), "/workspace/repo", StringComparison.Ordinal))
        {
            return cwd.Trim();
        }

        return AgentAdapterFactory.ResolveWorkspaceDirectory();
    }

    public static async Task<string> ExecuteToolLocallyAsync(
        string toolName,
        string argsJson,
        string cwd,
        CancellationToken ct,
        int? timeoutSeconds = null,
        Guid? sessionId = null,
        string? commandId = null,
        Func<string, Task>? onOutputChunk = null,
        string? personaId = null,
        string? agentId = null,
        bool isDiscoveryPhase = false)
    {
        var rawResult = await ExecuteToolLocallyRawAsync(toolName, argsJson, cwd, ct, timeoutSeconds, sessionId, commandId, onOutputChunk, personaId, agentId, isDiscoveryPhase);
        var truncated = TruncateToolResult(rawResult);
        return truncated.IndexOf('\0') >= 0 ? truncated.Replace("\0", string.Empty) : truncated;
    }

    private static async Task<string> ExecuteToolLocallyRawAsync(
        string toolName,
        string argsJson,
        string cwd,
        CancellationToken ct,
        int? timeoutSeconds = null,
        Guid? sessionId = null,
        string? commandId = null,
        Func<string, Task>? onOutputChunk = null,
        string? personaId = null,
        string? agentId = null,
        bool isDiscoveryPhase = false)
    {
        ct.ThrowIfCancellationRequested();

        cwd = ResolveDefaultToolWorkingDirectory(cwd);

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            var normalizedTool = toolName.ToLowerInvariant();

            if (isDiscoveryPhase)
            {
                if (normalizedTool is "write_file" or "write_to_file" or "edit_file" or "replace_file_content")
                {
                    return "Error: File modifications are forbidden during Stage 1 Discovery, Plan mode, and while awaiting plan approval. Complete discovery and receive plan approval before starting implementation.";
                }

                if (normalizedTool is "open_pull_request")
                {
                    return "Error: Opening pull requests is forbidden during Stage 1 Discovery, Plan mode, and while awaiting plan approval. Complete discovery using manage_task(action: 'set_checklist') and receive plan approval before starting implementation.";
                }

                if (normalizedTool is "run_command" or "bash" or "command")
                {
                    var cmd = GetPropertyString(root, "command", "CommandLine", "cmd", "commandLine", "script", "shell_command") ?? string.Empty;
                    if (IsMutatingGitCommand(cmd))
                    {
                        return "Error: Committing, pushing, or creating pull requests is forbidden during Stage 1 Discovery, Plan mode, and while awaiting plan approval. Your objective is read-only exploration and checklist registration via manage_task(action: 'set_checklist').";
                    }
                }
            }

            switch (normalizedTool)
            {
                case "manage_task":
                case "post_job_message":
                case "open_pull_request":
                case "save_memory":
                case "list_memories":
                case "search_memory":
                {
                    if (PlatformToolExecutor != null)
                    {
                        return await PlatformToolExecutor(toolName, argsJson, sessionId, personaId, agentId, ct);
                    }
                    return $"Error: Platform tool '{toolName}' is not available (no executor registered).";
                }

                case "run_command":
                case "bash":
                case "command":
                {
                    var cmd = GetPropertyString(root, "command", "CommandLine", "cmd", "commandLine", "script", "shell_command");
                    if (string.IsNullOrWhiteSpace(cmd)) return "Error: 'command' argument is required.";

                    var workDir = GetPropertyString(root, "workingDirectory", "cwd", "workDir");
                    var effectiveDir = !string.IsNullOrWhiteSpace(workDir)
                        ? (Path.IsPathRooted(workDir) ? workDir : Path.Combine(cwd, workDir))
                        : cwd;

                    if (!Directory.Exists(effectiveDir))
                    {
                        Directory.CreateDirectory(effectiveDir);
                    }

                    var requestedTimeout = GetPropertyInt(root, "timeout_seconds", "timeoutSeconds", "timeout")
                        ?? timeoutSeconds;

                    var result = await AgentAdapterFactory.RunCommandAsync(
                        cmd,
                        effectiveDir,
                        ct,
                        timeout: null,
                        sessionId: sessionId,
                        commandId: commandId,
                        onOutputChunk: onOutputChunk,
                        timeoutSeconds: requestedTimeout);

                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(result.Output)) sb.AppendLine(result.Output);

                    sb.Append($"Exit Code: {result.ExitCode}");
                    if (result.ExitCode == 1 && IsBenignExitOneCommand(cmd))
                    {
                        sb.Append(" — note: exit 1 is a NORMAL RESULT for this command (no matches / files differ / condition false). Treat it as an answer, not a failure.");
                    }

                    if (result.LogFilePath != null && !result.Output.Contains(result.LogFilePath, StringComparison.Ordinal))
                    {
                        sb.AppendLine();
                        sb.Append($"Full output: {result.LogFilePath}");
                    }

                    return sb.ToString().Trim();
                }

                case "read_file":
                case "view_file":
                {
                    var path = GetPropertyString(root, "path", "Path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath", "uri");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' argument is required.";

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    if (!File.Exists(fullPath)) return $"Error: File '{path}' does not exist.";

                    if (await IsBinaryFileAsync(fullPath, ct))
                    {
                        var ext = Path.GetExtension(fullPath);
                        var fi = new FileInfo(fullPath);
                        if (ImageExtensions.Contains(ext) && !string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"[Image file: '{path}' ({fi.Length:N0} bytes, format: {ext.TrimStart('.').ToUpperInvariant()}). This is a binary image file and cannot be read as plain text. Attached images are already provided directly to your multimodal visual context in the prompt. Do NOT attempt to install OCR packages (e.g., tesseract), write custom Python decoders, or parse raw image bytes with shell commands. If you need details about an unattached image, ask the user to attach or describe it.]";
                        }
                        return $"[Binary file: '{path}' ({fi.Length:N0} bytes). This is a binary file and cannot be read as plain text.]";
                    }

                    var lines = await File.ReadAllLinesAsync(fullPath, ct);
                    int startLine = GetPropertyInt(root, "startLine", "StartLine", "start_line") ?? 1;
                    int endLine = GetPropertyInt(root, "endLine", "EndLine", "end_line") ?? lines.Length;

                    startLine = Math.Max(1, startLine);
                    endLine = Math.Min(lines.Length, Math.Max(startLine, endLine));

                    var sb = new StringBuilder();
                    for (int i = startLine - 1; i < endLine; i++)
                    {
                        sb.AppendLine($"{i + 1}: {lines[i]}");
                    }
                    return sb.ToString().TrimEnd();
                }

                case "write_file":
                case "write_to_file":
                {
                    var path = GetPropertyString(root, "path", "Path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' argument is required.";

                    var content = GetPropertyString(root, "content", "Content", "text", "CodeContent", "code_content");
                    content ??= string.Empty;

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
                    return $"Successfully wrote {content.Length} characters to '{path}'.";
                }

                case "edit_file":
                case "replace_file_content":
                {
                    var path = GetPropertyString(root, "path", "Path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' argument is required.";

                    var target = GetPropertyString(root, "target", "Target", "old_string", "oldText", "TargetContent", "target_content", "old_content", "search", "find");
                    var replacement = GetPropertyString(root, "replacement", "Replacement", "new_string", "newText", "ReplacementContent", "replacement_content", "new_content", "replace") ?? string.Empty;
                    var replaceAll = GetPropertyBool(root, "replace_all", "replaceAll");

                    if (string.IsNullOrEmpty(target)) return "Error: 'target' argument is required.";

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    if (!File.Exists(fullPath)) return $"Error: File '{path}' does not exist. Verify the path with 'run_command' (ls) or read_file before editing; do not edit files you have not confirmed exist.";

                    var fileText = await File.ReadAllTextAsync(fullPath, ct);
                    var occurrences = CountSubstringOccurrences(fileText, target);
                    var viaNormalizedMatch = false;
                    var normalizedStart = 0;
                    var normalizedLength = 0;

                    if (occurrences == 0)
                    {
                        var strippedTarget = StripDisplayLinePrefixesFromEachLine(target);
                        var strippedMatches = strippedTarget != target ? FindOccurrencesNormalized(fileText, strippedTarget) : new List<NormalizedMatch>();
                        if (strippedMatches.Count == 1)
                        {
                            viaNormalizedMatch = true;
                            normalizedStart = strippedMatches[0].Start;
                            normalizedLength = strippedMatches[0].Length;
                        }
                        else if (strippedMatches.Count > 1)
                        {
                            return $"Error: Target string appears {strippedMatches.Count} times in '{path}' once display 'N:' line-number prefixes are ignored. Add surrounding lines to make the match unique, or set replace_all=true if every occurrence should be replaced.";
                        }
                        else if (strippedTarget != target)
                        {
                            return $"Error: Target string not found in '{path}' (0 matches — also checked with 'N:' line-number prefixes stripped from each line). Copy the text AFTER the ': ' prefix when quoting from a read_file view; those prefixes are display-only. If the file changed since you read it, read it again first — indentation itself must match character-for-character.";
                        }
                        else
                        {
                            return $"Error: Target string not found in '{path}' (0 matches). Re-read the file and copy the exact text including surrounding lines — whitespace and indentation must match character-for-character. If you copied from a read_file view, the leading 'N:' line numbers are display-only and must not be part of the target. If the file changed since you read it, read it again first.";
                        }
                    }

                    if (!viaNormalizedMatch && occurrences > 1 && !replaceAll)
                    {
                        return $"Error: Target string appears {occurrences} times in '{path}'. Add surrounding lines to make the match unique, or set replace_all=true if every occurrence should be replaced.";
                    }

                    string updatedText;
                    if (viaNormalizedMatch)
                    {
                        updatedText = string.Concat(fileText.AsSpan(0, normalizedStart), replacement, fileText.AsSpan(normalizedStart + normalizedLength));
                    }
                    else if (occurrences > 1 && replaceAll)
                    {
                        updatedText = fileText.Replace(target, replacement);
                    }
                    else
                    {
                        updatedText = ReplaceFirstOrdinal(fileText, target, replacement);
                    }
                    await File.WriteAllTextAsync(fullPath, updatedText, Encoding.UTF8, ct);
                    if (viaNormalizedMatch)
                        return $"Successfully replaced target in '{path}' (matched after ignoring the 'N:' line-number prefixes from your copy).";
                    return occurrences > 1
                        ? $"Successfully replaced all {occurrences} occurrences in '{path}'."
                        : $"Successfully replaced target in '{path}'.";
                }

                case "ask_question":
                {
                    return "Question submitted to operator. Waiting for response.";
                }

                default:
                    return $"Error: Unknown tool '{toolName}'.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool execution error: {ex.Message}";
        }
    }

    public static string FormatToolSummary(string toolName, string argsJson, McpClientManager? mcpManager = null)
    {
        if (mcpManager != null && mcpManager.TryGetTool(toolName, out var bridgedTool))
        {
            return $"MCP ({bridgedTool!.ServerName}): {bridgedTool.OriginalName}";
        }

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            switch (toolName.ToLowerInvariant())
            {
                case "run_command":
                case "bash":
                case "command":
                    var cmd = GetPropertyString(root, "command", "CommandLine", "commandLine", "cmd", "script", "shell_command");
                    return !string.IsNullOrWhiteSpace(cmd) ? $"Running command: {(cmd.Length > 60 ? cmd[..57] + "..." : cmd)}" : "Running command";
                case "read_file":
                case "view_file":
                    var rPath = GetPropertyString(root, "path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath", "uri");
                    return !string.IsNullOrWhiteSpace(rPath) ? $"Reading file: {Path.GetFileName(rPath)}" : "Reading file";
                case "write_file":
                case "write_to_file":
                    var wPath = GetPropertyString(root, "path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath");
                    return !string.IsNullOrWhiteSpace(wPath) ? $"Writing file: {Path.GetFileName(wPath)}" : "Writing file";
                case "edit_file":
                case "replace_file_content":
                    var ePath = GetPropertyString(root, "path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath");
                    return !string.IsNullOrWhiteSpace(ePath) ? $"Editing file: {Path.GetFileName(ePath)}" : "Editing file";
                case "ask_question":
                    return "Asking question";
                case "manage_task":
                    var action = GetPropertyString(root, "action") ?? "task";
                    return $"manage_task: {action}";
                case "post_job_message":
                    return "post_job_message";
                case "open_pull_request":
                    var title = GetPropertyString(root, "title") ?? "PR";
                    return $"open_pull_request: {title}";
                case "save_memory":
                    var key = GetPropertyString(root, "key") ?? "memory";
                    return $"save_memory: {key}";
                default:
                    return $"Executing {toolName}";
            }
        }
        catch
        {
            return $"Executing {toolName}";
        }
    }

    public static void TrackToolActivity(
        string toolName,
        string argsJson,
        HashSet<string> filesInspected,
        HashSet<string> filesModified,
        List<string> commandsRun)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            var tnLower = toolName.ToLowerInvariant();
            if (tnLower is "run_command" or "bash" or "command")
            {
                var cmd = GetPropertyString(root, "command", "CommandLine", "commandLine", "cmd", "script", "shell_command");
                if (!string.IsNullOrWhiteSpace(cmd)) commandsRun.Add(cmd);
            }
            else if (tnLower.Contains("write") || tnLower.Contains("edit") || tnLower.Contains("replace") || tnLower.Contains("modify") || tnLower.Contains("patch"))
            {
                var path = GetPropertyString(root, "path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath", "target_file", "source_file", "uri");
                if (!string.IsNullOrWhiteSpace(path)) filesModified.Add(path);
            }
            else if (tnLower.Contains("read") || tnLower.Contains("view") || tnLower.Contains("inspect"))
            {
                var path = GetPropertyString(root, "path", "file_path", "filePath", "file", "filename", "TargetFile", "AbsolutePath", "target_file", "source_file", "uri");
                if (!string.IsNullOrWhiteSpace(path)) filesInspected.Add(path);
            }
        }
        catch { }
    }

    internal static bool IsMutatingGitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var trimmed = command.Trim();
        return Regex.IsMatch(trimmed, @"\b(?:git\s+(?:commit|push|merge|rebase|cherry-pick)|gh\s+pr\s+create)\b", RegexOptions.IgnoreCase);
    }

    internal static bool IsReadOnlyTool(string? toolName, string? argsJson, McpClientManager? mcpManager = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return true;
        if (mcpManager != null && mcpManager.TryGetTool(toolName, out var bridgedTool))
        {
            return bridgedTool!.IsReadOnly;
        }

        var name = toolName.Trim().ToLowerInvariant();
        if (name is "read_file" or "view_file" or "ask_question" or "save_memory" or "list_memories" or "search_memory") return true;
        if (name is "manage_task")
        {
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                return argsJson.Contains("get_checklist", StringComparison.OrdinalIgnoreCase) ||
                       argsJson.Contains("get_details", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        if (name is "run_command" or "bash" or "command")
        {
            if (!string.IsNullOrWhiteSpace(argsJson))
            {
                var lower = argsJson.ToLowerInvariant();
                return lower.Contains("git status") || lower.Contains("git log") || lower.Contains("git diff") ||
                       lower.Contains("git branch") || lower.Contains("grep ") || lower.Contains("find ") ||
                       lower.Contains("cat ") || lower.Contains("ls ");
            }
        }
        return false;
    }

    internal static bool IsToolResultError(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return true;
        var trimmed = result.Trim();
        if (trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Error ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Tool execution error:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("An error occurred", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
                {
                    return true;
                }
                if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null && !string.IsNullOrWhiteSpace(err.ToString()))
                {
                    return true;
                }
                if (root.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Non-JSON string without error prefix is not treated as an error
        }

        return false;
    }

    internal static bool IsRequiresReply(string? toolArgs)
    {
        if (string.IsNullOrWhiteSpace(toolArgs)) return false;
        try
        {
            using var doc = JsonDocument.Parse(toolArgs);
            if (doc.RootElement.TryGetProperty("requires_reply", out var req) ||
                doc.RootElement.TryGetProperty("requiresReply", out req))
            {
                if (req.ValueKind == JsonValueKind.True) return true;
                if (req.ValueKind == JsonValueKind.String &&
                    bool.TryParse(req.GetString(), out var b) && b)
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    internal static bool IsSetChecklistAction(string? toolArgs)
    {
        if (string.IsNullOrWhiteSpace(toolArgs)) return false;
        try
        {
            using var doc = JsonDocument.Parse(toolArgs);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("action", out var act) ||
                    root.TryGetProperty("Action", out act) ||
                    root.TryGetProperty("operation", out act) ||
                    root.TryGetProperty("command", out act))
                {
                    return string.Equals(act.GetString()?.Trim(), "set_checklist", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
        return false;
    }

    internal static bool LooksLikeUnexecutedIntent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 2000) return false;
        var tail = content[^Math.Min(400, content.Length)..];
        if (tail.Contains('?')) return false;
        var promisesAction = Regex.IsMatch(
            content,
            @"\b(I'?ll|I will|let me|I am going to|I'm going to|Next,? I will|Now I (?:will|check|run|read|look))\b",
            RegexOptions.IgnoreCase);
        if (!promisesAction) return false;
        return Regex.IsMatch(
            content,
            @"\b(check|inspect|read|open|edit|modify|write|run|add|implement|fix|update|create|apply|test|retry|continue|proceed|look into|verify|submit)\b",
            RegexOptions.IgnoreCase);
    }

    internal static (string? ExtractedThought, string CleanedContent) ExtractInlineThinkBlocks(string? content)
    {
        if (string.IsNullOrEmpty(content)) return (null, string.Empty);

        var matches = Regex.Matches(content, @"<think>([\s\S]*?)</think>", RegexOptions.IgnoreCase);
        if (matches.Count == 0) return (null, content);

        var thoughts = new List<string>();
        foreach (Match m in matches)
        {
            var inner = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(inner))
            {
                thoughts.Add(inner);
            }
        }

        var cleaned = Regex.Replace(content, @"<think>[\s\S]*?</think>", string.Empty, RegexOptions.IgnoreCase).Trim();
        var extracted = thoughts.Count > 0 ? string.Join("\n\n", thoughts) : null;
        return (extracted, cleaned);
    }

    internal static string? GetPropertyString(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(root.GetString()!);
                return GetPropertyString(doc.RootElement, names);
            }
            catch { return null; }
        }

        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
                if (prop.ValueKind != JsonValueKind.Null && prop.ValueKind != JsonValueKind.Undefined) return prop.GetRawText();
            }
        }

        foreach (var name in names)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
                    if (prop.Value.ValueKind != JsonValueKind.Null && prop.Value.ValueKind != JsonValueKind.Undefined) return prop.Value.GetRawText();
                }
            }
        }
        return null;
    }

    internal static string ExtractGoal(PromptReceivedEvent promptEvent)
    {
        if (!string.IsNullOrWhiteSpace(promptEvent.Prompt))
        {
            var rawPrompt = promptEvent.Prompt.Trim();
            var match = Regex.Match(rawPrompt, @"\[Job Context:\s*([^\]\n]+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();

            match = Regex.Match(rawPrompt, @"(?:Task|Title|Goal):\s*([^\n]+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();

            var firstLine = rawPrompt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => !l.StartsWith("[") && !l.StartsWith("#"));
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine.Length > 120 ? firstLine[..117] + "..." : firstLine;
            }
        }
        return "Autonomous Job Implementation";
    }

    private static int? GetPropertyInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var val))
            {
                return val;
            }
        }
        return null;
    }

    internal static bool GetPropertyBool(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in root.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.True) return true;
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        bool.TryParse(prop.Value.GetString(), out var b) && b)
                    {
                        return true;
                    }
                    return false;
                }
            }
        }
        return false;
    }

    internal static int CountSubstringOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    internal readonly record struct NormalizedMatch(int Start, int Length);

    internal static List<NormalizedMatch> FindOccurrencesNormalized(string text, string targetLinesJoined)
    {
        var matches = new List<NormalizedMatch>();
        if (targetLinesJoined.Length == 0) return matches;
        var lines = targetLinesJoined.Replace("\r\n", "\n").Split('\n');
        var pattern = string.Join(@"\r?\n", lines.Select(Regex.Escape));
        foreach (Match m in Regex.Matches(text, pattern))
        {
            if (m.Index == 0 || text[m.Index - 1] == '\n')
            {
                matches.Add(new NormalizedMatch(m.Index, m.Length));
            }
        }
        return matches;
    }

    internal static string StripDisplayLinePrefixesFromEachLine(string target)
    {
        var lines = target.Split('\n');
        var sawContentLine = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
            if (line.Trim().Length == 0) continue;
            sawContentLine = true;
            var stripped = Regex.Replace(line, @"^\s*\d+:\s?", "");
            if (stripped == line) return target;
            lines[i] = stripped + (lines[i].EndsWith('\r') ? "\r" : "");
        }
        return sawContentLine ? string.Join("\n", lines) : target;
    }

    internal static bool IsBenignExitOneCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var firstToken = command.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var bare = Path.GetFileName(firstToken.TrimEnd('(', ')', '&', ';', '|', '>', '<'));
        if (bare is "grep" or "egrep" or "fgrep" or "rg" or "ugrep" or "bfs" or "ag" or "ack"
                   or "diff" or "diff3" or "sdiff" or "cmp" or "comm" or "find" or "test" or "[")
        {
            return true;
        }
        var oneLine = command.Replace('\n', ' ');
        return oneLine.Contains("git grep", StringComparison.OrdinalIgnoreCase)
               || Regex.IsMatch(oneLine, @"git\s+diff\b.*--exit-code");
    }

    internal static string ReplaceFirstOrdinal(string text, string search, string replace)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        if (index < 0) return text;
        return string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + search.Length));
    }
}
