/**
 * Pure state helpers behind the attention bell — no React, no browser globals beyond `Storage`.
 *
 * These live in `utils/` rather than inside `AttentionBell.tsx` for two reasons: the component file
 * must export components only (the repo's `react/only-export-components` lint rule), and the
 * read/dismiss bookkeeping is the part of the bell most likely to be wrong, so it deserves unit tests
 * that do not mount anything.
 *
 * ### Why the client keeps read/dismiss state at all
 *
 * `GET /api/attention` is derived from live job state (the job's heartbeat and its pending timeline
 * cards — see `src/AgentMesh.Api/Services/AttentionQueryService.cs`). There is deliberately no
 * `POST /api/attention/dismiss`: the server's answer to "is this still waiting?" is "ask me again",
 * and an ack column would create a second, stale source of truth for a wait that has already moved on.
 * So "I have seen this" and "hide this one" are operator-local facts, persisted under our own keys.
 *
 * Both maps expire on the same 24h window as the notification seen-set
 * (`SEEN_STORAGE_KEY` in `src/services/notifications.ts`), which keeps one mental rule across the
 * feature: a wait that is still open tomorrow is a wait you should be told about again.
 */
import type { AttentionItem, AttentionRollup } from '../types/attention';
import { AttentionType } from '../types/attention';

/** Waits the operator has viewed in the bell: `{ "{jobId}:{type}": epochMs }`. */
export const READ_STORAGE_KEY = 'agentmesh_attention_read_v1';
/** Waits the operator hid from the list without resolving: `{ "{jobId}:{type}": epochMs }`. */
export const DISMISSED_STORAGE_KEY = 'agentmesh_attention_dismissed_v1';

/** How long a read/dismiss stamp survives, mirroring `DEFAULT_SEEN_TTL_MS` in the notification service. */
export const ATTENTION_STATE_TTL_MS = 24 * 60 * 60 * 1000;
/** Cap per map, oldest evicted first, so a long-lived browser profile cannot grow the key forever. */
export const ATTENTION_STATE_MAX_ENTRIES = 300;

/** Steady-state poll cadence for the rollup. SignalR is the fast path; this is the correctness path. */
export const DEFAULT_POLL_INTERVAL_MS = 30_000;
/** Coalescing window after a SignalR push before the bell re-reads the authoritative rollup. */
export const PUSH_RECONCILE_DELAY_MS = 400;
/** Badge threshold past which the count collapses to `9+` instead of widening the header. */
export const BADGE_COLLAPSE_AT = 10;

/** `id -> epochMs`. Insertion order is meaningful only as a tiebreaker for eviction. */
export type AttentionStampMap = Record<string, number>;

const MINUTE_MS = 60_000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

/** Human age for a wait, e.g. `just now`, `12m`, `5h`, `3d`. `since` is the wait's start, not its last touch. */
export const formatAttentionAge = (since: string | null | undefined, nowMs: number): string => {
  const then = since ? Date.parse(since) : Number.NaN;
  if (!Number.isFinite(then)) return 'unknown';
  // A clock skew that puts the wait in the future reads as "just now" rather than "-4m".
  const delta = Math.max(0, nowMs - then);
  if (delta < MINUTE_MS) return 'just now';
  if (delta < HOUR_MS) return `${Math.floor(delta / MINUTE_MS)}m`;
  if (delta < DAY_MS) return `${Math.floor(delta / HOUR_MS)}h`;
  const days = Math.floor(delta / DAY_MS);
  if (days < 30) return `${days}d`;
  return `${Math.floor(days / 30)}mo`;
};

/** Absolute timestamp behind the relative age, for a tooltip: relative alone cannot be trusted at a glance. */
export const formatAttentionAbsolute = (since: string | null | undefined): string => {
  const then = since ? Date.parse(since) : Number.NaN;
  if (!Number.isFinite(then)) return 'Unknown time';
  const date = new Date(then);
  return typeof date.toLocaleString === 'function' ? date.toLocaleString() : date.toISOString();
};

/** Display label per wait kind. Unknown kinds render generically instead of dropping the row. */
export const ATTENTION_TYPE_LABELS: Record<string, string> = {
  [AttentionType.PlanApproval]: 'Plan approval',
  [AttentionType.DiscoveryQuestions]: 'Discovery questions',
  [AttentionType.InterruptedTurn]: 'Interrupted turn',
  [AttentionType.PrReview]: 'PR review',
  [AttentionType.Escalation]: 'Escalation'
};

export const ATTENTION_FALLBACK_LABEL = 'Needs attention';

export const attentionLabelFor = (type: string | null | undefined): string =>
  (type && ATTENTION_TYPE_LABELS[type]) || ATTENTION_FALLBACK_LABEL;

/** `undefined` means "not injected, use the browser default"; `null` means "do not persist". */
export const resolveStorage = (injected?: Storage | null): Storage | null => {
  if (injected !== undefined) return injected;
  try {
    return globalThis.window?.localStorage ?? null;
  } catch {
    // Private-mode / disabled-storage throws on access, not merely on write.
    return null;
  }
};

const readItem = (storage: Storage | null, key: string): string | null => {
  try {
    return storage?.getItem(key) ?? null;
  } catch {
    return null;
  }
};

/** Tolerant parse: a hand-edited or half-written key degrades to "no state", never to a crashed cockpit. */
export const parseStampMap = (raw: string | null | undefined): AttentionStampMap => {
  if (!raw) return {};
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return {};
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
  const next: AttentionStampMap = {};
  for (const [id, value] of Object.entries(parsed as Record<string, unknown>)) {
    // `Number(null)` is 0 and `Number('')` is 0, so an untyped coercion would resurrect a junk entry
    // as a stamp from the epoch — which the TTL pass then drops, silently clearing the real state.
    const stamp =
      typeof value === 'number' ? value : typeof value === 'string' && value.trim() ? Number(value) : Number.NaN;
    if (id && Number.isFinite(stamp)) next[id] = stamp;
  }
  return next;
};

/**
 * Drop entries older than `ttlMs`, then evict the oldest until the map fits `maxEntries`.
 *
 * Eviction is also what makes a dismissed wait come back if the operator dismisses hundreds of them:
 * the cap is the documented, bounded failure mode; an unbounded key is the undocumented one.
 */
export const pruneStampMap = (
  map: AttentionStampMap,
  nowMs: number,
  ttlMs: number = ATTENTION_STATE_TTL_MS,
  maxEntries: number = ATTENTION_STATE_MAX_ENTRIES
): AttentionStampMap => {
  const cutoff = nowMs - ttlMs;
  const kept = Object.entries(map)
    .filter(([, stamp]) => stamp >= cutoff)
    .sort((a, b) => a[1] - b[1]);
  const trimmed = kept.length > maxEntries ? kept.slice(kept.length - maxEntries) : kept;
  const next: AttentionStampMap = {};
  for (const [id, stamp] of trimmed) next[id] = stamp;
  return next;
};

export const loadStampMap = (
  storage: Storage | null,
  key: string,
  nowMs: number,
  ttlMs: number = ATTENTION_STATE_TTL_MS
): AttentionStampMap => pruneStampMap(parseStampMap(readItem(storage, key)), nowMs, ttlMs);

/** Persisted through the same prune, and the key is removed when empty so a cleared inbox leaves no litter. */
export const saveStampMap = (
  storage: Storage | null,
  key: string,
  map: AttentionStampMap,
  nowMs: number,
  ttlMs: number = ATTENTION_STATE_TTL_MS,
  maxEntries: number = ATTENTION_STATE_MAX_ENTRIES
): AttentionStampMap => {
  const pruned = pruneStampMap(map, nowMs, ttlMs, maxEntries);
  if (!storage) return pruned;
  try {
    if (Object.keys(pruned).length === 0) storage.removeItem(key);
    else storage.setItem(key, JSON.stringify(pruned));
  } catch {
    // Quota or private mode: the bell still works for this session, it just will not remember across reloads.
  }
  return pruned;
};

export const hasStamp = (map: AttentionStampMap, id: string): boolean =>
  Object.prototype.hasOwnProperty.call(map, id);

/** Returns a new map with `ids` stamped `nowMs`, leaving already-stamped ids untouched. */
export const stampIds = (
  ids: readonly string[],
  existing: AttentionStampMap,
  nowMs: number
): AttentionStampMap => {
  const next: AttentionStampMap = { ...existing };
  for (const id of ids) {
    if (id && !hasStamp(next, id)) next[id] = nowMs;
  }
  return next;
};

export const dropStamps = (existing: AttentionStampMap, ids: Iterable<string>): AttentionStampMap => {
  const next: AttentionStampMap = { ...existing };
  for (const id of ids) delete next[id];
  return next;
};

/**
 * Server order is documented as oldest-first, but the list is re-sorted anyway: a poll that lands out
 * of order (or a future endpoint variant that orders by project) must not make rows jump under a click.
 */
export const sortAttentionItems = (items: readonly AttentionItem[]): AttentionItem[] =>
  items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => {
      const left = a.item?.since ? Date.parse(a.item.since) : Number.NaN;
      const right = b.item?.since ? Date.parse(b.item.since) : Number.NaN;
      // Unparseable ages sort last rather than to epoch, so a malformed row cannot outrank a real blocker.
      const leftKey = Number.isFinite(left) ? left : Number.POSITIVE_INFINITY;
      const rightKey = Number.isFinite(right) ? right : Number.POSITIVE_INFINITY;
      if (leftKey !== rightKey) return leftKey - rightKey;
      return a.index - b.index;
    })
    .map((entry) => entry.item);

/** Defensive read of the rollup body: a partial or legacy payload yields an empty inbox, not a throw. */
export const normalizeAttentionItems = (rollup: AttentionRollup | null | undefined): AttentionItem[] => {
  const items = rollup?.items;
  if (!Array.isArray(items)) return [];
  return items.filter((item): item is AttentionItem => Boolean(item && typeof item === 'object' && item.id));
};

/** Rows the operator has not hidden. Dismissed waits are still pending on the server; they are just quiet here. */
export const visibleAttentionItems = (
  items: readonly AttentionItem[],
  dismissed: AttentionStampMap
): AttentionItem[] => items.filter((item) => !hasStamp(dismissed, item.id));

/** Waiting rows the operator has not opened in the bell yet — the badge count. */
export const unreadAttentionItems = (
  items: readonly AttentionItem[],
  dismissed: AttentionStampMap,
  read: AttentionStampMap
): AttentionItem[] =>
  visibleAttentionItems(items, dismissed).filter((item) => !hasStamp(read, item.id));

export const badgeCount = (count: number): string =>
  count >= BADGE_COLLAPSE_AT ? `${BADGE_COLLAPSE_AT - 1}+` : String(count);

export const uniqueIds = (ids: readonly string[]): string[] => Array.from(new Set(ids.filter(Boolean)));
