import React, { useState, useEffect } from 'react';
import { Box, Play, Square, RefreshCw, X, Terminal, Cpu } from 'lucide-react';
import { api } from '../services/api';
import type { Persona, PersonaEnvironmentStatus } from '../types';

interface PersonaEnvironmentModalProps {
  persona: Persona;
  onClose: () => void;
}

export const PersonaEnvironmentModal: React.FC<PersonaEnvironmentModalProps> = ({ persona, onClose }) => {
  const [status, setStatus] = useState<PersonaEnvironmentStatus | null>(null);
  const [logs, setLogs] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [actionPending, setActionPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fetchStatusAndLogs = async () => {
    try {
      setLoading(true);
      setError(null);
      const [envStatus, envLogs] = await Promise.all([
        api.getPersonaEnvironment(persona.id).catch(() => null),
        api.getPersonaEnvironmentLogs(persona.id, 150).catch(() => ({ logs: 'No logs available.' }))
      ]);
      setStatus(envStatus);
      setLogs(envLogs.logs || 'No log output.');
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchStatusAndLogs();
    const interval = setInterval(fetchStatusAndLogs, 10000);
    return () => clearInterval(interval);
  }, [persona.id]);

  const handleWake = async () => {
    try {
      setActionPending(true);
      setError(null);
      const res = await api.wakePersonaEnvironment(persona.id);
      setStatus(res);
      await fetchStatusAndLogs();
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setActionPending(false);
    }
  };

  const handleStop = async () => {
    try {
      setActionPending(true);
      setError(null);
      const res = await api.stopPersonaEnvironment(persona.id);
      setStatus(res);
      await fetchStatusAndLogs();
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setActionPending(false);
    }
  };

  const isRunning = status?.status === 'running';
  const isStarting = status?.status === 'starting';

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-950/80 backdrop-blur-sm animate-in fade-in duration-200">
      <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-2xl max-h-[85vh] flex flex-col shadow-2xl text-slate-100 overflow-hidden">
        {/* Header */}
        <div className="p-4 border-b border-slate-800/80 flex items-center justify-between bg-slate-900/50">
          <div className="flex items-center gap-2.5">
            <div className="p-2 rounded-lg bg-sky-950/60 border border-sky-800/40 text-sky-400">
              <Box className="w-5 h-5" />
            </div>
            <div>
              <h3 className="font-semibold text-base text-white flex items-center gap-2">
                <span>{persona.name} Environment</span>
                <span className="font-mono text-xs text-slate-400 font-normal">@{persona.id}</span>
              </h3>
              <p className="text-xs text-slate-400">Isolated container dev environment</p>
            </div>
          </div>
          <button
            onClick={onClose}
            className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Content */}
        <div className="p-5 overflow-y-auto space-y-4 flex-1">
          {error && (
            <div className="p-3 rounded-lg bg-red-950/40 border border-red-500/30 text-xs text-red-300">
              {error}
            </div>
          )}

          {/* Status Bar */}
          <div className="grid grid-cols-3 gap-3 p-3.5 rounded-xl bg-slate-950/60 border border-slate-800/80">
            <div>
              <span className="text-[11px] text-slate-500 block uppercase font-mono">Status</span>
              <div className="flex items-center gap-1.5 mt-0.5">
                <span
                  className={`w-2 h-2 rounded-full ${
                    isRunning
                      ? 'bg-emerald-400 animate-pulse'
                      : isStarting
                      ? 'bg-amber-400 animate-pulse'
                      : 'bg-slate-500'
                  }`}
                />
                <span className="font-semibold text-xs capitalize text-white">
                  {status?.status || (loading ? 'Checking...' : 'Stopped')}
                </span>
              </div>
            </div>

            <div>
              <span className="text-[11px] text-slate-500 block uppercase font-mono">Runtime</span>
              <span className="text-xs font-mono font-medium text-slate-200 mt-0.5 block uppercase">
                {status?.runtime || 'docker'}
              </span>
            </div>

            <div>
              <span className="text-[11px] text-slate-500 block uppercase font-mono">Container / Pod ID</span>
              <span className="text-xs font-mono font-medium text-slate-300 mt-0.5 block truncate" title={status?.containerId || 'None'}>
                {status?.containerId || 'None'}
              </span>
            </div>
          </div>

          {/* Action Controls */}
          <div className="flex items-center justify-between gap-3 pt-1">
            <div className="flex items-center gap-2">
              {isRunning ? (
                <button
                  onClick={handleStop}
                  disabled={actionPending}
                  className="px-3.5 py-1.5 rounded-lg bg-amber-600/20 hover:bg-amber-600/30 text-amber-300 border border-amber-500/30 text-xs font-medium flex items-center gap-1.5 transition disabled:opacity-50"
                >
                  <Square className="w-3.5 h-3.5 fill-current" />
                  <span>Stop Environment</span>
                </button>
              ) : (
                <button
                  onClick={handleWake}
                  disabled={actionPending}
                  className="px-3.5 py-1.5 rounded-lg bg-emerald-600/20 hover:bg-emerald-600/30 text-emerald-300 border border-emerald-500/30 text-xs font-medium flex items-center gap-1.5 transition disabled:opacity-50"
                >
                  <Play className="w-3.5 h-3.5 fill-current" />
                  <span>Wake Environment</span>
                </button>
              )}

              <button
                onClick={fetchStatusAndLogs}
                disabled={loading || actionPending}
                className="px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-300 text-xs font-medium flex items-center gap-1.5 transition disabled:opacity-50"
              >
                <RefreshCw className={`w-3.5 h-3.5 ${loading ? 'animate-spin' : ''}`} />
                <span>Refresh</span>
              </button>
            </div>

            <div className="flex items-center gap-1.5 text-[11px] text-slate-400 font-mono">
              <Cpu className="w-3 h-3 text-slate-500" />
              <span>Volume: /workspace</span>
            </div>
          </div>

          {/* Logs */}
          <div className="space-y-1.5 pt-2">
            <div className="flex items-center justify-between text-xs text-slate-400">
              <span className="flex items-center gap-1.5 font-medium">
                <Terminal className="w-3.5 h-3.5 text-sky-400" />
                <span>Container Stdout / Stderr</span>
              </span>
              <span className="text-[11px] font-mono text-slate-500">tail: 150 lines</span>
            </div>
            <pre className="p-3 rounded-xl bg-slate-950 font-mono text-xs text-slate-300 border border-slate-800/80 overflow-x-auto max-h-64 whitespace-pre-wrap leading-relaxed select-text">
              {logs || 'No log messages output by runner container.'}
            </pre>
          </div>
        </div>

        {/* Footer */}
        <div className="p-3 border-t border-slate-800/80 flex items-center justify-between text-[11px] text-slate-500 bg-slate-900/40">
          <span>Auto-stops after 30 minutes of inactivity.</span>
          <button
            onClick={onClose}
            className="px-3 py-1 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 text-xs transition"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
};
