import type {
  Agent,
  AgentSession,
  AgentUsageResponse,
  FilesystemBrowseResponse,
  QueuedPromptItem,
  SessionMessage,
  StagedJob,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from '../types';

let tokenProvider: (() => string | null) | null = null;

export const setAuthTokenProvider = (provider: (() => string | null) | null) => {
  tokenProvider = provider;
};

const getHeaders = (): Record<string, string> => {
  const token = tokenProvider?.();
  if (token) {
    return {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    };
  }

  return {
    'Content-Type': 'application/json',
    'Authorization': 'Bearer mesh-default-secret-key',
    'X-Agent-Id': 'agy-core'
  };
};

export interface SessionControlOptions {
  workingDirectory?: string;
  model?: string;
  effort?: string;
  mode?: string;
  tier?: string;
}

export const api = {
  async getStatus(): Promise<SystemStatusResponse> {
    const res = await fetch('/api/status', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load system status: ${res.statusText}`);
    return res.json();
  },

  async getCapabilities(): Promise<SystemCapabilitiesResponse> {
    const res = await fetch('/api/system/capabilities', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load system capabilities: ${res.statusText}`);
    return res.json();
  },

  async browseFilesystem(path?: string): Promise<FilesystemBrowseResponse> {
    const query = path ? `?path=${encodeURIComponent(path)}` : '';
    const res = await fetch(`/api/system/filesystem${query}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to browse directory: ${res.statusText}`);
    return res.json();
  },

  async getAgents(): Promise<Agent[]> {
    const res = await fetch('/api/agents', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load agents: ${res.statusText}`);
    return res.json();
  },

  async getAgentUsage(agentId: string): Promise<AgentUsageResponse> {
    const res = await fetch(`/api/agents/${encodeURIComponent(agentId)}/usage`, { headers: getHeaders() });
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
    const res = await fetch('/api/agents/register', {
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
    const res = await fetch('/api/agents', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to register/update agent: ${res.statusText}`);
    return res.json();
  },

  async getJobs(approvalStatus?: string): Promise<StagedJob[]> {
    const query = approvalStatus ? `?approvalStatus=${encodeURIComponent(approvalStatus)}` : '';
    const res = await fetch(`/api/jobs${query}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load jobs: ${res.statusText}`);
    return res.json();
  },

  async submitApproval(jobId: string, approved: boolean, reason?: string): Promise<void> {
    const res = await fetch(`/api/jobs/${jobId}/approval`, {
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
    const res = await fetch(`/api/sessions${query}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load sessions: ${res.statusText}`);
    return res.json();
  },

  async getAgentSessions(agentId: string): Promise<AgentSession[]> {
    const res = await fetch(`/api/agents/${encodeURIComponent(agentId)}/sessions`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load agent sessions: ${res.statusText}`);
    return res.json();
  },

  async createAgentSession(
    agentId: string,
    title: string,
    initialPrompt?: string,
    options?: SessionControlOptions
  ): Promise<AgentSession> {
    const res = await fetch(`/api/agents/${encodeURIComponent(agentId)}/sessions`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({
        title,
        initialPrompt,
        workingDirectory: options?.workingDirectory,
        model: options?.model,
        effort: options?.effort,
        mode: options?.mode,
        tier: options?.tier
      })
    });
    if (!res.ok) throw new Error(`Failed to create agent session: ${res.statusText}`);
    return res.json();
  },

  async getSession(id: string): Promise<AgentSession> {
    const res = await fetch(`/api/sessions/${id}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load session details: ${res.statusText}`);
    return res.json();
  },

  async createSession(
    title: string,
    metadataJson?: string,
    initialMessage?: string,
    creatorAgentId = 'agy-core',
    options?: SessionControlOptions
  ): Promise<AgentSession> {
    const res = await fetch('/api/sessions', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({
        title,
        metadataJson: metadataJson || '{}',
        initialMessage,
        creatorAgentId,
        workingDirectory: options?.workingDirectory,
        model: options?.model,
        effort: options?.effort,
        mode: options?.mode,
        tier: options?.tier
      })
    });
    if (!res.ok) throw new Error(`Failed to create session: ${res.statusText}`);
    return res.json();
  },

  async updateSessionStatus(id: string, status: string): Promise<AgentSession> {
    const res = await fetch(`/api/sessions/${id}`, {
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
      mode?: string;
      workingDirectory?: string;
    }
  ): Promise<AgentSession> {
    const res = await fetch(`/api/sessions/${id}`, {
      method: 'PATCH',
      headers: getHeaders(),
      body: JSON.stringify(params)
    });
    if (!res.ok) throw new Error(`Failed to update session controls: ${res.statusText}`);
    return res.json();
  },

  async getSessionMessages(sessionId: string): Promise<SessionMessage[]> {
    const res = await fetch(`/api/sessions/${sessionId}/events`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load session events: ${res.statusText}`);
    return res.json();
  },

  async postSessionMessage(
    sessionId: string,
    content: string,
    targetAgentId?: string,
    senderAgentId = 'operator',
    eventType = 'prompt'
  ): Promise<SessionMessage> {
    const res = await fetch(`/api/sessions/${sessionId}/events`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({
        content,
        targetAgentId,
        senderAgentId,
        eventType
      })
    });
    if (!res.ok) throw new Error(`Failed to post session event: ${res.statusText}`);
    return res.json();
  },

  async stopSession(sessionId: string): Promise<{ status: string; sessionId: string }> {
    const res = await fetch(`/api/sessions/${sessionId}/stop`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to stop session: ${res.statusText}`);
    return res.json();
  },

  async stopCommand(sessionId: string, commandId: string): Promise<{ status: string; commandId: string }> {
    const res = await fetch(`/api/sessions/${sessionId}/commands/${encodeURIComponent(commandId)}/stop`, {
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
      mode?: string;
      workingDirectory?: string;
    }
  ): Promise<{ session: AgentSession; queue: QueuedPromptItem[]; dispatched: boolean }> {
    const res = await fetch(`/api/sessions/${sessionId}/queue`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to enqueue prompt: ${res.statusText}`);
    return res.json();
  },

  async dequeuePrompt(sessionId: string, queueItemId: string): Promise<QueuedPromptItem[]> {
    const res = await fetch(`/api/sessions/${sessionId}/queue/${encodeURIComponent(queueItemId)}`, {
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
    const res = await fetch(`/api/sessions/${sessionId}/queue/${encodeURIComponent(queueItemId)}/send-now`, {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to send queued prompt immediately: ${res.statusText}`);
    return res.json();
  },


  async resetMesh(): Promise<{ status: string; message: string }> {
    const res = await fetch('/api/dev/reset-mesh', {
      method: 'POST',
      headers: getHeaders()
    });
    if (!res.ok) throw new Error(`Failed to reset mesh data: ${res.statusText}`);
    return res.json();
  }
};
