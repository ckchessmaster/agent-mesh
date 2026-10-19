using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

public class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string MetadataJson { get; set; } = "{}";
    public string? CreatorAgentId { get; set; }

    [JsonIgnore]
    public Agent? CreatorAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<SessionMessage> Messages { get; set; } = new List<SessionMessage>();

    [JsonIgnore]
    public ICollection<AgentJob> Jobs { get; set; } = new List<AgentJob>();
}

