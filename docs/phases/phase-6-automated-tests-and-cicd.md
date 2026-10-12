# Phase 6: Automated Test Suite & CI/CD Pipeline

## 1. Phase Objective & Scope

The objective of Phase 6 is to establish complete automated test coverage across the control plane and implement a production-grade CI/CD pipeline.

By the end of this phase:
1. `tests/AgentMesh.Tests`: Comprehensive xUnit test suite covering authentication handlers, REST controllers, SignalR broadcasting, MCP tools, and Temporal worker activities.
2. `src/AgentMesh.Dashboard`: Vitest + React Testing Library component tests covering the Human Approval Cockpit and Agent Inventory cards.
3. `.github/workflows/ci.yml`: Multi-stage GitHub Actions CI/CD matrix executing .NET 10 compilation & tests, Node 22 React dashboard verification, Helm chart linting, and Docker container build dry-runs.
4. All test runs achieve 100% pass rates with zero warnings or errors.

---

## 2. Test Architecture & Coverage Matrix

```text
tests/AgentMesh.Tests (xUnit .NET 10)
  ├── Auth/
  │     └── ApiKeyAuthenticationHandlerTests.cs   (Bearer tokens, X-Api-Key, X-Agent-Id mapping, 401 rejections)
  ├── Controllers/
  │     ├── AgentsControllerTests.cs              (Agent directory & trust tier querying)
  │     ├── ApprovalControllerTests.cs            (Temporal workflow signaling, SignalR broadcasting)
  │     ├── DiscordWebhookControllerTests.cs      (Ping Type 1, Button Type 3 approve/reject interactions)
  │     ├── JobsControllerTests.cs                (Filtering by PendingHuman, ID lookup, ordering)
  │     └── SessionsControllerTests.cs            (Session persistence, messaging, SignalR groups)
  ├── Tools/
  │     └── StagingToolsTests.cs                  (stage_agent_intent tool, Temporal workflow dispatch, SignalR)
  └── Activities/
        └── ActivitiesTests.cs                    (Subsystem execution, simulated Discord notifications)

src/AgentMesh.Dashboard (Vitest + JSDOM)
  ├── components/__tests__/
  │     ├── AgentInventoryCard.test.tsx           (Trust Tier 1/2/3 rendering, runtime labels, memory)
  │     └── ApprovalCockpit.test.tsx              (Empty state, staged jobs rendering, approve/reject buttons)
```

---

## 3. CI/CD Pipeline (`.github/workflows/ci.yml`)

The pipeline runs on every push to `main`/`develop` and on pull requests:
* **Job 1 (`backend`)**: Restores, builds (`Release`), and executes all .NET 10 test projects with test result `.trx` archiving.
* **Job 2 (`frontend`)**: Installs dependencies (`npm ci`), runs Vitest component tests, and builds Vite client bundle.
* **Job 3 (`helm-and-docker`)**: Runs `helm lint`, validates Helm template rendering, and builds Docker images for API, Worker, and Dashboard.

---

## 4. Verification & Results

### Backend Unit & Integration Tests:
```bash
dotnet test tests/AgentMesh.Tests
```
* **Result**: `Passed! - Failed: 0, Passed: 25, Skipped: 0, Total: 25`

### Frontend React Component Tests:
```bash
cd src/AgentMesh.Dashboard && npm test
```
* **Result**: `Test Files: 2 passed (2), Tests: 4 passed (4)`

### Helm Manifest Validation:
```bash
helm lint deploy/helm/agent-mesh
```
* **Result**: `1 chart(s) linted, 0 chart(s) failed`
