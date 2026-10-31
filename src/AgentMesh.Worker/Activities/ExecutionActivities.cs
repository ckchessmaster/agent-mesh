using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

/// <summary>
/// Worker-side activities: the Job A shell executor and the Job D macro coding turn.
/// </summary>
/// <remarks>
/// Declared <c>partial</c> across two files but registered as one activity type, so the turn's
/// internal shell calls and the workflow's direct <c>ExecuteCommand</c> calls cannot drift on how a
/// working directory is resolved. The Job D dependencies are <c>init</c> properties on the partial in
/// <c>ExecutionActivities.Turn.cs</c> rather than primary-constructor parameters: Job A's tests build
/// this type with a bare logger, and widening the constructor would rewrite every one of those call
/// sites to pass four nulls that mean nothing to the test.
/// </remarks>
/// <param name="assembler">
/// Prompt assembly (pre-turn hooks). Null on a host that registers only the shell activity — a macro
/// turn then fails with a message naming the interface to register, rather than dispatching an empty
/// prompt.
/// </param>
/// <param name="checkpoints">Durable per-task working checkpoint store.</param>
/// <param name="turnRunner">
/// Executes one agent turn. The worker holds no LLM client by design, so this is always the thing that
/// reaches the platform's own dispatch path.
/// </param>
/// <param name="reconciler">
/// Workspace git probe. Null builds a <see cref="ShellWorkspaceReconciler"/> over
/// <see cref="SandboxShell"/>, which is the production behaviour.
/// </param>
/// <remarks>
/// The Job D dependencies are trailing optional constructor parameters rather than <c>init</c>
/// properties on the other partial. The worker registers this type with
/// <c>AddScopedActivities&lt;ExecutionActivities&gt;()</c>, which resolves through the DI
/// <em>constructor</em> activator and performs no property injection — so properties would have been
/// silently null on every real turn, and the failure would surface as "no assembler registered" in
/// production while every unit test that sets them passed. Optional keeps Job A's
/// <c>new ExecutionActivities(logger)</c> call sites compiling unchanged.
/// </remarks>
public partial class ExecutionActivities(
    ILogger<ExecutionActivities> logger,
    IStorageService? storageService = null,
    IConfiguration? config = null,
    ITurnContextAssembler? assembler = null,
    IWorkspaceStateRepository? checkpoints = null,
    IAgentTurnRunner? turnRunner = null,
    IWorkspaceReconciler? reconciler = null) : IExecutionActivities
{
    [Activity]
    public async Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson, Guid? jobId = null)
    {
        logger.LogInformation("Executing subsystem {Service}:{Verb} for Job {JobId} with payload: {Payload}", 
            targetService, actionVerb, jobId, payloadJson);

        // Simulation/MVP execution
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            service = targetService,
            verb = actionVerb,
            executedAt = DateTimeOffset.UtcNow,
            status = "success"
        });

        var threshold = config?.GetValue<int>("Storage:S3:ThresholdBytes", 65536) ?? 65536;

        if (result.Length > threshold && storageService != null && jobId.HasValue)
        {
            var s3Key = $"jobs/{jobId.Value}/result.json";
            var s3Uri = await storageService.UploadTextAsync(s3Key, result);
            logger.LogInformation("Offloaded result for job {JobId} ({Bytes} bytes) to {S3Uri}", jobId, result.Length, s3Uri);

            result = System.Text.Json.JsonSerializer.Serialize(new
            {
                service = targetService,
                verb = actionVerb,
                _offloaded = true,
                s3_uri = s3Uri,
                byte_size = result.Length,
                executedAt = DateTimeOffset.UtcNow,
                status = "success"
            });
        }

        return result;
    }

    /// <summary>
    /// Runs a shell command inside the sandbox. The policy lives in
    /// <see cref="SandboxShell"/>/<see cref="CommandExecutionPolicy"/>, shared with the CLI runner, so a
    /// command cannot get a longer timeout or a softer teardown by choosing a different host.
    /// </summary>
    /// <param name="taskWorkspacePath">
    /// The resolved checkout root for the task this command belongs to —
    /// <c>TaskExecutionInput.WorkspacePath</c>, i.e. <c>/workspace/repos/{repo_name}</c>. Used when
    /// <paramref name="args"/> names no directory, which is the case for the majority of an agent's
    /// commands: it is working in its checkout and does not restate the path every call.
    /// <para>
    /// Trailing and optional rather than a field on <see cref="RunCommandArgs"/> because the args
    /// record is the <em>model-facing</em> tool schema (Job A) and the task workspace is
    /// <em>harness</em> context. Putting it in the args would advertise it to the model as a settable
    /// parameter, and a model that can name an arbitrary task workspace can point its commands at
    /// another task's checkout.
    /// </para>
    /// <para>
    /// Adding it here is also wire-compatible: Temporal binds activity arguments positionally by
    /// count, so an existing caller that passes three arguments still resolves identically and the new
    /// one arrives as null.
    /// </para>
    /// </param>
    [Activity]
    public async Task<CommandExecutionResult> ExecuteCommand(
        RunCommandArgs args,
        Guid? sessionId = null,
        string? commandId = null,
        string? taskWorkspacePath = null,
        CancellationToken cancellationToken = default)
    {
        if (args == null || string.IsNullOrWhiteSpace(args.Command))
        {
            logger.LogWarning("ExecuteCommand invoked without a command for session {SessionId}", sessionId);
            return new CommandExecutionResult(false, 127, "Error: 'command' argument is required.");
        }

        var requested = args.TimeoutSeconds ?? ConfiguredDefaultTimeoutSeconds;
        var timeoutSeconds = CommandExecutionPolicy.ClampTimeoutSeconds(requested);

        logger.LogInformation(
            "Executing sandboxed command for session {SessionId} (limit {Timeout}s): {Command}",
            sessionId, timeoutSeconds, args.Command);

        var executionId = sessionId.HasValue
            ? $"{sessionId.Value:N}-{(string.IsNullOrWhiteSpace(commandId) ? "adhoc" : commandId)}"
            : Guid.NewGuid().ToString("N");

        var effectiveArgs = args with
        {
            WorkingDirectory = ResolveWorkingDirectory(args.WorkingDirectory, taskWorkspacePath),
            TimeoutSeconds = timeoutSeconds,
        };

        var result = await SandboxShell.RunAsync(
            effectiveArgs,
            cancellationToken,
            log: message => logger.LogWarning("{Message}", message),
            executionId: executionId);

        if (result.TimedOut)
        {
            logger.LogWarning(
                "Command for session {SessionId} timed out after {Timeout}s; process group {Pgid} torn down. Log: {LogPath}",
                sessionId, timeoutSeconds, result.ProcessGroupId, result.LogFilePath ?? "(none)");
        }
        else if (result.LogFilePath != null)
        {
            logger.LogInformation(
                "Command for session {SessionId} exited {ExitCode}; full output spilled to {LogPath}",
                sessionId, result.ExitCode, result.LogFilePath);
        }
        else
        {
            logger.LogInformation(
                "Command for session {SessionId} exited {ExitCode}", sessionId, result.ExitCode);
        }

        return result;
    }

    /// <summary>
    /// Operator-configurable default for commands that do not state a budget. Read per call rather than
    /// cached so a config reload takes effect without restarting the worker.
    /// </summary>
    private int? ConfiguredDefaultTimeoutSeconds
    {
        get
        {
            var configured = config?.GetValue<int?>("Sandbox:DefaultTimeoutSeconds");
            return configured is > 0 ? configured : null;
        }
    }

    /// <summary>
    /// Chooses the directory a command runs in, in priority order: what the caller named, then the
    /// task's resolved checkout, then the operator-configured workspace root, then the worker's own
    /// fallbacks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The task workspace sits second rather than first because an explicit per-command directory is
    /// the caller overriding the default on purpose — a build script that needs to run in a sibling
    /// checkout, or the reconciler probing a path it was handed. Silently preferring the task path
    /// over a stated one would make such a call impossible to express.
    /// </para>
    /// <para>
    /// A directory that is named but does not exist is <em>not</em> used. <c>ProcessStartInfo</c>
    /// throws before the command can fail on its own merits, so a stale
    /// <c>projects.working_directory</c> would surface as an opaque activity exception rather than as
    /// a command that ran somewhere and reported what it found. Falling through is the safer
    /// behaviour, and the fallthrough is logged because a configured path that does not exist is a
    /// misconfiguration worth seeing.
    /// </para>
    /// </remarks>
    private string ResolveWorkingDirectory(string? workingDirectory, string? taskWorkspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (Directory.Exists(workingDirectory)) return workingDirectory;
            LogMissingDirectory("caller-supplied", workingDirectory);
        }

        if (!string.IsNullOrWhiteSpace(taskWorkspacePath))
        {
            var resolved = WorkspacePathResolver.Normalize(taskWorkspacePath.Trim());
            if (Directory.Exists(resolved)) return resolved;
            LogMissingDirectory("task workspace", resolved);
        }

        var workspace = config?["Sandbox:WorkspaceRoot"];
        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            return workspace;
        }

        return Directory.Exists("/workspace") ? "/workspace" : AppContext.BaseDirectory;
    }

    private void LogMissingDirectory(string kind, string path) =>
        logger.LogWarning(
            "{Kind} working directory '{Path}' does not exist; falling back to a lower-priority path. " +
            "Check projects.working_directory for this task's project.",
            kind, path);
}
