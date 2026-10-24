import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getStatus: vi.fn(),
    getCapabilities: vi.fn().mockResolvedValue({
      workspace: {
        defaultSandboxPath: '~/.agentmesh/workspaces/<session-id>',
        currentDirectory: '/workspace/agent-mesh',
        userHome: '/home/testuser',
        bookmarks: []
      },
      agentRuntimes: {
        openai: {
          adapter: 'openai',
          name: 'Custom OpenAI Compatible Server',
          modes: [
            { id: 'auto', label: 'Auto (Autonomous)', description: 'Auto execution' },
            { id: 'plan', label: 'Plan Only', description: 'Plan only' },
            { id: 'direct', label: 'Direct Chat', description: 'Direct chat' }
          ],
          contextSizes: [
            { id: '32k', label: '32k Tokens', description: '32k context' },
            { id: '128k', label: '128k Tokens', description: '128k context' }
          ],
          models: [
            { id: 'llama3.3', name: 'Llama 3.3 (70B)', efforts: ['low', 'medium', 'high'], contextSizes: ['32k', '128k'] },
            { id: 'qwen2.5-coder', name: 'Qwen 2.5 Coder (32B)', efforts: ['medium'], contextSizes: ['32k', '128k'] }
          ]
        }
      }
    }),
    getAgents: vi.fn().mockResolvedValue([]),
    getPersonas: vi.fn().mockResolvedValue([]),
    createPersona: vi.fn(),
    updatePersona: vi.fn(),
    deletePersona: vi.fn(),
    createPersonaSession: vi.fn(),
    deleteSession: vi.fn(),
    uploadSessionAttachments: vi.fn(),
    getAgentUsage: vi.fn().mockResolvedValue(null),
    getSessions: vi.fn().mockResolvedValue([]),
    getSession: vi.fn().mockResolvedValue(null),
    getSessionMessages: vi.fn().mockResolvedValue([]),
    resetMesh: vi.fn(),
    createAgentSession: vi.fn(),
    updateSessionControls: vi.fn(),
    postSessionMessage: vi.fn(),
    upsertAgent: vi.fn(),
    testOpenAiServer: vi.fn(),
    stopSession: vi.fn().mockResolvedValue({ status: 'stopped', sessionId: 's1' }),
    stopCommand: vi.fn().mockResolvedValue({ status: 'stopped', commandId: 'c1' }),
    enqueuePrompt: vi.fn().mockResolvedValue({ session: {}, queue: [], dispatched: false }),
    dequeuePrompt: vi.fn().mockResolvedValue([]),
    sendQueuedPromptNow: vi.fn().mockResolvedValue({ session: {}, queue: [], dispatched: true }),
    getJobs: vi.fn().mockResolvedValue([]),
    submitApproval: vi.fn(),
    createJob: vi.fn(),
    getProjects: vi.fn().mockResolvedValue([]),
    getProjectJobs: vi.fn().mockResolvedValue([]),
    createProject: vi.fn(),
    updateProject: vi.fn(),
    deleteProject: vi.fn(),
    createProjectJob: vi.fn(),
    getProjectJobMessages: vi.fn().mockResolvedValue([]),
    postProjectJobMessage: vi.fn(),
    getProjectJobTasks: vi.fn().mockResolvedValue([]),
    createProjectJobTask: vi.fn(),
    updateProjectJobTask: vi.fn(),
    deleteProjectJobTask: vi.fn(),
    submitQuestionAnswer: vi.fn(),
    archiveJob: vi.fn(),
    reopenJob: vi.fn(),
    uploadProjectJobAttachment: vi.fn(),
    uploadTaskAttachment: vi.fn(),
    deleteTaskAttachment: vi.fn()
  }
}));

describe('OpenAI-Compatible Driver Integration', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
  });

  it('allows registering an OpenAI compatible driver with endpoint and fetching models', async () => {
    vi.mocked(api.testOpenAiServer).mockResolvedValueOnce({
      success: true,
      models: [
        { id: 'llama3.3:70b', name: 'llama3.3:70b' },
        { id: 'qwen2.5-coder:32b', name: 'qwen2.5-coder:32b' }
      ]
    });

    vi.mocked(api.upsertAgent).mockResolvedValueOnce({
      id: 'ollama-local',
      name: 'Local Ollama Server',
      role: 'Autonomous AI Engineer',
      status: 'offline',
      runtimeType: 'OpenAiCompatible',
      trustTier: 0,
      personaPrompt: '',
      createdAt: new Date().toISOString()
    });

    render(<App />);

    // Switch to Drivers tab
    const driversTab = await screen.findByRole('button', { name: /drivers/i });
    fireEvent.click(driversTab);

    // Open Register Driver drawer
    const addDriverBtn = await screen.findByRole('button', { name: /add driver/i });
    fireEvent.click(addDriverBtn);

    // Verify OpenAI compatible fields are shown
    const endpointInput = await screen.findByLabelText(/openai endpoint url/i);
    expect(endpointInput).toBeInTheDocument();
    expect(endpointInput).toHaveValue('http://localhost:11434/v1');

    // Click "Test & Fetch Models"
    const testBtn = screen.getByRole('button', { name: /test & fetch models/i });
    fireEvent.click(testBtn);

    // Verify testOpenAiServer was called
    await waitFor(() => {
      expect(api.testOpenAiServer).toHaveBeenCalledWith({
        endpoint: 'http://localhost:11434/v1',
        apiKey: undefined
      });
    });

    // Check success badge and fetched models
    expect(await screen.findByText(/connected successfully! found 2 model\(s\)/i)).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: /fetched model selection/i })).toBeInTheDocument();

    // Fill driver ID and name
    const idInput = screen.getByPlaceholderText(/driver id/i);
    fireEvent.change(idInput, { target: { value: 'ollama-local' } });

    const nameInput = screen.getByPlaceholderText(/display name/i);
    fireEvent.change(nameInput, { target: { value: 'Local Ollama Server' } });

    // Submit registration
    const registerBtn = screen.getByRole('button', { name: /^register$/i });
    fireEvent.click(registerBtn);

    // Verify upsertAgent was called with capabilitiesJson containing endpoint and models
    await waitFor(() => {
      expect(api.upsertAgent).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 'ollama-local',
          name: 'Local Ollama Server',
          capabilitiesJson: expect.stringContaining('llama3.3:70b')
        })
      );
    });
  });

  it('renders custom model override input when creating a session with an OpenAI driver', async () => {
    vi.mocked(api.getAgents).mockResolvedValueOnce([
      {
        id: 'ollama-node',
        name: 'Ollama Node',
        role: 'Autonomous AI Engineer',
        status: 'online',
        runtimeType: 'OpenAiCompatible',
        trustTier: 0,
        personaPrompt: '',
        createdAt: new Date().toISOString(),
        capabilitiesJson: JSON.stringify({
          adapter: 'openai',
          endpoint: 'http://localhost:11434/v1',
          models: [{ id: 'qwen2.5-coder:32b', name: 'Qwen 2.5 Coder 32B' }]
        })
      }
    ]);

    render(<App />);

    // Switch to Drivers tab
    const driversTab = await screen.findByRole('button', { name: /drivers/i });
    fireEvent.click(driversTab);

    // Click driver card to select driver
    const driverCard = await screen.findByText('Ollama Node');
    fireEvent.click(driverCard);

    // Click New Session
    const newSessionBtns = await screen.findAllByRole('button', { name: /new session with ollama node/i });
    expect(newSessionBtns.length).toBeGreaterThan(0);
    fireEvent.click(newSessionBtns[0]);

    // Verify Custom Session Model Input is rendered for OpenAI driver
    const customModelInput = await screen.findByLabelText(/custom session model input/i);
    expect(customModelInput).toBeInTheDocument();

    fireEvent.change(customModelInput, { target: { value: 'deepseek-r1:70b' } });
    expect(customModelInput).toHaveValue('deepseek-r1:70b');
  });
});
