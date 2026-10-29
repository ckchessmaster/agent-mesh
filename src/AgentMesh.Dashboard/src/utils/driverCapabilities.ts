import type {
  Agent,
  ContextSizeOption,
  DriverCapabilities,
  ModeOption,
  ModelCapability,
  RuntimeCapabilities,
  SystemCapabilitiesResponse,
  TierOption
} from '../types';

/**
 * Resolving what a driver can do.
 *
 * This used to be a substring match on the driver's id and name inside App.tsx: a driver named
 * "my-agy-fork" pointing at an OpenAI-compatible server was handed the agy picker, and every
 * OpenAI-compatible driver on the host was handed the same hardcoded model table regardless of
 * which server it actually talked to.
 *
 * The rule now: the driver's own recorded `adapter` and `endpoint` decide, the per-driver reading
 * the API resolved decides what models exist, and the baseline map only supplies interaction
 * concepts (modes, tiers) that our adapters implement rather than provider facts.
 */

export type CapabilitySource = 'probed' | 'static_preset' | 'user_override' | 'unavailable';

const CANONICAL_KEYS = ['agy', 'copilot', 'claude', 'openai', 'generic'] as const;

/** Adapter aliases the API canonicalizes; mirrored here so both sides agree. */
const ADAPTER_ALIASES: Record<string, string> = {
  agy: 'agy',
  antigravity: 'agy',
  claude: 'claude',
  'claude-code': 'claude',
  copilot: 'copilot',
  'github-copilot': 'copilot',
  openai: 'openai',
  ollama: 'openai',
  vllm: 'openai',
  lmstudio: 'openai',
  'lm-studio': 'openai',
  generic: 'generic',
  custom: 'generic',
  command: 'generic'
};

/**
 * The runtimeType enum values that imply a transport, matching AgentMesh.Core's RuntimeType.
 * Numeric because the API serializes the enum as an int.
 */
const RUNTIME_TYPE_NAMES: Record<number, string> = {
  1: 'generic', // McpCloud
  2: 'openai', // LocalOllama
  3: 'agy', // CliAgy
  4: 'openai' // OpenAiCompatible
};

function readAdapterField(source: unknown): string | undefined {
  if (!source || typeof source !== 'object') return undefined;
  const value = (source as Record<string, unknown>).adapter;
  return typeof value === 'string' && value.trim() ? value.trim() : undefined;
}

/**
 * The adapter a driver actually uses, from its own configuration.
 *
 * Order matters: an explicit `adapter` field is a fact the operator or CLI recorded; the runtime
 * enum is a weaker hint; a substring of the id/name is a guess and is used only as a last resort.
 * The old code did only the last one.
 */
export function resolveAdapterKey(
  agent: Agent | null | undefined,
  runtimes?: Record<string, RuntimeCapabilities>
): string | null {
  if (!agent) return null;

  const fromCapabilities = parseCapabilitiesJson(agent.capabilitiesJson);
  const explicit =
    readAdapterField(fromCapabilities) ??
    readAdapterField(agent.config) ??
    readAdapterField(fromCapabilities?.config);

  if (explicit) {
    const canonical = ADAPTER_ALIASES[explicit.toLowerCase()];
    if (canonical) return canonical;
    // An adapter we do not know is still the driver's adapter; if the API sent a baseline entry
    // for it, use that rather than falling back to agy.
    if (runtimes && runtimes[explicit.toLowerCase()]) return explicit.toLowerCase();
  }

  // An endpoint means the driver speaks OpenAI-compatible HTTP whatever its enum says. This is the
  // case the substring guess got wrong most often.
  const endpoint = readEndpoint(fromCapabilities, agent.config);
  if (endpoint) return 'openai';

  const runtimeKey = RUNTIME_TYPE_NAMES[Number(agent.runtimeType)];
  if (runtimeKey && (!runtimes || runtimes[runtimeKey])) return runtimeKey;

  // Last resort: name heuristics, kept so drivers written before `adapter` was recorded still get
  // a sensible picker instead of nothing.
  const haystack = `${agent.id ?? ''} ${agent.name ?? ''}`.toLowerCase();
  for (const key of CANONICAL_KEYS) {
    if (haystack.includes(key)) return key;
  }
  if (haystack.includes('antigravity')) return 'agy';
  if (haystack.includes('ollama') || haystack.includes('vllm') || haystack.includes('lm studio')) {
    return 'openai';
  }

  return runtimes && runtimes['agy'] ? 'agy' : null;
}

function parseCapabilitiesJson(raw?: string | null): Record<string, unknown> | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    // A malformed blob is a reason to fall back to other signals, not a reason to blank the UI.
    return null;
  }
}

function readEndpoint(
  capabilities: Record<string, unknown> | null,
  config?: Record<string, unknown> | object
): string | null {
  const sources: (Record<string, unknown> | null | undefined)[] = [
    capabilities,
    config as Record<string, unknown> | undefined,
    (capabilities?.config as Record<string, unknown> | undefined) ?? null
  ];

  for (const source of sources) {
    if (!source) continue;
    for (const key of ['endpoint', 'baseUrl', 'base_url']) {
      const value = source[key];
      if (typeof value === 'string' && value.trim()) return value.trim();
    }
  }
  return null;
}

/**
 * Operator-authored model allow-list, if any.
 *
 * Handles both shapes that write this field: the CLI's list of id strings and the dashboard's own
 * driver form, which saves objects of the shape `{ id, name }` taken from a live `/models`
 * response. Reading only one shape would silently drop the other.
 *
 * Reads the raw blob first and the API's normalized `config.models` second, so a driver whose blob
 * the API could not parse still surfaces whatever the API did understand.
 */
export function operatorModelsOf(agent: Agent | null | undefined): string[] {
  const fromBlob = flattenModelIds(parseCapabilitiesJson(agent?.capabilitiesJson)?.models);
  if (fromBlob.length > 0) return fromBlob;

  const fromConfig = flattenModelIds(agent?.config?.models);
  if (fromConfig.length > 0) return fromConfig;

  const defaultModel = agent?.config?.defaultModel;
  return defaultModel && defaultModel.trim() ? [defaultModel.trim()] : [];
}

function flattenModelIds(value: unknown): string[] {
  if (!Array.isArray(value)) return [];

  const ids: string[] = [];
  for (const entry of value) {
    if (typeof entry === 'string') {
      if (entry.trim()) ids.push(entry.trim());
      continue;
    }

    if (entry && typeof entry === 'object') {
      const id = (entry as Record<string, unknown>).id;
      if (typeof id === 'string' && id.trim()) ids.push(id.trim());
    }
  }

  return ids;
}

/**
 * The per-driver reading the API resolved for this driver, if it sent one.
 */
export function driverReadingOf(
  capabilities: SystemCapabilitiesResponse | null | undefined,
  agent: Agent | null | undefined
): DriverCapabilities | null {
  if (!capabilities?.drivers || !agent) return null;

  const id = (agent.id ?? '').toLowerCase();
  return capabilities.drivers.find((d) => d.driverId?.toLowerCase() === id) ?? null;
}

/**
 * Everything the pickers need to know about one driver, with the per-driver reading layered over
 * the baseline map.
 *
 * The field names deliberately mirror `RuntimeCapabilities` (`models`, `modes`, `tiers`,
 * `contextSizes`, `adapter`) so existing call sites keep compiling while their *contents* change
 * from "one host-wide table" to "this driver's answer".
 */
export interface ResolvedDriverCapabilities {
  adapter: string;
  name: string;
  /** The baseline entry: modes and tiers, which are adapter-level concepts. */
  baseline: RuntimeCapabilities | null;
  /** The per-driver reading, or null when the API could not resolve one. */
  reading: DriverCapabilities | null;
  models: ModelCapability[];
  modes: ModeOption[];
  tiers?: TierOption[];
  source: CapabilitySource;
  observedAt?: string;
  unavailableReason?: string;
  supportsContextSelection: boolean;
  effortSource?: string | null;
  /**
   * The context ladder to offer. Empty when the driver cannot be told its window, which is the
   * point: a selector that silently does nothing is worse than no selector.
   */
  contextSizes: ContextSizeOption[];
  /** The driver's endpoint, when it has one. Shown so an operator can see which server was read. */
  endpoint?: string | null;
  operatorModels?: string[];
}

const EMPTY_SOURCE: CapabilitySource = 'unavailable';

function normalizeSource(value?: string): CapabilitySource {
  const normalized = (value ?? '').toLowerCase();
  return normalized === 'probed' ||
    normalized === 'static_preset' ||
    normalized === 'user_override' ||
    normalized === 'unavailable'
    ? (normalized as CapabilitySource)
    : EMPTY_SOURCE;
}

/**
 * Resolves the capability surface for a driver.
 *
 * Never returns invented models. When the API could not read the driver, `models` is empty and
 * `source` is `unavailable`, which the caller renders as an explicit message rather than a picker
 * full of guesses.
 */
export function resolveDriverCapabilities(
  capabilities: SystemCapabilitiesResponse | null | undefined,
  agent: Agent | null | undefined
): ResolvedDriverCapabilities | null {
  if (!capabilities) return null;

  const adapter = resolveAdapterKey(agent, capabilities.agentRuntimes) ?? 'agy';
  const baseline = capabilities.agentRuntimes?.[adapter] ?? null;
  const reading = driverReadingOf(capabilities, agent);

  const modes = reading?.modes?.length ? reading.modes : baseline?.modes ?? [];
  const tiers = reading?.tiers?.length ? reading.tiers : baseline?.tiers;
  const ladder = baseline?.contextSizes ?? [];

  if (reading) {
    // The per-driver flag wins: it is the driver's answer, the baseline is ours.
    const supportsContextSelection = reading.supportsContextSelection === true;
    return {
      adapter: reading.adapter || adapter,
      name: reading.name || baseline?.name || adapter,
      baseline,
      reading,
      models: reading.models ?? [],
      modes,
      tiers,
      source: normalizeSource(reading.modelSource),
      observedAt: reading.observedAt,
      unavailableReason: reading.unavailableReason ?? undefined,
      supportsContextSelection,
      effortSource: reading.effortSource,
      // Hidden entirely when the driver cannot honour a window, so a stale saved value has nothing
      // to render against.
      contextSizes: supportsContextSelection ? ladder : [],
      endpoint: reading.endpoint,
      operatorModels: reading.operatorModels ?? undefined
    };
  }

  // No reading for this driver. The baseline still supplies modes and tiers, but it carries no
  // model list by design, so the honest answer on models is "unavailable" - unless the operator
  // hand-wrote a list, which is not a guess about a driver we failed to read and so is shown.
  const operatorModels = operatorModelsOf(agent);
  const fallbackSource: CapabilitySource = operatorModels.length > 0 ? 'user_override' : normalizeSource(baseline?.modelSource);

  return {
    adapter,
    name: baseline?.name ?? adapter,
    baseline,
    reading: null,
    models: operatorModels.map((id) => ({ id, name: id, efforts: [] as string[] })),
    modes,
    tiers,
    source: fallbackSource,
    observedAt: baseline?.observedAt,
    unavailableReason:
      baseline?.unavailableReason ?? 'This driver has no resolved capability reading yet.',
    supportsContextSelection: baseline?.supportsContextSelection === true,
    effortSource: baseline?.effortSource,
    contextSizes: baseline?.supportsContextSelection === true ? ladder : [],
    endpoint: null,
    operatorModels
  };
}

/**
 * Resolves capabilities for a driver referenced either by object or by id string.
 *
 * Call sites in App.tsx hold both: a selected session has an `Agent`, while the persona form's
 * driver field is free text the operator types. When the string names a registered driver we
 * resolve through that driver's own record; when it does not (a persona pointing at a driver that
 * is not installed, or a half-typed id) we fall back to treating it as an adapter name so the form
 * still renders a plausible picker instead of nothing.
 */
export function resolveCapabilitiesFor(
  capabilities: SystemCapabilitiesResponse | null | undefined,
  target: string | Agent | null | undefined,
  agents?: Agent[] | null
): ResolvedDriverCapabilities | null {
  if (!capabilities) return null;

  if (target && typeof target === 'object') {
    return resolveDriverCapabilities(capabilities, target);
  }

  if (typeof target === 'string' && target.trim()) {
    const needle = target.trim().toLowerCase();
    const match = agents?.find((a) => a.id?.toLowerCase() === needle) ?? null;
    if (match) return resolveDriverCapabilities(capabilities, match);

    // Unregistered id: honour it as an adapter key when the API sent a baseline for it, so a
    // persona typed as "copilot" gets copilot modes rather than the agy defaults.
    const canonical = ADAPTER_ALIASES[needle];
    const key = canonical ?? (capabilities.agentRuntimes?.[needle] ? needle : null);
    if (key) {
      const baseline = capabilities.agentRuntimes?.[key] ?? null;
      const ladder = baseline?.contextSizes ?? [];
      const supportsContextSelection = baseline?.supportsContextSelection === true;
      return {
        adapter: key,
        name: baseline?.name ?? key,
        baseline,
        reading: null,
        models: [],
        modes: baseline?.modes ?? [],
        tiers: baseline?.tiers,
        source: normalizeSource(baseline?.modelSource),
        observedAt: baseline?.observedAt,
        unavailableReason:
          baseline?.unavailableReason ??
          `'${target.trim()}' is not a registered driver, so its model list cannot be resolved.`,
        supportsContextSelection,
        effortSource: baseline?.effortSource,
        contextSizes: supportsContextSelection ? ladder : [],
        endpoint: null,
        operatorModels: []
      };
    }

    // Unknown, unregistered driver: say so rather than defaulting to agy, which is what the old
    // substring chain did for anything it could not classify.
    return {
      adapter: needle,
      name: target.trim(),
      baseline: null,
      reading: null,
      models: [],
      modes: [],
      tiers: undefined,
      source: 'unavailable',
      unavailableReason: `'${target.trim()}' is not a registered driver, so its capabilities cannot be resolved.`,
      supportsContextSelection: false,
      contextSizes: [],
      endpoint: null,
      operatorModels: []
    };
  }

  // Nothing selected: fall back to the host default rather than an empty form.
  return resolveDriverCapabilities(capabilities, null);
}

/**
 * The context-window options to offer for a driver (and optionally one model).
 *
 * Returns an empty list when the driver cannot be told its window. That is the whole point: a
 * selector that silently does nothing is worse than no selector, because the user believes they
 * configured something.
 *
 * A model's own `contextSizes` win over the adapter ladder: the API derives them from the same
 * table the runtime applies, so a 128k model is not offered 1m.
 */
export function contextOptionsFor(
  resolved: ResolvedDriverCapabilities | null | undefined,
  modelId?: string | null
): ContextSizeOption[] {
  if (!resolved || !resolved.supportsContextSelection) return [];

  const ladder = resolved.contextSizes ?? [];

  const model = modelId
    ? resolved.models.find((m) => m.id?.toLowerCase() === modelId.toLowerCase())
    : undefined;

  const perModel = model?.contextSizes;
  if (perModel && perModel.length > 0) {
    return perModel.map((id) => {
      const match = ladder.find((c) => c.id.toLowerCase() === String(id).toLowerCase());
      return match ?? { id: String(id), label: String(id), description: `${id} context window` };
    });
  }

  return ladder;
}

/**
 * Whether a saved context size is still meaningful for this driver.
 *
 * A persona or session created before this fix can carry `defaultContextSize: "500k"` on an agy
 * driver, where the value was never forwarded to the binary. Once the control is hidden the user
 * cannot change or clear it, so a badge showing "500k" would be an unfalsifiable claim about a
 * setting that does nothing. Callers must treat a false result as "do not display, do not send".
 */
export function isContextSizeMeaningful(
  resolved: ResolvedDriverCapabilities | null | undefined,
  savedContextSize?: string | null
): boolean {
  if (!savedContextSize || !savedContextSize.trim()) return false;
  if (!resolved || !resolved.supportsContextSelection) return false;

  const normalized = savedContextSize.trim().toLowerCase();
  // "default"/"auto" are no-ops by definition and safe to keep, but they are not worth a badge.
  if (normalized === 'default' || normalized === 'auto') return false;

  const options = contextOptionsFor(resolved);
  if (options.length === 0) return false;

  return options.some((o) => o.id.toLowerCase() === normalized);
}

/**
 * The effective context size to send for a run: the saved value only when the driver can honour
 * it, otherwise nothing at all.
 */
export function effectiveContextSize(
  resolved: ResolvedDriverCapabilities | null | undefined,
  savedContextSize?: string | null
): string | undefined {
  return isContextSizeMeaningful(resolved, savedContextSize) ? savedContextSize!.trim() : undefined;
}

/**
 * A short human label for where a model list came from, for the picker header.
 *
 * The distinction is the product requirement: a bundled preset must not read like a driver answer,
 * because the user will otherwise trust an entry the driver may reject.
 */
export function capabilitySourceLabel(resolved: ResolvedDriverCapabilities | null | undefined): string {
  if (!resolved) return '';

  switch (resolved.source) {
    case 'probed':
      return 'Read from this driver';
    case 'static_preset':
      return 'Bundled preset — not verified by driver';
    case 'user_override':
      return 'Operator-configured list';
    case 'unavailable':
    default:
      return 'Model list unavailable';
  }
}

/**
 * The message to show in place of a model picker when there is nothing honest to show.
 */
export function capabilityUnavailableMessage(
  resolved: ResolvedDriverCapabilities | null | undefined
): string | null {
  if (!resolved || resolved.models.length > 0) return null;

  if (resolved.source === 'unavailable') {
    return (
      resolved.unavailableReason ??
      'Model list unavailable (driver not signed in or unreachable).'
    );
  }

  return 'No models reported for this driver.';
}

/**
 * Effort levels to offer for a model.
 *
 * Empty when the driver has no effort surface. The old code fell back to `['low','medium','high']`
 * for any driver that reported nothing, which put a control on copilot and openai drivers whose
 * adapters never forward an effort.
 */
export function effortsFor(
  resolved: ResolvedDriverCapabilities | null | undefined,
  modelId?: string | null
): string[] {
  if (!resolved) return [];

  const model = modelId
    ? resolved.models.find((m) => m.id?.toLowerCase() === modelId.toLowerCase())
    : undefined;

  if (model?.efforts && model.efforts.length > 0) return model.efforts;

  // Union across the driver's models: efforts are per-model on agy (the CLI lists one id per
  // effort variant), so a driver-wide union is the widest honest set.
  const union = new Set<string>();
  for (const m of resolved.models) {
    for (const e of m.efforts ?? []) union.add(e);
  }
  return Array.from(union);
}
