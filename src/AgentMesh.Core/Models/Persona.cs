using System.Text.Json.Serialization;
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class Persona
{
    public string Id { get; set; } = string.Empty; // e.g. 'system-architect'
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "Autonomous Persona";
    public string PersonaPrompt { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty; // e.g. 'agy', 'copilot', 'claude'

    [JsonIgnore]
    public Agent? Driver { get; set; }

    public string? DefaultModel { get; set; }
    public string? DefaultEffort { get; set; }
    public string? DefaultMode { get; set; }
    public string? DefaultTier { get; set; }
    public string? DefaultContextSize { get; set; }
    public string? DefaultWorkingDirectory { get; set; }
    public TrustTier TrustTier { get; set; } = TrustTier.Intern;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<AgentSession> Sessions { get; set; } = new List<AgentSession>();

    [JsonIgnore]
    public ICollection<ProjectPersona> ProjectPersonas { get; set; } = new List<ProjectPersona>();
}
