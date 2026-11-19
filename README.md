# AgentMesh

> **Heterogeneous Agent Control Plane & Communication Bus**

AgentMesh is a centralized control plane, identity fabric, and durable execution engine for heterogeneous AI software engineering agents. It connects external cloud agents (OpenAI, Anthropic Claude, Gemini), local terminal coding harnesses (`agy` / Antigravity), self-hosted LLMs (Qwen/Llama via Ollama/vLLM), and IDE assistants (GitHub Copilot) into a coordinated development team.

---

## Key Highlights

* **Job-First Autonomous Lifecycle**: Models software development through structured, verifiable 4-stage engineering jobs: Stage 1 Discovery & Planning $\to$ Stage 2 Plan Alignment Gate $\to$ Stage 3 Task Execution & Observability $\to$ Stage 4 Verification & Two-Man Review.
* **KV Prefix Cache Preservation**: Optimizes LLM inference latency and token costs through a strict 6-layer volatility-ordered prompt stack that physically isolates long-lived invariants from per-turn volatile state.
* **Decoupled Identity vs. Runtime**: Separates **Personas** (semantic roles, system prompts, context budgets, GitHub credentials) from **Drivers** (technical execution harnesses, API endpoints, model discovery, sidecars).
* **Durable Orchestration via Temporal**: Crash-proof workflows, durable timers, signal-based approvals, and automatic handoffs spanning days without maintaining open connections or losing state.
* **Model Context Protocol (MCP) Suite**: Zero-friction `/mcp` endpoint providing autonomous agent tools (`manage_task`, `post_job_message`, `save_memory`, `open_pull_request`).
* **Unified Dual-Stack Developer Experience**: Rapid local development orchestrated by .NET 10 Aspire (`Aspire.AppHost`) with OpenTelemetry, and scalable cloud deployments using production Kubernetes Helm charts.

---

## Architecture Overview

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        HETEROGENEOUS AGENTS                            │
│  [Cloud Models]       [agy / Antigravity]    [Copilot]   [Local Qwen]  │
│  (OpenAI / Claude)    (CLI / Stdout Daemon)  (Webhooks)  (Ollama/vLLM) │
└───────┬───────────────────────┬──────────────────┬─────────────┬───────┘
        │ HTTP / SSE            │ REST / gRPC      │ Webhooks    │ REST
        ▼                       ▼                  ▼             ▼
┌────────────────────────────────────────────────────────────────────────┐
│                 AGENTMESH CONTROL PLANE API (`/mcp`, `/api`)           │
│                 (ASP.NET Core 10 / Aspire Service Defaults)            │
│                                                                        │
│   ├── MCP Tool Registry (manage_task, post_job_message, save_memory)   │
│   ├── Prompt Stack Builder (6-Layer Volatility-Ordered Assembly)       │
│   ├── Job Turn Dispatcher & Macro-Turn Controller                      │
│   ├── SignalR Real-Time Hubs (/hubs/agent-mesh, /hubs/terminal)        │
│   └── Temporal Workflow Client Launcher                                │
└──────────────┬──────────────────────────────────────────┬──────────────┘
               │                                          │
               ▼ (Workflows & Signals)                    ▼ (State & Audits)
┌──────────────────────────────┐        ┌────────────────────────────────┐
│      TEMPORAL CLUSTER        │        │      POSTGRESQL & REDIS        │
│  (Existing K8s Service)      │        │      (EF Core 10 / PubSub)     │
└──────────────┬───────────────┘        └────────────────┬───────────────┘
               │                                         │ (Large Payloads > 64KB)
               ▼ (Durable Activities)                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                   AUTONOMOUS WORKER FLEET (.NET 10)                    │
│                                                                        │
│  ├── JobExecutionWorkflow (4-stage autonomous lifecycle gating)        │
│  ├── TaskExecutionWorkflow (macro-turn dispatch, retries, handoffs)    │
│  ├── SeaweedFS S3 Storage Bridge (artifact & execution offload)        │
│  └── Runner Container Managers (Docker local / Kubernetes Pods)        │
└────────────────────────────────────────────────────────────────────────┘
```

---

## System Architecture & Specifications

| Living Specification | Scope & Focus |
| :--- | :--- |
| 📖 [**Master Architecture Plan**](docs/architecture-plan.md) | Canonical system architecture, data models, entity relationships, and technology decision matrix. |
| 📋 [**Job Lifecycle & Workflows**](docs/specs/job-lifecycle-and-workflows.md) | 4-stage lifecycle (Discovery $\to$ Alignment $\to$ Execution $\to$ Review), Temporal workflows, macro-turns, and handoffs. |
| 🧠 [**Prompt Stack Architecture**](docs/specs/prompt-stack-architecture.md) | 6-layer volatility-ordered prompt stack, KV prefix cache optimization, sliding windows, and payload caps. |
| 🤖 [**Personas, Drivers & Runners**](docs/specs/personas-drivers-and-runners.md) | Persona vs. Driver decoupling, runner pods, companion supervisors, sidecars, and container managers. |
| 🛠️ [**Platform MCP Tools & Contracts**](docs/specs/platform-mcp-tools-and-contracts.md) | Platform MCP tools (`manage_task`, `post_job_message`, `save_memory`, `open_pull_request`), schemas, and SignalR events. |
| 📦 [**Archived Development Phases**](docs/archive/phases/) | Historical documentation covering initial bootstrap phases 1 through 7. |

---

## Operator Attention & Desktop Notifications

The cockpit answers one question continuously: **what needs a human right now, across every project?**

### What counts as "waiting on you"

The rules live server-side (`src/AgentMesh.Api/Services/AttentionQueryService.cs`) so the desktop
banner and the bell inbox can never disagree about what is blocked. A job contributes **at most one**
item, and only `Active` / `Pending` jobs are considered — a job that finishes, is archived, or gets a
human reply resolves itself.

| Kind (`type`) | Raised when you are needed |
| :--- | :--- |
| `planApproval` | A plan is parked at the approval gate. |
| `discoveryQuestions` | An agent posted discovery/trade-off questions and is waiting for answers. |
| `interruptedTurn` | A turn was interrupted mid-flight and needs an operator to recover it. |
| `prReview` | A pull request is open and awaiting your review. |
| `escalation` | An agent posted a message with `requiresReply` addressed to you (3-R escalation). |

### Contract

| Surface | Shape |
| :--- | :--- |
| `GET /api/attention` (authorized) | `{ items: [{ id, type, title, detail, jobId, jobTitle, projectId, projectName, cardId, prUrl, prNumber, since }], count, generatedAt }` — oldest first, empty rather than 404 when nothing is blocked. `id` is `"{jobId}:{type}"`. |
| SignalR `AttentionNeeded` (broadcast to all clients) | One `AttentionItem`, emitted when a job starts waiting. |
| SignalR `AttentionResolved` (broadcast to all clients) | `{ jobId }`, emitted when that job stops waiting. |

Both events fire from the three state-transition write sites (job ledger message, progress heartbeat,
timeline card), so a wait is announced when it actually opens rather than on the next poll.

### In the cockpit

The header **bell** badges the number of waits you have not looked at, lists them with the job, the
project, the reason and how long each has been waiting, and click-throughs: a PR review opens the pull
request, everything else selects that project and job in the cockpit. Dismissing hides a wait from the
list without answering the agent — it returns if the job clears and parks again. The switch beside the
bell is the master mute for desktop alerts.

State is deliberately operator-local (there is no "ack" endpoint; the server's answer to "is this still
waiting?" is "ask me again"):

| `localStorage` key | Holds | Lifetime |
| :--- | :--- | :--- |
| `agentmesh_attention_seen_v1` | Which waits were already announced (banner + chime), shared by the push path and the poll. | 24h — a wait still open tomorrow nudges again. |
| `agentmesh_attention_read_v1` | Which rows you have opened in the bell (clears the badge). | 24h |
| `agentmesh_attention_dismissed_v1` | Rows you hid from the list. | 24h, or until the wait resolves |
| `agentmesh_notifications_muted_v1` | The master mute flag. | until toggled back |

### Secure context — read this if no banner ever appears

Browsers only expose the `Notification` API and an audible `AudioContext` in a **secure context**:
`https://…`, or `http://localhost` / `http://127.0.0.1`. Serving the cockpit on a bare LAN address or an
http tunnel — `http://192.168.0.20:5173`, `http://agents.internal` — means the OS banner is never raised
and the chime is silent, **with no error thrown**, because the API is simply not exposed to insecure
origins. The bell states this in its footer instead of failing silently:

> Desktop alerts need HTTPS or localhost

The in-app bell, badge and inbox keep working either way; only the operating-system alert is lost.

* **Deployment:** terminate TLS at the ingress. The Helm chart ships `ingress.className`, `ingress.host`,
  `ingress.clusterIssuer` (Let's Encrypt) and `ingress.tlsSecretName`, so a normal install is already a
  secure context.
* **Remote machine, no certificate:** port-forward and open it as localhost —
  `kubectl port-forward svc/<release>-dashboard 8080:80` then browse `http://localhost:8080`.
* **Local development:** `npm run dev` serves `http://localhost:5173`, which *is* a secure context, so
  notifications work in development. Opening the dev server through a machine name or LAN IP instead
  turns them off.
* **Permission is asked, never assumed:** the browser prompt fires only from the bell's
  *Enable desktop alerts* button, never on page load (browsers auto-deny unprompted requests, and an
  immediate prompt trains you to dismiss it unread). A prior "block" is a terminal state — the footer
  then tells you to allow alerts in site settings.
* **Sound needs a gesture too:** autoplay policy creates `AudioContext` suspended until the page sees
  input, so the cockpit registers a one-shot `pointerdown`/`keydown` listener. A tab that is never
  touched will banner silently, by design.

---

## Local Development Quickstart

### Prerequisites
* .NET SDK 10.0+
* Docker or Podman
* Node.js v20+

### Launching with Aspire
```bash
# Clone the repository
git clone https://github.com/your-org/agent-mesh.git
cd agent-mesh

# Run the Aspire orchestrator (starts Postgres, Redis, Temporal, API, Worker, and Dashboard)
dotnet run --project src/AgentMesh.AppHost
```

Open the Aspire dashboard link output in your console to monitor container statuses, distributed traces, and metrics.

---

## Repository Structure

```text
agent-mesh/
├── AgentMesh.sln
├── src/
│   ├── AgentMesh.AppHost/             # Aspire 10 Host (Local dev orchestrator)
│   ├── AgentMesh.ServiceDefaults/     # OpenTelemetry, Health checks, Resiliency
│   ├── AgentMesh.Core/                # Domain Entities, Workflow Contracts, DTOs
│   ├── AgentMesh.Infrastructure/      # EF Core DbContext, Migrations, Repositories
│   ├── AgentMesh.Api/                 # Web API, MCP Endpoint (/mcp), SignalR Hubs
│   ├── AgentMesh.Cli/                 # Cross-platform CLI runner daemon for external agents
│   ├── AgentMesh.Worker/              # Temporal Worker, Workflows, Activities, Prompt Stack
│   └── AgentMesh.Dashboard/           # React 19 + Vite + Tailwind Cockpit UI
├── deploy/
│   ├── docker/                        # Dockerfiles for API, Worker, Dashboard
│   └── helm/agent-mesh/               # Production Kubernetes Helm charts
├── tests/
│   └── AgentMesh.Tests/               # Comprehensive xUnit .NET 10 Test Suite
├── scripts/
│   └── build-cli.sh                   # Builds single-file CLI binaries for Linux, Windows & macOS
└── docs/
    ├── architecture-plan.md           # Master canonical architecture plan
    ├── specs/                         # Modular living system specifications
    └── archive/                       # Historical phase records
```

---

## AgentMesh CLI Cross-Platform Binaries

The AgentMesh CLI (`agentmesh`) connects external CLI coding harnesses (`agy`, `copilot`) to the AgentMesh control plane, handles real-time trajectory streaming, artifact syncing, execution mode controls, and command interruption.

### Building Binaries

Build self-contained, single-file executables for all platforms:

```bash
./scripts/build-cli.sh
```

Binaries are output to `dist/cli/`:

| Platform | Target Identifier | Output Binary |
| :--- | :--- | :--- |
| **Linux (x64)** | `linux-x64` | `dist/cli/linux-x64/agentmesh` |
| **Windows (x64)** | `win-x64` | `dist/cli/win-x64/agentmesh.exe` |
| **macOS (Apple Silicon M-Series)** | `osx-arm64` | `dist/cli/osx-arm64/agentmesh` |
| **macOS (Intel x64)** | `osx-x64` | `dist/cli/osx-x64/agentmesh` |
