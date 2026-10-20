using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

public class JobMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }

    [JsonIgnore]
    public ProjectJob? Job { get; set; }

    public string SenderType { get; set; } = "user"; // "user" | "persona"
    public string? SenderId { get; set; } // "user" or personaId like "astra"
    public string? SenderName { get; set; }
    public string Content { get; set; } = string.Empty;

    public string MentionsJson { get; set; } = "[]"; // e.g. ["astra", "qa"]
    public Guid? TurnSessionId { get; set; } // Link to detailed AgentSession for full execution logs
    public string? StructuredPayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
