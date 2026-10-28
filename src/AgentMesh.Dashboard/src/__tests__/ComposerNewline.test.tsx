import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type {
  Agent,
  AgentSession,
  JobMessage,
  Project,
  ProjectJob,
  SessionMessage,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from '../types';
import { ProjectJobStatus, WorkspaceType } from '../types';

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getStatus: vi.fn(),
    getCapabilities: vi.fn(),
    getAgents: vi.fn(),
    getPersonas: vi.fn(),
    getSessions: vi.fn(),
    getSession: vi.fn(),
    getSessionMessages: vi.fn(),
    getSessionArtifacts: vi.fn(),
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
    createAgentSession: vi.fn(),
    deleteSession: vi.fn(),
    postSessionMessage: vi.fn(),
    uploadSessionAttachments: vi.fn(),
    stopSession: vi.fn(),
    stopCommand: vi.fn(),
    enqueuePrompt: vi.fn(),
    dequeuePrompt: vi.fn(),
    sendQueuedPromptNow: vi.fn(),
    submitQuestionAnswer: vi.fn(),
    resetMesh: vi.fn()
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

const healthyStatus: SystemStatusResponse = {
  status: 'Healthy',
  timestamp: new Date().toISOString(),
  environment: 'Development',
  services: { api: { status: 'Healthy', latencyMs: 1, description: 'api' } }
};

const caps = {
  agentRuntimes: {}
} as unknown as SystemCapabilitiesResponse;

const mockAgent: Agent = {
  id: 'agy-core',
  name: 'Core Agent',
  status: 'online',
  runtimeType: 'CliAntigravity',
  trustTier: 1,
  createdAt: new Date().toISOString()
};

// creatorAgentId matches mockAgent.id so the session renders under the Drivers tab,
// which is the tab App auto-selects when there are no projects and no personas.
const mockSession: AgentSession = {
  id: 'session-nl-1',
  title: 'Newline Session',
  status: 'active',
  creatorAgentId: 'agy-core',
  metadataJson: JSON.stringify({}),
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const mockProject: Project = {
  id: 'proj-nl-1',
  name: 'Newline Project',
  description: 'Project for composer newline coverage',
  workspaceType: WorkspaceType.Git,
  gitRepositoryUrl: 'https://github.com/acme/nl.git',
  gitBaseBranch: 'main',
  projectInstructions: '',
  assignedPersonas: [],
  jobCount: 1,
  activeJobCount: 1,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const mockJob: ProjectJob = {
  id: 'job-nl-1',
  projectId: 'proj-nl-1',
  projectName: 'Newline Project',
  title: 'Newline Job',
  description: '',
  status: ProjectJobStatus.Active,
  gitBranchName: 'mesh/job-nl-1',
  tasks: [],
  messageCount: 0,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const createdMessage: SessionMessage = {
  id: 'msg-nl-1',
  sessionId: 'session-nl-1',
  senderAgentId: 'admin',
  eventType: 'prompt',
  content: 'line one',
  createdAt: new Date().toISOString()
};

const createdJobMessage: JobMessage = {
  id: 'jm-1',
  jobId: 'job-nl-1',
  content: '',
  senderType: 'User',
  senderId: 'admin',
  senderName: 'admin',
  mentions: [],
  createdAt: new Date().toISOString()
};

const SESSION_COMPOSER = /Send instruction to/i;
const JOB_COMPOSER = /Type your message, tasks, or delegate with @persona/i;

/**
 * `fireEvent.keyDown` returns `false` when a handler called preventDefault.
 * That is the honest way to assert keyboard semantics here: whether the
 * browser's default action (inserting a newline) survives our handler.
 * Actual "\n" insertion is native textarea behaviour, not our code, so we
 * assert (a) the element is a textarea and (b) we do not cancel the default.
 */
async function openComposer(placeholder: RegExp, label: string) {
  render(<App />);
  await waitFor(() => {
    expect(screen.getAllByText(label).length).toBeGreaterThan(0);
  });
  fireEvent.click(screen.getAllByText(label)[0]);
  await waitFor(() => {
    expect(screen.getByPlaceholderText(placeholder)).toBeInTheDocument();
  });
  return screen.getByPlaceholderText(placeholder);
}

describe('Session chat composer: shift+Enter inserts a newline', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    vi.mocked(api.getStatus).mockResolvedValue(healthyStatus);
    vi.mocked(api.getCapabilities).mockResolvedValue(caps);
    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getPersonas).mockResolvedValue([]);
    // No projects: App auto-selects the Drivers tab, where the session is listed.
    vi.mocked(api.getProjects).mockResolvedValue([]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
    vi.mocked(api.getSessionArtifacts).mockResolvedValue([]);
    vi.mocked(api.postSessionMessage).mockResolvedValue(createdMessage);
  });

  it('is a multiline-capable textarea, not a single-line input', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');
    // <input type=text> physically cannot hold "\n" — the browser strips it.
    // That is the root cause of the bug report, so it is asserted directly.
    expect(composer.tagName).toBe('TEXTAREA');
  });

  it('shift+Enter does not send and leaves the default newline insertion intact', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');

    fireEvent.change(composer, { target: { value: 'line one' } });
    const notCancelled = fireEvent.keyDown(composer, { key: 'Enter', shiftKey: true });

    expect(notCancelled).toBe(true);
    expect(api.postSessionMessage).not.toHaveBeenCalled();
  });

  it('plain Enter sends and cancels the default (a textarea cannot rely on implicit form submit)', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');

    fireEvent.change(composer, { target: { value: 'do the thing' } });
    const notCancelled = fireEvent.keyDown(composer, { key: 'Enter', shiftKey: false });

    expect(notCancelled).toBe(false);
    await waitFor(() => {
      expect(api.postSessionMessage).toHaveBeenCalledTimes(1);
    });
    const [sessionId, content] = vi.mocked(api.postSessionMessage).mock.calls[0];
    expect(sessionId).toBe('session-nl-1');
    expect(content).toBe('do the thing');
  });

  it('sends a multi-line draft with its newlines intact', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');

    fireEvent.change(composer, { target: { value: 'step 1\nstep 2' } });
    fireEvent.keyDown(composer, { key: 'Enter', shiftKey: false });

    await waitFor(() => {
      expect(api.postSessionMessage).toHaveBeenCalledTimes(1);
    });
    expect(vi.mocked(api.postSessionMessage).mock.calls[0][1]).toBe('step 1\nstep 2');
  });

  it('Enter selects a highlighted slash command instead of sending', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');

    // '/grill' is a partial query: the highlighted '/grill-me' differs from the
    // typed text, so the palette claims Enter. (A query that exactly equals a
    // command falls through to send — pre-existing behaviour, unchanged here.)
    fireEvent.change(composer, { target: { value: '/grill' } });
    await waitFor(() => {
      expect(screen.getByTestId('slash-command-palette')).toBeInTheDocument();
    });

    fireEvent.keyDown(composer, { key: 'Enter', shiftKey: false });

    // The palette owns Enter while open; the prompt must not be posted.
    expect(api.postSessionMessage).not.toHaveBeenCalled();
    expect((composer as HTMLTextAreaElement).value).toBe('/grill-me ');
  });

  it('shift+Enter does not send while the slash palette is open', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');

    fireEvent.change(composer, { target: { value: '/grill' } });
    await waitFor(() => {
      expect(screen.getByTestId('slash-command-palette')).toBeInTheDocument();
    });

    fireEvent.keyDown(composer, { key: 'Enter', shiftKey: true });
    expect(api.postSessionMessage).not.toHaveBeenCalled();
  });

  it('keeps the composer a textarea after a send, so multi-line drafting survives', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');

    fireEvent.change(composer, { target: { value: 'reset me' } });
    fireEvent.keyDown(composer, { key: 'Enter', shiftKey: false });

    await waitFor(() => {
      expect(api.postSessionMessage).toHaveBeenCalledTimes(1);
    });
    const after = screen.getByPlaceholderText(SESSION_COMPOSER);
    expect(after.tagName).toBe('TEXTAREA');
    expect((after as HTMLTextAreaElement).value).toBe('');
  });

  // Regression guard for this change. The old <input> sent via *implicit form
  // submission*, which browsers suppress while an IME composition is open (so
  // pressing Enter to accept a CJK candidate never sent a half-finished prompt).
  // A <textarea> has no implicit submit, so Enter is handled explicitly here —
  // which means the composition guard has to be explicit too.
  it('does not send when Enter accepts an IME composition', async () => {
    const composer = await openComposer(SESSION_COMPOSER, 'Newline Session');

    fireEvent.change(composer, { target: { value: '日本' } });
    const notCancelled = fireEvent.keyDown(composer, {
      key: 'Enter',
      shiftKey: false,
      isComposing: true
    });

    expect(api.postSessionMessage).not.toHaveBeenCalled();
    // Default left intact so the IME can still commit its candidate.
    expect(notCancelled).toBe(true);
  });
});

describe('Project chat composer: shift+Enter keeps working (regression guard)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    vi.mocked(api.getStatus).mockResolvedValue(healthyStatus);
    vi.mocked(api.getCapabilities).mockResolvedValue(caps);
    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getPersonas).mockResolvedValue([]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getProjects).mockResolvedValue([mockProject]);
    vi.mocked(api.getProjectJobs).mockResolvedValue([mockJob]);
    vi.mocked(api.getJobMessages).mockResolvedValue([]);
    vi.mocked(api.getJobTasks).mockResolvedValue([]);
    vi.mocked(api.postJobMessage).mockResolvedValue(createdJobMessage);
  });

  it('shift+Enter does not send and leaves the default newline insertion intact', async () => {
    const composer = await openComposer(JOB_COMPOSER, 'Newline Job');

    fireEvent.change(composer, { target: { value: 'plan it' } });
    const notCancelled = fireEvent.keyDown(composer, { key: 'Enter', shiftKey: true });

    expect(notCancelled).toBe(true);
    expect(api.postJobMessage).not.toHaveBeenCalled();
  });

  it('plain Enter still sends', async () => {
    const composer = await openComposer(JOB_COMPOSER, 'Newline Job');

    fireEvent.change(composer, { target: { value: 'ship it' } });
    fireEvent.keyDown(composer, { key: 'Enter', shiftKey: false });

    await waitFor(() => {
      expect(api.postJobMessage).toHaveBeenCalledTimes(1);
    });
  });
});
