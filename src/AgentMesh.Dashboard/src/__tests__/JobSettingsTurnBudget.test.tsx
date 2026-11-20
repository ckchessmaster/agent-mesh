// @vitest-environment jsdom
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { JobSettingsModal } from '../components/JobSettingsModal';
import { ProjectJobStatus, type Project, type ProjectJob } from '../types';

const project = {
  id: 'agent-mesh',
  name: 'Agent Mesh',
  assignedPersonas: [],
  defaultLeadPersonaId: 'astra'
} as unknown as Project;

const baseJob = (over: Partial<ProjectJob> = {}): ProjectJob =>
  ({
    id: 'ed40ae7c-c816-4d71-850d-44e009ce0f77',
    projectId: 'agent-mesh',
    title: 'job attachments',
    description: '',
    status: ProjectJobStatus.Active,
    gitBranchName: 'mesh/job-ed40ae7c',
    tasks: [],
    lifecycleStatus: 'Blocked',
    progressHeartbeat: 'Halted: workflow halted after 40 turns',
    createdAt: '2026-11-19T19:30:20Z',
    updatedAt: '2026-11-20T02:20:45Z',
    ...over
  }) as unknown as ProjectJob;

const renderModal = (job: ProjectJob, onSave = vi.fn().mockResolvedValue(undefined)) => {
  render(<JobSettingsModal isOpen onClose={vi.fn()} project={project} job={job} onSave={onSave} />);
  return onSave;
};

const submit = async () => {
  fireEvent.click(screen.getByTestId('job-settings-save-btn'));
};

// A click on Save runs the browser's native number-input validation first (min/max/step), which
// jsdom enforces too: out-of-range values never reach onSubmit. The component's own guard is the
// backstop for callers that do reach submit (Enter key paths, future markup without min/max), so
// these tests drive the form's submit event directly to exercise the JS layer being tested.
const submitForm = () => {
  const form = screen.getByTestId('job-settings-turn-budget-input').closest('form');
  expect(form).not.toBeNull();
  fireEvent.submit(form!);
};

describe('JobSettingsModal turn budget', () => {
  it("pre-fills the job's current budget so the field shows truth, not a placeholder", () => {
    renderModal(baseJob({ turnBudget: 60 }));
    expect(screen.getByTestId('job-settings-turn-budget-input')).toHaveValue(60);
  });

  it('sends an edited budget on save — the operator\'s lever before Recover after a 40-turn halt', async () => {
    const onSave = renderModal(baseJob());

    fireEvent.change(screen.getByTestId('job-settings-turn-budget-input'), { target: { value: '120' } });
    await submit();

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0][0]).toMatchObject({ turnBudget: 120 });
  });

  it('omits the field when blank, so leaving it empty keeps the current value instead of resetting to default', async () => {
    const onSave = renderModal(baseJob({ turnBudget: 80 }));

    // The API's whole handler is "null = unchanged"; sending an explicit reset from a blank text box
    // would silently undo the raised budget the operator set precisely because the default was too low.
    fireEvent.change(screen.getByTestId('job-settings-turn-budget-input'), { target: { value: '' } });
    await submit();

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0][0].turnBudget).toBeUndefined();
  });

  it.each(['0', '-4', '1001', '3.5'])('refuses %s with the server\'s own range in the message', async (bad) => {
    const onSave = renderModal(baseJob());

    fireEvent.change(screen.getByTestId('job-settings-turn-budget-input'), { target: { value: bad } });
    submitForm();

    await waitFor(() =>
      expect(screen.getByText(/between 1 and 1000/i)).toBeInTheDocument()
    );
    expect(onSave).not.toHaveBeenCalled();
  });
});
