import React, { useEffect, useRef } from 'react';
import { Terminal, Zap, Sparkles, X } from 'lucide-react';

export interface SlashCommandItem {
  command: string;
  category: 'mesh' | 'agent';
  label: string;
  description: string;
  usage: string;
  insertText?: string;
  executeImmediately?: boolean;
}

export const SLASH_COMMANDS: SlashCommandItem[] = [
  // Mesh Cockpit Actions (Cyan / Teal)
  {
    command: '/mesh stop',
    category: 'mesh',
    label: 'Stop Execution',
    description: 'Halt active model execution immediately (<500ms)',
    usage: '/mesh stop',
    executeImmediately: true
  },
  {
    command: '/mesh mode plan',
    category: 'mesh',
    label: 'Mode: Plan',
    description: 'Switch session to planning mode (read-only with artifacts)',
    usage: '/mesh mode plan',
    executeImmediately: true
  },
  {
    command: '/mesh mode auto',
    category: 'mesh',
    label: 'Mode: Auto',
    description: 'Switch session to standard autonomous execution mode',
    usage: '/mesh mode auto',
    executeImmediately: true
  },
  {
    command: '/mesh mode accept-edits',
    category: 'mesh',
    label: 'Mode: Accept Edits',
    description: 'Switch session to accept-edits mode (auto-approves changes)',
    usage: '/mesh mode accept-edits',
    executeImmediately: true
  },
  {
    command: '/mesh model',
    category: 'mesh',
    label: 'Change Model',
    description: 'Set active LLM (e.g. gemini-3.8-flash, claude-3-7-sonnet)',
    usage: '/mesh model <model-id>',
    insertText: '/mesh model '
  },
  {
    command: '/mesh effort',
    category: 'mesh',
    label: 'Reasoning Effort',
    description: 'Set reasoning effort (low, medium, high, max)',
    usage: '/mesh effort <low|medium|high|max>',
    insertText: '/mesh effort '
  },
  {
    command: '/mesh artifacts',
    category: 'mesh',
    label: 'Toggle Artifacts',
    description: 'Open or close the side-by-side Artifacts split viewer',
    usage: '/mesh artifacts',
    executeImmediately: true
  },
  {
    command: '/mesh queue',
    category: 'mesh',
    label: 'Queue Prompt',
    description: 'Add an instruction to the sequential prompt execution queue',
    usage: '/mesh queue <prompt>',
    insertText: '/mesh queue '
  },
  {
    command: '/mesh clear',
    category: 'mesh',
    label: 'Clear Filters',
    description: 'Clear event filters and reset timeline view',
    usage: '/mesh clear',
    executeImmediately: true
  },
  {
    command: '/mesh help',
    category: 'mesh',
    label: 'Command Help',
    description: 'Display quick reference guide for all slash commands',
    usage: '/mesh help',
    executeImmediately: true
  },

  // Agent Workflows & Skills (Purple / Indigo)
  {
    command: '/plan',
    category: 'agent',
    label: 'Plan Mode',
    description: 'Formulate an implementation plan artifact before execution',
    usage: '/plan <task description>',
    insertText: '/plan '
  },
  {
    command: '/grill-me',
    category: 'agent',
    label: 'Grill-Me Interview',
    description: 'Interactive requirement & design interview via question cards',
    usage: '/grill-me <topic or decision>',
    insertText: '/grill-me '
  },
  {
    command: '/goal',
    category: 'agent',
    label: 'Goal Pursuit',
    description: 'Long-running autonomous execution without premature stopping',
    usage: '/goal <high-level objective>',
    insertText: '/goal '
  },
  {
    command: '/compact',
    category: 'agent',
    label: 'Compact Context',
    description: 'Summarize history to free up agent context window',
    usage: '/compact',
    insertText: '/compact '
  },
  {
    command: '/browser',
    category: 'agent',
    label: 'Browser Automation',
    description: 'Web navigation, scraping, and browser testing tools',
    usage: '/browser <url or query>',
    insertText: '/browser '
  },
  {
    command: '/schedule',
    category: 'agent',
    label: 'Schedule Task',
    description: 'Set a one-time reminder timer or recurring cron job',
    usage: '/schedule <duration or cron> <prompt>',
    insertText: '/schedule '
  },
  {
    command: '/skills',
    category: 'agent',
    label: 'List Skills',
    description: 'Inspect available skills, subagents, and extensions',
    usage: '/skills',
    insertText: '/skills '
  },
  {
    command: '/learn',
    category: 'agent',
    label: 'Persist Learning',
    description: 'Save user preferences or patterns for future sessions',
    usage: '/learn <instruction or guideline>',
    insertText: '/learn '
  },
  {
    command: '/boost',
    category: 'agent',
    label: 'Deep Reasoning Boost',
    description: 'Execute multi-perspective deep reasoning and verification',
    usage: '/boost <complex challenge>',
    insertText: '/boost '
  },
  {
    command: '/help',
    category: 'agent',
    label: 'Agent Help',
    description: 'Request underlying agent capabilities and command reference',
    usage: '/help',
    insertText: '/help '
  }
];

interface SlashCommandPaletteProps {
  query: string;
  isOpen: boolean;
  highlightedIndex: number;
  onSelect: (command: SlashCommandItem) => void;
  onClose: () => void;
}

export const SlashCommandPalette: React.FC<SlashCommandPaletteProps> = ({
  query,
  isOpen,
  highlightedIndex,
  onSelect,
  onClose
}) => {
  const containerRef = useRef<HTMLDivElement | null>(null);

  const cleanQuery = (query.startsWith('/') || query.startsWith('\\'))
    ? query.slice(1).trim().toLowerCase()
    : query.trim().toLowerCase();

  const filteredCommands = SLASH_COMMANDS.filter((cmd) => {
    if (!cleanQuery) return true;
    const cmdBody = cmd.command.slice(1).toLowerCase();
    return (
      cmdBody.includes(cleanQuery) ||
      cmd.label.toLowerCase().includes(cleanQuery) ||
      cmd.description.toLowerCase().includes(cleanQuery)
    );
  });

  useEffect(() => {
    const handleOutsideClick = (event: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        onClose();
      }
    };
    if (isOpen) {
      document.addEventListener('mousedown', handleOutsideClick);
    }
    return () => {
      document.removeEventListener('mousedown', handleOutsideClick);
    };
  }, [isOpen, onClose]);

  useEffect(() => {
    if (containerRef.current && highlightedIndex >= 0) {
      const activeElement = containerRef.current.querySelector(`[data-index="${highlightedIndex}"]`);
      if (activeElement && typeof activeElement.scrollIntoView === 'function') {
        activeElement.scrollIntoView({ block: 'nearest' });
      }
    }
  }, [highlightedIndex]);

  if (!isOpen || filteredCommands.length === 0) return null;

  return (
    <div
      ref={containerRef}
      className="absolute left-0 bottom-full mb-2 w-full min-w-[320px] sm:min-w-[500px] max-w-xl max-h-80 overflow-y-auto rounded-xl bg-slate-900 border border-slate-700 shadow-2xl z-50 p-2 text-xs divide-y divide-slate-800"
      data-testid="slash-command-palette"
    >
      <div className="px-2 py-1 flex items-center justify-between text-[11px] font-mono text-slate-400">
        <span className="flex items-center gap-1.5 font-semibold text-slate-300">
          <Terminal className="w-3.5 h-3.5 text-sky-400" />
          <span>Slash Command Palette</span>
        </span>
        <div className="flex items-center gap-2">
          <span className="text-[10px] text-slate-500">
            {filteredCommands.length} command{filteredCommands.length === 1 ? '' : 's'} • ↑↓ navigate • Enter/Tab select
          </span>
          <button
            type="button"
            onClick={onClose}
            title="Close command palette (Esc)"
            className="p-0.5 rounded text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
          >
            <X className="w-3.5 h-3.5" />
          </button>
        </div>
      </div>

      <div className="pt-1.5 space-y-1">
        {filteredCommands.map((cmd, idx) => {
          const isSelected = idx === highlightedIndex;
          const isMesh = cmd.category === 'mesh';

          return (
            <div
              key={cmd.command}
              data-index={idx}
              onMouseDown={(e) => {
                e.preventDefault();
                onSelect(cmd);
              }}
              onClick={() => onSelect(cmd)}
              className={`px-2.5 py-2 rounded-lg cursor-pointer transition flex items-start justify-between gap-3 ${
                isSelected
                  ? isMesh
                    ? 'bg-cyan-950/70 border border-cyan-500/50 text-white shadow-sm'
                    : 'bg-indigo-950/70 border border-indigo-500/50 text-white shadow-sm'
                  : 'hover:bg-slate-800/80 text-slate-300 border border-transparent'
              }`}
            >
              <div className="flex items-start gap-2.5 min-w-0">
                <div
                  className={`p-1.5 rounded-md shrink-0 mt-0.5 ${
                    isMesh
                      ? 'bg-cyan-500/20 text-cyan-300 border border-cyan-500/30'
                      : 'bg-indigo-500/20 text-indigo-300 border border-indigo-500/30'
                  }`}
                >
                  {isMesh ? (
                    <Zap className="w-3 h-3 text-cyan-400" />
                  ) : (
                    <Sparkles className="w-3 h-3 text-indigo-400" />
                  )}
                </div>

                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="font-mono font-bold text-xs text-white">{cmd.command}</span>
                    <span className="text-[11px] text-slate-400 font-medium">{cmd.label}</span>
                  </div>
                  <div className="text-[11px] text-slate-400 truncate mt-0.5">{cmd.description}</div>
                </div>
              </div>

              <div className="shrink-0 flex items-center gap-1.5 self-center">
                <span
                  className={`px-1.5 py-0.5 rounded text-[10px] font-mono font-semibold uppercase ${
                    isMesh
                      ? 'bg-cyan-500/10 text-cyan-300 border border-cyan-500/20'
                      : 'bg-indigo-500/10 text-indigo-300 border border-indigo-500/20'
                  }`}
                >
                  {isMesh ? 'Cockpit' : 'Workflow'}
                </span>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};
