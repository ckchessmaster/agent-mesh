using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class ExecutionActivities(
    ILogger<ExecutionActivities> logger,
    IStorageService? storageService = null,
    IConfiguration? config = null) : IExecutionActivities
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
    [Activity]
    public async Task<CommandExecutionResult> ExecuteCommand(
        RunCommandArgs args,
        Guid? sessionId = null,
        string? commandId = null,
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
            WorkingDirectory = ResolveWorkingDirectory(args.WorkingDirectory),
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
    /// Falls back to the worker's own directory when the caller did not name one. A missing directory is
    /// coerced rather than trusted: <c>ProcessStartInfo</c> throws before the command can fail on its own.
    /// </summary>
    private string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            return workingDirectory;
        }

        var workspace = config?["Sandbox:WorkspaceRoot"];
        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            return workspace;
        }

        return Directory.Exists("/workspace") ? "/workspace" : AppContext.BaseDirectory;
    }
}
