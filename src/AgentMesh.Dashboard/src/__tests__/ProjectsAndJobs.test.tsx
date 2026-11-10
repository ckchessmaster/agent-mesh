// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within, act } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type { Persona, Project, ProjectJob, JobTask, JobMessage, AgentSession, JobArtifact } from '../types';
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
    // Job artifacts. Declared here because this file replaces the whole api module: without it,
    // api.getJobArtifacts is undefined and App's fetch throws into its catch, so every job would render
    // "Artifacts (0)" and the tests below could not tell a broken panel from an unmocked one.
    getJobArtifacts: vi.fn(),
    getJobArtifact: vi.fn(),
    // Job file changes. Declared for the same reason as the two above: this file replaces the whole api
    // module, and without these App's Changes tab would see `undefined` and report a transport fault
    // rather than the state the test is actually asserting.
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
    user: {
      profile: {
        email: 'admin',
        name: 'admin'
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

describe('Projects & Multi-Persona Jobs Workflow', () => {
  const mockAstraPersona: Persona = {
    id: 'astra',
    name: 'Astra',
    role: 'Tech Lead',
    personaPrompt: 'You are Astra, Engineering Tech Lead.',
    driverId: 'agy',
    trustTier: 2,
    createdAt: new Date().toISOString()
  };

  const mockQaPersona: Persona = {
    id: 'qa-tester',
    name: 'QA Tester',
    role: 'Quality Engineer',
    personaPrompt: 'You are QA Tester.',
    driverId: 'agy',
    trustTier: 1,
    createdAt: new Date().toISOString()
  };

  const mockProject: Project = {
    id: 'mesh-core',
    name: 'Mesh Core System',
    description: 'Autonomous multi-agent platform',
    workspaceType: WorkspaceType.Git,
    gitRepositoryUrl: 'https://github.com/example/mesh.git',
    gitBaseBranch: 'main',
    projectInstructions: 'Maintain clean architecture and tests.',
    defaultLeadPersonaId: 'astra',
    defaultLeadPersonaName: 'Astra',
    assignedPersonas: [
      { id: 'astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' },
      { id: 'qa-tester', name: 'QA Tester', role: 'Quality Engineer', driverId: 'agy' }
    ],
    jobCount: 1,
    activeJobCount: 1,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockTasks: JobTask[] = [
    { id: 'task-1', title: 'Implement OAuth endpoints', isCompleted: true, completedAt: new Date().toISOString() },
    { id: 'task-2', title: 'Write integration test coverage', isCompleted: false }
  ];

  const mockJob: ProjectJob = {
    id: 'job-101',
    projectId: 'mesh-core',
    projectName: 'Mesh Core System',
    title: 'Implement OAuth Provider',
    description: 'Add Google & GitHub OAuth handlers',
    status: ProjectJobStatus.Active,
    gitBranchName: 'mesh/job-auth-oauth',
    tasks: mockTasks,
    messageCount: 2,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockMessages: JobMessage[] = [
    {
      id: 'm-1',
      jobId: 'job-101',
      senderType: 'user',
      content: '@astra please implement the OAuth endpoints',
      mentions: ['astra'],
      createdAt: new Date().toISOString()
    },
    {
      id: 'm-2',
      jobId: 'job-101',
      senderType: 'persona',
      senderId: 'astra',
      senderName: 'Astra',
      content: 'I have finished the OAuth controller. @qa-tester please test token exchange edge cases.',
      mentions: ['qa-tester'],
      turnSessionId: 'sess-turn-456',
      createdAt: new Date().toISOString()
    }
  ];

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getStatus).mockResolvedValue({
      status: 'Healthy',
      timestamp: new Date().toISOString(),
      environment: 'Development',
      services: {
        api: { status: 'Healthy', latencyMs: 2, description: 'ASP.NET Core 10 Web API' }
      }
    });
    vi.mocked(api.getAgents).mockResolvedValue([]);
    vi.mocked(api.getPersonas).mockResolvedValue([mockAstraPersona, mockQaPersona]);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.getCapabilities).mockResolvedValue({
      workspace: {
        defaultSandboxPath: '~/.agentmesh/workspaces/<session-id>',
        currentDirectory: '/workspace/agent-mesh',
        userHome: '/home/testuser',
        bookmarks: [
          { label: 'Default Sandbox', path: '', description: 'Sandbox' },
          { label: 'Current Project', path: '/workspace/agent-mesh', description: 'Project' },
          { label: 'User Home', path: '/home/testuser', description: 'Home' }
        ]
      },
      agentRuntimes: {
        agy: {
          adapter: 'agy',
          name: 'Antigravity CLI (agy)',
          modes: [
            { id: 'auto', label: 'Auto (Autonomous)', description: 'Auto execution' },
            { id: 'plan', label: 'Plan Only', description: 'Plan only' }
          ],
          models: [
            { id: 'gemini-4-argon', name: 'Gemini 4 Argon', efforts: ['low', 'medium', 'high'] }
          ]
        }
      }
    } as any);
    vi.mocked(api.getProjects).mockResolvedValue([mockProject]);
    vi.mocked(api.getProjectJobs).mockResolvedValue([mockJob]);
    vi.mocked(api.getJobTasks).mockResolvedValue(mockTasks);
    vi.mocked(api.getJobMessages).mockResolvedValue(mockMessages);
    // Default: the job has no artifacts. Tests that exercise the panel override this.
    vi.mocked(api.getJobArtifacts).mockResolvedValue([]);
    vi.mocked(api.getJobArtifact).mockResolvedValue({} as any);
    // Default: no readable changes. Tests that exercise the Changes tab override this — and must not
    // depend on the artifacts default, since opening one tab must not fetch the other.
    vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet({ files: [] }));
    vi.mocked(api.getJobChangePatch).mockResolvedValue({ found: false, message: 'no patch in default mock' });
  });

  it('renders Projects & Jobs tab and auto-selects project and job into the Job Cockpit', async () => {
    render(<App />);

    // Top tab button exists and indicates projects count
    await waitFor(() => {
      expect(screen.getByText('Projects & Jobs')).toBeInTheDocument();
    });

    // Project name in sidebar
    await waitFor(() => {
      expect(screen.getAllByText('Mesh Core System').length).toBeGreaterThan(0);
    });

    // Clicking the job in the sidebar opens JobCockpit
    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    // Job Cockpit header displays branch and project details
    await waitFor(() => {
      expect(screen.getByText('mesh/job-auth-oauth')).toBeInTheDocument();
      expect(screen.getByText('Active Job')).toBeInTheDocument();
      expect(screen.getByText('Complete Job')).toBeInTheDocument();
    });

    // Task checklist bar displays progress (1/2 completed)
    expect(screen.getByText(/1\/2 completed/i)).toBeInTheDocument();
    // Default filter is 'pending', showing only incomplete tasks
    expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
    expect(screen.queryByText('Implement OAuth endpoints')).not.toBeInTheDocument();

    // Clicking All filter shows all tasks including completed
    fireEvent.click(screen.getByRole('button', { name: /^All/i }));
    expect(screen.getByText('Implement OAuth endpoints')).toBeInTheDocument();
    expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();

    // Messages render in chat feed with persona badges and two-tier trajectory button
    expect(screen.getByText('@astra please implement the OAuth endpoints')).toBeInTheDocument();
    expect(screen.getByText(/I have finished the OAuth controller/)).toBeInTheDocument();
    expect(screen.getByText('View Turn Execution & Trajectory')).toBeInTheDocument();

    // Quick mention chips are visible
    expect(screen.getAllByText('@astra').length).toBeGreaterThan(0);
    expect(screen.getAllByText('@qa-tester').length).toBeGreaterThan(0);
  }, 15000);

  it('allows clicking quick mention chip to insert persona mention into prompt input and sending message', async () => {
    vi.mocked(api.postJobMessage).mockResolvedValue({
      id: 'm-3',
      jobId: 'job-101',
      senderType: 'user',
      content: '@qa-tester please verify token expiration',
      mentions: ['qa-tester'],
      createdAt: new Date().toISOString()
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getAllByText('@qa-tester').length).toBeGreaterThan(0);
    });

    // Click mention chip (the button element)
    const mentionButtons = screen.getAllByRole('button', { name: /@qa-tester/i });
    fireEvent.click(mentionButtons[0]);

    const textarea = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i);
    expect((textarea as HTMLTextAreaElement).value).toContain('@qa-tester ');

    // Type the rest of the message
    fireEvent.change(textarea, { target: { value: '@qa-tester please verify token expiration' } });

    // Submit message
    const sendBtn = screen.getByTitle('Send Message');
    fireEvent.click(sendBtn);

    await waitFor(() => {
      expect(api.postJobMessage).toHaveBeenCalledWith('job-101', {
        content: '@qa-tester please verify token expiration',
        senderType: 'user'
      });
    });
  });

  it('opens team member mention autocomplete when typing @ in the job message area and autocompletes persona', async () => {
    vi.mocked(api.postJobMessage).mockResolvedValue({
      id: 'm-4',
      jobId: 'job-101',
      senderType: 'user',
      content: '@astra please review security architecture',
      mentions: ['astra'],
      createdAt: new Date().toISOString()
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(
        screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i)
      ).toBeInTheDocument();
    });

    const textarea = screen.getByPlaceholderText(
      /Type your message, tasks, or delegate with @persona/i
    );

    // Enter @ in the job message area
    fireEvent.change(textarea, { target: { value: '@' } });

    // Autocomplete palette should appear showing project team members
    const palette = screen.getByTestId('team-mention-palette');
    expect(palette).toBeInTheDocument();
    expect(within(palette).getByText('Team Members')).toBeInTheDocument();
    expect(within(palette).getByText('Astra')).toBeInTheDocument();
    expect(within(palette).getByText('QA Tester')).toBeInTheDocument();

    // Select Astra from the autocomplete dropdown
    const astraOption = screen.getByTestId('team-mention-option-astra');
    fireEvent.click(astraOption);

    // Textarea updated with mention and palette closed
    expect((textarea as HTMLTextAreaElement).value).toBe('@astra ');
    expect(screen.queryByTestId('team-mention-palette')).not.toBeInTheDocument();

    // Finish typing message and send
    fireEvent.change(textarea, {
      target: { value: '@astra please review security architecture' }
    });
    const sendBtn = screen.getByTitle('Send Message');
    fireEvent.click(sendBtn);

    await waitFor(() => {
      expect(api.postJobMessage).toHaveBeenCalledWith('job-101', {
        content: '@astra please review security architecture',
        senderType: 'user'
      });
    });
  });

  it('renders autonomous living checklist tasks for the active job', async () => {
    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
    });

    // Living checklist displays tasks
    expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
  });

  it('applies the server job DTO after reassigning a task, converging queue and chat', async () => {
    // Reassignment can move parked queued turns and append a system message. Patching only
    // the assignee field locally would leave the queue and the transcript disagreeing with
    // the DB, so App must take the returned job wholesale.
    const initialJob: ProjectJob = {
      ...mockJob,
      queuedTurns: [
        {
          id: 'turn-9',
          jobId: 'job-101',
          projectId: 'mesh-core',
          targetPersonaId: 'astra',
          senderType: 'user',
          content: 'please cover token exchange',
          mentions: ['astra'],
          priority: 1,
          createdAt: new Date().toISOString()
        }
      ]
    };
    vi.mocked(api.getProjectJobs).mockResolvedValue([initialJob]);

    const reassignedJob: ProjectJob = {
      ...initialJob,
      tasks: [
        { ...mockTasks[0] },
        { ...mockTasks[1], assignedPersonaId: 'qa-tester' }
      ],
      queuedTurns: [
        {
          id: 'turn-9',
          jobId: 'job-101',
          projectId: 'mesh-core',
          targetPersonaId: 'qa-tester',
          senderType: 'user',
          content: 'please cover token exchange',
          mentions: ['qa-tester'],
          priority: 1,
          createdAt: new Date().toISOString()
        }
      ]
    };

    vi.mocked(api.assignJobTask).mockResolvedValue({
      task: reassignedJob.tasks[1],
      fromPersonaId: null,
      toPersonaId: 'qa-tester',
      fromPersonaName: null,
      toPersonaName: 'QA Tester',
      isTargetTask: true,
      movedQueuedTurnCount: 1,
      dispatchedToNewAssignee: false,
      job: reassignedJob
    });

    const messagesAfterHandover: JobMessage[] = [
      ...mockMessages,
      {
        id: 'm-system-1',
        jobId: 'job-101',
        senderType: 'system',
        senderName: 'System',
        content: 'Task "Write integration test coverage" reassigned from @Astra to @QA Tester — moved 1 queued turn to @QA Tester.',
        mentions: ['astra', 'qa-tester'],
        createdAt: new Date().toISOString()
      }
    ];
    vi.mocked(api.getJobMessages).mockResolvedValue(messagesAfterHandover);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    const assignSelect = await screen.findByTestId('reassign-queued-turn-turn-9');
    fireEvent.change(assignSelect, { target: { value: 'qa-tester' } });

    await waitFor(() => {
      expect(api.assignJobTask).toHaveBeenCalledWith('job-101', 'task-2', 'qa-tester');
    });

    // The handover message is pulled back in so the chat explains why the queue moved.
    await waitFor(() => {
      expect(screen.getByText(/reassigned from @Astra to @QA Tester/)).toBeInTheDocument();
    });
  });

  it('shows the server rejection inline when assigning to someone off the team', async () => {
    const initialJob: ProjectJob = {
      ...mockJob,
      queuedTurns: [
        {
          id: 'turn-9',
          jobId: 'job-101',
          projectId: 'mesh-core',
          targetPersonaId: 'astra',
          senderType: 'user',
          content: 'please cover token exchange',
          mentions: ['astra'],
          priority: 1,
          createdAt: new Date().toISOString()
        }
      ]
    };
    vi.mocked(api.getProjectJobs).mockResolvedValue([initialJob]);
    vi.mocked(api.assignJobTask).mockRejectedValue(
      new Error("Persona 'Ghost' is not on the team for project 'mesh-core'")
    );

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    const assignSelect = await screen.findByTestId('reassign-queued-turn-turn-9');
    fireEvent.change(assignSelect, { target: { value: 'qa-tester' } });

    await waitFor(() => {
      expect(screen.getByTestId('task-assign-error')).toHaveTextContent('is not on the team');
    });
  });

  it('supports completing a job with result summary', async () => {
    vi.mocked(api.completeJob).mockResolvedValue({
      ...mockJob,
      status: ProjectJobStatus.Completed,
      resultSummary: 'OAuth verified across all providers'
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByTestId('cockpit-complete-job-btn')).toBeInTheDocument();
    });

    // Click Complete Job to open confirmation popover
    fireEvent.click(screen.getByTestId('cockpit-complete-job-btn'));

    expect(screen.getByTestId('deliverables-summary-input')).toBeInTheDocument();
    const summaryInput = screen.getByTestId('deliverables-summary-input');
    fireEvent.change(summaryInput, { target: { value: 'OAuth verified across all providers' } });

    // Confirm complete
    fireEvent.click(screen.getByTestId('cockpit-confirm-complete-btn'));

    await waitFor(() => {
      expect(api.completeJob).toHaveBeenCalledWith('job-101', {
        resultSummary: 'OAuth verified across all providers'
      });
    });
  });

  it('does not create duplicate project rows when creating a project even if SignalR arrives concurrently', async () => {
    const newProject: Project = {
      id: 'mesh-billing',
      name: 'Mesh Billing Subsystem',
      description: 'Stripe webhook handling',
      workspaceType: WorkspaceType.Git,
      gitBaseBranch: 'main',
      projectInstructions: '',
      assignedPersonas: [],
      jobCount: 0,
      activeJobCount: 0,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.createProject).mockImplementation(async () => {
      if (signalRHandlers['ProjectCreated']) {
        signalRHandlers['ProjectCreated'](newProject);
      }
      return newProject;
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('New Project')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('New Project'));

    const nameInput = screen.getByPlaceholderText('e.g. AgentMesh Core');
    fireEvent.change(nameInput, { target: { value: 'Mesh Billing Subsystem' } });
    fireEvent.submit(nameInput.closest('form')!);

    await waitFor(() => {
      expect(screen.getAllByText('Mesh Billing Subsystem')).toHaveLength(1);
    });
  });

  it('does not create duplicate job rows when creating a job even if SignalR arrives concurrently', async () => {
    const newJob: ProjectJob = {
      id: 'job-102',
      projectId: 'mesh-core',
      projectName: 'Mesh Core System',
      title: 'Setup Redis Distributed Lock',
      description: '',
      status: ProjectJobStatus.Active,
      gitBranchName: 'mesh/job-redis-lock',
      tasks: [],
      messageCount: 0,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.createProjectJob).mockImplementation(async () => {
      if (signalRHandlers['JobCreated']) {
        signalRHandlers['JobCreated'](newJob);
      }
      return newJob;
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByTitle('Create new Job in this project')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTitle('Create new Job in this project'));

    const jobTitleInput = screen.getByPlaceholderText('e.g. Implement OAuth2 flow with Google & GitHub');
    fireEvent.change(jobTitleInput, { target: { value: 'Setup Redis Distributed Lock' } });
    fireEvent.submit(jobTitleInput.closest('form')!);

    await waitFor(() => {
      expect(screen.getAllByText('Setup Redis Distributed Lock')).toHaveLength(1);
    });
  });

  it('updates task items in living checklist when SignalR JobTaskUpdated arrives', async () => {
    const newTask: JobTask = {
      id: 'task-3',
      title: 'Deploy to staging',
      isCompleted: false
    };

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
    });

    // Simulate SignalR JobTaskUpdated event
    act(() => {
      if (signalRHandlers['JobTaskUpdated']) {
        signalRHandlers['JobTaskUpdated']({
          jobId: 'job-101',
          tasks: [...mockTasks, newTask]
        });
      }
    });

    await waitFor(() => {
      expect(screen.getAllByText('Deploy to staging')).toHaveLength(1);
    });
  });

  it('allows attaching files in job cockpit, stages them, sends attachments to postJobMessage, and renders attachments in job messages', async () => {
    const uploadedAtt = {
      id: 'att-job-99',
      sessionId: 'job-101',
      fileName: 'spec.pdf',
      contentType: 'application/pdf',
      sizeBytes: 4096,
      s3Key: 'jobs/job-101/attachments/att-job-99/spec.pdf',
      downloadUrl: '/api/project-jobs/job-101/attachments/att-job-99/content',
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.uploadJobAttachments).mockResolvedValue([uploadedAtt]);

    const createdJobMsg: JobMessage = {
      id: 'msg-job-created-1',
      jobId: 'job-101',
      senderType: 'user',
      content: 'Please review the attached file(s).',
      mentions: [],
      structuredPayloadJson: JSON.stringify({
        attachments: [uploadedAtt]
      }),
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.postJobMessage).mockResolvedValue(createdJobMsg);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByTestId('job-attach-btn')).toBeInTheDocument();
    });

    const fileInput = screen.getByTestId('job-file-input');
    const pdfFile = new File(['pdf-data'], 'spec.pdf', { type: 'application/pdf' });
    fireEvent.change(fileInput, { target: { files: [pdfFile] } });

    await waitFor(() => {
      expect(api.uploadJobAttachments).toHaveBeenCalledWith('job-101', [pdfFile]);
      expect(screen.getByTestId('job-staged-attachments')).toBeInTheDocument();
      expect(screen.getByText('spec.pdf')).toBeInTheDocument();
    });

    const sendBtn = screen.getByRole('button', { name: /Send Message/i });
    expect(sendBtn).not.toBeDisabled();

    fireEvent.click(sendBtn);

    await waitFor(() => {
      expect(api.postJobMessage).toHaveBeenCalledWith('job-101', {
        content: 'Please review the attached file(s).',
        senderType: 'user',
        attachments: [uploadedAtt]
      });
    });

    // The message with attachment is rendered in the message feed
    await waitFor(() => {
      expect(screen.getByTestId('job-message-attachments-msg-job-created-1')).toBeInTheDocument();
      expect(screen.getByText('spec.pdf')).toBeInTheDocument();
    });
  });

  it('allows editing project details and team members from project list and updates project', async () => {
    const updatedProj = {
      ...mockProject,
      name: 'Mesh Core System (Updated)',
      description: 'Updated description'
    };
    vi.mocked(api.updateProject).mockResolvedValue(updatedProj);

    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('edit-project-btn-mesh-core')).toBeInTheDocument();
    });

    // Click edit button on project in sidebar
    fireEvent.click(screen.getByTestId('edit-project-btn-mesh-core'));

    // Modal opens in edit mode
    await waitFor(() => {
      expect(screen.getByText('Edit Project')).toBeInTheDocument();
    });

    const nameInput = screen.getByDisplayValue('Mesh Core System');
    fireEvent.change(nameInput, { target: { value: 'Mesh Core System (Updated)' } });

    const saveBtn = screen.getByRole('button', { name: /Update Project/i });
    fireEvent.click(saveBtn);

    await waitFor(() => {
      expect(api.updateProject).toHaveBeenCalledWith(
        'mesh-core',
        expect.objectContaining({
          name: 'Mesh Core System (Updated)'
        })
      );
    });
  });

  it('clicking View Turn Execution & Trajectory loads turn session without empty observatory and provides Return to Job button', async () => {
    vi.mocked(api.getSession).mockResolvedValue({
      id: 'sess-turn-456',
      title: 'Job Turn: Implement OAuth Provider (@Astra)',
      status: 'active',
      metadataJson: JSON.stringify({
        jobId: 'job-101',
        projectId: 'mesh-core',
        projectName: 'Mesh Core System',
        personaId: 'astra'
      }),
      driverId: 'agy',
      personaId: 'astra',
      personaName: 'Astra',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    });
    vi.mocked(api.getSessionMessages).mockResolvedValue([
      {
        id: 'msg-ev-1',
        sessionId: 'sess-turn-456',
        senderAgentId: 'astra',
        eventType: 'thought',
        content: 'Analyzing OAuth specifications and scopes',
        createdAt: new Date().toISOString()
      }
    ]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByText('View Turn Execution & Trajectory')).toBeInTheDocument();
    });

    // Click View Turn Execution & Trajectory
    fireEvent.click(screen.getByText('View Turn Execution & Trajectory'));

    // Should load the session details in both sidebar and main panel, not show the empty observatory screen
    await waitFor(() => {
      expect(screen.getAllByText('Job Turn: Implement OAuth Provider (@Astra)').length).toBeGreaterThan(0);
      expect(screen.queryByText('Agent Session Observatory')).not.toBeInTheDocument();
      expect(screen.getByText('← Back to Job')).toBeInTheDocument();
    });

    // Click Back to Job returns to Job Cockpit
    fireEvent.click(screen.getByText('← Back to Job'));

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
      expect(screen.getByText('View Turn Execution & Trajectory')).toBeInTheDocument();
    });
  });

  it('hides archived jobs by default and allows toggling to show and hide them in the project list', async () => {
    const activeJob: ProjectJob = {
      id: 'job-active-1',
      projectId: 'mesh-core',
      title: 'Active Job Work',
      description: 'Active job description',
      status: ProjectJobStatus.Active,
      gitBranchName: 'mesh/job-active-1',
      tasks: [],
      messageCount: 0,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const archivedJob: ProjectJob = {
      id: 'job-archived-1',
      projectId: 'mesh-core',
      title: 'Old Archived Sprint',
      description: 'Archived sprint',
      status: ProjectJobStatus.Archived,
      gitBranchName: 'mesh/job-archived-1',
      tasks: [],
      messageCount: 0,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getProjectJobs).mockResolvedValue([activeJob, archivedJob]);

    render(<App />);

    // Active job is rendered by default
    await waitFor(() => {
      expect(screen.getAllByText('Active Job Work').length).toBeGreaterThan(0);
    });

    // Archived job is hidden by default
    expect(screen.queryByText('Old Archived Sprint')).not.toBeInTheDocument();

    // Toggle button for archived jobs is visible with count 1
    const toggleArchivedBtn = screen.getByTestId('toggle-archived-jobs-mesh-core');
    expect(toggleArchivedBtn).toBeInTheDocument();
    expect(toggleArchivedBtn).toHaveTextContent('Show Archived');
    expect(toggleArchivedBtn).toHaveTextContent('1');

    // Click Show Archived to reveal
    fireEvent.click(toggleArchivedBtn);

    await waitFor(() => {
      expect(screen.getByText('Old Archived Sprint')).toBeInTheDocument();
      expect(toggleArchivedBtn).toHaveTextContent('Hide Archived');
    });

    // Click Hide Archived to re-hide
    fireEvent.click(toggleArchivedBtn);

    await waitFor(() => {
      expect(screen.queryByText('Old Archived Sprint')).not.toBeInTheDocument();
      expect(toggleArchivedBtn).toHaveTextContent('Show Archived');
    });
  });

  it('surfaces questions in job chat, renders interactive question cards, and calls submitQuestionAnswer when answered', async () => {
    const jobWithQuestion: ProjectJob = {
      id: 'job-q-101',
      projectId: 'mesh-core',
      title: 'Question Workflow Job',
      description: 'Job with question',
      status: ProjectJobStatus.Active,
      gitBranchName: 'mesh/job-q-101',
      tasks: [],
      messageCount: 1,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const questionMsg: JobMessage = {
      id: 'msg-job-q-1',
      jobId: 'job-q-101',
      senderType: 'persona',
      senderId: 'agy-astra',
      senderName: 'Astra',
      content: 'I have a question before we begin:',
      mentions: [],
      turnSessionId: 'sess-q-turn-1',
      structuredPayloadJson: JSON.stringify({
        toolName: 'ask_question',
        questions: [
          {
            question: 'Should we enable streaming logs?',
            options: ['Yes', 'No'],
            is_multi_select: false
          }
        ]
      }),
      createdAt: new Date().toISOString()
    };

    const turnSession: AgentSession = {
      id: 'sess-q-turn-1',
      title: 'Turn Session Q',
      status: 'waiting_for_input',
      metadataJson: '{}',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getProjectJobs).mockResolvedValue([jobWithQuestion]);
    vi.mocked(api.getJobMessages).mockResolvedValue([questionMsg]);
    vi.mocked(api.getSessions).mockResolvedValue([turnSession]);
    vi.mocked(api.submitQuestionAnswer).mockResolvedValue({
      toolResult: {} as any,
      prompt: {} as any,
      session: turnSession
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Question Workflow Job').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Question Workflow Job')[0]);

    // Wait for job chat to load
    await waitFor(() => {
      expect(screen.getByTestId('question-card-msg-job-q-1')).toBeInTheDocument();
      expect(screen.getByText('Should we enable streaming logs?')).toBeInTheDocument();
    });

    // Option pill 'Yes' is rendered
    const yesBtn = screen.getByText('Yes');
    expect(yesBtn).toBeInTheDocument();
    fireEvent.click(yesBtn);

    // Click Submit Answer
    const submitBtn = screen.getByRole('button', { name: /Submit Answer/i });
    fireEvent.click(submitBtn);

    await waitFor(() => {
      expect(api.submitQuestionAnswer).toHaveBeenCalledWith(
        'sess-q-turn-1',
        expect.objectContaining({
          toolCallId: 'msg-job-q-1',
          answers: [
            expect.objectContaining({
              question: 'Should we enable streaming logs?',
              selectedOptions: ['Yes']
            })
          ]
        })
      );
    });
  });

  it('does not drag user back to projects tab when viewing personas tab and background session update arrives', async () => {
    render(<App />);

    // Initially loads on projects tab (since project exists)
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Personas & Sessions/i })).toBeInTheDocument();
    });

    // Switch to Personas & Sessions tab
    const personasTabBtn = screen.getByRole('button', { name: /Personas & Sessions/i });
    fireEvent.click(personasTabBtn);

    // Verify Personas tab is active
    await waitFor(() => {
      expect(screen.getByText('New Persona')).toBeInTheDocument();
      expect(screen.getByText('Astra')).toBeInTheDocument();
    });

    // Simulate background SignalR event (SessionUpdated)
    await act(async () => {
      if (signalRHandlers['SessionUpdated']) {
        await signalRHandlers['SessionUpdated']({ id: 'sess-bg-1', status: 'thinking' });
      }
    });

    // Verify user remains on Personas tab and is NOT dragged back to projects tab
    expect(screen.getByText('New Persona')).toBeInTheDocument();
    expect(screen.queryByTestId('job-dropzone-overlay')).not.toBeInTheDocument();
  });

  describe('Job Artifacts panel', () => {
    const markdownArtifact: JobArtifact = {
      id: 'job-art-plan',
      jobId: 'job-101',
      sourceSessionId: 'sess-turn-456',
      name: 'architecture_plan.md',
      summary: 'Architecture specification with Mermaid flow',
      contentType: 'text/markdown',
      sizeBytes: 2048,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      // The list endpoint sends no body; the panel fetches it on selection.
      content: undefined,
      downloadUrl: '/api/project-jobs/job-101/artifacts/job-art-plan/content'
    };

    const mermaidArtifact: JobArtifact = {
      ...markdownArtifact,
      id: 'job-art-diagram',
      name: 'diagram.mmd',
      summary: 'Mermaid Architecture Diagram',
      contentType: 'text/plain',
      content: 'graph TD;\n  A[API] --> B[Worker];',
      downloadUrl: '/api/project-jobs/job-101/artifacts/job-art-diagram/content'
    };

    const renderJob = async () => {
      render(<App />);
      await waitFor(() => {
        expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
      }, { timeout: 4000 });
      fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);
      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toBeInTheDocument();
      });
    };

    it('shows the artifact count on the toggle and renders the panel with the mirrored files', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact, mermaidArtifact]);
      vi.mocked(api.getJobArtifact).mockResolvedValue({ ...markdownArtifact, content: '# Architecture Specification\n\nBody text' });

      await renderJob();

      // The count is visible before the panel is opened, which is why artifacts are fetched on job
      // switch rather than on toggle.
      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (2)');
      });
      expect(screen.queryByTestId('job-artifact-panel-container')).not.toBeInTheDocument();

      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByTestId('job-artifact-panel-container')).toBeInTheDocument();
        expect(screen.getByTestId('artifact-viewer')).toBeInTheDocument();
        // Each name renders twice by design: once as a selector tab and once as the active artifact's
        // header. Asserting membership rather than a single match keeps that viewer detail out of this
        // test's contract.
        expect(screen.getAllByText('architecture_plan.md').length).toBeGreaterThan(0);
        expect(screen.getAllByText('diagram.mmd').length).toBeGreaterThan(0);
        expect(screen.getByText('Architecture specification with Mermaid flow')).toBeInTheDocument();
      });

      expect(api.getJobArtifacts).toHaveBeenCalledWith('job-101');
    });

    it('renders markdown for the selected artifact and switches tabs without a second fetch', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact, mermaidArtifact]);
      vi.mocked(api.getJobArtifact).mockResolvedValue({ ...markdownArtifact, content: '# Architecture Specification\n\nKeep it simple' });

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: /Architecture Specification/i })).toBeInTheDocument();
      });

      // The mermaid artifact arrived with a body already folded in from the live event path, so the
      // panel renders it without asking the server.
      fireEvent.click(screen.getByText('diagram.mmd'));
      await waitFor(() => {
        expect(screen.getByText(/Mermaid Architecture Diagram/i)).toBeInTheDocument();
      });

      expect(api.getJobArtifact).toHaveBeenCalledTimes(1);
      expect(api.getJobArtifact).toHaveBeenCalledWith('job-101', 'job-art-plan');
    });

    it('fetches the body lazily when the list omits it, and does not refetch on reselect', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact]);
      vi.mocked(api.getJobArtifact).mockResolvedValue({ ...markdownArtifact, content: '# Lazy Body\n\nfetched on demand' });

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: /Lazy Body/i })).toBeInTheDocument();
      });

      // Close and reopen: the body is already in state, so no second detail request.
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));
      await waitFor(() => {
        expect(screen.queryByTestId('job-artifact-panel-container')).not.toBeInTheDocument();
      });
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: /Lazy Body/i })).toBeInTheDocument();
      });
      expect(api.getJobArtifact).toHaveBeenCalledTimes(1);
    });

    it('folds a live JobArtifactCreated event into the panel and bumps the count', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact]);

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (1)');
      });

      // A turn syncs a new artifact mid-flight: the mirror broadcasts, and the panel shows it without a
      // refetch of the whole job.
      await act(async () => {
        signalRHandlers['JobArtifactCreated']?.({
          ...mermaidArtifact,
          content: 'graph TD;\n  X --> Y;'
        });
      });

      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (2)');
        expect(screen.getByText('diagram.mmd')).toBeInTheDocument();
      });
    });

    it('replaces on JobArtifactUpdated instead of duplicating the same file', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact]);

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getAllByText('architecture_plan.md')).toHaveLength(1);
      });

      // Re-sync of an edited file: same name, new size. Two rows for one file is the bug this guards.
      await act(async () => {
        signalRHandlers['JobArtifactUpdated']?.({ ...markdownArtifact, sizeBytes: 9000 });
      });

      await waitFor(() => {
        expect(screen.getAllByText('architecture_plan.md')).toHaveLength(1);
      });
      expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (1)');
    });

    it('re-fetches the body when a live JobArtifactUpdated brings a newer revision', async () => {
      // The bug this guards: the mirror broadcasts JobArtifactUpdated with the list shape — no body, by
      // design, so a 2 MB transcript never rides a SignalR push. Folding it in blanks the body the panel
      // had already loaded. If the body fetch is then keyed on job+id alone, the guard sees that id as
      // already attempted and refuses to re-fetch: the operator watches an open plan.md go blank mid-turn
      // and cannot get it back without switching jobs. Keying on the revision makes it fetchable again.
      const v1 = '2026-11-05T10:00:00.000Z';
      const v2 = '2026-11-05T10:05:00.000Z';
      vi.mocked(api.getJobArtifacts).mockResolvedValue([{ ...markdownArtifact, updatedAt: v1 }]);
      vi.mocked(api.getJobArtifact).mockResolvedValue({
        ...markdownArtifact,
        updatedAt: v1,
        content: '# First Revision\n\noriginal body'
      });

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: /First Revision/i })).toBeInTheDocument();
      });
      expect(api.getJobArtifact).toHaveBeenCalledTimes(1);

      // The agent edits and re-syncs the same file: same id (so the selection survives), newer updatedAt,
      // and no body in the payload.
      vi.mocked(api.getJobArtifact).mockResolvedValue({
        ...markdownArtifact,
        updatedAt: v2,
        sizeBytes: 9000,
        content: '# Second Revision\n\nedited body'
      });

      await act(async () => {
        signalRHandlers['JobArtifactUpdated']?.({
          ...markdownArtifact,
          updatedAt: v2,
          sizeBytes: 9000,
          content: undefined
        });
      });

      await waitFor(() => {
        expect(screen.getByRole('heading', { name: /Second Revision/i })).toBeInTheDocument();
      });
      expect(api.getJobArtifact).toHaveBeenCalledTimes(2);
      expect(api.getJobArtifact).toHaveBeenLastCalledWith('job-101', 'job-art-plan');
      expect(screen.queryByText(/First Revision/i)).not.toBeInTheDocument();
    });

    it('does not re-fetch a body for a revision it already tried', async () => {
      // The other half of the guard: a body that comes back null (file deleted from disk, a type the
      // server refuses to inline) leaves the entry looking exactly as it did, so without the attempt key
      // an effect depending on the artifact list would fire again immediately. Adding the revision to the
      // key must not turn a dead end into a request storm — same revision, one attempt only.
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact]);
      // The server sends a JSON null body; the client type models both as "no body" and the effect's
      // `content != null` check treats them identically, so undefined is the type-correct spelling here.
      vi.mocked(api.getJobArtifact).mockResolvedValue({ ...markdownArtifact, content: undefined });

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(api.getJobArtifact).toHaveBeenCalledTimes(1);
      });

      // Three events for the same revision (a duplicate broadcast, a re-render, a refresh button press).
      for (let i = 0; i < 3; i++) {
        await act(async () => {
          signalRHandlers['JobArtifactUpdated']?.({ ...markdownArtifact, sizeBytes: 9000 + i });
        });
      }

      expect(api.getJobArtifact).toHaveBeenCalledTimes(1);
    });

    it('ignores artifact events belonging to another job', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact]);

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (1)');
      });

      await act(async () => {
        signalRHandlers['JobArtifactCreated']?.({ ...mermaidArtifact, jobId: 'some-other-job' });
      });

      expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (1)');
      expect(screen.queryByText('diagram.mmd')).not.toBeInTheDocument();
    });

    it('keeps job artifacts and session artifacts in separate state', async () => {
      // The two surfaces share a component but not an array: opening a session must not push the job's
      // files out of the job panel, and vice versa.
      vi.mocked(api.getJobArtifacts).mockResolvedValue([markdownArtifact]);
      vi.mocked(api.getSessionArtifacts).mockResolvedValue([{
        id: 'sess-art-9',
        sessionId: 'sess-turn-456',
        name: 'session_only_note.md',
        contentType: 'text/markdown',
        sizeBytes: 12,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        content: '# Session note'
      }]);

      await renderJob();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      await waitFor(() => {
        expect(screen.getByText('architecture_plan.md')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));

      // Switch to the personas tab and open the session's artifacts.
      const personasTabBtn = screen.getByRole('button', { name: /personas/i });
      fireEvent.click(personasTabBtn);
      await waitFor(() => {
        expect(screen.getByText('New Persona')).toBeInTheDocument();
      });

      expect(screen.queryByText('architecture_plan.md')).not.toBeInTheDocument();
    });

    it('renders Artifacts (0) and no panel content when the job has no artifacts', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([]);

      await renderJob();

      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (0)');
      });

      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));
      await waitFor(() => {
        expect(screen.getByTestId('job-artifact-panel-container')).toBeInTheDocument();
      });
      expect(screen.getByText(/No artifacts/i)).toBeInTheDocument();
    });

    it('degrades to an empty panel when the artifact endpoint is unavailable', async () => {
      // A dashboard newer than its API is a real deployment state. The cockpit must still render chat and
      // checklist, with the panel simply empty — not an error boundary and not a rejected Promise.all.
      vi.mocked(api.getJobArtifacts).mockRejectedValue(new Error('404 Not Found'));

      await renderJob();

      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (0)');
      });

      // The rest of the cockpit survived the failed call.
      expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
      expect(screen.getByText(/please implement the OAuth endpoints/i)).toBeInTheDocument();
    });

    it('clears artifacts when switching to a job with none', async () => {
      // Both jobs are listed from the start so the switch is a click in the job list. getProjectJobs is
      // mocked before render rather than mid-test: re-mocking it after mount triggers a refetch that
      // duplicates the rows this test navigates by.
      const secondJob: ProjectJob = {
        ...mockJob,
        id: 'job-202',
        title: 'Second Job',
        messageCount: 0
      };
      vi.mocked(api.getProjectJobs).mockResolvedValue([mockJob, secondJob]);
      vi.mocked(api.getJobArtifacts).mockImplementation(async (jobId: string) =>
        jobId === 'job-101' ? [markdownArtifact] : []
      );

      render(<App />);

      // The app auto-selects the first job, whose artifacts load.
      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (1)');
      }, { timeout: 4000 });

      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));
      await waitFor(() => {
        expect(screen.getByTestId('job-artifact-panel-container')).toBeInTheDocument();
      });

      // Switch jobs. 'Second Job' is unique to the list until it becomes the selection.
      fireEvent.click(screen.getByText('Second Job'));

      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toHaveTextContent('Artifacts (0)');
      });
      expect(screen.queryByText('architecture_plan.md')).not.toBeInTheDocument();
      expect(api.getJobArtifacts).toHaveBeenCalledWith('job-202');
    });
  });

  // ── The Changes tab ─────────────────────────────────────────────────────────
  //
  // The integration contract, not the panel's own behaviour (that has its own suite in
  // components/__tests__/ChangesPanel.test.tsx): what these assert is the wiring — that the tab exists
  // beside Artifacts in the same column, that opening it costs exactly one change-set read, that
  // switching tabs does not disturb the other panel's state, and that closing the column is one action
  // rather than two.

  // Local to this suite: the artifacts fixture is scoped inside the 'Job Artifacts panel' describe, and
  // hoisting it out to share would edit a passing suite for a change the tab does not need.
  const anArtifact: JobArtifact = {
    id: 'job-art-plan',
    jobId: 'job-101',
    sourceSessionId: 'sess-turn-456',
    name: 'architecture_plan.md',
    summary: 'Architecture specification with Mermaid flow',
    contentType: 'text/markdown',
    sizeBytes: 2048,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    content: '# Architecture Specification\n\nbody',
    downloadUrl: '/api/project-jobs/job-101/artifacts/job-art-plan/content'
  };

  const makeChangeSet = (over: any = {}) => ({
    jobId: 'job-101',
    branch: 'mesh/job-auth-oauth',
    baseBranch: 'main',
    headSha: 'c'.repeat(40),
    baseSha: 'd'.repeat(40),
    stats: { filesChanged: 2, additions: 30, deletions: 4, omittedFiles: 0 },
    files: [
      {
        path: 'src/AgentMesh.Api/Controllers/OAuthController.cs',
        status: 'added',
        previousPath: null,
        additions: 25,
        deletions: 0,
        binary: false,
        patchAvailable: true
      },
      {
        path: 'src/AgentMesh.Api/Program.cs',
        status: 'modified',
        previousPath: null,
        additions: 5,
        deletions: 4,
        binary: false,
        patchAvailable: true
      }
    ],
    source: {
      kind: 'localGit',
      reasonCode: null,
      message: null,
      degraded: false,
      committedOnly: false,
      truncated: false,
      repository: null
    },
    commits: [],
    hasUncommittedChanges: false,
    uncommittedPaths: null,
    uncommittedCount: null,
    generatedAt: null,
    notes: null,
    ...over
  });

  const renderChangesTab = async () => {
    render(<App />);
    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    }, { timeout: 4000 });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);
    await waitFor(() => {
      expect(screen.getByTestId('toggle-job-changes-btn')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId('toggle-job-changes-btn'));
    await waitFor(() => {
      expect(screen.getByTestId('job-changes-panel-container')).toBeInTheDocument();
    });
  };

  describe('Job Changes tab', () => {
    it('renders a Changes toggle beside Artifacts in the cockpit header', async () => {
      render(<App />);
      await waitFor(() => {
        expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
      }, { timeout: 4000 });
      fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-changes-btn')).toBeInTheDocument();
        expect(screen.getByTestId('toggle-job-artifacts-btn')).toBeInTheDocument();
      });
    });

    it('does not read git until the tab is opened, then reads it exactly once', async () => {
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet());

      render(<App />);
      await waitFor(() => {
        expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
      }, { timeout: 4000 });
      fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

      // The whole reason the count is not on the header badge: a change set is a `git`-backed read, and
      // the app must not pay for one per job the operator merely clicks through.
      await waitFor(() => {
        expect(screen.getByTestId('toggle-job-changes-btn')).toBeInTheDocument();
      });
      expect(api.getJobChanges).not.toHaveBeenCalled();

      fireEvent.click(screen.getByTestId('toggle-job-changes-btn'));
      await waitFor(() => {
        expect(api.getJobChanges).toHaveBeenCalledTimes(1);
      });
      expect(api.getJobChanges).toHaveBeenCalledWith('job-101', { refresh: false });
      expect(screen.getByTestId('changes-count')).toHaveTextContent('2');
    });

    it('shows the file list and the diff of the selected file with +/- line colouring', async () => {
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet());
      vi.mocked(api.getJobChangePatch).mockResolvedValue({
        found: true,
        patch: {
          jobId: 'job-101',
          branch: 'mesh/job-auth-oauth',
          baseBranch: 'main',
          headSha: 'c'.repeat(40),
          baseSha: 'd'.repeat(40),
          patch: [
            'diff --git a/src/AgentMesh.Api/Controllers/OAuthController.cs b/src/AgentMesh.Api/Controllers/OAuthController.cs',
            'new file mode 100644',
            'index 0000000..3a4f1c2',
            '--- /dev/null',
            '+++ b/src/AgentMesh.Api/Controllers/OAuthController.cs',
            '@@ -0,0 +1,3 @@',
            '+[ApiController]',
            '+public class OAuthController : ControllerBase',
            '+  // token exchange'
          ].join('\n'),
          path: 'src/AgentMesh.Api/Controllers/OAuthController.cs',
          previousPath: null,
          status: 'added',
          binary: false,
          empty: false,
          truncated: false,
          additions: 3,
          deletions: 0,
          contextLines: 3,
          source: null
        }
      });

      await renderChangesTab();

      await waitFor(() => {
        expect(screen.getAllByTestId('changes-file-row')).toHaveLength(2);
      });
      // The patch is a second request per file, so it lands after the list render.
      await waitFor(() => {
        expect(screen.getByTestId('changes-diff-body')).toBeInTheDocument();
      });
      expect(screen.getByTestId('changes-diff-body').querySelectorAll('[data-diff-kind="add"]')).toHaveLength(3);
      expect(screen.getByTestId('changes-diff-body').querySelectorAll('[data-diff-kind="del"]')).toHaveLength(0);

      // The row's counts come from the list payload, the diff from the patch endpoint — one request for
      // the list, one per clicked file, which is what the backend's reuse window is sized for.
      expect(api.getJobChangePatch).toHaveBeenCalledWith('job-101', expect.objectContaining({
        path: 'src/AgentMesh.Api/Controllers/OAuthController.cs',
        baseSha: 'd'.repeat(40),
        headSha: 'c'.repeat(40)
      }));
    });

    it('is the only tab open at a time, and switching tabs keeps the same column width', async () => {
      vi.mocked(api.getJobArtifacts).mockResolvedValue([anArtifact]);
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet());

      render(<App />);
      await waitFor(() => {
        expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
      }, { timeout: 4000 });
      fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));
      await waitFor(() => {
        expect(screen.getByTestId('job-artifact-panel-container')).toBeInTheDocument();
      });

      // Switching to Changes closes Artifacts: one column, one owner of "what is showing". Two panels
      // rendered side by side (or stacked in the same column) is the state a single tab value prevents.
      fireEvent.click(screen.getByTestId('toggle-job-changes-btn'));
      await waitFor(() => {
        expect(screen.getByTestId('job-changes-panel-container')).toBeInTheDocument();
      });
      expect(screen.queryByTestId('job-artifact-panel-container')).not.toBeInTheDocument();

      // And back again.
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));
      await waitFor(() => {
        expect(screen.getByTestId('job-artifact-panel-container')).toBeInTheDocument();
      });
      expect(screen.queryByTestId('job-changes-panel-container')).not.toBeInTheDocument();
    });

    it('switches tabs from inside the panel, where the cockpit header is covered', async () => {
      // On a phone the panel is a fixed inset-0 drawer over the header, so the header toggles are not
      // clickable while it is open. The in-panel strip is what makes the other tab reachable there, and
      // this asserts it exists on both tabs rather than only on the one that needed it.
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet());

      await renderChangesTab();

      const strip = screen.getByTestId('panel-tab-strip');
      fireEvent.click(within(strip).getByTestId('panel-tab-artifacts'));

      await waitFor(() => {
        expect(screen.getByTestId('job-artifact-panel-container')).toBeInTheDocument();
      });
      expect(screen.queryByTestId('job-changes-panel-container')).not.toBeInTheDocument();
    });

    it('closes the column from either toggle when that tab is already open', async () => {
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet());

      await renderChangesTab();
      expect(screen.getByTestId('job-changes-panel-container')).toBeInTheDocument();

      fireEvent.click(screen.getByTestId('toggle-job-changes-btn'));
      await waitFor(() => {
        expect(screen.queryByTestId('job-changes-panel-container')).not.toBeInTheDocument();
      });
      expect(screen.queryByTestId('job-artifact-panel-container')).not.toBeInTheDocument();
      expect(screen.queryByTestId('job-artifact-resize-divider')).not.toBeInTheDocument();
    });

    it('shows the reason, not an error, when no source can read changes', async () => {
      // The K8s / no-token deployment shape the operator chose behaviour for: explain why, and fall back
      // to the checkpoint list if there is one. Neither is an error boundary.
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet({
        files: [],
        stats: { filesChanged: 0, additions: 0, deletions: 0, omittedFiles: 0 },
        source: {
          kind: null,
          reasonCode: 'no_workspace',
          message: 'The control plane sees no working directory for this job.',
          degraded: false,
          committedOnly: false,
          truncated: false,
          repository: null
        },
        notes: ['Checkout: no_workspace — the control plane sees no working directory for this job.']
      }));

      await renderChangesTab();

      await waitFor(() => {
        expect(screen.getByTestId('changes-empty-reason')).toHaveTextContent(/no working directory/i);
      });
      expect(screen.queryByText(/Could not read this job/i)).not.toBeInTheDocument();
    });

    it('falls back to the checkpoint file list when that is all the server could read', async () => {
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet({
        files: [
          {
            path: 'src/checkpointed/File.cs',
            status: 'modified',
            previousPath: null,
            additions: null,
            deletions: null,
            binary: false,
            patchAvailable: false
          }
        ],
        stats: { filesChanged: 1, additions: 0, deletions: 0, omittedFiles: 0 },
        source: {
          kind: 'checkpoint',
          reasonCode: null,
          message: 'The working checkpoint written at the last turn boundary.',
          degraded: true,
          committedOnly: true,
          truncated: false,
          repository: null
        }
      }));

      await renderChangesTab();

      await waitFor(() => {
        expect(screen.getByTestId('changes-file-row')).toHaveTextContent('File.cs');
      });
      expect(screen.getByTestId('changes-source-chip')).toHaveTextContent('checkpoint');
      // No diff link is offered, so the patch endpoint is never called for a source that has no content.
      expect(screen.getByText('no diff')).toBeInTheDocument();
      expect(api.getJobChangePatch).not.toHaveBeenCalled();
    });

    it('leaves artifacts untouched when the changes endpoint fails', async () => {
      // The two tabs read different endpoints; a newer dashboard against an older API must lose the
      // Changes tab and keep the rest of the cockpit, exactly as the artifacts panel's own degradation
      // test asserts for its endpoint.
      vi.mocked(api.getJobChanges).mockRejectedValue(new Error('Failed to load file changes: 404'));
      vi.mocked(api.getJobArtifacts).mockResolvedValue([anArtifact]);

      await renderChangesTab();

      await waitFor(() => {
        expect(screen.getByText(/Could not read this job/i)).toBeInTheDocument();
      });

      // The cockpit survived it.
      expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
      fireEvent.click(screen.getByTestId('toggle-job-artifacts-btn'));
      await waitFor(() => {
        expect(screen.getByTestId('job-artifact-panel-container')).toBeInTheDocument();
      });
      expect(screen.getAllByText('architecture_plan.md').length).toBeGreaterThan(0);
    });

    it('does not fetch changes when the job has none to ask about', async () => {
      // Closing the column must not leave a request in flight that repaints a closed panel. The guard is
      // the list-token check in ChangesPanel.load; here the assertion is the observable half: switching
      // jobs while the tab is open re-reads for the new job only.
      const secondJob: ProjectJob = { ...mockJob, id: 'job-202', title: 'Second Job', messageCount: 0 };
      vi.mocked(api.getProjectJobs).mockResolvedValue([mockJob, secondJob]);
      vi.mocked(api.getJobChanges).mockResolvedValue(makeChangeSet());

      render(<App />);
      await waitFor(() => {
        expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
      }, { timeout: 4000 });
      fireEvent.click(screen.getByTestId('toggle-job-changes-btn'));

      await waitFor(() => {
        expect(api.getJobChanges).toHaveBeenCalledWith('job-101', { refresh: false });
      });

      fireEvent.click(screen.getByText('Second Job'));
      await waitFor(() => {
        expect(api.getJobChanges).toHaveBeenCalledWith('job-202', { refresh: false });
      });
    });
  });
});
