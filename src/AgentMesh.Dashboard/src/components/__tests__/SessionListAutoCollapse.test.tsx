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

const props = (sessions: AgentSession[], serverTotal: number | null = sessions.length) => ({
  ownerKind: 'persona' as const,
  ownerId: 'rook',
  sessions,
  serverTotal,
  selectedSessionId: null,
  isCreatingSession: false,
  deletingSessionId: null,
  onSelectSession: noop,
  onRequestDelete: noop,
  onCancelDelete: noop,
  onConfirmDelete: noop
});

const expand = () => fireEvent.click(screen.getByTestId('show-all-persona-sessions-rook'));

/**
 * The stale-response guard is supposed to cover every way a card can leave the expanded state.
 * Manual collapse bumps `requestIdRef` (SessionListSection.tsx:129) but auto-collapse does not, so a
 * page still in flight when the owner's row set changes survives invalidation and writes its totals
 * back onto a card that is no longer expanded.
 */
describe('SessionListSection auto-collapse request invalidation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getPersonaSessionsPage).mockResolvedValue({ items: rows(8), total: 8 });
    vi.mocked(api.getAgentSessionsPage).mockResolvedValue({ items: rows(8), total: 8 });
  });

  it('does not let a stale page total inflate the collapsed "Show all" count', async () => {
    let resolvePage: (value: { items: AgentSession[]; total: number }) => void = () => {};
    vi.mocked(api.getPersonaSessionsPage).mockReturnValue(
      new Promise((resolve) => {
        resolvePage = resolve;
      })
    );

    // The persona really does have 40 sessions; the client window holds all 40.
    const view = render(<SessionListSection {...props(rows(40), 40)} />);
    expand();

    // Operator mass-deletes down to 6 while that page request is still in flight. The row-set
    // identity changed, so the card auto-collapses and drops its cached page.
    view.rerender(<SessionListSection {...props(rows(6), 6)} />);

    const toggleBtn = screen.getByTestId('show-all-persona-sessions-rook');
    expect(toggleBtn).toHaveTextContent('Show all (1)');

    // Now the page for the abandoned expansion lands. It reports the pre-delete total of 40.
    resolvePage({ items: rows(40), total: 40 });
    await waitFor(() => expect(api.getPersonaSessionsPage).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 0));

    // The card is collapsed and the server says 6 sessions exist. The stale 40 must not be adopted,
    // or the label promises 35 hidden rows that do not exist.
    expect(screen.getByTestId('show-all-persona-sessions-rook')).toHaveTextContent('Show all (1)');
    expect(screen.queryByText('Row 7')).not.toBeInTheDocument();
  });

  it('still adopts a fresh page when the row set did not change', async () => {
    vi.mocked(api.getPersonaSessionsPage).mockResolvedValue({ items: rows(40), total: 40 });

    render(<SessionListSection {...props(rows(40), 40)} />);
    expand();

    await waitFor(() =>
      expect(screen.getByTestId('show-fewer-persona-sessions-rook')).toBeInTheDocument()
    );
    expect(screen.getByText('Row 40')).toBeInTheDocument();
  });
});
