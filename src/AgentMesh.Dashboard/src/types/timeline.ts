export type TimelineCardType =
  | 'JobInitialized'
  | 'DiscoveryQa'
  | 'PlanApproval'
  | 'TaskStep'
  | 'InternalConsultation'
  | 'TurnInterrupted'
  | 'Escalation3R'
  | 'PrReview';

export type TimelineCardStatus =
  | 'Active'
  | 'PendingInput'
  | 'Approved'
  | 'Rejected'
  | 'Resolved'
  | 'Closed';

export interface JobCardComment {
  id: string;
  cardId: string;
  senderId: string;
  senderType: 'operator' | 'agent' | 'system' | string;
  content: string;
  createdAt: string;
}

export interface JobTimelineCard {
  id: string;
  jobId: string;
  taskId?: string | null;
  cardType: TimelineCardType;
  status: TimelineCardStatus;
  title: string;
  structuredPayloadJson: string;
  createdAt: string;
  updatedAt: string;
  comments?: JobCardComment[];
}

export interface DiscoveryQuestion {
  id: string;
  question: string;
  options?: string[];
}

export interface DiscoveryPayload {
  questions: DiscoveryQuestion[];
  summary: string;
  /**
   * Answers keyed by question id. Cards written before November 2026 — and any card written straight from
   * an answered session question rather than from the Discovery Q&A activity — carry the answer array the
   * operator submitted instead, which `DiscoveryQaCard` still renders.
   */
  answers?: Record<string, string> | DiscoveryAnswerRow[];
  feedback?: string;
  /** The human-readable Q/A digest, present on cards written when a session question was answered. */
  content?: string;
}

export interface DiscoveryAnswerRow {
  question?: string;
  questionIndex?: number;
  selectedOptions?: string[];
  customText?: string;
}

export interface ProposedChecklistItem {
  taskId: string;
  title: string;
  targetFile: string;
  verificationMethod: string;
  status?: 'pending' | 'in_progress' | 'completed';
}

export interface PlanApprovalPayload {
  tasks: ProposedChecklistItem[];
  reasoning: string;
}

export interface TaskStepPayload {
  taskIndex: number;
  taskTitle: string;
  heartbeat?: string;
  stepDetails?: string;
  logs?: string;
  consultations?: Array<{
    personaId: string;
    query: string;
    response: string;
  }>;
  updatedAt?: string;
}

export interface PrReviewPayload {
  prUrl?: string | null;
  prNumber?: number | null;
  headSha?: string | null;
  authorPersonaId?: string | null;
  senderPersona?: string | null;
  targetBranch?: string | null;
  round?: number | null;
  reviewerId?: string | null;
  note?: string | null;
  /** The `open_pull_request` ledger variant puts its message here ("PR #1 opened: …"). */
  content?: string | null;
  source?: string | null;
}

export interface TurnInterruptedPayload {
  reason: string;
  requestedBy: string;
  lastOutput?: string;
  interruptedAt: string;
  availableActions?: string[];
}

export interface JobInitializedPayload {
  jobId: string;
  projectId?: string;
  workspacePath?: string;
  targetBranch?: string;
  assignedAgentId?: string;
  initialPrompt?: string;
  approvalPolicy?: string;
}

export interface AddCommentRequest {
  senderId: string;
  senderType: string;
  content: string;
}

export interface DiscoveryAnswersPayload {
  answers: Record<string, string>;
  feedback?: string;
}

export interface PlanDecisionPayload {
  approved: boolean;
  feedback?: string;
  approverId?: string;
}

/**
 * Body of `POST /api/jobs/{id}/reviews`. `approvedHeadSha` is deliberately not sent by the Cockpit:
 * the server binds the decision to the head SHA recorded on the gate's own card, which is the
 * durable value — a UI could only send a rendering that may already be stale.
 */
export interface PrReviewDecisionPayload {
  isApproved: boolean;
  reviewComments?: string;
  reviewerId?: string;
  prNumber?: number;
  approvedHeadSha?: string;
}

export interface AbortTurnRequest {
  reason: string;
  requestedBy?: string;
}

export interface InterventionRequest {
  action: 'resume' | 'retry_step' | 'realign' | string;
  additionalContext?: string;
}

export interface TemporalHealthState {
  workflowId: string;
  runId: string;
  attempt: number;
  status: string;
}
