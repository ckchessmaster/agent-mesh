import { describe, expect, it } from 'vitest';
import type { AgentSession } from '../../types';
import {
  SESSION_PREVIEW_LIMIT,
  collapseSessions,
  decorateSessions,
  formatRelativeTime,
  isJobTurnSession,
  isPlaceholderTitle,
  mergeSessionsById,
  parseSessionMetadata,
  resolveHiddenCount,
  sortSessionsByRecency,
  stripJobTurnNoise,
  truncateLabel
} from '../sessionDisplay';

const makeSession = (overrides: Partial<AgentSession> = {}): AgentSession => ({
  id: overrides.id ?? 'sess-1',
  title: overrides.title ?? 'Some session',
  status: overrides.status ?? 'active',
  metadataJson: overrides.metadataJson ?? '{}',
  createdAt: overrides.createdAt ?? new Date('2026-10-27T10:00:00Z').toISOString(),
  updatedAt: overrides.updatedAt ?? new Date('2026-10-27T10:00:00Z').toISOString(),
  ...overrides
});

describe('parseSessionMetadata', () => {
  it('reads camelCase keys written by the API', () => {
    const meta = parseSessionMetadata(
      JSON.stringify({ jobId: 'job-1', projectName: 'Agent Mesh', personaName: 'Rook' })
    );
    expect(meta).toMatchObject({ jobId: 'job-1', projectName: 'Agent Mesh', personaName: 'Rook' });
  });

  it('reads PascalCase keys written by older producers', () => {
    const meta = parseSessionMetadata(JSON.stringify({ JobId: 'job-2', ProjectName: 'Mesh' }));
    expect(meta).toMatchObject({ jobId: 'job-2', projectName: 'Mesh' });
  });

  it('degrades to empty metadata instead of throwing on malformed payloads', () => {
    expect(parseSessionMetadata(undefined)).toEqual({});
    expect(parseSessionMetadata('')).toEqual({});
    expect(parseSessionMetadata('not json')).toEqual({});
    expect(parseSessionMetadata('[]')).toEqual({});
    expect(parseSessionMetadata('"a string"')).toEqual({});
  });
});

describe('stripJobTurnNoise', () => {
  it('removes the Job Turn wrapper and the persona tag', () => {
    expect(stripJobTurnNoise('Job Turn: Misc. Fixes (@Rook)')).toBe('Misc. Fixes');
    expect(stripJobTurnNoise('Job Turn: Private AI Support over tailscale (@Astra)')).toBe(
      'Private AI Support over tailscale'
    );
  });

  it('keeps legitimate trailing parentheticals that are not persona tags', () => {
    expect(stripJobTurnNoise('WIP (do not merge)')).toBe('WIP (do not merge)');
  });

  it('handles empty input', () => {
    expect(stripJobTurnNoise('')).toBe('');
    expect(stripJobTurnNoise(null)).toBe('');
  });
});

describe('isJobTurnSession', () => {
  it('detects job turns from metadata', () => {
    expect(isJobTurnSession(makeSession({ metadataJson: JSON.stringify({ jobId: 'j1' }) }))).toBe(true);
  });

  it('detects job turns from the title when metadata is missing the job id', () => {
    expect(isJobTurnSession(makeSession({ title: 'Job Turn: Misc. Fixes (@Rook)' }))).toBe(true);
  });

  it('returns false for ordinary chat sessions', () => {
    expect(isJobTurnSession(makeSession({ title: 'Hello' }))).toBe(false);
  });
});

describe('isPlaceholderTitle', () => {
  it('flags blank and fallback titles', () => {
    for (const title of ['', '   ', 'Session', 'New Session', 'Untitled', 'n/a', 'null', undefined]) {
      expect(isPlaceholderTitle(title as string | undefined)).toBe(true);
    }
  });

  it('flags a bare "Job Turn" that carries no job title', () => {
    expect(isPlaceholderTitle('Job Turn')).toBe(true);
  });

  it('accepts real titles', () => {
    expect(isPlaceholderTitle('Audit OIDC Handshake')).toBe(false);
    expect(isPlaceholderTitle('Job Turn: Misc. Fixes (@Rook)')).toBe(false);
  });
});

describe('truncateLabel', () => {
  it('leaves short labels alone and clips long ones with an ellipsis', () => {
    expect(truncateLabel('short', 10)).toBe('short');
    expect(truncateLabel('abcdefghij', 5)).toBe('abcd…');
    expect(truncateLabel('abcdefghij', 10)).toBe('abcdefghij');
  });
});

describe('formatRelativeTime', () => {
  const now = Date.now();
  const ago = (ms: number) => new Date(now - ms).toISOString();

  it('renders compact buckets', () => {
    expect(formatRelativeTime(ago(0))).toBe('now');
    expect(formatRelativeTime(ago(5 * 60_000))).toBe('5m');
    expect(formatRelativeTime(ago(3 * 3_600_000))).toBe('3h');
    expect(formatRelativeTime(ago(2 * 86_400_000))).toBe('2d');
  });

  it('handles empty and invalid input', () => {
    expect(formatRelativeTime(null)).toBe('');
    expect(formatRelativeTime('not-a-date')).toBe('');
  });
});

describe('decorateSessions', () => {
  it('numbers turns per job oldest-first and labels them with the job title', () => {
    const sessions = [
      makeSession({
        id: 'newer',
        title: 'Job Turn: Misc. Fixes (@Rook)',
        metadataJson: JSON.stringify({ jobId: 'job-1', projectName: 'Agent Mesh' }),
        createdAt: new Date('2026-10-27T12:00:00Z').toISOString()
      }),
      makeSession({
        id: 'older',
        title: 'Job Turn: Misc. Fixes (@Rook)',
        metadataJson: JSON.stringify({ jobId: 'job-1' }),
        createdAt: new Date('2026-10-27T09:00:00Z').toISOString()
      })
    ];

    const byId = Object.fromEntries(decorateSessions(sessions).map((d) => [d.session.id, d]));

    expect(byId.older.turnNumber).toBe(1);
    expect(byId.newer.turnNumber).toBe(2);
    expect(byId.newer.label).toBe('#2 Misc. Fixes');
    expect(byId.newer.fullTitle).toBe('Misc. Fixes — turn 2 of 2');
    expect(byId.newer.contextLabel).toBe('Agent Mesh');
  });

  it('numbers each job independently', () => {
    const sessions = [
      makeSession({ id: 'a', title: 'Job Turn: A (@R)', metadataJson: JSON.stringify({ jobId: 'ja' }) }),
      makeSession({ id: 'b', title: 'Job Turn: B (@R)', metadataJson: JSON.stringify({ jobId: 'jb' }) })
    ];
    const byId = Object.fromEntries(decorateSessions(sessions).map((d) => [d.session.id, d]));
    expect(byId.a.turnNumber).toBe(1);
    expect(byId.b.turnNumber).toBe(1);
  });

  it('falls back to the persona name for untitled chat sessions', () => {
    const [item] = decorateSessions([
      makeSession({ title: 'Session', metadataJson: JSON.stringify({ personaName: 'Rook' }) })
    ]);
    expect(item.label).toBe('Rook');
    expect(item.isJobTurn).toBe(false);
  });

  it('preserves real chat titles verbatim', () => {
    const [item] = decorateSessions([makeSession({ title: 'Audit OIDC Handshake' })]);
    expect(item.label).toBe('Audit OIDC Handshake');
    expect(item.turnNumber).toBeNull();
  });
});

describe('collapseSessions', () => {
  const jobTurns = (count: number) =>
    decorateSessions(
      Array.from({ length: count }, (_, i) =>
        makeSession({
          id: `s${i}`,
          title: `Job Turn: Misc. Fixes (@Rook) #${i}`,
          metadataJson: JSON.stringify({ jobId: 'job-1' }),
          createdAt: new Date(Date.now() - i * 60_000).toISOString()
        })
      )
    );

  it('returns everything when within the limit', () => {
    const items = jobTurns(3);
    const { visible, hiddenCount } = collapseSessions(items);
    expect(visible).toHaveLength(3);
    expect(hiddenCount).toBe(0);
  });

  it('caps the list at the preview limit and reports how many were hidden', () => {
    const items = jobTurns(18);
    const { visible, hiddenCount } = collapseSessions(items);
    expect(visible).toHaveLength(SESSION_PREVIEW_LIMIT);
    expect(hiddenCount).toBe(18 - SESSION_PREVIEW_LIMIT);
  });

  it('keeps the selected session visible even when it falls outside the preview window', () => {
    const items = jobTurns(18);
    const last = items[items.length - 1];
    const { visible } = collapseSessions(items, SESSION_PREVIEW_LIMIT, last.session.id);
    expect(visible.map((v) => v.session.id)).toContain(last.session.id);
    expect(visible).toHaveLength(SESSION_PREVIEW_LIMIT);
  });

  it('is a no-op when the limit is disabled', () => {
    const items = jobTurns(18);
    const { visible, hiddenCount } = collapseSessions(items, 0);
    expect(visible).toHaveLength(18);
    expect(hiddenCount).toBe(0);
  });

  it('does not duplicate the selected row when it is already inside the preview window', () => {
    const items = jobTurns(18);
    const { visible } = collapseSessions(items, SESSION_PREVIEW_LIMIT, items[1].session.id);
    expect(visible.map((v) => v.session.id)).toEqual(items.slice(0, SESSION_PREVIEW_LIMIT).map((i) => i.session.id));
  });
});

describe('sortSessionsByRecency', () => {
  it('orders newest-first by updatedAt', () => {
    const sorted = sortSessionsByRecency([
      makeSession({ id: 'old', updatedAt: new Date('2026-01-01T00:00:00Z').toISOString() }),
      makeSession({ id: 'new', updatedAt: new Date('2026-06-01T00:00:00Z').toISOString() })
    ]);
    expect(sorted.map((s) => s.id)).toEqual(['new', 'old']);
  });

  it('does not mutate the caller array', () => {
    const input = [makeSession({ id: 'a' }), makeSession({ id: 'b' })];
    sortSessionsByRecency(input);
    expect(input.map((s) => s.id)).toEqual(['a', 'b']);
  });
});

describe('mergeSessionsById', () => {
  it('prefers the incoming copy when both lists hold the same session', () => {
    const merged = mergeSessionsById(
      [makeSession({ id: 'a', title: 'stale', updatedAt: new Date('2026-01-02T00:00:00Z').toISOString() })],
      [makeSession({ id: 'a', title: 'fresh', updatedAt: new Date('2026-01-02T00:00:00Z').toISOString() })]
    );
    expect(merged).toHaveLength(1);
    expect(merged[0].title).toBe('fresh');
  });

  it('unions disjoint pages and keeps them newest-first', () => {
    const merged = mergeSessionsById(
      [makeSession({ id: 'mid', updatedAt: new Date('2026-01-02T00:00:00Z').toISOString() })],
      [
        makeSession({ id: 'new', updatedAt: new Date('2026-01-03T00:00:00Z').toISOString() }),
        makeSession({ id: 'old', updatedAt: new Date('2026-01-01T00:00:00Z').toISOString() })
      ]
    );
    expect(merged.map((s) => s.id)).toEqual(['new', 'mid', 'old']);
  });
});

describe('resolveHiddenCount', () => {
  it('uses the server total when it exceeds what the client happens to hold', () => {
    // 12 on the server, only 8 inside the capped global window, 5 previewed => 7 hidden.
    expect(resolveHiddenCount(8, 5, 12)).toBe(7);
  });

  it('falls back to the loaded count when the total header is missing', () => {
    expect(resolveHiddenCount(8, 5, null)).toBe(3);
    expect(resolveHiddenCount(8, 5)).toBe(3);
  });

  it('never reports hidden rows for a list that fits inside the preview', () => {
    expect(resolveHiddenCount(3, 5, 3)).toBe(0);
  });

  it('still offers expansion when the server holds more than the client loaded', () => {
    expect(resolveHiddenCount(3, 5, 12)).toBe(9);
  });

  it('clamps a stale total that under-reports the rows already on screen', () => {
    expect(resolveHiddenCount(9, 5, 4)).toBe(4);
  });

  it('returns zero when collapsing is disabled', () => {
    expect(resolveHiddenCount(30, 0, 30)).toBe(0);
  });
});
