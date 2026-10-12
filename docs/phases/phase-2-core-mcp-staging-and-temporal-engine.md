# Phase 2: Core MCP Staging Endpoint & Temporal Worker

## 1. Phase Objective & Scope

The objective of Phase 2 is to implement the zero-friction intent staging bridge via Model Context Protocol (MCP) in ASP.NET Core and wire it to a durable execution engine powered by Temporal.

By the end of this phase:
1. `AgentMesh.Api` exposes an MCP endpoint at `/mcp` implementing `stage_agent_intent` and `inspect_job_status`.
2. Any MCP client can trigger `stage_agent_intent` to enqueue an operation without requiring immediate execution or user modals.
3. `AgentMesh.Worker` polls the `agent-task-queue`, executes `AgentTaskWorkflow`, invokes database and execution activities, and updates PostgreSQL.

---

## 2. Prerequisites & Environment

* Completion of **Phase 1** (Solution scaffolding, database schema, Aspire AppHost).
* Temporal dev server container running (via Aspire or `temporal server start-dev`).
* NuGet packages:
  * `ModelContextProtocol.AspNetCore` (or `ModelContextProtocol`)
  * `Temporalio` and `Temporalio.Extensions.Hosting`

---

## 3. Architecture & Contract Specifications

```text
MCP Client (Claude / Spark / agy)
       │
       │ POST /mcp (JSON-RPC tool call: stage_agent_intent)
       ▼
AgentMesh.Api (ASP.NET Core 10)
  ├── StagingTools.cs
  │     │ 
  │     └── ITemporalClient.StartWorkflowAsync("job-{Guid}", input)
  ▼
Temporal Cluster (task queue: "agent-task-queue")
  ▼
AgentMesh.Worker (.NET 10 Temporal Worker)
  ├── AgentTaskWorkflow.cs
  │     ├── Activity 1: DatabaseActivities.CreateStagedJobRecord(...)
  │     ├── Activity 2: ExecutionActivities.ExecuteSubsystemAction(...)
  │     └── Activity 3: DatabaseActivities.UpdateJobStatus(...)
  ▼
PostgreSQL ("agentdb") -> Table: agent_jobs (Status: completed)
```

---

## 4. Step-by-Step Implementation Guide

### Step 1: Add NuGet Dependencies

```bash
# Add MCP and Temporal Client to API
dotnet add src/AgentMesh.Api package ModelContextProtocol.AspNetCore --version 1.0.0-preview*
dotnet add src/AgentMesh.Api package Temporalio --version 1.4.*
dotnet add src/AgentMesh.Api package Temporalio.Extensions.Hosting --version 1.4.*

# Add Temporal Worker packages to Worker
dotnet add src/AgentMesh.Worker package Temporalio --version 1.4.*
dotnet add src/AgentMesh.Worker package Temporalio.Extensions.Hosting --version 1.4.*

# Add Temporal contracts to Core
dotnet add src/AgentMesh.Core package Temporalio --version 1.4.*
```

---

### Step 2: Implement Workflow Contracts (`AgentMesh.Core`)

```csharp
// src/AgentMesh.Core/Workflows/AgentTaskWorkflowInput.cs
namespace AgentMesh.Core.Workflows;

public record AgentTaskWorkflowInput(
    Guid JobId,
    Guid SessionId,
    string TargetService,
    string ActionVerb,
    string PayloadJson,
    string RequestedByAgentId
);
```

```csharp
// src/AgentMesh.Core/Activities/IDatabaseActivities.cs
using AgentMesh.Core.Workflows;
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

[ActivityInterface]
public interface IDatabaseActivities
{
    [Activity]
    Task CreateStagedJobRecord(AgentTaskWorkflowInput input);

    [Activity]
    Task UpdateApprovalStatus(Guid jobId, string approvalStatus);

    [Activity]
    Task UpdateExecutionStatus(Guid jobId, string executionStatus);

    [Activity]
    Task UpdateJobStatus(Guid jobId, string executionStatus, string? resultJson, string? errorMessage);
}
```

```csharp
// src/AgentMesh.Core/Activities/IExecutionActivities.cs
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

[ActivityInterface]
public interface IExecutionActivities
{
    [Activity]
    Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson);
}
```

```csharp
// src/AgentMesh.Core/Workflows/AgentTaskWorkflow.cs
using Temporalio.Workflows;
using AgentMesh.Core.Activities;

namespace AgentMesh.Core.Workflows;

[Workflow]
public class AgentTaskWorkflow
{
    private bool _approvalReceived = false;
    private bool _isApproved = false;

    [WorkflowSignal]
    public async Task SubmitApproval(bool approved)
    {
        _isApproved = approved;
        _approvalReceived = true;
    }

    [WorkflowRun]
    public async Task<string> RunAsync(AgentTaskWorkflowInput input)
    {
        // 1. Persist initial staged job record
        await Workflow.ExecuteActivityAsync(
            (IDatabaseActivities act) => act.CreateStagedJobRecord(input),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        // 2. Routing logic: platform_ops auto-approves; others await approval (Phase 4 integration)
        bool requiresApproval = input.TargetService != "platform_ops";
        if (requiresApproval)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "pending_human"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            // Durable wait for approval signal (up to 7 days)
            await Workflow.WaitConditionAsync(() => _approvalReceived, TimeSpan.FromDays(7));

            if (!_isApproved)
            {
                await Workflow.ExecuteActivityAsync(
                    (IDatabaseActivities act) => act.UpdateJobStatus(input.JobId, "rejected", null, "Rejected by operator"),
                    new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
                return "Task rejected by operator.";
            }

            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "approved"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
        }

        // 3. Subsystem execution
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

### Step 3: Implement MCP Tools (`AgentMesh.Api`)

```csharp
// src/AgentMesh.Api/Tools/StagingTools.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using Temporalio.Client;
using AgentMesh.Core.Workflows;

namespace AgentMesh.Api.Tools;

[McpServerToolType]
public class StagingTools(ITemporalClient temporalClient, ILogger<StagingTools> logger)
{
    [McpServerTool]
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

        return System.Text.Json.JsonSerializer.Serialize(new {
            status = "staged",
            job_id = jobId,
            workflow_id = workflowId,
            message = "Task successfully queued for background evaluation."
        });
    }

    [McpServerTool]
    [Description("Retrieves the execution status and output metrics of a previously staged task.")]
    public async Task<string> InspectJobStatus(
        [Description("The unique job ID returned during staging")] string jobId,
        CancellationToken ct)
    {
        var handle = temporalClient.GetWorkflowHandle<AgentTaskWorkflow>($"job-{jobId}");
        var desc = await handle.DescribeAsync();
        
        return System.Text.Json.JsonSerializer.Serialize(new {
            job_id = jobId,
            status = desc.Status.ToString()
        });
    }
}
```

---

### Step 4: Configure `Program.cs` in `AgentMesh.Api`

```csharp
// src/AgentMesh.Api/Program.cs
using AgentMesh.Api.Tools;
using Temporalio.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AgentMesh.Infrastructure.Data.AgentMeshDbContext>("agentdb");

// Configure Temporal Client
var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
builder.Services.AddSingleton<ITemporalClient>(sp =>
    TemporalClient.ConnectAsync(new TemporalClientConnectOptions
    {
        TargetHost = temporalHost,
        Namespace = "default"
    }).GetAwaiter().GetResult()
);

// Register MCP Server
builder.Services.AddMcpServer()
    .AddToolType<StagingTools>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapMcp("/mcp");

app.Run();
```

---

### Step 5: Implement Activities and Worker Host (`AgentMesh.Worker`)

```csharp
// src/AgentMesh.Worker/Activities/DatabaseActivities.cs
using AgentMesh.Core.Activities;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class DatabaseActivities(AgentMeshDbContext dbContext, ILogger<DatabaseActivities> logger) : IDatabaseActivities
{
    [Activity]
    public async Task CreateStagedJobRecord(AgentTaskWorkflowInput input)
    {
        var job = new AgentJob
        {
            Id = input.JobId,
            SessionId = input.SessionId,
            RequestedByAgentId = input.RequestedByAgentId,
            TargetService = input.TargetService,
            ActionVerb = input.ActionVerb,
            PayloadJson = input.PayloadJson,
            ApprovalStatus = input.TargetService == "platform_ops" ? ApprovalStatus.AutoApproved : ApprovalStatus.PendingHuman,
            ExecutionStatus = ExecutionStatus.Staged,
            TemporalWorkflowId = $"job-{input.JobId}"
        };

        dbContext.AgentJobs.Add(job);
        await dbContext.SaveChangesAsync();
        logger.LogInformation("Persisted staged job {JobId}", input.JobId);
    }

    [Activity]
    public async Task UpdateApprovalStatus(Guid jobId, string approvalStatus)
    {
        var job = await dbContext.AgentJobs.FindAsync(jobId);
        if (job != null)
        {
            job.ApprovalStatus = Enum.Parse<ApprovalStatus>(approvalStatus, ignoreCase: true);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }

    [Activity]
    public async Task UpdateExecutionStatus(Guid jobId, string executionStatus)
    {
        var job = await dbContext.AgentJobs.FindAsync(jobId);
        if (job != null)
        {
            job.ExecutionStatus = Enum.Parse<ExecutionStatus>(executionStatus, ignoreCase: true);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }

    [Activity]
    public async Task UpdateJobStatus(Guid jobId, string executionStatus, string? resultJson, string? errorMessage)
    {
        var job = await dbContext.AgentJobs.FindAsync(jobId);
        if (job != null)
        {
            job.ExecutionStatus = Enum.Parse<ExecutionStatus>(executionStatus, ignoreCase: true);
            job.ResultJson = resultJson;
            job.ErrorMessage = errorMessage;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
        }
    }
}
```

```csharp
// src/AgentMesh.Worker/Activities/ExecutionActivities.cs
using AgentMesh.Core.Activities;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class ExecutionActivities(ILogger<ExecutionActivities> logger) : IExecutionActivities
{
    [Activity]
    public Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson)
    {
        logger.LogInformation("Executing subsystem {Service}:{Verb} with payload: {Payload}", 
            targetService, actionVerb, payloadJson);

        // Simulation/MVP execution
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            service = targetService,
            verb = actionVerb,
            executedAt = DateTimeOffset.UtcNow,
            status = "success"
        });

        return Task.FromResult(result);
    }
}
```

```csharp
// src/AgentMesh.Worker/Program.cs
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Worker.Activities;
using Temporalio.Client;
using Temporalio.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb");

var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";

builder.Services.AddScoped<DatabaseActivities>();
builder.Services.AddScoped<ExecutionActivities>();

builder.Services.AddHostedTemporalWorker(
    clientTargetHost: temporalHost,
    clientNamespace: "default",
    taskQueue: "agent-task-queue")
    .AddWorkflow<AgentTaskWorkflow>()
    .AddAllActivities<DatabaseActivities>()
    .AddAllActivities<ExecutionActivities>();

var host = builder.Build();
host.Run();
```

---

## 5. Verification & Acceptance Criteria

### Test 1: Seed Prerequisite Records
```sql
INSERT INTO agents (id, name, runtime_type, trust_tier, persona_prompt)
VALUES ('gemini-spark', 'Gemini Spark', 'McpCloud', 2, 'Lead cloud agent')
ON CONFLICT (id) DO NOTHING;

INSERT INTO agent_sessions (id, title, status)
VALUES ('a0000000-0000-0000-0000-000000000001', 'Phase 2 Test Session', 'active')
ON CONFLICT (id) DO NOTHING;
```

### Test 2: Trigger Intent via MCP Tool Protocol
Send an MCP JSON-RPC `tools/call` request to `http://localhost:5000/mcp`:
```bash
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "stage_agent_intent",
      "arguments": {
        "targetService": "platform_ops",
        "actionVerb": "stage_journal_entry",
        "payloadJson": "{\"title\":\"Phase 2 Test\",\"summary\":\"Temporal and MCP verified\"}",
        "sessionId": "a0000000-0000-0000-0000-000000000001"
      }
    }
  }'
```
* **Success Output**:
  ```json
  {"status":"staged","job_id":"<UUID>","workflow_id":"job-<UUID>","message":"Task successfully queued for background evaluation."}
  ```

### Test 3: Verify Temporal Execution & Database State
1. Check Temporal Web UI (`http://localhost:8233`): Workflow `job-<UUID>` status is `Completed`.
2. Inspect PostgreSQL database:
   ```sql
   SELECT id, target_service, action_verb, approval_status, execution_status, result 
   FROM agent_jobs 
   WHERE session_id = 'a0000000-0000-0000-0000-000000000001';
   ```
   * **Expect**: `execution_status = 'Completed'`, `approval_status = 'AutoApproved'`.

---

## 6. Agent Execution Prompt & Checklist

```markdown
### AI Developer Task: Phase 2 Execution
Please implement Phase 2 of AgentMesh according to docs/phases/phase-2-core-mcp-staging-and-temporal-engine.md.
Follow these steps:
- [ ] Add ModelContextProtocol and Temporalio packages to Api, Worker, and Core.
- [ ] Define AgentTaskWorkflowInput and activity interfaces in AgentMesh.Core.
- [ ] Implement AgentTaskWorkflow in AgentMesh.Core.
- [ ] Implement StagingTools in AgentMesh.Api and configure app.MapMcp("/mcp").
- [ ] Implement DatabaseActivities and ExecutionActivities in AgentMesh.Worker.
- [ ] Configure HostedTemporalWorker on 'agent-task-queue'.
- [ ] Verify execution via curl and database query.
```
