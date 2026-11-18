import React, { useState } from 'react';
import { KeyRound, Plus, Trash2, Eye, EyeOff, RotateCcw } from 'lucide-react';

export interface SecretEntryRow {
  id: string;
  key: string;
  value: string;
}

export interface SecretMapPatch {
  incoming?: Record<string, string>;
  removeKeys?: string[];
  hasChanges: boolean;
}

/**
 * Builds a patch payload compatible with `McpControllerHelpers.TryUpdateSecretMap`
 * (`merge: true` + `removeKeys`), avoiding accidental wipes of untouched stored secrets.
 */
export function buildSecretMapPatch(
  entries: SecretEntryRow[],
  removedKeys: string[]
): SecretMapPatch {
  const incoming: Record<string, string> = {};
  for (const row of entries) {
    const trimmedKey = row.key.trim();
    if (trimmedKey) {
      incoming[trimmedKey] = row.value;
    }
  }

  const cleanedRemovals = Array.from(
    new Set(removedKeys.map((k) => k.trim()).filter((k) => k.length > 0))
  );

  const hasIncoming = Object.keys(incoming).length > 0;
  const hasRemovals = cleanedRemovals.length > 0;

  return {
    incoming: hasIncoming ? incoming : undefined,
    removeKeys: hasRemovals ? cleanedRemovals : undefined,
    hasChanges: hasIncoming || hasRemovals
  };
}

/** Parses comma- or newline-separated tool names into a unique ordered list. */
export function parseToolListInput(raw: string): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const part of raw.split(/[\n,]+/)) {
    const trimmed = part.trim();
    if (trimmed && !seen.has(trimmed)) {
      seen.add(trimmed);
      result.push(trimmed);
    }
  }
  return result;
}

export function formatMcpTarget(item: {
  transport: string;
  command?: string | null;
  args?: string[];
  url?: string | null;
}): string {
  if (item.transport === 'Stdio') {
    const cmd = item.command?.trim() || '';
    const args = item.args?.length ? ` ${item.args.join(' ')}` : '';
    return `${cmd}${args}`.trim() || 'No command configured';
  }
  return item.url?.trim() || 'No URL configured';
}

interface McpSecretMapEditorProps {
  label: string;
  description?: string;
  keyPlaceholder?: string;
  valuePlaceholder?: string;
  existingMasked: Record<string, string>;
  inheritedMasked?: Record<string, string>;
  entries: SecretEntryRow[];
  removedKeys: string[];
  onEntriesChange: (next: SecretEntryRow[]) => void;
  onRemovedKeysChange: (next: string[]) => void;
  testIdPrefix: string;
}

export const McpSecretMapEditor: React.FC<McpSecretMapEditorProps> = ({
  label,
  description,
  keyPlaceholder = 'KEY_NAME',
  valuePlaceholder = 'Secret value',
  existingMasked,
  inheritedMasked,
  entries,
  removedKeys,
  onEntriesChange,
  onRemovedKeysChange,
  testIdPrefix
}) => {
  const [revealedRows, setRevealedRows] = useState<Record<string, boolean>>({});
  const existingEntries = Object.entries(existingMasked);
  const inheritedEntries = Object.entries(inheritedMasked ?? {});

  const handleAddRow = () => {
    onEntriesChange([
      ...entries,
      { id: `${testIdPrefix}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`, key: '', value: '' }
    ]);
  };

  const handleUpdateRow = (id: string, field: 'key' | 'value', val: string) => {
    onEntriesChange(entries.map((row) => (row.id === id ? { ...row, [field]: val } : row)));
  };

  const handleRemoveRow = (id: string) => {
    onEntriesChange(entries.filter((row) => row.id !== id));
  };

  const toggleExistingRemoval = (key: string) => {
    if (removedKeys.includes(key)) {
      onRemovedKeysChange(removedKeys.filter((k) => k !== key));
    } else {
      onRemovedKeysChange([...removedKeys, key]);
    }
  };

  return (
    <div className="space-y-2 rounded-xl border border-slate-800 bg-slate-950/60 p-3">
      <div className="flex items-center justify-between gap-2">
        <div>
          <div className="flex items-center gap-1.5 text-xs font-semibold text-slate-200">
            <KeyRound className="w-3.5 h-3.5 text-sky-400" />
            <span>{label}</span>
          </div>
          {description && <p className="text-[10px] text-slate-400 mt-0.5">{description}</p>}
        </div>
        <button
          type="button"
          onClick={handleAddRow}
          data-testid={`${testIdPrefix}-add-btn`}
          className="px-2 py-1 rounded-md bg-slate-800 hover:bg-slate-700 text-sky-300 text-[11px] font-medium flex items-center gap-1 transition cursor-pointer shrink-0"
        >
          <Plus className="w-3 h-3" />
          <span>Add Entry</span>
        </button>
      </div>

      {inheritedEntries.length > 0 && (
        <div className="rounded-lg border border-slate-800/80 bg-slate-900/50 p-2 space-y-1">
          <div className="text-[10px] uppercase tracking-wider font-semibold text-slate-400">
            Inherited from Catalog
          </div>
          <div className="flex flex-wrap gap-1.5">
            {inheritedEntries.map(([k, masked]) => (
              <span
                key={k}
                className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-slate-950 border border-slate-800 text-[10px] font-mono text-slate-300"
              >
                <span className="text-slate-200">{k}</span>
                <span className="text-slate-500">=</span>
                <span className="text-slate-400">{masked}</span>
              </span>
            ))}
          </div>
        </div>
      )}

      {existingEntries.length > 0 && (
        <div className="space-y-1">
          <div className="text-[10px] uppercase tracking-wider font-semibold text-slate-400">
            Stored Encrypted Entries
          </div>
          <div className="space-y-1">
            {existingEntries.map(([k, masked]) => {
              const markedForRemoval = removedKeys.includes(k);
              return (
                <div
                  key={k}
                  className={`flex items-center justify-between gap-2 px-2.5 py-1.5 rounded-lg border text-[11px] font-mono ${
                    markedForRemoval
                      ? 'bg-rose-950/30 border-rose-500/40 text-rose-300'
                      : 'bg-slate-900/70 border-slate-800 text-slate-200'
                  }`}
                >
                  <div className="min-w-0 truncate">
                    <span className={markedForRemoval ? 'line-through' : 'font-semibold'}>{k}</span>
                    <span className="text-slate-500 mx-1.5">=</span>
                    <span className="text-slate-400">{masked}</span>
                    {markedForRemoval && (
                      <span className="ml-2 text-[10px] text-rose-400 font-sans">(will be removed)</span>
                    )}
                  </div>
                  <button
                    type="button"
                    onClick={() => toggleExistingRemoval(k)}
                    data-testid={`${testIdPrefix}-toggle-remove-${k}`}
                    className={`px-2 py-0.5 rounded text-[10px] font-sans font-medium flex items-center gap-1 transition cursor-pointer shrink-0 ${
                      markedForRemoval
                        ? 'bg-slate-800 text-sky-300 hover:bg-slate-700'
                        : 'bg-rose-950/60 text-rose-300 hover:bg-rose-900/70'
                    }`}
                  >
                    {markedForRemoval ? (
                      <>
                        <RotateCcw className="w-2.5 h-2.5" />
                        <span>Undo</span>
                      </>
                    ) : (
                      <>
                        <Trash2 className="w-2.5 h-2.5" />
                        <span>Remove</span>
                      </>
                    )}
                  </button>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {entries.length > 0 ? (
        <div className="space-y-1.5 pt-1">
          {entries.map((row, idx) => {
            const revealed = Boolean(revealedRows[row.id]);
            return (
              <div key={row.id} className="flex items-center gap-1.5">
                <input
                  type="text"
                  aria-label={`${label} Key ${idx + 1}`}
                  placeholder={keyPlaceholder}
                  value={row.key}
                  onChange={(e) => handleUpdateRow(row.id, 'key', e.target.value)}
                  className="w-2/5 bg-slate-900 border border-slate-800 rounded-lg px-2.5 py-1.5 text-white font-mono text-[11px] focus:outline-none focus:border-sky-500"
                />
                <div className="flex-1 relative flex items-center">
                  <input
                    type={revealed ? 'text' : 'password'}
                    aria-label={`${label} Value ${idx + 1}`}
                    placeholder={valuePlaceholder}
                    value={row.value}
                    onChange={(e) => handleUpdateRow(row.id, 'value', e.target.value)}
                    className="w-full bg-slate-900 border border-slate-800 rounded-lg pl-2.5 pr-8 py-1.5 text-white font-mono text-[11px] focus:outline-none focus:border-sky-500"
                  />
                  <button
                    type="button"
                    onClick={() => setRevealedRows((prev) => ({ ...prev, [row.id]: !prev[row.id] }))}
                    aria-label={revealed ? `Hide ${label} Value ${idx + 1}` : `Show ${label} Value ${idx + 1}`}
                    className="absolute right-2 text-slate-400 hover:text-slate-200 cursor-pointer"
                  >
                    {revealed ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
                  </button>
                </div>
                <button
                  type="button"
                  onClick={() => handleRemoveRow(row.id)}
                  aria-label={`Remove ${label} Row ${idx + 1}`}
                  className="p-1.5 rounded-lg text-slate-400 hover:text-rose-400 hover:bg-slate-800 transition cursor-pointer"
                >
                  <Trash2 className="w-3.5 h-3.5" />
                </button>
              </div>
            );
          })}
        </div>
      ) : existingEntries.length === 0 ? (
        <p className="text-[11px] text-slate-500 italic">No entries configured.</p>
      ) : null}
    </div>
  );
};
