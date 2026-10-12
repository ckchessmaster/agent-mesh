using System.ComponentModel;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Workflows;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.Server;
using Temporalio.Client;

namespace AgentMesh.Api.Tools;

[McpServerToolType]
public class StagingTools(
    ITemporalClient temporalClient,
    ILogger<StagingTools> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    IHubContext<AgentMeshHub>? hubContext = null)
{
    [McpServerTool(Name = "stage_agent_intent")]
    [Description("Stages an operational journal entry or intent payload into the central task ledger for execution.")]
    public async Task<string> StageAgentIntent(
        [Description("Target subsystem (e.g. 'platform_ops', 'google_docs')")] string targetService,
        [Description("Action verb (e.g. 'stage_journal_entry', 'create_task')")] string actionVerb,
        [Description("Strict JSON payload with action arguments")] string payloadJson,
        [Description("Active session correlation ID")] string sessionId,
        CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        var workflowId = $"job-{jobId}";

        var agentId = httpContextAccessor?.HttpContext?.User.FindFirst("AgentId")?.Value
            ?? (httpContextAccessor?.HttpContext?.Request.Headers.TryGetValue("X-Agent-Id", out var h) == true ? h.ToString() : null)
            ?? "gemini-spark";

        var input = new AgentTaskWorkflowInput(
            JobId: jobId,
            SessionId: Guid.Parse(sessionId),
            TargetService: targetService,
            ActionVerb: actionVerb,
            PayloadJson: payloadJson,
            RequestedByAgentId: agentId
        );

        logger.LogInformation("Starting Temporal workflow {WorkflowId} for service {Service}:{Verb} requested by {AgentId}", 
            workflowId, targetService, actionVerb, agentId);

        await temporalClient.StartWorkflowAsync(
            (AgentTaskWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: workflowId, taskQueue: "agent-task-queue"));

        if (hubContext != null)
        {
            try
            {
                await hubContext.Clients.All.SendAsync("JobStaged", new
                {
                    jobId,
                    targetService,
                    actionVerb,
                    sessionId,
                    requestedByAgentId = agentId
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast JobStaged over SignalR");
            }
        }

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "staged",
            job_id = jobId,
            workflow_id = workflowId,
            message = "Task successfully queued for background evaluation."
        });
    }

    [McpServerTool(Name = "inspect_job_status")]
    [Description("Retrieves the execution status and output metrics of a previously staged task.")]
    public async Task<string> InspectJobStatus(
        [Description("The unique job ID returned during staging")] string jobId,
        CancellationToken ct)
    {
        var handle = temporalClient.GetWorkflowHandle<AgentTaskWorkflow>($"job-{jobId}");
        var desc = await handle.DescribeAsync();
        
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            job_id = jobId,
            status = desc.Status.ToString()
        });
    }
}
