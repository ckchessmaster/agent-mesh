import React, { useState, useEffect, useRef, useCallback } from 'react';
import {
  MessageSquare,
  Send,
  Radio,
  User,
  Bot,
  Plus,
  Search,
  CheckCircle2,
  Clock,
  Sparkles,
  Terminal,
  RefreshCw,
  Archive
} from 'lucide-react';
import type { AgentSession, SessionMessage, Agent } from '../types';
import { api } from '../services/api';
import type * as signalR from '@microsoft/signalr';
import { MarkdownMessage } from './MarkdownMessage';

interface SessionLedgerProps {
  connection?: signalR.HubConnection | null;
  agents?: Agent[];
}

export const SessionLedger: React.FC<SessionLedgerProps> = ({ connection, agents = [] }) => {
  const [sessions, setSessions] = useState<AgentSession[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string>('');
  const [messages, setMessages] = useState<SessionMessage[]>([]);
  const [isLoadingSessions, setIsLoadingSessions] = useState(false);
  const [isLoadingMessages, setIsLoadingMessages] = useState(false);
  const [statusFilter, setStatusFilter] = useState<'all' | 'active' | 'completed'>('all');
  const [searchQuery, setSearchQuery] = useState('');
  const [newMessage, setNewMessage] = useState('');
  const [targetAgentId, setTargetAgentId] = useState<string>('');
  const [isSending, setIsSending] = useState(false);

  // New session modal / form state
  const [showNewModal, setShowNewModal] = useState(false);
  const [newTitle, setNewTitle] = useState('');
  const [newInitialMsg, setNewInitialMsg] = useState('');
  const [isCreatingSession, setIsCreatingSession] = useState(false);

  const messagesEndRef = useRef<HTMLDivElement>(null);

  const loadSessions = useCallback(async () => {
    setIsLoadingSessions(true);
    try {
      const data = await api.getSessions(statusFilter);
      setSessions(data);
      if (data.length > 0 && (!selectedSessionId || !data.some((s) => s.id === selectedSessionId))) {
        setSelectedSessionId(data[0].id);
      }
    } catch (err) {
      console.error('Failed to load sessions', err);
    } finally {
      setIsLoadingSessions(false);
    }
  }, [statusFilter, selectedSessionId]);

  const loadMessages = useCallback(async (sessionId: string) => {
    if (!sessionId) return;
    setIsLoadingMessages(true);
    try {
      const msgs = await api.getSessionMessages(sessionId);
      setMessages(msgs);
    } catch (err) {
      console.error('Failed to load session messages', err);
    } finally {
      setIsLoadingMessages(false);
    }
  }, []);

  useEffect(() => {
    loadSessions();
  }, [loadSessions]);

  useEffect(() => {
    if (selectedSessionId) {
      loadMessages(selectedSessionId);
    } else {
      setMessages([]);
    }
  }, [selectedSessionId, loadMessages]);

  // SignalR live updates
  useEffect(() => {
    if (!connection || !selectedSessionId) return;

    connection.invoke('JoinSession', selectedSessionId).catch((err) =>
      console.warn('Could not join SignalR group for session:', err)
    );

    const handleReceiveMessage = (msg: SessionMessage) => {
      if (msg.sessionId === selectedSessionId) {
        setMessages((prev) => {
          if (prev.some((m) => m.id === msg.id)) return prev;
          return [...prev, msg];
        });
      }
      // Update message count in session list
      setSessions((prev) =>
        prev.map((s) =>
          s.id === msg.sessionId ? { ...s, messageCount: (s.messageCount || 0) + 1, updatedAt: msg.createdAt } : s
        )
      );
    };

    const handleSessionCreated = (session: AgentSession) => {
      setSessions((prev) => [session, ...prev]);
    };

    const handleSessionUpdated = (session: AgentSession) => {
      setSessions((prev) => prev.map((s) => (s.id === session.id ? session : s)));
    };

    connection.on('ReceiveMessage', handleReceiveMessage);
    connection.on('SessionCreated', handleSessionCreated);
    connection.on('SessionUpdated', handleSessionUpdated);

    return () => {
      connection.invoke('LeaveSession', selectedSessionId).catch(() => {});
      connection.off('ReceiveMessage', handleReceiveMessage);
      connection.off('SessionCreated', handleSessionCreated);
      connection.off('SessionUpdated', handleSessionUpdated);
    };
  }, [connection, selectedSessionId]);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const handleSendMessage = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newMessage.trim() || !selectedSessionId || isSending) return;

    setIsSending(true);
    try {
      const sent = await api.postSessionMessage(
        selectedSessionId,
        newMessage.trim(),
        targetAgentId || undefined,
        'operator'
      );
      setMessages((prev) => {
        if (prev.some((m) => m.id === sent.id)) return prev;
        return [...prev, sent];
      });
      setNewMessage('');
    } catch (err) {
      console.error('Failed to send message', err);
    } finally {
      setIsSending(false);
    }
  };

  const handleCreateSession = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newTitle.trim() || isCreatingSession) return;

    setIsCreatingSession(true);
    try {
      const created = await api.createSession(newTitle.trim(), '{}', newInitialMsg.trim() || undefined);
      setSessions((prev) => [created, ...prev]);
      setSelectedSessionId(created.id);
      setShowNewModal(false);
      setNewTitle('');
      setNewInitialMsg('');
    } catch (err) {
      console.error('Failed to create session', err);
    } finally {
      setIsCreatingSession(false);
    }
  };

  const handleToggleStatus = async (sessionId: string, currentStatus: string) => {
    const nextStatus = currentStatus === 'active' ? 'completed' : 'active';
    try {
      const updated = await api.updateSessionStatus(sessionId, nextStatus);
      setSessions((prev) => prev.map((s) => (s.id === sessionId ? updated : s)));
    } catch (err) {
      console.error('Failed to update session status', err);
    }
  };

  const filteredSessions = sessions.filter((s) => {
    const matchesFilter = statusFilter === 'all' || s.status.toLowerCase() === statusFilter;
    const matchesSearch =
      s.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
      s.id.toLowerCase().includes(searchQuery.toLowerCase()) ||
      (s.creatorAgentId && s.creatorAgentId.toLowerCase().includes(searchQuery.toLowerCase()));
    return matchesFilter && matchesSearch;
  });

  const selectedSession = sessions.find((s) => s.id === selectedSessionId);

  return (
    <div className="bg-slate-900/90 border border-slate-800/90 rounded-2xl shadow-2xl overflow-hidden flex flex-col h-[750px] font-sans">
      {/* Container split: Left list & Right transcript */}
      <div className="flex-1 flex flex-col md:flex-row overflow-hidden">
        {/* Left Sidebar: Session List */}
        <aside className="w-full md:w-80 lg:w-96 border-r border-slate-800/80 bg-slate-950/60 flex flex-col shrink-0">
          {/* Header & New Session Button */}
          <div className="p-4 border-b border-slate-800/80 space-y-3">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <MessageSquare className="w-5 h-5 text-sky-400" />
                <h3 className="font-bold text-white text-base">Sessions</h3>
                <span className="text-xs bg-slate-800 text-sky-300 font-mono px-2 py-0.5 rounded-full border border-slate-700">
                  {sessions.length}
                </span>
              </div>
              <button
                onClick={() => setShowNewModal(true)}
                className="bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold px-2.5 py-1.5 rounded-lg flex items-center gap-1 transition shadow-sm"
              >
                <Plus className="w-3.5 h-3.5" />
                New
              </button>
            </div>

            {/* Search Input */}
            <div className="relative">
              <Search className="w-3.5 h-3.5 text-slate-500 absolute left-3 top-2.5" />
              <input
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Search sessions or agents..."
                className="w-full bg-slate-900 border border-slate-800 text-slate-200 text-xs rounded-lg pl-8 pr-3 py-1.5 focus:outline-none focus:border-sky-500 placeholder:text-slate-500"
              />
            </div>

            {/* Filter Pills */}
            <div className="flex items-center gap-1 bg-slate-900/80 p-1 rounded-lg border border-slate-800">
              {(['all', 'active', 'completed'] as const).map((filter) => (
                <button
                  key={filter}
                  onClick={() => setStatusFilter(filter)}
                  className={`flex-1 text-xs py-1 rounded-md capitalize font-medium transition ${
                    statusFilter === filter
                      ? 'bg-sky-600/30 text-sky-300 border border-sky-500/30 shadow-sm'
                      : 'text-slate-400 hover:text-slate-200'
                  }`}
                >
                  {filter}
                </button>
              ))}
            </div>
          </div>

          {/* Session Cards List */}
          <div className="flex-1 overflow-y-auto divide-y divide-slate-800/40">
            {isLoadingSessions && sessions.length === 0 ? (
              <div className="p-8 text-center text-slate-500 text-xs flex flex-col items-center gap-2">
                <RefreshCw className="w-4 h-4 animate-spin text-sky-400" />
                Loading sessions...
              </div>
            ) : filteredSessions.length === 0 ? (
              <div className="p-8 text-center text-slate-500 text-xs">
                No sessions found matching filters.
              </div>
            ) : (
              filteredSessions.map((s) => {
                const isSelected = s.id === selectedSessionId;
                const isActive = s.status === 'active';
                const isAgy = s.creatorAgentId?.toLowerCase() === 'agy';

                return (
                  <button
                    key={s.id}
                    onClick={() => setSelectedSessionId(s.id)}
                    className={`w-full text-left p-3.5 transition flex flex-col gap-1.5 ${
                      isSelected
                        ? 'bg-sky-950/40 border-l-4 border-l-sky-500 border-b border-b-sky-900/30'
                        : 'hover:bg-slate-900/50'
                    }`}
                  >
                    <div className="flex items-start justify-between gap-2">
                      <span className="font-semibold text-xs text-slate-200 line-clamp-1">
                        {s.title}
                      </span>
                      <span
                        className={`text-[10px] font-mono uppercase px-1.5 py-0.5 rounded border shrink-0 ${
                          isActive
                            ? 'bg-emerald-950/60 text-emerald-400 border-emerald-500/30'
                            : 'bg-slate-800 text-slate-400 border-slate-700'
                        }`}
                      >
                        {s.status}
                      </span>
                    </div>

                    <div className="flex items-center justify-between text-[11px] text-slate-400">
                      <div className="flex items-center gap-1.5">
                        {isAgy ? (
                          <span className="flex items-center gap-1 text-purple-300 font-mono text-[10px] bg-purple-950/60 px-1.5 py-0.5 rounded border border-purple-800/40">
                            <Terminal className="w-3 h-3 text-purple-400" />
                            agy
                          </span>
                        ) : (
                          <span className="text-slate-400 font-mono text-[10px]">
                            {s.creatorAgentId || 'system'}
                          </span>
                        )}
                        <span className="text-slate-600">•</span>
                        <span className="text-[10px] text-slate-500">
                          {new Date(s.createdAt).toLocaleDateString([], { month: 'short', day: 'numeric' })}
                        </span>
                      </div>

                      {s.messageCount !== undefined && s.messageCount > 0 && (
                        <span className="text-[10px] font-mono text-slate-400 bg-slate-800/80 px-1.5 py-0.5 rounded">
                          {s.messageCount} msgs
                        </span>
                      )}
                    </div>
                  </button>
                );
              })
            )}
          </div>
        </aside>

        {/* Right Pane: Session Detail & Message Transcript */}
        <section className="flex-1 flex flex-col bg-slate-900/40 overflow-hidden">
          {selectedSession ? (
            <>
              {/* Session Detail Header */}
              <div className="p-4 border-b border-slate-800/80 bg-slate-950/40 flex items-center justify-between">
                <div>
                  <div className="flex items-center gap-2.5">
                    <h2 className="text-base font-bold text-white tracking-tight">
                      {selectedSession.title}
                    </h2>
                    <span
                      className={`text-xs px-2 py-0.5 rounded-full font-mono uppercase border ${
                        selectedSession.status === 'active'
                          ? 'bg-emerald-950/60 text-emerald-400 border-emerald-500/40'
                          : 'bg-slate-800 text-slate-400 border-slate-700'
                      }`}
                    >
                      {selectedSession.status}
                    </span>
                  </div>
                  <div className="flex items-center gap-3 text-xs text-slate-400 mt-1">
                    <span className="font-mono text-slate-500 text-[11px]">
                      ID: {selectedSession.id}
                    </span>
                    <span>•</span>
                    <span className="flex items-center gap-1 text-[11px]">
                      <Clock className="w-3 h-3 text-slate-500" />
                      Created {new Date(selectedSession.createdAt).toLocaleString()}
                    </span>
                  </div>
                </div>

                <div className="flex items-center gap-2">
                  <button
                    onClick={() => handleToggleStatus(selectedSession.id, selectedSession.status)}
                    className="text-xs px-3 py-1.5 rounded-lg border border-slate-700 bg-slate-800 hover:bg-slate-700 text-slate-300 font-medium transition flex items-center gap-1.5"
                  >
                    {selectedSession.status === 'active' ? (
                      <>
                        <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400" />
                        Mark Completed
                      </>
                    ) : (
                      <>
                        <Archive className="w-3.5 h-3.5 text-sky-400" />
                        Reopen Session
                      </>
                    )}
                  </button>
                </div>
              </div>

              {/* Message Transcript Timeline */}
              <div className="flex-1 overflow-y-auto p-5 space-y-3.5">
                {isLoadingMessages ? (
                  <div className="h-full flex items-center justify-center text-slate-500 text-xs">
                    <RefreshCw className="w-4 h-4 animate-spin mr-2 text-sky-400" />
                    Loading conversation transcript...
                  </div>
                ) : messages.length === 0 ? (
                  <div className="h-full flex flex-col items-center justify-center text-slate-500">
                    <Radio className="w-8 h-8 mb-2 opacity-40 text-slate-400" />
                    <p className="text-sm font-medium">No messages recorded in this session ledger.</p>
                    <p className="text-xs text-slate-600 mt-1">
                      CLI agents or human operators can post messages below to begin collaboration.
                    </p>
                  </div>
                ) : (
                  messages.map((m) => {
                    const isAgy = m.senderAgentId.toLowerCase() === 'agy';
                    const isOperator = m.senderAgentId === 'operator' || m.senderAgentId === 'human';

                    return (
                      <div
                        key={m.id}
                        className={`p-4 rounded-xl border text-xs shadow-sm transition ${
                          isAgy
                            ? 'bg-purple-950/30 border-purple-900/60 ml-4'
                            : isOperator
                            ? 'bg-sky-950/30 border-sky-900/60 mr-4'
                            : 'bg-slate-850 border-slate-800'
                        }`}
                      >
                        <div className="flex items-center justify-between mb-2 pb-1.5 border-b border-slate-800/60">
                          <div className="flex items-center gap-2 font-semibold">
                            {isAgy ? (
                              <div className="flex items-center gap-1 text-purple-300 bg-purple-900/40 px-2 py-0.5 rounded border border-purple-700/50">
                                <Terminal className="w-3 h-3 text-purple-400" />
                                <span>agy (Antigravity CLI)</span>
                              </div>
                            ) : isOperator ? (
                              <div className="flex items-center gap-1 text-sky-300 bg-sky-900/40 px-2 py-0.5 rounded border border-sky-700/50">
                                <User className="w-3 h-3 text-sky-400" />
                                <span>Human Operator</span>
                              </div>
                            ) : (
                              <div className="flex items-center gap-1 text-amber-300 bg-amber-900/40 px-2 py-0.5 rounded border border-amber-700/50">
                                <Bot className="w-3 h-3 text-amber-400" />
                                <span>@{m.senderAgentId}</span>
                              </div>
                            )}

                            {m.targetAgentId && (
                              <span className="text-slate-400 font-mono text-[11px]">
                                ➔ @{m.targetAgentId}
                              </span>
                            )}
                          </div>

                          <span className="text-[11px] text-slate-500 font-mono">
                            {new Date(m.createdAt).toLocaleTimeString()}
                          </span>
                        </div>

                        <div className="text-slate-200 leading-relaxed font-sans">
                          <MarkdownMessage content={m.content} />
                        </div>

                        {m.structuredPayloadJson && (
                          <div className="mt-2.5 pt-2 border-t border-slate-800/60">
                            <span className="text-[10px] uppercase font-mono tracking-wider text-slate-400 block mb-1">
                              Structured Payload
                            </span>
                            <pre className="bg-slate-950 p-2.5 rounded-lg text-[11px] font-mono text-purple-300/90 overflow-x-auto border border-slate-800">
                              {m.structuredPayloadJson}
                            </pre>
                          </div>
                        )}
                      </div>
                    );
                  })
                )}
                <div ref={messagesEndRef} />
              </div>

              {/* Message Input Footer */}
              <form
                onSubmit={handleSendMessage}
                className="p-3 border-t border-slate-800/80 bg-slate-950/60 flex items-center gap-2"
              >
                <select
                  value={targetAgentId}
                  onChange={(e) => setTargetAgentId(e.target.value)}
                  className="bg-slate-900 border border-slate-700 text-slate-300 text-xs rounded-lg px-2.5 py-2 focus:outline-none focus:border-sky-500"
                >
                  <option value="">Broadcast (All Agents)</option>
                  <option value="agy">@agy</option>
                  {agents
                    .filter((a) => a.id.toLowerCase() !== 'agy')
                    .map((a) => (
                      <option key={a.id} value={a.id}>
                        @{a.name}
                      </option>
                    ))}
                </select>

                <input
                  type="text"
                  value={newMessage}
                  onChange={(e) => setNewMessage(e.target.value)}
                  placeholder={`Post directive or message to ${selectedSession.title}...`}
                  className="flex-1 bg-slate-900 border border-slate-700 text-slate-100 text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-sky-500"
                />

                <button
                  type="submit"
                  disabled={!newMessage.trim() || isSending}
                  className="bg-sky-600 hover:bg-sky-500 text-white px-4 py-2 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition disabled:opacity-50 shrink-0"
                >
                  <Send className="w-3.5 h-3.5" />
                  Transmit
                </button>
              </form>
            </>
          ) : (
            <div className="h-full flex flex-col items-center justify-center text-slate-500">
              <Sparkles className="w-10 h-10 mb-3 text-slate-600" />
              <h4 className="text-sm font-semibold text-slate-300">No Session Selected</h4>
              <p className="text-xs text-slate-500 mt-1 max-w-sm text-center">
                Select an existing session from the sidebar or click &ldquo;New&rdquo; to launch a new agent workspace session.
              </p>
            </div>
          )}
        </section>
      </div>

      {/* New Session Modal Dialog */}
      {showNewModal && (
        <div className="fixed inset-0 bg-black/70 backdrop-blur-sm z-50 flex items-center justify-center p-4">
          <div className="bg-slate-900 border border-slate-800 rounded-2xl max-w-md w-full p-6 shadow-2xl space-y-4">
            <div className="flex items-center justify-between pb-2 border-b border-slate-800">
              <h3 className="font-bold text-base text-white flex items-center gap-2">
                <Plus className="w-4 h-4 text-sky-400" />
                Initialize New Agent Session
              </h3>
              <button
                onClick={() => setShowNewModal(false)}
                className="text-slate-400 hover:text-white text-xs font-mono"
              >
                ✕
              </button>
            </div>

            <form onSubmit={handleCreateSession} className="space-y-4">
              <div>
                <label className="block text-xs font-semibold text-slate-300 mb-1">
                  Session Title *
                </label>
                <input
                  type="text"
                  required
                  value={newTitle}
                  onChange={(e) => setNewTitle(e.target.value)}
                  placeholder="e.g. CLI Verification Session"
                  className="w-full bg-slate-950 border border-slate-700 text-slate-100 text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-semibold text-slate-300 mb-1">
                  Initial Operational Directive (Optional)
                </label>
                <textarea
                  rows={3}
                  value={newInitialMsg}
                  onChange={(e) => setNewInitialMsg(e.target.value)}
                  placeholder="e.g. Initialize workspace and verify connectivity."
                  className="w-full bg-slate-950 border border-slate-700 text-slate-100 text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-sky-500"
                />
              </div>

              <div className="flex items-center justify-end gap-2 pt-2 border-t border-slate-800">
                <button
                  type="button"
                  onClick={() => setShowNewModal(false)}
                  className="px-3 py-1.5 rounded-lg border border-slate-700 text-slate-300 text-xs hover:bg-slate-800 transition"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={!newTitle.trim() || isCreatingSession}
                  className="bg-sky-600 hover:bg-sky-500 text-white px-4 py-1.5 rounded-lg text-xs font-semibold transition disabled:opacity-50"
                >
                  {isCreatingSession ? 'Creating...' : 'Create Session'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
};
