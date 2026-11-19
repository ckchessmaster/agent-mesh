import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api, setAuthTokenProvider } from '../api';
import { McpTransportType } from '../../types';

describe('MCP API Client', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    setAuthTokenProvider(() => 'test-token');
  });

  it('lists MCP servers with optional query filters and normalizes PascalCase and numeric transports', async () => {
    const mockFetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(
        JSON.stringify([
          {
            Id: 'github-mcp',
            Name: 'GitHub MCP',
            Description: 'Repo tools',
            Transport: 0,
            Command: 'npx',
            Args: ['-y', '@modelcontextprotocol/server-github'],
            EnvMasked: { GITHUB_TOKEN: 'ghp_...1234' },
            HasEnv: true,
            HasHeaders: false,
            Enabled: true,
            TimeoutSeconds: 45,
            AssignedPersonaCount: 2,
            CreatedAt: '2026-11-18T00:00:00Z',
            UpdatedAt: '2026-11-18T00:00:00Z'
          },
          {
            id: 'docs-http',
            name: 'Docs HTTP',
            description: '',
            transport: 'Http',
            url: 'https://docs.example.com/mcp',
            headersMasked: { Authorization: 'Bea...9999' },
            hasEnv: false,
            hasHeaders: true,
            enabled: false,
            timeoutSeconds: 30,
            assignedPersonaCount: 0,
            createdAt: '2026-11-18T00:00:00Z',
            updatedAt: '2026-11-18T00:00:00Z'
          }
        ]),
        { status: 200, headers: { 'Content-Type': 'application/json' } }
      )
    );

    const servers = await api.getMcpServers({ enabled: true, transport: McpTransportType.Stdio });

    expect(mockFetch).toHaveBeenCalledWith(
      '/api/mcp-servers?enabled=true&transport=Stdio',
      expect.objectContaining({
        headers: expect.objectContaining({ Authorization: 'Bearer test-token' })
      })
    );
    expect(servers).toHaveLength(2);
    expect(servers[0]).toMatchObject({
      id: 'github-mcp',
      name: 'GitHub MCP',
      transport: 'Stdio',
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-github'],
      envMasked: { GITHUB_TOKEN: 'ghp_...1234' },
      hasEnv: true,
      enabled: true,
      timeoutSeconds: 45,
      assignedPersonaCount: 2
    });
    expect(servers[1]).toMatchObject({
      id: 'docs-http',
      transport: 'Http',
      url: 'https://docs.example.com/mcp',
      enabled: false
    });
  });

  it('creates, updates, and deletes an MCP server and surfaces API error messages', async () => {
    const mockFetch = vi.spyOn(globalThis, 'fetch');

    mockFetch.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          id: 'fs-mcp',
          name: 'Filesystem',
          description: '',
          transport: 'Stdio',
          command: 'npx',
          args: ['-y', '@mcp/fs'],
          envMasked: {},
          headersMasked: {},
          hasEnv: false,
          hasHeaders: false,
          enabled: true,
          timeoutSeconds: 30,
          assignedPersonaCount: 0,
          createdAt: '2026-11-18T00:00:00Z',
          updatedAt: '2026-11-18T00:00:00Z'
        }),
        { status: 201 }
      )
    );

    const created = await api.createMcpServer({
      name: 'Filesystem',
      transport: 'Stdio',
      command: 'npx',
      args: ['-y', '@mcp/fs']
    });
    expect(created.id).toBe('fs-mcp');
    expect(mockFetch).toHaveBeenLastCalledWith(
      '/api/mcp-servers',
      expect.objectContaining({ method: 'POST' })
    );

    mockFetch.mockResolvedValueOnce(
      new Response(JSON.stringify({ id: 'fs-mcp', name: 'Filesystem Updated', transport: 'Stdio', enabled: false }), {
        status: 200
      })
    );
    const updated = await api.updateMcpServer('fs-mcp', { name: 'Filesystem Updated', enabled: false });
    expect(updated.enabled).toBe(false);
    expect(mockFetch).toHaveBeenLastCalledWith(
      '/api/mcp-servers/fs-mcp',
      expect.objectContaining({ method: 'PUT' })
    );

    mockFetch.mockResolvedValueOnce(
      new Response(JSON.stringify({ status: 'deleted', id: 'fs-mcp', unassignedPersonaCount: 1 }), { status: 200 })
    );
    const deleted = await api.deleteMcpServer('fs-mcp');
    expect(deleted.unassignedPersonaCount).toBe(1);
    expect(mockFetch).toHaveBeenLastCalledWith(
      '/api/mcp-servers/fs-mcp',
      expect.objectContaining({ method: 'DELETE' })
    );

    mockFetch.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "MCP server with Id 'fs-mcp' already exists." }), {
        status: 409,
        statusText: 'Conflict'
      })
    );
    await expect(api.createMcpServer({ name: 'Filesystem' })).rejects.toThrow(
      "Failed to create MCP server: MCP server with Id 'fs-mcp' already exists."
    );
  });

  it('manages per-persona MCP assignments and preserves null vs empty allowedTools', async () => {
    const mockFetch = vi.spyOn(globalThis, 'fetch');

    mockFetch.mockResolvedValueOnce(
      new Response(
        JSON.stringify([
          {
            personaId: 'astra',
            mcpServerId: 'github-mcp',
            mcpServerName: 'GitHub MCP',
            mcpServerDescription: '',
            transport: 'Stdio',
            command: 'npx',
            args: [],
            serverEnabled: true,
            enabled: true,
            effectiveEnabled: true,
            timeoutSeconds: 30,
            serverEnvMasked: { TOKEN: 'ghp_...1111' },
            serverHeadersMasked: {},
            envOverridesMasked: { TOKEN: 'ghp_...2222' },
            headersOverridesMasked: {},
            hasEnvOverrides: true,
            hasHeadersOverrides: false,
            allowedTools: null,
            blockedTools: ['delete_repo'],
            assignedAt: '2026-11-18T00:00:00Z',
            updatedAt: '2026-11-18T00:00:00Z'
          }
        ]),
        { status: 200 }
      )
    );

    const assigned = await api.getPersonaMcpServers('astra');
    expect(assigned[0].allowedTools).toBeNull();
    expect(assigned[0].blockedTools).toEqual(['delete_repo']);
    expect(assigned[0].envOverridesMasked).toEqual({ TOKEN: 'ghp_...2222' });

    mockFetch.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          personaId: 'astra',
          mcpServerId: 'docs-http',
          mcpServerName: 'Docs',
          transport: 'Http',
          url: 'https://docs.example.com/mcp',
          serverEnabled: true,
          enabled: true,
          effectiveEnabled: true
        }),
        { status: 201 }
      )
    );
    const added = await api.assignPersonaMcpServer('astra', { mcpServerId: 'docs-http' });
    expect(added.mcpServerId).toBe('docs-http');
    expect(mockFetch).toHaveBeenLastCalledWith(
      '/api/personas/astra/mcp-servers',
      expect.objectContaining({ method: 'POST' })
    );

    mockFetch.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          personaId: 'astra',
          mcpServerId: 'docs-http',
          mcpServerName: 'Docs',
          transport: 'Http',
          allowedTools: [],
          enabled: false
        }),
        { status: 200 }
      )
    );
    const patched = await api.updatePersonaMcpServer('astra', 'docs-http', {
      enabled: false,
      allowedTools: []
    });
    expect(patched.allowedTools).toEqual([]);
    expect(mockFetch).toHaveBeenLastCalledWith(
      '/api/personas/astra/mcp-servers/docs-http',
      expect.objectContaining({ method: 'PATCH' })
    );

    mockFetch.mockResolvedValueOnce(
      new Response(JSON.stringify({ status: 'deleted', personaId: 'astra', mcpServerId: 'docs-http' }), {
        status: 200
      })
    );
    await api.removePersonaMcpServer('astra', 'docs-http');
    expect(mockFetch).toHaveBeenLastCalledWith(
      '/api/personas/astra/mcp-servers/docs-http',
      expect.objectContaining({ method: 'DELETE' })
    );
  });
});
