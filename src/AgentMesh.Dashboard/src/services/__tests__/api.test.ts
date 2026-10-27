import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api, setAuthTokenProvider, setUnauthorizedHandler } from '../api';

describe('API Service & Auth Interceptor', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    setAuthTokenProvider(null);
    setUnauthorizedHandler(null);
  });

  it('sends default master key when no auth token provider is set', async () => {
    const mockFetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ status: 'Healthy' }), { status: 200 })
    );

    await api.getStatus();

    expect(mockFetch).toHaveBeenCalledWith(
      '/api/status',
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: 'Bearer mesh-default-secret-key',
          'X-Agent-Id': 'agy-core'
        })
      })
    );
  });

  it('sends active user token when token provider provides one', async () => {
    setAuthTokenProvider(() => 'custom-jwt-token');

    const mockFetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ status: 'Healthy' }), { status: 200 })
    );

    await api.getStatus();

    expect(mockFetch).toHaveBeenCalledWith(
      '/api/status',
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: 'Bearer custom-jwt-token'
        })
      })
    );
  });

  it('calls verifyAuth and returns response data', async () => {
    setAuthTokenProvider(() => 'valid-token');

    const mockFetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ authenticated: true, email: 'admin@agentmesh.local' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      })
    );

    const result = await api.verifyAuth();

    expect(result.authenticated).toBe(true);
    expect(result.email).toBe('admin@agentmesh.local');
    expect(mockFetch).toHaveBeenCalledWith(
      '/api/auth/verify',
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: 'Bearer valid-token'
        })
      })
    );
  });

  it('triggers unauthorizedHandler when an API request returns 401', async () => {
    setAuthTokenProvider(() => 'expired-token');
    const unauthorizedCallback = vi.fn();
    setUnauthorizedHandler(unauthorizedCallback);

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ error: 'Unauthorized' }), { status: 401, statusText: 'Unauthorized' })
    );

    await expect(
      api.postJobMessage('job-123', { content: 'Hello world' })
    ).rejects.toThrow('Failed to post job message: Unauthorized');

    expect(unauthorizedCallback).toHaveBeenCalledTimes(1);
  });

  it('does not trigger unauthorizedHandler on successful 200 requests', async () => {
    setAuthTokenProvider(() => 'valid-token');
    const unauthorizedCallback = vi.fn();
    setUnauthorizedHandler(unauthorizedCallback);

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ id: 'msg-1', content: 'Hello world' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      })
    );

    const result = await api.postJobMessage('job-123', { content: 'Hello world' });

    expect(result.id).toBe('msg-1');
    expect(unauthorizedCallback).not.toHaveBeenCalled();
  });

  describe('owner-scoped session paging', () => {
    const sessionJson = JSON.stringify([{ id: 's1', title: 'Row 1', status: 'active', metadataJson: '{}' }]);

    it('reads the server total from X-Total-Count', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        new Response(sessionJson, { status: 200, headers: { 'X-Total-Count': '127' } })
      );

      const page = await api.getPersonaSessionsPage('rook');

      expect(page.items).toHaveLength(1);
      expect(page.total).toBe(127);
    });

    it('reports a null total when the header is missing or unparseable', async () => {
      const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(sessionJson, { status: 200 }));
      expect((await api.getPersonaSessionsPage('rook')).total).toBeNull();

      fetchSpy.mockResolvedValue(new Response(sessionJson, { status: 200, headers: { 'X-Total-Count': 'not-a-number' } }));
      expect((await api.getPersonaSessionsPage('rook')).total).toBeNull();
    });

    it('requests the whole history when no page size is given', async () => {
      const mockFetch = vi
        .spyOn(globalThis, 'fetch')
        .mockResolvedValue(new Response(sessionJson, { status: 200, headers: { 'X-Total-Count': '1' } }));

      await api.getPersonaSessionsPage('sec auditor');

      // Expansion must not silently re-cap the list, and ids are slug-normalised with spaces.
      expect(mockFetch).toHaveBeenCalledWith('/api/personas/sec%20auditor/sessions', expect.anything());
    });

    it('builds take/skip query parameters for the driver endpoint', async () => {
      const mockFetch = vi
        .spyOn(globalThis, 'fetch')
        .mockResolvedValue(new Response(sessionJson, { status: 200, headers: { 'X-Total-Count': '40' } }));

      const page = await api.getAgentSessionsPage('agy', 25, 50);

      expect(mockFetch).toHaveBeenCalledWith('/api/agents/agy/sessions?take=25&skip=50', expect.anything());
      expect(page.total).toBe(40);
    });

    it('surfaces a failed page request instead of returning a truncated list', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('boom', { status: 503 }));

      await expect(api.getAgentSessionsPage('agy')).rejects.toThrow(/agent sessions/i);
    });
  });
});
