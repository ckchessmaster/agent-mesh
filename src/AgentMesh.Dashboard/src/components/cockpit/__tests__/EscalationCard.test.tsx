import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { EscalationCard } from '../cards/EscalationCard';
import { TimelineFeed } from '../TimelineFeed';
import type { JobTimelineCard } from '../../../types/timeline';

const pausedCard: JobTimelineCard = {
  id: 'card-esc-1',
  jobId: 'ed40ae7c-c816-4d71-850d-44e009ce0f77',
  cardType: 'Escalation3R',
  status: 'PendingInput',
  title: 'Agent @test paused on error',
  structuredPayloadJson: JSON.stringify({
    content:
      '⚠️ **Agent @test encountered an error and paused for assistance:**\n\n> 429 budget exhausted\n\nPlease reply on this timeline card.',
    requiresReply: true,
    targetPersona: 'operator',
    senderPersona: 'test',
    authorPersonaId: 'test',
    source: 'worker:error_escalation'
  }),
  createdAt: '2026-11-19T21:00:00Z',
  updatedAt: '2026-11-19T21:00:00Z',
  comments: []
};

describe('EscalationCard', () => {
  it('renders the escalation body and who raised it', () => {
    render(<EscalationCard card={pausedCard} />);

    // Raised-by line scoped via the <p> that owns it: the title also names @test, so a bare /@test/
    // would match both.
    expect(screen.getByText('Agent @test paused on error')).toBeInTheDocument();
    expect(screen.getByText(/429 budget exhausted/)).toBeInTheDocument();
    expect(screen.getByText('Raised by')).toHaveTextContent('@test');
    expect(screen.getByTestId('escalation-card-status')).toHaveTextContent('Needs You');
  });

  it('resolves with an optional note, straight from the card', async () => {
    const onResolve = vi.fn().mockResolvedValue(undefined);
    render(<EscalationCard card={pausedCard} onResolve={onResolve} />);

    fireEvent.change(screen.getByTestId('escalation-resolve-note'), {
      target: { value: 'login refreshed out-of-band' }
    });
    fireEvent.click(screen.getByTestId('escalation-resolve-btn'));

    await waitFor(() =>
      expect(onResolve).toHaveBeenCalledWith('card-esc-1', 'login refreshed out-of-band')
    );
  });

  it('keeps the button live when the resolve fails — a failed resolve must not look retired', async () => {
    const onResolve = vi.fn().mockRejectedValue(new Error('This deployment has no resolve route.'));
    render(<EscalationCard card={pausedCard} onResolve={onResolve} />);

    fireEvent.click(screen.getByTestId('escalation-resolve-btn'));

    await waitFor(() =>
      expect(screen.getByTestId('escalation-resolve-error')).toHaveTextContent('no resolve route')
    );
    const btn = screen.getByTestId('escalation-resolve-btn');
    expect(btn).toBeEnabled();
    expect(btn).toHaveTextContent('Resolve Escalation');
  });

  it('renders a resolved card as settled, with the bell gone and no button to ring it again', () => {
    render(
      <EscalationCard
        card={{ ...pausedCard, status: 'Resolved' }}
        onResolve={vi.fn()}
      />
    );

    expect(screen.queryByTestId('escalation-resolve-btn')).not.toBeInTheDocument();
    expect(screen.getByTestId('escalation-card-status')).toHaveTextContent('Resolved');
    expect(screen.getByText(/off the attention queue/i)).toBeInTheDocument();
  });

  it('routes the feed on: Escalation3R cards render as escalations, not generic message slivers', () => {
    // Before this case existed, the one card the Needs-Attention bell rings for fell through the
    // feed's default renderer — no status emphasis, no resolve, buried among turn updates.
    const onResolve = vi.fn().mockResolvedValue(undefined);
    render(
      <TimelineFeed cards={[{ ...pausedCard, comments: [] }]} onResolveCard={onResolve} />
    );

    expect(screen.getByTestId('escalation-card')).toBeInTheDocument();
    fireEvent.click(screen.getByTestId('escalation-resolve-btn'));
    return waitFor(() => expect(onResolve).toHaveBeenCalledWith('card-esc-1', undefined));
  });

  it('renders a turn-budget-exhausted escalation with the recovery guidance the workflow wrote', () => {
    const budgetCard: JobTimelineCard = {
      ...pausedCard,
      id: 'card-esc-2',
      title: 'Turn budget exhausted: task 1: Wire the widget (40/40 turns)',
      structuredPayloadJson: JSON.stringify({
        content:
          '⏳ **Turn budget exhausted (40/40) on task 1: Wire the widget.**\n\nNothing is running on this task any more. To continue:\n- raise this job\'s **Turn budget** (currently 40) and press **Recover Job**',
        source: 'task_workflow:turn_budget',
        turnBudget: 40,
        turnsUsed: 40
      })
    };
    render(<EscalationCard card={budgetCard} onResolve={vi.fn()} />);

    // The phrase lives in both the title and the body, so assert on the heading element.
    expect(
      screen.getByRole('heading', { name: /Turn budget exhausted/ })
    ).toBeInTheDocument();
    expect(screen.getByText(/Recover Job/)).toBeInTheDocument();
  });
});
