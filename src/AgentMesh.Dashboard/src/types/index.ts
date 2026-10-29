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
  maxConcurrentJobs?: number | null;
  trustTier: number;
  sessionCount?: number;
  hasGitHubToken?: boolean;
  gitHubTokenPreview?: string | null;
  gitAuthorName?: string | null;
  gitAuthorEmail?: string | null;
  createdAt: string;
  updatedAt?: string;
}

export interface PersonaEnvironmentStatus {
  personaId: string;
  status: 'stopped' | 'running' | 'starting' | 'error' | 'none' | string;
  runtime: 'docker' | 'podman' | 'kubernetes' | 'none' | string;
  containerId?: string | null;
  startedAt?: string | null;
  lastActivityAt?: string | null;
  memoryUsageBytes?: number | null;
  errorMessage?: string | null;
  image?: string | null;
  configuredImage?: string | null;
  updateAvailable?: boolean;
}

export interface PersonaDriverVerificationStep {
  name: string;
  status: 'pending' | 'success' | 'failed' | 'warning';
  details: string;
}

export interface PersonaDriverVerificationResult {
  personaId: string;
  driverId: string;
  overallStatus: 'passed' | 'failed' | 'warning';
  steps: PersonaDriverVerificationStep[];
  errorMessage?: string | null;
}

export interface PersonaAuthStartResult {
  personaId: string;
  driverId: string;
  status: 'ready' | 'waiting_code' | 'already_authenticated' | 'apikey_required' | 'error';
  authUrl?: string | null;
  instructions?: string | null;
  errorMessage?: string | null;
}

export interface PersonaAuthSubmitResult {
  personaId: string;
  status: 'authenticated' | 'failed';
  message?: string | null;
  errorMessage?: string | null;
}

export interface DriverSidecarConfig {
  enabled: boolean;
  type: 'tailscale' | 'custom';
  authKey?: string | null;
  secretRefName?: string | null;
  secretRefKey?: string | null;
  hostname?: string | null;
  tags?: string | null;
  userspaceMode?: boolean;
  extraArgs?: string | null;
  image?: string | null;
  command?: string | null;
  arguments?: string[] | null;
  environmentVariables?: Record<string, string> | null;
  companionSetup?: string | null;
  companionCommand?: string | null;
  companionPort?: number | null;
  loginCommand?: string | null;
  authCheckCommand?: string | null;
}

/**
 * Parsed driver runtime config, as returned by GET /api/agents/{id}/config and mirrored on
 * Agent.config. The API never sends the API key itself — only whether one is stored.
 */
export interface DriverConfig {
  adapter?: string | null;
  endpoint?: string | null;
  defaultModel?: string | null;
  /**
   * Operator-authored model allow-list, normalized to ids by the API. The driver form writes this
   * as `{ id, name }` objects from a live `/models` response; the API flattens it.
   */
  models?: string[] | null;
  timeoutSeconds?: number | null;
  maxRetries?: number | null;
  retryDelayMilliseconds?: number | null;
  maxSteps?: number | null;
  capabilities?: string[];
  hasApiKey?: boolean;
  sidecar?: DriverSidecarConfig | null;
  hasSidecarAuthKey?: boolean;
  loginCommand?: string | null;
  authCheckCommand?: string | null;
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
  config?: DriverConfig;
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

/**
 * One page of a persona's or driver's session history, plus the server's exact total for that owner.
 * `total` is null when the API did not send `X-Total-Count` (older build, stripped by a proxy), and
 * callers must then fall back to the rows they already hold.
 */
export interface SessionPage {
  items: AgentSession[];
  total: number | null;
}

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
  /**
   * Effort levels this model accepts. Empty means the driver has no effort surface, which is
   * different from "unknown": the picker is hidden rather than filled with a default ladder.
   */
  efforts: string[];
  /**
   * Context windows this model accepts, or null when the driver cannot be told its window.
   * Absent on a baseline entry, which carries no models at all.
   */
  contextSizes?: string[] | null;
}

/**
 * Where a model list came from. Rendered differently in every place it appears: a bundled preset
 * must never read like a driver answer, because the user will trust an entry the driver rejects.
 */
export type CapabilitySource = 'probed' | 'static_preset' | 'user_override' | 'unavailable';

export interface RuntimeCapabilities {
  adapter: string;
  name: string;
  modes: ModeOption[];
  /**
   * Always empty on the baseline map. Models are provider facts and are resolved per driver; the
   * baseline exists only so mode and tier pickers can render before any driver is resolved.
   */
  models: ModelCapability[];
  tiers?: TierOption[];
  contextSizes?: ContextSizeOption[] | null;
  /** False when the driver has no way to be told its context window, so the picker is hidden. */
  supportsContextSelection?: boolean;
  modelSource?: CapabilitySource;
  observedAt?: string;
  effortSource?: string | null;
  unavailableReason?: string | null;
}

/** One driver's resolved capabilities — the surface the pickers should read. */
export interface DriverCapabilities {
  driverId: string;
  name: string;
  adapter: string;
  endpoint?: string | null;
  models: ModelCapability[];
  modelSource: CapabilitySource;
  observedAt: string;
  supportsContextSelection: boolean;
  contextSizes?: ContextSizeOption[] | null;
  modes?: ModeOption[];
  tiers?: TierOption[] | null;
  effortSource?: string | null;
  unavailableReason?: string | null;
  defaultModel?: string | null;
  operatorModels?: string[] | null;
}

export interface SystemCapabilitiesResponse {
  agentRuntimes: Record<string, RuntimeCapabilities>;
  /**
   * Per-driver readings. Undefined on an API build older than the capability work; an empty array
   * means the newer API has no drivers, which is a different statement.
   */
  drivers?: DriverCapabilities[] | null;
  schemaVersion?: number;
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
  /** Which introspection channel produced this reading, e.g. "agy:/usage". */
  source?: string | null;
  /** When the reading was taken, so the UI can age it instead of implying live data. */
  observedAt?: string | null;
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
  driverName?: string;
  driverStatus?: 'online' | 'busy' | 'offline' | string;
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

export interface AssignJobTaskResult {
  task: JobTask;
  fromPersonaId?: string | null;
  toPersonaId?: string | null;
  fromPersonaName?: string | null;
  toPersonaName?: string | null;
  isTargetTask: boolean;
  movedQueuedTurnCount: number;
  dispatchedToNewAssignee: boolean;
  job: ProjectJob;
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
  attachments?: SessionAttachment[] | null;
}

export interface AskQuestionOptionObject {
  label?: string;
  text?: string;
  value?: string;
  title?: string;
  description?: string;
}

export type AskQuestionOption = string | AskQuestionOptionObject;

export interface AskQuestionItem {
  question: string;
  header?: string;
  options: AskQuestionOption[];
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



