using AgentMesh.Core.Models;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

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
    string InitialPrompt,
    string? ProjectId = null,
    string? TargetBranch = null
);

public record CodingTurnInput(
    string JobId,
    ChecklistTaskItem CurrentTask,
    ChatMessageSignal? PendingMessage,
    string AssignedAgentId,
    string ProjectId,
    string WorkspacePath,
    string TargetBranch
);

public record MergePullRequestInput(
    string JobId,
    string TargetBranch,
    string? PrUrl = null
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

[Workflow]
public interface IJobExecutionWorkflow
{
    [WorkflowRun]
    Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input);

    [WorkflowSignal]
    Task ReceiveChatMessageAsync(ChatMessageSignal signal);

    [WorkflowSignal]
    Task ReceivePlanApprovalAsync(PlanApprovedSignal signal);

    [WorkflowSignal]
    Task ReceivePrReviewAsync(PrReviewSignal signal);

    [WorkflowSignal]
    Task ReceiveSetChecklistAsync(List<ChecklistTaskItem> tasks);

    [WorkflowSignal]
    Task UpdateHeartbeatAsync(string heartbeat);

    [WorkflowQuery]
    JobWorkflowState GetCurrentState();
}
