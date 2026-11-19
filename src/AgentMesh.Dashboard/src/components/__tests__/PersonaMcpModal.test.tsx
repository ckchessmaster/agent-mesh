import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { PersonaMcpModal } from '../PersonaMcpModal';
import { api } from '../../services/api';
import type { McpServer, Persona, PersonaMcpServer } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getPersonaMcpServers: vi.fn(),
    getMcpServers: vi.fn(),
    assignPersonaMcpServer: vi.fn(),
    updatePersonaMcpServer: vi.fn(),
    removePersonaMcpServer: vi.fn()
  }
}));

const persona: Persona = {
  id: 'astra',
  name: 'Astra',
  role: 'Senior Software Engineer',
  personaPrompt: 'Ship robust code.',
  driverId: 'agy',
  trustTier: 2,
  mcpServerCount: 1,
  createdAt: '2026-11-18T00:00:00Z'
};

const catalogServers: McpServer[] = [
  {
    id: 'github-mcp',
    name: 'GitHub MCP',
    description: 'GitHub tools',
    transport: 'Stdio',
    command: 'npx',
    args: ['-y', '@modelcontextprotocol/server-github'],
    url: null,
    envMasked: { GITHUB_TOKEN: 'ghp_...1111' },
    headersMasked: {},
    hasEnv: true,
    hasHeaders: false,
    enabled: true,
    timeoutSeconds: 30,
    assignedPersonaCount: 1,
    createdAt: '2026-11-18T00:00:00Z',
    updatedAt: '2026-11-18T00:00:00Z'
  },
  {
    id: 'docs-http',
    name: 'Docs HTTP',
    description: 'Internal docs',
    transport: 'Http',
    command: null,
    args: [],
    url: 'https://docs.internal/mcp',
    envMasked: {},
    headersMasked: { Authorization: 'Bea...9999' },
    hasEnv: false,
    hasHeaders: true,
    enabled: false,
    timeoutSeconds: 30,
    assignedPersonaCount: 0,
    createdAt: '2026-11-18T00:00:00Z',
    updatedAt: '2026-11-18T00:00:00Z'
  }
];

const initialAssignments: PersonaMcpServer[] = [
  {
    personaId: 'astra',
    mcpServerId: 'github-mcp',
    mcpServerName: 'GitHub MCP',
    mcpServerDescription: 'GitHub tools',
    transport: 'Stdio',
    command: 'npx',
    args: ['-y', '@modelcontextprotocol/server-github'],
    url: null,
    serverEnabled: true,
    enabled: true,
    effectiveEnabled: true,
    timeoutSeconds: 30,
    serverEnvMasked: { GITHUB_TOKEN: 'ghp_...1111' },
    serverHeadersMasked: {},
    envOverridesMasked: { GITHUB_TOKEN: 'ghp_...2222' },
    headersOverridesMasked: {},
    hasEnvOverrides: true,
    hasHeadersOverrides: false,
    allowedTools: ['get_issue', 'list_commits'],
    blockedTools: ['delete_repo'],
    assignedAt: '2026-11-18T00:00:00Z',
    updatedAt: '2026-11-18T00:00:00Z'
  }
];

describe('PersonaMcpModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getPersonaMcpServers).mockResolvedValue(initialAssignments);
    vi.mocked(api.getMcpServers).mockResolvedValue(catalogServers);
  });

  it('displays assigned MCP servers with tool filters and override badges', async () => {
    render(<PersonaMcpModal persona={persona} onClose={vi.fn()} />);

    expect(await screen.findByText('GitHub MCP')).toBeInTheDocument();
    expect(screen.getByText('Allowed: get_issue, list_commits')).toBeInTheDocument();
    expect(screen.getByText('Blocked: delete_repo')).toBeInTheDocument();
    expect(screen.getByText('1 env override(s)')).toBeInTheDocument();
  });

  it('assigns an unassigned catalog server to the persona', async () => {
    const onAssignmentsChanged = vi.fn();
    vi.mocked(api.assignPersonaMcpServer).mockResolvedValue({
      personaId: 'astra',
      mcpServerId: 'docs-http',
      mcpServerName: 'Docs HTTP',
      mcpServerDescription: 'Internal docs',
      transport: 'Http',
      command: null,
      args: [],
      url: 'https://docs.internal/mcp',
      serverEnabled: false,
      enabled: true,
      effectiveEnabled: false,
      timeoutSeconds: 30,
      serverEnvMasked: {},
      serverHeadersMasked: { Authorization: 'Bea...9999' },
      envOverridesMasked: {},
      headersOverridesMasked: {},
      hasEnvOverrides: false,
      hasHeadersOverrides: false,
      allowedTools: null,
      blockedTools: null,
      assignedAt: '2026-11-18T00:00:00Z',
      updatedAt: '2026-11-18T00:00:00Z'
    });

    render(
      <PersonaMcpModal
        persona={persona}
        onClose={vi.fn()}
        onAssignmentsChanged={onAssignmentsChanged}
      />
    );

    await screen.findByText('GitHub MCP');
    const select = screen.getByLabelText('Select MCP server to assign') as HTMLSelectElement;
    expect(select.value).toBe('docs-http');

    fireEvent.click(screen.getByTestId('persona-mcp-assign-btn'));

    await waitFor(() =>
      expect(api.assignPersonaMcpServer).toHaveBeenCalledWith('astra', {
        mcpServerId: 'docs-http',
        enabled: true
      })
    );
    expect(await screen.findByText('Catalog Disabled')).toBeInTheDocument();
    expect(onAssignmentsChanged).toHaveBeenCalledTimes(1);
  });

  it('updates tool allowlist/blocklist and secret overrides for an assignment', async () => {
    vi.mocked(api.updatePersonaMcpServer).mockResolvedValue({
      ...initialAssignments[0],
      allowedTools: null,
      blockedTools: ['force_push']
    });

    render(<PersonaMcpModal persona={persona} onClose={vi.fn()} />);
    await screen.findByText('GitHub MCP');

    fireEvent.click(screen.getByTestId('persona-mcp-configure-github-mcp'));

    // Switch allowed tools back to All tools
    fireEvent.click(screen.getByLabelText('All tools (no allowlist)'));

    // Update blocked tools
    fireEvent.change(screen.getByLabelText('Blocked Tools List'), {
      target: { value: 'force_push' }
    });

    // Remove existing override and add a new override
    fireEvent.click(screen.getByTestId('persona-mcp-env-override-toggle-remove-GITHUB_TOKEN'));
    fireEvent.click(screen.getByTestId('persona-mcp-env-override-add-btn'));
    fireEvent.change(screen.getByLabelText('Environment Overrides (Encrypted) Key 1'), {
      target: { value: 'CUSTOM_ENV' }
    });
    fireEvent.change(screen.getByLabelText('Environment Overrides (Encrypted) Value 1'), {
      target: { value: 'custom-secret' }
    });

    fireEvent.click(screen.getByTestId('persona-mcp-save-overrides-btn'));

    await waitFor(() => expect(api.updatePersonaMcpServer).toHaveBeenCalledTimes(1));
    expect(api.updatePersonaMcpServer).toHaveBeenCalledWith('astra', 'github-mcp', {
      enabled: true,
      clearAllowedTools: true,
      blockedTools: ['force_push'],
      envOverrides: { CUSTOM_ENV: 'custom-secret' },
      mergeEnvOverrides: true,
      removeEnvOverrideKeys: ['GITHUB_TOKEN']
    });
  });

  it('unassigns an MCP server via inline confirmation', async () => {
    vi.mocked(api.removePersonaMcpServer).mockResolvedValue({
      status: 'deleted',
      personaId: 'astra',
      mcpServerId: 'github-mcp'
    });

    render(<PersonaMcpModal persona={persona} onClose={vi.fn()} />);
    await screen.findByText('GitHub MCP');

    fireEvent.click(screen.getByTestId('persona-mcp-remove-github-mcp'));
    fireEvent.click(screen.getByTestId('persona-mcp-confirm-remove-github-mcp'));

    await waitFor(() =>
      expect(api.removePersonaMcpServer).toHaveBeenCalledWith('astra', 'github-mcp')
    );
    await waitFor(() => expect(screen.queryByText('GitHub MCP')).not.toBeInTheDocument());
  });
});
