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

public record AppendRemediationTasksInput(
    string JobId,
    int Round,
    string Source,
    IReadOnlyList<ProposedChecklistItem> Tasks
);

/// <summary>
/// Why a job execution workflow ended without success, to be written onto the job itself.
/// </summary>
/// <param name="JobId">The job whose run halted.</param>
/// <param name="Reason">
/// The halt reason as the workflow reported it. The activity adds the <c>Halted:</c> heartbeat
/// prefix when the reason does not already carry it; the prefix is what the cockpit's lifecycle
/// derivation reads to render the job as Blocked rather than still-working.
/// </param>
public record PersistJobHaltInput(
    string JobId,
    string Reason
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

    [Activity]
    Task<IReadOnlyList<ProposedChecklistItem>> AppendRemediationTasksAsync(AppendRemediationTasksInput input) =>
        Task.FromResult(input.Tasks);

    /// <summary>
    /// Writes a failed job-execution outcome onto the job row so a halted workflow cannot leave the
    /// job claiming it is still working.
    /// </summary>
    /// <remarks>
    /// Default no-op for hosts that do not register it: a settle failure must never be able to mask
    /// the job's actual halt reason, and an embedding that persists outcomes some other way (or runs
    /// the workflow result-await style) should not be forced to implement a second write path. The
    /// production worker overrides this with the database write.
    /// </remarks>
    [Activity]
    Task PersistJobHaltAsync(PersistJobHaltInput input) => Task.CompletedTask;
}
