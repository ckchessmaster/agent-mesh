import type { Agent, AgentSession, SessionMessage, StagedJob, SystemStatusResponse } from '../types';

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
    'X-Agent-Id': 'gemini-spark'
  };
};

export const api = {
  async getStatus(): Promise<SystemStatusResponse> {
    const res = await fetch('/api/status', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load system status: ${res.statusText}`);
    return res.json();
  },

  async getAgents(): Promise<Agent[]> {
    const res = await fetch('/api/agents', { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load agents: ${res.statusText}`);
    return res.json();
  },

  async registerAgent(req: {
    id: string;
    name: string;
    runtimeType?: number;
    trustTier?: number;
    personaPrompt?: string;
    memorySummary?: string;
  }): Promise<{ agent: Agent; token: string; instructions: string }> {
    const res = await fetch('/api/agents/register', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify(req)
    });
    if (!res.ok) throw new Error(`Failed to register agent: ${res.statusText}`);
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

  async getSession(id: string): Promise<AgentSession> {
    const res = await fetch(`/api/sessions/${id}`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load session details: ${res.statusText}`);
    return res.json();
  },

  async createSession(title: string, metadataJson?: string, initialMessage?: string): Promise<AgentSession> {
    const res = await fetch('/api/sessions', {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ title, metadataJson: metadataJson || '{}', initialMessage, creatorAgentId: 'operator' })
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

  async getSessionMessages(sessionId: string): Promise<SessionMessage[]> {
    const res = await fetch(`/api/sessions/${sessionId}/messages`, { headers: getHeaders() });
    if (!res.ok) throw new Error(`Failed to load session messages: ${res.statusText}`);
    return res.json();
  },

  async postSessionMessage(
    sessionId: string,
    content: string,
    targetAgentId?: string,
    senderAgentId = 'operator'
  ): Promise<SessionMessage> {
    const res = await fetch(`/api/sessions/${sessionId}/messages`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ content, targetAgentId, senderAgentId })
    });
    if (!res.ok) throw new Error(`Failed to post message: ${res.statusText}`);
    return res.json();
  }
};
