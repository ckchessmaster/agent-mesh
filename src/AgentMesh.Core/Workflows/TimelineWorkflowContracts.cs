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

/// <summary>
/// Free-form operator guidance delivered while a plan gate is open, which is not a decision.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the gate had exactly two exits: approve, or reject with feedback. Rejecting marks the
/// card Rejected and says so on the timeline, so an operator who wanted to add a fact the agent could
/// not have known ("the billing module is frozen this sprint") had to file it as a criticism of a plan
/// that was not wrong, and an operator whose job skipped Discovery Q&A entirely had no way to reach
/// the agent at all until the implementation tasks were already running.
/// </para>
/// <para>
/// Guidance therefore must not borrow <see cref="PlanApprovalSignal"/> with <c>Approved = false</c>:
/// that would write a Rejected card, and the record of what the operator asked for would be indistinguishable
/// from a record of what the operator refused. The gate stays open and the card stays waiting.
/// </para>
/// </remarks>
/// <param name="CardId">The PlanApproval card the guidance was written against.</param>
/// <param name="Content">Raw operator text, handed to Stage 1B as planning context.</param>
/// <param name="SenderId">Who wrote it; "operator" from the cockpit.</param>
/// <param name="MessageId">The persisted comment row id, so a retried POST cannot wake the gate twice.</param>
public record PlanGuidanceSignal(
    Guid CardId,
    string Content,
    string SenderId,
    string? MessageId = null
);

public record AbortActiveTurnSignal(
    string Reason,
    string RequestedBy
);

public record TurnInterventionSignal(
    string Action, // "resume" | "retry_step" | "realign"
    string? AdditionalContext = null
);

public record JobAssigneeUpdatedSignal(
    string? AssignedAgentId
);

public record JobExecutionContinuationState(
    int ReviewRound,
    Guid? InitCardId,
    Guid? PrCardId,
    string? ActivePrUrl,
    int? ActivePrNumber,
    string? CandidateHeadSha,
    int CompletedTaskOffset,
    IReadOnlyList<ProposedChecklistItem> PendingTasks,
    string? RemediationContext = null
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
    JobExecutionContinuationState? Continuation = null,
    int? ContinueAsNewAfterRounds = null
)
{
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
        string? ProjectId = null,
        string? RemediationContext = null)
        : base(
            JobId: JobId,
            TaskId: TaskId,
            AgentId: AssignedAgentId,
            ProjectId: ProjectId ?? JobId,
            WorkspacePath: WorkspacePath,
            TargetBranch: TargetBranch,
            PersonaId: AssignedAgentId,
            TaskIndex: TaskIndex,
            TaskTitle: TaskTitle,
            RemediationContext: RemediationContext)
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
    string? PrUrl = null,
    string? ApprovedHeadSha = null
);

public record JobWorkflowState(
    int CurrentTaskIndex,
    int TotalTasks,
    string CurrentHeartbeat,
    int ReviewRound = 0,
    string? ActivePrUrl = null,
    int IgnoredSelfReviewCount = 0,
    string? AssignedAgentId = null
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
    Task SubmitPlanGuidanceAsync(PlanGuidanceSignal signal);

    [WorkflowSignal]
    Task AbortTurnAsync(AbortActiveTurnSignal signal);

    [WorkflowSignal]
    Task InterveneTurnAsync(TurnInterventionSignal signal);

    [WorkflowSignal]
    Task UpdateAssigneeAsync(JobAssigneeUpdatedSignal signal);

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
    Task UpdateAssigneeAsync(JobAssigneeUpdatedSignal signal);

    [WorkflowSignal]
    Task ReceiveChatMessageAsync(ChatMessageSignal signal);

    [WorkflowSignal]
    Task ReceivePrReviewSignalAsync(PrReviewSignal signal);

    [WorkflowQuery]
    TaskStatusView GetTaskStatus();

    Task<TaskExecutionResult> RunAsync(TaskExecutionInput input) => ExecuteTaskAsync(input);
}
