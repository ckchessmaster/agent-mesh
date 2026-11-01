import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { JobCockpit } from '../JobCockpit';
import { api } from '../../services/api';
import type { JobTask, Project, ProjectJob } from '../../types';
import { PlanApprovalPolicy, JobLifecycleStatus } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    approvePlan: vi.fn(),
    uploadJobAttachments: vi.fn(),
    deleteTaskAttachment: vi.fn(),
  }
}));

describe('JobCockpit - Living Checklist & Autonomous Lifecycle Gates', () => {
  const mockProject: Project = {
    id: 'proj-1',
    name: 'Agent Mesh',
    gitRepoPath: '/workspace',
    activeBranch: 'main',
    gitStatusSummary: 'clean',
    team: [{ personaId: 'astra', personaName: 'Astra', role: 'Tech Lead', color: 'sky' }]
  };

  const mockJob: ProjectJob = {
    id: 'job-autonomous-1',
    projectId: 'proj-1',
    title: 'Autonomous Feature Job',
    description: 'Implement autonomous lifecycle',
    status: 'active',
    lifecycleStatus: JobLifecycleStatus.InProgress,
    approvalPolicy: PlanApprovalPolicy.LeadOrHuman,
    gitBranchName: 'feat/autonomous-lifecycle',
    currentTaskId: 'task-2',
    progressHeartbeat: 'Configuring temporal approval gate and signals.',
    tasks: [],
    messageCount: 0,
    activeTurnSessionId: null
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

  it('renders deterministic progress meter and hides manual add task form for autonomous job', () => {
    render(<JobCockpit {...baseProps} />);

    // Deterministic progress meter: Task 2 of 3 (33% complete)
    const meter = screen.getByTestId('deterministic-progress-meter');
    expect(meter).toBeInTheDocument();
    expect(meter).toHaveTextContent(/Task 2 of 3/);

    // Manual add task form should NOT be rendered for autonomous job
    expect(screen.queryByTestId('new-task-input')).toBeNull();
  });

  it('renders living checklist with in-progress indicators and heartbeat note beneath active task', () => {
    render(<JobCockpit {...baseProps} />);

    // Progress heartbeat note rendered beneath active task indicator
    const heartbeatNotes = screen.getAllByTestId('task-heartbeat-note');
    expect(heartbeatNotes.length).toBeGreaterThan(0);
    expect(heartbeatNotes[0]).toHaveTextContent('Configuring temporal approval gate and signals.');

    // Task 2 has in-progress indicator / spinner
    expect(screen.getByTestId('task-progress-indicator-task-2')).toBeInTheDocument();
  });

  it('renders Plan Approval Action Bar during AwaitingApproval stage and triggers approvePlan API', async () => {
    const awaitingApprovalJob: ProjectJob = {
      ...mockJob,
      status: 'awaiting_approval',
      lifecycleStatus: JobLifecycleStatus.AwaitingApproval,
      approvalPolicy: PlanApprovalPolicy.LeadOrHuman
    };

    vi.mocked(api.approvePlan).mockResolvedValue({ success: true, approverRole: 'operator' });

    render(<JobCockpit {...baseProps} job={awaitingApprovalJob} />);

    // Plan Approval Action Bar rendered
    expect(screen.getByTestId('plan-approval-action-bar')).toBeInTheDocument();
    expect(screen.getByText(/Plan Approval Gate Required/i)).toBeInTheDocument();
    expect(screen.getByText(/Lead or Human/i)).toBeInTheDocument();

    // Click Approve Plan button
    const approveBtn = screen.getByTestId('approve-plan-btn');
    fireEvent.click(approveBtn);

    await waitFor(() => {
      expect(api.approvePlan).toHaveBeenCalledWith('job-autonomous-1', 'operator');
    });
  });

  it('enforces HumanOnly approval policy on Plan Approval button when user is not operator', () => {
    const humanOnlyJob: ProjectJob = {
      ...mockJob,
      status: 'awaiting_approval',
      lifecycleStatus: JobLifecycleStatus.AwaitingApproval,
      approvalPolicy: PlanApprovalPolicy.HumanOnly
    };

    // User acting as tech_lead (non-operator)
    render(<JobCockpit {...baseProps} job={humanOnlyJob} userRole="tech_lead" />);

    expect(screen.getByTestId('plan-approval-action-bar')).toBeInTheDocument();
    expect(screen.getByText(/Human Only/i)).toBeInTheDocument();

    const approveBtn = screen.getByTestId('approve-plan-btn');
    expect(approveBtn).toBeDisabled();
  });
});
