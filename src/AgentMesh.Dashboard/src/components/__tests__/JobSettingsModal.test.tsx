// @vitest-environment jsdom
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api } from '../../services/api';
import { JobSettingsModal } from '../JobSettingsModal';
import {
  ProjectJobStatus,
  PlanApprovalPolicy,
  WorkspaceType,
  type Project,
  type ProjectJob,
  type Persona,
  type SessionAttachment
} from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    uploadJobAttachments: vi.fn(),
    deleteJobAttachment: vi.fn()
  }
}));

const mockProject: Project = {
  id: 'agent-mesh',
  name: 'Agent Mesh',
  description: 'Autonomous multi-agent platform',
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

const baseJob: ProjectJob = {
  id: 'job-settings-1',
  projectId: 'agent-mesh',
  projectName: 'Agent Mesh',
  title: 'Original Job Title',
  description: 'Original job description',
  status: ProjectJobStatus.Active,
  priority: 1,
  approvalPolicy: PlanApprovalPolicy.Autonomous,
  gitBranchName: 'mesh/job-settings-1',
  tasks: [],
  attachments: [
    {
      id: 'att-existing-1',
      sessionId: 'job-settings-1',
      jobId: 'job-settings-1',
      fileName: 'screenshot-1.png',
      contentType: 'image/png',
      sizeBytes: 4096,
      s3Key: 'jobs/job-settings-1/attachments/att-existing-1/screenshot-1.png',
      downloadUrl: '/api/project-jobs/job-settings-1/attachments/att-existing-1/content',
      createdAt: '2026-11-22T01:00:00Z'
    }
  ],
  messageCount: 0,
  createdAt: '2026-11-22T00:00:00Z',
  updatedAt: '2026-11-22T00:00:00Z'
};

describe('JobSettingsModal attachments, screenshot paste, lightbox, and deletion', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('previews image attachments in lightbox without submitting the form and closes on Escape', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <JobSettingsModal
        isOpen={true}
        onClose={onClose}
        project={mockProject}
        job={baseJob}
        availablePersonas={mockPersonas}
        onSave={onSave}
      />
    );

    expect(screen.getByTestId('job-settings-attachment-count')).toHaveTextContent('1');
    expect(screen.getByText('screenshot-1.png')).toBeInTheDocument();

    fireEvent.click(screen.getByTestId('job-settings-attachment-preview-att-existing-1'));

    const lightbox = await screen.findByTestId('attachment-lightbox');
    expect(lightbox).toBeInTheDocument();
    expect(screen.getByTestId('lightbox-image')).toHaveAttribute(
      'src',
      '/api/project-jobs/job-settings-1/attachments/att-existing-1/content'
    );
    expect(onSave).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    // Pressing Escape closes the lightbox while keeping JobSettingsModal open
    fireEvent.keyDown(document, { key: 'Escape' });
    await waitFor(() => {
      expect(screen.queryByTestId('attachment-lightbox')).not.toBeInTheDocument();
    });
    expect(screen.getByTestId('job-settings-modal')).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('uploads pasted screenshots and picked files while preserving unsaved form edits, and deletes attachments', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    const uploadedShot: SessionAttachment = {
      id: 'att-pasted-2',
      sessionId: 'job-settings-1',
      jobId: 'job-settings-1',
      fileName: 'screenshot-2.png',
      contentType: 'image/png',
      sizeBytes: 1536,
      s3Key: 'jobs/job-settings-1/attachments/att-pasted-2/screenshot-2.png',
      downloadUrl: '/api/project-jobs/job-settings-1/attachments/att-pasted-2/content',
      createdAt: '2026-11-22T01:10:00Z'
    };

    vi.mocked(api.uploadJobAttachments).mockResolvedValue([uploadedShot]);
    vi.mocked(api.deleteJobAttachment).mockResolvedValue({
      status: 'deleted',
      attachmentId: 'att-existing-1'
    });

    const { rerender } = render(
      <JobSettingsModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        job={baseJob}
        availablePersonas={mockPersonas}
        onSave={onSave}
      />
    );

    // Edit title & description before pasting an attachment
    const titleInput = screen.getByTestId('job-settings-title-input');
    const descInput = screen.getByTestId('job-settings-desc-input');
    fireEvent.change(titleInput, { target: { value: 'Unsaved Edited Title' } });
    fireEvent.change(descInput, { target: { value: 'Unsaved Edited Description' } });

    // Paste unnamed screenshot into description textarea
    const unnamedPng = new File(['png-bytes'], '', { type: 'image/png' });
    fireEvent.paste(descInput, {
      clipboardData: { files: [unnamedPng] }
    });

    await waitFor(() => {
      expect(api.uploadJobAttachments).toHaveBeenCalledWith(
        'job-settings-1',
        expect.arrayContaining([expect.objectContaining({ name: 'screenshot-2.png' })])
      );
      expect(screen.getByText('screenshot-2.png')).toBeInTheDocument();
      expect(screen.getByTestId('job-settings-attachment-count')).toHaveTextContent('2');
    });

    // Simulate parent re-rendering job with updated attachments (e.g. via SignalR JobUpdated)
    rerender(
      <JobSettingsModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        job={{
          ...baseJob,
          attachments: [...(baseJob.attachments ?? []), uploadedShot]
        }}
        availablePersonas={mockPersonas}
        onSave={onSave}
      />
    );

    // Unsaved edits to title and description MUST be preserved!
    expect(screen.getByTestId('job-settings-title-input')).toHaveValue('Unsaved Edited Title');
    expect(screen.getByTestId('job-settings-desc-input')).toHaveValue('Unsaved Edited Description');

    // Delete the original attachment without submitting the settings form
    fireEvent.click(screen.getByTestId('job-settings-attachment-delete-att-existing-1'));
    await waitFor(() => {
      expect(api.deleteJobAttachment).toHaveBeenCalledWith('job-settings-1', 'att-existing-1');
      expect(screen.queryByText('screenshot-1.png')).not.toBeInTheDocument();
      expect(screen.getByTestId('job-settings-attachment-count')).toHaveTextContent('1');
    });
    expect(onSave).not.toHaveBeenCalled();
  });

  it('rejects unsupported file types with an inline validation alert before calling the API', () => {
    render(
      <JobSettingsModal
        isOpen={true}
        onClose={vi.fn()}
        project={mockProject}
        job={{ ...baseJob, attachments: [] }}
        availablePersonas={mockPersonas}
        onSave={vi.fn()}
      />
    );

    const fileInput = screen.getByTestId('job-settings-file-input');
    const badExe = new File(['MZ...'], 'installer.exe', { type: 'application/x-msdownload' });
    fireEvent.change(fileInput, { target: { files: [badExe] } });

    expect(screen.getByTestId('job-settings-error')).toHaveTextContent(/unsupported file type/i);
    expect(api.uploadJobAttachments).not.toHaveBeenCalled();
  });
});
