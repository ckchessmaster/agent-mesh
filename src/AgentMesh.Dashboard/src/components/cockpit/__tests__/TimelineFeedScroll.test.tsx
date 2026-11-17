import { describe, it, expect, beforeAll, afterAll, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { ProjectJobStatus, type ProjectJob } from '../../../types';
import type { JobTimelineCard } from '../../../types/timeline';
import { TimelineFeed } from '../TimelineFeed';

/**
 * jsdom lays nothing out, so every element reports scrollHeight/clientHeight 0 and writes to scrollTop
 * are discarded. Both are given real (and deliberately fake) numbers here so the feed's positioning has
 * something to be right or wrong about: a 1000px transcript in a 300px viewport.
 */
const SCROLL_HEIGHT = 1000;
const CLIENT_HEIGHT = 300;
const BOTTOM_SCROLL_TOP = SCROLL_HEIGHT - CLIENT_HEIGHT;

const proto = window.HTMLElement.prototype;
const originalDescriptors = {
  scrollTop: Object.getOwnPropertyDescriptor(proto, 'scrollTop'),
  scrollHeight: Object.getOwnPropertyDescriptor(proto, 'scrollHeight'),
  clientHeight: Object.getOwnPropertyDescriptor(proto, 'clientHeight')
};

beforeAll(() => {
  Object.defineProperty(proto, 'scrollTop', {
    configurable: true,
    get(this: any) {
      return this.__scrollTop ?? 0;
    },
    set(this: any, value: number) {
      this.__scrollTop = value;
    }
  });
  Object.defineProperty(proto, 'scrollHeight', {
    configurable: true,
    get(this: any) {
      return this.__scrollHeight ?? SCROLL_HEIGHT;
    }
  });
  Object.defineProperty(proto, 'clientHeight', {
    configurable: true,
    get(this: any) {
      return this.__clientHeight ?? CLIENT_HEIGHT;
    }
  });
});

afterAll(() => {
  for (const [name, descriptor] of Object.entries(originalDescriptors)) {
    if (descriptor) Object.defineProperty(proto, name, descriptor);
    else delete (proto as any)[name];
  }
});

afterEach(() => {
  cleanup();
  document.documentElement.scrollTop = 0;
});

const makeCard = (id: string, jobId: string): JobTimelineCard => ({
  id,
  jobId,
  cardType: 'JobInitialized',
  status: 'Resolved',
  title: `Card ${id}`,
  structuredPayloadJson: '{}',
  comments: [],
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
});

const makeJob = (id: string): ProjectJob => ({
  id,
  projectId: 'agent-mesh',
  projectName: 'Agent Mesh',
  title: 'Standardized project setup',
  description: '',
  status: ProjectJobStatus.Active,
  gitBranchName: `mesh/${id}`,
  tasks: [],
  messageCount: 0,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString()
});

const feed = () => screen.getByTestId('timeline-feed') as HTMLElement;
const scrollOf = (el: HTMLElement) => (el as any).__scrollTop ?? 0;

/** Scrolls to a position and reports it through the same event path the browser would. */
const moveTo = (el: HTMLElement, scrollTop: number) => {
  (el as any).__scrollTop = scrollTop;
  fireEvent.scroll(el);
};

describe('TimelineFeed - stick to bottom', () => {
  it('opens at the newest card instead of the top of the run', () => {
    render(
      <TimelineFeed cards={[makeCard('c1', 'job-1'), makeCard('c2', 'job-1')]} job={makeJob('job-1')} />
    );

    expect(scrollOf(feed())).toBe(SCROLL_HEIGHT);
  });

  it('pins when cards arrive while the operator is already at the bottom', () => {
    const { rerender } = render(
      <TimelineFeed cards={[makeCard('c1', 'job-1')]} job={makeJob('job-1')} />
    );

    rerender(
      <TimelineFeed
        cards={[makeCard('c1', 'job-1'), makeCard('c2', 'job-1'), makeCard('c3', 'job-1')]}
        job={makeJob('job-1')}
      />
    );

    expect(scrollOf(feed())).toBe(SCROLL_HEIGHT);
  });

  it('leaves an operator reading history alone when a new card lands', () => {
    const { rerender } = render(
      <TimelineFeed cards={[makeCard('c1', 'job-1'), makeCard('c2', 'job-1')]} job={makeJob('job-1')} />
    );

    // Mid-run, well outside the follow slack.
    moveTo(feed(), 120);

    rerender(
      <TimelineFeed
        cards={[makeCard('c1', 'job-1'), makeCard('c2', 'job-1'), makeCard('c3', 'job-1')]}
        job={makeJob('job-1')}
      />
    );

    expect(scrollOf(feed())).toBe(120);
  });

  it('starts following again once the operator returns to the bottom', () => {
    const { rerender } = render(
      <TimelineFeed cards={[makeCard('c1', 'job-1')]} job={makeJob('job-1')} />
    );

    moveTo(feed(), 0);
    expect(scrollOf(feed())).toBe(0);

    moveTo(feed(), BOTTOM_SCROLL_TOP);

    rerender(
      <TimelineFeed
        cards={[makeCard('c1', 'job-1'), makeCard('c2', 'job-1')]}
        job={makeJob('job-1')}
      />
    );

    expect(scrollOf(feed())).toBe(SCROLL_HEIGHT);
  });

  it('re-pins when the cockpit swaps to a different job', () => {
    const { rerender } = render(
      <TimelineFeed cards={[makeCard('c1', 'job-1'), makeCard('c2', 'job-1')]} job={makeJob('job-1')} />
    );

    moveTo(feed(), 40);

    rerender(<TimelineFeed cards={[makeCard('c9', 'job-2')]} job={makeJob('job-2')} />);

    expect(scrollOf(feed())).toBe(SCROLL_HEIGHT);
  });
});
