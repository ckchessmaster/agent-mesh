import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { JobCockpit } from '../JobCockpit';
import type { JobTask, Project, ProjectJob } from '../../types';
import { ProjectJobStatus, PlanApprovalPolicy, JobLifecycleStatus } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    approvePlan: vi.fn(),
    uploadJobAttachments: vi.fn(),
    deleteTaskAttachment: vi.fn(),
    getTimelineCards: vi.fn().mockResolvedValue([])
  }
}));

vi.mock('../../hooks/useSignalR', () => ({
  useSignalR: () => ({
    connection: {
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn().mockResolvedValue(undefined),
      stop: vi.fn().mockResolvedValue(undefined)
    },
    connected: false
  })
}));

describe('JobCockpit - Living Checklist & Autonomous Lifecycle Gates', () => {
  const mockProject = {
    id: 'proj-1',
    name: 'Agent Mesh',
    description: 'Autonomous mesh',
    workspaceType: 0,
    gitBaseBranch: 'main',
    projectInstructions: '',
    activeBranch: 'main',
    gitStatusSummary: 'clean',
    team: [{ personaId: 'astra', personaName: 'Astra', role: 'Tech Lead', color: 'sky' }]
  } as unknown as Project;

  const mockJob: ProjectJob = {
    id: 'job-autonomous-1',
    projectId: 'proj-1',
    title: 'Autonomous Feature Job',
    description: 'Implement autonomous lifecycle',
    status: ProjectJobStatus.Active,
    lifecycleStatus: JobLifecycleStatus.InProgress,
    approvalPolicy: PlanApprovalPolicy.LeadOrHuman,
    gitBranchName: 'feat/autonomous-lifecycle',
    currentTaskId: 'task-2',
    progressHeartbeat: 'Configuring temporal approval gate and signals.',
    tasks: [],
    messageCount: 0,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockTasks: JobTask[] = [
    { id: 'task-1', title: 'Task 1: Inspect repository', isCompleted: true, status: 'completed' },
    { id: 'task-2', title: 'Task 2: Configure temporal workflow', isCompleted: false, status: 'in_progress' },
    { id: 'task-3', title: 'Task 3: Verify with unit tests', isCompleted: false, status: 'todo' }
  ];

  const baseProps = {
    project: mockProject,
    job: mockJob,
    tasks: mockTasks,
    messages: [],
    onSendMessage: vi.fn(),
    onAddTask: vi.fn(),
    onToggleTask: vi.fn(),
    onDeleteTask: vi.fn(),
    onCompleteJob: vi.fn()
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders living checklist with tasks and status indicators in StickySidebar', () => {
    render(<JobCockpit {...baseProps} />);

    // Living checklist container in StickySidebar
    const checklist = screen.getByTestId('sidebar-checklist');
    expect(checklist).toBeInTheDocument();

    // All tasks from mockTasks are rendered in the living checklist
    expect(screen.getByText(/Task 1: Inspect repository/i)).toBeInTheDocument();
    expect(screen.getByText(/Task 2: Configure temporal workflow/i)).toBeInTheDocument();
    expect(screen.getByText(/Task 3: Verify with unit tests/i)).toBeInTheDocument();

    // Task items have checklist-item testids
    expect(screen.getByTestId('checklist-item-task-1')).toBeInTheDocument();
    expect(screen.getByTestId('checklist-item-task-2')).toBeInTheDocument();
    expect(screen.getByTestId('checklist-item-task-3')).toBeInTheDocument();
  });

  it('renders living checklist with heartbeat note beneath active agent profile', () => {
    render(<JobCockpit {...baseProps} />);

    // Progress heartbeat note rendered in StickySidebar
    const heartbeatNote = screen.getByTestId('sidebar-heartbeat');
    expect(heartbeatNote).toBeInTheDocument();
    expect(heartbeatNote).toHaveTextContent('Configuring temporal approval gate and signals.');
  });

  it('renders Temporal Health metrics and Emergency Stop Turn button in StickySidebar', () => {
    render(<JobCockpit {...baseProps} />);

    // Temporal Health section is rendered
    expect(screen.getByText(/Temporal Health/i)).toBeInTheDocument();
    expect(screen.getByText('Workflow:')).toBeInTheDocument();

    // Stop active turn button is rendered
    expect(screen.getByTestId('stop-active-turn-btn')).toBeInTheDocument();
    expect(screen.getByTestId('stop-active-turn-btn')).toHaveTextContent(/Stop Active Turn/i);
  });
});
