using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class AgentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AgentSession? Session { get; set; }
    public string RequestedByAgentId { get; set; } = string.Empty;
    public Agent? RequestedByAgent { get; set; }
    public string TargetService { get; set; } = string.Empty;
    public string ActionVerb { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.AutoApproved;
    public ExecutionStatus ExecutionStatus { get; set; } = ExecutionStatus.Staged;
    public string? TemporalWorkflowId { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
