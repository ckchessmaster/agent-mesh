using System.ComponentModel;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Services;
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
    IHubContext<AgentMeshHub>? hubContext = null,
    IStorageService? storageService = null,
    IConfiguration? configuration = null)
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

        string? payloadS3Uri = null;
        var threshold = configuration?.GetValue<int>("Storage:S3:ThresholdBytes", 65536) ?? 65536;

        if (payloadJson.Length > threshold && storageService != null)
        {
            var s3Key = $"jobs/{jobId}/payload.json";
            payloadS3Uri = await storageService.UploadTextAsync(s3Key, payloadJson, "application/json", ct);
            logger.LogInformation("Offloaded large payload for job {JobId} ({Bytes} chars) to {S3Uri}",
                jobId, payloadJson.Length, payloadS3Uri);

            payloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                _offloaded = true,
                s3_uri = payloadS3Uri,
                byte_size = payloadJson.Length,
                preview = payloadJson[..Math.Min(payloadJson.Length, 512)]
            });
        }

        var input = new AgentTaskWorkflowInput(
            JobId: jobId,
            SessionId: Guid.Parse(sessionId),
            TargetService: targetService,
            ActionVerb: actionVerb,
            PayloadJson: payloadJson,
            RequestedByAgentId: agentId,
            PayloadS3Uri: payloadS3Uri
        );

        logger.LogInformation("Starting Temporal workflow {WorkflowId} for service {Service}:{Verb} requested by {AgentId} (S3: {S3Uri})", 
            workflowId, targetService, actionVerb, agentId, payloadS3Uri ?? "inline");

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
                    requestedByAgentId = agentId,
                    payloadS3Uri
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
            payload_s3_uri = payloadS3Uri,
            message = payloadS3Uri != null 
                ? "Task successfully offloaded to SeaweedFS and queued for background evaluation."
                : "Task successfully queued for background evaluation."
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
