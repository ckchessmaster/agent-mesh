import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { StickySidebar } from '../StickySidebar';
import type { ProposedChecklistItem } from '../../../types/timeline';

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
});

