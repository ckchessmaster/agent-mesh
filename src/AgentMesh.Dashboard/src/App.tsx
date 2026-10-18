import { useEffect, useState, useCallback, useRef } from 'react';
import {
  Bot,
  Brain,
  Terminal,
  Send,
  Plus,
  Trash2,
  RefreshCw,
  CheckCircle2,
  AlertTriangle,
  LogOut,
  User,
  Wrench,
  MessageSquare,
  ChevronRight,
  Activity,
  Cpu
} from 'lucide-react';
import { api } from './services/api';
import { useSignalR } from './hooks/useSignalR';
import { useAuth } from './auth/AuthProvider';
import type { Agent, AgentSession, SessionMessage, SystemStatusResponse } from './types';

export default function App() {
  const auth = useAuth();
  const userEmail = auth?.user?.profile?.email || auth?.user?.profile?.name || 'admin';

  const [agents, setAgents] = useState<Agent[]>([]);
  const [sessions, setSessions] = useState<AgentSession[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [events, setEvents] = useState<SessionMessage[]>([]);

  const [statusData, setStatusData] = useState<SystemStatusResponse | null>(null);
  const [apiError, setApiError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [isActionPending, setIsActionPending] = useState<boolean>(false);

  // Prompt & New Session state
  const [promptInput, setPromptInput] = useState('');
  const [isCreatingSession, setIsCreatingSession] = useState(false);
  const [newSessionTitle, setNewSessionTitle] = useState('');
  const [newSessionPrompt, setNewSessionPrompt] = useState('');

  // Quick Register Agent modal/drawer state
  const [showRegisterModal, setShowRegisterModal] = useState(false);
  const [newAgentId, setNewAgentId] = useState('');
  const [newAgentName, setNewAgentName] = useState('');
  const [newAgentRole, setNewAgentRole] = useState('Codebase Engineer');

  const timelineEndRef = useRef<HTMLDivElement | null>(null);
  const { connection, connected: signalRConnected } = useSignalR('/hubs/agent-mesh');

  const fetchAllData = useCallback(async () => {
    try {
      const [statusRes, agentsRes, sessionsRes] = await Promise.all([
        api.getStatus(),
        api.getAgents(),
        api.getSessions('all')
      ]);
      setStatusData(statusRes);
      setAgents(agentsRes);
      setSessions(sessionsRes);
      setApiError(null);

      // Auto-select first agent & session if none selected
      setSelectedAgentId((prevAgent) => {
        const nextAgent = prevAgent && agentsRes.some((a) => a.id === prevAgent)
          ? prevAgent
          : agentsRes[0]?.id || null;
        return nextAgent;
      });

      setSelectedSessionId((prevSession) => {
        if (prevSession && sessionsRes.some((s) => s.id === prevSession)) {
          return prevSession;
        }
        return sessionsRes[0]?.id || null;
      });
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Unable to reach AgentMesh API');
    } finally {
      setIsLoading(false);
    }
  }, []);

  const fetchSessionEvents = useCallback(async (sessionId: string) => {
    try {
      const msgs = await api.getSessionMessages(sessionId);
      setEvents(msgs);
    } catch (err) {
      console.error('Failed to load session events:', err);
    }
  }, []);

  useEffect(() => {
    fetchAllData();
  }, [fetchAllData]);

  useEffect(() => {
    if (selectedSessionId) {
      fetchSessionEvents(selectedSessionId);
      if (connection && signalRConnected) {
        connection.invoke('JoinSession', selectedSessionId).catch(() => {});
      }
    } else {
      setEvents([]);
    }
  }, [selectedSessionId, fetchSessionEvents, connection, signalRConnected]);

  // Real-time SignalR listeners
  useEffect(() => {
    if (!connection) return;

    const handleReceiveMessage = (msg: SessionMessage) => {
      if (msg.sessionId === selectedSessionId) {
        setEvents((prev) => {
          if (prev.some((e) => e.id === msg.id)) return prev;
          return [...prev, msg];
        });
      }
      fetchAllData();
    };

    const handleSessionCreated = (session: AgentSession) => {
      fetchAllData();
      setSelectedSessionId(session.id);
      if (session.creatorAgentId) {
        setSelectedAgentId(session.creatorAgentId);
      }
    };

    const handleSessionUpdated = () => {
      fetchAllData();
    };

    const handleMeshReset = () => {
      setAgents([]);
      setSessions([]);
      setEvents([]);
      setSelectedAgentId(null);
      setSelectedSessionId(null);
    };

    connection.on('ReceiveMessage', handleReceiveMessage);
    connection.on('SessionCreated', handleSessionCreated);
    connection.on('SessionUpdated', handleSessionUpdated);
    connection.on('AgentRegistered', handleSessionUpdated);
    connection.on('AgentUpdated', handleSessionUpdated);
    connection.on('MeshReset', handleMeshReset);

    return () => {
      connection.off('ReceiveMessage', handleReceiveMessage);
      connection.off('SessionCreated', handleSessionCreated);
      connection.off('SessionUpdated', handleSessionUpdated);
      connection.off('AgentRegistered', handleSessionUpdated);
      connection.off('AgentUpdated', handleSessionUpdated);
      connection.off('MeshReset', handleMeshReset);
    };
  }, [connection, selectedSessionId, fetchAllData, fetchSessionEvents]);

  // Auto-scroll timeline when new events arrive
  useEffect(() => {
    if (typeof timelineEndRef.current?.scrollIntoView === 'function') {
      timelineEndRef.current.scrollIntoView({ behavior: 'smooth' });
    }
  }, [events.length]);

  const handleResetMesh = async () => {
    setIsActionPending(true);
    try {
      await api.resetMesh();
      setAgents([]);
      setSessions([]);
      setEvents([]);
      setSelectedAgentId(null);
      setSelectedSessionId(null);
      setIsCreatingSession(false);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to reset mesh data');
    } finally {
      setIsActionPending(false);
    }
  };

  const handleCreateSession = async (e: React.FormEvent) => {
    e.preventDefault();
    const targetAgent = selectedAgentId || agents[0]?.id;
    if (!targetAgent || !newSessionTitle.trim()) return;

    setIsActionPending(true);
    try {
      const created = await api.createAgentSession(
        targetAgent,
        newSessionTitle.trim(),
        newSessionPrompt.trim() || undefined
      );
      setNewSessionTitle('');
      setNewSessionPrompt('');
      setIsCreatingSession(false);
      await fetchAllData();
      setSelectedSessionId(created.id);
      await fetchSessionEvents(created.id);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to create session');
    } finally {
      setIsActionPending(false);
    }
  };

  const handleSendPrompt = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedSessionId || !promptInput.trim()) return;

    const text = promptInput.trim();
    setPromptInput('');
    const currentSession = sessions.find((s) => s.id === selectedSessionId);

    try {
      const createdMsg = await api.postSessionMessage(
        selectedSessionId,
        text,
        currentSession?.creatorAgentId || selectedAgentId || 'agy-core',
        userEmail,
        'prompt'
      );
      setEvents((prev) => (prev.some((m) => m.id === createdMsg.id) ? prev : [...prev, createdMsg]));
      await fetchAllData();
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to send prompt');
    }
  };

  const handleRegisterAgent = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newAgentId.trim() || !newAgentName.trim()) return;

    setIsActionPending(true);
    try {
      const created = await api.upsertAgent({
        id: newAgentId.trim().toLowerCase(),
        name: newAgentName.trim(),
        role: newAgentRole.trim() || 'Autonomous Agent',
        status: 'offline'
      });
      setNewAgentId('');
      setNewAgentName('');
      setShowRegisterModal(false);
      await fetchAllData();
      setSelectedAgentId(created.id);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to register agent');
    } finally {
      setIsActionPending(false);
    }
  };

  const selectedSession = sessions.find((s) => s.id === selectedSessionId) || null;
  const selectedAgent =
    agents.find((a) => a.id === (selectedSession?.creatorAgentId || selectedAgentId)) || null;

  return (
    <div className="min-h-screen h-screen bg-slate-950 text-slate-100 flex flex-col font-sans overflow-hidden">
      {/* Top Bar */}
      <header className="h-14 border-b border-slate-800/80 bg-slate-900/70 backdrop-blur px-5 flex items-center justify-between shrink-0">
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
            <Cpu className="w-4 h-4" />
          </div>
          <div className="flex items-center gap-2.5">
            <h1 className="text-base font-bold tracking-tight text-white">AgentMesh</h1>
            <span className="text-[11px] font-mono px-2 py-0.5 rounded bg-slate-800 text-slate-400 border border-slate-700">
              Session Cockpit MVP
            </span>
          </div>

          {/* System Health Pill */}
          <div className="hidden md:flex items-center gap-2 ml-3 pl-3 border-l border-slate-800 text-xs">
            <span
              className={`w-2 h-2 rounded-full ${
                apiError
                  ? 'bg-rose-500'
                  : statusData?.status === 'Healthy'
                  ? 'bg-emerald-400'
                  : 'bg-amber-400'
              }`}
            />
            <span className="text-slate-400">
              {apiError ? 'API Offline' : statusData?.status === 'Healthy' ? 'Mesh Healthy' : 'Connecting...'}
            </span>
            {signalRConnected && (
              <span className="text-[10px] font-mono text-emerald-400 bg-emerald-950/60 border border-emerald-500/30 px-1.5 py-0.5 rounded">
                LIVE
              </span>
            )}
          </div>
        </div>

        {/* Developer Controls & Auth */}
        <div className="flex items-center gap-2.5">
          <button
            onClick={handleResetMesh}
            disabled={isActionPending}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium bg-rose-950/40 hover:bg-rose-900/50 text-rose-300 border border-rose-500/30 transition disabled:opacity-50 cursor-pointer"
            title="Purge all agents, sessions, and events from PostgreSQL"
          >
            <Trash2 className="w-3.5 h-3.5 text-rose-400" />
            <span>Clear Mesh Data</span>
          </button>

          <div className="h-5 w-px bg-slate-800 mx-1" />

          <div className="flex items-center gap-2 bg-slate-900 border border-slate-800 rounded-lg px-2.5 py-1 text-xs">
            <User className="w-3.5 h-3.5 text-sky-400" />
            <span className="font-mono text-slate-300">{userEmail}</span>
            {auth?.signoutRedirect && (
              <button
                onClick={() => auth.signoutRedirect()}
                className="text-slate-500 hover:text-rose-400 ml-1 transition cursor-pointer"
                title="Sign out"
              >
                <LogOut className="w-3.5 h-3.5" />
              </button>
            )}
          </div>
        </div>
      </header>

      {/* API Error Banner */}
      {apiError && (
        <div className="bg-rose-950/80 border-b border-rose-500/40 px-5 py-2.5 flex items-center justify-between text-xs text-rose-200 shrink-0">
          <div className="flex items-center gap-2">
            <AlertTriangle className="w-4 h-4 text-rose-400 shrink-0" />
            <span className="font-semibold">API Gateway Unreachable:</span>
            <span className="font-mono">{apiError}</span>
          </div>
          <button
            onClick={() => {
              setApiError(null);
              fetchAllData();
            }}
            className="px-2.5 py-1 rounded bg-rose-900/60 hover:bg-rose-800 text-rose-100 font-medium transition cursor-pointer"
          >
            Retry
          </button>
        </div>
      )}

      {/* Main Split View */}
      <div className="flex-1 flex overflow-hidden">
        {/* Left Sidebar: Connected Agents & Their Sessions */}
        <aside className="w-80 border-r border-slate-800/80 bg-slate-900/40 flex flex-col shrink-0">
          <div className="p-3.5 border-b border-slate-800/80 flex items-center justify-between">
            <div className="flex items-center gap-2">
              <Bot className="w-4 h-4 text-sky-400" />
              <span className="text-xs font-bold uppercase tracking-wider text-slate-400">
                Connected Agents ({agents.length})
              </span>
            </div>
            <button
              onClick={() => setShowRegisterModal((v) => !v)}
              className="text-xs flex items-center gap-1 text-sky-400 hover:text-sky-300 font-medium px-2 py-1 rounded hover:bg-slate-800/60 transition cursor-pointer"
            >
              <Plus className="w-3.5 h-3.5" />
              <span>Add Agent</span>
            </button>
          </div>

          {/* Inline Register Agent Drawer */}
          {showRegisterModal && (
            <form
              onSubmit={handleRegisterAgent}
              className="p-3.5 bg-slate-900/90 border-b border-slate-800 space-y-2.5 text-xs"
            >
              <div className="font-semibold text-slate-200">Register Live Agent</div>
              <input
                type="text"
                placeholder="Agent ID (e.g. agy-cli)"
                value={newAgentId}
                onChange={(e) => setNewAgentId(e.target.value)}
                className="w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 focus:outline-none focus:border-sky-500"
                required
              />
              <input
                type="text"
                placeholder="Display Name (e.g. Agy Local CLI)"
                value={newAgentName}
                onChange={(e) => setNewAgentName(e.target.value)}
                className="w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 focus:outline-none focus:border-sky-500"
                required
              />
              <input
                type="text"
                placeholder="Role (e.g. Codebase Engineer)"
                value={newAgentRole}
                onChange={(e) => setNewAgentRole(e.target.value)}
                className="w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 focus:outline-none focus:border-sky-500"
              />
              <div className="flex justify-end gap-2 pt-1">
                <button
                  type="button"
                  onClick={() => setShowRegisterModal(false)}
                  className="px-2.5 py-1 rounded text-slate-400 hover:text-slate-200"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  className="px-3 py-1 rounded bg-sky-600 hover:bg-sky-500 text-white font-medium"
                >
                  Register
                </button>
              </div>
            </form>
          )}

          {/* Agent & Session List */}
          <div className="flex-1 overflow-y-auto p-3 space-y-4">
            {isLoading ? (
              <div className="py-12 flex flex-col items-center justify-center text-slate-500 text-xs gap-2">
                <RefreshCw className="w-5 h-5 animate-spin text-sky-400" />
                <span>Loading agents...</span>
              </div>
            ) : agents.length === 0 ? (
              <div className="p-5 rounded-xl border border-dashed border-slate-800 bg-slate-900/30 text-center space-y-3 my-4">
                <Bot className="w-8 h-8 text-slate-600 mx-auto" />
                <div>
                  <h3 className="text-xs font-semibold text-slate-300">No Agents Connected</h3>
                  <p className="text-[11px] text-slate-500 mt-1 leading-relaxed">
                    Connect an external CLI agent or register an agent to start a session.
                  </p>
                </div>
                <button
                  onClick={() => setShowRegisterModal(true)}
                  disabled={isActionPending}
                  className="w-full py-2 px-3 rounded-lg bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center justify-center gap-1.5 transition cursor-pointer"
                >
                  <Plus className="w-3.5 h-3.5" />
                  <span>Register Agent</span>
                </button>
              </div>
            ) : (
              agents.map((agent) => {
                const agentSessions = sessions.filter((s) => s.creatorAgentId === agent.id);
                const isAgentSelected = selectedAgent?.id === agent.id;
                const isOffline = agent.status === 'offline';
                const isBusy =
                  !isOffline &&
                  (agent.status === 'busy' || agentSessions.some((s) => s.status === 'thinking'));

                return (
                  <div
                    key={agent.id}
                    className={`rounded-xl border transition ${
                      isAgentSelected
                        ? 'border-slate-700 bg-slate-900/80'
                        : 'border-slate-800/70 bg-slate-900/30 hover:border-slate-800'
                    }`}
                  >
                    {/* Agent Header */}
                    <div
                      onClick={() => {
                        setSelectedAgentId(agent.id);
                        if (agentSessions.length > 0 && !agentSessions.some((s) => s.id === selectedSessionId)) {
                          setSelectedSessionId(agentSessions[0].id);
                        }
                      }}
                      className="p-3 flex items-start justify-between cursor-pointer"
                    >
                      <div className="flex items-start gap-2.5 min-w-0">
                        <span
                          className={`w-2.5 h-2.5 rounded-full mt-1 shrink-0 ${
                            isOffline
                              ? 'bg-slate-600'
                              : isBusy
                              ? 'bg-amber-400 animate-pulse'
                              : 'bg-emerald-400'
                          }`}
                        />
                        <div className="min-w-0">
                          <div className="flex items-center gap-1.5">
                            <span className="text-xs font-bold text-white truncate">{agent.name}</span>
                            <span
                              className={`text-[9px] font-mono uppercase px-1.5 py-0.2 rounded ${
                                isOffline
                                  ? 'bg-slate-800 text-slate-400'
                                  : isBusy
                                  ? 'bg-amber-500/20 text-amber-300'
                                  : 'bg-emerald-500/20 text-emerald-300'
                              }`}
                            >
                              {isOffline ? 'offline' : isBusy ? 'busy' : 'online'}
                            </span>
                          </div>
                          <div className="text-[11px] font-mono text-slate-400 truncate">
                            @{agent.id} · {agent.role || 'Agent'}
                          </div>
                        </div>
                      </div>

                      <button
                        onClick={(e) => {
                          e.stopPropagation();
                          setSelectedAgentId(agent.id);
                          setIsCreatingSession(true);
                        }}
                        className="p-1 rounded-md bg-slate-800 hover:bg-sky-600 text-slate-300 hover:text-white transition shrink-0 cursor-pointer"
                        title={`New Session with ${agent.name}`}
                      >
                        <Plus className="w-3.5 h-3.5" />
                      </button>
                    </div>

                    {/* Agent's Sessions */}
                    <div className="px-2 pb-2 space-y-1">
                      {agentSessions.length === 0 ? (
                        <div className="px-2 py-1.5 text-[11px] text-slate-500 italic">
                          No sessions yet
                        </div>
                      ) : (
                        agentSessions.map((session) => {
                          const isSelected = session.id === selectedSessionId && !isCreatingSession;
                          const isThinking = session.status === 'thinking';

                          return (
                            <button
                              key={session.id}
                              onClick={() => {
                                setSelectedAgentId(agent.id);
                                setSelectedSessionId(session.id);
                                setIsCreatingSession(false);
                              }}
                              className={`w-full text-left px-2.5 py-2 rounded-lg text-xs flex items-center justify-between gap-2 transition cursor-pointer ${
                                isSelected
                                  ? 'bg-sky-500/15 text-sky-300 border border-sky-500/30 font-medium'
                                  : 'text-slate-400 hover:bg-slate-800/50 hover:text-slate-200 border border-transparent'
                              }`}
                            >
                              <div className="flex items-center gap-2 min-w-0">
                                {isThinking ? (
                                  <RefreshCw className="w-3 h-3 text-amber-400 animate-spin shrink-0" />
                                ) : (
                                  <MessageSquare className="w-3 h-3 text-slate-500 shrink-0" />
                                )}
                                <span className="truncate">{session.title}</span>
                              </div>
                              <ChevronRight className="w-3 h-3 opacity-50 shrink-0" />
                            </button>
                          );
                        })
                      )}
                    </div>
                  </div>
                );
              })
            )}
          </div>
        </aside>

        {/* Right Main Panel: Active Session Trajectory or New Session Composer */}
        <main className="flex-1 flex flex-col bg-slate-950 overflow-hidden">
          {isCreatingSession && selectedAgent ? (
            <div className="flex-1 flex items-center justify-center p-6">
              <form
                onSubmit={handleCreateSession}
                className="max-w-lg w-full bg-slate-900/70 border border-slate-800 rounded-2xl p-6 space-y-4 shadow-2xl"
              >
                <div className="flex items-center gap-2.5">
                  <div className="w-9 h-9 rounded-xl bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
                    <Bot className="w-5 h-5" />
                  </div>
                  <div>
                    <h2 className="text-base font-bold text-white">
                      Start New Session with {selectedAgent.name}
                    </h2>
                    <p className="text-xs text-slate-400 font-mono">@{selectedAgent.id}</p>
                  </div>
                </div>

                <div className="space-y-1.5">
                  <label className="block text-xs font-semibold text-slate-300">
                    Session Objective / Title
                  </label>
                  <input
                    type="text"
                    placeholder="e.g. Refactor authentication middleware"
                    value={newSessionTitle}
                    onChange={(e) => setNewSessionTitle(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3.5 py-2 text-sm text-white focus:outline-none focus:border-sky-500"
                    required
                    autoFocus
                  />
                </div>

                <div className="space-y-1.5">
                  <label className="block text-xs font-semibold text-slate-300">
                    Initial Prompt (Optional)
                  </label>
                  <textarea
                    rows={3}
                    placeholder="Enter an initial instruction for the agent to begin reasoning..."
                    value={newSessionPrompt}
                    onChange={(e) => setNewSessionPrompt(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3.5 py-2 text-sm text-white focus:outline-none focus:border-sky-500"
                  />
                </div>

                <div className="flex items-center justify-end gap-2.5 pt-2">
                  <button
                    type="button"
                    onClick={() => setIsCreatingSession(false)}
                    className="px-4 py-2 rounded-lg text-xs font-medium text-slate-400 hover:text-white transition"
                  >
                    Cancel
                  </button>
                  <button
                    type="submit"
                    disabled={isActionPending}
                    className="px-4 py-2 rounded-lg text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white transition cursor-pointer"
                  >
                    Launch Session
                  </button>
                </div>
              </form>
            </div>
          ) : selectedSession ? (
            <>
              {/* Active Session Header */}
              <div className="px-6 py-3.5 border-b border-slate-800/80 bg-slate-900/30 flex items-center justify-between shrink-0">
                <div className="min-w-0">
                  <div className="flex items-center gap-2.5">
                    <h2 className="text-sm font-bold text-white truncate">
                      {selectedSession.title}
                    </h2>
                    <span
                      className={`text-[11px] font-mono px-2 py-0.5 rounded-full border ${
                        selectedSession.status === 'thinking'
                          ? 'bg-amber-500/10 text-amber-300 border-amber-500/30 animate-pulse'
                          : 'bg-emerald-500/10 text-emerald-300 border-emerald-500/30'
                      }`}
                    >
                      {selectedSession.status === 'thinking' ? 'Thinking...' : selectedSession.status}
                    </span>
                  </div>
                  <div className="text-xs text-slate-400 flex items-center gap-2 mt-0.5 font-mono">
                    <span>Agent: {selectedAgent?.name || selectedSession.creatorAgentId || 'agy'}</span>
                    <span>·</span>
                    <span>{events.length} trajectory events</span>
                  </div>
                </div>

                {selectedAgent && (
                  <button
                    onClick={() => setIsCreatingSession(true)}
                    className="px-3 py-1.5 rounded-lg text-xs font-medium bg-slate-800 hover:bg-slate-700 text-slate-200 flex items-center gap-1.5 transition cursor-pointer"
                  >
                    <Plus className="w-3.5 h-3.5" />
                    <span>New Session</span>
                  </button>
                )}
              </div>

              {/* Trajectory Events Stream */}
              <div className="flex-1 overflow-y-auto p-6 space-y-4">
                {events.length === 0 ? (
                  <div className="h-full flex flex-col items-center justify-center text-center text-slate-500 space-y-2">
                    <Activity className="w-8 h-8 text-slate-700" />
                    <p className="text-sm font-medium text-slate-400">Session initialized</p>
                    <p className="text-xs max-w-sm">
                      Send a prompt below or stream events from your CLI agent to see live reasoning, tool calls, and outputs.
                    </p>
                  </div>
                ) : (
                  events.map((ev) => {
                    const type = ev.eventType || 'response';

                    if (type === 'prompt') {
                      return (
                        <div key={ev.id} className="flex justify-end">
                          <div className="max-w-2xl bg-sky-600/15 border border-sky-500/30 rounded-2xl rounded-tr-sm px-4 py-3 text-slate-100">
                            <div className="flex items-center justify-between gap-4 text-[11px] font-mono text-sky-400 mb-1">
                              <span>Prompt ({ev.senderAgentId})</span>
                              <span>{new Date(ev.createdAt).toLocaleTimeString()}</span>
                            </div>
                            <p className="text-sm whitespace-pre-wrap leading-relaxed">{ev.content}</p>
                          </div>
                        </div>
                      );
                    }

                    if (type === 'thought') {
                      return (
                        <details
                          key={ev.id}
                          open
                          className="group bg-indigo-950/20 border border-indigo-500/25 rounded-xl overflow-hidden"
                        >
                          <summary className="px-4 py-2.5 cursor-pointer flex items-center justify-between text-xs font-medium text-indigo-300 hover:bg-indigo-950/30 select-none">
                            <div className="flex items-center gap-2">
                              <Brain className="w-4 h-4 text-indigo-400 shrink-0" />
                              <span>Agent Reasoning / Thought</span>
                              <span className="text-[11px] font-mono text-indigo-400/70">
                                @{ev.senderAgentId}
                              </span>
                            </div>
                            <span className="text-[11px] font-mono text-slate-500">
                              {new Date(ev.createdAt).toLocaleTimeString()}
                            </span>
                          </summary>
                          <div className="px-4 pb-3 pt-1 text-xs text-slate-300 font-mono leading-relaxed whitespace-pre-wrap border-t border-indigo-500/10">
                            {ev.content}
                          </div>
                        </details>
                      );
                    }

                    if (type === 'tool_call') {
                      return (
                        <details
                          key={ev.id}
                          className="bg-amber-950/15 border border-amber-500/25 rounded-xl overflow-hidden"
                        >
                          <summary className="px-4 py-2.5 cursor-pointer flex items-center justify-between text-xs font-medium text-amber-300 hover:bg-amber-950/25 select-none">
                            <div className="flex items-center gap-2">
                              <Wrench className="w-4 h-4 text-amber-400 shrink-0" />
                              <span className="font-mono font-bold text-amber-200">
                                {ev.toolName || 'tool_call'}
                              </span>
                              <span className="text-slate-300">{ev.content}</span>
                            </div>
                            <span className="text-[11px] font-mono text-slate-500">
                              {new Date(ev.createdAt).toLocaleTimeString()}
                            </span>
                          </summary>
                          {ev.toolArgsJson && (
                            <div className="px-4 py-2.5 bg-slate-950/80 border-t border-amber-500/15">
                              <div className="text-[10px] font-mono uppercase text-slate-500 mb-1">
                                Arguments
                              </div>
                              <pre className="text-xs font-mono text-amber-200/90 overflow-x-auto">
                                {ev.toolArgsJson}
                              </pre>
                            </div>
                          )}
                        </details>
                      );
                    }

                    if (type === 'tool_result') {
                      return (
                        <details
                          key={ev.id}
                          className="bg-slate-900/70 border border-slate-800 rounded-xl overflow-hidden"
                        >
                          <summary className="px-4 py-2 cursor-pointer flex items-center justify-between text-xs text-slate-300 hover:bg-slate-900 select-none">
                            <div className="flex items-center gap-2">
                              <Terminal className="w-3.5 h-3.5 text-emerald-400 shrink-0" />
                              <span className="font-mono text-emerald-400">
                                {ev.toolName ? `${ev.toolName} →` : 'Tool Output'}
                              </span>
                              <span className="text-slate-400">{ev.content}</span>
                            </div>
                            <span className="text-[11px] font-mono text-slate-500">
                              {new Date(ev.createdAt).toLocaleTimeString()}
                            </span>
                          </summary>
                          {ev.toolResultJson && (
                            <div className="px-4 py-2.5 bg-slate-950 border-t border-slate-800">
                              <pre className="text-xs font-mono text-slate-300 overflow-x-auto whitespace-pre-wrap">
                                {ev.toolResultJson}
                              </pre>
                            </div>
                          )}
                        </details>
                      );
                    }

                    // Default: 'response'
                    return (
                      <div
                        key={ev.id}
                        className="bg-slate-900/90 border border-slate-800 rounded-2xl rounded-tl-sm px-5 py-4 text-slate-100 shadow-lg"
                      >
                        <div className="flex items-center justify-between text-xs font-mono text-emerald-400 mb-2">
                          <div className="flex items-center gap-2">
                            <CheckCircle2 className="w-4 h-4" />
                            <span className="font-bold">@{ev.senderAgentId}</span>
                          </div>
                          <span className="text-slate-500">
                            {new Date(ev.createdAt).toLocaleTimeString()}
                          </span>
                        </div>
                        <div className="text-sm whitespace-pre-wrap leading-relaxed text-slate-200">
                          {ev.content}
                        </div>
                      </div>
                    );
                  })
                )}

                {selectedSession.status === 'thinking' && (
                  selectedAgent?.status === 'offline' ? (
                    <div className="flex flex-col gap-1.5 px-4 py-3 rounded-xl bg-amber-950/30 border border-amber-500/30 text-xs text-amber-200 max-w-xl">
                      <div className="flex items-center gap-2 font-semibold text-amber-300">
                        <AlertTriangle className="w-4 h-4 text-amber-400 shrink-0" />
                        <span>Agent @{selectedAgent.id} is offline (waiting for CLI connection)</span>
                      </div>
                      <p className="text-slate-300 leading-relaxed">
                        Connect the agent runner from your terminal to process this prompt:
                      </p>
                      <code className="bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 font-mono text-sky-300 select-all">
                        agentmesh start {selectedAgent.id}
                      </code>
                    </div>
                  ) : (
                    <div className="flex items-center gap-2.5 px-4 py-2.5 rounded-xl bg-indigo-950/20 border border-indigo-500/20 text-xs text-indigo-300 w-fit">
                      <RefreshCw className="w-3.5 h-3.5 animate-spin text-indigo-400" />
                      <span>Agent is reasoning...</span>
                    </div>
                  )
                )}

                <div ref={timelineEndRef} />
              </div>

              {/* Bottom Prompt Bar */}
              <form
                onSubmit={handleSendPrompt}
                className="p-4 border-t border-slate-800/80 bg-slate-900/50 shrink-0 space-y-2"
              >
                <div className="flex items-center gap-2.5">
                  <input
                    type="text"
                    placeholder={`Send instruction to ${selectedAgent?.name || selectedSession.creatorAgentId || 'agent'}...`}
                    value={promptInput}
                    onChange={(e) => setPromptInput(e.target.value)}
                    className="flex-1 bg-slate-950 border border-slate-800 rounded-xl px-4 py-2.5 text-sm text-white placeholder-slate-500 focus:outline-none focus:border-sky-500"
                  />
                  <button
                    type="submit"
                    disabled={!promptInput.trim()}
                    className="px-4 py-2.5 rounded-xl bg-sky-600 hover:bg-sky-500 disabled:opacity-40 text-white font-semibold text-xs flex items-center gap-1.5 transition cursor-pointer"
                  >
                    <Send className="w-3.5 h-3.5" />
                    <span>Send</span>
                  </button>
                </div>
                <div className="flex items-center justify-between text-[11px] text-slate-400 px-1">
                  <span className="font-mono text-slate-500">
                    Agent: @{selectedSession.creatorAgentId || selectedAgent?.id || 'agy'}
                  </span>
                  <span className="font-mono text-slate-500">
                    Session ID: {selectedSession.id.slice(0, 8)}
                  </span>
                </div>
              </form>
            </>
          ) : (
            <div className="flex-1 flex flex-col items-center justify-center p-8 text-center space-y-4">
              <div className="w-14 h-14 rounded-2xl bg-slate-900 border border-slate-800 flex items-center justify-center text-sky-400">
                <Bot className="w-7 h-7" />
              </div>
              <div className="max-w-md space-y-1.5">
                <h2 className="text-lg font-bold text-white">Agent Session Observatory</h2>
                <p className="text-xs text-slate-400 leading-relaxed">
                  Select a connected agent on the left to inspect its sessions, or connect a live CLI agent to stream reasoning steps and tool executions.
                </p>
              </div>
              <div className="flex items-center gap-3">
                {selectedAgent ? (
                  <button
                    onClick={() => setIsCreatingSession(true)}
                    className="px-4 py-2 rounded-xl bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center gap-2 transition cursor-pointer"
                  >
                    <Plus className="w-4 h-4" />
                    <span>New Session with {selectedAgent.name}</span>
                  </button>
                ) : (
                  <button
                    onClick={() => setShowRegisterModal(true)}
                    className="px-4 py-2 rounded-xl bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center gap-2 transition cursor-pointer"
                  >
                    <Plus className="w-4 h-4" />
                    <span>Register Agent</span>
                  </button>
                )}
              </div>
            </div>
          )}
        </main>
      </div>
    </div>
  );
}
