import React, { useState } from 'react';
import type { JobTimelineCard, TurnInterruptedPayload } from '../../../types/timeline';
import { CardComments } from './CardComments';
import { AlertOctagon, Play, RotateCcw, Compass, CheckCircle2, Terminal } from 'lucide-react';

export interface TurnInterruptedCardProps {
  card: JobTimelineCard;
  onSubmitIntervention?: (
    action: 'resume' | 'retry_step' | 'realign',
    additionalContext?: string
  ) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

export const TurnInterruptedCard: React.FC<TurnInterruptedCardProps> = ({
  card,
  onSubmitIntervention,
  onAddComment
}) => {
  let payload: TurnInterruptedPayload = {
    reason: 'Turn interrupted by operator',
    requestedBy: 'operator',
    interruptedAt: new Date().toISOString()
  };
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    // fallback
  }

  const [guidance, setGuidance] = useState('');
  const [submittingAction, setSubmittingAction] = useState<string | null>(null);
  const [resolvedAction, setResolvedAction] = useState<string | null>(
    card.status === 'Resolved' || card.status === 'Closed' ? 'resolved' : null
  );

  const handleAction = async (action: 'resume' | 'retry_step' | 'realign') => {
    if (submittingAction || resolvedAction || !onSubmitIntervention) return;
    try {
      setSubmittingAction(action);
      await onSubmitIntervention(action, guidance.trim() || undefined);
      setResolvedAction(action);
    } catch (err) {
      console.error(`Failed to submit intervention ${action}:`, err);
    } finally {
      setSubmittingAction(null);
    }
  };

  const isPending = !resolvedAction && card.status !== 'Resolved' && card.status !== 'Closed';

  return (
    <div
      data-testid="turn-interrupted-card"
      className="p-4 rounded-lg bg-rose-950/20 border-2 border-rose-600/70 shadow-lg text-slate-200 mb-4 transition"
    >
      {/* Alert Header */}
      <div className="flex items-center justify-between pb-3 border-b border-rose-900/60">
        <div className="flex items-center space-x-2.5">
          <div className="p-2 rounded bg-rose-600/30 text-rose-400">
            <AlertOctagon className="w-5 h-5 animate-pulse" />
          </div>
          <div>
            <h3 className="text-sm font-bold text-rose-200 tracking-wide">
              {card.title || 'Turn Interrupted: Process Group Terminated'}
            </h3>
            <p className="text-xs text-rose-300/80">
              Triggered by <span className="font-semibold text-rose-200">{payload.requestedBy || 'operator'}</span>: {payload.reason}
            </p>
          </div>
        </div>

        <span className="px-2.5 py-1 text-xs font-mono font-bold rounded bg-rose-600/30 text-rose-300 border border-rose-500/50 uppercase">
          {resolvedAction ? 'Intervened' : 'Turn Interrupted'}
        </span>
      </div>

      {/* Captured Output / Subprocess Info */}
      {payload.lastOutput && (
        <div className="mt-3">
          <div className="flex items-center space-x-1.5 text-xs text-slate-400 mb-1">
            <Terminal className="w-3.5 h-3.5 text-slate-500" />
            <span className="font-mono text-[11px]">Last Subprocess Output:</span>
          </div>
          <div className="p-2.5 rounded bg-black/70 border border-rose-900/40 font-mono text-[11px] text-rose-300/90 whitespace-pre-wrap max-h-36 overflow-y-auto">
            {payload.lastOutput}
          </div>
        </div>
      )}

      {/* Guidance Textarea */}
      {isPending && (
        <div className="mt-4 space-y-1.5">
          <label className="block text-xs font-semibold text-slate-300">
            Operator Guidance & Course Correction Context
          </label>
          <textarea
            rows={2}
            placeholder="e.g. Ensure you rollback the unmigrated table before resuming; use isolated port 5433."
            value={guidance}
            onChange={(e) => setGuidance(e.target.value)}
            disabled={submittingAction !== null}
            className="w-full bg-slate-900 border border-slate-700/90 rounded px-2.5 py-1.5 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-rose-500"
          />
        </div>
      )}

      {/* Recovery Triggers */}
      {isPending ? (
        <div className="mt-4 pt-3 border-t border-rose-900/60 flex flex-wrap items-center justify-end gap-2">
          <button
            type="button"
            data-testid="realign-turn-btn"
            disabled={submittingAction !== null}
            onClick={() => handleAction('realign')}
            className="px-3 py-1.5 bg-slate-800 hover:bg-slate-700 text-slate-200 border border-slate-700 rounded text-xs font-semibold flex items-center space-x-1.5 transition disabled:opacity-50"
          >
            <Compass className="w-3.5 h-3.5 text-indigo-400" />
            <span>
              {submittingAction === 'realign' ? 'Realigning...' : 'Send Back to Alignment'}
            </span>
          </button>

          <button
            type="button"
            data-testid="retry-step-btn"
            disabled={submittingAction !== null}
            onClick={() => handleAction('retry_step')}
            className="px-3.5 py-1.5 bg-amber-600/80 hover:bg-amber-600 text-white rounded text-xs font-semibold flex items-center space-x-1.5 shadow transition disabled:opacity-50"
          >
            <RotateCcw className="w-3.5 h-3.5" />
            <span>
              {submittingAction === 'retry_step' ? 'Retrying Step...' : 'Retry Last Step'}
            </span>
          </button>

          <button
            type="button"
            data-testid="resume-turn-btn"
            disabled={submittingAction !== null}
            onClick={() => handleAction('resume')}
            className="px-4 py-1.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded text-xs font-semibold flex items-center space-x-1.5 shadow transition disabled:opacity-50"
          >
            <Play className="w-3.5 h-3.5" />
            <span>
              {submittingAction === 'resume' ? 'Resuming...' : 'Resume Turn'}
            </span>
          </button>
        </div>
      ) : (
        <div className="mt-3 p-2.5 rounded bg-emerald-950/20 border border-emerald-500/30 flex items-center space-x-2 text-xs text-emerald-400">
          <CheckCircle2 className="w-4 h-4 flex-shrink-0" />
          <span>
            Intervention action dispatched ({resolvedAction}). Turn unblocked in Temporal coordinator.
          </span>
        </div>
      )}

      {/* Card Comments */}
      <CardComments cardId={card.id} comments={card.comments} onAddComment={onAddComment} />
    </div>
  );
};
