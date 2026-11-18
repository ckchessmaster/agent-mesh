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

  describe('dashboard overview client & mapper', () => {
    it('fetches /api/dashboard/overview and maps camelCase stats, recentJobs, and attentionItems', async () => {
      const payload = {
        generatedAt: '2026-11-18T15:30:00Z',
        jobStats: {
          totalProjects: 3,
          activeProjects: 2,
          totalJobs: 9,
          activeJobs: 2,
          pendingJobs: 1,
          completedJobs: 4,
          archivedJobs: 2,
          attentionCount: 5,
          activeSessions: 3,
          thinkingSessions: 2,
          waitingForInputSessions: 1,
        },
        recentJobs: [
          {
            id: 'job-1',
            projectId: 'agent-mesh',
            projectName: 'Agent Mesh',
            title: 'Agent Mesh Dashboard',
            description: 'Build main landing page',
            status: 0,
            gitBranchName: 'mesh/job-1ff55314',
            tasks: [
              {
                id: 'task-1',
                title: 'Add backend endpoint',
                isCompleted: true,
                status: 'completed',
                assignedPersonaId: 'bee',
              },
            ],
            priority: 2,
            approvalPolicy: 2,
            currentTaskId: 'task-2',
            progressHeartbeat: 'Executing task 2',
            lifecycleStatus: 'InProgress',
            assignedAgentId: 'bee',
            messageCount: 4,
            createdAt: '2026-11-18T15:00:00Z',
            updatedAt: '2026-11-18T15:29:00Z',
          },
        ],
        attentionItems: [
          {
            id: 'card:card-99',
            kind: 'escalation',
            severity: 'critical',
            title: 'Database migration blocked',
            summary: 'Requires schema decision',
            projectId: 'agent-mesh',
            projectName: 'Agent Mesh',
            jobId: 'job-1',
            jobTitle: 'Agent Mesh Dashboard',
            cardId: 'card-99',
            cardType: 'Escalation3R',
            sessionId: null,
            personaId: 'bee',
            personaName: 'Bee',
            driverId: 'beeline-gh-copilot',
            createdAt: '2026-11-18T15:20:00Z',
            updatedAt: '2026-11-18T15:25:00Z',
          },
        ],
      };

      const mockFetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        new Response(JSON.stringify(payload), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      );

      const overview = await api.getDashboardOverview();

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/dashboard/overview',
        expect.objectContaining({
          headers: expect.objectContaining({
            Authorization: 'Bearer mesh-default-secret-key',
          }),
        })
      );
      expect(overview.generatedAt).toBe('2026-11-18T15:30:00Z');
      expect(overview.jobStats).toEqual({
        totalProjects: 3,
        activeProjects: 2,
        totalJobs: 9,
        activeJobs: 2,
        pendingJobs: 1,
        completedJobs: 4,
        archivedJobs: 2,
        attentionCount: 5,
        activeSessions: 3,
        thinkingSessions: 2,
        waitingForInputSessions: 1,
      });
      expect(overview.recentJobs).toHaveLength(1);
      expect(overview.recentJobs[0].projectName).toBe('Agent Mesh');
      expect(overview.recentJobs[0].lifecycleStatus).toBe('InProgress');
      expect(overview.recentJobs[0].tasks).toEqual([
        expect.objectContaining({
          id: 'task-1',
          title: 'Add backend endpoint',
          isCompleted: true,
          status: 'completed',
          assignedPersonaId: 'bee',
        }),
      ]);
      expect(overview.attentionItems).toHaveLength(1);
      expect(overview.attentionItems[0]).toEqual(
        expect.objectContaining({
          id: 'card:card-99',
          kind: 'escalation',
          severity: 'critical',
          cardId: 'card-99',
          cardType: 'Escalation3R',
          personaId: 'bee',
          personaName: 'Bee',
          driverId: 'beeline-gh-copilot',
        })
      );
    });

    it('builds recentJobsLimit, attentionLimit, and includeArchived query parameters', async () => {
      const mockFetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        new Response(JSON.stringify({ jobStats: {}, recentJobs: [], attentionItems: [] }), { status: 200 })
      );

      await api.getDashboardOverview({
        recentJobsLimit: 10,
        attentionLimit: 15,
        includeArchived: false,
      });

      expect(mockFetch).toHaveBeenCalledWith(
        '/api/dashboard/overview?recentJobsLimit=10&attentionLimit=15&includeArchived=false',
        expect.anything()
      );
    });

    it('normalizes PascalCase wire fields and tolerates omitted arrays or stats', async () => {
      const pascalPayload = {
        GeneratedAt: '2026-11-18T16:00:00Z',
        JobStats: {
          TotalProjects: 2,
          ActiveProjects: 1,
          TotalJobs: 3,
          ActiveJobs: 1,
          PendingJobs: 0,
          CompletedJobs: 2,
          ArchivedJobs: 0,
          AttentionCount: 2,
          ActiveSessions: 1,
          ThinkingSessions: 0,
          WaitingForInputSessions: 1,
        },
        RecentJobs: [
          {
            Id: 'job-pascal',
            ProjectId: 'proj-alpha',
            ProjectName: 'Alpha',
            Title: 'Pascal Job',
            Description: 'From PascalCase proxy',
            Status: 3,
            GitBranchName: 'mesh/job-pascal',
            Tasks: [{ Id: 't-1', Title: 'Verify', IsCompleted: false, RequiresDiscussion: true }],
            MessageCount: 2,
            CreatedAt: '2026-11-18T15:50:00Z',
            UpdatedAt: '2026-11-18T15:55:00Z',
          },
        ],
        AttentionItems: [
          {
            Id: 'session:sess-1',
            Kind: 'session_waiting_input',
            Severity: 'warning',
            Title: 'Interactive session',
            Summary: 'Waiting for operator input',
            SessionId: 'sess-1',
            PersonaId: 'astra',
            PersonaName: 'Astra',
            CreatedAt: '2026-11-18T15:52:00Z',
          },
        ],
      };

      const fetchSpy = vi
        .spyOn(globalThis, 'fetch')
        .mockResolvedValueOnce(new Response(JSON.stringify(pascalPayload), { status: 200 }))
        .mockResolvedValueOnce(new Response(JSON.stringify({}), { status: 200 }));

      const normalized = await api.getDashboardOverview();
      expect(normalized.generatedAt).toBe('2026-11-18T16:00:00Z');
      expect(normalized.jobStats.totalProjects).toBe(2);
      expect(normalized.jobStats.attentionCount).toBe(2);
      expect(normalized.recentJobs[0]).toEqual(
        expect.objectContaining({
          id: 'job-pascal',
          projectId: 'proj-alpha',
          projectName: 'Alpha',
          status: 3,
          messageCount: 2,
        })
      );
      expect(normalized.recentJobs[0].tasks[0]).toEqual(
        expect.objectContaining({
          id: 't-1',
          title: 'Verify',
          isCompleted: false,
          requiresDiscussion: true,
        })
      );
      expect(normalized.attentionItems[0]).toEqual(
        expect.objectContaining({
          id: 'session:sess-1',
          kind: 'session_waiting_input',
          severity: 'warning',
          sessionId: 'sess-1',
          personaId: 'astra',
          personaName: 'Astra',
          updatedAt: '2026-11-18T15:52:00Z',
        })
      );

      const emptyFallback = await api.getDashboardOverview();
      expect(fetchSpy).toHaveBeenCalledTimes(2);
      expect(emptyFallback.recentJobs).toEqual([]);
      expect(emptyFallback.attentionItems).toEqual([]);
      expect(emptyFallback.jobStats.totalJobs).toBe(0);
      expect(emptyFallback.jobStats.attentionCount).toBe(0);
    });

    it('surfaces server error message when /api/dashboard/overview fails', async () => {
      vi.spyOn(globalThis, 'fetch').mockResolvedValue(
        new Response(JSON.stringify({ error: 'Database unavailable' }), {
          status: 503,
          statusText: 'Service Unavailable',
        })
      );

      await expect(api.getDashboardOverview()).rejects.toThrow(
        'Failed to load dashboard overview: Database unavailable'
      );
    });
  });
});
