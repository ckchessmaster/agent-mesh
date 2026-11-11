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
