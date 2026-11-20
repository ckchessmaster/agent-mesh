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

  it('offers a resolve exit for the case where there is no run left to intervene on', async () => {
    // resume/retry/realign are live-run controls. Once the workflow has halted or completed — the
    // state this whole incident was about — they can only fail, and an operator with no third door
    // has no way to acknowledge what they already know.
    const onResolve = vi.fn().mockResolvedValue(undefined);
    render(<TurnInterruptedCard card={sampleCard} onResolve={onResolve} />);

    const resolveBtn = screen.getByTestId('resolve-turn-card-btn');
    expect(resolveBtn).toHaveTextContent('Mark Resolved');
    fireEvent.click(resolveBtn);

    await waitFor(() => expect(onResolve).toHaveBeenCalledWith('card-interrupted-1', undefined));
    // The resolve path gets its own confirmation: "Intervention action dispatched / turn unblocked"
    // would claim a signal that was never sent — the point of resolving is that there is no run.
    expect(screen.getByText(/off the attention queue/)).toBeInTheDocument();
  });

  it('carries typed guidance into the resolve note, so context written is not lost when retiring', async () => {
    const onResolve = vi.fn().mockResolvedValue(undefined);
    render(<TurnInterruptedCard card={sampleCard} onResolve={onResolve} />);

    fireEvent.change(screen.getByPlaceholderText(/Ensure you rollback/), {
      target: { value: 'PAT was expired; rotated. Reopen the job instead of resuming.' }
    });
    fireEvent.click(screen.getByTestId('resolve-turn-card-btn'));

    await waitFor(() =>
      expect(onResolve).toHaveBeenCalledWith(
        'card-interrupted-1',
        'Operator guidance recorded at resolve: PAT was expired; rotated. Reopen the job instead of resuming.'
      )
    );
  });

  it('without a resolve wiring, renders the original three exits only', () => {
    // Older parents (and the feed without the route) must not grow a dead button.
    render(<TurnInterruptedCard card={sampleCard} onSubmitIntervention={vi.fn()} />);
    expect(screen.queryByTestId('resolve-turn-card-btn')).not.toBeInTheDocument();
  });

  it('keeps the resolve button live when the resolve fails', async () => {
    const onResolve = vi.fn().mockRejectedValue(new Error('no resolve route on this build'));
    render(<TurnInterruptedCard card={sampleCard} onResolve={onResolve} />);

    fireEvent.click(screen.getByTestId('resolve-turn-card-btn'));

    await waitFor(() =>
      expect(screen.getByTestId('resolve-turn-card-error')).toHaveTextContent('no resolve route')
    );
    expect(screen.getByTestId('resolve-turn-card-btn')).toBeEnabled();
  });
});
