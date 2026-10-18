export interface Agent {
  id: string;
  name: string;
  role?: string;
  status?: 'online' | 'busy' | 'offline' | string;
  runtimeType: number | string;
  trustTier: number;
  personaPrompt?: string;
  memorySummary?: string;
  capabilitiesJson?: string;
  lastHeartbeatAt?: string;
  sessionCount?: number;
  createdAt: string;
  updatedAt?: string;
}

export interface StagedJob {
  id: string;
  sessionId: string;
  requestedByAgentId: string;
  requestedByAgent?: Agent | null;
  targetService: string;
  actionVerb: string;
  payloadJson: string;
  payloadS3Uri?: string | null;
  approvalStatus: number | string;
  executionStatus: number | string;
  temporalWorkflowId?: string | null;
  resultJson?: string | null;
  resultS3Uri?: string | null;
  artifactsManifestJson?: string;
  errorMessage?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface AgentSession {
  id: string;
  title: string;
  status: 'active' | 'thinking' | 'waiting_for_input' | 'completed' | 'failed' | string;
  metadataJson: string;
  creatorAgentId?: string | null;
  creatorAgentName?: string | null;
  messageCount?: number;
  createdAt: string;
  updatedAt: string;
  messages?: SessionMessage[];
}

export type SessionEventType = 'prompt' | 'thought' | 'tool_call' | 'tool_result' | 'response' | string;

export interface SessionMessage {
  id: string;
  sessionId: string;
  senderAgentId: string;
  targetAgentId?: string | null;
  eventType?: SessionEventType;
  content: string;
  toolName?: string | null;
  toolArgsJson?: string | null;
  toolResultJson?: string | null;
  structuredPayloadJson?: string | null;
  createdAt: string;
}

export interface ServiceHealth {
  status: 'Healthy' | 'Degraded' | 'Unhealthy' | string;
  latencyMs?: number;
  description: string;
  error?: string;
  bucket?: string;
  version?: string;
}

export interface SystemStatusResponse {
  status: 'Healthy' | 'Degraded' | 'Unhealthy' | string;
  timestamp: string;
  environment: string;
  services: {
    api?: ServiceHealth;
    postgres?: ServiceHealth;
    redis?: ServiceHealth;
    temporal?: ServiceHealth;
    s3?: ServiceHealth;
    [key: string]: ServiceHealth | undefined;
  };
}
