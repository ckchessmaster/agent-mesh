using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

public record CreateTimelineCardInput(
    string JobId,
    string? TaskId,
    TimelineCardType CardType,
    TimelineCardStatus Status,
    string Title,
    string StructuredPayloadJson
);

public record UpdateTimelineCardStatusInput(
    Guid CardId,
    TimelineCardStatus Status,
    string? StructuredPayloadJson = null,
    string? Title = null
);

public record DiscoveryQaActivityInput(
    string JobId,
    string InitialPrompt,
    string WorkspacePath
);

public record DiscoveryQaActivityResult(
    Guid CardId,
    DiscoveryPayload Payload
);

public record DecompositionActivityInput(
    string JobId,
    string InitialPrompt,
    string WorkspacePath,
    string? Feedback = null,
    Dictionary<string, string>? Answers = null
);

public record DecompositionActivityResult(
    Guid CardId,
    PlanApprovalPayload Payload
);

public record TurnInterruptedCardInput(
    string JobId,
    string TaskId,
    string Reason,
    string RequestedBy,
    string? LastOutput = null
);

public record TaskStepCardInput(
    string JobId,
    string TaskId,
    int TaskIndex,
    string TaskTitle,
    TimelineCardStatus Status,
    string? Heartbeat = null,
    string? StepDetails = null
);

public interface ITimelineActivities
{
    [Activity]
    Task<JobTimelineCard> CreateCardAsync(CreateTimelineCardInput input);

    [Activity]
    Task UpdateCardStatusAsync(UpdateTimelineCardStatusInput input);

    [Activity]
    Task<DiscoveryQaActivityResult> ExecuteDiscoveryQaActivityAsync(DiscoveryQaActivityInput input);

    [Activity]
    Task<DecompositionActivityResult> ExecuteDecompositionActivityAsync(DecompositionActivityInput input);

    [Activity]
    Task<Guid> RecordTurnInterruptedAsync(TurnInterruptedCardInput input);

    [Activity]
    Task<Guid> UpdateTaskStepCardAsync(TaskStepCardInput input);
}
