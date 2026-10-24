import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type {
  Agent,
  AgentSession,
  Persona,
  SessionMessage,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from '../types';

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getStatus: vi.fn(),
    getCapabilities: vi.fn().mockResolvedValue({
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
          contextSizes: [
            { id: '128k', label: '128k Tokens', description: '128k context' },
            { id: '200k', label: '200k Tokens', description: '200k context' },
            { id: '1m', label: '1M Tokens', description: '1M context' }
          ],
          models: [
            { id: 'gemini-4-argon', name: 'Gemini 4 Argon', efforts: ['low', 'medium', 'high'], contextSizes: ['200k', '1m'] },
            { id: 'gpt-oss-120b', name: 'GPT-OSS 120b', efforts: ['medium'], contextSizes: ['128k', '200k'] }
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
    getAgentUsage: vi.fn().mockResolvedValue({
      agentId: 'agy-core',
      agentName: 'Agy Lead Architect',
      sessionCount: 3,
      lifetimeTokens: {
        inputTokens: 12400,
        outputTokens: 4200,
        thinkingTokens: 1800,
        totalTokens: 18400,
        totalDurationSeconds: 14.2
      },
      providerQuota: {
        provider: 'agy',
        planName: 'Pro Tier',
        primaryRemainingFraction: 0.88,
        groups: [
          {
            name: 'Main Buckets',
            description: 'Core quota',
            buckets: [
              {
                id: 'gemini-weekly',
                name: 'Gemini Weekly',
                description: 'Standard quota',
                window: 'weekly',
                remainingFraction: 0.88,
                resetTime: '2026-10-24T14:07:55Z'
              }
            ]
          }
        ]
      }
    }),
    getSessions: vi.fn().mockResolvedValue([]),
    getSession: vi.fn().mockResolvedValue(null),
    getSessionMessages: vi.fn().mockResolvedValue([]),
    resetMesh: vi.fn(),
    createAgentSession: vi.fn(),
    updateSessionControls: vi.fn(),
    postSessionMessage: vi.fn(),
    upsertAgent: vi.fn(),
    updateDriverConfig: vi.fn().mockResolvedValue({ adapter: 'openai', maxSteps: 250 }),
    stopSession: vi.fn().mockResolvedValue({ status: 'stopped', sessionId: 's1' }),
    stopCommand: vi.fn().mockResolvedValue({ status: 'stopped', commandId: 'c1' }),
    enqueuePrompt: vi.fn().mockResolvedValue({ session: {}, queue: [], dispatched: false }),
    dequeuePrompt: vi.fn().mockResolvedValue([]),
    sendQueuedPromptNow: vi.fn().mockResolvedValue({ session: {}, queue: [], dispatched: true }),
    submitQuestionAnswer: vi.fn().mockResolvedValue({ toolResult: {}, prompt: {}, session: {} }),
    getSessionArtifacts: vi.fn().mockResolvedValue([]),
    getSessionArtifact: vi.fn().mockResolvedValue({ id: 'art-1', name: 'plan.md', content: '# Plan' }),
    syncSessionArtifact: vi.fn()
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
    signoutRedirect: vi.fn()
  })),
  AppAuthProvider: ({ children }: { children: React.ReactNode }) => children
}));

describe('App - Agent-Centric Session Cockpit MVP', () => {
  const mockHealthyStatus: SystemStatusResponse = {
    status: 'Healthy',
    timestamp: new Date().toISOString(),
    environment: 'Development',
    services: {
      api: { status: 'Healthy', latencyMs: 2, description: 'ASP.NET Core 10 Web API' }
    }
  };

  const mockCaps: SystemCapabilitiesResponse = {
    agentRuntimes: {
      agy: {
        adapter: 'agy',
        name: 'Antigravity CLI (agy)',
        modes: [
          { id: 'auto', label: 'Auto (Autonomous)', description: 'Auto execution' },
          { id: 'plan', label: 'Plan Only', description: 'Plan only' }
        ],
        contextSizes: [
          { id: '128k', label: '128k Tokens', description: '128k context' },
          { id: '200k', label: '200k Tokens', description: '200k context' },
          { id: '1m', label: '1M Tokens', description: '1M context' }
        ],
        models: [
          { id: 'gemini-4-argon', name: 'Gemini 4 Argon', efforts: ['low', 'medium', 'high'], contextSizes: ['200k', '1m'] },
          { id: 'gpt-oss-120b', name: 'GPT-OSS 120b', efforts: ['medium'], contextSizes: ['128k', '200k'] }
        ]
      }
    }
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getStatus).mockResolvedValue(mockHealthyStatus);
    vi.mocked(api.getCapabilities).mockResolvedValue(mockCaps);
    vi.mocked(api.getAgents).mockResolvedValue([]);
    vi.mocked(api.getPersonas).mockResolvedValue([]);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
  });

  it('renders clean empty state when no agents are connected', async () => {
    render(<App />);

    expect(screen.getByText('AgentMesh')).toBeInTheDocument();
    expect(screen.getByText('Session Cockpit MVP')).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText('No Agents Connected')).toBeInTheDocument();
      expect(screen.getByText('Agent Session Observatory')).toBeInTheDocument();
    });
  });

  it('displays API error banner when getStatus fails', async () => {
    vi.mocked(api.getStatus).mockRejectedValueOnce(new Error('Network connection refused'));

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('API Gateway Unreachable:')).toBeInTheDocument();
      expect(screen.getByText('Network connection refused')).toBeInTheDocument();
    });
  });

  it('renders connected agents, their sessions, and full trajectory events (prompt, thought, tool_call, response)', async () => {
    const mockAgents: Agent[] = [
      {
        id: 'agy-core',
        name: 'Agy Lead Architect',
        role: 'Codebase Engineer',
        status: 'online',
        runtimeType: 'CliAgy',
        trustTier: 3,
        createdAt: new Date().toISOString()
      }
    ];

    const mockSessions: AgentSession[] = [
      {
        id: 'session-12345678',
        title: 'Fix OIDC Token Handshake',
        status: 'active',
        metadataJson: '{}',
        creatorAgentId: 'agy-core',
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      }
    ];

    const mockEvents: SessionMessage[] = [
      {
        id: 'ev-1',
        sessionId: 'session-12345678',
        senderAgentId: 'admin',
        eventType: 'prompt',
        content: 'Why is token verification failing?',
        createdAt: new Date().toISOString()
      },
      {
        id: 'ev-2',
        sessionId: 'session-12345678',
        senderAgentId: 'agy-core',
        eventType: 'thought',
        content: 'Inspecting Dex staticClients configuration...',
        createdAt: new Date().toISOString()
      },
      {
        id: 'ev-3',
        sessionId: 'session-12345678',
        senderAgentId: 'agy-core',
        eventType: 'tool_call',
        toolName: 'view_file',
        content: 'Reading dex/config.yaml',
        toolArgsJson: '{"path":"dex/config.yaml"}',
        createdAt: new Date().toISOString()
      },
      {
        id: 'ev-4',
        sessionId: 'session-12345678',
        senderAgentId: 'agy-core',
        eventType: 'response',
        content: 'Removed client secret from public PKCE client.',
        createdAt: new Date().toISOString()
      }
    ];

    vi.mocked(api.getAgents).mockResolvedValue(mockAgents);
    vi.mocked(api.getSessions).mockResolvedValue(mockSessions);
    vi.mocked(api.getSessionMessages).mockResolvedValue(mockEvents);

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Agy Lead Architect')).toBeInTheDocument();
      expect(screen.getAllByText('Fix OIDC Token Handshake').length).toBeGreaterThan(0);
    });

    await waitFor(() => {
      expect(screen.getByText('Why is token verification failing?')).toBeInTheDocument();
      expect(screen.getByText('Inspecting Dex staticClients configuration...')).toBeInTheDocument();
      expect(screen.getByText('view_file')).toBeInTheDocument();
      expect(screen.getByText('Removed client secret from public PKCE client.')).toBeInTheDocument();
    });
  });

  it('triggers resetMesh from Clear Mesh Data header button', async () => {
    vi.mocked(api.resetMesh).mockResolvedValue({
      status: 'reset',
      message: 'Purged'
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Clear Mesh Data')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Clear Mesh Data'));
    await waitFor(() => {
      expect(api.resetMesh).toHaveBeenCalledTimes(1);
    });
  });

  it('renders session parameter badges and token usage telemetry in active session and trajectory', async () => {
    const mockAgents: Agent[] = [
      {
        id: 'agy-core',
        name: 'Agy Lead Architect',
        role: 'Codebase Engineer',
        status: 'online',
        runtimeType: 'CliAgy',
        trustTier: 3,
        createdAt: new Date().toISOString()
      }
    ];

    const mockSessions: AgentSession[] = [
      {
        id: 'sess-telemetry-1',
        title: 'Telemetry Session',
        status: 'active',
        metadataJson: JSON.stringify({
          workingDirectory: '/home/user/workspace/proj',
          model: 'gemini-2.5-pro',
          effort: 'high',
          mode: 'plan',
          cumulativeUsage: {
            inputTokens: 2500,
            outputTokens: 350,
            thinkingTokens: 120,
            totalTokens: 2850,
            totalDurationSeconds: 4.2
          }
        }),
        creatorAgentId: 'agy-core',
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      }
    ];

    const mockEvents: SessionMessage[] = [
      {
        id: 'ev-usage-1',
        sessionId: 'sess-telemetry-1',
        senderAgentId: 'agy-core',
        eventType: 'response',
        content: 'Plan generated successfully.',
        structuredPayloadJson: JSON.stringify({
          usage: {
            inputTokens: 2500,
            outputTokens: 350,
            thinkingTokens: 120,
            totalTokens: 2850,
            durationSeconds: 4.2
          }
        }),
        createdAt: new Date().toISOString()
      }
    ];

    vi.mocked(api.getAgents).mockResolvedValue(mockAgents);
    vi.mocked(api.getSessions).mockResolvedValue(mockSessions);
    vi.mocked(api.getSessionMessages).mockResolvedValue(mockEvents);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Telemetry Session').length).toBeGreaterThan(0);
      expect(screen.getByText('gemini-2.5-pro')).toBeInTheDocument();
      expect(screen.getByText('high')).toBeInTheDocument();
      expect(screen.getByText('plan')).toBeInTheDocument();
      expect(screen.getByText('2,850 tokens (4.2s)')).toBeInTheDocument();
      expect(screen.getByText('⚡ 2,850 tokens (4.2s)')).toBeInTheDocument();
    });
  });

  it('supports dynamic model, effort, and mode controls when creating a session', async () => {
    const mockAgents: Agent[] = [
      {
        id: 'agy-core',
        name: 'Agy Lead Architect',
        role: 'Codebase Engineer',
        status: 'online',
        runtimeType: 'CliAgy',
        trustTier: 3,
        createdAt: new Date().toISOString()
      }
    ];

    vi.mocked(api.getAgents).mockResolvedValue(mockAgents);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.createAgentSession).mockResolvedValue({
      id: 'sess-new-1',
      title: 'Dynamic Controls Session',
      status: 'active',
      metadataJson: '{}',
      creatorAgentId: 'agy-core',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Agy Lead Architect')).toBeInTheDocument();
    });

    // Open New Session composer
    const newSessionBtns = screen.getAllByRole('button', { name: /New Session with Agy Lead Architect/i });
    fireEvent.click(newSessionBtns[0]);

    expect(screen.getByText('Start New Session with Agy Lead Architect')).toBeInTheDocument();

    // Model, Effort, and Mode inputs are selects
    const allSelects = screen.getAllByRole('combobox');
    const modelDropdown = allSelects[0];
    const effortDropdown = allSelects[1];
    const modeDropdown = allSelects[2];

    expect(modelDropdown).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Default Model' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Gemini 4 Argon' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'GPT-OSS 120b' })).toBeInTheDocument();

    // Initially with default model, effort is disabled (Not applicable)
    expect(effortDropdown).toBeDisabled();
    expect(screen.getByRole('option', { name: 'Not applicable' })).toBeInTheDocument();

    // Mode dropdown shows runtime modes
    expect(screen.getByRole('option', { name: 'Auto (Autonomous)' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Plan Only' })).toBeInTheDocument();

    // Select Gemini 4 Argon -> Effort enables with low, medium, high
    fireEvent.change(modelDropdown, { target: { value: 'gemini-4-argon' } });
    expect(effortDropdown).not.toBeDisabled();
    expect(screen.getByRole('option', { name: 'low' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'medium' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'high' })).toBeInTheDocument();

    // Select GPT-OSS 120b -> Effort updates to ONLY medium
    fireEvent.change(modelDropdown, { target: { value: 'gpt-oss-120b' } });
    expect(effortDropdown).not.toBeDisabled();
    expect(screen.queryByRole('option', { name: 'low' })).not.toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'high' })).not.toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'medium' })).toBeInTheDocument();
    fireEvent.change(effortDropdown, { target: { value: 'medium' } });

    // Select Mode
    fireEvent.change(modeDropdown, { target: { value: 'plan' } });

    // Fill title and submit
    fireEvent.change(screen.getByPlaceholderText(/Refactor authentication middleware/i), {
      target: { value: 'Dynamic Session Test' }
    });

    fireEvent.click(screen.getByRole('button', { name: /Launch Session/i }));

    await waitFor(() => {
      expect(api.createAgentSession).toHaveBeenCalledWith(
        'agy-core',
        'Dynamic Session Test',
        undefined,
        {
          model: 'gpt-oss-120b',
          effort: 'medium',
          mode: 'plan',
          tier: undefined
        }
      );
    });
  });

  it('displays agent quota and usage badge on sidebar card and opens detail modal', async () => {
    const mockAgent: Agent = {
      id: 'agy-core',
      name: 'Agy Lead Architect',
      role: 'Principal Engineer',
      status: 'online',
      runtimeType: 'CliAgy',
      trustTier: 4,
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getAgentUsage).mockResolvedValue({
      agentId: 'agy-core',
      agentName: 'Agy Lead Architect',
      sessionCount: 5,
      lifetimeTokens: {
        inputTokens: 12400,
        outputTokens: 4200,
        thinkingTokens: 1800,
        totalTokens: 18400,
        totalDurationSeconds: 14.2
      },
      providerQuota: {
        provider: 'agy',
        planName: 'Pro Tier',
        primaryRemainingFraction: 0.88,
        groups: [
          {
            name: 'Core Buckets',
            description: null,
            buckets: [
              {
                id: 'gemini-weekly',
                name: 'Gemini Weekly',
                description: 'Weekly quota limit',
                window: 'weekly',
                remainingFraction: 0.88,
                resetTime: '2026-10-24T14:07:55Z'
              }
            ]
          }
        ]
      }
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText(/88% quota · 18.4k tokens/i)).toBeInTheDocument();
    });

    // Click the quota pill
    fireEvent.click(screen.getByText(/88% quota · 18.4k tokens/i));

    // Verify modal is open
    await waitFor(() => {
      expect(screen.getByRole('dialog', { name: 'Agent Quota and Usage Details' })).toBeInTheDocument();
      expect(screen.getByText('Lifetime Token Consumption')).toBeInTheDocument();
      expect(screen.getByText('18,400')).toBeInTheDocument();
      expect(screen.getByText('88.0% Remaining')).toBeInTheDocument();
      expect(screen.getByText('Gemini Weekly')).toBeInTheDocument();
    });

    // Close modal
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(screen.queryByRole('dialog', { name: 'Agent Quota and Usage Details' })).not.toBeInTheDocument();
  });

  it('allows changing model and effort mid-flight from active session header', async () => {
    const mockAgent: Agent = {
      id: 'agy-core',
      name: 'Agy Lead Architect',
      status: 'online',
      runtimeType: 'CliAgy',
      trustTier: 4,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-42',
      title: 'Active Optimization Task',
      status: 'active',
      metadataJson: JSON.stringify({
        model: 'gemini-4-argon',
        effort: 'medium'
      }),
      creatorAgentId: 'agy-core',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.updateSessionControls).mockResolvedValue({
      ...mockSession,
      metadataJson: JSON.stringify({
        model: 'gpt-oss-120b',
        effort: 'medium'
      })
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Active Optimization Task').length).toBeGreaterThan(0);
      expect(screen.getByText('gemini-4-argon')).toBeInTheDocument();
    });

    // Click model badge to open popover
    fireEvent.click(screen.getByRole('button', { name: /gemini-4-argon/i }));

    expect(screen.getByText('Switch Session Model')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /GPT-OSS 120b/i })).toBeInTheDocument();

    // Select GPT-OSS 120b
    fireEvent.click(screen.getByRole('button', { name: /GPT-OSS 120b/i }));

    await waitFor(() => {
      expect(api.updateSessionControls).toHaveBeenCalledWith('session-42', {
        model: 'gpt-oss-120b'
      });
    });
  });

  it('renders Copilot optimization tiers when runtime defines tiers', async () => {
    const mockCopilotAgent: Agent = {
      id: 'copilot-cli',
      name: 'GitHub Copilot CLI',
      role: 'Copilot Assistant',
      status: 'online',
      runtimeType: 'CliCopilot',
      trustTier: 3,
      createdAt: new Date().toISOString()
    };

    const mockCopilotCaps: SystemCapabilitiesResponse = {
      agentRuntimes: {
        copilot: {
          adapter: 'copilot',
          name: 'GitHub Copilot CLI',
          modes: [{ id: 'autopilot', label: 'Autopilot', description: 'Auto' }],
          models: [{ id: 'gpt-6.1-sol', name: 'GPT 6.1 Sol', efforts: ['low', 'medium', 'high'] }],
          tiers: [
            { id: 'auto', label: 'Auto (Recommended)', description: 'Auto optimize' },
            { id: 'efficiency', label: 'Efficiency', description: 'Fast' },
            { id: 'intelligence', label: 'Intelligence', description: 'Deep reasoning' }
          ]
        }
      }
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockCopilotAgent]);
    vi.mocked(api.getCapabilities).mockResolvedValue(mockCopilotCaps);

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('GitHub Copilot CLI')).toBeInTheDocument();
    });

    // Open New Session composer
    const newSessionBtns = screen.getAllByRole('button', { name: /New Session with GitHub Copilot CLI/i });
    fireEvent.click(newSessionBtns[0]);

    await waitFor(() => {
      expect(screen.getByText('Start New Session with GitHub Copilot CLI')).toBeInTheDocument();
      expect(screen.getByText('Optimize For (Tier)')).toBeInTheDocument();
      expect(screen.getByRole('option', { name: 'Auto (Recommended)' })).toBeInTheDocument();
      expect(screen.getByRole('option', { name: 'Efficiency' })).toBeInTheDocument();
      expect(screen.getByRole('option', { name: 'Intelligence' })).toBeInTheDocument();
    });
  });

  it('renders Stop button beside input bar disabled when idle, enabled when thinking, and stops session on click', async () => {
    const mockAgent: Agent = {
      id: 'agy',
      name: 'Antigravity CLI',
      runtimeType: 1,
      trustTier: 1,
      status: 'busy',
      createdAt: new Date().toISOString()
    };

    const mockThinkingSession: AgentSession = {
      id: 'session-thinking-1',
      title: 'Thinking Session',
      status: 'thinking',
      metadataJson: JSON.stringify({ model: 'gemini-4-argon' }),
      creatorAgentId: 'agy',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockThinkingSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Thinking Session').length).toBeGreaterThan(0);
    });

    // Select the session
    fireEvent.click(screen.getAllByText('Thinking Session')[0]);

    await waitFor(() => {
      const stopBtn = screen.getByRole('button', { name: /stop/i });
      expect(stopBtn).toBeInTheDocument();
      expect(stopBtn).not.toBeDisabled();
    });

    const stopButton = screen.getByRole('button', { name: /stop/i });
    fireEvent.click(stopButton);

    expect(api.stopSession).toHaveBeenCalledWith('session-thinking-1');
  });

  it('transforms Send button into Queue when session is thinking, and supports split dropdown for Send Now', async () => {
    const mockAgent: Agent = {
      id: 'agy',
      name: 'Antigravity CLI',
      runtimeType: 1,
      trustTier: 1,
      status: 'busy',
      createdAt: new Date().toISOString()
    };

    const mockThinkingSession: AgentSession = {
      id: 'session-thinking-2',
      title: 'Thinking Session 2',
      status: 'thinking',
      metadataJson: JSON.stringify({ model: 'gemini-4-argon' }),
      creatorAgentId: 'agy',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockThinkingSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
    vi.mocked(api.enqueuePrompt).mockResolvedValue({
      session: mockThinkingSession,
      queue: [{ id: 'q1', prompt: 'Queued prompt message', sender: 'test', createdAt: new Date().toISOString() }],
      dispatched: false
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Thinking Session 2').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Thinking Session 2')[0]);

    // Input prompt
    const input = await screen.findByPlaceholderText(/Add instruction to queue for Antigravity CLI/i);
    fireEvent.change(input, { target: { value: 'Followup instruction' } });

    // The submit button is now "Queue"
    const queueBtn = screen.getByRole('button', { name: /^queue$/i });
    expect(queueBtn).toBeInTheDocument();

    // The dropdown toggle button exists
    const dropdownToggle = screen.getByTitle('More sending options');
    fireEvent.click(dropdownToggle);

    // "Send Now (Interrupt)" option appears
    const sendNowOption = await screen.findByText(/Send Now \(Interrupt\)/i);
    expect(sendNowOption).toBeInTheDocument();

    // Clicking Send Now calls enqueuePrompt with sendNow: true
    fireEvent.click(sendNowOption);

    expect(api.enqueuePrompt).toHaveBeenCalledWith('session-thinking-2', expect.objectContaining({
      prompt: 'Followup instruction',
      sendNow: true
    }));
  });

  it('renders queued prompts strip with items, Send Now button, and Remove button', async () => {
    const mockAgent: Agent = {
      id: 'agy',
      name: 'Antigravity CLI',
      runtimeType: 1,
      trustTier: 1,
      status: 'online',
      createdAt: new Date().toISOString()
    };

    const mockSessionWithQueue: AgentSession = {
      id: 'session-queued-1',
      title: 'Session With Queue',
      status: 'active',
      metadataJson: JSON.stringify({
        promptQueue: [
          { id: 'item-1', prompt: 'First queued message', sender: 'operator', createdAt: new Date().toISOString() },
          { id: 'item-2', prompt: 'Second queued message', sender: 'operator', createdAt: new Date().toISOString() }
        ]
      }),
      creatorAgentId: 'agy',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSessionWithQueue]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Session With Queue').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Session With Queue')[0]);

    await waitFor(() => {
      expect(screen.getByText(/Prompt Queue \(2\)/i)).toBeInTheDocument();
      expect(screen.getByText('First queued message')).toBeInTheDocument();
      expect(screen.getByText('Second queued message')).toBeInTheDocument();
    });

    // Test clicking Send Now on the first queued item
    const sendNowBtns = screen.getAllByRole('button', { name: /Send Now/i });
    fireEvent.click(sendNowBtns[0]);
    expect(api.sendQueuedPromptNow).toHaveBeenCalledWith('session-queued-1', 'item-1');

    // Test clicking remove on the second queued item
    const removeBtns = screen.getAllByTitle('Remove from queue');
    fireEvent.click(removeBtns[1]);
    expect(api.dequeuePrompt).toHaveBeenCalledWith('session-queued-1', 'item-2');
  });

  it('renders active command pill with Kill Command button when model is running a command', async () => {
    const mockAgent: Agent = {
      id: 'agy',
      name: 'Antigravity CLI',
      runtimeType: 1,
      trustTier: 1,
      status: 'busy',
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-cmd-1',
      title: 'Active Command Session',
      status: 'thinking',
      metadataJson: JSON.stringify({}),
      creatorAgentId: 'agy',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const mockToolCallMessage: SessionMessage = {
      id: 'msg-cmd-1',
      sessionId: 'session-cmd-1',
      senderAgentId: 'agy',
      eventType: 'tool_call',
      toolName: 'run_command',
      content: 'Running command: npm test',
      toolArgsJson: JSON.stringify({ CommandLine: 'npm test' }),
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([mockToolCallMessage]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Active Command Session').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Active Command Session')[0]);

    await waitFor(() => {
      expect(screen.getByText('Running command:')).toBeInTheDocument();
      expect(screen.getByText('npm test')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Kill Command/i })).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Kill Command/i }));
    expect(api.stopCommand).toHaveBeenCalledWith('session-cmd-1', 'msg-cmd-1');
  });

  it('renders interactive question card for ask_question tool call and allows submitting answers', async () => {
    const mockAgent: Agent = {
      id: 'agy-qa',
      name: 'Agy QA Agent',
      status: 'online',
      runtimeType: 1,
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-qa-1',
      title: 'Database Setup Discussion',
      status: 'waiting_for_input',
      creatorAgentId: 'agy-qa',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const mockQuestionCall: SessionMessage = {
      id: 'msg-question-1',
      sessionId: 'session-qa-1',
      senderAgentId: 'agy-qa',
      eventType: 'tool_call',
      toolName: 'ask_question',
      content: 'Which database would you prefer for the service?',
      toolArgsJson: JSON.stringify({
        questions: [
          {
            question: 'Which database would you prefer?',
            options: ['PostgreSQL', 'SQLite', 'In-Memory'],
            is_multi_select: false
          }
        ]
      }),
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([mockQuestionCall]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Database Setup Discussion').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Database Setup Discussion')[0]);

    await waitFor(() => {
      expect(screen.getByText(/Agent Question — Human Input Required/i)).toBeInTheDocument();
      expect(screen.getByText('Which database would you prefer?')).toBeInTheDocument();
      expect(screen.getByText('PostgreSQL')).toBeInTheDocument();
      expect(screen.getByText('SQLite')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Submit Answer/i })).toBeInTheDocument();
    });

    // Select PostgreSQL option
    fireEvent.click(screen.getByText('PostgreSQL'));

    // Submit Answer
    fireEvent.click(screen.getByRole('button', { name: /Submit Answer/i }));

    expect(api.submitQuestionAnswer).toHaveBeenCalledWith('session-qa-1', {
      toolCallId: 'msg-question-1',
      answers: [
        {
          questionIndex: 0,
          question: 'Which database would you prefer?',
          selectedOptions: ['PostgreSQL'],
          customText: ''
        }
      ]
    });
  });

  it('renders interactive question card for agent text response containing questions (e.g. Copilot)', async () => {
    const mockAgent: Agent = {
      id: 'copilot',
      name: 'GitHub Copilot',
      status: 'online',
      runtimeType: 'CliCopilot',
      trustTier: 2,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-copilot-dinner',
      title: 'Dinner Planning',
      status: 'waiting_for_input',
      creatorAgentId: 'copilot',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const mockResponseWithQuestions: SessionMessage = {
      id: 'msg-dinner-1',
      sessionId: 'session-copilot-dinner',
      senderAgentId: 'copilot',
      eventType: 'response',
      content: `I can help you decide! To narrow it down:
1. Cuisine type (Italian, Mexican, Asian, etc.)?
2. Dietary preferences or restrictions (vegetarian, vegan, gluten-free, etc.)?
3. Cooking at home or dining out / takeout?
4. Fast & easy vs. elaborate?
Let me know what sounds good, or I can suggest a few quick ideas right now!
(Note: I can't see your replies in this non-interactive session. Run me interactively in your terminal to continue the conversation.)`,
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([mockResponseWithQuestions]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Dinner Planning').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Dinner Planning')[0]);

    await waitFor(() => {
      expect(screen.getByText(/I can help you decide/i)).toBeInTheDocument();
      expect(screen.getByText(/Agent Question — Human Input Required/i)).toBeInTheDocument();
      expect(screen.getByText('Italian')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Submit Answer/i })).toBeInTheDocument();
    });

    // Select Italian
    fireEvent.click(screen.getByText('Italian'));

    // Submit Answer
    fireEvent.click(screen.getByRole('button', { name: /Submit Answer/i }));

    expect(api.submitQuestionAnswer).toHaveBeenCalledWith('session-copilot-dinner', {
      toolCallId: 'msg-dinner-1',
      answers: expect.arrayContaining([
        expect.objectContaining({
          questionIndex: 0,
          selectedOptions: ['Italian']
        })
      ])
    });
  });

  it('renders slash command autocomplete palette when typing / and executes /mesh commands', async () => {
    const mockAgent: Agent = {
      id: 'agy',
      name: 'Antigravity Lead',
      role: 'Codebase Engineer',
      status: 'online',
      runtimeType: 'agy',
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-slash-1',
      title: 'Slash Command Test',
      status: 'active',
      creatorAgentId: 'agy',
      metadataJson: JSON.stringify({ model: 'gemini-4-argon' }),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Slash Command Test').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Slash Command Test')[0]);

    const input = screen.getByPlaceholderText(/type \/ for commands/i);

    // Type "/" to trigger palette
    fireEvent.change(input, { target: { value: '/' } });

    await waitFor(() => {
      expect(screen.getByTestId('slash-command-palette')).toBeInTheDocument();
      expect(screen.getByText('/mesh stop')).toBeInTheDocument();
      expect(screen.getByText('/plan')).toBeInTheDocument();
      expect(screen.getByText('/grill-me')).toBeInTheDocument();
    });

    // Execute /mesh mode plan
    fireEvent.change(input, { target: { value: '/mesh mode plan' } });
    fireEvent.submit(input.closest('form')!);

    await waitFor(() => {
      expect(api.updateSessionControls).toHaveBeenCalledWith('session-slash-1', { mode: 'plan' });
    });

    // Execute \mesh effort (no args) -> should intercept and display reasoning effort card without forwarding as prompt
    fireEvent.change(input, { target: { value: '\\mesh effort' } });
    fireEvent.submit(input.closest('form')!);

    await waitFor(() => {
      expect(screen.getByText(/Reasoning Effort Configuration/i)).toBeInTheDocument();
      // Verify it was NOT posted as a prompt to the agent runner
      expect(api.postSessionMessage).not.toHaveBeenCalledWith(
        'session-slash-1',
        '\\mesh effort',
        expect.anything(),
        expect.anything(),
        'prompt'
      );
    });

    // Execute /mesh effort high -> updates controls
    fireEvent.change(input, { target: { value: '/mesh effort high' } });
    fireEvent.submit(input.closest('form')!);

    await waitFor(() => {
      expect(api.updateSessionControls).toHaveBeenCalledWith('session-slash-1', { effort: 'high' });
    });
  });

  it('toggles resizable artifacts split panel and renders markdown and mermaid diagram', async () => {
    const mockAgent: Agent = {
      id: 'agy',
      name: 'Antigravity Lead',
      role: 'Codebase Engineer',
      status: 'online',
      runtimeType: 'agy',
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-art-1',
      title: 'Artifact Session',
      status: 'active',
      creatorAgentId: 'agy',
      metadataJson: '{}',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const mockArtifact = {
      id: 'art-1',
      sessionId: 'session-art-1',
      name: 'architecture_plan.md',
      summary: 'Architecture specification with Mermaid flow',
      contentType: 'text/markdown',
      sizeBytes: 1024,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      content: '# Architecture Specification\n\n```mermaid\ngraph TD\nClient-->API\n```\n\n- Step 1: Initialize\n- Step 2: Connect'
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
    vi.mocked(api.getSessionArtifacts).mockResolvedValue([mockArtifact]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Artifact Session').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Artifact Session')[0]);

    // Find and click Artifacts button in header
    const artifactsButton = await screen.findByRole('button', { name: /Artifacts/i });
    fireEvent.click(artifactsButton);

    await waitFor(() => {
      expect(screen.getByTestId('artifact-viewer')).toBeInTheDocument();
      expect(screen.getByText('architecture_plan.md')).toBeInTheDocument();
      expect(screen.getByText('Architecture specification with Mermaid flow')).toBeInTheDocument();
      expect(screen.getByText(/Mermaid Architecture Diagram/i)).toBeInTheDocument();
      expect(screen.getByRole('heading', { name: /Architecture Specification/i })).toBeInTheDocument();
    });

    // Toggle Raw view
    const rawButton = screen.getByRole('button', { name: /Raw/i });
    fireEvent.click(rawButton);

    await waitFor(() => {
      expect(screen.getByText(/# Architecture Specification/i)).toBeInTheDocument();
    });
  });

  it('supports creating a Persona, launching a session with Persona default model, and viewing sessions at both Persona and Driver levels', async () => {
    const mockDriver: Agent = {
      id: 'agy',
      name: 'Antigravity CLI',
      role: 'Autonomous Codebase Driver',
      status: 'online',
      runtimeType: 'CliAgy',
      trustTier: 3,
      createdAt: new Date().toISOString()
    };

    const mockPersona: Persona = {
      id: 'sec-auditor',
      name: 'Security Auditor',
      role: 'Principal AppSec Architect',
      personaPrompt: 'Review all authentication and token flows.',
      driverId: 'agy',
      driverName: 'Antigravity CLI',
      driverStatus: 'online',
      defaultModel: 'gemini-4-argon',
      defaultEffort: 'high',
      defaultMode: 'plan',
      defaultContextSize: '1m',
      trustTier: 3,
      sessionCount: 1,
      createdAt: new Date().toISOString()
    };

    const mockPersonaSession: AgentSession = {
      id: 'sess-persona-1',
      title: 'Audit OIDC Handshake',
      status: 'active',
      personaId: 'sec-auditor',
      personaName: 'Security Auditor',
      driverId: 'agy',
      driverName: 'Antigravity CLI',
      creatorAgentId: 'agy',
      metadataJson: JSON.stringify({
        personaId: 'sec-auditor',
        personaName: 'Security Auditor',
        model: 'gemini-4-argon',
        effort: 'high',
        mode: 'plan',
        contextSize: '1m'
      }),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockDriver]);
    vi.mocked(api.getPersonas).mockResolvedValue([mockPersona]);
    vi.mocked(api.getSessions).mockResolvedValue([mockPersonaSession]);
    vi.mocked(api.createPersonaSession).mockResolvedValue({
      id: 'sess-persona-2',
      title: 'Review JWT Scopes',
      status: 'active',
      personaId: 'sec-auditor',
      driverId: 'agy',
      creatorAgentId: 'agy',
      metadataJson: '{}',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    });

    render(<App />);

    // 1. Verify Personas & Sessions tab renders Persona card and its session
    await waitFor(() => {
      expect(screen.getByText('Security Auditor')).toBeInTheDocument();
      expect(screen.getAllByText('Audit OIDC Handshake').length).toBeGreaterThan(0);
    });

    // Verify Active Session Header displays both Persona and Driver
    expect(screen.getAllByText('@sec-auditor').length).toBeGreaterThan(0);
    expect(screen.getAllByText('@agy').length).toBeGreaterThan(0);

    // 2. Click "+" on Persona to start a new session and verify Persona's default model/effort/mode are pre-selected
    const newPersonaSessionBtn = screen.getByTitle('New Session with Security Auditor');
    fireEvent.click(newPersonaSessionBtn);

    expect(screen.getByText('Start New Session with Security Auditor')).toBeInTheDocument();
    const selects = screen.getAllByRole('combobox');
    expect((selects[0] as HTMLSelectElement).value).toBe('gemini-4-argon');
    expect((selects[1] as HTMLSelectElement).value).toBe('high');
    expect((selects[2] as HTMLSelectElement).value).toBe('plan');
    expect((selects[3] as HTMLSelectElement).value).toBe('1m');

    // Can also access any other model on the Driver (e.g. gpt-oss-120b)
    expect(screen.getByRole('option', { name: 'GPT-OSS 120b' })).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText(/Refactor authentication middleware/i), {
      target: { value: 'Review JWT Scopes' }
    });
    fireEvent.click(screen.getByRole('button', { name: /Launch Session/i }));

    await waitFor(() => {
      expect(api.createPersonaSession).toHaveBeenCalledWith(
        'sec-auditor',
        'Review JWT Scopes',
        undefined,
        expect.objectContaining({
          model: 'gemini-4-argon',
          effort: 'high',
          mode: 'plan',
          contextSize: '1m'
        })
      );
    });

    // 3. Switch to "Drivers & Infrastructure" tab and verify the Driver shows associated Persona badge and session
    fireEvent.click(screen.getByRole('button', { name: /Drivers & Infrastructure/i }));

    await waitFor(() => {
      expect(screen.getByText('Antigravity CLI')).toBeInTheDocument();
      expect(screen.getAllByText('Audit OIDC Handshake').length).toBeGreaterThan(0);
      expect(screen.getAllByText('@sec-auditor').length).toBeGreaterThan(0);
    });
  });

  it('enforces mutual exclusivity between option pills and write-in input for single-select questions', async () => {
    const mockAgent: Agent = {
      id: 'agy-qa',
      name: 'Agy QA Agent',
      status: 'online',
      runtimeType: 1,
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-qa-mut',
      title: 'Mutual Exclusivity Test',
      status: 'waiting_for_input',
      creatorAgentId: 'agy-qa',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const mockQuestionCall: SessionMessage = {
      id: 'msg-question-mut',
      sessionId: 'session-qa-mut',
      senderAgentId: 'agy-qa',
      eventType: 'tool_call',
      toolName: 'ask_question',
      content: 'Choose one storage provider:',
      toolArgsJson: JSON.stringify({
        questions: [
          {
            question: 'Choose one storage provider:',
            options: ['S3', 'GCS', 'Azure Blob'],
            is_multi_select: false
          }
        ]
      }),
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([mockQuestionCall]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Mutual Exclusivity Test').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Mutual Exclusivity Test')[0]);

    await waitFor(() => {
      expect(screen.getByText('S3')).toBeInTheDocument();
      expect(screen.getByPlaceholderText(/Or type a custom write-in response.../i)).toBeInTheDocument();
    });

    const writeInInput = screen.getByPlaceholderText(/Or type a custom write-in response.../i) as HTMLInputElement;

    // 1. Select 'S3' option pill
    fireEvent.click(screen.getByText('S3'));

    // 2. Change mind and type a write-in: 'MinIO'
    fireEvent.change(writeInInput, { target: { value: 'MinIO' } });

    // Submit Answer
    fireEvent.click(screen.getByRole('button', { name: /Submit Answer/i }));

    expect(api.submitQuestionAnswer).toHaveBeenCalledWith('session-qa-mut', {
      toolCallId: 'msg-question-mut',
      answers: [
        {
          questionIndex: 0,
          question: 'Choose one storage provider:',
          selectedOptions: [],
          customText: 'MinIO'
        }
      ]
    });
  });

  it('supports deleting a session from sidebar with inline confirmation', async () => {
    const mockAgent: Agent = {
      id: 'agy-del',
      name: 'Agy Delete Agent',
      status: 'online',
      runtimeType: 1,
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockPersona: Persona = {
      id: 'del-persona',
      name: 'Delete Test Persona',
      role: 'Engineer',
      personaPrompt: 'Delete test prompt',
      trustTier: 1,
      driverId: 'agy-del',
      driverName: 'Agy Delete Agent',
      driverStatus: 'online',
      createdAt: new Date().toISOString()
    };

    const mockSession1: AgentSession = {
      id: 'session-del-1',
      title: 'Session to Remove',
      status: 'active',
      creatorAgentId: 'agy-del',
      driverId: 'agy-del',
      personaId: 'del-persona',
      metadataJson: JSON.stringify({}),
      createdAt: new Date(Date.now() - 1000).toISOString(),
      updatedAt: new Date(Date.now() - 1000).toISOString()
    };

    const mockSession2: AgentSession = {
      id: 'session-del-2',
      title: 'Session Sibling',
      status: 'active',
      creatorAgentId: 'agy-del',
      driverId: 'agy-del',
      personaId: 'del-persona',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getPersonas).mockResolvedValue([mockPersona]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession1, mockSession2]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
    vi.mocked(api.deleteSession).mockResolvedValue({ status: 'deleted', id: 'session-del-1' });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Delete Test Persona')).toBeInTheDocument();
      expect(screen.getByTitle('Delete session Session to Remove')).toBeInTheDocument();
    });

    // Find delete button for Session to Remove
    const deleteBtn = screen.getByTitle('Delete session Session to Remove');
    fireEvent.click(deleteBtn);

    // Verify confirmation banner appears
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Confirm Delete/i })).toBeInTheDocument();
    });

    // Confirm deletion
    fireEvent.click(screen.getByRole('button', { name: /Confirm Delete/i }));

    await waitFor(() => {
      expect(api.deleteSession).toHaveBeenCalledWith('session-del-1');
      expect(screen.queryByTitle('Delete session Session to Remove')).not.toBeInTheDocument();
    });
  });

  it('does not trigger question card on bullet lists with markdown links and embedded question marks in completed turns', async () => {
    const mockAgent: Agent = {
      id: 'agy-core',
      name: 'AgentMesh Core',
      status: 'online',
      runtimeType: 'CliAntigravity',
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-summary-1',
      title: 'Implementation Summary',
      status: 'completed',
      creatorAgentId: 'agy-core',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const mockSummaryResponse: SessionMessage = {
      id: 'msg-summary-1',
      sessionId: 'session-summary-1',
      senderAgentId: 'agy-core',
      eventType: 'response',
      content: `All features and bug fixes have been implemented:
* **Sidebar UI**: Added hover trash icons to every session row in both Personas and Drivers tabs in [App.tsx](file:///home/ckingdon/projects/agent-mesh/src/AgentMesh.Dashboard/src/App.tsx). Clicking shows an inline confirmation banner ('Delete {title}? [Cancel] [Confirm Delete]').
* **Verification**: All tests passed with zero errors.`,
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([mockSummaryResponse]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Implementation Summary').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Implementation Summary')[0]);

    await waitFor(() => {
      expect(screen.getByText(/All features and bug fixes have been implemented/i)).toBeInTheDocument();
    });

    // Verify false positive question card is NOT rendered
    expect(screen.queryByText(/Agent Question — Human Input Required/i)).not.toBeInTheDocument();
    expect(screen.queryByText('file:')).not.toBeInTheDocument();
    expect(screen.queryByText('App.tsx')).not.toBeInTheDocument();
  });

  it('toggles auto-scroll on and off, updates localStorage, and toggles button label', async () => {
    const mockAgent: Agent = {
      id: 'agy-core',
      name: 'AgentMesh Core',
      status: 'online',
      runtimeType: 'CliAntigravity',
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-scroll-1',
      title: 'Auto Scroll Test Session',
      status: 'completed',
      creatorAgentId: 'agy-core',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Auto Scroll Test Session').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Auto Scroll Test Session')[0]);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Toggle Auto-scroll/i })).toBeInTheDocument();
    });

    const toggleBtn = screen.getByRole('button', { name: /Toggle Auto-scroll/i });
    expect(toggleBtn).toHaveTextContent('Auto-scroll: On');

    // Click to turn OFF
    fireEvent.click(toggleBtn);
    expect(toggleBtn).toHaveTextContent('Auto-scroll: Off');
    expect(localStorage.getItem('agentmesh_autoscroll_enabled')).toBe('false');

    // Click to turn back ON
    fireEvent.click(toggleBtn);
    expect(toggleBtn).toHaveTextContent('Auto-scroll: On');
    expect(localStorage.getItem('agentmesh_autoscroll_enabled')).toBe('true');
  });

  it('stages attachments via file picker or clipboard paste, enables sending attachment-only prompts, and opens image lightbox from timeline', async () => {
    const mockAgent: Agent = {
      id: 'agy-core',
      name: 'AgentMesh Core',
      status: 'online',
      runtimeType: 'CliAntigravity',
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-attach-1',
      title: 'Multimodal Attachment Session',
      status: 'active',
      creatorAgentId: 'agy-core',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const uploadedAttachment = {
      id: 'att-img-1',
      sessionId: 'session-attach-1',
      fileName: 'ui-mockup.png',
      contentType: 'image/png',
      sizeBytes: 2048,
      s3Key: 'sessions/session-attach-1/attachments/att-img-1/ui-mockup.png',
      downloadUrl: '/api/sessions/session-attach-1/attachments/att-img-1/content',
      createdAt: new Date().toISOString()
    };

    const mockCreatedMessage: SessionMessage = {
      id: 'msg-with-att-1',
      sessionId: 'session-attach-1',
      senderAgentId: 'admin',
      eventType: 'prompt',
      content: 'Please review the attached file(s).',
      structuredPayloadJson: JSON.stringify({
        attachments: [uploadedAttachment]
      }),
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
    vi.mocked(api.uploadSessionAttachments).mockResolvedValue([uploadedAttachment]);
    vi.mocked(api.postSessionMessage).mockResolvedValue(mockCreatedMessage);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Multimodal Attachment Session').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Multimodal Attachment Session')[0]);

    const fileInput = await screen.findByTestId('chat-file-input');
    const sendBtn = screen.getByRole('button', { name: /^Send$/i });
    expect(sendBtn).toBeDisabled();

    // Stage an image file via the file input
    const testFile = new File(['fake-png-bytes'], 'ui-mockup.png', { type: 'image/png' });
    fireEvent.change(fileInput, { target: { files: [testFile] } });

    await waitFor(() => {
      expect(api.uploadSessionAttachments).toHaveBeenCalledWith('session-attach-1', [testFile]);
      expect(screen.getByTestId('chat-staged-attachments')).toBeInTheDocument();
      expect(screen.getByText('ui-mockup.png')).toBeInTheDocument();
    });

    // Send button is now enabled even though text input is empty
    await waitFor(() => {
      expect(sendBtn).not.toBeDisabled();
    });

    fireEvent.click(sendBtn);

    await waitFor(() => {
      expect(api.postSessionMessage).toHaveBeenCalledWith(
        'session-attach-1',
        'Please review the attached file(s).',
        'agy-core',
        expect.any(String),
        'prompt',
        [uploadedAttachment]
      );
    });

    // Inline image thumbnail is rendered in the message timeline
    const zoomBtn = await screen.findByRole('button', { name: /Zoom image ui-mockup.png/i });
    expect(zoomBtn).toBeInTheDocument();

    // Clicking the inline thumbnail opens the Fullscreen Image Lightbox modal
    fireEvent.click(zoomBtn);
    const lightbox = await screen.findByTestId('attachment-lightbox');
    expect(lightbox).toBeInTheDocument();
    expect(screen.getByTestId('lightbox-image')).toHaveAttribute(
      'src',
      '/api/sessions/session-attach-1/attachments/att-img-1/content'
    );

    // Close the lightbox
    fireEvent.click(screen.getByRole('button', { name: /Close image preview/i }));
    await waitFor(() => {
      expect(screen.queryByTestId('attachment-lightbox')).not.toBeInTheDocument();
    });
  });

  it('correctly parses and renders attachments serialized with PascalCase or missing downloadUrl without producing undefined in URL or 0 B', async () => {
    const mockAgent: Agent = {
      id: 'agy-core',
      name: 'AgentMesh Core',
      status: 'online',
      runtimeType: 'CliAntigravity',
      trustTier: 1,
      createdAt: new Date().toISOString()
    };

    const mockSession: AgentSession = {
      id: 'session-pascal-att',
      title: 'Pascal Case Attachment Session',
      status: 'active',
      creatorAgentId: 'agy-core',
      metadataJson: JSON.stringify({}),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    const pascalMessage: SessionMessage = {
      id: 'msg-pascal-1',
      sessionId: 'session-pascal-att',
      senderAgentId: 'admin',
      eventType: 'prompt',
      content: 'give me a 1 sentence overview',
      structuredPayloadJson: JSON.stringify({
        attachments: [
          {
            Id: 'att-doc-999',
            SessionId: 'session-pascal-att',
            FileName: 'financial-report.pdf',
            ContentType: 'application/pdf',
            SizeBytes: 1048576,
            S3Key: 'sessions/session-pascal-att/attachments/att-doc-999/financial-report.pdf',
            DownloadUrl: '/api/sessions/session-pascal-att/attachments/att-doc-999/content',
            CreatedAt: new Date().toISOString()
          }
        ]
      }),
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([mockSession]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([pascalMessage]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Pascal Case Attachment Session').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getAllByText('Pascal Case Attachment Session')[0]);

    await waitFor(() => {
      // Filename should be rendered
      expect(screen.getByText('financial-report.pdf')).toBeInTheDocument();
      // Size should be formatted (1.0 MB, NOT 0 B)
      expect(screen.getByText('1.0 MB')).toBeInTheDocument();
    });

    // Download link must NOT contain 'undefined'
    const downloadLink = screen.getByRole('link', { name: /financial-report\.pdf/i });
    expect(downloadLink).toBeInTheDocument();
    expect(downloadLink).toHaveAttribute('href', expect.stringContaining('/api/sessions/session-pascal-att/attachments/att-doc-999/content'));
    expect(downloadLink.getAttribute('href')).not.toContain('undefined');
  });

  it('supports configuring persona default context size per driver and model, and updating it in the persona modal', async () => {
    const mockDriver: Agent = {
      id: 'agy',
      name: 'Antigravity CLI',
      role: 'Autonomous Codebase Driver',
      status: 'online',
      runtimeType: 'CliAgy',
      trustTier: 3,
      createdAt: new Date().toISOString()
    };

    const mockPersona: Persona = {
      id: 'sec-auditor',
      name: 'Security Auditor',
      role: 'Principal AppSec Architect',
      personaPrompt: 'Review all authentication and token flows.',
      driverId: 'agy',
      driverName: 'Antigravity CLI',
      driverStatus: 'online',
      defaultModel: 'gemini-4-argon',
      defaultEffort: 'high',
      defaultMode: 'plan',
      defaultContextSize: '1m',
      trustTier: 3,
      sessionCount: 0,
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockDriver]);
    vi.mocked(api.getPersonas).mockResolvedValue([mockPersona]);
    vi.mocked(api.updatePersona).mockResolvedValue({
      ...mockPersona,
      defaultModel: 'gpt-oss-120b',
      defaultContextSize: '200k'
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Security Auditor')).toBeInTheDocument();
      expect(screen.getByText('1m')).toBeInTheDocument();
    });

    // Open edit persona modal
    const editBtn = screen.getByTitle('Edit Security Auditor');
    fireEvent.click(editBtn);

    expect(screen.getByText('Edit Persona: Security Auditor')).toBeInTheDocument();

    const contextSizeSelect = screen.getByLabelText('Persona Default Context Size') as HTMLSelectElement;
    expect(contextSizeSelect.value).toBe('1m');

    // Change model to gpt-oss-120b (supports 128k, 200k)
    const modelSelect = screen.getByLabelText('Persona Default Model') as HTMLSelectElement;
    fireEvent.change(modelSelect, { target: { value: 'gpt-oss-120b' } });

    // Since 1m is not supported by gpt-oss-120b, context size should reset or let user select 200k
    fireEvent.change(contextSizeSelect, { target: { value: '200k' } });
    expect(contextSizeSelect.value).toBe('200k');

    fireEvent.click(screen.getByRole('button', { name: /Save Changes/i }));

    await waitFor(() => {
      expect(api.updatePersona).toHaveBeenCalledWith(
        'sec-auditor',
        expect.objectContaining({
          defaultModel: 'gpt-oss-120b',
          defaultContextSize: '200k'
        })
      );
    });
  });

  it('supports configuring persona max concurrent jobs in the persona modal and displaying it in the sidebar', async () => {
    const mockDriver: Agent = {
      id: 'agy',
      name: 'Antigravity CLI',
      role: 'Autonomous Codebase Driver',
      status: 'online',
      runtimeType: 'CliAgy',
      trustTier: 3,
      createdAt: new Date().toISOString()
    };

    const mockPersona: Persona = {
      id: 'astra-lead',
      name: 'Astra Lead',
      role: 'Tech Lead',
      personaPrompt: 'Ship robust code.',
      driverId: 'agy',
      driverName: 'Antigravity CLI',
      driverStatus: 'online',
      maxConcurrentJobs: 2,
      trustTier: 3,
      sessionCount: 0,
      createdAt: new Date().toISOString()
    };

    vi.mocked(api.getAgents).mockResolvedValue([mockDriver]);
    vi.mocked(api.getPersonas).mockResolvedValue([mockPersona]);
    vi.mocked(api.updatePersona).mockResolvedValue({
      ...mockPersona,
      maxConcurrentJobs: 3
    });

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Astra Lead')).toBeInTheDocument();
      expect(screen.getByText('Max 2 Jobs')).toBeInTheDocument();
    });

    // Open edit persona modal
    const editBtn = screen.getByTitle('Edit Astra Lead');
    fireEvent.click(editBtn);

    expect(screen.getByText('Edit Persona: Astra Lead')).toBeInTheDocument();

    const maxJobsInput = screen.getByLabelText('Persona Max Concurrent Jobs') as HTMLInputElement;
    expect(maxJobsInput.value).toBe('2');

    fireEvent.change(maxJobsInput, { target: { value: '3' } });
    expect(maxJobsInput.value).toBe('3');

    fireEvent.click(screen.getByRole('button', { name: /Save Changes/i }));

    await waitFor(() => {
      expect(api.updatePersona).toHaveBeenCalledWith(
        'astra-lead',
        expect.objectContaining({
          maxConcurrentJobs: 3
        })
      );
    });
  });
  it('opens the driver config modal from the driver row and saves limits through the API', async () => {
    const openAiDriver: Agent = {
      id: 'spence-ai',
      name: 'Spence Private AI',
      role: 'Autonomous Agent',
      status: 'online',
      runtimeType: 3,
      trustTier: 2,
      createdAt: new Date().toISOString(),
      capabilitiesJson: JSON.stringify({ adapter: 'openai' }),
      config: {
        adapter: 'openai',
        endpoint: 'http://127.0.0.1:11435/v1',
        defaultModel: 'qwen3',
        maxSteps: 100,
        timeoutSeconds: 300,
        maxRetries: 4,
        hasApiKey: true
      }
    };

    vi.mocked(api.getAgents).mockResolvedValue([openAiDriver]);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Spence Private AI')).toBeInTheDocument();
    });

    // The trigger must exist per driver row; before this wiring the modal was built
    // and unit-tested in isolation but nothing in the app could ever open it.
    fireEvent.click(screen.getByRole('button', { name: 'Configure Spence Private AI driver settings' }));

    const dialog = await screen.findByRole('dialog', { name: 'Configure driver Spence Private AI' });
    expect(dialog).toBeInTheDocument();
    expect(screen.getByLabelText(/Max steps per turn/i)).toHaveValue(100);

    fireEvent.change(screen.getByLabelText(/Max steps per turn/i), { target: { value: '250' } });
    fireEvent.click(screen.getByRole('button', { name: /^Save$/i }));

    await waitFor(() => {
      expect(api.updateDriverConfig).toHaveBeenCalledWith('spence-ai', { maxSteps: 250 });
    });

    // Saved config comes back from the API and the modal closes, so a reopen shows the new value.
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Configure driver Spence Private AI' })).not.toBeInTheDocument();
    });
  });

  it('does not offer turn limits for CLI drivers, which have no step loop to configure', async () => {
    const cliDriver: Agent = {
      id: 'claude',
      name: 'Claude Code CLI',
      role: 'Terminal Architect',
      status: 'online',
      runtimeType: 0,
      trustTier: 2,
      createdAt: new Date().toISOString(),
      capabilitiesJson: JSON.stringify({ adapter: 'claude' }),
      config: { adapter: 'claude', hasApiKey: false }
    };

    vi.mocked(api.getAgents).mockResolvedValue([cliDriver]);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Claude Code CLI')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Configure Claude Code CLI driver settings' }));
    await screen.findByRole('dialog', { name: 'Configure driver Claude Code CLI' });

    expect(screen.queryByLabelText(/Max steps per turn/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Endpoint URL/i)).not.toBeInTheDocument();
  });
});

