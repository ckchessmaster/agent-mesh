namespace AgentMesh.Core.Models;

public enum PlanApprovalPolicy
{
    LeadOrHuman = 0,   // Assigned Team Lead or Human Operator can approve the plan
    HumanOnly = 1      // Strictly requires authenticated Human Operator sign-off
}

public enum JobStatus
{
    Planning,          // Discovery phase in progress
    AwaitingApproval,  // Plan proposed, waiting on approval signal
    InProgress,        // Plan approved, tasks actively executing
    AwaitingReview,    // Pull request opened, awaiting code review
    Completed,         // PR merged and verified
    Blocked,           // Escalated blocker (e.g. 3-strike threshold reached)
    Failed             // Fatal unrecoverable runtime failure
}
