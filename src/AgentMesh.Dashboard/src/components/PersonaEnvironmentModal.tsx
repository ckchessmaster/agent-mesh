import React, { useState, useEffect, useRef } from 'react';
import {
  Box,
  Play,
  Square,
  RefreshCw,
  X,
  Terminal as TerminalIcon,
  Cpu,
  AlertTriangle,
  Layers
} from 'lucide-react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import * as signalR from '@microsoft/signalr';
import '@xterm/xterm/css/xterm.css';
import { api } from '../services/api';
import type {
  Persona,
  PersonaEnvironmentStatus
} from '../types';

interface PersonaEnvironmentModalProps {
  persona: Persona;
  onClose: () => void;
}

export const PersonaEnvironmentModal: React.FC<PersonaEnvironmentModalProps> = ({ persona, onClose }) => {
  const [activeTab, setActiveTab] = useState<'overview' | 'terminal'>('overview');
  const [status, setStatus] = useState<PersonaEnvironmentStatus | null>(null);
  const [logs, setLogs] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [actionPending, setActionPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Web Terminal state & refs
  const terminalContainerRef = useRef<HTMLDivElement | null>(null);
  const xtermRef = useRef<Terminal | null>(null);
  const fitAddonRef = useRef<FitAddon | null>(null);
  const hubConnectionRef = useRef<signalR.HubConnection | null>(null);
  const [terminalConnected, setTerminalConnected] = useState(false);

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

  const handleRestart = async () => {
    try {
      setActionPending(true);
      setError(null);
      const res = await api.restartPersonaEnvironment(persona.id);
      setStatus(res);
      await fetchStatusAndLogs();
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setActionPending(false);
    }
  };

  // Setup Web Terminal when terminal tab is active
  useEffect(() => {
    if (activeTab !== 'terminal') {
      if (hubConnectionRef.current) {
        hubConnectionRef.current.stop();
        hubConnectionRef.current = null;
      }
      if (xtermRef.current) {
        xtermRef.current.dispose();
        xtermRef.current = null;
      }
      setTerminalConnected(false);
      return;
    }

    if (!terminalContainerRef.current) return;

    const term = new Terminal({
      cursorBlink: true,
      fontSize: 13,
      fontFamily: 'Menlo, Monaco, "Courier New", monospace',
      theme: {
        background: '#020617', // slate-950
        foreground: '#e2e8f0', // slate-200
        cursor: '#38bdf8'     // sky-400
      }
    });

    const fitAddon = new FitAddon();
    term.loadAddon(fitAddon);
    term.open(terminalContainerRef.current);
    fitAddon.fit();
    term.focus();
    setTimeout(() => term.focus(), 80);

    xtermRef.current = term;
    fitAddonRef.current = fitAddon;

    const hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/terminal')
      .withAutomaticReconnect()
      .build();

    hub.on('Output', (data: string) => {
      term.write(data);
    });

    hub.on('Closed', () => {
      term.writeln('\r\n\x1b[33mTerminal connection closed.\x1b[0m');
      setTerminalConnected(false);
    });

    hub.start().then(async () => {
      setTerminalConnected(true);
      await hub.invoke('Attach', persona.id, term.cols, term.rows);
      term.focus();
    }).catch(err => {
      term.writeln(`\r\n\x1b[31mFailed to connect to terminal hub: ${err}\x1b[0m\r\n`);
    });

    const onDataDisposable = term.onData(data => {
      if (hub.state === signalR.HubConnectionState.Connected) {
        hub.invoke('Input', data).catch(err => {
          console.warn('Terminal input dispatch error:', err);
        });
      }
    });

    const handleResize = () => {
      if (fitAddonRef.current && xtermRef.current && hub.state === signalR.HubConnectionState.Connected) {
        fitAddonRef.current.fit();
        hub.invoke('Resize', xtermRef.current.cols, xtermRef.current.rows);
      }
    };

    window.addEventListener('resize', handleResize);
    hubConnectionRef.current = hub;

    return () => {
      onDataDisposable.dispose();
      window.removeEventListener('resize', handleResize);
      if (hub) hub.stop();
      if (term) term.dispose();
    };
  }, [activeTab, persona.id]);

  const isRunning = status?.status === 'running';
  const isStarting = status?.status === 'starting';

  return (
    <div className="fixed inset-0 z-50 flex items-end sm:items-center justify-center p-0 sm:p-4 bg-slate-950/80 backdrop-blur-sm animate-in fade-in duration-200">
      <div className="bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl w-full max-w-3xl max-h-[92dvh] sm:max-h-[88vh] flex flex-col shadow-2xl text-slate-100 overflow-hidden">
        {/* Header */}
        <div className="p-4 border-b border-slate-800/80 flex items-center justify-between bg-slate-900 shrink-0 sticky top-0 z-20">
          <div className="flex items-center gap-2.5">
            <div className="p-2 rounded-lg bg-sky-950/60 border border-sky-800/40 text-sky-400">
              <Box className="w-5 h-5" />
            </div>
            <div>
              <h3 className="font-semibold text-base text-white flex items-center gap-2">
                <span>{persona.name} Environment</span>
                <span className="font-mono text-xs text-slate-400 font-normal">@{persona.id}</span>
              </h3>
              <p className="text-xs text-slate-400">Dedicated persona container &amp; interactive terminal</p>
            </div>
          </div>
          <button
            onClick={onClose}
            className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Tab Navigation */}
        <div className="flex items-center gap-1 px-4 pt-2 border-b border-slate-800 bg-slate-950/90 backdrop-blur text-xs shrink-0 sticky top-[65px] z-10 overflow-x-auto whitespace-nowrap">
          <button
            onClick={() => setActiveTab('overview')}
            className={`px-3 py-2 border-b-2 font-medium flex items-center gap-1.5 transition shrink-0 ${
              activeTab === 'overview'
                ? 'border-sky-500 text-sky-400 font-semibold'
                : 'border-transparent text-slate-400 hover:text-slate-200'
            }`}
          >
            <Layers className="w-3.5 h-3.5" />
            <span>Overview &amp; Logs</span>
          </button>

          <button
            onClick={() => setActiveTab('terminal')}
            className={`px-3 py-2 border-b-2 font-medium flex items-center gap-1.5 transition shrink-0 ${
              activeTab === 'terminal'
                ? 'border-sky-500 text-sky-400 font-semibold'
                : 'border-transparent text-slate-400 hover:text-slate-200'
            }`}
          >
            <TerminalIcon className="w-3.5 h-3.5" />
            <span>Interactive Web Terminal</span>
            {terminalConnected && <span className="w-2 h-2 rounded-full bg-emerald-400 animate-pulse" />}
          </button>
        </div>

        {/* Content */}
        <div className="p-5 overflow-y-auto space-y-4 flex-1">
          {(error || status?.errorMessage) && (
            <div className="p-3 rounded-lg bg-red-950/40 border border-red-500/30 text-xs text-red-300 leading-relaxed font-mono whitespace-pre-wrap break-all">
              {error || status?.errorMessage}
            </div>
          )}

          {/* TAB 1: OVERVIEW */}
          {activeTab === 'overview' && (
            <div className="space-y-4">
              {status?.updateAvailable && (
                <div className="p-3.5 rounded-xl bg-amber-950/40 border border-amber-500/30 text-xs text-amber-200 flex items-center justify-between gap-3">
                  <div className="flex items-center gap-2.5">
                    <AlertTriangle className="w-4 h-4 text-amber-400 shrink-0" />
                    <div className="space-y-0.5">
                      <div className="font-semibold text-amber-300">Runner Image Update Available</div>
                      <div className="text-[11px] text-slate-300">
                        Running: <code className="text-amber-300 font-mono bg-amber-950/60 px-1 py-0.5 rounded">{status.image || 'unknown'}</code>
                        <span className="mx-1.5">➔</span>
                        Target: <code className="text-emerald-300 font-mono bg-emerald-950/60 px-1 py-0.5 rounded">{status.configuredImage}</code>
                      </div>
                    </div>
                  </div>
                  <button
                    onClick={handleRestart}
                    disabled={actionPending}
                    className="px-3 py-1.5 rounded-lg bg-amber-500/20 hover:bg-amber-500/30 text-amber-300 border border-amber-500/40 text-xs font-semibold whitespace-nowrap flex items-center gap-1.5 transition disabled:opacity-50"
                  >
                    <RefreshCw className={`w-3.5 h-3.5 ${actionPending ? 'animate-spin' : ''}`} />
                    <span>Restart &amp; Update</span>
                  </button>
                </div>
              )}

              {/* Status Bar */}
              <div className="grid grid-cols-2 md:grid-cols-4 gap-3 p-3.5 rounded-xl bg-slate-950/60 border border-slate-800/80">
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
                  <span className="text-[11px] text-slate-500 block uppercase font-mono">Image</span>
                  <div className="flex items-center gap-1.5 mt-0.5">
                    <span className="text-xs font-mono font-medium text-slate-300 truncate max-w-[150px]" title={status?.image || status?.configuredImage || 'default'}>
                      {status?.image || status?.configuredImage || 'default'}
                    </span>
                    {status?.updateAvailable && (
                      <span className="px-1.5 py-0.2 rounded text-[10px] font-semibold bg-amber-500/20 text-amber-300 border border-amber-500/30 shrink-0">
                        update
                      </span>
                    )}
                  </div>
                </div>

                <div>
                  <span className="text-[11px] text-slate-500 block uppercase font-mono">Container / Pod ID</span>
                  <span className="text-xs font-mono font-medium text-slate-300 mt-0.5 block truncate" title={status?.containerId || 'None'}>
                    {status?.containerId || 'None'}
                  </span>
                </div>
              </div>

              {/* Action Controls */}
              <div className="flex flex-wrap items-center justify-between gap-3 pt-1">
                <div className="flex flex-wrap items-center gap-2">
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
                    onClick={handleRestart}
                    disabled={actionPending}
                    title="Restart container/deployment with the latest configured image"
                    className="px-3.5 py-1.5 rounded-lg bg-sky-600/20 hover:bg-sky-600/30 text-sky-300 border border-sky-500/30 text-xs font-medium flex items-center gap-1.5 transition disabled:opacity-50"
                  >
                    <RefreshCw className={`w-3.5 h-3.5 ${actionPending ? 'animate-spin' : ''}`} />
                    <span>{status?.updateAvailable ? 'Restart & Update' : 'Restart Environment'}</span>
                  </button>

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
                  <span>Volumes: /workspace &amp; /root</span>
                </div>
              </div>

              {/* Logs */}
              <div className="space-y-1.5 pt-2">
                <div className="flex items-center justify-between text-xs text-slate-400">
                  <span className="flex items-center gap-1.5 font-medium">
                    <TerminalIcon className="w-3.5 h-3.5 text-sky-400" />
                    <span>Container Stdout / Stderr</span>
                  </span>
                  <span className="text-[11px] font-mono text-slate-500">tail: 150 lines</span>
                </div>
                <pre className="p-3 rounded-xl bg-slate-950 font-mono text-xs text-slate-300 border border-slate-800/80 overflow-x-auto max-h-64 whitespace-pre-wrap leading-relaxed select-text">
                  {logs || 'No log messages output by runner container.'}
                </pre>
              </div>
            </div>
          )}

          {/* TAB 2: INTERACTIVE WEB TERMINAL */}
          {activeTab === 'terminal' && (
            <div className="space-y-3">
              <div className="flex items-center justify-between text-xs text-slate-400">
                <span className="flex items-center gap-2">
                  <TerminalIcon className="w-3.5 h-3.5 text-sky-400" />
                  <span>VT100 PTY Session (bash inside container)</span>
                </span>
                <span className="text-[11px] font-mono text-slate-500">
                  {terminalConnected ? 'Connected via SignalR' : 'Connecting...'}
                </span>
              </div>
              <div
                ref={terminalContainerRef}
                onClick={() => xtermRef.current?.focus()}
                className="p-3 rounded-xl bg-slate-950 border border-slate-800/80 h-80 overflow-hidden cursor-text"
              />
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="p-3 border-t border-slate-800/80 flex items-center justify-between text-[11px] text-slate-500 bg-slate-900/40">
          <span>Driver authentication &amp; model discovery are managed in Driver Configuration.</span>
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
