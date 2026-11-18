import React, { useState, useEffect } from 'react';
import { X, FolderGit2, Check, AlertCircle, FolderCheck, Loader2, FolderCog } from 'lucide-react';
import type {
  CreateProjectRequest,
  Persona,
  Project,
  ProjectWorkspaceSetupResult
} from '../types';
import { WorkspaceType } from '../types';
import { api } from '../services/api';
import { ProjectDocumentsSection } from './ProjectDocumentsSection';

interface ProjectModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSave: (req: CreateProjectRequest) => Promise<void>;
  onSetupWorkspace?: (projectId: string) => Promise<ProjectWorkspaceSetupResult>;
  editingProject?: Project | null;
  availablePersonas: Persona[];
}

const MAX_REPOSITORY_NAME_LENGTH = 128;
const MAX_WORKING_DIRECTORY_LENGTH = 256;

const normalizeSlug = (input: string): string =>
  input
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9\-_]+/g, '-')
    .replace(/^-+|-+$/g, '');

const isSafeRepositoryName = (value: string): boolean => {
  if (!value || value.length > MAX_REPOSITORY_NAME_LENGTH) return false;
  if (value === '.' || value === '..') return false;
  if (!/^[A-Za-z0-9._-]+$/.test(value)) return false;
  return value.replace(/\./g, '') !== '';
};

const isSafeWorkingDirectory = (value: string): boolean => {
  if (!value) return true;
  if (value.length > MAX_WORKING_DIRECTORY_LENGTH) return false;
  const hasPosixRoot = value.startsWith('/');
  const hasWindowsRoot = /^[A-Za-z]:[/\\]/.test(value);
  if (!hasPosixRoot && !hasWindowsRoot) return false;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 32 || code === 127) return false;
  }
  return true;
};

const normalizeWorkspacePath = (path: string): string => {
  let normalized = path.trim();
  if (!normalized) return normalized;
  if (normalized.startsWith('/')) {
    normalized = normalized.replace(/\\/g, '/');
  }
  while (normalized.length > 1 && normalized.endsWith('/')) {
    normalized = normalized.slice(0, -1);
  }
  return normalized;
};

export const ProjectModal: React.FC<ProjectModalProps> = ({
  isOpen,
  onClose,
  onSave,
  onSetupWorkspace,
  editingProject,
  availablePersonas
}) => {
  const [name, setName] = useState('');
  const [id, setId] = useState('');
  const [description, setDescription] = useState('');
  const [workspaceType, setWorkspaceType] = useState<number>(WorkspaceType.Git);
  const [gitRepoUrl, setGitRepoUrl] = useState('');
  const [gitBaseBranch, setGitBaseBranch] = useState('main');
  const [repositoryName, setRepositoryName] = useState('');
  const [workingDirectory, setWorkingDirectory] = useState('');
  const [s3Bucket, setS3Bucket] = useState('agent-mesh-artifacts');
  const [s3Prefix, setS3Prefix] = useState('');
  const [instructions, setInstructions] = useState('');
  const [defaultLeadId, setDefaultLeadId] = useState('');
  const [selectedPersonaIds, setSelectedPersonaIds] = useState<string[]>([]);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isSettingUpWorkspace, setIsSettingUpWorkspace] = useState(false);
  const [setupResult, setSetupResult] = useState<ProjectWorkspaceSetupResult | null>(null);
  const [setupError, setSetupError] = useState<string | null>(null);

  useEffect(() => {
    if (editingProject) {
      setName(editingProject.name);
      setId(editingProject.id);
      setDescription(editingProject.description || '');
      setWorkspaceType(typeof editingProject.workspaceType === 'number' ? editingProject.workspaceType : WorkspaceType.Git);
      setGitRepoUrl(editingProject.gitRepositoryUrl || '');
      setGitBaseBranch(editingProject.gitBaseBranch || 'main');
      setRepositoryName(editingProject.repositoryName || '');
      setWorkingDirectory(editingProject.workingDirectory || '');
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
      setRepositoryName('');
      setWorkingDirectory('');
      setS3Bucket('agent-mesh-artifacts');
      setS3Prefix('');
      setInstructions('');
      setDefaultLeadId(availablePersonas[0]?.id || '');
      setSelectedPersonaIds(availablePersonas.map((p) => p.id));
    }
    setError(null);
    setSetupResult(null);
    setSetupError(null);
  }, [editingProject, isOpen, availablePersonas]);

  if (!isOpen) return null;

  const trimmedRepoName = repositoryName.trim();
  const trimmedWorkingDirectory = workingDirectory.trim();
  const effectiveRepoName =
    trimmedRepoName ||
    editingProject?.repositoryName ||
    normalizeSlug(id) ||
    normalizeSlug(name) ||
    'agent-mesh';
  const resolvedWorkingDirectoryPreview = trimmedWorkingDirectory
    ? normalizeWorkspacePath(trimmedWorkingDirectory)
    : editingProject?.resolvedWorkingDirectory &&
        trimmedRepoName === (editingProject.repositoryName || '') &&
        !(editingProject.workingDirectory || '')
      ? editingProject.resolvedWorkingDirectory
      : `/workspace/repos/${effectiveRepoName}`;

  const hasUnsavedWorkspaceChanges = Boolean(
    editingProject &&
      (trimmedRepoName !== (editingProject.repositoryName || '') ||
        trimmedWorkingDirectory !== (editingProject.workingDirectory || '') ||
        workspaceType !==
          (typeof editingProject.workspaceType === 'number'
            ? editingProject.workspaceType
            : WorkspaceType.Git) ||
        gitRepoUrl.trim() !== (editingProject.gitRepositoryUrl || '') ||
        (gitBaseBranch.trim() || 'main') !== (editingProject.gitBaseBranch || 'main'))
  );

  const togglePersona = (pId: string) => {
    setSelectedPersonaIds((prev) => {
      const next = prev.includes(pId) ? prev.filter((x) => x !== pId) : [...prev, pId];
      if (!next.includes(defaultLeadId)) {
        setDefaultLeadId(next[0] || '');
      }
      return next;
    });
  };

  const handleSetupWorkspace = async () => {
    if (!editingProject || isSettingUpWorkspace || isSaving) return;
    setIsSettingUpWorkspace(true);
    setSetupError(null);
    setSetupResult(null);
    try {
      const runSetup = onSetupWorkspace ?? ((projectId: string) => api.setupProjectWorkspace(projectId));
      const result = await runSetup(editingProject.id);
      setSetupResult(result);
    } catch (err: any) {
      setSetupError(err?.message || 'Failed to setup project workspace.');
    } finally {
      setIsSettingUpWorkspace(false);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isSaving) return;
    if (!name.trim()) {
      setError('Project Name is required.');
      return;
    }
    if (trimmedRepoName && !isSafeRepositoryName(trimmedRepoName)) {
      setError('Repository Name must be 1-128 characters using only ASCII letters, digits, ".", "-", or "_".');
      return;
    }
    if (trimmedWorkingDirectory && !isSafeWorkingDirectory(trimmedWorkingDirectory)) {
      setError('Working Directory must be an absolute path up to 256 characters with no control characters.');
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
        repositoryName: trimmedRepoName || undefined,
        workingDirectory: editingProject ? trimmedWorkingDirectory : trimmedWorkingDirectory || undefined,
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
    <div className="fixed inset-0 z-50 flex items-end sm:items-center justify-center bg-slate-950/80 backdrop-blur-sm p-0 sm:p-4 overflow-y-auto">
      <div className="bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl w-full max-w-2xl overflow-hidden shadow-2xl my-0 sm:my-8 max-h-[90dvh] flex flex-col">
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

          <div className="p-3.5 bg-slate-950/60 border border-slate-800/80 rounded-xl space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                <FolderCog className="w-3.5 h-3.5 text-sky-400" />
                <span className="text-xs font-semibold text-slate-300">
                  Workspace Directory & Setup
                </span>
              </div>
              {editingProject ? (
                <button
                  type="button"
                  onClick={handleSetupWorkspace}
                  disabled={isSettingUpWorkspace || isSaving}
                  data-testid="setup-project-workspace-btn"
                  className="px-3 py-1.5 rounded-lg text-[11px] font-semibold bg-sky-500/15 hover:bg-sky-500/25 border border-sky-500/30 text-sky-300 disabled:opacity-50 transition flex items-center gap-1.5 cursor-pointer"
                >
                  {isSettingUpWorkspace ? (
                    <>
                      <Loader2 className="w-3.5 h-3.5 animate-spin" />
                      <span>Setting up...</span>
                    </>
                  ) : (
                    <>
                      <FolderCheck className="w-3.5 h-3.5" />
                      <span>Setup Workspace</span>
                    </>
                  )}
                </button>
              ) : (
                <span className="text-[11px] text-slate-500">
                  Save this project first to run workspace setup on disk.
                </span>
              )}
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              <div>
                <label
                  htmlFor="project-repository-name"
                  className="block text-[11px] font-semibold text-slate-400 mb-1"
                >
                  Repository Name <span className="text-slate-500 font-normal">(optional)</span>
                </label>
                <input
                  id="project-repository-name"
                  type="text"
                  value={repositoryName}
                  onChange={(e) => setRepositoryName(e.target.value)}
                  placeholder="e.g. agent-mesh (defaults to slug)"
                  data-testid="project-repository-name-input"
                  className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-1.5 text-xs text-slate-100 placeholder-slate-600 focus:outline-none focus:border-sky-500 font-mono"
                />
              </div>

              <div>
                <label
                  htmlFor="project-working-directory"
                  className="block text-[11px] font-semibold text-slate-400 mb-1"
                >
                  Working Directory <span className="text-slate-500 font-normal">(optional override)</span>
                </label>
                <input
                  id="project-working-directory"
                  type="text"
                  value={workingDirectory}
                  onChange={(e) => setWorkingDirectory(e.target.value)}
                  placeholder="/workspace/repos/..."
                  data-testid="project-working-directory-input"
                  className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-1.5 text-xs text-slate-100 placeholder-slate-600 focus:outline-none focus:border-sky-500 font-mono"
                />
              </div>
            </div>

            <div
              className="text-[11px] text-slate-400 flex flex-wrap items-center gap-1.5"
              data-testid="project-resolved-working-directory"
            >
              <span>Effective working directory:</span>
              <code className="px-1.5 py-0.5 rounded bg-slate-900 border border-slate-800 text-slate-200 font-mono">
                {resolvedWorkingDirectoryPreview}
              </code>
            </div>

            {hasUnsavedWorkspaceChanges && (
              <div
                className="text-[11px] text-amber-300/90 bg-amber-500/10 border border-amber-500/20 rounded-lg px-2.5 py-1.5"
                data-testid="project-workspace-dirty-hint"
              >
                Workspace settings have unsaved changes. Click Update Project before running setup to use the updated values.
              </div>
            )}

            {setupError && (
              <div
                className="p-2.5 bg-red-500/10 border border-red-500/30 rounded-lg flex items-start gap-2 text-xs text-red-300"
                data-testid="project-workspace-setup-result"
              >
                <AlertCircle className="w-4 h-4 shrink-0 text-red-400 mt-0.5" />
                <span>{setupError}</span>
              </div>
            )}

            {setupResult && (
              <div
                className={`p-2.5 rounded-lg border text-xs space-y-1 ${
                  !setupResult.success
                    ? 'bg-red-500/10 border-red-500/30 text-red-200'
                    : setupResult.usedFallback
                      ? 'bg-amber-500/10 border-amber-500/30 text-amber-200'
                      : 'bg-emerald-500/10 border-emerald-500/30 text-emerald-200'
                }`}
                data-testid="project-workspace-setup-result"
              >
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-semibold">{setupResult.outcome}</span>
                  <span className="text-[11px] opacity-90">{setupResult.message}</span>
                </div>
                {setupResult.effectiveDirectory && (
                  <div className="text-[11px] opacity-90">
                    Effective directory:{' '}
                    <code className="font-mono">{setupResult.effectiveDirectory}</code>
                  </div>
                )}
                {!setupResult.success && setupResult.targetDirectory && (
                  <div className="text-[11px] opacity-90">
                    Target directory:{' '}
                    <code className="font-mono">{setupResult.targetDirectory}</code>
                  </div>
                )}
                {setupResult.warnings.length > 0 && (
                  <ul className="list-disc list-inside text-[11px] opacity-90 space-y-0.5">
                    {setupResult.warnings.map((w, idx) => (
                      <li key={idx}>{w}</li>
                    ))}
                  </ul>
                )}
              </div>
            )}
          </div>

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

          {/*
            The document library is rendered only for an existing project. A new project has no id yet,
            and its documents are addressed by project id in both the storage key and the download URL, so
            there is nowhere to put a file before the row exists. The section says so rather than showing a
            disabled control, because "Save this project first to attach documents" is an instruction, while
            a greyed-out upload is a mystery.

            It lives inside the <form> because that is where the project's own fields are, and every control
            in it is type="button" so adding a document cannot submit (and close) the project form.
          */}
          <ProjectDocumentsSection projectId={editingProject?.id ?? null} />

          <div className="flex items-center justify-end gap-3 pt-4 border-t border-slate-800 pb-[max(0.5rem,env(safe-area-inset-bottom))]">
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
