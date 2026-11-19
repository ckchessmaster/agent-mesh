import { createRef } from 'react';
import type { RefObject } from 'react';
import { render, screen, fireEvent, waitFor, within, act } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AttentionBell, MuteToggle } from '../AttentionBell';
import type { AttentionBellHandle } from '../AttentionBell';
import {
  ATTENTION_STATE_MAX_ENTRIES,
  ATTENTION_STATE_TTL_MS,
  DISMISSED_STORAGE_KEY,
  READ_STORAGE_KEY,
  attentionLabelFor,
  badgeCount,
  dropStamps,
  formatAttentionAge,
  hasStamp,
  loadStampMap,
  normalizeAttentionItems,
  parseStampMap,
  pruneStampMap,
  saveStampMap,
  sortAttentionItems,
  stampIds,
  unreadAttentionItems,
  visibleAttentionItems
} from '../../utils/attentionInbox';
import {
  ATTENTION_NAVIGATE_EVENT,
  MUTE_STORAGE_KEY,
  NotificationService,
  SEEN_STORAGE_KEY,
  SELECTED_JOB_KEY,
  SELECTED_PROJECT_KEY
} from '../../services/notifications';
import { api } from '../../services/api';
import type { AttentionItem, AttentionRollup } from '../../types/attention';

/**
 * Coverage for the attention bell and the read/dismiss bookkeeping underneath it.
 *
 * The pure helpers are tested directly because they decide the badge and the dismissal rules, and a
 * component test would hide an off-by-one behind a click. The component is then tested against a REAL
 * `NotificationService` (only its permission getters are stubbed) — the interesting failure mode in
 * this feature is not "does the row render" but "the bell and the OS banner disagree about what is
 * still pending", and that only shows up if the seen-set is actually running.
 */

const NOW = Date.UTC(2026, 10, 19, 12, 0, 0);
const HOUR = 60 * 60 * 1000;

const at = (msAgo: number) => new Date(NOW - msAgo).toISOString();

const makeItem = (overrides: Partial<AttentionItem> = {}): AttentionItem => ({
  id: 'job-1:planApproval',
  type: 'planApproval',
  title: 'Plan approval needed',
  detail: 'Waiting for plan approval before decomposing tasks',
  jobId: 'job-1',
  jobTitle: 'Add retry backoff to the ingest worker',
  projectId: 'proj-1',
  projectName: 'agent-mesh',
  cardId: null,
  prUrl: null,
  prNumber: null,
  since: at(3 * HOUR),
  ...overrides
});

const prItem = (overrides: Partial<AttentionItem> = {}): AttentionItem =>
  makeItem({
    id: 'job-2:prReview',
    type: 'prReview',
    jobId: 'job-2',
    title: 'PR review requested',
    jobTitle: 'Ship the notifications bell',
    detail: 'PR #7 opened and awaiting review',
    prUrl: 'https://github.com/ckchessmaster/agent-mesh/pull/7',
    prNumber: 7,
    since: at(40 * 60_000),
    ...overrides
  });

const rollup = (...items: AttentionItem[]): AttentionRollup => ({
  items,
  count: items.length,
  generatedAt: new Date(NOW).toISOString()
});

const makeService = (storage: Storage | null = window.localStorage) =>
  new NotificationService({ now: () => NOW, storage });

type BellFixture = {
  service?: NotificationService;
  storage?: Storage | null;
  pollIntervalMs?: number;
  ref?: RefObject<AttentionBellHandle | null>;
  fetchRollup?: () => Promise<AttentionRollup | null | undefined>;
};

const mountBell = (
  items: AttentionItem[],
  options: BellFixture = {}
) => {
  const service = options.service ?? makeService();
  const fetchRollup = options.fetchRollup ?? vi.fn(async () => rollup(...items));
  const view = render(
    <AttentionBell
      ref={options.ref}
      service={service}
      fetchRollup={fetchRollup}
      storage={options.storage === undefined ? window.localStorage : options.storage}
      pollIntervalMs={options.pollIntervalMs ?? 0}
      now={() => NOW}
    />
  );
  return { ...view, service, fetchRollup };
};

const bellButton = () => screen.getByTestId('attention-bell-button');
const openBell = () => fireEvent.click(bellButton());
const closeBell = () => fireEvent.click(screen.getByTestId('attention-close'));
const dismissRow = (id: string) => fireEvent.click(screen.getByTestId(`attention-dismiss-${id}`));
const openRow = (id: string) => fireEvent.click(screen.getByTestId(`attention-open-${id}`));
const refreshNow = async (ref: RefObject<AttentionBellHandle | null>) => {
  await act(async () => {
    await ref.current?.refresh();
  });
};

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  localStorage.clear();
});

/* ========================================================================= *
 * Pure inbox state
 * ========================================================================= */

describe('attentionInbox state helpers', () => {
  it('formats an age the way an operator reads it', () => {
    expect(formatAttentionAge(at(30_000), NOW)).toBe('just now');
    expect(formatAttentionAge(at(12 * 60_000), NOW)).toBe('12m');
    expect(formatAttentionAge(at(3 * HOUR), NOW)).toBe('3h');
    expect(formatAttentionAge(at(26 * HOUR), NOW)).toBe('1d');
    expect(formatAttentionAge(at(95 * 24 * HOUR), NOW)).toBe('3mo');
  });

  it('does not print a negative age for a clock skew, and says unknown rather than guessing', () => {
    expect(formatAttentionAge(new Date(NOW + HOUR).toISOString(), NOW)).toBe('just now');
    expect(formatAttentionAge('not-a-date', NOW)).toBe('unknown');
    expect(formatAttentionAge(null, NOW)).toBe('unknown');
  });

  it('prunes read/dismiss stamps past the TTL and evicts oldest-first at the cap', () => {
    const expired = pruneStampMap({ old: NOW - ATTENTION_STATE_TTL_MS - 1, fresh: NOW - 1000 }, NOW);
    expect(Object.keys(expired)).toEqual(['fresh']);

    const crowded: Record<string, number> = {};
    for (let i = 0; i < ATTENTION_STATE_MAX_ENTRIES + 5; i += 1) crowded[`id-${i}`] = NOW + i;
    const capped = pruneStampMap(crowded, NOW);
    expect(Object.keys(capped)).toHaveLength(ATTENTION_STATE_MAX_ENTRIES);
    expect(hasStamp(capped, 'id-0')).toBe(false);
    expect(hasStamp(capped, `id-${ATTENTION_STATE_MAX_ENTRIES + 4}`)).toBe(true);
  });

  it('tolerates a corrupt persisted map instead of crashing the cockpit', () => {
    expect(parseStampMap(null)).toEqual({});
    expect(parseStampMap('{not json')).toEqual({});
    expect(parseStampMap('[1,2]')).toEqual({});
    // `null` and `''` must not become epoch stamps: a 0 would survive the TTL pass and mask real state.
    expect(parseStampMap(JSON.stringify({ a: 'nope', b: '123', c: null, d: '' }))).toEqual({ b: 123 });
  });

  it('removes the storage key when the last stamp goes, so a cleared inbox leaves no litter', () => {
    saveStampMap(window.localStorage, READ_STORAGE_KEY, { 'job-1:planApproval': NOW }, NOW);
    expect(window.localStorage.getItem(READ_STORAGE_KEY)).toContain('job-1:planApproval');
    saveStampMap(window.localStorage, READ_STORAGE_KEY, {}, NOW);
    expect(window.localStorage.getItem(READ_STORAGE_KEY)).toBeNull();
  });

  it('survives a storage that refuses to be read or written (private mode, quota)', () => {
    const hostile = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('quota');
      },
      removeItem: () => {
        throw new Error('blocked');
      },
      clear: () => undefined,
      key: () => null,
      length: 0
    } as unknown as Storage;

    expect(loadStampMap(hostile, READ_STORAGE_KEY, NOW)).toEqual({});
    expect(() => saveStampMap(hostile, READ_STORAGE_KEY, { a: NOW }, NOW)).not.toThrow();
    expect(loadStampMap(null, READ_STORAGE_KEY, NOW)).toEqual({});
  });

  it('stamps only ids it has not seen before and drops the ones the server stopped reporting', () => {
    const stamped = stampIds(['a', 'b'], { a: 1 }, NOW);
    expect(stamped).toEqual({ a: 1, b: NOW });
    expect(dropStamps(stamped, ['a', 'missing'])).toEqual({ b: NOW });
  });

  it('normalizes a rollup body into rows, dropping anything without a dedupe id', () => {
    expect(normalizeAttentionItems(null)).toEqual([]);
    expect(normalizeAttentionItems({ count: 0 } as unknown as AttentionRollup)).toEqual([]);
    expect(normalizeAttentionItems({ items: 'nope' } as unknown as AttentionRollup)).toEqual([]);
    const junk = { items: [makeItem(), { type: 'escalation' }, null] } as unknown as AttentionRollup;
    expect(normalizeAttentionItems(junk)).toHaveLength(1);
  });

  it('sorts the inbox oldest-first and parks an unparseable age at the end', () => {
    const sorted = sortAttentionItems([
      makeItem({ id: 'new', since: at(1 * HOUR) }),
      makeItem({ id: 'broken', since: 'yesterday' }),
      makeItem({ id: 'old', since: at(30 * HOUR) })
    ]);
    expect(sorted.map((item) => item.id)).toEqual(['old', 'new', 'broken']);
  });

  it('keeps dismissed waits out of the list and out of the badge, and unseen ones in the badge', () => {
    const a = makeItem({ id: 'a:planApproval' });
    const b = prItem({ id: 'b:prReview' });
    const items = [a, b];
    const dismissed = { 'b:prReview': NOW };

    expect(visibleAttentionItems(items, dismissed).map((i) => i.id)).toEqual(['a:planApproval']);
    expect(unreadAttentionItems(items, dismissed, {}).map((i) => i.id)).toEqual(['a:planApproval']);
    expect(unreadAttentionItems(items, dismissed, { 'a:planApproval': NOW })).toEqual([]);
  });

  it('collapses a large badge instead of widening the header', () => {
    expect(badgeCount(1)).toBe('1');
    expect(badgeCount(9)).toBe('9');
    expect(badgeCount(10)).toBe('9+');
    expect(badgeCount(47)).toBe('9+');
  });

  it('labels the five known waits and renders an unknown server kind generically', () => {
    expect(attentionLabelFor('planApproval')).toBe('Plan approval');
    expect(attentionLabelFor('discoveryQuestions')).toBe('Discovery questions');
    expect(attentionLabelFor('interruptedTurn')).toBe('Interrupted turn');
    expect(attentionLabelFor('prReview')).toBe('PR review');
    expect(attentionLabelFor('escalation')).toBe('Escalation');
    expect(attentionLabelFor('somethingNewInAFutureBuild')).toBe('Needs attention');
    expect(attentionLabelFor(null)).toBe('Needs attention');
  });
});

/* ========================================================================= *
 * api.getAttention
 * ========================================================================= */

describe('api.getAttention', () => {
  it('reads GET /api/attention with the mesh auth headers', async () => {
    const payload = rollup(makeItem());
    const mockFetch = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(new Response(JSON.stringify(payload), { status: 200 }));

    await expect(api.getAttention()).resolves.toEqual(payload);

    expect(mockFetch).toHaveBeenCalledWith(
      '/api/attention',
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: 'Bearer mesh-default-secret-key',
          'X-Agent-Id': 'agy-core'
        })
      })
    );
  });

  it('surfaces the server sentence on a failure so the bell can show it', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ error: 'attention rollup unavailable' }), {
        status: 503,
        statusText: 'Service Unavailable'
      })
    );

    await expect(api.getAttention()).rejects.toThrow(
      'Failed to load pending attention: attention rollup unavailable'
    );
  });
});

/* ========================================================================= *
 * AttentionBell
 * ========================================================================= */

describe('AttentionBell', () => {
  it('loads the rollup on mount and shows the unread count on the badge', async () => {
    const { fetchRollup } = mountBell([makeItem(), prItem()]);

    expect(await screen.findByTestId('attention-badge')).toHaveTextContent('2');
    expect(fetchRollup).toHaveBeenCalledTimes(1);
    expect(bellButton()).toHaveAccessibleName(/2 unread, 2 waiting on you/);
  });

  it('uses api.getAttention when no rollup source is injected', async () => {
    const spy = vi.spyOn(api, 'getAttention').mockResolvedValue(rollup(makeItem()));
    render(
      <AttentionBell
        service={makeService()}
        storage={window.localStorage}
        pollIntervalMs={0}
        now={() => NOW}
      />
    );

    expect(await screen.findByTestId('attention-badge')).toHaveTextContent('1');
    expect(spy).toHaveBeenCalledTimes(1);
  });

  it('feeds the whole rollup to the notification service so banners and the list cannot disagree', async () => {
    const service = makeService();
    const sync = vi.spyOn(service, 'sync');
    const items = [makeItem(), prItem()];
    mountBell(items, { service });

    await waitFor(() => expect(sync).toHaveBeenCalledTimes(1));
    expect(sync).toHaveBeenCalledWith(items);
    // The seen-set is what stops the next poll from re-bannering these two waits.
    expect(window.localStorage.getItem(SEEN_STORAGE_KEY)).toContain('job-1:planApproval');
  });

  it('stays closed until clicked, then lists every wait with its kind, project and age', async () => {
    mountBell([makeItem(), prItem()]);
    await screen.findByTestId('attention-badge');
    expect(screen.queryByTestId('attention-panel')).toBeNull();

    openBell();
    const panel = await screen.findByTestId('attention-panel');

    expect(within(panel).getByText('Plan approval')).toBeInTheDocument();
    expect(within(panel).getByText('PR review')).toBeInTheDocument();
    expect(within(panel).getByText('Add retry backoff to the ingest worker')).toBeInTheDocument();
    expect(within(panel).getByText('Ship the notifications bell')).toBeInTheDocument();
    expect(within(panel).getByText('waiting 3h')).toBeInTheDocument();
    expect(within(panel).getByText('waiting 40m')).toBeInTheDocument();
    expect(within(panel).getByText('#7')).toBeInTheDocument();
    expect(within(panel).getAllByText('agent-mesh')).toHaveLength(2);
    // Oldest wait first, matching the server's documented order.
    const rows = within(panel).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Plan approval');
    expect(rows[1]).toHaveTextContent('PR review');
  });

  it('marks waits read when the inbox is opened, so the badge means "not looked at yet"', async () => {
    mountBell([makeItem()]);
    await screen.findByTestId('attention-badge');

    openBell();
    await waitFor(() => expect(screen.queryByTestId('attention-badge')).toBeNull());
    expect(window.localStorage.getItem(READ_STORAGE_KEY)).toContain('job-1:planApproval');
    // The wait itself is still pending, so the quiet dot keeps the bell from looking empty.
    expect(screen.getByTestId('attention-quiet-dot')).toBeInTheDocument();
  });

  it('restores read state across a remount instead of re-lighting the badge on every reload', async () => {
    const first = mountBell([makeItem()]);
    await screen.findByTestId('attention-badge');
    openBell();
    await waitFor(() => expect(screen.queryByTestId('attention-badge')).toBeNull());
    first.unmount();

    mountBell([makeItem()]);
    await waitFor(() => expect(screen.getByTestId('attention-quiet-dot')).toBeInTheDocument());
    expect(screen.queryByTestId('attention-badge')).toBeNull();
  });

  it('renders the unread dot per row before the inbox is opened and never after', async () => {
    mountBell([makeItem()]);
    await screen.findByTestId('attention-badge');
    openBell();
    expect(screen.queryByTestId('attention-unread-dot-job-1:planApproval')).toBeNull();
  });

  it('dismisses one wait and keeps it hidden across a refresh that still reports it', async () => {
    const ref = createRef<AttentionBellHandle>();
    const service = makeService();
    const dismiss = vi.spyOn(service, 'dismiss');
    mountBell([makeItem(), prItem()], { ref, service });

    await screen.findByTestId('attention-badge');
    openBell();
    dismissRow('job-1:planApproval');

    expect(await screen.findByTestId('attention-list')).not.toHaveTextContent('Plan approval');
    expect(screen.queryByTestId('attention-row-job-1:planApproval')).toBeNull();
    expect(screen.getByTestId('attention-row-job-2:prReview')).toBeInTheDocument();
    expect(dismiss).toHaveBeenCalledWith('job-1:planApproval');
    expect(window.localStorage.getItem(DISMISSED_STORAGE_KEY)).toContain('job-1:planApproval');
    expect(screen.getByTestId('attention-hidden-note')).toHaveTextContent('1 dismissed wait hidden');

    await refreshNow(ref);
    expect(screen.queryByTestId('attention-row-job-1:planApproval')).toBeNull();
    expect(screen.getByTestId('attention-row-job-2:prReview')).toBeInTheDocument();
  });

  it('brings a dismissed wait back once the server stops reporting it and the job parks again', async () => {
    const ref = createRef<AttentionBellHandle>();
    let current: AttentionItem[] = [makeItem()];
    const fetchRollup = vi.fn(async () => rollup(...current));
    mountBell([], { ref, fetchRollup });

    await screen.findByTestId('attention-badge');
    openBell();
    dismissRow('job-1:planApproval');
    await waitFor(() => expect(screen.getByTestId('attention-empty')).toBeInTheDocument());

    // Resolved: the server stops reporting the wait, so the dismissal is forgotten.
    current = [];
    await refreshNow(ref);
    expect(window.localStorage.getItem(DISMISSED_STORAGE_KEY)).toBeNull();
    expect(window.localStorage.getItem(READ_STORAGE_KEY)).toBeNull();

    // Re-parked: the same id is a new wait again, unread and listed.
    current = [makeItem()];
    await refreshNow(ref);
    expect(screen.getByTestId('attention-row-job-1:planApproval')).toBeInTheDocument();
    expect(screen.getByTestId('attention-unread-dot-job-1:planApproval')).toBeInTheDocument();
    expect(screen.getByTestId('attention-badge')).toHaveTextContent('1');
  });

  it('dismisses every listed wait at once and shows the all-clear state', async () => {
    const service = makeService();
    const dismissAll = vi.spyOn(service, 'dismissAll');
    mountBell([makeItem(), prItem()], { service });

    await screen.findByTestId('attention-badge');
    openBell();
    fireEvent.click(screen.getByTestId('attention-dismiss-all'));

    expect(await screen.findByTestId('attention-empty')).toHaveTextContent('All clear');
    expect(screen.queryByTestId('attention-badge')).toBeNull();
    expect(dismissAll).toHaveBeenCalledTimes(1);
    expect(JSON.parse(window.localStorage.getItem(DISMISSED_STORAGE_KEY) ?? '{}')).toEqual({
      'job-1:planApproval': NOW,
      'job-2:prReview': NOW
    });
  });

  it('opens the pull request for a PR-review wait through the service click-through', async () => {
    const service = makeService();
    const openItem = vi.spyOn(service, 'openItem');
    const item = prItem();
    mountBell([item], { service });

    await screen.findByTestId('attention-badge');
    openBell();
    openRow('job-2:prReview');

    expect(openItem).toHaveBeenCalledWith(item);
    expect(screen.queryByTestId('attention-panel')).toBeNull();
    expect(screen.queryByTestId('attention-badge')).toBeNull();
  });

  it('reuses the service navigation for in-app waits: selection keys plus the navigate event', async () => {
    const service = makeService();
    mountBell([makeItem()], { service });
    const focus = vi.spyOn(window, 'focus').mockImplementation(() => undefined);
    const seen = vi.fn();
    window.addEventListener(ATTENTION_NAVIGATE_EVENT, seen as EventListener);

    await screen.findByTestId('attention-badge');
    openBell();
    openRow('job-1:planApproval');

    expect(localStorage.getItem(SELECTED_JOB_KEY)).toBe('job-1');
    expect(localStorage.getItem(SELECTED_PROJECT_KEY)).toBe('proj-1');
    expect(seen).toHaveBeenCalledTimes(1);
    expect((seen.mock.calls[0][0] as CustomEvent).detail).toMatchObject({
      itemId: 'job-1:planApproval',
      jobId: 'job-1',
      projectId: 'proj-1',
      type: 'planApproval',
      prUrl: null
    });
    window.removeEventListener(ATTENTION_NAVIGATE_EVENT, seen as EventListener);
    focus.mockRestore();
  });

  it('refuses a javascript: prUrl from a ledger payload and falls back to in-app navigation', async () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    const focus = vi.spyOn(window, 'focus').mockImplementation(() => undefined);
    mountBell([prItem({ prUrl: 'javascript:alert(1)' })]);

    await screen.findByTestId('attention-badge');
    openBell();
    openRow('job-2:prReview');

    expect(open).not.toHaveBeenCalled();
    expect(localStorage.getItem(SELECTED_JOB_KEY)).toBe('job-2');
    open.mockRestore();
    focus.mockRestore();
  });

  it('merges a SignalR push in immediately, banners it once, then reconciles with the server', async () => {
    const ref = createRef<AttentionBellHandle>();
    const service = makeService();
    const announce = vi.spyOn(service, 'announce');
    const incoming = prItem({ id: 'job-3:prReview', jobId: 'job-3' });
    const fetchRollup = vi.fn(async () => rollup());
    mountBell([], { ref, service, fetchRollup });

    await waitFor(() => expect(fetchRollup).toHaveBeenCalledTimes(1));
    expect(screen.queryByTestId('attention-badge')).toBeNull();

    act(() => {
      ref.current?.pushNeeded(incoming);
    });

    expect(announce).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('attention-badge')).toHaveTextContent('1');
    openBell();
    expect(screen.getByTestId('attention-row-job-3:prReview')).toBeInTheDocument();

    // The push is a hint, not a fact: the authoritative rollup is re-read and the phantom row goes.
    await waitFor(() => expect(fetchRollup.mock.calls.length).toBeGreaterThanOrEqual(2), { timeout: 3000 });
    expect(screen.queryByTestId('attention-row-job-3:prReview')).toBeNull();
    expect(screen.getByTestId('attention-empty')).toBeInTheDocument();
  });

  it('ignores a malformed push and a resolve for an unknown job', () => {
    const ref = createRef<AttentionBellHandle>();
    const resolveJob = vi.spyOn(makeService(), 'resolveJob');
    mountBell([], { ref });

    act(() => {
      ref.current?.pushNeeded(null);
      ref.current?.pushNeeded({} as AttentionItem);
      ref.current?.pushResolved('');
    });
    expect(screen.queryByTestId('attention-badge')).toBeNull();
    expect(resolveJob).not.toHaveBeenCalled();
  });

  it('drops every row of a resolved job and forgets its read and dismiss stamps', async () => {
    const ref = createRef<AttentionBellHandle>();
    const service = makeService();
    const resolveJob = vi.spyOn(service, 'resolveJob');
    mountBell([makeItem(), prItem()], { ref, service });

    await screen.findByTestId('attention-badge');
    openBell();
    dismissRow('job-1:planApproval');
    await waitFor(() => expect(screen.queryByTestId('attention-row-job-1:planApproval')).toBeNull());

    act(() => {
      ref.current?.pushResolved('job-1');
    });

    expect(resolveJob).toHaveBeenCalledWith('job-1');
    expect(screen.queryByTestId('attention-row-job-1:planApproval')).toBeNull();
    expect(screen.getByTestId('attention-row-job-2:prReview')).toBeInTheDocument();

    // Only the resolved job's stamps are forgotten. Job 2 is still waiting and still seen, so its
    // badge must not light up again just because a different job cleared.
    expect(window.localStorage.getItem(DISMISSED_STORAGE_KEY)).toBeNull();
    expect(Object.keys(JSON.parse(window.localStorage.getItem(READ_STORAGE_KEY) ?? '{}'))).toEqual([
      'job-2:prReview'
    ]);
    expect(Object.keys(JSON.parse(window.localStorage.getItem(SEEN_STORAGE_KEY) ?? '{}'))).toEqual([
      'job-2:prReview'
    ]);
  });

  it('does not reconcile banners when the rollup read fails, and shows the server sentence', async () => {
    const service = makeService();
    const sync = vi.spyOn(service, 'sync');
    const failing = vi.fn(async () => {
      throw new Error('Failed to load pending attention: attention rollup unavailable');
    });
    mountBell([], { service, fetchRollup: failing });

    expect(await screen.findByTestId('attention-error-dot')).toBeInTheDocument();
    openBell();
    expect(await screen.findByTestId('attention-error')).toHaveTextContent('attention rollup unavailable');
    expect(screen.getByTestId('attention-empty')).toHaveTextContent('Nothing to show yet');
    expect(sync).not.toHaveBeenCalled();
  });

  it('recovers through the retry control and clears the error state', async () => {
    let fail = true;
    const fetchRollup = vi.fn(async () => {
      if (fail) throw new Error('boom');
      return rollup(makeItem());
    });
    mountBell([], { fetchRollup });

    await screen.findByTestId('attention-error-dot');
    openBell();
    fail = false;
    fireEvent.click(screen.getByTestId('attention-retry'));

    expect(await screen.findByTestId('attention-list')).toBeInTheDocument();
    expect(screen.queryByTestId('attention-error-dot')).toBeNull();
  });

  it('never reads the rollup twice at once, and still re-reads after a coalesced request', async () => {
    const ref = createRef<AttentionBellHandle>();
    let inFlight = 0;
    let peak = 0;
    const fetchRollup = vi.fn(async () => {
      inFlight += 1;
      peak = Math.max(peak, inFlight);
      await new Promise((resolve) => setTimeout(resolve, 20));
      inFlight -= 1;
      return rollup();
    });
    mountBell([], { ref, fetchRollup });
    await waitFor(() => expect(fetchRollup).toHaveBeenCalledTimes(1));

    await act(async () => {
      await Promise.all([ref.current?.refresh(), ref.current?.refresh(), ref.current?.refresh()]);
    });

    // Two reads for four requests: the batch collapses to one, plus a trailing read so a Retry click
    // that lands during an in-flight poll is not swallowed. Never two at once.
    expect(peak).toBe(1);
    await waitFor(() => expect(fetchRollup.mock.calls.length).toBeGreaterThanOrEqual(2), { timeout: 2000 });
    expect(fetchRollup.mock.calls.length).toBeLessThanOrEqual(3);
  });

  it('keeps the bell quiet when nothing is waiting', async () => {
    mountBell([]);
    await waitFor(() => expect(screen.queryByTestId('attention-error-dot')).toBeNull());
    expect(screen.queryByTestId('attention-badge')).toBeNull();
    expect(screen.queryByTestId('attention-quiet-dot')).toBeNull();
    expect(bellButton()).toHaveAccessibleName(/nothing waiting/);
  });

  it('closes on the header X, on Escape, and on a pointer down outside the panel', async () => {
    mountBell([makeItem()]);
    await screen.findByTestId('attention-badge');

    openBell();
    await screen.findByTestId('attention-panel');
    closeBell();
    expect(screen.queryByTestId('attention-panel')).toBeNull();

    openBell();
    await screen.findByTestId('attention-panel');
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByTestId('attention-panel')).toBeNull();

    openBell();
    await screen.findByTestId('attention-panel');
    fireEvent.pointerDown(document.body);
    expect(screen.queryByTestId('attention-panel')).toBeNull();
  });

  it('polls on an interval and refreshes when the tab comes back to the foreground', async () => {
    const fetchRollup = vi.fn(async () => rollup());
    mountBell([], { fetchRollup, pollIntervalMs: 50 });
    await waitFor(() => expect(fetchRollup).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(fetchRollup.mock.calls.length).toBeGreaterThanOrEqual(3), { timeout: 3000 });

    const before = fetchRollup.mock.calls.length;
    act(() => {
      window.dispatchEvent(new Event('focus'));
    });
    await waitFor(() => expect(fetchRollup.mock.calls.length).toBeGreaterThan(before), { timeout: 2000 });
  });

  it('does not poll while the tab is hidden', async () => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    const fetchRollup = vi.fn(async () => rollup());
    try {
      mountBell([], { fetchRollup, pollIntervalMs: 40 });
      await waitFor(() => expect(fetchRollup).toHaveBeenCalledTimes(1));
      await new Promise((resolve) => setTimeout(resolve, 160));
      expect(fetchRollup).toHaveBeenCalledTimes(1);
    } finally {
      delete (document as unknown as Record<string, unknown>).visibilityState;
    }
  });

  it('offers the desktop-alert prompt and asks for permission only from a click', async () => {
    const service = makeService();
    const permission = vi.spyOn(service, 'permission').mockReturnValue('default');
    const requestPermission = vi.spyOn(service, 'requestPermission').mockResolvedValue('granted');
    mountBell([makeItem()], { service });

    await screen.findByTestId('attention-badge');
    openBell();
    const prompt = screen.getByTestId('attention-enable-desktop-alerts');
    expect(requestPermission).not.toHaveBeenCalled();

    permission.mockReturnValue('granted');
    fireEvent.click(prompt);
    await waitFor(() => expect(requestPermission).toHaveBeenCalledTimes(1));
    expect(await screen.findByTestId('attention-desktop-alerts-granted')).toBeInTheDocument();
  });

  it('explains that desktop alerts need a secure context instead of showing a dead prompt', async () => {
    const service = makeService();
    vi.spyOn(service, 'permission').mockReturnValue('unsupported');
    vi.spyOn(service, 'unsupportedReason').mockReturnValue('insecure-context');
    mountBell([makeItem()], { service });

    await screen.findByTestId('attention-badge');
    openBell();
    expect(await screen.findByTestId('attention-desktop-alerts-unsupported')).toHaveTextContent(
      'Desktop alerts need HTTPS or localhost'
    );
    expect(screen.queryByTestId('attention-enable-desktop-alerts')).toBeNull();
  });

  it('mutes desktop alerts from the popover footer and shows the muted bell', async () => {
    const service = makeService();
    mountBell([makeItem()], { service });
    await screen.findByTestId('attention-badge');

    openBell();
    const toggle = await screen.findByTestId('notification-mute-toggle');
    expect(toggle).toHaveAttribute('aria-pressed', 'false');

    fireEvent.click(toggle);
    expect(window.localStorage.getItem(MUTE_STORAGE_KEY)).toBe('true');
    expect(toggle).toHaveAttribute('aria-pressed', 'true');
    expect(bellButton()).toHaveAccessibleName(/desktop alerts muted/);
  });

  it('follows a mute change made elsewhere through the service subscription', async () => {
    const service = makeService();
    render(<MuteToggle service={service} />);
    const toggle = screen.getByTestId('notification-mute-toggle');

    act(() => {
      service.setMuted(true);
    });
    await waitFor(() => expect(toggle).toHaveAttribute('aria-pressed', 'true'));
  });

  it('does not banner a wait twice when a push and a poll race for the same id', async () => {
    const ref = createRef<AttentionBellHandle>();
    const service = makeService();
    const pushed = makeItem();
    const fetchRollup = vi.fn(async () => rollup(pushed));
    mountBell([], { ref, service, fetchRollup });

    await waitFor(() => expect(fetchRollup).toHaveBeenCalledTimes(1));
    expect(window.localStorage.getItem(SEEN_STORAGE_KEY)).toContain('job-1:planApproval');

    const announce = vi.spyOn(service, 'announce');
    act(() => {
      ref.current?.pushNeeded(pushed);
    });
    expect(announce).toHaveBeenCalledTimes(1);
    expect(announce.mock.results[0]?.value).toMatchObject({ reason: 'duplicate', banner: false });
  });

  it('survives a missing localStorage and a rollup of the wrong shape', async () => {
    const fetchRollup = vi.fn(async () => ({ items: 'nope' }) as unknown as AttentionRollup);
    render(
      <AttentionBell
        service={makeService(null)}
        fetchRollup={fetchRollup}
        storage={null}
        pollIntervalMs={0}
        now={() => NOW}
      />
    );

    await waitFor(() => expect(fetchRollup).toHaveBeenCalledTimes(1));
    openBell();
    expect(await screen.findByTestId('attention-empty')).toHaveTextContent('All clear');
  });

  it('unmounts without leaving a pending reconcile timer behind', async () => {
    const ref = createRef<AttentionBellHandle>();
    const fetchRollup = vi.fn(async () => rollup());
    const view = mountBell([], { ref, fetchRollup });
    await waitFor(() => expect(fetchRollup).toHaveBeenCalledTimes(1));

    act(() => {
      ref.current?.pushNeeded(prItem());
    });
    view.unmount();

    const after = fetchRollup.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 600));
    expect(fetchRollup).toHaveBeenCalledTimes(after);
  });
});
