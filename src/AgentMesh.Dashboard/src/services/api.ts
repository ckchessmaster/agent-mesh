import type { Agent, AgentSession, SessionMessage, StagedJob, SystemStatusResponse } from '../types';

const API_KEY = 'mesh-default-secret-key';

const defaultHeaders = {
  'Content-Type': 'application/json',
  'Authorization': `Bearer ${API_KEY}`,
  'X-Agent-Id': 'gemini-spark'
};

export const api = {
  async getStatus(): Promise<SystemStatusResponse> {
    const res = await fetch('/api/status', { headers: defaultHeaders });
    if (!res.ok) throw new Error(`Failed to load system status: ${res.statusText}`);
    return res.json();
  },

  async getAgents(): Promise<Agent[]> {
    const res = await fetch('/api/agents', { headers: defaultHeaders });
    if (!res.ok) throw new Error(`Failed to load agents: ${res.statusText}`);
    return res.json();
  },

  async getJobs(approvalStatus?: string): Promise<StagedJob[]> {
    const query = approvalStatus ? `?approvalStatus=${encodeURIComponent(approvalStatus)}` : '';
    const res = await fetch(`/api/jobs${query}`, { headers: defaultHeaders });
    if (!res.ok) throw new Error(`Failed to load jobs: ${res.statusText}`);
    return res.json();
  },

  async submitApproval(jobId: string, approved: boolean, reason?: string): Promise<void> {
    const res = await fetch(`/api/jobs/${jobId}/approval`, {
      method: 'POST',
      headers: defaultHeaders,
      body: JSON.stringify({ approved, reason: reason || (approved ? 'Approved via Web Cockpit' : 'Rejected via Web Cockpit') })
    });
    if (!res.ok) throw new Error(`Failed to submit approval: ${res.statusText}`);
  },

  async getSessions(): Promise<AgentSession[]> {
    const res = await fetch('/api/sessions', { headers: defaultHeaders });
    if (!res.ok) throw new Error(`Failed to load sessions: ${res.statusText}`);
    return res.json();
  },

  async getSessionMessages(sessionId: string): Promise<SessionMessage[]> {
    const res = await fetch(`/api/sessions/${sessionId}/messages`, { headers: defaultHeaders });
    if (!res.ok) throw new Error(`Failed to load session messages: ${res.statusText}`);
    return res.json();
  },

  async postSessionMessage(sessionId: string, content: string, targetAgentId?: string): Promise<SessionMessage> {
    const res = await fetch(`/api/sessions/${sessionId}/messages`, {
      method: 'POST',
      headers: defaultHeaders,
      body: JSON.stringify({ content, targetAgentId, senderAgentId: 'gemini-spark' })
    });
    if (!res.ok) throw new Error(`Failed to post message: ${res.statusText}`);
    return res.json();
  }
};
