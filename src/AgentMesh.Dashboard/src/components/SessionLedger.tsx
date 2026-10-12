import React, { useState, useEffect, useRef } from 'react';
import { MessageSquare, Send, Radio, User, Bot, Layers } from 'lucide-react';
import type { AgentSession, SessionMessage, Agent } from '../types';
import { api } from '../services/api';
import * as signalR from '@microsoft/signalr';

interface SessionLedgerProps {
  connection?: signalR.HubConnection | null;
  agents: Agent[];
}

export const SessionLedger: React.FC<SessionLedgerProps> = ({ connection, agents }) => {
  const [sessions, setSessions] = useState<AgentSession[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string>('');
  const [messages, setMessages] = useState<SessionMessage[]>([]);
  const [newMessage, setNewMessage] = useState('');
  const [targetAgentId, setTargetAgentId] = useState<string>('');
  const [isSending, setIsSending] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  const loadSessions = async () => {
    try {
      const data = await api.getSessions();
      setSessions(data);
      if (data.length > 0 && !selectedSessionId) {
        setSelectedSessionId(data[0].id);
      }
    } catch (err) {
      console.error('Failed to load sessions', err);
    }
  };

  const loadMessages = async (sessionId: string) => {
    if (!sessionId) return;
    try {
      const msgs = await api.getSessionMessages(sessionId);
      setMessages(msgs);
    } catch (err) {
      console.error('Failed to load session messages', err);
    }
  };

  useEffect(() => {
    loadSessions();
  }, []);

  useEffect(() => {
    if (selectedSessionId) {
      loadMessages(selectedSessionId);
    }
  }, [selectedSessionId]);

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
    };

    const handleSessionCreated = (session: AgentSession) => {
      setSessions((prev) => [session, ...prev]);
    };

    connection.on('ReceiveMessage', handleReceiveMessage);
    connection.on('SessionCreated', handleSessionCreated);

    return () => {
      connection.invoke('LeaveSession', selectedSessionId).catch(() => {});
      connection.off('ReceiveMessage', handleReceiveMessage);
      connection.off('SessionCreated', handleSessionCreated);
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
      await api.postSessionMessage(selectedSessionId, newMessage.trim(), targetAgentId || undefined);
      setNewMessage('');
    } catch (err) {
      console.error('Failed to send message', err);
    } finally {
      setIsSending(false);
    }
  };

  const selectedSession = sessions.find((s) => s.id === selectedSessionId);

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 text-white shadow-xl flex flex-col h-[580px]">
      <div className="flex items-center justify-between pb-4 border-b border-slate-800">
        <div className="flex items-center gap-2">
          <MessageSquare className="w-5 h-5 text-sky-400" />
          <h2 className="text-xl font-bold">Inter-Agent Ledger</h2>
        </div>
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-1.5 text-xs text-slate-400">
            <Layers className="w-4 h-4 text-slate-500" />
            <span>Session:</span>
          </div>
          <select
            value={selectedSessionId}
            onChange={(e) => setSelectedSessionId(e.target.value)}
            className="bg-slate-950 border border-slate-700 text-slate-200 text-xs rounded-lg px-3 py-1.5 focus:outline-none focus:border-sky-500 max-w-[260px]"
          >
            {sessions.map((s) => (
              <option key={s.id} value={s.id}>
                {s.title} ({s.id.slice(0, 8)})
              </option>
            ))}
          </select>
        </div>
      </div>

      <div className="flex-1 overflow-y-auto py-4 space-y-3 pr-1">
        {messages.length === 0 ? (
          <div className="h-full flex flex-col items-center justify-center text-slate-500">
            <Radio className="w-8 h-8 mb-2 opacity-50" />
            <p className="text-sm">No ledger messages recorded for this session.</p>
            <p className="text-xs text-slate-600 mt-1">Broadcast an operational directive below to initiate dialogue.</p>
          </div>
        ) : (
          messages.map((m) => {
            const isOperator = m.senderAgentId === 'operator' || m.senderAgentId === 'gemini-spark';
            return (
              <div
                key={m.id}
                className={`p-3.5 rounded-lg border text-xs ${
                  isOperator
                    ? 'bg-sky-950/30 border-sky-900/50 ml-6'
                    : 'bg-slate-850/90 border-slate-800 mr-6'
                }`}
              >
                <div className="flex items-center justify-between mb-1.5 text-slate-400">
                  <div className="flex items-center gap-1.5 font-semibold">
                    {isOperator ? (
                      <User className="w-3.5 h-3.5 text-sky-400" />
                    ) : (
                      <Bot className="w-3.5 h-3.5 text-amber-400" />
                    )}
                    <span className={isOperator ? 'text-sky-300' : 'text-amber-300'}>
                      {m.senderAgentId}
                    </span>
                    {m.targetAgentId && (
                      <span className="text-slate-500 font-normal">
                        {' '}➔ @{m.targetAgentId}
                      </span>
                    )}
                  </div>
                  <span className="text-[10px] text-slate-500">
                    {new Date(m.createdAt).toLocaleTimeString()}
                  </span>
                </div>
                <div className="text-slate-200 leading-relaxed font-sans">{m.content}</div>
                {m.structuredPayloadJson && (
                  <pre className="mt-2 bg-slate-950 p-2 rounded text-[11px] font-mono text-slate-400 overflow-x-auto">
                    {m.structuredPayloadJson}
                  </pre>
                )}
              </div>
            );
          })
        )}
        <div ref={messagesEndRef} />
      </div>

      <form onSubmit={handleSendMessage} className="pt-3 border-t border-slate-800 flex gap-2">
        <select
          value={targetAgentId}
          onChange={(e) => setTargetAgentId(e.target.value)}
          className="bg-slate-950 border border-slate-700 text-slate-300 text-xs rounded-lg px-2.5 py-2 focus:outline-none focus:border-sky-500"
        >
          <option value="">Broadcast (All)</option>
          {agents.map((a) => (
            <option key={a.id} value={a.id}>
              @{a.name}
            </option>
          ))}
        </select>
        <input
          type="text"
          value={newMessage}
          onChange={(e) => setNewMessage(e.target.value)}
          placeholder={`Directive for ${selectedSession?.title || 'active session'}...`}
          className="flex-1 bg-slate-950 border border-slate-700 text-slate-100 text-xs rounded-lg px-3 py-2 focus:outline-none focus:border-sky-500"
        />
        <button
          type="submit"
          disabled={!newMessage.trim() || isSending}
          className="bg-sky-600 hover:bg-sky-500 text-white px-4 py-2 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition disabled:opacity-50"
        >
          <Send className="w-3.5 h-3.5" />
          Transmit
        </button>
      </form>
    </div>
  );
};
