import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem } from '../../types/attention';
import {
  ACTIVE_TAB_KEY,
  ATTENTION_NAVIGATE_EVENT,
  MUTE_STORAGE_KEY,
  NOTIFICATION_ICON,
  SELECTED_JOB_KEY,
  SELECTED_PROJECT_KEY,
  SEEN_STORAGE_KEY,
  NotificationService,
  safeExternalUrl
} from '../notifications';
import type { AttentionNavigateDetail } from '../notifications';

/* ------------------------------------------------------------------ *
 * Browser-API doubles. jsdom ships neither Notification nor AudioContext,
 * so the service reads both off `globalThis` at call time and each test
 * installs exactly the one it is about to exercise.
 * ------------------------------------------------------------------ */

class MockNotification {
  static permission = 'default';
  static requestPermission: ((callback?: (p: string) => void) => unknown) | undefined = undefined;
  static instances: MockNotification[] = [];

  title: string;
  options: NotificationOptions;
  onclick: (() => void) | null = null;
  closed = false;

  constructor(title: string, options?: NotificationOptions) {
    this.title = title;
    this.options = options ?? {};
    MockNotification.instances.push(this);
  }

  close(): void {
    this.closed = true;
  }
}

interface OscStub {
  type: string;
  frequency: { setValueAtTime: ReturnType<typeof vi.fn> };
  connect: ReturnType<typeof vi.fn>;
  start: ReturnType<typeof vi.fn>;
  stop: ReturnType<typeof vi.fn>;
}

interface GainStub extends OscStub {
  gain: {
    setValueAtTime: ReturnType<typeof vi.fn>;
    linearRampToValueAtTime: ReturnType<typeof vi.fn>;
    exponentialRampToValueAtTime: ReturnType<typeof vi.fn>;
  };
}

class MockAudioContext {
  static instances: MockAudioContext[] = [];
  state = 'running';
  currentTime = 0;
  destination = { kind: 'destination' };
  oscillators: OscStub[] = [];
  gains: GainStub[] = [];
  resume = vi.fn(async () => {
    this.state = 'running';
  });

  constructor() {
    MockAudioContext.instances.push(this);
  }

  createOscillator(): OscStub {
    const node: OscStub = {
      type: '',
      frequency: { setValueAtTime: vi.fn() },
      connect: vi.fn(),
      start: vi.fn(),
      stop: vi.fn()
    };
    this.oscillators.push(node);
    return node;
  }

  createGain(): GainStub {
    const node: GainStub = {
      type: '',
      frequency: { setValueAtTime: vi.fn() },
      connect: vi.fn(),
      start: vi.fn(),
      stop: vi.fn(),
      gain: {
        setValueAtTime: vi.fn(),
        linearRampToValueAtTime: vi.fn(),
        exponentialRampToValueAtTime: vi.fn()
      }
    };
    this.gains.push(node);
    return node;
  }
}

const item = (overrides: Partial<AttentionItem> = {}): AttentionItem => ({
  id: '11111111-1111-1111-1111-111111111111:planApproval',
  type: 'planApproval',
  title: 'Plan approval needed',
  detail: 'Approve the 6-task checklist for the notifications job',
  jobId: '11111111-1111-1111-1111-111111111111',
  jobTitle: 'Add desktop notifications',
  projectId: '22222222-2222-2222-2222-222222222222',
  projectName: 'Agent Mesh',
  cardId: null,
  prUrl: null,
  prNumber: null,
  since: '2026-11-18T10:00:00Z',
  ...overrides
});

let clock = 1_700_000_000_000;
const now = () => clock;

const advance = (ms: number) => {
  clock += ms;
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('notification service: capability & permission', () => {
  beforeEach(() => {
    clock = 1_700_000_000_000;
    MockNotification.instances = [];
    MockNotification.permission = 'default';
    MockNotification.requestPermission = undefined;
    MockAudioContext.instances = [];
    vi.stubGlobal('Notification', MockNotification);
    vi.stubGlobal('isSecureContext', true);
  });

  it('reports the notification API as missing when the constructor is absent', () => {
    vi.stubGlobal('Notification', undefined);
    const service = new NotificationService();

    expect(service.isSupported()).toBe(false);
    expect(service.unsupportedReason()).toBe('no-notification-api');
    expect(service.permission()).toBe('unsupported');
    expect(service.announce(item()).reason).toBe('unsupported');
  });

  it('names insecure context as the reason, because that is the case an operator can fix', () => {
    vi.stubGlobal('isSecureContext', false);
    const service = new NotificationService();

    expect(service.unsupportedReason()).toBe('insecure-context');
    expect(service.isSupported()).toBe(false);
  });

  it('passes the three browser permission states through unchanged', () => {
    for (const state of ['default', 'granted', 'denied'] as const) {
      MockNotification.permission = state;
      expect(new NotificationService().permission()).toBe(state);
    }
  });

  it('resolves the promise form of requestPermission and notifies subscribers', async () => {
    MockNotification.permission = 'granted';
    MockNotification.requestPermission = () => Promise.resolve('granted');
    const service = new NotificationService();
    const listener = vi.fn();
    service.subscribe(listener);

    await expect(service.requestPermission()).resolves.toBe('granted');
    expect(listener).toHaveBeenCalled();
  });

  it('supports the legacy callback form of requestPermission', async () => {
    MockNotification.requestPermission = (callback?: (p: string) => void) => {
      callback?.('denied');
      return undefined;
    };
    const service = new NotificationService();

    await expect(service.requestPermission()).resolves.toBe('denied');
  });

  it('degrades to the current state instead of rejecting when the prompt throws', async () => {
    MockNotification.permission = 'denied';
    MockNotification.requestPermission = () => {
      throw new Error('user gesture required');
    };
    const service = new NotificationService();

    await expect(service.requestPermission()).resolves.toBe('denied');
  });
});

describe('notification service: OS banner, tag dedupe and seen-set', () => {
  beforeEach(() => {
    clock = 1_700_000_000_000;
    MockNotification.instances = [];
    MockNotification.permission = 'granted';
    MockNotification.requestPermission = undefined;
    MockAudioContext.instances = [];
    vi.stubGlobal('Notification', MockNotification);
    vi.stubGlobal('isSecureContext', true);
  });

  it('shows a tagged banner carrying the job, project and detail', () => {
    const service = new NotificationService();

    const delivery = service.announce(item());

    expect(delivery).toEqual({
      itemId: item().id,
      banner: true,
      sound: false,
      reason: 'raised'
    });
    expect(MockNotification.instances).toHaveLength(1);
    const banner = MockNotification.instances[0];
    expect(banner.title).toBe('Plan approval needed');
    expect(banner.options.tag).toBe(item().id);
    expect(banner.options.icon).toBe(NOTIFICATION_ICON);
    expect(banner.options.silent).toBe(true);
    expect(banner.options.body).toContain('Add desktop notifications');
    expect(banner.options.body).toContain('Agent Mesh');
    expect(banner.options.body).toContain('Approve the 6-task checklist');
  });

  it('announces a wait at most once, even across a reload', () => {
    const first = new NotificationService();
    expect(first.announce(item()).reason).toBe('raised');
    expect(first.announce(item()).reason).toBe('duplicate');
    expect(MockNotification.instances).toHaveLength(1);

    const afterReload = new NotificationService();
    expect(afterReload.announce(item()).reason).toBe('duplicate');
    expect(MockNotification.instances).toHaveLength(1);
    expect(localStorage.getItem(SEEN_STORAGE_KEY)).toContain(item().id);
  });

  it('truncates a long detail so the banner stays readable', () => {
    const service = new NotificationService();
    service.announce(item({ detail: 'x'.repeat(500) }));

    const body = MockNotification.instances[0].options.body ?? '';
    expect(body).toContain(`${'x'.repeat(239)}…`);
    expect(body).not.toContain('x'.repeat(240));
    // The job and project context survive the cut, because that is what tells the operator where to go.
    expect(body).toContain('Add desktop notifications');
    expect(body).toContain('Agent Mesh');
  });

  it('records the announcement timestamp for the bell to compute "new since X"', () => {
    const service = new NotificationService({ now });
    service.announce(item());

    expect(service.firstSeenAt(item().id)).toBe(clock);
    expect(service.seenIds()).toEqual([item().id]);
    expect(service.hasSeen('nope:escalation')).toBe(false);
  });

  it('refuses to banner without permission but still records the wait as seen', () => {
    MockNotification.permission = 'denied';
    const service = new NotificationService();

    expect(service.announce(item()).reason).toBe('permission-denied');
    expect(MockNotification.instances).toHaveLength(0);
    expect(service.hasSeen(item().id)).toBe(true);
  });

  it('survives a Notification constructor that throws, without retrying forever', () => {
    const throwing = class {
      static permission = 'granted';
      constructor() {
        throw new TypeError('Illegal constructor');
      }
    };
    vi.stubGlobal('Notification', throwing);
    const service = new NotificationService();

    expect(service.announce(item()).reason).toBe('error');
    expect(service.announce(item()).reason).toBe('duplicate');
  });

  it('closes the banner for a wait the rollup no longer reports, and re-notifies if it returns', () => {
    const service = new NotificationService();
    const blocked = item();
    service.sync([blocked]);
    const banner = MockNotification.instances[0];

    const resolved = service.sync([]);
    expect(resolved.resolvedIds).toEqual([blocked.id]);
    expect(banner.closed).toBe(true);
    expect(service.hasSeen(blocked.id)).toBe(false);

    service.sync([blocked]);
    expect(MockNotification.instances).toHaveLength(2);
  });

  it('suppresses items already in the seen-set instead of re-bannering on every poll', () => {
    const service = new NotificationService();
    const blocked = item();
    service.sync([blocked]);

    const second = service.sync([blocked]);
    expect(second.raised).toEqual([]);
    expect(second.suppressed).toHaveLength(1);
    expect(MockNotification.instances).toHaveLength(1);
  });

  it('ages an announcement out after the TTL and re-notifies a wait still open', () => {
    const service = new NotificationService({ now });
    const blocked = item();
    service.sync([blocked]);
    expect(JSON.parse(localStorage.getItem(SEEN_STORAGE_KEY) ?? '{}')).toHaveProperty(blocked.id);

    advance(25 * 60 * 60 * 1000);
    // Same rollup, one day later: the entry is pruned, so the still-blocked job nudges again.
    expect(service.sync([blocked]).suppressed).toHaveLength(1);
    expect(JSON.parse(localStorage.getItem(SEEN_STORAGE_KEY) ?? '{}')).toEqual({});
    expect(service.sync([blocked]).raised).toHaveLength(1);
  });

  it('caps persisted ids by evicting the oldest announcement', () => {
    const service = new NotificationService({ now, seenMaxEntries: 2 });
    service.announce(item({ id: 'job-a:planApproval' }));
    advance(1);
    service.announce(item({ id: 'job-b:planApproval' }));
    advance(1);
    service.announce(item({ id: 'job-c:planApproval' }));

    expect(service.seenIds().sort()).toEqual(['job-b:planApproval', 'job-c:planApproval']);
  });

  it('repairs a corrupt seen-set key instead of throwing on every poll', () => {
    localStorage.setItem(SEEN_STORAGE_KEY, '{not json');
    const service = new NotificationService();

    expect(service.seenIds()).toEqual([]);
    expect(service.announce(item()).reason).toBe('raised');
    expect(() => JSON.parse(localStorage.getItem(SEEN_STORAGE_KEY) ?? '')).not.toThrow();
  });

  it('keeps working when storage is unavailable', () => {
    const service = new NotificationService({ storage: null });

    expect(service.announce(item()).reason).toBe('raised');
    expect(service.announce(item()).reason).toBe('duplicate');
  });
});

describe('notification service: master mute', () => {
  beforeEach(() => {
    clock = 1_700_000_000_000;
    MockNotification.instances = [];
    MockAudioContext.instances = [];
    MockNotification.permission = 'granted';
    vi.stubGlobal('Notification', MockNotification);
    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('AudioContext', MockAudioContext);
  });

  it('suppresses banner and chime while muting the whole session backlog', () => {
    const service = new NotificationService();
    service.setMuted(true);

    expect(service.isMuted()).toBe(true);
    expect(service.announce(item()).reason).toBe('muted');
    expect(service.playChime()).toBe(false);
    expect(MockNotification.instances).toHaveLength(0);
    expect(MockAudioContext.instances).toHaveLength(0);
    expect(service.hasSeen(item().id)).toBe(true);
  });

  it('does not replay the backlog when the operator unmutes', () => {
    const service = new NotificationService();
    service.setMuted(true);
    service.sync([item()]);
    service.setMuted(false);

    const after = service.sync([item()]);
    expect(after.raised).toEqual([]);
    expect(after.muted).toBe(false);
    expect(MockNotification.instances).toHaveLength(0);
  });

  it('persists the toggle and reports it through sync', () => {
    const service = new NotificationService();
    expect(service.toggleMuted()).toBe(true);
    expect(localStorage.getItem(MUTE_STORAGE_KEY)).toBe('true');

    expect(new NotificationService().isMuted()).toBe(true);
    expect(service.toggleMuted()).toBe(false);
    expect(localStorage.getItem(MUTE_STORAGE_KEY)).toBeNull();
  });

  it('marks muted sync results and closes banners for waits that resolved while muted', () => {
    const service = new NotificationService();
    service.setMuted(true);
    const result = service.sync([item()]);

    expect(result.muted).toBe(true);
    expect(result.suppressed).toHaveLength(1);
    expect(service.sync([]).resolvedIds).toHaveLength(1);
  });
});

describe('notification service: WebAudio chime', () => {
  beforeEach(() => {
    clock = 1_700_000_000_000;
    MockNotification.instances = [];
    MockAudioContext.instances = [];
    MockNotification.permission = 'granted';
    vi.stubGlobal('Notification', MockNotification);
    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('AudioContext', MockAudioContext);
  });

  it('schedules a two-note envelope on one shared context', () => {
    const service = new NotificationService();

    expect(service.playChime()).toBe(true);
    expect(MockAudioContext.instances).toHaveLength(1);
    const ctx = MockAudioContext.instances[0];
    expect(ctx.oscillators).toHaveLength(2);
    expect(ctx.oscillators[0].frequency.setValueAtTime).toHaveBeenCalledWith(880, 0);
    expect(ctx.oscillators[1].frequency.setValueAtTime).toHaveBeenCalledWith(1318.5, 0.18);
    expect(ctx.oscillators[0].connect).toHaveBeenCalledWith(ctx.gains[0]);
    expect(ctx.gains[0].connect).toHaveBeenCalledWith(ctx.destination);
    expect(ctx.gains[0].gain.exponentialRampToValueAtTime).toHaveBeenCalled();
    expect(ctx.oscillators[0].start).toHaveBeenCalledWith(0);
    expect(ctx.oscillators[0].stop).toHaveBeenCalledWith(0.18);
  });

  it('reuses the same AudioContext for later chimes instead of leaking one per notification', () => {
    const service = new NotificationService();
    service.playChime();
    service.playChime();

    expect(MockAudioContext.instances).toHaveLength(1);
    expect(MockAudioContext.instances[0].oscillators).toHaveLength(4);
  });

  it('is silent when the browser offers no WebAudio, but the banner still shows', () => {
    vi.stubGlobal('AudioContext', undefined);
    vi.stubGlobal('webkitAudioContext', undefined);
    const service = new NotificationService();

    const delivery = service.announce(item());
    expect(delivery.banner).toBe(true);
    expect(delivery.sound).toBe(false);
  });

  it('does not pretend to play before a user gesture unlocks the context', () => {
    vi.stubGlobal('AudioContext', class extends MockAudioContext {
      constructor() {
        super();
        this.state = 'suspended';
      }
    });
    const service = new NotificationService();

    expect(service.audioUnlocked()).toBe(false);
    expect(service.playChime()).toBe(false);
    expect(MockAudioContext.instances[0].resume).not.toHaveBeenCalled();
    expect(MockAudioContext.instances[0].oscillators).toHaveLength(0);
  });

  it('resumes a suspended context once a gesture has been observed', () => {
    vi.stubGlobal('AudioContext', class extends MockAudioContext {
      constructor() {
        super();
        this.state = 'suspended';
      }
    });
    const service = new NotificationService();

    service.noteUserGesture();
    expect(service.audioUnlocked()).toBe(true);
    expect(service.playChime()).toBe(true);
    expect(MockAudioContext.instances[0].resume).toHaveBeenCalled();
    expect(MockAudioContext.instances[0].oscillators).toHaveLength(2);
  });

  it('retries the autoplay unlock on the next gesture after a refused resume', async () => {
    const refused = vi.fn(async () => {
      throw new Error('play() failed because the document does not have user activation');
    });
    vi.stubGlobal('AudioContext', class extends MockAudioContext {
      constructor() {
        super();
        this.state = 'suspended';
        this.resume = refused;
      }
    });
    const service = new NotificationService();

    service.noteUserGesture();
    expect(service.playChime()).toBe(true);
    await Promise.resolve();
    expect(refused).toHaveBeenCalled();
    expect(service.playChime()).toBe(true);
  });

  it('never throws when the audio engine itself fails', () => {
    vi.stubGlobal('AudioContext', class {
      constructor() {
        throw new Error('no audio device');
      }
    });
    const service = new NotificationService();

    expect(service.playChime()).toBe(false);
  });
});

describe('notification service: click-through', () => {
  beforeEach(() => {
    clock = 1_700_000_000_000;
    MockNotification.instances = [];
    MockNotification.permission = 'granted';
    vi.stubGlobal('Notification', MockNotification);
    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('AudioContext', undefined);
    vi.stubGlobal('webkitAudioContext', undefined);
  });

  it('opens a pull-request wait in a new tab without touching cockpit selection', () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    const service = new NotificationService();

    const detail = service.openItem(
      item({ type: 'prReview', id: 'job-9:prReview', prUrl: 'https://github.com/o/r/pull/9' })
    );

    expect(open).toHaveBeenCalledWith('https://github.com/o/r/pull/9', '_blank', 'noopener,noreferrer');
    expect(detail.prUrl).toBe('https://github.com/o/r/pull/9');
    expect(localStorage.getItem(SELECTED_JOB_KEY)).toBeNull();
  });

  it('selects the job in-app for a wait that lives inside the cockpit', () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    const focus = vi.spyOn(window, 'focus').mockImplementation(() => undefined);
    const received: AttentionNavigateDetail[] = [];
    const listener = (event: Event) => {
      received.push((event as CustomEvent<AttentionNavigateDetail>).detail);
    };
    window.addEventListener(ATTENTION_NAVIGATE_EVENT, listener);
    const service = new NotificationService();
    const blocked = item();

    const detail = service.openItem(blocked);

    expect(open).not.toHaveBeenCalled();
    expect(localStorage.getItem(ACTIVE_TAB_KEY)).toBe('projects');
    expect(localStorage.getItem(SELECTED_JOB_KEY)).toBe(blocked.jobId);
    expect(localStorage.getItem(SELECTED_PROJECT_KEY)).toBe(blocked.projectId);
    expect(detail).toEqual({
      itemId: blocked.id,
      jobId: blocked.jobId,
      projectId: blocked.projectId,
      type: 'planApproval',
      prUrl: null
    });
    expect(received).toHaveLength(1);
    expect(focus).toHaveBeenCalled();
    window.removeEventListener(ATTENTION_NAVIGATE_EVENT, listener);
  });

  it('clicking a banner follows the wait and withdraws the banner', () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    vi.spyOn(window, 'focus').mockImplementation(() => undefined);
    const service = new NotificationService();
    const review = item({ id: 'job-9:prReview', jobId: 'job-9', type: 'prReview', prUrl: 'https://github.com/o/r/pull/42' });
    service.announce(review);
    const banner = MockNotification.instances[0];

    expect(typeof banner.onclick).toBe('function');
    (banner.onclick as () => void)();

    expect(open).toHaveBeenCalledWith('https://github.com/o/r/pull/42', '_blank', 'noopener,noreferrer');
    expect(banner.closed).toBe(true);
    // Withdrawing the banner must not forget the wait, or the next poll banners it again.
    expect(service.seenIds()).toEqual([review.id]);
  });

  it('refuses a non-http target from the ledger and falls back to in-app navigation', () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    vi.spyOn(window, 'focus').mockImplementation(() => undefined);
    const service = new NotificationService();

    const detail = service.openItem(item({ prUrl: 'javascript:alert(1)' }));

    expect(detail.prUrl).toBeNull();
    expect(open).not.toHaveBeenCalled();
    expect(localStorage.getItem(SELECTED_JOB_KEY)).toBe('11111111-1111-1111-1111-111111111111');
  });

  it('classifies only absolute http(s) as openable', () => {
    expect(safeExternalUrl('https://github.com/o/r/pull/1')).toBe('https://github.com/o/r/pull/1');
    expect(safeExternalUrl('  https://example.com/x  ')).toBe('https://example.com/x');
    expect(safeExternalUrl('javascript:alert(1)')).toBeNull();
    expect(safeExternalUrl('data:text/html;base64,PHNjcmlwdD4=')).toBeNull();
    expect(safeExternalUrl('github.com/o/r/pull/1')).toBeNull();
    expect(safeExternalUrl('')).toBeNull();
    expect(safeExternalUrl(undefined)).toBeNull();
  });
});

describe('notification service: lifecycle plumbing', () => {
  beforeEach(() => {
    clock = 1_700_000_000_000;
    MockNotification.instances = [];
    MockNotification.permission = 'granted';
    vi.stubGlobal('Notification', MockNotification);
    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('AudioContext', undefined);
    vi.stubGlobal('webkitAudioContext', undefined);
  });

  it('clears every entry for one job when the server reports it resolved', () => {
    const service = new NotificationService();
    const mine = item({ id: 'job-a:planApproval' });
    const other = item({ id: 'job-b:escalation', jobId: 'job-b' });
    service.sync([mine, other]);

    const removed = service.handleResolved({ jobId: 'job-a' });

    expect(removed).toEqual(['job-a:planApproval']);
    expect(MockNotification.instances[0].closed).toBe(true);
    expect(MockNotification.instances[1].closed).toBe(false);
    expect(service.seenIds()).toEqual(['job-b:escalation']);
  });

  it('ignores a resolved event with no job id', () => {
    const service = new NotificationService();
    service.sync([item()]);

    expect(service.handleResolved(null)).toEqual([]);
    expect(service.seenIds()).toHaveLength(1);
  });

  it('dismisses a banner without forgetting it, so the same wait cannot re-banner', () => {
    const service = new NotificationService();
    const blocked = item();
    service.sync([blocked]);
    const banner = MockNotification.instances[0];

    service.dismiss(blocked.id);
    expect(banner.closed).toBe(true);

    service.sync([blocked]);
    expect(MockNotification.instances).toHaveLength(1);
  });

  it('dismissAll withdraws every open banner and leaves the seen-set intact', () => {
    const service = new NotificationService();
    service.sync([item({ id: 'job-a:planApproval' }), item({ id: 'job-b:escalation', jobId: 'job-b' })]);

    service.dismissAll();

    expect(MockNotification.instances.every((b) => b.closed)).toBe(true);
    expect(service.seenIds()).toHaveLength(2);
  });

  it('notifies subscribers on state change and stops after unsubscribe', () => {
    const service = new NotificationService();
    const listener = vi.fn();
    const unsubscribe = service.subscribe(listener);

    service.announce(item());
    expect(listener).toHaveBeenCalledTimes(1);
    unsubscribe();
    service.announce(item({ id: 'job-z:escalation', jobId: 'job-z' }));
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('does not let one throwing subscriber break delivery', () => {
    const service = new NotificationService();
    service.subscribe(() => {
      throw new Error('bad subscriber');
    });

    expect(service.announce(item()).reason).toBe('raised');
    expect(MockNotification.instances).toHaveLength(1);
  });

  it('resetSeen forgets history so a still-open wait announces again', () => {
    const service = new NotificationService();
    service.sync([item()]);
    service.resetSeen();

    service.sync([item()]);
    expect(MockNotification.instances).toHaveLength(2);
  });
});
