export interface Agent {
  id: string;
  name: string;
  runtimeType: number | string;
  trustTier: number;
  personaPrompt: string;
  memorySummary: string;
  createdAt: string;
  updatedAt: string;
}

export interface StagedJob {
  id: string;
  sessionId: string;
  requestedByAgentId: string;
  requestedByAgent?: Agent | null;
  targetService: string;
  actionVerb: string;
  payloadJson: string;
  approvalStatus: number | string;
  executionStatus: number | string;
  temporalWorkflowId?: string | null;
  resultJson?: string | null;
  errorMessage?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface AgentSession {
  id: string;
  title: string;
  status: string;
  metadataJson: string;
  createdAt: string;
  updatedAt: string;
}

export interface SessionMessage {
  id: string;
  sessionId: string;
  senderAgentId: string;
  targetAgentId?: string | null;
  content: string;
  structuredPayloadJson?: string | null;
  createdAt: string;
}
