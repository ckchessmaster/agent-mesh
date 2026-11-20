import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { StickySidebar } from '../StickySidebar';
import type { ProposedChecklistItem } from '../../../types/timeline';
import { ProjectJobStatus, type ProjectJob } from '../../../types';

/**
 * The exact shape a halted job produces: Active row, `Halted:` heartbeat the API derives as Blocked,
 * and — critically for these two tests — a TaskStep card still stuck at `Active`, because halting a
 * job never settles the card its last turn was updating.
 */
const haltedJob = {
  id: 'ed40ae7c-c816-4d71-850d-44e009ce0f77',
  projectId: 'agent-mesh',
  title: 'job attachments',
  description: '',
  status: ProjectJobStatus.Active,
  gitBranchName: 'mesh/job-ed40ae7c',
  tasks: [],
  lifecycleStatus: 'Blocked',
  progressHeartbeat: 'Halted: Task 0 (Add ProjectJob.AttachmentsJson) failed - workflow halted after 40 turns',
  createdAt: '2026-11-19T19:30:20Z',
  updatedAt: '2026-11-20T02:20:45Z'
} as unknown as ProjectJob;

const staleActiveStepCard = {
  id: 'step-9',
  jobId: 'ed40ae7c-c816-4d71-850d-44e009ce0f77',
  cardType: 'TaskStep' as const,
  status: 'Active' as const,
  title: 'Task 1: Add ProjectJob.AttachmentsJson',
  createdAt: '2026-11-19T19:40:00Z',
  updatedAt: '2026-11-19T20:25:00Z',
  structuredPayloadJson: JSON.stringify({ taskIndex: 0, heartbeat: 'Step 40: In progress' })
};

describe('StickySidebar', () => {
  const sampleChecklist: ProposedChecklistItem[] = [
    {
      taskId: 'task-1',
      title: 'Initialize Database Schemas',
      targetFile: 'src/AgentMesh.Core/Models/TimelineCards.cs',
      verificationMethod: 'Models compiled',
      status: 'completed'
    },
    {
      taskId: 'task-2',
      title: 'Implement Temporal Workflows',
      targetFile: 'src/AgentMesh.Worker/Workflows/JobExecutionWorkflow.cs',
      verificationMethod: 'Workflows registered',
      status: 'in_progress'
    },
    {
      taskId: 'task-3',
      title: 'Build Dashboard Components',
      targetFile: 'src/AgentMesh.Dashboard/src/components/cockpit/TimelineFeed.tsx',
      verificationMethod: 'UI rendered',
      status: 'pending'
    }
  ];

  it('renders cockpit header and living checklist with status indicators', () => {
    render(
      <StickySidebar
        jobId="test-job-123"
        checklist={sampleChecklist}
        heartbeat="Compiling TypeScript Dashboard"
        temporalHealth={{
          workflowId: 'job-test-job-123',
          runId: 'run-456',
          attempt: 2,
          status: 'Running'
        }}
      />
    );

    expect(screen.getByText('Execution Cockpit')).toBeInTheDocument();
    expect(screen.getByText('Initialize Database Schemas')).toBeInTheDocument();
    expect(screen.getByText('Implement Temporal Workflows')).toBeInTheDocument();
    expect(screen.getByText('Build Dashboard Components')).toBeInTheDocument();

    // Verify status screen-reader icons
    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('►')).toBeInTheDocument();
    expect(screen.getByText('○')).toBeInTheDocument();

    // Verify heartbeat
    expect(screen.getByText(/💓 Compiling TypeScript Dashboard/)).toBeInTheDocument();

    // Verify Temporal health
    expect(screen.getByText('job-test-job-123')).toBeInTheDocument();
    expect(screen.getByText('run-456')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
  });

  it('dispatches onAbortTurn when stop active turn button is clicked', async () => {
    const onAbortTurn = vi.fn().mockResolvedValue(undefined);

    render(
      <StickySidebar
        jobId="test-job-123"
        checklist={sampleChecklist}
        onAbortTurn={onAbortTurn}
      />
    );

    const stopBtn = screen.getByTestId('stop-active-turn-btn');
    expect(stopBtn).toBeInTheDocument();
    expect(stopBtn).toHaveTextContent('🛑 Stop Active Turn');

    fireEvent.click(stopBtn);

    await waitFor(() => {
      expect(onAbortTurn).toHaveBeenCalledTimes(1);
      expect(onAbortTurn).toHaveBeenCalledWith('Turn stopped by operator via Cockpit Sidebar');
    });
  });

  it('prefers the latest Approved PlanApproval card and marks Resolved TaskStep cards as completed', () => {
    const cards = [
      {
        id: 'plan-1',
        jobId: 'test-job-123',
        stage: 1,
        cardType: 'PlanApproval' as const,
        status: 'Rejected' as const,
        title: 'Initial Plan',
        summaryMarkdown: '',
        createdAt: '2026-11-11T00:00:00Z',
        updatedAt: '2026-11-11T00:00:00Z',
        structuredPayloadJson: JSON.stringify({
          tasks: [
            {
              taskId: 'old-task-1',
              title: 'Rejected Old Task',
              targetFile: 'old.ts',
              verificationMethod: 'old test',
              status: 'pending'
            }
          ]
        })
      },
      {
        id: 'plan-2',
        jobId: 'test-job-123',
        stage: 1,
        cardType: 'PlanApproval' as const,
        status: 'Approved' as const,
        title: 'Revised Plan',
        summaryMarkdown: '',
        createdAt: '2026-11-11T00:05:00Z',
        updatedAt: '2026-11-11T00:05:00Z',
        structuredPayloadJson: JSON.stringify({
          tasks: [
            {
              taskId: 'new-task-1',
              title: 'Approved Task One',
              targetFile: 'src/one.ts',
              verificationMethod: 'npm test',
              status: 'pending'
            },
            {
              taskId: 'new-task-2',
              title: 'Approved Task Two',
              targetFile: 'src/two.ts',
              verificationMethod: 'npm test',
              status: 'pending'
            }
          ]
        })
      },
      {
        id: 'step-1',
        jobId: 'test-job-123',
        stage: 2,
        cardType: 'TaskStep' as const,
        status: 'Resolved' as const,
        title: 'Step 1: Approved Task One',
        summaryMarkdown: '',
        createdAt: '2026-11-11T00:10:00Z',
        updatedAt: '2026-11-11T00:12:00Z',
        structuredPayloadJson: JSON.stringify({
          taskIndex: 0,
          title: 'Approved Task One'
        })
      }
    ];

    render(<StickySidebar jobId="test-job-123" cards={cards} />);

    expect(screen.queryByText('Rejected Old Task')).not.toBeInTheDocument();
    expect(screen.getByText('Approved Task One')).toBeInTheDocument();
    expect(screen.getByText('Approved Task Two')).toBeInTheDocument();
    expect(screen.getByText('1/2')).toBeInTheDocument();
    expect(screen.getByText('✓')).toBeInTheDocument();
    expect(screen.getByText('○')).toBeInTheDocument();
  });

  it('renders full-width on mobile with collapsible body toggle and 320px width on desktop', () => {
    render(<StickySidebar jobId="test-job-123" checklist={sampleChecklist} />);

    const aside = screen.getByTestId('sticky-sidebar');
    expect(aside.className).toContain('w-full');
    expect(aside.className).toContain('md:w-[320px]');
    expect(aside.className).toContain('max-h-[60vh]');
    expect(aside.className).toContain('md:h-full');

    const toggle = screen.getByTestId('mobile-cockpit-toggle');
    const body = screen.getByTestId('sticky-sidebar-body');

    // Starts collapsed on mobile (< md) while remaining always visible on md+
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(body.className).toContain('hidden');
    expect(body.className).toContain('md:flex');

    // Expand on mobile
    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(body.className).not.toContain('hidden');
    expect(body.className).toContain('flex');

    // Collapse again
    fireEvent.click(toggle);
    expect(body.className).toContain('hidden');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('says Blocked — not pulsing Running — when the run has halted, even with a stale Active step card', () => {
    // The halt settles the job, not its TaskStep card: the ledger keeps an `Active` card from the last
    // turn forever. The lifecycle marker has to outrank it, or the cockpit chip lies about what is
    // executing while the dashboard next door shows a Blocked badge.
    render(<StickySidebar jobId={haltedJob.id!} job={haltedJob} cards={[staleActiveStepCard]} />);

    const chip = screen.getByTestId('cockpit-status-chip');
    expect(chip).toHaveTextContent('Blocked');
    expect(chip.textContent).not.toContain('Running');
    expect(chip.getAttribute('title')).toContain('halted after 40 turns');
  });

  it('disables Stop Active Turn on a halted job, because the abort signal has nowhere to go', () => {
    // An abort is a signal to a live run; a halted job's run is closed, so the red button was a
    // control whose only honest outcome was a silent no-op.
    const onAbortTurn = vi.fn();
    render(
      <StickySidebar
        jobId={haltedJob.id!}
        job={haltedJob}
        cards={[staleActiveStepCard]}
        onAbortTurn={onAbortTurn}
      />
    );

    const stopBtn = screen.getByTestId('stop-active-turn-btn');
    expect(stopBtn).toBeDisabled();
    expect(stopBtn).toHaveTextContent('none running');
    expect(stopBtn.getAttribute('title')).toMatch(/Recover Job/);
  });

  it('keeps Stop Active Turn armed while a turn is genuinely running', () => {
    const onAbortTurn = vi.fn().mockResolvedValue(undefined);
    render(
      <StickySidebar
        jobId="live-job"
        cards={[staleActiveStepCard]}
        onAbortTurn={onAbortTurn}
      />
    );

    const stopBtn = screen.getByTestId('stop-active-turn-btn');
    expect(stopBtn).toBeEnabled();
    fireEvent.click(stopBtn);
    return waitFor(() => expect(onAbortTurn).toHaveBeenCalledTimes(1));
  });
});

