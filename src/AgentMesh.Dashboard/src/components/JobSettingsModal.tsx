import React, { useState, useEffect, useMemo, useRef, useCallback } from 'react';
import {
  X,
  Sliders,
  Save,
  AlertCircle,
  Trash2,
  ShieldCheck,
  Users,
  Paperclip,
  Upload,
  FileText,
  ClipboardPaste,
  Loader2,
  Download,
  Eye
} from 'lucide-react';
import { api } from '../services/api';
import type { Project, ProjectJob, Persona, SessionAttachment } from '../types';
import { ProjectJobStatus, PlanApprovalPolicy } from '../types';
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

interface JobSettingsModalProps {
  isOpen: boolean;
  onClose: () => void;
  project: Project;
  job: ProjectJob;
  availablePersonas?: Persona[];
  onSave: (req: {
    title?: string;
    description?: string;
    gitBranchName?: string;
    priority?: number;
    status?: ProjectJobStatus;
    approvalPolicy?: number;
    resultSummary?: string;
    assignedAgentId?: string | null;
    turnBudget?: number | null;
  }) => Promise<void>;
  onDelete?: () => Promise<void>;
  onUploadAttachments?: (files: File[]) => Promise<SessionAttachment[]>;
  onDeleteAttachment?: (attachmentId: string) => Promise<void>;
  onOpenLightbox?: (item: LightboxAttachmentItem) => void;
}

export const JobSettingsModal: React.FC<JobSettingsModalProps> = ({
  isOpen,
  onClose,
  project,
  job,
  availablePersonas = [],
  onSave,
  onDelete,
  onUploadAttachments,
  onDeleteAttachment,
  onOpenLightbox
}) => {
  const teamOptions = useMemo(() => {
    const list: Array<{ id: string; name: string; role: string }> = [];
    const seen = new Set<string>();

    for (const p of project.assignedPersonas || []) {
      if (!seen.has(p.id)) {
        seen.add(p.id);
        list.push({ id: p.id, name: p.name, role: p.role });
      }
    }
    for (const p of availablePersonas || []) {
      if (!seen.has(p.id)) {
        seen.add(p.id);
        list.push({ id: p.id, name: p.name, role: p.role });
      }
    }
    return list;
  }, [project.assignedPersonas, availablePersonas]);

  const [title, setTitle] = useState(job.title || '');
  const [description, setDescription] = useState(job.description || '');
  const [assignedAgentId, setAssignedAgentId] = useState(job.assignedAgentId || '');
  const [gitBranch, setGitBranch] = useState(job.gitBranchName || '');
  const [priority, setPriority] = useState<number>(job.priority ?? 1);
  const [status, setStatus] = useState<ProjectJobStatus>((job.status as ProjectJobStatus) ?? ProjectJobStatus.Active);
  const [approvalPolicy, setApprovalPolicy] = useState<number>(job.approvalPolicy ?? PlanApprovalPolicy.Autonomous);
  const [resultSummary, setResultSummary] = useState(job.resultSummary || '');
  const [attachments, setAttachments] = useState<SessionAttachment[]>(job.attachments ?? []);
  const [isDragging, setIsDragging] = useState(false);
  const [isUploadingAttachments, setIsUploadingAttachments] = useState(false);
  const [deletingAttachmentId, setDeletingAttachmentId] = useState<string | null>(null);
  const [fallbackLightbox, setFallbackLightbox] = useState<LightboxAttachmentItem | null>(null);
  // Text, not number: blank must mean "leave unchanged" (the API treats a missing TurnBudget as a
  // no-op and there is no way to clear one back to the platform default). A number input's NaN on
  // blur would silently send nothing while looking edited.
  const [turnBudget, setTurnBudget] = useState<string>(job.turnBudget != null ? String(job.turnBudget) : '');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [prevSessionKey, setPrevSessionKey] = useState<string | null>(
    isOpen ? job.id : null
  );
  const [prevPropAttachments, setPrevPropAttachments] = useState(job.attachments);

  const modalRef = useRef<HTMLDivElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const dragCounterRef = useRef(0);
  const pasteSeqRef = useRef(0);
  const attachmentsRef = useRef<SessionAttachment[]>(attachments);

  const currentSessionKey = isOpen ? job.id : null;
  if (prevSessionKey !== currentSessionKey) {
    setPrevSessionKey(currentSessionKey);
    setPrevPropAttachments(job.attachments);
    if (!isOpen) {
      setIsDragging(false);
      setFallbackLightbox(null);
    } else {
      setTitle(job.title || '');
      setDescription(job.description || '');
      setAssignedAgentId(job.assignedAgentId || '');
      setGitBranch(job.gitBranchName || '');
      setPriority(job.priority ?? 1);
      setStatus((job.status as ProjectJobStatus) ?? ProjectJobStatus.Active);
      setApprovalPolicy(job.approvalPolicy ?? PlanApprovalPolicy.Autonomous);
      setResultSummary(job.resultSummary || '');
      setAttachments(job.attachments ?? []);
      setTurnBudget(job.turnBudget != null ? String(job.turnBudget) : '');
      setConfirmDelete(false);
      setError(null);
    }
  } else if (isOpen && prevPropAttachments !== job.attachments) {
    setPrevPropAttachments(job.attachments);
    if (job.attachments !== undefined) {
      setAttachments(job.attachments ?? []);
    }
  }

  useEffect(() => {
    attachmentsRef.current = attachments;
  }, [attachments]);

  useEffect(() => {
    if (!isOpen) {
      dragCounterRef.current = 0;
      pasteSeqRef.current = 0;
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  }, [isOpen, job.id]);

  const handleUploadIncomingFiles = useCallback(
    async (rawFiles: File[], source: 'picker' | 'drop' | 'paste') => {
      if (rawFiles.length === 0 || isUploadingAttachments || isSubmitting) return;

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
        setError(validationProblem);
        return;
      }

      if (source === 'paste') {
        pasteSeqRef.current = seq;
      }

      setIsUploadingAttachments(true);
      setError(null);
      try {
        const uploaded = onUploadAttachments
          ? await onUploadAttachments(normalized)
          : await api.uploadJobAttachments(job.id, normalized);
        if (Array.isArray(uploaded) && uploaded.length > 0) {
          setAttachments((prev) => mergeJobAttachments(prev, uploaded));
        }
      } catch (err: any) {
        setError(err?.message || 'Failed to upload attachments.');
      } finally {
        setIsUploadingAttachments(false);
      }
    },
    [isUploadingAttachments, isSubmitting, job.id, onUploadAttachments]
  );

  useEffect(() => {
    if (!isOpen || fallbackLightbox) return;

    const handleDocumentPaste = (e: ClipboardEvent) => {
      if (modalRef.current && e.target instanceof Node && modalRef.current.contains(e.target)) {
        return;
      }
      const files = extractFilesFromTransfer(e.clipboardData);
      if (files.length > 0) {
        e.preventDefault();
        void handleUploadIncomingFiles(files, 'paste');
      }
    };

    document.addEventListener('paste', handleDocumentPaste);
    return () => document.removeEventListener('paste', handleDocumentPaste);
  }, [isOpen, fallbackLightbox, handleUploadIncomingFiles]);

  if (!isOpen) return null;

  const handleModalPaste = (e: React.ClipboardEvent) => {
    const files = extractFilesFromTransfer(e.clipboardData);
    if (files.length > 0) {
      e.preventDefault();
      void handleUploadIncomingFiles(files, 'paste');
    }
  };

  const handleFileInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const chosen = Array.from(e.target.files ?? []);
    e.target.value = '';
    void handleUploadIncomingFiles(chosen, 'picker');
  };

  const handleDragEnter = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (isUploadingAttachments || isSubmitting) return;
    dragCounterRef.current += 1;
    setIsDragging(true);
  };

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (!isUploadingAttachments && !isSubmitting && !isDragging) {
      setIsDragging(true);
    }
  };

  const handleDragLeave = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    dragCounterRef.current = Math.max(0, dragCounterRef.current - 1);
    if (dragCounterRef.current === 0) {
      setIsDragging(false);
    }
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    dragCounterRef.current = 0;
    setIsDragging(false);
    if (isUploadingAttachments || isSubmitting) return;
    const dropped = extractFilesFromTransfer(e.dataTransfer);
    void handleUploadIncomingFiles(dropped, 'drop');
  };

  const handlePreviewAttachment = (att: SessionAttachment) => {
    const url = resolveJobAttachmentUrl(job.id, att, false);
    if (!url) return;
    const item: LightboxAttachmentItem = {
      url,
      fileName: att.fileName,
      sizeBytes: att.sizeBytes
    };
    if (onOpenLightbox) {
      onOpenLightbox(item);
    } else {
      setFallbackLightbox(item);
    }
  };

  const handleRemoveAttachment = async (att: SessionAttachment) => {
    if (!att.id || deletingAttachmentId) return;
    setDeletingAttachmentId(att.id);
    setError(null);
    try {
      if (onDeleteAttachment) {
        await onDeleteAttachment(att.id);
      } else {
        await api.deleteJobAttachment(job.id, att.id);
      }
      const deletedUrl = resolveJobAttachmentUrl(job.id, att, false);
      setAttachments((prev) => prev.filter((item) => item.id !== att.id));
      if (fallbackLightbox && fallbackLightbox.url === deletedUrl) {
        setFallbackLightbox(null);
      }
    } catch (err: any) {
      setError(err?.message || 'Failed to delete attachment.');
    } finally {
      setDeletingAttachmentId(null);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim() || isSubmitting) {
      if (!title.trim()) setError('Job Title is required.');
      return;
    }
    const trimmedBudget = turnBudget.trim();
    if (trimmedBudget) {
      const parsed = Number(trimmedBudget);
      if (!Number.isInteger(parsed) || parsed < 1 || parsed > 1000) {
        setError('Turn budget must be a whole number between 1 and 1000 (blank keeps the current value).');
        return;
      }
    }
    setIsSubmitting(true);
    setError(null);
    try {
      await onSave({
        title: title.trim(),
        description: description.trim(),
        gitBranchName: gitBranch.trim() || undefined,
        priority,
        status,
        approvalPolicy,
        resultSummary: resultSummary.trim() || undefined,
        assignedAgentId: assignedAgentId.trim(),
        turnBudget: trimmedBudget ? Number(trimmedBudget) : undefined
      });
      onClose();
    } catch (err: any) {
      setError(err?.message || 'Failed to update job settings.');
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete || isDeleting) return;
    setIsDeleting(true);
    setError(null);
    try {
      await onDelete();
      onClose();
    } catch (err: any) {
      setError(err?.message || 'Failed to delete job.');
      setIsDeleting(false);
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-end sm:items-center justify-center bg-slate-950/80 backdrop-blur-sm p-0 sm:p-4 overflow-y-auto"
      data-testid="job-settings-modal"
    >
      <div
        ref={modalRef}
        onPaste={handleModalPaste}
        onDragEnter={handleDragEnter}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        className="bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl w-full max-w-lg overflow-hidden shadow-2xl my-0 sm:my-8 max-h-[90dvh] flex flex-col"
      >
        {/* Modal Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-slate-800 bg-slate-950/50">
          <div className="flex items-center gap-2.5">
            <div className="w-8 h-8 rounded-lg bg-sky-500/20 text-sky-400 flex items-center justify-center border border-sky-500/30">
              <Sliders className="w-4 h-4" />
            </div>
            <div>
              <h2 className="text-base font-semibold text-slate-100">Job Settings</h2>
              <p className="text-xs text-slate-400">
                Configure job details in <span className="font-semibold text-slate-300">"{project.name}"</span>
              </p>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="p-1.5 rounded-lg text-slate-400 hover:text-slate-200 hover:bg-slate-800 transition cursor-pointer"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-4 overflow-y-auto flex-1">
          {error && (
            <div
              role="alert"
              data-testid="job-settings-error"
              className="p-3 bg-red-500/10 border border-red-500/30 rounded-xl flex items-center gap-2 text-xs text-red-300"
            >
              <AlertCircle className="w-4 h-4 shrink-0 text-red-400" />
              <span>{error}</span>
            </div>
          )}

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-1.5">
              Job Title <span className="text-red-400">*</span>
            </label>
            <input
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="Job title..."
              required
              data-testid="job-settings-title-input"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-sm text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition"
            />
          </div>

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-1.5">
              Description / Objective
            </label>
            <textarea
              rows={3}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Summary of feature scope, requirements, or deliverables..."
              data-testid="job-settings-desc-input"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition leading-relaxed"
            />
          </div>

          <div>
            <div className="flex items-center justify-between mb-1.5">
              <label className="text-xs font-semibold text-slate-300 flex items-center gap-1.5">
                <Paperclip className="w-3.5 h-3.5 text-sky-400" />
                <span>Attachments</span>
                {attachments.length > 0 && (
                  <span
                    data-testid="job-settings-attachment-count"
                    className="px-1.5 py-0.5 rounded-full text-[10px] font-bold bg-sky-500/20 text-sky-300 border border-sky-500/30"
                  >
                    {attachments.length}
                  </span>
                )}
              </label>
              <span className="text-[11px] text-slate-400 flex items-center gap-1">
                <ClipboardPaste className="w-3 h-3 text-slate-500" />
                <span>Paste screenshot (Ctrl+V)</span>
              </span>
            </div>

            <input
              ref={fileInputRef}
              type="file"
              multiple
              accept={JOB_ATTACHMENT_ACCEPT}
              onChange={handleFileInputChange}
              data-testid="job-settings-file-input"
              className="hidden"
            />

            <div
              role="button"
              tabIndex={0}
              aria-label="Upload or drop job attachments"
              data-testid="job-settings-attachment-dropzone"
              data-dragging={isDragging ? 'true' : 'false'}
              onClick={() => {
                if (!isUploadingAttachments && !isSubmitting) fileInputRef.current?.click();
              }}
              onKeyDown={(e) => {
                if ((e.key === 'Enter' || e.key === ' ') && !isUploadingAttachments && !isSubmitting) {
                  e.preventDefault();
                  fileInputRef.current?.click();
                }
              }}
              className={`rounded-xl border border-dashed px-4 py-3 text-center transition cursor-pointer select-none ${
                isDragging
                  ? 'border-sky-400 bg-sky-500/10 text-sky-200'
                  : 'border-slate-700/80 bg-slate-950/70 hover:border-sky-500/60 hover:bg-slate-950 text-slate-300'
              }`}
            >
              <div className="flex items-center justify-center gap-2 text-xs font-medium">
                {isUploadingAttachments ? (
                  <>
                    <Loader2 className="w-4 h-4 text-sky-400 animate-spin" />
                    <span data-testid="job-settings-attachment-uploading">Uploading attachments...</span>
                  </>
                ) : (
                  <>
                    <Upload className={`w-4 h-4 ${isDragging ? 'text-sky-300' : 'text-sky-400'}`} />
                    <span>
                      {isDragging
                        ? 'Drop files or screenshots here'
                        : 'Click to upload, drag & drop files, or paste screenshots'}
                    </span>
                  </>
                )}
              </div>
              <p className="text-[11px] text-slate-500 mt-1">
                Images, PDFs, Markdown, code, JSON/CSV/logs, or ZIP up to 25 MB
              </p>
            </div>

            {attachments.length > 0 && (
              <div
                data-testid="job-settings-attachments-list"
                className="mt-2.5 grid grid-cols-1 sm:grid-cols-2 gap-2"
              >
                {attachments.map((att) => {
                  const contentUrl = resolveJobAttachmentUrl(job.id, att, false);
                  const downloadUrl = resolveJobAttachmentUrl(job.id, att, true);
                  const isImage = isImageAttachment(att.contentType, att.fileName);
                  const isRemoving = deletingAttachmentId === att.id;

                  return (
                    <div
                      key={att.id || att.fileName}
                      data-testid={`job-settings-attachment-item-${att.id || att.fileName}`}
                      className="flex items-center gap-2.5 px-2.5 py-2 rounded-xl bg-slate-950 border border-slate-800 text-xs text-slate-200 min-w-0"
                    >
                      {isImage && contentUrl ? (
                        <button
                          type="button"
                          onClick={() => handlePreviewAttachment(att)}
                          aria-label={`Preview attachment ${att.fileName}`}
                          data-testid={`job-settings-attachment-preview-${att.id || att.fileName}`}
                          className="relative group w-9 h-9 rounded-lg overflow-hidden border border-slate-700 shrink-0 bg-slate-900 cursor-pointer focus:outline-none focus:ring-2 focus:ring-sky-500"
                        >
                          <img
                            src={contentUrl}
                            alt={att.fileName}
                            className="w-full h-full object-cover"
                          />
                          <span className="absolute inset-0 bg-slate-950/60 opacity-0 group-hover:opacity-100 flex items-center justify-center transition">
                            <Eye className="w-3.5 h-3.5 text-sky-300" />
                          </span>
                        </button>
                      ) : (
                        <div className="w-9 h-9 rounded-lg bg-slate-900 border border-slate-800 flex items-center justify-center shrink-0">
                          <FileText className="w-4 h-4 text-sky-400" />
                        </div>
                      )}

                      <div className="min-w-0 flex-1">
                        {isImage && contentUrl ? (
                          <button
                            type="button"
                            onClick={() => handlePreviewAttachment(att)}
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
                          className="p-1 rounded-lg text-slate-400 hover:text-sky-300 hover:bg-slate-800 transition shrink-0"
                        >
                          <Download className="w-3.5 h-3.5" />
                        </a>
                      )}

                      <button
                        type="button"
                        disabled={Boolean(deletingAttachmentId) || isSubmitting}
                        onClick={() => void handleRemoveAttachment(att)}
                        aria-label={`Delete attachment ${att.fileName}`}
                        data-testid={`job-settings-attachment-delete-${att.id || att.fileName}`}
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

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-1.5 flex items-center gap-1">
              <Users className="w-3.5 h-3.5 text-sky-400" />
              <span>Assigned Team Member</span>
            </label>
            <select
              value={assignedAgentId}
              onChange={(e) => setAssignedAgentId(e.target.value)}
              aria-label="Assigned Team Member"
              data-testid="job-settings-assigned-agent"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 focus:outline-none focus:border-sky-500 transition cursor-pointer"
            >
              <option value="">Default (@{project.defaultLeadPersonaId || 'implementer'})</option>
              {teamOptions.map((p) => (
                <option key={p.id} value={p.id}>
                  @{p.id} — {p.name} ({p.role})
                </option>
              ))}
            </select>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5">
                Priority
              </label>
              <select
                value={priority}
                onChange={(e) => setPriority(Number(e.target.value))}
                data-testid="job-settings-priority-select"
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 focus:outline-none focus:border-sky-500 transition cursor-pointer"
              >
                <option value={0}>Low</option>
                <option value={1}>Normal</option>
                <option value={2}>High</option>
                <option value={3}>Urgent</option>
              </select>
            </div>

            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5">
                Status
              </label>
              <select
                value={status}
                onChange={(e) => setStatus(Number(e.target.value) as ProjectJobStatus)}
                data-testid="job-settings-status-select"
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 focus:outline-none focus:border-sky-500 transition cursor-pointer"
              >
                <option value={ProjectJobStatus.Pending}>Pending</option>
                <option value={ProjectJobStatus.Active}>Active</option>
                <option value={ProjectJobStatus.Completed}>Completed</option>
                <option value={ProjectJobStatus.Archived}>Archived</option>
              </select>
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5 flex items-center gap-1">
                <ShieldCheck className="w-3.5 h-3.5 text-sky-400" />
                <span>Plan Approval Policy</span>
              </label>
              <select
                value={approvalPolicy}
                onChange={(e) => setApprovalPolicy(Number(e.target.value))}
                data-testid="job-settings-policy-select"
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 focus:outline-none focus:border-sky-500 transition cursor-pointer"
              >
                <option value={PlanApprovalPolicy.Autonomous}>Autonomous (Default)</option>
                <option value={PlanApprovalPolicy.LeadOrHuman}>Lead or Human</option>
                <option value={PlanApprovalPolicy.HumanOnly}>Human Operator Only</option>
              </select>
            </div>

            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5">
                Git Branch Name
              </label>
              <input
                type="text"
                value={gitBranch}
                onChange={(e) => setGitBranch(e.target.value)}
                placeholder="e.g. mesh/feature-branch"
                data-testid="job-settings-branch-input"
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition font-mono"
              />
            </div>
          </div>

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-1.5">
              Task Turn Budget
            </label>
            <input
              type="number"
              min={1}
              max={1000}
              step={1}
              value={turnBudget}
              onChange={(e) => setTurnBudget(e.target.value)}
              placeholder="40 (platform default)"
              data-testid="job-settings-turn-budget-input"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition font-mono"
            />
            <p className="text-[11px] text-slate-500 mt-1">
              Turns one task may spend before the workflow halts it. Raise it when a job exhausted its
              budget on infrastructure failures (rate limits, expired logins) rather than on the work,
              then press Recover Job to run with the new budget. Blank keeps the current value; it
              applies to the next run, never one already going.
            </p>
          </div>

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-1.5">
              Result Summary / Notes <span className="text-slate-500 font-normal">(optional)</span>
            </label>
            <textarea
              rows={2}
              value={resultSummary}
              onChange={(e) => setResultSummary(e.target.value)}
              placeholder="Resolution summary or closing notes when completed..."
              data-testid="job-settings-summary-input"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition"
            />
          </div>

          {/* Danger Zone: Delete Job */}
          {onDelete && (
            <div className="pt-3 border-t border-slate-800 space-y-2">
              <div className="flex items-center justify-between">
                <div>
                  <div className="text-xs font-semibold text-red-400 flex items-center gap-1.5">
                    <Trash2 className="w-3.5 h-3.5" />
                    <span>Delete Job</span>
                  </div>
                  <p className="text-[11px] text-slate-400 mt-0.5">
                    Permanently remove this job and its message turns.
                  </p>
                </div>
                {!confirmDelete ? (
                  <button
                    type="button"
                    onClick={() => setConfirmDelete(true)}
                    className="px-3 py-1.5 rounded-lg border border-red-500/30 text-red-400 hover:bg-red-950/40 text-xs font-medium transition cursor-pointer"
                    data-testid="job-settings-delete-init-btn"
                  >
                    Delete...
                  </button>
                ) : (
                  <div className="flex items-center gap-1.5">
                    <button
                      type="button"
                      onClick={() => setConfirmDelete(false)}
                      className="px-2.5 py-1 text-xs text-slate-400 hover:text-slate-200"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      onClick={handleDelete}
                      disabled={isDeleting}
                      className="px-3 py-1 bg-red-600 hover:bg-red-500 text-white font-bold rounded-lg text-xs transition cursor-pointer shadow-sm"
                      data-testid="job-settings-delete-confirm-btn"
                    >
                      {isDeleting ? 'Deleting...' : 'Confirm'}
                    </button>
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Action buttons */}
          <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-800 pb-[max(0.5rem,env(safe-area-inset-bottom))]">
            <button
              type="button"
              onClick={onClose}
              disabled={isSubmitting}
              className="px-4 py-2 rounded-xl text-xs font-semibold text-slate-400 hover:text-slate-200 hover:bg-slate-800 transition cursor-pointer"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSubmitting}
              className="px-5 py-2 bg-sky-500 hover:bg-sky-400 disabled:opacity-50 text-slate-950 text-xs font-bold rounded-xl shadow-md transition flex items-center gap-1.5 cursor-pointer"
              data-testid="job-settings-save-btn"
            >
              <Save className="w-4 h-4" />
              <span>{isSubmitting ? 'Saving...' : 'Save Settings'}</span>
            </button>
          </div>
        </form>
      </div>
      <AttachmentLightboxModal
        attachment={fallbackLightbox}
        onClose={() => setFallbackLightbox(null)}
      />
    </div>
  );
};
