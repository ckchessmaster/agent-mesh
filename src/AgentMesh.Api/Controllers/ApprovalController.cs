using AgentMesh.Api.Hubs;
using AgentMesh.Core.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Temporalio.Client;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/jobs/{id:guid}/approval")]
[Authorize]
public class ApprovalController(
    ITemporalClient temporalClient,
    IHubContext<AgentMeshHub> hubContext,
    ILogger<ApprovalController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> SubmitApproval(Guid id, [FromBody] ApprovalDecisionRequest req)
    {
        var reviewer = User.Identity?.Name ?? "WebOperator";
        var workflowId = $"job-{id}";

        logger.LogInformation("Submitting approval for {WorkflowId}: Approved={Approved} by {Reviewer}",
            workflowId, req.Approved, reviewer);

        var handle = temporalClient.GetWorkflowHandle<AgentTaskWorkflow>(workflowId);
        await handle.SignalAsync(wf => wf.SubmitApproval(req.Approved, reviewer, req.Reason));

        await hubContext.Clients.All.SendAsync("JobApprovalUpdated", new
        {
            jobId = id,
            approved = req.Approved,
            reviewer,
            reason = req.Reason
        });

        return Ok(new { status = "signal_sent", job_id = id, approved = req.Approved });
    }
}

public record ApprovalDecisionRequest(bool Approved, string? Reason);
