import React from 'react';
import { ProjectJobStatus, type ProjectJob } from '../../types';
import type { JobTimelineCard } from '../../types/timeline';
import { JobInitializedCard } from './cards/JobInitializedCard';
import { DiscoveryQaCard } from './cards/DiscoveryQaCard';
import { PlanApprovalCard } from './cards/PlanApprovalCard';
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
  onSubmitIntervention?: (
    action: 'resume' | 'retry_step' | 'realign',
    additionalContext?: string
  ) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

export const TimelineFeed: React.FC<TimelineFeedProps> = ({
  cards,
  loading = false,
  job,
  onStartJob,
  isStarting = false,
  onSubmitAnswers,
  onSubmitPlanDecision,
  onSubmitIntervention,
  onAddComment
}) => {
  if (loading && cards.length === 0) {
    return (
      <div className="flex-1 flex flex-col items-center justify-center p-12 text-slate-400">
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
          className="flex-1 flex flex-col items-center justify-center p-12 text-slate-400"
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
      <div className="flex-1 flex flex-col items-center justify-center p-12 text-slate-500">
        <Layers className="w-10 h-10 mb-3 text-slate-600" />
        <h3 className="text-sm font-semibold text-slate-300">Timeline stream empty</h3>
        <p className="text-xs text-slate-500 mt-1">
          Cards will appear here as the Temporal orchestrator executes turns.
        </p>
      </div>
    );
  }

  return (
    <main
      data-testid="timeline-feed"
      className="flex-1 overflow-y-auto p-6 max-w-4xl mx-auto w-full space-y-4"
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
                className="p-4 rounded-lg bg-slate-900/80 border border-slate-800 text-slate-200 mb-4"
              >
                <div className="flex items-center justify-between gap-2 pb-2 border-b border-slate-800">
                  <div className="flex items-center gap-2 min-w-0">
                    <h4 className="text-sm font-semibold text-slate-100 truncate">{card.title}</h4>
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
                  <div className="mt-2 text-xs text-slate-200 whitespace-pre-wrap">
                    {textContent}
                  </div>
                ) : (
                  <div className="mt-2 text-xs font-mono text-slate-400 max-h-36 overflow-y-auto whitespace-pre-wrap">
                    {card.structuredPayloadJson}
                  </div>
                )}
                {prUrl && (
                  <div className="mt-2">
                    <a
                      href={prUrl}
                      target="_blank"
                      rel="noreferrer"
                      className="text-xs font-mono text-sky-400 hover:underline"
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
