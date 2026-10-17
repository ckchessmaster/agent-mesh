import { useState, useEffect, useCallback } from 'react';
import {
  Shield,
  Database,
  Zap,
  Clock,
  HardDrive,
  Server,
  CheckCircle2,
  AlertTriangle,
  XCircle,
  RefreshCw,
  ExternalLink,
  Code2,
  Terminal,
  Activity,
  Layers,
  MessageSquare,
  LogOut,
  UserCheck
} from 'lucide-react';
import { api } from './services/api';
import type { SystemStatusResponse, ServiceHealth, Agent } from './types';
import { SessionLedger } from './components/SessionLedger';
import { useAuth } from './auth/AuthProvider';

interface ServiceCardConfig {
  key: string;
  name: string;
  category: 'core' | 'infra';
  icon: typeof Server;
  accent: string;
  defaultDescription: string;
  portInfo?: string;
  externalLink?: string;
}

const SERVICE_CONFIGS: ServiceCardConfig[] = [
  {
    key: 'api',
    name: 'ASP.NET Core 10 Web API',
    category: 'core',
    icon: Server,
    accent: 'sky',
    defaultDescription: 'Central control plane, health monitoring & MCP router',
    portInfo: ':5000'
  },
  {
    key: 'postgres',
    name: 'PostgreSQL Relational DB',
    category: 'infra',
    icon: Database,
    accent: 'blue',
    defaultDescription: 'Relational persistence store for agent state, sessions & jobs',
    portInfo: ':5432 (agentdb) / :5050 (pgAdmin)',
    externalLink: 'http://localhost:5050'
  },
  {
    key: 'redis',
    name: 'Redis Distributed Cache',
    category: 'infra',
    icon: Zap,
    accent: 'rose',
    defaultDescription: 'In-memory pub/sub, distributed caching & rate limiting',
    portInfo: ':6379'
  },
  {
    key: 'temporal',
    name: 'Temporal Dev Server',
    category: 'infra',
    icon: Clock,
    accent: 'purple',
    defaultDescription: 'Durable execution engine & human-in-the-loop signal orchestrator',
    portInfo: ':7233 (gRPC) / :8233 (UI)',
    externalLink: 'http://localhost:8233'
  },
  {
    key: 's3',
    name: 'SeaweedFS S3 Storage',
    category: 'infra',
    icon: HardDrive,
    accent: 'amber',
    defaultDescription: 'S3-compatible object storage for artifact offloading',
    portInfo: ':8333 (S3) / :9333 (Master)'
  }
];

export default function App() {
  const auth = useAuth();
  const userEmail = auth?.user?.profile?.email || auth?.user?.profile?.name || 'admin@agentmesh.local';
  const [activeTab, setActiveTab] = useState<'health' | 'sessions'>('health');
  const [agents, setAgents] = useState<Agent[]>([]);
  const [statusData, setStatusData] = useState<SystemStatusResponse | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState<boolean>(true);
  const [showRawJson, setShowRawJson] = useState<boolean>(false);
  const [copySuccess, setCopySuccess] = useState<boolean>(false);

  useEffect(() => {
    api.getAgents().then(setAgents).catch(() => {});
  }, []);

  const fetchStatus = useCallback(async () => {
    setIsLoading(true);
    try {
      const data = await api.getStatus();
      setStatusData(data);
      setLastUpdated(new Date());
      setErrorMessage(null);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Failed to connect to API gateway';
      setErrorMessage(msg);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchStatus();
    if (!autoRefresh) return;

    const interval = setInterval(() => {
      fetchStatus();
    }, 5000);

    return () => clearInterval(interval);
  }, [fetchStatus, autoRefresh]);

  const copyToClipboard = () => {
    if (!statusData) return;
    navigator.clipboard.writeText(JSON.stringify(statusData, null, 2));
    setCopySuccess(true);
    setTimeout(() => setCopySuccess(false), 2000);
  };

  const services = statusData?.services || {};
  const isAllHealthy = statusData?.status === 'Healthy';
  const healthyCount = Object.values(services).filter(
    (s: ServiceHealth | undefined) => s?.status === 'Healthy'
  ).length;
  const totalCount = SERVICE_CONFIGS.length;

  return (
    <div className="min-h-screen bg-slate-950 text-slate-100 flex flex-col font-sans selection:bg-sky-500/30 selection:text-sky-200">
      {/* Top Header */}
      <header className="border-b border-slate-800/80 bg-slate-900/90 backdrop-blur sticky top-0 z-40">
        <div className="max-w-7xl mx-auto px-6 h-16 flex items-center justify-between">
          <div className="flex items-center gap-6">
            <div className="flex items-center gap-3">
              <div className="p-2.5 bg-gradient-to-br from-sky-500/20 to-indigo-500/20 border border-sky-500/30 rounded-xl text-sky-400 shadow-sm shadow-sky-950">
                <Shield className="w-5 h-5" />
              </div>
              <div>
                <div className="flex items-center gap-2">
                  <span className="font-black text-lg tracking-tight bg-gradient-to-r from-sky-400 via-indigo-300 to-emerald-400 bg-clip-text text-transparent">
                    AgentMesh
                  </span>
                  <span className="text-[10px] uppercase font-bold tracking-widest px-2 py-0.5 rounded-full bg-slate-800/80 text-sky-300 border border-slate-700/80">
                    Aspire Skeleton v1.0
                  </span>
                </div>
                <p className="text-[11px] text-slate-400">
                  Core Infrastructure & Agent Control Plane
                </p>
              </div>
            </div>

            {/* Top Navigation Tabs */}
            <nav className="flex items-center gap-1 bg-slate-950/80 p-1 rounded-xl border border-slate-800/80">
              <button
                onClick={() => setActiveTab('health')}
                className={`px-3 py-1.5 text-xs font-semibold rounded-lg transition flex items-center gap-1.5 ${
                  activeTab === 'health'
                    ? 'bg-sky-600/30 text-sky-300 border border-sky-500/40 shadow-sm'
                    : 'text-slate-400 hover:text-slate-200'
                }`}
              >
                <Activity className="w-3.5 h-3.5" />
                System Health
              </button>

              <button
                onClick={() => setActiveTab('sessions')}
                className={`px-3 py-1.5 text-xs font-semibold rounded-lg transition flex items-center gap-1.5 ${
                  activeTab === 'sessions'
                    ? 'bg-sky-600/30 text-sky-300 border border-sky-500/40 shadow-sm'
                    : 'text-slate-400 hover:text-slate-200'
                }`}
              >
                <MessageSquare className="w-3.5 h-3.5" />
                Sessions
              </button>
            </nav>
          </div>

          <div className="flex items-center gap-3">
            {/* Auto-Refresh Toggle */}
            <button
              onClick={() => setAutoRefresh(!autoRefresh)}
              className={`px-2.5 py-1 text-xs rounded-lg border font-mono transition flex items-center gap-1.5 ${
                autoRefresh
                  ? 'bg-slate-900 border-sky-500/40 text-sky-300'
                  : 'bg-slate-900/50 border-slate-800 text-slate-500 hover:text-slate-300'
              }`}
              title={autoRefresh ? 'Auto-refresh every 5s' : 'Auto-refresh paused'}
            >
              <Activity className={`w-3.5 h-3.5 ${autoRefresh ? 'animate-pulse text-sky-400' : ''}`} />
              Auto (5s): {autoRefresh ? 'ON' : 'OFF'}
            </button>

            {/* Manual Refresh Button */}
            <button
              onClick={fetchStatus}
              disabled={isLoading}
              className="p-2 text-slate-400 hover:text-white rounded-lg hover:bg-slate-800/80 border border-slate-800 transition disabled:opacity-50"
              title="Refresh status now"
            >
              <RefreshCw className={`w-4 h-4 ${isLoading ? 'animate-spin text-sky-400' : ''}`} />
            </button>

            {/* User SSO Profile & Sign Out */}
            <div className="flex items-center gap-2 pl-3 border-l border-slate-800/80">
              <div className="flex items-center gap-1.5 text-xs bg-slate-900 border border-slate-800 text-slate-300 px-2.5 py-1 rounded-lg">
                <UserCheck className="w-3.5 h-3.5 text-emerald-400" />
                <span className="font-mono text-[11px]">{userEmail}</span>
              </div>
              <button
                onClick={() => auth?.signoutRedirect?.()}
                title="Sign Out"
                className="p-1.5 text-slate-400 hover:text-rose-400 hover:bg-slate-800/80 rounded-lg border border-slate-800 transition"
              >
                <LogOut className="w-3.5 h-3.5" />
              </button>
            </div>
          </div>
        </div>
      </header>

      {/* Main Body */}
      <main className="max-w-7xl mx-auto px-6 py-8 flex-1 w-full space-y-8">
        {/* Connection Error Banner */}
        {errorMessage && (
          <div className="bg-rose-950/40 border border-rose-500/40 rounded-xl p-4 flex items-start gap-3 text-rose-200">
            <XCircle className="w-5 h-5 text-rose-400 shrink-0 mt-0.5" />
            <div>
              <h4 className="font-semibold text-sm">API Gateway Unreachable</h4>
              <p className="text-xs text-rose-300/80 mt-1">{errorMessage}</p>
              <p className="text-[11px] text-rose-400/60 mt-1 font-mono">
                Ensure Aspire AppHost is running: <code className="bg-black/30 px-1 py-0.5 rounded">dotnet run --project src/AgentMesh.AppHost</code>
              </p>
            </div>
          </div>
        )}

        {activeTab === 'health' ? (
          <>
            {/* High-Level Overview Metrics */}
            <section className="grid grid-cols-1 md:grid-cols-4 gap-4">
          {/* Card 1: System Status */}
          <div className="bg-slate-900/80 border border-slate-800/80 rounded-xl p-5 shadow-sm">
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Mesh Status</span>
              {isAllHealthy ? (
                <CheckCircle2 className="w-4 h-4 text-emerald-400" />
              ) : (
                <AlertTriangle className="w-4 h-4 text-amber-400" />
              )}
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className={`text-2xl font-black ${isAllHealthy ? 'text-emerald-400' : 'text-amber-400'}`}>
                {statusData?.status || (isLoading ? 'Checking...' : 'Offline')}
              </span>
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              {isAllHealthy ? 'All services operational' : 'One or more services degraded'}
            </p>
          </div>

          {/* Card 2: Services Verified */}
          <div className="bg-slate-900/80 border border-slate-800/80 rounded-xl p-5 shadow-sm">
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Verified Services</span>
              <Layers className="w-4 h-4 text-sky-400" />
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className="text-2xl font-black text-sky-400">
                {healthyCount} <span className="text-sm font-normal text-slate-400">/ {totalCount}</span>
              </span>
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              Postgres, Redis, Temporal, S3, API
            </p>
          </div>

          {/* Card 3: Runtime Environment */}
          <div className="bg-slate-900/80 border border-slate-800/80 rounded-xl p-5 shadow-sm">
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Host Platform</span>
              <Terminal className="w-4 h-4 text-purple-400" />
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className="text-lg font-bold text-slate-200">
                .NET 10 Aspire
              </span>
            </div>
            <p className="text-[11px] text-slate-400 mt-1 font-mono">
              Rootless Podman Containers
            </p>
          </div>

          {/* Card 4: Last Heartbeat */}
          <div className="bg-slate-900/80 border border-slate-800/80 rounded-xl p-5 shadow-sm">
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Last Check</span>
              <Clock className="w-4 h-4 text-emerald-400" />
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className="text-sm font-mono text-slate-300">
                {lastUpdated ? lastUpdated.toLocaleTimeString() : 'Pending'}
              </span>
            </div>
            <p className="text-[11px] text-slate-400 mt-1">
              Active polling every 5s
            </p>
          </div>
        </section>

        {/* Detailed Service Health Cards */}
        <section className="space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="text-base font-bold text-white tracking-tight flex items-center gap-2">
              <Activity className="w-4 h-4 text-sky-400" />
              Infrastructure & Service Mesh Health
            </h2>
            <span className="text-xs text-slate-400">
              Validated via live ping & gRPC health probes
            </span>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-5">
            {SERVICE_CONFIGS.map((config) => {
              const svcHealth = services[config.key] as ServiceHealth | undefined;
              const isHealthy = svcHealth?.status === 'Healthy';
              const isDegraded = svcHealth && !isHealthy;
              const Icon = config.icon;

              return (
                <div
                  key={config.key}
                  className="bg-slate-900/70 border border-slate-800/90 hover:border-slate-700/80 rounded-xl p-5 transition flex flex-col justify-between shadow-sm relative overflow-hidden group"
                >
                  {/* Status Indicator Bar at top */}
                  <div
                    className={`absolute top-0 left-0 right-0 h-1 transition-colors ${
                      isHealthy
                        ? 'bg-emerald-500'
                        : isDegraded
                        ? 'bg-rose-500'
                        : 'bg-slate-700'
                    }`}
                  />

                  <div>
                    <div className="flex items-start justify-between gap-3">
                      <div className="flex items-center gap-3">
                        <div className="p-2 rounded-lg bg-slate-800/90 text-sky-400 border border-slate-700/50">
                          <Icon className="w-5 h-5" />
                        </div>
                        <div>
                          <h3 className="text-sm font-bold text-white group-hover:text-sky-300 transition-colors">
                            {config.name}
                          </h3>
                          {config.portInfo && (
                            <span className="text-[11px] font-mono text-slate-400">
                              {config.portInfo}
                            </span>
                          )}
                        </div>
                      </div>

                      {/* Status Badge */}
                      <span
                        className={`text-xs font-semibold px-2.5 py-0.5 rounded-full border flex items-center gap-1 shrink-0 ${
                          isHealthy
                            ? 'bg-emerald-950/60 border-emerald-500/40 text-emerald-400'
                            : isDegraded
                            ? 'bg-rose-950/60 border-rose-500/40 text-rose-400'
                            : 'bg-slate-800 border-slate-700 text-slate-400'
                        }`}
                      >
                        {isHealthy ? (
                          <>
                            <span className="w-1.5 h-1.5 rounded-full bg-emerald-400 animate-pulse" />
                            Healthy
                          </>
                        ) : isDegraded ? (
                          <>
                            <span className="w-1.5 h-1.5 rounded-full bg-rose-400" />
                            Unhealthy
                          </>
                        ) : (
                          'Probing...'
                        )}
                      </span>
                    </div>

                    <p className="text-xs text-slate-400 mt-3 leading-relaxed">
                      {svcHealth?.description || config.defaultDescription}
                    </p>
                  </div>

                  <div className="mt-4 pt-3 border-t border-slate-800/70 flex items-center justify-between text-xs">
                    <div className="font-mono text-[11px] text-slate-400">
                      {svcHealth?.latencyMs !== undefined ? (
                        <span>
                          Latency:{' '}
                          <span
                            className={
                              svcHealth.latencyMs < 50
                                ? 'text-emerald-400 font-bold'
                                : 'text-amber-400 font-bold'
                            }
                          >
                            {svcHealth.latencyMs}ms
                          </span>
                        </span>
                      ) : (
                        <span>Latency: --</span>
                      )}
                    </div>

                    {config.externalLink && (
                      <a
                        href={config.externalLink}
                        target="_blank"
                        rel="noreferrer"
                        className="text-indigo-400 hover:text-indigo-300 flex items-center gap-1 font-semibold text-[11px] transition"
                      >
                        Open UI <ExternalLink className="w-3 h-3" />
                      </a>
                    )}
                  </div>
                </div>
              );
            })}

            {/* Extra Card: Temporal Worker Host */}
            <div className="bg-slate-900/70 border border-slate-800/90 rounded-xl p-5 flex flex-col justify-between shadow-sm relative overflow-hidden">
              <div className="absolute top-0 left-0 right-0 h-1 bg-sky-500" />
              <div>
                <div className="flex items-start justify-between gap-3">
                  <div className="flex items-center gap-3">
                    <div className="p-2 rounded-lg bg-slate-800/90 text-sky-400 border border-slate-700/50">
                      <Terminal className="w-5 h-5" />
                    </div>
                    <div>
                      <h3 className="text-sm font-bold text-white">Temporal Worker Host</h3>
                      <span className="text-[11px] font-mono text-slate-400">
                        agent-task-queue
                      </span>
                    </div>
                  </div>
                  <span className="text-xs font-semibold px-2.5 py-0.5 rounded-full border bg-sky-950/60 border-sky-500/40 text-sky-400 flex items-center gap-1 shrink-0">
                    <span className="w-1.5 h-1.5 rounded-full bg-sky-400 animate-pulse" />
                    Ready
                  </span>
                </div>

                <p className="text-xs text-slate-400 mt-3 leading-relaxed">
                  Background worker polling Temporal task queue for deterministic workflows and activities.
                </p>
              </div>

              <div className="mt-4 pt-3 border-t border-slate-800/70 flex items-center justify-between text-xs">
                <span className="text-slate-400 text-[11px]">Workflows: AgentTaskWorkflow</span>
                <span className="text-emerald-400 font-mono text-[11px]">Registered</span>
              </div>
            </div>
          </div>
        </section>

        {/* Raw Diagnostics Inspector (Collapsible) */}
        <section className="bg-slate-900/60 border border-slate-800/80 rounded-xl p-5 shadow-sm">
          <div className="flex items-center justify-between">
            <button
              onClick={() => setShowRawJson(!showRawJson)}
              className="flex items-center gap-2 text-xs font-bold text-slate-300 hover:text-white transition"
            >
              <Code2 className="w-4 h-4 text-sky-400" />
              <span>Diagnostic Raw JSON Payload (/api/status)</span>
              <span className="text-[10px] text-slate-400 font-normal">
                {showRawJson ? '(Click to collapse)' : '(Click to expand)'}
              </span>
            </button>

            {showRawJson && (
              <button
                onClick={copyToClipboard}
                className="text-xs font-mono px-2.5 py-1 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 border border-slate-700 transition"
              >
                {copySuccess ? 'Copied!' : 'Copy JSON'}
              </button>
            )}
          </div>

          {showRawJson && (
            <div className="mt-4">
              <pre className="bg-slate-950/90 border border-slate-800/90 rounded-lg p-4 text-xs font-mono text-sky-300/90 overflow-x-auto max-h-96">
                {statusData ? JSON.stringify(statusData, null, 2) : 'No diagnostic data available.'}
              </pre>
            </div>
          )}
        </section>
      </>
    ) : (
      <section>
        <SessionLedger agents={agents} />
      </section>
    )}
      </main>

      {/* Footer */}
      <footer className="border-t border-slate-800/70 py-4 text-center text-xs text-slate-400 bg-slate-950">
        AgentMesh Control Plane &copy; 2026. Powered by .NET 10, Aspire, Temporal, and React 19.
      </footer>
    </div>
  );
}
