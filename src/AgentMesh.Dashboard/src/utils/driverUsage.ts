import type { AgentUsageResponse, ProviderQuotaInfo } from '../types';

/**
 * Presentation helpers for driver usage.
 *
 * The API contract for `providerQuota` is deliberately nullable: a reading is only returned when
 * the driver's provider actually exposed quota data. `null` means "we could not read it", which
 * must render as an explicit *unavailable* state. Guessing a number here is what made the drivers
 * tab look like it had live data when it did not.
 */

export type QuotaTone = 'healthy' | 'warning' | 'critical' | 'unknown';

export interface QuotaSummary {
  /** True when the provider gave us a real reading. */
  available: boolean;
  /** 0-1 remaining fraction, or null when unavailable. */
  remainingFraction: number | null;
  /** Short chip label, e.g. "62% quota left" or "quota n/a". */
  label: string;
  tone: QuotaTone;
  /** Longer explanation for tooltips. */
  detail: string;
}

const UNAVAILABLE_DETAIL =
  'This driver does not expose a quota endpoint we can read, so no numbers are shown.';

export function summarizeQuota(quota?: ProviderQuotaInfo | null): QuotaSummary {
  if (!quota || !Array.isArray(quota.groups) || quota.groups.length === 0) {
    return {
      available: false,
      remainingFraction: null,
      label: 'quota n/a',
      tone: 'unknown',
      detail: UNAVAILABLE_DETAIL
    };
  }

  const fraction = clampFraction(quota.primaryRemainingFraction);
  const pct = Math.round(fraction * 100);
  const tone: QuotaTone = fraction > 0.3 ? 'healthy' : fraction > 0.15 ? 'warning' : 'critical';

  const provider = quota.provider?.trim();
  const detail = [
    provider ? `${provider}${quota.planName ? ` · ${quota.planName}` : ''}` : null,
    `${pct}% remaining`,
    quota.source ? `via ${quota.source}` : null,
    quota.observedAt ? `read ${relativeTime(quota.observedAt)}` : null
  ]
    .filter(Boolean)
    .join(' · ');

  return {
    available: true,
    remainingFraction: fraction,
    label: `${pct}% quota left`,
    tone,
    detail
  };
}

function clampFraction(value: number): number {
  if (typeof value !== 'number' || Number.isNaN(value)) return 0;
  return Math.min(1, Math.max(0, value));
}

export function relativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return 'unknown';

  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

/** Compact token count: 18400 -> "18.4k", 1500000 -> "1.5M". */
function formatTokenCount(total: number | undefined | null): string {
  const value = Number(total ?? 0);
  if (!Number.isFinite(value) || value <= 0) return '0';
  if (value < 1000) return `${value}`;
  if (value < 1_000_000) return `${(value / 1000).toFixed(1)}k`;
  return `${(value / 1_000_000).toFixed(2)}M`;
}

/**
 * The chip on a driver card. Lifetime tokens are always real (aggregated from session metadata),
 * so they render even when quota is unavailable.
 */
export function describeUsageChip(usage: AgentUsageResponse): {
  text: string;
  tone: QuotaTone;
  title: string;
} {
  const quota = summarizeQuota(usage.providerQuota);
  const tokens = formatTokenCount(usage.lifetimeTokens?.totalTokens);
  return {
    text: `${quota.label} · ${tokens} tokens`,
    tone: quota.tone,
    title: quota.available
      ? `${quota.detail} · ${tokens} tokens tracked by this mesh. Click for details.`
      : `${quota.detail} Click for lifetime token usage.`
  };
}

export const QUOTA_DOT_CLASS: Record<QuotaTone, string> = {
  healthy: 'bg-emerald-400',
  warning: 'bg-amber-400',
  critical: 'bg-rose-400',
  unknown: 'bg-slate-500'
};

export const QUOTA_TEXT_CLASS: Record<QuotaTone, string> = {
  healthy: 'text-emerald-400',
  warning: 'text-amber-400',
  critical: 'text-rose-400',
  unknown: 'text-slate-400'
};

/**
 * When a window resets. Returns null when the provider did not say, so callers can omit the
 * line rather than invent a cadence ("weekly"/"5 hours") for an unknown window.
 */
export function describeReset(resetTime?: string | null): string | null {
  if (!resetTime) return null;
  const when = new Date(resetTime);
  if (Number.isNaN(when.getTime())) return null;

  const span = when.getTime() - Date.now();
  if (span <= 0) return 'resets now';

  const hours = span / 3_600_000;
  if (hours < 1) return `resets in ${Math.max(1, Math.round(span / 60_000))} min`;
  if (hours < 48) return `resets in ${Math.round(hours)} hrs`;
  return `resets ${when.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}`;
}
