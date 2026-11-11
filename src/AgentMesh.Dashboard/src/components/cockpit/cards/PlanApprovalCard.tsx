import React, { useState } from 'react';
import type { JobTimelineCard, PlanApprovalPayload } from '../../../types/timeline';
import { CardComments } from './CardComments';
import { CheckCircle, XCircle, FileText, Check, ShieldCheck, AlertCircle } from 'lucide-react';

export interface PlanApprovalCardProps {
  card: JobTimelineCard;
  onSubmitPlanDecision?: (
    cardId: string,
    approved: boolean,
    feedback?: string,
    approverId?: string
  ) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

export const PlanApprovalCard: React.FC<PlanApprovalCardProps> = ({
  card,
  onSubmitPlanDecision,
  onAddComment
}) => {
  let payload: PlanApprovalPayload = { tasks: [], reasoning: '' };
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    // fallback
  }

  const [feedback, setFeedback] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [decision, setDecision] = useState<'Approved' | 'Rejected' | null>(
    card.status === 'Approved' ? 'Approved' : card.status === 'Rejected' ? 'Rejected' : null
  );

  const isPending = card.status === 'PendingInput' && !decision;

  const handleDecision = async (approved: boolean) => {
    if (submitting || !onSubmitPlanDecision) return;
    try {
      setSubmitting(true);
      await onSubmitPlanDecision(card.id, approved, feedback.trim() || undefined, 'operator');
      setDecision(approved ? 'Approved' : 'Rejected');
    } catch (err) {
      console.error('Failed to submit plan decision:', err);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div
      data-testid="plan-approval-card"
      className="p-4 rounded-lg bg-slate-900/90 border border-slate-700 shadow-md text-slate-200 mb-4 transition"
    >
      {/* Header */}
      <div className="flex items-center justify-between pb-3 border-b border-slate-800">
        <div className="flex items-center space-x-2">
          <div className="p-1.5 rounded bg-emerald-500/20 text-emerald-400">
            <ShieldCheck className="w-4 h-4" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-slate-100">{card.title || 'Plan Approval'}</h3>
            <p className="text-xs text-slate-400">
              {payload.reasoning || 'Review the proposed decomposition and verification steps'}
            </p>
          </div>
        </div>
        <span
          className={`px-2 py-0.5 text-xs font-mono rounded ${
            decision === 'Approved' || card.status === 'Approved'
              ? 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/30'
              : decision === 'Rejected' || card.status === 'Rejected'
              ? 'bg-rose-500/10 text-rose-400 border border-rose-500/30'
              : 'bg-amber-500/10 text-amber-400 border border-amber-500/30 animate-pulse'
          }`}
        >
          {decision === 'Approved' || card.status === 'Approved'
            ? 'Approved'
            : decision === 'Rejected' || card.status === 'Rejected'
            ? 'Rejected'
            : 'Pending Approval'}
        </span>
      </div>

      {/* Task Breakdown Preview */}
      <div className="mt-4 space-y-2">
        <h4 className="text-xs font-bold text-slate-300 uppercase tracking-wider">
          Proposed Checklist ({payload.tasks?.length || 0} Steps)
        </h4>
        <div className="space-y-2">
          {payload.tasks?.map((task, idx) => (
            <div
              key={task.taskId || idx}
              className="p-2.5 rounded bg-slate-950/40 border border-slate-800 text-xs flex items-start justify-between"
            >
              <div className="space-y-1">
                <div className="flex items-center space-x-2">
                  <span className="font-mono text-indigo-400 font-semibold">{idx + 1}.</span>
                  <span className="font-medium text-slate-200">{task.title}</span>
                </div>
                {task.targetFile && (
                  <div className="flex items-center space-x-1 text-[11px] text-slate-400 font-mono">
                    <FileText className="w-3 h-3 text-slate-500" />
                    <span>{task.targetFile}</span>
                  </div>
                )}
              </div>
              {task.verificationMethod && (
                <div className="text-[11px] text-slate-400 font-mono bg-slate-900 px-2 py-1 rounded border border-slate-800">
                  Verify: {task.verificationMethod}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>

      {/* Decision Section */}
      {isPending && (
        <div className="mt-4 pt-3 border-t border-slate-800 space-y-3">
          <div>
            <label className="block text-xs font-medium text-slate-400 mb-1">
              Feedback / Modification Requests (Required if rejecting)
            </label>
            <textarea
              rows={2}
              placeholder="e.g. Include integration tests for ProcessGroup isolation before UI phase."
              value={feedback}
              onChange={(e) => setFeedback(e.target.value)}
              className="w-full bg-slate-900 border border-slate-700/80 rounded px-2.5 py-1.5 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-indigo-500"
            />
          </div>

          <div className="flex items-center justify-end space-x-2">
            <button
              type="button"
              disabled={submitting}
              onClick={() => handleDecision(false)}
              className="px-3.5 py-1.5 bg-rose-900/40 hover:bg-rose-800/60 text-rose-300 border border-rose-700/60 rounded text-xs font-semibold flex items-center space-x-1.5 transition disabled:opacity-50"
            >
              <XCircle className="w-3.5 h-3.5" />
              <span>Reject with Feedback</span>
            </button>
            <button
              type="button"
              disabled={submitting}
              onClick={() => handleDecision(true)}
              className="px-4 py-1.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded text-xs font-semibold flex items-center space-x-1.5 shadow transition disabled:opacity-50"
            >
              <Check className="w-3.5 h-3.5" />
              <span>Approve Plan</span>
            </button>
          </div>
        </div>
      )}

      {/* Outcome Banner */}
      {!isPending && (
        <div
          className={`mt-4 p-2.5 rounded border text-xs flex items-center space-x-2 ${
            decision === 'Approved' || card.status === 'Approved'
              ? 'bg-emerald-950/20 border-emerald-500/20 text-emerald-400'
              : 'bg-rose-950/20 border-rose-500/20 text-rose-400'
          }`}
        >
          {decision === 'Approved' || card.status === 'Approved' ? (
            <>
              <CheckCircle className="w-4 h-4 flex-shrink-0" />
              <span>Plan approved. Sequential child tasks dispatched to Temporal workers.</span>
            </>
          ) : (
            <>
              <AlertCircle className="w-4 h-4 flex-shrink-0" />
              <span>Plan rejected. Decomposition will re-evaluate with operator feedback.</span>
            </>
          )}
        </div>
      )}

      {/* Card Comments */}
      <CardComments cardId={card.id} comments={card.comments} onAddComment={onAddComment} />
    </div>
  );
};
