import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertOctagon,
  AlertTriangle,
  ArrowRight,
  BarChart2,
  Bot,
  CheckCircle2,
  Clock,
  Cpu,
  FolderGit2,
  GitBranch,
  Layers,
  MessageSquare,
  Plus,
  RefreshCw,
  Server,
  ShieldAlert,
  Sparkles,
  Users,
  Zap,
} from 'lucide-react';
import { api } from '../services/api';
import {
  QUOTA_DOT_CLASS,
  QUOTA_TEXT_CLASS,
  describeReset,
  relativeTime,
  summarizeQuota,
  type QuotaTone,
} from '../utils/driverUsage';
import type {
  Agent,
  AgentSession,
  AgentUsageResponse,
  DashboardAttentionItem,
  DashboardJobStats,
  DashboardOverviewResponse,
  Persona,
  Project,
  ProjectJob,
  ServiceHealth,
  SystemStatusResponse,
} from '../types';
import {
  DashboardAttentionKind,
  DashboardAttentionSeverity,
  JobLifecycleStatus,
  ProjectJobPriority,
  ProjectJobStatus,
} from '../types';

export interface MainDashboardProps {
  statusData: SystemStatusResponse | null;
  apiError?: string | null;
  signalRConnected?: boolean;
  isLoading?: boolean;
  overview?: DashboardOverviewResponse | null;
  projects: Project[];
  jobsByProject?: Record<string, ProjectJob[]>;
  personas: Persona[];
  agents: Agent[];
  sessions: AgentSession[];
  agentUsages?: Record<string, AgentUsageResponse>;
  onRefresh?: () => void | Promise<void>;
  onNavigateTab?: (tab: 'projects' | 'personas' | 'drivers') => void;
  onSelectJob?: (projectId: string, jobId: string) => void;
  onSelectProject?: (projectId: string) => void;
  onSelectSession?: (session: AgentSession) => void;
  onSelectPersona?: (persona: Persona) => void;
  onStartPersonaSession?: (persona: Persona) => void;
  onSelectDriver?: (agent: Agent) => void;
  onOpenDriverUsage?: (agent: Agent) => void;
  onOpenNewProjectModal?: () => void;
  onOpenNewJobModal?: (project: Project) => void;
  onOpenCreatePersonaModal?: () => void;
  onOpenRegisterDriverModal?: () => void;
}

type JobFilterMode = 'all' | 'active' | 'completed' | 'archived';

const SERVICE_LABELS: Record<string, string> = {
  api: 'API Gateway',
  postgres: 'PostgreSQL',
  redis: 'Redis',
  temporal: 'Temporal',
  s3: 'Object Store',
};

const QUOTA_BAR_CLASS: Record<QuotaTone, string> = {
  healthy: 'bg-emerald-400',
  warning: 'bg-amber-400',
  critical: 'bg-rose-400',
  unknown: 'bg-slate-600',
};

function formatCompactTokens(total: number | undefined | null): string {
  const value = Number(total ?? 0);
  if (!Number.isFinite(value) || value <= 0) return '0';
  if (value < 1000) return `${value}`;
  if (value < 1_000_000) return `${(value / 1000).toFixed(1)}k`;
  return `${(value / 1_000_000).toFixed(2)}M`;
}

function formatServiceName(key: string): string {
  if (SERVICE_LABELS[key]) return SERVICE_LABELS[key];
  return key.charAt(0).toUpperCase() + key.slice(1);
}

function getServiceTone(health?: ServiceHealth): {
  dot: string;
  badge: string;
  label: string;
} {
  const raw = (health?.status || '').toLowerCase();
  if (raw === 'healthy') {
    return {
      dot: 'bg-emerald-400',
      badge: 'border-emerald-500/30 bg-emerald-950/30 text-emerald-200',
      label: 'Healthy',
    };
  }
  if (raw === 'degraded') {
    return {
      dot: 'bg-amber-400',
      badge: 'border-amber-500/30 bg-amber-950/30 text-amber-200',
      label: 'Degraded',
    };
  }
  if (raw === 'unhealthy') {
    return {
      dot: 'bg-rose-400',
      badge: 'border-rose-500/30 bg-rose-950/30 text-rose-200',
      label: 'Unhealthy',
    };
  }
  return {
    dot: 'bg-slate-500',
    badge: 'border-slate-800 bg-slate-900/60 text-slate-300',
    label: health?.status || 'Unknown',
  };
}

function formatAttentionKindLabel(kind: string): string {
  switch (kind) {
    case DashboardAttentionKind.Escalation:
      return 'Escalation';
    case DashboardAttentionKind.TurnInterrupted:
      return 'Turn Interrupted';
    case DashboardAttentionKind.PlanApproval:
      return 'Plan Approval';
    case DashboardAttentionKind.DiscoveryQa:
      return 'Discovery Q&A';
    case DashboardAttentionKind.PrReview:
      return 'PR Review';
    case DashboardAttentionKind.PendingCard:
      return 'Pending Input';
    case DashboardAttentionKind.SessionWaitingInput:
      return 'Session Waiting';
    case DashboardAttentionKind.JobAwaitingApproval:
      return 'Awaiting Approval';
    case DashboardAttentionKind.JobAwaitingReview:
      return 'Awaiting Review';
    case DashboardAttentionKind.JobBlocked:
      return 'Blocked';
    default:
      return kind.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
  }
}

function getAttentionSeverityStyle(severity: string): {
  card: string;
  badge: string;
  dot: string;
  rank: number;
} {
  switch (severity) {
    case DashboardAttentionSeverity.Critical:
      return {
        card: 'border-rose-500/40 bg-rose-950/20 hover:border-rose-400/60',
        badge: 'bg-rose-500/20 text-rose-200 border-rose-500/40',
        dot: 'bg-rose-400',
        rank: 0,
      };
    case DashboardAttentionSeverity.Warning:
      return {
        card: 'border-amber-500/35 bg-amber-950/15 hover:border-amber-400/55',
        badge: 'bg-amber-500/20 text-amber-200 border-amber-500/40',
        dot: 'bg-amber-400',
        rank: 1,
      };
    default:
      return {
        card: 'border-sky-500/35 bg-sky-950/15 hover:border-sky-400/55',
        badge: 'bg-sky-500/20 text-sky-200 border-sky-500/40',
        dot: 'bg-sky-400',
        rank: 2,
      };
  }
}

function getJobBadgeInfo(job: ProjectJob): { label: string; className: string } {
  if (job.status === ProjectJobStatus.Completed) {
    return {
      label: 'Completed',
      className: 'bg-emerald-500/15 text-emerald-300 border-emerald-500/30',
    };
  }
  if (job.status === ProjectJobStatus.Archived) {
    return {
      label: 'Archived',
      className: 'bg-slate-800/80 text-slate-400 border-slate-700',
    };
  }
  if (job.status === ProjectJobStatus.Pending) {
    return {
      label: 'Pending',
      className: 'bg-slate-800 text-slate-300 border-slate-700',
    };
  }

  switch (job.lifecycleStatus) {
    case JobLifecycleStatus.Planning:
      return {
        label: 'Planning',
        className: 'bg-indigo-500/15 text-indigo-300 border-indigo-500/30',
      };
    case JobLifecycleStatus.AwaitingApproval:
      return {
        label: 'Awaiting Approval',
        className: 'bg-amber-500/20 text-amber-200 border-amber-500/40',
      };
    case JobLifecycleStatus.AwaitingReview:
      return {
        label: 'Awaiting Review',
        className: 'bg-purple-500/20 text-purple-200 border-purple-500/40',
      };
    case JobLifecycleStatus.Blocked:
      return {
        label: 'Blocked',
        className: 'bg-rose-500/20 text-rose-200 border-rose-500/40',
      };
    case JobLifecycleStatus.Failed:
      return {
        label: 'Failed',
        className: 'bg-rose-500/20 text-rose-200 border-rose-500/40',
      };
    case JobLifecycleStatus.Completed:
      return {
        label: 'Completed',
        className: 'bg-emerald-500/15 text-emerald-300 border-emerald-500/30',
      };
    default:
      return {
        label: 'In Progress',
        className: 'bg-sky-500/15 text-sky-300 border-sky-500/30',
      };
  }
}

function findPrimaryResetTime(usage?: AgentUsageResponse): string | null {
  const groups = usage?.providerQuota?.groups;
  if (!Array.isArray(groups)) return null;
  for (const group of groups) {
    if (!Array.isArray(group?.buckets)) continue;
    for (const bucket of group.buckets) {
      const label = describeReset(bucket?.resetTime);
      if (label) return label;
    }
  }
  return null;
}

function buildFallbackOverview(
  projects: Project[],
  jobsByProject: Record<string, ProjectJob[]>,
  sessions: AgentSession[],
  personas: Persona[]
): DashboardOverviewResponse {
  const projectNames = new Map<string, string>();
  for (const project of projects) {
    projectNames.set(project.id, project.name);
  }
  const personaNames = new Map<string, string>();
  for (const persona of personas) {
    personaNames.set(persona.id, persona.name);
  }

  const seenJobIds = new Set<string>();
  const loadedJobs: ProjectJob[] = [];
  for (const [projectId, list] of Object.entries(jobsByProject)) {
    if (!Array.isArray(list)) continue;
    for (const job of list) {
      if (!job?.id || seenJobIds.has(job.id)) continue;
      seenJobIds.add(job.id);
      loadedJobs.push({
        ...job,
        projectName: job.projectName || projectNames.get(job.projectId || projectId) || null,
      });
    }
  }

  loadedJobs.sort((a, b) => {
    const aTime = new Date(a.updatedAt || a.createdAt || 0).getTime();
    const bTime = new Date(b.updatedAt || b.createdAt || 0).getTime();
    return bTime - aTime;
  });

  const loadedActive = loadedJobs.filter((j) => j.status === ProjectJobStatus.Active).length;
  const loadedPending = loadedJobs.filter((j) => j.status === ProjectJobStatus.Pending).length;
  const loadedCompleted = loadedJobs.filter((j) => j.status === ProjectJobStatus.Completed).length;
  const loadedArchived = loadedJobs.filter((j) => j.status === ProjectJobStatus.Archived).length;

  const projectSumJobs = projects.reduce((sum, p) => sum + (p.jobCount || 0), 0);
  const projectSumActive = projects.reduce((sum, p) => sum + (p.activeJobCount || 0), 0);

  const activeProjects = projects.filter(
    (p) =>
      (p.activeJobCount || 0) > 0 ||
      (jobsByProject[p.id] || []).some((j) => j.status === ProjectJobStatus.Active)
  ).length;

  const liveStatuses = new Set(['active', 'thinking', 'busy', 'waiting_for_input']);
  const liveSessions = sessions.filter((s) => liveStatuses.has((s.status || '').toLowerCase()));
  const thinkingSessions = liveSessions.filter((s) => {
    const st = (s.status || '').toLowerCase();
    return st === 'thinking' || st === 'busy';
  }).length;
  const waitingSessions = liveSessions.filter(
    (s) => (s.status || '').toLowerCase() === 'waiting_for_input'
  );

  const attentionItems: DashboardAttentionItem[] = [];

  for (const session of waitingSessions) {
    const personaName =
      session.personaName ||
      (session.personaId ? personaNames.get(session.personaId) || null : null);
    attentionItems.push({
      id: `session:${session.id}`,
      kind: DashboardAttentionKind.SessionWaitingInput,
      severity: DashboardAttentionSeverity.Warning,
      title: session.title || (personaName ? `Session with @${personaName}` : 'Interactive session'),
      summary: 'Waiting for operator input',
      sessionId: session.id,
      personaId: session.personaId || null,
      personaName,
      driverId: session.driverId || session.creatorAgentId || null,
      createdAt: session.createdAt,
      updatedAt: session.updatedAt,
    });
  }

  for (const job of loadedJobs) {
    if (job.status !== ProjectJobStatus.Active) continue;
    let kind: string | null = null;
    let severity: string = DashboardAttentionSeverity.Warning;
    let summary = job.progressHeartbeat || job.title;

    if (
      job.lifecycleStatus === JobLifecycleStatus.Blocked ||
      job.lifecycleStatus === JobLifecycleStatus.Failed
    ) {
      kind = DashboardAttentionKind.JobBlocked;
      severity = DashboardAttentionSeverity.Critical;
      summary = job.progressHeartbeat || 'Job execution is blocked and requires intervention';
    } else if (job.lifecycleStatus === JobLifecycleStatus.AwaitingApproval) {
      kind = DashboardAttentionKind.JobAwaitingApproval;
      severity = DashboardAttentionSeverity.Warning;
      summary = job.progressHeartbeat || 'Execution plan is waiting for approval';
    } else if (job.lifecycleStatus === JobLifecycleStatus.AwaitingReview) {
      kind = DashboardAttentionKind.JobAwaitingReview;
      severity = DashboardAttentionSeverity.Info;
      summary = job.progressHeartbeat || 'Pull request is waiting for review';
    }

    if (kind) {
      attentionItems.push({
        id: `job:${job.id}`,
        kind,
        severity,
        title: job.title,
        summary,
        projectId: job.projectId,
        projectName: job.projectName || projectNames.get(job.projectId) || null,
        jobId: job.id,
        jobTitle: job.title,
        personaId: job.assignedAgentId || null,
        personaName: job.assignedAgentId ? personaNames.get(job.assignedAgentId) || null : null,
        createdAt: job.createdAt,
        updatedAt: job.updatedAt,
      });
    }
  }

  const jobStats: DashboardJobStats = {
    totalProjects: projects.length,
    activeProjects,
    totalJobs: Math.max(loadedJobs.length, projectSumJobs),
    activeJobs: Math.max(loadedActive, projectSumActive),
    pendingJobs: loadedPending,
    completedJobs: loadedCompleted,
    archivedJobs: loadedArchived,
    attentionCount: attentionItems.length,
    activeSessions: liveSessions.length,
    thinkingSessions,
    waitingForInputSessions: waitingSessions.length,
  };

  return {
    generatedAt: new Date().toISOString(),
    jobStats,
    recentJobs: loadedJobs,
    attentionItems,
  };
}

export const MainDashboard: React.FC<MainDashboardProps> = ({
  statusData,
  apiError = null,
  signalRConnected,
  isLoading = false,
  overview,
  projects,
  jobsByProject = {},
  personas,
  agents,
  sessions,
  agentUsages = {},
  onRefresh,
  onNavigateTab,
  onSelectJob,
  onSelectProject,
  onSelectSession,
  onSelectPersona,
  onStartPersonaSession,
  onSelectDriver,
  onOpenDriverUsage,
  onOpenNewProjectModal,
  onOpenNewJobModal,
  onOpenCreatePersonaModal,
  onOpenRegisterDriverModal,
}) => {
  const [selfOverview, setSelfOverview] = useState<DashboardOverviewResponse | null>(null);
  const [isRefreshingLocal, setIsRefreshingLocal] = useState(false);
  const [jobFilter, setJobFilter] = useState<JobFilterMode>('all');

  const loadStandaloneOverview = useCallback(async (): Promise<DashboardOverviewResponse | null> => {
    if (overview !== undefined) return null;
    if (typeof api.getDashboardOverview !== 'function') return null;
    try {
      const maybePromise = api.getDashboardOverview();
      if (maybePromise && typeof maybePromise.then === 'function') {
        const data = await maybePromise;
        return data ?? null;
      }
    } catch {
      // Fall back cleanly to derived prop metrics when overview endpoint is unreachable
    }
    return null;
  }, [overview]);

  useEffect(() => {
    let cancelled = false;
    void loadStandaloneOverview().then((data) => {
      if (!cancelled && data) {
        setSelfOverview(data);
      }
    });
    return () => {
      cancelled = true;
    };
  }, [loadStandaloneOverview]);

  const handleRefreshClick = async () => {
    setIsRefreshingLocal(true);
    try {
      const [, refreshedOverview] = await Promise.all([
        Promise.resolve(onRefresh?.()),
        overview === undefined ? loadStandaloneOverview() : Promise.resolve(null),
      ]);
      if (refreshedOverview) {
        setSelfOverview(refreshedOverview);
      }
    } finally {
      setIsRefreshingLocal(false);
    }
  };

  const fallbackOverview = useMemo(
    () => buildFallbackOverview(projects, jobsByProject, sessions, personas),
    [projects, jobsByProject, sessions, personas]
  );

  const effectiveOverview = overview ?? selfOverview ?? fallbackOverview;
  const stats = effectiveOverview.jobStats;

  const projectNamesById = useMemo(() => {
    const map = new Map<string, string>();
    for (const p of projects) {
      map.set(p.id, p.name);
    }
    return map;
  }, [projects]);

  const personasById = useMemo(() => {
    const map = new Map<string, Persona>();
    for (const p of personas) {
      map.set(p.id, p);
    }
    return map;
  }, [personas]);

  const sortedAttentionItems = useMemo(() => {
    return [...effectiveOverview.attentionItems].sort((a, b) => {
      const rankDiff =
        getAttentionSeverityStyle(a.severity).rank - getAttentionSeverityStyle(b.severity).rank;
      if (rankDiff !== 0) return rankDiff;
      return (
        new Date(b.updatedAt || b.createdAt || 0).getTime() -
        new Date(a.updatedAt || a.createdAt || 0).getTime()
      );
    });
  }, [effectiveOverview.attentionItems]);

  const criticalAttentionCount = useMemo(
    () =>
      sortedAttentionItems.filter((item) => item.severity === DashboardAttentionSeverity.Critical)
        .length,
    [sortedAttentionItems]
  );

  const filteredJobs = useMemo(() => {
    const allJobs = effectiveOverview.recentJobs;
    switch (jobFilter) {
      case 'active':
        return allJobs.filter(
          (j) => j.status === ProjectJobStatus.Active || j.status === ProjectJobStatus.Pending
        );
      case 'completed':
        return allJobs.filter((j) => j.status === ProjectJobStatus.Completed);
      case 'archived':
        return allJobs.filter((j) => j.status === ProjectJobStatus.Archived);
      case 'all':
      default:
        return allJobs.filter((j) => j.status !== ProjectJobStatus.Archived);
    }
  }, [effectiveOverview.recentJobs, jobFilter]);

  const archivedInRecentCount = useMemo(
    () => effectiveOverview.recentJobs.filter((j) => j.status === ProjectJobStatus.Archived).length,
    [effectiveOverview.recentJobs]
  );

  const driverStats = useMemo(() => {
    let online = 0;
    let busy = 0;
    let offline = 0;
    let lowQuota = 0;

    for (const agent of agents) {
      const hasThinkingSession = sessions.some(
        (s) =>
          (s.driverId === agent.id || s.creatorAgentId === agent.id) &&
          (s.status === 'thinking' || s.status === 'busy')
      );
      if (agent.status === 'offline') {
        offline++;
      } else if (agent.status === 'busy' || hasThinkingSession) {
        online++;
        busy++;
      } else {
        online++;
      }

      const usage = agentUsages[agent.id];
      if (usage?.providerQuota) {
        const q = summarizeQuota(usage.providerQuota);
        if (q.available && (q.tone === 'warning' || q.tone === 'critical')) {
          lowQuota++;
        }
      }
    }

    return { online, busy, offline, lowQuota };
  }, [agents, sessions, agentUsages]);

  const serviceEntries = useMemo(() => {
    if (!statusData?.services) return [];
    return Object.entries(statusData.services).filter(
      (entry): entry is [string, ServiceHealth] => Boolean(entry[1])
    );
  }, [statusData]);

  const overallHealthBadge = useMemo(() => {
    if (apiError) {
      return {
        label: 'API Offline',
        dot: 'bg-rose-400',
        pill: 'bg-rose-950/60 border-rose-500/40 text-rose-200',
      };
    }
    const raw = (statusData?.status || '').toLowerCase();
    if (raw === 'healthy') {
      return {
        label: 'Mesh Healthy',
        dot: 'bg-emerald-400',
        pill: 'bg-emerald-950/50 border-emerald-500/35 text-emerald-200',
      };
    }
    if (raw === 'degraded') {
      return {
        label: 'Mesh Degraded',
        dot: 'bg-amber-400',
        pill: 'bg-amber-950/50 border-amber-500/35 text-amber-200',
      };
    }
    if (raw === 'unhealthy') {
      return {
        label: 'Mesh Unhealthy',
        dot: 'bg-rose-400',
        pill: 'bg-rose-950/60 border-rose-500/40 text-rose-200',
      };
    }
    return {
      label: 'Connecting...',
      dot: 'bg-amber-400 animate-pulse',
      pill: 'bg-slate-900 border-slate-700 text-slate-300',
    };
  }, [apiError, statusData?.status]);

  const handleOpenAttentionItem = (item: DashboardAttentionItem) => {
    if (
      item.sessionId &&
      (item.kind === DashboardAttentionKind.SessionWaitingInput || !item.jobId)
    ) {
      const existing = sessions.find((s) => s.id === item.sessionId);
      const targetSession: AgentSession = existing || {
        id: item.sessionId,
        title: item.title,
        status: 'waiting_for_input',
        metadataJson: '{}',
        personaId: item.personaId || null,
        personaName: item.personaName || null,
        driverId: item.driverId || null,
        createdAt: item.createdAt,
        updatedAt: item.updatedAt,
      };
      if (onSelectSession) {
        onSelectSession(targetSession);
      } else {
        onNavigateTab?.('personas');
      }
      return;
    }

    if (item.jobId && item.projectId && onSelectJob) {
      onSelectJob(item.projectId, item.jobId);
      return;
    }

    if (item.projectId && onSelectProject) {
      onSelectProject(item.projectId);
    }
    onNavigateTab?.('projects');
  };

  const attentionTotal = Math.max(stats.attentionCount, sortedAttentionItems.length);

  return (
    <main
      className="flex-1 overflow-y-auto bg-slate-950 p-4 sm:p-6 pb-24 sm:pb-8"
      data-testid="main-dashboard"
    >
      <div className="max-w-7xl mx-auto space-y-6">
        {/* Control Plane Health & Quick Actions Strip */}
        <section
          className="rounded-2xl border border-slate-800/80 bg-slate-900/50 p-4 sm:p-5 space-y-4"
          data-testid="dashboard-health-strip"
        >
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div className="space-y-1 min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <h2 className="text-lg sm:text-xl font-bold tracking-tight text-white">
                  Mesh Overview
                </h2>
                <span
                  className={`inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-semibold border ${overallHealthBadge.pill}`}
                  data-testid="dashboard-overall-health"
                >
                  <span className={`w-2 h-2 rounded-full ${overallHealthBadge.dot}`} />
                  <span>{overallHealthBadge.label}</span>
                </span>
                {statusData?.environment && (
                  <span className="text-[11px] font-mono px-2 py-0.5 rounded bg-slate-800/90 text-slate-300 border border-slate-700/80">
                    {statusData.environment}
                  </span>
                )}
                {signalRConnected !== undefined && (
                  <span
                    className={`text-[10px] font-mono px-1.5 py-0.5 rounded border ${
                      signalRConnected
                        ? 'text-emerald-300 bg-emerald-950/60 border-emerald-500/30'
                        : 'text-slate-400 bg-slate-900 border-slate-700'
                    }`}
                  >
                    {signalRConnected ? 'LIVE STREAM' : 'STREAM OFFLINE'}
                  </span>
                )}
              </div>
              <p className="text-xs text-slate-400">
                Real-time operational health, pending human decisions, active jobs, persona workload, and provider quotas across your mesh.
              </p>
            </div>

            <div className="flex flex-wrap items-center gap-2 shrink-0">
              {projects.length > 0 && onOpenNewJobModal ? (
                <button
                  type="button"
                  onClick={() => onOpenNewJobModal(projects[0])}
                  className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold flex items-center gap-1.5 transition cursor-pointer shadow-sm"
                  data-testid="dashboard-new-job-btn"
                >
                  <Plus className="w-3.5 h-3.5" />
                  <span>New Job</span>
                </button>
              ) : onOpenNewProjectModal ? (
                <button
                  type="button"
                  onClick={onOpenNewProjectModal}
                  className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold flex items-center gap-1.5 transition cursor-pointer shadow-sm"
                  data-testid="dashboard-new-project-btn"
                >
                  <Plus className="w-3.5 h-3.5" />
                  <span>New Project</span>
                </button>
              ) : null}

              {personas.length > 0 && onStartPersonaSession && (
                <button
                  type="button"
                  onClick={() => onStartPersonaSession(personas[0])}
                  className="px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 border border-slate-700 text-slate-200 text-xs font-medium flex items-center gap-1.5 transition cursor-pointer"
                  data-testid="dashboard-quick-session-btn"
                >
                  <MessageSquare className="w-3.5 h-3.5 text-sky-400" />
                  <span>New Session</span>
                </button>
              )}

              <button
                type="button"
                onClick={() => void handleRefreshClick()}
                disabled={isRefreshingLocal}
                aria-label="Refresh dashboard"
                data-testid="dashboard-refresh-btn"
                className="px-2.5 py-1.5 rounded-lg bg-slate-900 hover:bg-slate-800 border border-slate-800 hover:border-slate-700 text-slate-300 text-xs font-medium flex items-center gap-1.5 transition cursor-pointer disabled:opacity-60"
              >
                <RefreshCw
                  className={`w-3.5 h-3.5 text-sky-400 ${
                    isRefreshingLocal || isLoading ? 'animate-spin' : ''
                  }`}
                />
                <span className="hidden sm:inline">Refresh</span>
              </button>
            </div>
          </div>

          {/* Subsystem Health Pills */}
          {serviceEntries.length > 0 && (
            <div
              className="pt-3 border-t border-slate-800/70 flex flex-wrap items-center gap-2"
              data-testid="dashboard-service-pills"
            >
              <span className="text-[11px] font-mono uppercase tracking-wider text-slate-500 flex items-center gap-1 mr-1">
                <Server className="w-3 h-3" />
                <span>Subsystems</span>
              </span>
              {serviceEntries.map(([key, svc]) => {
                const tone = getServiceTone(svc);
                return (
                  <div
                    key={key}
                    title={svc.error || svc.description || `${formatServiceName(key)}: ${tone.label}`}
                    data-testid={`service-pill-${key}`}
                    className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-lg border text-xs ${tone.badge}`}
                  >
                    <span className={`w-1.5 h-1.5 rounded-full shrink-0 ${tone.dot}`} />
                    <span className="font-medium">{formatServiceName(key)}</span>
                    {typeof svc.latencyMs === 'number' && (
                      <span className="text-[10px] font-mono opacity-75">{svc.latencyMs}ms</span>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </section>

        {/* KPI Summary Grid */}
        <section
          className="grid grid-cols-2 lg:grid-cols-4 gap-3 sm:gap-4"
          data-testid="dashboard-kpi-grid"
        >
          {/* KPI 1: Needs Attention */}
          <div
            data-testid="kpi-attention"
            className={`rounded-2xl border p-4 transition ${
              criticalAttentionCount > 0
                ? 'border-rose-500/40 bg-rose-950/20'
                : attentionTotal > 0
                ? 'border-amber-500/35 bg-amber-950/15'
                : 'border-slate-800/80 bg-slate-900/40'
            }`}
          >
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400">Needs Attention</span>
              {attentionTotal > 0 ? (
                <ShieldAlert
                  className={`w-4 h-4 ${
                    criticalAttentionCount > 0 ? 'text-rose-400' : 'text-amber-400'
                  }`}
                />
              ) : (
                <CheckCircle2 className="w-4 h-4 text-emerald-400" />
              )}
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className="text-2xl sm:text-3xl font-bold text-white font-mono">
                {attentionTotal}
              </span>
              {criticalAttentionCount > 0 && (
                <span className="text-[11px] font-semibold text-rose-300">
                  {criticalAttentionCount} critical
                </span>
              )}
            </div>
            <p className="mt-1 text-[11px] text-slate-400 truncate">
              {attentionTotal === 0
                ? 'All clear — no blockers'
                : `${sortedAttentionItems.length} actionable item${sortedAttentionItems.length === 1 ? '' : 's'} queued`}
            </p>
          </div>

          {/* KPI 2: Active Jobs */}
          <button
            type="button"
            onClick={() => onNavigateTab?.('projects')}
            data-testid="kpi-active-jobs"
            className="text-left rounded-2xl border border-slate-800/80 bg-slate-900/40 hover:bg-slate-900/70 hover:border-slate-700 p-4 transition cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400">Active Jobs</span>
              <FolderGit2 className="w-4 h-4 text-sky-400" />
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className="text-2xl sm:text-3xl font-bold text-white font-mono">
                {stats.activeJobs}
              </span>
              <span className="text-[11px] text-slate-400">
                of {stats.totalJobs} total
              </span>
            </div>
            <p className="mt-1 text-[11px] text-slate-400 truncate">
              {stats.pendingJobs} pending · {stats.completedJobs} completed · {stats.totalProjects}{' '}
              {stats.totalProjects === 1 ? 'project' : 'projects'}
            </p>
          </button>

          {/* KPI 3: Live Sessions & Personas */}
          <button
            type="button"
            onClick={() => onNavigateTab?.('personas')}
            data-testid="kpi-sessions"
            className="text-left rounded-2xl border border-slate-800/80 bg-slate-900/40 hover:bg-slate-900/70 hover:border-slate-700 p-4 transition cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400">Live Sessions</span>
              <Activity className="w-4 h-4 text-indigo-400" />
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className="text-2xl sm:text-3xl font-bold text-white font-mono">
                {stats.activeSessions}
              </span>
              {stats.thinkingSessions > 0 && (
                <span className="text-[11px] font-semibold text-amber-300">
                  {stats.thinkingSessions} thinking
                </span>
              )}
            </div>
            <p className="mt-1 text-[11px] text-slate-400 truncate">
              {stats.waitingForInputSessions} waiting input · {personas.length}{' '}
              {personas.length === 1 ? 'persona' : 'personas'}
            </p>
          </button>

          {/* KPI 4: Connected Drivers */}
          <button
            type="button"
            onClick={() => onNavigateTab?.('drivers')}
            data-testid="kpi-drivers"
            className="text-left rounded-2xl border border-slate-800/80 bg-slate-900/40 hover:bg-slate-900/70 hover:border-slate-700 p-4 transition cursor-pointer"
          >
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold text-slate-400">Driver Fleet</span>
              <Cpu className="w-4 h-4 text-emerald-400" />
            </div>
            <div className="mt-2 flex items-baseline gap-2">
              <span className="text-2xl sm:text-3xl font-bold text-white font-mono">
                {driverStats.online}/{agents.length}
              </span>
              <span className="text-[11px] text-slate-400">online</span>
            </div>
            <p className="mt-1 text-[11px] text-slate-400 truncate">
              {driverStats.busy} busy · {driverStats.offline} offline
              {driverStats.lowQuota > 0 ? ` · ${driverStats.lowQuota} low quota` : ''}
            </p>
          </button>
        </section>

        {/* Main Two-Column Responsive Layout */}
        <div className="grid grid-cols-1 xl:grid-cols-12 gap-6 items-start">
          {/* Left Primary Column: Attention Queue + Recent Jobs */}
          <div className="xl:col-span-7 space-y-6">
            {/* Needs Attention Queue */}
            <section
              className="rounded-2xl border border-slate-800/80 bg-slate-900/40 overflow-hidden"
              data-testid="dashboard-attention-section"
            >
              <div className="px-4 py-3.5 border-b border-slate-800/80 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  {sortedAttentionItems.length > 0 ? (
                    <AlertTriangle className="w-4 h-4 text-amber-400" />
                  ) : (
                    <CheckCircle2 className="w-4 h-4 text-emerald-400" />
                  )}
                  <h3 className="text-sm font-bold text-white">Needs Your Attention</h3>
                  <span
                    className={`text-[11px] font-mono px-2 py-0.5 rounded-full border ${
                      sortedAttentionItems.length > 0
                        ? 'bg-amber-500/15 text-amber-200 border-amber-500/30'
                        : 'bg-emerald-500/10 text-emerald-300 border-emerald-500/25'
                    }`}
                  >
                    {sortedAttentionItems.length}
                  </span>
                </div>
                {stats.attentionCount > sortedAttentionItems.length && (
                  <span className="text-[11px] font-mono text-slate-400">
                    Showing top {sortedAttentionItems.length} of {stats.attentionCount}
                  </span>
                )}
              </div>

              <div className="p-4">
                {sortedAttentionItems.length === 0 ? (
                  <div
                    className="rounded-xl border border-emerald-500/20 bg-emerald-950/10 p-4 flex items-center gap-3 text-xs text-emerald-200"
                    data-testid="attention-empty-state"
                  >
                    <Sparkles className="w-5 h-5 text-emerald-400 shrink-0" />
                    <div>
                      <div className="font-semibold text-emerald-100">
                        All clear — nothing waiting on human input
                      </div>
                      <div className="text-emerald-300/80 mt-0.5">
                        Escalations, plan approvals, discovery questions, PR reviews, and paused sessions will surface here automatically.
                      </div>
                    </div>
                  </div>
                ) : (
                  <div className="space-y-2.5">
                    {sortedAttentionItems.map((item) => {
                      const sev = getAttentionSeverityStyle(item.severity);
                      const kindLabel = formatAttentionKindLabel(item.kind);
                      const personaLabel = item.personaName || item.personaId;

                      return (
                        <div
                          key={item.id}
                          onClick={() => handleOpenAttentionItem(item)}
                          data-testid={`attention-item-${item.id}`}
                          className={`rounded-xl border p-3.5 transition cursor-pointer group ${sev.card}`}
                        >
                          <div className="flex items-start justify-between gap-3">
                            <div className="min-w-0 space-y-1">
                              <div className="flex flex-wrap items-center gap-1.5">
                                <span
                                  className={`inline-flex items-center gap-1 text-[10px] font-semibold uppercase tracking-wide px-2 py-0.5 rounded border ${sev.badge}`}
                                >
                                  <span className={`w-1.5 h-1.5 rounded-full ${sev.dot}`} />
                                  <span>{kindLabel}</span>
                                </span>
                                {item.projectName && (
                                  <span className="text-[11px] font-mono text-slate-300 bg-slate-900/80 border border-slate-800 px-1.5 py-0.5 rounded">
                                    {item.projectName}
                                  </span>
                                )}
                                {personaLabel && (
                                  <span className="text-[11px] font-mono text-sky-300">
                                    @{item.personaId || personaLabel}
                                  </span>
                                )}
                              </div>

                              <div className="text-xs sm:text-sm font-semibold text-white group-hover:text-sky-200 transition truncate">
                                {item.title}
                              </div>

                              {item.summary && item.summary !== item.title && (
                                <p className="text-xs text-slate-300/90 line-clamp-2">
                                  {item.summary}
                                </p>
                              )}

                              {item.jobTitle && item.jobTitle !== item.title && (
                                <div className="text-[11px] text-slate-400 truncate">
                                  Job: <span className="text-slate-300">{item.jobTitle}</span>
                                </div>
                              )}
                            </div>

                            <div className="flex flex-col items-end justify-between gap-2 shrink-0">
                              <span className="text-[11px] font-mono text-slate-400">
                                {relativeTime(item.updatedAt || item.createdAt)}
                              </span>
                              <span className="inline-flex items-center gap-1 text-xs font-semibold text-sky-300 group-hover:text-sky-200">
                                <span>Review</span>
                                <ArrowRight className="w-3.5 h-3.5" />
                              </span>
                            </div>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </section>

            {/* Recent & Active Jobs Section */}
            <section
              className="rounded-2xl border border-slate-800/80 bg-slate-900/40 overflow-hidden"
              data-testid="dashboard-jobs-section"
            >
              <div className="px-4 py-3.5 border-b border-slate-800/80 flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <Layers className="w-4 h-4 text-sky-400" />
                  <h3 className="text-sm font-bold text-white">Recent Jobs</h3>
                  <span className="text-[11px] font-mono px-2 py-0.5 rounded-full bg-slate-800 text-slate-300">
                    {filteredJobs.length}
                  </span>
                </div>

                <div className="flex flex-wrap items-center gap-1.5">
                  <div className="flex items-center bg-slate-950/80 border border-slate-800 rounded-lg p-0.5 text-[11px]">
                    {(
                      [
                        { id: 'all', label: 'Recent' },
                        { id: 'active', label: 'Active' },
                        { id: 'completed', label: 'Done' },
                        ...(archivedInRecentCount > 0
                          ? [{ id: 'archived' as const, label: `Archived (${archivedInRecentCount})` }]
                          : []),
                      ] as { id: JobFilterMode; label: string }[]
                    ).map((tab) => (
                      <button
                        key={tab.id}
                        type="button"
                        onClick={() => setJobFilter(tab.id)}
                        data-testid={`jobs-filter-${tab.id}`}
                        className={`px-2 py-0.5 rounded-md font-medium transition cursor-pointer ${
                          jobFilter === tab.id
                            ? 'bg-sky-600/25 text-sky-300 border border-sky-500/40'
                            : 'text-slate-400 hover:text-slate-200 border border-transparent'
                        }`}
                      >
                        {tab.label}
                      </button>
                    ))}
                  </div>

                  {onNavigateTab && (
                    <button
                      type="button"
                      onClick={() => onNavigateTab('projects')}
                      className="text-xs font-medium text-sky-400 hover:text-sky-300 px-2 py-1 rounded hover:bg-slate-800/60 transition flex items-center gap-1 cursor-pointer"
                    >
                      <span>All Projects</span>
                      <ArrowRight className="w-3 h-3" />
                    </button>
                  )}
                </div>
              </div>

              <div className="p-4">
                {filteredJobs.length === 0 ? (
                  <div
                    className="rounded-xl border border-dashed border-slate-800 bg-slate-950/40 p-6 text-center space-y-3"
                    data-testid="jobs-empty-state"
                  >
                    <FolderGit2 className="w-8 h-8 text-slate-600 mx-auto" />
                    <div className="space-y-1">
                      <div className="text-xs font-semibold text-slate-300">
                        {effectiveOverview.recentJobs.length === 0
                          ? 'No jobs created yet'
                          : `No ${jobFilter} jobs found`}
                      </div>
                      <p className="text-[11px] text-slate-500 max-w-sm mx-auto">
                        {projects.length === 0
                          ? 'Create a project workspace first to assign autonomous engineering jobs to your personas.'
                          : 'Start a job to plan, implement, verify, and open a reviewed pull request on an isolated branch.'}
                      </p>
                    </div>
                    {projects.length === 0 && onOpenNewProjectModal ? (
                      <button
                        type="button"
                        onClick={onOpenNewProjectModal}
                        className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold inline-flex items-center gap-1.5 transition cursor-pointer"
                      >
                        <Plus className="w-3.5 h-3.5" />
                        <span>Create First Project</span>
                      </button>
                    ) : projects.length > 0 && onOpenNewJobModal && effectiveOverview.recentJobs.length === 0 ? (
                      <button
                        type="button"
                        onClick={() => onOpenNewJobModal(projects[0])}
                        className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold inline-flex items-center gap-1.5 transition cursor-pointer"
                      >
                        <Plus className="w-3.5 h-3.5" />
                        <span>Create First Job</span>
                      </button>
                    ) : null}
                  </div>
                ) : (
                  <div className="space-y-2.5">
                    {filteredJobs.map((job) => {
                      const badge = getJobBadgeInfo(job);
                      const projectName =
                        job.projectName || projectNamesById.get(job.projectId) || job.projectId;
                      const tasks = Array.isArray(job.tasks) ? job.tasks : [];
                      const completedTasks = tasks.filter(
                        (t) => t.isCompleted || t.status === 'completed'
                      ).length;
                      const taskPct =
                        tasks.length > 0 ? Math.round((completedTasks / tasks.length) * 100) : 0;
                      const assignedPersona = job.assignedAgentId
                        ? personasById.get(job.assignedAgentId)
                        : undefined;
                      const isUrgent = job.priority === ProjectJobPriority.Urgent;
                      const isHigh = job.priority === ProjectJobPriority.High;

                      return (
                        <div
                          key={job.id}
                          onClick={() => {
                            if (onSelectJob) {
                              onSelectJob(job.projectId, job.id);
                            } else {
                              onNavigateTab?.('projects');
                            }
                          }}
                          data-testid={`dashboard-job-${job.id}`}
                          className="rounded-xl border border-slate-800/80 bg-slate-950/50 hover:bg-slate-900/80 hover:border-slate-700 p-3.5 transition cursor-pointer group space-y-2"
                        >
                          <div className="flex items-start justify-between gap-3">
                            <div className="min-w-0 space-y-1">
                              <div className="flex flex-wrap items-center gap-1.5">
                                <span
                                  className={`text-[10px] font-semibold px-2 py-0.5 rounded border ${badge.className}`}
                                >
                                  {badge.label}
                                </span>
                                {isUrgent && (
                                  <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded bg-rose-500/20 text-rose-300 border border-rose-500/30 inline-flex items-center gap-0.5">
                                    <AlertOctagon className="w-2.5 h-2.5" />
                                    <span>Urgent</span>
                                  </span>
                                )}
                                {isHigh && (
                                  <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-300 border border-amber-500/30">
                                    High
                                  </span>
                                )}
                                {projectName && (
                                  <span className="text-[11px] font-mono text-slate-300 bg-slate-900 border border-slate-800 px-1.5 py-0.5 rounded truncate max-w-[180px]">
                                    {projectName}
                                  </span>
                                )}
                                {job.gitBranchName && (
                                  <span className="hidden sm:inline-flex items-center gap-1 text-[10px] font-mono text-slate-400 bg-slate-900/70 border border-slate-800/80 px-1.5 py-0.5 rounded truncate max-w-[180px]">
                                    <GitBranch className="w-2.5 h-2.5 text-slate-500 shrink-0" />
                                    <span className="truncate">{job.gitBranchName}</span>
                                  </span>
                                )}
                              </div>

                              <div className="text-xs sm:text-sm font-semibold text-slate-100 group-hover:text-sky-300 transition truncate">
                                {job.title}
                              </div>
                            </div>

                            <div className="flex items-center gap-1.5 text-[11px] font-mono text-slate-400 shrink-0">
                              <Clock className="w-3 h-3 text-slate-500" />
                              <span>{relativeTime(job.updatedAt || job.createdAt)}</span>
                            </div>
                          </div>

                          {job.progressHeartbeat && (
                            <p className="text-xs text-slate-400 line-clamp-1">
                              {job.progressHeartbeat}
                            </p>
                          )}

                          <div className="flex flex-wrap items-center justify-between gap-2 pt-0.5">
                            <div className="flex items-center gap-2 text-[11px] text-slate-400">
                              {job.assignedAgentId && (
                                <span className="font-mono text-sky-300">
                                  @{assignedPersona?.id || job.assignedAgentId}
                                </span>
                              )}
                              {tasks.length > 0 ? (
                                <span className="font-mono text-slate-300">
                                  {completedTasks}/{tasks.length} tasks ({taskPct}%)
                                </span>
                              ) : (
                                <span className="text-slate-500">Checklist pending</span>
                              )}
                            </div>

                            {tasks.length > 0 && (
                              <div className="w-28 sm:w-36 h-1.5 rounded-full bg-slate-800 overflow-hidden">
                                <div
                                  className={`h-full rounded-full transition-all ${
                                    completedTasks === tasks.length
                                      ? 'bg-emerald-400'
                                      : 'bg-sky-400'
                                  }`}
                                  style={{ width: `${taskPct}%` }}
                                />
                              </div>
                            )}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </section>
          </div>

          {/* Right Secondary Column: Personas & Workload + Drivers & Provider Quotas */}
          <div className="xl:col-span-5 space-y-6">
            {/* Persona Roster & Workload */}
            <section
              className="rounded-2xl border border-slate-800/80 bg-slate-900/40 overflow-hidden"
              data-testid="dashboard-personas-section"
            >
              <div className="px-4 py-3.5 border-b border-slate-800/80 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Users className="w-4 h-4 text-indigo-400" />
                  <h3 className="text-sm font-bold text-white">Personas & Workload</h3>
                  <span className="text-[11px] font-mono px-2 py-0.5 rounded-full bg-slate-800 text-slate-300">
                    {personas.length}
                  </span>
                </div>

                <div className="flex items-center gap-1.5">
                  {onOpenCreatePersonaModal && (
                    <button
                      type="button"
                      onClick={onOpenCreatePersonaModal}
                      className="text-xs text-sky-400 hover:text-sky-300 font-medium px-2 py-1 rounded hover:bg-slate-800/60 transition flex items-center gap-1 cursor-pointer"
                    >
                      <Plus className="w-3.5 h-3.5" />
                      <span>New</span>
                    </button>
                  )}
                  {onNavigateTab && (
                    <button
                      type="button"
                      onClick={() => onNavigateTab('personas')}
                      className="text-xs text-slate-400 hover:text-slate-200 font-medium px-2 py-1 rounded hover:bg-slate-800/60 transition flex items-center gap-1 cursor-pointer"
                    >
                      <span>View All</span>
                      <ArrowRight className="w-3 h-3" />
                    </button>
                  )}
                </div>
              </div>

              <div className="p-4">
                {personas.length === 0 ? (
                  <div
                    className="rounded-xl border border-dashed border-slate-800 bg-slate-950/40 p-5 text-center space-y-2.5"
                    data-testid="personas-empty-state"
                  >
                    <Users className="w-7 h-7 text-slate-600 mx-auto" />
                    <div className="text-xs font-semibold text-slate-300">No personas configured</div>
                    <p className="text-[11px] text-slate-500">
                      Create personas to bind specialized engineering roles to your connected CLI drivers.
                    </p>
                    {onOpenCreatePersonaModal && (
                      <button
                        type="button"
                        onClick={onOpenCreatePersonaModal}
                        className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold inline-flex items-center gap-1.5 transition cursor-pointer"
                      >
                        <Plus className="w-3.5 h-3.5" />
                        <span>Create Persona</span>
                      </button>
                    )}
                  </div>
                ) : (
                  <div className="space-y-2.5">
                    {personas.map((persona) => {
                      const linkedDriver = agents.find((a) => a.id === persona.driverId);
                      const driverStatus = linkedDriver?.status || persona.driverStatus || 'offline';
                      const personaSessions = sessions.filter((s) => s.personaId === persona.id);
                      const thinkingSession = personaSessions.find(
                        (s) => s.status === 'thinking' || s.status === 'busy'
                      );
                      const waitingSession = personaSessions.find(
                        (s) => s.status === 'waiting_for_input'
                      );
                      const assignedActiveJobs = effectiveOverview.recentJobs.filter(
                        (j) =>
                          j.status === ProjectJobStatus.Active &&
                          (j.assignedAgentId === persona.id ||
                            (Array.isArray(j.tasks) &&
                              j.tasks.some(
                                (t) => !t.isCompleted && t.assignedPersonaId === persona.id
                              )))
                      );

                      const isOffline = driverStatus === 'offline';
                      const isBusy = !isOffline && (driverStatus === 'busy' || Boolean(thinkingSession));
                      const isWaiting = !isOffline && Boolean(waitingSession);

                      const statusBadge = isOffline
                        ? {
                            dot: 'bg-slate-600',
                            label: 'Offline',
                            pill: 'bg-slate-800 text-slate-400 border-slate-700',
                          }
                        : isWaiting
                        ? {
                            dot: 'bg-amber-400',
                            label: 'Waiting Input',
                            pill: 'bg-amber-500/20 text-amber-200 border-amber-500/35',
                          }
                        : isBusy
                        ? {
                            dot: 'bg-amber-400 animate-pulse',
                            label: 'Working',
                            pill: 'bg-amber-500/15 text-amber-300 border-amber-500/30',
                          }
                        : {
                            dot: 'bg-emerald-400',
                            label: 'Ready',
                            pill: 'bg-emerald-500/15 text-emerald-300 border-emerald-500/30',
                          };

                      const totalSessionCount =
                        typeof persona.sessionCount === 'number'
                          ? Math.max(persona.sessionCount, personaSessions.length)
                          : personaSessions.length;

                      return (
                        <div
                          key={persona.id}
                          onClick={() => {
                            if (onSelectPersona) {
                              onSelectPersona(persona);
                            } else {
                              onNavigateTab?.('personas');
                            }
                          }}
                          data-testid={`dashboard-persona-${persona.id}`}
                          className="rounded-xl border border-slate-800/80 bg-slate-950/50 hover:bg-slate-900/80 hover:border-slate-700 p-3 transition cursor-pointer flex items-start justify-between gap-3"
                        >
                          <div className="flex items-start gap-2.5 min-w-0">
                            <span
                              className={`w-2.5 h-2.5 rounded-full mt-1.5 shrink-0 ${statusBadge.dot}`}
                            />
                            <div className="min-w-0 space-y-1">
                              <div className="flex flex-wrap items-center gap-1.5">
                                <span className="text-xs font-bold text-white truncate">
                                  {persona.name}
                                </span>
                                <span className="text-[11px] font-mono text-slate-400">
                                  @{persona.id}
                                </span>
                                <span
                                  className={`text-[10px] font-mono px-1.5 py-0.2 rounded border ${statusBadge.pill}`}
                                >
                                  {statusBadge.label}
                                </span>
                              </div>

                              <div className="text-[11px] text-slate-400 truncate">
                                {persona.role}
                              </div>

                              <div className="flex flex-wrap items-center gap-1.5 pt-0.5">
                                <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-slate-900 border border-slate-800 text-[10px] font-mono text-sky-300">
                                  <Cpu className="w-2.5 h-2.5 text-sky-400" />
                                  <span>@{persona.driverId}</span>
                                </span>
                                {persona.defaultModel && (
                                  <span className="px-1.5 py-0.5 rounded bg-purple-950/40 border border-purple-500/30 text-[10px] font-mono text-purple-300 truncate max-w-[140px]">
                                    {persona.defaultModel}
                                  </span>
                                )}
                                {assignedActiveJobs.length > 0 && (
                                  <span className="px-1.5 py-0.5 rounded bg-sky-950/50 border border-sky-500/30 text-[10px] font-mono text-sky-300">
                                    {assignedActiveJobs.length} active{' '}
                                    {assignedActiveJobs.length === 1 ? 'job' : 'jobs'}
                                  </span>
                                )}
                                <span className="text-[10px] font-mono text-slate-500">
                                  {totalSessionCount}{' '}
                                  {totalSessionCount === 1 ? 'session' : 'sessions'}
                                </span>
                              </div>

                              {thinkingSession && (
                                <div className="text-[11px] text-amber-300/90 truncate">
                                  Active: {thinkingSession.title}
                                </div>
                              )}
                            </div>
                          </div>

                          {onStartPersonaSession && (
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                onStartPersonaSession(persona);
                              }}
                              title={`Start new session with ${persona.name}`}
                              data-testid={`persona-start-session-${persona.id}`}
                              className="p-1.5 rounded-lg bg-slate-900 hover:bg-sky-600 border border-slate-800 hover:border-sky-500 text-slate-300 hover:text-white transition shrink-0 cursor-pointer"
                            >
                              <Plus className="w-3.5 h-3.5" />
                            </button>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </section>

            {/* Connected Drivers & Provider Quotas */}
            <section
              className="rounded-2xl border border-slate-800/80 bg-slate-900/40 overflow-hidden"
              data-testid="dashboard-drivers-section"
            >
              <div className="px-4 py-3.5 border-b border-slate-800/80 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Bot className="w-4 h-4 text-emerald-400" />
                  <h3 className="text-sm font-bold text-white">Drivers & Quotas</h3>
                  <span className="text-[11px] font-mono px-2 py-0.5 rounded-full bg-slate-800 text-slate-300">
                    {agents.length}
                  </span>
                </div>

                <div className="flex items-center gap-1.5">
                  {onOpenRegisterDriverModal && (
                    <button
                      type="button"
                      onClick={onOpenRegisterDriverModal}
                      className="text-xs text-sky-400 hover:text-sky-300 font-medium px-2 py-1 rounded hover:bg-slate-800/60 transition flex items-center gap-1 cursor-pointer"
                    >
                      <Plus className="w-3.5 h-3.5" />
                      <span>Add</span>
                    </button>
                  )}
                  {onNavigateTab && (
                    <button
                      type="button"
                      onClick={() => onNavigateTab('drivers')}
                      className="text-xs text-slate-400 hover:text-slate-200 font-medium px-2 py-1 rounded hover:bg-slate-800/60 transition flex items-center gap-1 cursor-pointer"
                    >
                      <span>Configure</span>
                      <ArrowRight className="w-3 h-3" />
                    </button>
                  )}
                </div>
              </div>

              <div className="p-4">
                {agents.length === 0 ? (
                  <div
                    className="rounded-xl border border-dashed border-slate-800 bg-slate-950/40 p-5 text-center space-y-2.5"
                    data-testid="drivers-empty-state"
                  >
                    <Bot className="w-7 h-7 text-slate-600 mx-auto" />
                    <div className="text-xs font-semibold text-slate-300">No drivers connected</div>
                    <p className="text-[11px] text-slate-500">
                      Register a CLI driver or OpenAI-compatible runtime to execute jobs and interactive sessions.
                    </p>
                    {onOpenRegisterDriverModal && (
                      <button
                        type="button"
                        onClick={onOpenRegisterDriverModal}
                        className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold inline-flex items-center gap-1.5 transition cursor-pointer"
                      >
                        <Plus className="w-3.5 h-3.5" />
                        <span>Register Driver</span>
                      </button>
                    )}
                  </div>
                ) : (
                  <div className="space-y-2.5">
                    {agents.map((agent) => {
                      const isOffline = agent.status === 'offline';
                      const hasThinkingSession = sessions.some(
                        (s) =>
                          (s.driverId === agent.id || s.creatorAgentId === agent.id) &&
                          (s.status === 'thinking' || s.status === 'busy')
                      );
                      const isBusy = !isOffline && (agent.status === 'busy' || hasThinkingSession);
                      const boundPersonas = personas.filter((p) => p.driverId === agent.id);
                      const usage = agentUsages[agent.id];
                      const quota = summarizeQuota(usage?.providerQuota);
                      const resetLabel = findPrimaryResetTime(usage);
                      const totalTokens = formatCompactTokens(usage?.lifetimeTokens?.totalTokens);
                      const remainingPct =
                        quota.available && quota.remainingFraction !== null
                          ? Math.round(quota.remainingFraction * 100)
                          : null;

                      return (
                        <div
                          key={agent.id}
                          onClick={() => {
                            if (onSelectDriver) {
                              onSelectDriver(agent);
                            } else {
                              onNavigateTab?.('drivers');
                            }
                          }}
                          data-testid={`dashboard-driver-${agent.id}`}
                          className="rounded-xl border border-slate-800/80 bg-slate-950/50 hover:bg-slate-900/80 hover:border-slate-700 p-3.5 transition cursor-pointer space-y-2.5"
                        >
                          <div className="flex items-start justify-between gap-2">
                            <div className="flex items-start gap-2.5 min-w-0">
                              <span
                                className={`w-2.5 h-2.5 rounded-full mt-1 shrink-0 ${
                                  isOffline
                                    ? 'bg-slate-600'
                                    : isBusy
                                    ? 'bg-amber-400 animate-pulse'
                                    : 'bg-emerald-400'
                                }`}
                              />
                              <div className="min-w-0">
                                <div className="flex flex-wrap items-center gap-1.5">
                                  <span className="text-xs font-bold text-white truncate">
                                    {agent.name}
                                  </span>
                                  <span
                                    className={`text-[9px] font-mono uppercase px-1.5 py-0.2 rounded ${
                                      isOffline
                                        ? 'bg-slate-800 text-slate-400'
                                        : isBusy
                                        ? 'bg-amber-500/20 text-amber-300'
                                        : 'bg-emerald-500/20 text-emerald-300'
                                    }`}
                                  >
                                    {isOffline ? 'offline' : isBusy ? 'busy' : 'online'}
                                  </span>
                                </div>
                                <div className="text-[11px] font-mono text-slate-400 truncate">
                                  @{agent.id}
                                </div>
                              </div>
                            </div>

                            {usage && (
                              <button
                                type="button"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  if (onOpenDriverUsage) {
                                    onOpenDriverUsage(agent);
                                  } else {
                                    onSelectDriver?.(agent);
                                  }
                                }}
                                title={quota.detail}
                                data-testid={`driver-usage-btn-${agent.id}`}
                                className="inline-flex items-center gap-1 px-2 py-0.5 rounded-md bg-slate-900 hover:bg-slate-800 border border-slate-800 text-[10px] font-mono text-slate-300 transition shrink-0 cursor-pointer"
                              >
                                <Zap className="w-2.5 h-2.5 text-sky-400" />
                                <span>{totalTokens} tokens</span>
                                <BarChart2 className="w-2.5 h-2.5 text-slate-500 ml-0.5" />
                              </button>
                            )}
                          </div>

                          {boundPersonas.length > 0 && (
                            <div className="flex flex-wrap items-center gap-1">
                              {boundPersonas.map((bp) => (
                                <span
                                  key={bp.id}
                                  className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-indigo-950/50 border border-indigo-500/30 text-[10px] font-mono text-indigo-300"
                                >
                                  <span>@{bp.id}</span>
                                </span>
                              ))}
                            </div>
                          )}

                          {/* Provider Quota Bar / Truthful Unavailable State */}
                          {usage ? (
                            quota.available && remainingPct !== null ? (
                              <div
                                className="space-y-1 pt-0.5"
                                data-testid={`driver-quota-meter-${agent.id}`}
                              >
                                <div className="flex items-center justify-between text-[11px] font-mono">
                                  <span className="flex items-center gap-1.5 min-w-0 truncate">
                                    <span
                                      className={`w-1.5 h-1.5 rounded-full shrink-0 ${
                                        QUOTA_DOT_CLASS[quota.tone]
                                      }`}
                                    />
                                    <span className={`font-semibold ${QUOTA_TEXT_CLASS[quota.tone]}`}>
                                      {quota.label}
                                    </span>
                                    {usage.providerQuota?.provider && (
                                      <span className="text-slate-400 truncate">
                                        · {usage.providerQuota.provider}
                                        {usage.providerQuota.planName
                                          ? ` (${usage.providerQuota.planName})`
                                          : ''}
                                      </span>
                                    )}
                                  </span>
                                  {resetLabel && (
                                    <span className="text-[10px] text-slate-400 shrink-0 ml-2">
                                      {resetLabel}
                                    </span>
                                  )}
                                </div>
                                <div className="w-full h-1.5 rounded-full bg-slate-800 overflow-hidden">
                                  <div
                                    className={`h-full rounded-full transition-all ${
                                      QUOTA_BAR_CLASS[quota.tone]
                                    }`}
                                    style={{ width: `${remainingPct}%` }}
                                  />
                                </div>
                              </div>
                            ) : (
                              <div
                                className="flex items-center justify-between text-[11px] font-mono text-slate-400 pt-0.5"
                                title={quota.detail}
                                data-testid={`driver-quota-unavailable-${agent.id}`}
                              >
                                <span className="inline-flex items-center gap-1.5">
                                  <span
                                    className={`w-1.5 h-1.5 rounded-full ${QUOTA_DOT_CLASS.unknown}`}
                                  />
                                  <span>{quota.label}</span>
                                </span>
                                <span className="text-[10px] text-slate-500">
                                  No provider quota feed
                                </span>
                              </div>
                            )
                          ) : (
                            <div className="text-[11px] font-mono text-slate-500 pt-0.5">
                              Usage telemetry pending…
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            </section>
          </div>
        </div>
      </div>
    </main>
  );
};
