import React, { useState, useRef, useEffect } from 'react';
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
  Check,
  Briefcase
} from 'lucide-react';
import type { JobMessage, JobTask, Project, ProjectJob } from '../types';
import { ProjectJobStatus } from '../types';

interface JobCockpitProps {
  project: Project;
  job: ProjectJob;
  tasks: JobTask[];
  messages: JobMessage[];
  onSendMessage: (content: string) => Promise<void>;
  onAddTask: (title: string) => Promise<void>;
  onToggleTask: (taskId: string, isCompleted: boolean) => Promise<void>;
  onDeleteTask: (taskId: string) => Promise<void>;
  onCompleteJob: (resultSummary?: string) => Promise<void>;
  onSelectTurnSession?: (sessionId: string) => void;
}

export const JobCockpit: React.FC<JobCockpitProps> = ({
  project,
  job,
  tasks,
  messages,
  onSendMessage,
  onAddTask,
  onToggleTask,
  onDeleteTask,
  onCompleteJob,
  onSelectTurnSession
}) => {
  const [prompt, setPrompt] = useState('');
  const [newTaskTitle, setNewTaskTitle] = useState('');
  const [isSending, setIsSending] = useState(false);
  const [isAddingTask, setIsAddingTask] = useState(false);
  const [showCompleteConfirm, setShowCompleteConfirm] = useState(false);
  const [resultSummary, setResultSummary] = useState('');
  const [isCompleting, setIsCompleting] = useState(false);

  const messagesEndRef = useRef<HTMLDivElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    if (typeof messagesEndRef.current?.scrollIntoView === 'function') {
      messagesEndRef.current.scrollIntoView({ behavior: 'smooth' });
    }
  }, [messages]);

  const handleSend = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    if (!prompt.trim() || isSending) return;
    const text = prompt.trim();
    setPrompt('');
    setIsSending(true);
    try {
      await onSendMessage(text);
    } finally {
      setIsSending(false);
    }
  };

  const handleAddTaskSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newTaskTitle.trim() || isAddingTask) return;
    const t = newTaskTitle.trim();
    setNewTaskTitle('');
    setIsAddingTask(true);
    try {
      await onAddTask(t);
    } finally {
      setIsAddingTask(false);
    }
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

  const completedCount = tasks.filter((t) => t.isCompleted).length;
  const progressPercent = tasks.length > 0 ? Math.round((completedCount / tasks.length) * 100) : 0;
  const isCompleted = job.status === ProjectJobStatus.Completed;

  return (
    <div className="flex-1 flex flex-col h-full bg-slate-950 overflow-hidden">
      {/* Header Bar */}
      <div className="px-6 py-4 border-b border-slate-800/80 bg-slate-900/60 backdrop-blur-sm flex items-center justify-between gap-4 shrink-0">
        <div className="flex items-center gap-3 min-w-0">
          <div className="w-9 h-9 rounded-xl bg-sky-500/15 text-sky-400 border border-sky-500/30 flex items-center justify-center shrink-0">
            <Briefcase className="w-4 h-4" />
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <span className="text-[11px] font-mono px-2 py-0.5 rounded-md bg-slate-800 text-slate-300 font-medium">
                {project.name}
              </span>
              {project.workspaceType === 0 && job.gitBranchName && (
                <span className="text-[11px] font-mono px-2 py-0.5 rounded-md bg-emerald-500/10 text-emerald-400 border border-emerald-500/30 flex items-center gap-1 font-medium">
                  <GitBranch className="w-3 h-3" />
                  {job.gitBranchName}
                </span>
              )}
              {isCompleted ? (
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-emerald-500/20 text-emerald-300 border border-emerald-500/40 flex items-center gap-1">
                  <Check className="w-3 h-3" /> Completed
                </span>
              ) : (
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-md bg-sky-500/20 text-sky-300 border border-sky-500/40">
                  Active Job
                </span>
              )}
            </div>
            <h1 className="text-base font-bold text-slate-100 truncate mt-0.5">
              {job.title}
            </h1>
          </div>
        </div>

        {/* Assigned Personas & Actions */}
        <div className="flex items-center gap-3 shrink-0">
          <div className="hidden lg:flex items-center gap-1 bg-slate-950/80 border border-slate-800 px-2.5 py-1.5 rounded-xl">
            <span className="text-[11px] font-semibold text-slate-400 mr-1.5">Team:</span>
            {project.assignedPersonas?.map((p) => (
              <span
                key={p.id}
                title={`${p.name} (${p.role})`}
                className="text-[11px] font-medium px-2 py-0.5 rounded-md bg-slate-800/80 text-sky-300 border border-slate-700/60"
              >
                @{p.id}
              </span>
            ))}
          </div>

          {!isCompleted && (
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
          )}
        </div>
      </div>

      {/* Task Checklist Bar */}
      <div className="px-6 py-3 border-b border-slate-800/60 bg-slate-900/40">
        <div className="flex items-center justify-between mb-2">
          <div className="flex items-center gap-2">
            <span className="text-xs font-semibold text-slate-300">Tasks Checklist</span>
            <span className="text-[11px] font-mono text-slate-400">
              ({completedCount}/{tasks.length} completed)
            </span>
          </div>
          <div className="w-40 h-1.5 bg-slate-800 rounded-full overflow-hidden">
            <div
              className="h-full bg-emerald-500 transition-all duration-300 rounded-full"
              style={{ width: `${progressPercent}%` }}
            />
          </div>
        </div>

        {/* Task Items */}
        <div className="flex flex-wrap items-center gap-2">
          {tasks.map((task) => (
            <div
              key={task.id}
              className={`flex items-center gap-2 px-2.5 py-1 rounded-lg border text-xs transition ${
                task.isCompleted
                  ? 'bg-emerald-500/10 border-emerald-500/30 text-emerald-300 line-through'
                  : 'bg-slate-950/80 border-slate-800 text-slate-200'
              }`}
            >
              <button
                type="button"
                onClick={() => onToggleTask(task.id, !task.isCompleted)}
                className="cursor-pointer"
              >
                {task.isCompleted ? (
                  <CheckCircle2 className="w-3.5 h-3.5 text-emerald-400" />
                ) : (
                  <Circle className="w-3.5 h-3.5 text-slate-500 hover:text-slate-300" />
                )}
              </button>
              <span>{task.title}</span>
              <button
                type="button"
                onClick={() => onDeleteTask(task.id)}
                className="text-slate-600 hover:text-red-400 transition ml-1"
                title="Delete task"
              >
                <Trash2 className="w-3 h-3" />
              </button>
            </div>
          ))}

          {/* Add Task Form */}
          <form onSubmit={handleAddTaskSubmit} className="flex items-center gap-1.5">
            <input
              type="text"
              value={newTaskTitle}
              onChange={(e) => setNewTaskTitle(e.target.value)}
              placeholder="+ Add task..."
              className="bg-slate-950/90 border border-slate-800 rounded-lg px-2.5 py-1 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-sky-500 w-36 focus:w-56 transition-all"
            />
            {newTaskTitle.trim() && (
              <button
                type="submit"
                disabled={isAddingTask}
                className="p-1 rounded-lg bg-sky-500 text-slate-950 hover:bg-sky-400 transition"
              >
                <Plus className="w-3.5 h-3.5" />
              </button>
            )}
          </form>
        </div>
      </div>

      {/* Live Chat Message Feed */}
      <div className="flex-1 overflow-y-auto p-6 space-y-4">
        {messages.length === 0 ? (
          <div className="h-full flex flex-col items-center justify-center text-center p-8 text-slate-500">
            <Bot className="w-12 h-12 stroke-[1.2] text-slate-600 mb-3" />
            <h3 className="text-sm font-semibold text-slate-300">Job Collaboration Channel Ready</h3>
            <p className="text-xs text-slate-500 max-w-sm mt-1">
              Give instructions or assign tasks to your team. Use <code className="text-sky-400 font-mono">@persona</code> to tag specific personas.
            </p>
          </div>
        ) : (
          messages.map((msg) => {
            const isUser = msg.senderType === 'user';
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

                  <div
                    className={`p-3.5 rounded-2xl text-xs leading-relaxed border whitespace-pre-wrap ${
                      isUser
                        ? 'bg-sky-600/20 border-sky-500/30 text-sky-100 text-left rounded-tr-none'
                        : 'bg-slate-900 border-slate-800 text-slate-200 rounded-tl-none'
                    }`}
                  >
                    {msg.content}
                  </div>

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
        <div ref={messagesEndRef} />
      </div>

      {/* Prompt Composer & Mention Bar */}
      <div className="p-4 border-t border-slate-800/80 bg-slate-900/80 backdrop-blur-sm shrink-0">
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
          <textarea
            ref={textareaRef}
            rows={2}
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                handleSend();
              }
            }}
            placeholder="Type your message, tasks, or delegate with @persona (Press Enter to send)..."
            disabled={isCompleted || isSending}
            className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition resize-none disabled:opacity-50"
          />
          <button
            type="submit"
            disabled={!prompt.trim() || isSending || isCompleted}
            className="p-3 bg-sky-500 hover:bg-sky-400 disabled:opacity-40 text-slate-950 rounded-xl transition cursor-pointer shrink-0 shadow-md flex items-center justify-center"
            title="Send Message"
          >
            <Send className="w-4 h-4" />
          </button>
        </form>
      </div>
    </div>
  );
};
