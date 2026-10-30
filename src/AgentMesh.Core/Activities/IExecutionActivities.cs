using AgentMesh.Core.Models;
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

public interface IExecutionActivities
{
    [Activity]
    Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson, Guid? jobId = null);

    /// <summary>
    /// Runs a shell command inside the sandbox with the shared timeout, process-tree teardown and log
    /// spillover policy.
    /// </summary>
    /// <remarks>
    /// Returns the record rather than a string so a workflow can branch on
    /// <see cref="CommandExecutionResult.TimedOut"/> and
    /// <see cref="CommandExecutionResult.ExitCode"/> without parsing prose. Temporal serializes the result
    /// into history, which is precisely why <see cref="CommandExecutionResult.Output"/> is the spillover
    /// preview and not the raw stream: a 5,000-line test log in a workflow's history would blow the gRPC
    /// message limit and the 50 KB payload guidance in one step.
    ///
    /// The trailing <see cref="CancellationToken"/> is not part of the payload: Temporal binds it to the
    /// activity's own cancellation, which is how a workflow cancel reaches the process group.
    /// </remarks>
    [Activity]
    Task<CommandExecutionResult> ExecuteCommand(
        RunCommandArgs args,
        Guid? sessionId = null,
        string? commandId = null,
        CancellationToken cancellationToken = default);
}
