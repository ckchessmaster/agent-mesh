namespace AgentMesh.Core.Models;

public class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string MetadataJson { get; set; } = "{}";
    public string? CreatorAgentId { get; set; }
    public Agent? CreatorAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<SessionMessage> Messages { get; set; } = new List<SessionMessage>();
    public ICollection<AgentJob> Jobs { get; set; } = new List<AgentJob>();
}
