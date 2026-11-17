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
      api.addTimelineComment('job-123', 'card-1', {
        senderId: 'operator',
        senderType: 'operator',
        content: 'Hello world'
      })
    ).rejects.toThrow('Failed to post timeline comment: Unauthorized');

    expect(unauthorizedCallback).toHaveBeenCalledTimes(1);
  });

  it('does not trigger unauthorizedHandler on successful 200 requests', async () => {
    setAuthTokenProvider(() => 'valid-token');
    const unauthorizedCallback = vi.fn();
    setUnauthorizedHandler(unauthorizedCallback);

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ id: 'comment-1', content: 'Hello world' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      })
    );

    const result = await api.addTimelineComment('job-123', 'card-1', {
      senderId: 'operator',
      senderType: 'operator',
      content: 'Hello world'
    });

    expect(result.id).toBe('comment-1');
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

  describe('project workspace fields & setup endpoint', () => {
    it('sends repositoryName and workingDirectory in createProject and updateProject', async () => {
      const mockFetch = vi
        .spyOn(globalThis, 'fetch')
        .mockResolvedValueOnce(
          new Response(
            JSON.stringify({
              id: 'billing-api',
              name: 'Billing API',
              repositoryName: 'billing-api',
              workingDirectory: '/workspace/repos/billing-api',
              resolvedWorkingDirectory: '/workspace/repos/billing-api'
            }),
            { status: 201 }
          )
        )
        .mockResolvedValueOnce(
          new Response(
            JSON.stringify({
              id: 'billing-api',
              name: 'Billing API',
              repositoryName: 'billing-svc',
              workingDirectory: '/workspace/custom/billing',
              resolvedWorkingDirectory: '/workspace/custom/billing'
            }),
            { status: 200 }
          )
        );

      const created = await api.createProject({
        name: 'Billing API',
        repositoryName: 'billing-api',
        workingDirectory: '/workspace/repos/billing-api'
      });
      expect(created.repositoryName).toBe('billing-api');
      expect(created.resolvedWorkingDirectory).toBe('/workspace/repos/billing-api');
      expect(mockFetch).toHaveBeenNthCalledWith(
        1,
        '/api/projects',
        expect.objectContaining({
          method: 'POST',
          body: expect.stringContaining('"repositoryName":"billing-api"')
        })
      );

      const updated = await api.updateProject('billing-api', {
        repositoryName: 'billing-svc',
        workingDirectory: '/workspace/custom/billing'
      });
      expect(updated.workingDirectory).toBe('/workspace/custom/billing');
      expect(mockFetch).toHaveBeenNthCalledWith(
        2,
        '/api/projects/billing-api',
        expect.objectContaining({
          method: 'PUT',
          body: expect.stringContaining('"workingDirectory":"/workspace/custom/billing"')
        })
      );
    });

    it('surfaces API validation error messages from createProject and updateProject', async () => {
      vi.spyOn(globalThis, 'fetch')
        .mockResolvedValueOnce(
          new Response(
            JSON.stringify({ error: 'WorkingDirectory must be an absolute path.' }),
            { status: 400, statusText: 'Bad Request' }
          )
        )
        .mockResolvedValueOnce(
          new Response(
            JSON.stringify({ error: 'RepositoryName cannot be empty.' }),
            { status: 400, statusText: 'Bad Request' }
          )
        );

      await expect(
        api.createProject({ name: 'Bad', workingDirectory: 'relative/dir' })
      ).rejects.toThrow('Failed to create project: WorkingDirectory must be an absolute path.');

      await expect(
        api.updateProject('bad', { repositoryName: '' })
      ).rejects.toThrow('Failed to update project: RepositoryName cannot be empty.');
    });

    it('calls POST /api/projects/{id}/setup and normalizes camelCase or PascalCase responses', async () => {
      const mockFetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        new Response(
          JSON.stringify({
            ProjectId: 'my service',
            Success: true,
            TargetDirectory: '/workspace/repos/my-service',
            EffectiveDirectory: '/workspace/repos/my-service',
            Outcome: 'Initialized',
            UsedFallback: false,
            CreatedFiles: true,
            Message: 'Initialized repository.',
            Warnings: ['git user fallback']
          }),
          { status: 200 }
        )
      );

      const result = await api.setupProjectWorkspace('my service', {
        fallbackDirectory: '/tmp/fallback'
      });

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/projects/my%20service/setup?fallbackDirectory=%2Ftmp%2Ffallback',
        expect.objectContaining({ method: 'POST' })
      );
      expect(result).toEqual({
        projectId: 'my service',
        success: true,
        targetDirectory: '/workspace/repos/my-service',
        effectiveDirectory: '/workspace/repos/my-service',
        outcome: 'Initialized',
        usedFallback: false,
        createdFiles: true,
        message: 'Initialized repository.',
        warnings: ['git user fallback'],
        error: null
      });
    });

    it('returns structured setup diagnostics on HTTP 422 and throws on 404', async () => {
      vi.spyOn(globalThis, 'fetch')
        .mockResolvedValueOnce(
          new Response(
            JSON.stringify({
              projectId: 'failing-proj',
              success: false,
              targetDirectory: '/workspace/repos/failing-proj',
              effectiveDirectory: '',
              outcome: 'Failed',
              usedFallback: false,
              createdFiles: false,
              message: 'Remote clone failed.',
              warnings: ['auth required'],
              error: 'Remote clone failed.'
            }),
            { status: 422, statusText: 'Unprocessable Entity' }
          )
        )
        .mockResolvedValueOnce(
          new Response(
            JSON.stringify({ error: "Project 'missing' not found." }),
            { status: 404, statusText: 'Not Found' }
          )
        );

      const failedResult = await api.setupProjectWorkspace('failing-proj');
      expect(failedResult.success).toBe(false);
      expect(failedResult.outcome).toBe('Failed');
      expect(failedResult.warnings).toEqual(['auth required']);
      expect(failedResult.error).toBe('Remote clone failed.');

      await expect(api.setupProjectWorkspace('missing')).rejects.toThrow(
        "Failed to setup project workspace: Project 'missing' not found."
      );
    });
  });
});
