import React, { useState, useMemo, useRef, useEffect, useCallback } from 'react';
import {
  X,
  Briefcase,
  Plus,
  AlertCircle,
  ShieldCheck,
  Users,
  Upload,
  FileText,
  Paperclip,
  ClipboardPaste
} from 'lucide-react';
import type { Project, Persona } from '../types';
import { PlanApprovalPolicy } from '../types';
import {
  MAX_JOB_ATTACHMENTS,
  JOB_ATTACHMENT_ACCEPT,
  type StagedLocalAttachment,
  formatAttachmentSize,
  extractFilesFromTransfer,
  normalizePastedClipboardFile,
  validateJobAttachmentFiles,
  createImagePreviewUrl,
  revokeImagePreviewUrl
} from '../utils/jobAttachments';

export interface CreateJobModalPayload {
  title: string;
  description?: string;
  priority?: number;
  approvalPolicy?: number;
  gitBranchName?: string;
  initialTasks?: string[];
  assignedAgentId?: string;
  files?: File[];
}

interface NewJobModalProps {
  isOpen: boolean;
  onClose: () => void;
  project: Project;
  availablePersonas?: Persona[];
  onCreate: (req: CreateJobModalPayload) => Promise<void>;
}

export const NewJobModal: React.FC<NewJobModalProps> = ({
  isOpen,
  onClose,
  project,
  availablePersonas = [],
  onCreate
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

  const defaultAssignee = project.defaultLeadPersonaId || teamOptions[0]?.id || '';
  const [wasOpen, setWasOpen] = useState(isOpen);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [assignedAgentId, setAssignedAgentId] = useState(defaultAssignee);
  const [priority, setPriority] = useState<number>(1);
  const [approvalPolicy, setApprovalPolicy] = useState<number>(PlanApprovalPolicy.Autonomous);
  const [stagedAttachments, setStagedAttachments] = useState<StagedLocalAttachment[]>([]);
  const [isDragging, setIsDragging] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (wasOpen !== isOpen) {
    setWasOpen(isOpen);
    if (!isOpen) {
      setStagedAttachments([]);
      setTitle('');
      setDescription('');
      setAssignedAgentId(defaultAssignee);
      setPriority(1);
      setApprovalPolicy(PlanApprovalPolicy.Autonomous);
      setIsDragging(false);
      setIsSubmitting(false);
      setError(null);
    }
  }

  const fileInputRef = useRef<HTMLInputElement>(null);
  const modalRef = useRef<HTMLDivElement>(null);
  const dragCounterRef = useRef(0);
  const pasteSeqRef = useRef(0);
  const stagedCountRef = useRef(0);
  const previewUrlsRef = useRef<Set<string>>(new Set());

  const revokeAllTrackedPreviews = useCallback(() => {
    for (const url of previewUrlsRef.current) {
      revokeImagePreviewUrl(url);
    }
    previewUrlsRef.current.clear();
  }, []);

  useEffect(() => {
    stagedCountRef.current = stagedAttachments.length;
  }, [stagedAttachments.length]);

  useEffect(() => {
    return () => {
      revokeAllTrackedPreviews();
    };
  }, [revokeAllTrackedPreviews]);

  useEffect(() => {
    if (!isOpen) {
      revokeAllTrackedPreviews();
      stagedCountRef.current = 0;
      dragCounterRef.current = 0;
      pasteSeqRef.current = 0;
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  }, [isOpen, revokeAllTrackedPreviews]);

  const stageIncomingFiles = useCallback(
    (rawFiles: File[], source: 'picker' | 'drop' | 'paste') => {
      if (rawFiles.length === 0 || isSubmitting) return;

      const normalized =
        source === 'paste'
          ? rawFiles.map((file) => {
              pasteSeqRef.current += 1;
              return normalizePastedClipboardFile(file, pasteSeqRef.current);
            })
          : rawFiles;

      const validationProblem = validateJobAttachmentFiles(
        normalized,
        stagedCountRef.current,
        MAX_JOB_ATTACHMENTS
      );
      if (validationProblem) {
        setError(validationProblem);
        return;
      }

      const now = Date.now();
      const newItems: StagedLocalAttachment[] = normalized.map((file, idx) => {
        const previewUrl = createImagePreviewUrl(file);
        if (previewUrl) {
          previewUrlsRef.current.add(previewUrl);
        }
        return {
          localId: `job-att-${now}-${idx}-${Math.random().toString(36).slice(2, 6)}`,
          file,
          name: file.name,
          contentType: file.type || 'application/octet-stream',
          sizeBytes: file.size,
          previewUrl
        };
      });

      stagedCountRef.current += newItems.length;
      setError(null);
      setStagedAttachments((prev) => [...prev, ...newItems]);
    },
    [isSubmitting]
  );

  useEffect(() => {
    if (!isOpen) return;

    const handleDocumentPaste = (e: ClipboardEvent) => {
      if (modalRef.current && e.target instanceof Node && modalRef.current.contains(e.target)) {
        return;
      }
      const files = extractFilesFromTransfer(e.clipboardData);
      if (files.length > 0) {
        e.preventDefault();
        stageIncomingFiles(files, 'paste');
      }
    };

    document.addEventListener('paste', handleDocumentPaste);
    return () => document.removeEventListener('paste', handleDocumentPaste);
  }, [isOpen, stageIncomingFiles]);

  if (!isOpen) return null;

  const handleModalPaste = (e: React.ClipboardEvent) => {
    const files = extractFilesFromTransfer(e.clipboardData);
    if (files.length > 0) {
      e.preventDefault();
      stageIncomingFiles(files, 'paste');
    }
  };

  const handleFileInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const chosen = Array.from(e.target.files ?? []);
    stageIncomingFiles(chosen, 'picker');
    e.target.value = '';
  };

  const handleRemoveAttachment = (localId: string) => {
    if (isSubmitting) return;
    setStagedAttachments((prev) => {
      const target = prev.find((item) => item.localId === localId);
      if (target) {
        if (target.previewUrl) {
          previewUrlsRef.current.delete(target.previewUrl);
          revokeImagePreviewUrl(target.previewUrl);
        }
        stagedCountRef.current = Math.max(0, stagedCountRef.current - 1);
      }
      return prev.filter((item) => item.localId !== localId);
    });
  };

  const handleDragEnter = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (isSubmitting) return;
    dragCounterRef.current += 1;
    setIsDragging(true);
  };

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    if (!isSubmitting && !isDragging) {
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
    if (isSubmitting) return;
    const droppedFiles = extractFilesFromTransfer(e.dataTransfer);
    stageIncomingFiles(droppedFiles, 'drop');
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim() || isSubmitting) {
      if (!title.trim()) setError('Job Title is required.');
      return;
    }
    setIsSubmitting(true);
    setError(null);
    try {
      const files = stagedAttachments.map((item) => item.file);
      await onCreate({
        title: title.trim(),
        description: description.trim() || undefined,
        priority,
        approvalPolicy,
        assignedAgentId: assignedAgentId.trim() || undefined,
        ...(files.length > 0 ? { files } : {})
      });
      onClose();
    } catch (err: any) {
      setError(err?.message || 'Failed to create job.');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-end sm:items-center justify-center bg-slate-950/80 backdrop-blur-sm p-0 sm:p-4 overflow-y-auto">
      <div
        ref={modalRef}
        data-testid="new-job-modal"
        onPaste={handleModalPaste}
        onDragEnter={handleDragEnter}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        className="bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl w-full max-w-lg overflow-hidden shadow-2xl my-0 sm:my-8 max-h-[90dvh] flex flex-col"
      >
        <div className="flex items-center justify-between px-6 py-4 border-b border-slate-800 bg-slate-950/50 shrink-0">
          <div className="flex items-center gap-2.5">
            <div className="w-8 h-8 rounded-lg bg-sky-500/20 text-sky-400 flex items-center justify-center border border-sky-500/30">
              <Briefcase className="w-4 h-4" />
            </div>
            <div>
              <h2 className="text-base font-semibold text-slate-100">Create New Job</h2>
              <p className="text-xs text-slate-400">
                Inside Project <span className="font-semibold text-slate-300">"{project.name}"</span>
              </p>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="p-1.5 rounded-lg text-slate-400 hover:text-slate-200 hover:bg-slate-800 transition"
          >
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-6 space-y-4 overflow-y-auto flex-1 min-h-0">
          {error && (
            <div
              role="alert"
              data-testid="new-job-error"
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
              placeholder="e.g. Implement OAuth2 flow with Google & GitHub"
              required
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
              placeholder="Brief summary of feature scope or bug to resolve. Paste screenshots directly (Ctrl+V / ⌘V) or attach files below."
              data-testid="new-job-description-input"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition leading-relaxed"
            />
          </div>

          <div>
            <div className="flex items-center justify-between mb-1.5">
              <label className="text-xs font-semibold text-slate-300 flex items-center gap-1.5">
                <Paperclip className="w-3.5 h-3.5 text-sky-400" />
                <span>Attachments</span>
                {stagedAttachments.length > 0 && (
                  <span
                    data-testid="new-job-attachment-count"
                    className="px-1.5 py-0.5 rounded-full text-[10px] font-bold bg-sky-500/20 text-sky-300 border border-sky-500/30"
                  >
                    {stagedAttachments.length}/{MAX_JOB_ATTACHMENTS}
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
              data-testid="new-job-file-input"
              className="hidden"
            />

            <div
              role="button"
              tabIndex={0}
              aria-label="Upload or drop job attachments"
              data-testid="new-job-attachment-dropzone"
              data-dragging={isDragging ? 'true' : 'false'}
              onClick={() => {
                if (!isSubmitting) fileInputRef.current?.click();
              }}
              onKeyDown={(e) => {
                if ((e.key === 'Enter' || e.key === ' ') && !isSubmitting) {
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
                <Upload className={`w-4 h-4 ${isDragging ? 'text-sky-300' : 'text-sky-400'}`} />
                <span>
                  {isDragging
                    ? 'Drop files or screenshots here'
                    : 'Click to upload, drag & drop files, or paste screenshots'}
                </span>
              </div>
              <p className="text-[11px] text-slate-500 mt-1">
                Images, PDFs, Markdown, code, JSON/CSV/logs, or ZIP up to 25 MB (max {MAX_JOB_ATTACHMENTS})
              </p>
            </div>

            {stagedAttachments.length > 0 && (
              <div
                data-testid="new-job-staged-attachments"
                className="mt-2.5 grid grid-cols-1 sm:grid-cols-2 gap-2"
              >
                {stagedAttachments.map((att) => (
                  <div
                    key={att.localId}
                    data-testid={`new-job-staged-attachment-${att.localId}`}
                    className="flex items-center gap-2.5 px-2.5 py-2 rounded-xl bg-slate-950 border border-slate-800 text-xs text-slate-200 min-w-0"
                  >
                    {att.previewUrl ? (
                      <img
                        src={att.previewUrl}
                        alt={att.name}
                        data-testid="new-job-attachment-preview"
                        className="w-9 h-9 rounded-lg object-cover border border-slate-700 shrink-0 bg-slate-900"
                      />
                    ) : (
                      <div className="w-9 h-9 rounded-lg bg-slate-900 border border-slate-800 flex items-center justify-center shrink-0">
                        <FileText className="w-4 h-4 text-sky-400" />
                      </div>
                    )}
                    <div className="min-w-0 flex-1">
                      <div className="truncate font-mono text-[11px] text-slate-100" title={att.name}>
                        {att.name}
                      </div>
                      <div className="text-[10px] text-slate-400">
                        {formatAttachmentSize(att.sizeBytes)}
                      </div>
                    </div>
                    <button
                      type="button"
                      disabled={isSubmitting}
                      onClick={() => handleRemoveAttachment(att.localId)}
                      aria-label={`Remove attachment ${att.name}`}
                      className="p-1 rounded-lg text-slate-400 hover:text-rose-400 hover:bg-slate-800 disabled:opacity-50 transition shrink-0 cursor-pointer"
                    >
                      <X className="w-3.5 h-3.5" />
                    </button>
                  </div>
                ))}
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
              data-testid="new-job-assigned-agent"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 focus:outline-none focus:border-sky-500 transition cursor-pointer"
            >
              {teamOptions.length === 0 ? (
                <option value="">Default (@{project.defaultLeadPersonaId || 'implementer'})</option>
              ) : (
                teamOptions.map((p) => (
                  <option key={p.id} value={p.id}>
                    @{p.id} — {p.name} ({p.role})
                  </option>
                ))
              )}
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
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 focus:outline-none focus:border-sky-500 transition cursor-pointer"
              >
                <option value={0}>Low</option>
                <option value={1}>Normal</option>
                <option value={2}>High</option>
                <option value={3}>Urgent</option>
              </select>
            </div>

            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5 flex items-center gap-1">
                <ShieldCheck className="w-3.5 h-3.5 text-sky-400" />
                <span>Plan Approval Policy</span>
              </label>
              <select
                value={approvalPolicy}
                onChange={(e) => setApprovalPolicy(Number(e.target.value))}
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 focus:outline-none focus:border-sky-500 transition cursor-pointer"
              >
                <option value={PlanApprovalPolicy.Autonomous}>Autonomous (Default)</option>
                <option value={PlanApprovalPolicy.LeadOrHuman}>Lead or Human</option>
                <option value={PlanApprovalPolicy.HumanOnly}>Human Operator Only</option>
              </select>
            </div>
          </div>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-800 pb-[max(0.5rem,env(safe-area-inset-bottom))]">
            <button
              type="button"
              onClick={onClose}
              disabled={isSubmitting}
              className="px-4 py-2 rounded-xl text-xs font-semibold text-slate-400 hover:text-slate-200 hover:bg-slate-800 transition"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSubmitting}
              className="px-5 py-2 bg-sky-500 hover:bg-sky-400 disabled:opacity-50 text-slate-950 text-xs font-bold rounded-xl shadow-md transition flex items-center gap-1.5 cursor-pointer"
              data-testid="create-job-submit-btn"
            >
              <Plus className="w-4 h-4" />
              <span>
                {isSubmitting
                  ? stagedAttachments.length > 0
                    ? 'Creating & Uploading...'
                    : 'Creating...'
                  : 'Create Job'}
              </span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
