import { createElement, forwardRef, useCallback, useEffect, useImperativeHandle, useRef, useState } from 'react';
import type { ComponentType, ReactNode } from 'react';
import {
  AlertTriangle,
  Ban,
  Bell,
  BellOff,
  CheckCheck,
  ClipboardCheck,
  Clock,
  ExternalLink,
  GitPullRequest,
  Inbox,
  Loader2,
  MessagesSquare,
  Volume2,
  VolumeX,
  X
} from 'lucide-react';
import { api } from '../services/api';
import { notifications as sharedNotifications, safeExternalUrl } from '../services/notifications';
import type { NotificationService } from '../services/notifications';
import { AttentionType } from '../types/attention';
import type { AttentionItem, AttentionRollup } from '../types/attention';
import {
  DEFAULT_POLL_INTERVAL_MS,
  DISMISSED_STORAGE_KEY,
  PUSH_RECONCILE_DELAY_MS,
  READ_STORAGE_KEY,
  attentionLabelFor,
  badgeCount,
  dropStamps,
  formatAttentionAbsolute,
  formatAttentionAge,
  hasStamp,
  loadStampMap,
  normalizeAttentionItems,
  resolveStorage,
  saveStampMap,
  sortAttentionItems,
  stampIds,
  uniqueIds,
  unreadAttentionItems,
  visibleAttentionItems
} from '../utils/attentionInbox';
import type { AttentionStampMap } from '../utils/attentionInbox';

/* --------------------------------------------------------------------------
 * AttentionBell — the in-cockpit half of the pending-attention surface.
 *
 * The OS banner in `src/services/notifications.ts` is fire-and-forget and dies
 * with the notification centre. The bell is the durable answer to "what did I
 * miss?": it reads `GET /api/attention` — the same rollup the banner path
 * reads — so a wait is never modelled twice. The server owns what counts as
 * blocked; this component owns only what the operator has looked at or hidden.
 *
 * Division of labour with the notification service, which is what makes the
 * SignalR wiring in `App.tsx` (task 5) safe:
 *   - `sync(items)` runs on every successful poll. It banners/chimes ids the
 *     seen-set has never held and closes banners for ids the server stopped
 *     reporting. It must be fed the WHOLE inbox, which is why the bell only
 *     ever passes it a full rollup.
 *   - `announce(item)` handles one push event (`AttentionNeeded`).
 *   - Both share the persisted seen-set, so a push racing a poll cannot
 *     banner the same wait twice.
 *
 * Deliberate omissions: no optimistic resolve (a row leaves when the server
 * stops reporting it, or when the operator dismisses it) and no ack endpoint —
 * the platform has none, and inventing client-side "handled" state would make
 * the bell disagree with the banner path about what is still pending.
 * -------------------------------------------------------------------------- */

/** Icon per wait kind. Unknown kinds fall back to the bell, so a new server kind still renders. */
const TYPE_ICONS: Record<string, ComponentType<{ className?: string }>> = {
  [AttentionType.PlanApproval]: ClipboardCheck,
  [AttentionType.DiscoveryQuestions]: MessagesSquare,
  [AttentionType.InterruptedTurn]: Ban,
  [AttentionType.PrReview]: GitPullRequest,
  [AttentionType.Escalation]: AlertTriangle
};

/** Tone per wait kind, reusing the cockpit's existing status colours (rose = the mesh is hard-blocked). */
const TYPE_TONES: Record<string, string> = {
  [AttentionType.PlanApproval]: 'text-sky-300 bg-sky-500/10 border-sky-500/30',
  [AttentionType.DiscoveryQuestions]: 'text-violet-300 bg-violet-500/10 border-violet-500/30',
  [AttentionType.InterruptedTurn]: 'text-amber-300 bg-amber-500/10 border-amber-500/30',
  [AttentionType.PrReview]: 'text-emerald-300 bg-emerald-500/10 border-emerald-500/30',
  [AttentionType.Escalation]: 'text-rose-300 bg-rose-500/10 border-rose-500/30'
};

const FALLBACK_TONE = 'text-slate-300 bg-slate-500/10 border-slate-500/30';

const defaultFetchRollup = (): Promise<AttentionRollup> => api.getAttention();
const defaultNow = () => Date.now();

export interface AttentionBellProps {
  /** Defaults to the process-wide singleton so the bell and the banner path share one seen-set. */
  service?: NotificationService;
  /** Override the rollup source (tests, or a parent that already polls). Defaults to `api.getAttention`. */
  fetchRollup?: () => Promise<AttentionRollup | null | undefined>;
  /** `0` disables polling — for a parent that drives `refresh()` itself. */
  pollIntervalMs?: number;
  /** `null` disables persistence: read/dismiss state then lives for the page load only. */
  storage?: Storage | null;
  /** Injectable clock for ages and stamps. */
  now?: () => number;
  className?: string;
}

/** Imperative surface `App.tsx` drives from its SignalR handlers. */
export interface AttentionBellHandle {
  /** Re-read the authoritative rollup (also banners anything new). Overlapping calls collapse. */
  refresh(): Promise<void>;
  /** A wait just appeared: banner it now, list it now, reconcile with the server a moment later. */
  pushNeeded(item: AttentionItem | null | undefined): void;
  /** A job stopped waiting: drop its rows and forget its read/dismiss stamps. */
  pushResolved(jobId: string | null | undefined): void;
}

export interface MuteToggleProps {
  service?: NotificationService;
  className?: string;
  /** Icon-only variant for a crowded header. */
  compact?: boolean;
}

/**
 * The master mute control, exported separately so the header can place it outside the bell if it
 * prefers. It reads and writes the flag through the service, so the bell, the chime and a second tab
 * all follow one source of truth (`MUTE_STORAGE_KEY`) instead of keeping a parallel notion of mute.
 */
export const MuteToggle = ({
  service = sharedNotifications,
  className = '',
  compact = false
}: MuteToggleProps) => {
  const [muted, setMuted] = useState(() => service.isMuted());

  useEffect(
    () =>
      service.subscribe(() => {
        setMuted(service.isMuted());
      }),
    [service]
  );

  const toggle = useCallback(() => {
    // A click is the gesture the autoplay policy wants before the next chime can be scheduled.
    service.noteUserGesture();
    service.toggleMuted();
  }, [service]);

  return (
    <button
      type="button"
      onClick={toggle}
      aria-pressed={muted}
      data-testid="notification-mute-toggle"
      title={muted ? 'Desktop alerts are muted — click to unmute' : 'Desktop alerts are on — click to mute'}
      className={`flex items-center gap-1.5 rounded-lg border px-2 py-1 text-[11px] font-semibold transition cursor-pointer ${
        muted
          ? 'border-slate-700/60 bg-slate-800/80 text-slate-400 hover:text-slate-200'
          : 'border-emerald-500/30 bg-emerald-500/10 text-emerald-300 hover:text-emerald-200'
      } ${className}`}
    >
      {muted ? <VolumeX className="w-3.5 h-3.5 shrink-0" /> : <Volume2 className="w-3.5 h-3.5 shrink-0" />}
      {!compact && <span>{muted ? 'Alerts muted' : 'Alerts on'}</span>}
    </button>
  );
};

const iconFor = (type: string): ComponentType<{ className?: string }> => TYPE_ICONS[type] ?? Bell;
const toneFor = (type: string): string => TYPE_TONES[type] ?? FALLBACK_TONE;

function AttentionRow({
  item,
  unread,
  nowMs,
  onOpen,
  onDismiss
}: {
  item: AttentionItem;
  unread: boolean;
  nowMs: number;
  onOpen: (item: AttentionItem) => void;
  onDismiss: (item: AttentionItem) => void;
}) {
  const external = safeExternalUrl(item.prUrl);
  const detail = item.detail?.trim() || '';
  const projectName = item.projectName?.trim() || '';
  const label = attentionLabelFor(item.type);

  return (
    <li
      data-testid={`attention-row-${item.id}`}
      className="flex items-start gap-2 px-3 py-2.5 border-b border-slate-800/60 last:border-b-0"
    >
      <span
        className={`mt-0.5 w-6 h-6 rounded-md border flex items-center justify-center shrink-0 ${toneFor(item.type)}`}
        aria-hidden="true"
      >
        {createElement(iconFor(item.type), { className: 'w-3.5 h-3.5' })}
      </span>

      <div className="min-w-0 flex-1">
        <button
          type="button"
          onClick={() => onOpen(item)}
          data-testid={`attention-open-${item.id}`}
          className="block w-full text-left group"
        >
          <div className="flex items-center gap-1.5 min-w-0">
            {unread && (
              <span
                data-testid={`attention-unread-dot-${item.id}`}
                className="w-1.5 h-1.5 rounded-full bg-rose-400 shrink-0"
                aria-hidden="true"
              />
            )}
            <span className="text-[10px] font-mono uppercase tracking-wide text-slate-500 shrink-0">{label}</span>
            {external && <ExternalLink className="w-3 h-3 text-slate-500 shrink-0" aria-hidden="true" />}
          </div>

          <div className="text-xs font-semibold text-slate-100 group-hover:text-sky-300 transition truncate">
            {item.jobTitle || item.jobId}
          </div>
          {projectName && <div className="text-[11px] text-slate-500 truncate">{projectName}</div>}
          {detail && <p className="text-[11px] text-slate-400 mt-0.5 line-clamp-2 break-words">{detail}</p>}

          <div
            className="flex items-center gap-1 text-[10px] text-slate-500 mt-1"
            title={formatAttentionAbsolute(item.since)}
          >
            <Clock className="w-3 h-3 shrink-0" aria-hidden="true" />
            <span>waiting {formatAttentionAge(item.since, nowMs)}</span>
            {typeof item.prNumber === 'number' && item.prNumber > 0 && (
              <span className="font-mono text-slate-600">#{item.prNumber}</span>
            )}
          </div>
        </button>
      </div>

      <button
        type="button"
        onClick={() => onDismiss(item)}
        data-testid={`attention-dismiss-${item.id}`}
        aria-label={`Dismiss ${label} for ${item.jobTitle || item.jobId}`}
        title="Hide this wait here until it clears and parks again — it does not answer the agent"
        className="text-slate-600 hover:text-slate-300 transition cursor-pointer shrink-0 p-1 -mr-1"
      >
        <X className="w-3.5 h-3.5" />
      </button>
    </li>
  );
}

/**
 * Bell + unread badge + inbox list + dismiss.
 *
 * Mount it in the header and drive it from the SignalR handlers:
 * `bellRef.current?.pushNeeded(payload)` on `AttentionNeeded` and
 * `bellRef.current?.pushResolved(payload.jobId)` on `AttentionResolved`.
 */
export const AttentionBell = forwardRef<AttentionBellHandle, AttentionBellProps>(function AttentionBell(
  {
    service = sharedNotifications,
    fetchRollup = defaultFetchRollup,
    pollIntervalMs = DEFAULT_POLL_INTERVAL_MS,
    storage,
    now = defaultNow,
    className = ''
  },
  ref
) {
  const store = resolveStorage(storage);
  const clock = now;

  const [items, setItems] = useState<AttentionItem[]>([]);
  const [dismissed, setDismissed] = useState<AttentionStampMap>(() =>
    loadStampMap(store, DISMISSED_STORAGE_KEY, clock())
  );
  const [read, setRead] = useState<AttentionStampMap>(() => loadStampMap(store, READ_STORAGE_KEY, clock()));
  const [muted, setMuted] = useState(() => service.isMuted());
  const [permission, setPermission] = useState(() => service.permission());
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Refs mirror the state the handlers must read synchronously: a push landing between two renders,
  // or a dismiss clicked twice, must not act on a stale snapshot.
  const itemsRef = useRef<AttentionItem[]>([]);
  const dismissedRef = useRef<AttentionStampMap>(dismissed);
  const readRef = useRef<AttentionStampMap>(read);
  const inFlightRef = useRef<Promise<void> | null>(null);
  const trailingRefreshRef = useRef(false);
  const refreshRef = useRef<() => Promise<void>>(() => Promise.resolve());
  const reconcileTimerRef = useRef<number | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
  const buttonRef = useRef<HTMLButtonElement | null>(null);

  const commitItems = useCallback(
    (next: AttentionItem[]) => {
      itemsRef.current = next;
      setItems(next);
    },
    []
  );

  const markRead = useCallback(
    (ids: readonly string[]) => {
      const missing = uniqueIds(ids).filter((id) => !hasStamp(readRef.current, id));
      if (missing.length === 0) return;
      const next = stampIds(missing, readRef.current, clock());
      readRef.current = next;
      setRead(next);
      saveStampMap(store, READ_STORAGE_KEY, next, clock());
    },
    [clock, store]
  );

  /** Forget stamps for waits that no longer exist, so a job that re-parks later reads as new again. */
  const forgetIds = useCallback(
    (ids: readonly string[]) => {
      const gone = new Set(uniqueIds(ids));
      if (gone.size === 0) return;

      const nextRead = dropStamps(readRef.current, gone);
      if (Object.keys(nextRead).length !== Object.keys(readRef.current).length) {
        readRef.current = nextRead;
        setRead(nextRead);
        saveStampMap(store, READ_STORAGE_KEY, nextRead, clock());
      }

      const nextDismissed = dropStamps(dismissedRef.current, gone);
      if (Object.keys(nextDismissed).length !== Object.keys(dismissedRef.current).length) {
        dismissedRef.current = nextDismissed;
        setDismissed(nextDismissed);
        saveStampMap(store, DISMISSED_STORAGE_KEY, nextDismissed, clock());
      }
    },
    [clock, store]
  );

  const refresh = useCallback((): Promise<void> => {
    // Coalesce, do not queue: a focus-driven refresh landing mid-poll adds nothing the next poll lacks,
    // and letting both run would fire two `sync` passes — two banner sweeps — per tick. A request that
    // arrives while one is in flight is remembered and run once after it settles, so a Retry click is
    // never silently swallowed (which is what a dropped refresh looks like to the operator).
    if (inFlightRef.current) {
      trailingRefreshRef.current = true;
      return inFlightRef.current;
    }

    const run = async (): Promise<void> => {
      setLoading(true);
      try {
        const rollup = await fetchRollup();
        const next = sortAttentionItems(normalizeAttentionItems(rollup));
        commitItems(next);
        setError(null);
        // Only a successful read may reconcile banners: syncing an empty list because the API 500'd
        // would close every open banner and clear the seen-set, so the next good poll re-notifies
        // waits the operator already saw.
        const result = service.sync(next);
        if (result.resolvedIds.length > 0) forgetIds(result.resolvedIds);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Attention inbox unavailable');
      } finally {
        setLoading(false);
      }
    };

    const promise = run().finally(() => {
      if (inFlightRef.current === promise) inFlightRef.current = null;
      if (trailingRefreshRef.current) {
        trailingRefreshRef.current = false;
        // Read through the ref so a re-render mid-flight cannot restart the timer with a stale closure.
        void refreshRef.current();
      }
    });
    inFlightRef.current = promise;
    return promise;
  }, [commitItems, fetchRollup, forgetIds, service]);

  useEffect(() => {
    refreshRef.current = refresh;
  }, [refresh]);

  const scheduleReconcile = useCallback(
    (delayMs = PUSH_RECONCILE_DELAY_MS) => {
      if (typeof window === 'undefined') return;
      if (reconcileTimerRef.current !== null) window.clearTimeout(reconcileTimerRef.current);
      reconcileTimerRef.current = window.setTimeout(() => {
        reconcileTimerRef.current = null;
        void refresh();
      }, delayMs);
    },
    [refresh]
  );

  const pushNeeded = useCallback(
    (item: AttentionItem | null | undefined) => {
      if (!item?.id) return;
      // Banner immediately — that is the point of a push — then reconcile: the event is a hint, not a
      // fact, and only the rollup decides whether the row is still there a moment later.
      service.announce(item);
      if (!itemsRef.current.some((existing) => existing.id === item.id)) {
        commitItems(sortAttentionItems([...itemsRef.current, item]));
      }
      scheduleReconcile();
    },
    [commitItems, scheduleReconcile, service]
  );

  const pushResolved = useCallback(
    (jobId: string | null | undefined) => {
      if (!jobId) return;
      const cleared = service.resolveJob(jobId);
      const ids = itemsRef.current.filter((item) => item.jobId === jobId).map((item) => item.id);
      commitItems(itemsRef.current.filter((item) => item.jobId !== jobId));
      forgetIds(uniqueIds([...cleared, ...ids]));
    },
    [commitItems, forgetIds, service]
  );

  useImperativeHandle(ref, () => ({ refresh, pushNeeded, pushResolved }), [pushNeeded, pushResolved, refresh]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useEffect(() => {
    if (!(pollIntervalMs > 0) || typeof window === 'undefined') return undefined;
    const timer = window.setInterval(() => {
      // A laptop left in a background tab for two days should not fire ~5,700 polls. The foreground
      // listener below catches the operator up the moment they look at the tab.
      if (typeof document === 'undefined' || document.visibilityState === 'visible') void refresh();
    }, pollIntervalMs);
    return () => window.clearInterval(timer);
  }, [pollIntervalMs, refresh]);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof document === 'undefined') return undefined;
    const onForeground = () => {
      if (document.visibilityState === 'visible') void refresh();
    };
    window.addEventListener('focus', onForeground);
    document.addEventListener('visibilitychange', onForeground);
    return () => {
      window.removeEventListener('focus', onForeground);
      document.removeEventListener('visibilitychange', onForeground);
    };
  }, [refresh]);

  // Clear a pending push-reconcile timer on unmount so it cannot fire into a dead component.
  useEffect(
    () => () => {
      if (reconcileTimerRef.current !== null && typeof window !== 'undefined') {
        window.clearTimeout(reconcileTimerRef.current);
      }
    },
    []
  );

  useEffect(
    () =>
      service.subscribe(() => {
        setMuted(service.isMuted());
        setPermission(service.permission());
      }),
    [service]
  );

  useEffect(() => {
    if (!open || typeof document === 'undefined') return undefined;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    const onPointerDown = (event: Event) => {
      const target = event.target as Node | null;
      if (!target) return;
      if (panelRef.current?.contains(target) || buttonRef.current?.contains(target)) return;
      setOpen(false);
    };
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('pointerdown', onPointerDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      document.removeEventListener('pointerdown', onPointerDown);
    };
  }, [open]);

  const openItem = useCallback(
    (item: AttentionItem) => {
      service.noteUserGesture();
      markRead([item.id]);
      // A PR review opens the pull request; everything else selects the job and fires the navigate
      // event App.tsx listens for. Same code path as clicking the OS banner, deliberately.
      service.openItem(item);
      setOpen(false);
    },
    [markRead, service]
  );

  const dismissItem = useCallback(
    (item: AttentionItem) => {
      service.noteUserGesture();
      const nextDismissed = stampIds([item.id], dismissedRef.current, clock());
      dismissedRef.current = nextDismissed;
      setDismissed(nextDismissed);
      saveStampMap(store, DISMISSED_STORAGE_KEY, nextDismissed, clock());
      markRead([item.id]);
      // Withdraw the banner but keep the seen key: "I saw it" must not become "announce it again".
      service.dismiss(item.id);
    },
    [clock, markRead, service, store]
  );

  const dismissAllVisible = useCallback(() => {
    const ids = visibleAttentionItems(itemsRef.current, dismissedRef.current).map((item) => item.id);
    if (ids.length === 0) return;
    const nextDismissed = stampIds(ids, dismissedRef.current, clock());
    dismissedRef.current = nextDismissed;
    setDismissed(nextDismissed);
    saveStampMap(store, DISMISSED_STORAGE_KEY, nextDismissed, clock());
    markRead(ids);
    // Closes every open OS banner too; the seen keys stay, so nothing re-nudges on the next poll.
    service.dismissAll();
  }, [clock, markRead, service, store]);

  const toggleOpen = useCallback(() => {
    service.noteUserGesture();
    if (open) {
      setOpen(false);
      return;
    }
    // Opening the bell IS the acknowledgement: the badge means "waits you have not looked at", and
    // looking is the only way it clears short of dismissing rows one by one.
    markRead(visibleAttentionItems(itemsRef.current, dismissedRef.current).map((item) => item.id));
    setOpen(true);
    void refresh();
  }, [markRead, open, refresh, service]);

  const requestPermission = useCallback(async () => {
    service.noteUserGesture();
    await service.requestPermission();
    setPermission(service.permission());
  }, [service]);

  const nowMs = clock();
  const visible = visibleAttentionItems(items, dismissed);
  const unread = unreadAttentionItems(items, dismissed, read);
  const unreadCount = unread.length;
  const hiddenCount = items.length - visible.length;
  const unreadIds = new Set(unread.map((item) => item.id));
  const pendingCount = visible.length;

  const alertState = muted ? 'desktop alerts muted' : 'desktop alerts on';
  const bellLabel =
    unreadCount > 0
      ? `Attention inbox: ${unreadCount} unread, ${pendingCount} waiting on you (${alertState})`
      : pendingCount > 0
        ? `Attention inbox: ${pendingCount} waiting on you, all seen (${alertState})`
        : `Attention inbox: nothing waiting (${alertState})`;

  let footerHint: ReactNode = null;
  if (permission === 'default') {
    footerHint = (
      <button
        type="button"
        onClick={() => void requestPermission()}
        data-testid="attention-enable-desktop-alerts"
        className="text-[11px] font-semibold text-sky-300 hover:text-sky-200 transition cursor-pointer"
      >
        Enable desktop alerts
      </button>
    );
  } else if (permission === 'denied') {
    footerHint = (
      <span data-testid="attention-desktop-alerts-denied" className="text-[10px] text-slate-500 text-right">
        Desktop alerts blocked — allow them in site settings
      </span>
    );
  } else if (permission === 'unsupported') {
    footerHint = (
      <span data-testid="attention-desktop-alerts-unsupported" className="text-[10px] text-slate-500 text-right">
        {service.unsupportedReason() === 'insecure-context'
          ? 'Desktop alerts need HTTPS or localhost'
          : 'Desktop alerts unsupported here'}
      </span>
    );
  } else if (permission === 'granted') {
    footerHint = (
      <span data-testid="attention-desktop-alerts-granted" className="text-[10px] text-emerald-400/80">
        Desktop alerts on
      </span>
    );
  }

  return (
    <div className={`relative shrink-0 ${className}`} data-testid="attention-bell">
      <button
        ref={buttonRef}
        type="button"
        onClick={toggleOpen}
        aria-expanded={open}
        aria-haspopup="true"
        aria-label={bellLabel}
        title={bellLabel}
        data-testid="attention-bell-button"
        className={`relative flex items-center justify-center w-9 h-9 rounded-lg border transition cursor-pointer ${
          open
            ? 'border-sky-500/40 bg-sky-600/20 text-sky-200'
            : 'border-slate-700/60 bg-slate-800/80 text-slate-300 hover:text-white hover:bg-slate-700'
        }`}
      >
        {muted ? <BellOff className="w-4 h-4" /> : <Bell className="w-4 h-4" />}

        {unreadCount > 0 && (
          <span
            data-testid="attention-badge"
            className="absolute -top-1 -right-1 min-w-[16px] h-[16px] px-1 rounded-full bg-rose-500 text-white text-[9px] font-bold flex items-center justify-center leading-none border border-slate-900"
          >
            {badgeCount(unreadCount)}
          </span>
        )}

        {unreadCount === 0 && pendingCount > 0 && (
          <span
            data-testid="attention-quiet-dot"
            className="absolute top-1.5 right-1.5 w-1.5 h-1.5 rounded-full bg-slate-400"
            aria-hidden="true"
          />
        )}

        {error && (
          <span
            data-testid="attention-error-dot"
            className="absolute -bottom-0.5 left-1/2 -translate-x-1/2 w-1.5 h-1.5 rounded-full bg-amber-400"
            title={`Attention refresh failed: ${error}`}
            aria-hidden="true"
          />
        )}
      </button>

      {open && (
        <div
          ref={panelRef}
          role="dialog"
          aria-label="Pending attention"
          data-testid="attention-panel"
          className="absolute right-0 top-full mt-2 w-[340px] max-w-[calc(100vw-1.5rem)] max-h-[70vh] overflow-y-auto rounded-xl border border-slate-800 bg-slate-900 shadow-2xl shadow-black/50 z-50"
        >
          <div className="sticky top-0 bg-slate-900/95 backdrop-blur border-b border-slate-800 px-3 py-2 flex items-center justify-between gap-2">
            <div className="flex items-center gap-1.5 min-w-0">
              <Inbox className="w-3.5 h-3.5 text-sky-400 shrink-0" aria-hidden="true" />
              <span className="text-xs font-bold text-white">Needs attention</span>
              {pendingCount > 0 && (
                <span className="text-[10px] font-mono px-1.5 py-0.2 rounded-full bg-slate-800 text-slate-300">
                  {pendingCount}
                </span>
              )}
              {loading && <Loader2 className="w-3 h-3 animate-spin text-slate-500" aria-hidden="true" />}
            </div>

            <div className="flex items-center gap-1 shrink-0">
              <button
                type="button"
                onClick={dismissAllVisible}
                disabled={pendingCount === 0}
                data-testid="attention-dismiss-all"
                aria-label="Dismiss all waiting items"
                title="Hide every listed wait until each one clears and parks again"
                className="text-slate-500 hover:text-slate-200 disabled:opacity-40 disabled:cursor-not-allowed transition cursor-pointer p-1"
              >
                <CheckCheck className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                onClick={() => setOpen(false)}
                data-testid="attention-close"
                aria-label="Close attention inbox"
                className="text-slate-500 hover:text-slate-200 transition cursor-pointer p-1"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            </div>
          </div>

          {error && (
            <div
              data-testid="attention-error"
              className="px-3 py-2 border-b border-rose-500/30 bg-rose-950/40 text-[11px] text-rose-200 flex items-start justify-between gap-2"
            >
              <span className="min-w-0 break-words">
                <span className="font-semibold">Refresh failed:</span> {error}
              </span>
              <button
                type="button"
                onClick={() => void refresh()}
                data-testid="attention-retry"
                className="font-semibold text-rose-100 hover:text-white underline shrink-0 cursor-pointer"
              >
                Retry
              </button>
            </div>
          )}

          {pendingCount === 0 ? (
            <div data-testid="attention-empty" className="px-3 py-6 text-center">
              <div className="text-slate-500 flex items-center justify-center gap-1.5 text-xs font-semibold">
                <Inbox className="w-3.5 h-3.5" aria-hidden="true" />
                {error ? 'Nothing to show yet' : 'All clear'}
              </div>
              <p className="text-[11px] text-slate-600 mt-1">
                {error
                  ? 'The rollup could not be read. Retry to reconnect.'
                  : hiddenCount > 0
                    ? `You dismissed ${hiddenCount} wait${hiddenCount === 1 ? '' : 's'} — they come back when the job clears and parks again.`
                    : 'Nothing is waiting on you across any project right now.'}
              </p>
            </div>
          ) : (
            <ul data-testid="attention-list">
              {visible.map((item) => (
                <AttentionRow
                  key={item.id}
                  item={item}
                  unread={unreadIds.has(item.id)}
                  nowMs={nowMs}
                  onOpen={openItem}
                  onDismiss={dismissItem}
                />
              ))}
            </ul>
          )}

          {pendingCount > 0 && hiddenCount > 0 && (
            <div
              data-testid="attention-hidden-note"
              className="px-3 py-1.5 text-[10px] text-slate-600 border-t border-slate-800/60"
            >
              {hiddenCount} dismissed wait{hiddenCount === 1 ? '' : 's'} hidden — they return if the job
              clears and parks again
            </div>
          )}

          <div className="sticky bottom-0 bg-slate-900/95 backdrop-blur border-t border-slate-800 px-3 py-2 flex items-center justify-between gap-2">
            <MuteToggle service={service} />
            {footerHint}
          </div>
        </div>
      )}
    </div>
  );
});
