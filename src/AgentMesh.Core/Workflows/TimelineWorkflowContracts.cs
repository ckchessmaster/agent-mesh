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
    string ApprovalPolicy = "LeadOrHuman",
    bool UseLegacyTurnLoop = false
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
        : this(JobId, ProjectId, WorkspacePath, InitialPrompt, TargetBranch, AssignedAgentId, ApprovalPolicy.ToString(), UseLegacyTurnLoop: true)
    {
    }
}

public record ChildTaskExecutionInput : TaskExecutionInput
{
    public string AssignedAgentId => AgentId;

    public ChildTaskExecutionInput(
        string JobId,
        string TaskId,
        int TaskIndex,
        string TaskTitle,
        string AssignedAgentId,
        string WorkspacePath,
        string TargetBranch,
        string? ProjectId = null)
        : base(
            JobId: JobId,
            TaskId: TaskId,
            AgentId: AssignedAgentId,
            ProjectId: ProjectId ?? JobId,
            WorkspacePath: WorkspacePath,
            TargetBranch: TargetBranch,
            TaskIndex: TaskIndex,
            TaskTitle: TaskTitle)
    {
    }
}

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

[Workflow("JobExecutionWorkflow")]
public interface IJobExecutionWorkflow
{
    [WorkflowRun]
    Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input);

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

    Task<JobExecutionResult> RunAsync(JobExecutionInput input) => ExecuteJobAsync(input);
}

[Workflow("TaskExecutionWorkflow")]
public interface ITaskExecutionWorkflow
{
    [WorkflowRun]
    Task<TaskExecutionResult> ExecuteTaskAsync(TaskExecutionInput input);

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

    Task<TaskExecutionResult> RunAsync(TaskExecutionInput input) => ExecuteTaskAsync(input);
}
