import React, { useState } from 'react';
import {
  FolderGit2,
  Plus,
  ChevronDown,
  ChevronRight,
  Trash2,
  CheckCircle2,
  Clock
} from 'lucide-react';
import type { Project, ProjectJob } from '../types';
import { ProjectJobStatus } from '../types';

interface ProjectListProps {
  projects: Project[];
  selectedProjectId: string | null;
  selectedJobId: string | null;
  jobsByProject: Record<string, ProjectJob[]>;
  onSelectProject: (projectId: string) => void;
  onSelectJob: (projectId: string, jobId: string) => void;
  onOpenNewProjectModal: () => void;
  onOpenNewJobModal: (project: Project) => void;
  onDeleteProject: (projectId: string) => Promise<void>;
  onDeleteJob: (jobId: string) => Promise<void>;
}

export const ProjectList: React.FC<ProjectListProps> = ({
  projects,
  selectedProjectId,
  selectedJobId,
  jobsByProject,
  onSelectProject,
  onSelectJob,
  onOpenNewProjectModal,
  onOpenNewJobModal,
  onDeleteProject,
  onDeleteJob
}) => {
  const [expandedProjectIds, setExpandedProjectIds] = useState<string[]>([]);
  const [confirmDeleteProjectId, setConfirmDeleteProjectId] = useState<string | null>(null);
  const [confirmDeleteJobId, setConfirmDeleteJobId] = useState<string | null>(null);

  const toggleExpand = (pId: string, e: React.MouseEvent) => {
    e.stopPropagation();
    setExpandedProjectIds((prev) =>
      prev.includes(pId) ? prev.filter((id) => id !== pId) : [...prev, pId]
    );
  };

  return (
    <div className="flex flex-col h-full">
      {/* Header */}
      <div className="p-3 border-b border-slate-800/80 flex items-center justify-between">
        <span className="text-xs font-bold uppercase tracking-wider text-slate-400">
          Projects & Teams
        </span>
        <button
          type="button"
          onClick={onOpenNewProjectModal}
          className="px-2.5 py-1 bg-sky-500/15 hover:bg-sky-500/25 border border-sky-500/30 text-sky-300 text-xs font-semibold rounded-lg transition flex items-center gap-1 cursor-pointer"
        >
          <Plus className="w-3.5 h-3.5" />
          <span>New Project</span>
        </button>
      </div>

      {/* Projects List */}
      <div className="flex-1 overflow-y-auto p-2 space-y-1.5">
        {projects.length === 0 ? (
          <div className="p-4 text-center text-slate-500 text-xs">
            No projects yet. Create your first project to collaborate with Personas.
          </div>
        ) : (
          projects.map((project) => {
            const isSelected = selectedProjectId === project.id;
            const isExpanded = expandedProjectIds.includes(project.id) || isSelected;
            const jobs = jobsByProject[project.id] || [];

            return (
              <div
                key={project.id}
                className={`rounded-xl border transition overflow-hidden ${
                  isSelected
                    ? 'bg-slate-900/90 border-sky-500/40 shadow-sm'
                    : 'bg-slate-950/40 border-slate-800/80 hover:border-slate-700/80'
                }`}
              >
                {/* Project Row */}
                <div
                  onClick={() => {
                    onSelectProject(project.id);
                    if (!expandedProjectIds.includes(project.id)) {
                      setExpandedProjectIds((prev) => [...prev, project.id]);
                    }
                  }}
                  className="p-2.5 flex items-center justify-between gap-2 cursor-pointer group"
                >
                  <div className="flex items-center gap-2 min-w-0">
                    <button
                      type="button"
                      onClick={(e) => toggleExpand(project.id, e)}
                      className="text-slate-500 hover:text-slate-300 transition p-0.5"
                    >
                      {isExpanded ? (
                        <ChevronDown className="w-3.5 h-3.5" />
                      ) : (
                        <ChevronRight className="w-3.5 h-3.5" />
                      )}
                    </button>
                    <FolderGit2 className="w-4 h-4 text-sky-400 shrink-0" />
                    <div className="min-w-0">
                      <div className="flex items-center gap-1.5">
                        <span className="text-xs font-semibold text-slate-200 truncate">
                          {project.name}
                        </span>
                        <span className="text-[10px] font-mono px-1.5 py-0.2 rounded bg-slate-800 text-slate-400">
                          {project.workspaceType === 0 ? 'Git' : 'S3'}
                        </span>
                      </div>
                    </div>
                  </div>

                  <div className="flex items-center gap-1 shrink-0">
                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation();
                        onOpenNewJobModal(project);
                      }}
                      title="Create new Job in this project"
                      className="p-1 rounded-md text-slate-400 hover:text-sky-300 hover:bg-slate-800 transition"
                    >
                      <Plus className="w-3.5 h-3.5" />
                    </button>

                    <button
                      type="button"
                      onClick={(e) => {
                        e.stopPropagation();
                        setConfirmDeleteProjectId(project.id);
                      }}
                      className="p-1 rounded-md text-slate-500 hover:text-red-400 hover:bg-slate-800 transition opacity-0 group-hover:opacity-100"
                    >
                      <Trash2 className="w-3.5 h-3.5" />
                    </button>
                  </div>
                </div>

                {/* Confirm Delete Project Banner */}
                {confirmDeleteProjectId === project.id && (
                  <div className="p-2 bg-red-950/80 border-t border-red-500/30 flex items-center justify-between text-xs text-red-200">
                    <span>Delete project & all jobs?</span>
                    <div className="flex items-center gap-1">
                      <button
                        type="button"
                        onClick={() => setConfirmDeleteProjectId(null)}
                        className="px-2 py-0.5 text-[11px] text-slate-300 hover:text-white"
                      >
                        Cancel
                      </button>
                      <button
                        type="button"
                        onClick={async () => {
                          await onDeleteProject(project.id);
                          setConfirmDeleteProjectId(null);
                        }}
                        className="px-2 py-0.5 text-[11px] bg-red-600 hover:bg-red-500 text-white font-bold rounded"
                      >
                        Delete
                      </button>
                    </div>
                  </div>
                )}

                {/* Jobs Sub-List */}
                {isExpanded && (
                  <div className="border-t border-slate-800/60 bg-slate-950/50 p-1.5 space-y-1">
                    {jobs.length === 0 ? (
                      <div className="p-2 text-center text-slate-600 text-[11px]">
                        No jobs yet. Click <span className="text-sky-400 font-semibold">+</span> to start a job.
                      </div>
                    ) : (
                      jobs.map((job) => {
                        const isJobSelected = selectedJobId === job.id;
                        const isJobDone = job.status === ProjectJobStatus.Completed;

                        return (
                          <div key={job.id}>
                            <div
                              onClick={() => onSelectJob(project.id, job.id)}
                              className={`p-2 rounded-lg flex items-center justify-between gap-2 text-xs cursor-pointer group/job transition ${
                                isJobSelected
                                  ? 'bg-sky-500/20 text-sky-200 border border-sky-500/40'
                                  : 'text-slate-300 hover:bg-slate-900 border border-transparent'
                              }`}
                            >
                              <div className="flex items-center gap-2 min-w-0">
                                {isJobDone ? (
                                  <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400 shrink-0" />
                                ) : (
                                  <Clock className="w-3.5 h-3.5 text-sky-400 shrink-0" />
                                )}
                                <span className="truncate font-medium">{job.title}</span>
                              </div>

                              <div className="flex items-center gap-1 shrink-0">
                                {job.gitBranchName && (
                                  <span className="text-[10px] font-mono text-slate-500 hidden sm:inline">
                                    {job.gitBranchName.replace('mesh/', '')}
                                  </span>
                                )}
                                <button
                                  type="button"
                                  onClick={(e) => {
                                    e.stopPropagation();
                                    setConfirmDeleteJobId(job.id);
                                  }}
                                  className="p-0.5 text-slate-500 hover:text-red-400 opacity-0 group-hover/job:opacity-100 transition"
                                >
                                  <Trash2 className="w-3 h-3" />
                                </button>
                              </div>
                            </div>

                            {confirmDeleteJobId === job.id && (
                              <div className="p-1.5 my-1 bg-red-950/80 border border-red-500/30 rounded flex items-center justify-between text-[11px] text-red-200">
                                <span>Delete job?</span>
                                <div className="flex items-center gap-1">
                                  <button
                                    type="button"
                                    onClick={() => setConfirmDeleteJobId(null)}
                                    className="px-1.5 py-0.5 text-slate-300 hover:text-white"
                                  >
                                    Cancel
                                  </button>
                                  <button
                                    type="button"
                                    onClick={async () => {
                                      await onDeleteJob(job.id);
                                      setConfirmDeleteJobId(null);
                                    }}
                                    className="px-2 py-0.5 bg-red-600 hover:bg-red-500 text-white font-bold rounded"
                                  >
                                    Delete
                                  </button>
                                </div>
                              </div>
                            )}
                          </div>
                        );
                      })
                    )}
                  </div>
                )}
              </div>
            );
          })
        )}
      </div>
    </div>
  );
};
