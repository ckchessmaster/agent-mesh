/**
 * Wire contract for the pending-attention surface ("what needs me right now?").
 *
 * Mirrors the server records in `src/AgentMesh.Api/Services/AttentionQueryService.cs`
 * (`AttentionItemDto`, `AttentionRollupDto`) and `src/AgentMesh.Api/Services/AttentionNotifier.cs`
 * (`AttentionEvents`, `AttentionResolvedDto`). Field names are camelCase because ASP.NET Core's
 * default JSON naming policy lower-cases the first letter of every record parameter.
 *
 * The `type` strings and the SignalR event names are frozen on the server for a reason that outlives
 * this file: the notification seen-set persists `"{jobId}:{type}"` keys in `localStorage`, so a rename
 * orphans every already-stored key and silently re-notifies waits the operator already saw. Treat
 * additions as allowed and renames as breaking.
 */

/** The five kinds of wait the rollup can report. Values match `AttentionTypes` on the server. */
export const AttentionType = {
  PlanApproval: 'planApproval',
  DiscoveryQuestions: 'discoveryQuestions',
  InterruptedTurn: 'interruptedTurn',
  PrReview: 'prReview',
  Escalation: 'escalation'
} as const;
export type AttentionType = (typeof AttentionType)[keyof typeof AttentionType];

/** SignalR event names broadcast globally by `AttentionNotifier`. */
export const AttentionEvent = {
  Needed: 'AttentionNeeded',
  Resolved: 'AttentionResolved'
} as const;
export type AttentionEvent = (typeof AttentionEvent)[keyof typeof AttentionEvent];

/**
 * One unit of work waiting on the operator, aggregated across every project.
 *
 * `type` is widened to `string` in the interface while `AttentionType` stays the documented set: the
 * server may add a kind before this client is rebuilt, and a bell that hard-fails on an unknown kind
 * would take the whole inbox down with it. Consumers should switch on the known kinds and render the
 * rest generically.
 */
export interface AttentionItem {
  /** Stable dedupe key: `"{jobId}:{type}"`. A job contributes at most one item. */
  id: string;
  type: AttentionType | string;
  title: string;
  detail?: string | null;
  jobId: string;
  jobTitle: string;
  projectId: string;
  projectName?: string | null;
  cardId?: string | null;
  prUrl?: string | null;
  prNumber?: number | null;
  /** ISO-8601 instant the wait started — the age an operator reads as "waiting 3h". */
  since: string;
}

/** Body of `GET /api/attention`. */
export interface AttentionRollup {
  items: AttentionItem[];
  count: number;
  generatedAt: string;
}

/**
 * Body of the `AttentionResolved` SignalR event.
 *
 * `itemId`/`type` are reserved (always null today): the server keeps no record of what it last
 * announced, so a client clears by `jobId` — every seen key whose prefix is `"{jobId}:"`.
 */
export interface AttentionResolved {
  jobId: string;
  itemId?: string | null;
  type?: string | null;
  resolvedAt: string;
}
