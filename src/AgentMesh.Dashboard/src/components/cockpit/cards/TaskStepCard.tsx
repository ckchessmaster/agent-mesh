import React, { useState } from 'react';
import type { JobTimelineCard, TaskStepPayload } from '../../../types/timeline';
import { CardComments } from './CardComments';
import { Terminal, Users, ChevronDown, ChevronRight, Activity, Heart, CheckCircle2, PlayCircle } from 'lucide-react';

export interface TaskStepCardProps {
  card: JobTimelineCard;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

export const TaskStepCard: React.FC<TaskStepCardProps> = ({
  card,
  onAddComment
}) => {
  let payload: TaskStepPayload = { taskIndex: 0, taskTitle: '' };
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    // fallback
  }

  const [consultationOpen, setConsultationOpen] = useState(false);
  const [logsOpen, setLogsOpen] = useState(false);

  const isActive = card.status === 'Active';
  const isResolved = card.status === 'Resolved' || card.status === 'Closed' || card.status === 'Approved';

  return (
    <div
      data-testid="task-step-card"
      className="p-4 rounded-lg bg-slate-900/90 border border-slate-800 shadow-md text-slate-200 mb-4 transition"
    >
      {/* Header */}
      <div className="flex items-center justify-between pb-3 border-b border-slate-800">
        <div className="flex items-center space-x-2">
          <div
            className={`p-1.5 rounded ${
              isActive
                ? 'bg-amber-500/20 text-amber-400'
                : 'bg-indigo-500/20 text-indigo-400'
            }`}
          >
            <Activity className="w-4 h-4" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-slate-100 flex items-center space-x-2">
              <span>{card.title || `Task ${payload.taskIndex + 1}: ${payload.taskTitle}`}</span>
            </h3>
            {payload.stepDetails && (
              <p className="text-xs text-slate-400 font-mono mt-0.5">{payload.stepDetails}</p>
            )}
          </div>
        </div>

        <div className="flex items-center space-x-2">
          {isActive ? (
            <span className="flex items-center space-x-1.5 px-2 py-0.5 text-xs font-mono rounded bg-amber-500/10 text-amber-400 border border-amber-500/30 animate-pulse">
              <PlayCircle className="w-3.5 h-3.5 animate-spin" />
              <span>In Progress</span>
            </span>
          ) : isResolved ? (
            <span className="flex items-center space-x-1.5 px-2 py-0.5 text-xs font-mono rounded bg-emerald-500/10 text-emerald-400 border border-emerald-500/30">
              <CheckCircle2 className="w-3.5 h-3.5" />
              <span>Completed</span>
            </span>
          ) : (
            <span className="px-2 py-0.5 text-xs font-mono rounded bg-slate-800 text-slate-400 border border-slate-700">
              {card.status}
            </span>
          )}
        </div>
      </div>

      {/* Heartbeat Display */}
      {payload.heartbeat && (
        <div className="mt-3 p-2 bg-slate-950/40 border border-slate-800/80 rounded flex items-center space-x-2 text-xs text-slate-300 font-mono">
          <Heart className="w-3.5 h-3.5 text-rose-400 animate-pulse flex-shrink-0" />
          <span className="truncate">💓 {payload.heartbeat}</span>
        </div>
      )}

      {/* Expandable Consultations Drawer */}
      {payload.consultations && payload.consultations.length > 0 && (
        <div className="mt-3 pt-2 border-t border-slate-800/60">
          <button
            type="button"
            onClick={() => setConsultationOpen(!consultationOpen)}
            className="flex items-center space-x-1.5 text-xs text-indigo-400 hover:text-indigo-300 font-medium"
          >
            {consultationOpen ? <ChevronDown className="w-3.5 h-3.5" /> : <ChevronRight className="w-3.5 h-3.5" />}
            <Users className="w-3.5 h-3.5" />
            <span>Persona Consultations ({payload.consultations.length})</span>
          </button>

          {consultationOpen && (
            <div className="mt-2 space-y-2 pl-3 border-l-2 border-indigo-500/40">
              {payload.consultations.map((c, i) => (
                <div key={i} className="p-2 rounded bg-slate-950/60 border border-slate-800 text-xs">
                  <div className="flex items-center justify-between font-mono text-[11px] text-indigo-300 mb-1">
                    <span>Consultation with @{c.personaId}</span>
                  </div>
                  <p className="text-slate-400 mb-1">
                    <span className="text-slate-500">Query: </span>
                    {c.query}
                  </p>
                  <p className="text-slate-200">
                    <span className="text-slate-500">Response: </span>
                    {c.response}
                  </p>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Execution Logs Drawer */}
      {payload.logs && (
        <div className="mt-3 pt-2 border-t border-slate-800/60">
          <button
            type="button"
            onClick={() => setLogsOpen(!logsOpen)}
            className="flex items-center space-x-1.5 text-xs text-slate-400 hover:text-slate-300 font-medium"
          >
            {logsOpen ? <ChevronDown className="w-3.5 h-3.5" /> : <ChevronRight className="w-3.5 h-3.5" />}
            <Terminal className="w-3.5 h-3.5" />
            <span>Process Group Output Logs</span>
          </button>

          {logsOpen && (
            <div className="mt-2 p-2.5 rounded bg-black/80 border border-slate-800 font-mono text-[11px] text-emerald-400/90 whitespace-pre-wrap max-h-48 overflow-y-auto">
              {payload.logs}
            </div>
          )}
        </div>
      )}

      {/* Card Comments */}
      <CardComments cardId={card.id} comments={card.comments} onAddComment={onAddComment} />
    </div>
  );
};
