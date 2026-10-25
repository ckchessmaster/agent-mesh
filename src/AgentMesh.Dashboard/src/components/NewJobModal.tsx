import React, { useState } from 'react';
import { X, Briefcase, Plus, AlertCircle } from 'lucide-react';
import type { Project } from '../types';

interface NewJobModalProps {
  isOpen: boolean;
  onClose: () => void;
  project: Project;
  onCreate: (req: {
    title: string;
    description?: string;
    gitBranchName?: string;
    initialTasks?: string[];
    priority?: number;
  }) => Promise<void>;
}

export const NewJobModal: React.FC<NewJobModalProps> = ({
  isOpen,
  onClose,
  project,
  onCreate
}) => {
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [gitBranch, setGitBranch] = useState('');
  const [priority, setPriority] = useState<number>(1);
  const [tasksText, setTasksText] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

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
      const initialTasks = tasksText
        .split('\n')
        .map((t) => t.trim())
        .filter((t) => t.length > 0);

      await onCreate({
        title: title.trim(),
        description: description.trim() || undefined,
        gitBranchName: gitBranch.trim() || undefined,
        initialTasks: initialTasks.length > 0 ? initialTasks : undefined,
        priority
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
      <div className="bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl w-full max-w-lg overflow-hidden shadow-2xl my-0 sm:my-8 max-h-[90dvh] flex flex-col">
        <div className="flex items-center justify-between px-6 py-4 border-b border-slate-800 bg-slate-950/50">
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

        <form onSubmit={handleSubmit} className="p-6 space-y-4">
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
              rows={2}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Brief summary of feature scope or bug to resolve..."
              className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition"
            />
          </div>

          {project.workspaceType === 0 && (
            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5">
                Git Branch Name <span className="text-slate-500 font-normal">(auto-generated if empty)</span>
              </label>
              <input
                type="text"
                value={gitBranch}
                onChange={(e) => setGitBranch(e.target.value)}
                placeholder="e.g. mesh/job-auth-oauth2"
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition font-mono"
              />
            </div>
          )}

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
            <label className="block text-xs font-semibold text-slate-300 mb-1.5">
              Initial Tasks / Checklist <span className="text-slate-500 font-normal">(one per line)</span>
            </label>
            <textarea
              rows={3}
              value={tasksText}
              onChange={(e) => setTasksText(e.target.value)}
              placeholder={"Scaffold controller endpoints\nWrite unit test coverage\nRun security & regression check"}
              className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition font-mono leading-relaxed"
            />
          </div>

          <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-800">
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
              className="px-5 py-2 bg-sky-500 hover:bg-sky-400 disabled:opacity-50 text-slate-950 text-xs font-bold rounded-xl shadow-md transition flex items-center gap-1.5"
            >
              <Plus className="w-4 h-4" />
              <span>{isSubmitting ? 'Creating...' : 'Start Job'}</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
