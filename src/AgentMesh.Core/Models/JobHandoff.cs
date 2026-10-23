using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

public enum JobHandoffStatus
{
    /// <summary>Outgoing persona declared intent; incoming agent is NOT yet released.</summary>
    Pending,

    /// <summary>Incoming agent released. See <see cref="JobHandoff.ReleaseReason"/>.</summary>
    Released,

    /// <summary>Incoming turn session created and dispatched to its runner.</summary>
    InFlight,

    /// <summary>Incoming turn completed. Terminal.</summary>
    Completed,

    /// <summary>Abandoned before release. Terminal.</summary>
    Cancelled,

    /// <summary>Release attempted but could not dispatch the incoming turn. Terminal.</summary>
    Failed
}

/// <summary>Why the incoming agent was let go. Kept separate from status so the lifecycle stays readable.</summary>
public enum JobHandoffReleaseReason
{
    /// <summary>Outgoing agent explicitly confirmed its work was saved. Normal path.</summary>
    OutgoingConfirmed,

    /// <summary>Grace window elapsed with no confirmation. Incoming agent released anyway.</summary>
    GraceExpired,

    /// <summary>Operator pressed "hand over now".</summary>
    OperatorOverride
}

/// <summary>
/// One live handoff of a job from one persona to another.
///
/// The release gate is the point: the outgoing agent keeps its turn slot (and therefore the
/// workspace lease) until it says "saved, hand over" — so we never have two agents writing to the
/// same job branch at once. If the outgoing agent dies before confirming, the deadline releases the
/// handoff anyway; a stuck job is worse than a possibly-dirty handoff.
/// </summary>
public class JobHandoff
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }

    [JsonIgnore]
    public ProjectJob? Job { get; set; }

    public string ProjectId { get; set; } = string.Empty;

    public string FromPersonaId { get; set; } = string.Empty;
    public string ToPersonaId { get; set; } = string.Empty;

    /// <summary>Session of the outgoing turn that declared the handoff. Authorises its confirm.</summary>
    public Guid? FromTurnSessionId { get; set; }

    /// <summary>Session created for the incoming agent. Null until released.</summary>
    public Guid? ToTurnSessionId { get; set; }

    public JobHandoffStatus Status { get; set; } = JobHandoffStatus.Pending;
    public JobHandoffReleaseReason? ReleaseReason { get; set; }

    /// <summary>Free-text note from the outgoing agent's confirmation message.</summary>
    public string? HandoffNote { get; set; }

    /// <summary>Snapshot the incoming agent is expected to hydrate from.</summary>
    public string? ExpectedHeadCommit { get; set; }

    public string? Error { get; set; }

    /// <summary>Wall-clock deadline after which the incoming agent is released without confirmation.</summary>
    public DateTimeOffset ReleaseDeadlineAt { get; set; }

    public DateTimeOffset DeclaredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
