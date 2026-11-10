import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  AlertCircle,
  AlertTriangle,
  ArrowRightLeft,
  Check,
  ChevronRight,
  Copy,
  FileDiff,
  FilePlus2,
  FileMinus2,
  FileCode,
  Info,
  Loader2,
  RefreshCw,
  X
} from 'lucide-react';
import type { JobChangePatchResult, JobChangeSet, JobChangedFile } from '../types';
import { JobChangeSourceKind } from '../types';
import { api } from '../services/api';
import { parseDiffGitPath, parseUnifiedDiff, type DiffLine } from '../utils/unifiedDiff';

/**
 * The Cockpit's Changes panel: which files a job's branch touched, and the diff for the one you picked.
 *
 * ## Why the fetch lives here rather than in App.tsx
 * The Artifacts panel's state lives in App because SignalR folds live artifact events into the same
 * array the panel renders, and two copies would disagree the moment one lands. Nothing broadcasts a
 * change set: the truth is a `git` read behind an endpoint whose whole contract is "ask me now, I am
 * ten seconds from stale". Keeping the state here means the panel owns exactly the data it re-reads on
 * refresh, and App does not grow a fourth job-artifact-shaped array whose only writer is this view.
 *
 * ## Why the list is fetched on open rather than on job switch
 * Same reason the endpoints have a reuse window: answering the list costs process spawns. Artifacts are
 * fetched eagerly because that payload is an index read; paying for `git` on every job the operator
 * clicks through, most of which they will never inspect, is a cost with no buyer. The trade is that the
 * header toggle cannot show a count before first open — so it does not show one.
 *
 * ## Why "nothing to show" is never an error state here
 * The backend returns `200` with an explanatory `source` for every case except a nonexistent job,
 * because "the branch was never pushed" and "this deployment sees no checkout" are ordinary states of
 * the system. Rendering them through the error branch would put a red box on a healthy job, and the
 * operator would have no way to tell a misconfigured token from a job that has not run yet. The error
 * branch is therefore reserved for a genuine transport or server fault.
 */

/** Sentinel for "no rows", so the derived `files` keeps one stable identity across renders. */
const EMPTY_FILES: JobChangedFile[] = [];

/**
 * Separator for the diff request key. `\u0000` because a NUL cannot appear in a repository path, a git
 * status token, or a ref name — a `|` or `:` separator would let a path containing it produce the same
 * key as a different request, silently reusing one file's diff for another.
 */
const KEY_SEPARATOR = '\u0000';

/** A row's status, in the colour language the rest of the dashboard already uses. */
const statusChip = (status: string): { label: string; className: string; Icon: typeof FileCode } => {
  switch (status) {
    case 'added':
      return { label: 'A', className: 'bg-emerald-500/15 text-emerald-300 border-emerald-500/30', Icon: FilePlus2 };
    case 'deleted':
      return { label: 'D', className: 'bg-rose-500/15 text-rose-300 border-rose-500/30', Icon: FileMinus2 };
    case 'renamed':
    case 'copied':
      return { label: status === 'renamed' ? 'R' : 'C', className: 'bg-sky-500/15 text-sky-300 border-sky-500/30', Icon: ArrowRightLeft };
    case 'untracked':
      return { label: 'U', className: 'bg-amber-500/15 text-amber-300 border-amber-500/30', Icon: FilePlus2 };
    case 'typeChanged':
      return { label: 'T', className: 'bg-violet-500/15 text-violet-300 border-violet-500/30', Icon: FileCode };
    case 'ignored':
      return { label: 'I', className: 'bg-slate-500/15 text-slate-400 border-slate-600/40', Icon: FileCode };
    case 'modified':
      return { label: 'M', className: 'bg-indigo-500/15 text-indigo-300 border-indigo-500/30', Icon: FileCode };
    default:
      // An unmapped status is passed through and coloured neutrally rather than coerced to "modified",
      // so a git letter nobody predicted stays visible and diagnosable instead of quietly plausible.
      return { label: (status.slice(0, 1) || '?').toUpperCase(), className: 'bg-slate-500/15 text-slate-300 border-slate-600/40', Icon: FileCode };
  }
};

/** How the source that answered should be labelled above the diff. */
const sourceChip = (kind: string | null | undefined): { label: string; title: string; className: string } => {
  switch (kind) {
    case JobChangeSourceKind.LocalGit:
      return {
        label: 'working tree',
        title: 'Diffed against the checkout on this server — includes work that is not committed or pushed.',
        className: 'bg-emerald-500/10 text-emerald-300 border-emerald-500/25'
      };
    case JobChangeSourceKind.GitHubCompare:
      return {
        label: 'pushed branch',
        title: 'Read from GitHub\'s comparison of the pushed branch — committed and pushed work only.',
        className: 'bg-sky-500/10 text-sky-300 border-sky-500/25'
      };
    case JobChangeSourceKind.Checkpoint:
      return {
        label: 'checkpoint',
        title: 'The file list the harness measured at the last turn boundary. Names only: no line counts, no diff.',
        className: 'bg-amber-500/10 text-amber-300 border-amber-500/25'
      };
    default:
      return {
        label: 'unavailable',
        title: 'No source could answer.',
        className: 'bg-slate-500/10 text-slate-400 border-slate-600/40'
      };
  }
};

const shortSha = (sha?: string | null): string => (sha ? sha.slice(0, 7) : '—');

const fileNameOf = (path: string): string => {
  const at = path.lastIndexOf('/');
  return at >= 0 ? path.slice(at + 1) : path;
};

const dirOf = (path: string): string => {
  const at = path.lastIndexOf('/');
  return at > 0 ? path.slice(0, at) : '';
};

interface ChangesPanelProps {
  /** The job whose branch to inspect. Null renders the panel's empty state without fetching. */
  jobId: string | null;
  onClose: () => void;
  /**
   * Rendered in the header before the action buttons — the shared Artifacts/Changes tab strip, so the
   * operator can switch tabs from inside a fullscreen mobile drawer that covers the cockpit header.
   */
  headerAccessory?: React.ReactNode;
}

/**
 * Mounted only while its tab is the visible one, which is also its fetch trigger: the change set is a
 * `git`-backed read behind a ten-second server-side reuse window, so asking for it on every job the
 * operator clicks through — most of which they will never inspect — is a cost with no buyer. The
 * trade-off is that flipping to Artifacts and back re-reads and re-selects the first file; the reuse
 * window makes that cheap, and the refresh button is there when it is not.
 */
export const ChangesPanel: React.FC<ChangesPanelProps> = ({ jobId, onClose, headerAccessory }) => {
  const [changeSet, setChangeSet] = useState<JobChangeSet | null>(null);
  // Which job `changeSet` describes. Rendering compares against this rather than trusting that the
  // state was cleared in time: a value read for another job is not stale-by-timing, it is the wrong
  // answer, and the comparison says so at the point of use.
  const [loadedListJob, setLoadedListJob] = useState<string | null>(null);
  // The job a *failed* list read belongs to, for the same reason `loadedListJob` exists: an error about
  // job A must not be drawn over job B's panel. Deriving "in flight" from "nothing has settled for this
  // job yet" (see `loading` below) is what removes the flag entirely — a boolean set true before the
  // await and false after can be left true by a response that arrives for a superseded request, and the
  // panel then spins forever over data it already has.
  const [failedListJob, setFailedListJob] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  // A patch result tagged with the request it answers. Anything whose key does not match what is on
  // screen right now is not "an old result" — it is an answer to a different question, so the panel
  // treats it as absent. That is why there is no `patchLoading` flag to clear: "still loading" is
  // derived as `key !== loadedKey` (see below), which cannot be left true by a response that arrived
  // for a superseded request.
  const [patchState, setPatchState] = useState<{ key: string; result: JobChangePatchResult } | null>(null);
  const [showNotes, setShowNotes] = useState(false);
  const [copied, setCopied] = useState(false);

  // Last-write-wins guards. Both the list and the patch can be superseded while a request is in
  // flight — a refresh click, a rapid file selection, a job switch — and React gives no ordering
  // guarantee between the responses. Without a token, the slower response paints over the newer one,
  // which on this panel reads as "my click did nothing" or, worse, a diff under a file it does not
  // belong to.
  const listToken = useRef(0);
  const patchToken = useRef(0);

  const load = useCallback(
    async (refresh: boolean) => {
      if (!jobId) {
        // Invalidate in flight reads and stop. No state is written here on purpose: what the panel
        // renders for "no job" is decided during render by `visibleSet`/`visiblePatch` below, which
        // compare each value against the job it was read for. Clearing five pieces of state from inside
        // an effect instead would cost a cascading render for a value that is already known not to
        // apply, and it would still leave the previous job's rows in memory for the frame in between.
        listToken.current += 1;
        patchToken.current += 1;
        return;
      }

      const token = ++listToken.current;
      // A second token invalidates any patch in flight for the previous list, whose SHAs are about to
      // be replaced. The pane needs no explicit clear: its key contains the pinned pair, so a new list
      // that re-resolves it makes the old answer belong to a question nobody is asking any more.
      patchToken.current += 1;

      // Deliberately no state writes before the await: every write below happens after a response has
      // been accepted as current, which is what lets the effect that calls this load a job without
      // triggering a cascading render, and what keeps a refresh from blanking the rows on screen.
      try {
        const result = await api.getJobChanges(jobId, { refresh });
        if (token !== listToken.current) return;
        setLoadError(null);
        setFailedListJob(null);
        setLoadedListJob(jobId);
        setChangeSet(result);

        setSelectedPath((previous) => {
          // Keep the operator's selection across a refresh when the file is still in the change set —
          // a mid-turn refresh that jumps the diff back to another file is indistinguishable from a
          // bug. Drop it when it is gone, since the new list has nothing to select.
          if (previous && result.files.some((f) => f.path === previous)) return previous;
          return result.files.length > 0 ? result.files[0].path : null;
        });
      } catch (err) {
        if (token !== listToken.current) return;
        setLoadedListJob(null);
        setFailedListJob(jobId);
        setChangeSet(null);
        setLoadError(err instanceof Error ? err.message : 'Failed to load file changes.');
      }
    },
    [jobId]
  );

  // Fetch when the panel mounts and whenever it is pointed at a different job. `load` writes no state
  // before its await, so this effect performs no synchronous setState and costs one render, not a
  // cascade: the spinner comes from the derived `loading` above, which is true the moment the job
  // changes because nothing has settled for it yet.
  useEffect(() => {
    void load(false);
  }, [load]);

  // A re-read of the list is only trustworthy for the job it was requested for. `files` deliberately
  // falls back to a module-level empty array rather than a fresh `[]` literal: the array carried by
  // state is a stable identity, and the memo and the diff key below depend on that stability — a new
  // literal each render would recompute the selection and re-fire the diff request on every unrelated
  // re-render, which looks like a cache miss and behaves like a request storm.
  const visibleSet = loadedListJob !== null && loadedListJob === jobId ? changeSet : null;
  const files = visibleSet?.files ?? EMPTY_FILES;
  const selected = useMemo(
    () => files.find((f) => f.path === selectedPath) ?? null,
    [files, selectedPath]
  );

  /**
   * The identity of the question the diff pane is answering — job, path, status, preimage, and the
   * pinned commit pair — joined into one string.
   *
   * This doubles as the diff request's *parameter list*: the effect below recovers each argument from
   * the key rather than reading render state, which is what makes "same key ⇒ byte-identical request"
   * true by construction instead of by inspection. Two render passes that produce the same key therefore
   * cannot refetch, a list refresh that re-resolves the branch pair produces a new key and refetches on
   * its own, and the effect needs no ref to the row (a ref assigned during render is a React correctness
   * violation, not merely a lint warning).
   */
  const patchKey =
    jobId && selected?.patchAvailable
      ? [
          jobId,
          selected.path,
          selected.status,
          selected.previousPath ?? '',
          visibleSet?.baseSha ?? '',
          visibleSet?.headSha ?? ''
        ].join(KEY_SEPARATOR)
      : null;

  const patchResult = patchState && patchState.key === patchKey ? patchState.result : null;
  const patchLoading = patchKey !== null && patchState?.key !== patchKey;

  // "In flight" is a comparison, not a flag: nothing has settled for this job yet, nothing has failed
  // for it, and there is no error on screen. It cannot be left stuck true, because there is no `true`
  // anywhere to leave stuck.
  const loading =
    jobId !== null && loadedListJob !== jobId && failedListJob !== jobId && loadError === null;

  // An error is as much a per-job fact as a change set is. Without this check a fault read for job A
  // would stay on screen after the operator navigated to a project with no job selected at all — the
  // panel then showing "could not read this job's changes" beside "no job selected", two bodies and no
  // truthful one.
  const visibleError = failedListJob !== null && failedListJob === jobId ? loadError : null;

  useEffect(() => {
    if (!patchKey) return;

    const token = ++patchToken.current;
    const [keyJob, keyPath, keyStatus, keyPrevious, keyBase, keyHead] = patchKey.split(KEY_SEPARATOR);

    api
      .getJobChangePatch(keyJob, {
        path: keyPath,
        previousPath: keyPrevious || null,
        status: keyStatus,
        // Pin the pair the counts on screen came from. Without it a base branch that moves between the
        // list call and this one changes the diff under numbers the operator can no longer reconcile.
        baseSha: keyBase || null,
        headSha: keyHead || null
      })
      .then((result) => {
        if (token !== patchToken.current) return;
        setPatchState({ key: patchKey, result });
      })
      .catch((err: unknown) => {
        if (token !== patchToken.current) return;
        setPatchState({
          key: patchKey,
          result: {
            found: false,
            message: err instanceof Error ? err.message : 'Failed to load the diff.'
          }
        });
      });
    // `patchKey` is the only dependency on purpose, and it carries every argument of the request: adding
    // `jobId` or the change set object back would re-fire this effect whenever the list is re-read, even
    // when the pinned pair (and so the diff itself) is unchanged.
  }, [patchKey]);

  const parsedPatch = useMemo(() => {
    if (!patchResult || !patchResult.found) return null;
    // Three `.patch` segments, and they mean three different things: the request outcome, the file's
    // diff payload, and the patch text inside it. The DTO's field name is fixed by the wire contract,
    // so the local that holds the *result* is the one renamed to keep the chain readable.
    return parseUnifiedDiff(patchResult.patch.patch);
  }, [patchResult]);

  const handleCopyPatch = async () => {
    const body = patchResult?.found ? patchResult.patch.patch : '';
    if (!body) return;
    try {
      await navigator.clipboard.writeText(body);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard refusal (insecure origin, denied permission) leaves the patch on screen to select.
    }
  };

  const source = visibleSet?.source;
  const chip = sourceChip(source?.kind);
  const stats = visibleSet?.stats;
  const notes = visibleSet?.notes ?? [];

  return (
    <div
      className="h-full flex flex-col min-h-0 bg-slate-950 border-l border-slate-800 select-text"
      data-testid="changes-panel"
    >
      {/* Header */}
      <div className="p-3 border-b border-slate-800 flex items-center justify-between gap-2 shrink-0 bg-slate-900/50 pt-[max(0.75rem,env(safe-area-inset-top))]">
        <div className="flex items-center gap-2 min-w-0">
          <FileDiff className="w-4 h-4 text-emerald-400 shrink-0" />
          <span className="font-semibold text-xs text-white shrink-0">Changes</span>
          {typeof stats?.filesChanged === 'number' && (
            <span
              className="px-1.5 py-0.5 rounded-full text-[10px] font-bold bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 shrink-0"
              data-testid="changes-count"
            >
              {stats.filesChanged}
            </span>
          )}
          {/* Hidden when no job is selected: with nothing asked there is no source to name, and
              drawing "unavailable" would read as "a source was tried and failed" — the exact wrong
              diagnosis the no-job state above exists to avoid. */}
          {jobId !== null && (
            <span
              className={`px-1.5 py-0.5 rounded-full text-[10px] font-semibold border shrink-0 ${chip.className}`}
              title={chip.title}
              data-testid="changes-source-chip"
            >
              {chip.label}
            </span>
          )}
        </div>

        <div className="flex items-center gap-1.5 shrink-0">
          {headerAccessory}
          <button
            type="button"
            onClick={() => void load(true)}
            disabled={loading}
            title="Re-read the change set from the source"
            data-testid="changes-refresh-btn"
            className="p-1.5 rounded-md text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer min-w-[32px] min-h-[32px] flex items-center justify-center disabled:opacity-50"
          >
            {/* Spins while the *first* read for this job is outstanding. A refresh of the job already
                on screen deliberately does not spin: the rows stay put and are replaced in place, and an
                in-flight flag that also covered that case would have to be a boolean set before the
                await — the one shape that can be left stuck true by a response superseded mid-flight. */}
            <RefreshCw className={`w-3.5 h-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button
            type="button"
            onClick={onClose}
            title="Close changes panel"
            className="p-1.5 rounded-md text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer min-w-[32px] min-h-[32px] flex items-center justify-center"
          >
            <X className="w-4 h-4" />
          </button>
        </div>
      </div>

      {/* Branch identity line: which two commits the numbers below describe. */}
      {visibleSet && (
        <div
          className="px-3 py-2 border-b border-slate-800/80 bg-slate-900/30 flex flex-wrap items-center gap-x-2 gap-y-1 text-[10px] font-mono shrink-0"
          data-testid="changes-identity"
        >
          {visibleSet.branch ? (
            <span className="text-slate-300 truncate max-w-full" title={visibleSet.branch}>
              {visibleSet.branch}
            </span>
          ) : (
            <span className="text-slate-500">no branch</span>
          )}
          {visibleSet.baseBranch && (
            <>
              <span className="text-slate-600">vs</span>
              <span className="text-slate-400 truncate max-w-full" title={visibleSet.baseBranch}>
                {visibleSet.baseBranch}
              </span>
            </>
          )}
          {visibleSet.headSha && (
            <span className="text-slate-500" title={`head ${visibleSet.headSha}`}>
              @{shortSha(visibleSet.headSha)}
            </span>
          )}
          {typeof stats?.additions === 'number' && stats.additions > 0 && (
            <span className="text-emerald-400">+{stats.additions}</span>
          )}
          {typeof stats?.deletions === 'number' && stats.deletions > 0 && (
            <span className="text-rose-400">-{stats.deletions}</span>
          )}
        </div>
      )}

      {/* Why the answer looks the way it does: degraded source, or nothing to read at all. */}
      {(source?.degraded || source?.committedOnly) && source?.message && (
        <div
          className="mx-3 mt-3 p-2.5 rounded-lg border bg-amber-950/30 border-amber-600/30 text-[11px] text-amber-200 flex items-start gap-2"
          data-testid="changes-degraded-note"
        >
          <AlertTriangle className="w-3.5 h-3.5 shrink-0 mt-0.5" />
          <span className="leading-relaxed">{source.message}</span>
        </div>
      )}

      {/* The fault branch: transport/server failure only. Everything the backend can explain arrives as
          a 200 and renders through the empty state below. */}
      {visibleError && (
        <div className="flex-1 flex flex-col items-center justify-center p-6 text-center space-y-3">
          <AlertCircle className="w-9 h-9 text-rose-400/80" />
          <div className="text-xs font-semibold text-slate-300">Could not read this job&apos;s changes</div>
          <p className="text-[11px] text-slate-500 max-w-xs leading-relaxed break-words" data-testid="changes-error">
            {visibleError}
          </p>
          {/* The retry carries no spinner of its own: every write in `load` happens after a response is
              accepted, so there is no in-flight boolean to draw from here without reintroducing exactly
              the stuck-spinner failure mode the derived `loading` removes. Retrying a job whose read
              failed replaces this box on success; the cost is a brief moment with no feedback. */}
          <button
            type="button"
            onClick={() => void load(true)}
            data-testid="changes-retry-btn"
            className="px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 text-[11px] font-medium transition cursor-pointer"
          >
            Try again
          </button>
        </div>
      )}

      {/* Gated on `visibleSet`, not `changeSet`: during a job switch the previous job's rows are still
          in memory, and treating them as "something is already on screen" would hide the spinner for the
          job the operator actually clicked into. */}
      {!visibleError && loading && !visibleSet && (
        <div className="flex-1 flex flex-col items-center justify-center gap-2 text-slate-500">
          <Loader2 className="w-8 h-8 animate-spin text-emerald-400/70" />
          <div className="text-[11px] font-mono">Reading git…</div>
        </div>
      )}

      {/* No job to ask about: the column was left open across a project switch or a job deletion, so
          there is no id to query. This is deliberately its own state rather than sharing the "nothing
          could be read" message below — telling an operator that a branch produced no changes, when the
          app simply has no job selected, blames the work for a navigation they made. */}
      {jobId === null && (
        <div className="flex-1 flex flex-col items-center justify-center p-6 text-center text-slate-500 space-y-2.5">
          <FileDiff className="w-10 h-10 text-slate-700" />
          <div className="text-xs font-semibold text-slate-400" data-testid="changes-no-job">
            No job selected
          </div>
          <p className="text-[11px] text-slate-500 max-w-xs leading-relaxed">
            Select a job to see which files its branch changed.
          </p>
        </div>
      )}

      {/* Nothing-readable state: the reason, plus the checkpoint's file list if one exists. */}
      {jobId !== null && !visibleError && !loading && visibleSet && files.length === 0 && (
        <div className="flex-1 flex flex-col items-center justify-center p-6 text-center text-slate-500 space-y-2.5">
          <FileDiff className="w-10 h-10 text-slate-700" />
          <div className="text-xs font-semibold text-slate-400" data-testid="changes-empty-title">
            {source?.kind ? 'No file changes in this source' : 'No file changes to show yet'}
          </div>
          {source?.message && (
            <p className="text-[11px] text-slate-500 max-w-xs leading-relaxed" data-testid="changes-empty-reason">
              {source.message}
            </p>
          )}
          {!source?.message && (
            <p className="text-[11px] text-slate-500 max-w-xs leading-relaxed">
              When this job&apos;s branch touches files, they will be listed here with their diffs.
            </p>
          )}
          <NotesToggle notes={notes} open={showNotes} onToggle={() => setShowNotes((v) => !v)} />
        </div>
      )}

      {files.length > 0 && (
        <>
          {/* File list */}
          <div
            className="shrink-0 max-h-[38%] overflow-y-auto border-b border-slate-800"
            data-testid="changes-file-list"
          >
            {files.map((file) => (
              <FileRow
                key={`${file.path}:${file.status}`}
                file={file}
                active={file.path === selectedPath}
                onSelect={() => setSelectedPath(file.path)}
              />
            ))}

            <div className="px-3 py-2 text-[10px] text-slate-500 font-mono flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-slate-800/60">
              <span>
                showing {files.length} of {stats?.filesChanged ?? files.length}
              </span>
              {(stats?.omittedFiles ?? 0) > 0 && (
                <span className="text-amber-400/80">({stats?.omittedFiles} more not listed)</span>
              )}
              {(visibleSet?.uncommittedCount ?? 0) > 0 && (
                <span className="text-amber-300/80" title="Work in the checkout that is not committed on the branch.">
                  {visibleSet?.uncommittedCount} uncommitted
                </span>
              )}
              <NotesToggle notes={notes} open={showNotes} onToggle={() => setShowNotes((v) => !v)} />
            </div>
          </div>

          {/* Diff pane */}
          <div className="flex-1 flex flex-col min-h-0">
            {selected && (
              <div className="px-3 py-2 border-b border-slate-800/80 bg-slate-900/40 flex items-center justify-between gap-2 shrink-0">
                <div className="min-w-0">
                  <div className="font-mono text-xs font-semibold text-white truncate flex items-center gap-1.5">
                    <span data-testid="changes-diff-path">{fileNameOf(selected.path)}</span>
                    {selected.binary && (
                      <span className="text-[10px] px-1 rounded bg-slate-700/60 text-slate-300 font-sans">
                        binary
                      </span>
                    )}
                  </div>
                  <div className="text-[10px] font-mono text-slate-500 truncate" title={selected.path}>
                    {selected.previousPath ? `${selected.previousPath} → ${selected.path}` : selected.path}
                  </div>
                </div>
                <div className="flex items-center gap-1 shrink-0">
                  {patchResult?.found && patchResult.patch.patch && (
                    <button
                      type="button"
                      onClick={handleCopyPatch}
                      title="Copy this patch"
                      data-testid="changes-copy-patch"
                      className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                    >
                      {copied ? <Check className="w-3.5 h-3.5 text-emerald-400" /> : <Copy className="w-3.5 h-3.5" />}
                    </button>
                  )}
                </div>
              </div>
            )}

            <div className="flex-1 overflow-auto min-h-0 pb-[max(1rem,env(safe-area-inset-bottom))]">
              {!selected && (
                <Empty text="Select a file above to see its diff." />
              )}

              {selected && !selected.patchAvailable && (
                <div className="p-4">
                  <div className="p-3 rounded-xl border bg-slate-900/50 border-slate-800 text-[11px] text-slate-400 flex items-start gap-2.5 leading-relaxed">
                    <Info className="w-4 h-4 shrink-0 mt-0.5 text-amber-400/80" />
                    <span>
                      This list came from the working checkpoint, which stores file names rather than
                      content, so there is no diff behind it. Push the branch or open a checkout this
                      server can read to get diffs.
                    </span>
                  </div>
                </div>
              )}

              {selected && selected.patchAvailable && patchLoading && (
                <div className="h-full flex items-center justify-center gap-2 text-slate-500 text-[11px] font-mono">
                  <Loader2 className="w-4 h-4 animate-spin" />
                  <span>Reading diff…</span>
                </div>
              )}

              {selected && selected.patchAvailable && !patchLoading && patchResult && !patchResult.found && (
                <div className="p-4 space-y-2">
                  <div className="p-3 rounded-xl border bg-amber-950/25 border-amber-600/30 text-[11px] text-amber-200 flex items-start gap-2.5 leading-relaxed">
                    <AlertTriangle className="w-4 h-4 shrink-0 mt-0.5" />
                    <div className="min-w-0">
                      <div>{patchResult.message}</div>
                      {patchResult.reasonCode && (
                        <div className="mt-1 text-[10px] font-mono text-amber-300/70">
                          {patchResult.reasonCode}
                        </div>
                      )}
                    </div>
                  </div>
                  {patchResult.notes && patchResult.notes.length > 0 && (
                    <ul className="text-[10px] font-mono text-slate-500 space-y-0.5 pl-1">
                      {patchResult.notes.map((note, i) => (
                        <li key={i} className="leading-relaxed break-words">
                          {note}
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}

              {selected && selected.patchAvailable && !patchLoading && parsedPatch && parsedPatch.empty && (
                <Empty
                  text={
                    selected.binary
                      ? 'Binary file — git reports no textual diff.'
                      : 'No textual changes for this file (a rename detected with no edits, or an empty body).'
                  }
                />
              )}

              {selected && selected.patchAvailable && !patchLoading && parsedPatch && !parsedPatch.empty && (
                <div className="font-mono text-[11px] leading-[1.45]">
                  {patchResult?.found && patchResult.patch.truncated && (
                    <div className="px-3 py-2 bg-amber-950/40 border-b border-amber-600/30 text-[10px] text-amber-200 flex items-center gap-2">
                      <AlertTriangle className="w-3.5 h-3.5 shrink-0" />
                      <span>
                        This diff is larger than the server sends and is cut off partway through. Open
                        the branch to read the rest.
                      </span>
                    </div>
                  )}

                  {/* A whole-branch body carries several files' headers; a per-file body carries one. In
                      both cases the labelled header comes from the patch itself rather than a second
                      request per file. */}
                  {parsedPatch.binary && (
                    <div className="px-3 py-2 text-[10px] text-slate-400 border-b border-slate-800/60">
                      Binary file — no textual diff.
                    </div>
                  )}

                  <DiffBody lines={parsedPatch.lines} />
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  );
};

const Empty: React.FC<{ text: string }> = ({ text }) => (
  <div className="h-full flex items-center justify-center p-6 text-center text-[11px] text-slate-500 max-w-xs mx-auto leading-relaxed">
    {text}
  </div>
);

/** The "why is this the answer" detail, collapsed by default: it is diagnostic, not the headline. */
const NotesToggle: React.FC<{ notes: string[]; open: boolean; onToggle: () => void }> = ({
  notes,
  open,
  onToggle
}) => {
  if (!notes || notes.length === 0) return null;

  return (
    <div className="w-full text-left">
      <button
        type="button"
        onClick={onToggle}
        data-testid="changes-notes-toggle"
        className="inline-flex items-center gap-1 text-[10px] text-slate-400 hover:text-slate-200 transition cursor-pointer font-mono"
      >
        <ChevronRight className={`w-3 h-3 transition-transform ${open ? 'rotate-90' : ''}`} />
        <span>{notes.length} note{notes.length === 1 ? '' : 's'} about what was read</span>
      </button>
      {open && (
        <ul className="mt-1.5 space-y-1 text-[10px] font-mono text-slate-500 leading-relaxed" data-testid="changes-notes">
          {notes.map((note, i) => (
            <li key={i} className="break-words">
              · {note}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
};

const FileRow: React.FC<{
  file: JobChangedFile;
  active: boolean;
  onSelect: () => void;
}> = ({ file, active, onSelect }) => {
  const { label, className, Icon } = statusChip(file.status);
  const dir = dirOf(file.path);

  return (
    <button
      type="button"
      onClick={onSelect}
      aria-current={active ? 'true' : undefined}
      data-testid="changes-file-row"
      data-path={file.path}
      className={`w-full text-left px-3 py-1.5 flex items-start gap-2 border-b border-slate-800/40 transition ${
        active ? 'bg-indigo-500/10' : 'hover:bg-slate-900/60'
      }`}
    >
      <span
        className={`mt-0.5 shrink-0 w-4 h-4 rounded border text-[9px] font-bold flex items-center justify-center ${className}`}
        title={file.status}
      >
        {label}
      </span>

      <span className="min-w-0 flex-1">
        <span className="block font-mono text-[11px] text-slate-200 break-all leading-tight">
          {fileNameOf(file.path)}
        </span>
        {dir && (
          <span className="block font-mono text-[10px] text-slate-600 truncate leading-tight" title={file.path}>
            {dir}
          </span>
        )}
        {file.previousPath && (
          <span
            className="block font-mono text-[10px] text-sky-400/80 truncate leading-tight"
            title={`${file.previousPath} → ${file.path}`}
          >
            from {fileNameOf(file.previousPath)}
          </span>
        )}
      </span>

      <span className="shrink-0 flex items-center gap-1.5 pt-0.5">
        {!file.patchAvailable && (
          <span className="text-[9px] font-mono text-slate-600" title="No diff available from this source">
            no diff
          </span>
        )}
        {file.binary ? (
          <span className="text-[10px] font-mono text-slate-500">bin</span>
        ) : (
          <>
            {typeof file.additions === 'number' && (
              <span className="text-[10px] font-mono text-emerald-400">+{file.additions}</span>
            )}
            {typeof file.deletions === 'number' && (
              <span className="text-[10px] font-mono text-rose-400">-{file.deletions}</span>
            )}
            {file.additions === null && file.deletions === null && file.patchAvailable && (
              <Icon className="w-3 h-3 text-slate-600" />
            )}
          </>
        )}
      </span>
    </button>
  );
};

/** The patch body: one row per line, gutter numbers on the left, background colour by kind. */
const DiffBody: React.FC<{ lines: DiffLine[] }> = ({ lines }) => (
  <div data-testid="changes-diff-body" className="min-w-full w-max">
    {lines.map((line) => (
      <DiffRow key={line.id} line={line} />
    ))}
  </div>
);

const rowTone = (kind: DiffLine['kind']): string => {
  switch (kind) {
    case 'add':
      return 'bg-emerald-500/12 text-emerald-200 border-l-2 border-emerald-500/70';
    case 'del':
      return 'bg-rose-500/12 text-rose-200 border-l-2 border-rose-500/70';
    case 'hunk':
      return 'bg-sky-500/10 text-sky-300 border-l-2 border-sky-500/40';
    case 'file':
      return 'bg-slate-800/70 text-slate-200 border-l-2 border-slate-600 font-semibold';
    case 'meta':
      return 'text-slate-500 border-l-2 border-transparent';
    case 'plain':
      return 'text-slate-400 border-l-2 border-transparent';
    default:
      return 'text-slate-400 bg-slate-950 border-l-2 border-transparent';
  }
};

const DiffRow: React.FC<{ line: DiffLine }> = ({ line }) => {
  const text = line.kind === 'file' ? (parseDiffGitPath(line.text) ?? line.text) : line.text;

  return (
    <div
      className={`flex items-start whitespace-pre ${rowTone(line.kind)}`}
      data-diff-kind={line.kind}
    >
      <span className="select-none shrink-0 w-10 text-right pr-1.5 text-[10px] text-slate-600 tabular-nums leading-[1.45]">
        {line.oldLine ?? ''}
      </span>
      <span className="select-none shrink-0 w-10 text-right pr-2 text-[10px] text-slate-600 tabular-nums leading-[1.45]">
        {line.newLine ?? ''}
      </span>
      <span className="pr-4">{text.length > 0 ? text : ' '}</span>
    </div>
  );
};
