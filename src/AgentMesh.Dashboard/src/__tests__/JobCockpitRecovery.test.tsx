// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { JobCockpit } from '../components/JobCockpit';
import { api } from '../services/api';
import type { Project, ProjectJob } from '../types';
import { ProjectJobStatus, WorkspaceType } from '../types';

const project: Project = {
  id: 'agent-mesh',
  name: 'agent-mesh',
  description: '',
  workspaceType: WorkspaceType.Git,
  gitBaseBranch: 'main',
  repositoryName: 'agent-mesh',
  workingDirectory: '/workspace/repos/agent-mesh',
  projectInstructions: '',
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
} as unknown as Project;

/**
 * The state the incident left behind: the job is Active, its last heartbeat still names the task it was
 * executing, and the platform's lifecycle derivation reads that as Stalled.
 */
const stalledJob = (over: Partial<ProjectJob> = {}): ProjectJob =>
  ({
    id: '538070c7-9e21-4dbc-a516-675bfff049e6',
    projectId: 'agent-mesh',
    title: 'Standardized project setup',
    description: '',
    status: ProjectJobStatus.Active,
    gitBranchName: 'mesh/job-538070c7',
    tasksJson: '[]',
    progressHeartbeat: 'Stalled: the 110-minute observation ceiling expired',
    lifecycleStatus: 'Stalled',
    assignedAgentId: 'astra',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...over
  }) as unknown as ProjectJob;

const renderCockpit = (job: ProjectJob, onRecoverJob?: () => Promise<void>) =>
  render(
    <JobCockpit
      project={project}
      job={job}
      tasks={[]}
      personas={[]}
      sessions={[]}
      onAddTask={vi.fn()}
      onToggleTask={vi.fn()}
      onDeleteTask={vi.fn()}
      onCompleteJob={vi.fn()}
      onResetJob={vi.fn()}
      onRecoverJob={onRecoverJob}
    />
  );

describe('JobCockpit stalled-job recovery', () => {
  it('shows that nothing is running, instead of an active job', () => {
    renderCockpit(stalledJob());

    expect(screen.getByTestId('job-status-stalled')).toBeInTheDocument();
    expect(screen.queryByTestId('job-status-active')).not.toBeInTheDocument();
  });

  it('offers Recover only when there is something to recover', () => {
    const { unmount } = renderCockpit(stalledJob(), vi.fn().mockResolvedValue(undefined));
    expect(screen.getByTestId('cockpit-recover-job-btn')).toBeInTheDocument();
    unmount();

    renderCockpit(stalledJob({ lifecycleStatus: 'InProgress' }), vi.fn().mockResolvedValue(undefined));
    expect(screen.queryByTestId('cockpit-recover-job-btn')).not.toBeInTheDocument();
    expect(screen.queryByTestId('job-status-stalled')).not.toBeInTheDocument();
  });

  it('stays silent when the parent cannot reach the recovery endpoint', () => {
    // An older API build answers 404 for /recover. A button that can only error is worse than no button.
    renderCockpit(stalledJob());
    expect(screen.queryByTestId('cockpit-recover-job-btn')).not.toBeInTheDocument();
  });

  it('does not offer recovery for an archived job the marker mentions', () => {
    renderCockpit(stalledJob({ status: ProjectJobStatus.Archived }));

    expect(screen.queryByTestId('cockpit-recover-job-btn')).not.toBeInTheDocument();
  });

  it('asks before rewriting a running job, and only then calls', async () => {
    const onRecoverJob = vi.fn().mockResolvedValue(undefined);
    renderCockpit(stalledJob(), onRecoverJob);

    fireEvent.click(screen.getByTestId('cockpit-recover-job-btn'));

    // Recovery starts a workflow run, so it gets the same confirmation step Reset has. Unlike Reset it
    // does not warn about discarding work, because that is the whole reason to prefer it.
    expect(onRecoverJob).not.toHaveBeenCalled();
    expect(screen.getByText(/resume from recorded progress/i)).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('cockpit-confirm-recover-btn'));

    await waitFor(() => expect(onRecoverJob).toHaveBeenCalledTimes(1));
    // Wait for the component to settle rather than returning the moment the callback fired: a state
    // update landing after the file tears down makes vitest's worker close its RPC mid-log, which
    // fails CI as an unhandled error with no failing assertion anywhere.
    await waitFor(() => expect(screen.getByTestId('cockpit-recover-job-btn')).toBeEnabled());
  });

  it('cancels without touching the job', () => {
    const onRecoverJob = vi.fn();
    renderCockpit(stalledJob(), onRecoverJob);

    fireEvent.click(screen.getByTestId('cockpit-recover-job-btn'));
    fireEvent.click(screen.getByText('Cancel'));

    expect(onRecoverJob).not.toHaveBeenCalled();
    expect(screen.queryByTestId('cockpit-confirm-recover-btn')).not.toBeInTheDocument();
    expect(screen.getByTestId('cockpit-recover-job-btn')).toBeInTheDocument();
  });

  it('reports that it is working while the call is in flight', async () => {
    let finish: () => void = () => {};
    const onRecoverJob = vi.fn().mockReturnValue(
      new Promise<void>((resolve) => {
        finish = resolve;
      })
    );
    renderCockpit(stalledJob(), onRecoverJob);

    fireEvent.click(screen.getByTestId('cockpit-recover-job-btn'));
    fireEvent.click(screen.getByTestId('cockpit-confirm-recover-btn'));

    const confirming = screen.getByTestId('cockpit-confirm-recover-btn');
    expect(confirming).toBeDisabled();
    expect(confirming.textContent).toMatch(/Recovering/);

    // act(): the resolution lands in React state, and it must resolve before this test returns.
    act(() => finish());
    await waitFor(() => expect(screen.queryByTestId('cockpit-confirm-recover-btn')).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId('cockpit-recover-job-btn')).toBeEnabled());
  });

  it('keeps offering the confirm row if recovery fails, so the operator can read the error', async () => {
    const onRecoverJob = vi.fn().mockRejectedValue(new Error('This deployment has no recovery service.'));
    const { container } = renderCockpit(stalledJob(), onRecoverJob);

    fireEvent.click(screen.getByTestId('cockpit-recover-job-btn'));
    fireEvent.click(screen.getByTestId('cockpit-confirm-recover-btn'));

    // The parent surfaces the failure; the cockpit must not optimistically close the row and pretend the
    // job was recovered, or the operator has to work out that nothing happened. Re-enabling proves the
    // in-flight flag was cleared on the failure path too, so the operator can retry without a refresh.
    await waitFor(() => expect(onRecoverJob).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByTestId('cockpit-confirm-recover-btn')).toBeEnabled());
    expect(screen.getByTestId('cockpit-confirm-recover-btn')).toBeInTheDocument();
    expect(container).toBeTruthy();
  });

  it('never replaces Reset', () => {
    renderCockpit(stalledJob(), vi.fn().mockResolvedValue(undefined));

    // Reset discards the checklist and terminates the run; Recover keeps both. The operator has to be
    // able to see the difference at the moment they choose, so they sit side by side.
    expect(screen.getByTestId('cockpit-recover-job-btn')).toBeInTheDocument();
    expect(screen.getByTestId('cockpit-reset-job-btn')).toBeInTheDocument();
  });
});

describe('api.recoverJob', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const json = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': 'application/json' }
    });

  it('posts to the recover route and returns both halves of the response', async () => {
    const job = stalledJob();
    fetchMock.mockResolvedValue(
      json({ recovery: { jobId: job.id, action: 'Resumed', message: 'Resumed at task 3/5.' }, job })
    );

    const res = await api.recoverJob(job.id, { note: 'driver wedged' });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toContain(`/api/project-jobs/${job.id}/recover`);
    expect(init.method).toBe('POST');
    expect(JSON.parse(String(init.body))).toMatchObject({ note: 'driver wedged' });
    expect(res.recovery.action).toBe('Resumed');
    expect(res.job?.id).toBe(job.id);
  });

  it('sends an empty body when the caller has no preferences', async () => {
    fetchMock.mockResolvedValue(json({ recovery: { action: 'SettledOnly' } }));

    await api.recoverJob('job-1');

    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(JSON.parse(String(init.body))).toEqual({});
  });

  it('passes resume:false through, so an operator can settle without starting anything', async () => {
    fetchMock.mockResolvedValue(json({ recovery: { action: 'SettledOnly' } }));

    await api.recoverJob('job-1', { resume: false });

    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(JSON.parse(String(init.body))).toMatchObject({ resume: false });
  });

  it('surfaces the server reason, which is the one line that explains a refusal', async () => {
    // A fresh Response per call: a body can only be read once.
    fetchMock.mockImplementation(async () =>
      json({ error: "Job 'job-1' is archived. Unarchive it first." }, 409));

    // A bare "Failed to recover job (409)" would send the operator to the API logs to learn that the fix
    // is to unarchive the job, so the server's sentence has to survive the transport.
    const failure = await api.recoverJob('job-1').catch((e: Error) => e);
    expect((failure as Error).message).toContain('Unarchive it first');
    expect((failure as Error).message).not.toContain('409');
  });

  it('falls back to the status when the body is not JSON', async () => {
    fetchMock.mockImplementation(async () => new Response('<html>gateway timeout</html>', { status: 504 }));

    await expect(api.recoverJob('job-1')).rejects.toThrow(/504/);
  });
});
