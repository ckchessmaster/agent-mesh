// @vitest-environment jsdom
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import React from 'react';
import { ProjectDocumentsSection } from '../ProjectDocumentsSection';
import { api } from '../../services/api';
import type { ProjectDocument } from '../../types';
import { ProjectDocumentKind } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getProjectDocuments: vi.fn(),
    addProjectDocument: vi.fn(),
    getProjectDocument: vi.fn(),
    deleteProjectDocument: vi.fn()
  }
}));

const doc = (over: Partial<ProjectDocument> = {}): ProjectDocument => ({
  id: 'd1',
  projectId: 'agent-mesh',
  title: 'API contract',
  kind: ProjectDocumentKind.Text,
  fileName: null,
  contentType: null,
  sizeBytes: 29,
  s3Key: null,
  downloadUrl: '/api/projects/agent-mesh/documents/d1/content',
  forceDownloadUrl: '/api/projects/agent-mesh/documents/d1/content?download=true',
  createdAt: '2026-11-04T00:00:00Z',
  updatedAt: '2026-11-04T00:00:00Z',
  content: null,
  ...over
});

const mocked = api as unknown as Record<string, ReturnType<typeof vi.fn>>;

describe('ProjectDocumentsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocked.getProjectDocuments.mockResolvedValue([]);
    mocked.addProjectDocument.mockResolvedValue([]);
    mocked.deleteProjectDocument.mockResolvedValue({ status: 'deleted' });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // ── the unsaved-project case ──────────────────────────────────────────────────

  it('tells the user to save the project before attaching documents', () => {
    render(<ProjectDocumentsSection projectId={null} />);

    expect(screen.getByText(/Save this project first/i)).toBeInTheDocument();
    expect(mocked.getProjectDocuments).not.toHaveBeenCalled();
  });

  it('does not fetch when there is no project id', () => {
    render(<ProjectDocumentsSection projectId={null} />);

    expect(mocked.getProjectDocuments).not.toHaveBeenCalled();
  });

  // ── loading the library ───────────────────────────────────────────────────────

  it('loads and lists the library on mount', async () => {
    mocked.getProjectDocuments.mockResolvedValue([
      doc(),
      doc({ id: 'd2', title: 'arch.png', kind: ProjectDocumentKind.File, fileName: 'arch.png', sizeBytes: 20480 })
    ]);

    render(<ProjectDocumentsSection projectId="agent-mesh" />);

    expect(await screen.findByText('API contract')).toBeInTheDocument();
    expect(screen.getByText('arch.png')).toBeInTheDocument();
    expect(mocked.getProjectDocuments).toHaveBeenCalledWith('agent-mesh');
  });

  it('shows a distinct empty state rather than a blank region', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);

    expect(await screen.findByText('No documents attached yet.')).toBeInTheDocument();
  });

  it('shows an error and keeps the retry affordance when the load fails', async () => {
    // An empty list would read as "nobody attached anything", so a failed load must not look like an
    // empty library — the user would re-attach a duplicate to work around a transient failure.
    mocked.getProjectDocuments.mockRejectedValue(new Error('Failed to load project documents: 503'));

    render(<ProjectDocumentsSection projectId="agent-mesh" />);

    expect(await screen.findByText(/503/)).toBeInTheDocument();
    expect(screen.getByText('No documents attached yet.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /reload/i })).toBeInTheDocument();
  });

  it('reloads when Reload is clicked', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    fireEvent.click(screen.getByRole('button', { name: /reload/i }));

    await waitFor(() => expect(mocked.getProjectDocuments).toHaveBeenCalledTimes(2));
  });

  it('reloads when the project changes', async () => {
    const { rerender } = render(<ProjectDocumentsSection projectId="a" />);
    await waitFor(() => expect(mocked.getProjectDocuments).toHaveBeenCalledWith('a'));

    rerender(<ProjectDocumentsSection projectId="b" />);

    await waitFor(() => expect(mocked.getProjectDocuments).toHaveBeenCalledWith('b'));
  });

  // ── the list row ──────────────────────────────────────────────────────────────

  it('links Open at the document content URL', async () => {
    mocked.getProjectDocuments.mockResolvedValue([doc()]);

    render(<ProjectDocumentsSection projectId="agent-mesh" />);

    const link = await screen.findByRole('link', { name: /open/i });
    expect(link).toHaveAttribute('href', '/api/projects/agent-mesh/documents/d1/content');
  });

  it('shows a file row with its filename and size', async () => {
    mocked.getProjectDocuments.mockResolvedValue([
      doc({ id: 'd2', title: 'Architecture diagram', kind: ProjectDocumentKind.File, fileName: 'arch.png', sizeBytes: 20480 })
    ]);

    render(<ProjectDocumentsSection projectId="agent-mesh" />);

    expect(await screen.findByText('Architecture diagram')).toBeInTheDocument();
    expect(screen.getByText(/arch\.png/)).toBeInTheDocument();
    expect(screen.getByText(/20\.0 KB/)).toBeInTheDocument();
  });

  it('shows a text row without inventing a filename', async () => {
    mocked.getProjectDocuments.mockResolvedValue([doc()]);

    render(<ProjectDocumentsSection projectId="agent-mesh" />);

    expect(await screen.findByText('API contract')).toBeInTheDocument();
    expect(screen.getByText(/^text · /)).toBeInTheDocument();
  });

  // ── adding ────────────────────────────────────────────────────────────────────

  it('adds a pasted text document and reloads the library', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');
    mocked.addProjectDocument.mockResolvedValue([doc()]);

    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), {
      target: { value: 'POST /things creates a thing.' }
    });
    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    await waitFor(() =>
      expect(mocked.addProjectDocument).toHaveBeenCalledWith('agent-mesh', {
        title: undefined,
        content: 'POST /things creates a thing.',
        files: undefined
      })
    );
    // The library is re-read rather than appended locally: the server assigns ids, derived titles and
    // timestamps, so a locally-built row would drift from what a reload shows.
    await waitFor(() => expect(mocked.getProjectDocuments).toHaveBeenCalledTimes(2));
  });

  it('sends the title when one is typed', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    fireEvent.change(screen.getByPlaceholderText(/Title \(optional/i), { target: { value: 'Runbook' } });
    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), { target: { value: 'body' } });
    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    await waitFor(() =>
      expect(mocked.addProjectDocument).toHaveBeenCalledWith(
        'agent-mesh',
        expect.objectContaining({ title: 'Runbook' })
      )
    );
  });

  it('sends chosen files together with pasted text in one request', async () => {
    // The whole reason the composer has one submit rather than two: a user who typed notes and attached a
    // screenshot expects both to land, or neither to.
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    const file = new File(['x'], 'shot.png', { type: 'image/png' });
    fireEvent.change(document.querySelector('input[type="file"]')!, { target: { files: [file] } });
    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), { target: { value: 'see the screenshot' } });
    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    await waitFor(() => expect(mocked.addProjectDocument).toHaveBeenCalledTimes(1));
    const options = mocked.addProjectDocument.mock.calls[0][1];
    expect(options.content).toBe('see the screenshot');
    expect(options.files).toHaveLength(1);
    expect(options.files[0].name).toBe('shot.png');
  });

  it('previews chosen files before submit', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    fireEvent.change(document.querySelector('input[type="file"]')!, {
      target: { files: [new File(['abc'], 'spec.pdf', { type: 'application/pdf' })] }
    });

    expect(await screen.findByText(/spec\.pdf \(3 B\)/)).toBeInTheDocument();
  });

  it('clears the composer after a successful add', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    const textarea = screen.getByPlaceholderText(/paste a document here/i) as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: 'shipped' } });
    mocked.addProjectDocument.mockResolvedValue([doc()]);
    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    await waitFor(() => expect(textarea.value).toBe(''));
  });

  it('keeps the composer when the add fails', async () => {
    // Losing typed notes to a transient 500 means retyping the spec from scratch. The text stays so the
    // user can press Add again.
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    const textarea = screen.getByPlaceholderText(/paste a document here/i) as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: 'long pasted spec' } });
    mocked.addProjectDocument.mockRejectedValue(new Error('Failed to add document: 500'));

    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    expect(await screen.findByText(/500/)).toBeInTheDocument();
    expect(textarea.value).toBe('long pasted spec');
  });

  it('surfaces the server\'s limit message', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    mocked.addProjectDocument.mockRejectedValue(
      new Error('Failed to add document: This project can hold 50 documents and already has 50; this request would add 1.')
    );
    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    expect(await screen.findByText(/can hold 50 documents/)).toBeInTheDocument();
  });

  // ── client-side validation (before any network call) ──────────────────────────

  it('refuses to add when nothing was entered', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    expect(await screen.findByText(/Type a document or choose a file/i)).toBeInTheDocument();
    expect(mocked.addProjectDocument).not.toHaveBeenCalled();
  });

  it('does not send a whitespace-only body', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), { target: { value: '   \n  ' } });
    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    expect(await screen.findByText(/Type a document or choose a file/i)).toBeInTheDocument();
    expect(mocked.addProjectDocument).not.toHaveBeenCalled();
  });

  it('rejects an oversized file before uploading it', async () => {
    // The API's cap is 25 MB. Catching it client-side is the difference between an instant message and a
    // 25 MB upload that fails a minute later.
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    const huge = new File([''], 'huge.pdf', { type: 'application/pdf' });
    Object.defineProperty(huge, 'size', { value: 25 * 1024 * 1024 + 1 });

    fireEvent.change(document.querySelector('input[type="file"]')!, { target: { files: [huge] } });

    expect(await screen.findByText(/exceeds the 25 MB maximum/i)).toBeInTheDocument();
    expect(mocked.addProjectDocument).not.toHaveBeenCalled();
  });

  it('rejects more than ten files before uploading any', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    const files = Array.from({ length: 11 }, (_, i) => new File(['x'], `f${i}.md`, { type: 'text/markdown' }));
    fireEvent.change(document.querySelector('input[type="file"]')!, { target: { files } });

    expect(await screen.findByText(/maximum of 10 files/i)).toBeInTheDocument();
    expect(mocked.addProjectDocument).not.toHaveBeenCalled();
  });

  it('rejects an empty file before uploading', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    fireEvent.change(document.querySelector('input[type="file"]')!, {
      target: { files: [new File([], 'empty.md', { type: 'text/markdown' })] }
    });

    expect(await screen.findByText(/is empty/i)).toBeInTheDocument();
    expect(mocked.addProjectDocument).not.toHaveBeenCalled();
  });

  it('rejects text over the inline limit and suggests uploading it instead', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), {
      target: { value: 'x'.repeat(200_001) }
    });
    fireEvent.click(screen.getByRole('button', { name: /add document/i }));

    expect(await screen.findByText(/Upload it as a \.md or \.txt file instead/i)).toBeInTheDocument();
    expect(mocked.addProjectDocument).not.toHaveBeenCalled();
  });

  it('shows a live character count against the inline limit', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    expect(screen.getByText(/0 \/ 200,000/)).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), { target: { value: 'hello' } });

    expect(screen.getByText(/5 \/ 200,000/)).toBeInTheDocument();
  });

  // ── removing ──────────────────────────────────────────────────────────────────

  it('asks before removing, then deletes and drops the row', async () => {
    const confirm = vi.fn().mockReturnValue(true);
    vi.stubGlobal('confirm', confirm);

    mocked.getProjectDocuments.mockResolvedValue([doc()]);
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('API contract');

    fireEvent.click(screen.getByRole('button', { name: /remove api contract/i }));

    await waitFor(() => expect(mocked.deleteProjectDocument).toHaveBeenCalledWith('agent-mesh', 'd1'));
    await waitFor(() => expect(screen.queryByText('API contract')).not.toBeInTheDocument());
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('API contract'));
  });

  it('does not delete when the confirmation is declined', async () => {
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(false));

    mocked.getProjectDocuments.mockResolvedValue([doc()]);
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('API contract');

    fireEvent.click(screen.getByRole('button', { name: /remove api contract/i }));

    await waitFor(() => expect(mocked.deleteProjectDocument).not.toHaveBeenCalled());
    expect(screen.getByText('API contract')).toBeInTheDocument();
  });

  it('restores the row when the delete fails', async () => {
    // The optimistic removal has to be undone, or the UI shows a library the server still has.
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    mocked.getProjectDocuments.mockResolvedValue([doc()]);
    mocked.deleteProjectDocument.mockRejectedValue(new Error('Failed to remove document: 409'));

    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('API contract');

    fireEvent.click(screen.getByRole('button', { name: /remove api contract/i }));

    expect(await screen.findByText(/409/)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('API contract')).toBeInTheDocument());
    // The recovery is a re-read, not a local re-insert, so the list ends up exactly as the server sees it.
    await waitFor(() => expect(mocked.getProjectDocuments).toHaveBeenCalledTimes(2));
  });

  // ── living inside the project <form> ──────────────────────────────────────────

  it('never submits the surrounding project form', async () => {
    // The section renders inside ProjectModal's <form>. A bare <button> defaults to type="submit", so one
    // click on "Add document" would PUT the project and close the modal mid-upload. This is the trap the
    // component is designed around, and it is only observable from the parent's perspective.
    const onSubmit = vi.fn((e: React.FormEvent) => e.preventDefault());

    render(
      <form onSubmit={onSubmit}>
        <ProjectDocumentsSection projectId="agent-mesh" />
      </form>
    );
    await screen.findByText('No documents attached yet.');

    // An invalid add: no request goes out, and the form must still not submit.
    fireEvent.click(screen.getByRole('button', { name: /^add document$/i }));
    expect(await screen.findByText(/Type a document or choose a file/i)).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();

    // A valid add: the request goes out, and the form still must not submit.
    fireEvent.change(screen.getByPlaceholderText(/paste a document here/i), { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: /^add document$/i }));

    await waitFor(() => expect(mocked.addProjectDocument).toHaveBeenCalledTimes(1));
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('does not expose its inputs as project-form fields', async () => {
    // A named input inside a form is picked up by the browser's own submission. The project form is
    // submitted programmatically with a JSON body, so a stray field is at best noise and at worst a
    // File object handed to JSON.stringify.
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    document.querySelectorAll('input, textarea').forEach((el) => {
      expect(el).not.toHaveAttribute('name');
    });
  });

  it('keeps every control out of the form default by giving it an explicit type', async () => {
    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('No documents attached yet.');

    const buttons = screen.getAllByRole('button');
    expect(buttons.length).toBeGreaterThan(0);
    buttons.forEach((b) => expect(b).toHaveAttribute('type', 'button'));
  });

  it('labels the remove control for a file document by its title', async () => {
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(false));
    mocked.getProjectDocuments.mockResolvedValue([
      doc({ id: 'f1', title: 'Architecture diagram', kind: ProjectDocumentKind.File, fileName: 'arch.png' })
    ]);

    render(<ProjectDocumentsSection projectId="agent-mesh" />);
    await screen.findByText('Architecture diagram');

    expect(
      within(screen.getByRole('listitem')).getByRole('button', { name: /remove Architecture diagram/i })
    ).toBeInTheDocument();
  });
});
