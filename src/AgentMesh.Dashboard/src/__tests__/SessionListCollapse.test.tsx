import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent, within } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type { Agent, AgentSession, Persona, SystemStatusResponse } from '../types';

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getStatus: vi.fn(),
    getCapabilities: vi.fn().mockResolvedValue({ agentRuntimes: {} }),
    getAgents: vi.fn().mockResolvedValue([]),
    getPersonas: vi.fn().mockResolvedValue([]),
    getProjects: vi.fn().mockResolvedValue([]),
    getSessions: vi.fn().mockResolvedValue([]),
    getPersonaSessionsPage: vi.fn(),
    getAgentSessionsPage: vi.fn(),
    getSessionMessages: vi.fn().mockResolvedValue([]),
    getAgentUsage: vi.fn().mockResolvedValue(null),
    createPersona: vi.fn(),
    updatePersona: vi.fn(),
    deletePersona: vi.fn(),
    createPersonaSession: vi.fn(),
    deleteSession: vi.fn(),
    uploadSessionAttachments: vi.fn(),
    resetMesh: vi.fn(),
    createAgentSession: vi.fn(),
    updateSessionControls: vi.fn(),
    postSessionMessage: vi.fn(),
    upsertAgent: vi.fn(),
    updateDriverConfig: vi.fn(),
    stopSession: vi.fn(),
    stopCommand: vi.fn(),
    enqueuePrompt: vi.fn(),
    dequeuePrompt: vi.fn(),
    sendQueuedPromptNow: vi.fn(),
    submitQuestionAnswer: vi.fn(),
    getSessionArtifacts: vi.fn().mockResolvedValue([]),
    getSessionArtifact: vi.fn(),
    syncSessionArtifact: vi.fn()
  }
}));

// App mounts a live SignalR hub on every render; without this stub each of the eight mounts below
// starts real negotiation with retry timers, which starves the shared test pool and makes unrelated
// timing-sensitive specs flake. ProjectsAndJobs.test.tsx uses the same stub.
//
// The connection object MUST be a module-level constant. App.tsx puts `connection` in the dependency
// array of the session effect (App.tsx:1106), which calls setEvents([]) with a fresh array. If the
// stub returned a new object per render, that effect would re-run on every render, and each run would
// queue another state update: an infinite render loop that exhausts the V8 heap and aborts the worker
// before a single assertion reports.
const mockSignalRConnection = {
  on: vi.fn(),
  off: vi.fn(),
  invoke: vi.fn().mockResolvedValue(undefined)
};

vi.mock('../hooks/useSignalR', () => ({
  useSignalR: () => ({
    connection: mockSignalRConnection,
    connected: true
  })
}));

vi.mock('../auth/AuthProvider', () => ({
  useAuth: vi.fn(() => ({
    isAuthenticated: true,
    isLoading: false,
    user: { profile: { email: 'admin', name: 'admin' }, access_token: 'mock-jwt-token' },
    signoutRedirect: vi.fn()
  })),
  AppAuthProvider: ({ children }: { children: React.ReactNode }) => children
}));

const mockStatus: SystemStatusResponse = {
  status: 'Healthy',
  timestamp: new Date().toISOString(),
  environment: 'Development',
  services: { api: { status: 'Healthy', latencyMs: 2, description: 'API' } }
};

const driver: Agent = {
  id: 'agy',
  name: 'Antigravity CLI',
  role: 'Autonomous Codebase Driver',
  status: 'online',
  runtimeType: 'CliAgy',
  trustTier: 3,
  createdAt: new Date().toISOString()
};

const makePersona = (sessionCount: number): Persona => ({
  id: 'rook',
  name: 'Rook',
  role: 'Software Engineer',
  personaPrompt: 'Implement resilient systems.',
  driverId: 'agy',
  driverName: 'Antigravity CLI',
  driverStatus: 'online',
  trustTier: 1,
  sessionCount,
  createdAt: new Date().toISOString()
});

/**
 * Most-recent-first, mirroring `OrderByDescending(UpdatedAt)` on the API: index 0 is the newest
 * turn. `sessionCount` on the owner is deliberately larger than `count`, because the global
 * `GET /api/sessions` window is capped and therefore cannot hand the client the whole history.
 */
const makeSessions = (
  count: number,
  opts: { personaId?: string; driverId?: string; titleFor?: (i: number) => string } = {}
) =>
  Array.from({ length: count }, (_, i) => ({
    id: `sess-${i + 1}`,
    title: opts.titleFor ? opts.titleFor(i + 1) : `Triage Report ${i + 1}`,
    status: 'active',
    personaId: opts.personaId,
    driverId: opts.driverId,
    creatorAgentId: opts.driverId,
    metadataJson: '{}',
    createdAt: new Date(Date.UTC(2026, 9, 27, 12, 0, 0) - i * 60_000).toISOString(),
    updatedAt: new Date(Date.UTC(2026, 9, 27, 12, 0, 0) - i * 60_000).toISOString()
  })) as AgentSession[];

/** The sidebar is the only place these tests care about; the main panel renders raw titles by design. */
const sidebarEl = () => document.querySelector('aside') as HTMLElement;
const sidebar = () => within(sidebarEl());

const rowText = (text: string) => sidebar().queryAllByText(text).length;

describe('Persona & Driver session lists are bounded', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getStatus).mockResolvedValue(mockStatus);
    vi.mocked(api.getCapabilities).mockResolvedValue({ agentRuntimes: {} } as never);
    vi.mocked(api.getAgents).mockResolvedValue([]);
    vi.mocked(api.getPersonas).mockResolvedValue([]);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.getProjects).mockResolvedValue([]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
    vi.mocked(api.getAgentUsage).mockResolvedValue(null as never);
    vi.mocked(api.getPersonaSessionsPage).mockResolvedValue({ items: [], total: 0 });
    vi.mocked(api.getAgentSessionsPage).mockResolvedValue({ items: [], total: 0 });
  });

  it('caps the persona session list at the preview limit instead of rendering every row', async () => {
    vi.mocked(api.getPersonas).mockResolvedValue([makePersona(12)]);
    vi.mocked(api.getSessions).mockResolvedValue(makeSessions(8, { personaId: 'rook', driverId: 'agy' }));

    render(<App />);

    await waitFor(() => expect(rowText('Triage Report 1')).toBeGreaterThan(0));

    // The preview shows the five most recent; 6-8 exist in state but stay out of the sidebar.
    expect(rowText('Triage Report 5')).toBe(1);
    expect(rowText('Triage Report 6')).toBe(0);
    expect(rowText('Triage Report 8')).toBe(0);
    expect(rowText('Triage Report 1')).toBe(1);
  });

  it('offers a show-all affordance whose count comes from the server total, not the loaded window', async () => {
    vi.mocked(api.getPersonas).mockResolvedValue([makePersona(12)]);
    vi.mocked(api.getSessions).mockResolvedValue(makeSessions(8, { personaId: 'rook', driverId: 'agy' }));

    render(<App />);

    const toggle = await screen.findByTestId('show-all-persona-sessions-rook');
    // 12 on the server, 8 inside the capped window, 5 previewed => 7 hidden, not 3.
    expect(toggle).toHaveTextContent('Show all (7)');
  });

  it('lazy-loads the full list from the persona endpoint when expanding', async () => {
    vi.mocked(api.getPersonas).mockResolvedValue([makePersona(12)]);
    vi.mocked(api.getSessions).mockResolvedValue(makeSessions(8, { personaId: 'rook', driverId: 'agy' }));
    vi.mocked(api.getPersonaSessionsPage).mockResolvedValue({
      items: makeSessions(12, { personaId: 'rook', driverId: 'agy' }),
      total: 12
    });

    render(<App />);

    fireEvent.click(await screen.findByTestId('show-all-persona-sessions-rook'));

    await waitFor(() => expect(api.getPersonaSessionsPage).toHaveBeenCalledWith('rook'));
    await waitFor(() => expect(rowText('Triage Report 12')).toBe(1));
    expect(screen.getByTestId('show-fewer-persona-sessions-rook')).toBeInTheDocument();
  });

  it('still collapses using the loaded window when the lazy fetch fails', async () => {
    vi.mocked(api.getPersonas).mockResolvedValue([makePersona(12)]);
    vi.mocked(api.getSessions).mockResolvedValue(makeSessions(8, { personaId: 'rook', driverId: 'agy' }));
    vi.mocked(api.getPersonaSessionsPage).mockRejectedValue(new Error('503 from nginx'));

    render(<App />);

    fireEvent.click(await screen.findByTestId('show-all-persona-sessions-rook'));

    // Degraded, not broken: the window we already have becomes visible and the card stays bounded.
    await waitFor(() => expect(rowText('Triage Report 8')).toBe(1));
    expect(rowText('Triage Report 12')).toBe(0);
    expect(screen.getByTestId('show-fewer-persona-sessions-rook')).toBeInTheDocument();
    expect(screen.getByTestId('session-list-error-persona-rook')).toBeInTheDocument();
  });

  it('does not render a toggle when the persona fits inside the preview limit', async () => {
    vi.mocked(api.getPersonas).mockResolvedValue([makePersona(3)]);
    vi.mocked(api.getSessions).mockResolvedValue(makeSessions(3, { personaId: 'rook', driverId: 'agy' }));

    render(<App />);

    await waitFor(() => expect(rowText('Triage Report 3')).toBe(1));
    expect(screen.queryByTestId('show-all-persona-sessions-rook')).not.toBeInTheDocument();
  });

  it('caps the driver session list and expands it through the driver endpoint', async () => {
    vi.mocked(api.getAgents).mockResolvedValue([{ ...driver, sessionCount: 12 }]);
    vi.mocked(api.getSessions).mockResolvedValue(makeSessions(8, { driverId: 'agy' }));
    vi.mocked(api.getAgentSessionsPage).mockResolvedValue({
      items: makeSessions(12, { driverId: 'agy' }),
      total: 12
    });

    render(<App />);
    fireEvent.click(screen.getByRole('button', { name: /Drivers & Infrastructure/i }));

    await waitFor(() => expect(rowText('Triage Report 1')).toBeGreaterThan(0));
    expect(rowText('Triage Report 8')).toBe(0);

    fireEvent.click(await screen.findByTestId('show-all-driver-sessions-agy'));

    await waitFor(() => expect(api.getAgentSessionsPage).toHaveBeenCalledWith('agy'));
    await waitFor(() => expect(rowText('Triage Report 12')).toBe(1));
  });

  it('labels job-turn rows with a per-job ordinal instead of the raw "Job Turn: X (@Persona)" title', async () => {
    const jobTurns = makeSessions(3, {
      personaId: 'rook',
      driverId: 'agy',
      titleFor: () => 'Job Turn: Tidy Sidebar (@Rook)'
    }).map((s) => ({
      ...s,
      metadataJson: JSON.stringify({ jobId: 'job-1', jobTitle: 'Tidy Sidebar', personaName: 'Rook' })
    })) as AgentSession[];

    vi.mocked(api.getPersonas).mockResolvedValue([makePersona(3)]);
    vi.mocked(api.getSessions).mockResolvedValue(jobTurns);

    render(<App />);

    // Newest turn is #3, the turn that kicked the job off is #1.
    await waitFor(() => expect(rowText('#3 Tidy Sidebar')).toBe(1));
    expect(rowText('#1 Tidy Sidebar')).toBe(1);
    expect(sidebar().queryByText('Job Turn: Tidy Sidebar (@Rook)')).not.toBeInTheDocument();
  });

  it('keeps the selected row visible when the list collapses back down', async () => {
    vi.mocked(api.getPersonas).mockResolvedValue([makePersona(20)]);
    vi.mocked(api.getSessions).mockResolvedValue(makeSessions(12, { personaId: 'rook', driverId: 'agy' }));
    vi.mocked(api.getPersonaSessionsPage).mockResolvedValue({
      items: makeSessions(12, { personaId: 'rook', driverId: 'agy' }),
      total: 12
    });

    render(<App />);

    await waitFor(() => expect(rowText('Triage Report 1')).toBeGreaterThan(0));

    fireEvent.click(await screen.findByTestId('show-all-persona-sessions-rook'));
    await waitFor(() => expect(api.getPersonaSessionsPage).toHaveBeenCalled());
    await waitFor(() => expect(rowText('Triage Report 11')).toBe(1));

    fireEvent.click(screen.getByTestId('session-row-sess-11'));

    fireEvent.click(screen.getByTestId('show-fewer-persona-sessions-rook'));

    // Collapsing must not scroll out the row the operator is reading.
    await waitFor(() => expect(rowText('Triage Report 11')).toBe(1));
    expect(rowText('Triage Report 12')).toBe(0);
  });
});
