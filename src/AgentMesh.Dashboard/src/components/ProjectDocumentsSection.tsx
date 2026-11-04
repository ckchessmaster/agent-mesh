import React, { useCallback, useEffect, useRef, useState } from 'react';
import { FileText, Image as ImageIcon, Link2, Loader2, Plus, Trash2, Upload } from 'lucide-react';
import { api } from '../services/api';
import type { ProjectDocument } from '../types';
import { ProjectDocumentKind } from '../types';

/**
 * The supporting-document library of one project: list, add pasted text, upload files, remove.
 *
 * Why this is its own component rather than another block inside ProjectModal: it owns state that the
 * project form does not (the library itself, an in-flight upload, a file input) and it talks to the API on
 * its own schedule. ProjectModal's save is a single PUT of the project row; a document is a separate
 * multipart POST that takes effect immediately, because the library is useful to a running job the moment
 * it is attached and making the user press "Update Project" to publish a spec would be wrong.
 *
 * Two consequences of living inside a <form> element, both easy to get wrong and both handled here:
 * every button in this component is type="button", so clicking "Add" cannot submit the project form and
 * close the modal mid-upload; and the file input is a controlled-by-ref element rather than a form field,
 * so the browser does not try to include it in the project's own submission.
 */

interface ProjectDocumentsSectionProps {
  /** The project whose library this is. Null when the project has not been created yet. */
  projectId: string | null;
}

const MAX_FILE_BYTES = 25 * 1024 * 1024;
const MAX_FILES_PER_REQUEST = 10;
const MAX_TEXT_CHARS = 200_000;

const formatSize = (bytes: number): string => {
  if (!bytes || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  let size = bytes;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  return `${size.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
};

export const ProjectDocumentsSection: React.FC<ProjectDocumentsSectionProps> = ({ projectId }) => {
  const [documents, setDocuments] = useState<ProjectDocument[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [title, setTitle] = useState('');
  const [content, setContent] = useState('');
  const [pendingFiles, setPendingFiles] = useState<File[]>([]);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const load = useCallback(async () => {
    if (!projectId) {
      setDocuments([]);
      return;
    }
    setLoading(true);
    setError(null);
    try {
      setDocuments(await api.getProjectDocuments(projectId));
    } catch (err: any) {
      // A library that will not load is shown as an error with a retry rather than as an empty list: an
      // empty list reads as "nobody has attached anything", and a user who knows they attached a spec
      // would re-attach a duplicate to work around a transient network failure.
      setError(err?.message || 'Failed to load project documents.');
      setDocuments([]);
    } finally {
      setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  // Client-side mirrors of the API's limits. They are not a substitute for the server's — the API is
  // authoritative and returns its own 400 — but catching an over-capacity file before a 25 MB upload
  // crosses the network is the difference between an instant message and a spinner for a minute.
  const validatePending = (files: File[]): string | null => {
    if (files.length > MAX_FILES_PER_REQUEST) {
      return `A maximum of ${MAX_FILES_PER_REQUEST} files can be uploaded at once.`;
    }
    const oversized = files.find((f) => f.size > MAX_FILE_BYTES);
    if (oversized) {
      return `'${oversized.name}' exceeds the 25 MB maximum file size limit.`;
    }
    const empty = files.find((f) => f.size === 0);
    if (empty) return `'${empty.name}' is empty.`;
    return null;
  };

  const handleFilesChosen = (event: React.ChangeEvent<HTMLInputElement>) => {
    const chosen = Array.from(event.target.files ?? []);
    const problem = validatePending(chosen);
    if (problem) {
      setError(problem);
      // Clear the input so re-picking the same rejected file fires a change event. Without this the
      // second attempt at the same file is silently a no-op.
      event.target.value = '';
      return;
    }
    setError(null);
    setPendingFiles(chosen);
  };

  const resetComposer = () => {
    setTitle('');
    setContent('');
    setPendingFiles([]);
    if (fileInputRef.current) fileInputRef.current.value = '';
  };

  const handleAdd = async () => {
    if (!projectId) return;

    const trimmedTitle = title.trim();
    const trimmedContent = content.trim();
    const hasText = trimmedContent.length > 0;

    if (!hasText && pendingFiles.length === 0) {
      setError('Type a document or choose a file to upload.');
      return;
    }
    if (hasText && trimmedContent.length > MAX_TEXT_CHARS) {
      setError(
        `That document is ${trimmedContent.length} characters, over the ${MAX_TEXT_CHARS} limit. Upload it as a .md or .txt file instead.`
      );
      return;
    }

    setSaving(true);
    setError(null);
    try {
      // One call carries both kinds, which is why the composer has one submit rather than two: a user who
      // typed notes and attached a screenshot expects both to land or neither to.
      const created = await api.addProjectDocument(projectId, {
        title: trimmedTitle || undefined,
        content: hasText ? trimmedContent : undefined,
        files: pendingFiles.length ? pendingFiles : undefined
      });
      resetComposer();
      // The response is the authoritative list of what was created, but the library is re-read rather than
      // appended to locally: the server assigns ids, titles (a file document defaults to its filename) and
      // timestamps, and a locally-constructed row would drift from what a reload shows.
      await load();
      if (!created.length) {
        setError('The server accepted the request but returned no documents.');
      }
    } catch (err: any) {
      setError(err?.message || 'Failed to add document.');
    } finally {
      setSaving(false);
    }
  };

  const handleRemove = async (doc: ProjectDocument) => {
    if (!projectId) return;

    // A document is attached once and consulted by every job afterwards, so removing it is a change other
    // people's running work depends on. The confirm is the only guard: there is no undo, and the row is
    // gone before the local list updates.
    const label = doc.title || doc.fileName || doc.id;
    if (!window.confirm(`Remove "${label}" from this project's documents? Jobs that rely on it will lose access.`)) {
      return;
    }

    setSaving(true);
    setError(null);
    try {
      await api.deleteProjectDocument(projectId, doc.id);
      setDocuments((prev) => prev.filter((d) => d.id !== doc.id));
    } catch (err: any) {
      // Order matters. load() clears the error banner on a successful re-read, so setting the message
      // first and reloading second would wipe the very failure being reported: the row correctly comes
      // back with no explanation of why it came back. Recover the list, then say what happened.
      //
      // The optimistic filter above has to be undone by that re-read in the first place, or the row stays
      // gone from a library the server still has.
      await load();
      setError(err?.message || 'Failed to remove document.');
    } finally {
      setSaving(false);
    }
  };

  if (!projectId) {
    return (
      <div className="rounded-xl border border-slate-800 bg-slate-950/60 p-3.5">
        <p className="text-xs text-slate-400">
          Save this project first to attach supporting documents (specs, API contracts, screenshots).
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3">
        <label className="block text-xs font-semibold text-slate-300">
          Supporting Documents{' '}
          <span className="text-slate-500 font-normal">
            ({documents.length})
          </span>
        </label>
        <button
          type="button"
          onClick={() => void load()}
          disabled={loading || saving}
          className="text-[11px] text-slate-400 hover:text-slate-200 transition disabled:opacity-40"
        >
          {loading ? 'Reloading...' : 'Reload'}
        </button>
      </div>

      <p className="text-[11px] text-slate-500 leading-relaxed">
        Attached once, available to every job in this project. Agents are told these exist and receive the
        uploaded files in their workspace.
      </p>

      {error && (
        <div className="p-2.5 bg-red-500/10 border border-red-500/30 rounded-xl text-[11px] text-red-300">
          {error}
        </div>
      )}

      {documents.length > 0 ? (
        <ul className="space-y-1.5 max-h-44 overflow-y-auto pr-1">
          {documents.map((doc) => {
            const isFile = doc.kind === ProjectDocumentKind.File;
            const Icon = isFile
              ? (doc.contentType || '').startsWith('image/')
                ? ImageIcon
                : FileText
              : Link2;
            const href = doc.downloadUrl || doc.forceDownloadUrl;

            return (
              <li
                key={doc.id}
                className="flex items-center gap-2.5 bg-slate-950 border border-slate-800 rounded-xl px-3 py-2"
              >
                <Icon className="w-3.5 h-3.5 shrink-0 text-slate-500" />
                <div className="min-w-0 flex-1">
                  <div className="text-xs text-slate-200 truncate">{doc.title}</div>
                  <div className="text-[10px] text-slate-500 truncate">
                    {isFile
                      ? `${doc.fileName || 'file'} · ${formatSize(doc.sizeBytes)}`
                      : `text · ${formatSize(doc.sizeBytes)}`}
                  </div>
                </div>
                {href ? (
                  <a
                    href={href}
                    target="_blank"
                    rel="noreferrer"
                    className="text-[11px] text-sky-400 hover:text-sky-300 shrink-0"
                  >
                    Open
                  </a>
                ) : null}
                <button
                  type="button"
                  onClick={() => void handleRemove(doc)}
                  disabled={saving}
                  aria-label={`Remove ${doc.title}`}
                  title="Remove document"
                  className="p-1 rounded-lg text-slate-500 hover:text-red-400 hover:bg-slate-800 transition disabled:opacity-40 shrink-0"
                >
                  <Trash2 className="w-3.5 h-3.5" />
                </button>
              </li>
            );
          })}
        </ul>
      ) : (
        <div className="text-[11px] text-slate-500 bg-slate-950/60 border border-slate-800 rounded-xl px-3 py-2.5">
          {loading ? 'Loading documents...' : 'No documents attached yet.'}
        </div>
      )}

      <div className="space-y-2 bg-slate-950/40 border border-slate-800 rounded-xl p-3">
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
          <input
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Title (optional for files)"
            className="bg-slate-950 border border-slate-800 rounded-xl px-3 py-2 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition"
          />
          <div className="flex items-center gap-2">
            <input
              ref={fileInputRef}
              type="file"
              multiple
              onChange={handleFilesChosen}
              className="block w-full text-[11px] text-slate-400 file:mr-2 file:px-3 file:py-1.5 file:rounded-lg file:border-0 file:bg-slate-800 file:text-[11px] file:font-semibold file:text-slate-200 hover:file:bg-slate-700 file:cursor-pointer"
            />
          </div>
        </div>

        {pendingFiles.length > 0 && (
          <div className="text-[10px] text-slate-400">
            {pendingFiles.length} file{pendingFiles.length === 1 ? '' : 's'} ready:
            {' '}
            {pendingFiles.map((f) => `${f.name} (${formatSize(f.size)})`).join(', ')}
            {' '}
            <button
              type="button"
              onClick={resetComposer}
              className="text-slate-500 hover:text-slate-300 underline"
            >
              clear
            </button>
          </div>
        )}

        <textarea
          rows={3}
          value={content}
          onChange={(e) => setContent(e.target.value)}
          placeholder="...or paste a document here: an API contract, a design note, a runbook."
          className="w-full bg-slate-950 border border-slate-800 rounded-xl p-3 text-xs text-slate-100 placeholder-slate-500 focus:outline-none focus:border-sky-500 transition font-mono leading-relaxed"
        />

        <div className="flex items-center justify-between gap-2">
          <span className="text-[10px] text-slate-500">
            {content.trim().length.toLocaleString()} / {MAX_TEXT_CHARS.toLocaleString()}
          </span>
          <button
            type="button"
            onClick={() => void handleAdd()}
            disabled={saving || loading}
            className="px-3.5 py-1.5 bg-slate-800 hover:bg-slate-700 disabled:opacity-50 text-slate-100 text-[11px] font-semibold rounded-xl transition flex items-center gap-1.5"
          >
            {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Plus className="w-3.5 h-3.5" />}
            {saving ? 'Adding...' : 'Add document'}
          </button>
        </div>

        {pendingFiles.length > 0 && (
          <p className="text-[10px] text-slate-500 flex items-center gap-1">
            <Upload className="w-3 h-3" />
            Files and pasted text are submitted together in one request.
          </p>
        )}
      </div>
    </div>
  );
};

export default ProjectDocumentsSection;
