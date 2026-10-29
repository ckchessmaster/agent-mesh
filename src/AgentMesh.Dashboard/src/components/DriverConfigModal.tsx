import React, { useEffect, useMemo, useState } from 'react';
import {
  X,
  Save,
  Info,
  KeyRound,
  AlertTriangle,
  Trash2,
  Network,
  ChevronDown,
  ChevronRight,
  Loader2,
  ShieldCheck,
  CheckCircle2,
  XCircle,
  ExternalLink,
  RefreshCw
} from 'lucide-react';
import type {
  Agent,
  DriverConfig,
  DriverSidecarConfig,
  PersonaDriverVerificationResult,
  PersonaAuthStartResult
} from '../types';
import { api } from '../services/api';

interface DriverConfigModalProps {
  agent: Agent;
  /** Latest known config. The list endpoint sends it on Agent.config; may be absent for stale data. */
  initialConfig?: DriverConfig;
  saving?: boolean;
  onClose: () => void;
  onSave: (patch: {
    endpoint?: string;
    apiKey?: string;
    defaultModel?: string;
    timeoutSeconds?: number;
    maxRetries?: number;
    retryDelayMilliseconds?: number;
    maxSteps?: number;
    sidecar?: DriverSidecarConfig;
    loginCommand?: string;
    authCheckCommand?: string;
  }) => Promise<void>;
  onDelete?: () => Promise<void>;
  onAuthSuccess?: () => void | Promise<void>;
}

/**
 * Mirrors the OpenAiAdapter fallbacks so the form shows what will actually happen when a field
 * is unset. Retry delay is deliberately absent: when unset the adapter uses exponential backoff
 * (1s → 2s → 4s, capped at 15s) rather than a fixed interval, so there is no number to show.
 */
export const DRIVER_LIMIT_DEFAULTS = {
  maxSteps: 100,
  timeoutSeconds: 600,
  maxRetries: 2
} as const;

const toText = (value: number | null | undefined, fallback: number) =>
  value === null || value === undefined ? String(fallback) : String(value);

/**
 * Driver runtime configuration.
 *
 * Two rules this component exists to enforce:
 *  1. Limits are only editable for OpenAI-compatible drivers. The agy/claude/copilot adapters
 *     shell out to a third-party CLI and have no step loop, so exposing "max steps" there would
 *     offer a control that does nothing.
 *  2. The API key is write-only. The server never returns it, so we render presence, not value,
 *     and an empty field means "keep what's stored".
 */
export const DriverConfigModal: React.FC<DriverConfigModalProps> = ({
  agent,
  initialConfig,
  saving,
  onClose,
  onSave,
  onDelete,
  onAuthSuccess
}) => {
  const config = initialConfig ?? agent.config ?? {};
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);

  const driverAdapter = useMemo(() => {
    if (config.adapter) return config.adapter.toLowerCase();
    try {
      const parsed = agent.capabilitiesJson ? JSON.parse(agent.capabilitiesJson) : null;
      if (parsed?.adapter) return String(parsed.adapter).toLowerCase();
      if (parsed?.endpoint) return 'openai';
    } catch {
      // ignore
    }
    const idLower = (agent.id || '').toLowerCase();
    if (idLower.includes('claude')) return 'claude';
    if (idLower.includes('copilot')) return 'copilot';
    if (idLower.includes('openai') || idLower.includes('ollama') || idLower.includes('vllm')) return 'openai';
    return 'agy';
  }, [config.adapter, agent.capabilitiesJson, agent.id]);

  const isOpenAiDriver = driverAdapter === 'openai';

  // Driver Verification & Authentication state
  const [verifying, setVerifying] = useState(false);
  const [verifyResult, setVerifyResult] = useState<PersonaDriverVerificationResult | null>(null);
  const [showAuthWizard, setShowAuthWizard] = useState(false);
  const [authMode, setAuthMode] = useState<'oauth' | 'apikey'>(isOpenAiDriver ? 'apikey' : 'oauth');
  const [authStarting, setAuthStarting] = useState(false);
  const [authSubmitting, setAuthSubmitting] = useState(false);
  const [authStartResult, setAuthStartResult] = useState<PersonaAuthStartResult | null>(null);
  const [authCodeInput, setAuthCodeInput] = useState('');
  const [wizardApiKeyInput, setWizardApiKeyInput] = useState('');
  const [wizardApiKeySaving, setWizardApiKeySaving] = useState(false);
  const [authSuccessMessage, setAuthSuccessMessage] = useState<string | null>(null);

  const handleVerifyDriver = async () => {
    try {
      setVerifying(true);
      setError(null);
      const res = await api.verifyDriver(agent.id);
      setVerifyResult(res);
      await onAuthSuccess?.();
    } catch (err: any) {
      setError(err?.message || 'Failed to verify driver.');
    } finally {
      setVerifying(false);
    }
  };

  const handleStartDriverAuth = async () => {
    try {
      setAuthStarting(true);
      setError(null);
      setAuthSuccessMessage(null);
      setShowAuthWizard(true);
      const res = await api.startDriverAuth(agent.id);
      setAuthStartResult(res);
      if (res.status === 'already_authenticated') {
        setAuthSuccessMessage(res.instructions || 'Driver is already authenticated.');
        await onAuthSuccess?.();
      } else if (res.status === 'apikey_required') {
        setAuthMode('apikey');
      }
    } catch (err: any) {
      setError(err?.message || 'Failed to start driver authentication.');
    } finally {
      setAuthStarting(false);
    }
  };

  const handleSubmitDriverAuthCode = async () => {
    if (!authCodeInput.trim()) return;
    try {
      setAuthSubmitting(true);
      setError(null);
      const res = await api.submitDriverAuthCode(agent.id, authCodeInput.trim());
      if (res.status === 'authenticated') {
        setAuthSuccessMessage(res.message || 'Successfully authenticated driver and refreshed models!');
        setAuthCodeInput('');
        setAuthStartResult(null);
        setShowAuthWizard(false);
        await handleVerifyDriver();
      } else {
        setError(res.errorMessage || 'Failed to verify authorization code.');
      }
    } catch (err: any) {
      setError(err?.message || 'Failed to submit authorization code.');
    } finally {
      setAuthSubmitting(false);
    }
  };

  const handleSaveWizardApiKey = async () => {
    if (!wizardApiKeyInput.trim()) return;
    try {
      setWizardApiKeySaving(true);
      setError(null);
      const res = await api.configureDriverApiKey(agent.id, wizardApiKeyInput.trim(), driverAdapter);
      if (res.status === 'authenticated') {
        setAuthSuccessMessage(res.message || 'API key saved and models refreshed!');
        setWizardApiKeyInput('');
        setShowAuthWizard(false);
        await handleVerifyDriver();
      } else {
        setError(res.errorMessage || 'Failed to save API key.');
      }
    } catch (err: any) {
      setError(err?.message || 'Failed to save API key.');
    } finally {
      setWizardApiKeySaving(false);
    }
  };

  const [endpoint, setEndpoint] = useState(config.endpoint ?? '');
  const [apiKey, setApiKey] = useState('');
  const [defaultModel, setDefaultModel] = useState(config.defaultModel ?? '');
  const [maxSteps, setMaxSteps] = useState(toText(config.maxSteps, DRIVER_LIMIT_DEFAULTS.maxSteps));
  const [timeoutSeconds, setTimeoutSeconds] = useState(
    toText(config.timeoutSeconds, DRIVER_LIMIT_DEFAULTS.timeoutSeconds)
  );
  const [maxRetries, setMaxRetries] = useState(toText(config.maxRetries, DRIVER_LIMIT_DEFAULTS.maxRetries));
  // Blank = unset = exponential backoff. Pre-filling a number here would silently pin a fixed delay.
  const [retryDelay, setRetryDelay] = useState(
    config.retryDelayMilliseconds === null || config.retryDelayMilliseconds === undefined
      ? ''
      : String(config.retryDelayMilliseconds)
  );

  const [sidecarEnabled, setSidecarEnabled] = useState(config.sidecar?.enabled ?? false);
  const [sidecarType, setSidecarType] = useState<'tailscale' | 'custom'>(config.sidecar?.type ?? 'tailscale');
  const [sidecarAuthKey, setSidecarAuthKey] = useState('');
  const [sidecarHostname, setSidecarHostname] = useState(config.sidecar?.hostname ?? '');
  const [sidecarTags, setSidecarTags] = useState(config.sidecar?.tags ?? '');
  const [sidecarUserspace, setSidecarUserspace] = useState(config.sidecar?.userspaceMode ?? false);
  const [sidecarExtraArgs, setSidecarExtraArgs] = useState(config.sidecar?.extraArgs ?? '');
  const [sidecarImage, setSidecarImage] = useState(config.sidecar?.image ?? '');
  const [sidecarCommand, setSidecarCommand] = useState(config.sidecar?.command ?? '');
  const [companionSetup, setCompanionSetup] = useState(config.sidecar?.companionSetup ?? '');
  const [companionCommand, setCompanionCommand] = useState(config.sidecar?.companionCommand ?? '');
  const [companionPort, setCompanionPort] = useState(config.sidecar?.companionPort ? String(config.sidecar.companionPort) : '');
  const [loginCommand, setLoginCommand] = useState(config.loginCommand ?? config.sidecar?.loginCommand ?? '');
  const [authCheckCommand, setAuthCheckCommand] = useState(config.authCheckCommand ?? config.sidecar?.authCheckCommand ?? '');
  const [companionExpanded, setCompanionExpanded] = useState(Boolean(config.sidecar?.companionCommand || config.sidecar?.companionSetup || config.loginCommand || config.sidecar?.loginCommand));
  const [sidecarExpanded, setSidecarExpanded] = useState(Boolean(config.sidecar?.enabled));
  const [isTestingEndpoint, setIsTestingEndpoint] = useState(false);
  const [endpointTestResult, setEndpointTestResult] = useState<{ success: boolean; message: string } | null>(null);

  const [error, setError] = useState<string | null>(null);

  const handleTestConnection = async () => {
    if (!endpoint.trim()) {
      setError('Endpoint URL is required to test connection.');
      return;
    }
    setIsTestingEndpoint(true);
    setEndpointTestResult(null);
    try {
      const activeSidecar: DriverSidecarConfig | null = sidecarEnabled
        ? {
            enabled: true,
            type: sidecarType,
            authKey: sidecarAuthKey.trim() || undefined,
            hostname: sidecarHostname.trim() || undefined,
            tags: sidecarTags.trim() || undefined,
            userspaceMode: sidecarUserspace,
            extraArgs: sidecarExtraArgs.trim() || undefined,
            image: sidecarImage.trim() || undefined,
            command: sidecarCommand.trim() || undefined,
            companionSetup: companionSetup.trim() || undefined,
            companionCommand: companionCommand.trim() || undefined,
            companionPort: companionPort.trim() ? Number(companionPort.trim()) : undefined,
            loginCommand: loginCommand.trim() || undefined,
            authCheckCommand: authCheckCommand.trim() || undefined
          }
        : null;

      const res = await api.testOpenAiServer({
        endpoint: endpoint.trim(),
        apiKey: apiKey.trim() || undefined,
        sidecar: activeSidecar,
        agentId: agent.id
      });

      if (res.success) {
        setEndpointTestResult({
          success: true,
          message: `Connected successfully! Found ${res.models?.length ?? 0} model(s).`
        });
        await onAuthSuccess?.();
      } else {
        setEndpointTestResult({
          success: false,
          message: res.error || 'Connection failed.'
        });
      }
    } catch (err: any) {
      setEndpointTestResult({
        success: false,
        message: err?.message || 'Connection failed.'
      });
    } finally {
      setIsTestingEndpoint(false);
    }
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  type ParsedLimit = { state: 'ok'; value?: number } | { state: 'empty' } | { state: 'invalid'; message: string };

  const parseLimit = (raw: string, label: string, min: number): ParsedLimit => {
    if (raw.trim() === '') return { state: 'empty' };
    const n = Number(raw);
    if (!Number.isFinite(n) || !Number.isInteger(n) || n < min) {
      return { state: 'invalid', message: `${label} must be a whole number ≥ ${min}.` };
    }
    return { state: 'ok', value: n };
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    const patch: Parameters<typeof onSave>[0] = {};

    // Send only what changed. The server merges, so omitting a field is what keeps it intact.
    if ((config.endpoint ?? '') !== endpoint.trim()) patch.endpoint = endpoint.trim();
    if ((config.defaultModel ?? '') !== defaultModel.trim()) patch.defaultModel = defaultModel.trim();
    if (apiKey.trim()) patch.apiKey = apiKey.trim();

    if (isOpenAiDriver) {
      const fields: Array<{
        parsed: ParsedLimit;
        key: 'maxSteps' | 'timeoutSeconds' | 'maxRetries' | 'retryDelayMilliseconds';
        /** What the adapter will actually use today, defaults included. */
        effective: number | undefined;
      }> = [
        {
          parsed: parseLimit(maxSteps, 'Max steps', 1),
          key: 'maxSteps',
          effective: config.maxSteps ?? DRIVER_LIMIT_DEFAULTS.maxSteps
        },
        {
          parsed: parseLimit(timeoutSeconds, 'Timeout (seconds)', 1),
          key: 'timeoutSeconds',
          effective: config.timeoutSeconds ?? DRIVER_LIMIT_DEFAULTS.timeoutSeconds
        },
        {
          parsed: parseLimit(maxRetries, 'Max retries', 0),
          key: 'maxRetries',
          effective: config.maxRetries ?? DRIVER_LIMIT_DEFAULTS.maxRetries
        },
        // No fixed default: unset means exponential backoff, so only a real number counts as set.
        { parsed: parseLimit(retryDelay, 'Retry delay (ms)', 0), key: 'retryDelayMilliseconds', effective: config.retryDelayMilliseconds ?? undefined }
      ];

      for (const { parsed, key, effective } of fields) {
        if (parsed.state === 'invalid') {
          setError(parsed.message);
          return;
        }
        // 'empty' means "leave alone": the server has no way to clear a limit, and for retry
        // delay blank explicitly means "keep using exponential backoff".
        if (parsed.state === 'empty' || parsed.value === effective) continue;
        (patch as Record<string, number>)[key] = parsed.value as number;
      }

      if (
        sidecarEnabled !== (config.sidecar?.enabled ?? false) ||
        sidecarType !== (config.sidecar?.type ?? 'tailscale') ||
        sidecarAuthKey.trim() ||
        sidecarHostname.trim() !== (config.sidecar?.hostname ?? '') ||
        sidecarTags.trim() !== (config.sidecar?.tags ?? '') ||
        sidecarUserspace !== (config.sidecar?.userspaceMode ?? false) ||
        sidecarExtraArgs.trim() !== (config.sidecar?.extraArgs ?? '') ||
        sidecarImage.trim() !== (config.sidecar?.image ?? '') ||
        sidecarCommand.trim() !== (config.sidecar?.command ?? '') ||
        companionSetup.trim() !== (config.sidecar?.companionSetup ?? '') ||
        companionCommand.trim() !== (config.sidecar?.companionCommand ?? '') ||
        (companionPort.trim() ? Number(companionPort.trim()) : undefined) !== (config.sidecar?.companionPort ?? undefined) ||
        loginCommand.trim() !== (config.sidecar?.loginCommand ?? '') ||
        authCheckCommand.trim() !== (config.sidecar?.authCheckCommand ?? '')
      ) {
        patch.sidecar = {
          enabled: sidecarEnabled,
          type: sidecarType,
          authKey: sidecarAuthKey.trim() || undefined,
          hostname: sidecarHostname.trim() || undefined,
          tags: sidecarTags.trim() || undefined,
          userspaceMode: sidecarUserspace,
          extraArgs: sidecarExtraArgs.trim() || undefined,
          image: sidecarImage.trim() || undefined,
          command: sidecarCommand.trim() || undefined,
          companionSetup: companionSetup.trim() || undefined,
          companionCommand: companionCommand.trim() || undefined,
          companionPort: companionPort.trim() ? Number(companionPort.trim()) : undefined,
          loginCommand: loginCommand.trim() || undefined,
          authCheckCommand: authCheckCommand.trim() || undefined
        };
      }

      if (loginCommand.trim() !== (config.loginCommand ?? '')) {
        patch.loginCommand = loginCommand.trim();
      }
      if (authCheckCommand.trim() !== (config.authCheckCommand ?? '')) {
        patch.authCheckCommand = authCheckCommand.trim();
      }
    }

    if (Object.keys(patch).length === 0) {
      onClose();
      return;
    }

    try {
      await onSave(patch);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save driver configuration.');
    }
  };

  const fieldClass =
    'w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 text-xs focus:outline-none focus:border-sky-500';
  const labelClass = 'block text-[11px] text-slate-400 mb-1';

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`Configure driver ${agent.name}`}
      className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-end sm:items-center justify-center p-0 sm:p-4"
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className="max-w-lg w-full bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl shadow-2xl flex flex-col max-h-[90dvh] sm:max-h-[85vh] overflow-hidden">
        <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
          <div>
            <h3 className="text-sm font-bold text-white flex items-center gap-2">
              <span>Driver Configuration</span>
              <span className="text-[10px] font-mono text-slate-400 font-normal">@{agent.id}</span>
            </h3>
            <p className="text-xs text-slate-400 mt-0.5">
              Applies to future turns. Changes are merged — fields you don't touch are preserved.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="p-1 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
            aria-label="Close"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="flex-1 overflow-y-auto p-5 space-y-4">
          {!isOpenAiDriver && (
            <div className="flex items-start gap-2 p-2.5 rounded-lg bg-slate-950/70 border border-slate-800 text-[11px] text-slate-400">
              <Info className="w-3.5 h-3.5 shrink-0 text-slate-500 mt-0.5" />
              <span>
                This driver shells out to an external CLI, so step limits, timeouts and retries are
                controlled by that tool — not by AgentMesh. Only the display settings below apply.
              </span>
            </div>
          )}

          {/* Driver Authentication & Model Discovery */}
          <div className="p-3 rounded-xl bg-slate-950/70 border border-slate-800 space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <div className="text-xs font-semibold text-white flex items-center gap-1.5">
                  <ShieldCheck className="w-3.5 h-3.5 text-sky-400" />
                  <span>Driver Authentication &amp; Model Discovery</span>
                  {(config.hasOAuthToken || config.hasApiKey) && (
                    <span className="px-1.5 py-0.2 rounded bg-emerald-950/80 border border-emerald-600/40 text-[9px] font-mono text-emerald-300">
                      authenticated
                    </span>
                  )}
                </div>
                <p className="text-[10px] text-slate-400 mt-0.5">
                  Authenticate once for this driver and automatically discover available models for all bound personas.
                </p>
              </div>
              <div className="flex items-center gap-1.5">
                <button
                  type="button"
                  onClick={handleVerifyDriver}
                  disabled={verifying}
                  className="px-2.5 py-1 rounded-lg bg-sky-950/60 hover:bg-sky-900/60 text-sky-300 border border-sky-800/40 text-[11px] font-medium flex items-center gap-1 transition disabled:opacity-50 cursor-pointer"
                >
                  <RefreshCw className={`w-3 h-3 ${verifying ? 'animate-spin' : ''}`} />
                  <span>{verifying ? 'Verifying...' : 'Verify & Refresh Models'}</span>
                </button>
                <button
                  type="button"
                  onClick={handleStartDriverAuth}
                  disabled={authStarting}
                  className="px-2.5 py-1 rounded-lg bg-purple-950/60 hover:bg-purple-900/60 text-purple-300 border border-purple-800/40 text-[11px] font-medium flex items-center gap-1 transition disabled:opacity-50 cursor-pointer"
                >
                  <KeyRound className="w-3 h-3" />
                  <span>Authenticate Driver</span>
                </button>
              </div>
            </div>

            {authSuccessMessage && (
              <div className="p-2.5 rounded-lg bg-emerald-950/40 border border-emerald-500/30 text-[11px] text-emerald-300 flex items-center gap-2">
                <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400 shrink-0" />
                <span>{authSuccessMessage}</span>
              </div>
            )}

            {verifyResult && (
              <div className="space-y-2 pt-1">
                <div className="divide-y divide-slate-800/80 rounded-lg bg-slate-900/80 border border-slate-800 overflow-hidden">
                  {verifyResult.steps.map((step, idx) => (
                    <div key={idx} className="p-2.5 flex items-start gap-2.5 text-xs">
                      <div className="mt-0.5">
                        {step.status === 'success' ? (
                          <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400" />
                        ) : step.status === 'warning' ? (
                          <AlertTriangle className="w-3.5 h-3.5 text-amber-400" />
                        ) : (
                          <XCircle className="w-3.5 h-3.5 text-rose-400" />
                        )}
                      </div>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between">
                          <span className="font-semibold text-[11px] text-white">{step.name}</span>
                          <span className="text-[9px] uppercase font-mono text-slate-400">{step.status}</span>
                        </div>
                        <p className="text-[11px] text-slate-300 mt-0.5 font-mono break-all">{step.details}</p>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {showAuthWizard && (
              <div className="p-3 rounded-lg bg-slate-900 border border-purple-500/40 space-y-2.5 text-xs">
                <div className="flex items-center justify-between border-b border-slate-800 pb-1.5">
                  <span className="font-semibold text-purple-300 flex items-center gap-1.5">
                    <KeyRound className="w-3.5 h-3.5 text-purple-400" />
                    <span>Driver Authentication</span>
                  </span>
                  <button
                    type="button"
                    onClick={() => setShowAuthWizard(false)}
                    className="text-slate-400 hover:text-white cursor-pointer"
                  >
                    <X className="w-3.5 h-3.5" />
                  </button>
                </div>

                <div className="flex items-center gap-1.5">
                  {!isOpenAiDriver && (
                    <button
                      type="button"
                      onClick={() => {
                        setAuthMode('oauth');
                        if (!authStartResult && !authStarting) handleStartDriverAuth();
                      }}
                      className={`px-2.5 py-1 rounded text-[11px] font-medium cursor-pointer ${
                        authMode === 'oauth'
                          ? 'bg-purple-600/30 text-purple-300 border border-purple-500/40'
                          : 'text-slate-400 hover:text-slate-200'
                      }`}
                    >
                      Interactive OAuth Login
                    </button>
                  )}
                  <button
                    type="button"
                    onClick={() => setAuthMode('apikey')}
                    className={`px-2.5 py-1 rounded text-[11px] font-medium cursor-pointer ${
                      authMode === 'apikey'
                        ? 'bg-purple-600/30 text-purple-300 border border-purple-500/40'
                        : 'text-slate-400 hover:text-slate-200'
                    }`}
                  >
                    Direct API Key
                  </button>
                </div>

                {authMode === 'apikey' ? (
                  <div className="flex items-center gap-2 pt-1">
                    <input
                      type="password"
                      value={wizardApiKeyInput}
                      onChange={(e) => setWizardApiKeyInput(e.target.value)}
                      placeholder="Paste API key..."
                      className="flex-1 px-2.5 py-1.5 rounded bg-slate-950 border border-slate-700 text-white text-xs font-mono focus:outline-none focus:border-purple-500"
                    />
                    <button
                      type="button"
                      onClick={handleSaveWizardApiKey}
                      disabled={wizardApiKeySaving || !wizardApiKeyInput.trim()}
                      className="px-3 py-1.5 rounded bg-purple-600 hover:bg-purple-500 text-white font-medium text-xs disabled:opacity-50 cursor-pointer"
                    >
                      {wizardApiKeySaving ? 'Saving...' : 'Save Key'}
                    </button>
                  </div>
                ) : authStarting ? (
                  <div className="p-3 text-center text-slate-400 text-[11px] font-mono">
                    Starting driver login session...
                  </div>
                ) : authStartResult?.status === 'error' || authStartResult?.errorMessage ? (
                  <div className="p-2.5 rounded bg-rose-950/50 border border-rose-500/40 text-[11px] text-rose-200 font-mono">
                    {authStartResult.errorMessage}
                  </div>
                ) : (
                  <div className="space-y-2 pt-1">
                    {authStartResult?.authUrl && (
                      <div className="space-y-1.5">
                        <p className="text-[11px] text-slate-300">
                          Step 1: Open the authorization link in your browser to sign in:
                        </p>
                        <a
                          href={authStartResult.authUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded bg-sky-600 hover:bg-sky-500 text-white font-medium text-[11px] transition"
                        >
                          <span>Authorize in Browser</span>
                          <ExternalLink className="w-3 h-3" />
                        </a>
                      </div>
                    )}
                    <p className="text-[11px] text-slate-300">
                      {authStartResult?.authUrl ? 'Step 2: ' : ''}Paste the authorization code below and click Submit:
                    </p>
                    <div className="flex items-center gap-2">
                      <input
                        type="text"
                        value={authCodeInput}
                        onChange={(e) => setAuthCodeInput(e.target.value)}
                        placeholder="Paste authorization code..."
                        className="flex-1 px-2.5 py-1.5 rounded bg-slate-950 border border-slate-700 text-white text-xs font-mono focus:outline-none focus:border-sky-500"
                      />
                      <button
                        type="button"
                        onClick={handleSubmitDriverAuthCode}
                        disabled={authSubmitting || !authCodeInput.trim()}
                        className="px-3 py-1.5 rounded bg-purple-600 hover:bg-purple-500 text-white font-medium text-xs disabled:opacity-50 cursor-pointer shrink-0"
                      >
                        {authSubmitting ? 'Verifying...' : 'Submit Code'}
                      </button>
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>

          {isOpenAiDriver && (
            <>
              <div>
                <label className={labelClass} htmlFor="driver-endpoint">
                  Endpoint URL
                </label>
                <input
                  id="driver-endpoint"
                  type="text"
                  value={endpoint}
                  onChange={(e) => setEndpoint(e.target.value)}
                  placeholder="http://localhost:11434/v1"
                  className={`${fieldClass} font-mono`}
                />
                <div className="flex items-center justify-between mt-1">
                  <button
                    type="button"
                    disabled={isTestingEndpoint || !endpoint.trim()}
                    onClick={handleTestConnection}
                    className="text-[11px] font-medium text-sky-400 hover:text-sky-300 disabled:opacity-50 flex items-center gap-1 cursor-pointer"
                  >
                    {isTestingEndpoint ? <Loader2 className="w-3 h-3 animate-spin" /> : <Network className="w-3 h-3" />}
                    <span>{isTestingEndpoint ? 'Testing reachability...' : 'Test Connection'}</span>
                  </button>
                  {endpointTestResult && (
                    <span className={`text-[10px] ${endpointTestResult.success ? 'text-emerald-400' : 'text-rose-400'}`}>
                      {endpointTestResult.message}
                    </span>
                  )}
                </div>
              </div>

              <div>
                <label className={labelClass} htmlFor="driver-api-key">
                  API Key
                </label>
                <div className="relative">
                  <KeyRound className="w-3.5 h-3.5 text-slate-500 absolute left-2 top-1/2 -translate-y-1/2" />
                  <input
                    id="driver-api-key"
                    type="password"
                    value={apiKey}
                    onChange={(e) => setApiKey(e.target.value)}
                    autoComplete="new-password"
                    placeholder={config.hasApiKey ? '•••••••••••• (stored — leave blank to keep)' : 'not set'}
                    className={`${fieldClass} pl-7 font-mono`}
                  />
                </div>
                <p className="text-[10px] text-slate-500 mt-1">
                  Stored server-side and never sent back to the browser.
                </p>
              </div>

              <div>
                <label className={labelClass} htmlFor="driver-model">
                  Default Model
                </label>
                <input
                  id="driver-model"
                  type="text"
                  value={defaultModel}
                  onChange={(e) => setDefaultModel(e.target.value)}
                  placeholder="e.g. qwen2.5-coder:32b"
                  className={`${fieldClass} font-mono`}
                />
              </div>

              <div className="pt-1">
                <div className="text-[10px] uppercase font-bold tracking-wider text-slate-500 mb-2">
                  Turn Limits
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className={labelClass} htmlFor="driver-max-steps">
                      Max steps per turn
                    </label>
                    <input
                      id="driver-max-steps"
                      type="number"
                      min={1}
                      step={1}
                      value={maxSteps}
                      onChange={(e) => setMaxSteps(e.target.value)}
                      className={fieldClass}
                    />
                    <p className="text-[10px] text-slate-500 mt-1">
                      Default {DRIVER_LIMIT_DEFAULTS.maxSteps}. On reaching it the agent is asked to
                      summarize rather than stopping silently.
                    </p>
                  </div>
                  <div>
                    <label className={labelClass} htmlFor="driver-timeout">
                      Request timeout (s)
                    </label>
                    <input
                      id="driver-timeout"
                      type="number"
                      min={1}
                      step={1}
                      value={timeoutSeconds}
                      onChange={(e) => setTimeoutSeconds(e.target.value)}
                      className={fieldClass}
                    />
                    <p className="text-[10px] text-slate-500 mt-1">
                      Also used as the streaming inactivity ceiling.
                    </p>
                  </div>
                  <div>
                    <label className={labelClass} htmlFor="driver-max-retries">
                      Max retries
                    </label>
                    <input
                      id="driver-max-retries"
                      type="number"
                      min={0}
                      step={1}
                      value={maxRetries}
                      onChange={(e) => setMaxRetries(e.target.value)}
                      className={fieldClass}
                    />
                  </div>
                  <div>
                    <label className={labelClass} htmlFor="driver-retry-delay">
                      Retry delay (ms)
                    </label>
                    <input
                      id="driver-retry-delay"
                      type="number"
                      min={0}
                      step={100}
                      value={retryDelay}
                      onChange={(e) => setRetryDelay(e.target.value)}
                      placeholder="backoff (1s → 2s → 4s)"
                      className={fieldClass}
                    />
                    <p className="text-[10px] text-slate-500 mt-1">
                      Blank keeps exponential backoff.
                    </p>
                  </div>
                </div>
              </div>

              <div className="pt-2 border-t border-slate-800">
                <button
                  type="button"
                  onClick={() => setSidecarExpanded(!sidecarExpanded)}
                  className="flex items-center justify-between w-full text-left py-1 text-slate-300 hover:text-white transition cursor-pointer"
                >
                  <div className="flex items-center gap-2">
                    <Network className="w-3.5 h-3.5 text-sky-400" />
                    <span className="text-xs font-semibold">Private Network & Sidecar (Tailscale / VPN)</span>
                    {sidecarEnabled && (
                      <span className="px-1.5 py-0.2 rounded bg-sky-950/80 border border-sky-600/40 text-[9px] font-mono text-sky-300">
                        active
                      </span>
                    )}
                  </div>
                  {sidecarExpanded ? <ChevronDown className="w-3.5 h-3.5 text-slate-400" /> : <ChevronRight className="w-3.5 h-3.5 text-slate-400" />}
                </button>

                {sidecarExpanded && (
                  <div className="mt-3 space-y-3 p-3 rounded-lg bg-slate-950/60 border border-slate-800">
                    <label className="flex items-center gap-2 text-xs text-slate-300 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={sidecarEnabled}
                        onChange={(e) => setSidecarEnabled(e.target.checked)}
                        className="rounded border-slate-700 bg-slate-900 text-sky-500 focus:ring-sky-500"
                      />
                      <span>Enable Network Sidecar Container</span>
                    </label>

                    {sidecarEnabled && (
                      <>
                        <div className="grid grid-cols-2 gap-2">
                          <label className="flex items-center gap-1.5 text-xs text-slate-400 cursor-pointer">
                            <input
                              type="radio"
                              name="sidecarType"
                              value="tailscale"
                              checked={sidecarType === 'tailscale'}
                              onChange={() => setSidecarType('tailscale')}
                              className="text-sky-500"
                            />
                            <span>Tailscale</span>
                          </label>
                          <label className="flex items-center gap-1.5 text-xs text-slate-400 cursor-pointer">
                            <input
                              type="radio"
                              name="sidecarType"
                              value="custom"
                              checked={sidecarType === 'custom'}
                              onChange={() => setSidecarType('custom')}
                              className="text-sky-500"
                            />
                            <span>Custom Container</span>
                          </label>
                        </div>

                        {sidecarType === 'tailscale' ? (
                          <div className="space-y-2.5 pt-1">
                            <div>
                              <label className={labelClass} htmlFor="sidecar-authkey">
                                Tailscale Auth Key
                              </label>
                              <div className="relative">
                                <KeyRound className="w-3.5 h-3.5 text-slate-500 absolute left-2 top-1/2 -translate-y-1/2" />
                                <input
                                  id="sidecar-authkey"
                                  type="password"
                                  value={sidecarAuthKey}
                                  onChange={(e) => setSidecarAuthKey(e.target.value)}
                                  placeholder={config.hasSidecarAuthKey ? '•••••••••••• (stored — leave blank to keep)' : 'tskey-auth-...'}
                                  className={`${fieldClass} pl-7 font-mono`}
                                />
                              </div>
                            </div>

                            <div className="grid grid-cols-2 gap-2">
                              <div>
                                <label className={labelClass} htmlFor="sidecar-hostname">
                                  Node Hostname (optional)
                                </label>
                                <input
                                  id="sidecar-hostname"
                                  type="text"
                                  value={sidecarHostname}
                                  onChange={(e) => setSidecarHostname(e.target.value)}
                                  placeholder={`mesh-${agent.id}`}
                                  className={`${fieldClass} font-mono`}
                                />
                              </div>
                              <div>
                                <label className={labelClass} htmlFor="sidecar-tags">
                                  Advertise Tags (optional)
                                </label>
                                <input
                                  id="sidecar-tags"
                                  type="text"
                                  value={sidecarTags}
                                  onChange={(e) => setSidecarTags(e.target.value)}
                                  placeholder="tag:agentmesh"
                                  className={`${fieldClass} font-mono`}
                                />
                              </div>
                            </div>

                            <label className="flex items-center gap-2 text-[11px] text-slate-400 cursor-pointer pt-1">
                              <input
                                type="checkbox"
                                checked={sidecarUserspace}
                                onChange={(e) => setSidecarUserspace(e.target.checked)}
                                className="rounded border-slate-700 bg-slate-900 text-sky-500"
                              />
                              <span>Userspace Mode (no NET_ADMIN / /dev/net/tun required)</span>
                            </label>

                            <div>
                              <label className={labelClass} htmlFor="sidecar-extra-args">
                                Extra Arguments (optional)
                              </label>
                              <input
                                id="sidecar-extra-args"
                                type="text"
                                value={sidecarExtraArgs}
                                onChange={(e) => setSidecarExtraArgs(e.target.value)}
                                placeholder="--accept-routes"
                                className={`${fieldClass} font-mono`}
                              />
                            </div>
                          </div>
                        ) : (
                          <div className="space-y-2 pt-1">
                            <div>
                              <label className={labelClass} htmlFor="sidecar-image">
                                Sidecar Image
                              </label>
                              <input
                                id="sidecar-image"
                                type="text"
                                value={sidecarImage}
                                onChange={(e) => setSidecarImage(e.target.value)}
                                placeholder="alpine:latest"
                                className={`${fieldClass} font-mono`}
                              />
                            </div>
                            <div>
                              <label className={labelClass} htmlFor="sidecar-cmd">
                                Command (optional)
                              </label>
                              <input
                                id="sidecar-cmd"
                                type="text"
                                value={sidecarCommand}
                                onChange={(e) => setSidecarCommand(e.target.value)}
                                placeholder="e.g. /entrypoint.sh"
                                className={`${fieldClass} font-mono`}
                              />
                            </div>
                          </div>
                        )}

                        <div className="pt-2 border-t border-slate-800/80">
                          <button
                            type="button"
                            onClick={() => setCompanionExpanded(!companionExpanded)}
                            className="flex items-center justify-between w-full text-left py-1 text-slate-400 hover:text-slate-200 transition cursor-pointer"
                          >
                            <span className="text-[11px] font-medium text-slate-300">Companion Proxy, Daemon &amp; Login CLI</span>
                            {companionExpanded ? <ChevronDown className="w-3 h-3 text-slate-400" /> : <ChevronRight className="w-3 h-3 text-slate-400" />}
                          </button>

                          {companionExpanded && (
                            <div className="mt-2 space-y-2.5 pl-2 border-l border-slate-800">
                              <div>
                                <label className={labelClass} htmlFor="companion-setup">
                                  Companion Setup Script (optional)
                                </label>
                                <input
                                  id="companion-setup"
                                  type="text"
                                  value={companionSetup}
                                  onChange={(e) => setCompanionSetup(e.target.value)}
                                  placeholder="which my-cli >/dev/null 2>&1 || curl -fsSL https://.../install.sh | sh"
                                  className={`${fieldClass} font-mono`}
                                />
                                <p className="text-[10px] text-slate-500 mt-1">
                                  Runs synchronously on boot before starting daemons.
                                </p>
                              </div>
                              <div className="grid grid-cols-3 gap-2">
                                <div className="col-span-2">
                                  <label className={labelClass} htmlFor="companion-cmd">
                                    Companion Daemon Command (optional)
                                  </label>
                                  <input
                                    id="companion-cmd"
                                    type="text"
                                    value={companionCommand}
                                    onChange={(e) => setCompanionCommand(e.target.value)}
                                    placeholder="my-proxy --port 11435"
                                    className={`${fieldClass} font-mono`}
                                  />
                                  <p className="text-[10px] text-slate-500 mt-1">
                                    Background proxy/auth process.
                                  </p>
                                </div>
                                <div>
                                  <label className={labelClass} htmlFor="companion-port">
                                    Ready Port (optional)
                                  </label>
                                  <input
                                    id="companion-port"
                                    type="number"
                                    min={1}
                                    max={65535}
                                    value={companionPort}
                                    onChange={(e) => setCompanionPort(e.target.value)}
                                    placeholder="11435"
                                    className={`${fieldClass} font-mono`}
                                  />
                                  <p className="text-[10px] text-slate-500 mt-1">
                                    TCP port to await.
                                  </p>
                                </div>
                              </div>
                              <div>
                                <label className={labelClass} htmlFor="companion-login-cmd">
                                  Driver Login Command (optional)
                                </label>
                                <input
                                  id="companion-login-cmd"
                                  type="text"
                                  value={loginCommand}
                                  onChange={(e) => setLoginCommand(e.target.value)}
                                  placeholder="my-cli login or /root/login.sh"
                                  className={`${fieldClass} font-mono`}
                                />
                                <p className="text-[10px] text-slate-500 mt-1">
                                  Command executed in container when clicking &quot;Authenticate Driver&quot;. Authorization URLs detected in output will be presented to the user.
                                </p>
                              </div>
                              <div>
                                <label className={labelClass} htmlFor="companion-auth-check-cmd">
                                  Auth Check Command (optional)
                                </label>
                                <input
                                  id="companion-auth-check-cmd"
                                  type="text"
                                  value={authCheckCommand}
                                  onChange={(e) => setAuthCheckCommand(e.target.value)}
                                  placeholder="my-cli status or test -f ~/.config/token"
                                  className={`${fieldClass} font-mono`}
                                />
                                <p className="text-[10px] text-slate-500 mt-1">
                                  Command executed in container to check if the driver or companion is already authenticated.
                                </p>
                              </div>
                            </div>
                          )}
                        </div>
                      </>
                    )}
                  </div>
                )}
              </div>
            </>
          )}

          {config.capabilities && config.capabilities.length > 0 && (
            <div>
              <div className="text-[10px] uppercase font-bold tracking-wider text-slate-500 mb-1.5">
                Capabilities
              </div>
              <div className="flex flex-wrap gap-1">
                {config.capabilities.map((cap) => (
                  <span
                    key={cap}
                    className="px-1.5 py-0.5 rounded bg-slate-800 border border-slate-700 text-[10px] font-mono text-slate-300"
                  >
                    {cap}
                  </span>
                ))}
              </div>
            </div>
          )}

          {error && (
            <div
              role="alert"
              className="flex items-center gap-1.5 p-2 rounded-lg bg-rose-950/50 border border-rose-800/50 text-[11px] text-rose-300"
            >
              <AlertTriangle className="w-3.5 h-3.5 shrink-0" />
              <span>{error}</span>
            </div>
          )}

          {onDelete && (
            <div className="pt-3 border-t border-slate-800 flex items-center justify-between">
              <div>
                <span className="text-[11px] font-semibold text-rose-400 block">Danger Zone</span>
                <span className="text-[10px] text-slate-500">Permanently delete this driver registration</span>
              </div>
              {!confirmDelete ? (
                <button
                  type="button"
                  onClick={() => setConfirmDelete(true)}
                  className="px-2.5 py-1 rounded bg-rose-950/40 hover:bg-rose-900/50 text-rose-300 border border-rose-500/30 text-xs font-medium flex items-center gap-1 transition cursor-pointer"
                  data-testid="driver-modal-delete-btn"
                >
                  <Trash2 className="w-3 h-3 text-rose-400" />
                  <span>Delete Driver</span>
                </button>
              ) : (
                <div className="flex items-center gap-1.5">
                  <span className="text-[11px] text-rose-300 font-medium">Are you sure?</span>
                  <button
                    type="button"
                    onClick={() => setConfirmDelete(false)}
                    className="px-2 py-0.5 text-xs text-slate-400 hover:text-slate-200"
                  >
                    Cancel
                  </button>
                  <button
                    type="button"
                    disabled={isDeleting}
                    onClick={async () => {
                      setIsDeleting(true);
                      setError(null);
                      try {
                        await onDelete();
                        onClose();
                      } catch (err: any) {
                        setError(err?.message || 'Failed to delete driver');
                        setIsDeleting(false);
                      }
                    }}
                    className="px-2.5 py-1 rounded bg-rose-600 hover:bg-rose-500 text-white font-bold text-xs transition cursor-pointer"
                    data-testid="driver-modal-confirm-delete-btn"
                  >
                    {isDeleting ? 'Deleting...' : 'Confirm'}
                  </button>
                </div>
              )}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <button
              type="button"
              onClick={onClose}
              className="px-3 py-1.5 rounded text-slate-400 hover:text-slate-200 text-xs cursor-pointer"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={saving}
              className="px-3 py-1.5 rounded bg-sky-600 hover:bg-sky-500 text-white text-xs font-medium disabled:opacity-50 flex items-center gap-1.5 cursor-pointer"
            >
              <Save className="w-3.5 h-3.5" />
              <span>Save</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
