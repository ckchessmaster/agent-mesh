import { useState, useEffect } from 'react';
import { Shield, Activity, Users, Layers, AlertCircle, RefreshCw, Cpu } from 'lucide-react';
import { useSignalR } from './hooks/useSignalR';
import { ApprovalCockpit } from './components/ApprovalCockpit';
import { AgentInventoryCard } from './components/AgentInventoryCard';
import { SessionLedger } from './components/SessionLedger';
import { JobHistoryTable } from './components/JobHistoryTable';
import { api } from './services/api';
import type { Agent } from './types';

export default function App() {
  const { connection, connected } = useSignalR();
  const [agents, setAgents] = useState<Agent[]>([]);
  const [refreshKey, setRefreshKey] = useState(0);
  const [activeTab, setActiveTab] = useState<'overview' | 'approvals' | 'ledger' | 'agents'>('overview');

  const loadAgents = async () => {
    try {
      const data = await api.getAgents();
      setAgents(data);
    } catch (err) {
      console.error('Failed to load agents', err);
    }
  };

  useEffect(() => {
    loadAgents();
  }, []);

  const handleJobStatusChange = () => {
    setRefreshKey((prev) => prev + 1);
  };

  return (
    <div className="min-h-screen bg-slate-950 text-slate-100 flex flex-col font-sans">
      {/* Top Navigation Bar */}
      <header className="border-b border-slate-800 bg-slate-900/90 backdrop-blur sticky top-0 z-40">
        <div className="max-w-7xl mx-auto px-6 h-16 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className="p-2 bg-sky-500/10 border border-sky-500/30 rounded-xl text-sky-400">
              <Shield className="w-6 h-6" />
            </div>
            <div>
              <div className="flex items-center gap-2">
                <span className="font-extrabold text-lg tracking-tight bg-gradient-to-r from-sky-400 via-indigo-300 to-purple-400 bg-clip-text text-transparent">
                  AgentMesh
                </span>
                <span className="text-[10px] uppercase font-bold tracking-widest px-2 py-0.5 rounded-full bg-slate-800 text-slate-400 border border-slate-700">
                  Cockpit v1.0
                </span>
              </div>
              <p className="text-[11px] text-slate-400">Autonomous Agent Governance & HITL Control Plane</p>
            </div>
          </div>

          <div className="flex items-center gap-4">
            {/* Live Connection Status */}
            <div className="flex items-center gap-2 px-3 py-1.5 rounded-full bg-slate-950 border border-slate-800 text-xs">
              <span
                className={`w-2.5 h-2.5 rounded-full ${
                  connected ? 'bg-emerald-400 animate-pulse' : 'bg-red-500'
                }`}
              />
              <span className="text-slate-300 font-mono text-[11px]">
                {connected ? 'SignalR Live' : 'Reconnecting...'}
              </span>
            </div>

            <button
              onClick={() => {
                loadAgents();
                setRefreshKey((k) => k + 1);
              }}
              className="p-2 text-slate-400 hover:text-white rounded-lg hover:bg-slate-800 transition"
              title="Refresh Workspace"
            >
              <RefreshCw className="w-4 h-4" />
            </button>
          </div>
        </div>

        {/* Navigation Tabs */}
        <div className="max-w-7xl mx-auto px-6 flex gap-2 border-t border-slate-800/60">
          <button
            onClick={() => setActiveTab('overview')}
            className={`px-4 py-2.5 text-xs font-semibold border-b-2 transition flex items-center gap-1.5 ${
              activeTab === 'overview'
                ? 'border-sky-500 text-sky-400'
                : 'border-transparent text-slate-400 hover:text-slate-200'
            }`}
          >
            <Activity className="w-3.5 h-3.5" /> Cockpit Overview
          </button>
          <button
            onClick={() => setActiveTab('approvals')}
            className={`px-4 py-2.5 text-xs font-semibold border-b-2 transition flex items-center gap-1.5 ${
              activeTab === 'approvals'
                ? 'border-amber-500 text-amber-400'
                : 'border-transparent text-slate-400 hover:text-slate-200'
            }`}
          >
            <AlertCircle className="w-3.5 h-3.5" /> Human Approvals
          </button>
          <button
            onClick={() => setActiveTab('ledger')}
            className={`px-4 py-2.5 text-xs font-semibold border-b-2 transition flex items-center gap-1.5 ${
              activeTab === 'ledger'
                ? 'border-indigo-500 text-indigo-400'
                : 'border-transparent text-slate-400 hover:text-slate-200'
            }`}
          >
            <Layers className="w-3.5 h-3.5" /> Inter-Agent Ledger
          </button>
          <button
            onClick={() => setActiveTab('agents')}
            className={`px-4 py-2.5 text-xs font-semibold border-b-2 transition flex items-center gap-1.5 ${
              activeTab === 'agents'
                ? 'border-purple-500 text-purple-400'
                : 'border-transparent text-slate-400 hover:text-slate-200'
            }`}
          >
            <Users className="w-3.5 h-3.5" /> Agent Roster
          </button>
        </div>
      </header>

      {/* Main Content Area */}
      <main className="max-w-7xl mx-auto px-6 py-6 flex-1 w-full space-y-6">
        {activeTab === 'overview' && (
          <>
            {/* Top Row: Approval Cockpit (Primary HITL) */}
            <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
              <div className="lg:col-span-2">
                <ApprovalCockpit
                  connection={connection}
                  onJobStatusChange={handleJobStatusChange}
                />
              </div>

              {/* System Stats & Status */}
              <div className="space-y-4">
                <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-lg">
                  <h3 className="text-sm font-bold text-slate-300 uppercase tracking-wider mb-3">
                    Active Governance
                  </h3>
                  <div className="grid grid-cols-2 gap-3 text-center">
                    <div className="bg-slate-950 p-3 rounded-lg border border-slate-800">
                      <div className="text-2xl font-black text-sky-400">{agents.length}</div>
                      <div className="text-[11px] text-slate-400 mt-1">Autonomous Agents</div>
                    </div>
                    <div className="bg-slate-950 p-3 rounded-lg border border-slate-800">
                      <div className="text-2xl font-black text-emerald-400">7 Days</div>
                      <div className="text-[11px] text-slate-400 mt-1">HITL Workflow SLA</div>
                    </div>
                  </div>
                </div>

                <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 shadow-lg">
                  <div className="flex items-center gap-2 mb-2 text-slate-300 font-semibold text-xs uppercase tracking-wider">
                    <Cpu className="w-4 h-4 text-sky-400" /> Control Plane Endpoints
                  </div>
                  <div className="text-xs font-mono text-slate-400 space-y-1.5">
                    <div className="flex justify-between">
                      <span className="text-slate-500">MCP Transport:</span>
                      <span className="text-sky-300">/mcp (HTTP SSE)</span>
                    </div>
                    <div className="flex justify-between">
                      <span className="text-slate-500">SignalR Hub:</span>
                      <span className="text-sky-300">/hubs/agent-mesh</span>
                    </div>
                    <div className="flex justify-between">
                      <span className="text-slate-500">Temporal UI:</span>
                      <a
                        href="http://localhost:8233"
                        target="_blank"
                        rel="noreferrer"
                        className="text-indigo-400 hover:underline"
                      >
                        :8233
                      </a>
                    </div>
                  </div>
                </div>
              </div>
            </div>

            {/* Middle Row: Session Ledger & Agents Preview */}
            <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
              <div className="lg:col-span-2">
                <SessionLedger connection={connection} agents={agents} />
              </div>

              <div className="space-y-4">
                <h3 className="text-base font-bold text-white flex items-center gap-2">
                  <Users className="w-4 h-4 text-sky-400" /> Registered Agent Roster
                </h3>
                <div className="space-y-4 max-h-[520px] overflow-y-auto pr-1">
                  {agents.length === 0 ? (
                    <div className="text-slate-500 text-xs py-8 text-center bg-slate-900 rounded-xl border border-slate-800">
                      No agents registered.
                    </div>
                  ) : (
                    agents.map((agent) => (
                      <AgentInventoryCard key={agent.id} agent={agent} />
                    ))
                  )}
                </div>
              </div>
            </div>

            {/* Bottom Row: Full Job Ledger */}
            <JobHistoryTable connection={connection} refreshKey={refreshKey} />
          </>
        )}

        {activeTab === 'approvals' && (
          <ApprovalCockpit
            connection={connection}
            onJobStatusChange={handleJobStatusChange}
          />
        )}

        {activeTab === 'ledger' && (
          <SessionLedger connection={connection} agents={agents} />
        )}

        {activeTab === 'agents' && (
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            {agents.map((agent) => (
              <AgentInventoryCard key={agent.id} agent={agent} />
            ))}
          </div>
        )}
      </main>

      {/* Footer */}
      <footer className="border-t border-slate-800 py-4 text-center text-xs text-slate-500">
        AgentMesh Control Plane &copy; 2026. Built with .NET 10, React 19, Temporal, and OpenTelemetry.
      </footer>
    </div>
  );
}
