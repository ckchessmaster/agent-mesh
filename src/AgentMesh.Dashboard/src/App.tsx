import { useEffect, useLayoutEffect, useState, useCallback, useRef, useMemo } from 'react';
import {
  Bot,
  Brain,
  Terminal,
  Send,
  Plus,
  Trash2,
  RefreshCw,
  CheckCircle2,
  AlertTriangle,
  LogOut,
  User,
  Wrench,
  MessageSquare,
  ChevronRight,
  Activity,
  Cpu,
  Folder,
  FolderOpen,
  ChevronUp,
  ChevronDown,
  Home,
  X,
  Check,
  RotateCcw,
  Zap,
  Sliders,
  BarChart2,
  Layers,
  Clock,
  Square,
  ListPlus,
  HelpCircle,
  FileText,
  Users,
  Edit3,
  ArrowDown,
  ArrowDownToLine,
  Paperclip,
  Image as ImageIcon,
  File as FileIcon,
  Download,
  ZoomIn,
  FolderGit2,
  Maximize2
} from 'lucide-react';
import { api } from './services/api';
import { useSignalR } from './hooks/useSignalR';
import { useAuth } from './auth/AuthProvider';
import type {
  Agent,
  AgentSession,
  AgentUsageResponse,
  AskQuestionItem,
  FilesystemBrowseResponse,
  JobMessage,
  JobTask,
  ModelCapability,
  Persona,
  Project,
  ProjectJob,
  QueuedPromptItem,
  QuestionAnswerPayload,
  SessionAttachment,
  SessionMessage,
  SessionArtifact,
  SystemCapabilitiesResponse,
  SystemStatusResponse
} from './types';
import { ProjectJobStatus } from './types';
import { SlashCommandPalette, SLASH_COMMANDS, type SlashCommandItem } from './components/SlashCommandPalette';
import { ArtifactViewer } from './components/ArtifactViewer';
import { ProjectModal } from './components/ProjectModal';
import { NewJobModal } from './components/NewJobModal';
import { JobCockpit } from './components/JobCockpit';
import { ProjectList } from './components/ProjectList';
import { MermaidViewer } from './components/MermaidViewer';
import { getOptionLabel, getOptionDescription } from './components/QuestionCard';
import { ErrorBoundary } from './components/ErrorBoundary';
import { DriverConfigModal } from './components/DriverConfigModal';

interface StagedAttachmentItem {
  localId: string;
  file: File;
  name: string;
  contentType: string;
  sizeBytes: number;
  previewUrl?: string;
  uploading: boolean;
  uploaded?: SessionAttachment;
  error?: string;
}

const MAX_ATTACHMENT_SIZE_BYTES = 25 * 1024 * 1024;
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

function extractMessageAttachments(ev: SessionMessage): SessionAttachment[] {
  if (!ev.structuredPayloadJson) return [];
  try {
    const parsed = JSON.parse(ev.structuredPayloadJson);
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
        const downloadUrl = item.downloadUrl || item.DownloadUrl || (targetId ? `/api/sessions/${ev.sessionId}/attachments/${encodeURIComponent(targetId)}/content` : '');
        const createdAt = item.createdAt || item.CreatedAt || ev.createdAt;

        return {
          id,
          sessionId: item.sessionId || item.SessionId || ev.sessionId,
          fileName,
          contentType,
          sizeBytes,
          s3Key,
          downloadUrl,
          createdAt
        };
      });
    }
  } catch {
    // Ignore malformed JSON
  }
  return [];
}

function parseTextQuestions(content: string): AskQuestionItem[] {
  if (!content) return [];
  const jsonMatch = content.match(/```(?:json:)?ask_question\s*([\s\S]*?)```/i);
  if (jsonMatch) {
    try {
      const parsed = JSON.parse(jsonMatch[1]);
      if (Array.isArray(parsed.questions)) return parsed.questions;
    } catch {}
  }
  const lines = content.split('\n');
  const items: AskQuestionItem[] = [];
  for (const rawLine of lines) {
    // Strip markdown links [text](url) -> text, and inline code `code` -> code
    const line = rawLine
      .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
      .replace(/`([^`]+)`/g, '$1')
      .trim();

    // Line must start with list marker (e.g. "1.", "1)", "*", "-")
    const listMatch = line.match(/^\s*(?:\d+[\.\)]|[-*])\s+(.+)$/);
    if (!listMatch) continue;

    const body = listMatch[1].trim();

    // Must strictly end with a question mark (allowing trailing formatting or quotes)
    if (!/\?\s*["'\)\]*_~]*$/.test(body)) continue;

    // Reject bullet points that are key-value headers (e.g. "**API Endpoint**: ...?")
    if (/^\*\*[^*]+?\*\*:\s*/.test(body)) continue;

    const fullQ = body;
    const extractedOptions: string[] = [];

    // Check for parenthesized choices, e.g. (Italian, Mexican, Asian, etc.)
    const parenMatch = fullQ.match(/\(([^)]+)\)/);
    if (parenMatch) {
      const rawChoices = parenMatch[1].split(/[,/]| or /i);
      for (const choice of rawChoices) {
        const trimmed = choice.trim().replace(/^etc\.?$/i, '').replace(/^e\.g\.?$/i, '').trim();
        // Ignore if looks like a URL, file path, or option is too long
        if (
          trimmed.length > 1 &&
          trimmed.length < 35 &&
          !trimmed.includes('://') &&
          !trimmed.includes('/') &&
          !extractedOptions.includes(trimmed)
        ) {
          extractedOptions.push(trimmed);
        }
      }
    }

    // If no paren choices, check for "A or B" pattern
    if (extractedOptions.length === 0 && fullQ.includes(' or ')) {
      const cleanedQ = fullQ.replace(/\?["'\)\]*_~]*$/, '');
      const orParts = cleanedQ.split(/\s+or\s+/i);
      if (orParts.length >= 2 && orParts.length <= 4) {
        for (const part of orParts) {
          const trimmed = part.trim();
          if (
            trimmed.length > 2 &&
            trimmed.length < 35 &&
            !trimmed.includes('://') &&
            !extractedOptions.includes(trimmed)
          ) {
            extractedOptions.push(trimmed);
          }
        }
      }
    }

    items.push({
      question: fullQ,
      options: extractedOptions,
      is_multi_select: false
    });
  }
  return items;
}

export default function App() {
  const auth = useAuth();
  const userEmail = auth?.user?.profile?.email || auth?.user?.profile?.name || 'admin';

  const [agents, setAgents] = useState<Agent[]>([]);
  const [personas, setPersonas] = useState<Persona[]>([]);
  const [sessions, setSessions] = useState<AgentSession[]>([]);
  const [activeTab, setActiveTab] = useState<'projects' | 'personas' | 'drivers'>('personas');
  const hasUserSwitchedTabRef = useRef(false);
  const [selectedPersonaId, setSelectedPersonaId] = useState<string | null>(null);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [events, setEvents] = useState<SessionMessage[]>([]);

  // Projects & Jobs state
  const [projects, setProjects] = useState<Project[]>([]);
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
  const [jobsByProject, setJobsByProject] = useState<Record<string, ProjectJob[]>>({});
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null);
  const [jobMessages, setJobMessages] = useState<JobMessage[]>([]);
  const [jobTasks, setJobTasks] = useState<JobTask[]>([]);
  const [showProjectModal, setShowProjectModal] = useState(false);
  const [editingProject, setEditingProject] = useState<Project | null>(null);
  const [showJobModal, setShowJobModal] = useState(false);
  const [jobModalProject, setJobModalProject] = useState<Project | null>(null);

  // Create / Edit Persona Modal state
  const [showPersonaModal, setShowPersonaModal] = useState(false);
  const [editingPersona, setEditingPersona] = useState<Persona | null>(null);
  const [personaIdInput, setPersonaIdInput] = useState('');
  const [personaNameInput, setPersonaNameInput] = useState('');
  const [personaRoleInput, setPersonaRoleInput] = useState('Codebase Engineer');
  const [personaPromptInput, setPersonaPromptInput] = useState('');
  const [personaDriverInput, setPersonaDriverInput] = useState('agy');
  const [personaDefaultModel, setPersonaDefaultModel] = useState('');
  const [personaDefaultEffort, setPersonaDefaultEffort] = useState('');
  const [personaDefaultMode, setPersonaDefaultMode] = useState('');
  const [personaDefaultTier, setPersonaDefaultTier] = useState('');
  const [personaDefaultContextSize, setPersonaDefaultContextSize] = useState('');
  const [personaDefaultWorkDir, setPersonaDefaultWorkDir] = useState('');
  const [personaMaxConcurrentJobs, setPersonaMaxConcurrentJobs] = useState<string>('');
  const [deletingPersonaId, setDeletingPersonaId] = useState<string | null>(null);
  const [deletingSessionId, setDeletingSessionId] = useState<string | null>(null);

  const [statusData, setStatusData] = useState<SystemStatusResponse | null>(null);
  const [capabilities, setCapabilities] = useState<SystemCapabilitiesResponse | null>(null);
  const [apiError, setApiError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [isActionPending, setIsActionPending] = useState<boolean>(false);

  // Folder picker modal state
  const [showFolderPicker, setShowFolderPicker] = useState<boolean>(false);
  const [browseData, setBrowseData] = useState<FilesystemBrowseResponse | null>(null);
  const [isBrowsing, setIsBrowsing] = useState<boolean>(false);
  const [browseError, setBrowseError] = useState<string | null>(null);

  // Prompt & New Session state
  const [promptInput, setPromptInput] = useState('');
  const [isCreatingSession, setIsCreatingSession] = useState(false);
  const [newSessionTitle, setNewSessionTitle] = useState('');
  const [newSessionPrompt, setNewSessionPrompt] = useState('');
  const [newSessionWorkDir, setNewSessionWorkDir] = useState('');
  const [newSessionModel, setNewSessionModel] = useState('');
  const [newSessionEffort, setNewSessionEffort] = useState('');
  const [newSessionMode, setNewSessionMode] = useState('');
  const [newSessionTier, setNewSessionTier] = useState('');
  const [newSessionContextSize, setNewSessionContextSize] = useState('');

  // Agent Quota & Usage details modal state
  const [agentUsages, setAgentUsages] = useState<Record<string, AgentUsageResponse>>({});
  const [selectedUsageAgent, setSelectedUsageAgent] = useState<Agent | null>(null);
  const [showUsageModal, setShowUsageModal] = useState(false);
  const [isRefreshingUsage, setIsRefreshingUsage] = useState(false);

  // Driver runtime configuration modal (limits live in the capabilities blob)
  const [configAgent, setConfigAgent] = useState<Agent | null>(null);
  const [isSavingDriverConfig, setIsSavingDriverConfig] = useState(false);

  // Active session inline control edit popover state
  const [editingSessionControl, setEditingSessionControl] = useState<'model' | 'effort' | 'tier' | 'mode' | 'contextSize' | null>(null);

  // Active prompt queue and Send Now dropdown state
  const [activeQueue, setActiveQueue] = useState<QueuedPromptItem[]>([]);
  const [showSendNowDropdown, setShowSendNowDropdown] = useState(false);

  // Quick Register Agent modal/drawer state
  const [showRegisterModal, setShowRegisterModal] = useState(false);
  const [newAgentType, setNewAgentType] = useState<'agy' | 'claude' | 'copilot' | 'openai' | 'generic'>('openai');
  const [newAgentId, setNewAgentId] = useState('');
  const [newAgentName, setNewAgentName] = useState('');
  const [newAgentRole, setNewAgentRole] = useState('Autonomous AI Engineer');
  const [newAgentEndpoint, setNewAgentEndpoint] = useState('http://localhost:11434/v1');
  const [newAgentApiKey, setNewAgentApiKey] = useState('');
  const [newAgentModel, setNewAgentModel] = useState('');
  const [isTestingOpenAi, setIsTestingOpenAi] = useState(false);
  const [openAiTestResult, setOpenAiTestResult] = useState<{
    success: boolean;
    message: string;
    models: { id: string; name: string }[];
  } | null>(null);

  // Interactive Question & Answer state (keyed by toolCallId)
  const [questionAnswers, setQuestionAnswers] = useState<
    Record<string, { selectedOptions: Record<number, string[]>; customTexts: Record<number, string> }>
  >({});
  const [submittingQuestionId, setSubmittingQuestionId] = useState<string | null>(null);

  // Artifacts state
  const [artifacts, setArtifacts] = useState<SessionArtifact[]>([]);
  const [selectedArtifactId, setSelectedArtifactId] = useState<string | null>(null);
  const [showArtifactsPanel, setShowArtifactsPanel] = useState(false);
  const [artifactPanelWidth, setArtifactPanelWidth] = useState(480);
  const [isResizingArtifacts, setIsResizingArtifacts] = useState(false);

  // Slash command palette state
  const [slashPaletteOpen, setSlashPaletteOpen] = useState(false);
  const [slashPaletteIndex, setSlashPaletteIndex] = useState(0);

  // Session Chat & New Session Attachments + Fullscreen Lightbox state
  const [stagedAttachments, setStagedAttachments] = useState<StagedAttachmentItem[]>([]);
  const [newSessionAttachments, setNewSessionAttachments] = useState<StagedAttachmentItem[]>([]);
  const [isDraggingChat, setIsDraggingChat] = useState(false);
  const [isDraggingNewSession, setIsDraggingNewSession] = useState(false);
  const [lightboxAttachment, setLightboxAttachment] = useState<{
    url: string;
    fileName: string;
    sizeBytes?: number;
  } | null>(null);
  const chatFileInputRef = useRef<HTMLInputElement | null>(null);
  const newSessionFileInputRef = useRef<HTMLInputElement | null>(null);

  // Auto-scroll toggle state (persisted in localStorage, default ON)
  const [autoScrollEnabled, setAutoScrollEnabled] = useState<boolean>(() => {
    try {
      const saved = localStorage.getItem('agentmesh_autoscroll_enabled');
      return saved !== null ? saved === 'true' : true;
    } catch {
      return true;
    }
  });
  const [isScrolledUp, setIsScrolledUp] = useState(false);
  const userScrolledUpRef = useRef(false);

  const timelineContainerRef = useRef<HTMLDivElement | null>(null);
  const prevSelectedSessionIdRef = useRef<string | null>(null);
  const { connection, connected: signalRConnected } = useSignalR('/hubs/agent-mesh');

  // --- Session trajectory "stick to bottom" -------------------------------------------
  // The code this replaces pinned once per session switch from an effect keyed on
  // `events`. That missed the cases operators actually hit:
  //   (a) the stream unmounts while Projects & Jobs (or the new-session composer) is open,
  //       so coming back mounts it at scrollTop 0 with `events` unchanged and nothing
  //       ever re-pins;
  //   (b) the pin fires against the *previous* session's still-rendered transcript, so it
  //       is spent on the wrong content and the real transcript lands at the top;
  //   (c) the transcript grows after the pin — mermaid resolves to SVG asynchronously,
  //       images decode late — so the scrollHeight measured at pin time is already stale.
  //
  // One function decides, from three distinct reasons to pin:
  //   * forced — the stream just mounted, or the operator asked (jump to bottom, or
  //              re-enable auto-scroll). Pin unconditionally.
  //   * new    — the transcript on screen changed identity (navigation) or gained events,
  //              i.e. a React render produced content we have not pinned yet.
  //   * grew   — the DOM got taller without a React render (case c). Only the observers
  //              can see that, so only they are allowed to ask for it.
  //
  // A scrollHeight read forces layout, so it happens only when we are about to write
  // scrollTop or are explicitly checking for growth — never on an ordinary render of what
  // is a very large component.
  const autoScrollEnabledRef = useRef(autoScrollEnabled);
  const forceTimelinePinRef = useRef(true);
  const pinnedTranscriptOwnerRef = useRef<string | null>(null);
  const pinnedTranscriptContentRef = useRef<string | null>(null);
  const pinnedTimelineHeightRef = useRef(-1);
  const timelineObserversCleanupRef = useRef<(() => void) | null>(null);
  const syncTimelineScrollRef = useRef<
    (options: { allowGrowthPin: boolean }) => void
  >(() => {});

  // Assigned during render rather than from an effect: the stream's ref callback fires
  // during commit, before passive effects run, and needs the current values immediately.
  autoScrollEnabledRef.current = autoScrollEnabled;

  const syncTimelineScroll = useCallback(
    ({ allowGrowthPin }: { allowGrowthPin: boolean }) => {
      const container = timelineContainerRef.current;
      if (!container) {
        // Stream not mounted. Invalidate the cached height so the next mount counts as
        // growth even if the transcript is byte-identical.
        pinnedTimelineHeightRef.current = -1;
        return;
      }

      // Owner is taken from the events themselves, not from selectedSessionId: while a
      // fetch is in flight the stream still shows the previous session's transcript, and
      // pinning against that would both look wrong and spend the pin (case b).
      const owner = events.length > 0 ? events[events.length - 1].sessionId : null;
      const contentKey = `${events.length}|${events[events.length - 1]?.id ?? ''}`;
      // A different session's transcript on screen means the operator navigated here, so
      // the newest event is what they came to see.
      const ownerChanged = pinnedTranscriptOwnerRef.current !== owner;
      const following = autoScrollEnabledRef.current && !userScrolledUpRef.current;
      // More events on the same transcript: follow them only if still tracking.
      const contentChanged = pinnedTranscriptContentRef.current !== contentKey;

      let shouldPin = forceTimelinePinRef.current || ownerChanged || (following && contentChanged);
      if (!shouldPin && allowGrowthPin && following) {
        shouldPin = container.scrollHeight !== pinnedTimelineHeightRef.current;
      }
      if (!shouldPin) return;

      forceTimelinePinRef.current = false;
      // Assigning scrollTop (rather than scrollIntoView) keeps the scroll on the timeline
      // container and avoids dragging ancestor scrollables around.
      container.scrollTop = container.scrollHeight;
      pinnedTimelineHeightRef.current = container.scrollHeight;
      pinnedTranscriptOwnerRef.current = owner;
      pinnedTranscriptContentRef.current = contentKey;

      if (ownerChanged) {
        // Opening a transcript clears any "scrolled up" carried over from the previous one,
        // so live events on the new session are followed again.
        userScrolledUpRef.current = false;
        setIsScrolledUp(false);
      }
    },
    [events]
  );

  // Also assigned during render: the ref callback below calls it synchronously on mount.
  syncTimelineScrollRef.current = syncTimelineScroll;

  // Runs before paint on renders that can change which transcript is on screen, so the top
  // of a newly opened session is never flashed. Deliberately keyed on the transcript
  // instead of running every render: growth that happens without a React render is the
  // observers' job, and an unrelated re-render should not pay for a layout read.
  useLayoutEffect(() => {
    syncTimelineScroll({ allowGrowthPin: false });
  }, [syncTimelineScroll]);

  // Container ref callback: mounting the stream is itself a reason to pin, and it is the
  // only place that learns when the observed node comes and goes.
  const handleTimelineContainerRef = useCallback((node: HTMLDivElement | null) => {
    timelineContainerRef.current = node;
    timelineObserversCleanupRef.current?.();
    timelineObserversCleanupRef.current = null;
    if (!node) {
      pinnedTimelineHeightRef.current = -1;
      return;
    }

    // Freshly mounted: it is at scrollTop 0 no matter what our caches say (case a).
    forceTimelinePinRef.current = true;
    syncTimelineScrollRef.current({ allowGrowthPin: false });

    if (typeof MutationObserver === 'undefined') return;

    // Content can grow without a React render: async mermaid SVG injection, streamed DOM,
    // lazily decoded images. Re-pin while the operator is still following (case c).
    const observer = new MutationObserver(() => syncTimelineScrollRef.current({ allowGrowthPin: true }));
    observer.observe(node, { childList: true, subtree: true });

    // An <img> changing intrinsic size on decode does not touch the DOM tree.
    const handleMediaLoad = (event: Event) => {
      const target = event.target as HTMLElement | null;
      if (target && target.tagName === 'IMG') syncTimelineScrollRef.current({ allowGrowthPin: true });
    };
    node.addEventListener('load', handleMediaLoad, true);

    timelineObserversCleanupRef.current = () => {
      observer.disconnect();
      node.removeEventListener('load', handleMediaLoad, true);
    };
  }, []);

  // A viewport resize moves the bottom without firing a scroll event on the container.
  useEffect(() => {
    const handleWindowResize = () => syncTimelineScrollRef.current({ allowGrowthPin: true });
    window.addEventListener('resize', handleWindowResize);
    return () => window.removeEventListener('resize', handleWindowResize);
  }, []);

  const handleToggleAutoScroll = () => {
    const next = !autoScrollEnabled;
    try {
      localStorage.setItem('agentmesh_autoscroll_enabled', String(next));
    } catch {}
    setAutoScrollEnabled(next);
    if (next) {
      // Turning following back on means "catch me up to now". Done here rather than in the
      // state updater so the scroll write isn't run twice under StrictMode.
      userScrolledUpRef.current = false;
      forceTimelinePinRef.current = true;
      syncTimelineScrollRef.current({ allowGrowthPin: false });
      setIsScrolledUp(false);
    }
  };

  const handleScrollToBottom = () => {
    userScrolledUpRef.current = false;
    forceTimelinePinRef.current = true;
    syncTimelineScrollRef.current({ allowGrowthPin: false });
    setIsScrolledUp(false);
  };

  const handleTimelineScroll = () => {
    if (!timelineContainerRef.current) return;
    const { scrollTop, scrollHeight, clientHeight } = timelineContainerRef.current;
    const distanceFromBottom = scrollHeight - (scrollTop + clientHeight);
    if (distanceFromBottom < 80) {
      userScrolledUpRef.current = false;
      setIsScrolledUp(false);
    } else if (distanceFromBottom > 200) {
      userScrolledUpRef.current = true;
      setIsScrolledUp(true);
    }
  };

  const handleOpenUsageModal = async (agent: Agent) => {
    setSelectedUsageAgent(agent);
    setShowUsageModal(true);
    setIsRefreshingUsage(true);
    try {
      const u = await api.getAgentUsage(agent.id, true);
      setAgentUsages((prev) => ({ ...prev, [agent.id]: u }));
    } catch (err) {
      console.error('Failed to load fresh agent usage:', err);
    } finally {
      setIsRefreshingUsage(false);
    }
  };

  const handleRefreshUsage = async (agentId: string) => {
    setIsRefreshingUsage(true);
    try {
      const u = await api.getAgentUsage(agentId, true);
      setAgentUsages((prev) => ({ ...prev, [agentId]: u }));
    } catch (err) {
      console.error('Failed to refresh agent usage:', err);
    } finally {
      setIsRefreshingUsage(false);
    }
  };

  const fetchAllData = useCallback(async () => {
    try {
      if (api.verifyAuth) {
        await api.verifyAuth().catch((err) => {
          throw err;
        });
      }

      const [statusRes, agentsRes, personasRes, sessionsRes, capsRes, projectsRes] = await Promise.all([
        api.getStatus(),
        api.getAgents(),
        api.getPersonas ? api.getPersonas().catch(() => []) : Promise.resolve([]),
        api.getSessions('all'),
        api.getCapabilities().catch(() => null),
        api.getProjects ? api.getProjects().catch(() => []) : Promise.resolve([])
      ]);
      setStatusData(statusRes);
      setAgents(agentsRes);
      setPersonas(personasRes);
      setSessions((prev) => {
        if (!sessionsRes || sessionsRes.length === 0) return prev;
        const fetchedMap = new Map(sessionsRes.map((s) => [s.id, s]));
        const result = [...sessionsRes];
        for (const s of prev) {
          if (!fetchedMap.has(s.id)) {
            result.push(s);
          }
        }
        return result;
      });
      setProjects(projectsRes || []);
      if (capsRes) {
        setCapabilities(capsRes);
      }
      setApiError(null);

      // If user hasn't manually switched tab, select initial tab
      if (!hasUserSwitchedTabRef.current) {
        if (projectsRes && projectsRes.length > 0) {
          setActiveTab('projects');
        } else if (personasRes.length > 0) {
          setActiveTab('personas');
        } else if (agentsRes.length > 0) {
          setActiveTab('drivers');
        }
      }

      // Auto-select first project if none selected
      if (projectsRes && projectsRes.length > 0) {
        setSelectedProjectId((prevProj) => {
          if (prevProj && projectsRes.some((p) => p.id === prevProj)) {
            return prevProj;
          }
          return projectsRes[0]?.id || null;
        });
      }

      // Fetch usage stats & quotas in parallel for all agents
      const usages: Record<string, AgentUsageResponse> = {};
      await Promise.all(
        agentsRes.map(async (agent) => {
          try {
            const u = await api.getAgentUsage(agent.id);
            usages[agent.id] = u;
          } catch {
            // Usage might not be available yet or endpoint error
          }
        })
      );
      setAgentUsages(usages);

      // Auto-select first persona, agent & session if none selected
      setSelectedPersonaId((prevPersona) => {
        if (prevPersona && personasRes.some((p) => p.id === prevPersona)) {
          return prevPersona;
        }
        return personasRes[0]?.id || null;
      });

      setSelectedAgentId((prevAgent) => {
        const nextAgent = prevAgent && agentsRes.some((a) => a.id === prevAgent)
          ? prevAgent
          : agentsRes[0]?.id || null;
        return nextAgent;
      });

      setSelectedSessionId((prevSession) => {
        if (prevSession && sessionsRes.some((s) => s.id === prevSession)) {
          return prevSession;
        }
        if (prevSession) {
          return prevSession;
        }
        return sessionsRes[0]?.id || null;
      });
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Unable to reach AgentMesh API');
    } finally {
      setIsLoading(false);
    }
  }, []);

  const fetchSessionEvents = useCallback(async (sessionId: string) => {
    try {
      const [msgs, latestSession] = await Promise.all([
        api.getSessionMessages(sessionId),
        api.getSession ? api.getSession(sessionId).catch(() => null) : Promise.resolve(null)
      ]);
      setEvents(msgs);
      if (latestSession) {
        setSessions((prev) =>
          prev.some((s) => s.id === latestSession.id)
            ? prev.map((s) => (s.id === latestSession.id ? { ...s, ...latestSession } : s))
            : [latestSession, ...prev]
        );
        if (latestSession.personaId) {
          setSelectedPersonaId(latestSession.personaId);
        }
        if (latestSession.driverId || latestSession.creatorAgentId) {
          setSelectedAgentId(latestSession.driverId || latestSession.creatorAgentId || null);
        }
      } else if (msgs.length > 0) {
        const lastMsg = msgs[msgs.length - 1];
        if (lastMsg.eventType === 'response') {
          setSessions((prev) =>
            prev.map((s) => (s.id === sessionId && s.status === 'thinking' ? { ...s, status: 'active' } : s))
          );
        }
      }
    } catch (err) {
      console.error('Failed to load session events:', err);
    }
  }, []);

  const fetchJobsForProject = useCallback(async (projectId: string) => {
    try {
      const jobs = await api.getProjectJobs(projectId);
      setJobsByProject((prev) => ({ ...prev, [projectId]: jobs }));
      return jobs;
    } catch {
      return [];
    }
  }, []);

  const handleSelectProject = (projectId: string) => {
    setSelectedProjectId(projectId);
  };

  const handleSelectJob = (projectId: string, jobId: string) => {
    setSelectedProjectId(projectId);
    setSelectedJobId(jobId);
  };

  useEffect(() => {
    if (selectedProjectId) {
      fetchJobsForProject(selectedProjectId).then((jobs) => {
        if (jobs && jobs.length > 0) {
          const nonArchived = jobs.filter((j) => j.status !== ProjectJobStatus.Archived);
          setSelectedJobId((cur) => (cur && jobs.some((j) => j.id === cur) ? cur : (nonArchived[0]?.id ?? jobs[0].id)));
        } else {
          setSelectedJobId(null);
        }
      });
    }
  }, [selectedProjectId, fetchJobsForProject]);

  useEffect(() => {
    if (selectedJobId) {
      Promise.all([
        api.getJobMessages(selectedJobId),
        api.getJobTasks(selectedJobId)
      ]).then(([msgs, tasks]) => {
        setJobMessages(msgs);
        setJobTasks(tasks);
      }).catch((err) => {
        console.error('Failed to load job details:', err);
      });
    } else {
      setJobMessages([]);
      setJobTasks([]);
    }
  }, [selectedJobId]);

  const handleSaveProject = async (req: any) => {
    if (editingProject) {
      const updated = await api.updateProject(editingProject.id, req);
      setProjects((prev) => prev.map((p) => (p.id === updated.id ? updated : p)));
    } else {
      const created = await api.createProject(req);
      setProjects((prev) => (prev.some((p) => p.id === created.id) ? prev : [created, ...prev]));
      setSelectedProjectId(created.id);
      fetchJobsForProject(created.id);
    }
  };

  const handleDeleteProject = async (projectId: string) => {
    await api.deleteProject(projectId);
    setProjects((prev) => prev.filter((p) => p.id !== projectId));
    if (selectedProjectId === projectId) {
      setSelectedProjectId(null);
      setSelectedJobId(null);
    }
  };

  const handleCreateJob = async (req: any) => {
    if (!jobModalProject) return;
    const created = await api.createProjectJob(jobModalProject.id, req);
    setJobsByProject((prev) => ({
      ...prev,
      [jobModalProject.id]: [
        created,
        ...(prev[jobModalProject.id] || []).filter((j) => j.id !== created.id)
      ]
    }));
    handleSelectJob(jobModalProject.id, created.id);
  };

  const handleDeleteJob = async (jobId: string) => {
    await api.deleteProjectJob(jobId);
    setJobsByProject((prev) => {
      const next = { ...prev };
      for (const pId of Object.keys(next)) {
        next[pId] = next[pId].filter((j) => j.id !== jobId);
      }
      return next;
    });
    if (selectedJobId === jobId) {
      setSelectedJobId(null);
      setJobMessages([]);
      setJobTasks([]);
    }
  };

  const handleSendJobMessage = async (content: string, attachments?: SessionAttachment[]) => {
    if (!selectedJobId) return;
    const msg = await api.postJobMessage(selectedJobId, {
      content,
      senderType: 'user',
      attachments
    });
    setJobMessages((prev) => {
      if (prev.some((m) => m.id === msg.id)) return prev;
      return [...prev, msg];
    });
  };

  const handleJobQuestionAnswer = async (
    sessionIdOrTargetId: string,
    payload: { toolCallId: string; answers: QuestionAnswerPayload[]; answerSummary: string }
  ) => {
    const matchingSession = sessions.find((s) => s.id === sessionIdOrTargetId);
    if (matchingSession) {
      await api.submitQuestionAnswer(matchingSession.id, {
        toolCallId: payload.toolCallId,
        answers: payload.answers
      });
    } else {
      if (selectedJobId) {
        await api.postJobMessage(selectedJobId, {
          content: `User answered question:\n${payload.answerSummary}\nPlease proceed with this answer.`,
          senderType: 'user'
        });
      }
    }
    if (selectedJobId) {
      const msgs = await api.getJobMessages(selectedJobId);
      setJobMessages(msgs);
    }
    await fetchAllData();
  };

  const handleAddTask = async (title: string, requiresDiscussion?: boolean, attachments?: SessionAttachment[]) => {
    if (!selectedJobId) return;
    const payload: { title: string; requiresDiscussion?: boolean; attachments?: SessionAttachment[] } = { title };
    if (requiresDiscussion) payload.requiresDiscussion = true;
    if (attachments && attachments.length > 0) payload.attachments = attachments;
    const task = await api.addJobTask(selectedJobId, payload);
    setJobTasks((prev) => (prev.some((t) => t.id === task.id) ? prev : [...prev, task]));
  };

  const handleUpdateTask = async (
    taskId: string,
    updates: { title?: string; isCompleted?: boolean; requiresDiscussion?: boolean; attachments?: SessionAttachment[] }
  ) => {
    if (!selectedJobId) return;
    const updated = await api.updateJobTask(selectedJobId, taskId, updates);
    setJobTasks((prev) => prev.map((t) => (t.id === taskId ? updated : t)));
  };

  const handleToggleTask = async (taskId: string, isCompleted: boolean) => {
    await handleUpdateTask(taskId, { isCompleted });
  };

  const handleAssignTask = async (taskId: string, personaId: string | null) => {
    if (!selectedJobId) return;
    try {
      const result = await api.assignJobTask(selectedJobId, taskId, personaId);
      // The server DTO is authoritative for both lists: a reassignment can move queued turns
      // and append a system message, so patching only the assignee field would leave the
      // queue and the checklist disagreeing with the DB.
      if (result.job) {
        applyJobFromServer(result.job);
        setJobTasks(result.job.tasks || []);
      } else {
        // No DTO to trust (older server): fall back to the single row rather than clearing
        // the whole checklist.
        setJobTasks((prev) => prev.map((t) => (t.id === taskId ? result.task : t)));
      }
      if (result.movedQueuedTurnCount > 0) {
        const msgs = await api.getJobMessages(selectedJobId);
        setJobMessages(msgs);
      }
    } catch (err) {
      console.error('Failed to assign task:', err);
      // Rethrown so the cockpit can show it against the row that failed instead of a
      // generic banner far away from where the click happened.
      throw err;
    }
  };

  const handleDeleteTask = async (taskId: string) => {
    if (!selectedJobId) return;
    await api.deleteJobTask(selectedJobId, taskId);
    setJobTasks((prev) => prev.filter((t) => t.id !== taskId));
  };

  const handleCompleteJob = async (resultSummary?: string) => {
    if (!selectedJobId) return;
    const updated = await api.completeJob(selectedJobId, { resultSummary });
    setJobsByProject((prev) => {
      const next = { ...prev };
      for (const pId of Object.keys(next)) {
        next[pId] = next[pId].map((j) => (j.id === updated.id ? updated : j));
      }
      return next;
    });
  };

  const handleArchiveJob = async (jobId?: string) => {
    const targetId = jobId || selectedJobId;
    if (!targetId) return;
    try {
      const updated = await api.archiveJob(targetId);
      setJobsByProject((prev) => {
        const next = { ...prev };
        for (const pId of Object.keys(next)) {
          next[pId] = next[pId].map((j) => (j.id === updated.id ? updated : j));
        }
        return next;
      });
    } catch (err) {
      console.error('Failed to archive job:', err);
      setApiError(err instanceof Error ? err.message : 'Failed to archive job');
    }
  };

  const handleReopenJob = async (jobId?: string) => {
    const targetId = jobId || selectedJobId;
    if (!targetId) return;
    try {
      const updated = await api.reopenJob(targetId);
      setJobsByProject((prev) => {
        const next = { ...prev };
        for (const pId of Object.keys(next)) {
          next[pId] = next[pId].map((j) => (j.id === updated.id ? updated : j));
        }
        return next;
      });
    } catch (err) {
      console.error('Failed to reopen job:', err);
      setApiError(err instanceof Error ? err.message : 'Failed to reopen job');
    }
  };

  const applyJobFromServer = useCallback((job?: ProjectJob) => {
    if (!job) return;
    setJobsByProject((prev) => ({
      ...prev,
      [job.projectId]: (prev[job.projectId] || []).map((j) => (j.id === job.id ? job : j))
    }));
  }, []);

  const handleCancelQueuedJobTurn = async (turnId: string, jobId?: string) => {
    const targetId = jobId || selectedJobId;
    if (!targetId) return;
    try {
      const job = await api.cancelQueuedJobTurn(targetId, turnId);
      // Trust the server DTO; the local filter can diverge from what was actually removed.
      if (job) {
        applyJobFromServer(job);
      } else {
        setJobsByProject((prev) => {
          const next = { ...prev };
          for (const pId of Object.keys(next)) {
            next[pId] = next[pId].map((j) => {
              if (j.id === targetId) {
                const updatedQueued = (j.queuedTurns || []).filter((t) => t.id !== turnId);
                return { ...j, queuedTurns: updatedQueued };
              }
              return j;
            });
          }
          return next;
        });
      }
      const msgs = await api.getJobMessages(targetId);
      setJobMessages(msgs);
    } catch (err) {
      console.error('Failed to cancel queued turn:', err);
      setApiError(err instanceof Error ? err.message : 'Failed to cancel queued turn');
    }
  };

  const handleCancelQueuedJobTurns = async (personaId: string, jobId?: string) => {
    const targetId = jobId || selectedJobId;
    if (!targetId) return;
    try {
      const job = await api.cancelQueuedJobTurns(targetId, personaId);
      if (job) applyJobFromServer(job);
      const msgs = await api.getJobMessages(targetId);
      setJobMessages(msgs);
    } catch (err) {
      console.error('Failed to cancel queued turns:', err);
      setApiError(err instanceof Error ? err.message : 'Failed to cancel queued turns');
    }
  };

  const fetchSessionArtifacts = useCallback(async (sessionId: string) => {
    try {
      const list = await api.getSessionArtifacts(sessionId);
      setArtifacts(list);
      if (list.length > 0) {
        setSelectedArtifactId((prev) => (prev && list.some((a) => a.id === prev) ? prev : list[0].id));
      }
    } catch {
      setArtifacts([]);
    }
  }, []);

  useEffect(() => {
    fetchAllData();
  }, [fetchAllData]);

  useEffect(() => {
    // No pin is applied here on purpose. The trajectory still shows the previous
    // session's transcript until this fetch resolves, and pinning that would both look
    // wrong and consume the pin. syncTimelineScroll pins when *this* session's
    // transcript is first mounted, keyed on the owner of the events on screen.
    if (selectedSessionId) {
      if (prevSelectedSessionIdRef.current && prevSelectedSessionIdRef.current !== selectedSessionId && connection && signalRConnected) {
        connection.invoke('LeaveSession', prevSelectedSessionIdRef.current).catch(() => {});
      }
      prevSelectedSessionIdRef.current = selectedSessionId;
      fetchSessionEvents(selectedSessionId);
      fetchSessionArtifacts(selectedSessionId);
      if (connection && signalRConnected) {
        connection.invoke('JoinSession', selectedSessionId).catch(() => {});
      }
    } else {
      if (prevSelectedSessionIdRef.current && connection && signalRConnected) {
        connection.invoke('LeaveSession', prevSelectedSessionIdRef.current).catch(() => {});
      }
      prevSelectedSessionIdRef.current = null;
      setEvents([]);
      setArtifacts([]);
      setSelectedArtifactId(null);
    }
  }, [selectedSessionId, fetchSessionEvents, fetchSessionArtifacts, connection, signalRConnected]);

  // Handle panel resizing
  useEffect(() => {
    const handleMouseMove = (e: MouseEvent) => {
      if (!isResizingArtifacts) return;
      const newWidth = window.innerWidth - e.clientX;
      if (newWidth >= 300 && newWidth <= Math.min(850, window.innerWidth - 300)) {
        setArtifactPanelWidth(newWidth);
      }
    };

    const handleMouseUp = () => {
      setIsResizingArtifacts(false);
    };

    if (isResizingArtifacts) {
      window.addEventListener('mousemove', handleMouseMove);
      window.addEventListener('mouseup', handleMouseUp);
    }
    return () => {
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
    };
  }, [isResizingArtifacts]);

  // Real-time SignalR listeners
  useEffect(() => {
    if (!connection) return;

    const handleReceiveMessage = (msg: SessionMessage) => {
      if (msg.sessionId === selectedSessionId) {
        setEvents((prev) => {
          if (prev.some((e) => e.id === msg.id)) return prev;
          return [...prev, msg];
        });
      }
      setSessions((prev) =>
        prev.map((s) =>
          s.id === msg.sessionId
            ? {
                ...s,
                messageCount: (s.messageCount || 0) + 1,
                status:
                  msg.eventType === 'tool_call' && (msg.toolName === 'ask_question' || msg.toolName === 'ask_user')
                    ? 'waiting_for_input'
                    : (msg.eventType === 'prompt' || msg.eventType === 'thought' || msg.eventType === 'tool_call')
                    ? 'thinking'
                    : msg.eventType === 'response'
                    ? (s.status === 'waiting_for_input' ? 'waiting_for_input' : 'active')
                    : s.status,
                updatedAt: msg.createdAt || new Date().toISOString()
              }
            : s
        )
      );
    };

    const handleSessionCreated = (session: AgentSession) => {
      setSessions((prev) => {
        if (prev.some((s) => s.id === session.id)) return prev;
        return [session, ...prev];
      });
      setSelectedSessionId(session.id);
      if (session.personaId) {
        setSelectedPersonaId(session.personaId);
      }
      if (session.driverId || session.creatorAgentId) {
        setSelectedAgentId(session.driverId || session.creatorAgentId || null);
      }
    };

    const handleSessionDeleted = (data: { id?: string; sessionId?: string; personaId?: string; driverId?: string }) => {
      const deletedId = data.id || data.sessionId;
      if (!deletedId) return;
      setSessions((prev) => prev.filter((s) => s.id !== deletedId));
      setSelectedSessionId((cur) => {
        if (cur === deletedId) {
          const siblings = sessions.filter((s) => s.id !== deletedId && (
            (data.personaId && s.personaId === data.personaId) ||
            (!data.personaId && (s.driverId === data.driverId || s.creatorAgentId === data.driverId))
          ));
          if (siblings.length > 0) {
            return siblings[0].id;
          }
          setIsCreatingSession(true);
          return null;
        }
        return cur;
      });
    };

    const handleSessionUpdated = (updatedSession?: AgentSession) => {
      if (updatedSession?.id) {
        setSessions((prev) =>
          prev.map((s) => (s.id === updatedSession.id ? { ...s, ...updatedSession } : s))
        );
      }
      fetchAllData();
      if (selectedSessionId) {
        fetchSessionArtifacts(selectedSessionId);
      }
    };

    const handleMeshReset = () => {
      setAgents([]);
      setPersonas([]);
      setSessions([]);
      setEvents([]);
      setArtifacts([]);
      setSelectedPersonaId(null);
      setSelectedAgentId(null);
      setSelectedSessionId(null);
    };

    const handleQueueUpdated = (data: { sessionId: string; queue: QueuedPromptItem[] }) => {
      if (data.sessionId === selectedSessionId) {
        setActiveQueue(data.queue || []);
      }
      fetchAllData();
    };

    const handleArtifactCreated = (artifact: SessionArtifact) => {
      if (artifact.sessionId === selectedSessionId) {
        setArtifacts((prev) => {
          const exists = prev.some((a) => a.id === artifact.id || a.name === artifact.name);
          return exists
            ? prev.map((a) => (a.id === artifact.id || a.name === artifact.name ? artifact : a))
            : [...prev, artifact];
        });
        setSelectedArtifactId((prev) => prev ?? artifact.id);
      }
    };

    const handleArtifactUpdated = (artifact: SessionArtifact) => {
      if (artifact.sessionId === selectedSessionId) {
        setArtifacts((prev) =>
          prev.map((a) => (a.id === artifact.id || a.name === artifact.name ? artifact : a))
        );
      }
    };

    const handleJobMessageAdded = (msg: JobMessage) => {
      setJobMessages((prev) => {
        if (msg.jobId === selectedJobId) {
          if (prev.some((m) => m.id === msg.id)) return prev;
          return [...prev, msg];
        }
        return prev;
      });
    };

    const handleJobTaskUpdated = (data: { jobId: string; tasks: JobTask[] }) => {
      if (data.jobId === selectedJobId) {
        const unique = (data.tasks || []).filter(
          (t, idx, self) => idx === self.findIndex((x) => x.id === t.id)
        );
        setJobTasks(unique);
      }
      setJobsByProject((prev) => {
        const next = { ...prev };
        for (const pId of Object.keys(next)) {
          if (next[pId]?.some((j) => j.id === data.jobId)) {
            next[pId] = next[pId].map((j) =>
              j.id === data.jobId ? { ...j, tasks: data.tasks } : j
            );
          }
        }
        return next;
      });
    };

    const handleJobCreated = (job: ProjectJob) => {
      setJobsByProject((prev) => ({
        ...prev,
        [job.projectId]: [job, ...(prev[job.projectId] || []).filter((j) => j.id !== job.id)]
      }));
    };

    const handleJobUpdated = (job: ProjectJob) => {
      setJobsByProject((prev) => ({
        ...prev,
        [job.projectId]: (prev[job.projectId] || []).map((j) => (j.id === job.id ? job : j))
      }));
    };

    const handleJobDeleted = (data: { id: string; projectId: string }) => {
      setJobsByProject((prev) => ({
        ...prev,
        [data.projectId]: (prev[data.projectId] || []).filter((j) => j.id !== data.id)
      }));
      setSelectedJobId((cur) => (cur === data.id ? null : cur));
    };

    const handleProjectCreated = (proj: Project) => {
      setProjects((prev) => [proj, ...prev.filter((p) => p.id !== proj.id)]);
    };

    const handleProjectUpdated = (proj: Project) => {
      setProjects((prev) => prev.map((p) => (p.id === proj.id ? proj : p)));
    };

    const handleProjectDeleted = (data: { id: string }) => {
      setProjects((prev) => prev.filter((p) => p.id !== data.id));
      setSelectedProjectId((cur) => (cur === data.id ? null : cur));
    };

    connection.on('ReceiveMessage', handleReceiveMessage);
    connection.on('SessionCreated', handleSessionCreated);
    connection.on('SessionDeleted', handleSessionDeleted);
    connection.on('SessionUpdated', handleSessionUpdated);
    connection.on('QueueUpdated', handleQueueUpdated);
    connection.on('ArtifactCreated', handleArtifactCreated);
    connection.on('ArtifactUpdated', handleArtifactUpdated);
    connection.on('AgentRegistered', handleSessionUpdated);
    connection.on('AgentUpdated', handleSessionUpdated);
    connection.on('PersonaCreated', handleSessionUpdated);
    connection.on('PersonaUpdated', handleSessionUpdated);
    connection.on('PersonaDeleted', handleSessionUpdated);
    connection.on('MeshReset', handleMeshReset);
    connection.on('JobMessageAdded', handleJobMessageAdded);
    connection.on('JobTaskUpdated', handleJobTaskUpdated);
    connection.on('JobCreated', handleJobCreated);
    connection.on('JobUpdated', handleJobUpdated);
    connection.on('JobDeleted', handleJobDeleted);
    connection.on('ProjectCreated', handleProjectCreated);
    connection.on('ProjectUpdated', handleProjectUpdated);
    connection.on('ProjectDeleted', handleProjectDeleted);

    return () => {
      connection.off('ReceiveMessage', handleReceiveMessage);
      connection.off('SessionCreated', handleSessionCreated);
      connection.off('SessionDeleted', handleSessionDeleted);
      connection.off('SessionUpdated', handleSessionUpdated);
      connection.off('QueueUpdated', handleQueueUpdated);
      connection.off('ArtifactCreated', handleArtifactCreated);
      connection.off('ArtifactUpdated', handleArtifactUpdated);
      connection.off('AgentRegistered', handleSessionUpdated);
      connection.off('AgentUpdated', handleSessionUpdated);
      connection.off('PersonaCreated', handleSessionUpdated);
      connection.off('PersonaUpdated', handleSessionUpdated);
      connection.off('PersonaDeleted', handleSessionUpdated);
      connection.off('MeshReset', handleMeshReset);
      connection.off('JobMessageAdded', handleJobMessageAdded);
      connection.off('JobTaskUpdated', handleJobTaskUpdated);
      connection.off('JobCreated', handleJobCreated);
      connection.off('JobUpdated', handleJobUpdated);
      connection.off('JobDeleted', handleJobDeleted);
      connection.off('ProjectCreated', handleProjectCreated);
      connection.off('ProjectUpdated', handleProjectUpdated);
      connection.off('ProjectDeleted', handleProjectDeleted);
    };
  }, [connection, selectedSessionId, fetchAllData, fetchSessionEvents, fetchSessionArtifacts]);

  const handleResetMesh = async () => {
    setIsActionPending(true);
    try {
      await api.resetMesh();
      setAgents([]);
      setPersonas([]);
      setSessions([]);
      setEvents([]);
      setSelectedPersonaId(null);
      setSelectedAgentId(null);
      setSelectedSessionId(null);
      setIsCreatingSession(false);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to reset mesh data');
    } finally {
      setIsActionPending(false);
    }
  };

  const loadBrowseDirectory = useCallback(async (path?: string) => {
    setIsBrowsing(true);
    setBrowseError(null);
    try {
      const res = await api.browseFilesystem(path);
      setBrowseData(res);
    } catch (err) {
      setBrowseError(err instanceof Error ? err.message : 'Failed to browse directory');
    } finally {
      setIsBrowsing(false);
    }
  }, []);

  const openFolderPicker = useCallback(() => {
    const initialPath = newSessionWorkDir || capabilities?.workspace?.currentDirectory || undefined;
    loadBrowseDirectory(initialPath);
    setShowFolderPicker(true);
  }, [newSessionWorkDir, capabilities?.workspace?.currentDirectory, loadBrowseDirectory]);

  const selectedSession = sessions.find((s) => s.id === selectedSessionId) || null;
  const isSessionThinking = useMemo(() => {
    if (!selectedSession) return false;
    if (selectedSession.status !== 'thinking') return false;
    if (events.length > 0) {
      const lastEv = events[events.length - 1];
      if (lastEv.eventType === 'response' || lastEv.eventType === 'system') {
        return false;
      }
    }
    return true;
  }, [selectedSession, events]);
  const selectedPersona = personas.find((p) => p.id === (selectedSession?.personaId || selectedPersonaId)) ||
    personas.find((p) => p.id === selectedPersonaId) ||
    personas[0] ||
    null;
  const activeCreationPersona = personas.find((p) => p.id === selectedPersonaId) || personas[0] || null;
  const activeCreationAgent =
    agents.find((a) => a.id === selectedAgentId) || agents[0] || null;
  const selectedAgent = isCreatingSession
    ? (activeTab === 'personas' && activeCreationPersona
        ? agents.find((a) => a.id === activeCreationPersona.driverId) || activeCreationAgent
        : activeCreationAgent)
    : agents.find((a) => a.id === (selectedSession?.driverId || selectedSession?.creatorAgentId || selectedAgentId)) || null;

  const getRuntimeCapsForDriver = useCallback((driverIdOrAgent?: string | Agent | null) => {
    if (!capabilities || !capabilities.agentRuntimes) return null;
    if (!driverIdOrAgent) return capabilities.agentRuntimes['agy'] || null;

    const idLower = (typeof driverIdOrAgent === 'string' ? driverIdOrAgent : driverIdOrAgent.id).toLowerCase();
    const nameLower = (typeof driverIdOrAgent === 'string' ? driverIdOrAgent : driverIdOrAgent.name).toLowerCase();
    let key = 'agy';
    if (idLower.includes('copilot') || nameLower.includes('copilot')) key = 'copilot';
    else if (idLower.includes('claude') || nameLower.includes('claude')) key = 'claude';
    else if (idLower.includes('openai') || nameLower.includes('openai') || idLower.includes('ollama') || nameLower.includes('ollama') || idLower.includes('vllm') || nameLower.includes('vllm') || idLower.includes('lmstudio') || nameLower.includes('lmstudio')) key = 'openai';
    else if (idLower.includes('agy') || idLower.includes('antigravity') || nameLower.includes('agy') || nameLower.includes('antigravity')) key = 'agy';
    else if (capabilities.agentRuntimes[idLower]) key = idLower;
    else key = 'agy';

    return capabilities.agentRuntimes[key] || capabilities.agentRuntimes['agy'] || null;
  }, [capabilities]);

  const currentRuntimeCaps = useMemo(() => {
    if (!capabilities) return null;
    if (isCreatingSession && activeTab === 'personas' && activeCreationPersona) {
      return getRuntimeCapsForDriver(activeCreationPersona.driverId);
    }
    const agent = isCreatingSession ? activeCreationAgent : selectedAgent;
    if (!agent && selectedSession?.driverId) {
      return getRuntimeCapsForDriver(selectedSession.driverId);
    }
    return getRuntimeCapsForDriver(agent);
  }, [capabilities, isCreatingSession, activeTab, activeCreationPersona, activeCreationAgent, selectedAgent, selectedSession, getRuntimeCapsForDriver]);

  const personaModalRuntimeCaps = useMemo(() => {
    return getRuntimeCapsForDriver(personaDriverInput);
  }, [getRuntimeCapsForDriver, personaDriverInput]);

  const openCreatePersonaModal = useCallback((defaultDriverId?: string) => {
    setEditingPersona(null);
    setPersonaIdInput('');
    setPersonaNameInput('');
    setPersonaRoleInput('Codebase Engineer');
    setPersonaPromptInput('');
    setPersonaDriverInput(defaultDriverId || selectedAgentId || agents[0]?.id || 'agy');
    setPersonaDefaultModel('');
    setPersonaDefaultEffort('');
    setPersonaDefaultMode('');
    setPersonaDefaultTier('');
    setPersonaDefaultContextSize('');
    setPersonaDefaultWorkDir('');
    setPersonaMaxConcurrentJobs('');
    setShowPersonaModal(true);
  }, [selectedAgentId, agents]);

  const openEditPersonaModal = useCallback((persona: Persona) => {
    setEditingPersona(persona);
    setPersonaIdInput(persona.id);
    setPersonaNameInput(persona.name);
    setPersonaRoleInput(persona.role || 'Codebase Engineer');
    setPersonaPromptInput(persona.personaPrompt || '');
    setPersonaDriverInput(persona.driverId || 'agy');
    setPersonaDefaultModel(persona.defaultModel || '');
    setPersonaDefaultEffort(persona.defaultEffort || '');
    setPersonaDefaultMode(persona.defaultMode || '');
    setPersonaDefaultTier(persona.defaultTier || '');
    setPersonaDefaultContextSize(persona.defaultContextSize || '');
    setPersonaDefaultWorkDir(persona.defaultWorkingDirectory || '');
    setPersonaMaxConcurrentJobs(persona.maxConcurrentJobs ? String(persona.maxConcurrentJobs) : '');
    setShowPersonaModal(true);
  }, []);

  const startNewSessionWithPersona = useCallback((persona: Persona) => {
    setSelectedPersonaId(persona.id);
    setSelectedAgentId(persona.driverId);
    setNewSessionModel(persona.defaultModel || '');
    setNewSessionEffort(persona.defaultEffort || '');
    setNewSessionMode(persona.defaultMode || '');
    setNewSessionTier(persona.defaultTier || '');
    setNewSessionContextSize(persona.defaultContextSize || '');
    setNewSessionWorkDir(persona.defaultWorkingDirectory || '');
    setIsCreatingSession(true);
  }, []);

  const handleSavePersona = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!personaNameInput.trim() || !personaDriverInput.trim()) return;

    setIsActionPending(true);
    try {
      if (editingPersona) {
        const updated = await api.updatePersona(editingPersona.id, {
          name: personaNameInput.trim(),
          role: personaRoleInput.trim() || 'Codebase Engineer',
          personaPrompt: personaPromptInput.trim(),
          driverId: personaDriverInput.trim().toLowerCase(),
          defaultModel: personaDefaultModel || '',
          defaultEffort: personaDefaultEffort || '',
          defaultMode: personaDefaultMode || '',
          defaultTier: personaDefaultTier || '',
          defaultContextSize: personaDefaultContextSize || '',
          defaultWorkingDirectory: personaDefaultWorkDir || '',
          maxConcurrentJobs: personaMaxConcurrentJobs.trim() ? Number(personaMaxConcurrentJobs) : undefined
        });
        setShowPersonaModal(false);
        await fetchAllData();
        setSelectedPersonaId(updated.id);
      } else {
        const created = await api.createPersona({
          id: personaIdInput.trim() || undefined,
          name: personaNameInput.trim(),
          role: personaRoleInput.trim() || 'Codebase Engineer',
          personaPrompt: personaPromptInput.trim() || undefined,
          driverId: personaDriverInput.trim().toLowerCase(),
          defaultModel: personaDefaultModel || undefined,
          defaultEffort: personaDefaultEffort || undefined,
          defaultMode: personaDefaultMode || undefined,
          defaultTier: personaDefaultTier || undefined,
          defaultContextSize: personaDefaultContextSize || undefined,
          defaultWorkingDirectory: personaDefaultWorkDir || undefined,
          maxConcurrentJobs: personaMaxConcurrentJobs.trim() ? Number(personaMaxConcurrentJobs) : undefined
        });
        setShowPersonaModal(false);
        hasUserSwitchedTabRef.current = true;
        setActiveTab('personas');
        await fetchAllData();
        setSelectedPersonaId(created.id);
      }
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to save persona');
    } finally {
      setIsActionPending(false);
    }
  };

  const handleDeletePersona = async (personaId: string) => {
    setIsActionPending(true);
    try {
      await api.deletePersona(personaId);
      setDeletingPersonaId(null);
      if (selectedPersonaId === personaId) {
        setSelectedPersonaId(null);
      }
      await fetchAllData();
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to delete persona');
    } finally {
      setIsActionPending(false);
    }
  };

  const handleDeleteSession = async (sessionId: string) => {
    setIsActionPending(true);
    try {
      await api.deleteSession(sessionId);
      setDeletingSessionId(null);
      setSessions((prev) => prev.filter((s) => s.id !== sessionId));

      if (selectedSessionId === sessionId) {
        const targetPersonaId = selectedPersona?.id || selectedPersonaId;
        const targetDriverId = selectedAgent?.id || selectedAgentId;

        const siblings = sessions.filter(
          (s) =>
            s.id !== sessionId &&
            ((activeTab === 'personas' && targetPersonaId && (s.personaId === targetPersonaId || (!s.personaId && s.driverId === selectedPersona?.driverId))) ||
             (activeTab === 'drivers' && targetDriverId && (s.driverId === targetDriverId || s.creatorAgentId === targetDriverId)))
        );

        if (siblings.length > 0) {
          setSelectedSessionId(siblings[0].id);
        } else {
          setSelectedSessionId(null);
          setIsCreatingSession(true);
        }
      }
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to delete session');
    } finally {
      setIsActionPending(false);
    }
  };

  const availableModels = useMemo(() => {
    const baseModels = currentRuntimeCaps?.models || [];
    const agent = isCreatingSession ? activeCreationAgent : selectedAgent;
    let extraModels: ModelCapability[] = [];
    if (agent?.capabilitiesJson) {
      try {
        const parsed = JSON.parse(agent.capabilitiesJson);
        if (Array.isArray(parsed.models)) {
          extraModels = parsed.models.map((m: any) => ({
            id: m.id || m.name,
            name: m.name || m.id,
            efforts: ['low', 'medium', 'high'],
            contextSizes: ['32k', '128k']
          }));
        }
      } catch {}
    }
    return [
      ...extraModels,
      ...baseModels.filter((m) => !extraModels.some((em) => em.id === m.id))
    ];
  }, [currentRuntimeCaps, isCreatingSession, activeCreationAgent, selectedAgent]);

  const availableModes = useMemo(() => {
    return currentRuntimeCaps?.modes || [];
  }, [currentRuntimeCaps]);

  const availableTiers = useMemo(() => {
    return currentRuntimeCaps?.tiers || [];
  }, [currentRuntimeCaps]);

  const selectedModelCap = useMemo(() => {
    if (!newSessionModel || !currentRuntimeCaps) return null;
    return availableModels.find((m) => m.id === newSessionModel) || null;
  }, [availableModels, currentRuntimeCaps, newSessionModel]);

  const availableEfforts = useMemo(() => {
    return selectedModelCap?.efforts || [];
  }, [selectedModelCap]);

  // Sync / validate effort when model changes
  useEffect(() => {
    if (newSessionEffort && !availableEfforts.includes(newSessionEffort)) {
      setNewSessionEffort('');
    }
  }, [availableEfforts, newSessionEffort]);

  // Sync / validate model, mode, tier when agent changes
  useEffect(() => {
    if (newSessionModel && availableModels.length > 0 && !availableModels.some((m) => m.id === newSessionModel) && currentRuntimeCaps?.adapter !== 'openai') {
      setNewSessionModel('');
    }
    if (newSessionMode && availableModes.length > 0 && !availableModes.some((m) => m.id === newSessionMode)) {
      setNewSessionMode('');
    }
    if (newSessionTier && availableTiers.length > 0 && !availableTiers.some((t) => t.id === newSessionTier)) {
      setNewSessionTier('');
    }
  }, [availableModels, availableModes, availableTiers, newSessionModel, newSessionMode, newSessionTier]);

  const availableContextSizes = useMemo(() => {
    if (!currentRuntimeCaps) return [];
    const modelContextIds = selectedModelCap?.contextSizes;
    if (modelContextIds && modelContextIds.length > 0) {
      const driverContexts = currentRuntimeCaps.contextSizes || [];
      return modelContextIds.map((id) => {
        const match = driverContexts.find((c) => c.id.toLowerCase() === id.toLowerCase());
        return match || { id, label: id, description: `${id} context window` };
      });
    }
    return currentRuntimeCaps.contextSizes || [];
  }, [currentRuntimeCaps, selectedModelCap]);

  // Sync / validate context size when model/driver changes
  useEffect(() => {
    if (newSessionContextSize && availableContextSizes.length > 0 && !availableContextSizes.some((c) => c.id.toLowerCase() === newSessionContextSize.toLowerCase())) {
      setNewSessionContextSize('');
    }
  }, [availableContextSizes, newSessionContextSize]);

  const createPreviewUrl = (file: File): string | undefined => {
    if (isImageAttachment(file.type, file.name) && typeof URL !== 'undefined' && typeof URL.createObjectURL === 'function') {
      try {
        return URL.createObjectURL(file);
      } catch {
        return undefined;
      }
    }
    return undefined;
  };

  const stageFilesForComposer = useCallback(
    async (rawFiles: FileList | File[], target: 'chat' | 'newSession') => {
      const incoming = Array.from(rawFiles || []);
      if (incoming.length === 0) return;

      const currentList = target === 'chat' ? stagedAttachments : newSessionAttachments;
      if (currentList.length + incoming.length > MAX_ATTACHMENTS_PER_MESSAGE) {
        setApiError(`Maximum of ${MAX_ATTACHMENTS_PER_MESSAGE} files can be attached per message.`);
        return;
      }

      for (const f of incoming) {
        if (f.size > MAX_ATTACHMENT_SIZE_BYTES) {
          setApiError(`File "${f.name}" exceeds the 25 MB maximum file size limit.`);
          return;
        }
      }

      const newItems: StagedAttachmentItem[] = incoming.map((file, idx) => {
        const safeName = file.name || `clipboard-image-${Date.now()}-${idx}.png`;
        return {
          localId: `att-${Date.now()}-${idx}-${Math.random().toString(36).slice(2, 6)}`,
          file,
          name: safeName,
          contentType: file.type || 'application/octet-stream',
          sizeBytes: file.size,
          previewUrl: createPreviewUrl(file),
          uploading: target === 'chat' && Boolean(selectedSessionId)
        };
      });

      if (target === 'newSession') {
        setNewSessionAttachments((prev) => [...prev, ...newItems]);
        return;
      }

      setStagedAttachments((prev) => [...prev, ...newItems]);

      if (selectedSessionId) {
        try {
          let uploadedDtos: SessionAttachment[];
          if (typeof api.uploadSessionAttachments === 'function') {
            uploadedDtos = await api.uploadSessionAttachments(
              selectedSessionId,
              newItems.map((i) => i.file)
            );
          } else {
            uploadedDtos = newItems.map((item) => ({
              id: item.localId,
              sessionId: selectedSessionId,
              fileName: item.name,
              contentType: item.contentType,
              sizeBytes: item.sizeBytes,
              s3Key: `sessions/${selectedSessionId}/attachments/${item.localId}/${item.name}`,
              downloadUrl: item.previewUrl || `/api/sessions/${selectedSessionId}/attachments/${item.localId}/content`,
              createdAt: new Date().toISOString()
            }));
          }

          setStagedAttachments((prev) =>
            prev.map((existing) => {
              const matchIdx = newItems.findIndex((n) => n.localId === existing.localId);
              if (matchIdx === -1) return existing;
              const dto = uploadedDtos[matchIdx] || {
                id: existing.localId,
                sessionId: selectedSessionId,
                fileName: existing.name,
                contentType: existing.contentType,
                sizeBytes: existing.sizeBytes,
                s3Key: `sessions/${selectedSessionId}/attachments/${existing.localId}/${existing.name}`,
                downloadUrl: existing.previewUrl || `/api/sessions/${selectedSessionId}/attachments/${existing.localId}/content`,
                createdAt: new Date().toISOString()
              };
              return {
                ...existing,
                uploading: false,
                uploaded: dto
              };
            })
          );
        } catch (err) {
          const errMsg = err instanceof Error ? err.message : 'Failed to upload attachment';
          setApiError(errMsg);
          setStagedAttachments((prev) =>
            prev.map((existing) =>
              newItems.some((n) => n.localId === existing.localId)
                ? { ...existing, uploading: false, error: errMsg }
                : existing
            )
          );
        }
      }
    },
    [stagedAttachments, newSessionAttachments, selectedSessionId]
  );

  const handleRemoveStagedAttachment = useCallback((localId: string, target: 'chat' | 'newSession') => {
    const updater = (prev: StagedAttachmentItem[]) => {
      const removed = prev.find((item) => item.localId === localId);
      if (removed?.previewUrl && typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
        try {
          URL.revokeObjectURL(removed.previewUrl);
        } catch {}
      }
      return prev.filter((item) => item.localId !== localId);
    };
    if (target === 'chat') {
      setStagedAttachments(updater);
    } else {
      setNewSessionAttachments(updater);
    }
  }, []);

  const extractClipboardFiles = (e: React.ClipboardEvent): File[] => {
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
  };

  const handleChatPaste = useCallback(
    (e: React.ClipboardEvent) => {
      const files = extractClipboardFiles(e);
      if (files.length > 0) {
        e.preventDefault();
        void stageFilesForComposer(files, 'chat');
      }
    },
    [stageFilesForComposer]
  );

  const handleNewSessionPaste = useCallback(
    (e: React.ClipboardEvent) => {
      const files = extractClipboardFiles(e);
      if (files.length > 0) {
        e.preventDefault();
        void stageFilesForComposer(files, 'newSession');
      }
    },
    [stageFilesForComposer]
  );

  const handleCreateSession = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newSessionTitle.trim()) return;

    const targetPersona = activeTab === 'personas' ? (personas.find((p) => p.id === selectedPersonaId) || activeCreationPersona) : null;
    const targetAgent = selectedAgentId || activeCreationAgent?.id || agents[0]?.id;
    if (!targetPersona && !targetAgent) return;

    const pendingNewFiles = newSessionAttachments.map((a) => a.file);
    const hasInitialFiles = pendingNewFiles.length > 0;
    const rawPrompt = newSessionPrompt.trim();

    setIsActionPending(true);
    try {
      const options = {
        workingDirectory: newSessionWorkDir.trim() || undefined,
        model: newSessionModel.trim() || undefined,
        effort: newSessionEffort.trim() || undefined,
        mode: newSessionMode.trim() || undefined,
        tier: newSessionTier.trim() || undefined,
        contextSize: newSessionContextSize.trim() || undefined
      };
      const created = targetPersona && api.createPersonaSession
        ? await api.createPersonaSession(
            targetPersona.id,
            newSessionTitle.trim(),
            hasInitialFiles ? undefined : (rawPrompt || undefined),
            options
          )
        : await api.createAgentSession(
            targetAgent!,
            newSessionTitle.trim(),
            hasInitialFiles ? undefined : (rawPrompt || undefined),
            options
          );

      if (hasInitialFiles) {
        let uploadedDtos: SessionAttachment[] = [];
        if (typeof api.uploadSessionAttachments === 'function') {
          uploadedDtos = await api.uploadSessionAttachments(created.id, pendingNewFiles);
        }
        const effectivePrompt = rawPrompt || DEFAULT_ATTACHMENT_PROMPT;
        await api.postSessionMessage(
          created.id,
          effectivePrompt,
          created.creatorAgentId || targetAgent || 'agy-core',
          userEmail,
          'prompt',
          uploadedDtos
        );
      }

      setNewSessionTitle('');
      setNewSessionPrompt('');
      setNewSessionWorkDir('');
      setNewSessionModel('');
      setNewSessionEffort('');
      setNewSessionMode('');
      setNewSessionTier('');
      setNewSessionContextSize('');
      setNewSessionAttachments([]);
      setIsCreatingSession(false);
      await fetchAllData();
      setSelectedSessionId(created.id);
      await fetchSessionEvents(created.id);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to create session');
    } finally {
      setIsActionPending(false);
    }
  };

  const handleUpdateSessionControl = async (params: {
    model?: string;
    effort?: string;
    tier?: string;
    contextSize?: string;
    mode?: string;
    workingDirectory?: string;
  }) => {
    if (!selectedSessionId) return;
    setIsActionPending(true);
    try {
      await api.updateSessionControls(selectedSessionId, params);
      setEditingSessionControl(null);
      await fetchAllData();
      await fetchSessionEvents(selectedSessionId);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to update session controls');
    } finally {
      setIsActionPending(false);
    }
  };

  const emitSystemMessage = async (content: string) => {
    const msgId = `sys-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
    const sysMsg: SessionMessage = {
      id: msgId,
      sessionId: selectedSessionId || '',
      senderAgentId: 'system',
      eventType: 'system',
      content,
      createdAt: new Date().toISOString()
    };
    setEvents((prev) => [...prev, sysMsg]);
    if (selectedSessionId) {
      try {
        await api.postSessionMessage(selectedSessionId, content, 'system', 'system', 'system');
      } catch {
        // Optimistic event was already added
      }
    }
  };

  const handleExecuteMeshCommand = async (commandText: string): Promise<boolean> => {
    const trimmed = commandText.trim();
    const parts = trimmed.split(/\s+/);
    const root = parts[0]?.toLowerCase();
    const isMesh = root === '/mesh' || root === '\\mesh';

    if (!isMesh) {
      return false;
    }

    const subCmd = parts[1]?.toLowerCase();
    const arg = parts.slice(2).join(' ').trim();

    if (!subCmd || subCmd === 'help') {
      await emitSystemMessage(
        '💡 **AgentMesh Command Reference**\n\n' +
        '• **`/mesh stop`**: Halt active model execution immediately (<500ms)\n' +
        '• **`/mesh mode <plan|auto|accept-edits>`**: Switch session execution mode\n' +
        '• **`/mesh model <model-id>`**: Set active model (e.g. gemini-3.8-flash)\n' +
        '• **`/mesh effort <low|medium|high|max>`**: Set reasoning effort\n' +
        '• **`/mesh artifacts`**: Toggle side-by-side Artifacts panel\n' +
        '• **`/mesh queue <prompt>`**: Enqueue prompt sequentially\n' +
        '• **`/mesh clear`**: Clear event timeline filters\n\n' +
        '*Agent Workflows*: `/plan`, `/grill-me`, `/goal`, `/compact`, `/browser`, `/schedule`, `/skills`, `/help`'
      );
      return true;
    }

    if (subCmd === 'stop') {
      await handleStopSession();
      await emitSystemMessage('🛑 Stop signal dispatched for active model execution.');
      return true;
    }

    if (subCmd === 'artifacts') {
      setShowArtifactsPanel((prev) => !prev);
      await emitSystemMessage(`Side-by-side Artifacts panel ${!showArtifactsPanel ? 'opened' : 'closed'}.`);
      return true;
    }

    if (subCmd === 'clear') {
      setEvents([]);
      return true;
    }

    if (subCmd === 'effort') {
      if (arg) {
        const eff = arg.toLowerCase();
        if (['low', 'medium', 'high', 'max'].includes(eff)) {
          await handleUpdateSessionControl({ effort: eff });
          await emitSystemMessage(`✓ Reasoning effort set to "${eff}" for active session.`);
        } else {
          await emitSystemMessage(`⚠️ Invalid effort level "${arg}". Supported levels: low, medium, high, max.`);
        }
      } else {
        const currentEffort = selectedSessionMetadata?.effort || 'medium';
        await emitSystemMessage(
          `⚙️ **Reasoning Effort Configuration**\n\n` +
          `• Current Level: **\`${currentEffort}\`**\n` +
          `• Supported Levels: \`low\` | \`medium\` | \`high\` | \`max\`\n` +
          `• Usage: \`/mesh effort <level>\` (e.g. \`/mesh effort high\`)`
        );
      }
      return true;
    }

    if (subCmd === 'model') {
      if (arg) {
        await handleUpdateSessionControl({ model: arg });
        await emitSystemMessage(`✓ Model updated to "${arg}" for active session.`);
      } else {
        const currentModel = selectedSessionMetadata?.model || 'default';
        const modelList = availableModels.length > 0
          ? availableModels.map((m) => `  - \`${m.id}\`${m.name ? ` (${m.name})` : ''}`).join('\n')
          : '  - Default system model';
        await emitSystemMessage(
          `⚙️ **Model Configuration**\n\n` +
          `• Current Model: **\`${currentModel}\`**\n` +
          `• Available Models:\n${modelList}\n\n` +
          `• Usage: \`/mesh model <model-id>\` (e.g. \`/mesh model gemini-3.8-flash\`)`
        );
      }
      return true;
    }

    if (subCmd === 'mode') {
      if (arg) {
        const m = arg.toLowerCase();
        if (['plan', 'auto', 'accept-edits'].includes(m)) {
          await handleUpdateSessionControl({ mode: m });
          await emitSystemMessage(`✓ Execution mode set to "${m}" for active session.`);
        } else {
          await emitSystemMessage(`⚠️ Invalid mode "${arg}". Supported modes: plan, auto, accept-edits.`);
        }
      } else {
        const currentMode = selectedSessionMetadata?.mode || 'auto';
        await emitSystemMessage(
          `⚙️ **Execution Mode Configuration**\n\n` +
          `• Current Mode: **\`${currentMode}\`**\n` +
          `• Supported Modes: \`plan\` | \`auto\` | \`accept-edits\`\n` +
          `• Usage: \`/mesh mode <mode>\` (e.g. \`/mesh mode plan\`)`
        );
      }
      return true;
    }

    if (subCmd === 'context') {
      if (arg) {
        const c = arg.toLowerCase();
        await handleUpdateSessionControl({ contextSize: c });
        await emitSystemMessage(`✓ Context size set to "${c}" for active session.`);
      } else {
        const currentContext = selectedSessionMetadata?.contextSize || 'default';
        const ctxList = sessionAvailableContextSizes.length > 0
          ? sessionAvailableContextSizes.map((c) => `  - \`${c.id}\`${c.label ? ` (${c.label})` : ''}`).join('\n')
          : '  - Default driver/model context window';
        await emitSystemMessage(
          `⚙️ **Context Window Configuration**\n\n` +
          `• Current Context Size: **\`${currentContext}\`**\n` +
          `• Available Context Sizes:\n${ctxList}\n\n` +
          `• Usage: \`/mesh context <size>\` (e.g. \`/mesh context 200k\`)`
        );
      }
      return true;
    }

    if (subCmd === 'queue') {
      if (arg && selectedSessionId) {
        const res = await api.enqueuePrompt(selectedSessionId, {
          prompt: arg,
          senderAgentId: userEmail,
          sendNow: false
        });
        setActiveQueue(res.queue || []);
        await fetchAllData();
        await emitSystemMessage(`✓ Prompt enqueued sequentially (${(res.queue || []).length} prompt(s) in queue).`);
      } else {
        await emitSystemMessage(
          `📋 **Prompt Queue Status**\n\n` +
          `• Pending in Queue: **${activeQueue.length}** prompt(s)\n` +
          `• Usage: \`/mesh queue <prompt>\` to enqueue a prompt sequentially.`
        );
      }
      return true;
    }

    // Any unrecognized /mesh or \mesh subcommand
    await emitSystemMessage(`⚠️ Unknown mesh subcommand: "${subCmd}". Type \`/mesh help\` for available commands.`);
    return true;
  };

  const handleSelectSlashCommand = (cmd: SlashCommandItem) => {
    setSlashPaletteOpen(false);
    if (cmd.executeImmediately) {
      handleExecuteMeshCommand(cmd.command);
      setPromptInput('');
    } else {
      setPromptInput(cmd.insertText || `${cmd.command} `);
    }
  };

  const handlePromptChange = (val: string) => {
    setPromptInput(val);
    if (val.startsWith('/') || val.startsWith('\\')) {
      setSlashPaletteOpen(true);
      setSlashPaletteIndex(0);
    } else {
      setSlashPaletteOpen(false);
    }
  };

  const handlePromptKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (slashPaletteOpen) {
      const cleanQuery = (promptInput.startsWith('/') || promptInput.startsWith('\\'))
        ? promptInput.slice(1).trim().toLowerCase()
        : promptInput.trim().toLowerCase();
      const filtered = SLASH_COMMANDS.filter((cmd) => {
        if (!cleanQuery) return true;
        const cmdBody = cmd.command.slice(1).toLowerCase();
        return (
          cmdBody.includes(cleanQuery) ||
          cmd.label.toLowerCase().includes(cleanQuery) ||
          cmd.description.toLowerCase().includes(cleanQuery)
        );
      });

      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setSlashPaletteIndex((prev) => (filtered.length > 0 ? (prev + 1) % filtered.length : 0));
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        setSlashPaletteIndex((prev) => (filtered.length > 0 ? (prev - 1 + filtered.length) % filtered.length : 0));
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        setSlashPaletteOpen(false);
        return;
      }
      if ((e.key === 'Enter' || e.key === 'Tab') && filtered.length > 0) {
        const selected = filtered[slashPaletteIndex] || filtered[0];
        if (selected && selected.command !== promptInput.trim()) {
          e.preventDefault();
          handleSelectSlashCommand(selected);
          return;
        }
      }
    }
  };

  const readyStagedAttachments = useMemo(
    () => stagedAttachments.filter((a) => !a.uploading && !a.error),
    [stagedAttachments]
  );

  const canSendPrompt = Boolean(promptInput.trim() || readyStagedAttachments.length > 0);

  const handleSendPrompt = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedSessionId || !canSendPrompt) return;

    const text = promptInput.trim();
    const attachmentsToSend: SessionAttachment[] = readyStagedAttachments.map(
      (item) =>
        item.uploaded || {
          id: item.localId,
          sessionId: selectedSessionId,
          fileName: item.name,
          contentType: item.contentType,
          sizeBytes: item.sizeBytes,
          s3Key: `sessions/${selectedSessionId}/attachments/${item.localId}/${item.name}`,
          downloadUrl: item.previewUrl || `/api/sessions/${selectedSessionId}/attachments/${item.localId}/content`,
          createdAt: new Date().toISOString()
        }
    );
    const effectiveText = text || (attachmentsToSend.length > 0 ? DEFAULT_ATTACHMENT_PROMPT : '');

    setPromptInput('');
    setStagedAttachments([]);
    setSlashPaletteOpen(false);
    setShowSendNowDropdown(false);

    if (effectiveText.startsWith('/mesh') || effectiveText.startsWith('\\mesh')) {
      await handleExecuteMeshCommand(effectiveText);
      return;
    }

    const currentSession = sessions.find((s) => s.id === selectedSessionId);
    const isThinking = currentSession?.status === 'thinking';

    try {
      if (isThinking) {
        // Model is busy: queue the message sequentially by default
        const res = await api.enqueuePrompt(selectedSessionId, {
          prompt: effectiveText,
          senderAgentId: userEmail,
          sendNow: false,
          ...(attachmentsToSend.length > 0 ? { attachments: attachmentsToSend } : {})
        });
        setActiveQueue(res.queue || []);
        await fetchAllData();
      } else {
        // Model is idle: dispatch prompt immediately
        const createdMsg =
          attachmentsToSend.length > 0
            ? await api.postSessionMessage(
                selectedSessionId,
                effectiveText,
                currentSession?.creatorAgentId || selectedAgentId || 'agy-core',
                userEmail,
                'prompt',
                attachmentsToSend
              )
            : await api.postSessionMessage(
                selectedSessionId,
                effectiveText,
                currentSession?.creatorAgentId || selectedAgentId || 'agy-core',
                userEmail,
                'prompt'
              );
        setEvents((prev) => (prev.some((m) => m.id === createdMsg.id) ? prev : [...prev, createdMsg]));
        await fetchAllData();
      }
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to send prompt');
    }
  };

  const handleSendPromptNow = async () => {
    if (!selectedSessionId || !canSendPrompt) return;

    const text = promptInput.trim();
    const attachmentsToSend: SessionAttachment[] = readyStagedAttachments.map(
      (item) =>
        item.uploaded || {
          id: item.localId,
          sessionId: selectedSessionId,
          fileName: item.name,
          contentType: item.contentType,
          sizeBytes: item.sizeBytes,
          s3Key: `sessions/${selectedSessionId}/attachments/${item.localId}/${item.name}`,
          downloadUrl: item.previewUrl || `/api/sessions/${selectedSessionId}/attachments/${item.localId}/content`,
          createdAt: new Date().toISOString()
        }
    );
    const effectiveText = text || (attachmentsToSend.length > 0 ? DEFAULT_ATTACHMENT_PROMPT : '');

    setPromptInput('');
    setStagedAttachments([]);
    setShowSendNowDropdown(false);

    if (effectiveText.startsWith('/mesh') || effectiveText.startsWith('\\mesh')) {
      await handleExecuteMeshCommand(effectiveText);
      return;
    }

    try {
      const res = await api.enqueuePrompt(selectedSessionId, {
        prompt: effectiveText,
        senderAgentId: userEmail,
        sendNow: true,
        ...(attachmentsToSend.length > 0 ? { attachments: attachmentsToSend } : {})
      });
      setActiveQueue(res.queue || []);
      await fetchAllData();
      await fetchSessionEvents(selectedSessionId);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to send prompt immediately');
    }
  };

  const handleStopSession = async (sessionId?: string | React.MouseEvent) => {
    const targetSessionId = typeof sessionId === 'string' && sessionId ? sessionId : selectedSessionId;
    if (!targetSessionId) return;
    setIsActionPending(true);
    try {
      await api.stopSession(targetSessionId);
      await fetchAllData();
      if (selectedSessionId) {
        await fetchSessionEvents(selectedSessionId);
      }
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to stop model');
    } finally {
      setIsActionPending(false);
    }
  };

  const handleStopCommand = async (commandId: string) => {
    if (!selectedSessionId) return;
    setIsActionPending(true);
    try {
      await api.stopCommand(selectedSessionId, commandId);
      await fetchSessionEvents(selectedSessionId);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to stop command');
    } finally {
      setIsActionPending(false);
    }
  };

  const handleRemoveFromQueue = async (queueItemId: string) => {
    if (!selectedSessionId) return;
    try {
      const updatedQueue = await api.dequeuePrompt(selectedSessionId, queueItemId);
      setActiveQueue(updatedQueue || []);
      await fetchAllData();
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to remove from queue');
    }
  };

  const handleSendQueuedNow = async (queueItemId: string) => {
    if (!selectedSessionId) return;
    try {
      const res = await api.sendQueuedPromptNow(selectedSessionId, queueItemId);
      setActiveQueue(res.queue || []);
      await fetchAllData();
      await fetchSessionEvents(selectedSessionId);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to send queued prompt now');
    }
  };

  const handleSubmitAnswers = async (toolCallId: string, parsedQuestions: AskQuestionItem[]) => {
    if (!selectedSessionId) return;
    setSubmittingQuestionId(toolCallId);
    try {
      const curAnswers = questionAnswers[toolCallId] || { selectedOptions: {}, customTexts: {} };
      const answers: QuestionAnswerPayload[] = parsedQuestions.map((q, idx) => {
        const selected = curAnswers.selectedOptions[idx] || [];
        const custom = (curAnswers.customTexts[idx] || '').trim();
        if (!q.is_multi_select) {
          if (custom.length > 0) {
            return {
              questionIndex: idx,
              question: q.question,
              selectedOptions: [],
              customText: custom
            };
          }
          return {
            questionIndex: idx,
            question: q.question,
            selectedOptions: selected.slice(0, 1),
            customText: ''
          };
        }
        return {
          questionIndex: idx,
          question: q.question,
          selectedOptions: selected,
          customText: custom
        };
      });

      await api.submitQuestionAnswer(selectedSessionId, {
        toolCallId,
        answers
      });

      setQuestionAnswers((prev) => {
        const next = { ...prev };
        delete next[toolCallId];
        return next;
      });

      await fetchAllData();
      if (selectedSessionId) {
        await fetchSessionEvents(selectedSessionId);
      }
    } catch (err) {
      console.error('Failed to submit question answer:', err);
      setApiError(err instanceof Error ? err.message : 'Failed to submit answer');
    } finally {
      setSubmittingQuestionId(null);
    }
  };

  const handleTestOpenAi = async () => {
    if (!newAgentEndpoint.trim()) return;
    setIsTestingOpenAi(true);
    setOpenAiTestResult(null);
    try {
      const res = await api.testOpenAiServer({
        endpoint: newAgentEndpoint.trim(),
        apiKey: newAgentApiKey.trim() || undefined
      });
      if (res.success) {
        setOpenAiTestResult({
          success: true,
          message: `Connected successfully! Found ${res.models.length} model(s).`,
          models: res.models
        });
        if (res.models.length > 0 && !newAgentModel) {
          setNewAgentModel(res.models[0].id);
        }
      } else {
        setOpenAiTestResult({
          success: false,
          message: res.error || 'Failed to connect to OpenAI-compatible server.',
          models: []
        });
      }
    } catch (err) {
      setOpenAiTestResult({
        success: false,
        message: err instanceof Error ? err.message : 'Connection failed.',
        models: []
      });
    } finally {
      setIsTestingOpenAi(false);
    }
  };

  const handleSaveDriverConfig = async (
    agentId: string,
    patch: Parameters<typeof api.updateDriverConfig>[1]
  ) => {
    setIsSavingDriverConfig(true);
    try {
      const config = await api.updateDriverConfig(agentId, patch);
      // Reflect immediately so the row and session pickers show the new limits.
      setAgents((prev) => prev.map((a) => (a.id === agentId ? { ...a, config } : a)));
      setConfigAgent(null);
    } finally {
      setIsSavingDriverConfig(false);
    }
  };

  const handleRegisterAgent = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newAgentId.trim() || !newAgentName.trim()) return;

    setIsActionPending(true);
    try {
      let capabilitiesJson: string | undefined = undefined;
      if (newAgentType === 'openai') {
        const caps = {
          adapter: 'openai',
          endpoint: newAgentEndpoint.trim(),
          apiKey: newAgentApiKey.trim() || undefined,
          defaultModel: newAgentModel.trim() || undefined,
          models: openAiTestResult?.models || (newAgentModel.trim() ? [{ id: newAgentModel.trim(), name: newAgentModel.trim() }] : [])
        };
        capabilitiesJson = JSON.stringify(caps);
      }

      const created = await api.upsertAgent({
        id: newAgentId.trim().toLowerCase(),
        name: newAgentName.trim(),
        role: newAgentRole.trim() || 'Autonomous Agent',
        status: 'offline',
        capabilitiesJson
      });
      setNewAgentId('');
      setNewAgentName('');
      setNewAgentModel('');
      setOpenAiTestResult(null);
      setShowRegisterModal(false);
      await fetchAllData();
      setSelectedAgentId(created.id);
    } catch (err) {
      setApiError(err instanceof Error ? err.message : 'Failed to register agent');
    } finally {
      setIsActionPending(false);
    }
  };

  const selectedSessionMetadata = useMemo(() => {
    if (!selectedSession?.metadataJson) return null;
    try {
      return JSON.parse(selectedSession.metadataJson) as Record<string, any>;
    } catch {
      return null;
    }
  }, [selectedSession?.metadataJson]);

  useEffect(() => {
    const rawQueue = selectedSessionMetadata?.promptQueue || (selectedSessionMetadata as any)?.PromptQueue;
    const q = Array.isArray(rawQueue)
      ? (rawQueue as any[]).map((item: any) => ({
          id: item.id || item.Id || '',
          prompt: item.prompt || item.Prompt || '',
          sender: item.sender || item.Sender || '',
          createdAt: item.createdAt || item.CreatedAt || '',
          model: item.model || item.Model || null,
          effort: item.effort || item.Effort || null,
          tier: item.tier || item.Tier || null,
          mode: item.mode || item.Mode || null,
          workingDirectory: item.workingDirectory || item.WorkingDirectory || null,
          attachments: Array.isArray(item.attachments || item.Attachments)
            ? (item.attachments || item.Attachments).map((att: any) => ({
                id: att.id || att.Id || '',
                sessionId: att.sessionId || att.SessionId || selectedSessionId,
                fileName: att.fileName || att.FileName || att.name || att.Name || '',
                contentType: att.contentType || att.ContentType || '',
                sizeBytes: typeof att.sizeBytes === 'number'
                  ? att.sizeBytes
                  : typeof att.SizeBytes === 'number'
                  ? att.SizeBytes
                  : 0,
                s3Key: att.s3Key || att.S3Key || '',
                downloadUrl: att.downloadUrl || att.DownloadUrl || (att.id || att.Id ? `/api/sessions/${selectedSessionId}/attachments/${encodeURIComponent(att.id || att.Id)}/content` : ''),
                createdAt: att.createdAt || att.CreatedAt || ''
              }))
            : null
        }))
      : [];
    setActiveQueue(q);
  }, [selectedSessionMetadata?.promptQueue, (selectedSessionMetadata as any)?.PromptQueue, selectedSessionId]);

  const activeCommand = useMemo(() => {
    if (selectedSession?.status !== 'thinking') return null;
    for (let i = events.length - 1; i >= 0; i--) {
      const ev = events[i];
      if (ev.eventType === 'tool_result' || ev.eventType === 'response') {
        return null;
      }
      if (ev.eventType === 'tool_call') {
        let commandText = ev.content || ev.toolName || 'command';
        if (ev.toolArgsJson) {
          try {
            const parsed = JSON.parse(ev.toolArgsJson);
            if (parsed.CommandLine) commandText = parsed.CommandLine;
            else if (parsed.command) commandText = parsed.command;
          } catch {}
        }
        return {
          id: ev.id,
          name: ev.toolName || 'command',
          commandText,
          createdAt: ev.createdAt
        };
      }
    }
    return null;
  }, [selectedSession?.status, events]);

  const sessionModelCap = useMemo(() => {
    if (!selectedSessionMetadata?.model || !currentRuntimeCaps) return null;
    return currentRuntimeCaps.models.find((m) => m.id === selectedSessionMetadata.model) || null;
  }, [currentRuntimeCaps, selectedSessionMetadata?.model]);

  const sessionAvailableEfforts = useMemo(() => {
    if (sessionModelCap && sessionModelCap.efforts.length > 0) return sessionModelCap.efforts;
    const allEfforts = Array.from(new Set(currentRuntimeCaps?.models.flatMap((m) => m.efforts) || []));
    return allEfforts.length > 0 ? allEfforts : ['low', 'medium', 'high'];
  }, [sessionModelCap, currentRuntimeCaps]);

  const sessionAvailableContextSizes = useMemo(() => {
    if (!currentRuntimeCaps) return [];
    const modelContextIds = sessionModelCap?.contextSizes;
    if (modelContextIds && modelContextIds.length > 0) {
      const driverContexts = currentRuntimeCaps.contextSizes || [];
      return modelContextIds.map((id) => {
        const match = driverContexts.find((c) => c.id.toLowerCase() === id.toLowerCase());
        return match || { id, label: id, description: `${id} context window` };
      });
    }
    return currentRuntimeCaps.contextSizes || [];
  }, [sessionModelCap, currentRuntimeCaps]);

  return (
    <div className="min-h-screen h-screen bg-slate-950 text-slate-100 flex flex-col font-sans overflow-hidden">
      {/* Top Bar */}
      <header className="h-14 border-b border-slate-800/80 bg-slate-900/70 backdrop-blur px-5 flex items-center justify-between shrink-0">
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
            <Cpu className="w-4 h-4" />
          </div>
          <div className="flex items-center gap-2.5">
            <h1 className="text-base font-bold tracking-tight text-white">AgentMesh</h1>
            <span className="text-[11px] font-mono px-2 py-0.5 rounded bg-slate-800 text-slate-400 border border-slate-700">
              Session Cockpit MVP
            </span>
          </div>

          {/* System Health Pill */}
          <div className="hidden md:flex items-center gap-2 ml-3 pl-3 border-l border-slate-800 text-xs">
            <span
              className={`w-2 h-2 rounded-full ${
                apiError
                  ? 'bg-rose-500'
                  : statusData?.status === 'Healthy'
                  ? 'bg-emerald-400'
                  : 'bg-amber-400'
              }`}
            />
            <span className="text-slate-400">
              {apiError ? 'API Offline' : statusData?.status === 'Healthy' ? 'Mesh Healthy' : 'Connecting...'}
            </span>
            {signalRConnected && (
              <span className="text-[10px] font-mono text-emerald-400 bg-emerald-950/60 border border-emerald-500/30 px-1.5 py-0.5 rounded">
                LIVE
              </span>
            )}
          </div>

          {/* Top-level Navigation Tabs: Projects vs Personas vs Drivers */}
          <div className="flex items-center gap-1 ml-4 bg-slate-950/80 border border-slate-800 rounded-xl p-1">
            <button
              type="button"
              onClick={() => {
                hasUserSwitchedTabRef.current = true;
                setActiveTab('projects');
              }}
              className={`px-3 py-1 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition cursor-pointer ${
                activeTab === 'projects'
                  ? 'bg-sky-600/25 text-sky-300 border border-sky-500/40 shadow-sm'
                  : 'text-slate-400 hover:text-slate-200 border border-transparent'
              }`}
            >
              <FolderGit2 className="w-3.5 h-3.5" />
              <span>Projects & Jobs</span>
              <span className="text-[10px] font-mono px-1.5 py-0.2 rounded-full bg-slate-800 text-slate-300">
                {projects.length}
              </span>
            </button>

            <button
              type="button"
              onClick={() => {
                hasUserSwitchedTabRef.current = true;
                setActiveTab('personas');
              }}
              className={`px-3 py-1 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition cursor-pointer ${
                activeTab === 'personas'
                  ? 'bg-sky-600/25 text-sky-300 border border-sky-500/40 shadow-sm'
                  : 'text-slate-400 hover:text-slate-200 border border-transparent'
              }`}
            >
              <Users className="w-3.5 h-3.5" />
              <span>Personas & Sessions</span>
              <span className="text-[10px] font-mono px-1.5 py-0.2 rounded-full bg-slate-800 text-slate-300">
                {personas.length}
              </span>
            </button>

            <button
              type="button"
              onClick={() => {
                hasUserSwitchedTabRef.current = true;
                setActiveTab('drivers');
              }}
              className={`px-3 py-1 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition cursor-pointer ${
                activeTab === 'drivers'
                  ? 'bg-sky-600/25 text-sky-300 border border-sky-500/40 shadow-sm'
                  : 'text-slate-400 hover:text-slate-200 border border-transparent'
              }`}
            >
              <Cpu className="w-3.5 h-3.5" />
              <span>Drivers & Infrastructure</span>
              <span className="text-[10px] font-mono px-1.5 py-0.2 rounded-full bg-slate-800 text-slate-300">
                {agents.length}
              </span>
            </button>
          </div>
        </div>

        {/* Developer Controls & Auth */}
        <div className="flex items-center gap-2.5">
          <button
            onClick={handleResetMesh}
            disabled={isActionPending}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium bg-rose-950/40 hover:bg-rose-900/50 text-rose-300 border border-rose-500/30 transition disabled:opacity-50 cursor-pointer"
            title="Purge all agents, sessions, and events from PostgreSQL"
          >
            <Trash2 className="w-3.5 h-3.5 text-rose-400" />
            <span>Clear Mesh Data</span>
          </button>

          <div className="h-5 w-px bg-slate-800 mx-1" />

          <div className="flex items-center gap-2 bg-slate-900 border border-slate-800 rounded-lg px-2.5 py-1 text-xs">
            <User className="w-3.5 h-3.5 text-sky-400" />
            <span className="font-mono text-slate-300">{userEmail}</span>
            {auth?.signoutRedirect && (
              <button
                onClick={() => auth.signoutRedirect()}
                className="text-slate-500 hover:text-rose-400 ml-1 transition cursor-pointer"
                title="Sign out"
              >
                <LogOut className="w-3.5 h-3.5" />
              </button>
            )}
          </div>
        </div>
      </header>

      {/* API Error Banner */}
      {apiError && (
        <div className="bg-rose-950/80 border-b border-rose-500/40 px-5 py-2.5 flex items-center justify-between text-xs text-rose-200 shrink-0">
          <div className="flex items-center gap-2">
            <AlertTriangle className="w-4 h-4 text-rose-400 shrink-0" />
            <span className="font-semibold">API Gateway Unreachable:</span>
            <span className="font-mono">{apiError}</span>
          </div>
          <button
            onClick={() => {
              setApiError(null);
              fetchAllData();
            }}
            className="px-2.5 py-1 rounded bg-rose-900/60 hover:bg-rose-800 text-rose-100 font-medium transition cursor-pointer"
          >
            Retry
          </button>
        </div>
      )}

      {/* Main Split View */}
      <div className="flex-1 flex overflow-hidden">
        {/* Left Sidebar: Projects, Personas or Drivers & Their Sessions */}
        <aside className="w-80 border-r border-slate-800/80 bg-slate-900/40 flex flex-col shrink-0">
          {activeTab === 'projects' ? (
            <ProjectList
              projects={projects}
              selectedProjectId={selectedProjectId}
              selectedJobId={selectedJobId}
              jobsByProject={jobsByProject}
              onSelectProject={handleSelectProject}
              onSelectJob={handleSelectJob}
              onOpenNewProjectModal={() => {
                setEditingProject(null);
                setShowProjectModal(true);
              }}
              onOpenNewJobModal={(proj) => {
                setJobModalProject(proj);
                setShowJobModal(true);
              }}
              onEditProject={(proj) => {
                setEditingProject(proj);
                setShowProjectModal(true);
              }}
              onDeleteProject={handleDeleteProject}
              onDeleteJob={handleDeleteJob}
              onArchiveJob={handleArchiveJob}
            />
          ) : activeTab === 'personas' ? (
            <>
              <div className="p-3.5 border-b border-slate-800/80 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Users className="w-4 h-4 text-sky-400" />
                  <span className="text-xs font-bold uppercase tracking-wider text-slate-400">
                    Personas ({personas.length})
                  </span>
                </div>
                <button
                  type="button"
                  onClick={() => openCreatePersonaModal()}
                  className="text-xs flex items-center gap-1 text-sky-400 hover:text-sky-300 font-medium px-2 py-1 rounded hover:bg-slate-800/60 transition cursor-pointer"
                >
                  <Plus className="w-3.5 h-3.5" />
                  <span>New Persona</span>
                </button>
              </div>

              <div className="flex-1 overflow-y-auto p-3 space-y-4">
                {isLoading ? (
                  <div className="py-12 flex flex-col items-center justify-center text-slate-500 text-xs gap-2">
                    <RefreshCw className="w-5 h-5 animate-spin text-sky-400" />
                    <span>Loading personas...</span>
                  </div>
                ) : personas.length === 0 ? (
                  <div className="p-5 rounded-xl border border-dashed border-slate-800 bg-slate-900/30 text-center space-y-3 my-4">
                    <Users className="w-8 h-8 text-sky-400/70 mx-auto" />
                    <div>
                      <h3 className="text-xs font-semibold text-slate-200">Create Your First Persona</h3>
                      <p className="text-[11px] text-slate-400 mt-1 leading-relaxed">
                        Personas define specialized roles, system instructions, a target Driver, and a default model for your sessions.
                      </p>
                      {agents.length === 0 && (
                        <p className="text-[10px] text-slate-500 mt-1">
                          No Agents Connected
                        </p>
                      )}
                    </div>
                    <button
                      type="button"
                      onClick={() => openCreatePersonaModal()}
                      disabled={isActionPending}
                      className="w-full py-2 px-3 rounded-lg bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center justify-center gap-1.5 transition cursor-pointer"
                    >
                      <Plus className="w-3.5 h-3.5" />
                      <span>Create Persona</span>
                    </button>
                  </div>
                ) : (
                  personas.map((persona) => {
                    const personaSessions = sessions
                      .filter((s) => s.personaId === persona.id)
                      .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
                    const isPersonaSelected = selectedPersona?.id === persona.id;
                    const linkedDriver = agents.find((a) => a.id === persona.driverId);
                    const driverStatus = linkedDriver?.status || persona.driverStatus || 'offline';
                    const isDriverOffline = driverStatus === 'offline';
                    const isDriverBusy =
                      !isDriverOffline &&
                      (driverStatus === 'busy' || personaSessions.some((s) => s.status === 'thinking'));

                    return (
                      <div
                        key={persona.id}
                        className={`rounded-xl border transition ${
                          isPersonaSelected
                            ? 'border-slate-700 bg-slate-900/80'
                            : 'border-slate-800/70 bg-slate-900/30 hover:border-slate-800'
                        }`}
                      >
                        {/* Persona Header */}
                        <div
                          onClick={() => {
                            setSelectedPersonaId(persona.id);
                            setSelectedAgentId(persona.driverId);
                            if (personaSessions.length > 0 && !personaSessions.some((s) => s.id === selectedSessionId)) {
                              setSelectedSessionId(personaSessions[0].id);
                            }
                          }}
                          className="p-3 flex items-start justify-between cursor-pointer"
                        >
                          <div className="flex items-start gap-2.5 min-w-0">
                            <span
                              className={`w-2.5 h-2.5 rounded-full mt-1 shrink-0 ${
                                isDriverOffline
                                  ? 'bg-slate-600'
                                  : isDriverBusy
                                  ? 'bg-amber-400 animate-pulse'
                                  : 'bg-emerald-400'
                              }`}
                              title={`Driver @${persona.driverId} is ${driverStatus}`}
                            />
                            <div className="min-w-0">
                              <div className="flex items-center gap-1.5">
                                <span className="text-xs font-bold text-white truncate">{persona.name}</span>
                              </div>
                              <div className="text-[11px] font-mono text-slate-400 truncate">
                                @{persona.id} · {persona.role}
                              </div>
                              <div className="flex flex-wrap items-center gap-1.5 mt-1.5">
                                <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-slate-950/80 border border-slate-800 text-[10px] font-mono text-sky-300">
                                  <Cpu className="w-2.5 h-2.5 text-sky-400" />
                                  <span>@{persona.driverId}</span>
                                </span>
                                {persona.defaultModel && (
                                  <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-purple-950/40 border border-purple-500/30 text-[10px] font-mono text-purple-300 truncate max-w-[140px]">
                                    <span>{persona.defaultModel}</span>
                                  </span>
                                )}
                                {persona.defaultContextSize && (
                                  <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-emerald-950/40 border border-emerald-500/30 text-[10px] font-mono text-emerald-300">
                                    <Maximize2 className="w-2.5 h-2.5 text-emerald-400" />
                                    <span>{persona.defaultContextSize}</span>
                                  </span>
                                )}
                                {persona.maxConcurrentJobs && (
                                  <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-amber-950/40 border border-amber-500/30 text-[10px] font-mono text-amber-300">
                                    <span>Max {persona.maxConcurrentJobs} {persona.maxConcurrentJobs === 1 ? 'Job' : 'Jobs'}</span>
                                  </span>
                                )}
                              </div>
                            </div>
                          </div>

                          <div className="flex items-center gap-1 shrink-0">
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                openEditPersonaModal(persona);
                              }}
                              className="p-1 rounded-md text-slate-400 hover:text-slate-200 hover:bg-slate-800 transition cursor-pointer"
                              title={`Edit ${persona.name}`}
                            >
                              <Edit3 className="w-3 h-3" />
                            </button>
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                setDeletingPersonaId(persona.id);
                              }}
                              className="p-1 rounded-md text-slate-400 hover:text-rose-400 hover:bg-slate-800 transition cursor-pointer"
                              title={`Delete ${persona.name}`}
                            >
                              <Trash2 className="w-3 h-3" />
                            </button>
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                startNewSessionWithPersona(persona);
                              }}
                              className="p-1 rounded-md bg-slate-800 hover:bg-sky-600 text-slate-300 hover:text-white transition shrink-0 cursor-pointer"
                              title={`New Session with ${persona.name}`}
                            >
                              <Plus className="w-3.5 h-3.5" />
                            </button>
                          </div>
                        </div>

                        {/* Delete Confirmation Inline Banner */}
                        {deletingPersonaId === persona.id && (
                          <div className="mx-2 mb-2 p-2.5 rounded-lg bg-rose-950/60 border border-rose-500/40 text-[11px] text-rose-200 space-y-2">
                            <p>
                              Delete <strong>{persona.name}</strong> and its {personaSessions.length} session(s)?
                            </p>
                            <div className="flex justify-end gap-1.5">
                              <button
                                type="button"
                                onClick={() => setDeletingPersonaId(null)}
                                className="px-2 py-0.5 rounded bg-slate-800 text-slate-300 hover:text-white cursor-pointer"
                              >
                                Cancel
                              </button>
                              <button
                                type="button"
                                onClick={() => handleDeletePersona(persona.id)}
                                className="px-2.5 py-0.5 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold cursor-pointer"
                              >
                                Confirm Delete
                              </button>
                            </div>
                          </div>
                        )}

                        {/* Persona's Sessions */}
                        <div className="px-2 pb-2 space-y-1">
                          {personaSessions.length === 0 ? (
                            <div className="px-2 py-1.5 text-[11px] text-slate-500 italic">
                              No sessions yet
                            </div>
                          ) : (
                            personaSessions.map((session) => {
                              const isSelected = session.id === selectedSessionId && !isCreatingSession;
                              const isThinking = session.status === 'thinking';

                              return (
                                <div key={session.id} className="space-y-1">
                                  <div
                                    onClick={() => {
                                      setSelectedPersonaId(persona.id);
                                      setSelectedAgentId(session.driverId || session.creatorAgentId || persona.driverId);
                                      setSelectedSessionId(session.id);
                                      setIsCreatingSession(false);
                                    }}
                                    className={`group w-full text-left px-2.5 py-2 rounded-lg text-xs flex items-center justify-between gap-2 transition cursor-pointer ${
                                      isSelected
                                        ? 'bg-sky-500/15 text-sky-300 border border-sky-500/30 font-medium'
                                        : 'text-slate-400 hover:bg-slate-800/50 hover:text-slate-200 border border-transparent'
                                    }`}
                                  >
                                    <div className="flex items-center gap-2 min-w-0 flex-1">
                                      {session.status === 'waiting_for_input' ? (
                                        <span className="w-2.5 h-2.5 rounded-full bg-amber-400 border border-amber-300 animate-ping shrink-0" title="Human input required" />
                                      ) : isThinking ? (
                                        <RefreshCw className="w-3 h-3 text-amber-400 animate-spin shrink-0" />
                                      ) : (
                                        <MessageSquare className="w-3 h-3 text-slate-500 shrink-0" />
                                      )}
                                      <span className="truncate">{session.title}</span>
                                    </div>

                                    <div className="flex items-center gap-1 shrink-0">
                                      <button
                                        type="button"
                                        onClick={(e) => {
                                          e.stopPropagation();
                                          setDeletingSessionId(session.id);
                                        }}
                                        className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-rose-500/20 text-slate-400 hover:text-rose-400 transition cursor-pointer"
                                        title={`Delete session ${session.title}`}
                                      >
                                        <Trash2 className="w-3 h-3" />
                                      </button>
                                      <ChevronRight className="w-3 h-3 opacity-50 shrink-0 group-hover:opacity-100" />
                                    </div>
                                  </div>

                                  {/* Delete Confirmation Inline Banner */}
                                  {deletingSessionId === session.id && (
                                    <div className="p-2 rounded-lg bg-rose-950/70 border border-rose-500/40 text-[11px] text-rose-200 space-y-1.5">
                                      <p className="truncate">Delete <strong>{session.title}</strong>?</p>
                                      <div className="flex justify-end gap-1.5">
                                        <button
                                          type="button"
                                          onClick={(e) => {
                                            e.stopPropagation();
                                            setDeletingSessionId(null);
                                          }}
                                          className="px-2 py-0.5 rounded bg-slate-800 text-slate-300 hover:text-white cursor-pointer"
                                        >
                                          Cancel
                                        </button>
                                        <button
                                          type="button"
                                          onClick={(e) => {
                                            e.stopPropagation();
                                            handleDeleteSession(session.id);
                                          }}
                                          className="px-2.5 py-0.5 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold cursor-pointer"
                                        >
                                          Confirm Delete
                                        </button>
                                      </div>
                                    </div>
                                  )}
                                </div>
                              );
                            })
                          )}
                        </div>
                      </div>
                    );
                  })
                )}
              </div>
            </>
          ) : (
            <>
              <div className="p-3.5 border-b border-slate-800/80 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Bot className="w-4 h-4 text-sky-400" />
                  <span className="text-xs font-bold uppercase tracking-wider text-slate-400">
                    Connected Drivers ({agents.length})
                  </span>
                </div>
                <button
                  onClick={() => setShowRegisterModal((v) => !v)}
                  className="text-xs flex items-center gap-1 text-sky-400 hover:text-sky-300 font-medium px-2 py-1 rounded hover:bg-slate-800/60 transition cursor-pointer"
                >
                  <Plus className="w-3.5 h-3.5" />
                  <span>Add Driver</span>
                </button>
              </div>

              {/* Inline Register Driver Drawer */}
              {showRegisterModal && (
                <form
                  onSubmit={handleRegisterAgent}
                  className="p-3.5 bg-slate-900/90 border-b border-slate-800 space-y-2.5 text-xs"
                >
                  <div className="font-semibold text-slate-200">Register Live Driver</div>

                  <div>
                    <label className="block text-[11px] text-slate-400 mb-1">Driver Type</label>
                    <select
                      aria-label="Driver Type"
                      value={newAgentType}
                      onChange={(e) => {
                        const t = e.target.value as any;
                        setNewAgentType(t);
                        if (t === 'openai' && !newAgentId) {
                          setNewAgentId('ollama-local');
                          setNewAgentName('Local Ollama Server');
                          setNewAgentRole('Autonomous AI Engineer');
                        }
                      }}
                      className="w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 focus:outline-none focus:border-sky-500 cursor-pointer"
                    >
                      <option value="openai">OpenAI Compatible Server (Ollama / vLLM / LM Studio / OpenAI)</option>
                      <option value="agy">Antigravity CLI (agy)</option>
                      <option value="claude">Claude Code CLI</option>
                      <option value="copilot">GitHub Copilot CLI</option>
                      <option value="generic">Custom Command Agent</option>
                    </select>
                  </div>

                  <input
                    type="text"
                    placeholder="Driver ID (e.g. agy-cli, ollama-local)"
                    value={newAgentId}
                    onChange={(e) => setNewAgentId(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 focus:outline-none focus:border-sky-500"
                    required
                  />
                  <input
                    type="text"
                    placeholder="Display Name (e.g. Agy Local CLI, Ollama Local)"
                    value={newAgentName}
                    onChange={(e) => setNewAgentName(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 focus:outline-none focus:border-sky-500"
                    required
                  />
                  <input
                    type="text"
                    placeholder="Role (e.g. Codebase Engineer, Autonomous AI Engineer)"
                    value={newAgentRole}
                    onChange={(e) => setNewAgentRole(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 text-slate-200 focus:outline-none focus:border-sky-500"
                  />

                  {newAgentType === 'openai' && (
                    <div className="space-y-2 p-2.5 rounded-lg bg-slate-950/60 border border-slate-800">
                      <div>
                        <label className="block text-[11px] text-slate-400 mb-0.5">Endpoint URL</label>
                        <input
                          type="text"
                          aria-label="OpenAI Endpoint URL"
                          placeholder="http://localhost:11434/v1"
                          value={newAgentEndpoint}
                          onChange={(e) => setNewAgentEndpoint(e.target.value)}
                          className="w-full bg-slate-950 border border-slate-800 rounded px-2 py-1 text-slate-200 font-mono text-[11px] focus:outline-none focus:border-sky-500"
                        />
                      </div>
                      <div>
                        <label className="block text-[11px] text-slate-400 mb-0.5">API Key (Optional)</label>
                        <input
                          type="password"
                          aria-label="OpenAI API Key"
                          placeholder="sk-... (optional for local servers)"
                          value={newAgentApiKey}
                          onChange={(e) => setNewAgentApiKey(e.target.value)}
                          className="w-full bg-slate-950 border border-slate-800 rounded px-2 py-1 text-slate-200 font-mono text-[11px] focus:outline-none focus:border-sky-500"
                        />
                      </div>

                      <div className="flex items-center gap-2 pt-1">
                        <button
                          type="button"
                          onClick={handleTestOpenAi}
                          disabled={isTestingOpenAi || !newAgentEndpoint.trim()}
                          className="px-2.5 py-1 rounded bg-slate-800 hover:bg-slate-700 text-sky-400 font-medium text-[11px] flex items-center gap-1.5 disabled:opacity-50 cursor-pointer"
                        >
                          {isTestingOpenAi && <RefreshCw className="w-3 h-3 animate-spin" />}
                          <span>Test & Fetch Models</span>
                        </button>
                      </div>

                      {openAiTestResult && (
                        <div
                          className={`p-2 rounded text-[11px] ${
                            openAiTestResult.success
                              ? 'bg-emerald-950/40 text-emerald-300 border border-emerald-800/40'
                              : 'bg-rose-950/40 text-rose-300 border border-rose-800/40'
                          }`}
                        >
                          {openAiTestResult.message}
                        </div>
                      )}

                      <div>
                        <label className="block text-[11px] text-slate-400 mb-0.5">Default Model</label>
                        {openAiTestResult?.models && openAiTestResult.models.length > 0 ? (
                          <div className="space-y-1">
                            <select
                              aria-label="Fetched Model Selection"
                              value={newAgentModel}
                              onChange={(e) => setNewAgentModel(e.target.value)}
                              className="w-full bg-slate-950 border border-slate-800 rounded px-2 py-1 text-slate-200 text-[11px] focus:outline-none focus:border-sky-500 cursor-pointer"
                            >
                              <option value="">Select a fetched model...</option>
                              {openAiTestResult.models.map((m) => (
                                <option key={m.id} value={m.id}>
                                  {m.name || m.id}
                                </option>
                              ))}
                            </select>
                            <input
                              type="text"
                              aria-label="Custom Model Input"
                              placeholder="Or type custom model name (e.g. qwen2.5-coder:32b)"
                              value={newAgentModel}
                              onChange={(e) => setNewAgentModel(e.target.value)}
                              className="w-full bg-slate-950 border border-slate-800 rounded px-2 py-1 text-slate-200 text-[11px] focus:outline-none focus:border-sky-500 font-mono"
                            />
                          </div>
                        ) : (
                          <input
                            type="text"
                            aria-label="Custom Model Input"
                            placeholder="e.g. llama3.3, qwen2.5-coder, gpt-4o"
                            value={newAgentModel}
                            onChange={(e) => setNewAgentModel(e.target.value)}
                            className="w-full bg-slate-950 border border-slate-800 rounded px-2 py-1 text-slate-200 text-[11px] focus:outline-none focus:border-sky-500 font-mono"
                          />
                        )}
                      </div>
                    </div>
                  )}

                  <div className="flex justify-end gap-2 pt-1">
                    <button
                      type="button"
                      onClick={() => {
                        setShowRegisterModal(false);
                        setOpenAiTestResult(null);
                      }}
                      className="px-2.5 py-1 rounded text-slate-400 hover:text-slate-200"
                    >
                      Cancel
                    </button>
                    <button
                      type="submit"
                      disabled={isActionPending}
                      className="px-3 py-1 rounded bg-sky-600 hover:bg-sky-500 text-white font-medium disabled:opacity-50"
                    >
                      Register
                    </button>
                  </div>
                </form>
              )}

              {/* Driver & Session List */}
              <div className="flex-1 overflow-y-auto p-3 space-y-4">
                {isLoading ? (
                  <div className="py-12 flex flex-col items-center justify-center text-slate-500 text-xs gap-2">
                    <RefreshCw className="w-5 h-5 animate-spin text-sky-400" />
                    <span>Loading drivers...</span>
                  </div>
                ) : agents.length === 0 ? (
                  <div className="p-5 rounded-xl border border-dashed border-slate-800 bg-slate-900/30 text-center space-y-3 my-4">
                    <Bot className="w-8 h-8 text-slate-600 mx-auto" />
                    <div>
                      <h3 className="text-xs font-semibold text-slate-300">No Agents Connected</h3>
                      <p className="text-[11px] text-slate-500 mt-1 leading-relaxed">
                        Connect an external CLI driver or register an agent to start a session.
                      </p>
                    </div>
                    <button
                      onClick={() => setShowRegisterModal(true)}
                      disabled={isActionPending}
                      className="w-full py-2 px-3 rounded-lg bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center justify-center gap-1.5 transition cursor-pointer"
                    >
                      <Plus className="w-3.5 h-3.5" />
                      <span>Register Agent</span>
                    </button>
                  </div>
                ) : (
                  agents.map((agent) => {
                    const agentSessions = sessions
                      .filter((s) => s.driverId === agent.id || s.creatorAgentId === agent.id)
                      .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
                    const driverPersonas = personas.filter((p) => p.driverId === agent.id);
                    const isAgentSelected = selectedAgent?.id === agent.id;
                    const isOffline = agent.status === 'offline';
                    const isBusy =
                      !isOffline &&
                      (agent.status === 'busy' || agentSessions.some((s) => s.status === 'thinking'));
                    const usage = agentUsages[agent.id];

                    return (
                      <div
                        key={agent.id}
                        className={`rounded-xl border transition ${
                          isAgentSelected
                            ? 'border-slate-700 bg-slate-900/80'
                            : 'border-slate-800/70 bg-slate-900/30 hover:border-slate-800'
                        }`}
                      >
                        {/* Driver Header */}
                        <div
                          onClick={() => {
                            setSelectedAgentId(agent.id);
                            if (agentSessions.length > 0 && !agentSessions.some((s) => s.id === selectedSessionId)) {
                              setSelectedSessionId(agentSessions[0].id);
                            }
                          }}
                          className="p-3 flex items-start justify-between cursor-pointer"
                        >
                          <div className="flex items-start gap-2.5 min-w-0">
                            <span
                              className={`w-2.5 h-2.5 rounded-full mt-1 shrink-0 ${
                                isOffline
                                  ? 'bg-slate-600'
                                  : isBusy
                                  ? 'bg-amber-400 animate-pulse'
                                  : 'bg-emerald-400'
                              }`}
                            />
                            <div className="min-w-0">
                              <div className="flex items-center gap-1.5">
                                <span className="text-xs font-bold text-white truncate">{agent.name}</span>
                                <span
                                  className={`text-[9px] font-mono uppercase px-1.5 py-0.2 rounded ${
                                    isOffline
                                      ? 'bg-slate-800 text-slate-400'
                                      : isBusy
                                      ? 'bg-amber-500/20 text-amber-300'
                                      : 'bg-emerald-500/20 text-emerald-300'
                                  }`}
                                >
                                  {isOffline ? 'offline' : isBusy ? 'busy' : 'online'}
                                </span>
                              </div>
                              <div className="text-[11px] font-mono text-slate-400 truncate">
                                @{agent.id} · {agent.role || 'Driver'}
                              </div>
                              {driverPersonas.length > 0 && (
                                <div className="flex flex-wrap items-center gap-1 mt-1.5">
                                  {driverPersonas.map((dp) => (
                                    <span
                                      key={dp.id}
                                      className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-indigo-950/50 border border-indigo-500/30 text-[10px] font-mono text-indigo-300"
                                      title={`Persona: ${dp.name} (${dp.role})`}
                                    >
                                      <Users className="w-2.5 h-2.5 text-indigo-400" />
                                      <span>@{dp.id}</span>
                                    </span>
                                  ))}
                                </div>
                              )}
                              {usage && (
                                <button
                                  type="button"
                                  onClick={(e) => {
                                    e.stopPropagation();
                                    handleOpenUsageModal(agent);
                                  }}
                                  className="mt-1.5 flex items-center gap-1.5 px-2 py-0.5 rounded-md bg-slate-950/70 hover:bg-slate-800 text-[10px] font-mono text-slate-300 border border-slate-800 hover:border-slate-700 transition cursor-pointer max-w-full"
                                  title="Click to view full quota and lifetime token usage"
                                >
                                  <span
                                    className={`w-1.5 h-1.5 rounded-full shrink-0 ${
                                      usage.providerQuota
                                        ? usage.providerQuota.primaryRemainingFraction > 0.3
                                          ? 'bg-emerald-400'
                                          : 'bg-rose-400'
                                        : 'bg-sky-400'
                                    }`}
                                  />
                                  <span className="truncate">
                                    {usage.providerQuota
                                      ? `${Math.round(usage.providerQuota.primaryRemainingFraction * 100)}% quota`
                                      : 'Quota'}
                                    {' · '}
                                    {usage.lifetimeTokens.totalTokens > 0
                                      ? `${(usage.lifetimeTokens.totalTokens / 1000).toFixed(1)}k tokens`
                                      : '0 tokens'}
                                  </span>
                                  <BarChart2 className="w-2.5 h-2.5 text-slate-500 hover:text-sky-400 ml-1 shrink-0" />
                                </button>
                              )}
                            </div>
                          </div>

                          <div className="flex items-center gap-1 shrink-0">
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                setConfigAgent(agent);
                              }}
                              className="p-1 rounded-md bg-slate-800 hover:bg-slate-700 text-slate-300 hover:text-sky-400 transition cursor-pointer"
                              title={`Configure ${agent.name} (endpoint, API key, limits)`}
                              aria-label={`Configure ${agent.name} driver settings`}
                            >
                              <Sliders className="w-3.5 h-3.5" />
                            </button>
                            <button
                              type="button"
                              onClick={(e) => {
                                e.stopPropagation();
                                setSelectedAgentId(agent.id);
                                setIsCreatingSession(true);
                              }}
                              className="p-1 rounded-md bg-slate-800 hover:bg-sky-600 text-slate-300 hover:text-white transition cursor-pointer"
                              title={`New Session with ${agent.name}`}
                            >
                              <Plus className="w-3.5 h-3.5" />
                            </button>
                          </div>
                        </div>

                        {/* Driver's Sessions (across all associated Personas) */}
                        <div className="px-2 pb-2 space-y-1">
                          {agentSessions.length === 0 ? (
                            <div className="px-2 py-1.5 text-[11px] text-slate-500 italic">
                              No sessions yet
                            </div>
                          ) : (
                            agentSessions.map((session) => {
                              const isSelected = session.id === selectedSessionId && !isCreatingSession;
                              const isThinking = session.status === 'thinking';

                              return (
                                <div key={session.id} className="space-y-1">
                                  <div
                                    onClick={() => {
                                      setSelectedAgentId(agent.id);
                                      if (session.personaId) {
                                        setSelectedPersonaId(session.personaId);
                                      }
                                      setSelectedSessionId(session.id);
                                      setIsCreatingSession(false);
                                    }}
                                    className={`group w-full text-left px-2.5 py-2 rounded-lg text-xs flex items-center justify-between gap-2 transition cursor-pointer ${
                                      isSelected
                                        ? 'bg-sky-500/15 text-sky-300 border border-sky-500/30 font-medium'
                                        : 'text-slate-400 hover:bg-slate-800/50 hover:text-slate-200 border border-transparent'
                                    }`}
                                  >
                                    <div className="flex items-center gap-2 min-w-0 flex-1">
                                      {session.status === 'waiting_for_input' ? (
                                        <span className="w-2.5 h-2.5 rounded-full bg-amber-400 border border-amber-300 animate-ping shrink-0" title="Human input required" />
                                      ) : isThinking ? (
                                        <RefreshCw className="w-3 h-3 text-amber-400 animate-spin shrink-0" />
                                      ) : (
                                        <MessageSquare className="w-3 h-3 text-slate-500 shrink-0" />
                                      )}
                                      <span className="truncate">{session.title}</span>
                                      {session.personaId && (
                                        <span className="text-[10px] font-mono px-1.5 py-0.2 rounded bg-indigo-500/15 text-indigo-300 border border-indigo-500/30 shrink-0">
                                          @{session.personaId}
                                        </span>
                                      )}
                                    </div>

                                    <div className="flex items-center gap-1 shrink-0">
                                      <button
                                        type="button"
                                        onClick={(e) => {
                                          e.stopPropagation();
                                          setDeletingSessionId(session.id);
                                        }}
                                        className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-rose-500/20 text-slate-400 hover:text-rose-400 transition cursor-pointer"
                                        title={`Delete session ${session.title}`}
                                      >
                                        <Trash2 className="w-3 h-3" />
                                      </button>
                                      <ChevronRight className="w-3 h-3 opacity-50 shrink-0 group-hover:opacity-100" />
                                    </div>
                                  </div>

                                  {/* Delete Confirmation Inline Banner */}
                                  {deletingSessionId === session.id && (
                                    <div className="p-2 rounded-lg bg-rose-950/70 border border-rose-500/40 text-[11px] text-rose-200 space-y-1.5">
                                      <p className="truncate">Delete <strong>{session.title}</strong>?</p>
                                      <div className="flex justify-end gap-1.5">
                                        <button
                                          type="button"
                                          onClick={(e) => {
                                            e.stopPropagation();
                                            setDeletingSessionId(null);
                                          }}
                                          className="px-2 py-0.5 rounded bg-slate-800 text-slate-300 hover:text-white cursor-pointer"
                                        >
                                          Cancel
                                        </button>
                                        <button
                                          type="button"
                                          onClick={(e) => {
                                            e.stopPropagation();
                                            handleDeleteSession(session.id);
                                          }}
                                          className="px-2.5 py-0.5 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold cursor-pointer"
                                        >
                                          Confirm Delete
                                        </button>
                                      </div>
                                    </div>
                                  )}
                                </div>
                              );
                            })
                          )}
                        </div>
                      </div>
                    );
                  })
                )}
              </div>
            </>
          )}
        </aside>

        {/* Right Main Panel: Active Session Trajectory or New Session Composer */}
        <main className="flex-1 flex flex-col bg-slate-950 overflow-hidden">
          {activeTab === 'projects' ? (
            (() => {
              const activeProj = projects.find((p) => p.id === selectedProjectId) || projects[0] || null;
              const activeJ = selectedJobId
                ? (jobsByProject[activeProj?.id || ''] || []).find((j) => j.id === selectedJobId) || null
                : null;

              if (activeProj && activeJ) {
                return (
                  <ErrorBoundary fallbackTitle="Job Cockpit Error">
                    <JobCockpit
                      project={activeProj}
                      job={activeJ}
                      tasks={jobTasks}
                      messages={jobMessages}
                      personas={personas}
                      sessions={sessions}
                      onSendMessage={handleSendJobMessage}
                      onAddTask={handleAddTask}
                      onToggleTask={handleToggleTask}
                      onUpdateTask={handleUpdateTask}
                      onAssignTask={handleAssignTask}
                      onDeleteTask={handleDeleteTask}
                      onCompleteJob={handleCompleteJob}
                      onArchiveJob={handleArchiveJob}
                      onReopenJob={handleReopenJob}
                      onOpenLightbox={setLightboxAttachment}
                      onStopTurn={handleStopSession}
                      onCancelQueuedTurn={(turnId) => handleCancelQueuedJobTurn(turnId, activeJ.id)}
                      onCancelQueuedTurns={(personaId) => handleCancelQueuedJobTurns(personaId, activeJ.id)}
                      onAnswerQuestion={handleJobQuestionAnswer}
                      onEditProject={() => {
                        setEditingProject(activeProj);
                        setShowProjectModal(true);
                      }}
                      onSelectTurnSession={async (sessionId) => {
                        hasUserSwitchedTabRef.current = true;
                        setIsCreatingSession(false);
                        setActiveTab('personas');
                        setSelectedSessionId(sessionId);
                        const existing = sessions.find((s) => s.id === sessionId);
                        if (existing) {
                          if (existing.personaId) setSelectedPersonaId(existing.personaId);
                          if (existing.driverId || existing.creatorAgentId) {
                            setSelectedAgentId(existing.driverId || existing.creatorAgentId || null);
                          }
                        }
                        try {
                          const fetched = await api.getSession(sessionId);
                          if (fetched) {
                            setSessions((prev) =>
                              prev.some((s) => s.id === fetched.id)
                                ? prev.map((s) => (s.id === fetched.id ? { ...s, ...fetched } : s))
                                : [fetched, ...prev]
                            );
                            if (fetched.personaId) setSelectedPersonaId(fetched.personaId);
                            if (fetched.driverId || fetched.creatorAgentId) {
                              setSelectedAgentId(fetched.driverId || fetched.creatorAgentId || null);
                            }
                          }
                        } catch (err) {
                          console.error('Failed to fetch turn session', err);
                        }
                      }}
                    />
                  </ErrorBoundary>
                );
              }

              return (
                <div className="flex-1 flex flex-col items-center justify-center text-center p-8 text-slate-500">
                  <FolderGit2 className="w-12 h-12 stroke-[1.2] text-slate-600 mb-3" />
                  <h3 className="text-sm font-semibold text-slate-300">
                    {activeProj ? `Project: ${activeProj.name}` : 'No Project Selected'}
                  </h3>
                  <p className="text-xs text-slate-500 max-w-sm mt-1">
                    {activeProj
                      ? 'Select a Job from the sidebar or click + to start a collaborative task.'
                      : 'Select or create a project to collaborate with Personas.'}
                  </p>
                  {activeProj ? (
                    <button
                      type="button"
                      onClick={() => {
                        setJobModalProject(activeProj);
                        setShowJobModal(true);
                      }}
                      className="mt-4 px-4 py-2 bg-sky-500 hover:bg-sky-400 text-slate-950 font-bold text-xs rounded-xl shadow-md transition flex items-center gap-1.5 cursor-pointer"
                    >
                      <Plus className="w-4 h-4" />
                      <span>Start New Job</span>
                    </button>
                  ) : (
                    <button
                      type="button"
                      onClick={() => {
                        setEditingProject(null);
                        setShowProjectModal(true);
                      }}
                      className="mt-4 px-4 py-2 bg-sky-500 hover:bg-sky-400 text-slate-950 font-bold text-xs rounded-xl shadow-md transition flex items-center gap-1.5 cursor-pointer"
                    >
                      <Plus className="w-4 h-4" />
                      <span>Create Project</span>
                    </button>
                  )}
                </div>
              );
            })()
          ) : isCreatingSession && (selectedAgent || activeCreationPersona) ? (
            <div className="flex-1 flex items-center justify-center p-6">
              <form
                onSubmit={handleCreateSession}
                onDragOver={(e) => {
                  e.preventDefault();
                  setIsDraggingNewSession(true);
                }}
                onDragLeave={(e) => {
                  e.preventDefault();
                  setIsDraggingNewSession(false);
                }}
                onDrop={(e) => {
                  e.preventDefault();
                  setIsDraggingNewSession(false);
                  if (e.dataTransfer?.files && e.dataTransfer.files.length > 0) {
                    void stageFilesForComposer(e.dataTransfer.files, 'newSession');
                  }
                }}
                className={`max-w-lg w-full bg-slate-900/70 border rounded-2xl p-6 space-y-4 shadow-2xl relative transition ${
                  isDraggingNewSession ? 'border-sky-400 ring-2 ring-sky-500/40' : 'border-slate-800'
                }`}
              >
                {isDraggingNewSession && (
                  <div className="absolute inset-0 z-30 rounded-2xl bg-sky-950/80 border-2 border-dashed border-sky-400 flex flex-col items-center justify-center text-sky-200 pointer-events-none">
                    <Paperclip className="w-8 h-8 mb-2 text-sky-400 animate-bounce" />
                    <span className="text-sm font-semibold">Drop files to attach to New Session</span>
                  </div>
                )}
                <div className="flex items-center gap-2.5">
                  <div className="w-9 h-9 rounded-xl bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
                    {activeTab === 'personas' && activeCreationPersona ? (
                      <Users className="w-5 h-5" />
                    ) : (
                      <Bot className="w-5 h-5" />
                    )}
                  </div>
                  <div>
                    <h2 className="text-base font-bold text-white">
                      Start New Session with{' '}
                      {activeTab === 'personas' && activeCreationPersona
                        ? activeCreationPersona.name
                        : selectedAgent?.name || 'Agent'}
                    </h2>
                    <div className="flex items-center gap-2 text-xs text-slate-400 font-mono">
                      {activeTab === 'personas' && activeCreationPersona ? (
                        <>
                          <span>@{activeCreationPersona.id}</span>
                          <span>·</span>
                          <span className="text-sky-400">Driver: @{activeCreationPersona.driverId}</span>
                        </>
                      ) : (
                        <span>@{selectedAgent?.id}</span>
                      )}
                    </div>
                  </div>
                </div>

                <div className="space-y-1.5">
                  <label className="block text-xs font-semibold text-slate-300">
                    Session Objective / Title
                  </label>
                  <input
                    type="text"
                    placeholder="e.g. Refactor authentication middleware"
                    value={newSessionTitle}
                    onChange={(e) => setNewSessionTitle(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3.5 py-2 text-sm text-white focus:outline-none focus:border-sky-500"
                    required
                    autoFocus
                  />
                </div>

                <div className="space-y-1.5">
                  <div className="flex items-center justify-between">
                    <label className="block text-xs font-semibold text-slate-300">
                      Initial Prompt (Optional)
                    </label>
                    <button
                      type="button"
                      onClick={() => newSessionFileInputRef.current?.click()}
                      className="text-[11px] font-medium text-sky-400 hover:text-sky-300 flex items-center gap-1 cursor-pointer"
                      aria-label="Attach files to new session"
                    >
                      <Paperclip className="w-3 h-3" />
                      <span>Attach files</span>
                    </button>
                    <input
                      ref={newSessionFileInputRef}
                      type="file"
                      multiple
                      className="hidden"
                      data-testid="new-session-file-input"
                      onChange={(e) => {
                        if (e.target.files && e.target.files.length > 0) {
                          void stageFilesForComposer(e.target.files, 'newSession');
                          e.target.value = '';
                        }
                      }}
                    />
                  </div>
                  <textarea
                    rows={2}
                    placeholder="Enter an initial instruction for the agent to begin reasoning (or paste/drop images & files)..."
                    value={newSessionPrompt}
                    onChange={(e) => setNewSessionPrompt(e.target.value)}
                    onPaste={handleNewSessionPaste}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3.5 py-2 text-sm text-white focus:outline-none focus:border-sky-500"
                  />
                  {newSessionAttachments.length > 0 && (
                    <div className="flex flex-wrap gap-2 pt-1.5" data-testid="new-session-staged-attachments">
                      {newSessionAttachments.map((att) => (
                        <div
                          key={att.localId}
                          className="flex items-center gap-2 px-2.5 py-1.5 rounded-lg bg-slate-950 border border-slate-800 text-xs text-slate-200 max-w-xs"
                        >
                          {att.previewUrl ? (
                            <img
                              src={att.previewUrl}
                              alt={att.name}
                              className="w-7 h-7 rounded object-cover border border-slate-700 shrink-0"
                            />
                          ) : (
                            <FileIcon className="w-4 h-4 text-sky-400 shrink-0" />
                          )}
                          <div className="min-w-0 flex-1">
                            <div className="truncate font-mono text-[11px] text-white">{att.name}</div>
                            <div className="text-[10px] text-slate-400">{formatAttachmentSize(att.sizeBytes)}</div>
                          </div>
                          <button
                            type="button"
                            onClick={() => handleRemoveStagedAttachment(att.localId, 'newSession')}
                            aria-label={`Remove attachment ${att.name}`}
                            className="p-0.5 rounded hover:bg-slate-800 text-slate-400 hover:text-rose-400 cursor-pointer"
                          >
                            <X className="w-3.5 h-3.5" />
                          </button>
                        </div>
                      ))}
                    </div>
                  )}
                </div>

                {/* Session Controls: Working Directory & Model Parameters */}
                <div className="pt-2 border-t border-slate-800/80 space-y-3.5">
                  <div className="flex items-center gap-1.5 text-xs font-semibold text-slate-300">
                    <Sliders className="w-3.5 h-3.5 text-sky-400" />
                    <span>Agent Execution Controls</span>
                  </div>

                  {/* Working Directory Selection */}
                  <div className="space-y-2">
                    <div className="flex items-center justify-between">
                      <label className="block text-[11px] font-medium text-slate-400">
                        Working Directory
                      </label>
                      {newSessionWorkDir && (
                        <button
                          type="button"
                          onClick={() => setNewSessionWorkDir('')}
                          className="text-[10px] text-sky-400 hover:text-sky-300 flex items-center gap-1 cursor-pointer"
                        >
                          <RotateCcw className="w-2.5 h-2.5" />
                          <span>Reset to Sandbox</span>
                        </button>
                      )}
                    </div>

                    <div className="flex items-center gap-2">
                      <div
                        onClick={openFolderPicker}
                        className="flex-1 min-w-0 bg-slate-950 border border-slate-800 hover:border-slate-700 rounded-lg px-3 py-2 flex items-center gap-2.5 cursor-pointer transition group"
                      >
                        <div
                          className={`p-1.5 rounded-md ${
                            newSessionWorkDir
                              ? 'bg-sky-500/10 text-sky-400'
                              : 'bg-emerald-500/10 text-emerald-400'
                          }`}
                        >
                          <Folder className="w-4 h-4" />
                        </div>
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-2">
                            <span className="text-xs font-semibold text-white truncate">
                              {newSessionWorkDir
                                ? newSessionWorkDir.split(/[\\/]/).filter(Boolean).pop() || newSessionWorkDir
                                : 'Default Sandbox Workspace'}
                            </span>
                            <span
                              className={`text-[9px] font-mono px-1.5 py-0.2 rounded border ${
                                newSessionWorkDir
                                  ? 'bg-sky-500/10 text-sky-300 border-sky-500/20'
                                  : 'bg-emerald-500/10 text-emerald-300 border-emerald-500/20'
                              }`}
                            >
                              {newSessionWorkDir ? 'Custom Path' : 'Safe Isolated'}
                            </span>
                          </div>
                          <p className="text-[10px] text-slate-400 font-mono truncate mt-0.5">
                            {newSessionWorkDir || '~/.agentmesh/workspaces/<session-id>'}
                          </p>
                        </div>
                      </div>

                      <button
                        type="button"
                        onClick={openFolderPicker}
                        className="px-3 py-2 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 border border-slate-700 hover:border-slate-600 text-xs font-medium flex items-center gap-1.5 transition shrink-0 cursor-pointer"
                      >
                        <FolderOpen className="w-3.5 h-3.5 text-sky-400" />
                        <span>Browse...</span>
                      </button>
                    </div>

                    {/* Quick Preset Bookmark Chips */}
                    <div className="flex flex-wrap items-center gap-1.5 pt-0.5">
                      <span className="text-[10px] text-slate-500 font-medium mr-1">Quick Select:</span>
                      <button
                        type="button"
                        onClick={() => setNewSessionWorkDir('')}
                        className={`text-[10px] font-mono px-2 py-0.5 rounded-md border transition cursor-pointer ${
                          !newSessionWorkDir
                            ? 'bg-emerald-500/20 text-emerald-300 border-emerald-500/40 font-semibold'
                            : 'bg-slate-900 text-slate-400 border-slate-800 hover:text-slate-200 hover:border-slate-700'
                        }`}
                      >
                        ⚡ Default Sandbox
                      </button>
                      {capabilities?.workspace?.bookmarks
                        ?.filter((b) => b.path)
                        .map((bookmark) => {
                          const isSelected = newSessionWorkDir === bookmark.path;
                          return (
                            <button
                              key={bookmark.label}
                              type="button"
                              onClick={() => setNewSessionWorkDir(bookmark.path)}
                              title={bookmark.path}
                              className={`text-[10px] font-mono px-2 py-0.5 rounded-md border transition cursor-pointer ${
                                isSelected
                                  ? 'bg-sky-500/20 text-sky-300 border-sky-500/40 font-semibold'
                                  : 'bg-slate-900 text-slate-400 border-slate-800 hover:text-slate-200 hover:border-slate-700'
                              }`}
                            >
                              {bookmark.label}
                            </button>
                          );
                        })}
                    </div>
                  </div>

                  <div className={`grid ${availableTiers.length > 0 ? 'grid-cols-2 sm:grid-cols-3 lg:grid-cols-5' : 'grid-cols-2 sm:grid-cols-4'} gap-2.5`}>
                    {/* Model Selection Dropdown */}
                    <div className="space-y-1">
                      <label className="block text-[11px] font-medium text-slate-400">
                        Model
                      </label>
                      <select
                        value={newSessionModel}
                        onChange={(e) => setNewSessionModel(e.target.value)}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1.5 text-xs text-white focus:outline-none focus:border-sky-500 cursor-pointer"
                      >
                        <option value="">Default Model</option>
                        {availableModels.map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.name || m.id}
                          </option>
                        ))}
                      </select>
                      {currentRuntimeCaps?.adapter === 'openai' && (
                        <input
                          type="text"
                          aria-label="Custom Session Model Input"
                          placeholder="Or custom model name..."
                          value={newSessionModel}
                          onChange={(e) => setNewSessionModel(e.target.value)}
                          className="w-full mt-1 bg-slate-950 border border-slate-800 rounded-lg px-2 py-1 text-slate-200 text-[11px] font-mono focus:outline-none focus:border-sky-500"
                        />
                      )}
                    </div>

                    {/* Reasoning Effort Dropdown (tied to model) */}
                    <div className="space-y-1">
                      <div className="flex items-center justify-between">
                        <label className="block text-[11px] font-medium text-slate-400">
                          Reasoning Effort
                        </label>
                      </div>
                      <select
                        value={newSessionEffort}
                        onChange={(e) => setNewSessionEffort(e.target.value)}
                        disabled={availableEfforts.length === 0}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2 py-1.5 text-xs text-slate-200 focus:outline-none focus:border-sky-500 disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer"
                      >
                        {availableEfforts.length === 0 ? (
                          <option value="">Not applicable</option>
                        ) : (
                          <>
                            <option value="">Default Effort</option>
                            {availableEfforts.map((eff) => (
                              <option key={eff} value={eff}>
                                {eff}
                              </option>
                            ))}
                          </>
                        )}
                      </select>
                    </div>

                    {/* Execution Mode Dropdown (tied to system) */}
                    <div className="space-y-1">
                      <label className="block text-[11px] font-medium text-slate-400">
                        Mode
                      </label>
                      <select
                        value={newSessionMode}
                        onChange={(e) => setNewSessionMode(e.target.value)}
                        disabled={availableModes.length === 0}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2 py-1.5 text-xs text-slate-200 focus:outline-none focus:border-sky-500 disabled:opacity-40 disabled:cursor-not-allowed cursor-pointer"
                      >
                        {availableModes.length === 0 ? (
                          <option value="">Not supported</option>
                        ) : (
                          <>
                            <option value="">Default Mode</option>
                            {availableModes.map((mode) => (
                              <option key={mode.id} value={mode.id}>
                                {mode.label}
                              </option>
                            ))}
                          </>
                        )}
                      </select>
                    </div>

                    {/* Context Size Dropdown (tied to model/driver) */}
                    {availableContextSizes.length > 0 && (
                      <div className="space-y-1">
                        <label className="block text-[11px] font-medium text-slate-400">
                          Context Window Size
                        </label>
                        <select
                          aria-label="New Session Context Size"
                          value={newSessionContextSize}
                          onChange={(e) => setNewSessionContextSize(e.target.value)}
                          className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2 py-1.5 text-xs text-slate-200 focus:outline-none focus:border-sky-500 cursor-pointer"
                        >
                          <option value="">Default Context</option>
                          {availableContextSizes.map((cs) => (
                            <option key={cs.id} value={cs.id} title={cs.description}>
                              {cs.label}
                            </option>
                          ))}
                        </select>
                      </div>
                    )}

                    {/* Copilot Tier / Optimize For Dropdown */}
                    {availableTiers.length > 0 && (
                      <div className="space-y-1">
                        <label className="block text-[11px] font-medium text-slate-400">
                          Optimize For (Tier)
                        </label>
                        <select
                          value={newSessionTier}
                          onChange={(e) => setNewSessionTier(e.target.value)}
                          className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2 py-1.5 text-xs text-slate-200 focus:outline-none focus:border-sky-500 cursor-pointer"
                        >
                          <option value="">Default Tier</option>
                          {availableTiers.map((t) => (
                            <option key={t.id} value={t.id}>
                              {t.label}
                            </option>
                          ))}
                        </select>
                      </div>
                    )}
                  </div>
                </div>

                <div className="flex items-center justify-end gap-2.5 pt-2">
                  <button
                    type="button"
                    onClick={() => setIsCreatingSession(false)}
                    className="px-4 py-2 rounded-lg text-xs font-medium text-slate-400 hover:text-white transition"
                  >
                    Cancel
                  </button>
                  <button
                    type="submit"
                    disabled={isActionPending}
                    className="px-4 py-2 rounded-lg text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white transition cursor-pointer"
                  >
                    Launch Session
                  </button>
                </div>
              </form>
            </div>
          ) : selectedSession ? (
            <>
              {/* Active Session Header */}
              <div className="px-6 py-3 border-b border-slate-800/80 bg-slate-900/30 flex items-center justify-between shrink-0">
                <div className="min-w-0">
                  <div className="flex items-center gap-2.5">
                    <h2 className="text-sm font-bold text-white truncate">
                      {selectedSession.title}
                    </h2>
                    <span
                      className={`text-[11px] font-mono px-2.5 py-0.5 rounded-full border ${
                        isSessionThinking
                          ? 'bg-amber-500/10 text-amber-300 border-amber-500/30 animate-pulse'
                          : selectedSession.status === 'waiting_for_input'
                          ? 'bg-amber-500/20 text-amber-200 border-amber-400 font-semibold shadow-sm animate-pulse flex items-center gap-1.5'
                          : 'bg-emerald-500/10 text-emerald-300 border-emerald-500/30'
                      }`}
                    >
                      {isSessionThinking
                        ? 'Thinking...'
                        : selectedSession.status === 'waiting_for_input'
                        ? 'Waiting for Input'
                        : selectedSession.status}
                    </span>
                    {selectedSessionMetadata?.jobId && selectedSessionMetadata?.projectId && (
                      <button
                        type="button"
                        onClick={() => {
                          hasUserSwitchedTabRef.current = true;
                          setSelectedProjectId(selectedSessionMetadata.projectId);
                          setSelectedJobId(selectedSessionMetadata.jobId);
                          setActiveTab('projects');
                        }}
                        className="inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-lg bg-sky-950/80 hover:bg-sky-900/80 text-sky-300 hover:text-white border border-sky-500/30 text-[11px] font-semibold transition cursor-pointer shadow-sm"
                        title={`Return to Job: ${selectedSessionMetadata.projectName || 'Job'}`}
                      >
                        <FolderGit2 className="w-3 h-3 text-sky-400" />
                        <span>← Back to Job</span>
                      </button>
                    )}
                  </div>

                  <div className="flex flex-wrap items-center gap-1.5 mt-1 font-mono text-[11px]">
                    {(selectedSession.personaId || selectedSessionMetadata?.personaId) && (
                      <>
                        <span className="text-slate-400">
                          Persona: <strong className="text-indigo-300">@{selectedSession.personaId || selectedSessionMetadata?.personaId}</strong>
                        </span>
                        <span className="text-slate-600">·</span>
                      </>
                    )}
                    <span className="text-slate-400">
                      Driver: <strong className="text-slate-200">@{selectedSession.driverId || selectedAgent?.id || selectedSession.creatorAgentId || 'agy'}</strong>
                    </span>
                    <span className="text-slate-600">·</span>
                    <span
                      className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 text-slate-300 border border-slate-700/80 truncate max-w-xs"
                      title={selectedSessionMetadata?.workingDirectory || `~/.agentmesh/workspaces/${selectedSession.id}`}
                    >
                      <Folder className="w-3 h-3 text-sky-400 shrink-0" />
                      <span className="truncate">
                        {selectedSessionMetadata?.workingDirectory
                          ? selectedSessionMetadata.workingDirectory
                          : `~/.agentmesh/workspaces/${selectedSession.id.slice(0, 8)}...`}
                      </span>
                    </span>

                    {/* Backdrop to close inline popover */}
                    {editingSessionControl && (
                      <div
                        className="fixed inset-0 z-30"
                        onClick={() => setEditingSessionControl(null)}
                      />
                    )}

                    {/* Model Selector Badge & Popover */}
                    <div className="relative inline-block">
                      <button
                        type="button"
                        onClick={() => setEditingSessionControl((prev) => (prev === 'model' ? null : 'model'))}
                        className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                        title="Click to change model for this session"
                      >
                        <Cpu className="w-3 h-3 text-purple-400 shrink-0" />
                        <span>{selectedSessionMetadata?.model || 'default model'}</span>
                        <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                      </button>
                      {editingSessionControl === 'model' && (
                        <div className="absolute left-0 top-full mt-1.5 z-40 w-64 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                          <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                            Switch Session Model
                          </div>
                          <button
                            type="button"
                            onClick={() => handleUpdateSessionControl({ model: '' })}
                            className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                          >
                            <span>Default Model</span>
                            {!selectedSessionMetadata?.model && <Check className="w-3 h-3 text-sky-400" />}
                          </button>
                          {availableModels.map((m) => (
                            <button
                              key={m.id}
                              type="button"
                              onClick={() => handleUpdateSessionControl({ model: m.id })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span className="truncate">{m.name || m.id}</span>
                              {selectedSessionMetadata?.model === m.id && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>

                    {/* Effort Selector Badge & Popover */}
                    {(selectedSessionMetadata?.effort || sessionAvailableEfforts.length > 0) && (
                      <div className="relative inline-block">
                        <button
                          type="button"
                          onClick={() => setEditingSessionControl((prev) => (prev === 'effort' ? null : 'effort'))}
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                          title="Click to change reasoning effort"
                        >
                          <Brain className="w-3 h-3 text-indigo-400 shrink-0" />
                          <span>{selectedSessionMetadata?.effort || 'effort'}</span>
                          <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                        </button>
                        {editingSessionControl === 'effort' && (
                          <div className="absolute left-0 top-full mt-1.5 z-40 w-44 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                            <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                              Reasoning Effort
                            </div>
                            <button
                              type="button"
                              onClick={() => handleUpdateSessionControl({ effort: '' })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span>Default</span>
                              {!selectedSessionMetadata?.effort && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                            {sessionAvailableEfforts.map((eff) => (
                              <button
                                key={eff}
                                type="button"
                                onClick={() => handleUpdateSessionControl({ effort: eff })}
                                className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                              >
                                <span>{eff}</span>
                                {selectedSessionMetadata?.effort === eff && <Check className="w-3 h-3 text-sky-400" />}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {/* Tier Selector Badge & Popover (for Copilot optimize-for) */}
                    {(selectedSessionMetadata?.tier || availableTiers.length > 0) && (
                      <div className="relative inline-block">
                        <button
                          type="button"
                          onClick={() => setEditingSessionControl((prev) => (prev === 'tier' ? null : 'tier'))}
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                          title="Click to change optimization tier"
                        >
                          <Layers className="w-3 h-3 text-teal-400 shrink-0" />
                          <span>{selectedSessionMetadata?.tier || 'tier'}</span>
                          <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                        </button>
                        {editingSessionControl === 'tier' && (
                          <div className="absolute left-0 top-full mt-1.5 z-40 w-48 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                            <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                              Optimization Tier
                            </div>
                            <button
                              type="button"
                              onClick={() => handleUpdateSessionControl({ tier: '' })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span>Default Tier</span>
                              {!selectedSessionMetadata?.tier && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                            {availableTiers.map((t) => (
                              <button
                                key={t.id}
                                type="button"
                                onClick={() => handleUpdateSessionControl({ tier: t.id })}
                                className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                              >
                                <span>{t.label}</span>
                                {selectedSessionMetadata?.tier === t.id && <Check className="w-3 h-3 text-sky-400" />}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {/* Mode Selector Badge & Popover */}
                    {(selectedSessionMetadata?.mode || availableModes.length > 0) && (
                      <div className="relative inline-block">
                        <button
                          type="button"
                          onClick={() => setEditingSessionControl((prev) => (prev === 'mode' ? null : 'mode'))}
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                          title="Click to change execution mode"
                        >
                          <Sliders className="w-3 h-3 text-amber-400 shrink-0" />
                          <span>{selectedSessionMetadata?.mode || 'mode'}</span>
                          <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                        </button>
                        {editingSessionControl === 'mode' && (
                          <div className="absolute left-0 top-full mt-1.5 z-40 w-48 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                            <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                              Execution Mode
                            </div>
                            <button
                              type="button"
                              onClick={() => handleUpdateSessionControl({ mode: '' })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span>Default Mode</span>
                              {!selectedSessionMetadata?.mode && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                            {availableModes.map((m) => (
                              <button
                                key={m.id}
                                type="button"
                                onClick={() => handleUpdateSessionControl({ mode: m.id })}
                                className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                              >
                                <span>{m.label}</span>
                                {selectedSessionMetadata?.mode === m.id && <Check className="w-3 h-3 text-sky-400" />}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {/* Context Size Selector Badge & Popover */}
                    {(selectedSessionMetadata?.contextSize || sessionAvailableContextSizes.length > 0) && (
                      <div className="relative inline-block">
                        <button
                          type="button"
                          onClick={() => setEditingSessionControl((prev) => (prev === 'contextSize' ? null : 'contextSize'))}
                          className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800/80 hover:bg-slate-700/90 text-slate-300 border border-slate-700/80 hover:border-slate-600 transition cursor-pointer"
                          title="Click to change context window size"
                        >
                          <Maximize2 className="w-3 h-3 text-emerald-400 shrink-0" />
                          <span>{selectedSessionMetadata?.contextSize || 'context'}</span>
                          <ChevronDown className="w-2.5 h-2.5 text-slate-400 shrink-0" />
                        </button>
                        {editingSessionControl === 'contextSize' && (
                          <div className="absolute left-0 top-full mt-1.5 z-40 w-48 bg-slate-900 border border-slate-700 rounded-xl shadow-2xl p-1.5 max-h-60 overflow-y-auto">
                            <div className="px-2 py-1 text-[10px] font-semibold text-slate-400 uppercase tracking-wider font-mono">
                              Context Window Size
                            </div>
                            <button
                              type="button"
                              onClick={() => handleUpdateSessionControl({ contextSize: '' })}
                              className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                            >
                              <span>Default Context</span>
                              {!selectedSessionMetadata?.contextSize && <Check className="w-3 h-3 text-sky-400" />}
                            </button>
                            {sessionAvailableContextSizes.map((cs) => (
                              <button
                                key={cs.id}
                                type="button"
                                onClick={() => handleUpdateSessionControl({ contextSize: cs.id })}
                                className="w-full text-left px-2.5 py-1.5 rounded-lg text-xs hover:bg-slate-800 text-slate-300 hover:text-white flex items-center justify-between cursor-pointer"
                              >
                                <span className="truncate">{cs.label}</span>
                                {selectedSessionMetadata?.contextSize === cs.id && <Check className="w-3 h-3 text-sky-400" />}
                              </button>
                            ))}
                          </div>
                        )}
                      </div>
                    )}

                    {selectedSessionMetadata?.cumulativeUsage?.totalTokens ? (
                      <span
                        className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-sky-500/10 text-sky-300 border border-sky-500/30"
                        title={`In: ${selectedSessionMetadata.cumulativeUsage.inputTokens || 0} | Out: ${selectedSessionMetadata.cumulativeUsage.outputTokens || 0} | Think: ${selectedSessionMetadata.cumulativeUsage.thinkingTokens || 0}`}
                      >
                        <Zap className="w-3 h-3 text-sky-400 shrink-0" />
                        <span>
                          {selectedSessionMetadata.cumulativeUsage.totalTokens.toLocaleString()} tokens
                          {selectedSessionMetadata.cumulativeUsage.totalDurationSeconds
                            ? ` (${selectedSessionMetadata.cumulativeUsage.totalDurationSeconds.toFixed(1)}s)`
                            : ''}
                        </span>
                      </span>
                    ) : null}
                  </div>
                </div>

                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={handleToggleAutoScroll}
                    className={`px-3 py-1.5 rounded-lg text-xs font-medium flex items-center gap-1.5 transition cursor-pointer border ${
                      autoScrollEnabled
                        ? 'bg-sky-600/20 text-sky-200 border-sky-500/40 hover:bg-sky-600/30 shadow-sm'
                        : 'bg-slate-800 hover:bg-slate-700 text-slate-400 border-slate-700'
                    }`}
                    title={autoScrollEnabled ? 'Auto-scroll is ON (tracking new events)' : 'Auto-scroll is OFF'}
                    aria-label={`Toggle Auto-scroll (currently ${autoScrollEnabled ? 'On' : 'Off'})`}
                  >
                    <ArrowDownToLine className={`w-3.5 h-3.5 ${autoScrollEnabled ? 'text-sky-400' : 'text-slate-400'}`} />
                    <span>Auto-scroll: {autoScrollEnabled ? 'On' : 'Off'}</span>
                  </button>

                  <button
                    type="button"
                    onClick={() => setShowArtifactsPanel((prev) => !prev)}
                    className={`px-3 py-1.5 rounded-lg text-xs font-medium flex items-center gap-1.5 transition cursor-pointer border ${
                      showArtifactsPanel
                        ? 'bg-indigo-600/30 text-indigo-200 border-indigo-500/50 shadow-sm'
                        : 'bg-slate-800 hover:bg-slate-700 text-slate-300 border-slate-700'
                    }`}
                    title="Toggle Artifacts viewer panel"
                  >
                    <FileText className="w-3.5 h-3.5 text-indigo-400" />
                    <span>Artifacts</span>
                    {artifacts.length > 0 && (
                      <span className="px-1.5 py-0.2 rounded-full text-[10px] font-bold bg-indigo-500/20 text-indigo-300 border border-indigo-500/30">
                        {artifacts.length}
                      </span>
                    )}
                  </button>

                  {selectedAgent && (
                    <button
                      onClick={() => setIsCreatingSession(true)}
                      className="px-3 py-1.5 rounded-lg text-xs font-medium bg-slate-800 hover:bg-slate-700 text-slate-200 flex items-center gap-1.5 transition cursor-pointer"
                    >
                      <Plus className="w-3.5 h-3.5" />
                      <span>New Session</span>
                    </button>
                  )}
                </div>
              </div>

              {/* Split Body: Left Chat Stream + Right Artifacts Panel */}
              <div className="flex-1 flex min-h-0 overflow-hidden relative">
                <div
                  className="flex-1 flex flex-col min-w-0 min-h-0 relative"
                  onDragOver={(e) => {
                    e.preventDefault();
                    setIsDraggingChat(true);
                  }}
                  onDragLeave={(e) => {
                    e.preventDefault();
                    setIsDraggingChat(false);
                  }}
                  onDrop={(e) => {
                    e.preventDefault();
                    setIsDraggingChat(false);
                    if (e.dataTransfer?.files && e.dataTransfer.files.length > 0) {
                      void stageFilesForComposer(e.dataTransfer.files, 'chat');
                    }
                  }}
                >
                  {isDraggingChat && (
                    <div
                      className="absolute inset-3 z-40 rounded-2xl bg-sky-950/85 border-2 border-dashed border-sky-400 flex flex-col items-center justify-center text-sky-200 pointer-events-none"
                      data-testid="chat-dropzone-overlay"
                    >
                      <Paperclip className="w-10 h-10 mb-2 text-sky-400 animate-bounce" />
                      <span className="text-sm font-semibold">Drop images or files to attach to this session</span>
                      <span className="text-xs text-sky-300/80 mt-1">Up to 10 files (max 25 MB each)</span>
                    </div>
                  )}
                  {/* Trajectory Events Stream */}
                  <div
                    ref={handleTimelineContainerRef}
                    onScroll={handleTimelineScroll}
                    className="flex-1 overflow-y-auto p-6 space-y-4"
                    data-testid="session-timeline"
                  >
                {events.length === 0 ? (
                  <div className="h-full flex flex-col items-center justify-center text-center text-slate-500 space-y-2">
                    <Activity className="w-8 h-8 text-slate-700" />
                    <p className="text-sm font-medium text-slate-400">Session initialized</p>
                    <p className="text-xs max-w-sm">
                      Send a prompt below or stream events from your CLI agent to see live reasoning, tool calls, and outputs.
                    </p>
                  </div>
                ) : (() => {
                  const renderSessionResponseText = (raw: string) => {
                    if (!raw.includes('```mermaid')) {
                      return (
                        <div className="text-sm whitespace-pre-wrap leading-relaxed text-slate-200">
                          {raw}
                        </div>
                      );
                    }
                    const parts: React.ReactNode[] = [];
                    const lines = raw.split('\n');
                    let inMermaid = false;
                    let mermaidBuffer: string[] = [];
                    let textBuffer: string[] = [];

                    const flushText = (idx: number) => {
                      if (textBuffer.length > 0) {
                        parts.push(
                          <div key={`text-${idx}`} className="text-sm whitespace-pre-wrap leading-relaxed text-slate-200">
                            {textBuffer.join('\n')}
                          </div>
                        );
                        textBuffer = [];
                      }
                    };

                    for (let i = 0; i < lines.length; i++) {
                      const line = lines[i];
                      if (line.trim().startsWith('```mermaid')) {
                        flushText(i);
                        inMermaid = true;
                        mermaidBuffer = [];
                        continue;
                      }
                      if (inMermaid) {
                        if (line.trim().startsWith('```')) {
                          inMermaid = false;
                          parts.push(
                            <MermaidViewer key={`mermaid-${i}`} code={mermaidBuffer.join('\n')} />
                          );
                          mermaidBuffer = [];
                        } else {
                          mermaidBuffer.push(line);
                        }
                        continue;
                      }
                      textBuffer.push(line);
                    }
                    flushText(lines.length);
                    if (inMermaid && mermaidBuffer.length > 0) {
                      parts.push(
                        <MermaidViewer key="mermaid-end" code={mermaidBuffer.join('\n')} />
                      );
                    }
                    return <div className="space-y-2">{parts}</div>;
                  };

                  const renderQuestionCard = (
                    cardKey: string,
                    targetId: string,
                    eventCreatedAt: string,
                    parsedQuestions: AskQuestionItem[]
                  ) => {
                    const matchingAnswer = events.find((res) => {
                      if (res.eventType !== 'tool_result' || res.toolName !== 'ask_question') return false;
                      if (res.structuredPayloadJson) {
                        try {
                          const p = JSON.parse(res.structuredPayloadJson);
                          if (p.toolCallId === targetId) return true;
                        } catch {}
                      }
                      return new Date(res.createdAt).getTime() > new Date(eventCreatedAt).getTime();
                    });

                    const curAnswers = questionAnswers[targetId] || { selectedOptions: {}, customTexts: {} };
                    const hasAnyAnswerInput = parsedQuestions.some((_, idx) => {
                      const opts = curAnswers.selectedOptions[idx] || [];
                      const text = (curAnswers.customTexts[idx] || '').trim();
                      return opts.length > 0 || text.length > 0;
                    });

                    if (matchingAnswer) {
                      return (
                        <div
                          key={`ans-${cardKey}`}
                          className="bg-emerald-950/15 border border-emerald-500/30 rounded-2xl p-5 shadow-sm space-y-3"
                        >
                          <div className="flex items-center justify-between pb-2.5 border-b border-emerald-500/20">
                            <div className="flex items-center gap-2 text-xs font-semibold text-emerald-300">
                              <CheckCircle2 className="w-4 h-4 text-emerald-400 shrink-0" />
                              <span>Agent Question — Answered</span>
                            </div>
                            <span className="text-[11px] font-mono text-slate-500">
                              {new Date(matchingAnswer.createdAt || eventCreatedAt).toLocaleTimeString()}
                            </span>
                          </div>

                          <div className="space-y-3">
                            {parsedQuestions.map((q, idx) => (
                              <div key={idx} className="space-y-1.5">
                                <div className="text-xs font-medium text-slate-200 flex items-center gap-1.5 flex-wrap">
                                  {q.header && (
                                    <span className="text-[10px] uppercase font-bold tracking-wider px-1.5 py-0.5 rounded bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-mono shrink-0">
                                      {q.header}
                                    </span>
                                  )}
                                  <span>{q.question}</span>
                                </div>
                                <div className="text-xs text-emerald-300 font-mono bg-emerald-950/40 border border-emerald-500/30 rounded-lg px-3 py-2 whitespace-pre-wrap">
                                  {matchingAnswer.content || 'Answer submitted'}
                                </div>
                              </div>
                            ))}
                          </div>
                        </div>
                      );
                    }

                    return (
                      <div
                        key={`q-${cardKey}`}
                        className="bg-amber-950/20 border border-amber-500/40 rounded-2xl p-5 shadow-lg space-y-4"
                      >
                        <div className="flex items-center justify-between pb-3 border-b border-amber-500/20">
                          <div className="flex items-center gap-2">
                            <HelpCircle className="w-5 h-5 text-amber-400 shrink-0 animate-pulse" />
                            <div>
                              <span className="text-xs font-bold text-amber-200 uppercase tracking-wider block">
                                Agent Question — Human Input Required
                              </span>
                              <span className="text-[11px] text-slate-400">
                                The model paused to request operator direction before proceeding
                              </span>
                            </div>
                          </div>
                          <span className="text-[10px] font-mono uppercase px-2 py-0.5 rounded-full bg-amber-500/20 text-amber-300 border border-amber-500/40 animate-pulse">
                            Pending Input
                          </span>
                        </div>

                        <div className="space-y-4">
                          {parsedQuestions.map((q, idx) => {
                            const selectedOpts = curAnswers.selectedOptions[idx] || [];
                            const customText = curAnswers.customTexts[idx] || '';
                            const hasDescriptions = q.options && q.options.some((opt) => Boolean(getOptionDescription(opt)));

                            return (
                              <div key={idx} className="space-y-2.5">
                                <div className="text-xs font-semibold text-slate-100 flex items-center gap-1.5 flex-wrap">
                                  <span className="w-5 h-5 rounded-full bg-amber-500/20 text-amber-300 border border-amber-500/30 inline-flex items-center justify-center text-[10px] shrink-0 font-mono">
                                    {idx + 1}
                                  </span>
                                  {q.header && (
                                    <span className="text-[10px] uppercase font-bold tracking-wider px-2 py-0.5 rounded bg-amber-500/20 text-amber-300 border border-amber-500/30 font-mono shrink-0">
                                      {q.header}
                                    </span>
                                  )}
                                  <span>{q.question}</span>
                                  {q.is_multi_select && (
                                    <span className="text-[10px] font-normal text-slate-400 font-mono">
                                      (multi-select)
                                    </span>
                                  )}
                                </div>

                                {q.options && q.options.length > 0 && (
                                  <div className={`pt-1 ${hasDescriptions ? 'flex flex-col gap-2' : 'flex flex-wrap gap-2'}`}>
                                    {q.options.map((opt, optIdx) => {
                                      const optLabel = getOptionLabel(opt);
                                      const optDesc = getOptionDescription(opt);
                                      const isSelected = selectedOpts.includes(optLabel);
                                      const optKey = `${optLabel}-${optIdx}`;

                                      return (
                                        <button
                                          key={optKey}
                                          type="button"
                                          onClick={() => {
                                            setQuestionAnswers((prev) => {
                                              const cur = prev[targetId] || { selectedOptions: {}, customTexts: {} };
                                              const existing = cur.selectedOptions[idx] || [];
                                              let nextSelected: string[];
                                              let nextCustom = cur.customTexts;
                                              if (q.is_multi_select) {
                                                nextSelected = existing.includes(optLabel)
                                                  ? existing.filter((x) => x !== optLabel)
                                                  : [...existing, optLabel];
                                              } else {
                                                nextSelected = existing.includes(optLabel) ? [] : [optLabel];
                                                nextCustom = { ...cur.customTexts, [idx]: '' };
                                              }
                                              return {
                                                ...prev,
                                                [targetId]: {
                                                  selectedOptions: {
                                                    ...cur.selectedOptions,
                                                    [idx]: nextSelected
                                                  },
                                                  customTexts: nextCustom
                                                }
                                              };
                                            });
                                          }}
                                          className={`rounded-xl text-xs font-medium border transition cursor-pointer flex text-left ${
                                            optDesc ? 'p-3 items-start gap-2.5 w-full' : 'px-3 py-1.5 items-center gap-1.5'
                                          } ${
                                            isSelected
                                              ? 'bg-sky-500/25 text-sky-200 border-sky-400 font-semibold shadow-sm'
                                              : 'bg-slate-900/80 text-slate-300 border-slate-700/80 hover:bg-slate-800 hover:text-slate-100'
                                          }`}
                                        >
                                          <div className={optDesc ? 'pt-0.5 shrink-0' : 'shrink-0'}>
                                            {isSelected ? (
                                              <Check className="w-3.5 h-3.5 text-sky-400" />
                                            ) : (
                                              <span className="w-3.5 h-3.5 rounded-full border border-slate-600 inline-block" />
                                            )}
                                          </div>
                                          <div className="flex-1 min-w-0">
                                            <span className={optDesc ? 'font-semibold block text-slate-100' : ''}>{optLabel}</span>
                                            {optDesc && (
                                              <span className="block text-[11px] text-slate-400 mt-1 leading-relaxed font-normal">
                                                {optDesc}
                                              </span>
                                            )}
                                          </div>
                                        </button>
                                      );
                                    })}
                                  </div>
                                )}

                                <div className="pt-1">
                                  <input
                                    type="text"
                                    value={customText}
                                    onChange={(e) => {
                                      const val = e.target.value;
                                      setQuestionAnswers((prev) => {
                                        const cur = prev[targetId] || { selectedOptions: {}, customTexts: {} };
                                        const nextSelected = (!q.is_multi_select && val.trim().length > 0)
                                          ? { ...cur.selectedOptions, [idx]: [] }
                                          : cur.selectedOptions;

                                        return {
                                          ...prev,
                                          [targetId]: {
                                            selectedOptions: nextSelected,
                                            customTexts: {
                                              ...cur.customTexts,
                                              [idx]: val
                                            }
                                          }
                                        };
                                      });
                                    }}
                                    placeholder={
                                      q.options && q.options.length > 0
                                        ? "Or type a custom write-in response..."
                                        : "Type your response to this question..."
                                    }
                                    className="w-full bg-slate-900/90 border border-slate-700/80 rounded-xl px-3 py-2 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-sky-500 focus:ring-1 focus:ring-sky-500 transition"
                                  />
                                </div>
                              </div>
                            );
                          })}
                        </div>

                        <div className="flex items-center justify-end gap-2 pt-2 border-t border-amber-500/20">
                          <button
                            type="button"
                            disabled={!hasAnyAnswerInput || submittingQuestionId === targetId}
                            onClick={() => handleSubmitAnswers(targetId, parsedQuestions)}
                            className="px-4 py-2 rounded-xl text-xs font-semibold bg-sky-600 hover:bg-sky-500 disabled:opacity-50 disabled:cursor-not-allowed text-white transition flex items-center gap-1.5 shadow-md cursor-pointer"
                          >
                            {submittingQuestionId === targetId ? (
                              <>
                                <RefreshCw className="w-3.5 h-3.5 animate-spin" />
                                <span>Submitting...</span>
                              </>
                            ) : (
                              <>
                                <Send className="w-3.5 h-3.5" />
                                <span>Submit Answer</span>
                              </>
                            )}
                          </button>
                        </div>
                      </div>
                    );
                  };

                  return events.map((ev) => {
                    const type = ev.eventType || 'response';

                    if (type === 'system') {
                      return (
                        <div key={ev.id} className="flex justify-start">
                          <div className="max-w-2xl bg-slate-900/90 border border-slate-700/60 rounded-xl px-4 py-3 text-slate-200 shadow-md">
                            <div className="flex items-center justify-between gap-4 text-[11px] font-mono text-sky-400 mb-1.5 pb-1 border-b border-slate-800">
                              <div className="flex items-center gap-1.5">
                                <Terminal className="w-3.5 h-3.5 text-sky-400 shrink-0" />
                                <span className="font-semibold uppercase tracking-wider">Mesh System</span>
                              </div>
                              <span className="text-slate-500">{new Date(ev.createdAt).toLocaleTimeString()}</span>
                            </div>
                            <div className="text-xs text-slate-300 font-mono leading-relaxed whitespace-pre-wrap">
                              {ev.content}
                            </div>
                          </div>
                        </div>
                      );
                    }

                    if (type === 'prompt') {
                      const msgAttachments = extractMessageAttachments(ev);
                      return (
                        <div key={ev.id} className="flex justify-end">
                          <div className="max-w-2xl bg-sky-600/15 border border-sky-500/30 rounded-2xl rounded-tr-sm px-4 py-3 text-slate-100 space-y-2">
                            <div className="flex items-center justify-between gap-4 text-[11px] font-mono text-sky-400 mb-1">
                              <span>Prompt ({ev.senderAgentId})</span>
                              <span>{new Date(ev.createdAt).toLocaleTimeString()}</span>
                            </div>
                            {ev.content && (
                              <p className="text-sm whitespace-pre-wrap leading-relaxed">{ev.content}</p>
                            )}
                            {msgAttachments.length > 0 && (
                              <div className="flex flex-wrap gap-2 pt-1" data-testid={`message-attachments-${ev.id}`}>
                                {msgAttachments.map((att) => {
                                  const isImg = isImageAttachment(att.contentType, att.fileName);
                                  const targetId = att.id || att.fileName;
                                  const streamUrl = att.downloadUrl || (targetId ? `/api/sessions/${ev.sessionId}/attachments/${encodeURIComponent(targetId)}/content` : '');
                                  if (isImg && streamUrl) {
                                    return (
                                      <button
                                        key={att.id || att.fileName}
                                        type="button"
                                        onClick={() =>
                                          setLightboxAttachment({
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
                                          className="max-h-40 max-w-[220px] object-cover block"
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
                                      <FileIcon className="w-4 h-4 text-sky-400 shrink-0" />
                                      <div className="min-w-0">
                                        <div className="font-mono text-xs text-white truncate max-w-[180px]">{att.fileName || 'Attachment'}</div>
                                        <div className="text-[10px] text-slate-400">{formatAttachmentSize(att.sizeBytes)}</div>
                                      </div>
                                      <Download className="w-3.5 h-3.5 text-sky-400 shrink-0 ml-1" />
                                    </a>
                                  );
                                })}
                              </div>
                            )}
                          </div>
                        </div>
                      );
                    }

                    if (type === 'thought') {
                      return (
                        <details
                          key={ev.id}
                          open
                          className="group bg-indigo-950/20 border border-indigo-500/25 rounded-xl overflow-hidden"
                        >
                          <summary className="px-4 py-2.5 cursor-pointer flex items-center justify-between text-xs font-medium text-indigo-300 hover:bg-indigo-950/30 select-none">
                            <div className="flex items-center gap-2">
                              <Brain className="w-4 h-4 text-indigo-400 shrink-0" />
                              <span>Agent Reasoning / Thought</span>
                              <span className="text-[11px] font-mono text-indigo-400/70">
                                @{ev.senderAgentId}
                              </span>
                            </div>
                            <span className="text-[11px] font-mono text-slate-500">
                              {new Date(ev.createdAt).toLocaleTimeString()}
                            </span>
                          </summary>
                          <div className="px-4 pb-3 pt-1 text-xs text-slate-300 font-mono leading-relaxed whitespace-pre-wrap border-t border-indigo-500/10">
                            {ev.content}
                          </div>
                        </details>
                      );
                    }

                    if (type === 'tool_call') {
                      if (ev.toolName === 'ask_question') {
                        let parsedQuestions: AskQuestionItem[] = [];
                        if (ev.toolArgsJson) {
                          try {
                            const raw = JSON.parse(ev.toolArgsJson);
                            if (Array.isArray(raw.questions)) {
                              parsedQuestions = raw.questions;
                            } else if (typeof raw.questions === 'string') {
                              try {
                                parsedQuestions = JSON.parse(raw.questions);
                              } catch {}
                            } else if (raw.question) {
                              parsedQuestions = [{
                                question: raw.question,
                                options: Array.isArray(raw.options) ? raw.options : [],
                                is_multi_select: !!raw.is_multi_select
                              }];
                            }
                          } catch {}
                        }

                        return renderQuestionCard(ev.id, ev.id, ev.createdAt, parsedQuestions);
                      }

                      return (
                        <details
                          key={ev.id}
                          className="bg-amber-950/15 border border-amber-500/25 rounded-xl overflow-hidden"
                        >
                          <summary className="px-4 py-2.5 cursor-pointer flex items-center justify-between text-xs font-medium text-amber-300 hover:bg-amber-950/25 select-none">
                            <div className="flex items-center gap-2">
                              <Wrench className="w-4 h-4 text-amber-400 shrink-0" />
                              <span className="font-mono font-bold text-amber-200">
                                {ev.toolName || 'tool_call'}
                              </span>
                              <span className="text-slate-300">{ev.content}</span>
                            </div>
                            <span className="text-[11px] font-mono text-slate-500">
                              {new Date(ev.createdAt).toLocaleTimeString()}
                            </span>
                          </summary>
                          {ev.toolArgsJson && (
                            <div className="px-4 py-2.5 bg-slate-950/80 border-t border-amber-500/15">
                              <div className="text-[10px] font-mono uppercase text-slate-500 mb-1">
                                Arguments
                              </div>
                              <pre className="text-xs font-mono text-amber-200/90 overflow-x-auto">
                                {ev.toolArgsJson}
                              </pre>
                            </div>
                          )}
                        </details>
                      );
                    }

                    if (type === 'tool_result') {
                      if (ev.toolName === 'ask_question') {
                        // Suppress duplicate tool_result; already rendered in Question card
                        return null;
                      }

                      return (
                        <details
                          key={ev.id}
                          className="bg-slate-900/70 border border-slate-800 rounded-xl overflow-hidden"
                        >
                          <summary className="px-4 py-2 cursor-pointer flex items-center justify-between text-xs text-slate-300 hover:bg-slate-900 select-none">
                            <div className="flex items-center gap-2">
                              <Terminal className="w-3.5 h-3.5 text-emerald-400 shrink-0" />
                              <span className="font-mono text-emerald-400">
                                {ev.toolName ? `${ev.toolName} →` : 'Tool Output'}
                              </span>
                              <span className="text-slate-400">{ev.content}</span>
                            </div>
                            <span className="text-[11px] font-mono text-slate-500">
                              {new Date(ev.createdAt).toLocaleTimeString()}
                            </span>
                          </summary>
                          {ev.toolResultJson && (
                            <div className="px-4 py-2.5 bg-slate-950 border-t border-slate-800">
                              <pre className="text-xs font-mono text-slate-300 overflow-x-auto whitespace-pre-wrap">
                                {ev.toolResultJson}
                              </pre>
                            </div>
                          )}
                        </details>
                      );
                    }

                    // Default: 'response'
                    let usage: {
                      inputTokens?: number;
                      outputTokens?: number;
                      thinkingTokens?: number;
                      totalTokens?: number;
                      durationSeconds?: number;
                    } | null = null;
                    let responseModel: string | null = null;
                    let responseEffort: string | null = null;
                    if (ev.structuredPayloadJson) {
                      try {
                        const parsed = JSON.parse(ev.structuredPayloadJson);
                        if (parsed.usage) usage = parsed.usage;
                        if (parsed.model) responseModel = parsed.model;
                        if (parsed.effort) responseEffort = parsed.effort;
                      } catch {}
                    }

                    const textQuestions = parseTextQuestions(ev.content);
                    const hasFollowupToolCall = events.some(
                      (e) =>
                        e.eventType === 'tool_call' &&
                        e.toolName === 'ask_question' &&
                        new Date(e.createdAt).getTime() >= new Date(ev.createdAt).getTime()
                    );
                    const isLatestEvent = events[events.length - 1]?.id === ev.id;
                    const isWaitingOrExplicit =
                      selectedSession?.status === 'waiting_for_input' ||
                      /```(?:json:)?ask_question/i.test(ev.content);
                    const shouldRenderTextQuestions =
                      textQuestions.length > 0 &&
                      !hasFollowupToolCall &&
                      isLatestEvent &&
                      isWaitingOrExplicit;

                    const cleanedContent = ev.content
                      .replace(/\(?Note:?\s*I can't see your replies in this non-interactive session\.?.*?(\)|$)/gi, '')
                      .trim();

                    return (
                      <div key={ev.id} className="space-y-3">
                        <div className="bg-slate-900/90 border border-slate-800 rounded-2xl rounded-tl-sm px-5 py-4 text-slate-100 shadow-lg">
                          <div className="flex items-center justify-between text-xs font-mono text-emerald-400 mb-2">
                            <div className="flex items-center gap-2">
                              <CheckCircle2 className="w-4 h-4" />
                              <span className="font-bold">@{ev.senderAgentId}</span>
                              {responseModel && (
                                <span
                                  className="inline-flex items-center gap-1 text-[11px] font-mono px-2 py-0.5 rounded bg-purple-500/15 text-purple-300 border border-purple-500/30"
                                  title={`Executed with model: ${responseModel}${responseEffort ? ` (${responseEffort} effort)` : ''}`}
                                >
                                  <Cpu className="w-3 h-3 text-purple-400 shrink-0" />
                                  <span>{responseModel}</span>
                                </span>
                              )}
                            </div>
                            <div className="flex items-center gap-2.5">
                              {usage && (
                                <span
                                  className="text-[11px] font-mono px-2 py-0.5 rounded bg-sky-500/10 text-sky-300 border border-sky-500/25"
                                  title={`In: ${usage.inputTokens || 0} | Out: ${usage.outputTokens || 0} | Think: ${usage.thinkingTokens || 0}`}
                                >
                                  ⚡ {(usage.totalTokens || ((usage.inputTokens || 0) + (usage.outputTokens || 0))).toLocaleString()} tokens
                                  {usage.durationSeconds ? ` (${usage.durationSeconds.toFixed(1)}s)` : ''}
                                </span>
                              )}
                              <span className="text-slate-500">
                                {new Date(ev.createdAt).toLocaleTimeString()}
                              </span>
                            </div>
                          </div>
                          {renderSessionResponseText(cleanedContent || ev.content)}
                        </div>

                        {shouldRenderTextQuestions && (
                          renderQuestionCard(`text-q-${ev.id}`, ev.id, ev.createdAt, textQuestions)
                        )}
                      </div>
                    );
                  });
                })()}

                {isSessionThinking && (
                  selectedAgent?.status === 'offline' ? (
                    <div className="flex flex-col gap-1.5 px-4 py-3 rounded-xl bg-amber-950/30 border border-amber-500/30 text-xs text-amber-200 max-w-xl">
                      <div className="flex items-center gap-2 font-semibold text-amber-300">
                        <AlertTriangle className="w-4 h-4 text-amber-400 shrink-0" />
                        <span>Agent @{selectedAgent.id} is offline (waiting for CLI connection)</span>
                      </div>
                      <p className="text-slate-300 leading-relaxed">
                        Connect the agent runner from your terminal to process this prompt:
                      </p>
                      <code className="bg-slate-950 border border-slate-800 rounded px-2.5 py-1.5 font-mono text-sky-300 select-all">
                        agentmesh start {selectedAgent.id}
                      </code>
                    </div>
                  ) : (
                    <div className="flex items-center gap-2.5 px-4 py-2.5 rounded-xl bg-indigo-950/20 border border-indigo-500/20 text-xs text-indigo-300 w-fit">
                      <RefreshCw className="w-3.5 h-3.5 animate-spin text-indigo-400" />
                      <span>Agent is reasoning...</span>
                    </div>
                  )
                )}

              </div>

              {/* Floating Jump to bottom button when scrolled up */}
              {isScrolledUp && (
                <div className="absolute bottom-24 right-6 z-30 pointer-events-auto">
                  <button
                    type="button"
                    onClick={handleScrollToBottom}
                    className="px-3.5 py-1.5 rounded-full text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white shadow-xl border border-sky-400/40 flex items-center gap-1.5 transition-all duration-150 cursor-pointer active:scale-95"
                    title="Jump to latest message and resume following"
                    aria-label="Jump to bottom"
                  >
                    <ArrowDown className="w-3.5 h-3.5 animate-bounce" />
                    <span>Jump to bottom</span>
                  </button>
                </div>
              )}

              {/* Bottom Prompt Bar */}
              <form
                onSubmit={handleSendPrompt}
                className="p-4 border-t border-slate-800/80 bg-slate-900/50 shrink-0 space-y-2.5 relative z-20"
              >
                {/* Active Command Pill */}
                {activeCommand && (
                  <div className="flex items-center justify-between px-3 py-1.5 rounded-xl bg-amber-950/30 border border-amber-500/30 text-xs text-amber-200">
                    <div className="flex items-center gap-2 min-w-0">
                      <Terminal className="w-3.5 h-3.5 text-amber-400 shrink-0 animate-pulse" />
                      <span className="font-mono text-amber-300 font-semibold shrink-0">Running command:</span>
                      <span className="font-mono truncate text-amber-100">{activeCommand.commandText}</span>
                    </div>
                    <button
                      type="button"
                      onClick={() => handleStopCommand(activeCommand.id)}
                      title="Kill this running command"
                      className="ml-3 px-2 py-0.5 rounded bg-rose-600/20 hover:bg-rose-600/40 border border-rose-500/40 text-rose-300 hover:text-rose-200 text-[11px] font-semibold flex items-center gap-1 shrink-0 transition cursor-pointer"
                    >
                      <Square className="w-2.5 h-2.5 fill-current" />
                      <span>Kill Command</span>
                    </button>
                  </div>
                )}

                {/* Queued Prompts Strip */}
                {activeQueue.length > 0 && (
                  <div className="flex flex-col gap-1.5 p-2.5 rounded-xl bg-slate-950/70 border border-slate-800/90 text-xs">
                    <div className="flex items-center justify-between text-slate-400 font-medium px-1">
                      <div className="flex items-center gap-1.5 text-sky-400">
                        <Layers className="w-3.5 h-3.5" />
                        <span>Prompt Queue ({activeQueue.length})</span>
                      </div>
                      <span className="text-[10px] text-slate-500">Auto-advances sequentially on response</span>
                    </div>
                    <div className="space-y-1 max-h-32 overflow-y-auto pr-1">
                      {activeQueue.map((item, idx) => (
                        <div
                          key={item.id}
                          className="flex items-center justify-between gap-2 px-2.5 py-1.5 rounded-lg bg-slate-900/80 border border-slate-800/60 text-slate-200"
                        >
                          <div className="flex items-center gap-2 min-w-0">
                            <span className="font-mono text-[10px] text-slate-400 shrink-0">#{idx + 1}</span>
                            <span className="truncate text-xs font-mono">{item.prompt}</span>
                            {item.attachments && item.attachments.length > 0 && (
                              <span
                                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-sky-500/15 text-sky-300 border border-sky-500/30 text-[10px] font-mono shrink-0"
                                title={`${item.attachments.length} attached file(s)`}
                              >
                                <Paperclip className="w-2.5 h-2.5" />
                                <span>{item.attachments.length}</span>
                              </span>
                            )}
                          </div>
                          <div className="flex items-center gap-1.5 shrink-0">
                            <button
                              type="button"
                              onClick={() => handleSendQueuedNow(item.id)}
                              title="Interrupt current turn and run this prompt immediately"
                              className="px-2 py-0.5 rounded bg-amber-500/10 hover:bg-amber-500/20 text-amber-300 border border-amber-500/30 text-[10px] font-semibold flex items-center gap-1 transition cursor-pointer"
                            >
                              <Zap className="w-2.5 h-2.5" />
                              <span>Send Now</span>
                            </button>
                            <button
                              type="button"
                              onClick={() => handleRemoveFromQueue(item.id)}
                              title="Remove from queue"
                              className="p-1 rounded hover:bg-slate-800 text-slate-400 hover:text-slate-200 transition cursor-pointer"
                            >
                              <X className="w-3 h-3" />
                            </button>
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                )}

                {/* Pending Staged Attachments Strip */}
                {stagedAttachments.length > 0 && (
                  <div
                    className="flex flex-wrap items-center gap-2 p-2.5 rounded-xl bg-slate-950/80 border border-slate-800"
                    data-testid="chat-staged-attachments"
                  >
                    {stagedAttachments.map((att) => {
                      const isImg = isImageAttachment(att.contentType, att.name);
                      const thumbSrc = att.previewUrl || att.uploaded?.downloadUrl;
                      return (
                        <div
                          key={att.localId}
                          className="flex items-center gap-2 px-2.5 py-1.5 rounded-lg bg-slate-900 border border-slate-700/80 text-xs text-slate-200 max-w-xs"
                        >
                          {isImg && thumbSrc ? (
                            <img
                              src={thumbSrc}
                              alt={att.name}
                              className="w-8 h-8 rounded object-cover border border-slate-700 shrink-0"
                            />
                          ) : isImg ? (
                            <ImageIcon className="w-4 h-4 text-sky-400 shrink-0" />
                          ) : (
                            <FileIcon className="w-4 h-4 text-sky-400 shrink-0" />
                          )}
                          <div className="min-w-0 flex-1">
                            <div className="truncate font-mono text-[11px] text-white">{att.name}</div>
                            <div className="flex items-center gap-1.5 text-[10px] text-slate-400">
                              <span>{formatAttachmentSize(att.sizeBytes)}</span>
                              {att.uploading && <span className="text-amber-300 animate-pulse">Uploading...</span>}
                              {att.error && <span className="text-rose-400">Failed</span>}
                            </div>
                          </div>
                          <button
                            type="button"
                            onClick={() => handleRemoveStagedAttachment(att.localId, 'chat')}
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

                {/* Input & Control Bar */}
                <div className="flex items-center gap-2">
                  <input
                    ref={chatFileInputRef}
                    type="file"
                    multiple
                    className="hidden"
                    data-testid="chat-file-input"
                    onChange={(e) => {
                      if (e.target.files && e.target.files.length > 0) {
                        void stageFilesForComposer(e.target.files, 'chat');
                        e.target.value = '';
                      }
                    }}
                  />
                  <button
                    type="button"
                    onClick={() => chatFileInputRef.current?.click()}
                    title="Attach files (Images, PDFs, Code, Logs, ZIP — max 25 MB)"
                    aria-label="Attach files"
                    className="p-2.5 rounded-xl bg-slate-950 hover:bg-slate-800 border border-slate-800 hover:border-slate-700 text-slate-300 hover:text-sky-300 transition cursor-pointer shrink-0"
                  >
                    <Paperclip className="w-4 h-4" />
                  </button>

                  <div className="relative flex-1">
                    {/* Slash Command Autocomplete Palette */}
                    <SlashCommandPalette
                      query={promptInput}
                      isOpen={slashPaletteOpen}
                      highlightedIndex={slashPaletteIndex}
                      onSelect={handleSelectSlashCommand}
                      onClose={() => setSlashPaletteOpen(false)}
                    />

                    <input
                      type="text"
                      placeholder={
                        isSessionThinking
                          ? `Add instruction to queue for ${selectedAgent?.name || selectedSession.creatorAgentId || 'agent'} (or type / for commands)...`
                          : `Send instruction to ${selectedAgent?.name || selectedSession.creatorAgentId || 'agent'} (or type / for commands)...`
                      }
                      value={promptInput}
                      onChange={(e) => handlePromptChange(e.target.value)}
                      onPaste={handleChatPaste}
                      onFocus={() => {
                        if (promptInput.startsWith('/') || promptInput.startsWith('\\')) {
                          setSlashPaletteOpen(true);
                        }
                      }}
                      onKeyDown={handlePromptKeyDown}
                      className="w-full bg-slate-950 border border-slate-800 rounded-xl px-4 py-2.5 text-sm text-white placeholder-slate-500 focus:outline-none focus:border-sky-500"
                    />
                  </div>

                  {/* Stop Button: beside the input bar, enabled when thinking or waiting_for_input */}
                  {(() => {
                    const isStoppable = isSessionThinking || selectedSession.status === 'waiting_for_input';
                    return (
                      <button
                        type="button"
                        onClick={handleStopSession}
                        disabled={!isStoppable || isActionPending}
                        title={
                          isStoppable
                            ? 'Stop model execution (Interrupt)'
                            : 'Model is idle'
                        }
                        className={`px-3.5 py-2.5 rounded-xl text-xs font-semibold flex items-center gap-1.5 transition ${
                          isStoppable
                            ? 'bg-rose-600/90 hover:bg-rose-500 text-white shadow-lg shadow-rose-950/40 cursor-pointer animate-pulse'
                            : 'bg-slate-800/30 text-slate-600 border border-slate-800/50 cursor-not-allowed opacity-50'
                        }`}
                      >
                        <Square className="w-3.5 h-3.5 fill-current" />
                        <span>Stop</span>
                      </button>
                    );
                  })()}

                  {/* Dynamic Send / Queue Split Button */}
                  {isSessionThinking ? (
                    <div className="relative flex items-center">
                      <button
                        type="submit"
                        disabled={!canSendPrompt}
                        title="Add prompt to sequential execution queue"
                        className="px-3.5 py-2.5 rounded-l-xl bg-amber-600 hover:bg-amber-500 disabled:opacity-40 text-white font-semibold text-xs flex items-center gap-1.5 transition cursor-pointer"
                      >
                        <ListPlus className="w-3.5 h-3.5" />
                        <span>Queue</span>
                      </button>
                      <button
                        type="button"
                        onClick={() => setShowSendNowDropdown((prev) => !prev)}
                        disabled={!canSendPrompt}
                        title="More sending options"
                        className="px-2 py-2.5 rounded-r-xl bg-amber-700 hover:bg-amber-600 border-l border-amber-800 disabled:opacity-40 text-white transition cursor-pointer"
                      >
                        <ChevronDown className="w-3.5 h-3.5" />
                      </button>

                      {showSendNowDropdown && canSendPrompt && (
                        <div className="absolute right-0 bottom-full mb-2 w-56 rounded-xl bg-slate-900 border border-slate-800 shadow-2xl p-1.5 z-50 text-xs">
                          <button
                            type="button"
                            onClick={handleSendPromptNow}
                            className="w-full text-left px-3 py-2 rounded-lg hover:bg-slate-800 text-amber-300 font-semibold flex items-center gap-2 cursor-pointer transition"
                          >
                            <Zap className="w-3.5 h-3.5 text-amber-400" />
                            <div>
                              <div>Send Now (Interrupt)</div>
                              <div className="text-[10px] text-slate-400 font-normal">
                                Stops current turn & runs immediately
                              </div>
                            </div>
                          </button>
                        </div>
                      )}
                    </div>
                  ) : (
                    <button
                      type="submit"
                      disabled={!canSendPrompt}
                      className="px-4 py-2.5 rounded-xl bg-sky-600 hover:bg-sky-500 disabled:opacity-40 text-white font-semibold text-xs flex items-center gap-1.5 transition cursor-pointer"
                    >
                      <Send className="w-3.5 h-3.5" />
                      <span>Send</span>
                    </button>
                  )}
                </div>

                <div className="flex items-center justify-between text-[11px] text-slate-400 px-1">
                  <span className="font-mono text-slate-500">
                    Agent: @{selectedSession.creatorAgentId || selectedAgent?.id || 'agy'}
                  </span>
                  <span className="font-mono text-slate-500">
                    Session ID: {selectedSession.id.slice(0, 8)}
                  </span>
                </div>
              </form>
            </div>

            {/* Resizable Split Divider */}
            {showArtifactsPanel && (
              <div
                onMouseDown={() => setIsResizingArtifacts(true)}
                className="w-1.5 hover:w-2 bg-slate-800 hover:bg-indigo-500 transition-colors cursor-col-resize select-none shrink-0"
                title="Drag to resize artifacts panel"
              />
            )}

            {/* Right: Resizable Artifacts Split Panel */}
            {showArtifactsPanel && (
              <div
                style={{ width: `${artifactPanelWidth}px` }}
                className="shrink-0 flex flex-col min-h-0 bg-slate-950 border-l border-slate-800 overflow-hidden"
              >
                <ArtifactViewer
                  artifacts={artifacts}
                  selectedArtifactId={selectedArtifactId}
                  onSelectArtifact={(id) => setSelectedArtifactId(id)}
                  onClose={() => setShowArtifactsPanel(false)}
                  onRefresh={() => selectedSessionId && fetchSessionArtifacts(selectedSessionId)}
                />
              </div>
            )}
          </div>
        </>
          ) : selectedSessionId ? (
            <div className="flex-1 flex flex-col items-center justify-center p-8 text-center space-y-4">
              <div className="w-14 h-14 rounded-2xl bg-slate-900 border border-slate-800 flex items-center justify-center text-sky-400">
                <RefreshCw className="w-7 h-7 animate-spin text-sky-400" />
              </div>
              <div className="max-w-md space-y-1.5">
                <h2 className="text-lg font-bold text-white">Loading Session Trajectory...</h2>
                <p className="text-xs text-slate-400 font-mono">
                  Loading telemetry for session <span className="text-sky-300">{selectedSessionId.slice(0, 8)}...</span>
                </p>
              </div>
            </div>
          ) : (
            <div className="flex-1 flex flex-col items-center justify-center p-8 text-center space-y-4">
              <div className="w-14 h-14 rounded-2xl bg-slate-900 border border-slate-800 flex items-center justify-center text-sky-400">
                <Bot className="w-7 h-7" />
              </div>
              <div className="max-w-md space-y-1.5">
                <h2 className="text-lg font-bold text-white">Agent Session Observatory</h2>
                <p className="text-xs text-slate-400 leading-relaxed">
                  Select a connected agent on the left to inspect its sessions, or connect a live CLI agent to stream reasoning steps and tool executions.
                </p>
              </div>
              <div className="flex items-center gap-3">
                {activeTab === 'personas' && selectedPersona ? (
                  <button
                    onClick={() => startNewSessionWithPersona(selectedPersona)}
                    className="px-4 py-2 rounded-xl bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center gap-2 transition cursor-pointer"
                  >
                    <Plus className="w-4 h-4" />
                    <span>New Session with {selectedPersona.name}</span>
                  </button>
                ) : selectedAgent ? (
                  <button
                    onClick={() => setIsCreatingSession(true)}
                    className="px-4 py-2 rounded-xl bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center gap-2 transition cursor-pointer"
                  >
                    <Plus className="w-4 h-4" />
                    <span>New Session with {selectedAgent.name}</span>
                  </button>
                ) : (
                  <>
                    <button
                      onClick={() => openCreatePersonaModal()}
                      className="px-4 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-500 text-white font-semibold text-xs flex items-center gap-2 transition cursor-pointer"
                    >
                      <Plus className="w-4 h-4" />
                      <span>Create Persona</span>
                    </button>
                    <button
                      onClick={() => setShowRegisterModal(true)}
                      className="px-4 py-2 rounded-xl bg-sky-600 hover:bg-sky-500 text-white font-semibold text-xs flex items-center gap-2 transition cursor-pointer"
                    >
                      <Plus className="w-4 h-4" />
                      <span>Register Agent</span>
                    </button>
                  </>
                )}
              </div>
            </div>
          )}
        </main>
      </div>

      {/* Interactive Folder Picker Modal */}
      {showFolderPicker && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label="Select Working Directory"
          className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-center justify-center p-4"
        >
          <div className="max-w-2xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl flex flex-col max-h-[85vh] overflow-hidden">
            {/* Header */}
            <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
              <div className="flex items-center gap-2.5">
                <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
                  <FolderOpen className="w-4 h-4" />
                </div>
                <div>
                  <h3 className="text-sm font-bold text-white">Select Working Directory</h3>
                  <p className="text-xs text-slate-400">Browse host filesystem for isolated session execution</p>
                </div>
              </div>
              <button
                type="button"
                onClick={() => setShowFolderPicker(false)}
                className="p-1 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            {/* Navigation & Breadcrumbs Bar */}
            <div className="px-5 py-2.5 bg-slate-950/60 border-b border-slate-800/80 flex items-center gap-2 text-xs">
              <button
                type="button"
                disabled={!browseData?.parentPath || isBrowsing}
                onClick={() => browseData?.parentPath && loadBrowseDirectory(browseData.parentPath)}
                className="p-1.5 rounded-md bg-slate-800 hover:bg-slate-700 text-slate-300 disabled:opacity-40 disabled:cursor-not-allowed transition cursor-pointer"
                title="Go up to parent folder"
              >
                <ChevronUp className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                disabled={isBrowsing}
                onClick={() => capabilities?.workspace.userHome && loadBrowseDirectory(capabilities.workspace.userHome)}
                className="p-1.5 rounded-md bg-slate-800 hover:bg-slate-700 text-slate-300 disabled:opacity-40 transition cursor-pointer"
                title="Go to User Home"
              >
                <Home className="w-3.5 h-3.5" />
              </button>
              <button
                type="button"
                disabled={isBrowsing}
                onClick={() => loadBrowseDirectory(browseData?.currentPath)}
                className="p-1.5 rounded-md bg-slate-800 hover:bg-slate-700 text-slate-300 disabled:opacity-40 transition cursor-pointer"
                title="Refresh current folder"
              >
                <RefreshCw className={`w-3.5 h-3.5 ${isBrowsing ? 'animate-spin' : ''}`} />
              </button>

              <div className="h-4 w-px bg-slate-800 mx-1" />

              {/* Breadcrumb Path */}
              <div className="flex-1 min-w-0 flex items-center gap-1 overflow-x-auto font-mono text-[11px] text-slate-300 py-0.5">
                {browseData?.currentPath ? (
                  browseData.currentPath
                    .split('/')
                    .map((segment, idx, arr) => {
                      if (!segment && idx === 0) {
                        return (
                          <button
                            key="root"
                            type="button"
                            onClick={() => loadBrowseDirectory('/')}
                            className="hover:text-sky-400 font-bold px-1 rounded hover:bg-slate-800/50 cursor-pointer"
                          >
                            /
                          </button>
                        );
                      }
                      if (!segment) return null;
                      const subPath = arr.slice(0, idx + 1).join('/') || '/';
                      const isLast = idx === arr.length - 1;
                      return (
                        <div key={subPath} className="flex items-center gap-1 shrink-0">
                          <span className="text-slate-600">/</span>
                          <button
                            type="button"
                            onClick={() => loadBrowseDirectory(subPath)}
                            className={`px-1 py-0.5 rounded transition cursor-pointer ${
                              isLast
                                ? 'text-sky-400 font-bold bg-sky-950/40 border border-sky-500/30'
                                : 'hover:text-white hover:bg-slate-800/60 text-slate-300'
                            }`}
                          >
                            {segment}
                          </button>
                        </div>
                      );
                    })
                ) : (
                  <span className="text-slate-500 italic">Reading directory...</span>
                )}
              </div>
            </div>

            {/* Quick Preset Location Chips in Picker */}
            {capabilities?.workspace?.bookmarks && (
              <div className="px-5 py-2 bg-slate-900/50 border-b border-slate-800/50 flex flex-wrap items-center gap-1.5 text-[11px]">
                <span className="text-slate-500 text-[10px] uppercase font-mono tracking-wider mr-1">Bookmarks:</span>
                <button
                  type="button"
                  onClick={() => {
                    setNewSessionWorkDir('');
                    setShowFolderPicker(false);
                  }}
                  className="px-2 py-0.5 rounded bg-emerald-500/10 text-emerald-300 border border-emerald-500/30 hover:bg-emerald-500/20 transition cursor-pointer"
                >
                  ⚡ Sandbox Default
                </button>
                {capabilities.workspace.bookmarks
                  .filter((b) => b.path)
                  .map((bookmark) => (
                    <button
                      key={bookmark.label}
                      type="button"
                      onClick={() => loadBrowseDirectory(bookmark.path)}
                      className={`px-2 py-0.5 rounded border transition cursor-pointer ${
                        browseData?.currentPath === bookmark.path
                          ? 'bg-sky-500/20 text-sky-300 border-sky-500/40 font-semibold'
                          : 'bg-slate-800 text-slate-300 border-slate-700 hover:bg-slate-700'
                      }`}
                    >
                      {bookmark.label}
                    </button>
                  ))}
              </div>
            )}

            {/* Directory Listing Body */}
            <div className="flex-1 overflow-y-auto p-4 space-y-1">
              {isBrowsing ? (
                <div className="py-12 flex flex-col items-center justify-center text-slate-500 text-xs gap-2">
                  <RefreshCw className="w-5 h-5 animate-spin text-sky-400" />
                  <span>Loading directory contents...</span>
                </div>
              ) : browseError ? (
                <div className="p-4 rounded-xl bg-rose-950/30 border border-rose-500/30 text-rose-300 text-xs flex items-center justify-between">
                  <span>{browseError}</span>
                  <button
                    type="button"
                    onClick={() => loadBrowseDirectory(capabilities?.workspace.currentDirectory)}
                    className="px-2.5 py-1 rounded bg-rose-900/50 hover:bg-rose-900 text-rose-200 border border-rose-500/40 text-[11px] cursor-pointer"
                  >
                    Return to Project
                  </button>
                </div>
              ) : browseData?.directories.length === 0 ? (
                <div className="py-10 text-center text-slate-500 text-xs italic">
                  No accessible subdirectories in this folder
                </div>
              ) : (
                browseData?.directories.map((dir) => (
                  <div
                    key={dir.path}
                    className="group flex items-center justify-between p-2 rounded-lg hover:bg-slate-800/60 border border-transparent hover:border-slate-800 transition"
                  >
                    <button
                      type="button"
                      onClick={() => loadBrowseDirectory(dir.path)}
                      className="flex-1 flex items-center gap-2.5 text-left text-xs text-slate-200 group-hover:text-sky-300 font-mono transition cursor-pointer"
                    >
                      <Folder className="w-4 h-4 text-sky-400 shrink-0" />
                      <span className="truncate">{dir.name}</span>
                    </button>
                    <button
                      type="button"
                      onClick={() => {
                        setNewSessionWorkDir(dir.path);
                        setShowFolderPicker(false);
                      }}
                      className="px-2.5 py-1 rounded bg-slate-800 hover:bg-sky-600 text-slate-300 hover:text-white text-[11px] font-medium opacity-80 group-hover:opacity-100 transition cursor-pointer"
                    >
                      Select
                    </button>
                  </div>
                ))
              )}
            </div>

            {/* Footer */}
            <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-between shrink-0">
              <div className="min-w-0 pr-3">
                <span className="text-[10px] text-slate-500 uppercase font-mono block">Current Folder</span>
                <p className="text-xs font-mono text-slate-300 truncate" title={browseData?.currentPath}>
                  {browseData?.currentPath || 'None'}
                </p>
              </div>
              <div className="flex items-center gap-2 shrink-0">
                <button
                  type="button"
                  onClick={() => setShowFolderPicker(false)}
                  className="px-3 py-1.5 rounded-lg text-xs font-medium text-slate-400 hover:text-white transition cursor-pointer"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  disabled={!browseData?.currentPath}
                  onClick={() => {
                    if (browseData?.currentPath) {
                      setNewSessionWorkDir(browseData.currentPath);
                      setShowFolderPicker(false);
                    }
                  }}
                  className="px-4 py-1.5 rounded-lg text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white flex items-center gap-1.5 transition disabled:opacity-40 cursor-pointer"
                >
                  <Check className="w-3.5 h-3.5" />
                  <span>Choose This Folder</span>
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Agent Quota & Usage Details Modal */}
      {showUsageModal && selectedUsageAgent && (() => {
        const usage = agentUsages[selectedUsageAgent.id];
        return (
          <div
            role="dialog"
            aria-modal="true"
            aria-label="Agent Quota and Usage Details"
            className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-center justify-center p-4"
          >
            <div className="max-w-2xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl flex flex-col max-h-[85vh] overflow-hidden">
              {/* Header */}
              <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
                <div className="flex items-center gap-2.5">
                  <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
                    <BarChart2 className="w-4 h-4" />
                  </div>
                  <div>
                    <h3 className="text-sm font-bold text-white flex items-center gap-2">
                      <span>{selectedUsageAgent.name}</span>
                      <span className="text-[10px] font-mono text-slate-400 font-normal">
                        @{selectedUsageAgent.id}
                      </span>
                    </h3>
                    <p className="text-xs text-slate-400">
                      Lifetime session token metrics & real-time provider quotas
                    </p>
                  </div>
                </div>
                <div className="flex items-center gap-1.5">
                  <button
                    type="button"
                    disabled={isRefreshingUsage}
                    onClick={() => handleRefreshUsage(selectedUsageAgent.id)}
                    className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer disabled:opacity-50"
                    title="Refresh live provider quotas"
                    aria-label="Refresh quota"
                  >
                    <RefreshCw className={`w-4 h-4 ${isRefreshingUsage ? 'animate-spin text-sky-400' : ''}`} />
                  </button>
                  <button
                    type="button"
                    onClick={() => setShowUsageModal(false)}
                    className="p-1 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                  >
                    <X className="w-4 h-4" />
                  </button>
                </div>
              </div>

              {/* Modal Body */}
              <div className="flex-1 overflow-y-auto p-5 space-y-5">
                {/* Lifetime Usage Cards */}
                <div>
                  <div className="flex items-center justify-between mb-2.5">
                    <span className="text-xs font-semibold text-slate-300 uppercase tracking-wider font-mono">
                      Lifetime Token Consumption
                    </span>
                    <span className="text-[11px] font-mono text-slate-400">
                      {usage?.sessionCount || 0} sessions · {(usage?.lifetimeTokens?.totalDurationSeconds || 0).toFixed(1)}s processing
                    </span>
                  </div>
                  <div className="grid grid-cols-2 sm:grid-cols-4 gap-2.5">
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Total Tokens</span>
                      <span className="text-base font-bold text-white font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.totalTokens || 0).toLocaleString()}
                      </span>
                    </div>
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Prompt / Input</span>
                      <span className="text-base font-bold text-sky-300 font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.inputTokens || 0).toLocaleString()}
                      </span>
                    </div>
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Completion / Out</span>
                      <span className="text-base font-bold text-emerald-300 font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.outputTokens || 0).toLocaleString()}
                      </span>
                    </div>
                    <div className="bg-slate-950/80 border border-slate-800/80 rounded-xl p-3">
                      <span className="text-[10px] font-mono text-slate-500 uppercase block">Reasoning / Think</span>
                      <span className="text-base font-bold text-purple-300 font-mono mt-0.5 block">
                        {(usage?.lifetimeTokens?.thinkingTokens || 0).toLocaleString()}
                      </span>
                    </div>
                  </div>
                </div>

                {/* Real-time Provider Quota Meters */}
                <div>
                  <div className="flex items-center justify-between mb-2.5">
                    <span className="text-xs font-semibold text-slate-300 uppercase tracking-wider font-mono">
                      Provider Quota & Rate Limits
                    </span>
                    {usage?.providerQuota && (
                      <span className="text-[11px] font-mono px-2 py-0.5 rounded bg-sky-500/10 text-sky-300 border border-sky-500/30">
                        {usage.providerQuota.provider.toUpperCase()} · {usage.providerQuota.planName}
                      </span>
                    )}
                  </div>

                  {usage?.providerQuota ? (
                    <div className="space-y-4 bg-slate-950/60 border border-slate-800 rounded-xl p-4">
                      {/* Primary Overall Meter */}
                      <div>
                        <div className="flex items-center justify-between text-xs font-mono mb-1.5">
                          <span className="text-slate-300 font-semibold">Primary Quota Availability</span>
                          <span
                            className={`font-bold ${
                              usage.providerQuota.primaryRemainingFraction > 0.4
                                ? 'text-emerald-400'
                                : usage.providerQuota.primaryRemainingFraction > 0.15
                                ? 'text-amber-400'
                                : 'text-rose-400'
                            }`}
                          >
                            {(usage.providerQuota.primaryRemainingFraction * 100).toFixed(1)}% Remaining
                          </span>
                        </div>
                        <div className="h-2.5 w-full bg-slate-800 rounded-full overflow-hidden">
                          <div
                            className={`h-full rounded-full transition-all duration-500 ${
                              usage.providerQuota.primaryRemainingFraction > 0.4
                                ? 'bg-emerald-500'
                                : usage.providerQuota.primaryRemainingFraction > 0.15
                                ? 'bg-amber-500'
                                : 'bg-rose-500'
                            }`}
                            style={{
                              width: `${Math.min(100, Math.max(0, usage.providerQuota.primaryRemainingFraction * 100))}%`
                            }}
                          />
                        </div>
                      </div>

                      {/* Quota Groups & Individual Buckets */}
                      <div className="space-y-3 pt-2 border-t border-slate-800/80">
                        {usage.providerQuota.groups.map((group) => (
                          <div key={group.name} className="space-y-2">
                            <div className="text-[11px] font-mono text-slate-400 font-medium">
                              {group.name}
                            </div>
                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
                              {group.buckets.map((bucket) => {
                                const pct = Math.round(bucket.remainingFraction * 100);
                                return (
                                  <div
                                    key={bucket.id}
                                    className="bg-slate-900/90 border border-slate-800 rounded-lg p-2.5 space-y-1.5"
                                  >
                                    <div className="flex items-center justify-between text-xs font-mono">
                                      <span className="font-semibold text-slate-200 truncate" title={bucket.id}>
                                        {bucket.name || bucket.id}
                                      </span>
                                      {bucket.window && (
                                        <span className="text-[10px] px-1.5 py-0.2 rounded bg-slate-800 text-slate-400 border border-slate-700 shrink-0">
                                          {bucket.window}
                                        </span>
                                      )}
                                    </div>
                                    <div className="flex items-center justify-between text-[11px] font-mono text-slate-400">
                                      <span>Remaining</span>
                                      <span
                                        className={`font-semibold ${
                                          pct > 40
                                            ? 'text-emerald-400'
                                            : pct > 15
                                            ? 'text-amber-400'
                                            : 'text-rose-400'
                                        }`}
                                      >
                                        {(bucket.remainingFraction * 100).toFixed(1)}%
                                      </span>
                                    </div>
                                    <div className="h-1.5 w-full bg-slate-800 rounded-full overflow-hidden">
                                      <div
                                        className={`h-full rounded-full transition-all duration-300 ${
                                          pct > 40
                                            ? 'bg-emerald-500'
                                            : pct > 15
                                            ? 'bg-amber-500'
                                            : 'bg-rose-500'
                                        }`}
                                        style={{ width: `${Math.min(100, Math.max(0, pct))}%` }}
                                      />
                                    </div>
                                    {bucket.resetTime && (
                                      <div className="text-[10px] font-mono text-slate-500 flex items-center gap-1 pt-0.5 truncate">
                                        <Clock className="w-2.5 h-2.5 shrink-0" />
                                        <span className="truncate">
                                          Resets {new Date(bucket.resetTime).toLocaleDateString()} {new Date(bucket.resetTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                                        </span>
                                      </div>
                                    )}
                                  </div>
                                );
                              })}
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>
                  ) : (
                    <div className="p-4 rounded-xl bg-slate-950/60 border border-slate-800 text-slate-400 text-xs space-y-1.5">
                      <div className="flex items-center gap-2 text-slate-300 font-semibold">
                        <Cpu className="w-4 h-4 text-sky-400" />
                        <span>Local Mesh Token Tracking Active</span>
                      </div>
                      <p className="leading-relaxed">
                        Real-time provider quota introspection is supported for Antigravity CLI (<code className="text-sky-300">agy</code>) via <code className="text-sky-300">/usage</code>.
                        For this agent runtime, token metrics are aggregated from session execution events.
                      </p>
                    </div>
                  )}
                </div>
              </div>

              {/* Footer */}
              <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-end shrink-0">
                <button
                  type="button"
                  onClick={() => setShowUsageModal(false)}
                  className="px-4 py-1.5 rounded-lg text-xs font-semibold bg-slate-800 hover:bg-slate-700 text-white transition cursor-pointer"
                >
                  Close
                </button>
              </div>
            </div>
          </div>
        );
      })()}

      {/* Create / Edit Persona Modal */}
      {showPersonaModal && (() => {
        const driverAgent = agents.find((a) => a.id.toLowerCase() === personaDriverInput.toLowerCase());
        let extraModels: ModelCapability[] = [];
        if (driverAgent?.capabilitiesJson) {
          try {
            const parsed = JSON.parse(driverAgent.capabilitiesJson);
            if (Array.isArray(parsed.models)) {
              extraModels = parsed.models.map((m: any) => ({
                id: m.id || m.name,
                name: m.name || m.id,
                efforts: ['low', 'medium', 'high'],
                contextSizes: ['32k', '128k']
              }));
            }
          } catch {}
        }
        const baseDriverModels = personaModalRuntimeCaps?.models || [];
        const driverModels = [
          ...extraModels,
          ...baseDriverModels.filter((m) => !extraModels.some((em) => em.id === m.id))
        ];
        const driverModes = personaModalRuntimeCaps?.modes || [];
        const driverTiers = personaModalRuntimeCaps?.tiers || [];
        const driverContextSizes = personaModalRuntimeCaps?.contextSizes || [];
        const selectedModelObj = driverModels.find((m) => m.id === personaDefaultModel);
        const driverEfforts = selectedModelObj?.efforts || [];
        const modelContextIds = selectedModelObj?.contextSizes;
        const availablePersonaContextSizes = (modelContextIds && modelContextIds.length > 0)
          ? modelContextIds.map((id) => {
              const match = driverContextSizes.find((c) => c.id.toLowerCase() === id.toLowerCase());
              return match || { id, label: id, description: `${id} context window` };
            })
          : driverContextSizes;

        const driverOptions = Array.from(
          new Map(
            [
              { id: 'agy', name: 'Antigravity CLI (agy)' },
              { id: 'copilot', name: 'GitHub Copilot CLI (copilot)' },
              { id: 'claude', name: 'Claude Code CLI (claude)' },
              ...agents.map((a) => ({ id: a.id, name: `${a.name} (${a.id})` }))
            ].map((item) => [item.id, item])
          ).values()
        );

        return (
          <div
            role="dialog"
            aria-modal="true"
            aria-label={editingPersona ? 'Edit Persona' : 'Create Persona'}
            className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-center justify-center p-4"
          >
            <form
              onSubmit={handleSavePersona}
              className="max-w-xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl flex flex-col max-h-[90vh] overflow-hidden"
            >
              {/* Header */}
              <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
                <div className="flex items-center gap-2.5">
                  <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
                    <Users className="w-4 h-4" />
                  </div>
                  <div>
                    <h3 className="text-sm font-bold text-white">
                      {editingPersona ? `Edit Persona: ${editingPersona.name}` : 'Create New Persona'}
                    </h3>
                    <p className="text-xs text-slate-400">
                      Configure specialized agent identity, target Driver, and default model
                    </p>
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => setShowPersonaModal(false)}
                  className="p-1 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>

              {/* Body */}
              <div className="flex-1 overflow-y-auto p-5 space-y-4 text-xs">
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  <div className="space-y-1">
                    <label className="block font-semibold text-slate-300">Persona Name</label>
                    <input
                      type="text"
                      placeholder="e.g. Agy Security Reviewer"
                      value={personaNameInput}
                      onChange={(e) => setPersonaNameInput(e.target.value)}
                      className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-sky-500"
                      required
                    />
                  </div>
                  <div className="space-y-1">
                    <label className="block font-semibold text-slate-300">
                      Handle / ID {editingPersona ? '(Fixed)' : '(Optional)'}
                    </label>
                    <input
                      type="text"
                      placeholder="e.g. agy-sec-reviewer"
                      value={personaIdInput}
                      onChange={(e) => setPersonaIdInput(e.target.value)}
                      disabled={!!editingPersona}
                      className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500 disabled:opacity-50"
                    />
                  </div>
                </div>

                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  <div className="space-y-1">
                    <label className="block font-semibold text-slate-300">Role Title</label>
                    <input
                      type="text"
                      placeholder="e.g. Principal Security Architect"
                      value={personaRoleInput}
                      onChange={(e) => setPersonaRoleInput(e.target.value)}
                      className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-sky-500"
                    />
                  </div>
                  <div className="space-y-1">
                    <label className="block font-semibold text-slate-300">Execution Driver</label>
                    <select
                      aria-label="Execution Driver"
                      value={personaDriverInput}
                      onChange={(e) => {
                        setPersonaDriverInput(e.target.value);
                        setPersonaDefaultModel('');
                        setPersonaDefaultEffort('');
                        setPersonaDefaultContextSize('');
                      }}
                      className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-sky-500 cursor-pointer"
                    >
                      {driverOptions.map((d) => (
                        <option key={d.id} value={d.id}>
                          {d.name}
                        </option>
                      ))}
                    </select>
                  </div>
                </div>

                <div className="space-y-1">
                  <label className="block font-semibold text-slate-300">
                    System Instructions / Persona Prompt
                  </label>
                  <textarea
                    rows={3}
                    placeholder="Define specialized behavior, architectural constraints, or review criteria for this Persona..."
                    value={personaPromptInput}
                    onChange={(e) => setPersonaPromptInput(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-sky-500"
                  />
                </div>

                {/* Default Model & Execution Preferences */}
                <div className="pt-2 border-t border-slate-800 space-y-3">
                  <div className="flex items-center justify-between">
                    <span className="font-semibold text-slate-300">
                      Default Driver Model & Execution Settings
                    </span>
                    <span className="text-[10px] font-mono text-slate-400">
                      All Driver models remain accessible per session
                    </span>
                  </div>

                  <div className="grid grid-cols-1 sm:grid-cols-3 gap-2.5">
                    <div className="space-y-1">
                      <label className="block text-[11px] text-slate-400">Default Model</label>
                      <select
                        aria-label="Persona Default Model"
                        value={personaDefaultModel}
                        onChange={(e) => {
                          const newModel = e.target.value;
                          setPersonaDefaultModel(newModel);
                          const newModelObj = driverModels.find((m) => m.id === newModel);
                          if (newModelObj?.contextSizes && personaDefaultContextSize && !newModelObj.contextSizes.some((cs) => cs.toLowerCase() === personaDefaultContextSize.toLowerCase())) {
                            setPersonaDefaultContextSize('');
                          }
                        }}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1.5 text-white focus:outline-none focus:border-sky-500 cursor-pointer"
                      >
                        <option value="">Driver Default</option>
                        {driverModels.map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.name || m.id}
                          </option>
                        ))}
                      </select>
                      {personaModalRuntimeCaps?.adapter === 'openai' && (
                        <input
                          type="text"
                          aria-label="Custom Persona Model Input"
                          placeholder="Or custom model name..."
                          value={personaDefaultModel}
                          onChange={(e) => setPersonaDefaultModel(e.target.value)}
                          className="w-full mt-1 bg-slate-950 border border-slate-800 rounded-lg px-2 py-1 text-slate-200 text-[11px] font-mono focus:outline-none focus:border-sky-500"
                        />
                      )}
                    </div>

                    <div className="space-y-1">
                      <label className="block text-[11px] text-slate-400">Default Effort</label>
                      <select
                        aria-label="Persona Default Effort"
                        value={personaDefaultEffort}
                        onChange={(e) => setPersonaDefaultEffort(e.target.value)}
                        disabled={driverEfforts.length === 0}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1.5 text-white focus:outline-none focus:border-sky-500 disabled:opacity-40 cursor-pointer"
                      >
                        <option value="">Default Effort</option>
                        {driverEfforts.map((eff) => (
                          <option key={eff} value={eff}>
                            {eff}
                          </option>
                        ))}
                      </select>
                    </div>

                    <div className="space-y-1">
                      <label className="block text-[11px] text-slate-400">Default Mode</label>
                      <select
                        aria-label="Persona Default Mode"
                        value={personaDefaultMode}
                        onChange={(e) => setPersonaDefaultMode(e.target.value)}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1.5 text-white focus:outline-none focus:border-sky-500 cursor-pointer"
                      >
                        <option value="">Default Mode</option>
                        {driverModes.map((mode) => (
                          <option key={mode.id} value={mode.id}>
                            {mode.label}
                          </option>
                        ))}
                      </select>
                    </div>
                  </div>

                  <div className={`grid ${driverTiers.length > 0 ? 'grid-cols-1 sm:grid-cols-2' : 'grid-cols-1'} gap-2.5`}>
                    <div className="space-y-1">
                      <label className="block text-[11px] text-slate-400">Default Context Window Size</label>
                      <select
                        aria-label="Persona Default Context Size"
                        value={personaDefaultContextSize}
                        onChange={(e) => setPersonaDefaultContextSize(e.target.value)}
                        disabled={availablePersonaContextSizes.length === 0}
                        className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1.5 text-white focus:outline-none focus:border-sky-500 disabled:opacity-40 cursor-pointer"
                      >
                        <option value="">Driver / Model Default</option>
                        {availablePersonaContextSizes.map((cs) => (
                          <option key={cs.id} value={cs.id} title={cs.description}>
                            {cs.label}
                          </option>
                        ))}
                      </select>
                    </div>

                    {driverTiers.length > 0 && (
                      <div className="space-y-1">
                        <label className="block text-[11px] text-slate-400">Default Optimization Tier</label>
                        <select
                          aria-label="Persona Default Tier"
                          value={personaDefaultTier}
                          onChange={(e) => setPersonaDefaultTier(e.target.value)}
                          className="w-full bg-slate-950 border border-slate-800 rounded-lg px-2.5 py-1.5 text-white focus:outline-none focus:border-sky-500 cursor-pointer"
                        >
                          <option value="">Default Tier</option>
                          {driverTiers.map((t) => (
                            <option key={t.id} value={t.id}>
                              {t.label}
                            </option>
                          ))}
                        </select>
                      </div>
                    )}
                  </div>

                  <div className="space-y-1">
                    <label className="block text-[11px] text-slate-400">Max Concurrent Jobs (Optional)</label>
                    <input
                      type="number"
                      min="1"
                      placeholder="Leave empty for unlimited jobs"
                      aria-label="Persona Max Concurrent Jobs"
                      value={personaMaxConcurrentJobs}
                      onChange={(e) => setPersonaMaxConcurrentJobs(e.target.value)}
                      className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-1.5 text-white font-mono focus:outline-none focus:border-sky-500"
                    />
                    <p className="text-[10px] text-slate-500">
                      Limit how many jobs this persona can actively execute turns in simultaneously. Additional turns will queue until a slot frees up.
                    </p>
                  </div>

                  <div className="space-y-1">
                    <label className="block text-[11px] text-slate-400">Default Working Directory (Optional)</label>
                    <input
                      type="text"
                      placeholder="Leave empty for isolated sandbox (~/.agentmesh/workspaces/<session-id>)"
                      value={personaDefaultWorkDir}
                      onChange={(e) => setPersonaDefaultWorkDir(e.target.value)}
                      className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-1.5 text-white font-mono focus:outline-none focus:border-sky-500"
                    />
                  </div>
                </div>
              </div>

              {/* Footer */}
              <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-end gap-2 shrink-0">
                <button
                  type="button"
                  onClick={() => setShowPersonaModal(false)}
                  className="px-3.5 py-1.5 rounded-lg text-xs font-medium text-slate-400 hover:text-white transition cursor-pointer"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={isActionPending}
                  className="px-4 py-1.5 rounded-lg text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white transition cursor-pointer"
                >
                  {editingPersona ? 'Save Changes' : 'Create Persona'}
                </button>
              </div>
            </form>
          </div>
        );
      })()}

      {/* Fullscreen Image Lightbox Modal */}
      {lightboxAttachment && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label={`Image preview: ${lightboxAttachment.fileName}`}
          data-testid="attachment-lightbox"
          onClick={() => setLightboxAttachment(null)}
          className="fixed inset-0 z-50 bg-black/85 backdrop-blur-md flex flex-col items-center justify-center p-6"
        >
          <div
            onClick={(e) => e.stopPropagation()}
            className="max-w-5xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl overflow-hidden flex flex-col max-h-[90vh]"
          >
            <div className="px-5 py-3 border-b border-slate-800 flex items-center justify-between bg-slate-950/80">
              <div className="flex items-center gap-2 min-w-0">
                <ImageIcon className="w-4 h-4 text-sky-400 shrink-0" />
                <span className="font-mono text-xs font-semibold text-white truncate">
                  {lightboxAttachment.fileName}
                </span>
                {lightboxAttachment.sizeBytes ? (
                  <span className="text-[11px] font-mono text-slate-400">
                    ({formatAttachmentSize(lightboxAttachment.sizeBytes)})
                  </span>
                ) : null}
              </div>
              <div className="flex items-center gap-2">
                <a
                  href={`${lightboxAttachment.url}${lightboxAttachment.url.includes('?') ? '&' : '?'}download=true`}
                  download={lightboxAttachment.fileName}
                  className="px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-medium flex items-center gap-1.5 transition"
                >
                  <Download className="w-3.5 h-3.5 text-sky-400" />
                  <span>Download</span>
                </a>
                <button
                  type="button"
                  onClick={() => setLightboxAttachment(null)}
                  aria-label="Close image preview"
                  className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                >
                  <X className="w-4 h-4" />
                </button>
              </div>
            </div>
            <div className="flex-1 p-6 bg-slate-950 flex items-center justify-center overflow-auto">
              <img
                src={lightboxAttachment.url}
                alt={lightboxAttachment.fileName}
                className="max-h-[75vh] max-w-full object-contain rounded-lg shadow-xl"
                data-testid="lightbox-image"
              />
            </div>
          </div>
        </div>
      )}
      {showProjectModal && (
        <ProjectModal
          isOpen={showProjectModal}
          onClose={() => setShowProjectModal(false)}
          onSave={handleSaveProject}
          editingProject={editingProject}
          availablePersonas={personas}
        />
      )}

      {showJobModal && jobModalProject && (
        <NewJobModal
          isOpen={showJobModal}
          onClose={() => setShowJobModal(false)}
          project={jobModalProject}
          onCreate={handleCreateJob}
        />
      )}

      {configAgent && (
        <DriverConfigModal
          agent={configAgent}
          initialConfig={agents.find((a) => a.id === configAgent.id)?.config ?? configAgent.config}
          saving={isSavingDriverConfig}
          onClose={() => setConfigAgent(null)}
          onSave={(patch) => handleSaveDriverConfig(configAgent.id, patch)}
        />
      )}
    </div>
  );
}
