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
    abortActiveTurn,
    submitIntervention
  } = useJobTimeline(jobId);

  return (
    <div
      data-testid="cockpit-layout"
      className={`flex h-full w-full bg-slate-950 text-slate-100 overflow-hidden ${className}`}
    >
      {/* Sticky Left Sidebar (320px) */}
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
      <div className="flex-1 flex flex-col h-full overflow-hidden">
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
          onSubmitIntervention={submitIntervention}
          onAddComment={(cardId, content) => { void addComment(cardId, content); }}
        />
      </div>
    </div>
  );
};

