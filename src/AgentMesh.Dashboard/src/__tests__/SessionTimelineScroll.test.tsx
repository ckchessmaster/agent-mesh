// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type { Agent, AgentSession, SessionMessage, SystemCapabilitiesResponse } from '../types';

vi.mock('../services/api', () => ({
  api: {
    verifyAuth: vi.fn().mockResolvedValue({ authenticated: true }),
    getStatus: vi.fn(),
    getCapabilities: vi.fn(),
    browseFilesystem: vi.fn().mockResolvedValue({ currentPath: '/workspace', directories: [] }),
    getAgents: vi.fn(),
    getPersonas: vi.fn().mockResolvedValue([]),
    getSessions: vi.fn(),
    getSession: vi.fn(),
    getSessionMessages: vi.fn(),
    getSessionArtifacts: vi.fn().mockResolvedValue([]),
    getAgentUsage: vi.fn().mockResolvedValue(null),
    getProjects: vi.fn().mockResolvedValue([]),
    resetMesh: vi.fn(),
    createAgentSession: vi.fn(),
    updateSessionControls: vi.fn(),
    postSessionMessage: vi.fn(),
    stopSession: vi.fn(),
    enqueuePrompt: vi.fn(),
    dequeuePrompt: vi.fn(),
    sendQueuedPromptNow: vi.fn(),
    submitQuestionAnswer: vi.fn(),
    getSessionArtifact: vi.fn(),
    syncSessionArtifact: vi.fn()
  }
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

const signalRHandlers: Record<string, Function> = {};
const mockSignalRConnection = {
  on: vi.fn((event: string, cb: Function) => {
    signalRHandlers[event] = cb;
  }),
  off: vi.fn((event: string) => {
    delete signalRHandlers[event];
  }),
  invoke: vi.fn().mockResolvedValue(undefined)
};

vi.mock('../hooks/useSignalR', () => ({
  useSignalR: () => ({ connection: mockSignalRConnection, connected: true })
}));

/**
 * jsdom has no layout engine: scrollHeight/clientHeight read as 0 and writing scrollTop is
 * a no-op, so "did it land at the bottom?" is unobservable out of the box. We fake one
 * scrollable viewport (the session trajectory) and clamp scrollTop like a browser does, so
 * "pinned" means scrollTop === contentHeight - viewportHeight.
 *
 * Content height is derived from how much DOM is actually inside the timeline rather than
 * handed to the test by hand. That matters: it makes "the transcript got taller" a
 * consequence of what was painted, including growth that happens without a React render
 * (async mermaid SVG injection, streamed DOM, lazily decoded images) — which is exactly
 * the class of failure the operator kept hitting.
 */
const TIMELINE_TESTID = 'session-timeline';
const VIEWPORT_HEIGHT = 600;
const ROW_HEIGHT = 40;
const scrollTopByElement = new WeakMap<Element, number>();
const isTimeline = (el: Element) => (el as HTMLElement).dataset?.testid === TIMELINE_TESTID;
const getTimeline = () => screen.getByTestId(TIMELINE_TESTID);
const contentHeight = () =>
  VIEWPORT_HEIGHT + (getTimeline() ? getTimeline().getElementsByTagName('*').length : 0) * ROW_HEIGHT;
const maxScroll = () => Math.max(0, contentHeight() - VIEWPORT_HEIGHT);

beforeAll(() => {
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get(this: HTMLElement) {
      return isTimeline(this) ? contentHeight() : 0;
    }
  });
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
    configurable: true,
    get(this: HTMLElement) {
      return isTimeline(this) ? VIEWPORT_HEIGHT : 0;
    }
  });
  Object.defineProperty(HTMLElement.prototype, 'scrollTop', {
    configurable: true,
    get(this: HTMLElement) {
      return scrollTopByElement.get(this) ?? 0;
    },
    set(this: HTMLElement, value: number) {
      scrollTopByElement.set(this, Math.min(Math.max(0, value), maxScroll()));
    }
  });
});

const mockAgent: Agent = {
  id: 'agy-core',
  name: 'Agy Lead Architect',
  status: 'online',
  runtimeType: 'CliAgy',
  trustTier: 3,
  createdAt: new Date().toISOString()
};

const mockCaps = {
  workspace: {
    defaultSandboxPath: '~/.agentmesh/workspaces/<session-id>',
    currentDirectory: '/workspace/agent-mesh',
    userHome: '/home/testuser',
    bookmarks: []
  },
  agentRuntimes: {}
} as unknown as SystemCapabilitiesResponse;

function makeSession(id: string, title: string): AgentSession {
  return {
    id,
    title,
    status: 'active',
    metadataJson: '{}',
    creatorAgentId: mockAgent.id,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };
}

function makeMessage(sessionId: string, index: number, content: string): SessionMessage {
  return {
    id: `${sessionId}-msg-${index}`,
    sessionId,
    senderAgentId: index % 2 === 0 ? 'operator' : mockAgent.id,
    eventType: index % 2 === 0 ? 'prompt' : 'response',
    content,
    createdAt: new Date(Date.parse('2026-10-22T00:00:00Z') + index * 1000).toISOString()
  };
}

function makeMessages(sessionId: string, count: number): SessionMessage[] {
  return Array.from({ length: count }, (_, i) => makeMessage(sessionId, i, `${sessionId} transcript line ${i}`));
}

async function openSession(title: string) {
  await waitFor(() => expect(screen.getAllByText(title).length).toBeGreaterThan(0));
  fireEvent.click(screen.getAllByText(title)[0]);
  await waitFor(() => expect(getTimeline()).toBeInTheDocument());
}

function pushLiveEvent(message: SessionMessage) {
  act(() => {
    signalRHandlers['ReceiveMessage'](message);
  });
}

describe('Session trajectory auto-scroll to bottom', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    for (const key of Object.keys(signalRHandlers)) delete signalRHandlers[key];

    vi.mocked(api.getStatus).mockResolvedValue({
      status: 'Healthy',
      timestamp: new Date().toISOString(),
      environment: 'Development',
      services: {}
    });
    vi.mocked(api.getCapabilities).mockResolvedValue(mockCaps);
    vi.mocked(api.getAgents).mockResolvedValue([mockAgent]);
    vi.mocked(api.getSessions).mockResolvedValue([]);
    vi.mocked(api.getSessionMessages).mockResolvedValue([]);
    vi.mocked(api.getSession).mockResolvedValue(null as unknown as AgentSession);
    vi.mocked(api.getProjects).mockResolvedValue([]);
  });

  it('lands at the bottom of the transcript when a session is opened', async () => {
    const sessionA = makeSession('sess-a', 'Session Alpha');
    vi.mocked(api.getSessions).mockResolvedValue([sessionA]);
    vi.mocked(api.getSessionMessages).mockResolvedValue(makeMessages('sess-a', 6));
    vi.mocked(api.getSession).mockResolvedValue(sessionA);

    render(<App />);
    await openSession('Session Alpha');
    await waitFor(() => expect(screen.getByText('sess-a transcript line 5')).toBeInTheDocument());

    expect(maxScroll()).toBeGreaterThan(0);
    expect(getTimeline().scrollTop).toBe(maxScroll());
  });

  it('lands at the bottom when returning to the session screen after visiting another tab', async () => {
    // The trajectory stream unmounts while Projects & Jobs is open, and `events` state
    // survives the trip. The code this replaces pinned only from an effect keyed on
    // `events`, so the remounted stream came back at scrollTop 0 and nothing re-pinned it
    // — the bug the operator reported.
    const sessionA = makeSession('sess-a', 'Session Alpha');
    vi.mocked(api.getSessions).mockResolvedValue([sessionA]);
    vi.mocked(api.getSessionMessages).mockResolvedValue(makeMessages('sess-a', 6));
    vi.mocked(api.getSession).mockResolvedValue(sessionA);

    render(<App />);
    await openSession('Session Alpha');
    await waitFor(() => expect(screen.getByText('sess-a transcript line 5')).toBeInTheDocument());
    expect(getTimeline().scrollTop).toBe(maxScroll());

    fireEvent.click(screen.getByRole('button', { name: /Projects & Jobs/i }));
    await waitFor(() => expect(screen.queryByTestId(TIMELINE_TESTID)).not.toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Personas & Sessions/i }));
    await waitFor(() => expect(getTimeline()).toBeInTheDocument());
    expect(getTimeline().scrollTop).toBe(maxScroll());
  });

  it('stays at the bottom when the transcript grows after the pin (async mermaid SVG)', async () => {
    // Mermaid resolves out of band: the SVG lands in the DOM well after the transcript was
    // pinned, and the effect keyed on `events` never re-runs because no new event arrived.
    // The pinned offset silently turns into "somewhere in the middle of the transcript".
    const sessionA = makeSession('sess-a', 'Session Alpha');
    vi.mocked(api.getSessions).mockResolvedValue([sessionA]);
    vi.mocked(api.getSession).mockResolvedValue(sessionA);
    vi.mocked(api.getSessionMessages).mockResolvedValue([
      makeMessage('sess-a', 1, '```mermaid\ngraph TD;\n  A-->B;\n```')
    ]);

    render(<App />);
    await openSession('Session Alpha');
    await waitFor(() => expect(screen.getByTestId('mermaid-viewer')).toBeInTheDocument());

    // The diagram body is injected into the DOM after the initial paint, growing the stream.
    await waitFor(() => expect(screen.getByTestId('mermaid-diagram-container')).toBeInTheDocument());
    await waitFor(() => expect(getTimeline().scrollTop).toBe(maxScroll()));
  });

  it('follows new events while tracking and stops following once the operator scrolls up', async () => {
    const sessionA = makeSession('sess-a', 'Session Alpha');
    vi.mocked(api.getSessions).mockResolvedValue([sessionA]);
    vi.mocked(api.getSessionMessages).mockResolvedValue(makeMessages('sess-a', 4));
    vi.mocked(api.getSession).mockResolvedValue(sessionA);

    render(<App />);
    await openSession('Session Alpha');
    await waitFor(() => expect(screen.getByText('sess-a transcript line 3')).toBeInTheDocument());
    expect(getTimeline().scrollTop).toBe(maxScroll());

    // A live event grows the stream: following tracks it.
    pushLiveEvent(makeMessage('sess-a', 50, 'live event while tracking'));
    await waitFor(() => expect(screen.getByText('live event while tracking')).toBeInTheDocument());
    expect(getTimeline().scrollTop).toBe(maxScroll());

    // Operator scrolls up to read something older.
    const scrolledUpTo = 100;
    act(() => {
      getTimeline().scrollTop = scrolledUpTo;
      fireEvent.scroll(getTimeline());
    });
    expect(getTimeline().scrollTop).toBe(scrolledUpTo);

    // A new event must not yank them back to the bottom.
    pushLiveEvent(makeMessage('sess-a', 51, 'live event while scrolled up'));
    await waitFor(() => expect(screen.getByText('live event while scrolled up')).toBeInTheDocument());
    expect(getTimeline().scrollTop).toBe(scrolledUpTo);

    // "Jump to bottom" re-engages following.
    fireEvent.click(screen.getByRole('button', { name: /Jump to bottom/i }));
    expect(getTimeline().scrollTop).toBe(maxScroll());

    // And subsequent live events are followed again.
    pushLiveEvent(makeMessage('sess-a', 52, 'live event after resuming'));
    await waitFor(() => expect(screen.getByText('live event after resuming')).toBeInTheDocument());
    expect(getTimeline().scrollTop).toBe(maxScroll());
  });
});
