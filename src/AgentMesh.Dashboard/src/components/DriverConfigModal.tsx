import React, { useEffect, useMemo, useState } from 'react';
import { X, Save, Info, KeyRound, AlertTriangle, Trash2 } from 'lucide-react';
import type { Agent, DriverConfig } from '../types';

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
  }) => Promise<void>;
  onDelete?: () => Promise<void>;
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
  onDelete
}) => {
  const config = initialConfig ?? agent.config ?? {};
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);

  const isOpenAiDriver = useMemo(() => {
    if (config.adapter) return config.adapter.toLowerCase() === 'openai';
    try {
      const parsed = agent.capabilitiesJson ? JSON.parse(agent.capabilitiesJson) : null;
      return String(parsed?.adapter ?? '').toLowerCase() === 'openai';
    } catch {
      return false;
    }
  }, [config.adapter, agent.capabilitiesJson]);

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
  const [error, setError] = useState<string | null>(null);

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
