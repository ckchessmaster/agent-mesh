import React from 'react';
import { ShieldCheck, Cpu, Terminal, Bot } from 'lucide-react';
import type { Agent } from '../types';

export const AgentInventoryCard: React.FC<{ agent: Agent }> = ({ agent }) => {
  const tier = Number(agent.trustTier);

  const tierColor = tier >= 3
    ? 'text-purple-400 border-purple-500/30 bg-purple-500/10'
    : tier === 2
    ? 'text-sky-400 border-sky-500/30 bg-sky-500/10'
    : 'text-amber-400 border-amber-500/30 bg-amber-500/10';

  const tierLabel = tier >= 3
    ? 'Tier 3 (Lead Human)'
    : tier === 2
    ? 'Tier 2 (Senior Manager)'
    : 'Tier 1 (Intern)';

  const formatRuntime = (rt: number | string) => {
    if (typeof rt === 'string') return rt;
    switch (rt) {
      case 0: return 'Claude Code (Native)';
      case 1: return 'Gemini Code Assist';
      case 2: return 'External MCP Client';
      default: return `Runtime (${rt})`;
    }
  };

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-xl p-5 text-white hover:border-slate-700 transition shadow-lg flex flex-col justify-between">
      <div>
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2.5">
            <div className="p-2 bg-sky-950/60 border border-sky-800/40 rounded-lg text-sky-400">
              <Bot className="w-5 h-5" />
            </div>
            <div>
              <h3 className="font-semibold text-base leading-tight">{agent.name}</h3>
              <span className="font-mono text-[11px] text-slate-500">{agent.id}</span>
            </div>
          </div>
          <span className={`text-xs px-2.5 py-1 rounded-full border ${tierColor} flex items-center gap-1 font-medium`}>
            <ShieldCheck className="w-3.5 h-3.5" /> {tierLabel}
          </span>
        </div>

        <div className="flex items-center gap-1.5 text-xs text-slate-400 mb-3 bg-slate-950/50 p-2 rounded border border-slate-800/60">
          <Cpu className="w-3.5 h-3.5 text-slate-500" />
          <span>Runtime:</span>
          <span className="font-mono text-slate-200 font-semibold">{formatRuntime(agent.runtimeType)}</span>
        </div>

        {agent.personaPrompt && (
          <div className="text-xs text-slate-400 mb-3 line-clamp-2 italic bg-slate-950/30 p-2 rounded">
            "{agent.personaPrompt}"
          </div>
        )}
      </div>

      <div className="text-xs text-slate-300 bg-slate-950 p-3 rounded-lg border border-slate-800 mt-2">
        <div className="text-slate-500 text-[10px] uppercase font-bold tracking-wider mb-1 flex items-center gap-1">
          <Terminal className="w-3 h-3 text-sky-400" /> Living Memory Summary
        </div>
        <p className="line-clamp-3 text-slate-300 font-mono text-[11px]">
          {agent.memorySummary || 'Initialized. Memory updates synchronize upon operational journal commits.'}
        </p>
      </div>
    </div>
  );
};
