using System.Net.Http.Json;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Workflows;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class NotificationActivities(HttpClient httpClient, IConfiguration config, ILogger<NotificationActivities> logger) : INotificationActivities
{
    [Activity]
    public async Task SendDiscordApprovalRequest(AgentTaskWorkflowInput input)
    {
        var webhookUrl = config["Discord:WebhookUrl"];
        if (string.IsNullOrEmpty(webhookUrl))
        {
            logger.LogWarning("Discord:WebhookUrl not configured. Simulating approval message for job {JobId}", input.JobId);
            return;
        }

        var payload = new
        {
            content = $"⚠️ **Agent Approval Required**: Job `{input.JobId}`",
            embeds = new[]
            {
                new
                {
                    title = $"Action: {input.TargetService}:{input.ActionVerb}",
                    description = $"Requested by agent: `{input.RequestedByAgentId}`\nSession: `{input.SessionId}`",
                    fields = new[]
                    {
                        new { name = "Payload", value = $"```{input.PayloadJson}```", inline = false }
                    },
                    color = 16753920 // Orange
                }
            },
            components = new[]
            {
                new
                {
                    type = 1, // Action Row
                    components = new[]
                    {
                        new { type = 2, style = 3, label = "Approve", custom_id = $"approve:{input.JobId}" },
                        new { type = 2, style = 4, label = "Reject", custom_id = $"reject:{input.JobId}" }
                    }
                }
            }
        };

        var response = await httpClient.PostAsJsonAsync(webhookUrl, payload);
        response.EnsureSuccessStatusCode();
        logger.LogInformation("Sent Discord approval request for job {JobId}", input.JobId);
    }
}
