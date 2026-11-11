// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { ChangesPanel } from '../ChangesPanel';
import { api } from '../../services/api';
import type { JobChangePatchResult, JobChangeSet } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getJobChanges: vi.fn(),
    getJobChangePatch: vi.fn()
  }
}));

const mocked = api as unknown as Record<string, ReturnType<typeof vi.fn>>;

const file = (over: Partial<JobChangeSet['files'][number]> = {}) => ({
  path: 'src/A.cs',
  status: 'modified',
  previousPath: null,
  additions: 2,
  deletions: 1,
  binary: false,
  patchAvailable: true,
  ...over
});

const changeSet = (over: Partial<JobChangeSet> = {}): JobChangeSet => ({
  jobId: 'job-1',
  branch: 'mesh/job-1',
  baseBranch: 'main',
  headSha: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  baseSha: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
  stats: { filesChanged: 1, additions: 2, deletions: 1, omittedFiles: 0 },
  files: [file()],
  source: { kind: 'localGit', reasonCode: null, message: null, degraded: false, committedOnly: false, truncated: false, repository: null },
  commits: [],
  hasUncommittedChanges: false,
  uncommittedPaths: null,
  uncommittedCount: null,
  generatedAt: null,
  notes: null,
  ...over
});

const SAMPLE_PATCH = [
  'diff --git a/src/A.cs b/src/A.cs',
  'index 83db48f..1a2b3c4 100644',
  '--- a/src/A.cs',
  '+++ b/src/A.cs',
  '@@ -10,4 +10,5 @@ public class A',
  '     unchanged line',
  '-    var removed = true;',
  '+    var added = true;',
  '+    var addedToo = 2;',
  '     trailing line'
].join('\n');

const found = (patch = SAMPLE_PATCH): JobChangePatchResult => ({
  found: true,
  patch: {
    jobId: 'job-1',
    branch: 'mesh/job-1',
    baseBranch: 'main',
    headSha: 'a'.repeat(40),
    baseSha: 'b'.repeat(40),
    patch,
    path: 'src/A.cs',
    previousPath: null,
    status: 'modified',
    binary: false,
    empty: false,
    truncated: false,
    additions: 2,
    deletions: 1,
    contextLines: 3,
    source: null
  }
});

describe('ChangesPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocked.getJobChanges.mockResolvedValue(changeSet());
    mocked.getJobChangePatch.mockResolvedValue(found());
  });

  // ── fetching ──────────────────────────────────────────────────────────────────

  it('fetches the change set on mount and renders the file list', async () => {
    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-file-list')).toBeInTheDocument();
    });

    expect(mocked.getJobChanges).toHaveBeenCalledWith('job-1', { refresh: false });
    expect(screen.getByTestId('changes-count')).toHaveTextContent('1');
    expect(screen.getByTestId('changes-file-row')).toBeInTheDocument();
  });

  it('labels the source that answered, so a fallback is not mistaken for the working tree', async () => {
    mocked.getJobChanges.mockResolvedValue(
      changeSet({
        // The server composes this sentence; the panel shows it verbatim rather than inventing its own.
        source: {
          kind: 'gitHubCompare',
          reasonCode: null,
          message: 'The pushed branch as GitHub has it: committed and pushed work only.',
          degraded: true,
          committedOnly: true,
          truncated: false,
          repository: 'o/r'
        }
      })
    );

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-source-chip')).toHaveTextContent('pushed branch');
    });
    // The degraded banner explains what the source cannot see — the distinction the panel exists to make.
    expect(screen.getByTestId('changes-degraded-note')).toHaveTextContent(/committed and pushed/i);
  });

  it('shows the reason and no error box when nothing could be read at all', async () => {
    // The K8s / no-token case: a 200 with kind null. A red box here would be indistinguishable from a
    // job that has simply not run yet, which is the failure the backend's 200-with-reason contract
    // exists to prevent; the panel has to keep its half of that bargain.
    mocked.getJobChanges.mockResolvedValue(
      changeSet({
        files: [],
        stats: { filesChanged: 0, additions: 0, deletions: 0, omittedFiles: 0 },
        source: {
          kind: null,
          reasonCode: 'no_workspace',
          message: 'The control plane sees no working directory for this job.',
          degraded: false,
          committedOnly: false,
          truncated: false,
          repository: null
        },
        notes: ['Checkout: no_workspace — the control plane sees no working directory for this job.']
      })
    );

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-empty-reason')).toHaveTextContent(/no working directory/i);
    });
    expect(screen.queryByText(/Could not read this job/i)).not.toBeInTheDocument();
    expect(screen.getByTestId('changes-source-chip')).toHaveTextContent('unavailable');
    expect(screen.queryByTestId('changes-file-list')).not.toBeInTheDocument();

    // The notes are collapsed by default and expandable on demand.
    expect(screen.getByTestId('changes-notes-toggle')).toBeInTheDocument();
    fireEvent.click(screen.getByTestId('changes-notes-toggle'));
    expect(screen.getByTestId('changes-notes')).toHaveTextContent('no_workspace');
  });

  it('shows the checkpoint file list with no diff link when that is all there is', async () => {
    mocked.getJobChanges.mockResolvedValue(
      changeSet({
        files: [
          file({ path: 'src/A.cs', additions: null, deletions: null, patchAvailable: false }),
          file({ path: 'src/B.cs', additions: null, deletions: null, patchAvailable: false })
        ],
        source: { kind: 'checkpoint', reasonCode: null, message: 'The working checkpoint.', degraded: true, committedOnly: true, truncated: false, repository: null }
      })
    );

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getAllByTestId('changes-file-row')).toHaveLength(2);
    });

    // Rows say "no diff" rather than offering a link that would 404 on every click.
    expect(screen.getAllByText('no diff')).toHaveLength(2);
    expect(screen.getByTestId('changes-source-chip')).toHaveTextContent('checkpoint');
    // And the patch endpoint is never called for a source that cannot produce one.
    expect(mocked.getJobChangePatch).not.toHaveBeenCalled();
    expect(screen.getByText(/stores file names rather than content/i)).toBeInTheDocument();
  });

  it('routes a transport fault to the error branch with a retry, not to the empty state', async () => {
    mocked.getJobChanges.mockRejectedValue(new Error('Failed to load file changes: 500'));

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByText(/Could not read this job/i)).toBeInTheDocument();
    });
    expect(screen.getByText(/500/)).toBeInTheDocument();

    mocked.getJobChanges.mockResolvedValue(changeSet());
    fireEvent.click(screen.getByRole('button', { name: /try again/i }));

    await waitFor(() => {
      expect(screen.getByTestId('changes-file-list')).toBeInTheDocument();
    });
  });

  it('refreshes through the endpoint bypassing the server reuse window', async () => {
    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-file-list')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('changes-refresh-btn'));

    await waitFor(() => {
      expect(mocked.getJobChanges).toHaveBeenLastCalledWith('job-1', { refresh: true });
    });
  });

  it('refetches for a different job and never paints the old job\'s answer over the new one', async () => {
    // The stale-response guard: job A's request is still in flight when the operator switches to job B.
    // Without a token the slower response wins, and the panel shows job A's files under job B's header.
    let resolveA: (v: JobChangeSet) => void = () => {};
    mocked.getJobChanges.mockImplementationOnce(
      () => new Promise<JobChangeSet>((res) => { resolveA = res; })
    );
    mocked.getJobChanges.mockResolvedValueOnce(
      changeSet({ files: [file({ path: 'src/B-only.cs' })] })
    );

    const { rerender } = render(<ChangesPanel jobId="job-a" onClose={() => {}} />);
    rerender(<ChangesPanel jobId="job-b" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-file-row')).toHaveTextContent('B-only.cs');
    });

    // A's late answer arrives after B's — it must be dropped.
    resolveA(changeSet({ files: [file({ path: 'src/A-late.cs' })] }));

    await new Promise((r) => setTimeout(r, 0));
    expect(screen.getByTestId('changes-file-row')).toHaveTextContent('B-only.cs');
    expect(screen.queryByText('A-late.cs')).not.toBeInTheDocument();
  });

  it('drops an error read for one job when the panel is pointed at another', async () => {
    // The failure the per-job error comparison prevents: job A 500s, the operator navigates away (to no
    // job at all here, since it is the cheapest way to make the wrong body unambiguous). Without tying
    // the error to the job that produced it, the panel would render "could not read this job's changes"
    // beside "no job selected" — two bodies, neither of them true.
    mocked.getJobChanges.mockRejectedValueOnce(new Error('Failed to load file changes: 500'));

    const { rerender } = render(<ChangesPanel jobId="job-a" onClose={() => {}} />);
    await waitFor(() => {
      expect(screen.getByTestId('changes-error')).toBeInTheDocument();
    });

    rerender(<ChangesPanel jobId={null} onClose={() => {}} />);
    await waitFor(() => {
      expect(screen.getByTestId('changes-no-job')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('changes-error')).not.toBeInTheDocument();

    // And a healthy job B is not held hostage by A's fault either.
    mocked.getJobChanges.mockResolvedValue(changeSet());
    rerender(<ChangesPanel jobId="job-b" onClose={() => {}} />);
    await waitFor(() => {
      expect(screen.getByTestId('changes-file-list')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('changes-error')).not.toBeInTheDocument();
  });

  // ── the diff pane ─────────────────────────────────────────────────────────────

  it('auto-selects the first file and renders its diff with +/- colouring', async () => {
    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-diff-body')).toBeInTheDocument();
    });

    const body = screen.getByTestId('changes-diff-body');

    // Regex matchers, not exact strings: testing-library normalizes whitespace before comparing, so a
    // leading-space exact string can never match a diff line whose indentation *is* the content.
    expect(within(body).getByText(/var added = true;/)).toBeInTheDocument();
    expect(within(body).getByText(/var removed = true;/)).toBeInTheDocument();
    expect(within(body).getByText('src/A.cs')).toBeInTheDocument();

    // Colour is asserted through the marker the row sets, because jsdom does not compute Tailwind
    // classes into styles — the kind is the contract between the parser and the palette.
    const rows = Array.from(body.querySelectorAll('[data-diff-kind]'));
    expect(rows.filter((r) => r.getAttribute('data-diff-kind') === 'add')).toHaveLength(2);
    expect(rows.filter((r) => r.getAttribute('data-diff-kind') === 'del')).toHaveLength(1);
    expect(rows.filter((r) => r.getAttribute('data-diff-kind') === 'hunk')).toHaveLength(1);
    expect(rows.filter((r) => r.getAttribute('data-diff-kind') === 'ctx')).toHaveLength(2);
  });

  it('pins the patch request to the SHAs the list was computed from', async () => {
    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(mocked.getJobChangePatch).toHaveBeenCalled();
    });

    expect(mocked.getJobChangePatch).toHaveBeenCalledWith('job-1', {
      path: 'src/A.cs',
      previousPath: null,
      status: 'modified',
      baseSha: 'b'.repeat(40),
      headSha: 'a'.repeat(40)
    });
  });

  it('forwards previousPath and status for a rename and for an untracked row', async () => {
    mocked.getJobChanges.mockResolvedValue(
      changeSet({
        files: [
          file({ path: 'src/New.cs', status: 'renamed', previousPath: 'src/Old.cs' }),
          file({ path: 'src/U.cs', status: 'untracked', previousPath: null })
        ]
      })
    );

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(mocked.getJobChangePatch).toHaveBeenCalledWith('job-1', expect.objectContaining({
        path: 'src/New.cs',
        previousPath: 'src/Old.cs',
        status: 'renamed'
      }));
    });

    fireEvent.click(screen.getAllByTestId('changes-file-row')[1]);

    await waitFor(() => {
      expect(mocked.getJobChangePatch).toHaveBeenCalledWith('job-1', expect.objectContaining({
        path: 'src/U.cs',
        status: 'untracked'
      }));
    });
  });

  it('renders the server\'s sentence when a patch is refused, not a generic failure', async () => {
    mocked.getJobChangePatch.mockResolvedValue({
      found: false,
      reasonCode: 'path_not_in_change_set',
      message: "'src/A.cs' is not part of the pushed comparison for this branch.",
      notes: ['Checkout (runner): branch_mismatch: on another branch']
    });

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByText(/is not part of the pushed comparison/i)).toBeInTheDocument();
    });
    expect(screen.getByText('path_not_in_change_set')).toBeInTheDocument();
    expect(screen.getByText(/branch_mismatch/)).toBeInTheDocument();
    expect(screen.queryByText(/Could not read this job/i)).not.toBeInTheDocument();
  });

  it('says so plainly when the diff was cut at the server byte cap', async () => {
    const cut = found();
    if (cut.found) cut.patch.truncated = true;
    mocked.getJobChangePatch.mockResolvedValue(cut);

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByText(/larger than the server sends/i)).toBeInTheDocument();
    });
  });

  it('reports a binary file without pretending there is a textual diff', async () => {
    mocked.getJobChanges.mockResolvedValue(
      changeSet({ files: [file({ path: 'assets/x.png', binary: true, additions: null, deletions: null })] })
    );
    const binaryResult = found('Binary files a/assets/x.png and b/assets/x.png differ');
    if (binaryResult.found) {
      binaryResult.patch.binary = true;
      binaryResult.patch.path = 'assets/x.png';
    }
    mocked.getJobChangePatch.mockResolvedValue(binaryResult);

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByText(/Binary file — no textual diff\./)).toBeInTheDocument();
    });
    // The row shows neither +0 nor -0: git could not count, and zero is a different fact.
    expect(screen.getByText('bin')).toBeInTheDocument();
  });

  it('does not refetch the patch when reselecting the same row', async () => {
    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(mocked.getJobChangePatch).toHaveBeenCalledTimes(1);
    });

    fireEvent.click(screen.getByTestId('changes-file-row'));
    await new Promise((r) => setTimeout(r, 0));
    expect(mocked.getJobChangePatch).toHaveBeenCalledTimes(1);
  });

  it('keeps the selected file across a refresh when it is still in the change set', async () => {
    mocked.getJobChanges.mockResolvedValue(
      changeSet({ files: [file({ path: 'src/A.cs' }), file({ path: 'src/B.cs' })] })
    );

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getAllByTestId('changes-file-row')).toHaveLength(2);
    });

    fireEvent.click(screen.getAllByTestId('changes-file-row')[1]);
    await waitFor(() => {
      expect(screen.getByTestId('changes-diff-path')).toHaveTextContent('B.cs');
    });

    fireEvent.click(screen.getByTestId('changes-refresh-btn'));

    await waitFor(() => {
      expect(mocked.getJobChanges).toHaveBeenLastCalledWith('job-1', { refresh: true });
    });
    await waitFor(() => {
      expect(screen.getByTestId('changes-diff-path')).toHaveTextContent('B.cs');
    });
  });

  it('reports how many rows were left out without understating the total', async () => {
    mocked.getJobChanges.mockResolvedValue(
      changeSet({
        files: [file({ path: 'src/A.cs' })],
        stats: { filesChanged: 42, additions: 900, deletions: 12, omittedFiles: 41 }
      })
    );

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-count')).toHaveTextContent('42');
    });
    expect(screen.getByText(/showing 1 of 42/)).toBeInTheDocument();
    expect(screen.getByText(/\(41 more not listed\)/)).toBeInTheDocument();
  });

  it('renders the identity line with branch, base, head and totals', async () => {
    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-identity')).toBeInTheDocument();
    });

    const identity = screen.getByTestId('changes-identity');
    expect(identity).toHaveTextContent('mesh/job-1');
    expect(identity).toHaveTextContent('main');
    expect(identity).toHaveTextContent('@aaaaaaa');
    expect(identity).toHaveTextContent('+2');
    expect(identity).toHaveTextContent('-1');
  });

  it('copies the raw patch', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });

    render(<ChangesPanel jobId="job-1" onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-copy-patch')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId('changes-copy-patch'));

    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith(SAMPLE_PATCH);
    });
  });

  it('says "no job selected" rather than "no changes" when there is nothing to ask about', async () => {
    // The panel can be left open across a project switch or a job deletion, which nulls the selection.
    // Falling through to the "nothing could be read" branch would tell the operator their branch is
    // empty when the app simply has no job — a wrong diagnosis printed as a fact.
    render(<ChangesPanel jobId={null} onClose={() => {}} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-no-job')).toBeInTheDocument();
    });
    expect(screen.queryByTestId('changes-empty-reason')).not.toBeInTheDocument();
    expect(screen.queryByTestId('changes-empty-title')).not.toBeInTheDocument();
    // No source chip either: nothing was asked, so there is no source to name and no failure to report.
    expect(screen.queryByTestId('changes-source-chip')).not.toBeInTheDocument();
    expect(mocked.getJobChanges).not.toHaveBeenCalled();
  });

  it('closes through the callback', async () => {
    const onClose = vi.fn();
    render(<ChangesPanel jobId="job-1" onClose={onClose} />);

    await waitFor(() => {
      expect(screen.getByTestId('changes-panel')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTitle('Close changes panel'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
