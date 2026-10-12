using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class Agent
{
    public string Id { get; set; } = string.Empty; // e.g. 'gemini-spark'
    public string Name { get; set; } = string.Empty;
    public RuntimeType RuntimeType { get; set; }
    public TrustTier TrustTier { get; set; } = TrustTier.Intern;
    public string PersonaPrompt { get; set; } = string.Empty;
    public string MemorySummary { get; set; } = string.Empty;
    public string? ApiKeyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<SessionMessage> SentMessages { get; set; } = new List<SessionMessage>();
    public ICollection<AgentJob> RequestedJobs { get; set; } = new List<AgentJob>();
}
