import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  Check,
  Edit3,
  Plus,
  RefreshCw,
  Search,
  Server,
  Trash2,
  X
} from 'lucide-react';
import { api } from '../services/api';
import { McpTransportType, type McpServer } from '../types';
import {
  McpSecretMapEditor,
  buildSecretMapPatch,
  formatMcpTarget,
  type SecretEntryRow
} from './McpSecretMapEditor';

interface McpCatalogModalProps {
  isOpen: boolean;
  onClose: () => void;
  initialServers?: McpServer[];
  onCatalogChanged?: (servers: McpServer[]) => void;
}

const TRANSPORT_OPTIONS = [
  { value: McpTransportType.Stdio, label: 'Stdio (Local Subprocess)' },
  { value: McpTransportType.Http, label: 'Streamable HTTP' },
  { value: McpTransportType.Sse, label: 'SSE (Server-Sent Events)' }
] as const;

export const McpCatalogModal: React.FC<McpCatalogModalProps> = ({
  isOpen,
  onClose,
  initialServers,
  onCatalogChanged
}) => {
  const [servers, setServers] = useState<McpServer[]>(initialServers ?? []);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [searchQuery, setSearchQuery] = useState('');
  const [transportFilter, setTransportFilter] = useState<'all' | string>('all');
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);

  // Create / Edit view state
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [editingServer, setEditingServer] = useState<McpServer | null>(null);
  const [nameInput, setNameInput] = useState('');
  const [idInput, setIdInput] = useState('');
  const [descriptionInput, setDescriptionInput] = useState('');
  const [transportInput, setTransportInput] = useState<string>(McpTransportType.Stdio);
  const [commandInput, setCommandInput] = useState('');
  const [argsInput, setArgsInput] = useState('');
  const [urlInput, setUrlInput] = useState('');
  const [enabledInput, setEnabledInput] = useState(true);
  const [timeoutInput, setTimeoutInput] = useState('30');

  const [envEntries, setEnvEntries] = useState<SecretEntryRow[]>([]);
  const [removedEnvKeys, setRemovedEnvKeys] = useState<string[]>([]);
  const [headerEntries, setHeaderEntries] = useState<SecretEntryRow[]>([]);
  const [removedHeaderKeys, setRemovedHeaderKeys] = useState<string[]>([]);

  const loadServers = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const list = await api.getMcpServers();
      setServers(list);
      onCatalogChanged?.(list);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load MCP servers');
    } finally {
      setLoading(false);
    }
  }, [onCatalogChanged]);

  useEffect(() => {
    if (!isOpen) return;
    if (initialServers && initialServers.length > 0) {
      setServers(initialServers);
    }
    void loadServers();
  }, [isOpen]); // eslint-disable-line react-hooks/exhaustive-deps

  const openCreateForm = () => {
    setEditingServer(null);
    setNameInput('');
    setIdInput('');
    setDescriptionInput('');
    setTransportInput(McpTransportType.Stdio);
    setCommandInput('');
    setArgsInput('');
    setUrlInput('');
    setEnabledInput(true);
    setTimeoutInput('30');
    setEnvEntries([]);
    setRemovedEnvKeys([]);
    setHeaderEntries([]);
    setRemovedHeaderKeys([]);
    setError(null);
    setIsFormOpen(true);
  };

  const openEditForm = (server: McpServer) => {
    setEditingServer(server);
    setNameInput(server.name);
    setIdInput(server.id);
    setDescriptionInput(server.description || '');
    setTransportInput(server.transport || McpTransportType.Stdio);
    setCommandInput(server.command || '');
    setArgsInput((server.args ?? []).join('\n'));
    setUrlInput(server.url || '');
    setEnabledInput(server.enabled);
    setTimeoutInput(String(server.timeoutSeconds || 30));
    setEnvEntries([]);
    setRemovedEnvKeys([]);
    setHeaderEntries([]);
    setRemovedHeaderKeys([]);
    setError(null);
    setIsFormOpen(true);
  };

  const filteredServers = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    return servers.filter((s) => {
      if (transportFilter !== 'all' && s.transport !== transportFilter) {
        return false;
      }
      if (!q) return true;
      return (
        s.name.toLowerCase().includes(q) ||
        s.id.toLowerCase().includes(q) ||
        (s.description || '').toLowerCase().includes(q) ||
        formatMcpTarget(s).toLowerCase().includes(q)
      );
    });
  }, [servers, searchQuery, transportFilter]);

  const handleToggleEnabled = async (server: McpServer) => {
    try {
      setSaving(true);
      setError(null);
      const updated = await api.updateMcpServer(server.id, { enabled: !server.enabled });
      const next = servers.map((s) => (s.id === updated.id ? updated : s));
      setServers(next);
      onCatalogChanged?.(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to toggle MCP server');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id: string) => {
    try {
      setSaving(true);
      setError(null);
      await api.deleteMcpServer(id);
      const next = servers.filter((s) => s.id !== id);
      setServers(next);
      setConfirmDeleteId(null);
      onCatalogChanged?.(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete MCP server');
    } finally {
      setSaving(false);
    }
  };

  const handleSubmitForm = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmedName = nameInput.trim();
    if (!trimmedName) {
      setError('Server name is required.');
      return;
    }

    const parsedTimeout = Number.parseInt(timeoutInput, 10);
    if (!Number.isFinite(parsedTimeout) || parsedTimeout < 1 || parsedTimeout > 3600) {
      setError('Timeout must be between 1 and 3600 seconds.');
      return;
    }

    const isStdio = transportInput === McpTransportType.Stdio;
    if (isStdio && !commandInput.trim()) {
      setError('Command is required for Stdio MCP servers.');
      return;
    }
    if (!isStdio && !urlInput.trim()) {
      setError(`URL is required for ${transportInput} MCP servers.`);
      return;
    }

    const parsedArgs = isStdio
      ? argsInput
          .split('\n')
          .map((line) => line.trim())
          .filter((line) => line.length > 0)
      : [];

    const envPatch = buildSecretMapPatch(envEntries, removedEnvKeys);
    const headerPatch = buildSecretMapPatch(headerEntries, removedHeaderKeys);

    try {
      setSaving(true);
      setError(null);

      if (editingServer) {
        const updated = await api.updateMcpServer(editingServer.id, {
          name: trimmedName,
          description: descriptionInput.trim(),
          transport: transportInput,
          command: isStdio ? commandInput.trim() : undefined,
          args: isStdio ? parsedArgs : undefined,
          url: !isStdio ? urlInput.trim() : undefined,
          enabled: enabledInput,
          timeoutSeconds: parsedTimeout,
          ...(envPatch.hasChanges
            ? {
                env: envPatch.incoming,
                mergeEnv: true,
                removeEnvKeys: envPatch.removeKeys
              }
            : {}),
          ...(headerPatch.hasChanges
            ? {
                headers: headerPatch.incoming,
                mergeHeaders: true,
                removeHeaderKeys: headerPatch.removeKeys
              }
            : {})
        });
        const next = servers.map((s) => (s.id === updated.id ? updated : s));
        setServers(next);
        onCatalogChanged?.(next);
      } else {
        const created = await api.createMcpServer({
          name: trimmedName,
          id: idInput.trim() || undefined,
          description: descriptionInput.trim() || undefined,
          transport: transportInput,
          command: isStdio ? commandInput.trim() : undefined,
          args: isStdio ? parsedArgs : undefined,
          url: !isStdio ? urlInput.trim() : undefined,
          env: envPatch.incoming,
          headers: headerPatch.incoming,
          enabled: enabledInput,
          timeoutSeconds: parsedTimeout
        });
        const next = [...servers, created].sort((a, b) => a.name.localeCompare(b.name));
        setServers(next);
        onCatalogChanged?.(next);
      }

      setIsFormOpen(false);
      setEditingServer(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save MCP server');
    } finally {
      setSaving(false);
    }
  };

  if (!isOpen) return null;

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label="MCP Server Catalog"
      data-testid="mcp-catalog-modal"
      className="fixed inset-0 z-50 bg-black/75 backdrop-blur-sm flex items-end sm:items-center justify-center p-0 sm:p-4"
    >
      <div className="max-w-3xl w-full bg-slate-900 border border-slate-800 rounded-t-2xl sm:rounded-2xl shadow-2xl flex flex-col max-h-[90dvh] sm:max-h-[90vh] overflow-hidden">
        {/* Header */}
        <div className="px-5 py-4 border-b border-slate-800 flex items-center justify-between shrink-0">
          <div className="flex items-center gap-2.5">
            <div className="w-8 h-8 rounded-lg bg-sky-500/10 border border-sky-500/30 flex items-center justify-center text-sky-400">
              <Server className="w-4 h-4" />
            </div>
            <div>
              <h3 className="text-sm font-bold text-white">
                {isFormOpen
                  ? editingServer
                    ? `Edit MCP Server: ${editingServer.name}`
                    : 'Register MCP Server'
                  : `MCP Server Catalog (${servers.length})`}
              </h3>
              <p className="text-xs text-slate-400">
                Configure shared Model Context Protocol tools for personas and drivers
              </p>
            </div>
          </div>

          <div className="flex items-center gap-2">
            {!isFormOpen && (
              <button
                type="button"
                onClick={openCreateForm}
                data-testid="mcp-catalog-add-server-btn"
                className="px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold flex items-center gap-1.5 transition cursor-pointer"
              >
                <Plus className="w-3.5 h-3.5" />
                <span>Add MCP Server</span>
              </button>
            )}
            <button
              type="button"
              onClick={onClose}
              aria-label="Close MCP Server Catalog"
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

        {isFormOpen ? (
          <form onSubmit={handleSubmitForm} className="flex-1 flex flex-col min-h-0">
            <div className="flex-1 overflow-y-auto p-5 space-y-4 text-xs">
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div className="space-y-1">
                  <label htmlFor="mcp-server-name" className="block font-semibold text-slate-300">
                    Server Name
                  </label>
                  <input
                    id="mcp-server-name"
                    type="text"
                    placeholder="e.g. GitHub MCP"
                    value={nameInput}
                    onChange={(e) => setNameInput(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-sky-500"
                    required
                  />
                </div>
                <div className="space-y-1">
                  <label htmlFor="mcp-server-id" className="block font-semibold text-slate-300">
                    Server ID / Slug {editingServer ? '(Fixed)' : '(Optional)'}
                  </label>
                  <input
                    id="mcp-server-id"
                    type="text"
                    placeholder="e.g. github-mcp"
                    value={idInput}
                    onChange={(e) => setIdInput(e.target.value)}
                    disabled={!!editingServer}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500 disabled:opacity-50"
                  />
                </div>
              </div>

              <div className="space-y-1">
                <label htmlFor="mcp-server-description" className="block font-semibold text-slate-300">
                  Description (Optional)
                </label>
                <input
                  id="mcp-server-description"
                  type="text"
                  placeholder="Tools exposed by this MCP server..."
                  value={descriptionInput}
                  onChange={(e) => setDescriptionInput(e.target.value)}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div className="space-y-1 sm:col-span-2">
                  <label htmlFor="mcp-server-transport" className="block font-semibold text-slate-300">
                    Transport Protocol
                  </label>
                  <select
                    id="mcp-server-transport"
                    aria-label="Transport Protocol"
                    value={transportInput}
                    onChange={(e) => setTransportInput(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white focus:outline-none focus:border-sky-500 cursor-pointer"
                  >
                    {TRANSPORT_OPTIONS.map((opt) => (
                      <option key={opt.value} value={opt.value}>
                        {opt.label}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="space-y-1">
                  <label htmlFor="mcp-server-timeout" className="block font-semibold text-slate-300">
                    Timeout (sec)
                  </label>
                  <input
                    id="mcp-server-timeout"
                    type="number"
                    min={1}
                    max={3600}
                    value={timeoutInput}
                    onChange={(e) => setTimeoutInput(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500"
                  />
                </div>
              </div>

              {transportInput === McpTransportType.Stdio ? (
                <div className="space-y-3 rounded-xl border border-slate-800 bg-slate-950/50 p-3">
                  <div className="space-y-1">
                    <label htmlFor="mcp-server-command" className="block font-semibold text-slate-300">
                      Command
                    </label>
                    <input
                      id="mcp-server-command"
                      type="text"
                      placeholder="e.g. npx, uvx, docker"
                      value={commandInput}
                      onChange={(e) => setCommandInput(e.target.value)}
                      className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500"
                      required
                    />
                  </div>
                  <div className="space-y-1">
                    <label htmlFor="mcp-server-args" className="block font-semibold text-slate-300">
                      Arguments (one per line)
                    </label>
                    <textarea
                      id="mcp-server-args"
                      rows={3}
                      placeholder={'-y\n@modelcontextprotocol/server-github'}
                      value={argsInput}
                      onChange={(e) => setArgsInput(e.target.value)}
                      className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500"
                    />
                  </div>
                </div>
              ) : (
                <div className="space-y-1 rounded-xl border border-slate-800 bg-slate-950/50 p-3">
                  <label htmlFor="mcp-server-url" className="block font-semibold text-slate-300">
                    Endpoint URL
                  </label>
                  <input
                    id="mcp-server-url"
                    type="url"
                    placeholder="https://mcp.example.com/mcp"
                    value={urlInput}
                    onChange={(e) => setUrlInput(e.target.value)}
                    className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-white font-mono focus:outline-none focus:border-sky-500"
                    required
                  />
                </div>
              )}

              <label className="flex items-center gap-2 cursor-pointer select-none">
                <input
                  type="checkbox"
                  checked={enabledInput}
                  onChange={(e) => setEnabledInput(e.target.checked)}
                  className="rounded border-slate-700 bg-slate-950 text-sky-500 focus:ring-sky-500"
                />
                <span className="font-semibold text-slate-200">Enabled globally in catalog</span>
              </label>

              {transportInput === McpTransportType.Stdio ? (
                <McpSecretMapEditor
                  label="Environment Variables (Encrypted)"
                  description="Injected into the stdio subprocess environment. Untouched stored keys stay encrypted."
                  keyPlaceholder="GITHUB_PERSONAL_ACCESS_TOKEN"
                  valuePlaceholder="Secret env value"
                  existingMasked={editingServer?.envMasked ?? {}}
                  entries={envEntries}
                  removedKeys={removedEnvKeys}
                  onEntriesChange={setEnvEntries}
                  onRemovedKeysChange={setRemovedEnvKeys}
                  testIdPrefix="mcp-catalog-env"
                />
              ) : (
                <McpSecretMapEditor
                  label="HTTP Headers (Encrypted)"
                  description="Sent on HTTP/SSE requests (e.g. Authorization). Untouched stored keys stay encrypted."
                  keyPlaceholder="Authorization"
                  valuePlaceholder="Bearer ..."
                  existingMasked={editingServer?.headersMasked ?? {}}
                  entries={headerEntries}
                  removedKeys={removedHeaderKeys}
                  onEntriesChange={setHeaderEntries}
                  onRemovedKeysChange={setRemovedHeaderKeys}
                  testIdPrefix="mcp-catalog-headers"
                />
              )}
            </div>

            <div className="px-5 py-3 border-t border-slate-800 bg-slate-950 flex items-center justify-end gap-2 shrink-0 pb-[max(0.75rem,env(safe-area-inset-bottom))]">
              <button
                type="button"
                onClick={() => {
                  setIsFormOpen(false);
                  setEditingServer(null);
                  setError(null);
                }}
                className="px-3.5 py-1.5 rounded-lg text-xs font-medium text-slate-400 hover:text-white transition cursor-pointer"
              >
                Back to Catalog
              </button>
              <button
                type="submit"
                disabled={saving}
                data-testid="mcp-catalog-save-btn"
                className="px-4 py-1.5 rounded-lg text-xs font-semibold bg-sky-600 hover:bg-sky-500 text-white transition cursor-pointer disabled:opacity-50 flex items-center gap-1.5"
              >
                <Check className="w-3.5 h-3.5" />
                <span>{editingServer ? 'Save Server' : 'Create Server'}</span>
              </button>
            </div>
          </form>
        ) : (
          <>
            {/* Search & Filters */}
            <div className="px-5 py-3 border-b border-slate-800/80 flex flex-col sm:flex-row gap-2 shrink-0">
              <div className="relative flex-1">
                <Search className="w-3.5 h-3.5 text-slate-500 absolute left-3 top-1/2 -translate-y-1/2" />
                <input
                  type="text"
                  aria-label="Filter MCP servers"
                  placeholder="Search by name, id, command, or URL..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg pl-8 pr-3 py-1.5 text-xs text-white focus:outline-none focus:border-sky-500"
                />
              </div>
              <div className="flex items-center gap-1.5">
                {(['all', McpTransportType.Stdio, McpTransportType.Http, McpTransportType.Sse] as const).map(
                  (t) => (
                    <button
                      key={t}
                      type="button"
                      onClick={() => setTransportFilter(t)}
                      className={`px-2.5 py-1.5 rounded-lg text-[11px] font-medium transition cursor-pointer ${
                        transportFilter === t
                          ? 'bg-sky-600/25 text-sky-300 border border-sky-500/40'
                          : 'bg-slate-950 text-slate-400 border border-slate-800 hover:text-slate-200'
                      }`}
                    >
                      {t === 'all' ? 'All' : t}
                    </button>
                  )
                )}
              </div>
            </div>

            {/* Catalog List */}
            <div className="flex-1 overflow-y-auto p-5 space-y-3">
              {loading && servers.length === 0 ? (
                <div className="py-12 flex flex-col items-center justify-center text-slate-400 text-xs gap-2">
                  <RefreshCw className="w-5 h-5 animate-spin text-sky-400" />
                  <span>Loading MCP server catalog...</span>
                </div>
              ) : filteredServers.length === 0 ? (
                <div className="p-6 rounded-xl border border-dashed border-slate-800 bg-slate-950/40 text-center space-y-2">
                  <Server className="w-8 h-8 text-slate-600 mx-auto" />
                  <h4 className="text-xs font-semibold text-slate-200">
                    {servers.length === 0 ? 'No MCP Servers Registered' : 'No Matching MCP Servers'}
                  </h4>
                  <p className="text-[11px] text-slate-400 max-w-md mx-auto">
                    {servers.length === 0
                      ? 'Add stdio or HTTP/SSE MCP servers to make external tools available for assignment to Personas.'
                      : 'Adjust your search query or transport filter.'}
                  </p>
                  {servers.length === 0 && (
                    <button
                      type="button"
                      onClick={openCreateForm}
                      className="mt-2 px-3 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-semibold inline-flex items-center gap-1.5 cursor-pointer"
                    >
                      <Plus className="w-3.5 h-3.5" />
                      <span>Register First MCP Server</span>
                    </button>
                  )}
                </div>
              ) : (
                filteredServers.map((server) => {
                  const envCount = Object.keys(server.envMasked || {}).length;
                  const headerCount = Object.keys(server.headersMasked || {}).length;
                  return (
                    <div
                      key={server.id}
                      data-testid={`mcp-catalog-item-${server.id}`}
                      className="rounded-xl border border-slate-800 bg-slate-950/60 p-3.5 space-y-2"
                    >
                      <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0 flex-1 space-y-1">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <span className="text-xs font-bold text-white">{server.name}</span>
                            <span className="text-[10px] font-mono text-slate-400">@{server.id}</span>
                            <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-900 border border-slate-700 text-sky-300">
                              {server.transport}
                            </span>
                            <span
                              className={`text-[10px] font-mono px-1.5 py-0.5 rounded border ${
                                server.enabled
                                  ? 'bg-emerald-950/50 border-emerald-500/30 text-emerald-300'
                                  : 'bg-slate-900 border-slate-700 text-slate-400'
                              }`}
                            >
                              {server.enabled ? 'Enabled' : 'Disabled'}
                            </span>
                            <span className="text-[10px] font-mono text-slate-500">
                              {server.timeoutSeconds}s timeout
                            </span>
                            <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-indigo-950/40 border border-indigo-500/30 text-indigo-300">
                              {server.assignedPersonaCount}{' '}
                              {server.assignedPersonaCount === 1 ? 'persona' : 'personas'}
                            </span>
                          </div>

                          {server.description && (
                            <p className="text-[11px] text-slate-400">{server.description}</p>
                          )}

                          <div className="text-[11px] font-mono text-slate-300 bg-slate-900/80 border border-slate-800/80 rounded-lg px-2.5 py-1.5 truncate">
                            {formatMcpTarget(server)}
                          </div>

                          {(envCount > 0 || headerCount > 0) && (
                            <div className="flex flex-wrap gap-1.5 pt-0.5">
                              {Object.entries(server.envMasked).map(([k, masked]) => (
                                <span
                                  key={`env-${k}`}
                                  className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-900 border border-slate-800 text-slate-300"
                                >
                                  env:{k}={masked}
                                </span>
                              ))}
                              {Object.entries(server.headersMasked).map(([k, masked]) => (
                                <span
                                  key={`hdr-${k}`}
                                  className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-slate-900 border border-slate-800 text-slate-300"
                                >
                                  hdr:{k}={masked}
                                </span>
                              ))}
                            </div>
                          )}
                        </div>

                        <div className="flex items-center gap-1.5 shrink-0">
                          <button
                            type="button"
                            onClick={() => handleToggleEnabled(server)}
                            disabled={saving}
                            data-testid={`mcp-catalog-toggle-${server.id}`}
                            className={`px-2 py-1 rounded-lg text-[11px] font-medium border transition cursor-pointer ${
                              server.enabled
                                ? 'bg-slate-900 border-slate-700 text-slate-300 hover:text-amber-300'
                                : 'bg-emerald-950/40 border-emerald-500/30 text-emerald-300 hover:bg-emerald-900/50'
                            }`}
                          >
                            {server.enabled ? 'Disable' : 'Enable'}
                          </button>
                          <button
                            type="button"
                            onClick={() => openEditForm(server)}
                            aria-label={`Edit ${server.name}`}
                            data-testid={`mcp-catalog-edit-${server.id}`}
                            className="p-1.5 rounded-lg bg-slate-900 border border-slate-800 text-slate-300 hover:text-white hover:border-slate-700 transition cursor-pointer"
                          >
                            <Edit3 className="w-3.5 h-3.5" />
                          </button>
                          <button
                            type="button"
                            onClick={() =>
                              setConfirmDeleteId((cur) => (cur === server.id ? null : server.id))
                            }
                            aria-label={`Delete ${server.name}`}
                            data-testid={`mcp-catalog-delete-${server.id}`}
                            className="p-1.5 rounded-lg bg-slate-900 border border-slate-800 text-slate-300 hover:text-rose-400 hover:border-rose-500/40 transition cursor-pointer"
                          >
                            <Trash2 className="w-3.5 h-3.5" />
                          </button>
                        </div>
                      </div>

                      {confirmDeleteId === server.id && (
                        <div className="p-2.5 rounded-lg bg-rose-950/60 border border-rose-500/40 flex items-center justify-between gap-2 text-xs text-rose-200">
                          <span>
                            Delete <strong>{server.name}</strong>?
                            {server.assignedPersonaCount > 0 &&
                              ` This will unassign it from ${server.assignedPersonaCount} persona(s).`}
                          </span>
                          <div className="flex items-center gap-1.5 shrink-0">
                            <button
                              type="button"
                              onClick={() => setConfirmDeleteId(null)}
                              className="px-2 py-1 rounded bg-slate-800 text-slate-300 hover:text-white text-[11px] cursor-pointer"
                            >
                              Cancel
                            </button>
                            <button
                              type="button"
                              onClick={() => handleDelete(server.id)}
                              disabled={saving}
                              data-testid={`mcp-catalog-confirm-delete-${server.id}`}
                              className="px-2.5 py-1 rounded bg-rose-600 hover:bg-rose-500 text-white font-semibold text-[11px] cursor-pointer"
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
