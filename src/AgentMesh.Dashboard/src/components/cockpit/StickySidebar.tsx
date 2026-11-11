import React, { useState, useMemo } from 'react';
import { ProjectJobStatus, type JobTask, type ProjectJob, type Persona, type Project } from '../../types';
import type {
  ProposedChecklistItem,
  TemporalHealthState,
  JobTimelineCard,
  PlanApprovalPayload,
  TaskStepPayload
} from '../../types/timeline';
import { resolveTargetTaskId } from '../JobCockpit';
import {
  SquareTerminal,
  Activity,
  Heart,
  AlertOctagon,
  CheckCircle2,
  PlayCircle,
  Circle,
  Clock,
  X
} from 'lucide-react';

export interface StickySidebarProps {
  jobId: string;
  job?: ProjectJob;
  tasks?: JobTask[];
  project?: Project;
  personas?: Persona[];
  checklist?: ProposedChecklistItem[];
  cards?: JobTimelineCard[];
  heartbeat?: string;
  temporalHealth?: Partial<TemporalHealthState>;
  onAbortTurn?: (reason?: string) => Promise<void> | void;
  isAborting?: boolean;
  onAssignTask?: (taskId: string, personaId: string | null) => Promise<void>;
  onCancelQueuedTurn?: (turnId: string) => Promise<void> | void;
  onCancelQueuedTurns?: (personaId: string) => Promise<void> | void;
}

export const StickySidebar: React.FC<StickySidebarProps> = ({
  jobId,
  job,
  tasks: jobTasks = [],
  project,
  personas = [],
  checklist: customChecklist,
  cards = [],
  heartbeat: propHeartbeat,
  temporalHealth,
  onAbortTurn,
  isAborting = false,
  onAssignTask,
  onCancelQueuedTurn,
  onCancelQueuedTurns: _onCancelQueuedTurns
}) => {
  const [aborting, setAborting] = useState(false);
  const [abortSuccess, setAbortSuccess] = useState(false);
  const [assignError, setAssignError] = useState<string | null>(null);
  const [assigningTaskId, setAssigningTaskId] = useState<string | null>(null);

  const teamMembers = useMemo(() => {
    const map = new Map<string, { id: string; name: string; role: string }>();
    if (project?.assignedPersonas) {
      for (const p of project.assignedPersonas) {
        map.set(p.id.toLowerCase(), { id: p.id, name: p.name, role: p.role });
      }
    }
    for (const p of personas) {
      if (!map.has(p.id.toLowerCase())) {
        map.set(p.id.toLowerCase(), { id: p.id, name: p.name, role: p.role });
      }
    }
    return Array.from(map.values());
  }, [project?.assignedPersonas, personas]);

  // Extract checklist from latest PlanApproval card (preferring Approved) if not explicitly provided
  let items: ProposedChecklistItem[] = customChecklist || [];
  if (items.length === 0) {
    const planCards = cards.filter(
      (c) => c.cardType === 'PlanApproval' && c.structuredPayloadJson
    );
    const approvedPlanCards = planCards.filter((c) => c.status === 'Approved');
    const planCard =
      approvedPlanCards.length > 0
        ? approvedPlanCards[approvedPlanCards.length - 1]
        : planCards[planCards.length - 1];
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

  if (items.length === 0 && jobTasks.length > 0) {
    items = jobTasks.map((t, idx) => ({
      taskId: t.id || `task-${idx + 1}`,
      title: t.title,
      targetFile: t.targetFile || '',
      verificationMethod: t.verificationMethod || '',
      status:
        t.isCompleted || t.status === 'completed'
          ? 'completed'
          : t.status === 'in_progress'
          ? 'in_progress'
          : 'pending'
    }));
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

  if (!currentHeartbeat && job?.progressHeartbeat) {
    currentHeartbeat = job.progressHeartbeat;
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

  const resolvedTaskIds = new Set<string>();
  const resolvedTaskIndices = new Set<number>();
  for (const c of cards) {
    if (c.cardType === 'TaskStep' && (c.status === 'Resolved' || c.status === 'Approved')) {
      if (c.taskId) resolvedTaskIds.add(c.taskId);
      try {
        const p: TaskStepPayload = JSON.parse(c.structuredPayloadJson);
        if (typeof p.taskIndex === 'number') {
          resolvedTaskIndices.add(p.taskIndex);
        }
      } catch {
        // ignore
      }
    }
  }

  const getItemStatus = (item: ProposedChecklistItem, idx: number): 'completed' | 'in_progress' | 'pending' => {
    if (item.status === 'completed') return 'completed';
    if ((item.taskId && resolvedTaskIds.has(item.taskId)) || resolvedTaskIndices.has(idx)) {
      return 'completed';
    }
    if (item.status === 'in_progress') return 'in_progress';
    if (activeTaskIndex >= 0) {
      if (idx < activeTaskIndex) return 'completed';
      if (idx === activeTaskIndex) return 'in_progress';
    }
    return item.status || 'pending';
  };

  const completedCount = items.filter((item, idx) => getItemStatus(item, idx) === 'completed').length;

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
  const derivedStatus = () => {
    if (temporalHealth?.status) return temporalHealth.status;
    if (activeTaskStep) return 'Running';
    if (job) {
      if (job.status === ProjectJobStatus.Pending && cards.length === 0) return 'Pending';
      if (job.status === ProjectJobStatus.Active) return 'Running';
      if (job.status === ProjectJobStatus.Completed) return 'Completed';
      if (job.status === ProjectJobStatus.Archived) return 'Archived';
    }
    return 'Ready';
  };
  const status = derivedStatus();

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
            {completedCount}/{items.length}
          </span>
        </div>

        {items.length === 0 ? (
          <div className="p-3 bg-slate-800/40 border border-slate-800 rounded text-xs text-slate-400 italic">
            Checklist pending decomposition...
          </div>
        ) : (
          <ul data-testid="sidebar-checklist" className="space-y-2">
            {items.map((item, idx) => {
              const itemStatus = getItemStatus(item, idx);

              return (
                <li
                  key={item.taskId || idx}
                  data-testid={`checklist-item-${item.taskId || idx}`}
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
          <div
            data-testid="sidebar-heartbeat"
            className="mt-4 p-2.5 bg-slate-800/60 border border-slate-700/60 rounded-md flex items-center space-x-2"
          >
            <Heart className="w-4 h-4 text-rose-400 animate-pulse flex-shrink-0" />
            <div className="min-w-0 flex-1">
              <span className="text-[11px] text-slate-300 font-mono block truncate">
                💓 {currentHeartbeat}
              </span>
            </div>
          </div>
        )}
      </div>

      {/* Queued Turns Section */}
      {job?.queuedTurns && job.queuedTurns.length > 0 && (
        <div
          className="p-3 rounded-lg bg-amber-950/40 border border-amber-500/40 text-xs shadow-md space-y-2 mb-4"
          data-testid="job-queued-turns-indicator"
        >
          <div className="flex items-center gap-2">
            <Clock className="w-4 h-4 text-amber-300 animate-spin shrink-0" />
            <div className="min-w-0 flex-1 flex items-center justify-between">
              <span className="font-semibold text-amber-200 truncate">
                {job.queuedTurns.length} Queued Turn{job.queuedTurns.length === 1 ? '' : 's'}
              </span>
              <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-amber-500/20 text-amber-300 border border-amber-500/30">
                Waiting for Slot
              </span>
            </div>
          </div>

          {assignError && (
            <div
              data-testid="task-assign-error"
              className="flex items-center justify-between text-xs text-rose-300 bg-rose-950/60 border border-rose-500/40 rounded-lg px-2 py-1"
            >
              <span className="truncate">{assignError}</span>
              <button
                type="button"
                data-testid="task-assign-error-dismiss"
                onClick={() => setAssignError(null)}
                className="text-rose-400 hover:text-rose-200 shrink-0 ml-2"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            </div>
          )}

          <div className="space-y-1.5 pt-1 border-t border-amber-500/20">
            {job.queuedTurns.map((turn) => {
              const targetPersona = personas.find((p) => p.id === turn.targetPersonaId);
              const displayName = targetPersona?.name || turn.targetPersonaId;
              return (
                <div
                  key={turn.id}
                  data-testid={`queued-turn-item-${turn.id}`}
                  className="flex items-center justify-between gap-2 p-1.5 rounded bg-amber-900/30 border border-amber-500/25 text-[11px]"
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-1.5">
                      <span className="font-semibold text-amber-200 truncate">@{displayName}</span>
                    </div>
                    <p className="text-[10px] text-amber-300/70 truncate font-mono">
                      {turn.content.slice(0, 45)}{turn.content.length > 45 ? '...' : ''}
                    </p>
                  </div>
                  <div className="flex items-center gap-1 shrink-0">
                    {onAssignTask && (
                      <select
                        value={turn.targetPersonaId}
                        disabled={assigningTaskId === `turn:${turn.id}`}
                        onChange={async (e) => {
                          const nextPersona = e.target.value;
                          if (!nextPersona || nextPersona === turn.targetPersonaId) return;
                          const targetTaskId = resolveTargetTaskId(jobTasks, turn.targetPersonaId);
                          if (!targetTaskId) {
                            setAssignError('This queued turn has no task to reassign. Add a task first, or cancel the turn.');
                            return;
                          }
                          setAssigningTaskId(`turn:${turn.id}`);
                          setAssignError(null);
                          try {
                            await onAssignTask(targetTaskId, nextPersona);
                          } catch (err) {
                            setAssignError(err instanceof Error ? err.message : 'Failed to reassign queued turn');
                          } finally {
                            setAssigningTaskId(null);
                          }
                        }}
                        aria-label={`Reassign queued turn for ${displayName}`}
                        data-testid={`reassign-queued-turn-${turn.id}`}
                        title="Send this queued turn to a different team member"
                        className="max-w-[100px] rounded border bg-amber-950/60 border-amber-500/30 text-amber-100 px-1 py-0.5 text-[10px] cursor-pointer"
                      >
                        <option value={turn.targetPersonaId}>@{displayName}</option>
                        {teamMembers
                          .filter((m) => m.id !== turn.targetPersonaId)
                          .map((m) => (
                            <option key={m.id} value={m.id}>
                              → {m.name}
                            </option>
                          ))}
                      </select>
                    )}
                    {onCancelQueuedTurn && (
                      <button
                        type="button"
                        onClick={() => onCancelQueuedTurn(turn.id)}
                        title="Cancel queued turn"
                        data-testid={`cancel-queued-turn-btn-${turn.id}`}
                        className="px-1.5 py-0.5 rounded bg-rose-600/80 hover:bg-rose-500 text-white font-medium text-[10px]"
                      >
                        <X className="w-3 h-3" />
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      )}

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
