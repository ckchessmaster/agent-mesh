import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { SessionListSection } from '../SessionListSection';
import { api } from '../../services/api';
import type { AgentSession } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getPersonaSessionsPage: vi.fn(),
    getAgentSessionsPage: vi.fn()
  }
}));

const make = (id: string, minuteOffset: number, title = `Row ${id}`): AgentSession => ({
  id,
  title,
  status: 'active',
  personaId: 'rook',
  driverId: 'agy',
  metadataJson: '{}',
  createdAt: new Date(Date.UTC(2026, 9, 27, 12, 0, 0) - minuteOffset * 60_000).toISOString(),
  updatedAt: new Date(Date.UTC(2026, 9, 27, 12, 0, 0) - minuteOffset * 60_000).toISOString()
});

const rows = (count: number) =>
  Array.from({ length: count }, (_, i) => make(`s${i + 1}`, i, `Row ${i + 1}`));

const noop = () => {};

/**
 * Re-render helper. These tests are about state surviving a prop change, so they must rerender the
 * same mounted instance rather than mount a fresh one (which would reset the expansion anyway).
 */
const mount = (sessions: AgentSession[], serverTotal: number | null = sessions.length) =>
  render(
    <SessionListSection
      ownerKind="persona"
      ownerId="rook"
      sessions={sessions}
      serverTotal={serverTotal}
      selectedSessionId={null}
      isCreatingSession={false}
      deletingSessionId={null}
      onSelectSession={noop}
      onRequestDelete={noop}
      onCancelDelete={noop}
      onConfirmDelete={noop}
    />
  );

const expand = () => fireEvent.click(screen.getByTestId('show-all-persona-sessions-rook'));
const collapse = () => fireEvent.click(screen.getByTestId('show-fewer-persona-sessions-rook'));

describe('SessionListSection expansion lifecycle', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getPersonaSessionsPage).mockResolvedValue({ items: rows(8), total: 8 });
    vi.mocked(api.getAgentSessionsPage).mockResolvedValue({ items: rows(8), total: 8 });
  });

  it('renders a bounded preview and never the whole list', () => {
    mount(rows(40));

    expect(screen.getByText('Row 1')).toBeInTheDocument();
    expect(screen.getByText('Row 5')).toBeInTheDocument();
    expect(screen.queryByText('Row 6')).not.toBeInTheDocument();
    expect(screen.queryByText('Row 40')).not.toBeInTheDocument();
    expect(screen.getByTestId('show-all-persona-sessions-rook')).toHaveTextContent('Show all (35)');
  });

  it('auto-collapses when a new session lands in the owner list', async () => {
    const view = mount(rows(8));

    expand();
    await waitFor(() => expect(screen.getByTestId('show-fewer-persona-sessions-rook')).toBeInTheDocument());
    expect(screen.getByText('Row 8')).toBeInTheDocument();

    // A fresh turn arrives. The cached page is now provably incomplete, so the card must snap back
    // to the bounded preview instead of holding a stale expansion.
    view.rerender(
      <SessionListSection
        ownerKind="persona"
        ownerId="rook"
        sessions={[make('s9', -1, 'Row 9'), ...rows(8)]}
        serverTotal={9}
        selectedSessionId={null}
        isCreatingSession={false}
        deletingSessionId={null}
        onSelectSession={noop}
        onRequestDelete={noop}
        onCancelDelete={noop}
        onConfirmDelete={noop}
      />
    );

    await waitFor(() => expect(screen.getByTestId('show-all-persona-sessions-rook')).toBeInTheDocument());
    expect(screen.getByText('Row 9')).toBeInTheDocument();
    expect(screen.queryByText('Row 8')).not.toBeInTheDocument();
  });

  it('keeps the expansion open when a row only changes status or timestamp', async () => {
    const view = mount(rows(8));

    expand();
    await waitFor(() => expect(screen.getByTestId('show-fewer-persona-sessions-rook')).toBeInTheDocument());

    // Same ids, but the newest row just ticked updatedAt / flipped to thinking. Collapsing here would
    // yank the list out from under an operator watching a live job.
    const churned = rows(8).map((s, i) =>
      i === 0 ? { ...s, status: 'thinking', updatedAt: new Date(Date.UTC(2026, 9, 28)).toISOString() } : s
    );

    view.rerender(
      <SessionListSection
        ownerKind="persona"
        ownerId="rook"
        sessions={churned}
        serverTotal={8}
        selectedSessionId={null}
        isCreatingSession={false}
        deletingSessionId={null}
        onSelectSession={noop}
        onRequestDelete={noop}
        onCancelDelete={noop}
        onConfirmDelete={noop}
      />
    );

    expect(screen.getByTestId('show-fewer-persona-sessions-rook')).toBeInTheDocument();
    expect(screen.getByText('Row 8')).toBeInTheDocument();
  });

  it('ignores a slow expansion response that lands after the operator collapsed the card', async () => {
    let resolvePage: (value: { items: AgentSession[]; total: number }) => void = () => {};
    vi.mocked(api.getPersonaSessionsPage).mockReturnValue(
      new Promise((resolve) => {
        resolvePage = resolve;
      })
    );

    mount(rows(8));
    expand();

    // Collapse while the request is still in flight, then let it land.
    collapse();
    resolvePage({ items: rows(8), total: 8 });

    await waitFor(() => expect(screen.getByTestId('show-all-persona-sessions-rook')).toBeInTheDocument());
    expect(screen.queryByText('Row 8')).not.toBeInTheDocument();
  });

  it('drops a deleted row from an open expansion instead of trusting the cached page', async () => {
    const view = mount(rows(8));

    expand();
    await waitFor(() => expect(screen.getByTestId('show-fewer-persona-sessions-rook')).toBeInTheDocument());

    view.rerender(
      <SessionListSection
        ownerKind="persona"
        ownerId="rook"
        sessions={rows(8).filter((s) => s.id !== 's1')}
        serverTotal={7}
        selectedSessionId={null}
        isCreatingSession={false}
        deletingSessionId={null}
        onSelectSession={noop}
        onRequestDelete={noop}
        onCancelDelete={noop}
        onConfirmDelete={noop}
      />
    );

    await waitFor(() => expect(screen.getByTestId('show-all-persona-sessions-rook')).toBeInTheDocument());
    expect(screen.queryByText('Row 1')).not.toBeInTheDocument();
  });

  it('hides the toggle when neither the server nor the client reports more rows than the preview', () => {
    mount(rows(3), null);

    expect(screen.getByText('Row 1')).toBeInTheDocument();
    expect(screen.queryByTestId('show-all-persona-sessions-rook')).not.toBeInTheDocument();
  });

  it('offers expansion when the server holds more rows than the capped client window loaded', () => {
    // Only 3 rows made it into the client's window, but the persona has 40.
    mount(rows(3), 40);

    expect(screen.getByTestId('show-all-persona-sessions-rook')).toHaveTextContent('Show all (37)');
  });

  it('routes expansion through the driver endpoint for driver cards', async () => {
    render(
      <SessionListSection
        ownerKind="driver"
        ownerId="agy"
        sessions={rows(8)}
        serverTotal={8}
        selectedSessionId={null}
        isCreatingSession={false}
        deletingSessionId={null}
        onSelectSession={noop}
        onRequestDelete={noop}
        onCancelDelete={noop}
        onConfirmDelete={noop}
        showPersonaBadge
      />
    );

    // Driver cards attribute a row to the persona that owned it.
    expect(screen.getAllByText('@rook').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByTestId('show-all-driver-sessions-agy'));

    await waitFor(() => expect(api.getAgentSessionsPage).toHaveBeenCalledWith('agy'));
    expect(api.getPersonaSessionsPage).not.toHaveBeenCalled();
  });

  it('calls back with the clicked session and the delete target', () => {
    const onSelectSession = vi.fn();
    const onRequestDelete = vi.fn();

    render(
      <SessionListSection
        ownerKind="persona"
        ownerId="rook"
        sessions={rows(2)}
        serverTotal={2}
        selectedSessionId={null}
        isCreatingSession={false}
        deletingSessionId={null}
        onSelectSession={onSelectSession}
        onRequestDelete={onRequestDelete}
        onCancelDelete={noop}
        onConfirmDelete={noop}
      />
    );

    fireEvent.click(screen.getByTestId('session-row-s1'));
    expect(onSelectSession).toHaveBeenCalledWith(expect.objectContaining({ id: 's1' }));

    fireEvent.click(screen.getByTitle('Delete session Row 1'));
    expect(onRequestDelete).toHaveBeenCalledWith('s1');
  });
});
