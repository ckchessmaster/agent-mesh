import React, { useState, useEffect } from 'react';
import { X, Sliders, Save, AlertCircle, Trash2, CheckCircle2 } from 'lucide-react';
import type { Project, ProjectJob } from '../types';
import { ProjectJobStatus } from '../types';

interface JobSettingsModalProps {
  isOpen: boolean;
  onClose: () => void;
  project: Project;
  job: ProjectJob;
  onSave: (req: {
    title?: string;
    description?: string;
    gitBranchName?: string;
    priority?: number;
    status?: ProjectJobStatus;
    resultSummary?: string;
  }) => Promise<void>;
  onDelete?: () => Promise<void>;
}

export const JobSettingsModal: React.FC<JobSettingsModalProps> = ({
  isOpen,
  onClose,
  project,
  job,
  onSave,
  onDelete
}) => {
  const [title, setTitle] = useState(job.title || '');
  const [description, setDescription] = useState(job.description || '');
  const [gitBranch, setGitBranch] = useState(job.gitBranchName || '');
  const [priority, setPriority] = useState<number>(job.priority ?? 1);
  const [status, setStatus] = useState<ProjectJobStatus>(job.status ?? ProjectJobStatus.Active);
  const [resultSummary, setResultSummary] = useState(job.resultSummary || '');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (isOpen) {
      setTitle(job.title || '');
      setDescription(job.description || '');
      setGitBranch(job.gitBranchName || '');
      setPriority(job.priority ?? 1);
      setStatus(job.status ?? ProjectJobStatus.Active);
      setResultSummary(job.resultSummary || '');
      setConfirmDelete(false);
      setError(null);
    }
  }, [isOpen, job]);

  if (!isOpen) return null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim() || isSubmitting) {
      if (!title.trim()) setError('Job Title is required.');
      return;
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
        resultSummary: resultSummary.trim() || undefined
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
      <div className="bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl w-full max-w-lg overflow-hidden shadow-2xl my-0 sm:my-8 max-h-[90dvh] flex flex-col">
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
            <div className="p-3 bg-red-500/10 border border-red-500/30 rounded-xl flex items-center gap-2 text-xs text-red-300">
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
                <option value={ProjectJobStatus.Active}>Active</option>
                <option value={ProjectJobStatus.Completed}>Completed</option>
                <option value={ProjectJobStatus.Archived}>Archived</option>
              </select>
            </div>
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
          <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-800">
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
    </div>
  );
};
