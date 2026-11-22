import React, { useState, useMemo, useRef, useEffect, useCallback } from 'react';
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
  FileDiff,
  Paperclip,
  Upload,
  ClipboardPaste,
  Loader2,
  AlertCircle,
  Download,
  Eye,
  X
} from 'lucide-react';
import { api } from '../services/api';
import type {
  AgentSession,
  JobTask,
  Persona,
  Project,
  ProjectJob,
  QuestionAnswerPayload,
  SessionAttachment
} from '../types';
import { ProjectJobStatus } from '../types';
import {
  MAX_JOB_ATTACHMENTS,
  JOB_ATTACHMENT_ACCEPT,
  formatAttachmentSize,
  isImageAttachment,
  extractFilesFromTransfer,
  normalizePastedClipboardFile,
  nextScreenshotSequenceNumber,
  validateJobAttachmentFiles,
  resolveJobAttachmentUrl,
  mergeJobAttachments
} from '../utils/jobAttachments';
import {
  AttachmentLightboxModal,
  type LightboxAttachmentItem
} from './AttachmentLightboxModal';
import { JobSettingsModal } from './JobSettingsModal';
import { CockpitLayout } from './cockpit/CockpitLayout';

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
  personas?: Persona[];
  allowManualTaskCreation?: boolean;
  userRole?: string;
  onStartJob?: () => Promise<void>;
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
  /**
   * Resumes a stalled job from the platform's own record of its progress. Present only when the parent
   * can reach the API; the button renders alongside (never instead of) Reset, because Reset discards the
   * checklist and Recover keeps it, and an operator needs to be able to see that difference at the moment
   * they choose.
   */
  onRecoverJob?: (note?: string) => Promise<void>;
  onSelectTurnSession?: (sessionId: string) => void;
  onOpenLightbox?: (item: LightboxAttachmentItem) => void;
  onUploadAttachments?: (files: File[]) => Promise<SessionAttachment[]>;
  onDeleteAttachment?: (attachmentId: string) => Promise<void>;
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
  onRecoverJob,
  onOpenLightbox,
  onUploadAttachments,
  onDeleteAttachment,
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
  const [showRecoverConfirm, setShowRecoverConfirm] = useState(false);
  const [isRecovering, setIsRecovering] = useState(false);
  const [isStarting, setIsStarting] = useState(false);

  const [prevJobId, setPrevJobId] = useState(job.id);
  const [prevPropAttachments, setPrevPropAttachments] = useState(job.attachments);
  const [attachments, setAttachments] = useState<SessionAttachment[]>(job.attachments ?? []);
  const [attachmentsTrayOverride, setAttachmentsTrayOverride] = useState<boolean | null>(null);
  const [isUploadingAttachments, setIsUploadingAttachments] = useState(false);
  const [deletingAttachmentId, setDeletingAttachmentId] = useState<string | null>(null);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [isDraggingOverCockpit, setIsDraggingOverCockpit] = useState(false);
  const [fallbackLightbox, setFallbackLightbox] = useState<LightboxAttachmentItem | null>(null);

  const cockpitRootRef = useRef<HTMLDivElement>(null);
  const cockpitFileInputRef = useRef<HTMLInputElement>(null);
  const cockpitDragCounterRef = useRef(0);
  const pasteSeqRef = useRef(0);
  const attachmentsRef = useRef<SessionAttachment[]>(attachments);

  if (prevJobId !== job.id) {
    setPrevJobId(job.id);
    setPrevPropAttachments(job.attachments);
    setAttachments(job.attachments ?? []);
    setAttachmentsTrayOverride(null);
    setAttachmentError(null);
    setIsDraggingOverCockpit(false);
    setFallbackLightbox(null);
  } else if (prevPropAttachments !== job.attachments) {
    setPrevPropAttachments(job.attachments);
    if (job.attachments !== undefined) {
      setAttachments(job.attachments ?? []);
    }
  }

  useEffect(() => {
    attachmentsRef.current = attachments;
  }, [attachments]);

  useEffect(() => {
    cockpitDragCounterRef.current = 0;
    pasteSeqRef.current = 0;
  }, [job.id]);

  const showAttachmentsBar =
    attachmentsTrayOverride !== null
      ? attachmentsTrayOverride
      : attachments.length > 0 || isUploadingAttachments || Boolean(attachmentError);

  const handleOpenLightbox = useCallback(
    (item: LightboxAttachmentItem) => {
      if (onOpenLightbox) {
        onOpenLightbox(item);
      } else {
        setFallbackLightbox(item);
      }
    },
    [onOpenLightbox]
  );

  const uploadFilesToJob = useCallback(
    async (files: File[]): Promise<SessionAttachment[]> => {
      const uploaded = onUploadAttachments
        ? await onUploadAttachments(files)
        : await api.uploadJobAttachments(job.id, files);
      const safeUploaded = Array.isArray(uploaded) ? uploaded : [];
      if (safeUploaded.length > 0) {
        setAttachments((prev) => mergeJobAttachments(prev, safeUploaded));
      }
      return safeUploaded;
    },
    [job.id, onUploadAttachments]
  );

  const deleteAttachmentFromJob = useCallback(
    async (attachmentId: string): Promise<void> => {
      const target = attachmentsRef.current.find((a) => a.id === attachmentId);
      if (onDeleteAttachment) {
        await onDeleteAttachment(attachmentId);
      } else {
        await api.deleteJobAttachment(job.id, attachmentId);
      }
      setAttachments((prev) => prev.filter((item) => item.id !== attachmentId));
      if (target) {
        const deletedUrl = resolveJobAttachmentUrl(job.id, target, false);
        setFallbackLightbox((cur) => (cur && cur.url === deletedUrl ? null : cur));
      }
    },
    [job.id, onDeleteAttachment]
  );

  const handleCockpitIncomingFiles = useCallback(
    async (rawFiles: File[], source: 'picker' | 'drop' | 'paste') => {
      if (rawFiles.length === 0 || isUploadingAttachments) return;

      let seq = nextScreenshotSequenceNumber(
        attachmentsRef.current.map((a) => a.fileName),
        pasteSeqRef.current
      ) - 1;

      const normalized =
        source === 'paste'
          ? rawFiles.map((file) => {
              seq += 1;
              return normalizePastedClipboardFile(file, seq);
            })
          : rawFiles;

      const validationProblem = validateJobAttachmentFiles(normalized, 0, MAX_JOB_ATTACHMENTS);
      if (validationProblem) {
        setAttachmentError(validationProblem);
        setAttachmentsTrayOverride(true);
        return;
      }

      if (source === 'paste') {
        pasteSeqRef.current = seq;
      }

      setAttachmentsTrayOverride(true);
      setIsUploadingAttachments(true);
      setAttachmentError(null);
      try {
        await uploadFilesToJob(normalized);
      } catch (err: any) {
        setAttachmentError(err?.message || 'Failed to upload attachments.');
      } finally {
        setIsUploadingAttachments(false);
      }
    },
    [isUploadingAttachments, uploadFilesToJob]
  );

  useEffect(() => {
    if (showJobSettingsModal || fallbackLightbox) return;

    const handleDocumentPaste = (e: ClipboardEvent) => {
      if (
        cockpitRootRef.current &&
        e.target instanceof Node &&
        cockpitRootRef.current.contains(e.target)
      ) {
        return;
      }
      if (
        document.querySelector(
          '[data-testid="new-job-modal"], [data-testid="job-settings-modal"], [data-testid="attachment-lightbox"]'
        )
      ) {
        return;
      }
      const files = extractFilesFromTransfer(e.clipboardData);
      if (files.length > 0) {
        e.preventDefault();
        void handleCockpitIncomingFiles(files, 'paste');
      }
    };

    document.addEventListener('paste', handleDocumentPaste);
    return () => document.removeEventListener('paste', handleDocumentPaste);
  }, [showJobSettingsModal, fallbackLightbox, handleCockpitIncomingFiles]);

  const handleCockpitPaste = (e: React.ClipboardEvent) => {
    if (showJobSettingsModal || fallbackLightbox) return;
    const files = extractFilesFromTransfer(e.clipboardData);
    if (files.length > 0) {
      e.preventDefault();
      void handleCockpitIncomingFiles(files, 'paste');
    }
  };

  const handleCockpitFileInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const chosen = Array.from(e.target.files ?? []);
    e.target.value = '';
    void handleCockpitIncomingFiles(chosen, 'picker');
  };

  const handleCockpitDragEnter = (e: React.DragEvent) => {
    if (showJobSettingsModal || fallbackLightbox) return;
    e.preventDefault();
    cockpitDragCounterRef.current += 1;
    setIsDraggingOverCockpit(true);
  };

  const handleCockpitDragOver = (e: React.DragEvent) => {
    if (showJobSettingsModal || fallbackLightbox) return;
    e.preventDefault();
    if (!isDraggingOverCockpit) {
      setIsDraggingOverCockpit(true);
    }
  };

  const handleCockpitDragLeave = (e: React.DragEvent) => {
    if (showJobSettingsModal || fallbackLightbox) return;
    e.preventDefault();
    cockpitDragCounterRef.current = Math.max(0, cockpitDragCounterRef.current - 1);
    if (cockpitDragCounterRef.current === 0) {
      setIsDraggingOverCockpit(false);
    }
  };

  const handleCockpitDrop = (e: React.DragEvent) => {
    if (showJobSettingsModal || fallbackLightbox) return;
    e.preventDefault();
    cockpitDragCounterRef.current = 0;
    setIsDraggingOverCockpit(false);
    const dropped = extractFilesFromTransfer(e.dataTransfer);
    void handleCockpitIncomingFiles(dropped, 'drop');
  };

  const handleDeleteCockpitAttachment = async (att: SessionAttachment) => {
    if (!att.id || deletingAttachmentId) return;
    setDeletingAttachmentId(att.id);
    setAttachmentError(null);
    try {
      await deleteAttachmentFromJob(att.id);
    } catch (err: any) {
      setAttachmentError(err?.message || 'Failed to delete attachment.');
    } finally {
      setDeletingAttachmentId(null);
    }
  };

  const isCompleted = job.status === ProjectJobStatus.Completed;
  const isArchived = job.status === ProjectJobStatus.Archived;
  const isPending = job.status === ProjectJobStatus.Pending;

  // The platform writes this when it settles a turn nobody is advancing. It is the only honest signal the
  // cockpit has that a job showing "Active Job" underneath has no agent working on it any more.
  const isStalled = job.lifecycleStatus === 'Stalled';

  // Recovery only applies to a job that is running in principle and stuck in practice. The badge above
  // already hides "Stalled" behind Archived/Completed, and the endpoint rejects an archived job with a
  // 409, so offering the button there would be a control that can only fail.
  const canRecover = Boolean(onRecoverJob) && isStalled && !isArchived && !isCompleted;

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

  const handleConfirmRecover = async () => {
    if (!onRecoverJob) return;
    setIsRecovering(true);
    try {
      await onRecoverJob();
      setShowRecoverConfirm(false);
    } catch {
      // The parent owns the error surface, and the confirm row deliberately stays open: closing it on a
      // failure would look exactly like a recovery that worked.
    } finally {
      setIsRecovering(false);
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
    <div
      ref={cockpitRootRef}
      data-testid="job-cockpit-root"
      onPaste={handleCockpitPaste}
      onDragEnter={handleCockpitDragEnter}
      onDragOver={handleCockpitDragOver}
      onDragLeave={handleCockpitDragLeave}
      onDrop={handleCockpitDrop}
      className="flex-1 flex flex-col h-full bg-slate-950 overflow-hidden relative"
    >
      <input
        ref={cockpitFileInputRef}
        type="file"
        multiple
        accept={JOB_ATTACHMENT_ACCEPT}
        onChange={handleCockpitFileInputChange}
        data-testid="cockpit-attachment-file-input"
        className="hidden"
      />

      {isDraggingOverCockpit && (
        <div
          data-testid="cockpit-drop-overlay"
          className="absolute inset-3 z-30 pointer-events-none rounded-2xl border-2 border-dashed border-sky-400 bg-sky-500/15 backdrop-blur-xs flex flex-col items-center justify-center gap-2 text-sky-100 shadow-2xl"
        >
          <Upload className="w-7 h-7 text-sky-300" />
          <span className="text-sm font-semibold">Drop files or screenshots to attach to this job</span>
        </div>
      )}

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
              ) : isStalled ? (
                <span
                  className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-amber-500/20 text-amber-300 border border-amber-500/40 flex items-center gap-1"
                  data-testid="job-status-stalled"
                  title="The agent's turn stopped reporting and was settled by the platform. Nothing is running."
                >
                  <Clock className="w-3 h-3" /> Stalled
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
          <div className="flex items-center gap-1.5 bg-slate-950/80 border border-slate-800 px-2.5 py-1.5 rounded-xl text-xs min-w-0 max-w-full">
            <span className="text-[11px] font-semibold text-slate-400 shrink-0">Assignee:</span>
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
              className="bg-slate-900 border border-slate-700/70 rounded-md px-2 py-0.5 text-xs text-sky-300 font-medium focus:outline-none focus:border-sky-500 cursor-pointer min-w-0 max-w-full truncate"
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

          <button
            type="button"
            onClick={() => setAttachmentsTrayOverride(!showAttachmentsBar)}
            title={
              showAttachmentsBar
                ? 'Hide Job Attachments tray'
                : 'Attach files or paste screenshots (Ctrl+V)'
            }
            aria-pressed={showAttachmentsBar}
            data-testid="toggle-job-attachments-btn"
            className={`inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-xl border text-xs font-semibold transition cursor-pointer ${
              showAttachmentsBar
                ? 'bg-sky-500/15 border-sky-500/30 text-sky-300'
                : 'bg-slate-950/80 border-slate-800 text-slate-400 hover:text-slate-200'
            }`}
          >
            <Paperclip className="w-3.5 h-3.5" />
            <span>Attachments ({attachments.length})</span>
          </button>

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
            <div className="flex flex-wrap items-center gap-1.5 sm:gap-2">
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

              {canRecover && (
                <div>
                  {showRecoverConfirm ? (
                    <div className="flex items-center gap-2 bg-slate-900 border border-amber-500/40 p-1.5 rounded-xl flex-wrap">
                      <span className="text-xs text-amber-200">
                        Settle the abandoned turn{onResetJob ? ' and resume from recorded progress' : ''}?
                      </span>
                      <button
                        type="button"
                        onClick={() => setShowRecoverConfirm(false)}
                        className="px-2 py-1 text-xs text-slate-400 hover:text-slate-200 cursor-pointer"
                      >
                        Cancel
                      </button>
                      <button
                        type="button"
                        onClick={handleConfirmRecover}
                        disabled={isRecovering}
                        className="px-3 py-1 bg-amber-400 hover:bg-amber-300 text-slate-950 text-xs font-bold rounded-lg shadow-sm cursor-pointer disabled:opacity-50"
                        data-testid="cockpit-confirm-recover-btn"
                      >
                        {isRecovering ? 'Recovering...' : 'Confirm Recover'}
                      </button>
                    </div>
                  ) : (
                    <button
                      type="button"
                      onClick={() => setShowRecoverConfirm(true)}
                      title="Settle the turn that stopped reporting and resume the job from the task it was on. Keeps the checklist and the workspace; unlike Reset, nothing is terminated."
                      className="px-3.5 py-1.5 bg-amber-500/15 hover:bg-amber-500/25 border border-amber-500/40 text-amber-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                      data-testid="cockpit-recover-job-btn"
                    >
                      <RefreshCw className="w-3.5 h-3.5" />
                      <span>Recover Job</span>
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

      {showAttachmentsBar && (
        <div
          data-testid="cockpit-attachments-bar"
          className="px-3 sm:px-6 py-2.5 border-b border-slate-800/80 bg-slate-900/40 shrink-0 space-y-2"
        >
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="flex items-center gap-2 text-xs text-slate-300">
              <Paperclip className="w-3.5 h-3.5 text-sky-400 shrink-0" />
              <span className="font-semibold">Job Attachments</span>
              <span className="text-[11px] text-slate-400 flex items-center gap-1">
                <ClipboardPaste className="w-3 h-3 text-slate-500" />
                <span>Paste screenshot anytime (Ctrl+V) or drag &amp; drop</span>
              </span>
              {isUploadingAttachments && (
                <span
                  data-testid="cockpit-attachment-uploading"
                  className="inline-flex items-center gap-1 text-[11px] text-sky-300 font-medium"
                >
                  <Loader2 className="w-3 h-3 animate-spin" />
                  <span>Uploading...</span>
                </span>
              )}
            </div>

            <button
              type="button"
              disabled={isUploadingAttachments}
              onClick={() => cockpitFileInputRef.current?.click()}
              data-testid="cockpit-upload-attachment-btn"
              className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-lg bg-sky-500/15 hover:bg-sky-500/25 border border-sky-500/30 text-sky-300 text-xs font-semibold transition disabled:opacity-50 cursor-pointer"
            >
              <Upload className="w-3.5 h-3.5" />
              <span>Attach Files</span>
            </button>
          </div>

          {attachmentError && (
            <div
              role="alert"
              data-testid="cockpit-attachment-error"
              className="px-3 py-1.5 rounded-lg bg-red-500/10 border border-red-500/30 flex items-center justify-between gap-2 text-xs text-red-300"
            >
              <div className="flex items-center gap-1.5 min-w-0">
                <AlertCircle className="w-3.5 h-3.5 shrink-0 text-red-400" />
                <span className="truncate">{attachmentError}</span>
              </div>
              <button
                type="button"
                onClick={() => setAttachmentError(null)}
                aria-label="Dismiss attachment error"
                className="text-red-400 hover:text-red-200 shrink-0 cursor-pointer"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            </div>
          )}

          {attachments.length === 0 && !isUploadingAttachments ? (
            <div
              role="button"
              tabIndex={0}
              onClick={() => cockpitFileInputRef.current?.click()}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  cockpitFileInputRef.current?.click();
                }
              }}
              data-testid="cockpit-attachment-empty-dropzone"
              className="rounded-xl border border-dashed border-slate-700/80 bg-slate-950/60 hover:border-sky-500/50 px-3 py-2.5 text-center text-xs text-slate-400 cursor-pointer transition"
            >
              No attachments yet — click to upload files, drag &amp; drop, or paste a screenshot (Ctrl+V).
            </div>
          ) : (
            <div
              data-testid="cockpit-attachments-list"
              className="flex flex-wrap items-center gap-2 max-h-36 overflow-y-auto"
            >
              {attachments.map((att) => {
                const contentUrl = resolveJobAttachmentUrl(job.id, att, false);
                const downloadUrl = resolveJobAttachmentUrl(job.id, att, true);
                const isImage = isImageAttachment(att.contentType, att.fileName);
                const isRemoving = deletingAttachmentId === att.id;
                const key = att.id || att.fileName;

                return (
                  <div
                    key={key}
                    data-testid={`cockpit-attachment-item-${key}`}
                    className="flex items-center gap-2 px-2.5 py-1.5 rounded-xl bg-slate-950 border border-slate-800 text-xs text-slate-200 max-w-xs min-w-0"
                  >
                    {isImage && contentUrl ? (
                      <button
                        type="button"
                        onClick={() =>
                          handleOpenLightbox({
                            url: contentUrl,
                            fileName: att.fileName,
                            sizeBytes: att.sizeBytes
                          })
                        }
                        aria-label={`Preview attachment ${att.fileName}`}
                        data-testid={`cockpit-attachment-preview-${key}`}
                        className="relative group w-8 h-8 rounded-lg overflow-hidden border border-slate-700 shrink-0 bg-slate-900 cursor-pointer focus:outline-none focus:ring-2 focus:ring-sky-500"
                      >
                        <img
                          src={contentUrl}
                          alt={att.fileName}
                          className="w-full h-full object-cover"
                        />
                        <span className="absolute inset-0 bg-slate-950/60 opacity-0 group-hover:opacity-100 flex items-center justify-center transition">
                          <Eye className="w-3 h-3 text-sky-300" />
                        </span>
                      </button>
                    ) : (
                      <div className="w-8 h-8 rounded-lg bg-slate-900 border border-slate-800 flex items-center justify-center shrink-0">
                        <FileText className="w-3.5 h-3.5 text-sky-400" />
                      </div>
                    )}

                    <div className="min-w-0 flex-1">
                      {isImage && contentUrl ? (
                        <button
                          type="button"
                          onClick={() =>
                            handleOpenLightbox({
                              url: contentUrl,
                              fileName: att.fileName,
                              sizeBytes: att.sizeBytes
                            })
                          }
                          title={`Preview ${att.fileName}`}
                          className="block max-w-full truncate font-mono text-[11px] text-slate-100 hover:text-sky-300 text-left cursor-pointer"
                        >
                          {att.fileName}
                        </button>
                      ) : downloadUrl ? (
                        <a
                          href={downloadUrl}
                          download={att.fileName}
                          title={`Download ${att.fileName}`}
                          className="block max-w-full truncate font-mono text-[11px] text-slate-100 hover:text-sky-300"
                        >
                          {att.fileName}
                        </a>
                      ) : (
                        <div className="truncate font-mono text-[11px] text-slate-100" title={att.fileName}>
                          {att.fileName}
                        </div>
                      )}
                      <div className="text-[10px] text-slate-400">
                        {formatAttachmentSize(att.sizeBytes)}
                      </div>
                    </div>

                    {downloadUrl && (
                      <a
                        href={downloadUrl}
                        download={att.fileName}
                        aria-label={`Download attachment ${att.fileName}`}
                        data-testid={`cockpit-attachment-download-${key}`}
                        className="p-1 rounded-lg text-slate-400 hover:text-sky-300 hover:bg-slate-800 transition shrink-0"
                      >
                        <Download className="w-3.5 h-3.5" />
                      </a>
                    )}

                    <button
                      type="button"
                      disabled={Boolean(deletingAttachmentId)}
                      onClick={() => void handleDeleteCockpitAttachment(att)}
                      aria-label={`Delete attachment ${att.fileName}`}
                      data-testid={`cockpit-attachment-delete-${key}`}
                      className="p-1 rounded-lg text-slate-400 hover:text-rose-400 hover:bg-slate-800 disabled:opacity-50 transition shrink-0 cursor-pointer"
                    >
                      {isRemoving ? (
                        <Loader2 className="w-3.5 h-3.5 animate-spin text-rose-400" />
                      ) : (
                        <X className="w-3.5 h-3.5" />
                      )}
                    </button>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      )}

      {/* Main Workspace: Structured Timeline Cockpit (StickySidebar + TimelineFeed) */}
      <div className="flex-1 min-h-0 overflow-hidden" data-testid="timeline-cockpit-container">
        <CockpitLayout
          jobId={job.id}
          job={{ ...job, attachments }}
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
          job={{ ...job, attachments }}
          availablePersonas={personas}
          onSave={async (updates) => {
            if (onUpdateJob) await onUpdateJob(updates);
          }}
          onDelete={onDeleteJob}
          onUploadAttachments={uploadFilesToJob}
          onDeleteAttachment={deleteAttachmentFromJob}
          onOpenLightbox={handleOpenLightbox}
        />
      )}

      <AttachmentLightboxModal
        attachment={fallbackLightbox}
        onClose={() => setFallbackLightbox(null)}
      />
    </div>
  );
};
