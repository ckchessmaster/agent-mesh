import React, { useState, useMemo } from 'react';
import {
  GitBranch,
  CheckCircle2,
  Settings,
  Briefcase,
  FileText,
  RefreshCw,
  Archive,
  RotateCcw,
  Clock,
  Play,
  Sliders,
  FileDiff
} from 'lucide-react';
import type {
  AgentSession,
  AskQuestionItem,
  JobMessage,
  JobTask,
  Persona,
  Project,
  ProjectJob,
  QuestionAnswerPayload,
  SessionAttachment
} from '../types';
import { ProjectJobStatus } from '../types';
import { parseTextQuestions } from './QuestionCard';
import { JobSettingsModal } from './JobSettingsModal';
import { CockpitLayout } from './cockpit/CockpitLayout';

export function extractQuestionsFromJobMessage(msg: JobMessage): AskQuestionItem[] {
  if (msg.senderType === 'user') return [];

  if (msg.structuredPayloadJson) {
    try {
      let p = JSON.parse(msg.structuredPayloadJson);
      if (typeof p === 'string') {
        try {
          p = JSON.parse(p);
        } catch {}
      }
      if (p) {
        if (Array.isArray(p.questions)) return p.questions as AskQuestionItem[];
        if (Array.isArray(p.Questions)) return p.Questions as AskQuestionItem[];
        if (typeof p.questions === 'string') {
          try {
            const inner = JSON.parse(p.questions);
            if (Array.isArray(inner)) return inner;
          } catch {}
        }
        if (p.question) {
          return [
            {
              question: p.question,
              options: Array.isArray(p.options) ? p.options : [],
              is_multi_select: !!p.is_multi_select
            }
          ];
        }
        if (Array.isArray(p) && p.length > 0 && (p[0].question || p[0].Question)) {
          return p as AskQuestionItem[];
        }
        if (p.toolArgs && typeof p.toolArgs === 'object') {
          if (Array.isArray(p.toolArgs.questions)) return p.toolArgs.questions;
        }
      }
    } catch {}
  }

  return parseTextQuestions(msg.content);
}

export function extractJobMessageAttachments(msg: JobMessage): SessionAttachment[] {
  if (Array.isArray(msg.attachments) && msg.attachments.length > 0) {
    return msg.attachments;
  }
  if (!msg.structuredPayloadJson) return [];
  try {
    const parsed = JSON.parse(msg.structuredPayloadJson);
    const rawList = Array.isArray(parsed?.attachments)
      ? parsed.attachments
      : Array.isArray(parsed?.Attachments)
      ? parsed.Attachments
      : null;

    if (Array.isArray(rawList)) {
      return rawList.map((item: any) => {
        const id = item.id || item.Id || '';
        const fileName = item.fileName || item.FileName || item.name || item.Name || '';
        const contentType = item.contentType || item.ContentType || '';
        const sizeBytes =
          typeof item.sizeBytes === 'number'
            ? item.sizeBytes
            : typeof item.SizeBytes === 'number'
            ? item.SizeBytes
            : typeof item.size === 'number'
            ? item.size
            : 0;
        const s3Key = item.s3Key || item.S3Key || '';
        const targetId = id || fileName;
        const downloadUrl =
          item.downloadUrl ||
          item.DownloadUrl ||
          (targetId
            ? `/api/project-jobs/${encodeURIComponent(msg.jobId)}/attachments/${encodeURIComponent(targetId)}/content`
            : '');
        const createdAt = item.createdAt || item.CreatedAt || msg.createdAt;

        return {
          id,
          sessionId: msg.jobId,
          fileName,
          contentType,
          sizeBytes,
          s3Key,
          downloadUrl,
          createdAt
        };
      });
    }
  } catch {}
  return [];
}

export interface StagedJobAttachmentItem {
  localId: string;
  file: File;
  name: string;
  sizeBytes: number;
  contentType: string;
  previewUrl?: string;
  uploading: boolean;
  error?: string;
  uploaded?: SessionAttachment;
}

export function resolveTargetTaskId(tasks: JobTask[], personaId?: string | null): string | null {
  const pending = tasks.filter((t) => !t.isCompleted);
  if (pending.length === 0) return null;
  if (!personaId) return pending[0].id;
  const norm = personaId.trim().toLowerCase();
  const mine = pending.find((t) => (t.assignedPersonaId || '').trim().toLowerCase() === norm);
  if (mine) return mine.id;
  const unowned = pending.find((t) => !(t.assignedPersonaId || '').trim());
  return unowned ? unowned.id : pending[0].id;
}

export function getActiveMentionQuery(
  text: string,
  cursorPos: number
): { query: string; startIndex: number } | null {
  const beforeCursor = text.slice(0, cursorPos);
  const lastAtIndex = beforeCursor.lastIndexOf('@');
  if (lastAtIndex === -1) return null;

  if (lastAtIndex > 0 && !/\s/.test(beforeCursor[lastAtIndex - 1])) {
    return null;
  }

  const query = beforeCursor.slice(lastAtIndex + 1);
  if (/\s/.test(query)) {
    return null;
  }

  return { query, startIndex: lastAtIndex };
}

export interface JobCockpitProps {
  project: Project;
  job: ProjectJob;
  tasks: JobTask[];
  messages: JobMessage[];
  personas?: Persona[];
  allowManualTaskCreation?: boolean;
  userRole?: string;
  onStartJob?: () => Promise<void>;
  onSendMessage: (content: string, attachments?: SessionAttachment[]) => Promise<void>;
  onAddTask?: (title: string, requiresDiscussion?: boolean, attachments?: SessionAttachment[]) => Promise<void>;
  onToggleTask?: (taskId: string, isCompleted: boolean) => Promise<void>;
  onUpdateTask?: (
    taskId: string,
    updates: {
      title?: string;
      isCompleted?: boolean;
      requiresDiscussion?: boolean;
      attachments?: SessionAttachment[];
    }
  ) => Promise<void>;
  onAssignTask?: (taskId: string, personaId: string | null) => Promise<void>;
  onDeleteTask?: (taskId: string) => Promise<void>;
  onCompleteJob: (resultSummary?: string) => Promise<void>;
  onArchiveJob?: () => Promise<void>;
  onReopenJob?: () => Promise<void>;
  onResetJob?: () => Promise<void>;
  onSelectTurnSession?: (sessionId: string) => void;
  onOpenLightbox?: (item: { url: string; fileName: string; sizeBytes?: number }) => void;
  onUploadAttachments?: (files: File[]) => Promise<SessionAttachment[]>;
  onEditProject?: () => void;
  onUpdateJob?: (updates: {
    title?: string;
    description?: string;
    gitBranchName?: string;
    priority?: number;
    status?: ProjectJobStatus;
    resultSummary?: string;
    assignedAgentId?: string | null;
  }) => Promise<void>;
  onDeleteJob?: () => Promise<void>;
  onStopTurn?: (sessionId: string) => Promise<void>;
  onCancelQueuedTurn?: (turnId: string) => Promise<void> | void;
  onCancelQueuedTurns?: (personaId: string) => Promise<void> | void;
  sessions?: AgentSession[];
  jobArtifactCount?: number;
  showJobArtifactsPanel?: boolean;
  onToggleJobArtifactsPanel?: () => void;
  showJobChangesPanel?: boolean;
  onToggleJobChangesPanel?: () => void;
  onAnswerQuestion?: (
    sessionIdOrTargetId: string,
    payload: { toolCallId: string; answers: QuestionAnswerPayload[]; answerSummary: string }
  ) => Promise<void>;
}

export const JobCockpit: React.FC<JobCockpitProps> = ({
  project,
  job,
  tasks,
  personas = [],
  onStartJob,
  onCompleteJob,
  onArchiveJob,
  onReopenJob,
  onResetJob,
  onEditProject,
  onUpdateJob,
  onDeleteJob,
  onAssignTask,
  onCancelQueuedTurn,
  onCancelQueuedTurns,
  jobArtifactCount = 0,
  showJobArtifactsPanel = false,
  onToggleJobArtifactsPanel,
  showJobChangesPanel = false,
  onToggleJobChangesPanel
}) => {
  const [showJobSettingsModal, setShowJobSettingsModal] = useState(false);
  const [showCompleteConfirm, setShowCompleteConfirm] = useState(false);
  const [showArchiveConfirm, setShowArchiveConfirm] = useState(false);
  const [showResetConfirm, setShowResetConfirm] = useState(false);
  const [resultSummary, setResultSummary] = useState('');
  const [isCompleting, setIsCompleting] = useState(false);
  const [isArchiving, setIsArchiving] = useState(false);
  const [isResetting, setIsResetting] = useState(false);
  const [isStarting, setIsStarting] = useState(false);

  const isCompleted = job.status === ProjectJobStatus.Completed;
  const isArchived = job.status === ProjectJobStatus.Archived;
  const isPending = job.status === ProjectJobStatus.Pending;

  const teamMembers = useMemo(() => {
    const map = new Map<string, { id: string; name: string; role: string }>();
    if (project.assignedPersonas) {
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
  }, [project.assignedPersonas, personas]);

  const handleStartJob = async () => {
    if (!onStartJob) return;
    setIsStarting(true);
    try {
      await onStartJob();
    } finally {
      setIsStarting(false);
    }
  };

  const handleConfirmComplete = async () => {
    setIsCompleting(true);
    try {
      await onCompleteJob(resultSummary.trim() || undefined);
      setShowCompleteConfirm(false);
      setResultSummary('');
    } finally {
      setIsCompleting(false);
    }
  };

  const handleConfirmArchive = async () => {
    if (!onArchiveJob) return;
    setIsArchiving(true);
    try {
      await onArchiveJob();
      setShowArchiveConfirm(false);
    } finally {
      setIsArchiving(false);
    }
  };

  const handleConfirmReopen = async () => {
    if (!onReopenJob) return;
    setIsArchiving(true);
    try {
      await onReopenJob();
    } finally {
      setIsArchiving(false);
    }
  };

  const handleConfirmReset = async () => {
    if (!onResetJob) return;
    setIsResetting(true);
    try {
      await onResetJob();
      setShowResetConfirm(false);
    } catch (err: any) {
      console.error('Failed to reset job:', err);
    } finally {
      setIsResetting(false);
    }
  };

  return (
    <div className="flex-1 flex flex-col h-full bg-slate-950 overflow-hidden relative">
      {/* Header Bar */}
      <div className="px-3 sm:px-6 py-2.5 sm:py-3.5 border-b border-slate-800/80 bg-slate-900/60 backdrop-blur-sm flex flex-col md:flex-row md:items-center justify-between gap-2.5 sm:gap-4 shrink-0">
        <div className="flex items-center gap-2.5 sm:gap-3 min-w-0 w-full md:w-auto">
          <div className="w-8 h-8 sm:w-9 sm:h-9 rounded-xl bg-sky-500/15 text-sky-400 border border-sky-500/30 flex items-center justify-center shrink-0">
            <Briefcase className="w-4 h-4" />
          </div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-1.5 sm:gap-2">
              <span className="text-[11px] font-mono px-2 py-0.5 rounded-md bg-slate-800 text-slate-300 font-medium">
                {project.name}
              </span>
              {project.workspaceType === 0 && job.gitBranchName && (
                <span className="text-[11px] font-mono px-2 py-0.5 rounded-md bg-emerald-500/10 text-emerald-400 border border-emerald-500/30 flex items-center gap-1 font-medium">
                  <GitBranch className="w-3 h-3" />
                  {job.gitBranchName}
                </span>
              )}
              {isArchived ? (
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-amber-500/20 text-amber-300 border border-amber-500/40 flex items-center gap-1">
                  <Archive className="w-3 h-3" /> Archived
                </span>
              ) : isCompleted ? (
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-emerald-500/20 text-emerald-300 border border-emerald-500/40">
                  Completed
                </span>
              ) : isPending ? (
                <span
                  className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-slate-800 text-slate-300 border border-slate-700/60 flex items-center gap-1"
                  data-testid="job-status-pending"
                >
                  <Clock className="w-3 h-3 text-slate-400" /> Pending Start
                </span>
              ) : (
                <span
                  className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-sky-500/20 text-sky-300 border border-sky-500/40"
                  data-testid="job-status-active"
                >
                  Active Job
                </span>
              )}
              {job.priority !== undefined && (
                <span
                  className={`text-[11px] font-semibold px-2 py-0.5 rounded-md border flex items-center gap-1 ${
                    job.priority === 3
                      ? 'bg-rose-500/20 text-rose-300 border-rose-500/40'
                      : job.priority === 2
                      ? 'bg-amber-500/20 text-amber-300 border-amber-500/40'
                      : job.priority === 0
                      ? 'bg-slate-800 text-slate-400 border-slate-700'
                      : 'bg-sky-500/20 text-sky-300 border-sky-500/40'
                  }`}
                  data-testid="job-priority-badge"
                >
                  {job.priority === 3 ? 'Urgent' : job.priority === 2 ? 'High' : job.priority === 0 ? 'Low' : 'Normal'}
                </span>
              )}
            </div>
            <h1 className="text-base font-bold text-slate-100 truncate mt-0.5">{job.title}</h1>
          </div>
        </div>

        {/* Assigned Personas & Actions */}
        <div className="flex items-center flex-wrap gap-1.5 sm:gap-2.5 shrink-0 w-full md:w-auto">
          {/* Job Assignee Dropdown */}
          <div className="flex items-center gap-1.5 bg-slate-950/80 border border-slate-800 px-2.5 py-1.5 rounded-xl text-xs">
            <span className="text-[11px] font-semibold text-slate-400">Assignee:</span>
            <select
              value={job.assignedAgentId || project.defaultLeadPersonaId || ''}
              onChange={async (e) => {
                if (onUpdateJob) {
                  await onUpdateJob({ assignedAgentId: e.target.value || null });
                }
              }}
              title="Assign this job to a team member"
              aria-label="Assigned Team Member"
              data-testid="cockpit-assigned-agent-select"
              className="bg-slate-900 border border-slate-700/70 rounded-md px-2 py-0.5 text-xs text-sky-300 font-medium focus:outline-none focus:border-sky-500 cursor-pointer"
            >
              <option value="">Default (@{project.defaultLeadPersonaId || 'implementer'})</option>
              {teamMembers.map((m) => (
                <option key={m.id} value={m.id}>
                  @{m.id} ({m.name})
                </option>
              ))}
            </select>
          </div>

          <div className="hidden lg:flex items-center gap-1.5 bg-slate-950/80 border border-slate-800 px-2.5 py-1.5 rounded-xl">
            <span className="text-[11px] font-semibold text-slate-400 mr-1">Team:</span>
            {project.assignedPersonas?.map((p) => (
              <span
                key={p.id}
                title={`${p.name} (${p.role})`}
                className="text-[11px] font-medium px-2 py-0.5 rounded-md bg-slate-800/80 text-sky-300 border border-slate-700/60"
              >
                @{p.id}
              </span>
            ))}
            {onEditProject && (
              <button
                type="button"
                onClick={onEditProject}
                title="Edit Project Details & Team Members"
                className="ml-1 p-1 rounded-md text-slate-400 hover:text-sky-300 hover:bg-slate-800 transition cursor-pointer"
                data-testid="cockpit-edit-project-btn"
              >
                <Settings className="w-3.5 h-3.5" />
              </button>
            )}
          </div>

          {onUpdateJob && (
            <button
              type="button"
              onClick={() => setShowJobSettingsModal(true)}
              title="Edit Job Settings (Title, Priority, Status, Branch)"
              className="inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-xl border border-slate-800 bg-slate-950/80 text-slate-400 hover:text-sky-300 hover:border-slate-700 text-xs font-semibold transition cursor-pointer"
              data-testid="cockpit-edit-job-btn"
            >
              <Sliders className="w-3.5 h-3.5" />
              <span className="hidden sm:inline">Settings</span>
            </button>
          )}

          {onToggleJobArtifactsPanel && (
            <button
              type="button"
              onClick={onToggleJobArtifactsPanel}
              title={showJobArtifactsPanel ? 'Hide Artifacts panel' : 'Show Artifacts panel'}
              aria-pressed={showJobArtifactsPanel}
              data-testid="toggle-job-artifacts-btn"
              className={`inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-xl border text-xs font-semibold transition cursor-pointer ${
                showJobArtifactsPanel
                  ? 'bg-indigo-500/15 border-indigo-500/30 text-indigo-300'
                  : 'bg-slate-950/80 border-slate-800 text-slate-400 hover:text-slate-200'
              }`}
            >
              <FileText className="w-3.5 h-3.5" />
              <span>Artifacts ({jobArtifactCount})</span>
            </button>
          )}

          {onToggleJobChangesPanel && (
            <button
              type="button"
              onClick={onToggleJobChangesPanel}
              title={showJobChangesPanel ? 'Hide Changes panel' : 'Show Changes panel'}
              aria-pressed={showJobChangesPanel}
              data-testid="toggle-job-changes-btn"
              className={`inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-xl border text-xs font-semibold transition cursor-pointer ${
                showJobChangesPanel
                  ? 'bg-emerald-500/15 border-emerald-500/30 text-emerald-300'
                  : 'bg-slate-950/80 border-slate-800 text-slate-400 hover:text-slate-200'
              }`}
            >
              <FileDiff className="w-3.5 h-3.5" />
              <span>Changes</span>
            </button>
          )}

          {isArchived ? (
            onReopenJob && (
              <button
                type="button"
                onClick={handleConfirmReopen}
                disabled={isArchiving}
                className="px-3.5 py-1.5 bg-sky-500/15 hover:bg-sky-500/25 border border-sky-500/40 text-sky-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                data-testid="cockpit-reopen-job-btn"
              >
                <RotateCcw className="w-3.5 h-3.5" />
                <span>{isArchiving ? 'Reopening...' : 'Reopen Job'}</span>
              </button>
            )
          ) : isPending ? (
            onStartJob && (
              <button
                type="button"
                onClick={handleStartJob}
                disabled={isStarting}
                className="px-4 py-1.5 bg-sky-500 hover:bg-sky-400 text-slate-950 text-xs font-bold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-md"
                data-testid="cockpit-start-job-btn"
              >
                {isStarting ? (
                  <>
                    <RefreshCw className="w-3.5 h-3.5 animate-spin" />
                    <span>Starting...</span>
                  </>
                ) : (
                  <>
                    <Play className="w-3.5 h-3.5 fill-current" />
                    <span>Start Job</span>
                  </>
                )}
              </button>
            )
          ) : (
            <div className="flex items-center gap-2">
              <div>
                {showCompleteConfirm ? (
                  <div className="flex items-center gap-2 bg-slate-900 border border-emerald-500/40 p-1.5 rounded-xl flex-wrap">
                    <input
                      type="text"
                      value={resultSummary}
                      onChange={(e) => setResultSummary(e.target.value)}
                      placeholder="Summary of deliverables (optional)..."
                      data-testid="deliverables-summary-input"
                      className="bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1 text-xs text-slate-100 placeholder-slate-500 focus:outline-none w-full sm:w-56"
                    />
                    <button
                      type="button"
                      onClick={() => setShowCompleteConfirm(false)}
                      className="px-2.5 py-1 text-xs text-slate-400 hover:text-slate-200"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      onClick={handleConfirmComplete}
                      disabled={isCompleting}
                      className="px-3 py-1 bg-emerald-500 hover:bg-emerald-400 text-slate-950 text-xs font-bold rounded-lg shadow-sm"
                      data-testid="cockpit-confirm-complete-btn"
                    >
                      {isCompleting ? 'Completing...' : 'Confirm Complete'}
                    </button>
                  </div>
                ) : (
                  <button
                    type="button"
                    onClick={() => setShowCompleteConfirm(true)}
                    className="px-3.5 py-1.5 bg-emerald-500/15 hover:bg-emerald-500/25 border border-emerald-500/40 text-emerald-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                    data-testid="cockpit-complete-job-btn"
                  >
                    <CheckCircle2 className="w-3.5 h-3.5" />
                    <span>Complete Job</span>
                  </button>
                )}
              </div>

              {onArchiveJob && (
                <div>
                  {showArchiveConfirm ? (
                    <div className="flex items-center gap-2 bg-slate-900 border border-amber-500/40 p-1.5 rounded-xl flex-wrap">
                      <span className="text-xs text-amber-200">Archive job?</span>
                      <button
                        type="button"
                        onClick={() => setShowArchiveConfirm(false)}
                        className="px-2 py-1 text-xs text-slate-400 hover:text-slate-200 cursor-pointer"
                      >
                        Cancel
                      </button>
                      <button
                        type="button"
                        onClick={handleConfirmArchive}
                        disabled={isArchiving}
                        className="px-3 py-1 bg-amber-500 hover:bg-amber-400 text-slate-950 text-xs font-bold rounded-lg shadow-sm cursor-pointer"
                        data-testid="cockpit-confirm-archive-btn"
                      >
                        {isArchiving ? 'Archiving...' : 'Confirm Archive'}
                      </button>
                    </div>
                  ) : (
                    <button
                      type="button"
                      onClick={() => setShowArchiveConfirm(true)}
                      title="Archive and close this job"
                      className="px-3.5 py-1.5 bg-slate-800/80 hover:bg-slate-700/80 border border-slate-700 text-slate-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                      data-testid="cockpit-archive-job-btn"
                    >
                      <Archive className="w-3.5 h-3.5" />
                      <span>Archive</span>
                    </button>
                  )}
                </div>
              )}

              {onResetJob && (
                <div>
                  {showResetConfirm ? (
                    <div className="flex items-center gap-2 bg-slate-900 border border-rose-500/40 p-1.5 rounded-xl flex-wrap">
                      <span className="text-xs text-rose-200">Reset to pending &amp; stop workflow?</span>
                      <button
                        type="button"
                        onClick={() => setShowResetConfirm(false)}
                        className="px-2 py-1 text-xs text-slate-400 hover:text-slate-200 cursor-pointer"
                      >
                        Cancel
                      </button>
                      <button
                        type="button"
                        onClick={handleConfirmReset}
                        disabled={isResetting}
                        className="px-3 py-1 bg-rose-500 hover:bg-rose-400 text-slate-950 text-xs font-bold rounded-lg shadow-sm cursor-pointer"
                        data-testid="cockpit-confirm-reset-btn"
                      >
                        {isResetting ? 'Resetting...' : 'Confirm Reset'}
                      </button>
                    </div>
                  ) : (
                    <button
                      type="button"
                      onClick={() => setShowResetConfirm(true)}
                      title="Terminate running workflow and reset job to pending so it can be restarted"
                      className="px-3.5 py-1.5 bg-rose-500/15 hover:bg-rose-500/25 border border-rose-500/40 text-rose-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                      data-testid="cockpit-reset-job-btn"
                    >
                      <RotateCcw className="w-3.5 h-3.5" />
                      <span>Reset Job</span>
                    </button>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      {/* Main Workspace: Structured Timeline Cockpit (StickySidebar + TimelineFeed) */}
      <div className="flex-1 min-h-0 overflow-hidden" data-testid="timeline-cockpit-container">
        <CockpitLayout
          jobId={job.id}
          job={job}
          tasks={tasks}
          project={project}
          personas={personas}
          onStartJob={isPending && onStartJob ? handleStartJob : undefined}
          isStarting={isStarting}
          onAssignTask={onAssignTask}
          onCancelQueuedTurn={onCancelQueuedTurn}
          onCancelQueuedTurns={onCancelQueuedTurns}
        />
      </div>

      {showJobSettingsModal && (
        <JobSettingsModal
          isOpen={showJobSettingsModal}
          onClose={() => setShowJobSettingsModal(false)}
          project={project}
          job={job}
          availablePersonas={personas}
          onSave={async (updates) => {
            if (onUpdateJob) await onUpdateJob(updates);
          }}
          onDelete={onDeleteJob}
        />
      )}
    </div>
  );
};
