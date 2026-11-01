using AgentMesh.Core.Models;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
public class WorkflowInterfaceAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class WorkflowMethodAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class SignalMethodAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class QueryMethodAttribute : Attribute { }

public record JobExecutionInput(
    string JobId,
    string AssignedAgentId,
    string ProjectId,
    string WorkspacePath,
    string InitialPrompt,
    string TargetBranch,
    PlanApprovalPolicy ApprovalPolicy
);

public record DiscoveryTurnInput(
    string JobId,
    string AssignedAgentId,
    string WorkspacePath,
    string InitialPrompt
);

public record CodingTurnInput(
    string JobId,
    ChecklistTaskItem CurrentTask,
    ChatMessageSignal? PendingMessage
);

public record MergePullRequestInput(
    string JobId,
    string TargetBranch
);

public record JobExecutionResult(
    bool Success,
    string Summary
);

public record JobWorkflowState(
    int CurrentTaskIndex,
    int TotalTasks,
    string CurrentHeartbeat
);

[WorkflowInterface]
[Workflow]
public interface IJobExecutionWorkflow
{
    [WorkflowMethod]
    [WorkflowRun]
    Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input);

    [SignalMethod]
    [WorkflowSignal]
    Task ReceiveChatMessageAsync(ChatMessageSignal signal);

    [SignalMethod]
    [WorkflowSignal]
    Task ReceivePlanApprovalAsync(PlanApprovedSignal signal);

    [SignalMethod]
    [WorkflowSignal]
    Task ReceivePrReviewAsync(PrReviewSignal signal);

    [QueryMethod]
    [WorkflowQuery]
    JobWorkflowState GetCurrentState();
}
