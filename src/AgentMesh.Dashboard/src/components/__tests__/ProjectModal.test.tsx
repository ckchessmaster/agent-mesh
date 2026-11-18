// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { ProjectModal } from '../ProjectModal';
import { api } from '../../services/api';
import type { Persona, Project, ProjectWorkspaceSetupResult } from '../../types';
import { WorkspaceType } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getProjectDocuments: vi.fn().mockResolvedValue([]),
    addProjectDocument: vi.fn(),
    getProjectDocument: vi.fn(),
    deleteProjectDocument: vi.fn(),
    setupProjectWorkspace: vi.fn()
  }
}));

describe('ProjectModal workspace fields & setup action', () => {
  const mockPersonas: Persona[] = [
    {
      id: 'astra',
      name: 'Astra',
      role: 'Tech Lead',
      personaPrompt: 'You are Astra.',
      driverId: 'agy',
      trustTier: 2,
      createdAt: '2026-11-01T00:00:00Z'
    }
  ];

  const existingProject: Project = {
    id: 'payments-api',
    name: 'Payments API',
    description: 'Handles billing',
    workspaceType: WorkspaceType.Git,
    gitRepositoryUrl: 'https://github.com/org/payments.git',
    gitBaseBranch: 'main',
    repositoryName: 'payments-api',
    workingDirectory: '/workspace/custom/payments',
    resolvedWorkingDirectory: '/workspace/custom/payments',
    projectInstructions: 'Write tests',
    defaultLeadPersonaId: 'astra',
    assignedPersonas: [{ id: 'astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' }],
    jobCount: 0,
    activeJobCount: 0,
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-01T00:00:00Z'
  };

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.getProjectDocuments).mockResolvedValue([]);
  });

  it('exposes repositoryName, workingDirectory, live resolved preview, and submits them on create', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <ProjectModal
        isOpen={true}
        onClose={onClose}
        onSave={onSave}
        availablePersonas={mockPersonas}
      />
    );

    expect(
      screen.getByText('Save this project first to run workspace setup on disk.')
    ).toBeInTheDocument();
    expect(screen.queryByTestId('setup-project-workspace-btn')).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('e.g. AgentMesh Core'), {
      target: { value: 'Billing Service' }
    });
    expect(screen.getByTestId('project-resolved-working-directory')).toHaveTextContent(
      '/workspace/repos/billing-service'
    );

    fireEvent.change(screen.getByTestId('project-repository-name-input'), {
      target: { value: 'billing-repo' }
    });
    expect(screen.getByTestId('project-resolved-working-directory')).toHaveTextContent(
      '/workspace/repos/billing-repo'
    );

    fireEvent.change(screen.getByTestId('project-working-directory-input'), {
      target: { value: '/workspace/custom/billing/' }
    });
    expect(screen.getByTestId('project-resolved-working-directory')).toHaveTextContent(
      '/workspace/custom/billing'
    );

    fireEvent.click(screen.getByRole('button', { name: 'Create Project' }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(
        expect.objectContaining({
          name: 'Billing Service',
          repositoryName: 'billing-repo',
          workingDirectory: '/workspace/custom/billing/'
        })
      );
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  it('validates unsafe repositoryName and non-absolute workingDirectory before calling onSave', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(
      <ProjectModal
        isOpen={true}
        onClose={vi.fn()}
        onSave={onSave}
        availablePersonas={mockPersonas}
      />
    );

    fireEvent.change(screen.getByPlaceholderText('e.g. AgentMesh Core'), {
      target: { value: 'Billing Service' }
    });
    fireEvent.change(screen.getByTestId('project-repository-name-input'), {
      target: { value: '../etc' }
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create Project' }));

    expect(
      await screen.findByText(/Repository Name must be 1-128 characters/i)
    ).toBeInTheDocument();
    expect(onSave).not.toHaveBeenCalled();

    fireEvent.change(screen.getByTestId('project-repository-name-input'), {
      target: { value: 'billing-repo' }
    });
    fireEvent.change(screen.getByTestId('project-working-directory-input'), {
      target: { value: 'relative/path' }
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create Project' }));

    expect(
      await screen.findByText(/Working Directory must be an absolute path/i)
    ).toBeInTheDocument();
    expect(onSave).not.toHaveBeenCalled();
  });

  it('populates existing workspace fields and clears workingDirectory override when emptied on edit', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(
      <ProjectModal
        isOpen={true}
        onClose={vi.fn()}
        onSave={onSave}
        editingProject={existingProject}
        availablePersonas={mockPersonas}
      />
    );

    expect(screen.getByTestId('project-repository-name-input')).toHaveValue('payments-api');
    expect(screen.getByTestId('project-working-directory-input')).toHaveValue(
      '/workspace/custom/payments'
    );
    expect(screen.getByTestId('project-resolved-working-directory')).toHaveTextContent(
      '/workspace/custom/payments'
    );

    fireEvent.change(screen.getByTestId('project-working-directory-input'), {
      target: { value: '' }
    });
    expect(screen.getByTestId('project-resolved-working-directory')).toHaveTextContent(
      '/workspace/repos/payments-api'
    );
    expect(screen.getByTestId('project-workspace-dirty-hint')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Update Project' }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(
        expect.objectContaining({
          repositoryName: 'payments-api',
          workingDirectory: ''
        })
      );
    });
  });

  it('runs setup workspace action for an existing project without submitting the form and displays result', async () => {
    const onSave = vi.fn();
    const onClose = vi.fn();
    const setupResult: ProjectWorkspaceSetupResult = {
      projectId: 'payments-api',
      success: true,
      targetDirectory: '/workspace/custom/payments',
      effectiveDirectory: '/workspace/custom/payments',
      outcome: 'Initialized',
      usedFallback: false,
      createdFiles: true,
      message: 'Initialized standard project repository.',
      warnings: ['Configured local git identity fallback.']
    };
    vi.mocked(api.setupProjectWorkspace).mockResolvedValue(setupResult);

    render(
      <ProjectModal
        isOpen={true}
        onClose={onClose}
        onSave={onSave}
        editingProject={existingProject}
        availablePersonas={mockPersonas}
      />
    );

    fireEvent.click(screen.getByTestId('setup-project-workspace-btn'));

    await waitFor(() => {
      expect(api.setupProjectWorkspace).toHaveBeenCalledWith('payments-api');
      const resultBanner = screen.getByTestId('project-workspace-setup-result');
      expect(resultBanner).toHaveTextContent('Initialized');
      expect(resultBanner).toHaveTextContent('Initialized standard project repository.');
      expect(resultBanner).toHaveTextContent('/workspace/custom/payments');
      expect(resultBanner).toHaveTextContent('Configured local git identity fallback.');
    });

    expect(onSave).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('displays structured failure diagnostics and thrown errors from setup workspace', async () => {
    vi.mocked(api.setupProjectWorkspace)
      .mockResolvedValueOnce({
        projectId: 'payments-api',
        success: false,
        targetDirectory: '/workspace/custom/payments',
        effectiveDirectory: '',
        outcome: 'Failed',
        usedFallback: false,
        createdFiles: false,
        message: 'Remote clone failed.',
        warnings: ['authentication required'],
        error: 'Remote clone failed.'
      })
      .mockRejectedValueOnce(new Error('Failed to setup project workspace: Network timeout'));

    render(
      <ProjectModal
        isOpen={true}
        onClose={vi.fn()}
        onSave={vi.fn()}
        editingProject={existingProject}
        availablePersonas={mockPersonas}
      />
    );

    fireEvent.click(screen.getByTestId('setup-project-workspace-btn'));

    await waitFor(() => {
      const banner = screen.getByTestId('project-workspace-setup-result');
      expect(banner).toHaveTextContent('Failed');
      expect(banner).toHaveTextContent('Remote clone failed.');
      expect(banner).toHaveTextContent('Target directory: /workspace/custom/payments');
      expect(banner).toHaveTextContent('authentication required');
    });

    fireEvent.click(screen.getByTestId('setup-project-workspace-btn'));

    await waitFor(() => {
      expect(screen.getByTestId('project-workspace-setup-result')).toHaveTextContent(
        'Failed to setup project workspace: Network timeout'
      );
    });
  });
});
