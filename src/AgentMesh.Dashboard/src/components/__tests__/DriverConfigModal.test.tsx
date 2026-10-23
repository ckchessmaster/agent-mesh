import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { DriverConfigModal, DRIVER_LIMIT_DEFAULTS } from '../DriverConfigModal';
import type { Agent } from '../../types';

const openAiAgent: Agent = {
  id: 'spence-ai',
  name: 'Spence Private AI',
  role: 'Autonomous Agent',
  status: 'online',
  runtimeType: 3,
  trustTier: 2,
  createdAt: new Date().toISOString(),
  capabilitiesJson: JSON.stringify({
    adapter: 'openai',
    endpoint: 'http://127.0.0.1:11435/v1',
    apiKey: 'super-secret-should-never-render',
    defaultModel: 'qwen3',
    maxSteps: 100,
    timeoutSeconds: 300,
    maxRetries: 4
  }),
  config: {
    adapter: 'openai',
    endpoint: 'http://127.0.0.1:11435/v1',
    defaultModel: 'qwen3',
    maxSteps: 100,
    timeoutSeconds: 300,
    maxRetries: 4,
    retryDelayMilliseconds: null,
    capabilities: ['run_command', 'ask_question'],
    hasApiKey: true
  }
};

const cliAgent: Agent = {
  id: 'claude',
  name: 'Claude Code CLI',
  runtimeType: 0,
  trustTier: 2,
  createdAt: new Date().toISOString(),
  capabilitiesJson: JSON.stringify({ adapter: 'claude' }),
  config: { adapter: 'claude', capabilities: ['bash', 'edit_file'], hasApiKey: false }
};

const renderModal = (props: Partial<React.ComponentProps<typeof DriverConfigModal>> = {}) => {
  const onSave = vi.fn().mockResolvedValue(undefined);
  const onClose = vi.fn();
  const utils = render(
    <DriverConfigModal
      agent={openAiAgent}
      initialConfig={openAiAgent.config}
      onClose={onClose}
      onSave={onSave}
      {...props}
    />
  );
  return { onSave, onClose, ...utils };
};

describe('DriverConfigModal', () => {
  it('shows stored limits for an OpenAI driver', () => {
    renderModal();

    expect(screen.getByLabelText(/Max steps per turn/i)).toHaveValue(100);
    expect(screen.getByLabelText(/Request timeout/i)).toHaveValue(300);
    expect(screen.getByLabelText(/Max retries/i)).toHaveValue(4);
  });

  it('never renders the stored API key', () => {
    const { container } = renderModal();

    expect(container.textContent).not.toContain('super-secret-should-never-render');
    const keyInput = screen.getByLabelText(/^API Key$/i) as HTMLInputElement;
    expect(keyInput.value).toBe('');
    expect(keyInput.type).toBe('password');
    // Presence is communicated, the secret is not.
    expect(keyInput.placeholder).toContain('stored');
  });

  it('sends only the field the operator changed', async () => {
    const { onSave } = renderModal();

    fireEvent.change(screen.getByLabelText(/Max steps per turn/i), { target: { value: '250' } });
    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave).toHaveBeenCalledWith({ maxSteps: 250 });
  });

  it('sends nothing when an untouched form is submitted', async () => {
    const { onSave, onClose } = renderModal();

    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    // The whole point of the round-trip fix: no spurious writes back over the blob.
    expect(onSave).not.toHaveBeenCalled();
  });

  it('does not re-send a default that was merely displayed, not stored', async () => {
    // Driver has maxSteps unset; the form shows the 100 default. Saving must not pin it.
    const { onSave, onClose } = renderModal({
      initialConfig: { adapter: 'openai', endpoint: 'http://h/v1', hasApiKey: false }
    });

    expect(screen.getByLabelText(/Max steps per turn/i)).toHaveValue(DRIVER_LIMIT_DEFAULTS.maxSteps);

    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(onSave).not.toHaveBeenCalled();
  });

  it('leaves retry delay blank by default and does not invent a fixed value', async () => {
    const { onSave } = renderModal();

    const delay = screen.getByLabelText(/Retry delay/i) as HTMLInputElement;
    // jsdom reports null for an empty number input; assert on the raw value.
    expect(delay.value).toBe('');

    fireEvent.change(screen.getByLabelText(/Max steps per turn/i), { target: { value: '120' } });
    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0][0]).not.toHaveProperty('retryDelayMilliseconds');
  });

  it('rejects a non-integer step count without calling onSave', async () => {
    const { onSave, onClose } = renderModal();

    fireEvent.change(screen.getByLabelText(/Max steps per turn/i), { target: { value: '12.5' } });
    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    expect(onSave).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByText(/Max steps must be a whole number/i)).toBeInTheDocument();
  });

  it('rejects a zero max-steps value', async () => {
    const { onSave } = renderModal();

    fireEvent.change(screen.getByLabelText(/Max steps per turn/i), { target: { value: '0' } });
    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);

    await waitFor(() => expect(screen.getByText(/whole number/i)).toBeInTheDocument());
    expect(onSave).not.toHaveBeenCalled();
  });

  it('surfaces a save failure instead of closing silently', async () => {
    const onSave = vi.fn().mockRejectedValue(new Error('boom: 500 from mesh'));
    const onClose = vi.fn();
    render(<DriverConfigModal agent={openAiAgent} initialConfig={openAiAgent.config} onClose={onClose} onSave={onSave} />);

    fireEvent.change(screen.getByLabelText(/Max steps per turn/i), { target: { value: '300' } });
    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);

    await waitFor(() => expect(screen.getByText(/boom: 500 from mesh/i)).toBeInTheDocument());
    expect(onClose).not.toHaveBeenCalled();
  });

  it('hides step limits for CLI-backed drivers, since those adapters have no step loop', () => {
    render(
      <DriverConfigModal
        agent={cliAgent}
        initialConfig={cliAgent.config}
        onClose={vi.fn()}
        onSave={vi.fn()}
      />
    );

    expect(screen.queryByLabelText(/Max steps per turn/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Request timeout/i)).not.toBeInTheDocument();
    expect(screen.getByText(/controlled by that tool/i)).toBeInTheDocument();
  });

  it('does not send limit fields for a CLI driver even if endpoint is edited', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(
      <DriverConfigModal
        agent={cliAgent}
        initialConfig={cliAgent.config}
        onClose={vi.fn()}
        onSave={onSave}
      />
    );

    fireEvent.submit(screen.getByRole('button', { name: /Save/i }).closest('form')!);
    await waitFor(() => expect(onSave).not.toHaveBeenCalled());
  });

  it('shows stored capabilities read-only', () => {
    renderModal();

    expect(screen.getByText('run_command')).toBeInTheDocument();
    expect(screen.getByText('ask_question')).toBeInTheDocument();
  });

  it('closes on Escape', () => {
    const { onClose } = renderModal();

    fireEvent.keyDown(window, { key: 'Escape' });

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('falls back to parsing capabilitiesJson when no parsed config is supplied', () => {
    render(
      <DriverConfigModal
        agent={openAiAgent}
        onClose={vi.fn()}
        onSave={vi.fn()}
      />
    );

    // adapter came from capabilitiesJson, so limits must still be offered.
    expect(screen.getByLabelText(/Max steps per turn/i)).toBeInTheDocument();
  });
});
