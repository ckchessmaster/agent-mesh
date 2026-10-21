// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type { Persona, Project, ProjectJob, JobTask, JobMessage } from '../types';
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
    deleteJobTask: vi.fn(),
    getJobMessages: vi.fn(),
    postJobMessage: vi.fn(),
    uploadJobAttachments: vi.fn(),
    completeJob: vi.fn()
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
    });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    // Job Cockpit header displays branch and project details
    await waitFor(() => {
      expect(screen.getByText('mesh/job-auth-oauth')).toBeInTheDocument();
      expect(screen.getByText('Active Job')).toBeInTheDocument();
      expect(screen.getByText('Complete & Merge Job')).toBeInTheDocument();
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
  });

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
    });
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
    });
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

  it('allows toggling task checklist items and adding a new task', async () => {
    vi.mocked(api.updateJobTask).mockResolvedValue({
      id: 'task-2',
      title: 'Write integration test coverage',
      isCompleted: true,
      completedAt: new Date().toISOString()
    });

    vi.mocked(api.addJobTask).mockResolvedValue({
      id: 'task-3',
      title: 'Deploy to staging',
      isCompleted: false
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
    });

    // Add new task
    const addTaskInput = screen.getByPlaceholderText('+ Add task...');
    fireEvent.change(addTaskInput, { target: { value: 'Deploy to staging' } });
    fireEvent.submit(addTaskInput.closest('form')!);

    await waitFor(() => {
      expect(api.addJobTask).toHaveBeenCalledWith('job-101', { title: 'Deploy to staging' });
    });
  });

  it('supports completing and merging a job with result summary', async () => {
    vi.mocked(api.completeJob).mockResolvedValue({
      ...mockJob,
      status: ProjectJobStatus.Completed,
      resultSummary: 'OAuth verified across all providers'
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByText('Complete & Merge Job')).toBeInTheDocument();
    });

    // Click Complete & Merge Job to open confirmation
    fireEvent.click(screen.getByText('Complete & Merge Job'));

    expect(screen.getByPlaceholderText('Summary of deliverables...')).toBeInTheDocument();
    const summaryInput = screen.getByPlaceholderText('Summary of deliverables...');
    fireEvent.change(summaryInput, { target: { value: 'OAuth verified across all providers' } });

    // Confirm merge
    fireEvent.click(screen.getByText('Confirm & Merge'));

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

  it('does not create duplicate task items in checklist when adding a task even if SignalR arrives concurrently', async () => {
    const newTask: JobTask = {
      id: 'task-3',
      title: 'Deploy to staging',
      isCompleted: false
    };

    vi.mocked(api.addJobTask).mockImplementation(async () => {
      if (signalRHandlers['JobTaskUpdated']) {
        signalRHandlers['JobTaskUpdated']({
          jobId: 'job-101',
          tasks: [...mockTasks, newTask]
        });
      }
      return newTask;
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implement OAuth Provider').length).toBeGreaterThan(0);
    });
    fireEvent.click(screen.getAllByText('Implement OAuth Provider')[0]);

    await waitFor(() => {
      expect(screen.getByText('Write integration test coverage')).toBeInTheDocument();
    });

    const addTaskInput = screen.getByPlaceholderText('+ Add task...');
    fireEvent.change(addTaskInput, { target: { value: 'Deploy to staging' } });
    fireEvent.submit(addTaskInput.closest('form')!);

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
    });
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
});
