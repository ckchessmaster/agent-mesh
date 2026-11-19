using System.Text;
using System.Text.Json;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;

namespace AgentMesh.Cli.Commands;

/// <summary>
/// The shell route to the control-plane tools (<c>agentmesh task …</c>, <c>agentmesh message …</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>manage_task</c>, <c>post_job_message</c> and <c>open_pull_request</c> are mounted as function tools
/// only by adapters that declare tool schemas. Subprocess drivers — <c>agy</c>, <c>claude -p</c>,
/// <c>copilot</c> — have nothing that reaches them: no MCP server is registered in a persona container
/// (<c>agy mcp list</c> answers "No MCP servers configured"), and the context a platform call needs
/// (turn session, persona, job) is per turn, so it cannot live in a static MCP registration either.
/// Every shell those drivers run inherits this process's environment, which the runner stamps with the
/// turn's context, so a shell command is a route that works for every driver without mounting anything.
/// </para>
/// <para>
/// The names are deliberately not <c>manage_task</c>. Antigravity ships a built-in tool called
/// <c>manage_task</c> of its own — a background-shell task manager whose actions are
/// <c>list | kill | status | send_input</c> — and in November 2026 a Stage 1B decomposition on that driver
/// "succeeded" against it. The checklist never reached the platform, the plan gate was built from the
/// worker's invented fallback, and nothing anywhere reported a failure. A command called <c>manage_task</c>
/// could be shadowed the same way; <c>agentmesh task</c> cannot, because there is only one <c>agentmesh</c>
/// on PATH and it is this one.
/// </para>
/// <para>
/// These verbs do not reimplement the tools. They build the argument document and hand it to the same
/// <c>POST /mcp</c> path the adapters use, so validation, provenance and job linkage stay in
/// <c>PlatformMcpTools</c> and cannot drift.
/// </para>
/// </remarks>
public static class PlatformToolCommand
{
    public const int SuccessExitCode = 0;
    public const int ErrorExitCode = 1;

    /// <summary>Context the runner exports into every driver child process for the current turn.</summary>
    public const string SessionIdEnvVar = "AGENTMESH_SESSION_ID";
    public const string JobIdEnvVar = "AGENTMESH_JOB_ID";
    public const string PersonaIdEnvVar = "AGENTMESH_PERSONA_ID";
    public const string AgentIdEnvVar = "AGENTMESH_DRIVER_ID";

    public delegate Task<string> McpToolExecutor(
        string toolName,
        string argsJson,
        Guid? sessionId,
        string? personaId,
        string? agentId,
        CancellationToken ct);

    // ── agentmesh task ───────────────────────────────────────────────────────────

    public static Task<int> ExecuteTaskAsync(
        IReadOnlyList<string> args,
        CliConfig config,
        ConfigStore configStore,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        McpToolExecutor? mcpExecutor = null,
        CancellationToken ct = default) =>
        ExecuteAsync("task", args, config, configStore, stdout, stderr, mcpExecutor, ct);

    // ── agentmesh message ────────────────────────────────────────────────────────

    public static Task<int> ExecuteMessageAsync(
        IReadOnlyList<string> args,
        CliConfig config,
        ConfigStore configStore,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        McpToolExecutor? mcpExecutor = null,
        CancellationToken ct = default) =>
        ExecuteAsync("message", args, config, configStore, stdout, stderr, mcpExecutor, ct);

    private static async Task<int> ExecuteAsync(
        string group,
        IReadOnlyList<string> args,
        CliConfig config,
        ConfigStore configStore,
        TextWriter? stdout,
        TextWriter? stderr,
        McpToolExecutor? mcpExecutor,
        CancellationToken ct)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;

        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            await PrintUsageAsync(group, outWriter);
            return SuccessExitCode;
        }

        var verb = args[0].Trim().ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToList(), config);
        var run = mcpExecutor ?? ((tool, toolArgs, sid, pid, aid, token) =>
            new MeshClient(config, configStore).CallPlatformMcpToolAsync(tool, toolArgs, sid, pid, aid, token));

        var toolName = group == "task" ? "manage_task" : "post_job_message";
        string argsJson;
        switch (group)
        {
            case "task":
                var taskArgs = await BuildTaskArgumentsAsync(verb, options, errWriter, ct);
                if (taskArgs is null) return ErrorExitCode;
                argsJson = taskArgs;
                break;

            default:
                var messageArgs = await BuildMessageArgumentsAsync(verb, options, errWriter, ct);
                if (messageArgs is null) return ErrorExitCode;
                argsJson = messageArgs;
                break;
        }

        var response = await run(toolName, argsJson, options.SessionId, options.PersonaId, options.AgentId, ct);

        if (LooksLikeFailure(response))
        {
            await errWriter.WriteLineAsync(response.Trim());
            await errWriter.WriteLineAsync(ContextHint(options));
            return ErrorExitCode;
        }

        await outWriter.WriteLineAsync(response.Trim());
        return SuccessExitCode;
    }

    private static async Task<string?> BuildTaskArgumentsAsync(string verb, ToolOptions options, TextWriter stderr, CancellationToken ct)
    {
        switch (verb)
        {
            case "set-checklist":
            {
                var payload = await ReadJsonInputAsync(options, stderr, ct);
                if (payload is null) return null;

                var tasks = ReadChecklistTasks(payload);
                if (tasks.Count == 0)
                {
                    await stderr.WriteLineAsync(
                        "NO_TASKS: expected a JSON array of tasks, each with at least a `title` " +
                        "(for example [{\"title\": \"Add reallocation endpoint\", \"target_file\": \"internal/server/routes.go\", " +
                        "\"verification_method\": \"go test ./...\"}]). Nothing was registered.");
                    return null;
                }

                return Serialize(new Dictionary<string, object?>
                {
                    ["action"] = "set_checklist",
                    ["tasks"] = tasks,
                    ["job_id"] = NullIfBlank(options.JobId)
                });
            }

            case "update-status" or "set-status":
            {
                if (string.IsNullOrWhiteSpace(options.TaskId) || string.IsNullOrWhiteSpace(options.Status))
                {
                    await stderr.WriteLineAsync(
                        "MISSING_ARGUMENT: `agentmesh task update-status` needs --task <task-id> and --status " +
                        "<todo|in_progress|awaiting_review|completed|blocked>. Run `agentmesh task get-checklist` to list task ids.");
                    return null;
                }

                return Serialize(new Dictionary<string, object?>
                {
                    ["action"] = "update_task_status",
                    ["task_id"] = options.TaskId.Trim(),
                    ["status"] = options.Status.Trim(),
                    ["progress_note"] = NullIfBlank(options.Note),
                    ["job_id"] = NullIfBlank(options.JobId)
                });
            }

            case "get-checklist" or "list":
                return Serialize(new Dictionary<string, object?>
                {
                    ["action"] = "get_checklist",
                    ["job_id"] = NullIfBlank(options.JobId)
                });

            default:
                await stderr.WriteLineAsync(
                    $"Unknown `agentmesh task` subcommand '{verb}'. Expected: set-checklist, update-status, or get-checklist.");
                return null;
        }
    }

    private static async Task<string?> BuildMessageArgumentsAsync(string verb, ToolOptions options, TextWriter stderr, CancellationToken ct)
    {
        if (verb is not ("post" or "send"))
        {
            await stderr.WriteLineAsync($"Unknown `agentmesh message` subcommand '{verb}'. Expected: post.");
            return null;
        }

        var content = options.Content;
        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(options.FilePath))
        {
            content = await ReadFileAsync(options.FilePath!, ct);
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            content = await Console.In.ReadToEndAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            await stderr.WriteLineAsync(
                "NO_CONTENT: pass --content <text>, --file <path>, or pipe the message body on stdin. Nothing was posted.");
            return null;
        }

        return Serialize(new Dictionary<string, object?>
        {
            ["content"] = content.Trim(),
            ["target_persona"] = NullIfBlank(options.Target) ?? "operator",
            ["requires_reply"] = options.RequiresReply,
            ["job_id"] = NullIfBlank(options.JobId)
        });
    }

    private static async Task<string?> ReadJsonInputAsync(ToolOptions options, TextWriter stderr, CancellationToken ct)
    {
        string? text = options.Json;

        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(options.FilePath))
        {
            text = await ReadFileAsync(options.FilePath!, ct);
            if (text is null)
            {
                await stderr.WriteLineAsync($"FILE_UNREADABLE: could not read '{options.FilePath}'.");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = await Console.In.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await stderr.WriteLineAsync(
                "NO_INPUT: give the checklist with --json '<array>', --file <path>, or on stdin. " +
                "A checklist is what the plan gate shows the operator, so an empty one is never registered silently.");
            return null;
        }

        return text;
    }

    private static async Task<string?> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads checklist items out of whatever JSON the caller supplied.
    /// </summary>
    /// <remarks>
    /// Tolerant on purpose: this document is written by an agent in a shell, and a task that is dropped
    /// because the agent wrote <c>targetFile</c> instead of <c>target_file</c> is a lost step in the plan,
    /// not a lesson in JSON. Unrecognised shapes contribute nothing rather than a half-built task.
    /// </remarks>
    internal static List<ChecklistTaskItem> ReadChecklistTasks(string json)
    {
        var result = new List<ChecklistTaskItem>();
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;

            var array = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array => tasks,
                JsonValueKind.Object => default,
                _ => default
            };

            if (array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (ReadTask(item) is { } task) result.Add(task);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object && ReadTask(root) is { } single)
            {
                result.Add(single);
            }
            else if (root.ValueKind == JsonValueKind.String && ReadTask(root) is { } oneLiner)
            {
                result.Add(oneLiner);
            }
        }
        catch (JsonException)
        {
            // Nothing readable: the caller reports NO_TASKS with an example rather than guessing.
        }

        return result;
    }

    private static ChecklistTaskItem? ReadTask(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var title = item.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(title) ? null : new ChecklistTaskItem(title);
        }

        if (item.ValueKind != JsonValueKind.Object) return null;

        // No PascalCase variants here on purpose: the exact pass runs before the case-insensitive one, so
        // listing "Task" made `{"Title": …, "Task": …}` pick the second field by list order instead of
        // taking whichever field the author actually wrote first.
        var taskTitle = ReadFirstString(item, "title", "name", "task");
        if (string.IsNullOrWhiteSpace(taskTitle)) return null;

        return new ChecklistTaskItem(
            taskTitle.Trim(),
            NullIfBlank(ReadFirstString(item, "target_file", "targetFile", "path", "file")),
            NullIfBlank(ReadFirstString(item, "verification_method", "verificationMethod", "verification", "test")),
            NullIfBlank(ReadFirstString(item, "id", "task_id", "taskId")));
    }

    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var text = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the control plane's answer means the call did not happen.
    /// </summary>
    /// <remarks>
    /// <see cref="MeshClient.CallPlatformMcpToolAsync"/> reports transport, HTTP and JSON-RPC failures as a
    /// string prefixed <c>Error</c> — including "Error executing platform tool 'x': Connection refused",
    /// which a colon-sensitive check walks straight past and then reports to the agent as a success. A tool
    /// that lies about registering a checklist is the exact failure this command exists to remove, so the
    /// shapes here are enumerated rather than guessed at: a plain-text success answer is only ever an
    /// unprefixed sentence, and a structured rejection is a JSON object carrying <c>success:false</c>.
    /// </remarks>
    internal static bool LooksLikeFailure(string? response)
    {
        var text = response?.Trim() ?? string.Empty;
        if (text.Length == 0) return true;
        if (text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return true;
        if (!text.StartsWith('{')) return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (doc.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                return true;
            }

            return doc.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// What to set when the control plane could not place this call on a job.
    /// </summary>
    /// <remarks>
    /// The generic "call failed" is what sent an agent hunting through its own transcript for a tool that
    /// was never the problem; the fix for a missing turn context belongs next to the failure that needs it.
    /// </remarks>
    private static string ContextHint(ToolOptions options) =>
        options.SessionId is null && string.IsNullOrWhiteSpace(options.JobId)
            ? $"HINT: this shell has no turn context — {SessionIdEnvVar} and {JobIdEnvVar} are unset, so the " +
              "control plane cannot tell which job the call belongs to. Run it inside a dispatched persona turn, " +
              $"or pass --session-id/--job-id explicitly. Current context: persona={options.PersonaId ?? "(unset)"}, " +
              $"driver={options.AgentId ?? "(unset)"}."
            : $"HINT: the tool rejected the call. Current context: session={options.SessionId?.ToString() ?? "(none)"}, " +
              $"job={options.JobId ?? "(none)"}, persona={options.PersonaId ?? "(unset)"}, driver={options.AgentId ?? "(unset)"}.";

    private static string Serialize(Dictionary<string, object?> payload) => JsonSerializer.Serialize(payload);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task PrintUsageAsync(string group, TextWriter stdout)
    {
        if (group == "task")
        {
            await stdout.WriteLineAsync("Usage:");
            await stdout.WriteLineAsync("  agentmesh task set-checklist (--json '<array>' | --file <path> | stdin) [--job-id <guid>] [--session-id <guid>]");
            await stdout.WriteLineAsync("  agentmesh task update-status --task <task-id> --status <todo|in_progress|awaiting_review|completed|blocked> [--note <text>]");
            await stdout.WriteLineAsync("  agentmesh task get-checklist");
            await stdout.WriteLineAsync();
            await stdout.WriteLineAsync("  Each checklist item: {\"title\": string, \"target_file\": string?, \"verification_method\": string?}");
        }
        else
        {
            await stdout.WriteLineAsync("Usage:");
            await stdout.WriteLineAsync("  agentmesh message post (--content <text> | --file <path> | stdin) [--target <persona|operator>] [--requires-reply]");
            await stdout.WriteLineAsync();
            await stdout.WriteLineAsync("  A question for the operator is not a message: use ask_question, which is the only channel they can answer.");
        }

        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync($"  Common: [--persona <id>] [--agent <driver-id>] [--url <mesh-url>] [--token <token>]");
        await stdout.WriteLineAsync($"  Context defaults from the environment: {SessionIdEnvVar}, {JobIdEnvVar}, {PersonaIdEnvVar}, {AgentIdEnvVar}.");
    }

    private sealed class ToolOptions
    {
        public string? Json { get; set; }
        public string? FilePath { get; set; }
        public string? Content { get; set; }
        public string? TaskId { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
        public string? Target { get; set; }
        public bool RequiresReply { get; set; }
        public string? JobId { get; set; }
        public Guid? SessionId { get; set; }
        public string? PersonaId { get; set; }
        public string? AgentId { get; set; }
    }

    private static ToolOptions ParseOptions(List<string> args, CliConfig config)
    {
        var opts = new ToolOptions
        {
            JobId = Environment.GetEnvironmentVariable(JobIdEnvVar),
            PersonaId = Environment.GetEnvironmentVariable(PersonaIdEnvVar),
            AgentId = Environment.GetEnvironmentVariable(AgentIdEnvVar)
        };

        var sessionRaw = Environment.GetEnvironmentVariable(SessionIdEnvVar);
        if (Guid.TryParse(sessionRaw, out var ambientSession))
        {
            opts.SessionId = ambientSession;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;

            switch (arg)
            {
                case "--json":
                    opts.Json = Next();
                    break;
                case "--file" or "--input":
                    opts.FilePath = Next();
                    break;
                case "--content" or "-m":
                    opts.Content = Next();
                    break;
                case "--task" or "--task-id":
                    opts.TaskId = Next();
                    break;
                case "--status":
                    opts.Status = Next();
                    break;
                case "--note" or "--progress-note":
                    opts.Note = Next();
                    break;
                case "--target" or "--to":
                    opts.Target = Next();
                    break;
                case "--requires-reply":
                    opts.RequiresReply = true;
                    break;
                case "--job-id":
                    opts.JobId = Next();
                    break;
                case "--session-id":
                    if (Guid.TryParse(Next(), out var sid)) opts.SessionId = sid;
                    break;
                case "--persona":
                    opts.PersonaId = Next();
                    break;
                case "--agent" or "--driver":
                    opts.AgentId = Next();
                    break;
                case "--url" or "--mesh-url":
                    if (Next() is { } url) config.MeshUrl = url.TrimEnd('/');
                    break;
                case "--token":
                    if (Next() is { } token) config.MasterToken = token.Trim();
                    break;
            }
        }

        return opts;
    }
}
