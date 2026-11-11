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
});
