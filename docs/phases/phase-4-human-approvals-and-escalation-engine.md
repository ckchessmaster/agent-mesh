# Phase 4: Human Approvals & Escalation Engine

## 1. Phase Objective & Scope

The objective of Phase 4 is to implement durable Human-in-the-Loop (HITL) escalations and hierarchical multi-agent approval boundaries.

High-risk mutations (actions where `TargetService != 'platform_ops'`) are paused at the Temporal workflow boundary. The workflow triggers multi-channel escalation notifications and durably waits up to 7 days for an approval signal. Approvals are unified through a single API endpoint (`POST /api/jobs/{id}/approval`) accessible by interactive Discord bot buttons and the Web Cockpit, while status changes stream in real-time via SignalR.

---

## 2. Prerequisites & Environment

* Completion of **Phase 1**, **Phase 2**, and **Phase 3**.
* Discord Bot Token & Channel ID (or local mock webhook simulator for local testing).
* SignalR client capabilities (for real-time frontend streaming).

---

## 3. Architecture & Dual Approval Flow

```text
                  AgentTaskWorkflow
                         │
                         ▼ (Requires Human Review)
            NotificationActivities.SendDiscordApprovalRequest
                         │
        ┌────────────────┴────────────────┐
        ▼                                 ▼
Interactive Discord Embed          Web Cockpit (SignalR /hubs/agent-mesh)
[Approve]      [Reject]            [Approve]           [Reject]
    │             │                    │                  │
    └──────┬──────┘                    └─────────┬────────┘
           ▼                                     ▼
POST /api/webhooks/discord              POST /api/jobs/{id}/approval
           │                                     │
           └─────────────────┬───────────────────┘
                             ▼
               AgentMesh.Api / ApprovalService
                             │
                             ▼ ITemporalClient.SignalWorkflowAsync("SubmitApproval")
                      AgentTaskWorkflow
                             │
                             ▼
              ExecutionActivities.ExecuteSubsystemAction(...)
```

---

## 4. Step-by-Step Implementation Guide

### Step 1: Temporal Workflow Approval Signal (`AgentMesh.Core`)

Update `AgentTaskWorkflow.cs` to handle full approval context:

```csharp
// src/AgentMesh.Core/Workflows/AgentTaskWorkflow.cs
using Temporalio.Workflows;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;

namespace AgentMesh.Core.Workflows;

[Workflow]
public class AgentTaskWorkflow
{
    private bool _approvalReceived = false;
    private bool _isApproved = false;
    private string? _reviewer;
    private string? _rejectionReason;

    [WorkflowSignal]
    public async Task SubmitApproval(bool approved, string reviewer, string? reason = null)
    {
        _isApproved = approved;
        _reviewer = reviewer;
        _rejectionReason = reason;
        _approvalReceived = true;
    }

    [WorkflowRun]
    public async Task<string> RunAsync(AgentTaskWorkflowInput input)
    {
        // 1. Stage initial record
        await Workflow.ExecuteActivityAsync(
            (IDatabaseActivities act) => act.CreateStagedJobRecord(input),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        // 2. High risk boundary evaluation
        bool requiresHumanReview = input.TargetService != "platform_ops";

        if (requiresHumanReview)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "pending_human"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            // Send interactive Discord notification
            await Workflow.ExecuteActivityAsync(
                (INotificationActivities act) => act.SendDiscordApprovalRequest(input),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) });

            // Durable pause up to 7 days
            await Workflow.WaitConditionAsync(() => _approvalReceived, TimeSpan.FromDays(7));

            if (!_isApproved)
            {
                var reason = _rejectionReason ?? "Rejected by operator";
                await Workflow.ExecuteActivityAsync(
                    (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "rejected", null, reason),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
                return $"Task rejected by {_reviewer}: {reason}";
            }

            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "approved"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
        }

        // 3. Execution Phase
        try
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateExecutionStatus(input.JobId, "running"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            var result = await Workflow.ExecuteActivityAsync(
                (IExecutionActivities act) => act.ExecuteSubsystemAction(input.TargetService, input.ActionVerb, input.PayloadJson),
                new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromMinutes(5),
                    RetryPolicy = new() { InitialInterval = TimeSpan.FromSeconds(2), MaximumAttempts = 3 }
                });

            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "completed", result, null),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            return result;
        }
        catch (Exception ex)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "failed", null, ex.Message),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
            throw;
        }
    }
}
```

---

### Step 2: Implement Discord Notification Activity (`AgentMesh.Worker`)

```csharp
// src/AgentMesh.Core/Activities/INotificationActivities.cs
using AgentMesh.Core.Workflows;
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

[ActivityInterface]
public interface INotificationActivities
{
    [Activity]
    Task SendDiscordApprovalRequest(AgentTaskWorkflowInput input);
}
```

```csharp
// src/AgentMesh.Worker/Activities/NotificationActivities.cs
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
```

---

### Step 3: Implement Unified Approval & Discord Webhook Endpoint (`AgentMesh.Api`)

```csharp
// src/AgentMesh.Api/Controllers/ApprovalController.cs
using AgentMesh.Core.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Temporalio.Client;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/jobs/{id:guid}/approval")]
[Authorize]
public class ApprovalController(ITemporalClient temporalClient, ILogger<ApprovalController> logger) : ControllerBase
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

        return Ok(new { status = "signal_sent", job_id = id, approved = req.Approved });
    }
}

public record ApprovalDecisionRequest(bool Approved, string? Reason);
```

```csharp
// src/AgentMesh.Api/Controllers/DiscordWebhookController.cs
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

public class DiscordInteractionData { public string? CustomId { get; set; } }
public class DiscordMember { public DiscordUser? User { get; set; } }
public class DiscordUser { public string? Username { get; set; } }
```

---

### Step 4: Real-time SignalR Hub (`AgentMesh.Api`)

```csharp
// src/AgentMesh.Api/Hubs/AgentMeshHub.cs
using Microsoft.AspNetCore.SignalR;

namespace AgentMesh.Api.Hubs;

public class AgentMeshHub : Hub
{
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session-{sessionId}");
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session-{sessionId}");
    }
}
```

Register SignalR in `src/AgentMesh.Api/Program.cs`:
```csharp
builder.Services.AddSignalR();
// ...
app.MapHub<AgentMeshHub>("/hubs/agent-mesh");
```

---

## 5. Verification & Acceptance Criteria

### Test 1: Stage a High-Risk Job Requiring Approval
```bash
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/call",
    "params": {
      "name": "stage_agent_intent",
      "arguments": {
        "targetService": "github",
        "actionVerb": "apply_diff",
        "payloadJson": "{\"repo\":\"agent-mesh\",\"branch\":\"feature/eval\"}",
        "sessionId": "a0000000-0000-0000-0000-000000000001"
      }
    }
  }'
```
* **Verify**: Workflow starts in Temporal UI, but pauses at `WaitConditionAsync`.
* **Verify Database**: `approval_status = 'PendingHuman'`, `execution_status = 'Staged'`.

### Test 2: Submit Approval via API
```bash
curl -X POST http://localhost:5000/api/jobs/<JOB_ID>/approval \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer mesh-default-secret-key" \
  -d '{"approved": true, "reason": "Verified security bounds"}'
```
* **Verify**: Temporal unblocks immediately.
* **Verify Execution**: `ExecutionActivities.ExecuteSubsystemAction` runs.
* **Verify Final Database**: `approval_status = 'Approved'`, `execution_status = 'Completed'`.

---

## 6. Agent Execution Prompt & Checklist

```markdown
### AI Developer Task: Phase 4 Execution
Please implement Phase 4 of AgentMesh according to docs/phases/phase-4-human-approvals-and-escalation-engine.md.
Follow these steps:
- [ ] Add SubmitApproval signal with reviewer and reason to AgentTaskWorkflow.
- [ ] Implement NotificationActivities for Discord webhooks with interactive components.
- [ ] Implement ApprovalController (POST /api/jobs/{id}/approval) to signal Temporal.
- [ ] Implement DiscordWebhookController for handling button clicks.
- [ ] Set up SignalR AgentMeshHub at /hubs/agent-mesh.
- [ ] Test approval unblocking and rejection termination flows.
```
