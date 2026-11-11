using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineCardType
{
    JobInitialized,
    DiscoveryQa,
    PlanApproval,
    TaskStep,
    InternalConsultation,
    TurnInterrupted,
    Escalation3R,
    PrReview
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineCardStatus
{
    Active,
    PendingInput,
    Approved,
    Rejected,
    Resolved,
    Closed
}

public class JobTimelineCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JobId { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public TimelineCardType CardType { get; set; }
    public TimelineCardStatus Status { get; set; } = TimelineCardStatus.Active;
    public string Title { get; set; } = string.Empty;
    public string StructuredPayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<JobCardComment> Comments { get; set; } = new();
}

public class JobCardComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CardId { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string SenderType { get; set; } = "operator"; // "operator" | "agent" | "system"
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public JobTimelineCard? Card { get; set; }
}
