import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { DiscoveryQaCard } from '../cards/DiscoveryQaCard';
import type { JobTimelineCard, TimelineCardStatus } from '../../../types/timeline';

function buildCard(payload: unknown, status: TimelineCardStatus): JobTimelineCard {
  return {
    id: 'card-discovery-1',
    jobId: 'job-c28eca57',
    cardType: 'DiscoveryQa',
    status,
    title: 'Discovery Q&A: Clarify Task Requirements',
    structuredPayloadJson: JSON.stringify(payload),
    createdAt: '2026-11-16T21:33:34Z',
    updatedAt: '2026-11-16T21:33:34Z',
    comments: [],
  };
}

/** What the Discovery Q&A activity writes: questions with options, answers keyed by question id. */
const activityPayload = {
  summary: 'Two answers change what Stage 3 clones.',
  questions: [
    { id: 'q_1', question: 'How should the repository workspace be resolved?', options: ['Re-provision', 'Clone into repos/budget-app'] },
    { id: 'q_2', question: 'What scope should this job deliver?', options: ['All 10 issues', 'Listed files only'] },
  ],
  answers: { q_1: 'Clone into repos/budget-app' },
};

/**
 * What `SessionsController.SubmitAnswer` wrote before November 2026: the answer array the operator
 * submitted, and no question set — so a job whose questions were asked and answered in the session chat
 * rendered an empty Q&A card on the timeline while the chat showed both questions.
 */
const legacyAnswerArrayPayload = {
  content: 'User answered question:\nQ: How should the workspace resolve? -> A: Clone into repos/budget-app',
  toolName: 'ask_question',
  turnSessionId: '39f40b9e-5bee-42f8-b75e-a077992a02e7',
  answers: [
    { questionIndex: 0, question: 'How should the workspace resolve?', selectedOptions: ['Clone into repos/budget-app'], customText: '' },
    { questionIndex: 1, question: 'What scope should this job deliver?', selectedOptions: ['All 10 issues'], customText: '' },
  ],
};

describe('DiscoveryQaCard', () => {
  it('renders the questions and the answers keyed by question id', () => {
    render(<DiscoveryQaCard card={buildCard(activityPayload, 'Approved')} />);

    expect(screen.getByText('How should the repository workspace be resolved?')).toBeInTheDocument();
    expect(screen.getByText('What scope should this job deliver?')).toBeInTheDocument();
    expect(screen.getByText('Two answers change what Stage 3 clones.')).toBeInTheDocument();
    expect(screen.getByText('Clone into repos/budget-app', { selector: '.text-indigo-300' })).toBeInTheDocument();
  });

  it('renders questions asked in the session transcript, not only ones the activity recorded', () => {
    render(<DiscoveryQaCard card={buildCard(legacyAnswerArrayPayload, 'Approved')} />);

    expect(screen.getByText('How should the workspace resolve?')).toBeInTheDocument();
    expect(screen.getByText('What scope should this job deliver?')).toBeInTheDocument();
    expect(screen.getAllByText('Clone into repos/budget-app').length).toBeGreaterThan(0);
    expect(screen.getByText(/Answers submitted/)).toBeInTheDocument();
  });

  it('still offers the pending form when the activity asked and nobody has answered yet', () => {
    const onsubmit = vi.fn();
    render(
      <DiscoveryQaCard
        card={buildCard({ questions: activityPayload.questions, summary: 'need input' }, 'PendingInput')}
        onSubmitAnswers={onsubmit}
      />
    );

    expect(screen.getByRole('button', { name: /submit answers/i })).toBeInTheDocument();
    expect(screen.getAllByPlaceholderText(/enter custom instruction/i)).toHaveLength(2);
  });
});
