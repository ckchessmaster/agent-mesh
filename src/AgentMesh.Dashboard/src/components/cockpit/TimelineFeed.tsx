import React from 'react';
import type { JobTimelineCard } from '../../types/timeline';
import { JobInitializedCard } from './cards/JobInitializedCard';
import { DiscoveryQaCard } from './cards/DiscoveryQaCard';
import { PlanApprovalCard } from './cards/PlanApprovalCard';
import { TaskStepCard } from './cards/TaskStepCard';
import { TurnInterruptedCard } from './cards/TurnInterruptedCard';
import { CardComments } from './cards/CardComments';
import { Layers, Loader2 } from 'lucide-react';

export interface TimelineFeedProps {
  cards: JobTimelineCard[];
  loading?: boolean;
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
          default:
            return (
              <div
                key={card.id}
                className="p-4 rounded-lg bg-slate-900/80 border border-slate-800 text-slate-200 mb-4"
              >
                <div className="flex items-center justify-between pb-2 border-b border-slate-800">
                  <h4 className="text-sm font-semibold text-slate-100">{card.title}</h4>
                  <span className="px-2 py-0.5 text-xs font-mono rounded bg-slate-800 text-slate-400">
                    {card.cardType}
                  </span>
                </div>
                <div className="mt-2 text-xs font-mono text-slate-400 max-h-36 overflow-y-auto whitespace-pre-wrap">
                  {card.structuredPayloadJson}
                </div>
                <CardComments
                  cardId={card.id}
                  comments={card.comments}
                  onAddComment={onAddComment}
                />
              </div>
            );
        }
      })}
    </main>
  );
};
