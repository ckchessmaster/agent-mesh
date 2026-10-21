export interface DriverPersonaSummary {
  id: string;
  name: string;
  role: string;
  defaultModel?: string | null;
}

export interface Persona {
  id: string;
  name: string;
  role: string;
  personaPrompt: string;
  driverId: string;
  driverName?: string;
  driverStatus?: 'online' | 'busy' | 'offline' | string;
  defaultModel?: string | null;
  defaultEffort?: string | null;
  defaultMode?: string | null;
  defaultTier?: string | null;
  defaultContextSize?: string | null;
  defaultWorkingDirectory?: string | null;
  maxConcurrentJobs?: number | null;
  trustTier: number;
  sessionCount?: number;
  createdAt: string;
  updatedAt?: string;
}

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
  personas?: DriverPersonaSummary[];
  createdAt: string;
  updatedAt?: string;
}

export type Driver = Agent;

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
  driverId?: string | null;
  driverName?: string | null;
  personaId?: string | null;
  personaName?: string | null;
  personaRole?: string | null;
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

export interface ContextSizeOption {
  id: string;
  label: string;
  description: string;
}

export interface ModelCapability {
  id: string;
  name: string;
  efforts: string[];
  contextSizes?: string[];
}

export interface RuntimeCapabilities {
  adapter: string;
  name: string;
  modes: ModeOption[];
  models: ModelCapability[];
  tiers?: TierOption[];
  contextSizes?: ContextSizeOption[];
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

export interface SessionAttachment {
  id: string;
  sessionId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  s3Key: string;
  downloadUrl: string;
  createdAt: string;
}

export const WorkspaceType = {
  Git: 0,
  S3Archive: 1
} as const;
export type WorkspaceType = (typeof WorkspaceType)[keyof typeof WorkspaceType];

export const ProjectJobStatus = {
  Active: 0,
  Completed: 1,
  Archived: 2
} as const;
export type ProjectJobStatus = (typeof ProjectJobStatus)[keyof typeof ProjectJobStatus];

export const ProjectJobPriority = {
  Low: 0,
  Normal: 1,
  High: 2,
  Urgent: 3
} as const;
export type ProjectJobPriority = (typeof ProjectJobPriority)[keyof typeof ProjectJobPriority];

export interface QueuedJobTurnDto {
  id: string;
  jobId: string;
  projectId: string;
  targetPersonaId: string;
  senderType: string;
  senderId?: string | null;
  senderName?: string | null;
  content: string;
  mentions: string[];
  attachments?: SessionAttachment[] | null;
  priority: ProjectJobPriority | number;
  createdAt: string;
}

export interface ProjectPersonaSummary {
  id: string;
  name: string;
  role: string;
  driverId: string;
}

export interface Project {
  id: string;
  name: string;
  description: string;
  workspaceType: WorkspaceType | number;
  gitRepositoryUrl?: string | null;
  gitBaseBranch: string;
  s3BucketName?: string | null;
  s3PrefixKey?: string | null;
  projectInstructions: string;
  defaultLeadPersonaId?: string | null;
  defaultLeadPersonaName?: string | null;
  assignedPersonas: ProjectPersonaSummary[];
  jobCount: number;
  activeJobCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface JobTask {
  id: string;
  title: string;
  isCompleted: boolean;
  requiresDiscussion?: boolean;
  assignedPersonaId?: string | null;
  completedAt?: string | null;
  attachments?: SessionAttachment[] | null;
}

export interface ProjectJob {
  id: string;
  projectId: string;
  projectName?: string | null;
  title: string;
  description: string;
  status: ProjectJobStatus | number;
  gitBranchName: string;
  s3ArchiveKey?: string | null;
  tasks: JobTask[];
  resultSummary?: string | null;
  priority?: ProjectJobPriority | number;
  queuedTurns?: QueuedJobTurnDto[];
  messageCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface JobMessage {
  id: string;
  jobId: string;
  senderType: 'user' | 'persona' | string;
  senderId?: string | null;
  senderName?: string | null;
  content: string;
  mentions: string[];
  turnSessionId?: string | null;
  structuredPayloadJson?: string | null;
  attachments?: SessionAttachment[] | null;
  createdAt: string;
}

export interface QueuedPromptItem {
  id: string;
  prompt: string;
  sender: string;
  createdAt: string;
  model?: string | null;
  effort?: string | null;
  tier?: string | null;
  contextSize?: string | null;
  mode?: string | null;
  workingDirectory?: string | null;
  attachments?: SessionAttachment[] | null;
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
  s3Key?: string | null;
  downloadUrl?: string | null;
}



