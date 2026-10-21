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
});
