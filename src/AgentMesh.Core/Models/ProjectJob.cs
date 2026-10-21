using System.Text.Json.Serialization;
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class JobTaskDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public string? AssignedPersonaId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
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
