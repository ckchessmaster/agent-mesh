// @vitest-environment jsdom
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { NewJobModal } from '../NewJobModal';
import type { Project, Persona } from '../../types';
import { WorkspaceType, PlanApprovalPolicy } from '../../types';

const mockProject: Project = {
  id: 'agent-mesh',
  name: 'Agent Mesh',
  description: 'Multi-agent cockpit',
  workspaceType: WorkspaceType.Git,
  gitBaseBranch: 'main',
  projectInstructions: '',
  defaultLeadPersonaId: 'bee',
  assignedPersonas: [
    { id: 'bee', name: 'Bee', role: 'Principal Software Engineer', driverId: 'copilot' }
  ],
  jobCount: 1,
  activeJobCount: 1,
  createdAt: '2026-11-22T00:00:00Z',
  updatedAt: '2026-11-22T00:00:00Z'
};

const mockPersonas: Persona[] = [
  {
    id: 'bee',
    name: 'Bee',
    role: 'Principal Software Engineer',
    personaPrompt: 'You are Bee.',
    driverId: 'copilot',
    trustTier: 2,
    createdAt: '2026-11-22T00:00:00Z'
  }
];

describe('NewJobModal', () => {
  const createObjectUrlMock = vi.fn((file: Blob) => `blob:preview/${(file as File).name || 'blob'}`);
  const revokeObjectUrlMock = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      writable: true,
      value: createObjectUrlMock
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      writable: true,
      value: revokeObjectUrlMock
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('uploads files via file picker, shows image thumbnails and sizes, supports removal, and submits files', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <NewJobModal
        isOpen={true}
        onClose={onClose}
        project={mockProject}
        availablePersonas={mockPersonas}
        onCreate={onCreate}
      />
    );

    const fileInput = screen.getByTestId('new-job-file-input');
    const screenshot = new File(['png-binary-content'], 'login-mockup.png', { type: 'image/png' });
    const specDoc = new File(['# Spec\nDetails here'], 'spec.md', { type: 'text/markdown' });

    fireEvent.change(fileInput, { target: { files: [screenshot, specDoc] } });

    expect(screen.getByTestId('new-job-attachment-count')).toHaveTextContent('2/10');
    expect(screen.getByText('login-mockup.png')).toBeInTheDocument();
    expect(screen.getByText('spec.md')).toBeInTheDocument();

    const previewImg = screen.getByTestId('new-job-attachment-preview');
    expect(previewImg).toHaveAttribute('src', 'blob:preview/login-mockup.png');

    // Remove the markdown doc and verify only the image remains
    fireEvent.click(screen.getByRole('button', { name: 'Remove attachment spec.md' }));
    expect(screen.queryByText('spec.md')).not.toBeInTheDocument();
    expect(screen.getByTestId('new-job-attachment-count')).toHaveTextContent('1/10');

    // Remove the image and verify preview URL is revoked
    fireEvent.click(screen.getByRole('button', { name: 'Remove attachment login-mockup.png' }));
    expect(revokeObjectUrlMock).toHaveBeenCalledWith('blob:preview/login-mockup.png');
    expect(screen.queryByTestId('new-job-staged-attachments')).not.toBeInTheDocument();

    // Re-pick the image and submit the job
    fireEvent.change(fileInput, { target: { files: [screenshot] } });

    const titleInput = screen.getByPlaceholderText('e.g. Implement OAuth2 flow with Google & GitHub');
    fireEvent.change(titleInput, { target: { value: 'Redesign login page' } });
    fireEvent.submit(titleInput.closest('form')!);

    await waitFor(() => {
      expect(onCreate).toHaveBeenCalledWith({
        title: 'Redesign login page',
        description: undefined,
        priority: 1,
        approvalPolicy: PlanApprovalPolicy.Autonomous,
        assignedAgentId: 'bee',
        files: [screenshot]
      });
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  it('supports drag-and-drop onto the dropzone with active drag feedback', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);

    render(
      <NewJobModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        availablePersonas={mockPersonas}
        onCreate={onCreate}
      />
    );

    const dropzone = screen.getByTestId('new-job-attachment-dropzone');
    expect(dropzone).toHaveAttribute('data-dragging', 'false');

    fireEvent.dragEnter(dropzone);
    expect(dropzone).toHaveAttribute('data-dragging', 'true');
    expect(screen.getByText('Drop files or screenshots here')).toBeInTheDocument();

    fireEvent.dragLeave(dropzone);
    expect(dropzone).toHaveAttribute('data-dragging', 'false');

    const droppedPdf = new File(['%PDF-1.7...'], 'architecture.pdf', { type: 'application/pdf' });
    fireEvent.dragEnter(dropzone);
    fireEvent.drop(dropzone, {
      dataTransfer: {
        files: [droppedPdf]
      }
    });

    expect(dropzone).toHaveAttribute('data-dragging', 'false');
    expect(screen.getByText('architecture.pdf')).toBeInTheDocument();
    expect(screen.getByTestId('new-job-attachment-count')).toHaveTextContent('1/10');
  });

  it('pastes screenshots from clipboard (both generic/unnamed and named) without blocking plain-text paste', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);

    render(
      <NewJobModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        availablePersonas={mockPersonas}
        onCreate={onCreate}
      />
    );

    const descriptionInput = screen.getByTestId('new-job-description-input');

    // 1. Plain-text paste must NOT be intercepted
    const plainTextPaste = fireEvent.paste(descriptionInput, {
      clipboardData: {
        files: [],
        items: [{ kind: 'string', type: 'text/plain', getAsFile: () => null }]
      }
    });
    expect(plainTextPaste).toBe(true);
    expect(screen.queryByTestId('new-job-staged-attachments')).not.toBeInTheDocument();

    // 2. First screenshot pasted with Chromium's generic "image.png" via clipboardData.items
    const genericClip1 = new File(['raw-png-1'], 'image.png', { type: 'image/png' });
    fireEvent.paste(descriptionInput, {
      clipboardData: {
        files: [],
        items: [
          {
            kind: 'file',
            type: 'image/png',
            getAsFile: () => genericClip1
          }
        ]
      }
    });

    expect(screen.getByText('screenshot-1.png')).toBeInTheDocument();

    // 3. Second screenshot pasted onto document.body (before focusing an input) with empty filename
    const unnamedClip2 = new File(['raw-png-2'], '', { type: 'image/png' });
    const bodyPasteEvent = new Event('paste', { bubbles: true, cancelable: true });
    Object.defineProperty(bodyPasteEvent, 'clipboardData', {
      value: { files: [unnamedClip2], items: [] }
    });
    document.body.dispatchEvent(bodyPasteEvent);

    await waitFor(() => {
      expect(screen.getByText('screenshot-2.png')).toBeInTheDocument();
    });
    expect(screen.getByTestId('new-job-attachment-count')).toHaveTextContent('2/10');

    // 4. Explicitly named image pasted preserves its filename
    const namedClip = new File(['raw-png-3'], 'stacktrace-banner.png', { type: 'image/png' });
    fireEvent.paste(descriptionInput, {
      clipboardData: {
        files: [namedClip]
      }
    });

    expect(screen.getByText('stacktrace-banner.png')).toBeInTheDocument();
    expect(screen.getByTestId('new-job-attachment-count')).toHaveTextContent('3/10');

    // Submit and verify all 3 normalized File instances are passed to onCreate
    const titleInput = screen.getByPlaceholderText('e.g. Implement OAuth2 flow with Google & GitHub');
    fireEvent.change(titleInput, { target: { value: 'Fix broken header from screenshots' } });
    fireEvent.submit(titleInput.closest('form')!);

    await waitFor(() => {
      expect(onCreate).toHaveBeenCalledTimes(1);
    });

    const submitted = onCreate.mock.calls[0][0];
    expect(submitted.files).toHaveLength(3);
    expect(submitted.files.map((f: File) => f.name)).toEqual([
      'screenshot-1.png',
      'screenshot-2.png',
      'stacktrace-banner.png'
    ]);
  });

  it('rejects empty, oversized, unsupported, and >10 files while preserving existing valid attachments', () => {
    render(
      <NewJobModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        availablePersonas={mockPersonas}
        onCreate={vi.fn()}
      />
    );

    const fileInput = screen.getByTestId('new-job-file-input');

    // Stage 1 valid file first
    const validFile = new File(['valid'], 'notes.txt', { type: 'text/plain' });
    fireEvent.change(fileInput, { target: { files: [validFile] } });
    expect(screen.getByText('notes.txt')).toBeInTheDocument();

    // Empty file rejected
    const emptyFile = new File([], 'empty.png', { type: 'image/png' });
    fireEvent.change(fileInput, { target: { files: [emptyFile] } });
    expect(screen.getByTestId('new-job-error')).toHaveTextContent("File 'empty.png' is empty.");
    expect(screen.getByTestId('new-job-attachment-count')).toHaveTextContent('1/10');

    // Unsupported extension rejected
    const exeFile = new File(['MZ...'], 'installer.exe', { type: 'application/x-msdownload' });
    fireEvent.change(fileInput, { target: { files: [exeFile] } });
    expect(screen.getByTestId('new-job-error')).toHaveTextContent(
      "File 'installer.exe' has an unsupported file type."
    );

    // Oversized (>25 MB) file rejected
    const hugeFile = new File(['x'], 'recording.png', { type: 'image/png' });
    Object.defineProperty(hugeFile, 'size', { value: 26 * 1024 * 1024 });
    fireEvent.change(fileInput, { target: { files: [hugeFile] } });
    expect(screen.getByTestId('new-job-error')).toHaveTextContent(
      "File 'recording.png' exceeds the 25 MB maximum file size limit."
    );

    // >10 total files rejected (1 already staged + 10 more = 11)
    const tenMore = Array.from({ length: 10 }, (_, idx) =>
      new File(['ok'], `img-${idx}.png`, { type: 'image/png' })
    );
    fireEvent.change(fileInput, { target: { files: tenMore } });
    expect(screen.getByTestId('new-job-error')).toHaveTextContent(
      'A maximum of 10 files can be attached at once.'
    );
    expect(screen.getByTestId('new-job-attachment-count')).toHaveTextContent('1/10');
  });

  it('omits files when none are staged and resets staged attachments when closed', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);
    const { rerender } = render(
      <NewJobModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        availablePersonas={mockPersonas}
        onCreate={onCreate}
      />
    );

    const fileInput = screen.getByTestId('new-job-file-input');
    const screenshot = new File(['bytes'], 'temp.png', { type: 'image/png' });
    fireEvent.change(fileInput, { target: { files: [screenshot] } });
    expect(screen.getByText('temp.png')).toBeInTheDocument();

    // Closing modal resets staged files and revokes preview URL
    rerender(
      <NewJobModal
        isOpen={false}
        onClose={vi.fn()}
        project={mockProject}
        availablePersonas={mockPersonas}
        onCreate={onCreate}
      />
    );
    expect(revokeObjectUrlMock).toHaveBeenCalledWith('blob:preview/temp.png');

    // Reopening starts fresh with 0 attachments and omits `files` on submit
    rerender(
      <NewJobModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        availablePersonas={mockPersonas}
        onCreate={onCreate}
      />
    );
    expect(screen.queryByTestId('new-job-staged-attachments')).not.toBeInTheDocument();

    const titleInput = screen.getByPlaceholderText('e.g. Implement OAuth2 flow with Google & GitHub');
    fireEvent.change(titleInput, { target: { value: 'Backend-only refactor' } });
    fireEvent.submit(titleInput.closest('form')!);

    await waitFor(() => {
      expect(onCreate).toHaveBeenCalledWith({
        title: 'Backend-only refactor',
        description: undefined,
        priority: 1,
        approvalPolicy: PlanApprovalPolicy.Autonomous,
        assignedAgentId: 'bee'
      });
    });
    expect('files' in onCreate.mock.calls[0][0]).toBe(false);
  });
});
