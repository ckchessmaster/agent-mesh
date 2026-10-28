import type { AgentSession } from '../types';

/**
 * Persona/Driver cards used to render every session a persona or driver ever had. Because every
 * job turn creates a brand new session, a single chatty job pushed the sidebar past a hundred
 * rows and the cards grew without bound. The sidebar now renders a bounded preview and offers an
 * explicit "show all" affordance.
 */
export const SESSION_PREVIEW_LIMIT = 5;

/** Titles the backend/CLI fall back to when nobody actually named the session. */
const PLACEHOLDER_TITLES = new Set([
  '',
  'session',
  'sessions',
  'new session',
  'untitled',
  'untitled session',
  'unnamed',
  'unnamed session',
  'chat',
  'conversation',
  'n/a',
  'null',
  'undefined',
  'job turn'
]);

const JOB_TURN_PREFIX = /^\s*job\s*turn\s*[:#-]?\s*/i;
const TRAILING_PERSONA_SUFFIX = /\s*\(\s*@[A-Za-z0-9_.-]+\s*\)\s*$/;

export interface SessionMetadataLite {
  jobId?: string;
  jobTitle?: string;
  projectId?: string;
  projectName?: string;
  personaId?: string;
  personaName?: string;
}

/**
 * Session metadata is written by several producers (API, CLI, dashboard) and has historically
 * used both camelCase and PascalCase keys. Parsing is best-effort by design: a malformed or
 * unexpected payload must degrade to "no metadata", never blank the row.
 */
export function parseSessionMetadata(metadataJson?: string | null): SessionMetadataLite {
  if (!metadataJson) return {};
  try {
    const parsed = JSON.parse(metadataJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};

    const pick = (...keys: string[]): string | undefined => {
      for (const key of keys) {
        const value = (parsed as Record<string, unknown>)[key];
        if (typeof value === 'string' && value.trim()) return value.trim();
        if (typeof value === 'number') return String(value);
      }
      return undefined;
    };

    return {
      jobId: pick('jobId', 'JobId', 'job_id'),
      jobTitle: pick('jobTitle', 'JobTitle', 'job_title'),
      projectId: pick('projectId', 'ProjectId', 'project_id'),
      projectName: pick('projectName', 'ProjectName', 'project_name'),
      personaId: pick('personaId', 'PersonaId', 'persona_id'),
      personaName: pick('personaName', 'PersonaName', 'persona_name')
    };
  } catch {
    return {};
  }
}

export function isJobTurnSession(session: AgentSession, metadata?: SessionMetadataLite): boolean {
  const meta = metadata ?? parseSessionMetadata(session.metadataJson);
  return Boolean(meta.jobId) || JOB_TURN_PREFIX.test(session.title ?? '');
}

/**
 * Turns "Job Turn: Misc. Fixes (@Rook)" into "Misc. Fixes". The wrapper and the persona suffix
 * are pure noise inside the sidebar: the persona is already the card the row lives under, and the
 * "Job Turn" prefix repeats on every single row of a job-heavy persona.
 *
 * The parenthetical is only dropped when it is an @-mention, so titles that legitimately end in a
 * parenthetical (e.g. "WIP (do not merge)") survive untouched.
 */
export function stripJobTurnNoise(title?: string | null): string {
  if (!title) return '';
  return title
    .replace(JOB_TURN_PREFIX, '')
    .replace(TRAILING_PERSONA_SUFFIX, '')
    .trim();
}

export function isPlaceholderTitle(title?: string | null): boolean {
  if (!title) return true;
  const normalized = title.trim().toLowerCase();
  if (PLACEHOLDER_TITLES.has(normalized)) return true;
  return stripJobTurnNoise(normalized).length === 0;
}

export function truncateLabel(label: string, max = 46): string {
  if (label.length <= max) return label;
  return `${label.slice(0, Math.max(0, max - 1)).trimEnd()}…`;
}

/** Compact relative timestamp ("now", "12m", "3h", "4d", "Mar 8") for dense rows. */
export function formatRelativeTime(value?: string | null): string {
  if (!value) return '';
  const then = new Date(value).getTime();
  if (Number.isNaN(then)) return '';

  const minutes = Math.floor((Date.now() - then) / 60_000);
  if (minutes < 1) return 'now';
  if (minutes < 60) return `${minutes}m`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;

  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d`;

  return new Date(then).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

export interface DisplaySession {
  session: AgentSession;
  metadata: SessionMetadataLite;
  isJobTurn: boolean;
  /** 1-based ordinal of this turn within its job, or null for non-job sessions. */
  turnNumber: number | null;
  /** Primary single-line label. */
  label: string;
  /** Full, untruncated text for tooltips and headers. */
  fullTitle: string;
  /** Secondary context (project / persona) shown on hover or in the header. */
  contextLabel: string;
}

/**
 * Annotates sessions with a per-job turn ordinal. Numbering is computed over the *whole* set so
 * the ordinal stays stable whether or not the list is currently collapsed.
 */
export function decorateSessions(sessions: AgentSession[]): DisplaySession[] {
  // Oldest first, so turn #1 is the turn that kicked the job off.
  const chronological = [...sessions].sort(
    (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()
  );

  // One oldest-first pass: the running count per job ends at that job's total, so the same map
  // yields both each session's ordinal and the job's turn count for the tooltip.
  const ordinals = new Map<string, number>();
  const turnTotals = new Map<string, number>();
  for (const session of chronological) {
    const jobId = parseSessionMetadata(session.metadataJson).jobId;
    if (!jobId) continue;
    const next = (turnTotals.get(jobId) ?? 0) + 1;
    turnTotals.set(jobId, next);
    ordinals.set(session.id, next);
  }

  return sessions.map((session) => {
    const metadata = parseSessionMetadata(session.metadataJson);
    const isJobTurn = isJobTurnSession(session, metadata);
    const turnNumber = metadata.jobId ? (ordinals.get(session.id) ?? null) : null;
    const jobTitle = metadata.jobTitle || stripJobTurnNoise(session.title);

    let label: string;
    let fullTitle: string;
    let contextLabel = '';

    if (isJobTurn) {
      const base = jobTitle || 'Job turn';
      label = turnNumber != null ? `#${turnNumber} ${base}` : base;
      const total = metadata.jobId ? turnTotals.get(metadata.jobId) : undefined;
      fullTitle = `${base}${turnNumber != null ? ` — turn ${turnNumber}${total ? ` of ${total}` : ''}` : ''}`;
      contextLabel = [metadata.projectName, metadata.personaName].filter(Boolean).join(' · ');
    } else if (isPlaceholderTitle(session.title)) {
      label = metadata.personaName || session.personaName || 'Untitled session';
      fullTitle = label;
      contextLabel = metadata.projectName || '';
    } else {
      label = session.title.trim();
      fullTitle = session.title.trim();
      contextLabel = [metadata.projectName, metadata.personaName].filter(Boolean).join(' · ');
    }

    return { session, metadata, isJobTurn, turnNumber, label, fullTitle, contextLabel };
  });
}

export interface CollapsedSessions {
  visible: DisplaySession[];
  hiddenCount: number;
}


/**
 * Trims a most-recent-first list to `limit` rows. The one exception: the session the operator is
 * currently reading must stay visible, otherwise collapsing the list would scroll out the very row
 * they selected. It replaces the oldest preview row rather than being appended, so the height
 * stays bounded either way.
 */
export function collapseSessions(
  items: DisplaySession[],
  limit: number = SESSION_PREVIEW_LIMIT,
  selectedSessionId?: string | null
): CollapsedSessions {
  if (!Number.isFinite(limit) || limit <= 0 || items.length <= limit) {
    return { visible: items, hiddenCount: 0 };
  }

  const visible = items.slice(0, limit);
  const selected = items.find((i) => i.session.id === selectedSessionId);

  if (selected && !visible.some((v) => v.session.id === selected.session.id)) {
    visible[visible.length - 1] = selected;
  }

  return { visible, hiddenCount: items.length - visible.length };
}

/**
 * Most-recent-first ordering by `UpdatedAt`, matching `OrderByDescending(UpdatedAt)` on the API.
 * Shared by the sidebar so the preview window and the lazily expanded list sort identically; a
 * mismatch would make rows jump around the moment the operator hits "Show all".
 */
export function sortSessionsByRecency(sessions: AgentSession[]): AgentSession[] {
  return [...sessions].sort(
    (a, b) => new Date(b.updatedAt ?? b.createdAt).getTime() - new Date(a.updatedAt ?? a.createdAt).getTime()
  );
}

/**
 * Union of two session lists keyed by id, newest first, with `incoming` winning on conflict so a
 * freshly fetched page always overwrites the stale copy the poller cached. Used when a card expands
 * and pulls the owner's full history from the per-owner endpoint.
 */
export function mergeSessionsById(
  base: AgentSession[],
  incoming: AgentSession[]
): AgentSession[] {
  const merged = new Map<string, AgentSession>();
  for (const session of base) merged.set(session.id, session);
  for (const session of incoming) merged.set(session.id, session);
  return sortSessionsByRecency(Array.from(merged.values()));
}

/**
 * How many rows the collapsed card is hiding.
 *
 * The server total is authoritative when we have it: the global session window is capped, so a
 * persona with 120 sessions only ever shows up as ~100 rows client-side and `loadedCount - limit`
 * would understate the hidden backlog. Without the header we degrade to the local count rather
 * than claim a number we cannot back up.
 */
export function resolveHiddenCount(
  loadedCount: number,
  limit: number = SESSION_PREVIEW_LIMIT,
  serverTotal?: number | null
): number {
  const effectiveTotal =
    typeof serverTotal === 'number' && Number.isFinite(serverTotal)
      ? Math.max(serverTotal, loadedCount)
      : loadedCount;

  if (limit <= 0) return 0;
  return Math.max(0, effectiveTotal - Math.min(loadedCount, limit));
}
