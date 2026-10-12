using System.ComponentModel;
using ModelContextProtocol.Server;
using Temporalio.Client;
using AgentMesh.Core.Workflows;

namespace AgentMesh.Api.Tools;

[McpServerToolType]
public class StagingTools(ITemporalClient temporalClient, ILogger<StagingTools> logger)
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

        var input = new AgentTaskWorkflowInput(
            JobId: jobId,
            SessionId: Guid.Parse(sessionId),
            TargetService: targetService,
            ActionVerb: actionVerb,
            PayloadJson: payloadJson,
            RequestedByAgentId: "gemini-spark"
        );

        logger.LogInformation("Starting Temporal workflow {WorkflowId} for service {Service}:{Verb}", 
            workflowId, targetService, actionVerb);

        await temporalClient.StartWorkflowAsync(
            (AgentTaskWorkflow wf) => wf.RunAsync(input),
            new WorkflowOptions(id: workflowId, taskQueue: "agent-task-queue"));

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
