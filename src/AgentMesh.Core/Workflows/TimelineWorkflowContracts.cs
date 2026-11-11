using System.Text.Json.Serialization;
using AgentMesh.Core.Models;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

public record DiscoveryQuestion(
    string Id,
    string Question,
    IReadOnlyList<string>? Options = null
);

public record DiscoveryPayload(
    IReadOnlyList<DiscoveryQuestion> Questions,
    string Summary
);

public record ProposedChecklistItem(
    string TaskId,
    string Title,
    string TargetFile,
    string VerificationMethod
);

public record PlanApprovalPayload(
    IReadOnlyList<ProposedChecklistItem> Tasks,
    string Reasoning
);

public record DiscoveryAnswersSignal(
    Guid CardId,
    Dictionary<string, string> Answers,
    string? GeneralFeedback = null
);

public record PlanApprovalSignal(
    Guid CardId,
    bool Approved,
    string? Feedback,
    string ApproverId
);

public record AbortActiveTurnSignal(
    string Reason,
    string RequestedBy
);

public record TurnInterventionSignal(
    string Action, // "resume" | "retry_step" | "realign"
    string? AdditionalContext = null
);

[method: JsonConstructor]
public record JobExecutionInput(
    string JobId,
    string ProjectId,
    string WorkspacePath,
    string InitialPrompt,
    string TargetBranch,
    string AssignedAgentId,
    string ApprovalPolicy = "LeadOrHuman"
)
{
    // Backwards-compatible constructor for existing tests/callers that pass PlanApprovalPolicy and different parameter order:
    public JobExecutionInput(
        string JobId,
        string AssignedAgentId,
        string ProjectId,
        string WorkspacePath,
        string InitialPrompt,
        string TargetBranch,
        PlanApprovalPolicy ApprovalPolicy)
        : this(JobId, ProjectId, WorkspacePath, InitialPrompt, TargetBranch, AssignedAgentId, ApprovalPolicy.ToString())
    {
    }
}

public record ChildTaskExecutionInput(
    string JobId,
    string TaskId,
    int TaskIndex,
    string TaskTitle,
    string AssignedAgentId,
    string WorkspacePath,
    string TargetBranch
);

public record JobExecutionResult(
    bool Success,
    string Message
)
{
    public string Summary => Message;
}

public record CommandInput(
    string CommandLine,
    string WorkspacePath
);

public record ExecutionOutput(
    int ExitCode,
    string Output
);

public record DiscoveryTurnInput(
    string JobId,
    string AssignedAgentId,
    string WorkspacePath,
    string InitialPrompt,
    string? ProjectId = null,
    string? TargetBranch = null,
    ChatMessageSignal? PendingMessage = null
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

public record JobWorkflowState(
    int CurrentTaskIndex,
    int TotalTasks,
    string CurrentHeartbeat
);

[Workflow]
public interface IJobExecutionWorkflow
{
    [WorkflowRun]
    Task<JobExecutionResult> RunAsync(JobExecutionInput input);

    [WorkflowSignal]
    Task SubmitDiscoveryAnswersAsync(DiscoveryAnswersSignal signal);

    [WorkflowSignal]
    Task SubmitPlanDecisionAsync(PlanApprovalSignal signal);

    [WorkflowSignal]
    Task AbortTurnAsync(AbortActiveTurnSignal signal);

    [WorkflowSignal]
    Task InterveneTurnAsync(TurnInterventionSignal signal);

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

    Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input) => RunAsync(input);
}

[Workflow]
public interface ITaskExecutionWorkflow
{
    [WorkflowRun]
    Task<TaskExecutionResult> ExecuteTaskAsync(ChildTaskExecutionInput input);

    [WorkflowSignal]
    Task AbortTurnAsync(AbortActiveTurnSignal signal);

    [WorkflowSignal]
    Task InterveneTurnAsync(TurnInterventionSignal signal);

    [WorkflowSignal]
    Task ReceiveChatMessageAsync(ChatMessageSignal signal);

    [WorkflowSignal]
    Task ReceivePrReviewSignalAsync(PrReviewSignal signal);

    [WorkflowQuery]
    TaskStatusView GetTaskStatus();

    Task<TaskExecutionResult> RunAsync(ChildTaskExecutionInput input) => ExecuteTaskAsync(input);

    Task<TaskExecutionResult> ExecuteTaskAsync(TaskExecutionInput input) => ExecuteTaskAsync(
        new ChildTaskExecutionInput(input.JobId, input.TaskId, 0, input.TaskId, input.AgentId, input.WorkspacePath, input.TargetBranch));
}
