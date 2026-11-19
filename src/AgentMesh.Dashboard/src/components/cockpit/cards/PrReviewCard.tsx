import React, { useState } from 'react';
import type { JobTimelineCard, PrReviewPayload } from '../../../types/timeline';
import { CardComments } from './CardComments';
import {
  AlertCircle,
  Check,
  CheckCircle,
  Clock,
  ExternalLink,
  GitMerge,
  GitPullRequest,
  XCircle,
} from 'lucide-react';

export interface PrReviewCardProps {
  card: JobTimelineCard;
  onSubmitPrDecision?: (
    cardId: string,
    approved: boolean,
    feedback?: string,
    prNumber?: number
  ) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

/**
 * The job-level pull request waiting on the two-man review gate.
 *
 * This card used to render as raw payload JSON through the feed's fallback branch, which left the
 * only in-Cockpit record of a parked merge looking like a debug dump. The card parses that same
 * payload (written by `UpsertPrReviewCardAsync` in the workflow, and by the review tool for
 * non-workflow jobs) into the fields a reviewer actually reads — the PR link, the branch it merges
 * into, the commit the decision binds to — and, while the gate is parked, offers Approve/Reject
 * buttons.
 *
 * Those buttons are the Cockpit half of the review loop; the GitHub poller is the other half. They
 * matter side by side because a shared-credential deployment (the human's GitHub identity *is* the
 * persona's) cannot produce a GitHub-side approval at all — GitHub refuses author self-approval —
 * so the Cockpit button signing as `operator` is the only second pair of eyes available there.
 * Server-side the decision lands on `POST /api/jobs/{id}/reviews`, which enforces the two-man rule
 * and records the approved head SHA exactly as any REST reviewer would; the workflow merges on the
 * strength of the signal, not of this component.
 */
export const PrReviewCard: React.FC<PrReviewCardProps> = ({
  card,
  onSubmitPrDecision,
  onAddComment,
}) => {
  let payload: PrReviewPayload = {};
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    // An unparseable payload still renders: title, status, and a comment thread. The reviewer loses
    // the deep link but keeps the card.
  }

  const prUrl = payload.prUrl || null;
  const prNumber = typeof payload.prNumber === 'number' ? payload.prNumber : null;
  const author = payload.authorPersonaId || payload.senderPersona || null;
  const headShort = payload.headSha ? payload.headSha.slice(0, 7) : null;
  const prLabel = prNumber !== null ? `PR #${prNumber}` : prUrl ? 'Job Pull Request' : 'Pull Request';

  const [feedback, setFeedback] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [decision, setDecision] = useState<'Approved' | 'Rejected' | null>(
    card.status === 'Approved' ? 'Approved' : card.status === 'Rejected' ? 'Rejected' : null
  );

  const isPending = card.status === 'PendingInput' && !decision;
  const reviewerShown = decision
    ? decision === 'Approved'
      ? card.status === 'Approved'
        ? payload.reviewerId
        : 'operator'
      : payload.reviewerId
    : payload.reviewerId;

  const handleDecision = async (approved: boolean) => {
    if (submitting || !onSubmitPrDecision) return;
    try {
      setSubmitting(true);
      setError(null);
      await onSubmitPrDecision(card.id, approved, feedback.trim() || undefined, prNumber ?? undefined);
      setDecision(approved ? 'Approved' : 'Rejected');
    } catch (err) {
      // Keep the buttons live: a 403 here usually means the signed-in identity *is* the author
      // persona (or no workflow is parked any more), and the operator needs to read why rather than
      // lose the form they just typed feedback into.
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubmitting(false);
    }
  };

  const statusBadge = () => {
    if (isPending) {
      return (
        <span className="px-2 py-0.5 text-xs font-mono rounded bg-amber-500/10 text-amber-400 border border-amber-500/30 animate-pulse flex items-center gap-1">
          <Clock className="w-3 h-3" />
          Awaiting Review
        </span>
      );
    }
    const map: Record<string, { label: string; cls: string; icon: React.ReactNode }> = {
      Approved: {
        label: 'Approved',
        cls: 'bg-emerald-500/10 text-emerald-400 border-emerald-500/30',
        icon: <CheckCircle className="w-3 h-3" />,
      },
      Resolved: {
        label: 'Merged',
        cls: 'bg-indigo-500/10 text-indigo-300 border-indigo-500/30',
        icon: <GitMerge className="w-3 h-3" />,
      },
      Rejected: {
        label: 'Changes Requested',
        cls: 'bg-rose-500/10 text-rose-400 border-rose-500/30',
        icon: <XCircle className="w-3 h-3" />,
      },
    };
    const entry = map[decision || card.status] || {
      label: card.status,
      cls: 'bg-slate-800 text-slate-400 border-slate-700',
      icon: null,
    };
    return (
      <span
        data-testid="pr-review-status-badge"
        className={`px-2 py-0.5 text-xs font-mono rounded border flex items-center gap-1 ${entry.cls}`}
      >
        {entry.icon}
        {entry.label}
      </span>
    );
  };

  return (
    <div
      data-testid="pr-review-card"
      className="p-3 sm:p-4 rounded-lg bg-slate-900/90 border border-slate-700 shadow-md text-slate-200 mb-3 sm:mb-4 transition"
    >
      {/* Header */}
      <div className="flex items-start sm:items-center justify-between pb-3 border-b border-slate-800 gap-2">
        <div className="flex items-start sm:items-center gap-2 min-w-0">
          <div className="p-1.5 rounded bg-indigo-500/20 text-indigo-300 shrink-0">
            <GitPullRequest className="w-4 h-4" />
          </div>
          <div className="min-w-0">
            <h3 className="text-sm font-semibold text-slate-100 truncate">
              {card.title || `${prLabel}: Job Pull Request`}
            </h3>
            <div className="flex items-center gap-1.5 mt-0.5 text-[11px] text-slate-400 font-mono flex-wrap">
              {author && (
                <span className="text-sky-300">
                  @{author}
                  <span className="text-slate-500"> &rarr; </span>
                  <span className="text-slate-300">{payload.targetBranch || 'base'}</span>
                </span>
              )}
              {headShort && (
                <span className="px-1.5 rounded bg-slate-800/80 border border-slate-700/60 text-slate-400">
                  {headShort}
                </span>
              )}
              {typeof payload.round === 'number' && payload.round > 1 && (
                <span className="px-1.5 rounded bg-slate-800/80 border border-slate-700/60 text-slate-400">
                  round {payload.round}
                </span>
              )}
              {reviewerShown && (
                <span className="px-1.5 rounded bg-slate-800/80 border border-slate-700/60 text-slate-300">
                  by {reviewerShown}
                </span>
              )}
            </div>
          </div>
        </div>
        <div className="shrink-0">{statusBadge()}</div>
      </div>

      {/* PR deep link */}
      {prUrl && (
        <div className="mt-3">
          <a
            data-testid="pr-review-link"
            href={prUrl}
            target="_blank"
            rel="noreferrer"
            className="inline-flex items-center gap-1.5 text-xs font-mono text-sky-400 hover:text-sky-300 hover:underline break-all"
          >
            <ExternalLink className="w-3.5 h-3.5 shrink-0" />
            {prUrl.replace(/^https?:\/\//, '')}
          </a>
        </div>
      )}

      {/* The open_pull_request ledger variant narrates ("PR #1 opened: … Awaiting review from @x.")
          where the workflow gate annotates (note). Show the narration too, or the tool's card — which
          is what the PR actually arrived with — reads as a link with no story. */}
      {typeof payload.content === 'string' && payload.content && payload.content !== payload.note && (
        <div className="mt-3 p-2.5 rounded border border-slate-800 bg-slate-950/40 text-xs text-slate-300 whitespace-pre-wrap">
          {payload.content}
        </div>
      )}

      {/* Note: the sha_moved warning while parked, the review comments when rejected, the merge
          summary when resolved. Whichever chapter the gate is on, this is its text. */}
      {payload.note && (
        <div
          className={`mt-3 p-2.5 rounded border text-xs whitespace-pre-wrap ${
            isPending
              ? 'bg-amber-950/20 border-amber-500/20 text-amber-300'
              : card.status === 'Rejected' || decision === 'Rejected'
              ? 'bg-rose-950/20 border-rose-500/20 text-rose-300'
              : 'bg-slate-950/40 border-slate-800 text-slate-300'
          }`}
        >
          {payload.note}
        </div>
      )}

      {/* Decision section */}
      {isPending && (
        <div className="mt-4 pt-3 border-t border-slate-800 space-y-3">
          <div>
            <label className="block text-xs font-medium text-slate-400 mb-1">
              Review Comments (required if rejecting; turned into remediation tasks for @{author || 'the author'})
            </label>
            <textarea
              rows={2}
              placeholder="e.g. The exec terminal reconnect drops stdin after 3 retries — handle the backoff before merge."
              value={feedback}
              onChange={(e) => setFeedback(e.target.value)}
              className="w-full bg-slate-900 border border-slate-700/80 rounded px-2.5 py-1.5 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-indigo-500"
            />
          </div>

          {error && (
            <div
              data-testid="pr-review-error"
              className="p-2 rounded border border-rose-500/30 bg-rose-950/30 text-rose-300 text-xs flex items-start gap-2"
            >
              <AlertCircle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
              <span>{error}</span>
            </div>
          )}

          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2">
            <span className="text-[10px] text-slate-500">
              Signs as <span className="font-mono text-slate-400">operator</span>; two-man rule and the
              approved-commit freeze are enforced server-side.
            </span>
            <div className="flex flex-wrap items-center gap-2 shrink-0">
              <button
                data-testid="reject-pr-btn"
                type="button"
                disabled={submitting}
                onClick={() => handleDecision(false)}
                className="px-3.5 py-1.5 bg-rose-900/40 hover:bg-rose-800/60 text-rose-300 border border-rose-700/60 rounded text-xs font-semibold flex items-center space-x-1.5 transition disabled:opacity-50"
              >
                <XCircle className="w-3.5 h-3.5" />
                <span>Reject with Feedback</span>
              </button>
              <button
                data-testid="approve-pr-btn"
                type="button"
                disabled={submitting}
                onClick={() => handleDecision(true)}
                className="px-4 py-1.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded text-xs font-semibold flex items-center space-x-1.5 shadow transition disabled:opacity-50"
              >
                <Check className="w-3.5 h-3.5" />
                <span>Approve &amp; Merge</span>
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Outcome banner */}
      {!isPending && (
        <div
          data-testid="pr-review-outcome"
          className={`mt-4 p-2.5 rounded border text-xs flex items-center space-x-2 ${
            decision === 'Rejected' || card.status === 'Rejected'
              ? 'bg-rose-950/20 border-rose-500/20 text-rose-400'
              : card.status === 'Resolved'
              ? 'bg-indigo-950/20 border-indigo-500/20 text-indigo-300'
              : 'bg-emerald-950/20 border-emerald-500/20 text-emerald-400'
          }`}
        >
          {decision === 'Rejected' || card.status === 'Rejected' ? (
            <>
              <AlertCircle className="w-4 h-4 flex-shrink-0" />
              <span>
                Changes requested. The author persona will run a remediation round and re-request
                review on the updated head.
              </span>
            </>
          ) : card.status === 'Resolved' ? (
            <>
              <GitMerge className="w-4 h-4 flex-shrink-0" />
              <span>Pull request merged into {payload.targetBranch || 'the base branch'}.</span>
            </>
          ) : (
            <>
              <CheckCircle className="w-4 h-4 flex-shrink-0" />
              <span>
                Approved{(reviewerShown ? ` by ${reviewerShown}` : '')}. The workflow is merging the
                reviewed head commit.
              </span>
            </>
          )}
        </div>
      )}

      {/* Card Comments */}
      <CardComments cardId={card.id} comments={card.comments} onAddComment={onAddComment} />
    </div>
  );
};
