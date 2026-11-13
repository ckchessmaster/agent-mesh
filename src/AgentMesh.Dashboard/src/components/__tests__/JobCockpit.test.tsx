import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { JobCockpit, getActiveMentionQuery, resolveTargetTaskId } from '../JobCockpit';
import { ProjectJobStatus, type Project, type ProjectJob, type JobTask, type Persona } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getTimelineCards: vi.fn().mockResolvedValue([]),
    uploadJobAttachments: vi.fn(),
    uploadTaskAttachments: vi.fn(),
    deleteTaskAttachment: vi.fn()
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

describe('getActiveMentionQuery helper', () => {
  it('detects mention query at beginning of string', () => {
    expect(getActiveMentionQuery('@', 1)).toEqual({ query: '', startIndex: 0 });
    expect(getActiveMentionQuery('@ast', 4)).toEqual({ query: 'ast', startIndex: 0 });
  });

  it('detects mention query after whitespace', () => {
    expect(getActiveMentionQuery('Hello @', 7)).toEqual({ query: '', startIndex: 6 });
    expect(getActiveMentionQuery('Hello @sor', 10)).toEqual({ query: 'sor', startIndex: 6 });
    expect(getActiveMentionQuery('Line 1\n@claude', 14)).toEqual({ query: 'claude', startIndex: 7 });
  });

  it('ignores email addresses where @ is preceded by a non-whitespace character', () => {
    expect(getActiveMentionQuery('contact@example.com', 20)).toBeNull();
    expect(getActiveMentionQuery('user@domain', 11)).toBeNull();
  });

  it('returns null if there is whitespace after the @ before the cursor', () => {
    expect(getActiveMentionQuery('@astra please test', 18)).toBeNull();
  });

  it('detects the active mention when multiple mentions exist', () => {
    const text = '@astra and @qa';
    expect(getActiveMentionQuery(text, text.length)).toEqual({ query: 'qa', startIndex: 11 });
  });
});

describe('resolveTargetTaskId helper', () => {
  const sampleTasks: JobTask[] = [
    { id: 'task-1', title: 'Task 1', isCompleted: true, assignedPersonaId: 'astra' },
    { id: 'task-2', title: 'Task 2', isCompleted: false, assignedPersonaId: 'soren' },
    { id: 'task-3', title: 'Task 3', isCompleted: false, assignedPersonaId: 'soren' },
    { id: 'task-4', title: 'Task 4', isCompleted: false }
  ];

  it('finds the first uncompleted task assigned to the persona', () => {
    expect(resolveTargetTaskId(sampleTasks, 'soren')).toBe('task-2');
  });

  it('falls back to an unowned uncompleted task if none assigned to persona', () => {
    expect(resolveTargetTaskId(sampleTasks, 'qa-tester')).toBe('task-4');
  });

  it('returns null if tasks array is empty', () => {
    expect(resolveTargetTaskId([], 'soren')).toBeNull();
  });
});

describe('JobCockpit - Header, Status, and Timeline Workspace', () => {
  const mockProject: Project = {
    id: 'agent-mesh',
    name: 'Agent Mesh',
    description: '',
    workspaceType: 0,
    gitBaseBranch: 'main',
    projectInstructions: '',
    defaultLeadPersonaId: 'agy-astra',
    defaultLeadPersonaName: 'Astra',
    assignedPersonas: [
      { id: 'agy-astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' },
      { id: 'agy-soren', name: 'Soren', role: 'Codebase Engineer', driverId: 'agy' }
    ],
    jobCount: 1,
    activeJobCount: 1,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockPersonas: Persona[] = [
    { id: 'agy-astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy', personaPrompt: '', trustTier: 2, createdAt: new Date().toISOString() },
    { id: 'agy-soren', name: 'Soren', role: 'Codebase Engineer', driverId: 'agy', personaPrompt: '', trustTier: 2, createdAt: new Date().toISOString() }
  ];

  const mockJob: ProjectJob = {
    id: 'job-1',
    projectId: 'agent-mesh',
    projectName: 'Agent Mesh',
    title: 'Enhance Timeline Cockpit',
    description: '',
    status: ProjectJobStatus.Active,
    gitBranchName: 'mesh/job-1',
    priority: 2,
    tasks: [
      { id: 't-1', title: 'Task 1', isCompleted: true },
      { id: 't-2', title: 'Task 2', isCompleted: false }
    ],
    messageCount: 0,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockProps = {
    project: mockProject,
    job: mockJob,
    tasks: mockJob.tasks,
    personas: mockPersonas,
    onAddTask: vi.fn(),
    onToggleTask: vi.fn(),
    onDeleteTask: vi.fn(),
    onCompleteJob: vi.fn(),
    onArchiveJob: vi.fn(),
    onResetJob: vi.fn(),
    onStartJob: vi.fn(),
    onUpdateJob: vi.fn()
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders header bar with project name, branch, active status, priority, and title', () => {
    render(<JobCockpit {...mockProps} />);

    expect(screen.getByText('Agent Mesh')).toBeInTheDocument();
    expect(screen.getByText('mesh/job-1')).toBeInTheDocument();
    expect(screen.getByTestId('job-status-active')).toHaveTextContent('Active Job');
    expect(screen.getByText('Enhance Timeline Cockpit')).toBeInTheDocument();
    expect(screen.getByTestId('job-priority-badge')).toHaveTextContent('High');
  });

  it('renders Timeline Cockpit container hosting CockpitLayout and StickySidebar', () => {
    render(<JobCockpit {...mockProps} />);

    expect(screen.getByTestId('timeline-cockpit-container')).toBeInTheDocument();
    expect(screen.getByTestId('cockpit-layout')).toBeInTheDocument();
    expect(screen.getByTestId('sticky-sidebar')).toBeInTheDocument();
  });

  it('renders pending status badge when job is pending start', () => {
    const pendingJob: ProjectJob = {
      ...mockJob,
      status: ProjectJobStatus.Pending
    };

    render(<JobCockpit {...mockProps} job={pendingJob} />);

    expect(screen.getByTestId('job-status-pending')).toHaveTextContent('Pending Start');
    // Start Job button visible in header when pending
    expect(screen.getByTestId('cockpit-start-job-btn')).toBeInTheDocument();
  });

  it('triggers onStartJob when Start Job button is clicked in header', async () => {
    const onStart = vi.fn().mockResolvedValue(undefined);
    const pendingJob: ProjectJob = {
      ...mockJob,
      status: ProjectJobStatus.Pending
    };

    render(<JobCockpit {...mockProps} job={pendingJob} onStartJob={onStart} />);

    const startBtn = screen.getByTestId('cockpit-start-job-btn');
    fireEvent.click(startBtn);

    await waitFor(() => {
      expect(onStart).toHaveBeenCalledTimes(1);
    });
  });

  it('shows confirmation and calls onCompleteJob when Complete Job is confirmed', async () => {
    const onComplete = vi.fn().mockResolvedValue(undefined);
    render(<JobCockpit {...mockProps} onCompleteJob={onComplete} />);

    const completeBtn = screen.getByTestId('cockpit-complete-job-btn');
    fireEvent.click(completeBtn);

    // Confirmation input and button appear
    expect(screen.getByTestId('cockpit-confirm-complete-btn')).toBeInTheDocument();

    const summaryInput = screen.getByTestId('deliverables-summary-input');
    fireEvent.change(summaryInput, { target: { value: 'All tasks completed successfully' } });

    fireEvent.click(screen.getByTestId('cockpit-confirm-complete-btn'));

    await waitFor(() => {
      expect(onComplete).toHaveBeenCalledWith('All tasks completed successfully');
    });
  });

  it('shows confirmation and calls onArchiveJob when Archive is confirmed', async () => {
    const onArchive = vi.fn().mockResolvedValue(undefined);
    render(<JobCockpit {...mockProps} onArchiveJob={onArchive} />);

    const archiveBtn = screen.getByTestId('cockpit-archive-job-btn');
    fireEvent.click(archiveBtn);

    const confirmArchiveBtn = screen.getByTestId('cockpit-confirm-archive-btn');
    expect(confirmArchiveBtn).toBeInTheDocument();

    fireEvent.click(confirmArchiveBtn);

    await waitFor(() => {
      expect(onArchive).toHaveBeenCalledTimes(1);
    });
  });

  it('shows confirmation and calls onResetJob when Reset Job is confirmed', async () => {
    const onReset = vi.fn().mockResolvedValue(undefined);
    render(<JobCockpit {...mockProps} onResetJob={onReset} />);

    const resetBtn = screen.getByTestId('cockpit-reset-job-btn');
    fireEvent.click(resetBtn);

    const confirmResetBtn = screen.getByTestId('cockpit-confirm-reset-btn');
    expect(confirmResetBtn).toBeInTheDocument();

    fireEvent.click(confirmResetBtn);

    await waitFor(() => {
      expect(onReset).toHaveBeenCalledTimes(1);
    });
  });

  it('allows changing the assigned agent via header dropdown', async () => {
    const onUpdate = vi.fn().mockResolvedValue(undefined);
    render(<JobCockpit {...mockProps} onUpdateJob={onUpdate} />);

    const assigneeSelect = screen.getByTestId('cockpit-assigned-agent-select');
    fireEvent.change(assigneeSelect, { target: { value: 'agy-soren' } });

    await waitFor(() => {
      expect(onUpdate).toHaveBeenCalledWith({ assignedAgentId: 'agy-soren' });
    });
  });

  it('renders archived job state with reopen option', () => {
    const archivedJob: ProjectJob = {
      ...mockJob,
      status: ProjectJobStatus.Archived
    };

    render(<JobCockpit {...mockProps} job={archivedJob} onReopenJob={vi.fn()} />);

    expect(screen.getAllByText('Archived').length).toBeGreaterThan(0);
    expect(screen.getByTestId('cockpit-reopen-job-btn')).toBeInTheDocument();
  });
});
