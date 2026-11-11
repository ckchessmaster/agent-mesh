import { render, screen, act, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { JobCockpit } from '../JobCockpit';
import { ProjectJobStatus, type Project, type ProjectJob, type JobMessage } from '../../types';
import type { JobTimelineCard } from '../../types/timeline';

const mockCards: JobTimelineCard[] = [
  {
    id: 'card-1',
    jobId: 'job-1',
    cardType: 'JobInitialized',
    status: 'Resolved',
    title: 'Job Initialized',
    structuredPayloadJson: '{}',
    comments: [],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  },
  {
    id: 'card-2',
    jobId: 'job-1',
    cardType: 'DiscoveryQa',
    status: 'Active',
    title: 'Discovery Questions',
    structuredPayloadJson: '{}',
    comments: [],
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  }
];

vi.mock('../../services/api', () => ({
  api: {
    getTimelineCards: vi.fn().mockImplementation(async () => mockCards),
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

const mockProject: Project = {
  id: 'agent-mesh',
  name: 'Agent Mesh',
  description: '',
  workspaceType: 0,
  gitBaseBranch: 'main',
  projectInstructions: '',
  defaultLeadPersonaId: 'astra',
  defaultLeadPersonaName: 'Astra',
  assignedPersonas: [{ id: 'astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' }],
  jobCount: 1,
  activeJobCount: 1,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const makeJob = (id = 'job-1'): ProjectJob => ({
  id,
  projectId: 'agent-mesh',
  projectName: 'Agent Mesh',
  title: 'Mobile Timeline Feed',
  description: '',
  status: ProjectJobStatus.Active,
  gitBranchName: `mesh/${id}`,
  tasks: [],
  messageCount: 0,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
});

const baseProps = {
  project: mockProject,
  tasks: [],
  personas: [],
  sessions: [],
  messages: [] as JobMessage[],
  onSendMessage: vi.fn().mockResolvedValue(undefined),
  onAddTask: vi.fn().mockResolvedValue(undefined),
  onToggleTask: vi.fn().mockResolvedValue(undefined),
  onDeleteTask: vi.fn().mockResolvedValue(undefined),
  onCompleteJob: vi.fn().mockResolvedValue(undefined)
};

describe('JobCockpit - mobile layout & timeline feed', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      }
    );
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('renders the timeline cockpit container and layout on mobile', async () => {
    render(<JobCockpit {...baseProps} job={makeJob('job-1')} />);

    expect(screen.getByTestId('timeline-cockpit-container')).toBeInTheDocument();
    expect(screen.getByTestId('cockpit-layout')).toBeInTheDocument();
    expect(screen.getByTestId('sticky-sidebar')).toBeInTheDocument();

    const feed = await screen.findByTestId('timeline-feed');
    expect(feed).toBeInTheDocument();
  });

  it('updates feed cards when navigating between jobs on mobile', async () => {
    const { rerender } = render(<JobCockpit {...baseProps} job={makeJob('job-1')} />);

    const feed1 = await screen.findByTestId('timeline-feed');
    expect(feed1).toBeInTheDocument();

    // Rerender with different job
    rerender(<JobCockpit {...baseProps} job={makeJob('job-2')} />);

    const feed2 = await screen.findByTestId('timeline-feed');
    expect(feed2).toBeInTheDocument();
  });

  it('handles window resize events cleanly without tearing down the timeline feed', async () => {
    render(<JobCockpit {...baseProps} job={makeJob('job-1')} />);

    const feed = await screen.findByTestId('timeline-feed');
    expect(feed).toBeInTheDocument();

    act(() => {
      window.dispatchEvent(new Event('resize'));
    });

    expect(screen.getByTestId('timeline-feed')).toBeInTheDocument();
  });
});
