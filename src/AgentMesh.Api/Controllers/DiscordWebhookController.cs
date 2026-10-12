using AgentMesh.Core.Workflows;
using Microsoft.AspNetCore.Mvc;
using Temporalio.Client;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/webhooks/discord")]
public class DiscordWebhookController(ITemporalClient temporalClient, ILogger<DiscordWebhookController> logger) : ControllerBase
{
    [HttpPost("interactions")]
    public async Task<IActionResult> HandleInteraction([FromBody] DiscordInteractionPayload payload)
    {
        // Type 1: Discord PING verification
        if (payload.Type == 1)
        {
            logger.LogInformation("Received Discord PING interaction");
            return Ok(new { type = 1 });
        }

        // Type 3: Message Component Button Click
        if (payload.Type == 3 && payload.Data?.CustomId != null)
        {
            var parts = payload.Data.CustomId.Split(':');
            var action = parts[0]; // "approve" or "reject"
            var jobId = Guid.Parse(parts[1]);
            bool approved = action.Equals("approve", StringComparison.OrdinalIgnoreCase);

            var reviewer = payload.Member?.User?.Username ?? "DiscordUser";
            logger.LogInformation("Received Discord interaction for job {JobId}: Action={Action} by {Reviewer}",
                jobId, action, reviewer);

            var handle = temporalClient.GetWorkflowHandle<AgentTaskWorkflow>($"job-{jobId}");
            await handle.SignalAsync(wf => wf.SubmitApproval(approved, reviewer, null));

            return Ok(new
            {
                type = 4, // ChannelMessageWithSource
                data = new
                {
                    content = $"Decision recorded: **{(approved ? "APPROVED" : "REJECTED")}** by {reviewer}."
                }
            });
        }

        return BadRequest();
    }
}

public class DiscordInteractionPayload
{
    public int Type { get; set; }
    public DiscordInteractionData? Data { get; set; }
    public DiscordMember? Member { get; set; }
}

public class DiscordInteractionData
{
    [System.Text.Json.Serialization.JsonPropertyName("custom_id")]
    public string? CustomId { get; set; }
}
public class DiscordMember { public DiscordUser? User { get; set; } }
public class DiscordUser { public string? Username { get; set; } }
