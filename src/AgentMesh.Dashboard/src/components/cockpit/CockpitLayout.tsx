import React from 'react';
import { useJobTimeline } from '../../hooks/useJobTimeline';
import type { JobTask, ProjectJob, Project, Persona } from '../../types';
import { StickySidebar } from './StickySidebar';
import { TimelineFeed } from './TimelineFeed';

export interface CockpitLayoutProps {
  jobId: string;
  job?: ProjectJob;
  tasks?: JobTask[];
  project?: Project;
  personas?: Persona[];
  onStartJob?: () => void;
  isStarting?: boolean;
  onAssignTask?: (taskId: string, personaId: string | null) => Promise<void>;
  onCancelQueuedTurn?: (turnId: string) => Promise<void> | void;
  onCancelQueuedTurns?: (personaId: string) => Promise<void> | void;
  onBack?: () => void;
  className?: string;
}

export const CockpitLayout: React.FC<CockpitLayoutProps> = ({
  jobId,
  job,
  tasks,
  project,
  personas,
  onStartJob,
  isStarting = false,
  onAssignTask,
  onCancelQueuedTurn,
  onCancelQueuedTurns,
  className = ''
}) => {
  const {
    cards,
    loading,
    error,
    addComment,
    submitAnswers,
    submitPlanDecision,
    sendPlanGuidance,
    submitPrReview,
    abortActiveTurn,
    submitIntervention,
    resolveCard
  } = useJobTimeline(jobId);

  return (
    <div
      data-testid="cockpit-layout"
      className={`flex flex-col md:flex-row h-full w-full bg-slate-950 text-slate-100 overflow-hidden ${className}`}
    >
      {/* Sticky Left Sidebar (320px on md+, collapsible top bar on mobile) */}
      <StickySidebar
        jobId={jobId}
        job={job}
        tasks={tasks}
        project={project}
        personas={personas}
        cards={cards}
        onAbortTurn={abortActiveTurn}
        onAssignTask={onAssignTask}
        onCancelQueuedTurn={onCancelQueuedTurn}
        onCancelQueuedTurns={onCancelQueuedTurns}
      />

      {/* Main Timeline Feed */}
      <div className="flex-1 min-w-0 min-h-0 flex flex-col md:h-full overflow-hidden">
        {error && (
          <div className="p-3 bg-rose-950/40 border-b border-rose-800 text-xs text-rose-300 flex justify-between items-center">
            <span>Error connecting to timeline: {error}</span>
          </div>
        )}
        <TimelineFeed
          cards={cards}
          loading={loading}
          job={job}
          onStartJob={onStartJob}
          isStarting={isStarting}
          onSubmitAnswers={submitAnswers}
          onSubmitPlanDecision={submitPlanDecision}
          // Passed straight through rather than fired-and-forgotten: the card renders a rejection as the
          // reason the gate is still open. `void` here would turn "nobody was told" back into silence,
          // which is the bug this whole path exists to remove.
          onSendPlanGuidance={sendPlanGuidance}
          onSubmitPrReview={submitPrReview}
          onSubmitIntervention={submitIntervention}
          // Awaited, not fired-and-forgotten: the escalation card renders a rejection inline, and a
          // failed resolve must not look like a retired escalation to the operator.
          onResolveCard={resolveCard}
          onAddComment={(cardId, content) => { void addComment(cardId, content); }}
        />
      </div>
    </div>
  );
};

