# AgentMesh

> **Heterogeneous Agent Control Plane & Communication Bus**

AgentMesh is a centralized management, identity, and durable execution fabric for heterogeneous AI agents. It connects external cloud agents (Gemini Spark), local terminal-based coding harnesses (`agy` / Antigravity), self-hosted LLMs (Qwen/Gemma via Ollama/vLLM), and IDE assistants (GitHub Copilot).

---

## Key Highlights

* **Cloud HITL Elimination**: Converts direct, mutation-triggering agent tool calls into asynchronous intent staging via a dedicated Model Context Protocol (MCP) server. Cloud agents perform zero-friction reasoning without triggering host confirmation modals.
* **Protocol & State Normalization**: Centrally manages agent identities, trust tiers, living working memories, and session communication ledgers.
* **Durable Orchestration via Temporal**: Crash-proof workflows, durable timers, and multi-channel human approvals (Discord & Web Cockpit) spanning up to 7 days without keeping open connections.
* **Unified Dual-Stack Experience**: Rapid local development orchestrated by .NET 10 Aspire (`Aspire.AppHost`) with OpenTelemetry, and scalable cloud deployments using production Kubernetes Helm charts.

---

## Architecture Overview

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
│   ├── Unified Approval API (POST /api/jobs/{id}/approval)              │
│   ├── SignalR Real-Time Hub (/hubs/agent-mesh)                         │
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

---

## Agentic Development Documentation

The project roadmap is structured into 5 autonomous agent development phases:

| Document | Focus & Scope |
| :--- | :--- |
| 📖 [**Master Architecture Plan**](docs/architecture-plan.md) | Complete system design, technology decision matrix, schemas, and end-to-end specifications. |
| 🛠️ [**Phase 1: Solution Scaffolding & Data Layer**](docs/phases/phase-1-scaffolding-and-data-layer.md) | .NET 10 solution topology, Aspire AppHost (Postgres, Redis, Temporal), EF Core 10 entities and migrations. |
| ⚡ [**Phase 2: Core MCP Staging & Temporal Worker**](docs/phases/phase-2-core-mcp-staging-and-temporal-engine.md) | `ModelContextProtocol.AspNetCore` endpoint (`/mcp`), `stage_agent_intent` tool, and Temporal worker pipeline. |
| 🛡️ [**Phase 3: Auth, Ingress & Spark E2E MVP**](docs/phases/phase-3-auth-ingress-and-spark-e2e-mvp.md) | API key auth handler, `X-Agent-Id` routing, Helm chart with SSE timeouts, and Gemini Spark live test. |
| 🔔 [**Phase 4: Human Approvals & Escalation Engine**](docs/phases/phase-4-human-approvals-and-escalation-engine.md) | Discord interactive buttons, unified approval API endpoint, and SignalR real-time event streaming. |
| 📊 [**Phase 5: React 19 Cockpit & Observability**](docs/phases/phase-5-react-dashboard-and-observability.md) | React 19 + Tailwind + shadcn/ui human cockpit, approval inbox, agent inventory visualizer, and OpenTelemetry. |

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
│   ├── AgentMesh.AppHost/             # Aspire 10 Host (Local dev only)
│   ├── AgentMesh.ServiceDefaults/     # OpenTelemetry, Health checks, Resiliency
│   ├── AgentMesh.Core/                # Domain Entities, Workflow Contracts, DTOs
│   ├── AgentMesh.Infrastructure/      # EF Core DbContext, Migrations, Repositories
│   ├── AgentMesh.Api/                 # Web API, Auth Handler, MCP Endpoint (/mcp), SignalR
│   ├── AgentMesh.Cli/                 # Cross-platform CLI runner for external agents (agy, copilot)
│   ├── AgentMesh.Worker/              # Temporal Worker, Workflows, Activities
│   └── AgentMesh.Dashboard/           # React 19 + Vite + Tailwind Frontend
├── deploy/
│   ├── docker/                        # Dockerfiles for API, Worker, Dashboard
│   └── helm/agent-mesh/               # Production Kubernetes Helm chart
├── scripts/
│   └── build-cli.sh                   # Builds single-file CLI binaries for Linux, Windows & macOS
└── docs/
    ├── architecture-plan.md           # Master system plan
    └── phases/                        # Step-by-step agentic development guides
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