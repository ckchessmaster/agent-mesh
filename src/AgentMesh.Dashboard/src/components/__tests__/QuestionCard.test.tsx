import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { QuestionCard, getOptionLabel, getOptionDescription } from '../QuestionCard';
import type { AskQuestionItem } from '../../types';

describe('QuestionCard Component', () => {
  it('correctly extracts labels and descriptions from string and object options', () => {
    expect(getOptionLabel('Simple string')).toBe('Simple string');
    expect(getOptionDescription('Simple string')).toBeUndefined();

    expect(getOptionLabel({ label: 'Complex option', description: 'Some details' })).toBe('Complex option');
    expect(getOptionDescription({ label: 'Complex option', description: 'Some details' })).toBe('Some details');

    expect(getOptionLabel({ text: 'Text option' })).toBe('Text option');
    expect(getOptionLabel({ title: 'Title option' })).toBe('Title option');
    expect(getOptionLabel({ value: 'Value option' })).toBe('Value option');
  });

  it('renders questions with object options { label, description } without crashing', async () => {
    const onSubmit = vi.fn();
    const questions: AskQuestionItem[] = [
      {
        header: 'Workspace persistence',
        question: 'Where does a job\'s work actually live?',
        options: [
          {
            label: '[RECOMMENDED] Per-job workspace',
            description: 'Key the dir by jobId so all turns on a job share one checkout.'
          },
          {
            label: 'Git as the handoff bus',
            description: 'Outgoing agent commits and pushes.'
          }
        ],
        is_multi_select: false
      }
    ];

    render(
      <QuestionCard
        cardKey="test-card-1"
        targetId="q-1"
        parsedQuestions={questions}
        onSubmit={onSubmit}
      />
    );

    expect(screen.getByText('Workspace persistence')).toBeInTheDocument();
    expect(screen.getByText('Where does a job\'s work actually live?')).toBeInTheDocument();
    expect(screen.getByText('[RECOMMENDED] Per-job workspace')).toBeInTheDocument();
    expect(screen.getByText('Key the dir by jobId so all turns on a job share one checkout.')).toBeInTheDocument();
    expect(screen.getByText('Git as the handoff bus')).toBeInTheDocument();

    // Select the recommended option
    fireEvent.click(screen.getByText('[RECOMMENDED] Per-job workspace'));

    // Submit answer
    const submitBtn = screen.getByText('Submit Answer');
    fireEvent.click(submitBtn);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith([
        expect.objectContaining({
          questionIndex: 0,
          question: 'Where does a job\'s work actually live?',
          selectedOptions: ['[RECOMMENDED] Per-job workspace']
        })
      ]);
    });
  });

  it('renders answered state with header badge and answer content', () => {
    const onSubmit = vi.fn();
    const questions: AskQuestionItem[] = [
      {
        header: 'Persistence',
        question: 'Where does a job\'s work actually live?',
        options: ['Per-job workspace', 'Git handoff bus']
      }
    ];

    render(
      <QuestionCard
        cardKey="test-card-2"
        targetId="q-2"
        parsedQuestions={questions}
        isAnswered={true}
        answeredContent="Q: Where does a job's work actually live? -> A: Per-job workspace"
        onSubmit={onSubmit}
      />
    );

    expect(screen.getByText('Persistence')).toBeInTheDocument();
    expect(screen.getByText('Agent Question — Answered')).toBeInTheDocument();
    expect(screen.getByText(/Q: Where does a job's work actually live\? -> A: Per-job workspace/)).toBeInTheDocument();
  });
});
