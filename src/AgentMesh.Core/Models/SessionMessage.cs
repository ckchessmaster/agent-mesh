namespace AgentMesh.Core.Models;

public class SessionMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AgentSession? Session { get; set; }
    public string SenderAgentId { get; set; } = string.Empty;
    public Agent? SenderAgent { get; set; }
    public string? TargetAgentId { get; set; }
    public Agent? TargetAgent { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? StructuredPayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
