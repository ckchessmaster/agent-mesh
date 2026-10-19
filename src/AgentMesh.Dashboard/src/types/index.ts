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

export interface DirectoryBookmark {
  label: string;
  path: string;
  description: string;
}

export interface WorkspaceDefaults {
  defaultSandboxPath: string;
  currentDirectory: string;
  userHome: string;
  bookmarks: DirectoryBookmark[];
}

export interface ModeOption {
  id: string;
  label: string;
  description: string;
}

export interface TierOption {
  id: string;
  label: string;
  description: string;
}

export interface ModelCapability {
  id: string;
  name: string;
  efforts: string[];
}

export interface RuntimeCapabilities {
  adapter: string;
  name: string;
  modes: ModeOption[];
  models: ModelCapability[];
  tiers?: TierOption[];
}

export interface SystemCapabilitiesResponse {
  workspace: WorkspaceDefaults;
  agentRuntimes: Record<string, RuntimeCapabilities>;
}

export interface DirectoryItem {
  name: string;
  path: string;
}

export interface FilesystemBrowseResponse {
  currentPath: string;
  parentPath: string | null;
  directories: DirectoryItem[];
}

export interface QuotaBucket {
  id: string;
  name: string;
  description?: string | null;
  window?: string | null;
  remainingFraction: number;
  resetTime?: string | null;
}

export interface QuotaGroup {
  name: string;
  description?: string | null;
  buckets: QuotaBucket[];
}

export interface ProviderQuotaInfo {
  provider: string;
  planName: string;
  primaryRemainingFraction: number;
  groups: QuotaGroup[];
}

export interface LifetimeTokens {
  inputTokens: number;
  outputTokens: number;
  thinkingTokens: number;
  totalTokens: number;
  totalDurationSeconds: number;
}

export interface AgentUsageResponse {
  agentId: string;
  agentName: string;
  sessionCount: number;
  lifetimeTokens: LifetimeTokens;
  providerQuota?: ProviderQuotaInfo | null;
}

export interface QueuedPromptItem {
  id: string;
  prompt: string;
  sender: string;
  createdAt: string;
  model?: string | null;
  effort?: string | null;
  tier?: string | null;
  mode?: string | null;
  workingDirectory?: string | null;
}

export interface AskQuestionItem {
  question: string;
  options: string[];
  is_multi_select?: boolean;
}

export interface QuestionAnswerPayload {
  questionIndex: number;
  question: string;
  selectedOptions: string[];
  customText?: string;
}

export interface SubmitAnswerRequest {
  toolCallId?: string | null;
  answers: QuestionAnswerPayload[];
}

export interface SessionArtifact {
  id: string;
  sessionId: string;
  name: string;
  summary?: string;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
  updatedAt: string;
  content?: string;
}



