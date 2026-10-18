import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
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
  Cpu,
  Folder,
  FolderOpen,
  ChevronUp,
  ChevronDown,
  Home,
  X,
  Check,
  RotateCcw,
  Zap,
  Sliders,
  BarChart2,
  Layers,
  Clock
} from 'lucide-react';
import { api } from './services/api';
import { useSignalR } from './hooks/useSignalR';
import { useAuth } from './auth/AuthProvider';
import type {
  Agent,
  AgentSession,
  AgentUsageResponse,
  FilesystemBrowseResponse,
  SessionMessage,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from './types';

export default function App() {
  const auth = useAuth();
  const userEmail = auth?.user?.profile?.email || auth?.user?.profile?.name || 'admin';

  const [agents, setAgents] = useState<Agent[]>([]);
  const [sessions, setSessions] = useState<AgentSession[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [events, setEvents] = useState<SessionMessage[]>([]);

  const [statusData, setStatusData] = useState<SystemStatusResponse | null>(null);
  const [capabilities, setCapabilities] = useState<SystemCapabilitiesResponse | null>(null);
  const [apiError, setApiError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [isActionPending, setIsActionPending] = useState<boolean>(false);

  // Folder picker modal state
  const [showFolderPicker, setShowFolderPicker] = useState<boolean>(false);
  const [browseData, setBrowseData] = useState<FilesystemBrowseResponse | null>(null);
  const [isBrowsing, setIsBrowsing] = useState<boolean>(false);
  const [browseError, setBrowseError] = useState<string | null>(null);

  // Prompt & New Session state
  const [promptInput, setPromptInput] = useState('');
  const [isCreatingSession, setIsCreatingSession] = useState(false);
  const [newSessionTitle, setNewSessionTitle] = useState('');
  const [newSessionPrompt, setNewSessionPrompt] = useState('');
  const [newSessionWorkDir, setNewSessionWorkDir] = useState('');
  const [newSessionModel, setNewSessionModel] = useState('');
  const [newSessionEffort, setNewSessionEffort] = useState('');
  const [newSessionMode, setNewSessionMode] = useState('');
  const [newSessionTier, setNewSessionTier] = useState('');

  // Agent Quota & Usage details modal state
  const [agentUsages, setAgentUsages] = useState<Record<string, AgentUsageResponse>>({});
  const [selectedUsageAgent, setSelectedUsageAgent] = useState<Agent | null>(null);
  const [showUsageModal, setShowUsageModal] = useState(false);

  // Active session inline control edit popover state
  const [editingSessionControl, setEditingSessionControl] = useState<'model' | 'effort' | 'tier' | 'mode' | null>(null);

  // Quick Register Agent modal/drawer state
  const [showRegisterModal, setShowRegisterModal] = useState(false);
  const [newAgentId, setNewAgentId] = useState('');
  const [newAgentName, setNewAgentName] = useState('');
  const [newAgentRole, setNewAgentRole] = useState('Codebase Engineer');

  const timelineEndRef = useRef<HTMLDivElement | null>(null);
  const { connection, connected: signalRConnected } = useSignalR('/hubs/agent-mesh');

  const fetchAllData = useCallback(async () => {
    try {
      const [statusRes, agentsRes, sessionsRes, capsRes] = await Promise.all([
        api.getStatus(),
        api.getAgents(),
        api.getSessions('all'),
        api.getCapabilities().catch(() => null)
      ]);
      setStatusData(statusRes);
      setAgents(agentsRes);
      setSessions(sessionsRes);
      if (capsRes) {
        setCapabilities(capsRes);
      }
      setApiError(null);

      // Fetch usage stats & quotas in parallel for all agents
      const usages: Record<string, AgentUsageResponse> = {};
      await Promise.all(
        agentsRes.map(async (agent) => {
          try {
            const u = await api.getAgentUsage(agent.id);
            usages[agent.id] = u;
          } catch {
            // Usage might not be available yet or endpoint error
          }
        })
      );
      setAgentUsages(usages);

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

  const loadBrowseDirectory = useCallback(async (path?: string) => {
    setIsBrowsing(true);
    setBrowseError(null);
    try {
      const res = await api.browseFilesystem(path);
      setBrowseData(res);
    } catch (err) {
      setBrowseError(err instanceof Error ? err.message : 'Failed to browse directory');
    } finally {
      setIsBrowsing(false);
    }
  }, []);

  const openFolderPicker = useCallback(() => {
    const initialPath = newSessionWorkDir || capabilities?.workspace?.currentDirectory || undefined;
    loadBrowseDirectory(initialPath);
    setShowFolderPicker(true);
  }, [newSessionWorkDir, capabilities?.workspace?.currentDirectory, loadBrowseDirectory]);

  const selectedSession = sessions.find((s) => s.id === selectedSessionId) || null;
  const activeCreationAgent =
    agents.find((a) => a.id === selectedAgentId) || agents[0] || null;
  const selectedAgent = isCreatingSession
    ? activeCreationAgent
    : agents.find((a) => a.id === (selectedSession?.creatorAgentId || selectedAgentId)) || null;

  const currentRuntimeCaps = useMemo(() => {
    if (!capabilities) return null;
    const agent = isCreatingSession ? activeCreationAgent : selectedAgent;
    if (!agent) return capabilities.agentRuntimes['agy'] || null;

    const idLower = agent.id.toLowerCase();
    const nameLower = agent.name.toLowerCase();
    let key = 'agy';
    if (idLower.includes('copilot') || nameLower.includes('copilot')) key = 'copilot';
    else if (idLower.includes('claude') || nameLower.includes('claude')) key = 'claude';
    else if (idLower.includes('agy') || idLower.includes('antigravity') || nameLower.includes('agy') || nameLower.includes('antigravity')) key = 'agy';
    else if (capabilities.agentRuntimes[agent.id]) key = agent.id;
    else key = 'agy';

    return capabilities.agentRuntimes[key] || capabilities.agentRuntimes['agy'] || null;
  }, [capabilities, isCreatingSession, activeCreationAgent, selectedAgent]);

  const availableModels = useMemo(() => {
    return currentRuntimeCaps?.models || [];
  }, [currentRuntimeCaps]);

  const availableModes = useMemo(() => {
    return currentRuntimeCaps?.modes || [];
  }, [currentRuntimeCaps]);

  const availableTiers = useMemo(() => {
    return currentRuntimeCaps?.tiers || [];
  }, [currentRuntimeCaps]);

  const selectedModelCap = useMemo(() => {
    if (!newSessionModel || !currentRuntimeCaps) return null;
    return currentRuntimeCaps.models.find((m) => m.id === newSessionModel) || null;
  }, [currentRuntimeCaps, newSessionModel]);

  const availableEfforts = useMemo(() => {
    return selectedModelCap?.efforts || [];
  }, [selectedModelCap]);

  // Sync / validate effort when model changes
  useEffect(() => {
    if (newSessionEffort && !availableEfforts.includes(newSessionEffort)) {
      setNewSessionEffort('');
    }
  }, [availableEfforts, newSessionEffort]);

  // Sync / validate model, mode, tier when agent changes
  useEffect(() => {
    if (newSessionModel && availableModels.length > 0 && !availableModels.some((m) => m.id === newSessionModel)) {
      setNewSessionModel('');
    }
    if (newSessionMode && availableModes.length > 0 && !availableModes.some((m) => m.id === newSessionMode)) {
      setNewSessionMode('');
    }
    if (newSessionTier && availableTiers.length > 0 && !availableTiers.some((t) => t.id === newSessionTier)) {
      setNewSessionTier('');
    }
  }, [availableModels, availableModes, availableTiers, newSessionModel, newSessionMode, newSessionTier]);

  const handleCreateSession = async (e: React.FormEvent) => {
    e.preventDefault();
    const targetAgent = selectedAgentId || activeCreationAgent?.id || agents[0]?.id;
    if (!targetAgent || !newSessionTitle.trim()) return;

    setIsActionPending(true);
    try {
      const created = await api.createAgentSession(
        targetAgent,
        newSessionTitle.trim(),
        newSessionPrompt.trim() || undefined,
        {
          workingDirectory: newSessionWorkDir.trim() || undefined,
          model: newSessionModel.trim() || undefined,
          effort: newSessionEffort.trim() || undefined,
          mode: newSessionMode.trim() || undefined,
          tier: newSessionTier.trim() || undefined
        }
      );
      setNewSessionTitle('');
      setNewSessionPrompt('');
      setNewSessionWorkDir('');
      setNewSessionModel('');
      setNewSessionEffort('');
      setNewSessionMode('');
      setNewSessionTier('');
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

  const handleUpdateSessionControl = async (params: {
    model?: string;
    effort?: string;
    tier?: string;
    mode?: string;
    workingDirectory?: string;
  }) => {
    if (!selectedSessionId) return;
    setIsActionPending(true);
    try {
      await api.updateSessionControls(selectedSessionId, params);
      setEditingSessionControl(null);
      await fetchAllData();
      await fetchSessionEvents(selectedSessionId);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to update session controls');
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

  const selectedSessionMetadata = useMemo(() => {
    if (!selectedSession?.metadataJson) return null;
    try {
      return JSON.parse(selectedSession.metadataJson) as Record<string, any>;
    } catch {
      return null;
    }
  }, [selectedSession?.metadataJson]);

  const sessionModelCap = useMemo(() => {
    if (!selectedSessionMetadata?.model || !currentRuntimeCaps) return null;
    return currentRuntimeCaps.models.find((m) => m.id === selectedSessionMetadata.model) || null;
  }, [currentRuntimeCaps, selectedSessionMetadata?.model]);

  const sessionAvailableEfforts = useMemo(() => {
    if (sessionModelCap && sessionModelCap.efforts.length > 0) return sessionModelCap.efforts;
    const allEfforts = Array.from(new Set(currentRuntimeCaps?.models.flatMap((m) => m.efforts) || []));
    return allEfforts.length > 0 ? allEfforts : ['low', 'medium', 'high'];
  }, [sessionModelCap, currentRuntimeCaps]);

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
                const usage = agentUsages[agent.id];

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
                          {usage && (
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                setSelectedUsageAgent(agent);
                                setShowUsageModal(true);
                              }}
                              className="mt-1.5 flex items-center gap-1.5 px-2 py-0.5 rounded-md bg-slate-950/70 hover:bg-slate-800 text-[10px] font-mono text-slate-300 border border-slate-800 hover:border-slate-700 transition cursor-pointer max-w-full"
                              title="Click to view full quota and lifetime token usage"
                            >
                              <span
                                className={`w-1.5 h-1.5 rounded-full shrink-0 ${
                                  usage.providerQuota
                                    ? usage.providerQuota.primaryRemainingFraction > 0.3
                                      ? 'bg-emerald-400'
                                      : 'bg-rose-400'
                                    : 'bg-sky-400'
                                }`}
                              />
                              <span className="truncate">
                                {usage.providerQuota
                                  ? `${Math.round(usage.providerQuota.primaryRemainingFraction * 100)}% quota`
                                  : 'Quota'}
                                {' · '}
                                {usage.lifetimeTokens.totalTokens > 0
                                  ? `${(usage.lifetimeTokens.totalTokens / 1000).toFixed(1)}k tokens`
                                  : '0 tokens'}
                              </span>
                              <BarChart2 className="w-2.5 h-2.5 text-slate-500 hover:text-sky-400 ml-1 shrink-0" />
                            </button>
                          )}
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
                    rows={2}
                    placeholder="Enter an initial instruction for the agent to begin reasoning..."
                    value={newSessionPrompt}
                    onChange={(e) => setNewSessionPrompt(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3.5 py-2 text-sm text-white focus:outline-none focus:border-sky-500"
                  />
                </div>

                {/* Session Controls: Working Directory & Model Parameters */}
                <div className="pt-2 border-t border-slate-800/80 space-y-3.5">
                  <div className="flex items-center gap-1.5 text-xs font-semibold text-slate-300">
                    <Sliders className="w-3.5 h-3.5 text-sky-400" />
                    <span>Agent Execution Controls</span>
                  </div>

                  {/* Working Directory Selection */}
                  <div className="space-y-2">
                    <div className="flex items-center justify-between">
                      <label className="block text-[11px] font-medium text-slate-400">
                        Working Directory
                      </label>
                      {newSessionWorkDir && (
                        <button
                          type="button"
                          onClick={() => setNewSessionWorkDir('')}
                          className="text-[10px] text-sky-400 hover:text-sky-300 flex items-center gap-1 cursor-pointer"
                        >
                          <RotateCcw className="w-2.5 h-2.5" />
                          <span>Reset to Sandbox</span>
                        </button>
                      )}
                    </div>

                    <div className="flex items-center gap-2">
                      <div
                        onClick={openFolderPicker}
                        className="flex-1 min-w-0 bg-slate-950 border border-slate-800 hover:border-slate-700 rounded-lg px-3 py-2 flex items-center gap-2.5 cursor-pointer transition group"
                      >
                        <div
                          className={`p-1.5 rounded-md ${
                            newSessionWorkDir
                              ? 'bg-sky-500/10 text-sky-400'
                              : 'bg-emerald-500/10 text-emerald-400'
                          }`}
                        >
                          <Folder className="w-4 h-4" />
                        </div>
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-2">
                            <span className="text-xs font-semibold text-white truncate">
                              {newSessionWorkDir
                                ? newSessionWorkDir.split(/[\\/]/).filter(Boolean).pop() || newSessionWorkDir
                                : 'Default Sandbox Workspace'}
                            </span>
                            <span
                              className={`text-[9px] font-mono px-1.5 py-0.2 rounded border ${
                                newSessionWorkDir
                                  ? 'bg-sky-500/10 text-sky-300 border-sky-500/20'
                                  : 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20'
                              }`}
                            >
                              {newSessionWorkDir ? 'Custom Path' : 'Safe Isolated'}
                            </span>
                          </div>
                          <p className="text-[10px] text-slate-400 font-mono truncate mt-0.5">
                            {newSessionWorkDir || '~/.agentmesh/workspaces/<session-id>'}
                          </p>
                        </div>
                      </div>

                      <button
                        type="button"
                        onClick={openFolderPicker}
                        className="px-3 py-2 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 border border-slate-700 hover:border-slate-600 text-xs font-medium flex items-center gap-1.5 transition shrink-0 cursor-pointer"
                      >
                        <FolderOpen className="w-3.5 h-3.5 text-sky-400" />
                        <span>Browse...</span>
                      </button>
                    </div>

                    {/* Quick Preset Bookmark Chips */}
                    <div className="flex flex-wrap items-center gap-1.5 pt-0.5">
                      <span className="text-[10px] text-slate-500 font-medium mr-1">Quick Select:</span>
                      <button
                        type="button"
                        onClick={() => setNewSessionWorkDir('')}
                        className={`text-[10px] font-mono px-2 py-0.5 rounded-md border transition cursor-pointer ${
                          !newSessionWorkDir
                            ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/40 font-semibold'
                            : 'bg-slate-900 text-slate-400 border-slate-800 hover:text-slate-200 hover:border-slate-700'
                        }`}
                      >
                        ⚡ Default Sandbox
                      </button>
                      {capabilities?.workspace?.bookmarks
                        ?.filter((b) => b.path)
                        .map((bookmark) => {
                          const isSelected = newSessionWorkDir === bookmark.path;
                          return (
                            <button
                              key={bookmark.label}
                              type="button"
                              onClick={() => setNewSessionWorkDir(bookmark.path)}
                              title={bookmark.path}
                              className={`text-[10px] font-mono px-2 py-0.5 rounded-md border transition cursor-pointer ${
                                isSelected
                                  ? 'bg-sky-500/20 text-sky-300 border-sky-500/40 font-semibold'
                                  : 'bg-slate-900 text-slate-400 border-slate-800 hover:text-slate-200 hover:border-slate-700'
                              }`}
                            >
                              {bookmark.label}
                            </button>
                          );
                        })}
                    </div>
                  </div>

                  <div className={`grid ${availableTiers.length > 0 ? 'grid-cols-2 sm:grid-cols-4' : 'grid-cols-3'} gap-2.5`}>
                    {/* Model Selection Dropdown */}
                    <div className="space-y-1">
                      <label className="block text-[11px] font-medium text-slate-400">
                        Model
                      </label>
                      <select
                        value={newSessionModel}
                        onChange={(e) => setNewSessionModel(e.target.value)}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1.5 text-xs text-white focus:outline-none focus:border-sky-500 cursor-pointer"
                      >
                        <option value="">Default Model</option>
                        {availableModels.map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.name || m.id}
                          </option>
                        ))}
                      </select>
                    </div>

                    {/* Reasoning Effort Dropdown (tied to model) */}
                    <div className="space-y-1">
                      <div className="flex items-center justify-between">
                        <label className="block text-[11px] font-medium text-slate-400">
                          Reasoning Effort
                        </label>
                      </div>
                      <select
                        value={newSessionEffort}
                        onChange={(e) => setNewSessionEffort(e.target.value)}
                        disabled={availableEfforts.length === 0}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2 py-1.5 text-xs text-slate-200 focus:outline-none focus:border-sky-500 disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer"
                      >
                        {availableEfforts.length === 0 ? (
                          <option value="">Not applicable</option>
                        ) : (
                          <>
                            <option value="">Default Effort</option>
                            {availableEfforts.map((eff) => (
                              <option key={eff} value={eff}>
                                {eff}
                              </option>
                            ))}
                          </>
                        )}
                      </select>
                    </div>

                    {/* Execution Mode Dropdown (tied to system) */}
                    <div className="space-y-1">
                      <label className="block text-[11px] font-medium text-slate-400">
                        Mode
                      </label>
                      <select
                        value={newSessionMode}
                        onChange={(e) => setNewSessionMode(e.target.value)}
                        disabled={availableModes.length === 0}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2 py-1.5 text-xs text-slate-200 focus:outline-none focus:border-sky-500 disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer"
                      >
                        {availableModes.length === 0 ? (
                          <option value="">Not supported</option>
                        ) : (
                          <>
                            <option value="">Default Mode</option>
                            {availableModes.map((mode) => (
                              <option key={mode.id} value={mode.id}>
                                {mode.label}
                              </option>
                            ))}
                          </>
                        )}
                      </select>
                    </div>

                    {/* Copilot Tier / Optimize For Dropdown */}
                    {availableTiers.length > 0 && (
                      <div className="space-y-1">
                        <label className="block text-[11px] font-medium text-slate-400">
                          Optimize For (Tier)
                        </label>
                        <select
                          value={newSessionTier}
                          onChange={(e) => setNewSessionTier(e.target.value)}
                          className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2 py-1.5 text-xs text-slate-200 focus:outline-none focus:border-sky-500 cursor-pointer"
                        >
                          <option value="">Default Tier</option>
                          {availableTiers.map((t) => (
                            <option key={t.id} value={t.id}>
                              {t.label}
                            </option>
                          ))}
                        </select>
                      </div>
                    )}
                  </div>
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
              <div className="px-6 py-3 border-b border-slate-800/80 bg-slate-900/30 flex items-center justify-between shrink-0">
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

                  <div className="flex flex-wrap items-center gap-1.5 mt-1 font-mono text-[11px]">
                    <span className="text-slate-400">
                      Agent: <strong className="text-slate-200">@{selectedAgent?.id || selectedSession.creatorAgentId || 'agy'}</strong>
                    </span>
                    <span className="text-slate-600">·</span>
                    <span
                      className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 text-slate-300 border border-slate-700/80 truncate max-w-xs"
                      title={selectedSessionMetadata?.workingDirectory || `~/.agentmesh/workspaces/${selectedSession.id}`}
                    >
                      <Folder className="w-3 h-3 text-sky-400 shrink-0" />
                      <span className="truncate">
                        {selectedSessionMetadata?.workingDirectory
                          ? selectedSessionMetadata.workingDirectory
                          : `~/.agentmesh/workspaces/${selectedSession.id.slice(0, 8)}...`}
                      </span>
                    </span>

                    {/* Backdrop to close inline popover */}
                    {editingSessionControl && (
                      <div
                        className="fixed inset-0 z-30"
                        onClick={() => setEditingSessionControl(null)}
                      />
                    )}

                    {/* Model Selector Badge & Popover */}
                    <div className="relative inline-block">
                      <button
                        type="button"
                        onClick={() => setEditingSessionControl((prev) => (prev === 'model' ? null : 'model'))}
                        className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                        title="Click to change model for this session"
                      >
                        <Cpu className="w-3 h-3 text-purple-400 shrink-0" />
                        <span>{selectedSessionMetadata?.model || 'default model'}</span>
                        <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                      </button>
                      {editingSessionControl === 'model' && (
                        <div className="absolute left-0 top-full mt-1.5 z-40 w-64 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                          <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                            Switch Session Model
                          </div>
                          <button
                            type="button"
                            onClick={() => handleUpdateSessionControl({ model: '' })}
                            className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                          >
                            <span>Default Model</span>
                            {!selectedSessionMetadata?.model && <Check className="w-3 h-3 text-sky-400" />}
                          </button>
                          {availableModels.map((m) => (
                            <button
                              key={m.id}
                              type="button"
                              onClick={() => handleUpdateSessionControl({ model: m.id })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span className="truncate">{m.name || m.id}</span>
                              {selectedSessionMetadata?.model === m.id && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>

                    {/* Effort Selector Badge & Popover */}
                    {(selectedSessionMetadata?.effort || sessionAvailableEfforts.length > 0) && (
                      <div className="relative inline-block">
                        <button
                          type="button"
                          onClick={() => setEditingSessionControl((prev) => (prev === 'effort' ? null : 'effort'))}
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                          title="Click to change reasoning effort"
                        >
                          <Brain className="w-3 h-3 text-indigo-400 shrink-0" />
                          <span>{selectedSessionMetadata?.effort || 'effort'}</span>
                          <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                        </button>
                        {editingSessionControl === 'effort' && (
                          <div className="absolute left-0 top-full mt-1.5 z-40 w-44 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                            <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                              Reasoning Effort
                            </div>
                            <button
                              type="button"
                              onClick={() => handleUpdateSessionControl({ effort: '' })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span>Default</span>
                              {!selectedSessionMetadata?.effort && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                            {sessionAvailableEfforts.map((eff) => (
                              <button
                                key={eff}
                                type="button"
                                onClick={() => handleUpdateSessionControl({ effort: eff })}
                                className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                              >
                                <span>{eff}</span>
                                {selectedSessionMetadata?.effort === eff && <Check className="w-3 h-3 text-sky-400" />}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {/* Tier Selector Badge & Popover (for Copilot optimize-for) */}
                    {(selectedSessionMetadata?.tier || availableTiers.length > 0) && (
                      <div className="relative inline-block">
                        <button
                          type="button"
                          onClick={() => setEditingSessionControl((prev) => (prev === 'tier' ? null : 'tier'))}
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                          title="Click to change optimization tier"
                        >
                          <Layers className="w-3 h-3 text-teal-400 shrink-0" />
                          <span>{selectedSessionMetadata?.tier || 'tier'}</span>
                          <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                        </button>
                        {editingSessionControl === 'tier' && (
                          <div className="absolute left-0 top-full mt-1.5 z-40 w-48 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                            <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                              Optimization Tier
                            </div>
                            <button
                              type="button"
                              onClick={() => handleUpdateSessionControl({ tier: '' })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span>Default Tier</span>
                              {!selectedSessionMetadata?.tier && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                            {availableTiers.map((t) => (
                              <button
                                key={t.id}
                                type="button"
                                onClick={() => handleUpdateSessionControl({ tier: t.id })}
                                className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                              >
                                <span>{t.label}</span>
                                {selectedSessionMetadata?.tier === t.id && <Check className="w-3 h-3 text-sky-400" />}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {/* Mode Selector Badge & Popover */}
                    {(selectedSessionMetadata?.mode || availableModes.length > 0) && (
                      <div className="relative inline-block">
                        <button
                          type="button"
                          onClick={() => setEditingSessionControl((prev) => (prev === 'mode' ? null : 'mode'))}
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                          title="Click to change execution mode"
                        >
                          <Sliders className="w-3 h-3 text-amber-400 shrink-0" />
                          <span>{selectedSessionMetadata?.mode || 'mode'}</span>
                          <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                        </button>
                        {editingSessionControl === 'mode' && (
                          <div className="absolute left-0 top-full mt-1.5 z-40 w-48 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                            <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                              Execution Mode
                            </div>
                            <button
                              type="button"
                              onClick={() => handleUpdateSessionControl({ mode: '' })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span>Default Mode</span>
                              {!selectedSessionMetadata?.mode && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                            {availableModes.map((m) => (
                              <button
                                key={m.id}
                                type="button"
                                onClick={() => handleUpdateSessionControl({ mode: m.id })}
                                className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                              >
                                <span>{m.label}</span>
                                {selectedSessionMetadata?.mode === m.id && <Check className="w-3 h-3 text-sky-400" />}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {selectedSessionMetadata?.cumulativeUsage?.totalTokens ? (
                      <span
                        className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-sky-500/10 text-sky-300 border border-sky-500/30"
                        title={`In: ${selectedSessionMetadata.cumulativeUsage.inputTokens || 0} | Out: ${selectedSessionMetadata.cumulativeUsage.outputTokens || 0} | Think: ${selectedSessionMetadata.cumulativeUsage.thinkingTokens || 0}`}
                      >
                        <Zap className="w-3 h-3 text-sky-400 shrink-0" />
                        <span>
                          {selectedSessionMetadata.cumulativeUsage.totalTokens.toLocaleString()} tokens
                          {selectedSessionMetadata.cumulativeUsage.totalDurationSeconds
                            ? ` (${selectedSessionMetadata.cumulativeUsage.totalDurationSeconds.toFixed(1)}s)`
                            : ''}
                        </span>
                      </span>
                    ) : null}
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
                    let usage: {
                      inputTokens?: number;
                      outputTokens?: number;
                      thinkingTokens?: number;
                      totalTokens?: number;
                      durationSeconds?: number;
                    } | null = null;
                    if (ev.structuredPayloadJson) {
                      try {
                        const parsed = JSON.parse(ev.structuredPayloadJson);
                        if (parsed.usage) usage = parsed.usage;
                      } catch {}
                    }

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
                          <div className="flex items-center gap-2.5">
                            {usage && (
                              <span
                                className="text-[11px] font-mono px-2 py-0.5 rounded bg-sky-500/10 text-sky-300 border border-sky-500/25"
                                title={`In: ${usage.inputTokens || 0} | Out: ${usage.outputTokens || 0} | Think: ${usage.thinkingTokens || 0}`}
                              >
                                ⚡ {(usage.totalTokens || ((usage.inputTokens || 0) + (usage.outputTokens || 0))).toLocaleString()} tokens
                                {usage.durationSeconds ? ` (${usage.durationSeconds.toFixed(1)}s)` : ''}
                              </span>
                            )}
                            <span className="text-slate-500">
                              {new Date(ev.createdAt).toLocaleTimeString()}
                            </span>
                          </div>
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

      {/* Interactive Folder Picker Modal */}
      {showFolderPicker && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label="Select Working Directory"
          className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-center justify-center p-4"
        >
          <div className="max-w-2xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl flex flex-col max-h-[85vh] overflow-hidden">
            {/* Header */}
            <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
              <div className="flex items-center gap-2.5">
                <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
                  <FolderOpen className="w-4 h-4" />
                </div>
                <div>
                  <h3 className="text-sm font-bold text-white">Select Working Directory</h3>
                  <p className="text-xs text-slate-400">Browse host filesystem for isolated session execution</p>
                </div>
              </div>
              <button
                type="button"
                onClick={() => setShowFolderPicker(false)}
                className="p-1 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            {/* Navigation & Breadcrumbs Bar */}
            <div className="px-5 py-2.5 bg-slate-950/60 border-b border-slate-800/80 flex items-center gap-2 text-xs">
              <button
                type="button"
                disabled={!browseData?.parentPath || isBrowsing}
                onClick={() => browseData?.parentPath && loadBrowseDirectory(browseData.parentPath)}
                className="p-1.5 rounded-md bg-slate-800 hover:bg-slate-700 text-slate-300 disabled:opacity-40 disabled:cursor-not-allowed transition cursor-pointer"
                title="Go up to parent folder"
              >
                <ChevronUp className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                disabled={isBrowsing}
                onClick={() => capabilities?.workspace.userHome && loadBrowseDirectory(capabilities.workspace.userHome)}
                className="p-1.5 rounded-md bg-slate-800 hover:bg-slate-700 text-slate-300 disabled:opacity-40 transition cursor-pointer"
                title="Go to User Home"
              >
                <Home className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                disabled={isBrowsing}
                onClick={() => loadBrowseDirectory(browseData?.currentPath)}
                className="p-1.5 rounded-md bg-slate-800 hover:bg-slate-700 text-slate-300 disabled:opacity-40 transition cursor-pointer"
                title="Refresh current folder"
              >
                <RefreshCw className={`w-3.5 h-3.5 ${isBrowsing ? 'animate-spin' : ''}`} />
              </button>

              <div className="h-4 w-px bg-slate-800 mx-1" />

              {/* Breadcrumb Path */}
              <div className="flex-1 min-w-0 flex items-center gap-1 overflow-x-auto font-mono text-[11px] text-slate-300 py-0.5">
                {browseData?.currentPath ? (
                  browseData.currentPath
                    .split('/')
                    .map((segment, idx, arr) => {
                      if (!segment && idx === 0) {
                        return (
                          <button
                            key="root"
                            type="button"
                            onClick={() => loadBrowseDirectory('/')}
                            className="hover:text-sky-400 font-bold px-1 rounded hover:bg-slate-800/50 cursor-pointer"
                          >
                            /
                          </button>
                        );
                      }
                      if (!segment) return null;
                      const subPath = arr.slice(0, idx + 1).join('/') || '/';
                      const isLast = idx === arr.length - 1;
                      return (
                        <div key={subPath} className="flex items-center gap-1 shrink-0">
                          <span className="text-slate-600">/</span>
                          <button
                            type="button"
                            onClick={() => loadBrowseDirectory(subPath)}
                            className={`px-1 py-0.5 rounded transition cursor-pointer ${
                              isLast
                                ? 'text-sky-400 font-bold bg-sky-950/40 border border-sky-500/30'
                                : 'hover:text-white hover:bg-slate-800/60 text-slate-300'
                            }`}
                          >
                            {segment}
                          </button>
                        </div>
                      );
                    })
                ) : (
                  <span className="text-slate-500 italic">Reading directory...</span>
                )}
              </div>
            </div>

            {/* Quick Preset Location Chips in Picker */}
            {capabilities?.workspace?.bookmarks && (
              <div className="px-5 py-2 bg-slate-900/50 border-b border-slate-800/50 flex flex-wrap items-center gap-1.5 text-[11px]">
                <span className="text-slate-500 text-[10px] uppercase font-mono tracking-wider mr-1">Bookmarks:</span>
                <button
                  type="button"
                  onClick={() => {
                    setNewSessionWorkDir('');
                    setShowFolderPicker(false);
                  }}
                  className="px-2 py-0.5 rounded bg-emerald-500/10 text-emerald-300 border border-emerald-500/30 hover:bg-emerald-500/20 transition cursor-pointer"
                >
                  ⚡ Sandbox Default
                </button>
                {capabilities.workspace.bookmarks
                  .filter((b) => b.path)
                  .map((bookmark) => (
                    <button
                      key={bookmark.label}
                      type="button"
                      onClick={() => loadBrowseDirectory(bookmark.path)}
                      className={`px-2 py-0.5 rounded border transition cursor-pointer ${
                        browseData?.currentPath === bookmark.path
                          ? 'bg-sky-500/20 text-sky-300 border-sky-500/40 font-semibold'
                          : 'bg-slate-800 text-slate-300 border-slate-700 hover:bg-slate-700'
                      }`}
                    >
                      {bookmark.label}
                    </button>
                  ))}
              </div>
            )}

            {/* Directory Listing Body */}
            <div className="flex-1 overflow-y-auto p-4 space-y-1">
              {isBrowsing ? (
                <div className="py-12 flex flex-col items-center justify-center text-slate-500 text-xs gap-2">
                  <RefreshCw className="w-5 h-5 animate-spin text-sky-400" />
                  <span>Loading directory contents...</span>
                </div>
              ) : browseError ? (
                <div className="p-4 rounded-xl bg-rose-950/30 border border-rose-500/30 text-rose-300 text-xs flex items-center justify-between">
                  <span>{browseError}</span>
                  <button
                    type="button"
                    onClick={() => loadBrowseDirectory(capabilities?.workspace.currentDirectory)}
                    className="px-2.5 py-1 rounded bg-rose-900/50 hover:bg-rose-900 text-rose-200 border border-rose-500/40 text-[11px] cursor-pointer"
                  >
                    Return to Project
                  </button>
                </div>
              ) : browseData?.directories.length === 0 ? (
                <div className="py-10 text-center text-slate-500 text-xs italic">
                  No accessible subdirectories in this folder
                </div>
              ) : (
                browseData?.directories.map((dir) => (
                  <div
                    key={dir.path}
                    className="group flex items-center justify-between p-2 rounded-lg hover:bg-slate-800/60 border border-transparent hover:border-slate-800 transition"
                  >
                    <button
                      type="button"
                      onClick={() => loadBrowseDirectory(dir.path)}
                      className="flex-1 flex items-center gap-2.5 text-left text-xs text-slate-200 group-hover:text-sky-300 font-mono transition cursor-pointer"
                    >
                      <Folder className="w-4 h-4 text-sky-400 shrink-0" />
                      <span className="truncate">{dir.name}</span>
                    </button>
                    <button
                      type="button"
                      onClick={() => {
                        setNewSessionWorkDir(dir.path);
                        setShowFolderPicker(false);
                      }}
                      className="px-2.5 py-1 rounded bg-slate-800 hover:bg-sky-600 text-slate-300 hover:text-white text-[11px] font-medium opacity-80 group-hover:opacity-100 transition cursor-pointer"
                    >
                      Select
                    </button>
                  </div>
                ))
              )}
            </div>

            {/* Footer */}
            <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-between shrink-0">
              <div className="min-w-0 pr-3">
                <span className="text-[10px] text-slate-500 uppercase font-mono block">Current Folder</span>
                <p className="text-xs font-mono text-slate-300 truncate" title={browseData?.currentPath}>
                  {browseData?.currentPath || 'None'}
                </p>
              </div>
              <div className="flex items-center gap-2 shrink-0">
                <button
                  type="button"
                  onClick={() => setShowFolderPicker(false)}
                  className="px-3 py-1.5 rounded-lg text-xs font-medium text-slate-400 hover:text-white transition cursor-pointer"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  disabled={!browseData?.currentPath}
                  onClick={() => {
                    if (browseData?.currentPath) {
                      setNewSessionWorkDir(browseData.currentPath);
                      setShowFolderPicker(false);
                    }
                  }}
                  className="px-4 py-1.5 rounded-lg text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white flex items-center gap-1.5 transition disabled:opacity-40 cursor-pointer"
                >
                  <Check className="w-3.5 h-3.5" />
                  <span>Choose This Folder</span>
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Agent Quota & Usage Details Modal */}
      {showUsageModal && selectedUsageAgent && (() => {
        const usage = agentUsages[selectedUsageAgent.id];
        return (
          <div
            role="dialog"
            aria-modal="true"
            aria-label="Agent Quota and Usage Details"
            className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-center justify-center p-4"
          >
            <div className="max-w-2xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl flex flex-col max-h-[85vh] overflow-hidden">
              {/* Header */}
              <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
                <div className="flex items-center gap-2.5">
                  <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
                    <BarChart2 className="w-4 h-4" />
                  </div>
                  <div>
                    <h3 className="text-sm font-bold text-white flex items-center gap-2">
                      <span>{selectedUsageAgent.name}</span>
                      <span className="text-[10px] font-mono text-slate-400 font-normal">
                        @{selectedUsageAgent.id}
                      </span>
                    </h3>
                    <p className="text-xs text-slate-400">
                      Lifetime session token metrics & real-time provider quotas
                    </p>
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => setShowUsageModal(false)}
                  className="p-1 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>

              {/* Modal Body */}
              <div className="flex-1 overflow-y-auto p-5 space-y-5">
                {/* Lifetime Usage Cards */}
                <div>
                  <div className="flex items-center justify-between mb-2.5">
                    <span className="text-xs font-semibold text-slate-300 uppercase tracking-wider font-mono">
                      Lifetime Token Consumption
                    </span>
                    <span className="text-[11px] font-mono text-slate-400">
                      {usage?.sessionCount || 0} sessions · {(usage?.lifetimeTokens?.totalDurationSeconds || 0).toFixed(1)}s processing
                    </span>
                  </div>
                  <div className="grid grid-cols-2 sm:grid-cols-4 gap-2.5">
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Total Tokens</span>
                      <span className="text-base font-bold text-white font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.totalTokens || 0).toLocaleString()}
                      </span>
                    </div>
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Prompt / Input</span>
                      <span className="text-base font-bold text-sky-300 font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.inputTokens || 0).toLocaleString()}
                      </span>
                    </div>
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Completion / Out</span>
                      <span className="text-base font-bold text-emerald-300 font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.outputTokens || 0).toLocaleString()}
                      </span>
                    </div>
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Reasoning / Think</span>
                      <span className="text-base font-bold text-purple-300 font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.thinkingTokens || 0).toLocaleString()}
                      </span>
                    </div>
                  </div>
                </div>

                {/* Real-time Provider Quota Meters */}
                <div>
                  <div className="flex items-center justify-between mb-2.5">
                    <span className="text-xs font-semibold text-slate-300 uppercase tracking-wider font-mono">
                      Provider Quota & Rate Limits
                    </span>
                    {usage?.providerQuota && (
                      <span className="text-[11px] font-mono px-2 py-0.5 rounded bg-sky-500/10 text-sky-300 border border-sky-500/30">
                        {usage.providerQuota.provider.toUpperCase()} · {usage.providerQuota.planName}
                      </span>
                    )}
                  </div>

                  {usage?.providerQuota ? (
                    <div className="space-y-4 bg-slate-950/60 border border-slate-800 rounded-xl p-4">
                      {/* Primary Overall Meter */}
                      <div>
                        <div className="flex items-center justify-between text-xs font-mono mb-1.5">
                          <span className="text-slate-300 font-semibold">Primary Quota Availability</span>
                          <span
                            className={`font-bold ${
                              usage.providerQuota.primaryRemainingFraction > 0.4
                                ? 'text-emerald-400'
                                : usage.providerQuota.primaryRemainingFraction > 0.15
                                ? 'text-amber-400'
                                : 'text-rose-400'
                            }`}
                          >
                            {(usage.providerQuota.primaryRemainingFraction * 100).toFixed(1)}% Remaining
                          </span>
                        </div>
                        <div className="h-2.5 w-full bg-slate-800 rounded-full overflow-hidden">
                          <div
                            className={`h-full rounded-full transition-all duration-500 ${
                              usage.providerQuota.primaryRemainingFraction > 0.4
                                ? 'bg-emerald-500'
                                : usage.providerQuota.primaryRemainingFraction > 0.15
                                ? 'bg-amber-500'
                                : 'bg-rose-500'
                            }`}
                            style={{
                              width: `${Math.min(100, Math.max(0, usage.providerQuota.primaryRemainingFraction * 100))}%`
                            }}
                          />
                        </div>
                      </div>

                      {/* Quota Groups & Individual Buckets */}
                      <div className="space-y-3 pt-2 border-t border-slate-800/80">
                        {usage.providerQuota.groups.map((group) => (
                          <div key={group.name} className="space-y-2">
                            <div className="text-[11px] font-mono text-slate-400 font-medium">
                              {group.name}
                            </div>
                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                              {group.buckets.map((bucket) => {
                                const pct = Math.round(bucket.remainingFraction * 100);
                                return (
                                  <div
                                    key={bucket.id}
                                    className="bg-slate-900/90 border border-slate-800 rounded-lg p-2.5 space-y-1.5"
                                  >
                                    <div className="flex items-center justify-between text-xs font-mono">
                                      <span className="font-semibold text-slate-200 truncate" title={bucket.id}>
                                        {bucket.name || bucket.id}
                                      </span>
                                      {bucket.window && (
                                        <span className="text-[10px] px-1.5 py-0.2 rounded bg-slate-800 text-slate-400 border border-slate-700 shrink-0">
                                          {bucket.window}
                                        </span>
                                      )}
                                    </div>
                                    <div className="flex items-center justify-between text-[11px] font-mono text-slate-400">
                                      <span>Remaining</span>
                                      <span
                                        className={`font-semibold ${
                                          pct > 40
                                            ? 'text-emerald-400'
                                            : pct > 15
                                            ? 'text-amber-400'
                                            : 'text-rose-400'
                                        }`}
                                      >
                                        {(bucket.remainingFraction * 100).toFixed(1)}%
                                      </span>
                                    </div>
                                    <div className="h-1.5 w-full bg-slate-800 rounded-full overflow-hidden">
                                      <div
                                        className={`h-full rounded-full transition-all duration-300 ${
                                          pct > 40
                                            ? 'bg-emerald-500'
                                            : pct > 15
                                            ? 'bg-amber-500'
                                            : 'bg-rose-500'
                                        }`}
                                        style={{ width: `${Math.min(100, Math.max(0, pct))}%` }}
                                      />
                                    </div>
                                    {bucket.resetTime && (
                                      <div className="text-[10px] font-mono text-slate-500 flex items-center gap-1 pt-0.5 truncate">
                                        <Clock className="w-2.5 h-2.5 shrink-0" />
                                        <span className="truncate">
                                          Resets {new Date(bucket.resetTime).toLocaleDateString()} {new Date(bucket.resetTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                                        </span>
                                      </div>
                                    )}
                                  </div>
                                );
                              })}
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>
                  ) : (
                    <div className="p-4 rounded-xl bg-slate-950/60 border border-slate-800 text-slate-400 text-xs space-y-1.5">
                      <div className="flex items-center gap-2 text-slate-300 font-semibold">
                        <Cpu className="w-4 h-4 text-sky-400" />
                        <span>Local Mesh Token Tracking Active</span>
                      </div>
                      <p className="leading-relaxed">
                        Real-time provider quota introspection is supported for Antigravity CLI (<code className="text-sky-300">agy</code>) via <code className="text-sky-300">/usage</code>.
                        For this agent runtime, token metrics are aggregated from session execution events.
                      </p>
                    </div>
                  )}
                </div>
              </div>

              {/* Footer */}
              <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-end shrink-0">
                <button
                  type="button"
                  onClick={() => setShowUsageModal(false)}
                  className="px-4 py-1.5 rounded-lg text-xs font-semibold bg-slate-800 hover:bg-slate-700 text-white transition cursor-pointer"
                >
                  Close
                </button>
              </div>
            </div>
          </div>
        );
      })()}
    </div>
  );
}
