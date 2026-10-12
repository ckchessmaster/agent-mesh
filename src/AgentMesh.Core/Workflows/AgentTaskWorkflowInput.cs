namespace AgentMesh.Core.Workflows;

public record AgentTaskWorkflowInput(
    Guid JobId,
    Guid SessionId,
    string TargetService,
    string ActionVerb,
    string PayloadJson,
    string RequestedByAgentId
);
