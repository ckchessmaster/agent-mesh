import React from 'react';
import type { JobTimelineCard, JobInitializedPayload } from '../../../types/timeline';
import { CardComments } from './CardComments';
import { Rocket, GitBranch, FolderGit2, User, FileText } from 'lucide-react';

export interface JobInitializedCardProps {
  card: JobTimelineCard;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

export const JobInitializedCard: React.FC<JobInitializedCardProps> = ({
  card,
  onAddComment
}) => {
  let payload: JobInitializedPayload = { jobId: card.jobId };
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    // fallback
  }

  return (
    <div
      data-testid="job-initialized-card"
      className="p-4 rounded-lg bg-slate-900/80 border border-slate-800 shadow-sm text-slate-200 mb-4 transition"
    >
      <div className="flex items-center justify-between pb-3 border-b border-slate-800">
        <div className="flex items-center space-x-2">
          <div className="p-1.5 rounded bg-sky-500/20 text-sky-400">
            <Rocket className="w-4 h-4" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-slate-100">{card.title || 'Job Initialized'}</h3>
            <p className="text-xs text-slate-400">Workflow orchestration initialized</p>
          </div>
        </div>
        <span className="px-2 py-0.5 text-xs font-mono rounded bg-slate-800 text-slate-400 border border-slate-700">
          Initialized
        </span>
      </div>

      <div className="mt-3 grid grid-cols-1 md:grid-cols-2 gap-2 text-xs font-mono">
        {payload.targetBranch && (
          <div className="flex items-center space-x-2 p-2 rounded bg-slate-950/40 border border-slate-800">
            <GitBranch className="w-3.5 h-3.5 text-slate-500" />
            <span className="text-slate-400">Branch:</span>
            <span className="text-indigo-300 font-semibold">{payload.targetBranch}</span>
          </div>
        )}
        {payload.assignedAgentId && (
          <div className="flex items-center space-x-2 p-2 rounded bg-slate-950/40 border border-slate-800">
            <User className="w-3.5 h-3.5 text-slate-500" />
            <span className="text-slate-400">Assigned Agent:</span>
            <span className="text-purple-300 font-semibold">{payload.assignedAgentId}</span>
          </div>
        )}
        {payload.workspacePath && (
          <div className="flex items-center space-x-2 p-2 rounded bg-slate-950/40 border border-slate-800 col-span-full">
            <FolderGit2 className="w-3.5 h-3.5 text-slate-500" />
            <span className="text-slate-400">Workspace:</span>
            <span className="text-slate-300 truncate">{payload.workspacePath}</span>
          </div>
        )}
      </div>

      {payload.initialPrompt && (
        <div className="mt-3 p-2.5 rounded bg-slate-950/60 border border-slate-800 text-xs">
          <div className="flex items-center space-x-1.5 text-slate-400 mb-1 font-medium">
            <FileText className="w-3.5 h-3.5 text-slate-500" />
            <span>Initial Objective:</span>
          </div>
          <p className="text-slate-300 whitespace-pre-wrap">{payload.initialPrompt}</p>
        </div>
      )}

      {/* Card Comments */}
      <CardComments cardId={card.id} comments={card.comments} onAddComment={onAddComment} />
    </div>
  );
};
