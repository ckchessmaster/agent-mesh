using System.Text.Json.Serialization;
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class Project
{
    public string Id { get; set; } = string.Empty; // slug identifier, e.g. "agent-mesh"
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WorkspaceType WorkspaceType { get; set; } = WorkspaceType.Git;

    public string? GitRepositoryUrl { get; set; }
    public string GitBaseBranch { get; set; } = "main";

    public string? S3BucketName { get; set; }
    public string? S3PrefixKey { get; set; }

    public string ProjectInstructions { get; set; } = string.Empty;
    public string? DefaultLeadPersonaId { get; set; }

    [JsonIgnore]
    public Persona? DefaultLeadPersona { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<ProjectPersona> ProjectPersonas { get; set; } = new List<ProjectPersona>();

    [JsonIgnore]
    public ICollection<ProjectJob> Jobs { get; set; } = new List<ProjectJob>();
}
