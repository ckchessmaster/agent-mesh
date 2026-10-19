import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type {
  Agent,
  AgentSession,
  SessionMessage,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from '../types';

vi.mock('../services/api', () => ({
  api: {
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
          models: [
            { id: 'gemini-4-argon', name: 'Gemini 4 Argon', efforts: ['low', 'medium', 'high'] },
            { id: 'gpt-oss-120b', name: 'GPT-OSS 120b', efforts: ['medium'] }
          ]
        }
      }
    }),
    browseFilesystem: vi.fn().mockResolvedValue({
      currentPath: '/workspace/agent-mesh',
      parentPath: '/workspace',
      directories: [
        { name: 'src', path: '/workspace/agent-mesh/src' },
        { name: 'docs', path: '/workspace/agent-mesh/docs' }
      ]
    }),
    getAgents: vi.fn().mockResolvedValue([]),
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
    getSessionMessages: vi.fn().mockResolvedValue([]),
    resetMesh: vi.fn(),
    createAgentSession: vi.fn(),
    updateSessionControls: vi.fn(),
    postSessionMessage: vi.fn(),
    upsertAgent: vi.fn(),
    stopSession: vi.fn().mockResolvedValue({ status: 'stopped', sessionId: 's1' }),
    stopCommand: vi.fn().mockResolvedValue({ status: 'stopped', commandId: 'c1' }),
    enqueuePrompt: vi.fn().mockResolvedValue({ session: {}, queue: [], dispatched: false }),
    dequeuePrompt: vi.fn().mockResolvedValue([]),
    sendQueuedPromptNow: vi.fn().mockResolvedValue({ session: {}, queue: [], dispatched: true })
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
          { id: 'gemini-4-argon', name: 'Gemini 4 Argon', efforts: ['low', 'medium', 'high'] },
          { id: 'gpt-oss-120b', name: 'GPT-OSS 120b', efforts: ['medium'] }
        ]
      }
    }
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getStatus).mockResolvedValue(mockHealthyStatus);
    vi.mocked(api.getCapabilities).mockResolvedValue(mockCaps);
    vi.mocked(api.browseFilesystem).mockResolvedValue({
      currentPath: '/workspace/agent-mesh',
      parentPath: '/workspace',
      directories: [
        { name: 'src', path: '/workspace/agent-mesh/src' },
        { name: 'docs', path: '/workspace/agent-mesh/docs' }
      ]
    });
    vi.mocked(api.getAgents).mockResolvedValue([]);
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
      expect(screen.getByText('/home/user/workspace/proj')).toBeInTheDocument();
      expect(screen.getByText('2,850 tokens (4.2s)')).toBeInTheDocument();
      expect(screen.getByText('⚡ 2,850 tokens (4.2s)')).toBeInTheDocument();
    });
  });

  it('supports directory preset chips, interactive folder picker modal, and dynamic model/effort/mode controls', async () => {
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
    expect(screen.getByText('Default Sandbox Workspace')).toBeInTheDocument();

    // Check quick bookmarks are visible
    expect(screen.getByText('⚡ Default Sandbox')).toBeInTheDocument();
    expect(screen.getByText('Current Project')).toBeInTheDocument();
    expect(screen.getByText('User Home')).toBeInTheDocument();

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

    // Test quick preset bookmark click
    fireEvent.click(screen.getByText('Current Project'));
    expect(screen.getByText('/workspace/agent-mesh')).toBeInTheDocument();

    // Test opening interactive Folder Picker modal
    fireEvent.click(screen.getByRole('button', { name: /Browse.../i }));
    expect(screen.getByRole('dialog', { name: 'Select Working Directory' })).toBeInTheDocument();
    expect(api.browseFilesystem).toHaveBeenCalled();

    // In modal, directories should be listed
    await waitFor(() => {
      expect(screen.getByText('docs')).toBeInTheDocument();
      expect(screen.getByText('src')).toBeInTheDocument();
    });

    // Click "Select" next to "docs"
    const selectDocsBtn = screen.getAllByRole('button', { name: 'Select' })[1];
    fireEvent.click(selectDocsBtn);

    // Modal closes and working directory updates to docs
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByText('/workspace/agent-mesh/docs')).toBeInTheDocument();

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
          workingDirectory: '/workspace/agent-mesh/docs',
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
      workspace: {
        defaultSandboxPath: '~/.agentmesh/workspaces/<session-id>',
        currentDirectory: '/workspace/agent-mesh',
        userHome: '/home/testuser',
        bookmarks: []
      },
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
});


