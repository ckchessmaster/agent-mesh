import React, { useState } from 'react';
import type { JobTimelineCard, PlanApprovalPayload } from '../../../types/timeline';
import { CardComments } from './CardComments';
import { CheckCircle, XCircle, FileText, Check, ShieldCheck, AlertCircle, Send } from 'lucide-react';

export interface PlanApprovalCardProps {
  card: JobTimelineCard;
  onSubmitPlanDecision?: (
    cardId: string,
    approved: boolean,
    feedback?: string,
    approverId?: string
  ) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
  /**
   * Sends a raw message to the agent without deciding the gate.
   *
   * The gate had two exits — approve, or reject with feedback — so the only way to tell the agent
   * something was to file it as a criticism of a plan that was not wrong. That matters most when
   * Discovery Q&A was skipped: an operator who wants to answer a question the agent never asked had
   * nowhere to say it. Without this prop the card degrades to the old two exits.
   */
  onSendGuidance?: (cardId: string, content: string) => Promise<void> | void;
}

export const PlanApprovalCard: React.FC<PlanApprovalCardProps> = ({
  card,
  onSubmitPlanDecision,
  onAddComment,
  onSendGuidance
}) => {
  let payload: PlanApprovalPayload = { tasks: [], reasoning: '' };
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    // fallback
  }

  const [feedback, setFeedback] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [guidanceSending, setGuidanceSending] = useState(false);
  const [guidanceSentCount, setGuidanceSentCount] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [decision, setDecision] = useState<'Approved' | 'Rejected' | null>(
    card.status === 'Approved' ? 'Approved' : card.status === 'Rejected' ? 'Rejected' : null
  );

  const isPending = card.status === 'PendingInput' && !decision;
  // Superseded: guidance sent the agent away to re-plan, so this card is no longer the live gate.
  const isSuperseded = card.status === 'Resolved' && !decision;
  const busy = submitting || guidanceSending;

  const verdict: 'Approved' | 'Rejected' | 'Superseded' | 'Pending' =
    decision === 'Approved' || card.status === 'Approved'
      ? 'Approved'
      : decision === 'Rejected' || card.status === 'Rejected'
        ? 'Rejected'
        : isSuperseded
          ? 'Superseded'
          : 'Pending';

  const handleDecision = async (approved: boolean) => {
    if (busy || decision || !onSubmitPlanDecision) return;
    try {
      setSubmitting(true);
      setError(null);
      await onSubmitPlanDecision(card.id, approved, feedback.trim() || undefined, 'operator');
      setDecision(approved ? 'Approved' : 'Rejected');
      setFeedback('');
    } catch (err) {
      // Say why, and leave the gate open. This was a console.error, so a refused decision — a workflow
      // past its gate, a worker mid-restart, a 409 from the API — looked exactly like a click that had
      // not registered, and the operator's only evidence was that the buttons were still there. Typed
      // feedback survives the failure so it can be resent rather than retyped.
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
  };

  const handleSendGuidance = async () => {
    const text = feedback.trim();
    if (busy || !text || !onSendGuidance || decision) return;
    try {
      setGuidanceSending(true);
      setError(null);
      await onSendGuidance(card.id, text);
      setFeedback('');
      setGuidanceSentCount((count) => count + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setGuidanceSending(false);
    }
  };

  return (
    <div
      data-testid="plan-approval-card"
      className="p-3 sm:p-4 rounded-lg bg-slate-900/90 border border-slate-700 shadow-md text-slate-200 mb-3 sm:mb-4 transition"
    >
      {/* Header */}
      <div className="flex items-start sm:items-center justify-between gap-2 pb-3 border-b border-slate-800">
        <div className="flex items-start sm:items-center space-x-2 min-w-0">
          <div className="p-1.5 rounded bg-emerald-500/20 text-emerald-400 shrink-0">
            <ShieldCheck className="w-4 h-4" />
          </div>
          <div className="min-w-0">
            <h3 className="text-sm font-semibold text-slate-100">{card.title || 'Plan Approval'}</h3>
            <p className="text-xs text-slate-400 break-words">
              {payload.reasoning || 'Review the proposed decomposition and verification steps'}
            </p>
          </div>
        </div>
        <span
          data-testid="plan-verdict-badge"
          className={`px-2 py-0.5 text-xs font-mono rounded ${
            verdict === 'Approved'
              ? 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/30'
              : verdict === 'Rejected'
                ? 'bg-rose-500/10 text-rose-400 border border-rose-500/30'
                : verdict === 'Superseded'
                  ? 'bg-indigo-500/10 text-indigo-300 border-indigo-500/30'
                  : 'bg-amber-500/10 text-amber-400 border border-amber-500/30 animate-pulse'
          }`}
        >
          {verdict === 'Pending' ? 'Pending Approval' : verdict}
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
              className="p-2.5 rounded bg-slate-950/40 border border-slate-800 text-xs flex flex-col sm:flex-row sm:items-start justify-between gap-2"
            >
              <div className="space-y-1 min-w-0 flex-1">
                <div className="flex items-start space-x-2">
                  <span className="font-mono text-indigo-400 font-semibold shrink-0">{idx + 1}.</span>
                  <span className="font-medium text-slate-200 break-words">{task.title}</span>
                </div>
                {task.targetFile && (
                  <div className="flex items-center space-x-1 text-[11px] text-slate-400 font-mono min-w-0">
                    <FileText className="w-3 h-3 text-slate-500 shrink-0" />
                    <span className="truncate">{task.targetFile}</span>
                  </div>
                )}
              </div>
              {task.verificationMethod && (
                <div className="text-[11px] text-slate-400 font-mono bg-slate-900 px-2 py-1 rounded border border-slate-800 self-start shrink-0 max-w-full break-words">
                  Verify: {task.verificationMethod}
                </div>
              )}
            </div>
          ))}
          {(payload.tasks?.length ?? 0) === 0 && (
            <div
              data-testid="plan-card-empty-checklist"
              className="p-2.5 rounded border border-amber-700/50 bg-amber-950/20 text-[11px] text-amber-200 leading-relaxed"
            >
              The persona registered no checklist, so there is nothing here to approve — approving halts the
              job. Reject with feedback to re-run decomposition; a driver without function tools registers
              tasks with <span className="font-mono">agentmesh task set-checklist</span>.
            </div>
          )}
        </div>
      </div>

      {/* Decision Section */}
      {isPending && (
        <div className="mt-4 pt-3 border-t border-slate-800 space-y-3">
          {error && (
            <div
              data-testid="plan-card-error"
              role="alert"
              className="p-2.5 rounded border border-rose-700/60 bg-rose-950/30 text-xs text-rose-300 flex items-start space-x-2"
            >
              <AlertCircle className="w-3.5 h-3.5 mt-0.5 flex-shrink-0" />
              <span>
                The plan gate is still open and nothing was sent to the agent: {error}
              </span>
            </div>
          )}

          <div>
            <label className="block text-xs font-medium text-slate-400 mb-1">
              {onSendGuidance
                ? 'Message for the agent (required to reject; also usable on its own below)'
                : 'Feedback / Modification Requests (Required if rejecting)'}
            </label>
            <textarea
              rows={2}
              placeholder="e.g. Include integration tests for ProcessGroup isolation before UI phase."
              value={feedback}
              onChange={(e) => setFeedback(e.target.value)}
              className="w-full bg-slate-900 border border-slate-700/80 rounded px-2.5 py-1.5 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-indigo-500"
            />
          </div>

          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2">
            {onSendGuidance ? (
              <div className="flex items-center space-x-2 min-w-0">
                <button
                  type="button"
                  data-testid="plan-send-guidance"
                  disabled={busy || !feedback.trim()}
                  onClick={handleSendGuidance}
                  title="Tell the agent something without rejecting this plan — it re-plans and comes back to a new gate."
                  className="px-3 py-1.5 bg-indigo-900/40 hover:bg-indigo-800/60 text-indigo-200 border border-indigo-700/60 rounded text-xs font-semibold flex items-center space-x-1.5 transition disabled:opacity-50 disabled:hover:bg-indigo-900/40"
                >
                  <Send className="w-3.5 h-3.5" />
                  <span>{guidanceSending ? 'Sending…' : 'Send guidance'}</span>
                </button>
                {guidanceSentCount > 0 && !error && (
                  <span
                    data-testid="plan-guidance-sent"
                    className="text-[11px] text-indigo-300 flex items-center space-x-1 truncate"
                  >
                    <CheckCircle className="w-3.5 h-3.5" />
                    <span>
                      Sent — the agent is revising this plan. This gate stays open until you decide.
                    </span>
                  </span>
                )}
              </div>
            ) : (
              <span />
            )}

            <div className="flex flex-wrap items-center gap-2 shrink-0">
              <button
                type="button"
                disabled={busy}
                onClick={() => handleDecision(false)}
              className="px-3.5 py-1.5 bg-rose-900/40 hover:bg-rose-800/60 text-rose-300 border border-rose-700/60 rounded text-xs font-semibold flex items-center space-x-1.5 transition disabled:opacity-50"
            >
                <XCircle className="w-3.5 h-3.5" />
                <span>Reject with Feedback</span>
              </button>
              <button
                type="button"
                disabled={busy}
                onClick={() => handleDecision(true)}
              className="px-4 py-1.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded text-xs font-semibold flex items-center space-x-1.5 shadow transition disabled:opacity-50"
            >
                <Check className="w-3.5 h-3.5" />
                <span>Approve Plan</span>
              </button>
            </div>
          </div>

          <p className="text-[11px] text-slate-500 leading-relaxed">
            Guidance keeps this gate open and the plan unrejected: the agent re-plans with what you
            wrote and posts a new checklist for the same decision. Rejecting records a refusal and
            does the same re-plan. Approving releases it to implementation.
          </p>
        </div>
      )}

      {/* Outcome Banner */}
      {!isPending && (
        <div
          data-testid="plan-outcome-banner"
          className={`mt-4 p-2.5 rounded border text-xs flex items-center space-x-2 ${
            verdict === 'Approved'
              ? 'bg-emerald-950/20 border-emerald-500/20 text-emerald-400'
              : verdict === 'Superseded'
                ? 'bg-indigo-950/20 border-indigo-500/20 text-indigo-300'
                : 'bg-rose-950/20 border-rose-500/20 text-rose-400'
          }`}
        >
          {verdict === 'Approved' ? (
            <>
              <CheckCircle className="w-4 h-4 flex-shrink-0" />
              <span>Plan approved. Sequential child tasks dispatched to Temporal workers.</span>
            </>
          ) : verdict === 'Superseded' ? (
            <>
              <Send className="w-4 h-4 flex-shrink-0" />
              <span>
                Superseded by guidance: the agent re-planned with your message, and the live gate is
                the newer card below. Nothing was rejected, and this card no longer accepts a
                decision.
              </span>
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
