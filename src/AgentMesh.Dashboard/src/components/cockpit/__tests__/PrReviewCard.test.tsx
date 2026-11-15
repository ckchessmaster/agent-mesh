import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { PrReviewCard } from '../cards/PrReviewCard';
import type { JobTimelineCard, TimelineCardStatus } from '../../../types/timeline';

const gatePayload = {
  note: null,
  prUrl: 'https://github.com/ckchessmaster/homelab-manager/pull/1',
  round: 1,
  source: 'workflow:job_pr_gate',
  headSha: '8b8796e53da59fea83065d4fee823b5c5f17530f',
  prNumber: 1,
  reviewerId: null,
  targetBranch: 'mesh/job-83d49334',
  senderPersona: 'bee',
  authorPersonaId: 'bee',
};

function buildCard(
  status: TimelineCardStatus,
  payload: Record<string, unknown> = gatePayload
): JobTimelineCard {
  return {
    id: 'card-pr-1',
    jobId: 'job-83d49334',
    cardType: 'PrReview',
    status,
    title: status === 'PendingInput' ? 'Awaiting Review: PR #1' : `Merged: PR #1`,
    structuredPayloadJson: JSON.stringify(payload),
    createdAt: '2026-11-15T10:00:00Z',
    updatedAt: '2026-11-15T10:00:00Z',
    comments: [],
  };
}

describe('PrReviewCard', () => {
  it('renders the parsed gate state instead of raw JSON: link, author, branch, commit', () => {
    render(<PrReviewCard card={buildCard('PendingInput')} />);

    expect(screen.getByText('Awaiting Review: PR #1')).toBeInTheDocument();
    const link = screen.getByTestId('pr-review-link');
    expect(link).toHaveAttribute('href', 'https://github.com/ckchessmaster/homelab-manager/pull/1');
    // The old raw-JSON dump must be gone — that is the whole point of the component existing.
    expect(screen.queryByText(/"source":\s*"workflow:job_pr_gate"/)).not.toBeInTheDocument();
    expect(screen.getByText(/homelab-manager\/pull\/1/)).toBeInTheDocument();
    // Exact matches: the rejection label interpolates the same @author, and a substring query
    // would silently assert on the wrong element.
    expect(screen.getByText('@bee')).toBeInTheDocument();
    expect(screen.getByText('8b8796e')).toBeInTheDocument();
    expect(screen.getByText('mesh/job-83d49334')).toBeInTheDocument();
  });

  it('pending gate shows Approve and Reject buttons that carry the PR number and feedback', async () => {
    const onSubmitPrDecision = vi.fn().mockResolvedValue(undefined);
    render(<PrReviewCard card={buildCard('PendingInput')} onSubmitPrDecision={onSubmitPrDecision} />);

    fireEvent.change(screen.getByRole('textbox'), {
      target: { value: 'Reconnect drops stdin after 3 retries' },
    });
    fireEvent.click(screen.getByTestId('reject-pr-btn'));

    await waitFor(() =>
      expect(onSubmitPrDecision).toHaveBeenCalledWith(
        'card-pr-1',
        false,
        'Reconnect drops stdin after 3 retries',
        1
      )
    );
  });

  it('approve sends no comments rather than an empty string', async () => {
    const onSubmitPrDecision = vi.fn().mockResolvedValue(undefined);
    render(<PrReviewCard card={buildCard('PendingInput')} onSubmitPrDecision={onSubmitPrDecision} />);

    fireEvent.click(screen.getByTestId('approve-pr-btn'));

    await waitFor(() =>
      expect(onSubmitPrDecision).toHaveBeenCalledWith('card-pr-1', true, undefined, 1)
    );
  });

  it('a rejected 403 stays visible inline and keeps the card actionable', async () => {
    const onSubmitPrDecision = vi
      .fn()
      .mockRejectedValue(new Error('Failed to submit PR review: self_review_not_allowed'));
    render(<PrReviewCard card={buildCard('PendingInput')} onSubmitPrDecision={onSubmitPrDecision} />);

    fireEvent.click(screen.getByTestId('approve-pr-btn'));

    await waitFor(() =>
      expect(screen.getByTestId('pr-review-error')).toHaveTextContent('self_review_not_allowed')
    );
    // Still PendingInput after a failed delivery: the operator can fix the identity and retry.
    expect(screen.getByTestId('approve-pr-btn')).not.toBeDisabled();
  });

  it('resolved card: no decision controls, merged banner, note and reviewer preserved', () => {
    const resolved = buildCard('Resolved', {
      ...gatePayload,
      reviewerId: 'github:ckingdon-chris',
      note: 'Merged pull request #1 into ckchessmaster/homelab-manager via squash (commit ff00ff0).',
    });
    render(<PrReviewCard card={resolved} />);

    expect(screen.queryByTestId('approve-pr-btn')).not.toBeInTheDocument();
    expect(screen.queryByTestId('reject-pr-btn')).not.toBeInTheDocument();
    expect(screen.getByTestId('pr-review-outcome')).toHaveTextContent('merged');
    expect(screen.getByText(/via squash/)).toBeInTheDocument();
    expect(screen.getByText(/by github:ckingdon-chris/)).toBeInTheDocument();
  });

  it('rejected card surfaces the reviewer feedback as the remediation source', () => {
    const rejected = buildCard('Rejected', {
      ...gatePayload,
      reviewerId: 'operator',
      note: 'Rename the exec-pty handler before merge.',
      round: 2,
    });
    render(<PrReviewCard card={rejected} />);

    expect(screen.getByTestId('pr-review-status-badge')).toHaveTextContent('Changes Requested');
    expect(screen.getByText('Rename the exec-pty handler before merge.')).toBeInTheDocument();
    expect(screen.getByText('round 2')).toBeInTheDocument();
  });

  it('unparseable payload still renders the card rather than throwing', () => {
    const broken = { ...buildCard('PendingInput'), structuredPayloadJson: '{not json' };
    render(<PrReviewCard card={broken} />);

    expect(screen.getByTestId('pr-review-card')).toBeInTheDocument();
    expect(screen.getByText(broken.title)).toBeInTheDocument();
  });
});
