# AgentMesh: Heterogeneous Agent Control Plane & Communication Bus

## 1. Executive Summary & Vision

**AgentMesh** is a centralized management, identity, and durable execution fabric for heterogeneous AI agents. It connects external cloud agents (Gemini Spark), local and terminal-based coding harnesses (`agy` / Antigravity), self-hosted LLMs (Qwen/Gemma via Ollama/vLLM), and IDE assistants (GitHub Copilot).

### Core Problems Solved

1. **Aggressive Cloud HITL Elimination**: Converts direct, mutation-triggering agent tool calls into asynchronous intent staging via a dedicated Model Context Protocol (MCP) server. Cloud agents perform zero-friction reasoning and staging without triggering host confirmation modals.
2. **Protocol & Lifecycle Normalization**: Decouples ephemeral, serverless agents (Spark, CLI runs) from long-lived state by managing identity, working memory, and session threads centrally.
3. **Durable Multi-Agent Orchestration**: Uses Temporal to handle hierarchical approvals ("Intern" agent $\to$ "Manager" agent $\to$ Human), retries, timeouts, and asynchronous execution across days without maintaining open connections.
4. **Local Developer Velocity vs. Cloud Scalability**: Local orchestration is driven by .NET 10 Aspire (`Aspire.AppHost`), while cluster deployments use clean, declarative Helm charts against existing Kubernetes infrastructure (PostgreSQL, Redis, and Temporal).

---

## 2. Technology Stack & Decision Matrix

| Layer | Technology | Justification |
| :--- | :--- | :--- |
| **Local Orchestrator** | .NET 10 Aspire (`Aspire.AppHost`) | Single-command local dev environment with OpenTelemetry dashboards, container lifecycle management, and service discovery. |
| **Backend & MCP** | ASP.NET Core 10 (`ModelContextProtocol.AspNetCore`) | Native Streamable HTTP and SSE transport for MCP tools, unified with REST API endpoints and SignalR streaming hubs. |
| **Durable Execution** | Temporal (`Temporalio` .NET SDK) | Replaces message brokers and custom sagas. Provides crash-proof workflows, durable timers, and native signal-based approvals. |
| **Canonical Store** | PostgreSQL 16+ (K8s) via EF Core 10 | Manages agent identities, session ledgers, structured job payloads (JSONB), and audit trails. |
| **Ephemeral Cache & Pub/Sub** | Redis (K8s) | Handles heartbeats, distributed lock leases, and high-frequency UI notification broadcasting. |
| **Frontend Cockpit** | React 19 + Vite + Tailwind CSS + shadcn/ui | Real-time session monitoring, agent inventory management, and human approval cockpit. |
| **Deployment Engine** | Kubernetes via Helm | Targets existing cluster services cleanly without proprietary cloud provider tooling. |

---

## 3. High-Level System Architecture

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        HETEROGENEOUS AGENTS                            │
│  [Gemini Spark]       [agy / Antigravity]    [Copilot]   [Local Qwen]  │
│  (Cloud MCP Client)   (CLI / Stdout Hook)    (Webhooks)  (Ollama/vLLM) │
└───────┬───────────────────────┬──────────────────┬─────────────┬───────┘
        │ HTTP / SSE            │ REST / gRPC      │ Webhooks    │ REST
        ▼                       ▼                  ▼             ▼
┌────────────────────────────────────────────────────────────────────────┐
│                 AGENTMESH CONTROL PLANE API (`/mcp`, `/api`)           │
│                 (ASP.NET Core 10 / Aspire Service Defaults)            │
│                                                                        │
│   ├── MCP Tool Registry (stage_agent_intent, inspect_job_status)       │
│   ├── Agent Session & Memory Hub                                       │
│   ├── Unified Approval API (`POST /api/jobs/{id}/approval`)            │
│   ├── SignalR Real-Time Hub (`/hubs/agent-mesh`)                       │
│   └── Temporal Workflow Client Launcher                                │
└──────────────┬──────────────────────────────────────────┬──────────────┘
               │                                          │
               ▼ (Workflows & Signals)                    ▼ (State & Audits)
┌──────────────────────────────┐        ┌────────────────────────────────┐
│      TEMPORAL CLUSTER        │        │      POSTGRESQL & REDIS        │
│  (Existing K8s Service)      │        │      (Existing K8s Services)   │
└──────────────┬───────────────┘        └────────────────────────────────┘
               │
               ▼ (Durable Activities)
┌────────────────────────────────────────────────────────────────────────┐
│                   AUTONOMOUS WORKER FLEET (.NET 10)                    │
│                                                                        │
│  ├── Discord Bot / Approval Activity (Interactive reaction buttons)    │
│  ├── Subsystem Executors (Google Docs API, GitHub API, Shell Runner)   │
│  └── Ephemeral K8s Pod Dispatcher (Batch compute / isolated sandboxes) │
└────────────────────────────────────────────────────────────────────────┘
```

### Architecture Sequence Diagram

```mermaid
sequenceDiagram
    autonumber
    actor Spark as Gemini Spark (Cloud MCP)
    participant Api as AgentMesh API (/mcp)
    participant Temporal as Temporal Cluster
    participant Worker as AgentMesh Worker Fleet
    participant DB as PostgreSQL
    actor Reviewer as Human (Discord / Cockpit)

    Spark->>Api: call_tool: stage_agent_intent(service, verb, payload, sessionId)
    Api->>Temporal: StartWorkflowAsync(AgentTaskWorkflow, workflowId)
    Api-->>Spark: { status: "staged", job_id: "...", workflow_id: "..." }
    Note over Spark: Cloud HITL bypassed; zero interactive prompt modal!
    Temporal->>Worker: Run Workflow Task
    Worker->>DB: DatabaseActivities.CreateStagedJobRecord(input)
    alt Requires Human Review
        Worker->>Reviewer: NotificationActivities.SendDiscordApprovalRequest(jobId)
        Worker->>Temporal: WaitConditionAsync(() => _approvalReceived, 7 days)
        Reviewer->>Api: POST /api/jobs/{id}/approval { approved: true }
        Api->>Temporal: SignalWorkflowAsync(SubmitApproval, true)
    end
    Worker->>Worker: ExecutionActivities.ExecuteSubsystemAction(...)
    Worker->>DB: DatabaseActivities.UpdateJobStatus(jobId, "completed", result)
```

---

## 4. Repository & Project Structure

```text
agent-mesh/
├── AgentMesh.sln
├── src/
│   ├── AgentMesh.AppHost/             # Aspire 10 Host (Local dev only)
│   ├── AgentMesh.ServiceDefaults/     # OpenTelemetry, Health checks, Resiliency
│   ├── AgentMesh.Core/                # Domain Entities, Workflow Contracts, DTOs
│   ├── AgentMesh.Infrastructure/      # EF Core DbContext, Migrations, Repositories
│   ├── AgentMesh.Api/                 # Web API, Auth Handler, MCP Endpoint (/mcp), SignalR
│   ├── AgentMesh.Worker/              # Temporal Worker, Workflows, Activities
│   └── AgentMesh.Dashboard/           # React 19 + Vite + Tailwind Frontend
├── deploy/
│   ├── docker/
│   │   ├── Dockerfile.api
│   │   ├── Dockerfile.worker
│   │   └── Dockerfile.dashboard
│   └── helm/
│       └── agent-mesh/
│           ├── Chart.yaml
│           ├── values.yaml
│           └── templates/
│               ├── ingress.yaml
│               ├── deployment-api.yaml
│               ├── deployment-worker.yaml
│               ├── deployment-dashboard.yaml
│               ├── service-api.yaml
│               ├── service-dashboard.yaml
│               └── configmap.yaml
└── docs/
    ├── architecture-plan.md           # Master plan document (this file)
    └── phases/
        ├── phase-1-scaffolding-and-data-layer.md
        ├── phase-2-core-mcp-staging-and-temporal-engine.md
        ├── phase-3-auth-ingress-and-spark-e2e-mvp.md
        ├── phase-4-human-approvals-and-escalation-engine.md
        └── phase-5-react-dashboard-and-observability.md
```

---

## 5. Domain Models & Database Schema

```sql
-- 1. Agent Inventory & Living Context
CREATE TABLE agents (
    id VARCHAR(64) PRIMARY KEY,                   -- e.g., 'gemini-spark', 'local-qwen-coder'
    name VARCHAR(128) NOT NULL,
    runtime_type VARCHAR(32) NOT NULL,            -- 'mcp_cloud', 'local_ollama', 'cli_agy'
    trust_tier INT NOT NULL DEFAULT 1,            -- 1: Intern, 2: Senior Manager, 3: Lead Human
    persona_prompt TEXT NOT NULL,                 -- Immutable system instructions & guardrails
    memory_summary TEXT NOT NULL DEFAULT '',      -- Living markdown summary of learnings
    api_key_hash VARCHAR(128),                    -- Optional hashed token for direct authentication
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- 2. Sessions (High-level goals/projects)
CREATE TABLE agent_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title VARCHAR(255) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active', -- 'active', 'paused', 'completed'
    metadata JSONB NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- 3. Inter-Agent Communication Ledger
CREATE TABLE session_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    sender_agent_id VARCHAR(64) NOT NULL REFERENCES agents(id),
    target_agent_id VARCHAR(64) REFERENCES agents(id), -- NULL = Public session broadcast
    content TEXT NOT NULL,
    structured_payload JSONB,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- 4. Staged Agent Jobs
CREATE TABLE agent_jobs (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    requested_by_agent_id VARCHAR(64) NOT NULL REFERENCES agents(id),
    target_service VARCHAR(64) NOT NULL,          -- 'platform_ops', 'google_docs', 'github'
    action_verb VARCHAR(64) NOT NULL,             -- 'stage_journal_entry', 'apply_diff'
    payload JSONB NOT NULL,
    approval_status VARCHAR(32) NOT NULL DEFAULT 'auto_approved', -- 'pending_human', 'approved', 'rejected'
    execution_status VARCHAR(32) NOT NULL DEFAULT 'staged',        -- 'staged', 'running', 'completed', 'failed'
    temporal_workflow_id VARCHAR(128),
    result JSONB,
    error_message TEXT,
    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_jobs_status ON agent_jobs(execution_status);
CREATE INDEX idx_jobs_session ON agent_jobs(session_id);
CREATE INDEX idx_session_messages_session ON session_messages(session_id);
```

---

## 6. MCP Server & Tool Definition Contract

The MCP bridge exposes passive, stage-focused actions to avoid client-side HITL triggers:

```csharp
// src/AgentMesh.Api/Tools/StagingTools.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using Temporalio.Client;
using AgentMesh.Core.Models;
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

## 7. Temporal Workflow & Approval Engine

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
        // 1. Persist initial 'staged' record to database
        await Workflow.ExecuteActivityAsync(
            (IDatabaseActivities act) => act.CreateStagedJobRecord(input),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        // 2. Determine Approval Boundary (Tier 3 / High Risk requires human approval)
        bool requiresHumanReview = input.TargetService != "platform_ops";

        if (requiresHumanReview)
        {
            await Workflow.ExecuteActivityAsync(
                (IDatabaseActivities act) => act.UpdateApprovalStatus(input.JobId, "pending_human"),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

            // Send interactive Discord notification with approval buttons
            await Workflow.ExecuteActivityAsync(
                (INotificationActivities act) => act.SendDiscordApprovalRequest(input),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) });

            // Durable wait up to 7 days for approval signal from Discord or Web Dashboard
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

        // 3. Execute the actual mutation via handler activity
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

## 8. Aspire Host Configuration (`Program.cs`)

```csharp
// src/AgentMesh.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// 1. Local Postgres with persistent volume
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("agentdb");

// 2. Local Redis with persistent volume
var redis = builder.AddRedis("redis")
    .WithDataVolume();

// 3. Local Temporal Dev Server Container (with Web UI on 8233)
var temporal = builder.AddContainer("temporal", "temporalio/admin-tools", "latest")
    .WithArgs("temporal", "server", "start-dev", "--ip", "0.0.0.0")
    .WithHttpEndpoint(targetPort: 7233, name: "grpc")
    .WithHttpEndpoint(targetPort: 8233, name: "ui");

// 4. API & MCP Server
var api = builder.AddProject<Projects.AgentMesh_Api>("api")
    .WithReference(postgres)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WithHttpEndpoint(targetPort: 5000, name: "http")
    .WaitFor(temporal);

// 5. Worker Service
var worker = builder.AddProject<Projects.AgentMesh_Worker>("worker")
    .WithReference(postgres)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WaitFor(temporal);

// 6. React Vite Frontend
builder.AddNpmApp("dashboard", "../AgentMesh.Dashboard")
    .WithReference(api)
    .WithHttpEndpoint(targetPort: 3000, env: "PORT");

builder.Build().Run();
```

---

## 9. Kubernetes & Helm Deployment Architecture

### Ingress Configuration (`deploy/helm/agent-mesh/templates/ingress.yaml`)

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ .Release.Name }}-ingress
  annotations:
    cert-manager.io/cluster-issuer: {{ .Values.ingress.clusterIssuer }}
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-buffering: "off"
    nginx.ingress.kubernetes.io/server-snippets: |
      location /mcp {
        proxy_set_header Connection '';
        chunked_transfer_encoding off;
      }
spec:
  ingressClassName: {{ .Values.ingress.className }}
  tls:
    - secretName: {{ .Values.ingress.tlsSecretName }}
      hosts:
        - {{ .Values.ingress.host }}
  rules:
    - host: {{ .Values.ingress.host }}
      http:
        paths:
          - path: /mcp
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-api
                port:
                  number: 5000
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-api
                port:
                  number: 5000
          - path: /hubs
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-api
                port:
                  number: 5000
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ .Release.Name }}-dashboard
                port:
                  number: 80
```

### Production `values.yaml` (`deploy/helm/agent-mesh/values.yaml`)

```yaml
ingress:
  className: "nginx"
  clusterIssuer: "letsencrypt-prod"
  host: "agents.yourdomain.local"
  tlsSecretName: "agents-tls-cert"

existingInfrastructure:
  postgresSecretName: "postgres-credentials"
  postgresHost: "postgres.infra.svc.cluster.local"
  postgresPort: 5432
  postgresDatabase: "agent_control_plane"
  redisHost: "redis-master.infra.svc.cluster.local:6379"
  temporalHost: "temporal-frontend.temporal.svc.cluster.local:7233"
  temporalNamespace: "default"

api:
  image: "registry.local/agent-mesh-api:latest"
  replicas: 2
  resources:
    limits: { cpu: "1000m", memory: "1Gi" }
    requests: { cpu: "100m", memory: "256Mi" }
  env:
    STATIC_API_KEY: "change-me-in-production-vault"

worker:
  image: "registry.local/agent-mesh-worker:latest"
  replicas: 2
  resources:
    limits: { cpu: "1000m", memory: "1Gi" }
    requests: { cpu: "100m", memory: "256Mi" }
  env:
    DISCORD_BOT_TOKEN: ""
    DISCORD_CHANNEL_ID: ""

dashboard:
  image: "registry.local/agent-mesh-dashboard:latest"
  replicas: 1
```

---

## 10. Step-by-Step Implementation Roadmap

```text
┌────────────────────────────────────────────────────────────────────────┐
│ PHASE 1: Solution Scaffolding & Data Layer                             │
│ • Create .NET 10 solution structure & projects                         │
│ • Configure Aspire AppHost with PostgreSQL, Redis, and Temporal        │
│ • Implement EF Core 10 domain entities & initial migration             │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ PHASE 2: Core MCP Staging Endpoint & Temporal Worker                   │
│ • Integrate ModelContextProtocol.AspNetCore into AgentMesh.Api         │
│ • Implement StagingTools (stage_agent_intent, inspect_job_status)     │
│ • Build AgentTaskWorkflow, database & execution activities in Worker   │
│ • Validate local end-to-end flow with Aspire + Temporal UI             │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ PHASE 3: Auth, Ingress & Spark E2E MVP                                 │
│ • Implement configurable API key authentication with X-Agent-Id mapping│
│ • Build Helm chart with SSE long-timeout Ingress annotations           │
│ • Deploy & execute live Gemini Spark validation test                   │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ PHASE 4: Human Approvals & Escalation Engine                           │
│ • Implement Discord approval notification activity with action buttons │
│ • Build unified approval API endpoint (POST /api/jobs/{id}/approval)   │
│ • Setup SignalR hub for real-time state and approval streaming         │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│ PHASE 5: React 19 Cockpit & Observability Dashboard                    │
│ • Scaffold React 19 + Vite + Tailwind + shadcn/ui frontend             │
│ • Real-time Session, Agent Inventory, and Approval Cockpit             │
│ • Integrate OpenTelemetry metrics, traces, and Temporal health checks  │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 11. MVP Verification & Hello-World Test Specification

### Test Objective
Verify that **Gemini Spark** successfully connects to the custom MCP server, stages an operational intent without triggering an interactive host HITL confirmation modal, initiates a Temporal workflow, and writes the completed job record into PostgreSQL.

### Execution Procedure

1. **Seed Agent Inventory & Session Records**:
   ```sql
   INSERT INTO agents (id, name, runtime_type, trust_tier, persona_prompt)
   VALUES ('gemini-spark', 'Gemini Spark', 'mcp_cloud', 2, 'Lead cloud agent');

   INSERT INTO agent_sessions (id, title, status)
   VALUES ('a0000000-0000-0000-0000-000000000001', 'MVP Validation Session', 'active');
   ```

2. **Configure MCP Server Connection in Gemini Spark**:
   * **Endpoint**: `https://agents.yourdomain.local/mcp`
   * **Auth Header**: `Bearer <STATIC_API_KEY>` or `X-Agent-Id: gemini-spark`

3. **Prompt Gemini Spark**:
   > "Record an operational journal update for our MVP milestone with title 'Architecture Verified' and note 'Temporal workflow execution confirmed' under session a0000000-0000-0000-0000-000000000001."

4. **Pass Criteria**:
   * Gemini Spark executes the tool call with **zero UI confirmation dialogs**.
   * A new row appears in `agent_jobs` with `execution_status = 'completed'` and `approval_status = 'auto_approved'`.
   * Temporal Web UI displays a successful run for workflow `job-<UUID>` with green status.
