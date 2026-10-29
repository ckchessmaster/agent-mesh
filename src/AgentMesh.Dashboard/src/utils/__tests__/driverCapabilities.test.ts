import { describe, expect, it } from 'vitest';
import type { Agent, DriverCapabilities, SystemCapabilitiesResponse } from '../../types';
import {
  capabilitySourceLabel,
  capabilityUnavailableMessage,
  contextOptionsFor,
  driverReadingOf,
  effectiveContextSize,
  effortsFor,
  isContextSizeMeaningful,
  operatorModelsOf,
  resolveAdapterKey,
  resolveDriverCapabilities
} from '../driverCapabilities';

const BASELINE: SystemCapabilitiesResponse['agentRuntimes'] = {
  agy: {
    adapter: 'agy',
    name: 'Antigravity CLI (agy)',
    modes: [{ id: 'accept-edits', label: 'Accept Edits', description: '' }],
    models: [],
    supportsContextSelection: false,
    modelSource: 'unavailable',
    unavailableReason: 'baseline carries no models'
  },
  claude: {
    adapter: 'claude',
    name: 'Claude Code CLI',
    modes: [{ id: 'auto', label: 'Auto', description: '' }],
    models: [],
    contextSizes: [
      { id: 'default', label: 'Driver Default', description: '' },
      { id: 'auto', label: 'Auto Compact', description: '' },
      { id: '100k', label: '100k', description: '' },
      { id: '1m', label: '1M', description: '' }
    ],
    supportsContextSelection: true,
    modelSource: 'unavailable',
    unavailableReason: 'baseline carries no models'
  },
  copilot: {
    adapter: 'copilot',
    name: 'GitHub Copilot CLI',
    modes: [{ id: 'autopilot', label: 'Autopilot', description: '' }],
    models: [],
    tiers: [{ id: 'intelligence', label: 'Intelligence', description: '' }],
    contextSizes: [{ id: 'long_context', label: 'Long Context', description: '' }],
    supportsContextSelection: true,
    modelSource: 'unavailable'
  },
  openai: {
    adapter: 'openai',
    name: 'OpenAI Compatible Server',
    modes: [{ id: 'direct', label: 'Direct Chat', description: '' }],
    models: [],
    contextSizes: [
      { id: 'default', label: 'Default', description: '' },
      { id: '128k', label: '128k', description: '' },
      { id: '1m', label: '1M', description: '' }
    ],
    supportsContextSelection: true,
    modelSource: 'unavailable'
  },
  generic: {
    adapter: 'generic',
    name: 'Custom Command Agent',
    modes: [],
    models: [],
    supportsContextSelection: false,
    modelSource: 'unavailable'
  }
};

const response = (drivers?: DriverCapabilities[] | null): SystemCapabilitiesResponse => ({
  agentRuntimes: BASELINE,
  drivers,
  schemaVersion: 2
});

const agent = (overrides: Partial<Agent> = {}): Agent =>
  ({
    id: 'agy-core',
    name: 'Agy Core',
    runtimeType: 3,
    trustTier: 1,
    ...overrides
  }) as Agent;

const probedAgy: DriverCapabilities = {
  driverId: 'agy-core',
  name: 'Agy Core',
  adapter: 'agy',
  models: [
    { id: 'gemini-4-argon', name: 'Gemini 4 Argon', efforts: ['low', 'high', 'xhigh', 'max'], contextSizes: null }
  ],
  modelSource: 'probed',
  observedAt: new Date('2026-10-28T12:00:00Z').toISOString(),
  supportsContextSelection: false,
  effortSource: 'derived:agy-model-suffixes'
};

describe('resolveAdapterKey', () => {
  it('uses the recorded adapter, not a substring of the id', () => {
    // A driver named "my-agy-fork" that actually speaks OpenAI-compatible HTTP used to be handed
    // the agy picker because its id contained "agy".
    const a = agent({
      id: 'my-agy-fork',
      name: 'My agy Fork',
      runtimeType: 4,
      capabilitiesJson: JSON.stringify({ adapter: 'openai', endpoint: 'http://10.0.0.9:8000/v1' })
    });

    expect(resolveAdapterKey(a, BASELINE)).toBe('openai');
  });

  it('canonicalizes aliases', () => {
    expect(
      resolveAdapterKey(agent({ capabilitiesJson: JSON.stringify({ adapter: 'antigravity' }) }), BASELINE)
    ).toBe('agy');
    expect(
      resolveAdapterKey(agent({ capabilitiesJson: JSON.stringify({ adapter: 'github-copilot' }) }), BASELINE)
    ).toBe('copilot');
    expect(
      resolveAdapterKey(agent({ capabilitiesJson: JSON.stringify({ adapter: 'ollama' }) }), BASELINE)
    ).toBe('openai');
  });

  it('treats an endpoint as OpenAI-compatible when no adapter was recorded', () => {
    const a = agent({ id: 'private-llm', name: 'Private LLM', capabilitiesJson: JSON.stringify({ endpoint: 'http://h:8000/v1' }) });
    expect(resolveAdapterKey(a, BASELINE)).toBe('openai');
  });

  it('falls back to the runtime enum when the blob carries nothing', () => {
    expect(resolveAdapterKey(agent({ runtimeType: 3 }), BASELINE)).toBe('agy');
    expect(resolveAdapterKey(agent({ runtimeType: 4 }), BASELINE)).toBe('openai');
    expect(resolveAdapterKey(agent({ runtimeType: 2 }), BASELINE)).toBe('openai');
  });

  it('survives a malformed capabilities blob', () => {
    const a = agent({ capabilitiesJson: '{not json' });
    expect(() => resolveAdapterKey(a, BASELINE)).not.toThrow();
    expect(resolveAdapterKey(a, BASELINE)).toBe('agy');
  });

  it('returns null with no capabilities map and no adapter', () => {
    // The id must not hint at an adapter either, or this is just the name-heuristic branch.
    const a = agent({ id: 'zz-9', name: 'Mystery', capabilitiesJson: '{}', runtimeType: 99 });
    expect(resolveAdapterKey(a, undefined)).toBeNull();
  });

  it('keeps an unknown adapter when the API sent a baseline entry for it', () => {
    const runtimes = { ...BASELINE, 'my-runner': { adapter: 'my-runner', name: 'My Runner', modes: [], models: [] } };
    const a = agent({ capabilitiesJson: JSON.stringify({ adapter: 'my-runner' }) });
    expect(resolveAdapterKey(a, runtimes)).toBe('my-runner');
  });
});

describe('resolveDriverCapabilities', () => {
  it('prefers the per-driver reading over the baseline', () => {
    const resolved = resolveDriverCapabilities(response([probedAgy]), agent());

    expect(resolved?.models.map((m) => m.id)).toEqual(['gemini-4-argon']);
    expect(resolved?.source).toBe('probed');
  });

  it('never invents models when the driver could not be read', () => {
    // The regression: an unreadable driver used to fall through to a fabricated catalogue that
    // looked identical to a real answer.
    const unreadable: DriverCapabilities = {
      ...probedAgy,
      models: [],
      modelSource: 'unavailable',
      unavailableReason: 'The agy CLI returned no model list; it is probably not signed in.'
    };

    const resolved = resolveDriverCapabilities(response([unreadable]), agent());

    expect(resolved?.models).toEqual([]);
    expect(resolved?.source).toBe('unavailable');
    expect(capabilityUnavailableMessage(resolved)).toContain('not signed in');
  });

  it('reports unavailable rather than an empty picker when there is no reading at all', () => {
    const resolved = resolveDriverCapabilities(response([]), agent());

    expect(resolved?.models).toEqual([]);
    expect(resolved?.source).toBe('unavailable');
  });

  it('still supplies baseline modes when models are unavailable', () => {
    // Modes are our adapter's own concepts, so the baseline is an accurate source for them even
    // when the provider could not be read.
    const resolved = resolveDriverCapabilities(response(null), agent({ capabilitiesJson: JSON.stringify({ adapter: 'copilot' }) }));

    expect(resolved?.tiers?.map((t) => t.id)).toContain('intelligence');
    expect(resolved?.modes.map((m) => m.id)).toContain('autopilot');
  });

  it('hands out the baseline ladder as contextSizes when the driver supports selection', () => {
    const a = agent({ id: 'claude-driver', capabilitiesJson: JSON.stringify({ adapter: 'claude' }) });
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, driverId: a.id, adapter: 'claude', supportsContextSelection: true }]),
      a
    );

    expect(resolved?.contextSizes.map((c) => c.id)).toContain('100k');
  });

  it('empties contextSizes for a driver that cannot be told its window', () => {
    const resolved = resolveDriverCapabilities(response([probedAgy]), agent());

    expect(resolved?.supportsContextSelection).toBe(false);
    expect(resolved?.contextSizes).toEqual([]);
  });

  it('exposes the endpoint that was read, so an operator can see which server a list came from', () => {
    const a = agent({ id: 'openai-driver', capabilitiesJson: JSON.stringify({ adapter: 'openai', endpoint: 'http://10.0.0.9:8000/v1' }) });
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, driverId: a.id, adapter: 'openai', endpoint: 'http://10.0.0.9:8000/v1' }]),
      a
    );

    expect(resolved?.endpoint).toBe('http://10.0.0.9:8000/v1');
  });

  it('prefers modes the reading sent over the baseline', () => {
    const a = agent();
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          driverId: a.id,
          modes: [{ id: 'plan', label: 'Plan Only', description: '' }]
        }
      ]),
      a
    );

    expect(resolved?.modes.map((m) => m.id)).toEqual(['plan']);
  });

  it('matches a driver reading case-insensitively', () => {
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, driverId: 'AGY-Core' }]),
      agent({ id: 'agy-core' })
    );
    expect(resolved?.source).toBe('probed');
  });

  it('returns null with no capabilities payload', () => {
    expect(resolveDriverCapabilities(null, agent())).toBeNull();
  });

  it('treats an unknown modelSource as unavailable', () => {
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, modelSource: 'definitely-not-a-source' as DriverCapabilities['modelSource'] }]),
      agent()
    );
    expect(resolved?.source).toBe('unavailable');
  });
});

describe('contextOptionsFor', () => {
  it('returns nothing for a driver that cannot be told its window', () => {
    // agy: --help exposes no context-size flag and AgyAdapter forwards none, so the old picker
    // (128k..2m) was a control that changed nothing.
    const resolved = resolveDriverCapabilities(response([probedAgy]), agent());
    expect(contextOptionsFor(resolved)).toEqual([]);
  });

  it('uses per-model sizes when the reading has them', () => {
    const a = agent({ id: 'openai-driver', capabilitiesJson: JSON.stringify({ adapter: 'openai', endpoint: 'http://h/v1' }) });
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          driverId: a.id,
          adapter: 'openai',
          supportsContextSelection: true,
          models: [{ id: 'gpt-4o', name: 'GPT-4o', efforts: [], contextSizes: ['default', '128k'] }]
        }
      ]),
      a
    );

    // '1m' is in the baseline ladder but not in this model's list: gpt-4o resolves to a 128k
    // window, so offering 1m is the exact picker/runtime drift this change removes.
    expect(contextOptionsFor(resolved, 'gpt-4o').map((o) => o.id)).toEqual(['default', '128k']);
  });

  it('labels per-model sizes from the baseline catalogue', () => {
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          adapter: 'claude',
          supportsContextSelection: true,
          models: [{ id: 'sonnet', name: 'Sonnet', efforts: [], contextSizes: ['100k', '1m'] }]
        }
      ]),
      agent({ capabilitiesJson: JSON.stringify({ adapter: 'claude' }) })
    );

    const options = contextOptionsFor(resolved, 'sonnet');
    expect(options[0].label).toBe('100k');
    expect(options[1].label).toBe('1M');
  });

  it('synthesizes a label for a size the baseline does not describe', () => {
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          adapter: 'claude',
          supportsContextSelection: true,
          models: [{ id: 'sonnet', name: 'Sonnet', efforts: [], contextSizes: ['777k'] }]
        }
      ]),
      agent({ capabilitiesJson: JSON.stringify({ adapter: 'claude' }) })
    );

    expect(contextOptionsFor(resolved, 'sonnet')[0]).toEqual({
      id: '777k',
      label: '777k',
      description: '777k context window'
    });
  });

  it('falls back to the baseline ladder when the model reports no sizes', () => {
    const a = agent({ id: 'openai-driver', capabilitiesJson: JSON.stringify({ adapter: 'openai', endpoint: 'http://h/v1' }) });
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          driverId: a.id,
          adapter: 'openai',
          supportsContextSelection: true,
          models: [{ id: 'unknown-model', name: 'Unknown', efforts: [] }]
        }
      ]),
      a
    );

    expect(contextOptionsFor(resolved, 'unknown-model').map((o) => o.id)).toContain('128k');
  });
});

describe('isContextSizeMeaningful / effectiveContextSize', () => {
  it('rejects a saved size on a driver that cannot honour it', () => {
    // A persona created before the fix can carry defaultContextSize "500k" on agy, where nothing
    // ever forwarded it. With the control hidden the user cannot clear it, so displaying it would
    // be an unfalsifiable claim.
    const resolved = resolveDriverCapabilities(response([probedAgy]), agent());

    expect(isContextSizeMeaningful(resolved, '500k')).toBe(false);
    expect(effectiveContextSize(resolved, '500k')).toBeUndefined();
  });

  it('accepts a saved size the driver actually offers', () => {
    const a = agent({ id: 'openai-driver', capabilitiesJson: JSON.stringify({ adapter: 'openai', endpoint: 'http://h/v1' }) });
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          driverId: a.id,
          adapter: 'openai',
          supportsContextSelection: true,
          models: [{ id: 'gpt-4o', name: 'GPT-4o', efforts: [], contextSizes: ['default', '128k'] }]
        }
      ]),
      a
    );

    expect(isContextSizeMeaningful(resolved, '128k')).toBe(true);
    expect(effectiveContextSize(resolved, ' 128k ')).toBe('128k');
  });

  it('treats default/auto as not worth a badge', () => {
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, adapter: 'claude', supportsContextSelection: true }]),
      agent({ capabilitiesJson: JSON.stringify({ adapter: 'claude' }) })
    );

    expect(isContextSizeMeaningful(resolved, 'default')).toBe(false);
    expect(isContextSizeMeaningful(resolved, 'auto')).toBe(false);
  });

  it('rejects a saved size the driver no longer offers', () => {
    // A model whose window shrank, or a driver switched from claude to agy: the stale value must
    // not be sent just because it is in the payload.
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, adapter: 'claude', supportsContextSelection: true }]),
      agent({ capabilitiesJson: JSON.stringify({ adapter: 'claude' }) })
    );

    expect(isContextSizeMeaningful(resolved, '2m')).toBe(false);
  });

  it('is false for empty or missing values', () => {
    const resolved = resolveDriverCapabilities(response([probedAgy]), agent());
    expect(isContextSizeMeaningful(resolved, null)).toBe(false);
    expect(isContextSizeMeaningful(resolved, '   ')).toBe(false);
    expect(isContextSizeMeaningful(null, '128k')).toBe(false);
  });
});

describe('effortsFor', () => {
  it('returns the model efforts when present', () => {
    const resolved = resolveDriverCapabilities(response([probedAgy]), agent());
    expect(effortsFor(resolved, 'gemini-4-argon')).toEqual(['low', 'high', 'xhigh', 'max']);
  });

  it('returns nothing for a driver with no effort surface', () => {
    // The old code fell back to ['low','medium','high'] for any driver that reported nothing,
    // putting a control on copilot/openai drivers whose adapters never forward an effort.
    const a = agent({ id: 'openai-driver', capabilitiesJson: JSON.stringify({ adapter: 'openai', endpoint: 'http://h/v1' }) });
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          driverId: a.id,
          adapter: 'openai',
          models: [{ id: 'llama3.3', name: 'Llama 3.3', efforts: [] }]
        }
      ]),
      a
    );

    expect(effortsFor(resolved, 'llama3.3')).toEqual([]);
    expect(effortsFor(resolved, null)).toEqual([]);
  });

  it('unions across models when no model is selected', () => {
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          models: [
            { id: 'a', name: 'A', efforts: ['low', 'high'] },
            { id: 'b', name: 'B', efforts: ['high', 'max'] }
          ]
        }
      ]),
      agent()
    );

    expect(effortsFor(resolved, null).sort()).toEqual(['high', 'low', 'max']);
  });
});

describe('capabilitySourceLabel', () => {
  it('never presents a preset as a driver answer', () => {
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, modelSource: 'static_preset' }]),
      agent()
    );

    expect(capabilitySourceLabel(resolved)).toMatch(/preset/i);
    expect(capabilitySourceLabel(resolved)).toMatch(/not verified/i);
  });

  it('labels a probed list as read from the driver', () => {
    expect(capabilitySourceLabel(resolveDriverCapabilities(response([probedAgy]), agent()))).toMatch(
      /read from this driver/i
    );
  });

  it('labels an operator list as operator-configured', () => {
    const resolved = resolveDriverCapabilities(
      response([{ ...probedAgy, modelSource: 'user_override' }]),
      agent()
    );
    expect(capabilitySourceLabel(resolved)).toMatch(/operator/i);
  });
});

describe('driverReadingOf / operatorModelsOf', () => {
  it('returns null when the API sent no drivers array (older build)', () => {
    expect(driverReadingOf(response(undefined), agent())).toBeNull();
  });

  it('reads operator models from the blob', () => {
    const a = agent({ capabilitiesJson: JSON.stringify({ adapter: 'agy', models: ['x', ' ', 'y'] }) });
    expect(operatorModelsOf(a)).toEqual(['x', 'y']);
  });

  it('reads the object shape the driver form writes', () => {
    // The dashboard's own registration flow saves `models` as { id, name } objects taken from a
    // live /models response. Reading only strings dropped every list the dashboard itself wrote.
    const a = agent({
      capabilitiesJson: JSON.stringify({
        adapter: 'openai',
        endpoint: 'http://h/v1',
        models: [{ id: 'qwen3-coder', name: 'Qwen3 Coder' }, { name: 'no-id' }, { id: ' ' }]
      })
    });

    expect(operatorModelsOf(a)).toEqual(['qwen3-coder']);
  });

  it('falls back to the API-normalized config.models when the blob has none', () => {
    const a = agent({ capabilitiesJson: '{}', config: { models: ['from-config'] } });
    expect(operatorModelsOf(a)).toEqual(['from-config']);
  });

  it('falls back to defaultModel when there is no models array', () => {
    const a = agent({ capabilitiesJson: '{}', config: { defaultModel: ' gpt-4o ' } });
    expect(operatorModelsOf(a)).toEqual(['gpt-4o']);
  });

  it('surfaces an operator list even when the API sent no reading', () => {
    // A hand-written list is not a guess about a driver we failed to read, so it is shown - marked
    // as the operator's own rather than as a driver answer.
    const a = agent({
      capabilitiesJson: JSON.stringify({ adapter: 'agy', models: ['hand-picked'] })
    });
    const resolved = resolveDriverCapabilities(response([]), a);

    expect(resolved?.models.map((m) => m.id)).toEqual(['hand-picked']);
    expect(resolved?.source).toBe('user_override');
  });

  it('returns no operator models for a malformed blob', () => {
    expect(operatorModelsOf(agent({ capabilitiesJson: 'nope' }))).toEqual([]);
    expect(operatorModelsOf(null)).toEqual([]);
  });
});

describe('capabilityUnavailableMessage', () => {
  it('is null when there are models to show', () => {
    expect(capabilityUnavailableMessage(resolveDriverCapabilities(response([probedAgy]), agent()))).toBeNull();
  });

  it('explains why the picker is empty', () => {
    const resolved = resolveDriverCapabilities(
      response([
        {
          ...probedAgy,
          models: [],
          modelSource: 'unavailable',
          unavailableReason: 'The agy CLI is not installed on the API host.'
        }
      ]),
      agent()
    );

    expect(capabilityUnavailableMessage(resolved)).toBe('The agy CLI is not installed on the API host.');
  });
});
