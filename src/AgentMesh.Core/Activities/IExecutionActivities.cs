using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
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
    /// <para>
    /// <paramref name="taskWorkspacePath"/> is the harness-supplied checkout root for the task the
    /// command belongs to (<c>TaskExecutionInput.WorkspacePath</c>, i.e.
    /// <c>/workspace/repos/{repo_name}</c>) and is the default when <c>args</c> names no directory. It
    /// is a separate parameter rather than a field on <see cref="RunCommandArgs"/> because the args
    /// record is the model-facing tool schema: a model that can name an arbitrary task workspace can
    /// point its commands at another task's checkout. It is declared <em>before</em> the cancellation
    /// token so the token stays last and out of the payload, and it is optional and trailing so every
    /// existing three-argument call site and every in-flight payload resolves unchanged.
    /// </para>
    /// </remarks>
    [Activity]
    Task<CommandExecutionResult> ExecuteCommand(
        RunCommandArgs args,
        Guid? sessionId = null,
        string? commandId = null,
        string? taskWorkspacePath = null,
        CancellationToken cancellationToken = default);

    [Activity]
    Task<TurnOutcome> RunAgentDiscoveryTurnAsync(AgentMesh.Core.Workflows.DiscoveryTurnInput input);

    [Activity]
    Task<TurnOutcome> RunJobCodingTurnAsync(AgentMesh.Core.Workflows.CodingTurnInput input);
}
