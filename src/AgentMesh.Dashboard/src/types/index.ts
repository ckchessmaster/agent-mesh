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
  hasOAuthToken?: boolean;
  learnedContextLimits?: Record<string, number> | null;
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
 * Where a model list came from, after normalization. Rendered differently in every place it
 * appears: a bundled preset must never read like a driver answer, because the user will trust an
 * entry the driver rejects.
 *
 * `declared` is the runner relaying what its own driver reported. It is a real driver answer, but
 * one we did not verify ourselves and which can be up to the declaration window stale, so it is
 * worded distinctly from `probed` rather than folded into it.
 *
 * This is the NORMALIZED type, not the wire type. The wire fields are typed `string` because this
 * bundle is frequently older than the API serving it - the dashboard is a static build while the
 * API rolls independently - and a field typed as this union would make `normalizeSource`'s
 * fall-through to 'unavailable' unreachable by construction: TypeScript would insist an unknown
 * value cannot arrive, while the whole reason that branch exists is that it can.
 * `CapabilitySourceKindValues_AreExactlyTheUnionTheDashboardDeclares` is what keeps this list and
 * the server's enum in step.
 */
export type CapabilitySource =
  | 'probed'
  | 'declared'
  | 'static_preset'
  | 'user_override'
  | 'unavailable';

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
  /** Raw wire value; read through `normalizeSource`, never compared directly. */
  modelSource?: string;
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
  /** Raw wire value; read through `normalizeSource`, never compared directly. */
  modelSource: string;
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

/**
 * How a project document got into the library.
 *
 * Mirrors `AgentMesh.Core.Models.ProjectDocumentKind`, which crosses the wire as a number because the
 * API registers no `JsonStringEnumConverter` — the same reason `WorkspaceType` above is a numeric const
 * object rather than a string union.
 */
export const ProjectDocumentKind = {
  /** Pasted or typed prose; its body is stored on the row and returned by the single-document read. */
  Text: 0,
  /** An uploaded document or image; its bytes live on disk and in the object store. */
  File: 1
} as const;

export type ProjectDocumentKind = (typeof ProjectDocumentKind)[keyof typeof ProjectDocumentKind];

/**
 * A supporting document attached to a project — a spec, a contract, a screenshot — that outlives any
 * single job or message.
 *
 * `content` is populated only by `getProjectDocument`, and only for a text document. The list endpoint
 * deliberately omits it: a library of fifty pasted specs is not something the modal should receive to
 * draw fifty rows.
 */
export interface ProjectDocument {
  id: string;
  projectId: string;
  title: string;
  kind: ProjectDocumentKind | number;
  fileName?: string | null;
  contentType?: string | null;
  sizeBytes: number;
  s3Key?: string | null;
  downloadUrl: string;
  forceDownloadUrl: string;
  createdAt: string;
  updatedAt: string;
  content?: string | null;
}

export const WorkspaceType = {
  Git: 0,
  S3Archive: 1
} as const;
export type WorkspaceType = (typeof WorkspaceType)[keyof typeof WorkspaceType];

export const ProjectJobStatus = {
  Active: 0,
  Completed: 1,
  Archived: 2,
  Pending: 3
} as const;
export type ProjectJobStatus = (typeof ProjectJobStatus)[keyof typeof ProjectJobStatus];

export const ProjectJobPriority = {
  Low: 0,
  Normal: 1,
  High: 2,
  Urgent: 3
} as const;
export type ProjectJobPriority = (typeof ProjectJobPriority)[keyof typeof ProjectJobPriority];

export const PlanApprovalPolicy = {
  LeadOrHuman: 0,
  HumanOnly: 1,
  Autonomous: 2
} as const;
export type PlanApprovalPolicy = (typeof PlanApprovalPolicy)[keyof typeof PlanApprovalPolicy];

export const JobLifecycleStatus = {
  Planning: 'Planning',
  AwaitingApproval: 'AwaitingApproval',
  InProgress: 'InProgress',
  AwaitingReview: 'AwaitingReview',
  Completed: 'Completed',
  Blocked: 'Blocked',
  Failed: 'Failed'
} as const;
export type JobLifecycleStatus = (typeof JobLifecycleStatus)[keyof typeof JobLifecycleStatus];

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
  status?: string;
  statusNotes?: string | null;
  targetFile?: string | null;
  verificationMethod?: string | null;
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
  approvalPolicy?: PlanApprovalPolicy | number;
  currentTaskId?: string | null;
  progressHeartbeat?: string | null;
  lifecycleStatus?: JobLifecycleStatus | string | null;
  assignedAgentId?: string | null;
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

/**
 * What the Artifacts panel needs to know about an artifact, whichever surface produced it.
 *
 * Sessions and jobs both have artifacts and both render them through `ArtifactViewer`, and the two
 * payloads are field-for-field compatible on every property the viewer reads. Naming that shared shape
 * once is what lets the viewer take either without a cast at the call site or a second component that
 * has to be kept in step with the first.
 *
 * The owner identity is deliberately *not* here. A session artifact belongs to a session and a job
 * artifact belongs to a job, and the viewer must not be able to build a URL for an owner it was not
 * given — the API sends an explicit `downloadUrl` on both, and that is what the viewer uses.
 */
export interface ArtifactLike {
  id: string;
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

export interface SessionArtifact extends ArtifactLike {
  sessionId: string;
}

/**
 * One artifact of a job — the mirror of whatever its turn sessions produced.
 *
 * `sourceSessionId` is the turn session whose sync created the entry, and is null for a file found in
 * the job's own artifact directory. It is display metadata only: the bytes are fetched through
 * `downloadUrl`, which the job endpoint resolves from the source session's directory on the server.
 */
export interface JobArtifact extends ArtifactLike {
  jobId: string;
  sourceSessionId?: string | null;
}

// ── Job file changes (the Cockpit's Changes tab) ─────────────────────────────
//
// Mirrors `AgentMesh.Core.Models.JobChangeSetDtos`. The three wire vocabularies below
// (`JobChangeSourceKind`, `JobFileChangeStatus`, `JobChangeReasonCode`) are declared as string-literal
// unions plus const objects rather than TS enums, for the same reason the C# side uses constants
// instead of an enum: the values cross a wire a browser reads, and a renumbered enum is a breaking
// change nobody notices until every row is mislabelled. Every field past the first few is optional on
// the client even where the server always sends it — a dashboard newer than its API build is a normal
// deployment state here, and the panel degrades to "no changes yet" rather than to a crash.

/** Which source answered the question — see the C# `JobChangesSources`. */
export const JobChangeSourceKind = {
  /** `git diff` in a checkout the API can see: includes uncommitted and unpushed work. */
  LocalGit: 'localGit',
  /** GitHub's comparison for the pushed branch: committed work only. */
  GitHubCompare: 'gitHubCompare',
  /** The working checkpoint: paths, no line stats, no diff behind them. */
  Checkpoint: 'checkpoint',
} as const;
export type JobChangeSourceKind = (typeof JobChangeSourceKind)[keyof typeof JobChangeSourceKind];

/** A row's status. Unknown values are preserved verbatim, so this is `string` widened by the known set. */
export const JobFileChangeStatus = {
  Added: 'added',
  Modified: 'modified',
  Deleted: 'deleted',
  Renamed: 'renamed',
  Copied: 'copied',
  TypeChanged: 'typeChanged',
  Untracked: 'untracked',
  Ignored: 'ignored',
} as const;
export type JobFileChangeStatus =
  | (typeof JobFileChangeStatus)[keyof typeof JobFileChangeStatus]
  | string;

/** Machine-readable reason a change set came back empty — see the C# `JobChangeUnavailableReasons`. */
export const JobChangeReasonCode = {
  NoWorkspace: 'no_workspace',
  BranchMismatch: 'branch_mismatch',
  NoBranch: 'no_branch',
  UnknownRef: 'unknown_ref',
  GitUnavailable: 'git_unavailable',
  RemoteUnavailable: 'remote_unavailable',
  RemoteFailed: 'remote_failed',
  NoChanges: 'no_changes',
  PathNotInChangeSet: 'path_not_in_change_set',
} as const;
export type JobChangeReasonCode =
  | (typeof JobChangeReasonCode)[keyof typeof JobChangeReasonCode]
  | string;

/** Where the answer came from, and how much of the answer it is. */
export interface JobChangeSourceInfo {
  /** Null when nothing could be read; `reasonCode` then says why. */
  kind: JobChangeSourceKind | string | null;
  reasonCode?: JobChangeReasonCode | string | null;
  /** Human-readable and safe to show verbatim: it carries no paths or secrets. */
  message?: string | null;
  /** True when a better source was tried first and failed, so this answer is a fallback. */
  degraded?: boolean;
  /** True when uncommitted work could not be included (a remote-only read). */
  committedOnly?: boolean;
  truncated?: boolean;
  repository?: string | null;
}

export interface JobChangeStats {
  /** The real total, even when `files` is capped — do not substitute `files.length`. */
  filesChanged: number;
  additions: number;
  deletions: number;
  omittedFiles?: number;
}

export interface JobChangedFile {
  path: string;
  status: JobFileChangeStatus;
  /** Populated only for a rename/copy, so the row can render `old → new`. */
  previousPath?: string | null;
  /** Null for a binary file: "not countable" is a different fact from zero. */
  additions?: number | null;
  deletions?: number | null;
  binary?: boolean;
  /** False when the source names paths but cannot diff them (the checkpoint fallback). */
  patchAvailable?: boolean;
}

export interface JobChangeCommit {
  sha: string;
  shortSha: string;
  subject: string;
  authorName?: string | null;
  authorEmail?: string | null;
  committedAt?: string | null;
}

export interface JobChangeSet {
  jobId: string;
  branch?: string | null;
  baseBranch?: string | null;
  headSha?: string | null;
  /** The fork point, not the base branch's tip. Forward with `headSha` to pin a patch request. */
  baseSha?: string | null;
  stats: JobChangeStats;
  files: JobChangedFile[];
  source: JobChangeSourceInfo;
  commits: JobChangeCommit[];
  hasUncommittedChanges?: boolean;
  uncommittedPaths?: string[] | null;
  uncommittedCount?: number | null;
  generatedAt?: string | null;
  /** Why better sources were skipped, in precedence order. */
  notes?: string[] | null;
}

export interface JobChangePatch {
  jobId: string;
  branch?: string | null;
  baseBranch?: string | null;
  headSha?: string | null;
  baseSha?: string | null;
  /** Unified diff text; empty for a binary file. */
  patch: string;
  path?: string | null;
  previousPath?: string | null;
  status?: JobFileChangeStatus | null;
  binary?: boolean;
  empty?: boolean;
  truncated?: boolean;
  additions?: number | null;
  deletions?: number | null;
  contextLines?: number;
  source?: JobChangeSourceInfo | null;
}

// ── Job side panel ───────────────────────────────────────────────────────────

/**
 * Which tab the job Cockpit's side panel is showing. `null` (no panel open) is represented by the
 * caller as `JobPanelTab | null`, not by an empty string, so a switch statement over the tab cannot be
 * handed a value that silently renders nothing.
 *
 * One union for both tabs is the whole point: the panel is one column with one width, so "which panel"
 * must have exactly one owner rather than two booleans that can both be true.
 */
export type JobPanelTab = 'artifacts' | 'changes';

/** The tab strip's labels, in display order. */
export const JOB_PANEL_TABS: { id: JobPanelTab; label: string }[] = [
  { id: 'artifacts', label: 'Artifacts' },
  { id: 'changes', label: 'Changes' }
];

/**
 * The outcome of a patch request, as a discriminated union rather than a thrown error.
 *
 * The endpoint answers a refused patch with `404` plus a machine-readable `reasonCode`, and "this path
 * is not in the change set" / "no source here can produce a diff" are ordinary states the panel renders
 * as a sentence — not exceptions. Modelling them in the return type is what stops the component from
 * reaching for a try/catch and then printing the generic "failed to load" text for a file that simply
 * has no diff behind it. A thrown error is then only what it should be: a transport or server fault.
 */
export type JobChangePatchResult =
  | { found: true; patch: JobChangePatch }
  | {
      found: false;
      reasonCode?: string | null;
      message: string;
      notes?: string[] | null;
    };

export * from './timeline';




