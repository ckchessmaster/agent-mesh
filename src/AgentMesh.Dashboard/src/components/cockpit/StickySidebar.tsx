import React, { useState } from 'react';
import type {
  ProposedChecklistItem,
  TemporalHealthState,
  JobTimelineCard,
  PlanApprovalPayload,
  TaskStepPayload
} from '../../types/timeline';
import {
  SquareTerminal,
  Activity,
  Heart,
  AlertOctagon,
  CheckCircle2,
  PlayCircle,
  Circle
} from 'lucide-react';

export interface StickySidebarProps {
  jobId: string;
  checklist?: ProposedChecklistItem[];
  cards?: JobTimelineCard[];
  heartbeat?: string;
  temporalHealth?: Partial<TemporalHealthState>;
  onAbortTurn?: (reason?: string) => Promise<void> | void;
  isAborting?: boolean;
}

export const StickySidebar: React.FC<StickySidebarProps> = ({
  jobId,
  checklist: customChecklist,
  cards = [],
  heartbeat: propHeartbeat,
  temporalHealth,
  onAbortTurn,
  isAborting = false
}) => {
  const [aborting, setAborting] = useState(false);
  const [abortSuccess, setAbortSuccess] = useState(false);

  // Extract checklist from PlanApproval card if not explicitly provided
  let items: ProposedChecklistItem[] = customChecklist || [];
  if (items.length === 0) {
    const planCard = cards.find(
      (c) => c.cardType === 'PlanApproval' && c.structuredPayloadJson
    );
    if (planCard) {
      try {
        const payload: PlanApprovalPayload = JSON.parse(planCard.structuredPayloadJson);
        if (Array.isArray(payload.tasks)) {
          items = payload.tasks;
        }
      } catch {
        // payload parse fallback
      }
    }
  }

  // Extract latest heartbeat from TaskStep cards if not provided
  let currentHeartbeat = propHeartbeat || '';
  if (!currentHeartbeat) {
    const stepCards = cards
      .filter((c) => c.cardType === 'TaskStep' && c.structuredPayloadJson)
      .sort(
        (a, b) =>
          new Date(b.updatedAt || b.createdAt).getTime() -
          new Date(a.updatedAt || a.createdAt).getTime()
      );

    if (stepCards.length > 0) {
      try {
        const payload: TaskStepPayload = JSON.parse(stepCards[0].structuredPayloadJson);
        if (payload.heartbeat) {
          currentHeartbeat = payload.heartbeat;
        }
      } catch {
        // ignore
      }
    }
  }

  // Determine item status based on cards
  const activeTaskStep = cards.find(
    (c) => c.cardType === 'TaskStep' && c.status === 'Active'
  );
  let activeTaskIndex = -1;
  if (activeTaskStep) {
    try {
      const p: TaskStepPayload = JSON.parse(activeTaskStep.structuredPayloadJson);
      activeTaskIndex = p.taskIndex ?? -1;
    } catch {
      // ignore
    }
  }

  const handleAbort = async () => {
    if (aborting || isAborting) return;
    try {
      setAborting(true);
      if (onAbortTurn) {
        await onAbortTurn('Turn stopped by operator via Cockpit Sidebar');
      } else {
        const res = await fetch(`/api/jobs/${encodeURIComponent(jobId)}/timeline/abort-turn`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            reason: 'Turn stopped by operator via Cockpit Sidebar',
            requestedBy: 'operator'
          })
        });
        if (!res.ok) throw new Error('Abort request failed');
      }
      setAbortSuccess(true);
      setTimeout(() => setAbortSuccess(false), 3000);
    } catch (e) {
      console.error('Failed to abort turn:', e);
    } finally {
      setAborting(false);
    }
  };

  const workflowId = temporalHealth?.workflowId || (jobId.startsWith('job-') ? jobId : `job-${jobId}`);
  const runId = temporalHealth?.runId || 'run-live-01';
  const attempt = temporalHealth?.attempt ?? 1;
  const status = temporalHealth?.status || (activeTaskStep ? 'Running' : 'Ready');

  return (
    <aside
      data-testid="sticky-sidebar"
      className="w-[320px] flex-shrink-0 bg-slate-900 border-r border-slate-800 flex flex-col h-full overflow-y-auto p-4 select-none"
    >
      {/* Top Header */}
      <div className="flex items-center justify-between pb-3 border-b border-slate-800 mb-4">
        <div className="flex items-center space-x-2">
          <SquareTerminal className="w-5 h-5 text-indigo-400" />
          <h2 className="text-sm font-semibold text-slate-100 tracking-wide uppercase">
            Execution Cockpit
          </h2>
        </div>
        <span
          className={`px-2 py-0.5 text-xs font-mono rounded ${
            status === 'Running'
              ? 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/30 animate-pulse'
              : 'bg-slate-800 text-slate-400 border border-slate-700'
          }`}
        >
          {status}
        </span>
      </div>

      {/* Living Checklist */}
      <div className="flex-1 mb-6">
        <div className="flex items-center justify-between mb-2">
          <span className="text-xs font-bold text-slate-300 uppercase tracking-wider">
            Plan Checklist
          </span>
          <span className="text-xs text-slate-500 font-mono">
            {items.filter((_, idx) => (activeTaskIndex >= 0 ? idx < activeTaskIndex : false)).length}/
            {items.length}
          </span>
        </div>

        {items.length === 0 ? (
          <div className="p-3 bg-slate-800/40 border border-slate-800 rounded text-xs text-slate-400 italic">
            Checklist pending decomposition...
          </div>
        ) : (
          <ul className="space-y-2">
            {items.map((item, idx) => {
              let itemStatus: 'completed' | 'in_progress' | 'pending' = 'pending';
              if (item.status) {
                itemStatus = item.status;
              } else if (activeTaskIndex >= 0) {
                if (idx < activeTaskIndex) itemStatus = 'completed';
                else if (idx === activeTaskIndex) itemStatus = 'in_progress';
                else itemStatus = 'pending';
              }

              return (
                <li
                  key={item.taskId || idx}
                  className={`p-2.5 rounded border text-xs transition-all ${
                    itemStatus === 'in_progress'
                      ? 'bg-indigo-950/40 border-indigo-500/50 text-indigo-200 shadow-sm'
                      : itemStatus === 'completed'
                      ? 'bg-slate-800/30 border-slate-800 text-slate-400 line-through'
                      : 'bg-slate-800/10 border-slate-800/60 text-slate-300'
                  }`}
                >
                  <div className="flex items-start space-x-2">
                    <span className="mt-0.5 flex-shrink-0 font-bold">
                      {itemStatus === 'completed' && (
                        <span className="text-emerald-400 flex items-center space-x-1" title="completed">
                          <CheckCircle2 className="w-3.5 h-3.5 inline" />
                          <span className="sr-only">✓</span>
                        </span>
                      )}
                      {itemStatus === 'in_progress' && (
                        <span className="text-amber-400 flex items-center space-x-1" title="in progress">
                          <PlayCircle className="w-3.5 h-3.5 inline animate-spin" />
                          <span className="sr-only">►</span>
                        </span>
                      )}
                      {itemStatus === 'pending' && (
                        <span className="text-slate-500 flex items-center space-x-1" title="pending">
                          <Circle className="w-3.5 h-3.5 inline" />
                          <span className="sr-only">○</span>
                        </span>
                      )}
                    </span>
                    <div className="flex-1 min-w-0">
                      <p className="font-medium truncate text-slate-200">{item.title}</p>
                      {item.targetFile && (
                        <p className="text-[10px] text-slate-500 font-mono truncate">
                          {item.targetFile}
                        </p>
                      )}
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        )}

        {/* Heartbeat Indicator */}
        {currentHeartbeat && (
          <div className="mt-4 p-2.5 bg-slate-800/60 border border-slate-700/60 rounded-md flex items-center space-x-2">
            <Heart className="w-4 h-4 text-rose-400 animate-pulse flex-shrink-0" />
            <div className="min-w-0 flex-1">
              <span className="text-[11px] text-slate-300 font-mono block truncate">
                💓 {currentHeartbeat}
              </span>
            </div>
          </div>
        )}
      </div>

      {/* Temporal Health Metrics */}
      <div className="p-3 bg-slate-950/60 border border-slate-800/80 rounded-md mb-4 text-xs font-mono space-y-1.5">
        <div className="flex items-center space-x-1.5 text-slate-400 pb-1 border-b border-slate-800">
          <Activity className="w-3.5 h-3.5 text-indigo-400" />
          <span className="font-bold text-slate-300 uppercase tracking-wider text-[10px]">
            Temporal Health
          </span>
        </div>
        <div className="flex justify-between text-slate-400 pt-1">
          <span className="text-slate-500">Workflow:</span>
          <span className="text-slate-300 truncate max-w-[170px]" title={workflowId}>
            {workflowId}
          </span>
        </div>
        <div className="flex justify-between text-slate-400">
          <span className="text-slate-500">Run ID:</span>
          <span className="text-slate-300 truncate max-w-[170px]" title={runId}>
            {runId}
          </span>
        </div>
        <div className="flex justify-between text-slate-400">
          <span className="text-slate-500">Attempt:</span>
          <span className="text-slate-300 font-semibold">{attempt}</span>
        </div>
      </div>

      {/* Stop Active Turn Emergency Action */}
      <div className="pt-2">
        <button
          type="button"
          data-testid="stop-active-turn-btn"
          disabled={aborting || isAborting}
          onClick={handleAbort}
          className={`w-full py-2.5 px-3 rounded-md font-semibold text-xs flex items-center justify-center space-x-2 transition shadow-sm ${
            abortSuccess
              ? 'bg-amber-600 text-white'
              : 'bg-red-600/90 hover:bg-red-600 text-white active:bg-red-700 disabled:opacity-50'
          }`}
        >
          <AlertOctagon className="w-4 h-4" />
          <span>
            {abortSuccess
              ? 'Signal Dispatched!'
              : aborting || isAborting
              ? 'Aborting Process Group...'
              : '🛑 Stop Active Turn'}
          </span>
        </button>
      </div>
    </aside>
  );
};
