import { render, screen, fireEvent, act, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { JobCockpit } from '../JobCockpit';
import { ProjectJobStatus, type Project, type ProjectJob, type JobMessage } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    uploadJobAttachments: vi.fn(),
    uploadTaskAttachments: vi.fn(),
    deleteTaskAttachment: vi.fn()
  }
}));

/**
 * Mobile auto-scroll regression cover.
 *
 * On a phone App.tsx keeps the job cockpit inside `<main class="hidden">` while the master
 * list is showing (App.tsx:3475). The transcript therefore mounts with `display: none`,
 * where every layout metric reads 0. The original "stick to bottom" effect wrote
 * scrollTop = scrollHeight against that zero box: a silent no-op that still looked like it
 * ran, and nothing re-pinned once the panel became visible. Tapping into a job left the
 * operator at the top of history.
 *
 * jsdom has no layout engine, so the one property the bug turns on is faked: a hidden
 * container reports 0 for clientHeight/scrollHeight, a visible one reports a real box.
 */

const VIEWPORT_HEIGHT = 800;
const CONTENT_HEIGHT = 1200;

const mockProject: Project = {
  id: 'agent-mesh',
  name: 'Agent Mesh',
  description: '',
  workspaceType: 0,
  gitBaseBranch: 'main',
  projectInstructions: '',
  defaultLeadPersonaId: 'astra',
  defaultLeadPersonaName: 'Astra',
  assignedPersonas: [{ id: 'astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' }],
  jobCount: 1,
  activeJobCount: 1,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
};

const makeJob = (id = 'job-1'): ProjectJob => ({
  id,
  projectId: 'agent-mesh',
  projectName: 'Agent Mesh',
  title: 'Mobile Chat Scroll',
  description: '',
  status: ProjectJobStatus.Active,
  gitBranchName: `mesh/${id}`,
  tasks: [],
  messageCount: 0,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
});

const makeMessages = (count: number): JobMessage[] =>
  Array.from({ length: count }, (_, i) => ({
    id: `msg-${i}`,
    jobId: 'job-1',
    senderType: i % 2 === 0 ? 'user' : 'persona',
    senderId: i % 2 === 0 ? null : 'astra',
    senderName: i % 2 === 0 ? 'Operator' : 'Astra',
    content: `Message number ${i + 1}`,
    mentions: [],
    turnSessionId: null,
    createdAt: new Date(Date.now() + i * 1000).toISOString()
  }));

const baseProps = {
  project: mockProject,
  tasks: [],
  personas: [],
  sessions: [],
  onSendMessage: vi.fn().mockResolvedValue(undefined),
  onAddTask: vi.fn().mockResolvedValue(undefined),
  onToggleTask: vi.fn().mockResolvedValue(undefined),
  onDeleteTask: vi.fn().mockResolvedValue(undefined),
  onCompleteJob: vi.fn().mockResolvedValue(undefined)
};

const chat = () => screen.getByTestId('job-messages-container') as HTMLDivElement;

/**
 * Models the browser: while `hidden` is true the element has no box, so it cannot scroll
 * and reports 0 for every metric. Once it gets a box, scrollTop is clamped to the real
 * scrollable range exactly like a layout engine would.
 *
 * Idempotent per node: the container element survives re-renders, and re-defining the
 * accessors would reset the scroll position the component just wrote.
 */
const stubbedNodes = new WeakSet<HTMLElement>();

function stubLayout(
  node: HTMLElement,
  hidden: () => boolean,
  getContentHeight: () => number = () => CONTENT_HEIGHT
) {
  if (stubbedNodes.has(node)) return;
  stubbedNodes.add(node);

  let scrollTop = 0;
  Object.defineProperty(node, 'clientHeight', {
    configurable: true,
    get: () => (hidden() ? 0 : VIEWPORT_HEIGHT)
  });
  Object.defineProperty(node, 'scrollHeight', {
    configurable: true,
    get: () => (hidden() ? 0 : getContentHeight())
  });
  Object.defineProperty(node, 'scrollTop', {
    configurable: true,
    get: () => scrollTop,
    set: (value: number) => {
      const max = Math.max(0, node.scrollHeight - node.clientHeight);
      scrollTop = hidden() ? 0 : Math.max(0, Math.min(value, max));
    }
  });

  // Laying the node out is itself a reason to re-pin, exactly as it is in a browser.
  flushResize();
}

/** Fires the event a real un-hide produces: the element gains a box. */
function flushResize() {
  act(() => {
    window.dispatchEvent(new Event('resize'));
  });
}

describe('JobCockpit - mobile auto-scroll to bottom', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      }
    );
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('opens at the newest message when the transcript mounts with a real box', () => {
    const { rerender } = render(
      <JobCockpit {...baseProps} job={makeJob()} messages={[]} />
    );
    stubLayout(chat(), () => false);

    rerender(<JobCockpit {...baseProps} job={makeJob()} messages={makeMessages(5)} />);
    stubLayout(chat(), () => false);
    flushResize();

    expect(chat().scrollTop).toBe(CONTENT_HEIGHT - VIEWPORT_HEIGHT);
  });

  it('re-pins when the panel is un-hidden after mounting inside a hidden mobile panel', () => {
    // Sequence the bug report describes: the cockpit mounts while <main> is `hidden`, the
    // messages resolve in that state, and only then does the operator drill into the job.
    let hidden = true;
    const { rerender } = render(
      <JobCockpit {...baseProps} job={makeJob()} messages={[]} />
    );
    stubLayout(chat(), () => hidden);

    rerender(<JobCockpit {...baseProps} job={makeJob()} messages={makeMessages(5)} />);
    // Still hidden: the pin must not be recorded as satisfied.
    expect(chat().scrollTop).toBe(0);

    // Operator taps the job. The panel gets a box; no new messages arrive, so `messages`
    // is unchanged and the old effect had nothing left to fire on.
    hidden = false;
    flushResize();

    expect(chat().scrollTop).toBe(CONTENT_HEIGHT - VIEWPORT_HEIGHT);
  });

  it('re-pins on un-hide even when the transcript was already rendered hidden', () => {
    // Same as above but the messages resolved *before* the component ever mounted, which
    // is what happens when the mobile master list is still on screen during the fetch.
    let hidden = true;
    render(<JobCockpit {...baseProps} job={makeJob()} messages={makeMessages(5)} />);
    stubLayout(chat(), () => hidden);
    expect(chat().scrollTop).toBe(0);

    hidden = false;
    flushResize();

    expect(chat().scrollTop).toBe(CONTENT_HEIGHT - VIEWPORT_HEIGHT);
  });

  it('does not yank the operator back down after a resize once they have scrolled up', () => {
    const { rerender } = render(
      <JobCockpit {...baseProps} job={makeJob()} messages={[]} />
    );
    stubLayout(chat(), () => false);
    rerender(<JobCockpit {...baseProps} job={makeJob()} messages={makeMessages(5)} />);
    stubLayout(chat(), () => false);
    flushResize();

    // Scroll to the top of history, well past the 200px release threshold.
    chat().scrollTop = 0;
    fireEvent.scroll(chat(), { target: { scrollTop: 0 } });

    flushResize();

    expect(chat().scrollTop).toBe(0);
  });

  it('keeps following live messages while the operator is at the bottom', () => {
    // The feed grows as replies land. A real browser keeps scrollTop where it is, so the
    // operator drifts away from the bottom; following must pull them back down.
    let contentHeight = CONTENT_HEIGHT;
    const { rerender } = render(
      <JobCockpit {...baseProps} job={makeJob()} messages={makeMessages(1)} />
    );
    stubLayout(chat(), () => false, () => contentHeight);
    expect(chat().scrollTop).toBe(CONTENT_HEIGHT - VIEWPORT_HEIGHT);

    // A reply arrives and the transcript gets taller.
    contentHeight = CONTENT_HEIGHT + 300;
    rerender(<JobCockpit {...baseProps} job={makeJob()} messages={makeMessages(2)} />);
    stubLayout(chat(), () => false, () => contentHeight);

    expect(chat().scrollTop).toBe(contentHeight - VIEWPORT_HEIGHT);
  });

  it('jumps to the newest message from the scroll-to-bottom pill after scrolling up', () => {
    render(<JobCockpit {...baseProps} job={makeJob()} messages={makeMessages(5)} />);
    stubLayout(chat(), () => false);

    chat().scrollTop = 0;
    fireEvent.scroll(chat(), { target: { scrollTop: 0 } });

    const pill = screen.getByTestId('job-scroll-to-bottom-btn');
    fireEvent.click(pill);

    expect(chat().scrollTop).toBe(CONTENT_HEIGHT - VIEWPORT_HEIGHT);
  });

  it('opens the newest message when switching jobs while scrolled up on the previous one', () => {
    const { rerender } = render(
      <JobCockpit {...baseProps} job={makeJob('job-1')} messages={makeMessages(5)} />
    );
    stubLayout(chat(), () => false);
    flushResize();

    chat().scrollTop = 0;
    fireEvent.scroll(chat(), { target: { scrollTop: 0 } });
    expect(chat().scrollTop).toBe(0);

    // Navigate to a different job: the stale "scrolled up" must not strand the operator at
    // the top of the transcript they just opened.
    rerender(<JobCockpit {...baseProps} job={makeJob('job-2')} messages={makeMessages(9)} />);
    stubLayout(chat(), () => false);
    flushResize();

    expect(chat().scrollTop).toBe(CONTENT_HEIGHT - VIEWPORT_HEIGHT);
  });
});
