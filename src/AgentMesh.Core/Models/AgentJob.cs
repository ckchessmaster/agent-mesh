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
    public string? PayloadS3Uri { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.AutoApproved;
    public ExecutionStatus ExecutionStatus { get; set; } = ExecutionStatus.Staged;
    public string? TemporalWorkflowId { get; set; }
    public PlanApprovalPolicy ApprovalPolicy { get; set; } = PlanApprovalPolicy.LeadOrHuman;
    public string? CurrentTaskId { get; set; }
    public string? ProgressHeartbeat { get; set; }
    public string? AssignedAgentId { get; set; }
    public string? ProjectId { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? TargetBranch { get; set; }
    public string? ResultJson { get; set; }
    public string? ResultS3Uri { get; set; }
    public string ArtifactsManifestJson { get; set; } = "{}";
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
