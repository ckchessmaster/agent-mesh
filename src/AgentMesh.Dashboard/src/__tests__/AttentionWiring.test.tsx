// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import { MUTE_STORAGE_KEY, SEEN_STORAGE_KEY, notifications } from '../services/notifications';
import type { Persona, Project, ProjectJob } from '../types';
import type { AttentionItem } from '../types/attention';
import { WorkspaceType, ProjectJobStatus } from '../types';

/**
 * End-to-end wiring of the pending-attention surface inside the mounted cockpit (task 5).
 *
 * The bell's own behaviour is covered in `components/__tests__/AttentionBell.test.tsx`; this file only
 * answers the questions that exist because the bell is mounted in `App.tsx`:
 *   - do the live `AttentionNeeded`/`AttentionResolved` broadcasts reach it,
 *   - does a click on a row actually navigate a router-less app,
 *   - is the chime unlocked by a real gesture,
 *   - and is there exactly ONE master mute switch in the header.
 *
 * Everything is asserted through the DOM or through persisted storage, never by reaching into
 * component internals, so a refactor of the bell cannot silently un-wire the app.
 */

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getAttention: vi.fn().mockResolvedValue({ items: [], count: 0, generatedAt: '2026-01-01T00:00:00.000Z' }),
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
    getJobArtifacts: vi.fn(),
    getJobArtifact: vi.fn(),
    getJobChanges: vi.fn(),
    getJobChangePatch: vi.fn(),
    postJobMessage: vi.fn(),
    uploadJobAttachments: vi.fn(),
    completeJob: vi.fn(),
    submitQuestionAnswer: vi.fn()
  }
}));

vi.mock('../auth/AuthProvider', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    isLoading: false,
    user: { profile: { email: 'admin', name: 'admin' }, access_token: 'mock-jwt-token' },
    signinRedirect: vi.fn(),
    signoutRedirect: vi.fn()
  }))
}));

const signalRHandlers: Record<string, Function> = {};

/**
 * Hoisted, not built inside the factory: `App`'s hub effect takes `connection` in its dependency list,
 * so a fresh object per render re-registers every handler on every render and the cockpit never settles.
 */
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
  useSignalR: () => ({ connection: mockSignalRConnection, connected: true })
}));

const persona: Persona = {
  id: 'rook',
  name: 'Rook',
  role: 'Software Engineer',
  personaPrompt: 'You are Rook.',
  driverId: 'agy',
  trustTier: 1,
  createdAt: new Date().toISOString()
};

const project: Project = {
  id: 'proj-mesh',
  name: 'Agent Mesh',
  description: 'Multi-agent control plane',
  workspaceType: WorkspaceType.Git,
  gitRepositoryUrl: 'https://github.com/example/agent-mesh.git',
  gitBaseBranch: 'main',
  projectInstructions: 'Keep the plumbing clean.',
  assignedPersonas: [{ id: 'rook', name: 'Rook', role: 'Software Engineer', driverId: 'agy' }],
  jobCount: 2,
  activeJobCount: 2,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const jobAlpha: ProjectJob = {
  id: 'job-alpha',
  projectId: 'proj-mesh',
  projectName: 'Agent Mesh',
  title: 'Wire the ingest retry loop',
  description: 'Backoff for the ingest worker',
  status: ProjectJobStatus.Active,
  gitBranchName: 'mesh/job-alpha-branch',
  tasks: [],
  messageCount: 0,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const jobBeta: ProjectJob = {
  id: 'job-beta',
  projectId: 'proj-mesh',
  projectName: 'Agent Mesh',
  title: 'Ship the desktop bell',
  description: 'Attention notifications for the cockpit',
  status: ProjectJobStatus.Active,
  gitBranchName: 'mesh/job-beta-branch',
  tasks: [],
  messageCount: 0,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const waitingItem = (overrides: Partial<AttentionItem> = {}): AttentionItem => ({
  id: 'job-beta:planApproval',
  type: 'planApproval',
  title: 'Plan approval needed',
  detail: 'Rook posted a plan and is parked at the approval gate',
  jobId: 'job-beta',
  jobTitle: jobBeta.title,
  projectId: 'proj-mesh',
  projectName: 'Agent Mesh',
  cardId: null,
  prUrl: null,
  prNumber: null,
  since: new Date(Date.UTC(2026, 10, 19, 9, 0, 0)).toISOString(),
  ...overrides
});

const rollupOf = (...items: AttentionItem[]) => ({
  items,
  count: items.length,
  generatedAt: new Date().toISOString()
});

const bellButton = () => screen.getByTestId('attention-bell-button');
const openBell = () => fireEvent.click(bellButton());

const pushAttentionNeeded = (item: AttentionItem) => {
  act(() => {
    signalRHandlers['AttentionNeeded']?.(item);
  });
};

const pushAttentionResolved = (jobId: string) => {
  act(() => {
    signalRHandlers['AttentionResolved']?.({ jobId, itemId: null, type: null, resolvedAt: new Date().toISOString() });
  });
};

beforeEach(() => {
  Object.keys(signalRHandlers).forEach((key) => delete signalRHandlers[key]);
  vi.clearAllMocks();
  localStorage.clear();

  vi.mocked(api.getStatus).mockResolvedValue({
    status: 'Healthy',
    timestamp: new Date().toISOString(),
    environment: 'Development',
    services: {}
  } as never);
  vi.mocked(api.getAgents).mockResolvedValue([]);
  vi.mocked(api.getPersonas).mockResolvedValue([persona]);
  vi.mocked(api.getSessions).mockResolvedValue([]);
  vi.mocked(api.getSessionMessages).mockResolvedValue([]);
  vi.mocked(api.getSessionArtifacts).mockResolvedValue([]);
  vi.mocked(api.getCapabilities).mockResolvedValue(undefined as never);
  vi.mocked(api.getAgentUsage).mockResolvedValue(undefined as never);
  vi.mocked(api.getProjects).mockResolvedValue([project]);
  vi.mocked(api.getProjectJobs).mockResolvedValue([jobAlpha, jobBeta]);
  vi.mocked(api.getProjectJob).mockResolvedValue(jobAlpha);
  vi.mocked(api.getJobTasks).mockResolvedValue([]);
  vi.mocked(api.getJobArtifacts).mockResolvedValue([]);
  vi.mocked(api.getJobChanges).mockResolvedValue({ files: [] } as never);
  vi.mocked(api.getAttention).mockResolvedValue(rollupOf());
});

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe('Attention wiring in App', () => {
  it('mounts the bell and exactly one master mute switch in the header', async () => {
    render(<App />);

    await waitFor(() => expect(screen.getByTestId('attention-bell-button')).toBeInTheDocument());
    // Two switches would mean two sources of truth for "am I being pinged?".
    expect(screen.getAllByTestId('notification-mute-toggle')).toHaveLength(1);
    expect(screen.getByTestId('notification-mute-toggle')).toHaveAttribute('aria-pressed', 'false');
  });

  it('registers the attention handlers on the live hub and unsubscribes on unmount', async () => {
    const view = render(<App />);
    await waitFor(() => expect(signalRHandlers['AttentionNeeded']).toBeTruthy());
    await waitFor(() => expect(signalRHandlers['AttentionResolved']).toBeTruthy());

    view.unmount();
    await waitFor(() => expect(signalRHandlers['AttentionNeeded']).toBeUndefined());
    expect(signalRHandlers['AttentionResolved']).toBeUndefined();
  });

  it('badges the bell from the rollup read on mount', async () => {
    vi.mocked(api.getAttention).mockResolvedValue(rollupOf(waitingItem()));
    render(<App />);

    expect(await screen.findByTestId('attention-badge')).toHaveTextContent('1');
    expect(api.getAttention).toHaveBeenCalled();
  });

  it('lights the badge the moment AttentionNeeded arrives, with no reload', async () => {
    // Nothing is pending until the hub says so. Polling is left at its 30s default, so within this
    // test's window the only thing that can light the badge is the push itself (and the single
    // reconciling read it schedules, which it would never have made on its own).
    let current: AttentionItem[] = [];
    vi.mocked(api.getAttention).mockImplementation(async () => rollupOf(...current));
    render(<App />);

    await waitFor(() => expect(bellButton()).toBeInTheDocument());
    expect(screen.queryByTestId('attention-badge')).toBeNull();

    const item = waitingItem();
    current = [item];
    pushAttentionNeeded(item);

    expect(await screen.findByTestId('attention-badge')).toHaveTextContent('1');
    openBell();
    expect(await screen.findByTestId('attention-row-job-beta:planApproval')).toBeInTheDocument();
  });

  it('drops the row and the seen key when AttentionResolved says the job is unblocked', async () => {
    vi.mocked(api.getAttention).mockResolvedValue(rollupOf(waitingItem()));
    render(<App />);

    await screen.findByTestId('attention-badge');
    // The seen-set is what keeps a still-open wait from re-banner on every poll.
    expect(window.localStorage.getItem(SEEN_STORAGE_KEY)).toContain('job-beta:planApproval');

    pushAttentionResolved('job-beta');

    await waitFor(() => expect(screen.queryByTestId('attention-badge')).toBeNull());
    expect(window.localStorage.getItem(SEEN_STORAGE_KEY)).not.toContain('job-beta:planApproval');
  });

  it('navigates the cockpit to the waiting job when a row is clicked', async () => {
    vi.mocked(api.getAttention).mockResolvedValue(rollupOf(waitingItem()));
    vi.mocked(api.getProjectJob).mockImplementation(async (id: string) =>
      id === 'job-beta' ? jobBeta : jobAlpha
    );
    render(<App />);

    await screen.findByTestId('attention-badge');
    // The wait belongs to job-beta while the cockpit is looking at some other job. Asserted on the
    // selection key, not on the branch string being absent from the DOM: the landing overview lists every
    // open job across projects, so job-beta's branch has been on screen the whole time and was always the
    // wrong thing to test.
    expect(window.localStorage.getItem('agentmesh_selected_job_id')).not.toBe('job-beta');

    openBell();
    fireEvent.click(await screen.findByTestId('attention-open-job-beta:planApproval'));

    // The service wrote the selection keys; App's listener is what actually moves the mounted cockpit.
    // `queryBy`, not `findBy`: a `findBy` would wait for the panel to appear, which is the opposite
    // of what is being asserted (the click closes it).
    expect(screen.queryByTestId('attention-panel')).toBeNull();
    expect(await screen.findByText('mesh/job-beta-branch', {}, { timeout: 4000 })).toBeInTheDocument();
    expect(window.localStorage.getItem('agentmesh_selected_job_id')).toBe('job-beta');
    expect(window.localStorage.getItem('agentmesh_active_tab')).toBe('projects');
  });

  it('unlocks the chime on the first real gesture, as the autoplay policy requires', async () => {
    const gesture = vi.spyOn(notifications, 'noteUserGesture');
    render(<App />);
    await waitFor(() => expect(bellButton()).toBeInTheDocument());
    expect(gesture).not.toHaveBeenCalled();

    fireEvent.pointerDown(document.body);
    expect(gesture).toHaveBeenCalled();
  });

  it('mutes desktop alerts from the header switch and persists it for the next load', async () => {
    vi.mocked(api.getAttention).mockResolvedValue(rollupOf(waitingItem()));
    render(<App />);

    await screen.findByTestId('attention-badge');
    const toggle = screen.getByTestId('notification-mute-toggle');
    fireEvent.click(toggle);

    expect(window.localStorage.getItem(MUTE_STORAGE_KEY)).toBe('true');
    expect(toggle).toHaveAttribute('aria-pressed', 'true');
    expect(bellButton()).toHaveAccessibleName(/desktop alerts muted/);

    // A muted bell still collects waits: mute silences the alert, it does not blind the inbox.
    openBell();
    expect(await screen.findByTestId('attention-row-job-beta:planApproval')).toBeInTheDocument();
  });

  it('keeps working when the attention endpoint is unreachable', async () => {
    vi.mocked(api.getAttention).mockRejectedValue(new Error('503 from the rollup'));
    render(<App />);

    // The bell degrades to its own error dot; the rest of the cockpit must be unaffected.
    expect(await screen.findByTestId('attention-error-dot')).toBeInTheDocument();
    expect(screen.getByText('Projects & Jobs')).toBeInTheDocument();
    // More than one status pill renders now (header and landing overview), so this is a "the app is
    // still standing" check rather than a unique-element one.
    expect(screen.getAllByText('Mesh Healthy').length).toBeGreaterThan(0);
  });
});
