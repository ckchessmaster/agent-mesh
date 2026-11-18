import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getStatus: vi.fn().mockResolvedValue({ status: 'Healthy', services: {} }),
    getCapabilities: vi.fn().mockResolvedValue({ agentRuntimes: {}, drivers: [] }),
    getAgents: vi.fn().mockResolvedValue([
      {
        id: 'agy',
        name: 'Antigravity CLI',
        status: 'online',
        runtimeType: 1,
        trustTier: 2,
        createdAt: '2026-11-18T00:00:00Z'
      }
    ]),
    getPersonas: vi.fn().mockResolvedValue([
      {
        id: 'astra',
        name: 'Astra',
        role: 'Senior Software Engineer',
        personaPrompt: 'Ship robust code.',
        driverId: 'agy',
        driverStatus: 'online',
        trustTier: 2,
        sessionCount: 0,
        mcpServerCount: 1,
        createdAt: '2026-11-18T00:00:00Z'
      }
    ]),
    getProjects: vi.fn().mockResolvedValue([]),
    getSessions: vi.fn().mockResolvedValue([]),
    getAgentUsage: vi.fn().mockResolvedValue({
      agentId: 'agy',
      agentName: 'Antigravity CLI',
      sessionCount: 0,
      lifetimeTokens: { inputTokens: 0, outputTokens: 0, thinkingTokens: 0, totalTokens: 0, totalDurationSeconds: 0 }
    }),
    getMcpServers: vi.fn().mockResolvedValue([
      {
        id: 'github-mcp',
        name: 'GitHub MCP',
        description: 'GitHub tools',
        transport: 'Stdio',
        command: 'npx',
        args: ['-y', '@modelcontextprotocol/server-github'],
        url: null,
        envMasked: {},
        headersMasked: {},
        hasEnv: false,
        hasHeaders: false,
        enabled: true,
        timeoutSeconds: 30,
        assignedPersonaCount: 1,
        createdAt: '2026-11-18T00:00:00Z',
        updatedAt: '2026-11-18T00:00:00Z'
      }
    ]),
    getPersonaMcpServers: vi.fn().mockResolvedValue([])
  }
}));

vi.mock('../auth/AuthProvider', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    isLoading: false,
    user: { profile: { email: 'admin', name: 'admin' } }
  }))
}));

describe('Dashboard MCP Integration', () => {
  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('agentmesh_active_tab', 'personas');
  });

  it('shows MCP catalog button, persona MCP count badge, and opens both modals', async () => {
    render(<App />);

    const catalogBtn = await screen.findByTestId('open-mcp-catalog-btn');
    expect(catalogBtn).toHaveTextContent('MCP (1)');
    expect(screen.getByText('1 MCP')).toBeInTheDocument();

    fireEvent.click(catalogBtn);
    expect(await screen.findByTestId('mcp-catalog-modal')).toBeInTheDocument();
    expect(screen.getByText('GitHub MCP')).toBeInTheDocument();

    fireEvent.click(screen.getByLabelText('Close MCP Server Catalog'));
    await waitFor(() => expect(screen.queryByTestId('mcp-catalog-modal')).not.toBeInTheDocument());

    fireEvent.click(screen.getByTestId('persona-mcp-btn-astra'));
    expect(await screen.findByTestId('persona-mcp-modal')).toBeInTheDocument();
    expect(api.getPersonaMcpServers).toHaveBeenCalledWith('astra');
  });
});
