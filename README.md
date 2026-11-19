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
item, and only `Active` / `Pending` jobs are considered — a job that finishes or is archived resolves
itself. Everything else resolves by the record of the wait stopping being a wait: approving the plan card,
answering the discovery card, or commenting on the escalation card takes it out of `PendingInput`, so
there is no "resolved" flag to write and nothing to get out of sync.

Every kind is read from the two places a wait is recorded: the job's free-text `ProgressHeartbeat` and its
timeline cards (`Escalation3R` for a parked ask, `PrReview` for a review gate, `PlanApproval` /
`DiscoveryQa` for the gates, `TurnInterrupted` for the prose of an interrupt).

| Kind (`type`) | Raised when you are needed |
| :--- | :--- |
| `planApproval` | A plan is parked at the approval gate. |
| `discoveryQuestions` | An agent posted discovery/trade-off questions and is waiting for answers. |
| `interruptedTurn` | A turn was interrupted mid-flight and needs an operator to recover it. |
| `prReview` | A pull request is open and awaiting your review. |
| `escalation` | An agent parked its turn asking you to reply (`requiresReply` addressed to you). |

### Contract

| Surface | Shape |
| :--- | :--- |
| `GET /api/attention` (authorized) | `{ items: [{ id, type, title, detail, jobId, jobTitle, projectId, projectName, cardId, prUrl, prNumber, since }], count, generatedAt }` — oldest first, empty rather than 404 when nothing is blocked. `id` is `"{jobId}:{type}"`. |
| SignalR `AttentionNeeded` (broadcast to all clients) | One `AttentionItem`, emitted when a job starts waiting. |
| SignalR `AttentionResolved` (broadcast to all clients) | `{ jobId }`, emitted when that job stops waiting. |

Both events fire from the state-transition write sites that can open or close a wait — the timeline
card routes (a comment on a pending escalation closes it), the plan gate, the job lifecycle routes, and
`post_job_message` — so a wait is announced when it actually opens rather than on the next poll.

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

## Observability & Metrics

The .NET services publish OpenTelemetry metrics through `src/AgentMesh.ServiceDefaults`. The Aspire
dashboard receives them over OTLP (as above); a Prometheus scraper can pull the same `MeterProvider`
from a `/metrics` endpoint, which is opt-in through configuration and wired up for the API by the
Helm chart:

```bash
# Local run: served on the port the app already listens on.
Metrics__Prometheus__Enabled=true dotnet run --project src/AgentMesh.Api

# Serve it on a dedicated listener instead. The port must also be in the app's listen URLs -
# ASPNETCORE_URLS="http://+:5000;http://+:9464" - because binding it from code makes Kestrel
# discard the configured list and the application's own port stops listening.
```

In Kubernetes, `api.metrics` in `deploy/helm/agent-mesh/values.yaml` enables the endpoint on
container port 9464 and creates a PodMonitor when the cluster has the prometheus-operator CRDs.
Three details are deliberate:

* **The metrics port is not part of the `agent-mesh-api` Service.** The scrape endpoint cannot be
  authenticated without handing Prometheus a bearer token, so it stays reachable only from the pod
  network and can never be hit through the Ingress. The listener answers nothing but `/metrics`.
* **`AddPrometheusExporter` is additive to the OTLP exporter**, so the Aspire dashboard keeps
  working with metrics enabled.
* **The chart grants Prometheus pod-read access in this namespace** (`api.metrics.podMonitor.rbac`).
  Discovery is the part people forget: a PodMonitor that syncs happily and shows up in
  `prometheus.yaml` can still scrape nothing, because listing pods is a permission the operator's
  own cluster roles may not cover. The symptom is an empty target list plus
  `pods is forbidden: User "system:serviceaccount:monitoring:prometheus-k8s"` in the Prometheus
  log. Set `api.metrics.podMonitor.rbac.subjects` if your Prometheus runs under a different
  service account, or `rbac.create=false` if the namespace is already authorised.

### Triaging memory and restarts

The API's `OOMKilled` restarts (exit `137`) were invisible from the outside: container metrics show
total memory but cannot distinguish "the GC is holding committed heap it means to return" from
"something is leaking". These series answer that:

```promql
# Am I about to be OOM-killed? Committed GC heap against the container ceiling.
dotnet_gc_last_collection_memory_committed_size_bytes{namespace="agent-mesh"}
container_spec_memory_limit_bytes{namespace="agent-mesh",container="api"}

# Where the heap actually is - gen2 vs LOH vs POH is the whole diagnosis.
dotnet_gc_last_collection_heap_size_bytes{namespace="agent-mesh",container="api"}

# Allocation pressure and how much of the interval is spent collecting.
rate(dotnet_gc_heap_total_allocated_bytes_total{namespace="agent-mesh"}[5m])
rate(dotnet_gc_pause_time_seconds_total{namespace="agent-mesh"}[5m])

# The kernel's side of the story, from cAdvisor.
increase(container_oom_events_total{namespace="agent-mesh",container="api"}[1h])
```

The same exporter carries the operational context around the heap, all of it worth reading next to
the memory curve: `aspnetcore_memory_pool_pooled_bytes` and
`aspnetcore_memory_pool_allocated_bytes_total` (the buffer pools that actually account for an
allocation spike), `dotnet_process_memory_working_set_bytes` (the process's own view of what the
cgroup sees), `dotnet_thread_pool_queue_length_total` and `kestrel_active_connections` for back-pressure,
and `http_server_active_requests` for what was in flight when it happened.

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
