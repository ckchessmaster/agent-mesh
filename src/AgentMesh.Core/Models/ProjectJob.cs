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

    /// <summary>
    /// The condition that makes this item done, written when the task was created.
    /// </summary>
    /// <remarks>
    /// Advisory and never parsed for control flow — the workflow parks on <see cref="Status"/>, not on
    /// this. It exists because the prompt stack renders it into the turn-0 envelope: an agent that can
    /// see the bar it is being held to stops guessing at scope, and the two failures this platform keeps
    /// re-diagnosing (an agent declaring a task complete on a passing build, an agent escalating a
    /// question the task description already answered) are both scope-guessing failures.
    /// <para>
    /// Additive and stored inside the <c>tasks_json</c> blob like <see cref="Status"/>, so it needs no
    /// migration and every pre-existing payload still deserialises, with this field null. Renderers must
    /// omit the line when it is absent rather than print an empty heading.
    /// </para>
    /// </remarks>
    public string? AcceptanceCriteria { get; set; }
    public string? TargetFile { get; set; }
    public string? VerificationMethod { get; set; }

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

    public PlanApprovalPolicy ApprovalPolicy { get; set; } = PlanApprovalPolicy.LeadOrHuman;
    public string? CurrentTaskId { get; set; }
    public string? ProgressHeartbeat { get; set; }
    public string? AssignedAgentId { get; set; }
    public string? WorkingDirectory { get; set; }
    public string TargetBranch => GitBranchName;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<JobMessage> Messages { get; set; } = new List<JobMessage>();
}
