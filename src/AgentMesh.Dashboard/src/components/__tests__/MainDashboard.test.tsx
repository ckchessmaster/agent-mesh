import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MainDashboard } from '../MainDashboard';
import { api } from '../../services/api';
import type {
  Agent,
  AgentSession,
  AgentUsageResponse,
  DashboardOverviewResponse,
  Persona,
  Project,
  ProjectJob,
  SystemStatusResponse,
} from '../../types';
import {
  JobLifecycleStatus,
  ProjectJobPriority,
  ProjectJobStatus,
  WorkspaceType,
} from '../../types';

const sampleStatus: SystemStatusResponse = {
  status: 'Healthy',
  timestamp: '2026-11-18T16:00:00Z',
  environment: 'Production',
  services: {
    api: { status: 'Healthy', latencyMs: 4, description: 'API responsive' },
    postgres: { status: 'Healthy', latencyMs: 12, description: 'Database connected' },
    redis: { status: 'Degraded', latencyMs: 85, description: 'High latency' },
    temporal: { status: 'Healthy', latencyMs: 19, description: 'Workflow engine ready' },
    s3: { status: 'Healthy', description: 'Bucket reachable' },
  },
};

const sampleProjects: Project[] = [
  {
    id: 'agent-mesh',
    name: 'Agent Mesh',
    description: 'Core platform repository',
    workspaceType: WorkspaceType.Git,
    gitRepositoryUrl: 'https://github.com/ckchessmaster/agent-mesh.git',
    gitBaseBranch: 'main',
    projectInstructions: '',
    assignedPersonas: [],
    jobCount: 4,
    activeJobCount: 2,
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-18T16:00:00Z',
  },
];

const samplePersonas: Persona[] = [
  {
    id: 'bee',
    name: 'Bee',
    role: 'Principal Software Engineer',
    personaPrompt: 'Senior architect',
    driverId: 'copilot-driver',
    driverStatus: 'busy',
    defaultModel: 'gemini-4-carbon',
    trustTier: 2,
    sessionCount: 6,
    createdAt: '2026-11-01T00:00:00Z',
  },
  {
    id: 'astra',
    name: 'Astra',
    role: 'Senior Software Engineer',
    personaPrompt: 'Frontend specialist',
    driverId: 'claude-driver',
    driverStatus: 'online',
    defaultModel: 'claude-sonnet-4.5',
    trustTier: 1,
    sessionCount: 2,
    createdAt: '2026-11-01T00:00:00Z',
  },
  {
    id: 'rook',
    name: 'Rook',
    role: 'Systems Engineer',
    personaPrompt: 'Infra specialist',
    driverId: 'offline-driver',
    driverStatus: 'offline',
    trustTier: 1,
    sessionCount: 0,
    createdAt: '2026-11-01T00:00:00Z',
  },
];

const sampleAgents: Agent[] = [
  {
    id: 'copilot-driver',
    name: 'GitHub Copilot CLI',
    status: 'busy',
    runtimeType: 'copilot',
    trustTier: 2,
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-18T16:00:00Z',
  },
  {
    id: 'claude-driver',
    name: 'Claude Code CLI',
    status: 'online',
    runtimeType: 'claude',
    trustTier: 2,
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-18T16:00:00Z',
  },
  {
    id: 'offline-driver',
    name: 'Local Ollama',
    status: 'offline',
    runtimeType: 'openai',
    trustTier: 1,
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-18T14:00:00Z',
  },
];

const sampleSessions: AgentSession[] = [
  {
    id: 'sess-thinking',
    title: 'Implementing MainDashboard view',
    status: 'thinking',
    metadataJson: '{}',
    personaId: 'bee',
    personaName: 'Bee',
    driverId: 'copilot-driver',
    createdAt: '2026-11-18T15:30:00Z',
    updatedAt: '2026-11-18T16:05:00Z',
  },
  {
    id: 'sess-waiting',
    title: 'Clarify OAuth callback redirect',
    status: 'waiting_for_input',
    metadataJson: '{}',
    personaId: 'astra',
    personaName: 'Astra',
    driverId: 'claude-driver',
    createdAt: '2026-11-18T15:40:00Z',
    updatedAt: '2026-11-18T16:02:00Z',
  },
];

const sampleUsages: Record<string, AgentUsageResponse> = {
  'copilot-driver': {
    agentId: 'copilot-driver',
    agentName: 'GitHub Copilot CLI',
    sessionCount: 12,
    lifetimeTokens: {
      inputTokens: 120_000,
      outputTokens: 30_000,
      thinkingTokens: 10_000,
      totalTokens: 160_000,
      totalDurationSeconds: 410,
    },
    providerQuota: {
      provider: 'GitHub Copilot',
      planName: 'Enterprise',
      primaryRemainingFraction: 0.12,
      source: 'copilot:/usage',
      observedAt: '2026-11-18T16:00:00Z',
      groups: [
        {
          name: 'Monthly Premium Requests',
          buckets: [
            {
              id: 'premium',
              name: 'Premium',
              remainingFraction: 0.12,
              resetTime: new Date(Date.now() + 6 * 3_600_000).toISOString(),
            },
          ],
        },
      ],
    },
  },
  'claude-driver': {
    agentId: 'claude-driver',
    agentName: 'Claude Code CLI',
    sessionCount: 4,
    lifetimeTokens: {
      inputTokens: 14_000,
      outputTokens: 4_400,
      thinkingTokens: 0,
      totalTokens: 18_400,
      totalDurationSeconds: 95,
    },
    providerQuota: null,
  },
};

const sampleJobs: ProjectJob[] = [
  {
    id: 'job-active-1',
    projectId: 'agent-mesh',
    projectName: 'Agent Mesh',
    title: 'Agent Mesh Dashboard',
    description: 'Build main landing page',
    status: ProjectJobStatus.Active,
    gitBranchName: 'mesh/job-1ff55314',
    priority: ProjectJobPriority.Urgent,
    lifecycleStatus: JobLifecycleStatus.InProgress,
    assignedAgentId: 'bee',
    progressHeartbeat: 'Building MainDashboard component and Vitest suite',
    tasks: [
      { id: 't1', title: 'Add API endpoint', isCompleted: true, status: 'completed' },
      { id: 't2', title: 'Add TS contracts', isCompleted: true, status: 'completed' },
      { id: 't3', title: 'Build MainDashboard', isCompleted: false, status: 'in_progress' },
      { id: 't4', title: 'Wire App tabs', isCompleted: false, status: 'todo' },
    ],
    messageCount: 4,
    createdAt: '2026-11-18T15:00:00Z',
    updatedAt: '2026-11-18T16:08:00Z',
  },
  {
    id: 'job-done-2',
    projectId: 'agent-mesh',
    projectName: 'Agent Mesh',
    title: 'Unclamp 512k OpenAI context',
    description: 'Context window fix',
    status: ProjectJobStatus.Completed,
    gitBranchName: 'mesh/job-512k',
    lifecycleStatus: JobLifecycleStatus.Completed,
    assignedAgentId: 'astra',
    tasks: [
      { id: 't5', title: 'Update adapter', isCompleted: true, status: 'completed' },
    ],
    messageCount: 2,
    createdAt: '2026-11-17T10:00:00Z',
    updatedAt: '2026-11-17T12:00:00Z',
  },
  {
    id: 'job-archived-3',
    projectId: 'agent-mesh',
    projectName: 'Agent Mesh',
    title: 'Legacy prototype spike',
    description: 'Archived spike',
    status: ProjectJobStatus.Archived,
    gitBranchName: 'mesh/job-spike',
    tasks: [],
    messageCount: 0,
    createdAt: '2026-11-10T10:00:00Z',
    updatedAt: '2026-11-10T12:00:00Z',
  },
];

const sampleOverview: DashboardOverviewResponse = {
  generatedAt: '2026-11-18T16:10:00Z',
  jobStats: {
    totalProjects: 1,
    activeProjects: 1,
    totalJobs: 3,
    activeJobs: 1,
    pendingJobs: 0,
    completedJobs: 1,
    archivedJobs: 1,
    attentionCount: 2,
    activeSessions: 2,
    thinkingSessions: 1,
    waitingForInputSessions: 1,
  },
  recentJobs: sampleJobs,
  attentionItems: [
    {
      id: 'session:sess-waiting',
      kind: 'session_waiting_input',
      severity: 'warning',
      title: 'Clarify OAuth callback redirect',
      summary: 'Waiting for operator input',
      sessionId: 'sess-waiting',
      personaId: 'astra',
      personaName: 'Astra',
      driverId: 'claude-driver',
      createdAt: '2026-11-18T15:40:00Z',
      updatedAt: '2026-11-18T16:02:00Z',
    },
    {
      id: 'card:card-crit-1',
      kind: 'escalation',
      severity: 'critical',
      title: 'Schema migration decision needed',
      summary: 'Need architect confirmation before dropping legacy column',
      projectId: 'agent-mesh',
      projectName: 'Agent Mesh',
      jobId: 'job-active-1',
      jobTitle: 'Agent Mesh Dashboard',
      cardId: 'card-crit-1',
      cardType: 'Escalation3R',
      personaId: 'bee',
      personaName: 'Bee',
      createdAt: '2026-11-18T15:55:00Z',
      updatedAt: '2026-11-18T16:00:00Z',
    },
  ],
};

describe('MainDashboard', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('renders control plane health, subsystem pills, KPI cards, and navigates tabs on KPI clicks', () => {
    const onNavigateTab = vi.fn();

    render(
      <MainDashboard
        statusData={sampleStatus}
        signalRConnected={true}
        overview={sampleOverview}
        projects={sampleProjects}
        personas={samplePersonas}
        agents={sampleAgents}
        sessions={sampleSessions}
        agentUsages={sampleUsages}
        onNavigateTab={onNavigateTab}
      />
    );

    expect(screen.getByTestId('dashboard-overall-health')).toHaveTextContent('Mesh Healthy');
    expect(screen.getByText('Production')).toBeInTheDocument();
    expect(screen.getByText('LIVE STREAM')).toBeInTheDocument();

    expect(screen.getByTestId('service-pill-api')).toHaveTextContent('API Gateway');
    expect(screen.getByTestId('service-pill-postgres')).toHaveTextContent('12ms');
    expect(screen.getByTestId('service-pill-redis')).toHaveTextContent('85ms');

    expect(screen.getByTestId('kpi-attention')).toHaveTextContent('2');
    expect(screen.getByTestId('kpi-attention')).toHaveTextContent('1 critical');
    expect(screen.getByTestId('kpi-active-jobs')).toHaveTextContent('1');
    expect(screen.getByTestId('kpi-active-jobs')).toHaveTextContent('of 3 total');
    expect(screen.getByTestId('kpi-sessions')).toHaveTextContent('2');
    expect(screen.getByTestId('kpi-sessions')).toHaveTextContent('1 thinking');
    expect(screen.getByTestId('kpi-drivers')).toHaveTextContent('2/3');
    expect(screen.getByTestId('kpi-drivers')).toHaveTextContent('1 low quota');

    fireEvent.click(screen.getByTestId('kpi-active-jobs'));
    expect(onNavigateTab).toHaveBeenCalledWith('projects');

    fireEvent.click(screen.getByTestId('kpi-sessions'));
    expect(onNavigateTab).toHaveBeenCalledWith('personas');

    fireEvent.click(screen.getByTestId('kpi-drivers'));
    expect(onNavigateTab).toHaveBeenCalledWith('drivers');
  });

  it('surfaces API Offline health state when apiError is present', () => {
    render(
      <MainDashboard
        statusData={sampleStatus}
        apiError="Connection refused"
        signalRConnected={false}
        overview={sampleOverview}
        projects={sampleProjects}
        personas={samplePersonas}
        agents={sampleAgents}
        sessions={sampleSessions}
      />
    );

    expect(screen.getByTestId('dashboard-overall-health')).toHaveTextContent('API Offline');
    expect(screen.getByText('STREAM OFFLINE')).toBeInTheDocument();
  });

  it('sorts attention items by severity and deep-links to target job or waiting session', () => {
    const onSelectJob = vi.fn();
    const onSelectSession = vi.fn();

    render(
      <MainDashboard
        statusData={sampleStatus}
        overview={sampleOverview}
        projects={sampleProjects}
        personas={samplePersonas}
        agents={sampleAgents}
        sessions={sampleSessions}
        onSelectJob={onSelectJob}
        onSelectSession={onSelectSession}
      />
    );

    const attentionSection = screen.getByTestId('dashboard-attention-section');
    const items = within(attentionSection).getAllByTestId(/^attention-item-/);
    expect(items).toHaveLength(2);
    // Critical escalation appears ahead of warning session item
    expect(items[0]).toHaveTextContent('Escalation');
    expect(items[0]).toHaveTextContent('Schema migration decision needed');
    expect(items[1]).toHaveTextContent('Session Waiting');
    expect(items[1]).toHaveTextContent('Clarify OAuth callback redirect');

    fireEvent.click(items[0]);
    expect(onSelectJob).toHaveBeenCalledWith('agent-mesh', 'job-active-1');

    fireEvent.click(items[1]);
    expect(onSelectSession).toHaveBeenCalledWith(
      expect.objectContaining({ id: 'sess-waiting', personaId: 'astra' })
    );
  });

  it('filters recent jobs across Recent, Active, Done, and Archived views and opens clicked jobs', () => {
    const onSelectJob = vi.fn();

    render(
      <MainDashboard
        statusData={sampleStatus}
        overview={sampleOverview}
        projects={sampleProjects}
        personas={samplePersonas}
        agents={sampleAgents}
        sessions={sampleSessions}
        onSelectJob={onSelectJob}
      />
    );

    // Default 'Recent' filter shows active + completed (hides archived)
    expect(screen.getByTestId('dashboard-job-job-active-1')).toBeInTheDocument();
    expect(screen.getByTestId('dashboard-job-job-done-2')).toBeInTheDocument();
    expect(screen.queryByTestId('dashboard-job-job-archived-3')).not.toBeInTheDocument();

    expect(screen.getByTestId('dashboard-job-job-active-1')).toHaveTextContent('Urgent');
    expect(screen.getByTestId('dashboard-job-job-active-1')).toHaveTextContent('2/4 tasks (50%)');
    expect(screen.getByTestId('dashboard-job-job-active-1')).toHaveTextContent(
      'Building MainDashboard component and Vitest suite'
    );

    fireEvent.click(screen.getByTestId('jobs-filter-active'));
    expect(screen.getByTestId('dashboard-job-job-active-1')).toBeInTheDocument();
    expect(screen.queryByTestId('dashboard-job-job-done-2')).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId('jobs-filter-completed'));
    expect(screen.queryByTestId('dashboard-job-job-active-1')).not.toBeInTheDocument();
    expect(screen.getByTestId('dashboard-job-job-done-2')).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('jobs-filter-archived'));
    expect(screen.getByTestId('dashboard-job-job-archived-3')).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('dashboard-job-job-archived-3'));
    expect(onSelectJob).toHaveBeenCalledWith('agent-mesh', 'job-archived-3');
  });

  it('renders persona workload states and truthful driver quotas vs explicit quota n/a', () => {
    const onSelectPersona = vi.fn();
    const onStartPersonaSession = vi.fn();
    const onOpenDriverUsage = vi.fn();

    render(
      <MainDashboard
        statusData={sampleStatus}
        overview={sampleOverview}
        projects={sampleProjects}
        personas={samplePersonas}
        agents={sampleAgents}
        sessions={sampleSessions}
        agentUsages={sampleUsages}
        onSelectPersona={onSelectPersona}
        onStartPersonaSession={onStartPersonaSession}
        onOpenDriverUsage={onOpenDriverUsage}
      />
    );

    // Personas: Bee is Working, Astra is Waiting Input, Rook is Offline
    const beeCard = screen.getByTestId('dashboard-persona-bee');
    expect(beeCard).toHaveTextContent('Working');
    expect(beeCard).toHaveTextContent('1 active job');
    expect(beeCard).toHaveTextContent('Active: Implementing MainDashboard view');

    const astraCard = screen.getByTestId('dashboard-persona-astra');
    expect(astraCard).toHaveTextContent('Waiting Input');

    const rookCard = screen.getByTestId('dashboard-persona-rook');
    expect(rookCard).toHaveTextContent('Offline');

    // Quick start session button does not trigger row selection
    fireEvent.click(screen.getByTestId('persona-start-session-bee'));
    expect(onStartPersonaSession).toHaveBeenCalledWith(expect.objectContaining({ id: 'bee' }));
    expect(onSelectPersona).not.toHaveBeenCalled();

    fireEvent.click(beeCard);
    expect(onSelectPersona).toHaveBeenCalledWith(expect.objectContaining({ id: 'bee' }));

    // Drivers: Copilot shows 12% quota left meter; Claude shows truthful quota n/a; Ollama shows pending telemetry
    const copilotQuota = screen.getByTestId('driver-quota-meter-copilot-driver');
    expect(copilotQuota).toHaveTextContent('12% quota left');
    expect(copilotQuota).toHaveTextContent('GitHub Copilot (Enterprise)');
    expect(copilotQuota).toHaveTextContent(/resets in 6 hrs/i);
    expect(screen.getByTestId('driver-usage-btn-copilot-driver')).toHaveTextContent('160.0k tokens');

    const claudeUnavailable = screen.getByTestId('driver-quota-unavailable-claude-driver');
    expect(claudeUnavailable).toHaveTextContent('quota n/a');
    expect(screen.getByTestId('driver-usage-btn-claude-driver')).toHaveTextContent('18.4k tokens');

    expect(screen.getByTestId('dashboard-driver-offline-driver')).toHaveTextContent(
      'Usage telemetry pending…'
    );

    fireEvent.click(screen.getByTestId('driver-usage-btn-claude-driver'));
    expect(onOpenDriverUsage).toHaveBeenCalledWith(
      expect.objectContaining({ id: 'claude-driver' })
    );
  });

  it('fetches overview independently when overview prop is omitted and falls back cleanly to props on error', async () => {
    const getOverviewSpy = vi
      .spyOn(api, 'getDashboardOverview')
      .mockRejectedValueOnce(new Error('Endpoint unavailable'))
      .mockResolvedValueOnce(sampleOverview);
    const onRefresh = vi.fn();

    render(
      <MainDashboard
        statusData={sampleStatus}
        projects={sampleProjects}
        jobsByProject={{ 'agent-mesh': [sampleJobs[0]] }}
        personas={samplePersonas}
        agents={sampleAgents}
        sessions={sampleSessions}
        onRefresh={onRefresh}
      />
    );

    // Fallback derives active job + waiting_for_input session from props even after initial fetch rejects
    await waitFor(() => {
      expect(getOverviewSpy).toHaveBeenCalledTimes(1);
    });
    expect(screen.getByTestId('dashboard-job-job-active-1')).toBeInTheDocument();
    expect(screen.getByTestId('attention-item-session:sess-waiting')).toBeInTheDocument();

    // Clicking Refresh invokes both onRefresh and standalone overview re-fetch
    fireEvent.click(screen.getByTestId('dashboard-refresh-btn'));
    await waitFor(() => {
      expect(getOverviewSpy).toHaveBeenCalledTimes(2);
    });
    expect(onRefresh).toHaveBeenCalledTimes(1);
    expect(await screen.findByText('Schema migration decision needed')).toBeInTheDocument();
  });

  it('renders onboarding empty states and modal triggers for a brand-new empty workspace', () => {
    const onOpenNewProjectModal = vi.fn();
    const onOpenCreatePersonaModal = vi.fn();
    const onOpenRegisterDriverModal = vi.fn();

    render(
      <MainDashboard
        statusData={sampleStatus}
        overview={null}
        projects={[]}
        personas={[]}
        agents={[]}
        sessions={[]}
        onOpenNewProjectModal={onOpenNewProjectModal}
        onOpenCreatePersonaModal={onOpenCreatePersonaModal}
        onOpenRegisterDriverModal={onOpenRegisterDriverModal}
      />
    );

    expect(screen.getByTestId('attention-empty-state')).toHaveTextContent(
      'All clear — nothing waiting on human input'
    );
    expect(screen.getByTestId('jobs-empty-state')).toBeInTheDocument();
    expect(screen.getByTestId('personas-empty-state')).toBeInTheDocument();
    expect(screen.getByTestId('drivers-empty-state')).toBeInTheDocument();

    fireEvent.click(screen.getByText('Create First Project'));
    expect(onOpenNewProjectModal).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByText('Create Persona'));
    expect(onOpenCreatePersonaModal).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByText('Register Driver'));
    expect(onOpenRegisterDriverModal).toHaveBeenCalledTimes(1);
  });
});
