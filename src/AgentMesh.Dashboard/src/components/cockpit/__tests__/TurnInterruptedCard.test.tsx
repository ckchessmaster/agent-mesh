import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { TurnInterruptedCard } from '../cards/TurnInterruptedCard';
import type { JobTimelineCard } from '../../../types/timeline';

describe('TurnInterruptedCard', () => {
  const sampleCard: JobTimelineCard = {
    id: 'card-interrupted-1',
    jobId: 'job-123',
    cardType: 'TurnInterrupted',
    status: 'Active',
    title: 'Turn Interrupted: Process Group Terminated',
    structuredPayloadJson: JSON.stringify({
      reason: 'Operator stopped turn due to hung vitest process',
      requestedBy: 'operator',
      interruptedAt: '2026-11-10T22:00:00Z',
      lastOutput: 'FAIL src/components/Sidebar.test.tsx (timeout)'
    }),
    createdAt: '2026-11-10T22:00:00Z',
    updatedAt: '2026-11-10T22:00:00Z',
    comments: []
  };

  it('renders alert banner, trigger reason, and captured subprocess output', () => {
    render(<TurnInterruptedCard card={sampleCard} />);

    expect(screen.getByText('Turn Interrupted: Process Group Terminated')).toBeInTheDocument();
    expect(
      screen.getByText(/Operator stopped turn due to hung vitest process/)
    ).toBeInTheDocument();
    expect(
      screen.getByText('FAIL src/components/Sidebar.test.tsx (timeout)')
    ).toBeInTheDocument();
  });

  it('submits intervention action "resume" with operator guidance', async () => {
    const onSubmitIntervention = vi.fn().mockResolvedValue(undefined);

    render(
      <TurnInterruptedCard
        card={sampleCard}
        onSubmitIntervention={onSubmitIntervention}
      />
    );

    const textarea = screen.getByPlaceholderText(
      /Ensure you rollback the unmigrated table before resuming/
    );
    fireEvent.change(textarea, {
      target: { value: 'Skip failing vitest suite and resume' }
    });

    const resumeBtn = screen.getByTestId('resume-turn-btn');
    fireEvent.click(resumeBtn);

    await waitFor(() => {
      expect(onSubmitIntervention).toHaveBeenCalledWith(
        'resume',
        'Skip failing vitest suite and resume'
      );
    });

    expect(screen.getByText(/Intervention action dispatched \(resume\)/)).toBeInTheDocument();
  });

  it('submits intervention action "retry_step"', async () => {
    const onSubmitIntervention = vi.fn().mockResolvedValue(undefined);

    render(
      <TurnInterruptedCard
        card={sampleCard}
        onSubmitIntervention={onSubmitIntervention}
      />
    );

    const retryBtn = screen.getByTestId('retry-step-btn');
    fireEvent.click(retryBtn);

    await waitFor(() => {
      expect(onSubmitIntervention).toHaveBeenCalledWith('retry_step', undefined);
    });

    expect(screen.getByText(/Intervention action dispatched \(retry_step\)/)).toBeInTheDocument();
  });

  it('submits intervention action "realign"', async () => {
    const onSubmitIntervention = vi.fn().mockResolvedValue(undefined);

    render(
      <TurnInterruptedCard
        card={sampleCard}
        onSubmitIntervention={onSubmitIntervention}
      />
    );

    const realignBtn = screen.getByTestId('realign-turn-btn');
    fireEvent.click(realignBtn);

    await waitFor(() => {
      expect(onSubmitIntervention).toHaveBeenCalledWith('realign', undefined);
    });

    expect(screen.getByText(/Intervention action dispatched \(realign\)/)).toBeInTheDocument();
  });
});
