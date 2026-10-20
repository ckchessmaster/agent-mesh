using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

public class ProjectPersona
{
    public string ProjectId { get; set; } = string.Empty;
    [JsonIgnore]
    public Project? Project { get; set; }

    public string PersonaId { get; set; } = string.Empty;
    [JsonIgnore]
    public Persona? Persona { get; set; }

    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
}
