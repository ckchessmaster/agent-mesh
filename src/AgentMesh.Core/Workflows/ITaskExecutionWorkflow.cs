using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

/// <summary>
/// The contract of the macro task-execution workflow.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a plain interface and not a <c>[WorkflowInterface]</c>:</strong> the .NET SDK
/// (1.20.0) has no such attribute. Its workflow surface is <c>[Workflow]</c> on the class,
/// <c>[WorkflowRun]</c> on exactly one method, <c>[WorkflowSignal]</c>, <c>[WorkflowQuery]</c> and
/// <c>[WorkflowUpdate]</c>. The <c>[WorkflowInterface]</c>/<c>[WorkflowMethod]</c> pair in the job
/// spec is the Go/Java shape; ported literally it does not compile here. The interface is still
/// worth having — it is the seam the API's signal router and the tests bind to, and it keeps the
/// handler names in one place — but it carries no SDK meaning and must not be passed to
/// <c>StartWorkflowAsync</c>/<c>GetWorkflowHandle</c> as the workflow type. Doing so registers
/// nothing and fails at runtime with "workflow not registered".
/// </para>
/// <para>
/// <strong>Handler names on the wire drop the <c>Async</c> suffix</strong> (see
/// <see cref="AgentMesh.Core.Workflows.SignalNames"/>). Call from the concrete class expression, or
/// use the constants; never hand-write the suffixed form.
/// </para>
/// </remarks>
public interface ITaskExecutionWorkflow
{
    /// <summary>Drives one checklist task to a terminal state. Maps to <c>[WorkflowRun]</c>.</summary>
    Task<TaskExecutionResult> ExecuteTaskAsync(TaskExecutionInput input);

    /// <summary>Delivers a Job Chat reply, releasing an <c>AwaitingInput</c> pause. Wire name: <c>ReceiveChatMessage</c>.</summary>
    Task ReceiveChatMessageAsync(ChatMessageSignal signal);

    /// <summary>Delivers a review decision, releasing a PR-review pause. Wire name: <c>ReceivePrReviewSignal</c>.</summary>
    Task ReceivePrReviewSignalAsync(PrReviewSignal signal);

    /// <summary>The workflow's current lifecycle state.</summary>
    TaskStatusView GetTaskStatus();
}
