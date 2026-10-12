# Phase 5: React 19 Cockpit & Observability Dashboard

## 1. Phase Objective & Scope

The objective of Phase 5 is to build the human operator cockpit using React 19, Vite, Tailwind CSS, and shadcn/ui, connect it in real time via SignalR to the control plane, and integrate comprehensive OpenTelemetry observability.

By the end of this phase:
1. `src/AgentMesh.Dashboard` runs seamlessly in Aspire via `builder.AddNpmApp("dashboard")`.
2. Operators have a real-time UI cockpit for:
   * **Agent Inventory**: Viewing registered agents, runtime types, trust tiers, and living memory summaries.
   * **Session & Communication Ledger**: Streaming inter-agent messages and session timelines.
   * **Approval Cockpit**: Visualizing pending human approval requests and approving/rejecting with a single click.
   * **Job Execution Ledger**: Inspecting payload arguments, execution logs, and direct links to Temporal workflow traces.
3. OpenTelemetry spans, metrics, and logs flow from API, Worker, and backing containers directly into the Aspire Developer Dashboard.

---

## 2. Prerequisites & Environment

* Completion of **Phases 1 through 4**.
* Node.js v20+ / v22+ / v24+ and npm installed (`node --version`).
* `@microsoft/signalr` package for real-time WebSocket communication.

---

## 3. UI Cockpit Architecture

```text
React 19 + Vite Dashboard (Port 3000)
  ├── Components:
  │     ├── AgentInventoryCard.tsx      (Trust Tier badges, Living Memory)
  │     ├── ApprovalCockpit.tsx         (Pending Jobs, JSON viewer, Approve/Reject)
  │     ├── SessionLedger.tsx           (Real-time chat/message stream)
  │     └── JobHistoryTable.tsx         (Temporal workflow link, execution status)
  │
  ├── State & Realtime:
  │     ├── useSignalR.ts               (Connects to /hubs/agent-mesh)
  │     └── useMeshApi.ts               (REST client for /api/agents, /api/jobs)
  │
  ▼
ASP.NET Core API (Port 5000)
  ├── /api/jobs/{id}/approval
  ├── /api/sessions/{id}/messages
  └── /hubs/agent-mesh (SignalR Hub)
```

---

## 4. Step-by-Step Implementation Guide

### Step 1: Scaffold React 19 + Vite Project (`src/AgentMesh.Dashboard`)

```bash
# From repository root
cd src
npm create vite@latest AgentMesh.Dashboard -- --template react-ts
cd AgentMesh.Dashboard

# Install Tailwind CSS and Lucide icons
npm install -D tailwindcss @tailwindcss/vite
npm install lucide-react @microsoft/signalr clsx tailwind-merge
```

Configure `vite.config.ts`:
```typescript
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: parseInt(process.env.PORT || '3000'),
    proxy: {
      '/api': {
        target: process.env.services__api__http__0 || 'http://localhost:5000',
        changeOrigin: true
      },
      '/hubs': {
        target: process.env.services__api__http__0 || 'http://localhost:5000',
        ws: true
      }
    }
  }
});
```

---

### Step 2: Register Dashboard in Aspire (`src/AgentMesh.AppHost/Program.cs`)

```csharp
// In src/AgentMesh.AppHost/Program.cs
builder.AddNpmApp("dashboard", "../AgentMesh.Dashboard")
    .WithReference(api)
    .WithHttpEndpoint(targetPort: 3000, env: "PORT");
```

---

### Step 3: Implement SignalR Hook (`useSignalR.ts`)

```typescript
// src/AgentMesh.Dashboard/src/hooks/useSignalR.ts
import { useEffect, useState } from 'react';
import * as signalR from '@microsoft/signalr';

export function useSignalR(hubUrl: string = '/hubs/agent-mesh') {
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .build();

    conn.start()
      .then(() => {
        setConnected(true);
        setConnection(conn);
      })
      .catch((err) => console.error('SignalR Connection Error: ', err));

    return () => {
      conn.stop();
    };
  }, [hubUrl]);

  return { connection, connected };
}
```

---

### Step 4: Implement Human Approval Cockpit Component

```tsx
// src/AgentMesh.Dashboard/src/components/ApprovalCockpit.tsx
import React, { useState, useEffect } from 'react';
import { CheckCircle2, XCircle, AlertTriangle } from 'lucide-react';

interface StagedJob {
  id: string;
  sessionId: string;
  requestedByAgentId: string;
  targetService: string;
  actionVerb: string;
  payloadJson: string;
  approvalStatus: string;
  createdAt: string;
}

export const ApprovalCockpit: React.FC = () => {
  const [pendingJobs, setPendingJobs] = useState<StagedJob[]>([]);
  const [loading, setLoading] = useState(false);

  const fetchPendingJobs = async () => {
    try {
      const res = await fetch('/api/jobs?approvalStatus=PendingHuman');
      if (res.ok) {
        const data = await res.json();
        setPendingJobs(data);
      }
    } catch (e) {
      console.error('Failed to load pending jobs', e);
    }
  };

  useEffect(() => {
    fetchPendingJobs();
    const interval = setInterval(fetchPendingJobs, 5000);
    return () => clearInterval(interval);
  }, []);

  const handleDecision = async (jobId: string, approved: boolean) => {
    setLoading(true);
    try {
      await fetch(`/api/jobs/${jobId}/approval`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ approved, reason: approved ? 'Approved via Web Cockpit' : 'Rejected via Web Cockpit' })
      });
      setPendingJobs((prev) => prev.filter((j) => j.id !== jobId));
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 text-white shadow-xl">
      <div className="flex items-center gap-2 mb-4">
        <AlertTriangle className="text-amber-400 w-6 h-6" />
        <h2 className="text-xl font-bold">Human Approval Cockpit</h2>
        <span className="ml-auto bg-amber-500/20 text-amber-300 px-3 py-1 rounded-full text-xs font-semibold">
          {pendingJobs.length} Pending
        </span>
      </div>

      {pendingJobs.length === 0 ? (
        <p className="text-slate-400 text-sm">No actions currently require human review. All agents operating within bounds.</p>
      ) : (
        <div className="space-y-4">
          {pendingJobs.map((job) => (
            <div key={job.id} className="bg-slate-800/80 border border-slate-700 p-4 rounded-lg">
              <div className="flex justify-between items-start mb-2">
                <div>
                  <span className="font-mono text-sm text-sky-400 font-semibold">{job.targetService}:{job.actionVerb}</span>
                  <div className="text-xs text-slate-400">Agent: <span className="text-slate-200">{job.requestedByAgentId}</span></div>
                </div>
                <span className="text-xs font-mono text-slate-500">{job.id.slice(0, 8)}</span>
              </div>
              
              <pre className="bg-slate-950 p-3 rounded text-xs font-mono text-slate-300 overflow-x-auto mb-3">
                {JSON.stringify(JSON.parse(job.payloadJson || '{}'), null, 2)}
              </pre>

              <div className="flex justify-end gap-2">
                <button
                  disabled={loading}
                  onClick={() => handleDecision(job.id, false)}
                  className="flex items-center gap-1.5 px-3 py-1.5 bg-red-600/20 hover:bg-red-600/30 text-red-300 border border-red-500/30 rounded text-sm transition"
                >
                  <XCircle className="w-4 h-4" /> Reject
                </button>
                <button
                  disabled={loading}
                  onClick={() => handleDecision(job.id, true)}
                  className="flex items-center gap-1.5 px-3 py-1.5 bg-emerald-600/20 hover:bg-emerald-600/30 text-emerald-300 border border-emerald-500/30 rounded text-sm transition"
                >
                  <CheckCircle2 className="w-4 h-4" /> Approve
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
```

---

### Step 5: Implement Agent Inventory & Trust Tier Component

```tsx
// src/AgentMesh.Dashboard/src/components/AgentInventoryCard.tsx
import React from 'react';
import { ShieldCheck, Cpu } from 'lucide-react';

interface Agent {
  id: string;
  name: string;
  runtimeType: string;
  trustTier: number;
  memorySummary: string;
}

export const AgentInventoryCard: React.FC<{ agent: Agent }> = ({ agent }) => {
  const tierColor = agent.trustTier === 3 ? 'text-red-400 border-red-500/30 bg-red-500/10' :
                   agent.trustTier === 2 ? 'text-blue-400 border-blue-500/30 bg-blue-500/10' :
                   'text-amber-400 border-amber-500/30 bg-amber-500/10';

  const tierLabel = agent.trustTier === 3 ? 'Tier 3 (Lead Human)' :
                    agent.trustTier === 2 ? 'Tier 2 (Senior Manager)' :
                    'Tier 1 (Intern)';

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 text-white">
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2.5">
          <Cpu className="text-sky-400 w-5 h-5" />
          <h3 className="font-semibold text-base">{agent.name}</h3>
        </div>
        <span className={`text-xs px-2.5 py-0.5 rounded-full border ${tierColor} flex items-center gap-1`}>
          <ShieldCheck className="w-3.5 h-3.5" /> {tierLabel}
        </span>
      </div>
      <div className="text-xs text-slate-400 mb-2">Runtime: <span className="font-mono text-slate-300">{agent.runtimeType}</span></div>
      <div className="text-xs text-slate-300 bg-slate-950 p-2.5 rounded border border-slate-800">
        <div className="text-slate-500 text-[10px] uppercase font-bold tracking-wider mb-1">Living Memory Summary</div>
        <p className="line-clamp-2">{agent.memorySummary || 'No stored learnings yet.'}</p>
      </div>
    </div>
  );
};
```

---

## 5. Verification & Acceptance Criteria

### Test 1: Full Aspire Local Stack Launch
```bash
dotnet run --project src/AgentMesh.AppHost
```
* **Verify Aspire Dashboard**:
  * `dashboard` (npm app) shows status `Running` on port 3000.
  * `api` shows status `Running` on port 5000.
  * `worker` shows status `Running`.
  * OpenTelemetry distributed traces show incoming requests from the frontend to `/api/jobs` with zero errors.

### Test 2: Approval Flow in Web Cockpit
1. Stage an intent requiring human approval via `/mcp`.
2. Open `http://localhost:3000` in browser.
3. Card appears in **Human Approval Cockpit** within 5 seconds.
4. Click **Approve**.
5. Job card vanishes from pending list.
6. Open Temporal UI (`http://localhost:8233`); workflow status transitions to `Completed`.

---

## 6. Agent Execution Prompt & Checklist

```markdown
### AI Developer Task: Phase 5 Execution
Please execute Phase 5 of AgentMesh according to docs/phases/phase-5-react-dashboard-and-observability.md.
Follow these steps:
- [ ] Scaffold React 19 + TypeScript + Vite project in src/AgentMesh.Dashboard.
- [ ] Configure Tailwind CSS and Lucide icons.
- [ ] Wire up dashboard project in src/AgentMesh.AppHost Program.cs with AddNpmApp.
- [ ] Implement useSignalR hook for real-time WebSocket connection to /hubs/agent-mesh.
- [ ] Implement ApprovalCockpit component with Approve and Reject actions.
- [ ] Implement AgentInventoryCard component showing Trust Tiers.
- [ ] Verify full stack launch and OpenTelemetry metrics in Aspire Dashboard.
```
