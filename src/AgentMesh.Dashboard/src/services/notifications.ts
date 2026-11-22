/**
 * Desktop notification surface for pending-attention waits.
 *
 * This module owns everything between "the server says a job is blocked" and "the operator's machine
 * made a noise and showed a banner". It is deliberately framework-free (no React, no SignalR, no
 * `api`): `App.tsx` feeds it rollups and push events, the `AttentionBell` renders its state, and the
 * unit tests drive it with plain objects. That seam is what makes the annoying parts of notifications —
 * permission timing, dedupe across reloads, autoplay policies — testable at all.
 *
 * ## Why the seen-set is client-side
 *
 * `GET /api/attention` is read-only by design (see `AttentionController`): the server keeps no
 * acknowledgement row, because a wait ends when the underlying state changes and a second source of
 * truth for "someone already looked" would drift. A banner still has to fire exactly once per wait, so
 * the once-only bookkeeping lives here, keyed on the item id `"{jobId}:{type}"`.
 *
 * It is persisted to `localStorage` rather than kept in memory for two concrete failure modes: the
 * cockpit reloads constantly (Vite HMR, auth redirects, an operator closing the tab mid-wait), and an
 * in-memory set re-announces every still-blocked job on each reload — which is how a notification
 * feature becomes the thing people disable.
 *
 * ## Why the chime is WebAudio and `Notification.silent` is true
 *
 * The OS sound is not ours to mute: once a banner is handed to the browser it plays its own tone on the
 * platform's terms, so a master mute toggle would still make noise on some platforms. We therefore ask
 * for a silent banner and play our own two-tone chime through a single `AudioContext` we control, which
 * also (a) works identically on every OS, (b) costs no asset download, and (c) is a no-op in jsdom, so
 * tests never need an audio device. Browsers gate `AudioContext` behind a user gesture, hence
 * {@link NotificationService.noteUserGesture}.
 *
 * ## Why mute suppresses but still records "seen"
 *
 * Muting means "do not tell me about this", not "pretend it never happened". A muted wait is written to
 * the seen-set, so unmuting does not dump a backlog of banners for everything that piled up while the
 * operator was in a meeting. The bell still lists those waits: it renders the rollup, not the seen-set.
 */
import type { AttentionItem } from '../types/attention';
import { AttentionType } from '../types/attention';

/** Banner icon. `public/favicon.svg`, served at the site root. */
export const NOTIFICATION_ICON = '/favicon.svg';
/** Banner badge for OSes that render one separately from `icon`. */
export const NOTIFICATION_BADGE = '/favicon.svg';

/**
 * DOM event dispatched on `window` when a banner is clicked for a wait that lives inside the cockpit.
 * There is no router in this app — selection state is `localStorage` plus `App.tsx` state — so a click
 * writes the keys (for a fresh load) and fires this event (for the already-mounted app). Task 5 listens.
 */
export const ATTENTION_NAVIGATE_EVENT = 'agentmesh:attention-navigate';
/** The tab that shows projects and jobs, per the `activeTab` union in `App.tsx`. */
export const ATTENTION_NAVIGATE_TAB = 'projects';
export const SELECTED_JOB_KEY = 'agentmesh_selected_job_id';
export const SELECTED_PROJECT_KEY = 'agentmesh_selected_project_id';
export const ACTIVE_TAB_KEY = 'agentmesh_active_tab';

/** Persisted map of announced item id -> first-announced epoch ms. */
export const SEEN_STORAGE_KEY = 'agentmesh_attention_seen_v1';
/** Persisted master mute flag. */
export const MUTE_STORAGE_KEY = 'agentmesh_notifications_muted_v1';

/**
 * How long an id stays "already announced", which bounds the persisted map across months of use.
 *
 * It is also, deliberately, a reminder interval: a wait that is still open when its entry ages out
 * announces again. The alternative — a permanent seen key — means a job parked on Tuesday is silent
 * forever after one banner, which is the failure mode that makes an operator miss a blocker entirely.
 * One nudge per day is the cheaper error.
 */
export const DEFAULT_SEEN_TTL_MS = 24 * 60 * 60 * 1000;

/** Hard cap on persisted ids, oldest evicted first, so a pathological host cannot grow the key forever. */
export const DEFAULT_SEEN_MAX_ENTRIES = 300;

export type AttentionPermission = 'unsupported' | 'default' | 'granted' | 'denied';
export type AttentionUnsupportedReason = 'insecure-context' | 'no-notification-api' | null;

/** Outcome of one announcement attempt — returned rather than logged so callers can assert and display it. */
export type AttentionDeliveryReason =
  | 'raised'
  | 'duplicate'
  | 'muted'
  | 'unsupported'
  | 'permission-denied'
  | 'error';

export interface AttentionDelivery {
  itemId: string;
  banner: boolean;
  sound: boolean;
  reason: AttentionDeliveryReason;
}

export interface AttentionNavigateDetail {
  itemId: string;
  jobId: string;
  projectId: string;
  type: string;
  prUrl: string | null;
}

/** Result of reconciling the authoritative rollup against the seen-set. */
export interface AttentionSyncResult {
  raised: AttentionItem[];
  suppressed: AttentionItem[];
  /** Ids dropped from the seen-set because the wait is gone (their banners were closed too). */
  resolvedIds: string[];
  muted: boolean;
}

export interface NotificationServiceOptions {
  /** Injectable clock, for TTL tests. */
  now?: () => number;
  /** Injectable storage; defaults to `window.localStorage`. Pass `null` to disable persistence. */
  storage?: Storage | null;
  seenTtlMs?: number;
  seenMaxEntries?: number;
}

/* ------------------------------------------------------------------ *
 * Minimal structural types for the two browser APIs we touch, so the
 * service can run (and be stubbed) without either existing.
 * ------------------------------------------------------------------ */

type NotificationConstructorLike = {
  new (title: string, options?: NotificationOptions): Notification;
  permission?: string;
  requestPermission?: (
    callback?: (permission: string) => void
  ) => string | Promise<string>;
};

type AudioNodeLike = {
  connect(destination: unknown): unknown;
};

type OscillatorLike = AudioNodeLike & {
  type: string;
  frequency: { setValueAtTime(value: number, when: number): void };
  start(when: number): void;
  stop(when: number): void;
};

type GainLike = AudioNodeLike & {
  gain: {
    setValueAtTime(value: number, when: number): void;
    linearRampToValueAtTime(value: number, when: number): void;
    exponentialRampToValueAtTime(value: number, when: number): void;
  };
};

type AudioContextLike = {
  readonly state?: string;
  readonly currentTime: number;
  readonly destination: unknown;
  resume?: () => Promise<unknown> | unknown;
  createOscillator: () => OscillatorLike;
  createGain: () => GainLike;
};

type AudioContextConstructorLike = new () => AudioContextLike;

const asNotificationCtor = (): NotificationConstructorLike | null => {
  const ctor = (globalThis as { Notification?: unknown }).Notification;
  return typeof ctor === 'function' ? (ctor as NotificationConstructorLike) : null;
};

const asAudioContextCtor = (): AudioContextConstructorLike | null => {
  const g = globalThis as { AudioContext?: unknown; webkitAudioContext?: unknown };
  const ctor = typeof g.AudioContext === 'function' ? g.AudioContext : g.webkitAudioContext;
  return typeof ctor === 'function' ? (ctor as AudioContextConstructorLike) : null;
};

const win = (): (Window & typeof globalThis) | null =>
  typeof window === 'undefined' ? null : window;

/**
 * `isSecureContext` is only false on a plain-HTTP origin reachable off localhost — the case where
 * `window.Notification` is absent and every banner silently fails. Undefined (older jsdom) counts as
 * secure so the capability check degrades to "does the constructor exist" instead of "assume broken".
 */
const isSecureContext = (): boolean => {
  const flag = (globalThis as { isSecureContext?: boolean }).isSecureContext;
  if (typeof flag === 'boolean') return flag;
  const w = win();
  return w ? w.isSecureContext !== false : false;
};

const readStorage = (storage: Storage | null, key: string): string | null => {
  if (!storage) return null;
  try {
    return storage.getItem(key);
  } catch {
    // Safari private mode throws on read. Persistence is an optimisation, never a prerequisite.
    return null;
  }
};

const writeStorage = (storage: Storage | null, key: string, value: string): void => {
  if (!storage) return;
  try {
    storage.setItem(key, value);
  } catch {
    /* quota or private mode: fall back to in-memory state */
  }
};

const removeStorage = (storage: Storage | null, key: string): void => {
  if (!storage) return;
  try {
    storage.removeItem(key);
  } catch {
    /* see writeStorage */
  }
};

const defaultStorage = (): Storage | null => {
  try {
    return win()?.localStorage ?? null;
  } catch {
    return null;
  }
};

/**
 * Only an *absolute* `http`/`https` URL is openable from a banner. Two reasons it is this strict:
 *
 * - The value comes from a timeline card payload an agent wrote, so `javascript:` or `data:` would
 *   otherwise execute in the cockpit's own origin on a single click — a one-click XSS by construction.
 * - Parsing is done without a base on purpose. Resolving `github.com/o/r/pull/1` against the app origin
 *   would turn a malformed value into a same-origin 404, which looks like a working link in a test and a
 *   broken one in production; refusing it falls back to in-app navigation, which always lands somewhere
 *   real.
 */
export const safeExternalUrl = (raw: string | null | undefined): string | null => {
  const value = raw?.trim();
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null;
  } catch {
    return null;
  }
};

/** Two-tone ascending ping (A5 then E6). Short, non-fatiguing, and audibly distinct from chat apps. */
const CHIME_NOTES: ReadonlyArray<{ frequency: number; start: number; duration: number }> = [
  { frequency: 880, start: 0, duration: 0.16 },
  { frequency: 1318.5, start: 0.18, duration: 0.22 }
];
const CHIME_PEAK_GAIN = 0.08;

export class NotificationService {
  private readonly nowFn: () => number;
  private readonly storage: Storage | null;
  private readonly seenTtlMs: number;
  private readonly seenMaxEntries: number;

  /** Banners this tab opened, keyed by `tag` (== item id), so we can close them on resolution. */
  private readonly openBanners = new Map<string, Notification>();
  private readonly listeners = new Set<() => void>();

  private audioContext: AudioContextLike | null = null;
  private gestureSeen = false;
  private inMemorySeen: Record<string, number> | null = null;
  private inMemoryMuted: boolean | null = null;

  constructor(options: NotificationServiceOptions = {}) {
    this.nowFn = options.now ?? (() => Date.now());
    this.storage = options.storage === undefined ? defaultStorage() : options.storage;
    this.seenTtlMs = options.seenTtlMs ?? DEFAULT_SEEN_TTL_MS;
    this.seenMaxEntries = options.seenMaxEntries ?? DEFAULT_SEEN_MAX_ENTRIES;
  }

  /* ---------------------------- capability ---------------------------- */

  unsupportedReason(): AttentionUnsupportedReason {
    if (!isSecureContext()) return 'insecure-context';
    if (asNotificationCtor() === null) return 'no-notification-api';
    return null;
  }

  isSupported(): boolean {
    return this.unsupportedReason() === null;
  }

  permission(): AttentionPermission {
    if (!this.isSupported()) return 'unsupported';
    const raw = asNotificationCtor()?.permission;
    return raw === 'granted' || raw === 'denied' || raw === 'default' ? raw : 'default';
  }

  /**
   * Asks the browser for permission. Call this from a user gesture (a click on the bell), never on page
   * load: browsers now suppress or auto-deny prompts that arrive unprompted, and an immediate prompt
   * trains the operator to dismiss the dialog without reading it.
   *
   * Resolves to the resulting state instead of the raw browser value, and never rejects — a denied or
   * unsupported prompt is an ordinary state the UI renders, not an exception.
   */
  async requestPermission(): Promise<AttentionPermission> {
    if (!this.isSupported()) return 'unsupported';
    const ctor = asNotificationCtor();
    if (!ctor) return 'unsupported';

    try {
      const result = await new Promise<string>((resolve) => {
        let settled = false;
        const done = (value: string) => {
          if (settled) return;
          settled = true;
          resolve(value);
        };
        // Legacy Safari/Edge take a callback and return undefined; Chrome returns a promise.
        const returned = ctor.requestPermission?.((value) => done(String(value)));
        if (returned && typeof (returned as Promise<string>).then === 'function') {
          (returned as Promise<string>).then(
            (value) => done(String(value)),
            () => done('default')
          );
        }
      });
      const next: AttentionPermission =
        result === 'granted' || result === 'denied' || result === 'default' ? result : 'default';
      this.emit();
      return next;
    } catch {
      this.emit();
      return this.permission();
    }
  }

  /* ------------------------------- mute ------------------------------- */

  isMuted(): boolean {
    if (this.inMemoryMuted !== null) return this.inMemoryMuted;
    const persisted = readStorage(this.storage, MUTE_STORAGE_KEY);
    return persisted === 'true';
  }

  setMuted(muted: boolean): void {
    this.inMemoryMuted = muted;
    if (muted) writeStorage(this.storage, MUTE_STORAGE_KEY, 'true');
    else removeStorage(this.storage, MUTE_STORAGE_KEY);
    this.emit();
  }

  toggleMuted(): boolean {
    this.setMuted(!this.isMuted());
    return this.isMuted();
  }

  /* ------------------------------ seen-set ----------------------------- */

  private readSeen(): Record<string, number> {
    if (this.inMemorySeen) return this.inMemorySeen;
    const raw = readStorage(this.storage, SEEN_STORAGE_KEY);
    let parsed: Record<string, number> = {};
    if (raw) {
      try {
        const candidate: unknown = JSON.parse(raw);
        if (candidate && typeof candidate === 'object' && !Array.isArray(candidate)) {
          for (const [id, value] of Object.entries(candidate as Record<string, unknown>)) {
            if (typeof value === 'number' && Number.isFinite(value)) parsed[id] = value;
          }
        }
      } catch {
        // A half-written or hand-edited key must not take notifications down; start clean and rewrite.
        parsed = {};
      }
    }
    this.inMemorySeen = parsed;
    return parsed;
  }

  /**
   * Apply TTL and size pruning, persist the result, and return the ids that aged out.
   *
   * Pruning is a separate pass from the rollup diff because a steady-state poll — same three blocked
   * jobs as last time — changes nothing in the diff and would otherwise never write, so a wait older
   * than the TTL would keep its seen key forever and the reminder in {@link DEFAULT_SEEN_TTL_MS} would
   * silently never fire.
   */
  private writeSeen(seen: Record<string, number>): string[] {
    const cutoff = this.nowFn() - this.seenTtlMs;
    const entries = Object.entries(seen)
      .filter(([, announcedAt]) => announcedAt >= cutoff)
      .sort((a, b) => a[1] - b[1]);

    // Evict oldest first: the entries that survive are the ones whose waits may still be open.
    const kept = entries.slice(Math.max(0, entries.length - this.seenMaxEntries));
    const next: Record<string, number> = {};
    for (const [id, announcedAt] of kept) next[id] = announcedAt;

    const dropped = Object.keys(seen).filter((id) => !(id in next));
    this.inMemorySeen = next;
    for (const id of dropped) this.closeBanner(id);
    writeStorage(this.storage, SEEN_STORAGE_KEY, JSON.stringify(next));
    return dropped;
  }

  seenIds(): string[] {
    return Object.keys(this.readSeen());
  }

  hasSeen(itemId: string): boolean {
    return Object.prototype.hasOwnProperty.call(this.readSeen(), itemId);
  }

  /** When this tab first announced the wait, or null if it never did. The bell uses it for "new since X". */
  firstSeenAt(itemId: string): number | null {
    const value = this.readSeen()[itemId];
    return typeof value === 'number' ? value : null;
  }

  /** Drops every recorded announcement, so the next rollup re-notifies. Intended for tests and a "reset" affordance. */
  resetSeen(): void {
    this.inMemorySeen = {};
    this.openBanners.clear();
    removeStorage(this.storage, SEEN_STORAGE_KEY);
    this.emit();
  }

  private markSeen(seen: Record<string, number>, itemId: string): void {
    if (!Object.prototype.hasOwnProperty.call(seen, itemId)) seen[itemId] = this.nowFn();
  }

  /* ------------------------------ delivery ------------------------------ */

  /**
   * Announce one wait if it has not been announced before.
   *
   * Use this for the `AttentionNeeded` SignalR push, where the event carries exactly one item and there
   * is no authoritative list to reconcile. It shares the persisted seen-set with {@link sync}, so a push
   * and a poll cannot double-banner the same wait — whichever arrives first wins.
   */
  announce(item: AttentionItem): AttentionDelivery {
    if (!item?.id) return this.refuse('', 'error');
    const seen = this.readSeen();
    if (Object.prototype.hasOwnProperty.call(seen, item.id)) {
      return { itemId: item.id, banner: false, sound: false, reason: 'duplicate' };
    }
    const delivery = this.deliver(item);
    // Recorded even when suppressed: muted/denied means "do not announce again", not "announce later".
    this.markSeen(seen, item.id);
    this.writeSeen(seen);
    this.emit();
    return delivery;
  }

  /**
   * Reconcile the authoritative rollup against the seen-set: banner what is new, close what is gone.
   *
   * `items` must be the *whole* inbox (`GET /api/attention`), not a filtered slice — the diff also decides
   * which waits have ended, and a partial list would clear seen entries for waits that are still open and
   * re-banner them on the next poll. Feed single push events to {@link announce} instead.
   */
  sync(items: readonly AttentionItem[]): AttentionSyncResult {
    const seen = this.readSeen();
    const raised: AttentionItem[] = [];
    const suppressed: AttentionItem[] = [];
    const present = new Set<string>();

    for (const item of items ?? []) {
      if (!item?.id) continue;
      present.add(item.id);
      if (Object.prototype.hasOwnProperty.call(seen, item.id)) {
        suppressed.push(item);
        continue;
      }
      this.markSeen(seen, item.id);
      const delivery = this.deliver(item);
      if (delivery.reason === 'raised') raised.push(item);
      else suppressed.push(item);
    }

    const resolvedIds: string[] = [];
    for (const id of Object.keys(seen)) {
      if (present.has(id)) continue;
      delete seen[id];
      this.closeBanner(id);
      resolvedIds.push(id);
    }

    // Always persist: this is also the TTL/cap pruning pass, which must run on a steady-state poll.
    const agedOut = this.writeSeen(seen);
    if (raised.length > 0 || resolvedIds.length > 0 || agedOut.length > 0) this.emit();

    return { raised, suppressed, resolvedIds, muted: this.isMuted() };
  }

  /**
   * Clear everything belonging to one job — the client half of `AttentionResolved`, whose payload is
   * deliberately just a job id. Closes the banner and forgets the seen key, so a job that parks again
   * later announces again instead of staying silently muted by its own history.
   */
  resolveJob(jobId: string): string[] {
    if (!jobId) return [];
    const seen = this.readSeen();
    const prefix = `${jobId}:`;
    const removed: string[] = [];
    for (const id of Object.keys(seen)) {
      if (id !== jobId && !id.startsWith(prefix)) continue;
      delete seen[id];
      this.closeBanner(id);
      removed.push(id);
    }
    if (removed.length > 0) {
      this.writeSeen(seen);
      this.emit();
    }
    return removed;
  }

  /**
   * Withdraw one banner without forgetting it: the operator acknowledged it locally, so the same wait
   * must not banner again on the next poll. The row disappears from the bell when the server stops
   * reporting it, which is the only resolution the platform has.
   */
  dismiss(itemId: string): void {
    this.closeBanner(itemId);
  }

  dismissAll(): void {
    for (const id of Array.from(this.openBanners.keys())) this.closeBanner(id);
  }

  private refuse(itemId: string, reason: AttentionDeliveryReason): AttentionDelivery {
    return { itemId, banner: false, sound: false, reason };
  }

  /** Banner + chime, honouring mute and permission. Never throws: a broken Notification must not break a poll. */
  private deliver(item: AttentionItem): AttentionDelivery {
    if (this.isMuted()) return this.refuse(item.id, 'muted');
    if (!this.isSupported()) return this.refuse(item.id, 'unsupported');
    if (this.permission() !== 'granted') return this.refuse(item.id, 'permission-denied');

    const banner = this.openBanner(item);
    const sound = this.playChime();
    return { itemId: item.id, banner, sound, reason: banner ? 'raised' : 'error' };
  }

  /**
   * Banner body: job, then project, then the prose. The server already caps `detail` at 240 characters
   * (`AttentionQueryService.DetailCharLimit`); the cap here is what keeps a banner readable when an older
   * API replica pushes an untruncated `AttentionNeeded` payload over SignalR.
   */
  private bodyFor(item: AttentionItem): string {
    const context = item.projectName?.trim() ? item.projectName.trim() : null;
    const detail = item.detail?.trim();
    const lines = [item.jobTitle || 'Job'];
    if (context) lines.push(context);
    if (detail) lines.push(detail.length > 240 ? `${detail.slice(0, 239)}…` : detail);
    return lines.join(' · ');
  }

  private openBanner(item: AttentionItem): boolean {
    const ctor = asNotificationCtor();
    if (!ctor) return false;
    const external = safeExternalUrl(item.prUrl);
    try {
      // `tag` is the browser-side dedupe: re-issuing the same tag replaces the banner instead of
      // stacking a second one, which is what keeps a re-parked wait from littering the notification centre.
      const banner = new ctor(item.title || 'Agent Mesh needs you', {
        body: this.bodyFor(item),
        tag: item.id,
        icon: NOTIFICATION_ICON,
        badge: NOTIFICATION_BADGE,
        // Our own chime is the audible signal; see the header note on why the OS tone is suppressed.
        // (`renotify` is left at its default of false: re-issuing the same tag replaces the banner, and
        // stealing focus back for a wait the operator has already dismissed is worse than staying quiet.)
        silent: true,
        // Not `requireInteraction`: a banner that never expires turns one ignored wait into a wall.
        requireInteraction: false,
        data: {
          itemId: item.id,
          jobId: item.jobId,
          projectId: item.projectId,
          type: item.type,
          prUrl: external ?? item.prUrl ?? null
        }
      });

      banner.onclick = () => {
        this.openItem(item);
        this.closeBanner(item.id);
      };
      this.openBanners.set(item.id, banner);
      return true;
    } catch {
      // Chrome throws TypeError when constructing without permission; a denied service worker can also
      // throw. The seen-set is still written by the caller, so this cannot become a retry storm.
      return false;
    }
  }

  private closeBanner(itemId: string): void {
    const banner = this.openBanners.get(itemId);
    if (!banner) return;
    this.openBanners.delete(itemId);
    try {
      banner.onclick = null;
      banner.close();
    } catch {
      /* already closed by the OS */
    }
  }

  /* ------------------------------ chime ------------------------------ */

  /**
   * Record that the operator interacted with the page. Browsers create `AudioContext` in the `suspended`
   * state until a gesture, and a suspended context plays nothing — so the first banner of a session is
   * silent unless a gesture was seen first. `App.tsx` calls this from a pointer/keydown listener.
   */
  noteUserGesture(): void {
    this.gestureSeen = true;
    if (this.audioContext) void this.resumeAudio();
  }

  /** True once a gesture has been observed or an audio context has successfully resumed. */
  audioUnlocked(): boolean {
    return this.gestureSeen;
  }

  private resumeAudio(): void {
    const ctx = this.audioContext;
    if (!ctx || typeof ctx.resume !== 'function') return;
    try {
      const resumed = ctx.resume();
      if (resumed && typeof (resumed as Promise<unknown>).catch === 'function') {
        (resumed as Promise<unknown>).catch(() => undefined);
      }
    } catch {
      /* autoplay policy refused; the next gesture retries */
    }
  }

  /**
   * Play the two-tone chime, creating the shared `AudioContext` on demand. Returns whether sound was
   * actually scheduled, so a caller can tell "silent because muted" from "silent because no WebAudio".
   * Never throws: a machine with no audio device, or an autoplay refusal, must still get its banner.
   */
  playChime(): boolean {
    if (this.isMuted()) return false;
    const ctor = asAudioContextCtor();
    if (!ctor) return false;
    try {
      if (!this.audioContext) this.audioContext = new ctor();
      const ctx = this.audioContext;
      if ((ctx.state ?? 'running') === 'suspended') {
        // Without a gesture the tone would be dropped anyway; report it so the caller can show a hint.
        if (!this.gestureSeen) return false;
        this.resumeAudio();
      }

      const origin = ctx.currentTime ?? 0;
      for (const note of CHIME_NOTES) {
        const start = origin + note.start;
        const end = start + note.duration;
        const oscillator = ctx.createOscillator();
        const envelope = ctx.createGain();
        oscillator.type = 'sine';
        oscillator.frequency.setValueAtTime(note.frequency, start);
        envelope.gain.setValueAtTime(0.0001, start);
        envelope.gain.linearRampToValueAtTime(CHIME_PEAK_GAIN, start + 0.01);
        envelope.gain.exponentialRampToValueAtTime(0.0001, end);
        oscillator.connect(envelope);
        envelope.connect(ctx.destination);
        oscillator.start(start);
        oscillator.stop(end + 0.02);
      }
      return true;
    } catch {
      return false;
    }
  }

  /* --------------------------- click-through --------------------------- */

  /**
   * Follow a wait: a PR review opens the pull request, everything else focuses the cockpit and selects
   * the job. Returns where the click landed so the caller can log or close a popup.
   */
  openItem(item: AttentionItem): AttentionNavigateDetail {
    const detail: AttentionNavigateDetail = {
      itemId: item.id,
      jobId: item.jobId,
      projectId: item.projectId,
      type: item.type,
      prUrl: safeExternalUrl(item.prUrl)
    };
    const target = win();

    if (detail.prUrl) {
      try {
        target?.open(detail.prUrl, '_blank', 'noopener,noreferrer');
      } catch {
        /* popup blocked — the bell list is still the fallback path */
      }
      return detail;
    }

    if (target) {
      writeStorage(this.storage, ACTIVE_TAB_KEY, ATTENTION_NAVIGATE_TAB);
      if (detail.jobId) writeStorage(this.storage, SELECTED_JOB_KEY, detail.jobId);
      if (detail.projectId) writeStorage(this.storage, SELECTED_PROJECT_KEY, detail.projectId);
      try {
        target.dispatchEvent(
          new CustomEvent<AttentionNavigateDetail>(ATTENTION_NAVIGATE_EVENT, { detail })
        );
      } catch {
        /* an environment without CustomEvent still got the storage writes above */
      }
      try {
        target.focus();
      } catch {
        /* headless */
      }
    }
    return detail;
  }

  /** Convenience for the SignalR handler: the payload is the same shape as the rollup item. */
  handleNeeded(item: AttentionItem): AttentionDelivery {
    return this.announce(item);
  }

  /** Convenience for the SignalR handler: clear by job id, which is all the resolved payload carries. */
  handleResolved(event: { jobId?: string | null } | null | undefined): string[] {
    return this.resolveJob(event?.jobId ?? '');
  }

  /* ------------------------------ listeners ------------------------------ */

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  private emit(): void {
    for (const listener of Array.from(this.listeners)) {
      try {
        listener();
      } catch {
        /* one bad subscriber must not stop the others, or the banner that follows */
      }
    }
  }
}

/** The process-wide instance `App.tsx` and the bell share, so the seen-set has exactly one owner. */
export const notifications = new NotificationService();

/** `AttentionType.PrReview` is the one kind whose click target is off-site. */
export const isPullRequestWait = (item: AttentionItem): boolean => item.type === AttentionType.PrReview;
