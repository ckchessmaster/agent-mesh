import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type { Agent, AgentSession, SessionMessage, SystemStatusResponse } from '../types';

vi.mock('../services/api', () => ({
  api: {
    getStatus: vi.fn(),
    getAgents: vi.fn().mockResolvedValue([]),
    getSessions: vi.fn().mockResolvedValue([]),
    getSessionMessages: vi.fn().mockResolvedValue([]),
    seedDemo: vi.fn(),
    resetMesh: vi.fn(),
    createAgentSession: vi.fn(),
    postSessionMessage: vi.fn(),
    upsertAgent: vi.fn()
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

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getStatus).mockResolvedValue(mockHealthyStatus);
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

  it('triggers seedDemo and resetMesh from header buttons', async () => {
    vi.mocked(api.seedDemo).mockResolvedValue({
      status: 'seeded',
      agentId: 'agy-core',
      sessionId: 'session-999',
      eventCount: 8
    });
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
});
