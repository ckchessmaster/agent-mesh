import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { ChevronDown, ChevronRight, ChevronUp, MessageSquare, RefreshCw, Trash2 } from 'lucide-react';
import { api } from '../services/api';
import type { AgentSession } from '../types';
import {
  SESSION_PREVIEW_LIMIT,
  collapseSessions,
  decorateSessions,
  mergeSessionsById,
  resolveHiddenCount,
  sortSessionsByRecency,
  truncateLabel
} from '../utils/sessionDisplay';

interface SessionListSectionProps {
  /** 'persona' expands via /api/personas/{id}/sessions, 'driver' via /api/agents/{id}/sessions. */
  ownerKind: 'persona' | 'driver';
  ownerId: string;
  /** Rows the client already holds for this owner (from the capped global session window). */
  sessions: AgentSession[];
  /**
   * The owner's true session count as reported by the persona/driver list endpoint. Authoritative
   * for the "Show all (N)" label, because the global window cannot see past its cap.
   */
  serverTotal?: number | null;
  selectedSessionId: string | null;
  /** Suppresses the selected highlight while the new-session composer owns the main panel. */
  isCreatingSession: boolean;
  deletingSessionId: string | null;
  onSelectSession: (session: AgentSession) => void;
  onRequestDelete: (sessionId: string) => void;
  onCancelDelete: () => void;
  onConfirmDelete: (sessionId: string) => void;
  /** Driver cards surface which persona owned a row; persona cards do not need it. */
  showPersonaBadge?: boolean;
}

/** Identity of the rows we hold, in display order. Changes only when a session is added or removed. */
const rowSignature = (sessions: AgentSession[]) => sessions.map((s) => s.id).join('|');

/**
 * Bounded session list for a persona or driver card.
 *
 * Every job turn creates a brand new session, so a single chatty job used to push these cards past a
 * hundred rows and the card grew without limit inside the sidebar. The list now renders a fixed
 * preview and lazily pulls the owner's full history from the per-owner endpoint on demand, so the
 * card height is bounded by design rather than by luck.
 *
 * Expansion state is deliberately not persisted: it lives in this component and is dropped whenever
 * the card unmounts (tab switch, reload) or the owner's row set changes underneath us.
 */
export function SessionListSection({
  ownerKind,
  ownerId,
  sessions,
  serverTotal,
  selectedSessionId,
  isCreatingSession,
  deletingSessionId,
  onSelectSession,
  onRequestDelete,
  onCancelDelete,
  onConfirmDelete,
  showPersonaBadge = false
}: SessionListSectionProps) {
  const [expanded, setExpanded] = useState(false);
  const [expandedSessions, setExpandedSessions] = useState<AgentSession[] | null>(null);
  const [expandedTotal, setExpandedTotal] = useState<number | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const baseSorted = sortSessionsByRecency(sessions);

  // Auto-collapse when the owner's row set changes (a turn was created, a session deleted): the
  // cached page is then provably incomplete, and the operator should land back on "most recent N"
  // rather than a stale expansion. Keyed on ids, NOT on order: `updatedAt` ticks every few seconds
  // while a job runs, so an order-based signature would collapse the card mid-read.
  const signature = rowSignature(baseSorted);

  // Guards against a slow response landing after the operator already collapsed the card (or the
  // owner changed underneath us), which would otherwise populate a list nobody is looking at.
  //
  // Keyed on `signature` as well as the owner, because a row-set change collapses the card through
  // the render-phase branch below and that path used to leave a request in flight. `total` prefers
  // `expandedTotal` over `serverTotal`, so an abandoned page counted before a mass delete kept
  // advertising the old history: "Show all (35)" over a persona that now has 6 sessions. That is the
  // regression pinned by SessionListAutoCollapse.test.tsx.
  //
  // A layout effect, deliberately not a render-phase write: refs must not be written during render
  // (oxlint's react(refs) rule flags it), and the write would be lost if React discarded a
  // concurrent render, invalidating a live request without collapsing anything and leaving the card
  // spinning. Commit-time is also what keeps the ordering right against a promise continuation in a
  // real browser, where a resolved microtask can run before a scheduled passive effect. Note that
  // the test suite cannot tell those two apart: RTL's act() flushes passive effects synchronously,
  // so the layout choice is verified by reasoning and by the lint rule, not by a failing test.
  // No unmount bump: after unmount there is no card left to update.
  const requestIdRef = useRef(0);
  useLayoutEffect(() => {
    requestIdRef.current += 1;
  }, [ownerKind, ownerId, signature]);

  const [prevSignature, setPrevSignature] = useState(signature);
  if (prevSignature !== signature) {
    setPrevSignature(signature);
    if (expanded || expandedSessions) {
      setExpanded(false);
      setExpandedSessions(null);
      setExpandedTotal(null);
      setLoadError(null);
      setIsLoading(false);
    }
  }

  const loadFullList = useCallback(async () => {
    const requestId = ++requestIdRef.current;
    setIsLoading(true);
    setLoadError(null);
    try {
      // No take/skip: expansion asks for the owner's whole history in one shot.
      const page =
        ownerKind === 'persona'
          ? await api.getPersonaSessionsPage(ownerId)
          : await api.getAgentSessionsPage(ownerId);

      if (requestId !== requestIdRef.current) return;

      setExpandedSessions(page.items);
      setExpandedTotal(page.total ?? Math.max(baseSorted.length, page.items.length));
    } catch {
      if (requestId !== requestIdRef.current) return;
      // Degraded, not broken: fall back to the rows we already hold and say the rest is unavailable.
      setExpandedSessions([]);
      setExpandedTotal(baseSorted.length);
      setLoadError(`Could not load the full ${ownerKind} session list.`);
    } finally {
      if (requestId === requestIdRef.current) setIsLoading(false);
    }
  }, [ownerKind, ownerId, baseSorted.length]);

  const toggle = () => {
    if (expanded) {
      requestIdRef.current += 1;
      setExpanded(false);
      setExpandedSessions(null);
      setExpandedTotal(null);
      setLoadError(null);
      setIsLoading(false);
      return;
    }
    setExpanded(true);
    void loadFullList();
  };

  // While expanded, keep the page current by merging in whatever the poller just handed us.
  const visibleSource =
    expanded && expandedSessions ? mergeSessionsById(baseSorted, expandedSessions) : baseSorted;

  const decorated = decorateSessions(visibleSource);
  const collapsed = expanded
    ? { visible: decorated, hiddenCount: 0 }
    : collapseSessions(decorated, SESSION_PREVIEW_LIMIT, selectedSessionId);

  const total = expandedTotal ?? serverTotal ?? null;
  const hiddenCount = resolveHiddenCount(baseSorted.length, SESSION_PREVIEW_LIMIT, total);
  const showToggle = expanded || hiddenCount > 0;

  return (
    <div className="px-2 pb-2 space-y-1">
      {visibleSource.length === 0 ? (
        <div className="px-2 py-1.5 text-[11px] text-slate-500 italic">No sessions yet</div>
      ) : (
        collapsed.visible.map((item) => {
          const { session } = item;
          const isSelected = session.id === selectedSessionId && !isCreatingSession;
          const isThinking = session.status === 'thinking';

          return (
            <div key={session.id} className="space-y-1">
              <div
                onClick={() => onSelectSession(session)}
                data-testid={`session-row-${session.id}`}
                className={`group w-full text-left px-2.5 py-2 rounded-lg text-xs flex items-center justify-between gap-2 transition cursor-pointer ${
                  isSelected
                    ? 'bg-sky-500/15 text-sky-300 border border-sky-500/30 font-medium'
                    : 'text-slate-400 hover:bg-slate-800/50 hover:text-slate-200 border border-transparent'
                }`}
              >
                <div className="flex items-center gap-2 min-w-0 flex-1">
                  {session.status === 'waiting_for_input' ? (
                    <span
                      className="w-2.5 h-2.5 rounded-full bg-amber-400 border border-amber-300 animate-ping shrink-0"
                      title="Human input required"
                    />
                  ) : isThinking ? (
                    <RefreshCw className="w-3 h-3 text-amber-400 animate-spin shrink-0" />
                  ) : (
                    <MessageSquare className="w-3 h-3 text-slate-500 shrink-0" />
                  )}
                  <span className="truncate" title={item.fullTitle || item.label}>
                    {truncateLabel(item.label)}
                  </span>
                  {showPersonaBadge && session.personaId && (
                    <span className="text-[10px] font-mono px-1.5 py-0.2 rounded bg-indigo-500/15 text-indigo-300 border border-indigo-500/30 shrink-0">
                      @{session.personaId}
                    </span>
                  )}
                </div>

                <div className="flex items-center gap-1 shrink-0">
                  <button
                    type="button"
                    onClick={(e) => {
                      e.stopPropagation();
                      onRequestDelete(session.id);
                    }}
                    className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-rose-500/20 text-slate-400 hover:text-rose-400 transition cursor-pointer"
                    title={`Delete session ${session.title}`}
                  >
                    <Trash2 className="w-3 h-3" />
                  </button>
                  <ChevronRight className="w-3 h-3 opacity-50 shrink-0 group-hover:opacity-100" />
                </div>
              </div>

              {deletingSessionId === session.id && (
                <div className="p-2 rounded-lg bg-rose-950/70 border border-rose-500/40 text-[11px] text-rose-200 space-y-1.5">
                  <p className="truncate">
                    Delete <strong>{session.title}</strong>?
                  </p>
                  <div className="flex justify-end gap-1.5">
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation();
                        onCancelDelete();
                      }}
                      className="px-2 py-0.5 rounded bg-slate-800 text-slate-300 hover:text-white cursor-pointer"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation();
                        onConfirmDelete(session.id);
                      }}
                      className="px-2.5 py-0.5 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold cursor-pointer"
                    >
                      Confirm Delete
                    </button>
                  </div>
                </div>
              )}
            </div>
          );
        })
      )}

      {showToggle && (
        <button
          type="button"
          onClick={toggle}
          data-testid={`${expanded ? 'show-fewer' : 'show-all'}-${ownerKind}-sessions-${ownerId}`}
          className="w-full px-2.5 py-1.5 rounded-lg text-[11px] font-medium flex items-center justify-center gap-1 text-slate-400 hover:text-sky-300 hover:bg-slate-800/50 border border-transparent hover:border-slate-800 transition cursor-pointer"
        >
          {isLoading ? (
            <RefreshCw className="w-3 h-3 animate-spin" />
          ) : expanded ? (
            <ChevronUp className="w-3 h-3" />
          ) : (
            <ChevronDown className="w-3 h-3" />
          )}
          <span>{expanded ? 'Show fewer' : `Show all (${hiddenCount})`}</span>
        </button>
      )}

      {loadError && (
        <p
          data-testid={`session-list-error-${ownerKind}-${ownerId}`}
          className="px-2.5 text-[10px] text-amber-400/90 leading-relaxed"
          role="status"
        >
          {loadError} Showing the {baseSorted.length} most recent.
        </p>
      )}
    </div>
  );
}
