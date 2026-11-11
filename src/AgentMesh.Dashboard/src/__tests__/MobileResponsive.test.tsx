// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import App from '../App';
import { JobCockpit } from '../components/JobCockpit';
import { ProjectList } from '../components/ProjectList';
import { QuestionCard } from '../components/QuestionCard';
import { SlashCommandPalette } from '../components/SlashCommandPalette';
import { TeamMentionPalette } from '../components/TeamMentionPalette';
import { api } from '../services/api';
import type { Persona, Project, ProjectJob, AgentUsageResponse } from '../types';
import { WorkspaceType, ProjectJobStatus } from '../types';

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getStatus: vi.fn(),
    getAgents: vi.fn(),
    getPersonas: vi.fn(),
    getSessions: vi.fn(),
    getSession: vi.fn(),
    getSessionMessages: vi.fn(),
    getSessionArtifacts: vi.fn(),
    getCapabilities: vi.fn(),
    getAgentUsage: vi.fn(),
    getProjects: vi.fn(),
    getProject: vi.fn(),
    createProject: vi.fn(),
    updateProject: vi.fn(),
    deleteProject: vi.fn(),
    getProjectJobs: vi.fn(),
    getProjectJob: vi.fn(),
    createProjectJob: vi.fn(),
    updateProjectJob: vi.fn(),
    deleteProjectJob: vi.fn(),
    getJobTasks: vi.fn(),
    addJobTask: vi.fn(),
    updateJobTask: vi.fn(),
    assignJobTask: vi.fn(),
    deleteJobTask: vi.fn(),
    getJobMessages: vi.fn(),
    postJobMessage: vi.fn(),
    uploadJobAttachments: vi.fn(),
    completeJob: vi.fn(),
    submitQuestionAnswer: vi.fn(),
    resetMesh: vi.fn().mockResolvedValue(undefined)
  }
}));

vi.mock('../auth/AuthProvider', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    isLoading: false,
    user: {
      profile: {
        email: 'operator@agentmesh.local',
        name: 'Operator'
      },
      access_token: 'mock-jwt-token'
    },
    signinRedirect: vi.fn(),
    signoutRedirect: vi.fn()
  }))
}));

const signalRHandlers: Record<string, Function> = {};
const mockSignalRConnection = {
  on: vi.fn((event: string, cb: Function) => {
    signalRHandlers[event] = cb;
  }),
  off: vi.fn((event: string) => {
    delete signalRHandlers[event];
  }),
  invoke: vi.fn().mockResolvedValue(undefined)
};

vi.mock('../hooks/useSignalR', () => ({
  useSignalR: () => ({
    connection: mockSignalRConnection,
    connected: true
  })
}));

describe('Mobile Responsiveness & Master-Detail Navigation', () => {
  const originalInnerWidth = window.innerWidth;

  const mockPersona: Persona = {
    id: 'astra',
    name: 'Astra',
    role: 'Tech Lead',
    personaPrompt: 'You are Astra.',
    driverId: 'agy',
    trustTier: 2,
    createdAt: new Date().toISOString()
  };

  const mockProject: Project = {
    id: 'proj-1',
    name: 'Mobile Core',
    description: 'Mobile responsive workspace',
    workspaceType: WorkspaceType.Git,
    gitBaseBranch: 'main',
    projectInstructions: '',
    jobCount: 1,
    activeJobCount: 1,
    assignedPersonas: [mockPersona],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockJob: ProjectJob = {
    id: 'job-1',
    projectId: 'proj-1',
    title: 'Mobile Navigation Polish',
    description: '',
    gitBranchName: 'mesh/job-1',
    status: ProjectJobStatus.Active,
    tasks: [],
    messageCount: 0,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockUsage: AgentUsageResponse = {
    agentId: 'agy',
    agentName: 'Antigravity CLI Driver',
    sessionCount: 0,
    lifetimeTokens: {
      inputTokens: 0,
      outputTokens: 0,
      thinkingTokens: 0,
      totalTokens: 0,
      totalDurationSeconds: 0
    }
  };

  beforeEach(() => {
    vi.clearAllMocks();
    window.innerWidth = 375;
    window.dispatchEvent(new Event('resize'));

    vi.mocked(api.getStatus).mockResolvedValue({
      status: 'Healthy',
      timestamp: new Date().toISOString(),
      environment: 'Development',
      services: {}
    });
    vi.mocked(api.getAgents).mockResolvedValue([
      {
        id: 'agy',
        name: 'Antigravity CLI Driver',
        status: 'online',
        runtimeType: 0,
        trustTier: 2
      } as any
    ]);
    vi.mocked(api.getPersonas).mockResolvedValue([mockPersona]);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.getCapabilities).mockResolvedValue({
      agentRuntimes: {}
    });
    vi.mocked(api.getAgentUsage).mockResolvedValue(mockUsage);
    vi.mocked(api.getProjects).mockResolvedValue([mockProject]);
    vi.mocked(api.getProjectJobs).mockResolvedValue([mockJob]);
    vi.mocked(api.getJobMessages).mockResolvedValue([]);
    vi.mocked(api.getJobTasks).mockResolvedValue([]);
  });

  afterEach(() => {
    window.innerWidth = originalInnerWidth;
    window.dispatchEvent(new Event('resize'));
  });

  it('renders mobile bottom nav when at master list and allows drilling into job detail with mobile back button', async () => {
    render(<App />);

    // Wait for project to appear in ProjectList
    expect(await screen.findByText('Mobile Core')).toBeInTheDocument();

    // Verify Mobile Bottom Navigation Bar is present
    const bottomNav = await screen.findByTestId('mobile-bottom-nav');
    expect(bottomNav).toBeInTheDocument();

    // Find the job item in the list and click it to drill into Detail view
    const jobItems = await screen.findAllByText('Mobile Navigation Polish');
    fireEvent.click(jobItems[0]);

    // Mobile back button should appear in the header
    const backBtn = await screen.findByTestId('mobile-back-button');
    expect(backBtn).toBeInTheDocument();

    // Bottom nav should auto-hide in detail view to maximize screen space
    expect(screen.queryByTestId('mobile-bottom-nav')).not.toBeInTheDocument();

    // Click Back to return to master list
    fireEvent.click(backBtn);

    // Master list should be back, back button gone, bottom nav restored
    expect(screen.queryByTestId('mobile-back-button')).not.toBeInTheDocument();
    expect(await screen.findByTestId('mobile-bottom-nav')).toBeInTheDocument();
  });

  it('switches between primary sections via the Mobile Bottom Navigation Bar', async () => {
    render(<App />);

    expect(await screen.findByText('Mobile Core')).toBeInTheDocument();

    const bottomNav = await screen.findByTestId('mobile-bottom-nav');
    expect(bottomNav).toBeInTheDocument();

    // Switch to Personas tab
    const personasBtn = within(bottomNav).getByRole('button', { name: /personas/i });
    fireEvent.click(personasBtn);

    expect(await screen.findByText(/Personas \(1\)/i)).toBeInTheDocument();
    expect(await screen.findByText('Astra')).toBeInTheDocument();

    // Switch to Drivers tab
    const driversBtn = within(bottomNav).getByRole('button', { name: /drivers/i });
    fireEvent.click(driversBtn);

    const driverElements = await screen.findAllByText(/Antigravity CLI Driver/i);
    expect(driverElements.length).toBeGreaterThan(0);

    // Danger Zone should be visible in Drivers tab
    const dangerZoneBtn = await screen.findByTestId('danger-zone-clear-mesh-btn');
    expect(dangerZoneBtn).toBeInTheDocument();
  });

  it('triggers reset mesh from Danger Zone button in Drivers tab', async () => {
    render(<App />);

    expect(await screen.findByText('Mobile Core')).toBeInTheDocument();

    const bottomNav = await screen.findByTestId('mobile-bottom-nav');
    const driversBtn = within(bottomNav).getByRole('button', { name: /drivers/i });
    fireEvent.click(driversBtn);

    const clearMeshBtn = await screen.findByTestId('danger-zone-clear-mesh-btn');
    fireEvent.click(clearMeshBtn);

    const confirmBtn = await screen.findByTestId('danger-zone-confirm-reset-btn');
    fireEvent.click(confirmBtn);

    await waitFor(() => {
      expect(api.resetMesh).toHaveBeenCalled();
    });
  });

  it('renders Timeline Cockpit and layout in JobCockpit on mobile', () => {
    window.innerWidth = 375;

    render(
      <JobCockpit
        project={mockProject}
        job={mockJob}
        tasks={[]}
        messages={[]}
        personas={[mockPersona]}
        sessions={[]}
        onSendMessage={vi.fn()}
        onAddTask={vi.fn()}
        onToggleTask={vi.fn()}
        onDeleteTask={vi.fn()}
        onCompleteJob={vi.fn()}
      />
    );

    // Timeline cockpit container is rendered on mobile
    expect(screen.getByTestId('timeline-cockpit-container')).toBeInTheDocument();
    expect(screen.getByTestId('cockpit-layout')).toBeInTheDocument();
    expect(screen.getByTestId('sticky-sidebar')).toBeInTheDocument();
  });

  it('renders responsive mobile header and CockpitLayout at 375px without breaking or clipping controls', () => {
    window.innerWidth = 375;

    render(
      <JobCockpit
        project={mockProject}
        job={mockJob}
        tasks={[
          {
            id: 't-1',
            title: 'Refactor responsive mobile layout',
            isCompleted: false,
            status: 'in_progress',
            assignedPersonaId: 'astra',
            targetFile: 'src/components/JobCockpit.tsx',
            verificationMethod: 'vitest test'
          }
        ]}
        messages={[]}
        personas={[mockPersona]}
        sessions={[]}
        onSendMessage={vi.fn()}
        onAddTask={vi.fn()}
        onToggleTask={vi.fn()}
        onDeleteTask={vi.fn()}
        onCompleteJob={vi.fn()}
        onUpdateJob={vi.fn()}
        onToggleJobArtifactsPanel={vi.fn()}
        jobArtifactCount={2}
      />
    );

    // Job title and project badges are present
    expect(screen.getByText('Mobile Navigation Polish')).toBeInTheDocument();
    expect(screen.getByText('Mobile Core')).toBeInTheDocument();

    // All mobile header action controls are accessible
    expect(screen.getByTestId('cockpit-assigned-agent-select')).toBeInTheDocument();
    expect(screen.getByTestId('cockpit-edit-job-btn')).toBeInTheDocument();
    expect(screen.getByTestId('toggle-job-artifacts-btn')).toBeInTheDocument();
    expect(screen.getByTestId('cockpit-complete-job-btn')).toBeInTheDocument();

    // Timeline Cockpit container is rendered
    expect(screen.getByTestId('timeline-cockpit-container')).toBeInTheDocument();
  });

  it('renders touch-accessible action buttons in ProjectList on mobile without requiring mouse hover', () => {
    const onEditProject = vi.fn();
    const onDeleteProject = vi.fn().mockResolvedValue(undefined);
    const onDeleteJob = vi.fn().mockResolvedValue(undefined);
    const onArchiveJob = vi.fn().mockResolvedValue(undefined);

    render(
      <ProjectList
        projects={[mockProject]}
        selectedProjectId="proj-1"
        selectedJobId="job-1"
        jobsByProject={{ 'proj-1': [mockJob] }}
        onSelectProject={vi.fn()}
        onSelectJob={vi.fn()}
        onOpenNewProjectModal={vi.fn()}
        onOpenNewJobModal={vi.fn()}
        onEditProject={onEditProject}
        onDeleteProject={onDeleteProject}
        onDeleteJob={onDeleteJob}
        onArchiveJob={onArchiveJob}
      />
    );

    // Project delete button is present in DOM with accessible title/label and visible mobile styling
    const deleteProjectBtn = screen.getByTestId('edit-project-btn-proj-1').parentElement?.querySelector('button[title="Delete Project"]') as HTMLElement;
    expect(deleteProjectBtn).toBeInTheDocument();
    expect(deleteProjectBtn.className).toContain('opacity-100');

    // Job action buttons (Archive, Delete) are rendered and touch-visible
    const archiveBtn = screen.getByTitle('Archive Job');
    expect(archiveBtn).toBeInTheDocument();
    expect(archiveBtn.className).toContain('opacity-100');

    const deleteJobBtn = screen.getByTitle('Delete Job');
    expect(deleteJobBtn).toBeInTheDocument();
    expect(deleteJobBtn.className).toContain('opacity-100');
  });

  it('renders responsive QuestionCard with touch-friendly option selection and submission on mobile', async () => {
    const onSubmit = vi.fn();

    render(
      <QuestionCard
        cardKey="test-card"
        targetId="target-1"
        parsedQuestions={[
          {
            question: 'Which mobile navigation pattern should we use?',
            options: ['Bottom Navigation Bar', 'Side Hamburger Drawer'],
            is_multi_select: false
          }
        ]}
        onSubmit={onSubmit}
      />
    );

    expect(screen.getByText('Agent Question — Human Input Required')).toBeInTheDocument();

    const bottomNavOption = screen.getByText('Bottom Navigation Bar');
    expect(bottomNavOption).toBeInTheDocument();

    // Select the option on mobile tap
    fireEvent.click(bottomNavOption);

    // Submit answer
    const submitBtn = screen.getByRole('button', { name: /submit answer/i });
    expect(submitBtn).toBeEnabled();
    expect(submitBtn.className).toContain('w-full sm:w-auto');

    fireEvent.click(submitBtn);
    expect(onSubmit).toHaveBeenCalledWith([
      expect.objectContaining({
        question: 'Which mobile navigation pattern should we use?',
        selectedOptions: ['Bottom Navigation Bar']
      })
    ]);
  });

  it('enforces responsive bounded max-width on SlashCommandPalette and TeamMentionPalette', () => {
    const { rerender } = render(
      <SlashCommandPalette
        query="/mesh"
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );

    const slashPalette = screen.getByTestId('slash-command-palette');
    expect(slashPalette.className).toContain('max-w-[calc(100vw-2rem)]');

    rerender(
      <TeamMentionPalette
        members={[{ id: 'astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' }]}
        query="ast"
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );

    const mentionPalette = screen.getByTestId('team-mention-palette');
    expect(mentionPalette.className).toContain('max-w-[calc(100vw-2rem)]');
  });
});
