import React, { useState } from 'react';
import { HelpCircle, CheckCircle2, Check, RefreshCw, Send } from 'lucide-react';
import type { AskQuestionItem, AskQuestionOption, QuestionAnswerPayload } from '../types';

export function getOptionLabel(opt: AskQuestionOption): string {
  if (typeof opt === 'string') return opt;
  if (opt && typeof opt === 'object') {
    return opt.label || opt.text || opt.title || opt.value || JSON.stringify(opt);
  }
  return String(opt ?? '');
}

export function getOptionDescription(opt: AskQuestionOption): string | undefined {
  if (opt && typeof opt === 'object' && opt.description) {
    return opt.description;
  }
  return undefined;
}

export function parseTextQuestions(content: string): AskQuestionItem[] {
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

export interface QuestionCardProps {
  cardKey: string;
  targetId: string;
  eventCreatedAt?: string;
  parsedQuestions: AskQuestionItem[];
  isAnswered?: boolean;
  answeredContent?: string;
  answeredAt?: string;
  isSubmitting?: boolean;
  onSubmit: (answers: QuestionAnswerPayload[]) => Promise<void> | void;
  className?: string;
}

export const QuestionCard: React.FC<QuestionCardProps> = ({
  cardKey,
  targetId,
  eventCreatedAt,
  parsedQuestions,
  isAnswered = false,
  answeredContent,
  answeredAt,
  isSubmitting = false,
  onSubmit,
  className = ''
}) => {
  const [selectedOptions, setSelectedOptions] = useState<Record<number, string[]>>({});
  const [customTexts, setCustomTexts] = useState<Record<number, string>>({});

  const hasAnyAnswerInput = parsedQuestions.some((_, idx) => {
    const opts = selectedOptions[idx] || [];
    const text = (customTexts[idx] || '').trim();
    return opts.length > 0 || text.length > 0;
  });

  if (isAnswered) {
    return (
      <div
        key={`ans-${cardKey}`}
        data-testid={`question-card-answered-${targetId}`}
        className={`bg-emerald-950/15 border border-emerald-500/30 rounded-2xl p-5 shadow-sm space-y-3 ${className}`}
      >
        <div className="flex items-center justify-between pb-2.5 border-b border-emerald-500/20">
          <div className="flex items-center gap-2 text-xs font-semibold text-emerald-300">
            <CheckCircle2 className="w-4 h-4 text-emerald-400 shrink-0" />
            <span>Agent Question — Answered</span>
          </div>
          <span className="text-[11px] font-mono text-slate-500">
            {answeredAt
              ? new Date(answeredAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
              : eventCreatedAt
              ? new Date(eventCreatedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
              : ''}
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
                {answeredContent || 'Answer submitted'}
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  const handleSubmit = async () => {
    const answers: QuestionAnswerPayload[] = parsedQuestions.map((q, idx) => {
      const selected = selectedOptions[idx] || [];
      const custom = (customTexts[idx] || '').trim();
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

    await onSubmit(answers);
  };

  return (
    <div
      key={`q-${cardKey}`}
      data-testid={`question-card-${targetId}`}
      className={`bg-amber-950/20 border border-amber-500/40 rounded-2xl p-5 shadow-lg space-y-4 ${className}`}
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
          const selectedOpts = selectedOptions[idx] || [];
          const customText = customTexts[idx] || '';
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
                          const existing = selectedOptions[idx] || [];
                          let nextSelected: string[];
                          let nextCustom = customTexts;
                          if (q.is_multi_select) {
                            nextSelected = existing.includes(optLabel)
                              ? existing.filter((x) => x !== optLabel)
                              : [...existing, optLabel];
                          } else {
                            nextSelected = existing.includes(optLabel) ? [] : [optLabel];
                            nextCustom = { ...customTexts, [idx]: '' };
                          }
                          setSelectedOptions((prev) => ({ ...prev, [idx]: nextSelected }));
                          setCustomTexts(nextCustom);
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
                    if (!q.is_multi_select && val.trim().length > 0) {
                      setSelectedOptions((prev) => ({ ...prev, [idx]: [] }));
                    }
                    setCustomTexts((prev) => ({ ...prev, [idx]: val }));
                  }}
                  placeholder={
                    q.options && q.options.length > 0
                      ? 'Or type a custom write-in response...'
                      : 'Type your response to this question...'
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
          disabled={!hasAnyAnswerInput || isSubmitting}
          onClick={handleSubmit}
          className="px-4 py-2 rounded-xl text-xs font-semibold bg-sky-600 hover:bg-sky-500 disabled:opacity-50 disabled:cursor-not-allowed text-white transition flex items-center gap-1.5 shadow-md cursor-pointer"
        >
          {isSubmitting ? (
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
