import React, { useState, useEffect } from 'react';
import { X, FolderGit2, Check, AlertCircle } from 'lucide-react';
import type { Persona, Project } from '../types';
import { WorkspaceType } from '../types';

interface ProjectModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSave: (req: {
    name: string;
    id?: string;
    description?: string;
    workspaceType: number;
    gitRepositoryUrl?: string;
    gitBaseBranch?: string;
    s3BucketName?: string;
    s3PrefixKey?: string;
    projectInstructions?: string;
    defaultLeadPersonaId?: string;
    allowedPersonaIds?: string[];
  }) => Promise<void>;
  editingProject?: Project | null;
  availablePersonas: Persona[];
}

export const ProjectModal: React.FC<ProjectModalProps> = ({
  isOpen,
  onClose,
  onSave,
  editingProject,
  availablePersonas
}) => {
  const [name, setName] = useState('');
  const [id, setId] = useState('');
  const [description, setDescription] = useState('');
  const [workspaceType, setWorkspaceType] = useState<number>(WorkspaceType.Git);
  const [gitRepoUrl, setGitRepoUrl] = useState('');
  const [gitBaseBranch, setGitBaseBranch] = useState('main');
  const [s3Bucket, setS3Bucket] = useState('agent-mesh-artifacts');
  const [s3Prefix, setS3Prefix] = useState('');
  const [instructions, setInstructions] = useState('');
  const [defaultLeadId, setDefaultLeadId] = useState('');
  const [selectedPersonaIds, setSelectedPersonaIds] = useState<string[]>([]);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (editingProject) {
      setName(editingProject.name);
      setId(editingProject.id);
      setDescription(editingProject.description || '');
      setWorkspaceType(typeof editingProject.workspaceType === 'number' ? editingProject.workspaceType : WorkspaceType.Git);
      setGitRepoUrl(editingProject.gitRepositoryUrl || '');
      setGitBaseBranch(editingProject.gitBaseBranch || 'main');
      setS3Bucket(editingProject.s3BucketName || 'agent-mesh-artifacts');
      setS3Prefix(editingProject.s3PrefixKey || '');
      setInstructions(editingProject.projectInstructions || '');
      setDefaultLeadId(editingProject.defaultLeadPersonaId || '');
      setSelectedPersonaIds(editingProject.assignedPersonas?.map((p) => p.id) || []);
    } else {
      setName('');
      setId('');
      setDescription('');
      setWorkspaceType(WorkspaceType.Git);
      setGitRepoUrl('');
      setGitBaseBranch('main');
      setS3Bucket('agent-mesh-artifacts');
      setS3Prefix('');
      setInstructions('');
      setDefaultLeadId(availablePersonas[0]?.id || '');
      setSelectedPersonaIds(availablePersonas.map((p) => p.id));
    }
    setError(null);
  }, [editingProject, isOpen, availablePersonas]);

  if (!isOpen) return null;

  const togglePersona = (pId: string) => {
    setSelectedPersonaIds((prev) => {
      const next = prev.includes(pId) ? prev.filter((x) => x !== pId) : [...prev, pId];
      if (!next.includes(defaultLeadId)) {
        setDefaultLeadId(next[0] || '');
      }
      return next;
    });
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) {
      setError('Project Name is required.');
      return;
    }
    setIsSaving(true);
    setError(null);
    try {
      await onSave({
        name: name.trim(),
        id: id.trim() || undefined,
        description: description.trim() || undefined,
        workspaceType,
        gitRepositoryUrl: workspaceType === WorkspaceType.Git ? gitRepoUrl.trim() || undefined : undefined,
        gitBaseBranch: workspaceType === WorkspaceType.Git ? gitBaseBranch.trim() || 'main' : undefined,
        s3BucketName: workspaceType === WorkspaceType.S3Archive ? s3Bucket.trim() || undefined : undefined,
        s3PrefixKey: workspaceType === WorkspaceType.S3Archive ? s3Prefix.trim() || undefined : undefined,
        projectInstructions: instructions.trim() || undefined,
        defaultLeadPersonaId: defaultLeadId.trim() || undefined,
        allowedPersonaIds: selectedPersonaIds
      });
      onClose();
    } catch (err: any) {
      setError(err?.message || 'Failed to save project.');
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/80 backdrop-blur-sm p-4 overflow-y-auto">
      <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-2xl overflow-hidden shadow-2xl my-8">
        <div className="flex items-center justify-between px-6 py-4 border-b border-slate-800 bg-slate-950/50">
          <div className="flex items-center gap-2.5">
            <div className="w-8 h-8 rounded-lg bg-sky-500/20 text-sky-400 flex items-center justify-center border border-sky-500/30">
              <FolderGit2 className="w-4 h-4" />
            </div>
            <div>
              <h2 className="text-base font-semibold text-slate-100">
                {editingProject ? 'Edit Project' : 'Create New Project'}
              </h2>
              <p className="text-xs text-slate-400">
                Configure codebase workspace, assigned personas, and project guidelines
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

        <form onSubmit={handleSubmit} className="p-6 space-y-5 max-h-[75vh] overflow-y-auto">
          {error && (
            <div className="p-3 bg-red-500/10 border border-red-500/30 rounded-xl flex items-center gap-2 text-xs text-red-300">
              <AlertCircle className="w-4 h-4 shrink-0 text-red-400" />
              <span>{error}</span>
            </div>
          )}

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5">
                Project Name <span className="text-red-400">*</span>
              </label>
              <input
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. AgentMesh Core"
                required
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-sm text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition"
              />
            </div>

            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5">
                Slug / ID <span className="text-slate-500 font-normal">(optional override)</span>
              </label>
              <input
                type="text"
                value={id}
                onChange={(e) => setId(e.target.value)}
                disabled={!!editingProject}
                placeholder="e.g. agent-mesh"
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-sm text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition disabled:opacity-50"
              />
            </div>
          </div>

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-1.5">
              Description
            </label>
            <input
              type="text"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="e.g. Multi-agent distributed orchestration platform"
              className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-sm text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition"
            />
          </div>

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-2">
              Workspace Type
            </label>
            <div className="grid grid-cols-2 gap-3">
              <button
                type="button"
                onClick={() => setWorkspaceType(WorkspaceType.Git)}
                className={`p-3 rounded-xl border text-left flex items-start gap-3 transition cursor-pointer ${
                  workspaceType === WorkspaceType.Git
                    ? 'bg-sky-500/10 border-sky-500/40 text-sky-200'
                    : 'bg-slate-950 border-slate-800 text-slate-400 hover:border-slate-700'
                }`}
              >
                <div className="mt-0.5 w-4 h-4 rounded-full border flex items-center justify-center shrink-0 border-current">
                  {workspaceType === WorkspaceType.Git && <div className="w-2 h-2 rounded-full bg-current" />}
                </div>
                <div>
                  <div className="text-xs font-semibold text-slate-200">Git Repository</div>
                  <div className="text-[11px] text-slate-400 mt-0.5">
                    Branch per Job (<code className="font-mono">mesh/job-123</code>), persona git commits
                  </div>
                </div>
              </button>

              <button
                type="button"
                onClick={() => setWorkspaceType(WorkspaceType.S3Archive)}
                className={`p-3 rounded-xl border text-left flex items-start gap-3 transition cursor-pointer ${
                  workspaceType === WorkspaceType.S3Archive
                    ? 'bg-sky-500/10 border-sky-500/40 text-sky-200'
                    : 'bg-slate-950 border-slate-800 text-slate-400 hover:border-slate-700'
                }`}
              >
                <div className="mt-0.5 w-4 h-4 rounded-full border flex items-center justify-center shrink-0 border-current">
                  {workspaceType === WorkspaceType.S3Archive && <div className="w-2 h-2 rounded-full bg-current" />}
                </div>
                <div>
                  <div className="text-xs font-semibold text-slate-200">S3 Archive (Non-Git)</div>
                  <div className="text-[11px] text-slate-400 mt-0.5">
                    Zip/tarball snapshot syncing with smart exclusions
                  </div>
                </div>
              </button>
            </div>
          </div>

          {workspaceType === WorkspaceType.Git ? (
            <div className="grid grid-cols-1 md:grid-cols-3 gap-3 p-3.5 bg-slate-950/60 border border-slate-800/80 rounded-xl">
              <div className="md:col-span-2">
                <label className="block text-[11px] font-semibold text-slate-400 mb-1">
                  Git Repository URL
                </label>
                <input
                  type="text"
                  value={gitRepoUrl}
                  onChange={(e) => setGitRepoUrl(e.target.value)}
                  placeholder="https://github.com/org/repo.git"
                  className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-1.5 text-xs text-slate-100 placeholder-slate-600 focus:outline-none focus:border-sky-500 font-mono"
                />
              </div>
              <div>
                <label className="block text-[11px] font-semibold text-slate-400 mb-1">
                  Base Branch
                </label>
                <input
                  type="text"
                  value={gitBaseBranch}
                  onChange={(e) => setGitBaseBranch(e.target.value)}
                  placeholder="main"
                  className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-1.5 text-xs text-slate-100 placeholder-slate-600 focus:outline-none focus:border-sky-500 font-mono"
                />
              </div>
            </div>
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3 p-3.5 bg-slate-950/60 border border-slate-800/80 rounded-xl">
              <div>
                <label className="block text-[11px] font-semibold text-slate-400 mb-1">
                  S3 Bucket Name
                </label>
                <input
                  type="text"
                  value={s3Bucket}
                  onChange={(e) => setS3Bucket(e.target.value)}
                  placeholder="agent-mesh-artifacts"
                  className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-1.5 text-xs text-slate-100 placeholder-slate-600 focus:outline-none focus:border-sky-500 font-mono"
                />
              </div>
              <div>
                <label className="block text-[11px] font-semibold text-slate-400 mb-1">
                  S3 Prefix Key
                </label>
                <input
                  type="text"
                  value={s3Prefix}
                  onChange={(e) => setS3Prefix(e.target.value)}
                  placeholder="projects/my-app"
                  className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-1.5 text-xs text-slate-100 placeholder-slate-600 focus:outline-none focus:border-sky-500 font-mono"
                />
              </div>
            </div>
          )}

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-2">
              Allowed Personas
            </label>
            <div className="flex flex-wrap gap-2 p-3 bg-slate-950 border border-slate-800 rounded-xl max-h-36 overflow-y-auto">
              {availablePersonas.map((p) => {
                const isSelected = selectedPersonaIds.includes(p.id);
                return (
                  <button
                    key={p.id}
                    type="button"
                    onClick={() => togglePersona(p.id)}
                    className={`px-3 py-1.5 rounded-lg text-xs font-medium flex items-center gap-1.5 border transition cursor-pointer ${
                      isSelected
                        ? 'bg-sky-500/20 text-sky-200 border-sky-500/50 shadow-sm'
                        : 'bg-slate-900 text-slate-400 border-slate-800 hover:text-slate-300'
                    }`}
                  >
                    <div className={`w-3.5 h-3.5 rounded flex items-center justify-center border ${
                      isSelected ? 'bg-sky-500 border-sky-400 text-slate-950' : 'border-slate-700'
                    }`}>
                      {isSelected && <Check className="w-2.5 h-2.5 stroke-[3]" />}
                    </div>
                    <span>{p.name}</span>
                    <span className="text-[10px] text-slate-500">(@{p.driverId})</span>
                  </button>
                );
              })}
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-xs font-semibold text-slate-300 mb-1.5">
                Default Lead Persona
              </label>
              <select
                value={defaultLeadId}
                onChange={(e) => setDefaultLeadId(e.target.value)}
                className="w-full bg-slate-950 border border-slate-800 rounded-xl px-3.5 py-2 text-sm text-slate-100 focus:outline-none focus:border-sky-500 transition"
              >
                <option value="">None (Prompt user when untagged)</option>
                {availablePersonas
                  .filter((p) => selectedPersonaIds.includes(p.id))
                  .map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name} ({p.role})
                    </option>
                  ))}
              </select>
            </div>
          </div>

          <div>
            <label className="block text-xs font-semibold text-slate-300 mb-1.5">
              Project Guidelines & Shared Instructions
            </label>
            <textarea
              rows={4}
              value={instructions}
              onChange={(e) => setInstructions(e.target.value)}
              placeholder="e.g. Always write xUnit tests for new controllers. Follow standard clean architecture."
              className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition font-mono leading-relaxed"
            />
          </div>

          <div className="flex items-center justify-end gap-3 pt-4 border-t border-slate-800">
            <button
              type="button"
              onClick={onClose}
              disabled={isSaving}
              className="px-4 py-2 rounded-xl text-xs font-semibold text-slate-400 hover:text-slate-200 hover:bg-slate-800 transition"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSaving}
              className="px-5 py-2 bg-sky-500 hover:bg-sky-400 disabled:opacity-50 text-slate-950 text-xs font-bold rounded-xl shadow-md transition flex items-center gap-1.5"
            >
              {isSaving ? 'Saving...' : editingProject ? 'Update Project' : 'Create Project'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
