using AgentMesh.Core.Models;

namespace AgentMesh.Api.Services;

public record DeclareHandoffRequest(
    Guid JobId,
    string FromPersonaId,
    string ToPersonaId,
    Guid? FromTurnSessionId,
    /// <summary>The outgoing agent's message that carried the @mention.</summary>
    string? DeclarationMessage = null);

public record JobHandoffDto(
    Guid Id,
    Guid JobId,
    string ProjectId,
    string FromPersonaId,
    string ToPersonaId,
    JobHandoffStatus Status,
    string? ReleaseReason,
    string? HandoffNote,
    string? ExpectedHeadCommit,
    Guid? FromTurnSessionId,
    Guid? ToTurnSessionId,
    DateTimeOffset DeclaredAt,
    DateTimeOffset ReleaseDeadlineAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public record HandoffDecision(bool Accepted, JobHandoff? Handoff, string? Reason, HandoffConfirmCode? Code = null);

/// <summary>Machine-readable outcomes for a confirm attempt, so runners can branch without string matching.</summary>
public enum HandoffConfirmCode
{
    Released,
    AlreadyReleased,
    /// <summary>No gate is open — a plain turn that did not hand anything over. Not an error.</summary>
    NothingPending,
    /// <summary>Right agent, wrong turn: a stale session must not release a gate a newer turn opened.</summary>
    StaleTurn,
    /// <summary>Caller is not the outgoing agent.</summary>
    NotOutgoingAgent
}

/// <summary>
/// Live handoff of a job between personas, with the incoming agent held at a gate.
///
/// Why a gate at all: the outgoing runner commits and pushes *after* its response message is
/// posted. Dispatching the incoming agent on the strength of the @mention alone means it can clone
/// a branch that does not yet contain the work it was handed. The gate closes that window.
///
/// Release happens on the outgoing agent's explicit confirmation, on deadline expiry, or on
/// operator override — whichever comes first. Confirmation is emitted by the runner once its save
/// actually succeeded, not by the model, because "I saved it" is exactly the kind of claim we
/// should not take on trust.
/// </summary>
public interface IJobHandoffService
{
    Task<HandoffDecision> DeclareAsync(DeclareHandoffRequest req, CancellationToken ct = default);

    Task<JobHandoff?> GetOpenAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Called by the outgoing runner after commit+push succeeded.
    /// <paramref name="turnSessionId"/> is checked when supplied so a stale turn cannot release a
    /// gate that a newer turn opened.
    /// </summary>
    Task<HandoffDecision> ConfirmAsync(
        Guid jobId,
        string agentId,
        string? note = null,
        bool operatorOverride = false,
        Guid? turnSessionId = null,
        CancellationToken ct = default);

    /// <summary>Releases every handoff whose grace window has elapsed. Returns how many were released.</summary>
    Task<int> ExpirePendingAsync(CancellationToken ct = default);

    /// <summary>Marks the incoming turn finished once its session reaches a terminal state.</summary>
    Task CompleteAsync(Guid turnSessionId, CancellationToken ct = default);

    Task CancelAsync(Guid jobId, string reason, CancellationToken ct = default);

    Task<JobHandoffDto?> GetDtoAsync(Guid jobId, CancellationToken ct = default);
}
