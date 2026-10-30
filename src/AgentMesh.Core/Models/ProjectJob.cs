using System.Text.Json.Serialization;
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public record JobAttachmentDto(
    string Id,
    Guid JobId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string S3Key,
    string DownloadUrl,
    DateTimeOffset CreatedAt
);

public class JobTaskDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public bool RequiresDiscussion { get; set; } = false;
    public string? AssignedPersonaId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<JobAttachmentDto>? Attachments { get; set; } = new();

    /// <summary>
    /// Lifecycle state: <c>todo</c>, <c>in_progress</c>, <c>awaiting_review</c>, or <c>completed</c>.
    /// </summary>
    /// <remarks>
    /// Null on every row written before the <c>manage_task</c> tool existed. Read it through
    /// <see cref="JobTaskStatus.Effective"/>, which derives the state from
    /// <see cref="IsCompleted"/> when this is absent — never branch on the raw field.
    /// <para>
    /// The field is additive and lives in the <c>tasks_json</c> blob, so it needs no migration and
    /// old payloads still deserialize. The React Cockpit does not yet render it; it keeps drawing
    /// the checkbox off <see cref="IsCompleted"/>, which the <c>manage_task</c> tool keeps in sync.
    /// </para>
    /// </remarks>
    public string? Status { get; set; }

    /// <summary>
    /// The most recent status note: why a task moved, or what blocked it. Written by
    /// <c>manage_task</c>; advisory, and never parsed for control flow.
    /// </summary>
    public string? StatusNotes { get; set; }

    /// <summary>When <see cref="Status"/> last changed, independent of <see cref="CompletedAt"/>.</summary>
    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary><see cref="Status"/> resolved against <see cref="IsCompleted"/>.</summary>
    public string EffectiveStatus => JobTaskStatus.Effective(Status, IsCompleted);
}

public class ProjectJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = string.Empty;

    [JsonIgnore]
    public Project? Project { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ProjectJobStatus Status { get; set; } = ProjectJobStatus.Active;
    public ProjectJobPriority Priority { get; set; } = ProjectJobPriority.Normal;

    public string GitBranchName { get; set; } = string.Empty;
    public string? S3ArchiveKey { get; set; }

    public string TasksJson { get; set; } = "[]";
    public string QueuedTurnsJson { get; set; } = "[]";
    public string? ResultSummary { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<JobMessage> Messages { get; set; } = new List<JobMessage>();
}
