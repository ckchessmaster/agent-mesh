import type {
  Agent,
  AgentSession,
  AgentUsageResponse,
  DriverConfig,
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
  ProjectPersonaSummary,
  QueuedPromptItem,
  SessionAttachment,
  SessionMessage,
  SessionArtifact,
  StagedJob,
  SubmitAnswerRequest,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from '../types';

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

  async testOpenAiServer(req: { endpoint: string; apiKey?: string }): Promise<{ success: boolean; error?: string; models: { id: string; name: string }[] }> {
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

  async getPersonaSessions(personaId: string): Promise<AgentSession[]> {
    const res = await apiFetch(`/api/personas/${encodeURIComponent(personaId)}/sessions`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load persona sessions: ${res.statusText}`);
    return res.json();
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

  async getAgentSessions(agentId: string): Promise<AgentSession[]> {
    const res = await apiFetch(`/api/agents/${encodeURIComponent(agentId)}/sessions`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load agent sessions: ${res.statusText}`);
    return res.json();
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

  async getSession(id: string): Promise<AgentSession> {
    const res = await apiFetch(`/api/sessions/${id}`, { headers: getHeaders() });
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

  async getSessionMessages(sessionId: string): Promise<SessionMessage[]> {
    const res = await apiFetch(`/api/sessions/${sessionId}/events`, { headers: getHeaders() });
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
    status?: number;
    resultSummary?: string;
    priority?: number;
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
