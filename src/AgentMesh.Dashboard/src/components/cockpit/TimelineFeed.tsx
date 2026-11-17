import React, { useCallback, useLayoutEffect, useRef } from 'react';
import { ProjectJobStatus, type ProjectJob } from '../../types';
import type { JobTimelineCard } from '../../types/timeline';
import { JobInitializedCard } from './cards/JobInitializedCard';
import { DiscoveryQaCard } from './cards/DiscoveryQaCard';
import { PlanApprovalCard } from './cards/PlanApprovalCard';
import { PrReviewCard } from './cards/PrReviewCard';
import { TaskStepCard } from './cards/TaskStepCard';
import { TurnInterruptedCard } from './cards/TurnInterruptedCard';
import { CardComments } from './cards/CardComments';
import { Layers, Loader2, Play } from 'lucide-react';

export interface TimelineFeedProps {
  cards: JobTimelineCard[];
  loading?: boolean;
  job?: ProjectJob;
  onStartJob?: () => void;
  isStarting?: boolean;
  onSubmitAnswers?: (
    cardId: string,
    answers: Record<string, string>,
    feedback?: string
  ) => Promise<void> | void;
  onSubmitPlanDecision?: (
    cardId: string,
    approved: boolean,
    feedback?: string,
    approverId?: string
  ) => Promise<void> | void;
  /**
   * Free-form operator guidance for a plan gate that stays open. Optional: a feed without it renders the
   * card with its two original exits.
   */
  onSendPlanGuidance?: (cardId: string, content: string) => Promise<void> | void;
  onSubmitPrReview?: (
    cardId: string,
    approved: boolean,
    feedback?: string,
    prNumber?: number
  ) => Promise<void> | void;
  onSubmitIntervention?: (
    action: 'resume' | 'retry_step' | 'realign',
    additionalContext?: string
  ) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

/**
 * How far above the bottom still counts as "following the feed". Cards are tall (a plan gate or a PR
 * review fills a screen), so a strict one-pixel test would treat an operator reading the newest card as
 * scrolled away, and then refuse to move them for the next one.
 */
const FOLLOW_SLACK_PX = 160;

const isFeedAtBottom = (el: HTMLElement) =>
  el.scrollHeight - (el.scrollTop + el.clientHeight) <= FOLLOW_SLACK_PX;

export const TimelineFeed: React.FC<TimelineFeedProps> = ({
  cards,
  loading = false,
  job,
  onStartJob,
  isStarting = false,
  onSubmitAnswers,
  onSubmitPlanDecision,
  onSendPlanGuidance,
  onSubmitPrReview,
  onSubmitIntervention,
  onAddComment
}) => {
  // --- Feed "stick to bottom" -----------------------------------------------------------
  // The feed is a transcript that grows at the bottom, so it used to open at the top: an
  // operator arriving at a job had to scroll past the entire run to see what it was doing
  // right now, on every single visit.
  //
  // Two pieces of state make that call, because the rule that actually matters is that a
  // live update must never yank down someone who scrolled up to read history:
  //   * followedJobId — which job this container is positioned for. A mismatch means the
  //     feed just mounted or the cockpit swapped jobs, and in both cases the operator came
  //     for the newest card, so we pin unconditionally.
  //   * atBottom — kept current by the scroll handler, and the only thing allowed to let a
  //     newly arrived card move the view.
  //
  // The loading/empty branches above leave `feedRef` null, which is what makes the first
  // render that has cards count as a fresh job rather than an already-positioned one.
  //
  // No dependency array on purpose: the effect must see the DOM after every render, and it
  // decides for itself whether anything changed. A scrollHeight read forces layout, so it
  // happens only when we are about to write scrollTop — never on an ordinary render.
  const feedRef = useRef<HTMLElement | null>(null);
  const followedJobIdRef = useRef<string | null>(null);
  const positionedKeyRef = useRef<string | null>(null);
  const atBottomRef = useRef(true);

  const handleFeedScroll = useCallback(() => {
    const el = feedRef.current;
    if (el) atBottomRef.current = isFeedAtBottom(el);
  }, []);

  useLayoutEffect(() => {
    const el = feedRef.current;
    if (!el) {
      // Unmounted feed: drop the key so remounting the same card list still counts as new.
      positionedKeyRef.current = null;
      return;
    }

    const jobId = job?.id ?? '';
    const key = `${jobId}|${cards.length}|${cards[cards.length - 1]?.id ?? ''}`;

    if (followedJobIdRef.current !== jobId) {
      el.scrollTop = el.scrollHeight;
      followedJobIdRef.current = jobId;
      positionedKeyRef.current = key;
      atBottomRef.current = true;
      return;
    }

    if (positionedKeyRef.current !== key) {
      if (atBottomRef.current) el.scrollTop = el.scrollHeight;
      positionedKeyRef.current = key;
    }
  });

  if (loading && cards.length === 0) {
    return (
      <div className="flex-1 flex flex-col items-center justify-center p-6 sm:p-12 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin text-indigo-400 mb-2" />
        <p className="text-sm">Loading timeline stream...</p>
      </div>
    );
  }

  if (cards.length === 0) {
    if (job?.status === ProjectJobStatus.Pending) {
      return (
        <div
          data-testid="timeline-pending-start-state"
          className="flex-1 flex flex-col items-center justify-center p-6 sm:p-12 text-slate-400"
        >
          <Layers className="w-10 h-10 mb-3 text-amber-400/80" />
          <h3 className="text-sm font-semibold text-slate-200">Job Pending Start</h3>
          <p className="text-xs text-slate-400 mt-1 text-center max-w-md">
            This job has not been started yet. Click Start Job to launch the Temporal orchestration workflow and begin Discovery Q&amp;A.
          </p>
          {onStartJob && (
            <button
              type="button"
              onClick={onStartJob}
              disabled={isStarting}
              className="mt-4 inline-flex items-center gap-2 px-4 py-2 rounded-lg text-xs font-semibold bg-emerald-600 hover:bg-emerald-500 text-white shadow-sm transition-colors disabled:opacity-50"
            >
              <Play size={14} />
              <span>{isStarting ? 'Starting Workflow...' : 'Start Job'}</span>
            </button>
          )}
        </div>
      );
    }

    return (
      <div className="flex-1 flex flex-col items-center justify-center p-6 sm:p-12 text-slate-500">
        <Layers className="w-10 h-10 mb-3 text-slate-600" />
        <h3 className="text-sm font-semibold text-slate-300">Timeline stream empty</h3>
        <p className="text-xs text-slate-500 mt-1 text-center">
          Cards will appear here as the Temporal orchestrator executes turns.
        </p>
      </div>
    );
  }

  return (
    <main
      ref={feedRef}
      onScroll={handleFeedScroll}
      data-testid="timeline-feed"
      className="flex-1 overflow-y-auto p-3 sm:p-6 max-w-4xl mx-auto w-full space-y-3 sm:space-y-4"
    >
      {cards.map((card) => {
        switch (card.cardType) {
          case 'JobInitialized':
            return (
              <JobInitializedCard
                key={card.id}
                card={card}
                onAddComment={onAddComment}
              />
            );
          case 'DiscoveryQa':
            return (
              <DiscoveryQaCard
                key={card.id}
                card={card}
                onSubmitAnswers={onSubmitAnswers}
                onAddComment={onAddComment}
              />
            );
          case 'PlanApproval':
            return (
              <PlanApprovalCard
                key={card.id}
                card={card}
                onSubmitPlanDecision={onSubmitPlanDecision}
                onSendGuidance={onSendPlanGuidance}
                onAddComment={onAddComment}
              />
            );
          case 'PrReview':
            return (
              <PrReviewCard
                key={card.id}
                card={card}
                onSubmitPrDecision={onSubmitPrReview}
                onAddComment={onAddComment}
              />
            );
          case 'TaskStep':
            return (
              <TaskStepCard
                key={card.id}
                card={card}
                onAddComment={onAddComment}
              />
            );
          case 'TurnInterrupted':
            return (
              <TurnInterruptedCard
                key={card.id}
                card={card}
                onSubmitIntervention={onSubmitIntervention}
                onAddComment={onAddComment}
              />
            );
          default: {
            let parsedPayload: Record<string, unknown> | null = null;
            try {
              parsedPayload = card.structuredPayloadJson
                ? (JSON.parse(card.structuredPayloadJson) as Record<string, unknown>)
                : null;
            } catch {
              parsedPayload = null;
            }
            const authorPersona =
              (typeof parsedPayload?.authorPersonaId === 'string' && parsedPayload.authorPersonaId) ||
              (typeof parsedPayload?.senderPersona === 'string' && parsedPayload.senderPersona) ||
              null;
            const targetPersona =
              typeof parsedPayload?.targetPersona === 'string' ? parsedPayload.targetPersona : null;
            const textContent =
              typeof parsedPayload?.content === 'string' ? parsedPayload.content : null;
            const prUrl = typeof parsedPayload?.prUrl === 'string' ? parsedPayload.prUrl : null;

            return (
              <div
                key={card.id}
                className="p-3 sm:p-4 rounded-lg bg-slate-900/80 border border-slate-800 text-slate-200 mb-3 sm:mb-4"
              >
                <div className="flex items-start sm:items-center justify-between gap-2 pb-2 border-b border-slate-800">
                  <div className="flex flex-wrap items-center gap-1.5 sm:gap-2 min-w-0">
                    <h4 className="text-sm font-semibold text-slate-100 truncate max-w-full">{card.title}</h4>
                    {authorPersona && (
                      <span className="px-2 py-0.5 text-[11px] font-mono rounded bg-sky-500/15 text-sky-300 border border-sky-500/30">
                        @{authorPersona}
                      </span>
                    )}
                    {targetPersona && (
                      <span className="px-2 py-0.5 text-[11px] font-mono rounded bg-slate-800 text-slate-300">
                        -&gt; @{targetPersona}
                      </span>
                    )}
                  </div>
                  <span className="px-2 py-0.5 text-xs font-mono rounded bg-slate-800 text-slate-400 shrink-0">
                    {card.cardType}
                  </span>
                </div>
                {textContent ? (
                  <div className="mt-2 text-xs text-slate-200 whitespace-pre-wrap break-words">
                    {textContent}
                  </div>
                ) : (
                  <div className="mt-2 text-xs font-mono text-slate-400 max-h-36 overflow-y-auto whitespace-pre-wrap break-words">
                    {card.structuredPayloadJson}
                  </div>
                )}
                {prUrl && (
                  <div className="mt-2">
                    <a
                      href={prUrl}
                      target="_blank"
                      rel="noreferrer"
                      className="text-xs font-mono text-sky-400 hover:underline break-all"
                    >
                      {prUrl}
                    </a>
                  </div>
                )}
                <CardComments
                  cardId={card.id}
                  comments={card.comments}
                  onAddComment={onAddComment}
                />
              </div>
            );
          }
        }
      })}
    </main>
  );
};
