import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { McpCatalogModal } from '../McpCatalogModal';
import { api } from '../../services/api';
import type { McpServer } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getMcpServers: vi.fn(),
    createMcpServer: vi.fn(),
    updateMcpServer: vi.fn(),
    deleteMcpServer: vi.fn()
  }
}));

const sampleServers: McpServer[] = [
  {
    id: 'github-mcp',
    name: 'GitHub MCP',
    description: 'GitHub repository operations',
    transport: 'Stdio',
    command: 'npx',
    args: ['-y', '@modelcontextprotocol/server-github'],
    url: null,
    envMasked: { GITHUB_PERSONAL_ACCESS_TOKEN: 'ghp_...1234' },
    headersMasked: {},
    hasEnv: true,
    hasHeaders: false,
    enabled: true,
    timeoutSeconds: 30,
    assignedPersonaCount: 2,
    createdAt: '2026-11-18T00:00:00Z',
    updatedAt: '2026-11-18T00:00:00Z'
  },
  {
    id: 'internal-docs',
    name: 'Internal Docs',
    description: 'Company search endpoint',
    transport: 'Http',
    command: null,
    args: [],
    url: 'https://docs.internal/mcp',
    envMasked: {},
    headersMasked: { Authorization: 'Bea...9999' },
    hasEnv: false,
    hasHeaders: true,
    enabled: false,
    timeoutSeconds: 45,
    assignedPersonaCount: 0,
    createdAt: '2026-11-18T00:00:00Z',
    updatedAt: '2026-11-18T00:00:00Z'
  }
];

describe('McpCatalogModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getMcpServers).mockResolvedValue(sampleServers);
  });

  it('renders catalog servers with masked secrets and supports filtering', async () => {
    render(<McpCatalogModal isOpen onClose={vi.fn()} />);

    expect(await screen.findByText('GitHub MCP')).toBeInTheDocument();
    expect(screen.getByText('Internal Docs')).toBeInTheDocument();
    expect(screen.getByText(/env:GITHUB_PERSONAL_ACCESS_TOKEN=ghp_\.\.\.1234/)).toBeInTheDocument();
    expect(screen.getByText(/2 personas/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Http' }));
    expect(screen.queryByText('GitHub MCP')).not.toBeInTheDocument();
    expect(screen.getByText('Internal Docs')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'All' }));
    fireEvent.change(screen.getByLabelText('Filter MCP servers'), { target: { value: 'github' } });
    expect(screen.getByText('GitHub MCP')).toBeInTheDocument();
    expect(screen.queryByText('Internal Docs')).not.toBeInTheDocument();
  });

  it('creates a new Stdio MCP server with args and encrypted env variables', async () => {
    const created: McpServer = {
      id: 'sqlite-mcp',
      name: 'SQLite MCP',
      description: 'Read-only SQL',
      transport: 'Stdio',
      command: 'uvx',
      args: ['mcp-server-sqlite', '--db-path', '/data/app.db'],
      url: null,
      envMasked: { SQLITE_MODE: 'ro...ro' },
      headersMasked: {},
      hasEnv: true,
      hasHeaders: false,
      enabled: true,
      timeoutSeconds: 60,
      assignedPersonaCount: 0,
      createdAt: '2026-11-18T00:00:00Z',
      updatedAt: '2026-11-18T00:00:00Z'
    };
    vi.mocked(api.createMcpServer).mockResolvedValue(created);
    const onCatalogChanged = vi.fn();

    render(<McpCatalogModal isOpen onClose={vi.fn()} onCatalogChanged={onCatalogChanged} />);
    await screen.findByText('GitHub MCP');

    fireEvent.click(screen.getByTestId('mcp-catalog-add-server-btn'));
    fireEvent.change(screen.getByLabelText('Server Name'), { target: { value: 'SQLite MCP' } });
    fireEvent.change(screen.getByLabelText('Description (Optional)'), { target: { value: 'Read-only SQL' } });
    fireEvent.change(screen.getByLabelText('Timeout (sec)'), { target: { value: '60' } });
    fireEvent.change(screen.getByLabelText('Command'), { target: { value: 'uvx' } });
    fireEvent.change(screen.getByLabelText('Arguments (one per line)'), {
      target: { value: 'mcp-server-sqlite\n--db-path\n/data/app.db' }
    });

    fireEvent.click(screen.getByTestId('mcp-catalog-env-add-btn'));
    fireEvent.change(screen.getByLabelText('Environment Variables (Encrypted) Key 1'), {
      target: { value: 'SQLITE_MODE' }
    });
    fireEvent.change(screen.getByLabelText('Environment Variables (Encrypted) Value 1'), {
      target: { value: 'ro' }
    });

    fireEvent.click(screen.getByTestId('mcp-catalog-save-btn'));

    await waitFor(() => expect(api.createMcpServer).toHaveBeenCalledTimes(1));
    expect(api.createMcpServer).toHaveBeenCalledWith({
      name: 'SQLite MCP',
      id: undefined,
      description: 'Read-only SQL',
      transport: 'Stdio',
      command: 'uvx',
      args: ['mcp-server-sqlite', '--db-path', '/data/app.db'],
      url: undefined,
      env: { SQLITE_MODE: 'ro' },
      headers: undefined,
      enabled: true,
      timeoutSeconds: 60
    });
    expect(await screen.findByText('SQLite MCP')).toBeInTheDocument();
  });

  it('edits an existing server preserving untouched secrets or sending merge/remove keys', async () => {
    vi.mocked(api.updateMcpServer).mockResolvedValue({
      ...sampleServers[0],
      timeoutSeconds: 90,
      envMasked: { NEW_VAR: '***' }
    });

    render(<McpCatalogModal isOpen onClose={vi.fn()} />);
    await screen.findByText('GitHub MCP');

    fireEvent.click(screen.getByTestId('mcp-catalog-edit-github-mcp'));
    fireEvent.change(screen.getByLabelText('Timeout (sec)'), { target: { value: '90' } });
    fireEvent.click(screen.getByTestId('mcp-catalog-env-toggle-remove-GITHUB_PERSONAL_ACCESS_TOKEN'));
    fireEvent.click(screen.getByTestId('mcp-catalog-env-add-btn'));
    fireEvent.change(screen.getByLabelText('Environment Variables (Encrypted) Key 1'), {
      target: { value: 'NEW_VAR' }
    });
    fireEvent.change(screen.getByLabelText('Environment Variables (Encrypted) Value 1'), {
      target: { value: 'secret-val' }
    });

    fireEvent.click(screen.getByTestId('mcp-catalog-save-btn'));

    await waitFor(() => expect(api.updateMcpServer).toHaveBeenCalledTimes(1));
    expect(api.updateMcpServer).toHaveBeenCalledWith(
      'github-mcp',
      expect.objectContaining({
        timeoutSeconds: 90,
        env: { NEW_VAR: 'secret-val' },
        mergeEnv: true,
        removeEnvKeys: ['GITHUB_PERSONAL_ACCESS_TOKEN']
      })
    );
  });

  it('toggles enable/disable and confirms delete with assigned persona count warning', async () => {
    vi.mocked(api.updateMcpServer).mockResolvedValue({ ...sampleServers[0], enabled: false });
    vi.mocked(api.deleteMcpServer).mockResolvedValue({
      status: 'deleted',
      id: 'github-mcp',
      unassignedPersonaCount: 2
    });

    render(<McpCatalogModal isOpen onClose={vi.fn()} />);
    await screen.findByText('GitHub MCP');

    fireEvent.click(screen.getByTestId('mcp-catalog-toggle-github-mcp'));
    await waitFor(() =>
      expect(api.updateMcpServer).toHaveBeenCalledWith('github-mcp', { enabled: false })
    );

    fireEvent.click(screen.getByTestId('mcp-catalog-delete-github-mcp'));
    expect(screen.getByText(/This will unassign it from 2 persona\(s\)/)).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('mcp-catalog-confirm-delete-github-mcp'));
    await waitFor(() => expect(api.deleteMcpServer).toHaveBeenCalledWith('github-mcp'));
    await waitFor(() => expect(screen.queryByText('GitHub MCP')).not.toBeInTheDocument());
  });
});
