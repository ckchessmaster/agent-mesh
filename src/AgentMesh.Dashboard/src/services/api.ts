import type {
  Agent,
  AgentSession,
  AgentUsageResponse,
  DriverConfig,
  DriverSidecarConfig,
  JobMessage,
  JobTask,
  AssignJobTaskResult,
  Persona,
  PersonaEnvironmentStatus,
  PersonaDriverVerificationResult,
  PersonaAuthStartResult,
  PersonaAuthSubmitResult,
  Project,
  ProjectJob,
  ProjectDocument,
  ProjectPersonaSummary,
  QueuedPromptItem,
  SessionAttachment,
  SessionMessage,
  SessionPage,
  SessionArtifact,
  JobArtifact,
  JobChangeSet,
  JobChangedFile,
  JobChangeCommit,
  JobChangeSourceInfo,
  JobChangeStats,
  JobChangePatch,
  JobChangePatchResult,
  StagedJob,
  SubmitAnswerRequest,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from '../types';
import { JobFileChangeStatus } from '../types';

let tokenProvider: (() => string | null) | null = null;
let unauthorizedHandler: (() => void | Promise<void>) | null = null;

export const setAuthTokenProvider = (provider: (() => string | null) | null) => {
  tokenProvider = provider;
};

export const setUnauthorizedHandler = (handler: (() => void | Promise<void>) | null) => {
  unauthorizedHandler = handler;
};

const apiFetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const res = await fetch(input, init);
  if (res.status === 401 && unauthorizedHandler) {
    try {
      void unauthorizedHandler();
    } catch (e) {
      console.error('Error in unauthorizedHandler:', e);
    }
  }
  return res;
};

/**
 * Reads the owner-scoped session total the API echoes in `X-Total-Count`. Returns null when the
 * header is absent or unparseable (older API build, or a proxy that strips it) so callers can fall
 * back to whatever rows they already hold instead of showing a bogus count.
 */
const readTotalCount = (res: Response): number | null => {
  const raw = res.headers.get('X-Total-Count') ?? res.headers.get('x-total-count');
  if (!raw) return null;
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
};

/**
 * Pulls the server's own sentence out of an error response, falling back to `fallback` plus the status
 * text. The API's job endpoints report faults as `{ error: "…" }`; the change endpoints do the same and
 * add a `reasonCode`, which is why the patch route has its own handling instead of this helper.
 */
const readApiErrorMessage = async (res: Response, fallback: string): Promise<string> => {
  let detail = res.statusText || String(res.status);
  try {
    const body = await res.json();
    const message = body?.error || body?.Error || body?.message || body?.Message;
    if (typeof message === 'string' && message.trim()) detail = message.trim();
  } catch {
    // A non-JSON error body (a proxy's HTML error page) leaves the status text in place.
  }
  return `${fallback}: ${detail}`;
};

/**
 * Normalizes one project document from the API onto the `ProjectDocument` shape.
 *
 * The camelCase-or-PascalCase fallbacks are the convention every attachment mapper in this file already
 * follows (`uploadJobAttachments`, `uploadTaskAttachments`), and they are load-bearing rather than
 * defensive theatre: the API serializes camelCase, but a proxied or older build that leaves the naming
 * policy unset would emit PascalCase, and a modal that read only one of the two would render an empty
 * library with no error — the user would conclude their upload vanished.
 *
 * `downloadUrl` is rebuilt from the id when the response omits it, for the same reason the job-attachment
 * mapper does: without a URL the row cannot be opened, so a missing field should degrade to a derived one
 * rather than to a dead row.
 */
/**
 * Normalizes one job artifact from the API onto the `JobArtifact` shape.
 *
 * Same camelCase-or-PascalCase tolerance as `mapProjectDocument` and the attachment mappers, for the same
 * reason: the API writes camelCase, a proxied or older build may not, and a panel that read only one form
 * would render an empty list with no error.
 *
 * `downloadUrl` is rebuilt from the job id and artifact id when the response omits it. Without a URL the
 * row cannot be opened, so a missing field degrades to a derived one rather than to a dead row — and
 * unlike a session artifact, a job artifact's URL is fully derivable from the two ids the panel already
 * has, because the server resolves the bytes itself.
 */
const mapJobArtifact = (item: any): JobArtifact => {
  const id = item?.id || item?.Id || '';
  const jobId = item?.jobId || item?.JobId || '';
  const name = item?.name || item?.Name || '';
  const downloadUrl = item?.downloadUrl || item?.DownloadUrl
    || (jobId && id
      ? `/api/project-jobs/${encodeURIComponent(jobId)}/artifacts/${encodeURIComponent(id)}/content`
      : '');

  return {
    id,
    jobId,
    sourceSessionId: item?.sourceSessionId || item?.SourceSessionId || null,
    name,
    summary: item?.summary || item?.Summary || undefined,
    contentType: item?.contentType || item?.ContentType || 'text/plain',
    sizeBytes: typeof item?.sizeBytes === 'number'
      ? item.sizeBytes
      : (typeof item?.SizeBytes === 'number' ? item.SizeBytes : 0),
    createdAt: item?.createdAt || item?.CreatedAt || new Date().toISOString(),
    updatedAt: item?.updatedAt || item?.UpdatedAt || new Date().toISOString(),
    content: item?.content ?? item?.Content ?? undefined,
    s3Key: item?.s3Key || item?.S3Key || null,
    downloadUrl
  };
};

const mapProjectDocument = (item: any): ProjectDocument => {  const id = item?.id || item?.Id || '';  const projectId = item?.projectId || item?.ProjectId || '';
  const fileName = item?.fileName || item?.FileName || null;
  const downloadUrl = item?.downloadUrl || item?.DownloadUrl
    || (id && projectId
      ? `/api/projects/${encodeURIComponent(projectId)}/documents/${encodeURIComponent(id)}/content`
      : '');

  return {
    id,
    projectId,
    title: item?.title || item?.Title || fileName || 'Untitled document',
    kind: typeof item?.kind === 'number' ? item.kind : (typeof item?.Kind === 'number' ? item.Kind : 0),
    fileName,
    contentType: item?.contentType || item?.ContentType || null,
    sizeBytes: typeof item?.sizeBytes === 'number'
      ? item.sizeBytes
      : (typeof item?.SizeBytes === 'number' ? item.SizeBytes : 0),
    s3Key: item?.s3Key || item?.S3Key || null,
    downloadUrl,
    forceDownloadUrl: item?.forceDownloadUrl || item?.ForceDownloadUrl
      || (downloadUrl ? `${downloadUrl}?download=true` : ''),
    createdAt: item?.createdAt || item?.CreatedAt || new Date().toISOString(),
    updatedAt: item?.updatedAt || item?.UpdatedAt || new Date().toISOString(),
    content: item?.content ?? item?.Content ?? null
  };
};

/**
 * The same camelCase-or-PascalCase tolerance as `mapJobArtifact`, applied to the change-set payload.
 *
 * These mappers are defensive about *shape*, not about presence: the server always sends `stats`,
 * `files`, `source` and `commits`, but the numbers arrive as nullable ints (`additions: null` means
 * "not countable" for a binary file, which is a different fact from `0`), and coercing a null to zero
 * here would put a false line count on screen that no later layer could recover. So the mappers keep
 * nulls as nulls, default *lists* to empty rather than undefined so callers never need a `?? []`, and
 * read both spellings of every property.
 */
const readInt = (item: any, camel: string, pascal: string): number | null => {
  const value = item?.[camel] ?? item?.[pascal];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
};

const readBool = (item: any, camel: string, pascal: string): boolean => {
  const value = item?.[camel] ?? item?.[pascal];
  return value === true;
};

const readStr = (item: any, camel: string, pascal: string): string | null => {
  const value = item?.[camel] ?? item?.[pascal];
  return typeof value === 'string' && value.length > 0 ? value : null;
};

const readStrList = (item: any, camel: string, pascal: string): string[] | null => {
  const value = item?.[camel] ?? item?.[pascal];
  return Array.isArray(value) ? value.filter((v) => typeof v === 'string') : null;
};

const mapJobChangeSource = (item: any): JobChangeSourceInfo => ({
  kind: item?.kind ?? item?.Kind ?? null,
  reasonCode: readStr(item, 'reasonCode', 'ReasonCode'),
  message: readStr(item, 'message', 'Message'),
  degraded: readBool(item, 'degraded', 'Degraded'),
  committedOnly: readBool(item, 'committedOnly', 'CommittedOnly'),
  truncated: readBool(item, 'truncated', 'Truncated'),
  repository: readStr(item, 'repository', 'Repository'),
});

const mapJobChangeStats = (item: any): JobChangeStats => ({
  filesChanged: readInt(item, 'filesChanged', 'FilesChanged') ?? 0,
  additions: readInt(item, 'additions', 'Additions') ?? 0,
  deletions: readInt(item, 'deletions', 'Deletions') ?? 0,
  omittedFiles: readInt(item, 'omittedFiles', 'OmittedFiles') ?? 0,
});

const mapJobChangedFile = (item: any): JobChangedFile => {
  const additions = readInt(item, 'additions', 'Additions');
  const deletions = readInt(item, 'deletions', 'Deletions');
  const binary = readBool(item, 'binary', 'Binary');

  return {
    path: readStr(item, 'path', 'Path') ?? '',
    status: readStr(item, 'status', 'Status') ?? JobFileChangeStatus.Modified,
    previousPath: readStr(item, 'previousPath', 'PreviousPath'),
    // Binary on purpose nulls the counts: git reports `-` in those columns and the server says so with
    // a null. Rendering `+0 -0` for a PNG claims the file changed by zero lines.
    additions: binary ? null : additions,
    deletions: binary ? null : deletions,
    binary,
    // Defaults to *false* when the field is missing entirely (an older API build): a row that offers a
    // patch and 404s on every click is worse than a row that shows counts without a diff link.
    patchAvailable: item?.patchAvailable ?? item?.PatchAvailable ?? false,
  };
};

const mapJobChangeCommit = (item: any): JobChangeCommit => {
  const sha = readStr(item, 'sha', 'Sha') ?? '';
  return {
    sha,
    shortSha: readStr(item, 'shortSha', 'ShortSha') ?? sha.slice(0, 7),
    subject: readStr(item, 'subject', 'Subject') ?? '',
    authorName: readStr(item, 'authorName', 'AuthorName'),
    authorEmail: readStr(item, 'authorEmail', 'AuthorEmail'),
    committedAt: readStr(item, 'committedAt', 'CommittedAt'),
  };
};

const mapJobChangeSet = (raw: any): JobChangeSet => {
  // Bound to locals before use. The mappers below read both spellings of a property off a value that
  // may be undefined, and `(item?.files ?? item?.Files).map(...)` chains a `.map` onto a `??` that can
  // still short-circuit to undefined when the outer `Array.isArray` is checking a different expression
  // than the one it dereferences — the linter is right that the safe form is to decide once, locally.
  const item = raw ?? {};
  const rawFiles = item.files ?? item.Files;
  const rawCommits = item.commits ?? item.Commits;
  const files: any[] = Array.isArray(rawFiles) ? rawFiles : [];
  const commits: any[] = Array.isArray(rawCommits) ? rawCommits : [];
  const stats = mapJobChangeStats(item.stats ?? item.Stats);
  const rows = Array.isArray(item.uncommittedPaths ?? item.UncommittedPaths)
    ? readStrList(item, 'uncommittedPaths', 'UncommittedPaths')
    : null;

  return {
    jobId: readStr(item, 'jobId', 'JobId') ?? '',
    branch: readStr(item, 'branch', 'Branch'),
    baseBranch: readStr(item, 'baseBranch', 'BaseBranch'),
    headSha: readStr(item, 'headSha', 'HeadSha'),
    baseSha: readStr(item, 'baseSha', 'BaseSha'),
    // A missing `stats` block still has to agree with the rows on screen, so a zero total is raised to
    // `files.length` rather than left at zero: "0 files changed" above a list of twelve rows is the
    // self-contradicting panel this feature exists to prevent.
    stats: stats.filesChanged === 0 && files.length > 0
      ? { ...stats, filesChanged: files.length }
      : stats,
    files: files.map(mapJobChangedFile),
    source: mapJobChangeSource(item.source ?? item.Source),
    commits: commits.map(mapJobChangeCommit),
    hasUncommittedChanges: readBool(item, 'hasUncommittedChanges', 'HasUncommittedChanges'),
    uncommittedPaths: rows,
    uncommittedCount: readInt(item, 'uncommittedCount', 'UncommittedCount'),
    generatedAt: readStr(item, 'generatedAt', 'GeneratedAt'),
    notes: readStrList(item, 'notes', 'Notes'),
  };
};

const mapJobChangePatch = (item: any): JobChangePatch => ({
  jobId: readStr(item, 'jobId', 'JobId') ?? '',
  branch: readStr(item, 'branch', 'Branch'),
  baseBranch: readStr(item, 'baseBranch', 'BaseBranch'),
  headSha: readStr(item, 'headSha', 'HeadSha'),
  baseSha: readStr(item, 'baseSha', 'BaseSha'),
  patch: typeof item?.patch === 'string' ? item.patch : (typeof item?.Patch === 'string' ? item.Patch : ''),
  path: readStr(item, 'path', 'Path'),
  previousPath: readStr(item, 'previousPath', 'PreviousPath'),
  status: readStr(item, 'status', 'Status'),
  binary: readBool(item, 'binary', 'Binary'),
  empty: readBool(item, 'empty', 'Empty'),
  truncated: readBool(item, 'truncated', 'Truncated'),
  additions: readInt(item, 'additions', 'Additions'),
  deletions: readInt(item, 'deletions', 'Deletions'),
  contextLines: readInt(item, 'contextLines', 'ContextLines') ?? 3,
  source: item?.source || item?.Source ? mapJobChangeSource(item?.source ?? item?.Source) : null,
});

const getAuthHeadersOnly = (): Record<string, string> => {
  const token = tokenProvider?.();
  if (token) {
    return {
      'Authorization': `Bearer ${token}`
    };
  }

  return {
    'Authorization': 'Bearer mesh-default-secret-key',
    'X-Agent-Id': 'agy-core'
  };
};

const getHeaders = (): Record<string, string> => ({
  'Content-Type': 'application/json',
  ...getAuthHeadersOnly()
});

export interface SessionControlOptions {
  model?: string;
  effort?: string;
  mode?: string;
  tier?: string;
  contextSize?: string;
  attachments?: SessionAttachment[];
}

export const api = {
  async verifyAuth(): Promise<{ authenticated: boolean; email?: string; name?: string; agentId?: string; trustTier?: string }> {
    const res = await apiFetch('/api/auth/verify', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Auth verification failed: ${res.statusText}`);
    return res.json();
  },

  async getStatus(): Promise<SystemStatusResponse> {
    const res = await apiFetch('/api/status', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load system status: ${res.statusText}`);
    return res.json();
  },

  async getCapabilities(): Promise<SystemCapabilitiesResponse> {
    const res = await apiFetch('/api/system/capabilities', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load system capabilities: ${res.statusText}`);
    return res.json();
  },

  async testOpenAiServer(req: { endpoint: string; apiKey?: string; sidecar?: DriverSidecarConfig | null; agentId?: string }): Promise<{ success: boolean; error?: string; models: { id: string; name: string }[] }> {
    const res = await apiFetch('/api/system/drivers/test-openai', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to test OpenAI server: ${res.statusText}`);
    return res.json();
  },

  async getAgents(): Promise<Agent[]> {
    const res = await apiFetch('/api/agents', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load agents: ${res.statusText}`);
    return res.json();
  },

  async getDrivers(): Promise<Agent[]> {
    const res = await apiFetch('/api/drivers', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load drivers: ${res.statusText}`);
    return res.json();
  },

  async getPersonas(driverId?: string): Promise<Persona[]> {
    const query = driverId ? `?driverId=${encodeURIComponent(driverId)}` : '';
    const res = await apiFetch(`/api/personas${query}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load personas: ${res.statusText}`);
    return res.json();
  },

  async getPersona(id: string): Promise<Persona> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(id)}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load persona: ${res.statusText}`);
    return res.json();
  },

  async createPersona(req: {
    id?: string;
    name: string;
    driverId: string;
    role?: string;
    personaPrompt?: string;
    defaultModel?: string;
    defaultEffort?: string;
    defaultMode?: string;
    defaultTier?: string;
    defaultContextSize?: string;
    maxConcurrentJobs?: number;
    trustTier?: number;
    gitHubToken?: string;
    gitAuthorName?: string;
    gitAuthorEmail?: string;
  }): Promise<Persona> {
    const res = await apiFetch('/api/personas', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to create persona: ${res.statusText}`);
    return res.json();
  },

  async updatePersona(
    id: string,
    req: {
      name?: string;
      role?: string;
      personaPrompt?: string;
      driverId?: string;
      defaultModel?: string;
      defaultEffort?: string;
      defaultMode?: string;
      defaultTier?: string;
      defaultContextSize?: string;
      maxConcurrentJobs?: number;
      trustTier?: number;
      gitHubToken?: string;
      gitAuthorName?: string;
      gitAuthorEmail?: string;
    }
  ): Promise<Persona> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to update persona: ${res.statusText}`);
    return res.json();
  },

  async deletePersona(id: string): Promise<{ status: string; id: string; deletedSessionsCount: number }> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to delete persona: ${res.statusText}`);
    return res.json();
  },

  /**
   * Owner-scoped session page plus the server's exact total for that owner. The global
   * `GET /api/sessions` window is capped, so this is the only way the sidebar can truthfully say
   * "Show all (N)" and actually reach the older rows. `total` is null when the header is missing
   * (older API, or a proxy that strips it) and callers must then fall back to what they hold.
   *
   * Omitting `take` returns the owner's entire history, which is what card expansion asks for.
   */
  async getPersonaSessionsPage(personaId: string, take?: number, skip?: number): Promise<SessionPage> {
    const params = new URLSearchParams();
    if (take && take > 0) params.append('take', String(take));
    if (skip && skip > 0) params.append('skip', String(skip));
    const query = params.toString() ? `?${params.toString()}` : '';
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/sessions${query}`, {
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to load persona sessions: ${res.statusText}`);
    return { items: await res.json(), total: readTotalCount(res) };
  },

  async createPersonaSession(
    personaId: string,
    title: string,
    initialPrompt?: string,
    options?: SessionControlOptions
  ): Promise<AgentSession> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/sessions`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({
        title,
        initialPrompt,
        model: options?.model,
        effort: options?.effort,
        mode: options?.mode,
        tier: options?.tier,
        contextSize: options?.contextSize,
        attachments: options?.attachments
      })
    });
    if (!res.ok) throw new Error(`Failed to create persona session: ${res.statusText}`);
    return res.json();
  },

  async getPersonaEnvironment(personaId: string): Promise<PersonaEnvironmentStatus> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load persona environment status: ${res.statusText}`);
    return res.json();
  },

  async wakePersonaEnvironment(personaId: string): Promise<PersonaEnvironmentStatus> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/wake`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to wake persona environment: ${res.statusText}`);
    return res.json();
  },

  async stopPersonaEnvironment(personaId: string): Promise<PersonaEnvironmentStatus> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/stop`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to stop persona environment: ${res.statusText}`);
    return res.json();
  },

  async restartPersonaEnvironment(personaId: string): Promise<PersonaEnvironmentStatus> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/restart`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to restart persona environment: ${res.statusText}`);
    return res.json();
  },

  async getPersonaEnvironmentLogs(personaId: string, tail = 100): Promise<{ logs: string }> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/logs?tail=${tail}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load persona environment logs: ${res.statusText}`);
    return res.json();
  },

  async verifyPersonaDriver(personaId: string): Promise<PersonaDriverVerificationResult> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/verify`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to verify persona driver: ${res.statusText}`);
    return res.json();
  },

  async startPersonaAuth(personaId: string): Promise<PersonaAuthStartResult> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/auth/start`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to initiate driver login: ${res.statusText}`);
    return res.json();
  },

  async submitPersonaAuthCode(personaId: string, code: string): Promise<PersonaAuthSubmitResult> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/auth/submit`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ code })
    });
    if (!res.ok) throw new Error(`Failed to submit driver auth code: ${res.statusText}`);
    return res.json();
  },

  async configurePersonaApiKey(personaId: string, apiKey: string, provider?: string): Promise<PersonaAuthSubmitResult> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/environment/auth/apikey`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ apiKey, provider })
    });
    if (!res.ok) throw new Error(`Failed to configure API key: ${res.statusText}`);
    return res.json();
  },

  async verifyDriver(driverId: string): Promise<PersonaDriverVerificationResult> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(driverId)}/verify`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to verify driver: ${res.statusText}`);
    return res.json();
  },

  async startDriverAuth(driverId: string): Promise<PersonaAuthStartResult> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(driverId)}/auth/start`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to initiate driver login: ${res.statusText}`);
    return res.json();
  },

  async submitDriverAuthCode(driverId: string, code: string): Promise<PersonaAuthSubmitResult> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(driverId)}/auth/submit`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ code })
    });
    if (!res.ok) throw new Error(`Failed to submit driver auth code: ${res.statusText}`);
    return res.json();
  },

  async configureDriverApiKey(driverId: string, apiKey: string, provider?: string): Promise<PersonaAuthSubmitResult> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(driverId)}/auth/apikey`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ apiKey, provider })
    });
    if (!res.ok) throw new Error(`Failed to configure driver API key: ${res.statusText}`);
    return res.json();
  },

  async getAgentUsage(agentId: string, refresh = false): Promise<AgentUsageResponse> {
    const url = `/api/agents/${encodeURIComponent(agentId)}/usage${refresh ? '?refresh=true' : ''}`;
    const res = await apiFetch(url, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load agent usage: ${res.statusText}`);
    return res.json();
  },

  async registerAgent(req: {
    id: string;
    name: string;
    role?: string;
    runtimeType?: number;
    trustTier?: number;
    personaPrompt?: string;
    memorySummary?: string;
    capabilitiesJson?: string;
  }): Promise<{ agent: Agent; token: string; instructions: string }> {
    const res = await apiFetch('/api/agents/register', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to register agent: ${res.statusText}`);
    return res.json();
  },

  async upsertAgent(req: {
    id: string;
    name: string;
    role?: string;
    status?: string;
    capabilitiesJson?: string;
  }): Promise<Agent> {
    const res = await apiFetch('/api/agents', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to register/update agent: ${res.statusText}`);
    return res.json();
  },

  async getDriverConfig(agentId: string): Promise<DriverConfig> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(agentId)}/config`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load driver config: ${res.statusText}`);
    return res.json();
  },

  async deleteAgent(id: string): Promise<void> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) {
      let errMsg = res.statusText;
      try {
        const b = await res.json();
        if (b?.error) errMsg = b.error;
      } catch {}
      throw new Error(errMsg);
    }
  },

  /**
   * Patch-style update. Send only changed fields — the server merges, so omitted fields keep
   * their stored values. An empty apiKey means "leave the stored key alone".
   */
  async updateDriverConfig(
    agentId: string,
    req: {
      adapter?: string;
      endpoint?: string;
      apiKey?: string;
      defaultModel?: string;
      timeoutSeconds?: number;
      maxRetries?: number;
      retryDelayMilliseconds?: number;
      maxSteps?: number;
      capabilities?: string[];
      sidecar?: DriverSidecarConfig;
      loginCommand?: string;
      authCheckCommand?: string;
    }
  ): Promise<DriverConfig> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(agentId)}/config`, {
      method: 'PUT',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to update driver config: ${res.statusText}`);
    return res.json();
  },

  async restartDriverRunners(driverId: string): Promise<{ driverId: string; restarted: number; runners: PersonaEnvironmentStatus[] }> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(driverId)}/restart-runners`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) {
      let errMsg = `Failed to restart driver runners: ${res.statusText}`;
      try {
        const b = await res.json();
        if (b?.error) errMsg = b.error;
      } catch {}
      throw new Error(errMsg);
    }
    return res.json();
  },

  async getJobs(approvalStatus?: string): Promise<StagedJob[]> {
    const query = approvalStatus ? `?approvalStatus=${encodeURIComponent(approvalStatus)}` : '';
    const res = await apiFetch(`/api/jobs${query}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load jobs: ${res.statusText}`);
    return res.json();
  },

  async submitApproval(jobId: string, approved: boolean, reason?: string): Promise<void> {
    const res = await apiFetch(`/api/jobs/${jobId}/approval`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ approved, reason: reason || (approved ? 'Approved via Web Cockpit' : 'Rejected via Web Cockpit') })
    });
    if (!res.ok) throw new Error(`Failed to submit approval: ${res.statusText}`);
  },

  async getSessions(status?: string, creatorAgentId?: string): Promise<AgentSession[]> {
    const params = new URLSearchParams();
    if (status && status !== 'all') params.append('status', status);
    if (creatorAgentId) params.append('creatorAgentId', creatorAgentId);
    const query = params.toString() ? `?${params.toString()}` : '';
    const res = await apiFetch(`/api/sessions${query}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load sessions: ${res.statusText}`);
    return res.json();
  },

  /** Driver-card counterpart of `getPersonaSessionsPage`. See that method for the contract. */
  async getAgentSessionsPage(agentId: string, take?: number, skip?: number): Promise<SessionPage> {
    const params = new URLSearchParams();
    if (take && take > 0) params.append('take', String(take));
    if (skip && skip > 0) params.append('skip', String(skip));
    const query = params.toString() ? `?${params.toString()}` : '';
    const res = await apiFetch(`/api/agents/${encodeURIComponent(agentId)}/sessions${query}`, {
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to load agent sessions: ${res.statusText}`);
    return { items: await res.json(), total: readTotalCount(res) };
  },

  async createAgentSession(
    agentId: string,
    title: string,
    initialPrompt?: string,
    options?: SessionControlOptions
  ): Promise<AgentSession> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(agentId)}/sessions`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({
        title,
        initialPrompt,
        model: options?.model,
        effort: options?.effort,
        mode: options?.mode,
        tier: options?.tier,
        contextSize: options?.contextSize,
        attachments: options?.attachments
      })
    });
    if (!res.ok) throw new Error(`Failed to create agent session: ${res.statusText}`);
    return res.json();
  },

  async getSession(id: string, includeMessages: boolean = true): Promise<AgentSession> {
    const query = includeMessages ? '' : '?includeMessages=false';
    const res = await apiFetch(`/api/sessions/${id}${query}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load session details: ${res.statusText}`);
    return res.json();
  },

  async deleteSession(id: string): Promise<{ status: string; id: string; personaId?: string; driverId?: string }> {
    const res = await apiFetch(`/api/sessions/${id}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to delete session: ${res.statusText}`);
    return res.json();
  },

  async createSession(
    title: string,
    metadataJson?: string,
    initialMessage?: string,
    creatorAgentId = 'agy-core',
    options?: SessionControlOptions
  ): Promise<AgentSession> {
    const res = await apiFetch('/api/sessions', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({
        title,
        metadataJson: metadataJson || '{}',
        initialMessage,
        creatorAgentId,
        model: options?.model,
        effort: options?.effort,
        mode: options?.mode,
        tier: options?.tier,
        contextSize: options?.contextSize,
        attachments: options?.attachments
      })
    });
    if (!res.ok) throw new Error(`Failed to create session: ${res.statusText}`);
    return res.json();
  },

  async updateSessionStatus(id: string, status: string): Promise<AgentSession> {
    const res = await apiFetch(`/api/sessions/${id}`, {
      method: 'PATCH',
      headers: getHeaders(),
      body: JSON.stringify({ status })
    });
    if (!res.ok) throw new Error(`Failed to update session: ${res.statusText}`);
    return res.json();
  },

  async updateSessionControls(
    id: string,
    params: {
      model?: string;
      effort?: string;
      tier?: string;
      contextSize?: string;
      mode?: string;
    }
  ): Promise<AgentSession> {
    const res = await apiFetch(`/api/sessions/${id}`, {
      method: 'PATCH',
      headers: getHeaders(),
      body: JSON.stringify(params)
    });
    if (!res.ok) throw new Error(`Failed to update session controls: ${res.statusText}`);
    return res.json();
  },

  async getSessionMessages(sessionId: string, limit?: number): Promise<SessionMessage[]> {
    const url = limit ? `/api/sessions/${sessionId}/events?limit=${limit}` : `/api/sessions/${sessionId}/events`;
    const res = await apiFetch(url, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load session events: ${res.statusText}`);
    return res.json();
  },

  async uploadSessionAttachments(sessionId: string, files: File[]): Promise<SessionAttachment[]> {
    const formData = new FormData();
    files.forEach((f) => formData.append('files', f, f.name));
    const res = await apiFetch(`/api/sessions/${sessionId}/attachments`, {
      method: 'POST',
      headers: getAuthHeadersOnly(),
      body: formData
    });
    if (!res.ok) {
      let errMsg = res.statusText;
      try {
        const errBody = await res.json();
        if (errBody?.error) errMsg = errBody.error;
      } catch {
        // Ignore JSON parse error
      }
      throw new Error(`Failed to upload attachments: ${errMsg}`);
    }
    const raw = await res.json();
    return (Array.isArray(raw) ? raw : []).map((item: any) => {
      const id = item.id || item.Id || '';
      const fileName = item.fileName || item.FileName || item.name || item.Name || '';
      const targetId = id || fileName;
      return {
        id,
        sessionId: item.sessionId || item.SessionId || sessionId,
        fileName,
        contentType: item.contentType || item.ContentType || 'application/octet-stream',
        sizeBytes: typeof item.sizeBytes === 'number'
          ? item.sizeBytes
          : typeof item.SizeBytes === 'number'
          ? item.SizeBytes
          : 0,
        s3Key: item.s3Key || item.S3Key || '',
        downloadUrl: item.downloadUrl || item.DownloadUrl || (targetId ? `/api/sessions/${sessionId}/attachments/${encodeURIComponent(targetId)}/content` : ''),
        createdAt: item.createdAt || item.CreatedAt || new Date().toISOString()
      };
    });
  },

  async postSessionMessage(
    sessionId: string,
    content: string,
    targetAgentId?: string,
    senderAgentId = 'operator',
    eventType = 'prompt',
    attachments?: SessionAttachment[]
  ): Promise<SessionMessage> {
    const res = await apiFetch(`/api/sessions/${sessionId}/events`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({
        content,
        targetAgentId,
        senderAgentId,
        eventType,
        attachments
      })
    });
    if (!res.ok) throw new Error(`Failed to post session event: ${res.statusText}`);
    return res.json();
  },

  async stopSession(sessionId: string): Promise<{ status: string; sessionId: string }> {
    const res = await apiFetch(`/api/sessions/${sessionId}/stop`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to stop session: ${res.statusText}`);
    return res.json();
  },

  async stopCommand(sessionId: string, commandId: string): Promise<{ status: string; commandId: string }> {
    const res = await apiFetch(`/api/sessions/${sessionId}/commands/${encodeURIComponent(commandId)}/stop`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to stop command: ${res.statusText}`);
    return res.json();
  },

  async enqueuePrompt(
    sessionId: string,
    req: {
      prompt: string;
      senderAgentId?: string;
      sendNow?: boolean;
      model?: string;
      effort?: string;
      tier?: string;
      contextSize?: string;
      mode?: string;
      attachments?: SessionAttachment[];
    }
  ): Promise<{ session: AgentSession; queue: QueuedPromptItem[]; dispatched: boolean }> {
    const res = await apiFetch(`/api/sessions/${sessionId}/queue`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to enqueue prompt: ${res.statusText}`);
    return res.json();
  },

  async dequeuePrompt(sessionId: string, queueItemId: string): Promise<QueuedPromptItem[]> {
    const res = await apiFetch(`/api/sessions/${sessionId}/queue/${encodeURIComponent(queueItemId)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to remove queued prompt: ${res.statusText}`);
    return res.json();
  },

  async sendQueuedPromptNow(
    sessionId: string,
    queueItemId: string
  ): Promise<{ session: AgentSession; queue: QueuedPromptItem[]; dispatched: boolean }> {
    const res = await apiFetch(`/api/sessions/${sessionId}/queue/${encodeURIComponent(queueItemId)}/send-now`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to send queued prompt immediately: ${res.statusText}`);
    return res.json();
  },


  async submitQuestionAnswer(
    sessionId: string,
    req: SubmitAnswerRequest
  ): Promise<{ toolResult: SessionMessage; prompt: SessionMessage; session: AgentSession }> {
    const res = await apiFetch(`/api/sessions/${sessionId}/answer`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to submit answer: ${res.statusText}`);
    return res.json();
  },

  async getSessionArtifacts(sessionId: string): Promise<SessionArtifact[]> {
    const res = await apiFetch(`/api/sessions/${sessionId}/artifacts`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load artifacts: ${res.statusText}`);
    return res.json();
  },

  async getSessionArtifact(sessionId: string, artifactId: string): Promise<SessionArtifact> {
    const res = await apiFetch(`/api/sessions/${sessionId}/artifacts/${encodeURIComponent(artifactId)}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load artifact details: ${res.statusText}`);
    return res.json();
  },

  async syncSessionArtifact(
    sessionId: string,
    req: { name: string; content: string; summary?: string; contentType?: string }
  ): Promise<SessionArtifact> {
    const res = await apiFetch(`/api/sessions/${sessionId}/artifacts`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to sync artifact: ${res.statusText}`);
    return res.json();
  },

  async resetMesh(): Promise<{ status: string; message: string }> {
    const res = await apiFetch('/api/dev/reset-mesh', {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to reset mesh data: ${res.statusText}`);
    return res.json();
  },

  // Projects API
  async getProjects(): Promise<Project[]> {
    const res = await apiFetch('/api/projects', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load projects: ${res.statusText}`);
    return res.json();
  },

  async getProject(id: string): Promise<Project> {
    const res = await apiFetch(`/api/projects/${encodeURIComponent(id)}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load project: ${res.statusText}`);
    return res.json();
  },

  async createProject(req: {
    name: string;
    id?: string;
    description?: string;
    workspaceType?: number;
    gitRepositoryUrl?: string;
    gitBaseBranch?: string;
    s3BucketName?: string;
    s3PrefixKey?: string;
    projectInstructions?: string;
    defaultLeadPersonaId?: string;
    allowedPersonaIds?: string[];
  }): Promise<Project> {
    const res = await apiFetch('/api/projects', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to create project: ${res.statusText}`);
    return res.json();
  },

  async updateProject(id: string, req: {
    name?: string;
    description?: string;
    workspaceType?: number;
    gitRepositoryUrl?: string;
    gitBaseBranch?: string;
    s3BucketName?: string;
    s3PrefixKey?: string;
    projectInstructions?: string;
    defaultLeadPersonaId?: string;
    allowedPersonaIds?: string[];
  }): Promise<Project> {
    const res = await apiFetch(`/api/projects/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to update project: ${res.statusText}`);
    return res.json();
  },

  async deleteProject(id: string): Promise<{ status: string; id: string; deletedJobsCount: number }> {
    const res = await apiFetch(`/api/projects/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to delete project: ${res.statusText}`);
    return res.json();
  },

  async assignProjectPersona(projectId: string, personaId: string): Promise<ProjectPersonaSummary> {
    const res = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}/personas`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ personaId })
    });
    if (!res.ok) throw new Error(`Failed to assign persona: ${res.statusText}`);
    return res.json();
  },

  async removeProjectPersona(projectId: string, personaId: string): Promise<{ status: string }> {
    const res = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}/personas/${encodeURIComponent(personaId)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to remove persona: ${res.statusText}`);
    return res.json();
  },

  // ── Project supporting documents ──────────────────────────────────────────────
  //
  // A project's library of specs, contracts and screenshots. The endpoints accept a multipart form
  // carrying files, a pasted text document, or both in one submit, which is why addProjectDocument takes
  // an options bag rather than being two methods: the modal's "Add documents" flow lets a user type notes
  // and attach a screenshot in the same action, and sequencing two calls would leave a half-saved library
  // if the second one failed.

  async getProjectDocuments(projectId: string): Promise<ProjectDocument[]> {
    const res = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}/documents`, {
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to load project documents: ${res.statusText}`);
    const raw = await res.json();
    return (Array.isArray(raw) ? raw : []).map(mapProjectDocument);
  },

  /**
   * Adds one pasted text document, one or more uploaded files, or both.
   *
   * `title` and `content` create the text document; `files` create the uploads. A request with neither is
   * rejected by the API with a 400, and the message is surfaced rather than swallowed because it is the
   * user's own "I clicked Add but typed nothing" case.
   */
  async addProjectDocument(
    projectId: string,
    options: { title?: string; content?: string; files?: File[] }
  ): Promise<ProjectDocument[]> {
    const formData = new FormData();
    (options.files ?? []).forEach((f) => formData.append('files', f, f.name));
    if (options.title) formData.append('title', options.title);
    if (options.content) formData.append('content', options.content);

    const res = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}/documents`, {
      method: 'POST',
      // getAuthHeadersOnly, not getHeaders: setting Content-Type by hand would overwrite the multipart
      // boundary the browser generates, and the API would read an empty form. Same reason
      // uploadJobAttachments above does the same.
      headers: getAuthHeadersOnly(),
      body: formData
    });
    if (!res.ok) {
      let errMsg = res.statusText;
      try {
        const errBody = await res.json();
        if (errBody?.error) errMsg = errBody.error;
      } catch {
        // Ignore JSON parse error
      }
      throw new Error(`Failed to add document: ${errMsg}`);
    }
    const raw = await res.json();
    return (Array.isArray(raw) ? raw : []).map(mapProjectDocument);
  },

  /** Reads one document including its text body. Uploads still have no body in the row. */
  async getProjectDocument(projectId: string, docId: string): Promise<ProjectDocument> {
    const res = await apiFetch(
      `/api/projects/${encodeURIComponent(projectId)}/documents/${encodeURIComponent(docId)}`,
      { headers: getHeaders() }
    );
    if (!res.ok) throw new Error(`Failed to load document: ${res.statusText}`);
    return mapProjectDocument(await res.json());
  },

  async deleteProjectDocument(projectId: string, docId: string): Promise<{ status: string }> {
    const res = await apiFetch(
      `/api/projects/${encodeURIComponent(projectId)}/documents/${encodeURIComponent(docId)}`,
      { method: 'DELETE', headers: getHeaders() }
    );
    if (!res.ok) {
      let errMsg = res.statusText;
      try {
        const errBody = await res.json();
        if (errBody?.error) errMsg = errBody.error;
      } catch {
        // Ignore JSON parse error
      }
      throw new Error(`Failed to remove document: ${errMsg}`);
    }
    return res.json();
  },

  // Project Jobs API
  async getProjectJobs(projectId: string, includeArchived = true): Promise<ProjectJob[]> {
    const url = `/api/projects/${encodeURIComponent(projectId)}/jobs${includeArchived ? '' : '?includeArchived=false'}`;
    const res = await apiFetch(url, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load project jobs: ${res.statusText}`);
    return res.json();
  },

  async getProjectJob(id: string): Promise<ProjectJob> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load job: ${res.statusText}`);
    return res.json();
  },

  async createProjectJob(projectId: string, req: {
    title: string;
    description?: string;
    gitBranchName?: string;
    initialTasks?: string[];
    priority?: number;
    approvalPolicy?: number;
    assignedAgentId?: string | null;
  }): Promise<ProjectJob> {
    const res = await apiFetch(`/api/projects/${encodeURIComponent(projectId)}/jobs`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to create job: ${res.statusText}`);
    return res.json();
  },

  async updateProjectJob(id: string, req: {
    title?: string;
    description?: string;
    gitBranchName?: string;
    status?: number;
    resultSummary?: string;
    priority?: number;
    approvalPolicy?: number;
    assignedAgentId?: string | null;
  }): Promise<ProjectJob> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}`, {
      method: 'PUT',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to update job: ${res.statusText}`);
    return res.json();
  },

  async deleteProjectJob(id: string): Promise<{ status: string; id: string; projectId: string }> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to delete job: ${res.statusText}`);
    return res.json();
  },

  async getJobTasks(id: string): Promise<JobTask[]> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/tasks`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load job tasks: ${res.statusText}`);
    return res.json();
  },

  /** assignedPersonaId is accepted on create (a task can be born owned); updates must go
   * through assignJobTask, which also moves the queued turns that follow assignment. */
  async addJobTask(id: string, req: { title: string; assignedPersonaId?: string; requiresDiscussion?: boolean; attachments?: SessionAttachment[] }): Promise<JobTask> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/tasks`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to add task: ${res.statusText}`);
    return res.json();
  },

  async updateJobTask(id: string, taskId: string, req: { title?: string; isCompleted?: boolean; requiresDiscussion?: boolean; attachments?: SessionAttachment[] }): Promise<JobTask> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/tasks/${encodeURIComponent(taskId)}`, {
      method: 'PUT',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to update task: ${res.statusText}`);
    return res.json();
  },

  /**
   * Assigns/reassigns a task. The server returns the full job DTO because reassignment can
   * move queued turns and append a system message; filtering those locally would diverge
   * from what actually persisted.
   */
  async assignJobTask(
    jobId: string,
    taskId: string,
    personaId: string | null,
    takeQueuedTurns = true
  ): Promise<AssignJobTaskResult> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/tasks/${encodeURIComponent(taskId)}/assignee`, {
      method: 'PUT',
      headers: getHeaders(),
      body: JSON.stringify({ personaId, takeQueuedTurns })
    });
    if (!res.ok) {
      let errMsg = res.statusText;
      try {
        const errBody = await res.json();
        if (errBody?.error) errMsg = errBody.error;
      } catch { }
      throw new Error(`Failed to assign task: ${errMsg}`);
    }
    return res.json();
  },

  async uploadTaskAttachments(jobId: string, taskId: string, files: File[]): Promise<SessionAttachment[]> {
    const formData = new FormData();
    files.forEach((f) => formData.append('files', f, f.name));
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/tasks/${encodeURIComponent(taskId)}/attachments`, {
      method: 'POST',
      headers: getAuthHeadersOnly(),
      body: formData
    });
    if (!res.ok) {
      let errMsg = res.statusText;
      try {
        const errBody = await res.json();
        if (errBody?.error) errMsg = errBody.error;
      } catch { }
      throw new Error(`Failed to upload task attachments: ${errMsg}`);
    }
    const raw = await res.json();
    return (Array.isArray(raw) ? raw : []).map((item: any) => ({
      id: item.id || item.Id || '',
      sessionId: jobId,
      jobId,
      fileName: item.fileName || item.FileName || item.name || item.Name || '',
      contentType: item.contentType || item.ContentType || '',
      sizeBytes: typeof item.sizeBytes === 'number' ? item.sizeBytes : typeof item.SizeBytes === 'number' ? item.SizeBytes : 0,
      s3Key: item.s3Key || item.S3Key || '',
      downloadUrl: item.downloadUrl || item.DownloadUrl || (item.id ? `/api/project-jobs/${encodeURIComponent(jobId)}/attachments/${encodeURIComponent(item.id)}/content` : ''),
      createdAt: item.createdAt || item.CreatedAt || new Date().toISOString()
    }));
  },

  async deleteTaskAttachment(jobId: string, taskId: string, attachmentId: string): Promise<void> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/tasks/${encodeURIComponent(taskId)}/attachments/${encodeURIComponent(attachmentId)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to delete task attachment: ${res.statusText}`);
  },

  async deleteJobTask(id: string, taskId: string): Promise<{ status: string; taskId: string }> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/tasks/${encodeURIComponent(taskId)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to delete task: ${res.statusText}`);
    return res.json();
  },

  async getJobMessages(id: string): Promise<JobMessage[]> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/messages`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load job messages: ${res.statusText}`);
    return res.json();
  },

  /**
   * A job's artifacts — the job twin of `getSessionArtifacts`.
   *
   * The server folds the manifest stored on the job row with everything its turn sessions recorded and
   * any loose file in the job's artifact directory, so this is the one call the panel needs; there is no
   * client-side aggregation of per-session lists to do here.
   */
  async getJobArtifacts(jobId: string): Promise<JobArtifact[]> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/artifacts`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load job artifacts: ${res.statusText}`);
    const raw = await res.json();
    return (Array.isArray(raw) ? raw : []).map(mapJobArtifact);
  },

  /** One job artifact with its body inlined, for the rendered/raw text view. */
  async getJobArtifact(jobId: string, artifactId: string): Promise<JobArtifact> {
    const res = await apiFetch(
      `/api/project-jobs/${encodeURIComponent(jobId)}/artifacts/${encodeURIComponent(artifactId)}`,
      { headers: getHeaders() }
    );
    if (!res.ok) throw new Error(`Failed to load job artifact details: ${res.statusText}`);
    return mapJobArtifact(await res.json());
  },

  // ── Job file changes (the Cockpit's Changes tab) ────────────────────────────

  /**
   * What a job's branch changed, from the best source the API could reach.
   *
   * `refresh` bypasses the server's short reuse window — it is the panel's refresh button, and the
   * answer to "I know it changed". Note the endpoint's failure contract: only a nonexistent job is a
   * `404`. "The branch was never pushed" and "this deployment sees no checkout" are `200`s carrying an
   * explanatory `source`, so the caller renders them as a sentence rather than as an error boundary.
   */
  async getJobChanges(jobId: string, opts?: { limit?: number; refresh?: boolean }): Promise<JobChangeSet> {
    const params = new URLSearchParams();
    if (opts?.limit && opts.limit > 0) params.append('limit', String(opts.limit));
    if (opts?.refresh) params.append('refresh', 'true');
    const query = params.toString() ? `?${params.toString()}` : '';

    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/changes${query}`, {
      headers: getHeaders()
    });
    if (!res.ok) {
      throw new Error(await readApiErrorMessage(res, 'Failed to load file changes'));
    }
    return mapJobChangeSet(await res.json());
  },

  /**
   * One file's unified diff, or the whole change set's when `path` is omitted.
   *
   * `previousPath` must be forwarded from the list row for a rename or git reports a two-line edit as a
   * 60-line new file, and `status` must be forwarded because an untracked file is in no tree — no
   * two-sided comparison can produce its patch, so the server routes those rows through a one-sided
   * read instead. `baseSha`/`headSha` pin the request to the exact pair the counts on screen came from;
   * without them a base branch that moves between the list call and this one silently changes the diff.
   */
  async getJobChangePatch(
    jobId: string,
    opts?: {
      path?: string | null;
      previousPath?: string | null;
      status?: string | null;
      context?: number;
      baseSha?: string | null;
      headSha?: string | null;
    }
  ): Promise<JobChangePatchResult> {
    const params = new URLSearchParams();
    if (opts?.path) params.append('path', opts.path);
    if (opts?.previousPath) params.append('from', opts.previousPath);
    if (opts?.status) params.append('status', opts.status);
    if (typeof opts?.context === 'number') params.append('context', String(opts.context));
    if (opts?.baseSha) params.append('base', opts.baseSha);
    if (opts?.headSha) params.append('head', opts.headSha);
    const query = params.toString() ? `?${params.toString()}` : '';

    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/changes/patch${query}`, {
      headers: getHeaders()
    });

    if (res.status === 404) {
      // A refused patch is a state, not a fault: surface the server's own sentence and reason code so
      // the panel can distinguish "this path is not in the change set" from "no source here can diff".
      const body = await res.json().catch(() => ({} as any));
      return {
        found: false,
        reasonCode: readStr(body, 'reasonCode', 'ReasonCode'),
        message: readStr(body, 'error', 'Error') ?? 'No patch could be produced for this job.',
        notes: readStrList(body, 'notes', 'Notes'),
      };
    }

    if (!res.ok) {
      throw new Error(await readApiErrorMessage(res, 'Failed to load the diff'));
    }

    return { found: true, patch: mapJobChangePatch(await res.json()) };
  },

  async uploadJobAttachments(jobId: string, files: File[]): Promise<SessionAttachment[]> {
    const formData = new FormData();
    files.forEach((f) => formData.append('files', f, f.name));
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/attachments`, {
      method: 'POST',
      headers: getAuthHeadersOnly(),
      body: formData
    });
    if (!res.ok) {
      let errMsg = res.statusText;
      try {
        const errBody = await res.json();
        if (errBody?.error) errMsg = errBody.error;
      } catch {
        // Ignore JSON parse error
      }
      throw new Error(`Failed to upload attachments: ${errMsg}`);
    }
    const raw = await res.json();
    return (Array.isArray(raw) ? raw : []).map((item: any) => {
      const id = item.id || item.Id || '';
      const fileName = item.fileName || item.FileName || item.name || item.Name || '';
      const contentType = item.contentType || item.ContentType || '';
      const sizeBytes = typeof item.sizeBytes === 'number'
        ? item.sizeBytes
        : typeof item.SizeBytes === 'number'
        ? item.SizeBytes
        : (typeof item.size === 'number' ? item.size : 0);
      const s3Key = item.s3Key || item.S3Key || '';
      const targetId = id || fileName;
      const downloadUrl = item.downloadUrl || item.DownloadUrl || (targetId ? `/api/project-jobs/${encodeURIComponent(jobId)}/attachments/${encodeURIComponent(targetId)}/content` : '');
      const createdAt = item.createdAt || item.CreatedAt || new Date().toISOString();

      return {
        id,
        sessionId: jobId,
        fileName,
        contentType,
        sizeBytes,
        s3Key,
        downloadUrl,
        createdAt
      };
    });
  },

  async postJobMessage(id: string, req: {
    content: string;
    senderType?: string;
    senderId?: string;
    senderName?: string;
    turnSessionId?: string;
    attachments?: SessionAttachment[];
  }): Promise<JobMessage> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/messages`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to post job message: ${res.statusText}`);
    return res.json();
  },

  async approvePlan(jobId: string, approverRole?: string, comments?: string): Promise<{ success: boolean; approverId?: string; approverRole?: string }> {
    const res = await apiFetch(`/api/jobs/${encodeURIComponent(jobId)}/approve-plan`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ approverRole, comments })
    });
    if (!res.ok) throw new Error(`Failed to approve plan: ${res.statusText}`);
    return res.json();
  },

  async startJob(id: string): Promise<ProjectJob> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/start`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to start job: ${res.statusText}`);
    return res.json();
  },

  async completeJob(id: string, req?: { resultSummary?: string }): Promise<ProjectJob> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/complete`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req || {})
    });
    if (!res.ok) throw new Error(`Failed to complete job: ${res.statusText}`);
    return res.json();
  },

  async archiveJob(id: string): Promise<ProjectJob> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/archive`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to archive job: ${res.statusText}`);
    return res.json();
  },

  async reopenJob(id: string): Promise<ProjectJob> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/reopen`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to reopen job: ${res.statusText}`);
    return res.json();
  },

  async resetJob(id: string): Promise<ProjectJob> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(id)}/reset`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to reset job: ${res.statusText}`);
    return res.json();
  },

  async cancelQueuedJobTurn(jobId: string, turnId: string): Promise<ProjectJob | undefined> {
    const res = await apiFetch(`/api/project-jobs/${encodeURIComponent(jobId)}/queued-turns/${encodeURIComponent(turnId)}`, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.error || err.message || `Failed to cancel queued turn: ${res.statusText}`);
    }
    const body = await res.json().catch(() => ({}));
    return body?.job as ProjectJob | undefined;
  },

  async cancelQueuedJobTurns(jobId: string, personaId?: string): Promise<ProjectJob | undefined> {
    const url = personaId
      ? `/api/project-jobs/${encodeURIComponent(jobId)}/queued-turns?personaId=${encodeURIComponent(personaId)}`
      : `/api/project-jobs/${encodeURIComponent(jobId)}/queued-turns`;
    const res = await apiFetch(url, {
      method: 'DELETE',
      headers: getHeaders()
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.error || err.message || `Failed to cancel queued turns: ${res.statusText}`);
    }
    const body = await res.json().catch(() => ({}));
    return body?.job as ProjectJob | undefined;
  }
};
