import React, { useState, useEffect, useRef } from 'react';
import {
  Box,
  Play,
  Square,
  RefreshCw,
  X,
  Terminal as TerminalIcon,
  Cpu,
  CheckCircle2,
  AlertTriangle,
  XCircle,
  KeyRound,
  ExternalLink,
  ShieldCheck,
  Activity,
  Layers
} from 'lucide-react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import * as signalR from '@microsoft/signalr';
import '@xterm/xterm/css/xterm.css';
import { api } from '../services/api';
import type {
  Persona,
  PersonaEnvironmentStatus,
  PersonaDriverVerificationResult,
  PersonaAuthStartResult
} from '../types';

interface PersonaEnvironmentModalProps {
  persona: Persona;
  onClose: () => void;
}

export const PersonaEnvironmentModal: React.FC<PersonaEnvironmentModalProps> = ({ persona, onClose }) => {
  const [activeTab, setActiveTab] = useState<'overview' | 'verify' | 'terminal'>('overview');
  const [status, setStatus] = useState<PersonaEnvironmentStatus | null>(null);
  const [logs, setLogs] = useState<string>('');
  const [loading, setLoading] = useState(true);
  const [actionPending, setActionPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Verification state
  const [verifying, setVerifying] = useState(false);
  const [verifyResult, setVerifyResult] = useState<PersonaDriverVerificationResult | null>(null);

  // Auth state (Dual-Mode: OAuth & API Key)
  const [showAuthWizard, setShowAuthWizard] = useState(false);
  const [authMode, setAuthMode] = useState<'oauth' | 'apikey'>('oauth');
  const [authStarting, setAuthStarting] = useState(false);
  const [authSubmitting, setAuthSubmitting] = useState(false);
  const [authStartResult, setAuthStartResult] = useState<PersonaAuthStartResult | null>(null);
  const [authCodeInput, setAuthCodeInput] = useState('');
  const [apiKeyInput, setApiKeyInput] = useState('');
  const [apiKeySaving, setApiKeySaving] = useState(false);
  const [authSuccessMessage, setAuthSuccessMessage] = useState<string | null>(null);

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

  const handleRunVerification = async () => {
    try {
      setVerifying(true);
      setError(null);
      const res = await api.verifyPersonaDriver(persona.id);
      setVerifyResult(res);
      await fetchStatusAndLogs();
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setVerifying(false);
    }
  };

  const handleStartAuth = async () => {
    try {
      setAuthStarting(true);
      setError(null);
      setAuthSuccessMessage(null);
      setShowAuthWizard(true);
      const res = await api.startPersonaAuth(persona.id);
      setAuthStartResult(res);
      if (res.status === 'already_authenticated') {
        setAuthSuccessMessage('Driver is already authenticated with Google AI Pro.');
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setAuthStarting(false);
    }
  };

  const handleSubmitAuthCode = async () => {
    if (!authCodeInput.trim()) return;
    try {
      setAuthSubmitting(true);
      setError(null);
      const res = await api.submitPersonaAuthCode(persona.id, authCodeInput.trim());
      if (res.status === 'authenticated') {
        setAuthSuccessMessage(res.message || 'Successfully authenticated Google AI Pro credentials!');
        setAuthCodeInput('');
        setAuthStartResult(null);
        await handleRunVerification();
      } else {
        setError(res.errorMessage || 'Failed to submit authorization code.');
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setAuthSubmitting(false);
    }
  };

  const handleSaveApiKey = async () => {
    if (!apiKeyInput.trim()) return;
    try {
      setApiKeySaving(true);
      setError(null);
      const res = await api.configurePersonaApiKey(persona.id, apiKeyInput.trim());
      if (res.status === 'authenticated') {
        setAuthSuccessMessage(res.message || 'API key successfully configured!');
        setApiKeyInput('');
        setShowAuthWizard(false);
        await handleRunVerification();
      } else {
        setError(res.errorMessage || 'Failed to configure API key.');
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setApiKeySaving(false);
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
    }).catch(err => {
      term.writeln(`\r\n\x1b[31mFailed to connect to terminal hub: ${err}\x1b[0m\r\n`);
    });

    const onDataDisposable = term.onData(data => {
      if (hub.state === signalR.HubConnectionState.Connected) {
        hub.invoke('Input', data);
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
              <p className="text-xs text-slate-400">Dedicated persona container & driver management</p>
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
            <span>Overview & Logs</span>
          </button>

          <button
            onClick={() => {
              setActiveTab('verify');
              if (!verifyResult && !verifying) handleRunVerification();
            }}
            className={`px-3 py-2 border-b-2 font-medium flex items-center gap-1.5 transition shrink-0 ${
              activeTab === 'verify'
                ? 'border-sky-500 text-sky-400 font-semibold'
                : 'border-transparent text-slate-400 hover:text-slate-200'
            }`}
          >
            <ShieldCheck className="w-3.5 h-3.5" />
            <span>Driver Health</span>
            {verifyResult && (
              <span
                className={`w-2 h-2 rounded-full ${
                  verifyResult.overallStatus === 'passed'
                    ? 'bg-emerald-400'
                    : verifyResult.overallStatus === 'warning'
                    ? 'bg-amber-400'
                    : 'bg-red-400'
                }`}
              />
            )}
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

          {authSuccessMessage && (
            <div className="p-3 rounded-lg bg-emerald-950/40 border border-emerald-500/30 text-xs text-emerald-300 leading-relaxed flex items-center gap-2">
              <CheckCircle2 className="w-4 h-4 text-emerald-400 shrink-0" />
              <span>{authSuccessMessage}</span>
            </div>
          )}

          {/* TAB 1: OVERVIEW */}
          {activeTab === 'overview' && (
            <div className="space-y-4">
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
                  <span className="text-[11px] text-slate-500 block uppercase font-mono">Container ID</span>
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
                    onClick={() => {
                      setActiveTab('verify');
                      handleRunVerification();
                    }}
                    disabled={verifying}
                    className="px-3 py-1.5 rounded-lg bg-sky-950/60 hover:bg-sky-900/60 text-sky-300 border border-sky-800/40 text-xs font-medium flex items-center gap-1.5 transition disabled:opacity-50"
                  >
                    <Activity className={`w-3.5 h-3.5 ${verifying ? 'animate-spin' : ''}`} />
                    <span>Verify Driver</span>
                  </button>

                  <button
                    onClick={handleStartAuth}
                    disabled={authStarting}
                    className="px-3 py-1.5 rounded-lg bg-purple-950/60 hover:bg-purple-900/60 text-purple-300 border border-purple-800/40 text-xs font-medium flex items-center gap-1.5 transition disabled:opacity-50"
                  >
                    <KeyRound className="w-3.5 h-3.5" />
                    <span>Authenticate Driver</span>
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
                  <span>Volumes: /workspace & /root</span>
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

          {/* TAB 2: DRIVER HEALTH & VERIFICATION */}
          {activeTab === 'verify' && (
            <div className="space-y-4">
              <div className="flex items-center justify-between">
                <div>
                  <h4 className="font-semibold text-sm text-white">Driver Diagnostics</h4>
                  <p className="text-xs text-slate-400">Verifying CLI executable, auth tokens, mesh connectivity & dry-run execution</p>
                </div>
                <button
                  onClick={handleRunVerification}
                  disabled={verifying}
                  className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-medium flex items-center gap-1.5 transition disabled:opacity-50"
                >
                  <RefreshCw className={`w-3.5 h-3.5 ${verifying ? 'animate-spin' : ''}`} />
                  <span>{verifying ? 'Verifying...' : 'Re-run Diagnostics'}</span>
                </button>
              </div>

              {verifyResult ? (
                <div className="space-y-3">
                  <div className={`p-3 rounded-xl border flex items-center justify-between text-xs font-medium ${
                    verifyResult.overallStatus === 'passed'
                      ? 'bg-emerald-950/30 border-emerald-500/30 text-emerald-300'
                      : verifyResult.overallStatus === 'warning'
                      ? 'bg-amber-950/30 border-amber-500/30 text-amber-300'
                      : 'bg-red-950/30 border-red-500/30 text-red-300'
                  }`}>
                    <div className="flex items-center gap-2">
                      {verifyResult.overallStatus === 'passed' ? (
                        <CheckCircle2 className="w-4 h-4 text-emerald-400" />
                      ) : verifyResult.overallStatus === 'warning' ? (
                        <AlertTriangle className="w-4 h-4 text-amber-400" />
                      ) : (
                        <XCircle className="w-4 h-4 text-red-400" />
                      )}
                      <span>
                        Overall Diagnostic Result: <strong className="uppercase font-mono">{verifyResult.overallStatus}</strong>
                      </span>
                    </div>
                    <span className="text-[11px] font-mono text-slate-400">Driver: @{verifyResult.driverId}</span>
                  </div>

                  <div className="divide-y divide-slate-800/80 rounded-xl bg-slate-950/60 border border-slate-800/80 overflow-hidden">
                    {verifyResult.steps.map((step, idx) => (
                      <div key={idx} className="p-3 flex items-start gap-3">
                        <div className="mt-0.5">
                          {step.status === 'success' ? (
                            <CheckCircle2 className="w-4 h-4 text-emerald-400" />
                          ) : step.status === 'warning' ? (
                            <AlertTriangle className="w-4 h-4 text-amber-400" />
                          ) : (
                            <XCircle className="w-4 h-4 text-red-400" />
                          )}
                        </div>
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center justify-between">
                            <span className="font-semibold text-xs text-white">{step.name}</span>
                            <span className={`text-[10px] uppercase font-mono px-2 py-0.5 rounded ${
                              step.status === 'success'
                                ? 'bg-emerald-950/80 text-emerald-400 border border-emerald-800/40'
                                : step.status === 'warning'
                                ? 'bg-amber-950/80 text-amber-400 border border-amber-800/40'
                                : 'bg-red-950/80 text-red-400 border border-red-800/40'
                            }`}>
                              {step.status}
                            </span>
                          </div>
                          <p className="text-xs text-slate-300 mt-1 font-mono break-all leading-relaxed">
                            {step.details}
                          </p>
                        </div>
                      </div>
                    ))}
                  </div>

                  {verifyResult.steps.some(s => s.status !== 'success' && s.name.includes('Auth')) && (
                    <div className="p-3.5 rounded-xl bg-purple-950/40 border border-purple-800/40 flex items-center justify-between gap-3 text-xs">
                      <div>
                        <strong className="text-purple-200 block">Google AI Pro Login Required</strong>
                        <p className="text-purple-300/80 text-[11px] mt-0.5">Authenticate with your Google account to enable unlimited reasoning turns for this persona.</p>
                      </div>
                      <button
                        onClick={handleStartAuth}
                        className="px-3 py-1.5 rounded-lg bg-purple-600 hover:bg-purple-500 text-white font-medium text-xs flex items-center gap-1.5 shrink-0 transition"
                      >
                        <KeyRound className="w-3.5 h-3.5" />
                        <span>Authenticate</span>
                      </button>
                    </div>
                  )}
                </div>
              ) : (
                <div className="p-8 text-center text-slate-500 text-xs font-mono">
                  {verifying ? 'Running diagnostics in container...' : 'Click "Re-run Diagnostics" to test driver readiness.'}
                </div>
              )}
            </div>
          )}

          {/* TAB 3: INTERACTIVE WEB TERMINAL */}
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
                className="p-3 rounded-xl bg-slate-950 border border-slate-800/80 h-80 overflow-hidden"
              />
            </div>
          )}

          {/* OAUTH DEVICE-CODE WIZARD MODAL / CARD */}
          {showAuthWizard && (
            <div className="p-4 rounded-xl bg-slate-950 border border-purple-500/40 space-y-3 text-xs animate-in fade-in">
              <div className="flex items-center justify-between border-b border-slate-800/80 pb-2">
                <div className="flex items-center gap-2 text-purple-300 font-semibold">
                  <KeyRound className="w-4 h-4 text-purple-400" />
                  <span>Google AI Pro Authentication Wizard</span>
                </div>
                <button
                  onClick={() => setShowAuthWizard(false)}
                  className="p-1 rounded text-slate-400 hover:text-white transition"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>

              {/* Auth Mode Toggle */}
              <div className="flex items-center gap-1.5 border-b border-slate-800 pb-2">
                <button
                  type="button"
                  onClick={() => {
                    setAuthMode('oauth');
                    if (!authStartResult && !authStarting) handleStartAuth();
                  }}
                  className={`px-3 py-1.5 rounded-lg text-xs font-medium transition cursor-pointer ${
                    authMode === 'oauth'
                      ? 'bg-purple-600/30 text-purple-300 border border-purple-500/40 font-semibold'
                      : 'text-slate-400 hover:text-slate-200 border border-transparent'
                  }`}
                >
                  Google AI Pro OAuth (Browser Login)
                </button>
                <button
                  type="button"
                  onClick={() => setAuthMode('apikey')}
                  className={`px-3 py-1.5 rounded-lg text-xs font-medium transition cursor-pointer ${
                    authMode === 'apikey'
                      ? 'bg-purple-600/30 text-purple-300 border border-purple-500/40 font-semibold'
                      : 'text-slate-400 hover:text-slate-200 border border-transparent'
                  }`}
                >
                  Direct API Key
                </button>
              </div>

              {authMode === 'apikey' ? (
                <div className="space-y-3 pt-1">
                  <p className="text-slate-300 leading-relaxed">
                    Configure your API key directly from the UI. Credentials will be securely saved into the environment secret and injected into the container:
                  </p>
                  <div className="flex items-center gap-2">
                    <input
                      type="password"
                      value={apiKeyInput}
                      onChange={e => setApiKeyInput(e.target.value)}
                      placeholder="Paste GEMINI_API_KEY or ANTHROPIC_API_KEY..."
                      className="flex-1 px-3 py-1.5 rounded-lg bg-slate-900 border border-slate-700 text-white text-xs font-mono focus:outline-none focus:border-purple-500"
                    />
                    <button
                      onClick={handleSaveApiKey}
                      disabled={apiKeySaving || !apiKeyInput.trim()}
                      className="px-4 py-1.5 rounded-lg bg-purple-600 hover:bg-purple-500 text-white font-medium text-xs transition disabled:opacity-50 cursor-pointer"
                    >
                      {apiKeySaving ? 'Saving...' : 'Save API Key'}
                    </button>
                  </div>
                </div>
              ) : authStarting ? (
                <div className="p-4 text-center text-slate-400 text-xs font-mono">
                  Initiating device-code OAuth flow inside container...
                </div>
              ) : authStartResult?.authUrl ? (
                <div className="space-y-3">
                  <p className="text-slate-300 leading-relaxed">
                    Step 1: Click the button below to sign in with your Google account (which has Google AI Pro):
                  </p>
                  <a
                    href={authStartResult.authUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-2 px-4 py-2 rounded-lg bg-sky-600 hover:bg-sky-500 text-white font-medium text-xs transition"
                  >
                    <span>Authorize with Google</span>
                    <ExternalLink className="w-3.5 h-3.5" />
                  </a>

                  <p className="text-slate-300 leading-relaxed pt-1">
                    Step 2: Paste the authorization code provided after login and click Submit:
                  </p>
                  <div className="flex items-center gap-2">
                    <input
                      type="text"
                      value={authCodeInput}
                      onChange={e => setAuthCodeInput(e.target.value)}
                      placeholder="Paste authorization code here (e.g. 4/0AY0...)"
                      className="flex-1 px-3 py-1.5 rounded-lg bg-slate-900 border border-slate-700 text-white text-xs font-mono focus:outline-none focus:border-sky-500"
                    />
                    <button
                      onClick={handleSubmitAuthCode}
                      disabled={authSubmitting || !authCodeInput.trim()}
                      className="px-4 py-1.5 rounded-lg bg-purple-600 hover:bg-purple-500 text-white font-medium text-xs transition disabled:opacity-50 cursor-pointer"
                    >
                      {authSubmitting ? 'Verifying...' : 'Submit Code'}
                    </button>
                  </div>
                </div>
              ) : (
                <div className="space-y-3">
                  <p className="text-slate-400">{authStartResult?.instructions || 'Driver is ready.'}</p>
                  <button
                    onClick={handleStartAuth}
                    disabled={authStarting}
                    className="px-3 py-1.5 rounded-lg bg-purple-600 hover:bg-purple-500 text-white text-xs font-medium transition cursor-pointer"
                  >
                    Start Google Sign-In
                  </button>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="p-3 border-t border-slate-800/80 flex items-center justify-between text-[11px] text-slate-500 bg-slate-900/40">
          <span>Dedicated persona runner volume persists across container updates.</span>
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
