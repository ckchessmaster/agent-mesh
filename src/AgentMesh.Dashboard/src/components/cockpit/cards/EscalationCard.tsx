import React, { useState } from 'react';
import type { JobTimelineCard } from '../../../types/timeline';
import { CardComments } from './CardComments';
import { Siren, CheckCircle2, MessageSquareReply, ShieldAlert } from 'lucide-react';

export interface EscalationCardProps {
  card: JobTimelineCard;
  /**
   * Retires the escalation without replying to it — the exit for "already handled elsewhere" and for
   * stale waits whose workflow is long gone. Absent (or after the card is resolved) the card renders
   * read-only, which is the same contract every other gate card follows: no control that cannot work.
   */
  onResolve?: (cardId: string, note?: string) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

interface EscalationPayload {
  content?: string;
  senderPersona?: string;
  authorPersonaId?: string;
  targetPersona?: string;
  requiresReply?: boolean;
  source?: string;
  turnBudget?: number;
  turnsUsed?: number;
}

/**
 * An escalation raised at the operator: "agent paused on error", "turn budget exhausted", internal
 * consultations addressed to a human. Before this component these cards fell through the feed's
 * default renderer, so the one event the dashboard rings the bell for had no dedicated shape in the
 * cockpit and — worse — no exit other than writing a comment the dead run would never read.
 */
export const EscalationCard: React.FC<EscalationCardProps> = ({ card, onResolve, onAddComment }) => {
  let payload: EscalationPayload = {};
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    payload = {};
  }

  const [note, setNote] = useState('');
  const [isResolving, setIsResolving] = useState(false);
  const [resolveError, setResolveError] = useState<string | null>(null);

  const isSettled = card.status === 'Resolved' || card.status === 'Closed';
  const sender = payload.senderPersona || payload.authorPersonaId || null;

  const handleResolve = async () => {
    if (!onResolve || isResolving) return;
    setIsResolving(true);
    setResolveError(null);
    try {
      await onResolve(card.id, note.trim() || undefined);
    } catch (err: any) {
      // Deliberately keeps the button live: a failed resolve must not look like a retired escalation.
      setResolveError(err?.message || 'Failed to resolve escalation.');
    } finally {
      setIsResolving(false);
    }
  };

  return (
    <div
      data-testid="escalation-card"
      className={`p-3 sm:p-4 rounded-lg border-2 shadow-lg text-slate-200 mb-3 sm:mb-4 transition ${
        isSettled
          ? 'bg-slate-900/60 border-slate-700/60'
          : 'bg-amber-950/25 border-amber-600/70'
      }`}
    >
      {/* Header */}
      <div className="flex items-start sm:items-center justify-between gap-2 pb-3 border-b border-amber-900/40">
        <div className="flex items-start sm:items-center space-x-2.5 min-w-0">
          <div
            className={`p-2 rounded shrink-0 ${
              isSettled ? 'bg-slate-800 text-slate-500' : 'bg-amber-600/30 text-amber-400'
            }`}
          >
            {isSettled ? <ShieldAlert className="w-5 h-5" /> : <Siren className="w-5 h-5 animate-pulse" />}
          </div>
          <div className="min-w-0">
            <h3
              className={`text-sm font-bold tracking-wide break-words ${
                isSettled ? 'text-slate-400' : 'text-amber-200'
              }`}
            >
              {card.title || 'Escalation'}
            </h3>
            <p className="text-xs text-slate-400">
              {sender ? (
                <>
                  Raised by <span className="font-semibold text-amber-300">@{sender}</span>
                </>
              ) : (
                'Waiting on the operator'
              )}
            </p>
          </div>
        </div>

        <span
          data-testid="escalation-card-status"
          className={`px-2.5 py-1 text-xs font-mono font-bold rounded uppercase shrink-0 ${
            isSettled
              ? 'bg-slate-800 text-slate-400 border border-slate-700'
              : 'bg-amber-600/30 text-amber-300 border border-amber-500/50'
          }`}
        >
          {isSettled ? card.status : 'Needs You'}
        </span>
      </div>

      {/* Body */}
      {payload.content ? (
        <div className="mt-3 text-xs sm:text-[13px] text-slate-200 whitespace-pre-wrap break-words leading-relaxed">
          {payload.content}
        </div>
      ) : (
        <div className="mt-3 text-xs font-mono text-slate-400 max-h-36 overflow-y-auto whitespace-pre-wrap break-words">
          {card.structuredPayloadJson}
        </div>
      )}

      {/* Resolution row */}
      {isSettled ? (
        <div className="mt-3 p-2.5 rounded bg-emerald-950/20 border border-emerald-500/30 flex items-center space-x-2 text-xs text-emerald-400">
          <CheckCircle2 className="w-4 h-4 flex-shrink-0" />
          <span>Escalation {card.status.toLowerCase()} — off the attention queue.</span>
        </div>
      ) : (
        <div className="mt-4 pt-3 border-t border-amber-900/40">
          <div className="flex flex-col sm:flex-row sm:items-center gap-2">
            <div className="relative flex-1 min-w-0">
              <MessageSquareReply className="w-3.5 h-3.5 text-slate-500 absolute left-2.5 top-1/2 -translate-y-1/2 pointer-events-none" />
              <input
                type="text"
                value={note}
                onChange={(e) => setNote(e.target.value)}
                disabled={isResolving}
                placeholder="Why this is resolved (optional — recorded on the card)"
                data-testid="escalation-resolve-note"
                className="w-full bg-slate-900 border border-slate-700/90 rounded px-2.5 py-1.5 pl-8 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-amber-500"
              />
            </div>
            <button
              type="button"
              data-testid="escalation-resolve-btn"
              disabled={isResolving}
              onClick={handleResolve}
              title="Retire this escalation without replying — it leaves Needs Your Attention immediately. The job itself needs Recover/Reset/Complete from the header."
              className="px-3.5 py-1.5 bg-emerald-600 hover:bg-emerald-500 disabled:opacity-50 text-white rounded text-xs font-semibold flex items-center space-x-1.5 shadow transition shrink-0"
            >
              <CheckCircle2 className="w-3.5 h-3.5" />
              <span>{isResolving ? 'Resolving...' : 'Resolve Escalation'}</span>
            </button>
          </div>
          {resolveError && (
            <div
              data-testid="escalation-resolve-error"
              className="mt-2 text-xs text-rose-300 bg-rose-950/60 border border-rose-500/40 rounded px-2 py-1"
            >
              {resolveError}
            </div>
          )}
          <p className="mt-2 text-[11px] text-slate-500">
            Have something to say instead? Reply in the comments — a reply reaches the next run when
            the job is recovered.
          </p>
        </div>
      )}

      <CardComments cardId={card.id} comments={card.comments} onAddComment={onAddComment} />
    </div>
  );
};
