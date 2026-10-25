import React, { useState, useRef, useEffect, useMemo, useCallback } from 'react';
import {
  GitBranch,
  CheckCircle2,
  Circle,
  Plus,
  Trash2,
  Send,
  Sparkles,
  Bot,
  User,
  ChevronRight,
  ChevronDown,
  Settings,
  ArrowDown,
  ListTodo,
  Check,
  Briefcase,
  Paperclip,
  FileText,
  Image as ImageIcon,
  X,
  ZoomIn,
  Download,
  AlertCircle,
  Square,
  Users,
  RefreshCw,
  Archive,
  RotateCcw,
  Clock,
  MessageSquareQuote,
  Play,
  HelpCircle
} from 'lucide-react';
import type { AgentSession, AskQuestionItem, JobMessage, JobTask, Persona, Project, ProjectJob, QuestionAnswerPayload, SessionAttachment } from '../types';
import { ProjectJobStatus } from '../types';
import { TeamMentionPalette, type MentionableMember } from './TeamMentionPalette';
import { MarkdownMessage } from './MarkdownMessage';
import { QuestionCard, parseTextQuestions } from './QuestionCard';
import { api } from '../services/api';

const MAX_ATTACHMENT_SIZE_BYTES = 25 * 1024 * 1024; // 25 MB
const MAX_ATTACHMENTS_PER_MESSAGE = 10;
const DEFAULT_ATTACHMENT_PROMPT = 'Please review the attached file(s).';

function formatAttachmentSize(bytes?: number): string {
  if (!bytes || bytes <= 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function isImageAttachment(contentType?: string, fileName?: string): boolean {
  const ct = (contentType || '').toLowerCase();
  const name = (fileName || '').toLowerCase();
  return ct.startsWith('image/') || /\.(png|jpg|jpeg|gif|webp|svg|bmp|ico)$/.test(name);
}

export function extractJobMessageAttachments(msg: JobMessage): SessionAttachment[] {
  if (Array.isArray(msg.attachments) && msg.attachments.length > 0) {
    return msg.attachments;
  }
  if (!msg.structuredPayloadJson) return [];
  try {
    const parsed = JSON.parse(msg.structuredPayloadJson);
    const rawList = Array.isArray(parsed?.attachments)
      ? parsed.attachments
      : Array.isArray(parsed?.Attachments)
      ? parsed.Attachments
      : null;

    if (Array.isArray(rawList)) {
      return rawList.map((item: any) => {
        const id = item.id || item.Id || '';
        const fileName = item.fileName || item.FileName || item.name || item.Name || '';
        const contentType = item.contentType || item.ContentType || '';
        const sizeBytes = typeof item.sizeBytes === 'number'
          ? item.sizeBytes
          : typeof item.SizeBytes === 'number'
          ? item.SizeBytes
          : (typeof item.size === 'number' ? item.size : 0);
        const s3Key = item.s3Key || item.S3Key || '';
        const targetId = id || fileName;
        const downloadUrl = item.downloadUrl || item.DownloadUrl || (targetId ? `/api/project-jobs/${encodeURIComponent(msg.jobId)}/attachments/${encodeURIComponent(targetId)}/content` : '');
        const createdAt = item.createdAt || item.CreatedAt || msg.createdAt;

        return {
          id,
          sessionId: msg.jobId,
          fileName,
          contentType,
          sizeBytes,
          s3Key,
          downloadUrl,
          createdAt
        };
      });
    }
  } catch {}
  return [];
}

function extractClipboardFiles(e: React.ClipboardEvent): File[] {
  const files: File[] = [];
  if (e.clipboardData?.files && e.clipboardData.files.length > 0) {
    return Array.from(e.clipboardData.files);
  }
  if (e.clipboardData?.items) {
    for (let i = 0; i < e.clipboardData.items.length; i++) {
      const item = e.clipboardData.items[i];
      if (item.kind === 'file') {
        const f = item.getAsFile();
        if (f) files.push(f);
      }
    }
  }
  return files;
}

export interface StagedJobAttachmentItem {
  localId: string;
  file: File;
  name: string;
  sizeBytes: number;
  contentType: string;
  previewUrl?: string;
  uploading: boolean;
  error?: string;
  uploaded?: SessionAttachment;
}

/**
 * Which pending task a persona would treat as its focus: the first one assigned to them,
 * else the first nobody owns (the "tell an agent to grab it" flow), else the first pending.
 * Mirrors ProjectJobsController.ResolveTargetTask so the cockpit can point a queued turn's
 * reassignment at the right task without another round trip.
 */
export function resolveTargetTaskId(tasks: JobTask[], personaId?: string | null): string | null {
  const pending = tasks.filter((t) => !t.isCompleted);
  if (pending.length === 0) return null;
  if (!personaId) return pending[0].id;
  const norm = personaId.trim().toLowerCase();
  const mine = pending.find((t) => (t.assignedPersonaId || '').trim().toLowerCase() === norm);
  if (mine) return mine.id;
  const unowned = pending.find((t) => !(t.assignedPersonaId || '').trim());
  return unowned ? unowned.id : pending[0].id;
}

export function getActiveMentionQuery(
  text: string,
  cursorPos: number
): { query: string; startIndex: number } | null {
  const beforeCursor = text.slice(0, cursorPos);
  const lastAtIndex = beforeCursor.lastIndexOf('@');
  if (lastAtIndex === -1) return null;

  // The @ symbol must be at start of string or preceded by whitespace
  if (lastAtIndex > 0 && !/\s/.test(beforeCursor[lastAtIndex - 1])) {
    return null;
  }

  const query = beforeCursor.slice(lastAtIndex + 1);

  // If there's whitespace inside the query, the mention has ended
  if (/\s/.test(query)) {
    return null;
  }

  return { query, startIndex: lastAtIndex };
}

interface JobCockpitProps {
  project: Project;
  job: ProjectJob;
  tasks: JobTask[];
  messages: JobMessage[];
  personas?: Persona[];
  onSendMessage: (content: string, attachments?: SessionAttachment[]) => Promise<void>;
  onAddTask: (title: string, requiresDiscussion?: boolean, attachments?: SessionAttachment[]) => Promise<void>;
  onToggleTask: (taskId: string, isCompleted: boolean) => Promise<void>;
  onUpdateTask?: (taskId: string, updates: { title?: string; isCompleted?: boolean; requiresDiscussion?: boolean; attachments?: SessionAttachment[] }) => Promise<void>;
  /** Assign/reassign a task. Passing null clears the assignee. */
  onAssignTask?: (taskId: string, personaId: string | null) => Promise<void>;
  onDeleteTask: (taskId: string) => Promise<void>;
  onCompleteJob: (resultSummary?: string) => Promise<void>;
  onArchiveJob?: () => Promise<void>;
  onReopenJob?: () => Promise<void>;
  onSelectTurnSession?: (sessionId: string) => void;
  onOpenLightbox?: (item: { url: string; fileName: string; sizeBytes?: number }) => void;
  onUploadAttachments?: (files: File[]) => Promise<SessionAttachment[]>;
  onEditProject?: () => void;
  onStopTurn?: (sessionId: string) => Promise<void>;
  onCancelQueuedTurn?: (turnId: string) => Promise<void> | void;
  onCancelQueuedTurns?: (personaId: string) => Promise<void> | void;
  sessions?: AgentSession[];
  onAnswerQuestion?: (
    sessionIdOrTargetId: string,
    payload: { toolCallId: string; answers: QuestionAnswerPayload[]; answerSummary: string }
  ) => Promise<void>;
}

export const JobCockpit: React.FC<JobCockpitProps> = ({
  project,
  job,
  tasks,
  messages,
  personas = [],
  sessions = [],
  onSendMessage,
  onAddTask,
  onToggleTask,
  onUpdateTask,
  onAssignTask,
  onDeleteTask,
  onCompleteJob,
  onArchiveJob,
  onReopenJob,
  onSelectTurnSession,
  onOpenLightbox,
  onUploadAttachments,
  onEditProject,
  onStopTurn,
  onCancelQueuedTurn,
  onCancelQueuedTurns,
  onAnswerQuestion
}) => {
  const [prompt, setPrompt] = useState('');
  const [newTaskTitle, setNewTaskTitle] = useState('');
  const [newTaskRequiresDiscussion, setNewTaskRequiresDiscussion] = useState(false);
  const [isSending, setIsSending] = useState(false);
  const [isAddingTask, setIsAddingTask] = useState(false);
  const [showCompleteConfirm, setShowCompleteConfirm] = useState(false);
  const [showArchiveConfirm, setShowArchiveConfirm] = useState(false);
  const [resultSummary, setResultSummary] = useState('');
  const [isCompleting, setIsCompleting] = useState(false);
  const [isArchiving, setIsArchiving] = useState(false);

  // Task checklist presentation state
  const [isTasksExpanded, setIsTasksExpanded] = useState(() =>
    typeof window !== 'undefined' ? window.innerWidth >= 768 : true
  );
  const [taskFilter, setTaskFilter] = useState<'all' | 'pending' | 'completed'>('pending');

  // Attachment & Drag-Drop State
  const [stagedAttachments, setStagedAttachments] = useState<StagedJobAttachmentItem[]>([]);
  const [isDragging, setIsDragging] = useState(false);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [internalLightbox, setInternalLightbox] = useState<{
    url: string;
    fileName: string;
    sizeBytes?: number;
  } | null>(null);

  const fileInputRef = useRef<HTMLInputElement>(null);

  // Task Attachment State
  const [newTaskAttachments, setNewTaskAttachments] = useState<StagedJobAttachmentItem[]>([]);
  const newTaskFileInputRef = useRef<HTMLInputElement>(null);
  const existingTaskFileInputRef = useRef<HTMLInputElement>(null);
  const [targetTaskForUpload, setTargetTaskForUpload] = useState<string | null>(null);

  // Assignment: which row is mid-request, and the last failure to surface inline.
  const [assigningTaskId, setAssigningTaskId] = useState<string | null>(null);
  const [assignError, setAssignError] = useState<string | null>(null);
  const [isUploadingTaskAttachment, setIsUploadingTaskAttachment] = useState<string | null>(null);

  // Team Member Autocomplete Palette State
  const [mentionState, setMentionState] = useState<{
    isOpen: boolean;
    query: string;
    startIndex: number;
  }>({
    isOpen: false,
    query: '',
    startIndex: -1
  });
  const [mentionHighlightedIndex, setMentionHighlightedIndex] = useState(0);
  const [showTeamSidebar, setShowTeamSidebar] = useState(() =>
    typeof window !== 'undefined' ? window.innerWidth >= 768 : true
  );

  // Question answering state
  const [submittingQuestionId, setSubmittingQuestionId] = useState<string | null>(null);
  const [answeredQuestionIds, setAnsweredQuestionIds] = useState<string[]>([]);

  const handleAnswerQuestion = async (
    msg: JobMessage,
    _questions: AskQuestionItem[],
    answers: QuestionAnswerPayload[]
  ) => {
    setSubmittingQuestionId(msg.id);
    try {
      const parts = answers.map((ans) => {
        const chosen = [
          ...(ans.selectedOptions || []),
          ...(ans.customText?.trim() ? [ans.customText.trim()] : [])
        ];
        return `Q: ${ans.question} -> A: ${chosen.length > 0 ? chosen.join(', ') : 'User skipped'}`;
      });
      const answerSummary = parts.join('\n');

      if (onAnswerQuestion) {
        await onAnswerQuestion(msg.turnSessionId || msg.id, {
          toolCallId: msg.id,
          answers,
          answerSummary
        });
      } else if (msg.turnSessionId) {
        await api.submitQuestionAnswer(msg.turnSessionId, {
          toolCallId: msg.id,
          answers
        });
      } else {
        await onSendMessage(
          `User answered question:\n${answerSummary}\nPlease proceed with this answer.`,
          []
        );
      }
      setAnsweredQuestionIds((prev) => [...prev, msg.id]);
    } catch (err) {
      console.error('Failed to submit question answer:', err);
    } finally {
      setSubmittingQuestionId(null);
    }
  };

  const pendingQuestion = useMemo(() => {
    // Only search backwards from the latest messages.
    // If a user message has already been sent after a question,
    // that question is superseded and cannot be an active "pending question".
    for (let i = messages.length - 1; i >= 0; i--) {
      const m = messages[i];
      if (m.senderType === 'user') {
        // Any question asked before this user message has already been replied to / superseded
        break;
      }
      let qs: AskQuestionItem[] = [];
      if (m.structuredPayloadJson) {
        try {
          const p = JSON.parse(m.structuredPayloadJson);
          if (Array.isArray(p.questions) && p.questions.length > 0) qs = p.questions;
        } catch {}
      }
      if (qs.length === 0) {
        qs = parseTextQuestions(m.content);
      }
      if (qs.length > 0) {
        const isAns = answeredQuestionIds.includes(m.id);
        if (!isAns) {
          return { msg: m, questions: qs };
        }
      }
    }
    return null;
  }, [messages, answeredQuestionIds]);

  const isSessionWaitingForInput = useMemo(() => {
    const lastMsgWithSession = [...messages].reverse().find((m) => m.turnSessionId);
    if (!lastMsgWithSession?.turnSessionId) return false;
    const lastUserMsgIdx = [...messages].reverse().findIndex((m) => m.senderType === 'user');
    const lastTurnMsgIdx = [...messages].reverse().findIndex((m) => m.id === lastMsgWithSession.id);
    if (lastUserMsgIdx !== -1 && lastUserMsgIdx < lastTurnMsgIdx) {
      return false;
    }
    return sessions.some(
      (s) => s.id === lastMsgWithSession.turnSessionId && s.status === 'waiting_for_input'
    );
  }, [sessions, messages]);

  const isWaitingForInput = Boolean(pendingQuestion || isSessionWaitingForInput);

  const messagesEndRef = useRef<HTMLDivElement>(null);
  const messagesContainerRef = useRef<HTMLDivElement>(null);
  const userScrolledUpRef = useRef(false);
  const [isScrolledUp, setIsScrolledUp] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  // Auto-scroll management: track user manual scrolling vs automatic tracking
  const handleMessagesScroll = () => {
    if (!messagesContainerRef.current) return;
    const { scrollTop, scrollHeight, clientHeight } = messagesContainerRef.current;
    const distanceFromBottom = scrollHeight - (scrollTop + clientHeight);
    if (distanceFromBottom < 80) {
      userScrolledUpRef.current = false;
      setIsScrolledUp(false);
    } else if (distanceFromBottom > 200) {
      userScrolledUpRef.current = true;
      setIsScrolledUp(true);
    }
  };

  const scrollToBottom = (instant = false) => {
    userScrolledUpRef.current = false;
    setIsScrolledUp(false);
    if (messagesContainerRef.current) {
      if (instant) {
        messagesContainerRef.current.scrollTop = messagesContainerRef.current.scrollHeight;
      } else if (typeof messagesContainerRef.current.scrollTo === 'function') {
        messagesContainerRef.current.scrollTo({
          top: messagesContainerRef.current.scrollHeight,
          behavior: 'smooth'
        });
      } else {
        messagesContainerRef.current.scrollTop = messagesContainerRef.current.scrollHeight;
      }
    } else if (typeof messagesEndRef.current?.scrollIntoView === 'function') {
      messagesEndRef.current.scrollIntoView({ behavior: instant ? 'auto' : 'smooth' });
    }
  };

  // Immediate feedback / active turn detection
  const activeTurnInfo = useMemo(() => {
    if (isSending) {
      return {
        isActive: true,
        personaName: 'Team',
        turnSessionId: undefined,
        statusText: 'Sending message and initiating turn...'
      };
    }
    if (messages.length === 0) return null;
    const lastMsg = messages[messages.length - 1];

    if (lastMsg.turnSessionId && sessions.length > 0) {
      const turnSession = sessions.find((s) => s.id === lastMsg.turnSessionId);
      if (turnSession && turnSession.status !== 'thinking' && turnSession.status !== 'waiting_for_input') {
        return null;
      }
    }

    if (lastMsg.senderType === 'user' && lastMsg.turnSessionId) {
      const mention = lastMsg.mentions?.[0];
      const personaObj =
        personas.find((p) => p.id === mention) ||
        project.assignedPersonas?.find((p) => p.id === mention);
      const isOffline = personaObj?.driverStatus === 'offline';
      return {
        isActive: true,
        isOffline,
        driverId: personaObj?.driverId,
        personaName: personaObj?.name || mention || 'Persona',
        turnSessionId: lastMsg.turnSessionId,
        statusText: isOffline ? 'is queued (waiting for runner connection)...' : 'is actively working on this task...'
      };
    }

    if (
      lastMsg.senderType === 'persona' &&
      lastMsg.turnSessionId &&
      (lastMsg.content.includes("I'm on it") || lastMsg.content.includes('starting work') || lastMsg.content.includes('queued'))
    ) {
      const personaObj =
        personas.find((p) => p.id === lastMsg.senderId) ||
        project.assignedPersonas?.find((p) => p.id === lastMsg.senderId) ||
        personas.find((p) => p.name === lastMsg.senderName);
      const isOffline = personaObj?.driverStatus === 'offline';
      return {
        isActive: true,
        isOffline,
        driverId: personaObj?.driverId,
        personaName: lastMsg.senderName || 'Persona',
        turnSessionId: lastMsg.turnSessionId,
        statusText: isOffline ? 'is queued (waiting for runner connection)...' : 'is actively working on this task...'
      };
    }

    return null;
  }, [messages, isSending, personas, project.assignedPersonas, sessions]);

  // Filtered task items for the structured checklist
  const uniqueTasks = useMemo(() => {
    return tasks.filter(
      (t, index, self) => index === self.findIndex((item) => item.id === t.id)
    );
  }, [tasks]);

  const filteredTasks = useMemo(() => {
    if (taskFilter === 'pending') return uniqueTasks.filter((t) => !t.isCompleted);
    if (taskFilter === 'completed') return uniqueTasks.filter((t) => t.isCompleted);
    return uniqueTasks;
  }, [uniqueTasks, taskFilter]);

  // Keep pinned to bottom when messages arrive, unless user has deliberately scrolled up
  useEffect(() => {
    if (userScrolledUpRef.current) return;
    if (messagesContainerRef.current) {
      messagesContainerRef.current.scrollTop = messagesContainerRef.current.scrollHeight;
      setIsScrolledUp(false);
    } else if (typeof messagesEndRef.current?.scrollIntoView === 'function') {
      messagesEndRef.current.scrollIntoView({ behavior: 'auto' });
    }
  }, [messages, activeTurnInfo]);

  // Team members list: priority given to project's assigned personas, fallback to all personas
  const teamMembers: MentionableMember[] = useMemo(() => {
    const assigned = project.assignedPersonas || [];
    const leadId = project.defaultLeadPersonaId;

    if (assigned.length > 0) {
      return assigned.map((p) => ({
        id: p.id,
        name: p.name,
        role: p.role,
        driverId: p.driverId,
        isLead: p.id === leadId,
        isAssigned: true
      }));
    }

    if (personas && personas.length > 0) {
      return personas.map((p) => ({
        id: p.id,
        name: p.name,
        role: p.role,
        driverId: p.driverId,
        isLead: p.id === leadId,
        isAssigned: false
      }));
    }

    return [];
  }, [project.assignedPersonas, project.defaultLeadPersonaId, personas]);

  // Assignee ids come from the server, but the cockpit shows display names.
  const memberNameFor = useCallback(
    (personaId?: string | null) => {
      if (!personaId) return '';
      const norm = personaId.trim().toLowerCase();
      const member = teamMembers.find((m) => m.id.toLowerCase() === norm);
      if (member) return member.name;
      const persona = personas.find((p) => p.id.toLowerCase() === norm);
      return persona?.name || personaId;
    },
    [teamMembers, personas]
  );

  const filteredMentionMembers = useMemo(() => {
    const q = mentionState.query.toLowerCase().trim();
    return teamMembers.filter((m) => {
      if (!q) return true;
      return (
        m.id.toLowerCase().includes(q) ||
        m.name.toLowerCase().includes(q) ||
        m.role.toLowerCase().includes(q) ||
        m.driverId.toLowerCase().includes(q)
      );
    });
  }, [teamMembers, mentionState.query]);

  const handleOpenLightbox = useCallback(
    (item: { url: string; fileName: string; sizeBytes?: number }) => {
      if (onOpenLightbox) {
        onOpenLightbox(item);
      } else {
        setInternalLightbox(item);
      }
    },
    [onOpenLightbox]
  );

  const stageFiles = useCallback(
    async (rawFiles: FileList | File[]) => {
      const incoming = Array.from(rawFiles || []);
      if (incoming.length === 0) return;

      setAttachmentError(null);

      if (stagedAttachments.length + incoming.length > MAX_ATTACHMENTS_PER_MESSAGE) {
        setAttachmentError(`Maximum of ${MAX_ATTACHMENTS_PER_MESSAGE} files can be attached per message.`);
        return;
      }

      for (const f of incoming) {
        if (f.size > MAX_ATTACHMENT_SIZE_BYTES) {
          setAttachmentError(`File "${f.name}" exceeds the 25 MB maximum file size limit.`);
          return;
        }
      }

      const newItems: StagedJobAttachmentItem[] = incoming.map((file, idx) => {
        const safeName = file.name || `attachment-${Date.now()}-${idx}.bin`;
        let previewUrl: string | undefined;
        if (isImageAttachment(file.type, safeName) && typeof URL !== 'undefined' && typeof URL.createObjectURL === 'function') {
          try {
            previewUrl = URL.createObjectURL(file);
          } catch {}
        }

        return {
          localId: `local-${Date.now()}-${idx}-${Math.random().toString(36).slice(2, 7)}`,
          file,
          name: safeName,
          sizeBytes: file.size,
          contentType: file.type || 'application/octet-stream',
          previewUrl,
          uploading: true
        };
      });

      setStagedAttachments((prev) => [...prev, ...newItems]);

      try {
        let uploadedDtos: SessionAttachment[];
        if (typeof onUploadAttachments === 'function') {
          uploadedDtos = await onUploadAttachments(incoming);
        } else {
          uploadedDtos = await api.uploadJobAttachments(job.id, incoming);
        }

        setStagedAttachments((prev) =>
          prev.map((item) => {
            const matched = newItems.find((n) => n.localId === item.localId);
            if (!matched) return item;

            const idx = newItems.indexOf(matched);
            const dto = uploadedDtos[idx] || uploadedDtos.find((d) => d.fileName === item.name);
            return {
              ...item,
              uploading: false,
              uploaded: dto
            };
          })
        );
      } catch (err: any) {
        const errMsg = err?.message || 'Failed to upload attachments';
        setAttachmentError(errMsg);
        setStagedAttachments((prev) =>
          prev.map((item) => {
            const isOneOfThese = newItems.some((n) => n.localId === item.localId);
            return isOneOfThese ? { ...item, uploading: false, error: errMsg } : item;
          })
        );
      }
    },
    [stagedAttachments.length, onUploadAttachments, job.id]
  );

  const removeStagedAttachment = (localId: string) => {
    setStagedAttachments((prev) => {
      const match = prev.find((a) => a.localId === localId);
      if (match?.previewUrl && typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
        try {
          URL.revokeObjectURL(match.previewUrl);
        } catch {}
      }
      return prev.filter((a) => a.localId !== localId);
    });
  };

  const handlePaste = useCallback(
    (e: React.ClipboardEvent) => {
      const files = extractClipboardFiles(e);
      if (files.length > 0) {
        e.preventDefault();
        void stageFiles(files);
      }
    },
    [stageFiles]
  );

  const checkMentionTrigger = (text: string, cursorPos: number) => {
    const mentionInfo = getActiveMentionQuery(text, cursorPos);
    if (mentionInfo) {
      setMentionState({
        isOpen: true,
        query: mentionInfo.query,
        startIndex: mentionInfo.startIndex
      });
      setMentionHighlightedIndex(0);
    } else {
      setMentionState((prev) => (prev.isOpen ? { ...prev, isOpen: false } : prev));
    }
  };

  const handlePromptChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const val = e.target.value;
    setPrompt(val);
    const cursorPos = e.target.selectionStart ?? val.length;
    checkMentionTrigger(val, cursorPos);
  };

  const handleCursorActivity = (e: React.SyntheticEvent<HTMLTextAreaElement>) => {
    const target = e.currentTarget;
    checkMentionTrigger(target.value, target.selectionStart ?? target.value.length);
  };

  const handleSelectMention = (member: MentionableMember) => {
    const cursorPos = textareaRef.current?.selectionStart ?? prompt.length;
    const mentionInfo = getActiveMentionQuery(prompt, cursorPos);

    let nextPrompt: string;
    let nextCursorPos: number;

    if (mentionInfo) {
      const before = prompt.slice(0, mentionInfo.startIndex);
      const after = prompt.slice(cursorPos);
      const afterClean = after.startsWith(' ') ? after.slice(1) : after;
      const mentionTag = `@${member.id} `;
      nextPrompt = `${before}${mentionTag}${afterClean}`;
      nextCursorPos = before.length + mentionTag.length;
    } else {
      const mentionTag = `@${member.id} `;
      nextPrompt = prompt ? `${prompt} ${mentionTag}` : mentionTag;
      nextCursorPos = nextPrompt.length;
    }

    setPrompt(nextPrompt);
    setMentionState({ isOpen: false, query: '', startIndex: -1 });
    setMentionHighlightedIndex(0);

    requestAnimationFrame(() => {
      if (textareaRef.current) {
        textareaRef.current.focus();
        textareaRef.current.setSelectionRange(nextCursorPos, nextCursorPos);
      }
    });
  };

  const handlePromptKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (mentionState.isOpen && filteredMentionMembers.length > 0) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setMentionHighlightedIndex((prev) => (prev + 1) % filteredMentionMembers.length);
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        setMentionHighlightedIndex(
          (prev) => (prev - 1 + filteredMentionMembers.length) % filteredMentionMembers.length
        );
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        setMentionState((prev) => ({ ...prev, isOpen: false }));
        return;
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault();
        const selected =
          filteredMentionMembers[mentionHighlightedIndex] || filteredMentionMembers[0];
        if (selected) {
          handleSelectMention(selected);
        }
        return;
      }
    } else if (mentionState.isOpen && e.key === 'Escape') {
      e.preventDefault();
      setMentionState((prev) => ({ ...prev, isOpen: false }));
      return;
    }

    // Normal Enter without Shift to send message
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void handleSend();
    }
  };

  const readyStagedAttachments = useMemo(
    () => stagedAttachments.filter((a) => !a.uploading && !a.error),
    [stagedAttachments]
  );

  const hasPendingUploads = stagedAttachments.some((a) => a.uploading);
  const isCompleted = job.status === ProjectJobStatus.Completed;
  const isArchived = job.status === ProjectJobStatus.Archived;
  const isClosed = isArchived;
  const canSend = Boolean(prompt.trim() || readyStagedAttachments.length > 0) && !hasPendingUploads && !isSending && !isClosed;

  const handleSend = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    if (!canSend) return;

    const text = prompt.trim();
    const attachmentsToSend: SessionAttachment[] = readyStagedAttachments.map(
      (item) =>
        item.uploaded || {
          id: item.localId,
          sessionId: job.id,
          fileName: item.name,
          contentType: item.contentType,
          sizeBytes: item.sizeBytes,
          s3Key: `jobs/${job.id}/attachments/${item.localId}/${item.name}`,
          downloadUrl: item.previewUrl || `/api/project-jobs/${encodeURIComponent(job.id)}/attachments/${encodeURIComponent(item.localId)}/content`,
          createdAt: new Date().toISOString()
        }
    );

    if (pendingQuestion && text && attachmentsToSend.length === 0) {
      setPrompt('');
      const answers: QuestionAnswerPayload[] = pendingQuestion.questions.map((q, idx) => ({
        questionIndex: idx,
        question: q.question,
        selectedOptions: [],
        customText: text
      }));
      await handleAnswerQuestion(pendingQuestion.msg, pendingQuestion.questions, answers);
      return;
    }

    const effectiveText = text || (attachmentsToSend.length > 0 ? DEFAULT_ATTACHMENT_PROMPT : '');

    setPrompt('');
    setStagedAttachments([]);
    setAttachmentError(null);
    setIsSending(true);

    try {
      await onSendMessage(effectiveText, attachmentsToSend.length > 0 ? attachmentsToSend : undefined);
    } catch (err: any) {
      setAttachmentError(err?.message || 'Failed to send message');
    } finally {
      setIsSending(false);
    }
  };

  const handleNewTaskFiles = async (rawFiles: FileList | File[] | null) => {
    const incoming = Array.from(rawFiles || []);
    if (incoming.length === 0) return;

    const newItems: StagedJobAttachmentItem[] = incoming.map((f, idx) => ({
      localId: `new-task-${Date.now()}-${idx}`,
      file: f,
      name: f.name,
      sizeBytes: f.size,
      contentType: f.type,
      uploading: true
    }));

    setNewTaskAttachments((prev) => [...prev, ...newItems]);

    try {
      const uploaded = onUploadAttachments
        ? await onUploadAttachments(incoming)
        : await api.uploadJobAttachments(job.id, incoming);

      setNewTaskAttachments((prev) =>
        prev.map((item) => {
          const match = uploaded.find((u) => u.fileName === item.name);
          return match ? { ...item, uploading: false, uploaded: match } : item;
        })
      );
    } catch (err: any) {
      setNewTaskAttachments((prev) =>
        prev.map((item) => {
          const isIncoming = incoming.some((f) => f.name === item.name);
          return isIncoming ? { ...item, uploading: false, error: err?.message || 'Upload failed' } : item;
        })
      );
    }
  };

  const removeNewTaskAttachment = (localId: string) => {
    setNewTaskAttachments((prev) => prev.filter((a) => a.localId !== localId));
  };

  const handleUploadExistingTaskFiles = async (taskId: string, rawFiles: FileList | File[] | null) => {
    const incoming = Array.from(rawFiles || []);
    if (incoming.length === 0) return;

    setIsUploadingTaskAttachment(taskId);
    try {
      const uploaded = await api.uploadTaskAttachments(job.id, taskId, incoming);
      if (onUpdateTask) {
        const target = tasks.find((t) => t.id === taskId);
        const existingAtts = target?.attachments || [];
        const merged = [...existingAtts];
        for (const u of uploaded) {
          if (!merged.some((m) => m.id === u.id)) {
            merged.push(u);
          }
        }
        await onUpdateTask(taskId, { attachments: merged });
      }
    } catch (err: any) {
      setAttachmentError(err?.message || 'Failed to upload task attachment');
    } finally {
      setIsUploadingTaskAttachment(null);
      setTargetTaskForUpload(null);
    }
  };

  const handleAddTaskSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newTaskTitle.trim() || isAddingTask) return;
    const t = newTaskTitle.trim();
    const reqDisc = newTaskRequiresDiscussion;
    const uploadedAttachments = newTaskAttachments
      .map((a) => a.uploaded)
      .filter((u): u is SessionAttachment => Boolean(u));

    setNewTaskTitle('');
    setNewTaskRequiresDiscussion(false);
    setNewTaskAttachments([]);
    setIsAddingTask(true);
    try {
      if (uploadedAttachments.length > 0) {
        await onAddTask(t, reqDisc, uploadedAttachments);
      } else {
        await onAddTask(t, reqDisc);
      }
    } finally {
      setIsAddingTask(false);
    }
  };

  const handleAssignTask = async (taskId: string, rawValue: string) => {
    if (!onAssignTask) return;
    const personaId = rawValue ? rawValue : null;
    setAssigningTaskId(taskId);
    setAssignError(null);
    try {
      await onAssignTask(taskId, personaId);
    } catch (err) {
      // Keep the failure inline on the row that caused it: a silent revert looks like the
      // click did nothing, which is how people end up clicking assign three times.
      setAssignError(err instanceof Error ? err.message : 'Failed to assign task');
    } finally {
      setAssigningTaskId(null);
    }
  };

  const handleProceedToCoding = async (targetTask?: JobTask, personaId?: string | null) => {
    if (targetTask && onUpdateTask && targetTask.requiresDiscussion) {
      await onUpdateTask(targetTask.id, { requiresDiscussion: false });
    }
    const mentionPrefix = personaId ? `@${personaId} ` : '';
    const proceedText = `${mentionPrefix}Discussion approved — proceed with implementation of "${targetTask?.title || 'the task'}".`;
    await onSendMessage(proceedText);
  };

  const handleMentionInsert = (personaId: string) => {
    const mentionTag = `@${personaId} `;
    setPrompt((prev) => {
      if (prev.endsWith(' ') || prev.length === 0) {
        return prev + mentionTag;
      }
      return `${prev} ${mentionTag}`;
    });
    textareaRef.current?.focus();
  };

  const handleConfirmComplete = async () => {
    setIsCompleting(true);
    try {
      await onCompleteJob(resultSummary.trim() || undefined);
      setShowCompleteConfirm(false);
    } finally {
      setIsCompleting(false);
    }
  };

  const handleConfirmArchive = async () => {
    if (!onArchiveJob) return;
    setIsArchiving(true);
    try {
      await onArchiveJob();
      setShowArchiveConfirm(false);
    } finally {
      setIsArchiving(false);
    }
  };

  const handleConfirmReopen = async () => {
    if (!onReopenJob) return;
    setIsArchiving(true);
    try {
      await onReopenJob();
    } finally {
      setIsArchiving(false);
    }
  };

  const completedCount = uniqueTasks.filter((t) => t.isCompleted).length;
  const progressPercent = uniqueTasks.length > 0 ? Math.round((completedCount / uniqueTasks.length) * 100) : 0;
  const firstPendingTask = uniqueTasks.find((t) => !t.isCompleted);
  const firstPendingTaskId = firstPendingTask?.id;
  const firstPendingTaskIndex = firstPendingTask ? uniqueTasks.findIndex((t) => t.id === firstPendingTaskId) : -1;

  return (
    <div
      className="flex-1 flex flex-col h-full bg-slate-950 overflow-hidden relative"
      onDragOver={(e) => {
        e.preventDefault();
        setIsDragging(true);
      }}
      onDragLeave={(e) => {
        e.preventDefault();
        setIsDragging(false);
      }}
      onDrop={(e) => {
        e.preventDefault();
        setIsDragging(false);
        if (e.dataTransfer?.files && e.dataTransfer.files.length > 0) {
          void stageFiles(e.dataTransfer.files);
        }
      }}
    >
      {/* Drag & Drop Overlay */}
      {isDragging && (
        <div
          className="absolute inset-3 z-40 rounded-2xl bg-sky-950/90 border-2 border-dashed border-sky-400 flex flex-col items-center justify-center text-sky-200 pointer-events-none"
          data-testid="job-dropzone-overlay"
        >
          <Paperclip className="w-10 h-10 mb-2 text-sky-400 animate-bounce" />
          <span className="text-sm font-semibold">Drop images or files to attach to this job</span>
          <span className="text-xs text-sky-300/80 mt-1">Up to 10 files (max 25 MB each)</span>
        </div>
      )}

      {/* Header Bar */}
      <div className="px-3 sm:px-6 py-2.5 sm:py-4 border-b border-slate-800/80 bg-slate-900/60 backdrop-blur-sm flex items-center justify-between gap-2.5 sm:gap-4 shrink-0">
        <div className="flex items-center gap-2.5 sm:gap-3 min-w-0">
          <div className="w-8 h-8 sm:w-9 sm:h-9 rounded-xl bg-sky-500/15 text-sky-400 border border-sky-500/30 flex items-center justify-center shrink-0">
            <Briefcase className="w-4 h-4" />
          </div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-1.5 sm:gap-2">
              <span className="text-[11px] font-mono px-2 py-0.5 rounded-md bg-slate-800 text-slate-300 font-medium">
                {project.name}
              </span>
              {project.workspaceType === 0 && job.gitBranchName && (
                <span className="text-[11px] font-mono px-2 py-0.5 rounded-md bg-emerald-500/10 text-emerald-400 border border-emerald-500/30 flex items-center gap-1 font-medium">
                  <GitBranch className="w-3 h-3" />
                  {job.gitBranchName}
                </span>
              )}
              {isArchived ? (
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-amber-500/20 text-amber-300 border border-amber-500/40 flex items-center gap-1">
                  <Archive className="w-3 h-3" /> Archived
                </span>
              ) : isCompleted ? (
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-emerald-500/20 text-emerald-300 border border-emerald-500/40 flex items-center gap-1">
                  <Check className="w-3 h-3" /> Completed
                </span>
              ) : (
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-sky-500/20 text-sky-300 border border-sky-500/40">
                  Active Job
                </span>
              )}
              {isWaitingForInput && (
                <span
                  className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-amber-500/20 text-amber-300 border border-amber-500/40 flex items-center gap-1 animate-pulse"
                  data-testid="waiting-for-input-badge"
                >
                  <HelpCircle className="w-3 h-3 text-amber-400" /> Waiting for Input
                </span>
              )}
              {job.priority !== undefined && (
                <span
                  className={`text-[11px] font-semibold px-2 py-0.5 rounded-md border flex items-center gap-1 ${
                    job.priority === 3
                      ? 'bg-rose-500/20 text-rose-300 border-rose-500/40'
                      : job.priority === 2
                      ? 'bg-amber-500/20 text-amber-300 border-amber-500/40'
                      : job.priority === 0
                      ? 'bg-slate-800 text-slate-400 border-slate-700'
                      : 'bg-sky-500/20 text-sky-300 border-sky-500/40'
                  }`}
                  data-testid="job-priority-badge"
                >
                  {job.priority === 3 ? 'Urgent' : job.priority === 2 ? 'High' : job.priority === 0 ? 'Low' : 'Normal'}
                </span>
              )}
            </div>
            <h1 className="text-base font-bold text-slate-100 truncate mt-0.5">
              {job.title}
            </h1>
          </div>
        </div>

        {/* Assigned Personas & Actions */}
        <div className="flex items-center gap-2 sm:gap-3 shrink-0">
          <div className="hidden lg:flex items-center gap-1.5 bg-slate-950/80 border border-slate-800 px-2.5 py-1.5 rounded-xl">
            <span className="text-[11px] font-semibold text-slate-400 mr-1">Team:</span>
            {project.assignedPersonas?.map((p) => (
              <span
                key={p.id}
                title={`${p.name} (${p.role})`}
                className="text-[11px] font-medium px-2 py-0.5 rounded-md bg-slate-800/80 text-sky-300 border border-slate-700/60"
              >
                @{p.id}
              </span>
            ))}
            {onEditProject && (
              <button
                type="button"
                onClick={onEditProject}
                title="Edit Project Details & Team Members"
                className="ml-1 p-1 rounded-md text-slate-400 hover:text-sky-300 hover:bg-slate-800 transition cursor-pointer"
                data-testid="cockpit-edit-project-btn"
              >
                <Settings className="w-3.5 h-3.5" />
              </button>
            )}
          </div>

          <button
            type="button"
            onClick={() => setShowTeamSidebar((prev) => !prev)}
            title={showTeamSidebar ? 'Hide Team & Activity Sidebar' : 'Show Team & Activity Sidebar'}
            data-testid="toggle-team-sidebar-btn"
            className={`inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-xl border text-xs font-semibold transition cursor-pointer ${
              showTeamSidebar
                ? 'bg-sky-500/15 border-sky-500/30 text-sky-300'
                : 'bg-slate-950/80 border-slate-800 text-slate-400 hover:text-slate-200'
            }`}
          >
            <Users className="w-3.5 h-3.5" />
            <span>Team ({teamMembers.length})</span>
            {activeTurnInfo?.isActive && (
              <span className="w-2 h-2 rounded-full bg-sky-400 animate-ping shrink-0" />
            )}
          </button>

          {isArchived ? (
            onReopenJob && (
              <button
                type="button"
                onClick={handleConfirmReopen}
                disabled={isArchiving}
                className="px-3.5 py-1.5 bg-sky-500/15 hover:bg-sky-500/25 border border-sky-500/40 text-sky-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                data-testid="cockpit-reopen-job-btn"
              >
                <RotateCcw className="w-3.5 h-3.5" />
                <span>{isArchiving ? 'Reopening...' : 'Reopen Job'}</span>
              </button>
            )
          ) : (
            <div className="flex items-center gap-2">
              {!isCompleted ? (
                <div>
                  {showCompleteConfirm ? (
                    <div className="flex items-center gap-2 bg-slate-900 border border-emerald-500/40 p-1.5 rounded-xl">
                      <input
                        type="text"
                        value={resultSummary}
                        onChange={(e) => setResultSummary(e.target.value)}
                        placeholder="Summary of deliverables..."
                        className="bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1 text-xs text-slate-100 placeholder-slate-500 focus:outline-none w-48"
                      />
                      <button
                        type="button"
                        onClick={() => setShowCompleteConfirm(false)}
                        className="px-2.5 py-1 text-xs text-slate-400 hover:text-slate-200"
                      >
                        Cancel
                      </button>
                      <button
                        type="button"
                        onClick={handleConfirmComplete}
                        disabled={isCompleting}
                        className="px-3 py-1 bg-emerald-500 hover:bg-emerald-400 text-slate-950 text-xs font-bold rounded-lg shadow-sm"
                      >
                        {isCompleting ? 'Merging...' : 'Confirm & Merge'}
                      </button>
                    </div>
                  ) : (
                    <button
                      type="button"
                      onClick={() => setShowCompleteConfirm(true)}
                      className="px-3.5 py-1.5 bg-emerald-500/15 hover:bg-emerald-500/25 border border-emerald-500/40 text-emerald-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                    >
                      <CheckCircle2 className="w-3.5 h-3.5" />
                      <span>Complete & Merge Job</span>
                    </button>
                  )}
                </div>
              ) : (
                onReopenJob && (
                  <button
                    type="button"
                    onClick={handleConfirmReopen}
                    disabled={isArchiving}
                    className="px-3.5 py-1.5 bg-sky-500/15 hover:bg-sky-500/25 border border-sky-500/40 text-sky-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                    data-testid="cockpit-reopen-job-btn"
                  >
                    <RotateCcw className="w-3.5 h-3.5" />
                    <span>{isArchiving ? 'Reopening...' : 'Reopen Job'}</span>
                  </button>
                )
              )}

              {onArchiveJob && (
                <div>
                  {showArchiveConfirm ? (
                    <div className="flex items-center gap-2 bg-slate-900 border border-amber-500/40 p-1.5 rounded-xl">
                      <span className="text-xs text-amber-200">Archive job?</span>
                      <button
                        type="button"
                        onClick={() => setShowArchiveConfirm(false)}
                        className="px-2 py-1 text-xs text-slate-400 hover:text-slate-200"
                      >
                        Cancel
                      </button>
                      <button
                        type="button"
                        onClick={handleConfirmArchive}
                        disabled={isArchiving}
                        className="px-3 py-1 bg-amber-500 hover:bg-amber-400 text-slate-950 text-xs font-bold rounded-lg shadow-sm"
                        data-testid="cockpit-confirm-archive-btn"
                      >
                        {isArchiving ? 'Archiving...' : 'Confirm Archive'}
                      </button>
                    </div>
                  ) : (
                    <button
                      type="button"
                      onClick={() => setShowArchiveConfirm(true)}
                      title="Archive and close this job"
                      className="px-3 py-1.5 bg-slate-800/80 hover:bg-slate-700/80 border border-slate-700 text-slate-300 text-xs font-semibold rounded-xl transition flex items-center gap-1.5 cursor-pointer shadow-sm"
                      data-testid="cockpit-archive-job-btn"
                    >
                      <Archive className="w-3.5 h-3.5 text-slate-400" />
                      <span>Archive</span>
                    </button>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      {/* Redesigned Task Checklist */}
      <div className="px-3 sm:px-6 py-2 sm:py-3 border-b border-slate-800/60 bg-slate-900/50">
        {/* Task Header & Progress Bar */}
        <div className="flex items-center justify-between gap-4 mb-2">
          <div className="flex items-center gap-2.5 min-w-0">
            <button
              type="button"
              onClick={() => setIsTasksExpanded((prev) => !prev)}
              className="text-slate-400 hover:text-slate-200 p-0.5 rounded transition flex items-center gap-1.5 cursor-pointer"
              title={isTasksExpanded ? 'Collapse task list' : 'Expand task list'}
              data-testid="toggle-tasks-btn"
            >
              {isTasksExpanded ? (
                <ChevronDown className="w-4 h-4 text-slate-400" />
              ) : (
                <ChevronRight className="w-4 h-4 text-slate-400" />
              )}
              <ListTodo className="w-4 h-4 text-sky-400 shrink-0" />
              <span className="text-xs font-semibold text-slate-200">Tasks & Objectives</span>
            </button>
            <span className="text-[11px] font-mono px-2 py-0.5 rounded-full bg-slate-800/80 text-slate-300 border border-slate-700/50">
              ({completedCount}/{uniqueTasks.length} completed • {progressPercent}%)
            </span>
            {firstPendingTask && (
              <span
                className={`hidden xl:inline-flex items-center gap-1.5 text-[11px] font-medium px-2.5 py-0.5 rounded-lg border max-w-xs truncate ${
                  firstPendingTask.requiresDiscussion
                    ? 'bg-amber-500/10 text-amber-300 border-amber-500/30'
                    : 'bg-sky-500/10 text-sky-300 border-sky-500/25'
                }`}
                title={`Current sequential target: ${firstPendingTask.title}${firstPendingTask.requiresDiscussion ? ' (Discussion Required)' : ''}`}
                data-testid="current-focus-badge"
              >
                <span className={`w-1.5 h-1.5 rounded-full shrink-0 ${firstPendingTask.requiresDiscussion ? 'bg-amber-400 animate-pulse' : 'bg-sky-400 animate-pulse'}`} />
                <span className="text-slate-400 font-normal">Focus:</span>
                {firstPendingTask.requiresDiscussion && (
                  <span className="text-amber-400 font-semibold" data-testid="focus-discuss-tag">[Discuss]</span>
                )}
                <span className="truncate">#{firstPendingTaskIndex + 1} {firstPendingTask.title}</span>
              </span>
            )}
          </div>

          <div className="flex items-center gap-3 shrink-0">
            {isTasksExpanded && (
              <div className="flex items-center bg-slate-950/80 border border-slate-800 rounded-lg p-0.5 text-[11px]">
                <button
                  type="button"
                  onClick={() => setTaskFilter('all')}
                  className={`px-2 py-0.5 rounded-md transition ${
                    taskFilter === 'all'
                      ? 'bg-slate-800 text-sky-300 font-semibold shadow-sm'
                      : 'text-slate-400 hover:text-slate-200'
                  }`}
                >
                  All ({uniqueTasks.length})
                </button>
                <button
                  type="button"
                  onClick={() => setTaskFilter('pending')}
                  className={`px-2 py-0.5 rounded-md transition ${
                    taskFilter === 'pending'
                      ? 'bg-slate-800 text-sky-300 font-semibold shadow-sm'
                      : 'text-slate-400 hover:text-slate-200'
                  }`}
                >
                  Pending ({uniqueTasks.length - completedCount})
                </button>
                <button
                  type="button"
                  onClick={() => setTaskFilter('completed')}
                  className={`px-2 py-0.5 rounded-md transition ${
                    taskFilter === 'completed'
                      ? 'bg-slate-800 text-sky-300 font-semibold shadow-sm'
                      : 'text-slate-400 hover:text-slate-200'
                  }`}
                >
                  Done ({completedCount})
                </button>
              </div>
            )}
            <div className="w-32 h-1.5 bg-slate-800 rounded-full overflow-hidden shrink-0">
              <div
                className="h-full bg-emerald-500 transition-all duration-300 rounded-full"
                style={{ width: `${progressPercent}%` }}
              />
            </div>
          </div>
        </div>

        {/* Expandable Task Content */}
        {isTasksExpanded && (
          <div className="space-y-2 mt-2">
            {/* Task Item Rows */}
            {filteredTasks.length === 0 ? (
              <div className="py-3 px-4 rounded-xl bg-slate-950/40 border border-slate-800/60 text-center text-xs text-slate-500">
                {uniqueTasks.length === 0
                  ? 'No tasks yet. Add an objective below to track deliverables for this job.'
                  : taskFilter === 'pending' && completedCount === uniqueTasks.length
                  ? `All ${uniqueTasks.length} tasks completed! Switch to All or Done to review.`
                  : `No ${taskFilter} tasks found.`}
              </div>
            ) : (
              <div className="max-h-56 overflow-y-auto space-y-1.5 pr-1" data-testid="job-tasks-list">
                {filteredTasks.map((task, idx) => {
                  const isCurrentFocus = !task.isCompleted && task.id === firstPendingTaskId;

                  return (
                    <div
                      key={task.id}
                      className={`flex items-center justify-between gap-3 px-3 py-2 rounded-xl border text-xs transition group ${
                        task.isCompleted
                          ? 'bg-emerald-950/15 border-emerald-500/25 text-emerald-300/80'
                          : isCurrentFocus
                          ? 'bg-sky-950/25 border-sky-500/40 text-slate-100 shadow-sm ring-1 ring-sky-500/20'
                          : 'bg-slate-950/60 border-slate-800/80 text-slate-300 hover:border-slate-700/80'
                      }`}
                    >
                      <div className="flex items-center gap-2.5 min-w-0 flex-1">
                        <button
                          type="button"
                          onClick={() => onToggleTask(task.id, !task.isCompleted)}
                          className="cursor-pointer text-slate-400 hover:text-sky-400 transition shrink-0 p-0.5"
                          title={task.isCompleted ? 'Mark as incomplete' : 'Mark as complete'}
                          data-testid={`toggle-task-${task.id}`}
                        >
                          {task.isCompleted ? (
                            <CheckCircle2 className="w-4 h-4 text-emerald-400" />
                          ) : (
                            <Circle className="w-4 h-4 text-slate-500 hover:text-sky-400" />
                          )}
                        </button>
                        <span className={`font-mono text-[10px] shrink-0 ${isCurrentFocus ? 'text-sky-400 font-semibold' : 'text-slate-500'}`}>
                          #{idx + 1}
                        </span>
                        <div className="min-w-0 flex-1">
                          <span
                            className={`break-words leading-relaxed ${
                              task.isCompleted
                                ? 'line-through text-slate-400'
                                : isCurrentFocus
                                ? 'text-slate-100 font-semibold'
                                : 'text-slate-200 font-medium'
                            }`}
                          >
                            {task.title}
                          </span>
                          {/* Task Attachments Badge List */}
                          {task.attachments && task.attachments.length > 0 && (
                            <div className="flex flex-wrap gap-1.5 mt-1" data-testid={`task-attachments-${task.id}`}>
                              {task.attachments.map((att) => {
                                const isImg = isImageAttachment(att.contentType, att.fileName);
                                return (
                                  <div
                                    key={att.id}
                                    className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-slate-900 border border-slate-800 text-[10px] text-slate-300 group/taskatt"
                                    data-testid={`task-att-${att.id}`}
                                  >
                                    {isImg ? (
                                      <button
                                        type="button"
                                        onClick={() => handleOpenLightbox({ url: att.downloadUrl, fileName: att.fileName, sizeBytes: att.sizeBytes })}
                                        className="cursor-pointer flex items-center gap-1 hover:text-sky-300"
                                        title="View image"
                                      >
                                        <ImageIcon className="w-3 h-3 text-sky-400 shrink-0" />
                                        <span className="truncate max-w-[110px]">{att.fileName}</span>
                                      </button>
                                    ) : (
                                      <a
                                        href={att.downloadUrl}
                                        target="_blank"
                                        rel="noreferrer"
                                        className="flex items-center gap-1 text-slate-300 hover:text-sky-300"
                                        title="Download attachment"
                                      >
                                        <Paperclip className="w-3 h-3 text-slate-400 shrink-0" />
                                        <span className="truncate max-w-[110px]">{att.fileName}</span>
                                      </a>
                                    )}
                                    {att.sizeBytes > 0 && (
                                      <span className="text-[9px] text-slate-500 font-mono">
                                        ({formatAttachmentSize(att.sizeBytes)})
                                      </span>
                                    )}
                                    {!task.isCompleted && onUpdateTask && (
                                      <button
                                        type="button"
                                        onClick={async (e) => {
                                          e.stopPropagation();
                                          try {
                                            await api.deleteTaskAttachment(job.id, task.id, att.id);
                                          } catch { }
                                          const remaining = (task.attachments || []).filter((a) => a.id !== att.id);
                                          await onUpdateTask(task.id, { attachments: remaining });
                                        }}
                                        className="text-slate-500 hover:text-rose-400 p-0.5 rounded transition cursor-pointer ml-0.5 opacity-0 group-hover/taskatt:opacity-100"
                                        title="Remove attachment"
                                        data-testid={`delete-task-att-${att.id}`}
                                      >
                                        <X className="w-2.5 h-2.5" />
                                      </button>
                                    )}
                                  </div>
                                );
                              })}
                            </div>
                          )}
                        </div>
                      </div>

                      <div className="flex items-center gap-2 shrink-0">
                        {!task.isCompleted && onAssignTask ? (
                          <select
                            value={task.assignedPersonaId || ''}
                            disabled={assigningTaskId === task.id}
                            onChange={(e) => handleAssignTask(task.id, e.target.value)}
                            onClick={(e) => e.stopPropagation()}
                            aria-label={`Assignee for task ${task.title}`}
                            data-testid={`assign-task-${task.id}`}
                            title={
                              task.assignedPersonaId
                                ? `Assigned to @${memberNameFor(task.assignedPersonaId)} — change assignee`
                                : 'Unassigned — pick who owns this task'
                            }
                            className={`max-w-[130px] rounded-md border px-1.5 py-1 text-[10px] transition focus:outline-none focus:border-sky-500 disabled:opacity-50 cursor-pointer ${
                              task.assignedPersonaId
                                ? 'bg-sky-500/10 border-sky-500/30 text-sky-200'
                                : 'bg-slate-950/60 border-slate-800 text-slate-400 hover:text-slate-200'
                            }`}
                          >
                            <option value="">Unassigned</option>
                            {teamMembers.map((m) => (
                              <option key={m.id} value={m.id}>
                                {m.name}
                              </option>
                            ))}
                          </select>
                        ) : task.assignedPersonaId ? (
                          // Completed rows keep the assignee as read-only history: reassigning
                          // work that is already done would move nothing but the label.
                          <span
                            className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-800/60 text-slate-400 border border-slate-700/50 truncate max-w-[110px]"
                            title={`Was handled by @${memberNameFor(task.assignedPersonaId)}`}
                            data-testid={`task-assignee-static-${task.id}`}
                          >
                            @{memberNameFor(task.assignedPersonaId)}
                          </span>
                        ) : null}
                        {task.isCompleted ? (
                          <span className="text-[10px] font-mono px-2 py-0.5 rounded-md bg-emerald-500/10 text-emerald-300 border border-emerald-500/20">
                            Completed
                          </span>
                        ) : (
                          <>
                            {task.requiresDiscussion && (
                              <span
                                className="text-[10px] font-mono px-2 py-0.5 rounded-md bg-amber-500/15 text-amber-300 border border-amber-500/30 flex items-center gap-1 font-semibold"
                                data-testid={`task-discuss-${task.id}`}
                                title="Discussion required before starting coding"
                              >
                                <MessageSquareQuote className="w-3 h-3 text-amber-400 shrink-0" />
                                Discuss First
                              </span>
                            )}
                            {isCurrentFocus ? (
                              <span
                                className="text-[10px] font-mono px-2 py-0.5 rounded-md bg-sky-500/15 text-sky-300 border border-sky-500/30 flex items-center gap-1 font-semibold"
                                data-testid={`task-focus-${task.id}`}
                              >
                                <span className="w-1.5 h-1.5 rounded-full bg-sky-400 animate-pulse" />
                                In Progress
                              </span>
                            ) : (
                              <span className="text-[10px] font-mono px-2 py-0.5 rounded-md bg-slate-800/80 text-slate-400 border border-slate-700/40">
                                Queued
                              </span>
                            )}
                          </>
                        )}
                        {!task.isCompleted && onUpdateTask && (
                          <button
                            type="button"
                            disabled={isUploadingTaskAttachment === task.id}
                            onClick={() => {
                              setTargetTaskForUpload(task.id);
                              existingTaskFileInputRef.current?.click();
                            }}
                            className="p-1 rounded text-slate-500 hover:text-sky-400 hover:bg-slate-800 transition opacity-0 group-hover:opacity-100 cursor-pointer disabled:opacity-50"
                            title="Attach file to task"
                            data-testid={`attach-task-${task.id}`}
                          >
                            {isUploadingTaskAttachment === task.id ? (
                              <RefreshCw className="w-3.5 h-3.5 animate-spin text-sky-400" />
                            ) : (
                              <Paperclip className="w-3.5 h-3.5" />
                            )}
                          </button>
                        )}
                        {!task.isCompleted && onUpdateTask && (
                          <button
                            type="button"
                            onClick={() => onUpdateTask(task.id, { requiresDiscussion: !task.requiresDiscussion })}
                            className={`p-1 rounded transition cursor-pointer ${
                              task.requiresDiscussion
                                ? 'text-amber-400 bg-amber-500/10 hover:bg-amber-500/20 border border-amber-500/30'
                                : 'text-slate-500 hover:text-amber-400 hover:bg-slate-800 opacity-0 group-hover:opacity-100'
                            }`}
                            title={task.requiresDiscussion ? 'Discussion required before coding (click to toggle off)' : 'Flag for discussion before coding (click to toggle on)'}
                            data-testid={`toggle-discuss-task-${task.id}`}
                          >
                            <MessageSquareQuote className="w-3.5 h-3.5" />
                          </button>
                        )}
                        <button
                          type="button"
                          onClick={() => onDeleteTask(task.id)}
                          className="p-1 rounded text-slate-500 hover:text-rose-400 hover:bg-slate-800 transition opacity-0 group-hover:opacity-100 cursor-pointer"
                          title="Delete task"
                          data-testid={`delete-task-${task.id}`}
                        >
                          <Trash2 className="w-3.5 h-3.5" />
                        </button>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}

            {assignError && (
              <div
                role="alert"
                className="flex items-start gap-2 px-3 py-2 rounded-xl bg-rose-950/40 border border-rose-500/40 text-[11px] text-rose-200"
                data-testid="task-assign-error"
              >
                <AlertCircle className="w-3.5 h-3.5 shrink-0 mt-0.5 text-rose-400" />
                <span className="min-w-0 flex-1 break-words">{assignError}</span>
                <button
                  type="button"
                  onClick={() => setAssignError(null)}
                  className="text-rose-300 hover:text-white p-0.5 rounded transition cursor-pointer shrink-0"
                  title="Dismiss"
                  data-testid="task-assign-error-dismiss"
                >
                  <X className="w-3 h-3" />
                </button>
              </div>
            )}

            {/* Clean Inline Add Task Form */}
            <form onSubmit={handleAddTaskSubmit} className="space-y-1.5 pt-1">
              <div className="flex items-center gap-2">
                <input
                  type="text"
                  value={newTaskTitle}
                  onChange={(e) => setNewTaskTitle(e.target.value)}
                  placeholder="+ Add task..."
                  className="flex-1 bg-slate-950/90 border border-slate-800 rounded-xl px-3 py-1.5 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition shadow-inner"
                  data-testid="new-task-input"
                />
                <button
                  type="button"
                  onClick={() => newTaskFileInputRef.current?.click()}
                  className={`px-2.5 py-1.5 rounded-xl border transition text-xs flex items-center gap-1.5 shrink-0 cursor-pointer ${
                    newTaskAttachments.length > 0
                      ? 'bg-sky-500/20 border-sky-500/40 text-sky-300 font-semibold shadow-sm'
                      : 'bg-slate-950/60 border-slate-800 text-slate-400 hover:text-slate-200'
                  }`}
                  title="Attach files to task"
                  data-testid="new-task-attach-btn"
                >
                  <Paperclip className="w-3.5 h-3.5" />
                  <span className="hidden sm:inline">
                    Attach{newTaskAttachments.length > 0 ? ` (${newTaskAttachments.length})` : ''}
                  </span>
                </button>
                <button
                  type="button"
                  onClick={() => setNewTaskRequiresDiscussion((prev) => !prev)}
                  className={`px-2.5 py-1.5 rounded-xl border transition text-xs flex items-center gap-1.5 shrink-0 cursor-pointer ${
                    newTaskRequiresDiscussion
                      ? 'bg-amber-500/20 border-amber-500/40 text-amber-300 font-semibold shadow-sm'
                      : 'bg-slate-950/60 border-slate-800 text-slate-400 hover:text-slate-200'
                  }`}
                  title={newTaskRequiresDiscussion ? 'Flagged for discussion before coding (click to toggle off)' : 'Flag for discussion before coding (click to toggle on)'}
                  data-testid="new-task-discuss-toggle"
                >
                  <MessageSquareQuote className="w-3.5 h-3.5" />
                  <span className="hidden sm:inline">Discuss</span>
                </button>
                <button
                  type="submit"
                  disabled={!newTaskTitle.trim() || isAddingTask}
                  className="px-3.5 py-1.5 rounded-xl bg-sky-500 hover:bg-sky-400 disabled:opacity-40 text-slate-950 font-bold transition text-xs flex items-center gap-1.5 shrink-0 cursor-pointer shadow-sm"
                >
                  <Plus className="w-3.5 h-3.5" />
                  <span>Add Task</span>
                </button>
              </div>

              {/* Staged attachments for new task */}
              {newTaskAttachments.length > 0 && (
                <div className="flex flex-wrap gap-1.5 px-1" data-testid="new-task-staged-attachments">
                  {newTaskAttachments.map((att) => (
                    <div
                      key={att.localId}
                      className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-md bg-slate-900 border border-slate-800 text-[10px] text-slate-300"
                    >
                      {att.uploading ? (
                        <RefreshCw className="w-3 h-3 text-sky-400 animate-spin" />
                      ) : (
                        <Paperclip className="w-3 h-3 text-sky-400" />
                      )}
                      <span className="truncate max-w-[120px]">{att.name}</span>
                      {att.sizeBytes && (
                        <span className="text-[9px] text-slate-500 font-mono">
                          ({formatAttachmentSize(att.sizeBytes)})
                        </span>
                      )}
                      <button
                        type="button"
                        onClick={() => removeNewTaskAttachment(att.localId)}
                        className="text-slate-500 hover:text-rose-400 p-0.5 rounded cursor-pointer"
                        title="Remove file"
                        data-testid={`remove-new-task-att-${att.localId}`}
                      >
                        <X className="w-2.5 h-2.5" />
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </form>
          </div>
        )}
      </div>

      {/* Main Workspace: Conversational Chat Flow + Team & Activity Sidebar */}
      <div className="flex-1 flex min-h-0 overflow-hidden relative">
        {/* Left Column: Conversational Chat Flow */}
        <div className="flex-1 flex flex-col min-h-0 relative">
          {/* Live Chat Message Feed */}
          <div
            ref={messagesContainerRef}
            onScroll={handleMessagesScroll}
            data-testid="job-messages-container"
            className="flex-1 overflow-y-auto p-3 sm:p-6 space-y-3 sm:space-y-4 relative"
          >
            {messages.length === 0 ? (
              <div className="h-full flex flex-col items-center justify-center text-center p-8 text-slate-500">
                <Bot className="w-12 h-12 stroke-[1.2] text-slate-600 mb-3" />
                <h3 className="text-sm font-semibold text-slate-300">Job Collaboration Channel Ready</h3>
                <p className="text-xs text-slate-500 max-w-sm mt-1">
                  Give instructions, attach files, or assign tasks to your team. Use <code className="text-sky-400 font-mono">@persona</code> to tag specific personas.
                </p>
              </div>
            ) : (
              messages.map((msg) => {
                const isUser = msg.senderType === 'user';
                const isSystem = msg.senderType === 'system';
                const msgAttachments = extractJobMessageAttachments(msg);

                if (isSystem) {
                  return (
                    <div
                      key={msg.id}
                      className="flex justify-center my-2"
                      data-testid={`system-message-${msg.id}`}
                    >
                      <div className="flex items-center gap-2 px-3.5 py-1.5 rounded-full bg-slate-800/80 border border-slate-700/60 text-xs text-slate-300 shadow-sm max-w-xl text-center">
                        <AlertCircle className="w-3.5 h-3.5 text-amber-400 shrink-0" />
                        <span>{msg.content}</span>
                        <span className="text-[10px] text-slate-500 font-mono shrink-0">
                          {new Date(msg.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                        </span>
                      </div>
                    </div>
                  );
                }

                return (
                  <div
                    key={msg.id}
                    className={`flex gap-3 max-w-3xl ${isUser ? 'ml-auto flex-row-reverse' : ''}`}
                  >
                    <div
                      className={`w-8 h-8 rounded-xl flex items-center justify-center shrink-0 border ${
                        isUser
                          ? 'bg-sky-500/20 text-sky-300 border-sky-500/30'
                          : 'bg-indigo-500/20 text-indigo-300 border-indigo-500/30'
                      }`}
                    >
                      {isUser ? <User className="w-4 h-4" /> : <Bot className="w-4 h-4" />}
                    </div>

                    <div className={`space-y-1.5 ${isUser ? 'text-right' : ''}`}>
                      <div className="flex items-center gap-2 text-xs">
                        <span className="font-semibold text-slate-200">
                          {isUser ? 'You' : msg.senderName || msg.senderId || 'Persona'}
                        </span>
                        {!isUser && msg.senderId && (
                          <span className="text-[10px] font-mono text-sky-400 bg-sky-500/10 px-1.5 py-0.2 rounded border border-sky-500/20">
                            @{msg.senderId}
                          </span>
                        )}
                        <span className="text-[10px] text-slate-500">
                          {new Date(msg.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                        </span>
                      </div>

                      {(() => {
                        const hasReadyToProceed = !isUser && msg.content.includes('[READY_TO_PROCEED]');
                        const displayContent = hasReadyToProceed
                          ? msg.content.replace(/\[READY_TO_PROCEED\]/gi, '').trim()
                          : msg.content;

                        return (
                          <>
                            <div
                              className={`p-3.5 rounded-2xl text-xs leading-relaxed border ${
                                isUser
                                  ? 'bg-sky-600/20 border-sky-500/30 text-sky-100 text-left rounded-tr-none'
                                  : 'bg-slate-900 border-slate-800 text-slate-200 rounded-tl-none shadow-sm text-left'
                              }`}
                            >
                              <MarkdownMessage content={displayContent} isUser={isUser} />
                            </div>

                            {hasReadyToProceed && (
                              <div
                                className="mt-2.5 p-3.5 rounded-2xl bg-sky-950/30 border border-sky-500/30 text-xs space-y-2.5 shadow-sm text-left"
                                data-testid={`ready-to-proceed-card-${msg.id}`}
                              >
                                <div className="flex items-center justify-between gap-2">
                                  <div className="flex items-center gap-2 text-sky-200 font-semibold">
                                    <Sparkles className="w-4 h-4 text-sky-400 shrink-0" />
                                    <span>Ready to Proceed</span>
                                  </div>
                                  <span className="text-[10px] font-mono px-2 py-0.5 rounded-full bg-sky-500/10 text-sky-300 border border-sky-500/25">
                                    Alignment Proposed
                                  </span>
                                </div>
                                <p className="text-[11px] text-slate-300 leading-relaxed">
                                  The team has aligned on requirements and is ready to begin coding on the target task. Confirm to proceed with implementation, or reply in chat to continue discussing.
                                </p>
                                <div className="flex items-center gap-2 pt-1 flex-wrap">
                                  {firstPendingTask && firstPendingTask.requiresDiscussion ? (
                                    <button
                                      type="button"
                                      onClick={() => handleProceedToCoding(firstPendingTask, msg.senderId)}
                                      className="px-3.5 py-1.5 rounded-xl bg-emerald-500 hover:bg-emerald-400 text-slate-950 font-bold transition text-xs flex items-center gap-1.5 cursor-pointer shadow-sm"
                                      data-testid="proceed-to-coding-btn"
                                    >
                                      <Play className="w-3.5 h-3.5 fill-current" />
                                      <span>Proceed to Coding</span>
                                    </button>
                                  ) : (
                                    <div className="flex items-center gap-1.5 text-[11px] text-emerald-400 font-medium">
                                      <CheckCircle2 className="w-4 h-4 text-emerald-400" />
                                      <span>Discussion approved — implementation unlocked</span>
                                    </div>
                                  )}
                                  <span className="text-[11px] text-slate-500 italic ml-1">
                                    or continue talking below
                                  </span>
                                </div>
                              </div>
                            )}

                            {(() => {
                              const msgQuestions = (() => {
                                if (isUser) return [];
                                if (msg.structuredPayloadJson) {
                                  try {
                                    const p = JSON.parse(msg.structuredPayloadJson);
                                    if (Array.isArray(p.questions) && p.questions.length > 0) return p.questions as AskQuestionItem[];
                                  } catch {}
                                }
                                return parseTextQuestions(msg.content);
                              })();

                              if (msgQuestions.length === 0) return null;

                              const msgIndex = messages.findIndex((m) => m.id === msg.id);
                              const subsequentUserMsg = messages
                                .slice(msgIndex >= 0 ? msgIndex + 1 : 0)
                                .find((other) => other.senderType === 'user');

                              const isMsgQuestionAnswered =
                                answeredQuestionIds.includes(msg.id) ||
                                Boolean(subsequentUserMsg);

                              const answeredText = (() => {
                                if (!isMsgQuestionAnswered) return undefined;
                                if (subsequentUserMsg?.content) {
                                  if (subsequentUserMsg.content.includes('User answered question')) {
                                    return subsequentUserMsg.content.replace(/^User answered question:\n?/i, '');
                                  }
                                  return `Addressed in subsequent reply: "${subsequentUserMsg.content.slice(0, 120)}${subsequentUserMsg.content.length > 120 ? '...' : ''}"`;
                                }
                                return 'Answer submitted';
                              })();

                              return (
                                <div className="mt-2.5 text-left" data-testid={`job-question-container-${msg.id}`}>
                                  <QuestionCard
                                    cardKey={`job-q-${msg.id}`}
                                    targetId={msg.id}
                                    eventCreatedAt={msg.createdAt}
                                    parsedQuestions={msgQuestions}
                                    isAnswered={isMsgQuestionAnswered}
                                    answeredContent={answeredText}
                                    isSubmitting={submittingQuestionId === msg.id}
                                    onSubmit={async (answers) => {
                                      await handleAnswerQuestion(msg, msgQuestions, answers);
                                    }}
                                  />
                                </div>
                              );
                            })()}
                          </>
                        );
                      })()}

                  {/* Message Attachments Rendering */}
                  {msgAttachments.length > 0 && (
                    <div
                      className={`flex flex-wrap gap-2 pt-1 ${isUser ? 'justify-end' : 'justify-start'}`}
                      data-testid={`job-message-attachments-${msg.id}`}
                    >
                      {msgAttachments.map((att) => {
                        const isImg = isImageAttachment(att.contentType, att.fileName);
                        const targetId = att.id || att.fileName;
                        const streamUrl = att.downloadUrl || (targetId ? `/api/project-jobs/${encodeURIComponent(msg.jobId)}/attachments/${encodeURIComponent(targetId)}/content` : '');

                        if (isImg && streamUrl) {
                          return (
                            <button
                              key={att.id || att.fileName}
                              type="button"
                              onClick={() =>
                                handleOpenLightbox({
                                  url: streamUrl,
                                  fileName: att.fileName || 'attachment.png',
                                  sizeBytes: att.sizeBytes
                                })
                              }
                              aria-label={`Zoom image ${att.fileName || 'image'}`}
                              className="group relative rounded-xl overflow-hidden border border-sky-500/40 bg-slate-950/80 hover:border-sky-300 transition cursor-pointer"
                            >
                              <img
                                src={streamUrl}
                                alt={att.fileName || 'attachment image'}
                                className="max-h-36 max-w-[200px] object-cover block"
                              />
                              <div className="absolute inset-0 bg-black/50 opacity-0 group-hover:opacity-100 transition flex items-center justify-center gap-1 text-xs font-semibold text-white">
                                <ZoomIn className="w-4 h-4 text-sky-300" />
                                <span>Zoom</span>
                              </div>
                            </button>
                          );
                        }

                        return (
                          <a
                            key={att.id || att.fileName}
                            href={streamUrl ? `${streamUrl}${streamUrl.includes('?') ? '&' : '?'}download=true` : '#'}
                            download={att.fileName || 'attachment'}
                            className="flex items-center gap-2.5 px-3 py-2 rounded-xl bg-slate-950/80 hover:bg-slate-900 border border-sky-500/30 hover:border-sky-400 text-xs text-slate-200 transition"
                          >
                            <FileText className="w-4 h-4 text-sky-400 shrink-0" />
                            <div className="min-w-0 text-left">
                              <div className="font-mono text-xs text-white truncate max-w-[160px]">
                                {att.fileName || 'Attachment'}
                              </div>
                              <div className="text-[10px] text-slate-400">
                                {formatAttachmentSize(att.sizeBytes)}
                              </div>
                            </div>
                            <Download className="w-3.5 h-3.5 text-sky-400 shrink-0 ml-1" />
                          </a>
                        );
                      })}
                    </div>
                  )}

                  {/* Two-Tier Turn Execution Button */}
                  {msg.turnSessionId && (
                    <div className={isUser ? 'text-right' : 'text-left'}>
                      <button
                        type="button"
                        onClick={() => onSelectTurnSession?.(msg.turnSessionId!)}
                        className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-lg bg-slate-900 hover:bg-slate-800 border border-slate-800 text-[11px] text-sky-400 hover:text-sky-300 transition cursor-pointer shadow-sm"
                      >
                        <Sparkles className="w-3 h-3 text-sky-400" />
                        <span>View Turn Execution & Trajectory</span>
                        <ChevronRight className="w-3 h-3" />
                      </button>
                    </div>
                  )}
                </div>
              </div>
            );
          })
        )}

        {/* Subtle conversational working indicator at bottom of message stream */}
        {activeTurnInfo?.isActive && (
          <div className={`flex items-center gap-2 py-1.5 px-3 text-xs font-medium ${activeTurnInfo.isOffline ? 'text-amber-400' : 'text-sky-400'}`}>
            {activeTurnInfo.isOffline ? (
              <AlertCircle className="w-3.5 h-3.5 text-amber-400 shrink-0" />
            ) : (
              <RefreshCw className="w-3.5 h-3.5 animate-spin text-sky-400 shrink-0" />
            )}
            <span>
              {activeTurnInfo.isOffline
                ? `${activeTurnInfo.personaName} is queued (waiting for runner @${activeTurnInfo.driverId || 'agent'} to connect)...`
                : `${activeTurnInfo.personaName} is working on this turn...`}
            </span>
          </div>
        )}

        <div ref={messagesEndRef} />
      </div>

      {/* Floating Scroll-to-Bottom Pill when user scrolls up */}
      {isScrolledUp && (
        <button
          type="button"
          onClick={() => scrollToBottom(true)}
          className="absolute bottom-24 right-8 z-30 px-3.5 py-2 rounded-full bg-slate-900/95 hover:bg-slate-800 text-sky-400 hover:text-sky-300 border border-slate-700 shadow-2xl flex items-center gap-2 text-xs font-semibold backdrop-blur transition cursor-pointer"
          data-testid="job-scroll-to-bottom-btn"
        >
          <ArrowDown className="w-3.5 h-3.5 animate-bounce" />
          <span>New messages below</span>
        </button>
      )}

      {/* Prompt Composer & Mention Bar */}
      <div className="p-3 sm:p-4 border-t border-slate-800/80 bg-slate-900/80 backdrop-blur-sm shrink-0 pb-[max(0.75rem,env(safe-area-inset-bottom))]">
        {/* Attachment Error Banner */}
        {attachmentError && (
          <div className="mb-2.5 px-3 py-2 rounded-xl bg-rose-500/15 border border-rose-500/30 text-rose-300 text-xs flex items-center justify-between gap-2">
            <div className="flex items-center gap-2">
              <AlertCircle className="w-4 h-4 text-rose-400 shrink-0" />
              <span>{attachmentError}</span>
            </div>
            <button
              type="button"
              onClick={() => setAttachmentError(null)}
              className="text-rose-400 hover:text-rose-200 text-xs font-semibold cursor-pointer"
            >
              Dismiss
            </button>
          </div>
        )}

        {/* Staged Attachments Preview Bar */}
        {stagedAttachments.length > 0 && (
          <div className="flex flex-wrap gap-2 mb-2.5" data-testid="job-staged-attachments">
            {stagedAttachments.map((att) => {
              const isImg = isImageAttachment(att.contentType, att.name);
              return (
                <div
                  key={att.localId}
                  className={`flex items-center gap-2 px-2.5 py-1.5 rounded-lg bg-slate-950 border text-xs text-slate-200 max-w-xs ${
                    att.error ? 'border-rose-500/50 bg-rose-950/20' : 'border-slate-800'
                  }`}
                >
                  {isImg && att.previewUrl ? (
                    <img
                      src={att.previewUrl}
                      alt={att.name}
                      className="w-7 h-7 object-cover rounded shrink-0 border border-slate-700"
                    />
                  ) : isImg ? (
                    <ImageIcon className="w-4 h-4 text-sky-400 shrink-0" />
                  ) : (
                    <FileText className="w-4 h-4 text-slate-400 shrink-0" />
                  )}
                  <div className="min-w-0 flex-1">
                    <div className="truncate font-mono text-[11px] text-white max-w-[130px]">{att.name}</div>
                    <div className="text-[10px] text-slate-400">
                      {att.uploading ? (
                        <span className="text-sky-400 animate-pulse">Uploading...</span>
                      ) : att.error ? (
                        <span className="text-rose-400">{att.error}</span>
                      ) : (
                        formatAttachmentSize(att.sizeBytes)
                      )}
                    </div>
                  </div>
                  <button
                    type="button"
                    onClick={() => removeStagedAttachment(att.localId)}
                    aria-label={`Remove attachment ${att.name}`}
                    className="p-0.5 rounded hover:bg-slate-800 text-slate-400 hover:text-rose-400 transition cursor-pointer"
                  >
                    <X className="w-3.5 h-3.5" />
                  </button>
                </div>
              );
            })}
          </div>
        )}

        {/* Quick Mention Chips */}
        {project.assignedPersonas && project.assignedPersonas.length > 0 && (
          <div className="flex items-center gap-1.5 mb-2.5 overflow-x-auto pb-1 text-xs">
            <span className="text-[11px] font-semibold text-slate-500 mr-1 shrink-0">Quick Mention:</span>
            {project.assignedPersonas.map((p) => (
              <button
                key={p.id}
                type="button"
                onClick={() => handleMentionInsert(p.id)}
                className="px-2 py-0.5 rounded-md bg-slate-950 hover:bg-slate-800 border border-slate-800 hover:border-sky-500/40 text-[11px] text-slate-300 hover:text-sky-300 transition shrink-0 cursor-pointer"
              >
                @{p.id}
              </button>
            ))}
          </div>
        )}

        <form onSubmit={handleSend} className="relative flex items-center gap-2">
          {/* Hidden File Input */}
          <input
            ref={fileInputRef}
            type="file"
            multiple
            className="hidden"
            data-testid="job-file-input"
            onChange={(e) => {
              if (e.target.files && e.target.files.length > 0) {
                void stageFiles(e.target.files);
                e.target.value = '';
              }
            }}
          />

          {/* Hidden File Input for New Task Attachments */}
          <input
            ref={newTaskFileInputRef}
            type="file"
            multiple
            className="hidden"
            data-testid="new-task-file-input"
            onChange={(e) => {
              if (e.target.files && e.target.files.length > 0) {
                void handleNewTaskFiles(e.target.files);
                e.target.value = '';
              }
            }}
          />

          {/* Hidden File Input for Existing Task Attachments */}
          <input
            ref={existingTaskFileInputRef}
            type="file"
            multiple
            className="hidden"
            data-testid="existing-task-file-input"
            onChange={(e) => {
              if (e.target.files && e.target.files.length > 0 && targetTaskForUpload) {
                void handleUploadExistingTaskFiles(targetTaskForUpload, e.target.files);
                e.target.value = '';
              }
            }}
          />

          {/* Paperclip Attach Button */}
          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            title="Attach files (Images, PDFs, Code, Logs, ZIP — max 25 MB)"
            aria-label="Attach files"
            data-testid="job-attach-btn"
            disabled={isClosed || isSending}
            className="p-3 bg-slate-950 hover:bg-slate-800 border border-slate-800 hover:border-slate-700 text-slate-300 hover:text-sky-300 disabled:opacity-40 rounded-xl transition cursor-pointer shrink-0 shadow-md flex items-center justify-center self-stretch"
          >
            <Paperclip className="w-4 h-4" />
          </button>

          <div className="relative flex-1">
            <TeamMentionPalette
              members={teamMembers}
              query={mentionState.query}
              isOpen={mentionState.isOpen}
              highlightedIndex={mentionHighlightedIndex}
              onSelect={handleSelectMention}
              onClose={() => setMentionState((prev) => ({ ...prev, isOpen: false }))}
              triggerRef={textareaRef}
            />
            <textarea
              ref={textareaRef}
              rows={2}
              value={prompt}
              onChange={handlePromptChange}
              onKeyUp={handleCursorActivity}
              onClick={handleCursorActivity}
              onKeyDown={handlePromptKeyDown}
              onPaste={handlePaste}
              placeholder={
                isArchived
                  ? "This job is archived and read-only. Reopen the job to post messages..."
                  : isWaitingForInput
                  ? "Agent is waiting for your answer above (type here or use the card above to respond)..."
                  : isCompleted
                  ? "Job completed. Continue discussion, delegate with @persona, or reopen if needed..."
                  : "Type your message, tasks, or delegate with @persona (Press Enter to send)..."
              }
              disabled={isClosed || isSending}
              className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition resize-none disabled:opacity-50"
            />
          </div>
          <button
            type="submit"
            disabled={!canSend}
            className="p-3 bg-sky-500 hover:bg-sky-400 disabled:opacity-40 text-slate-950 rounded-xl transition cursor-pointer shrink-0 shadow-md flex items-center justify-center self-stretch"
            title="Send Message"
          >
            <Send className="w-4 h-4" />
          </button>
        </form>
      </div>
    </div>

      {/* Team & Activity Sidebar */}
      {showTeamSidebar && (
        <>
          {/* Mobile Backdrop Overlay */}
          <div
            className="md:hidden fixed inset-0 z-40 bg-black/60 backdrop-blur-xs"
            onClick={() => setShowTeamSidebar(false)}
            data-testid="job-team-sidebar-backdrop"
          />
          <aside
            className="fixed inset-y-0 right-0 z-50 w-full sm:w-80 max-w-[85vw] border-l border-slate-800 bg-slate-950 shadow-2xl flex flex-col overflow-y-auto md:static md:z-auto md:w-80 md:shadow-none"
            data-testid="job-team-sidebar"
          >
            {/* Sidebar Header */}
            <div className="p-4 border-b border-slate-800/80 flex items-center justify-between shrink-0 bg-slate-900/40">
              <div className="flex items-center gap-2">
                <Users className="w-4 h-4 text-sky-400" />
                <h3 className="text-xs font-bold text-slate-100 uppercase tracking-wider">
                  Team & Activity
                </h3>
              </div>
              <div className="flex items-center gap-2">
                <span className="text-[10px] font-mono px-2 py-0.5 rounded-full bg-slate-800/80 text-slate-300 border border-slate-700/50">
                  {teamMembers.length} {teamMembers.length === 1 ? 'member' : 'members'}
                </span>
                <button
                  type="button"
                  onClick={() => setShowTeamSidebar(false)}
                  className="md:hidden p-1 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                  title="Close Team & Activity Sidebar"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>
            </div>

          <div className="p-4 space-y-4 flex-1">
            {/* Active Turn Real-Time Feedback Card (inside sidebar) */}
            {activeTurnInfo?.isActive && (
              <div
                className={`p-3.5 rounded-xl text-xs shadow-md space-y-3 ${
                  activeTurnInfo.isOffline
                    ? 'bg-amber-950/40 border border-amber-500/40 text-amber-200'
                    : 'bg-sky-950/40 border border-sky-500/40'
                }`}
                data-testid="job-active-turn-indicator"
              >
                <div className="flex items-center gap-2.5">
                  <div
                    className={`relative flex items-center justify-center w-7 h-7 rounded-lg shrink-0 ${
                      activeTurnInfo.isOffline
                        ? 'bg-amber-500/20 text-amber-400 border border-amber-500/30'
                        : 'bg-sky-500/20 text-sky-400 border border-sky-500/30'
                    }`}
                  >
                    {activeTurnInfo.isOffline ? (
                      <AlertCircle className="w-3.5 h-3.5 text-amber-400" />
                    ) : (
                      <>
                        <Bot className="w-3.5 h-3.5 animate-pulse" />
                        <span className="absolute -top-0.5 -right-0.5 w-2 h-2 rounded-full bg-sky-400 animate-ping" />
                      </>
                    )}
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-1.5 justify-between">
                      <span className="font-semibold text-slate-100 truncate">
                        {activeTurnInfo.personaName} {activeTurnInfo.statusText}
                      </span>
                      <span
                        className={`text-[10px] font-mono px-1.5 py-0.2 rounded border shrink-0 ${
                          activeTurnInfo.isOffline
                            ? 'bg-amber-500/20 text-amber-300 border-amber-500/30'
                            : 'bg-sky-500/20 text-sky-300 border-sky-500/30 animate-pulse'
                        }`}
                      >
                        {activeTurnInfo.isOffline ? 'Queued' : 'Working'}
                      </span>
                    </div>
                    <p className="text-[11px] text-slate-400 leading-relaxed mt-0.5">
                      {activeTurnInfo.isOffline
                        ? `Starting environment & waiting for runner connection (@${activeTurnInfo.driverId || 'agy'})...`
                        : 'Executing workspace commands, tests, and active persona logic.'}
                    </p>
                  </div>
                </div>

                {activeTurnInfo.turnSessionId && (
                  <div className="flex items-center gap-2 pt-1 border-t border-sky-500/20">
                    {onStopTurn && (
                      <button
                        type="button"
                        onClick={() => onStopTurn(activeTurnInfo.turnSessionId!)}
                        title="Stop active turn execution"
                        data-testid="job-stop-turn-btn"
                        className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-rose-600/90 hover:bg-rose-500 text-white border border-rose-500/40 text-xs font-semibold transition cursor-pointer shadow-sm animate-pulse"
                      >
                        <Square className="w-3 h-3 fill-current" />
                        <span>Stop</span>
                      </button>
                    )}
                    <button
                      type="button"
                      onClick={() => onSelectTurnSession?.(activeTurnInfo.turnSessionId!)}
                      className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg bg-sky-500/20 hover:bg-sky-500/30 text-sky-300 hover:text-white border border-sky-500/40 text-xs font-semibold transition cursor-pointer shadow-sm"
                    >
                      <Sparkles className="w-3 h-3 text-sky-400" />
                      <span>View Trajectory</span>
                      <ChevronRight className="w-3 h-3" />
                    </button>
                  </div>
                )}
              </div>
            )}

            {/* Queued Turns Card */}
            {job.queuedTurns && job.queuedTurns.length > 0 && (
              <div
                className="p-3.5 rounded-xl bg-amber-950/40 border border-amber-500/40 text-xs shadow-md space-y-2.5"
                data-testid="job-queued-turns-indicator"
              >
                <div className="flex items-center gap-2.5">
                  <div className="relative flex items-center justify-center w-7 h-7 rounded-lg bg-amber-500/20 text-amber-300 border border-amber-500/30 shrink-0">
                    <Clock className="w-3.5 h-3.5 animate-spin" />
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-1.5 justify-between">
                      <span className="font-semibold text-amber-200 truncate">
                        {job.queuedTurns.length} Queued Turn{job.queuedTurns.length === 1 ? '' : 's'}
                      </span>
                      <span className="text-[10px] font-mono px-1.5 py-0.2 rounded bg-amber-500/20 text-amber-300 border border-amber-500/30 shrink-0">
                        Waiting for Slot
                      </span>
                    </div>
                    <p className="text-[11px] text-amber-300/80 leading-relaxed mt-0.5">
                      Target persona reached concurrency limit on other jobs. Will execute automatically when free.
                    </p>
                  </div>
                </div>

                {/* Individual Queued Turns */}
                <div className="space-y-1.5 pt-1.5 border-t border-amber-500/20">
                  {job.queuedTurns.map((turn) => {
                    const targetPersona = personas.find((p) => p.id === turn.targetPersonaId);
                    const displayName = targetPersona?.name || turn.targetPersonaId;
                    return (
                      <div
                        key={turn.id}
                        data-testid={`queued-turn-item-${turn.id}`}
                        className="flex items-center justify-between gap-2 p-2 rounded-lg bg-amber-900/30 border border-amber-500/25 text-[11px]"
                      >
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-1.5">
                            <span className="font-semibold text-amber-200 truncate">@{displayName}</span>
                            <span className="text-[10px] text-amber-400/60 font-mono">
                              {new Date(turn.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                            </span>
                          </div>
                          <p className="text-[11px] text-amber-300/70 truncate mt-0.5 font-mono">
                            {turn.content.slice(0, 60)}{turn.content.length > 60 ? '...' : ''}
                          </p>
                        </div>
                        <div className="flex items-center gap-1.5 shrink-0">
                        {onAssignTask && (
                          <select
                            value={turn.targetPersonaId}
                            disabled={assigningTaskId === `turn:${turn.id}`}
                            onChange={async (e) => {
                              const nextPersona = e.target.value;
                              if (!nextPersona || nextPersona === turn.targetPersonaId) return;
                              // The queued message belongs to whoever was going to run the
                              // task it landed on, so redirecting it means reassigning that
                              // task — which is what moves the turn server-side.
                              const targetTaskId = resolveTargetTaskId(uniqueTasks, turn.targetPersonaId);
                              if (!targetTaskId) {
                                setAssignError('This queued turn has no task to reassign. Add a task first, or cancel the turn.');
                                return;
                              }
                              setAssigningTaskId(`turn:${turn.id}`);
                              setAssignError(null);
                              try {
                                await onAssignTask(targetTaskId, nextPersona);
                              } catch (err) {
                                setAssignError(err instanceof Error ? err.message : 'Failed to reassign queued turn');
                              } finally {
                                setAssigningTaskId(null);
                              }
                            }}
                            aria-label={`Reassign queued turn for ${displayName}`}
                            data-testid={`reassign-queued-turn-${turn.id}`}
                            title="Send this queued turn to a different team member"
                            className="max-w-[110px] rounded-md border bg-amber-950/60 border-amber-500/30 text-amber-100 px-1.5 py-1 text-[10px] transition focus:outline-none focus:border-sky-500 disabled:opacity-50 cursor-pointer"
                          >
                            <option value={turn.targetPersonaId}>@{displayName}</option>
                            {teamMembers
                              .filter((m) => m.id !== turn.targetPersonaId)
                              .map((m) => (
                                <option key={m.id} value={m.id}>
                                  → {m.name}
                                </option>
                              ))}
                          </select>
                        )}
                        {onCancelQueuedTurn && (
                          <button
                            type="button"
                            onClick={() => onCancelQueuedTurn(turn.id)}
                            title="Cancel queued turn"
                            data-testid={`cancel-queued-turn-btn-${turn.id}`}
                            className="inline-flex items-center gap-1 px-2 py-1 rounded bg-rose-600/80 hover:bg-rose-500 text-white font-medium text-[10px] transition cursor-pointer shrink-0 shadow-sm"
                          >
                            <X className="w-3 h-3" />
                            <span>Cancel</span>
                          </button>
                        )}
                        </div>
                      </div>
                    );
                  })}
                </div>
              </div>
            )}

            {/* Team Members List */}
            <div className="space-y-2">
              <div className="text-[10px] font-mono uppercase tracking-wider text-slate-400 font-semibold px-0.5">
                Assigned Team Members
              </div>
              <div className="space-y-2">
                {teamMembers.map((member) => {
                  const isWorking =
                    activeTurnInfo?.isActive &&
                    (activeTurnInfo.personaName === member.name ||
                      activeTurnInfo.personaName === member.id);
                  const queuedCount = (job.queuedTurns || []).filter(
                    (t) => t.targetPersonaId === member.id
                  ).length;
                  const personaObj = personas.find((p) => p.id === member.id);

                  return (
                    <div
                      key={member.id}
                      className={`p-3 rounded-xl border transition ${
                        isWorking
                          ? 'bg-sky-950/25 border-sky-500/40 shadow-sm'
                          : queuedCount > 0
                          ? 'bg-amber-950/20 border-amber-500/30'
                          : 'bg-slate-900/50 border-slate-800/80'
                      }`}
                    >
                      <div className="flex items-start justify-between gap-2">
                        <div className="flex items-center gap-2.5 min-w-0">
                          <div className="relative w-7 h-7 rounded-lg bg-slate-800 border border-slate-700 flex items-center justify-center text-slate-300 shrink-0">
                            <Bot className="w-3.5 h-3.5" />
                            <span
                              className={`absolute -bottom-0.5 -right-0.5 w-1.5 h-1.5 rounded-full border border-slate-900 ${
                                isWorking
                                  ? 'bg-sky-400 animate-ping'
                                  : queuedCount > 0
                                  ? 'bg-amber-400'
                                  : 'bg-emerald-400'
                              }`}
                            />
                          </div>
                          <div className="min-w-0">
                            <div className="flex items-center gap-1.5">
                              <span className="font-semibold text-xs text-white truncate">
                                {member.name}
                              </span>
                              {member.isLead && (
                                <span className="text-[9px] font-mono px-1 py-0.2 rounded bg-amber-500/15 text-amber-300 border border-amber-500/30">
                                  Lead
                                </span>
                              )}
                            </div>
                            <div className="text-[10px] text-slate-400 truncate">
                              {member.role || 'Member'}
                            </div>
                          </div>
                        </div>

                        <span
                          className={`text-[9px] font-mono px-1.5 py-0.2 rounded shrink-0 ${
                            isWorking
                              ? 'bg-sky-500/20 text-sky-300 border border-sky-500/40 animate-pulse'
                              : queuedCount > 0
                              ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40'
                              : 'bg-slate-800/60 text-slate-400 border border-slate-700/50'
                          }`}
                        >
                          {isWorking ? 'Working' : queuedCount > 0 ? `Queued (${queuedCount})` : 'Idle'}
                        </span>
                      </div>

                      <div className="mt-2 text-[10px] flex items-center gap-1.5 text-slate-400 font-mono">
                        <span className="text-sky-400/90">@{member.id}</span>
                        <span className="text-slate-600">•</span>
                        <span className="text-slate-400 truncate">
                          {isWorking
                            ? 'Executing active turn...'
                            : queuedCount > 0
                            ? `${queuedCount} turn(s) queued`
                            : personaObj?.maxConcurrentJobs
                            ? `Max ${personaObj.maxConcurrentJobs} job(s)`
                            : 'Ready for instructions'}
                        </span>
                      </div>

                      {queuedCount > 0 && (onCancelQueuedTurns || onCancelQueuedTurn) && (
                        <div className="mt-2 pt-1.5 border-t border-amber-500/20 flex items-center justify-between">
                          <span className="text-[10px] text-amber-300/80 font-medium">
                            {queuedCount} turn{queuedCount === 1 ? '' : 's'} waiting
                          </span>
                          <button
                            type="button"
                            onClick={() => {
                              const turnsForMember = (job.queuedTurns || []).filter(
                                (t) => t.targetPersonaId === member.id
                              );
                              if (turnsForMember.length === 0) return;
                              // Prefer the bulk endpoint: N concurrent per-turn DELETEs each
                              // read-modify-write QueuedTurnsJson and lose updates.
                              if (onCancelQueuedTurns) {
                                onCancelQueuedTurns(member.id);
                                return;
                              }
                              turnsForMember.forEach((t) => onCancelQueuedTurn?.(t.id));
                            }}
                            title={`Cancel queued turns for ${member.name}`}
                            data-testid={`cancel-member-queued-turns-${member.id}`}
                            className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-rose-600/80 hover:bg-rose-500 text-white font-medium text-[10px] transition cursor-pointer shadow-sm"
                          >
                            <X className="w-2.5 h-2.5" />
                            <span>Cancel</span>
                          </button>
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        </aside>
      </>
    )}
    </div>

      {/* Internal Fullscreen Image Lightbox Modal fallback */}
      {internalLightbox && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label={`Image preview: ${internalLightbox.fileName}`}
          data-testid="job-attachment-lightbox"
          onClick={() => setInternalLightbox(null)}
          className="fixed inset-0 z-50 bg-black/85 backdrop-blur-md flex flex-col items-center justify-center p-2 sm:p-6"
        >
          <div
            onClick={(e) => e.stopPropagation()}
            className="max-w-5xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl overflow-hidden flex flex-col max-h-[90vh]"
          >
            <div className="px-5 py-3 border-b border-slate-800 flex items-center justify-between bg-slate-950/80">
              <div className="flex items-center gap-2 min-w-0">
                <ImageIcon className="w-4 h-4 text-sky-400 shrink-0" />
                <span className="font-mono text-xs font-semibold text-white truncate">
                  {internalLightbox.fileName}
                </span>
                {internalLightbox.sizeBytes ? (
                  <span className="text-[11px] font-mono text-slate-400">
                    ({formatAttachmentSize(internalLightbox.sizeBytes)})
                  </span>
                ) : null}
              </div>
              <div className="flex items-center gap-2">
                <a
                  href={`${internalLightbox.url}${internalLightbox.url.includes('?') ? '&' : '?'}download=true`}
                  download={internalLightbox.fileName}
                  className="px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-medium flex items-center gap-1.5 transition"
                >
                  <Download className="w-3.5 h-3.5 text-sky-400" />
                  <span>Download</span>
                </a>
                <button
                  type="button"
                  onClick={() => setInternalLightbox(null)}
                  aria-label="Close image preview"
                  className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>
            </div>
            <div className="flex-1 p-6 bg-slate-950 flex items-center justify-center overflow-auto">
              <img
                src={internalLightbox.url}
                alt={internalLightbox.fileName}
                className="max-h-[75vh] max-w-full object-contain rounded-lg shadow-xl"
                data-testid="job-lightbox-image"
              />
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
