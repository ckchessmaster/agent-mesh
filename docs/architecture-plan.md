# AgentMesh: Heterogeneous Agent Control Plane & Communication Bus

## 1. Executive Summary & Vision

**AgentMesh** is an enterprise-grade control plane, identity fabric, and durable execution engine for heterogeneous AI software engineering agents. It connects external cloud agents (OpenAI, Anthropic Claude, Gemini), local terminal coding harnesses (`agy` / Antigravity), self-hosted LLMs (Qwen/Llama via Ollama/vLLM), and IDE assistants (GitHub Copilot) into a coordinated development team.

### Core Problems Solved

1. **Job-First Autonomous Lifecycle**: Replaces unstructured, ephemeral chat turns with structured, multi-stage engineering jobs featuring strict discovery phases, plan alignment gates, sequential checklist execution, and mandatory two-man review verification.
2. **KV Prefix Cache Preservation**: Eliminates cache thrashing and optimizes inference costs through a 6-layer volatility-ordered prompt stack that physically isolates long-lived invariants from per-turn volatile state.
3. **Decoupled Identity vs. Runtime**: Separates **Personas** (semantic roles, specialized system prompts, context budgets, GitHub credentials) from **Drivers** (technical runtime engines, API endpoints, model discovery, sidecar networks).
4. **Durable Multi-Agent Orchestration**: Powered by **Temporal**, workflows survive network cuts, pod restarts, rate limits, and days-long human review delays without holding open socket connections or risking corrupted state.
5. **Human-in-the-Loop (HITL) Observability**: Provides an interactive React 19 Cockpit backed by real-time SignalR event streaming, live interactive terminals, and multi-repo workspace tracking.

---

## 2. Technology Stack & Decision Matrix

| Layer | Technology | Justification |
| :--- | :--- | :--- |
| **Control Plane API** | ASP.NET Core 10 (`ModelContextProtocol.AspNetCore`) | Unified HTTP REST, Streamable HTTP / SSE Model Context Protocol (MCP) server at `/mcp`, and SignalR streaming hubs. |
| **Durable Execution** | Temporal (`Temporalio` .NET SDK) | Distributed workflow engine delivering crash-proof orchestration, durable timers, signal-driven human approvals, and activity retries. |
| **Canonical Store** | PostgreSQL 16+ via EF Core 10 | Stores relational project hierarchies, personas, drivers, job lifecycles, structured checklists (JSONB), and audit ledgers. |
| **Large Payload Offload** | SeaweedFS (S3-Compatible) | Transparently offloads execution artifacts, compilation traces, and payloads exceeding 64 KB from PostgreSQL and Temporal gRPC envelopes. |
| **Ephemeral Cache & Pub/Sub** | Redis 7+ | Handles low-latency distributed locks, heartbeat tracking, and cross-instance notification fan-out. |
| **Frontend Cockpit** | React 19 + Vite + Tailwind CSS + shadcn/ui | Real-time human approval inbox, living task checklists, Job Chat, driver configuration modal, and terminal streams. |
| **Local Orchestrator** | .NET 10 Aspire (`Aspire.AppHost`) | Single-command developer environment (`dotnet run --project src/AgentMesh.AppHost`) with OpenTelemetry telemetry. |
| **Cloud Deployment** | Kubernetes via Helm | Declarative Helm charts deploying scalable API, Worker, and Runner pods against cluster-native PostgreSQL, Redis, and Temporal. |

---

## 3. High-Level System Architecture

```mermaid
flowchart TD
    subgraph Clients["Heterogeneous Agent Drivers & Clients"]
        C1["Cloud Models<br/>(OpenAI, Anthropic, Gemini)"]
        C2["Local CLI Harnesses<br/>(agy / Antigravity, Copilot)"]
        C3["Local Models<br/>(Ollama / vLLM / Qwen)"]
    end

    subgraph ControlPlane["AgentMesh Control Plane (ASP.NET Core 10)"]
        MCP["MCP Tool Registry (/mcp)<br/>manage_task, post_job_message, save_memory, open_pull_request"]
        REST["REST API (/api/projects, /api/jobs, /api/agents, /api/personas)"]
        SIG["SignalR Real-Time Hub (/hubs/agent-mesh, /hubs/terminal)"]
        DISP["JobTurnDispatcher & PromptStackBuilder"]
    end

    subgraph Orchestration["Durable Workflow Fabric"]
        TEMP["Temporal Cluster"]
        W1["JobExecutionWorkflow (4-Stage Lifecycle Gate)"]
        W2["TaskExecutionWorkflow (Turn Session & Retry Loop)"]
    end

    subgraph StateLayer["Storage & Cache"]
        PG[("PostgreSQL 16+<br/>(EF Core 10 Schema)")]
        RD[("Redis Cache")]
        S3[("SeaweedFS S3<br/>(Artifacts > 64 KB)")]
    end

    subgraph Runners["Runner Fleet & Sandboxes"]
        RP1["Docker / Kubernetes Runner Pods"]
        RP2["Companion Supervisor & Tailscale Sidecar"]
        RP3["Workspace Root (/workspace/repos/{repo_name})"]
    end

    subgraph UI["Human Operators"]
        DASH["React 19 Cockpit<br/>(Approval Inbox, Checklist, Chat, Terminals)"]
    end

    Clients <-->|MCP / SSE / REST| ControlPlane
    ControlPlane <-->|Signal / Query| TEMP
    TEMP <-->|Durable Activities| W1 & W2
    ControlPlane <-->|CRUD / Migrations| PG
    ControlPlane <-->|Pub/Sub| RD
    ControlPlane <-->|Payload Offload| S3
    DISP -->|Macro-Turn Dispatch| Runners
    SIG <-->|WebSockets| DASH
    REST <-->|Approvals & Admin| DASH
```

---

## 4. Core Domain Entities & Relational Schema

AgentMesh models autonomous engineering work with a normalized relational schema:

```mermaid
erDiagram
    Project ||--o{ ProjectJob : contains
    Project ||--o{ ProjectPersona : assigns
    Persona ||--o{ ProjectPersona : assigned_to
    Agent ||--o{ Persona : powers_driver
    ProjectJob ||--o{ JobMessage : logs
    ProjectJob ||--o{ JobWorkspaceState : tracks
    ProjectJob ||--o{ TaskWorkingCheckpoint : records
    ProjectJob ||--o{ JobHandoff : coordinates
    Project ||--o{ AgentMemory : scopes
    Agent ||--o{ AgentMemory : owns

    Project {
        string Id PK
        string Name
        string GitRepositoryUrl
        string DefaultBranch
        string RepoPathsJson
        string DefaultLeadPersonaId
        PlanApprovalPolicy PlanApprovalPolicy
    }

    Persona {
        string Id PK
        string Name
        string Role
        string PersonaPrompt
        string DriverId FK
        int MaxConcurrentJobs
        int DefaultContextSize
        string EncryptedGitHubToken
    }

    Agent {
        string Id PK
        string Name
        RuntimeType RuntimeType
        TrustTier TrustTier
        string CapabilitiesJson
        string Status
    }

    ProjectJob {
        uuid Id PK
        string ProjectId FK
        string Title
        string Description
        ProjectJobStatus Status
        ProjectJobPriority Priority
        string AssignedPersonaId FK
        string TasksJson
        string GitBranchName
        string PullRequestUrl
    }

    JobMessage {
        uuid Id PK
        uuid JobId FK
        string SenderType
        string SenderId
        string Content
        string MentionsJson
        uuid TurnSessionId
        string StructuredPayloadJson
    }

    JobWorkspaceState {
        uuid Id PK
        uuid JobId FK
        string ActiveBranch
        string DirtyFilesJson
        int StashCount
        string HeadCommitSha
    }

    TaskWorkingCheckpoint {
        uuid Id PK
        uuid JobId FK
        string TaskId
        string TurnSessionId
        string Summary
        string OpenIssuesJson
    }

    AgentMemory {
        uuid Id PK
        string AgentId FK
        string ProjectId FK
        string Key
        string Content
        string Category
    }
```

---

## 5. The 4-Stage Autonomous Job Lifecycle

Engineering tasks follow an autonomous lifecycle enforced by `JobExecutionWorkflow`:

1. **Stage 1: Discovery & Planning**
   * Persona operates in read-only mode (`grep`, `find`, `read_file`, `git log`). File edits are prohibited.
   * Agent decomposes requirements into 3–6 verifiable tasks via `manage_task(action: "set_checklist")`.
   * Posts plan to Job Chat via `post_job_message(requires_reply: true)` and yields turn.
2. **Stage 2: Plan Alignment Gate**
   * Workflow pauses execution until an operator or assigned team lead approves the checklist.
   * Evaluated automatically if `PlanApprovalPolicy == Automatic` or durably held if `ManualReviewRequired`.
3. **Stage 3: Task Execution & Observability**
   * Tasks are executed sequentially with status updates via `manage_task(action: "update_task_status")`.
   * 1-sentence progress note heartbeats stream to the Cockpit.
   * **The 3-Strike Rule**: If an agent fails 3 consecutive edit-and-test attempts, it reverts dirty changes (`git checkout --`), halts, and escalates to the supervisor via `post_job_message`.
4. **Stage 4: Verification & Two-Man Review**
   * Verification test suite executes in full.
   * Agent calls `open_pull_request`.
   * **Anti-Self-Merge Rule**: Agents cannot approve or merge their own PRs. Turn is yielded to the assigned reviewer.

---

## 6. Prompt Stack Architecture: 6 Volatility-Ordered Layers

To maximize inference engine KV prefix caching, `PromptStackBuilder` composes turns into six strictly ordered layers:

* **Layer 1: Platform Invariants** (100% Static) – Core rules of engagement, 3-strike rule, sandbox constraints.
* **Layer 2: Master Persona Prompt** (Static per Persona) – Behavioral instructions, role definition.
* **Layer 3: Project Manifest** (Static per Project) – Repository paths, architecture conventions.
* **Layer 4: Team Topology & Escalations** (Semi-Dynamic) – Project roster from `project_personas` with self-escalation deadlock prevention.
* **Layer 5: Persistent Agent Memories** (Low Volatility) – Project-specific notes from `save_memory`, capped at 40 entries.
* **Layer 6: Turn-0 Task Envelope** (High Volatility) – Physically isolated as the User message, containing task acceptance criteria, active branch, and live git probe state.

---

## 7. Platform MCP Tools & Wire Contracts

Autonomous agents interact with the platform through `/mcp`:

* [`manage_task`](specs/platform-mcp-tools-and-contracts.md#31-manage_task): Manipulates the living task checklist and statuses (`set_checklist`, `update_task_status`, `get_checklist`).
* [`post_job_message`](specs/platform-mcp-tools-and-contracts.md#32-post_job_message): Posts to Job Chat, tags teammates with `@mentions`, surfaces 3-R escalations (Result, Reasoning, Risk), or pauses turn execution (`requires_reply: true`).
* [`save_memory`](specs/platform-mcp-tools-and-contracts.md#33-save_memory): Retains codebase quirks, environment fixes, and architectural learnings across sessions.
* [`open_pull_request`](specs/platform-mcp-tools-and-contracts.md#34-open_pull_request): Triggers the Stage 4 Two-Man Review gate.

---

## 8. Modular System Specifications Index

For detailed deep-dives into individual subsystems, refer to the living specifications:

| Living Specification | Scope |
| :--- | :--- |
| 📋 [**Job Lifecycle & Temporal Workflows**](specs/job-lifecycle-and-workflows.md) | 4-stage execution gates, Temporal workflows, macro-turn dispatching, and handoff reapers. |
| 🧠 [**Prompt Stack Architecture**](specs/prompt-stack-architecture.md) | 6-layer volatility ordering, prefix cache optimization, sliding windows, and payload caps. |
| 🤖 [**Personas, Drivers & Runners**](specs/personas-drivers-and-runners.md) | Decoupled personas vs drivers, container managers, companion supervisors, and sidecars. |
| 🛠️ [**Platform MCP Tools & Contracts**](specs/platform-mcp-tools-and-contracts.md) | Wire contracts, schemas, 3-R escalation formats, and SignalR live broadcasting. |
| 📦 [**Archived Development Phases**](archive/phases/) | Historical documentation covering initial bootstrap phases 1 through 7. |
