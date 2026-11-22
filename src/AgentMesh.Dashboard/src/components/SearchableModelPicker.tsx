import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Brain, Check, ChevronDown, Search, Sparkles } from 'lucide-react';
import type { ModelCapability } from '../types';

export interface SearchableModelPickerProps {
  ariaLabel: string;
  models: ModelCapability[];
  value: string;
  onChange: (modelId: string) => void;
  defaultOptionLabel?: string;
  placeholder?: string;
  allowCustom?: boolean;
  disabled?: boolean;
  title?: string;
  compact?: boolean;
  className?: string;
  id?: string;
}

function formatContextBadge(tokens?: number | null): string | null {
  if (typeof tokens !== 'number' || !Number.isFinite(tokens) || tokens <= 0) return null;
  if (tokens >= 1_000_000) {
    const m = tokens / 1_000_000;
    const text = Number.isInteger(m) ? String(m) : m.toFixed(1).replace(/\.0$/, '');
    return `${text}M`;
  }
  if (tokens >= 1_000) {
    return `${Math.round(tokens / 1000)}k`;
  }
  return `${tokens}`;
}

function formatSinglePrice(value: number): string {
  if (value === 0) return '0';
  if (value < 0.1) return Number(value.toFixed(2)).toString();
  if (value < 10) return Number(value.toFixed(2)).toString();
  return Number(value.toFixed(1)).toString();
}

function formatPriceBadge(prompt?: number | null, completion?: number | null): { text: string; isFree: boolean } | null {
  if (typeof prompt !== 'number' || typeof completion !== 'number') return null;
  if (!Number.isFinite(prompt) || !Number.isFinite(completion)) return null;
  if (prompt < 0 || completion < 0) return null;
  if (prompt === 0 && completion === 0) return { text: 'free', isFree: true };
  return { text: `$${formatSinglePrice(prompt)}/$${formatSinglePrice(completion)}`, isFree: false };
}

type PickerEntry =
  | { kind: 'default'; id: string; label: string }
  | { kind: 'model'; id: string; model: ModelCapability }
  | { kind: 'custom'; id: string; label: string };

/**
 * Searchable combobox for large model catalogs (e.g., OpenRouter's 300+ models),
 * displaying context window, per-1M pricing, and reasoning support badges while
 * allowing custom model slugs when enabled.
 */
export const SearchableModelPicker: React.FC<SearchableModelPickerProps> = ({
  ariaLabel,
  models,
  value,
  onChange,
  defaultOptionLabel = 'Default Model',
  placeholder,
  allowCustom = false,
  disabled = false,
  title,
  compact = false,
  className = '',
  id
}) => {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [highlightedIndex, setHighlightedIndex] = useState(0);

  const containerRef = useRef<HTMLDivElement>(null);
  const searchInputRef = useRef<HTMLInputElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  const selectedModel = useMemo(
    () => (value ? models.find((m) => m.id.toLowerCase() === value.toLowerCase()) ?? null : null),
    [models, value]
  );

  const filteredModels = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return models;
    return models.filter(
      (m) =>
        m.id.toLowerCase().includes(q) ||
        (m.name && m.name.toLowerCase().includes(q))
    );
  }, [models, query]);

  const entries: PickerEntry[] = useMemo(() => {
    const list: PickerEntry[] = [];
    const q = query.trim();
    const qLower = q.toLowerCase();

    if (!qLower || defaultOptionLabel.toLowerCase().includes(qLower) || 'default'.includes(qLower)) {
      list.push({ kind: 'default', id: '', label: defaultOptionLabel });
    }

    for (const model of filteredModels) {
      list.push({ kind: 'model', id: model.id, model });
    }

    if (
      allowCustom &&
      q.length > 0 &&
      !models.some((m) => m.id.toLowerCase() === qLower)
    ) {
      list.push({ kind: 'custom', id: q, label: q });
    }

    return list;
  }, [allowCustom, defaultOptionLabel, filteredModels, models, query]);

  useEffect(() => {
    setHighlightedIndex(0);
  }, [query, open]);

  useEffect(() => {
    if (!open) return;
    const handleOutside = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handleOutside);
    return () => document.removeEventListener('mousedown', handleOutside);
  }, [open]);

  const handleSelect = (nextValue: string) => {
    onChange(nextValue);
    setOpen(false);
    setQuery('');
    triggerRef.current?.focus();
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      setOpen(false);
      triggerRef.current?.focus();
      return;
    }

    if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (entries.length > 0) {
        setHighlightedIndex((prev) => (prev + 1) % entries.length);
      }
      return;
    }

    if (e.key === 'ArrowUp') {
      e.preventDefault();
      if (entries.length > 0) {
        setHighlightedIndex((prev) => (prev - 1 + entries.length) % entries.length);
      }
      return;
    }

    if (e.key === 'Enter' && entries.length > 0) {
      e.preventDefault();
      const entry = entries[highlightedIndex] ?? entries[0];
      if (entry) {
        handleSelect(entry.id);
      }
    }
  };

  const displayLabel = selectedModel
    ? selectedModel.name || selectedModel.id
    : value
    ? value
    : placeholder || defaultOptionLabel;

  const isDisabled = disabled && !allowCustom;

  return (
    <div ref={containerRef} className={`relative ${className}`}>
      <button
        ref={triggerRef}
        id={id}
        type="button"
        aria-label={ariaLabel}
        aria-haspopup="listbox"
        aria-expanded={open}
        disabled={isDisabled}
        title={title}
        onClick={() => {
          if (isDisabled) return;
          setOpen((prev) => !prev);
          setQuery('');
        }}
        className={`w-full flex items-center justify-between gap-2 text-left bg-slate-950 border border-slate-800 rounded-lg text-xs text-white focus:outline-none focus:border-sky-500 cursor-pointer disabled:cursor-not-allowed disabled:opacity-60 ${
          compact ? 'px-2 py-1 text-[11px]' : 'px-2.5 py-1.5'
        }`}
      >
        <span className="truncate font-medium">
          {displayLabel}
        </span>
        <ChevronDown className="w-3.5 h-3.5 text-slate-400 shrink-0" />
      </button>

      {open && (
        <div
          onKeyDown={handleKeyDown}
          className="absolute left-0 top-full mt-1 z-50 w-full min-w-[280px] sm:min-w-[340px] bg-slate-900 border border-slate-700 rounded-xl shadow-2xl overflow-hidden"
        >
          <div className="p-2 border-b border-slate-800 flex items-center gap-2 bg-slate-950/60">
            <Search className="w-3.5 h-3.5 text-slate-500 shrink-0" />
            <input
              ref={searchInputRef}
              type="text"
              autoFocus
              aria-label="Filter models"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder={allowCustom ? 'Search models or enter slug (e.g. :nitro)...' : 'Search models...'}
              className="w-full bg-transparent text-xs text-slate-100 placeholder-slate-500 focus:outline-none font-mono"
            />
          </div>

          <div
            role="listbox"
            aria-label={`${ariaLabel} options`}
            className="max-h-72 overflow-y-auto p-1.5 space-y-0.5"
          >
            {entries.length === 0 ? (
              <div className="px-2.5 py-3 text-center text-[11px] text-slate-500">
                No matching models
              </div>
            ) : (
              entries.map((entry, index) => {
                const isHighlighted = index === highlightedIndex;
                const isSelected = value.toLowerCase() === entry.id.toLowerCase();

                if (entry.kind === 'default') {
                  return (
                    <button
                      key="__default__"
                      type="button"
                      role="option"
                      aria-selected={!value}
                      onMouseEnter={() => setHighlightedIndex(index)}
                      onClick={() => handleSelect('')}
                      className={`w-full text-left px-2.5 py-1.5 rounded-lg text-xs flex items-center justify-between cursor-pointer ${
                        isHighlighted ? 'bg-slate-800 text-white' : 'text-slate-300 hover:bg-slate-800/70'
                      }`}
                    >
                      <span>{entry.label}</span>
                      {!value && <Check className="w-3.5 h-3.5 text-sky-400 shrink-0" />}
                    </button>
                  );
                }

                if (entry.kind === 'custom') {
                  return (
                    <button
                      key={`__custom__:${entry.id}`}
                      type="button"
                      role="option"
                      aria-selected={isSelected}
                      onMouseEnter={() => setHighlightedIndex(index)}
                      onClick={() => handleSelect(entry.id)}
                      className={`w-full text-left px-2.5 py-1.5 rounded-lg text-xs flex items-center justify-between gap-2 cursor-pointer border border-dashed border-sky-500/40 ${
                        isHighlighted ? 'bg-sky-950/60 text-sky-200' : 'text-sky-300 hover:bg-sky-950/40'
                      }`}
                    >
                      <span className="truncate font-mono">
                        Use custom model: <strong className="text-white">{entry.label}</strong>
                      </span>
                      <Sparkles className="w-3.5 h-3.5 text-sky-400 shrink-0" />
                    </button>
                  );
                }

                const { model } = entry;
                const contextBadge = formatContextBadge(model.maxContextTokens);
                const priceBadge = formatPriceBadge(model.promptPricePerMillion, model.completionPricePerMillion);
                const hasReasoning = (model.efforts?.length ?? 0) > 0;

                return (
                  <button
                    key={model.id}
                    type="button"
                    role="option"
                    aria-selected={isSelected}
                    onMouseEnter={() => setHighlightedIndex(index)}
                    onClick={() => handleSelect(model.id)}
                    className={`w-full text-left px-2.5 py-1.5 rounded-lg text-xs flex items-start justify-between gap-2 cursor-pointer ${
                      isHighlighted ? 'bg-slate-800 text-white' : 'text-slate-200 hover:bg-slate-800/70'
                    }`}
                  >
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-1.5 min-w-0">
                        <span className="truncate font-medium">{model.name || model.id}</span>
                        {isSelected && <Check className="w-3 h-3 text-sky-400 shrink-0" />}
                      </div>
                      <div className="flex flex-wrap items-center gap-1.5 mt-0.5">
                        {model.name && model.name !== model.id && (
                          <span className="text-[10px] font-mono text-slate-400 truncate max-w-[180px]">
                            {model.id}
                          </span>
                        )}
                        {contextBadge && (
                          <span className="px-1.5 py-0.2 rounded bg-slate-950 border border-slate-700 text-[10px] font-mono text-slate-300">
                            {contextBadge}
                          </span>
                        )}
                        {priceBadge && (
                          <span
                            className={`px-1.5 py-0.2 rounded border text-[10px] font-mono ${
                              priceBadge.isFree
                                ? 'bg-emerald-950/60 border-emerald-600/40 text-emerald-300'
                                : 'bg-sky-950/60 border-sky-700/40 text-sky-300'
                            }`}
                          >
                            {priceBadge.text}
                          </span>
                        )}
                        {hasReasoning && (
                          <span className="inline-flex items-center gap-0.5 px-1.5 py-0.2 rounded bg-indigo-950/60 border border-indigo-600/40 text-[10px] font-mono text-indigo-300">
                            <Brain className="w-2.5 h-2.5" />
                            <span>reasoning</span>
                          </span>
                        )}
                      </div>
                    </div>
                  </button>
                );
              })
            )}
          </div>
        </div>
      )}
    </div>
  );
};
