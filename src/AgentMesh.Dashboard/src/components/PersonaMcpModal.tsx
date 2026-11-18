import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  Check,
  Plug,
  Plus,
  RefreshCw,
  Server,
  Shield,
  Sliders,
  Trash2,
  X
} from 'lucide-react';
import { api } from '../services/api';
import {
  McpTransportType,
  type McpServer,
  type Persona,
  type PersonaMcpServer,
  type UpsertPersonaMcpServerRequest
} from '../types';
import {
  McpSecretMapEditor,
  buildSecretMapPatch,
  formatMcpTarget,
  parseToolListInput,
  type SecretEntryRow
} from './McpSecretMapEditor';

interface PersonaMcpModalProps {
  persona: Persona | null;
  onClose: () => void;
  onOpenCatalog?: () => void;
  onAssignmentsChanged?: () => void | Promise<void>;
  catalogRefreshTick?: number;
}

export const PersonaMcpModal: React.FC<PersonaMcpModalProps> = ({
  persona,
  onClose,
  onOpenCatalog,
  onAssignmentsChanged,
  catalogRefreshTick
}) => {
  const [assignments, setAssignments] = useState<PersonaMcpServer[]>([]);
  const [catalog, setCatalog] = useState<McpServer[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [selectedServerIdToAssign, setSelectedServerIdToAssign] = useState('');
  const [confirmUnassignId, setConfirmUnassignId] = useState<string | null>(null);

  // Override editor state
  const [editingAssignment, setEditingAssignment] = useState<PersonaMcpServer | null>(null);
  const [overrideEnabled, setOverrideEnabled] = useState(true);
  const [allowedToolsMode, setAllowedToolsMode] = useState<'all' | 'restrict'>('all');
  const [allowedToolsInput, setAllowedToolsInput] = useState('');
  const [blockedToolsInput, setBlockedToolsInput] = useState('');
  const [envEntries, setEnvEntries] = useState<SecretEntryRow[]>([]);
  const [removedEnvKeys, setRemovedEnvKeys] = useState<string[]>([]);
  const [headerEntries, setHeaderEntries] = useState<SecretEntryRow[]>([]);
  const [removedHeaderKeys, setRemovedHeaderKeys] = useState<string[]>([]);

  const loadData = useCallback(async () => {
    if (!persona) return;
    try {
      setLoading(true);
      setError(null);
      const [personaAssignments, catalogServers] = await Promise.all([
        api.getPersonaMcpServers(persona.id),
        api.getMcpServers().catch(() => [] as McpServer[])
      ]);
      setAssignments(personaAssignments);
      setCatalog(catalogServers);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load persona MCP servers');
    } finally {
      setLoading(false);
    }
  }, [persona]);

  useEffect(() => {
    if (!persona) return;
    setEditingAssignment(null);
    setConfirmUnassignId(null);
    setSelectedServerIdToAssign('');
    void loadData();
  }, [persona, catalogRefreshTick, loadData]);

  const unassignedServers = useMemo(() => {
    const assignedIds = new Set(assignments.map((a) => a.mcpServerId.toLowerCase()));
    return catalog.filter((s) => !assignedIds.has(s.id.toLowerCase()));
  }, [catalog, assignments]);

  const effectiveServerIdToAssign = useMemo(() => {
    if (
      selectedServerIdToAssign &&
      unassignedServers.some((s) => s.id === selectedServerIdToAssign)
    ) {
      return selectedServerIdToAssign;
    }
    return unassignedServers[0]?.id ?? '';
  }, [unassignedServers, selectedServerIdToAssign]);

  const openOverrideEditor = (assignment: PersonaMcpServer) => {
    setEditingAssignment(assignment);
    setOverrideEnabled(assignment.enabled);
    if (assignment.allowedTools === null || assignment.allowedTools === undefined) {
      setAllowedToolsMode('all');
      setAllowedToolsInput('');
    } else {
      setAllowedToolsMode('restrict');
      setAllowedToolsInput(assignment.allowedTools.join(', '));
    }
    setBlockedToolsInput((assignment.blockedTools ?? []).join(', '));
    setEnvEntries([]);
    setRemovedEnvKeys([]);
    setHeaderEntries([]);
    setRemovedHeaderKeys([]);
    setError(null);
  };

  const handleAssign = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!persona || !effectiveServerIdToAssign) return;

    try {
      setSaving(true);
      setError(null);
      const created = await api.assignPersonaMcpServer(persona.id, {
        mcpServerId: effectiveServerIdToAssign,
        enabled: true
      });
      setAssignments((prev) =>
        [...prev, created].sort((a, b) => a.mcpServerName.localeCompare(b.mcpServerName))
      );
      setSelectedServerIdToAssign('');
      await onAssignmentsChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to assign MCP server');
    } finally {
      setSaving(false);
    }
  };

  const handleToggleAssignment = async (assignment: PersonaMcpServer) => {
    if (!persona) return;
    try {
      setSaving(true);
      setError(null);
      const updated = await api.updatePersonaMcpServer(persona.id, assignment.mcpServerId, {
        enabled: !assignment.enabled
      });
      setAssignments((prev) =>
        prev.map((a) => (a.mcpServerId === updated.mcpServerId ? updated : a))
      );
      await onAssignmentsChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update assignment');
    } finally {
      setSaving(false);
    }
  };

  const handleUnassign = async (serverId: string) => {
    if (!persona) return;
    try {
      setSaving(true);
      setError(null);
      await api.removePersonaMcpServer(persona.id, serverId);
      setAssignments((prev) => prev.filter((a) => a.mcpServerId !== serverId));
      setConfirmUnassignId(null);
      if (editingAssignment?.mcpServerId === serverId) {
        setEditingAssignment(null);
      }
      await onAssignmentsChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to unassign MCP server');
    } finally {
      setSaving(false);
    }
  };

  const handleSaveOverrides = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!persona || !editingAssignment) return;

    const envPatch = buildSecretMapPatch(envEntries, removedEnvKeys);
    const headerPatch = buildSecretMapPatch(headerEntries, removedHeaderKeys);
    const parsedBlocked = parseToolListInput(blockedToolsInput);

    const payload: UpsertPersonaMcpServerRequest = {
      enabled: overrideEnabled,
      ...(allowedToolsMode === 'all'
        ? { clearAllowedTools: true }
        : { allowedTools: parseToolListInput(allowedToolsInput) }),
      ...(parsedBlocked.length > 0
        ? { blockedTools: parsedBlocked }
        : { clearBlockedTools: true }),
      ...(envPatch.hasChanges
        ? {
            envOverrides: envPatch.incoming,
            mergeEnvOverrides: true,
            removeEnvOverrideKeys: envPatch.removeKeys
          }
        : {}),
      ...(headerPatch.hasChanges
        ? {
            headersOverrides: headerPatch.incoming,
            mergeHeadersOverrides: true,
            removeHeaderOverrideKeys: headerPatch.removeKeys
          }
        : {})
    };

    try {
      setSaving(true);
      setError(null);
      const updated = await api.updatePersonaMcpServer(
        persona.id,
        editingAssignment.mcpServerId,
        payload
      );
      setAssignments((prev) =>
        prev.map((a) => (a.mcpServerId === updated.mcpServerId ? updated : a))
      );
      setEditingAssignment(null);
      await onAssignmentsChanged?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save overrides');
    } finally {
      setSaving(false);
    }
  };

  if (!persona) return null;

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`MCP Servers for ${persona.name}`}
      data-testid="persona-mcp-modal"
      className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-end sm:items-center justify-center p-0 sm:p-4"
    >
      <div className="max-w-3xl w-full bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl shadow-2xl flex flex-col max-h-[90dvh] sm:max-h-[90vh] overflow-hidden">
        {/* Header */}
        <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
          <div className="flex items-center gap-2.5 min-w-0">
            <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400 shrink-0">
              <Plug className="w-4 h-4" />
            </div>
            <div className="min-w-0">
              <h3 className="text-sm font-bold text-white truncate">
                {editingAssignment
                  ? `Overrides: ${editingAssignment.mcpServerName}`
                  : `MCP Servers for ${persona.name}`}
              </h3>
              <p className="text-xs text-slate-400 font-mono truncate">
                @{persona.id} · Driver @{persona.driverId}
              </p>
            </div>
          </div>

          <div className="flex items-center gap-2 shrink-0">
            {onOpenCatalog && !editingAssignment && (
              <button
                type="button"
                onClick={onOpenCatalog}
                data-testid="persona-mcp-manage-catalog-btn"
                className="px-2.5 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-sky-300 text-xs font-medium flex items-center gap-1.5 transition cursor-pointer"
              >
                <Server className="w-3.5 h-3.5" />
                <span>Manage Catalog</span>
              </button>
            )}
            <button
              type="button"
              onClick={onClose}
              aria-label="Close Persona MCP Servers"
              className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>

        {/* Error Banner */}
        {error && (
          <div className="mx-5 mt-3 px-3 py-2 rounded-lg bg-rose-950/70 border border-rose-500/40 text-xs text-rose-200 flex items-center gap-2 shrink-0">
            <AlertTriangle className="w-4 h-4 text-rose-400 shrink-0" />
            <span className="flex-1">{error}</span>
            <button
              type="button"
              onClick={() => setError(null)}
              className="text-rose-300 hover:text-white cursor-pointer"
            >
              <X className="w-3.5 h-3.5" />
            </button>
          </div>
        )}

        {editingAssignment ? (
          <form onSubmit={handleSaveOverrides} className="flex-1 flex flex-col min-h-0">
            <div className="flex-1 overflow-y-auto p-5 space-y-4 text-xs">
              <div className="rounded-xl border border-slate-800 bg-slate-950/50 p-3 space-y-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-bold text-white">{editingAssignment.mcpServerName}</span>
                  <span className="font-mono text-[10px] text-slate-400">
                    @{editingAssignment.mcpServerId}
                  </span>
                  <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-900 border border-slate-700 text-sky-300">
                    {editingAssignment.transport}
                  </span>
                  {!editingAssignment.serverEnabled && (
                    <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-amber-950/50 border border-amber-500/30 text-amber-300">
                      Catalog Server Disabled
                    </span>
                  )}
                </div>
                <div className="font-mono text-[11px] text-slate-400 truncate">
                  {formatMcpTarget(editingAssignment)}
                </div>
              </div>

              <label className="flex items-center gap-2 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={overrideEnabled}
                  onChange={(e) => setOverrideEnabled(e.target.checked)}
                  className="rounded border-slate-700 bg-slate-950 text-sky-500 focus:ring-sky-500"
                />
                <span className="font-semibold text-slate-200">
                  Enabled for {persona.name}
                </span>
              </label>

              {/* Tool Access Controls */}
              <div className="space-y-3 rounded-xl border border-slate-800 bg-slate-950/60 p-3">
                <div className="flex items-center gap-1.5 font-semibold text-slate-200">
                  <Shield className="w-3.5 h-3.5 text-sky-400" />
                  <span>Tool Access Filtering</span>
                </div>

                <div className="space-y-2">
                  <label className="block text-[11px] font-semibold text-slate-300">
                    Allowed Tools
                  </label>
                  <div className="flex items-center gap-3">
                    <label className="flex items-center gap-1.5 cursor-pointer">
                      <input
                        type="radio"
                        name="allowed-tools-mode"
                        checked={allowedToolsMode === 'all'}
                        onChange={() => setAllowedToolsMode('all')}
                        className="text-sky-500 bg-slate-950 border-slate-700"
                      />
                      <span className="text-slate-300">All tools (no allowlist)</span>
                    </label>
                    <label className="flex items-center gap-1.5 cursor-pointer">
                      <input
                        type="radio"
                        name="allowed-tools-mode"
                        checked={allowedToolsMode === 'restrict'}
                        onChange={() => setAllowedToolsMode('restrict')}
                        className="text-sky-500 bg-slate-950 border-slate-700"
                      />
                      <span className="text-slate-300">Restrict to specific tools</span>
                    </label>
                  </div>

                  {allowedToolsMode === 'restrict' && (
                    <textarea
                      aria-label="Allowed Tools List"
                      rows={2}
                      placeholder="Comma or newline separated tool names, e.g. read_file, search_code"
                      value={allowedToolsInput}
                      onChange={(e) => setAllowedToolsInput(e.target.value)}
                      className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500"
                    />
                  )}
                </div>

                <div className="space-y-1">
                  <label htmlFor="persona-mcp-blocked-tools" className="block text-[11px] font-semibold text-slate-300">
                    Blocked Tools (Optional)
                  </label>
                  <textarea
                    id="persona-mcp-blocked-tools"
                    aria-label="Blocked Tools List"
                    rows={2}
                    placeholder="Comma or newline separated tool names to deny, e.g. delete_repo, push_files"
                    value={blockedToolsInput}
                    onChange={(e) => setBlockedToolsInput(e.target.value)}
                    className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500"
                  />
                </div>
              </div>

              {editingAssignment.transport === McpTransportType.Stdio ? (
                <McpSecretMapEditor
                  label="Environment Overrides (Encrypted)"
                  description="Merged over the catalog server's environment variables for this persona only."
                  keyPlaceholder="GITHUB_PERSONAL_ACCESS_TOKEN"
                  valuePlaceholder="Persona-specific env value"
                  inheritedMasked={editingAssignment.serverEnvMasked}
                  existingMasked={editingAssignment.envOverridesMasked}
                  entries={envEntries}
                  removedKeys={removedEnvKeys}
                  onEntriesChange={setEnvEntries}
                  onRemovedKeysChange={setRemovedEnvKeys}
                  testIdPrefix="persona-mcp-env-override"
                />
              ) : (
                <McpSecretMapEditor
                  label="HTTP Header Overrides (Encrypted)"
                  description="Merged over the catalog server's headers for this persona only."
                  keyPlaceholder="Authorization"
                  valuePlaceholder="Bearer persona-token"
                  inheritedMasked={editingAssignment.serverHeadersMasked}
                  existingMasked={editingAssignment.headersOverridesMasked}
                  entries={headerEntries}
                  removedKeys={removedHeaderKeys}
                  onEntriesChange={setHeaderEntries}
                  onRemovedKeysChange={setRemovedHeaderKeys}
                  testIdPrefix="persona-mcp-headers-override"
                />
              )}
            </div>

            <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-end gap-2 shrink-0 pb-[max(0.75rem,env(safe-area-inset-bottom))]">
              <button
                type="button"
                onClick={() => {
                  setEditingAssignment(null);
                  setError(null);
                }}
                className="px-3.5 py-1.5 rounded-lg text-xs font-medium text-slate-400 hover:text-white transition cursor-pointer"
              >
                Back
              </button>
              <button
                type="submit"
                disabled={saving}
                data-testid="persona-mcp-save-overrides-btn"
                className="px-4 py-1.5 rounded-lg text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white transition cursor-pointer disabled:opacity-50 flex items-center gap-1.5"
              >
                <Check className="w-3.5 h-3.5" />
                <span>Save Overrides</span>
              </button>
            </div>
          </form>
        ) : (
          <>
            {/* Assign from Catalog Bar */}
            <form
              onSubmit={handleAssign}
              className="px-5 py-3 border-b border-slate-800/80 bg-slate-950/40 flex flex-col sm:flex-row items-stretch sm:items-center gap-2 shrink-0"
            >
              <select
                aria-label="Select MCP server to assign"
                value={effectiveServerIdToAssign}
                onChange={(e) => setSelectedServerIdToAssign(e.target.value)}
                disabled={unassignedServers.length === 0 || saving}
                className="flex-1 bg-slate-950 border border-slate-800 rounded-lg px-3 py-1.5 text-xs text-white focus:outline-none focus:border-sky-500 disabled:opacity-50 cursor-pointer"
              >
                {unassignedServers.length === 0 ? (
                  <option value="">
                    {catalog.length === 0
                      ? 'No MCP servers in catalog yet'
                      : 'All catalog MCP servers are already assigned'}
                  </option>
                ) : (
                  unassignedServers.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name} (@{s.id}) · {s.transport}
                      {!s.enabled ? ' [Disabled in catalog]' : ''}
                    </option>
                  ))
                )}
              </select>
              <button
                type="submit"
                disabled={!effectiveServerIdToAssign || saving}
                data-testid="persona-mcp-assign-btn"
                className="px-3.5 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 disabled:opacity-50 text-white text-xs font-semibold flex items-center justify-center gap-1.5 transition cursor-pointer shrink-0"
              >
                <Plus className="w-3.5 h-3.5" />
                <span>Assign Server</span>
              </button>
            </form>

            {/* Assigned Servers List */}
            <div className="flex-1 overflow-y-auto p-5 space-y-3">
              {loading && assignments.length === 0 ? (
                <div className="py-12 flex flex-col items-center justify-center text-slate-400 text-xs gap-2">
                  <RefreshCw className="w-5 h-5 animate-spin text-sky-400" />
                  <span>Loading assigned MCP servers...</span>
                </div>
              ) : assignments.length === 0 ? (
                <div className="p-6 rounded-xl border border-dashed border-slate-800 bg-slate-950/40 text-center space-y-2">
                  <Plug className="w-8 h-8 text-slate-600 mx-auto" />
                  <h4 className="text-xs font-semibold text-slate-200">
                    No MCP Servers Assigned to {persona.name}
                  </h4>
                  <p className="text-[11px] text-slate-400 max-w-md mx-auto">
                    Assign a shared MCP server from the catalog above, then customize env/header overrides or allowed/blocked tool lists for this persona.
                  </p>
                </div>
              ) : (
                assignments.map((assignment) => {
                  const envOverrideCount = Object.keys(assignment.envOverridesMasked || {}).length;
                  const hdrOverrideCount = Object.keys(
                    assignment.headersOverridesMasked || {}
                  ).length;

                  return (
                    <div
                      key={assignment.mcpServerId}
                      data-testid={`persona-mcp-item-${assignment.mcpServerId}`}
                      className="rounded-xl border border-slate-800 bg-slate-950/60 p-3.5 space-y-2"
                    >
                      <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0 flex-1 space-y-1">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <span className="text-xs font-bold text-white">
                              {assignment.mcpServerName}
                            </span>
                            <span className="text-[10px] font-mono text-slate-400">
                              @{assignment.mcpServerId}
                            </span>
                            <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-900 border border-slate-700 text-sky-300">
                              {assignment.transport}
                            </span>
                            {assignment.effectiveEnabled ? (
                              <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-emerald-950/50 border border-emerald-500/30 text-emerald-300">
                                Active
                              </span>
                            ) : !assignment.serverEnabled ? (
                              <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-amber-950/50 border border-amber-500/30 text-amber-300">
                                Catalog Disabled
                              </span>
                            ) : (
                              <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-900 border border-slate-700 text-slate-400">
                                Disabled for Persona
                              </span>
                            )}
                          </div>

                          {assignment.mcpServerDescription && (
                            <p className="text-[11px] text-slate-400">
                              {assignment.mcpServerDescription}
                            </p>
                          )}

                          <div className="text-[11px] font-mono text-slate-300 bg-slate-900/80 border border-slate-800/80 rounded-lg px-2.5 py-1.5 truncate">
                            {formatMcpTarget(assignment)}
                          </div>

                          <div className="flex flex-wrap items-center gap-1.5 pt-0.5">
                            <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-900 border border-slate-800 text-slate-300">
                              {assignment.allowedTools == null
                                ? 'Tools: All Allowed'
                                : assignment.allowedTools.length === 0
                                ? 'Tools: None Allowed (Empty Allowlist)'
                                : `Allowed: ${assignment.allowedTools.join(', ')}`}
                            </span>
                            {assignment.blockedTools && assignment.blockedTools.length > 0 && (
                              <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-rose-950/40 border border-rose-500/30 text-rose-300">
                                Blocked: {assignment.blockedTools.join(', ')}
                              </span>
                            )}
                            {envOverrideCount > 0 && (
                              <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-indigo-950/40 border border-indigo-500/30 text-indigo-300">
                                {envOverrideCount} env override(s)
                              </span>
                            )}
                            {hdrOverrideCount > 0 && (
                              <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-indigo-950/40 border border-indigo-500/30 text-indigo-300">
                                {hdrOverrideCount} header override(s)
                              </span>
                            )}
                          </div>
                        </div>

                        <div className="flex items-center gap-1.5 shrink-0">
                          <button
                            type="button"
                            onClick={() => handleToggleAssignment(assignment)}
                            disabled={saving}
                            data-testid={`persona-mcp-toggle-${assignment.mcpServerId}`}
                            className={`px-2 py-1 rounded-lg text-[11px] font-medium border transition cursor-pointer ${
                              assignment.enabled
                                ? 'bg-slate-900 border-slate-700 text-slate-300 hover:text-amber-300'
                                : 'bg-emerald-950/40 border-emerald-500/30 text-emerald-300 hover:bg-emerald-900/50'
                            }`}
                          >
                            {assignment.enabled ? 'Disable' : 'Enable'}
                          </button>
                          <button
                            type="button"
                            onClick={() => openOverrideEditor(assignment)}
                            data-testid={`persona-mcp-configure-${assignment.mcpServerId}`}
                            className="px-2.5 py-1 rounded-lg bg-slate-900 border border-slate-800 text-sky-300 hover:text-white hover:border-slate-700 text-[11px] font-medium flex items-center gap-1 transition cursor-pointer"
                          >
                            <Sliders className="w-3 h-3" />
                            <span>Overrides</span>
                          </button>
                          <button
                            type="button"
                            onClick={() =>
                              setConfirmUnassignId((cur) =>
                                cur === assignment.mcpServerId ? null : assignment.mcpServerId
                              )
                            }
                            aria-label={`Unassign ${assignment.mcpServerName}`}
                            data-testid={`persona-mcp-remove-${assignment.mcpServerId}`}
                            className="p-1.5 rounded-lg bg-slate-900 border border-slate-800 text-slate-300 hover:text-rose-400 hover:border-rose-500/40 transition cursor-pointer"
                          >
                            <Trash2 className="w-3.5 h-3.5" />
                          </button>
                        </div>
                      </div>

                      {confirmUnassignId === assignment.mcpServerId && (
                        <div className="p-2.5 rounded-lg bg-rose-950/60 border border-rose-500/40 flex items-center justify-between gap-2 text-xs text-rose-200">
                          <span>
                            Unassign <strong>{assignment.mcpServerName}</strong> from {persona.name}?
                          </span>
                          <div className="flex items-center gap-1.5 shrink-0">
                            <button
                              type="button"
                              onClick={() => setConfirmUnassignId(null)}
                              className="px-2 py-1 rounded bg-slate-800 text-slate-300 hover:text-white text-[11px] cursor-pointer"
                            >
                              Cancel
                            </button>
                            <button
                              type="button"
                              onClick={() => handleUnassign(assignment.mcpServerId)}
                              disabled={saving}
                              data-testid={`persona-mcp-confirm-remove-${assignment.mcpServerId}`}
                              className="px-2.5 py-1 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold text-[11px] cursor-pointer"
                            >
                              Confirm Unassign
                            </button>
                          </div>
                        </div>
                      )}
                    </div>
                  );
                })
              )}
            </div>

            <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-end shrink-0 pb-[max(0.75rem,env(safe-area-inset-bottom))]">
              <button
                type="button"
                onClick={onClose}
                className="px-4 py-1.5 rounded-lg text-xs font-medium bg-slate-800 hover:bg-slate-700 text-slate-200 transition cursor-pointer"
              >
                Done
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
};
