import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { PlanApprovalCard } from '../cards/PlanApprovalCard';
import type { JobTimelineCard, TimelineCardStatus } from '../../../types/timeline';

const planPayload = {
  reasoning: 'Three sequential tasks; no schema change required.',
  tasks: [
    { taskId: 't1', title: 'Gate wakes on guidance', targetFile: 'src/Core/JobExecutionWorkflow.cs', verificationMethod: 'dotnet test' },
    { taskId: 't2', title: 'Endpoint returns 409 when nobody is parked', targetFile: 'src/Api/JobTimelineController.cs', verificationMethod: 'dotnet test' },
  ],
};

function buildCard(status: TimelineCardStatus): JobTimelineCard {
  return {
    id: 'card-plan-1',
    jobId: 'job-83d49334',
    cardType: 'PlanApproval',
    status,
    title: 'Plan Approval: Proposed Implementation Checklist',
    structuredPayloadJson: JSON.stringify(planPayload),
    createdAt: '2026-11-16T10:00:00Z',
    updatedAt: '2026-11-16T10:00:00Z',
    comments: [],
  };
}

const feedbackBox = () => screen.getByPlaceholderText(/integration tests for ProcessGroup isolation/);

describe('PlanApprovalCard', () => {
  it('renders the proposed checklist and all three gate exits', () => {
    render(<PlanApprovalCard card={buildCard('PendingInput')} onSubmitPlanDecision={vi.fn()} onSendGuidance={vi.fn()} />);

    expect(screen.getByText('Gate wakes on guidance')).toBeInTheDocument();
    expect(screen.getByTestId('plan-verdict-badge')).toHaveTextContent('Pending Approval');
    expect(screen.getByRole('button', { name: /approve plan/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /reject with feedback/i })).toBeInTheDocument();
    expect(screen.getByTestId('plan-send-guidance')).toBeInTheDocument();
  });

  it('sends guidance without deciding, and leaves the gate open to be decided', async () => {
    const onSubmitPlanDecision = vi.fn();
    const onSendGuidance = vi.fn().mockResolvedValue(undefined);
    render(
      <PlanApprovalCard
        card={buildCard('PendingInput')}
        onSubmitPlanDecision={onSubmitPlanDecision}
        onSendGuidance={onSendGuidance}
      />
    );

    expect(screen.getByTestId('plan-send-guidance')).toBeDisabled();

    fireEvent.change(feedbackBox(), { target: { value: '  The billing module is frozen this sprint.  ' } });
    expect(screen.getByTestId('plan-send-guidance')).toBeEnabled();
    fireEvent.click(screen.getByTestId('plan-send-guidance'));

    await waitFor(() =>
      expect(onSendGuidance).toHaveBeenCalledWith('card-plan-1', 'The billing module is frozen this sprint.')
    );
    expect(onSubmitPlanDecision).not.toHaveBeenCalled();
    expect(await screen.findByTestId('plan-guidance-sent')).toHaveTextContent('stays open');
    expect(feedbackBox()).toHaveValue('');
    expect(screen.getByTestId('plan-verdict-badge')).toHaveTextContent('Pending Approval');
    expect(screen.getByRole('button', { name: /approve plan/i })).toBeEnabled();
  });

  it('shows why a refused guidance left the gate open instead of going quiet', async () => {
    const onSendGuidance = vi.fn().mockRejectedValue(new Error('no running workflow is holding this job plan gate'));
    render(<PlanApprovalCard card={buildCard('PendingInput')} onSendGuidance={onSendGuidance} />);

    fireEvent.change(feedbackBox(), { target: { value: 'Drop task 2, it is already merged upstream.' } });
    fireEvent.click(screen.getByTestId('plan-send-guidance'));

    const error = await screen.findByTestId('plan-card-error');
    expect(error).toHaveTextContent('no running workflow is holding this job plan gate');
    expect(screen.queryByTestId('plan-guidance-sent')).not.toBeInTheDocument();
    // The typed message survives the failure so the operator is not asked to retype it.
    expect(feedbackBox()).toHaveValue('Drop task 2, it is already merged upstream.');
    expect(screen.getByTestId('plan-verdict-badge')).toHaveTextContent('Pending Approval');
  });

  it('shows why a refused decision left the gate open instead of looking like a dead button', async () => {
    const onSubmitPlanDecision = vi.fn().mockRejectedValue(new Error('This plan gate is already Resolved'));
    render(<PlanApprovalCard card={buildCard('PendingInput')} onSubmitPlanDecision={onSubmitPlanDecision} />);

    fireEvent.click(screen.getByRole('button', { name: /approve plan/i }));

    expect(await screen.findByTestId('plan-card-error')).toHaveTextContent('already Resolved');
    expect(screen.getByTestId('plan-verdict-badge')).toHaveTextContent('Pending Approval');
    expect(screen.getByRole('button', { name: /approve plan/i })).toBeEnabled();
  });

  it('records a decision exactly once and retires the action row', async () => {
    const onSubmitPlanDecision = vi.fn().mockResolvedValue(undefined);
    const onSendGuidance = vi.fn();
    render(
      <PlanApprovalCard
        card={buildCard('PendingInput')}
        onSubmitPlanDecision={onSubmitPlanDecision}
        onSendGuidance={onSendGuidance}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /approve plan/i }));
    await waitFor(() => expect(onSubmitPlanDecision).toHaveBeenCalledTimes(1));
    expect(onSubmitPlanDecision).toHaveBeenCalledWith('card-plan-1', true, undefined, 'operator');

    expect(screen.queryByRole('button', { name: /approve plan/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('plan-send-guidance')).not.toBeInTheDocument();
    expect(screen.getByTestId('plan-outcome-banner')).toHaveTextContent('Plan approved');
  });

  it('offers no decision on a superseded card, whose live gate is a newer one', () => {
    render(<PlanApprovalCard card={buildCard('Resolved')} onSubmitPlanDecision={vi.fn()} onSendGuidance={vi.fn()} />);

    expect(screen.getByTestId('plan-verdict-badge')).toHaveTextContent('Superseded');
    expect(screen.queryByRole('button', { name: /approve plan/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('plan-send-guidance')).not.toBeInTheDocument();
    expect(screen.getByTestId('plan-outcome-banner')).toHaveTextContent('Superseded by guidance');
  });

  it('degrades to the two original exits when no guidance handler is wired', () => {
    render(<PlanApprovalCard card={buildCard('PendingInput')} onSubmitPlanDecision={vi.fn()} />);

    expect(screen.queryByTestId('plan-send-guidance')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /reject with feedback/i })).toBeEnabled();
  });

  /**
   * The gate as it stands when the persona reached it with nothing registered. Before November 2026 the
   * worker filled this in with four hardcoded AgentMesh tasks, so a budget-app job asked for approval of
   * a plan for this repository and an operator could not tell the difference.
   */
  function buildEmptyCard(): JobTimelineCard {
    return {
      ...buildCard('PendingInput'),
      title: 'Plan Approval: No Checklist Registered',
      structuredPayloadJson: JSON.stringify({
        reasoning:
          'No checklist was registered, so there is nothing to approve. Nothing was invented to fill the gap.',
        tasks: [],
      }),
    };
  }

  it('says there is nothing to approve instead of showing an empty list and silence', () => {
    render(<PlanApprovalCard card={buildEmptyCard()} onSubmitPlanDecision={vi.fn()} onSendGuidance={vi.fn()} />);

    expect(screen.getByText('Proposed Checklist (0 Steps)')).toBeInTheDocument();
    const warning = screen.getByTestId('plan-card-empty-checklist');
    expect(warning).toHaveTextContent('no checklist');
    expect(warning).toHaveTextContent('agentmesh task set-checklist');
    expect(warning).toHaveTextContent('halts the job');
  });

  it('still lets the operator reject an empty plan, which is the useful decision', () => {
    const onSubmitPlanDecision = vi.fn();
    render(
      <PlanApprovalCard
        card={buildEmptyCard()}
        onSubmitPlanDecision={onSubmitPlanDecision}
        onSendGuidance={vi.fn()}
      />
    );

    fireEvent.change(feedbackBox(), { target: { value: 'Register the checklist with agentmesh task set-checklist.' } });
    fireEvent.click(screen.getByRole('button', { name: /reject with feedback/i }));

    expect(onSubmitPlanDecision).toHaveBeenCalledWith(
      'card-plan-1',
      false,
      'Register the checklist with agentmesh task set-checklist.',
      'operator'
    );
  });

  it('says nothing about an empty checklist when tasks exist', () => {
    render(<PlanApprovalCard card={buildCard('PendingInput')} onSubmitPlanDecision={vi.fn()} onSendGuidance={vi.fn()} />);

    expect(screen.queryByTestId('plan-card-empty-checklist')).not.toBeInTheDocument();
  });
});
