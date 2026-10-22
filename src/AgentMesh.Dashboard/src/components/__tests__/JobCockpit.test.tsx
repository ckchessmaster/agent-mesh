import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { JobCockpit, getActiveMentionQuery } from '../JobCockpit';
import { ProjectJobStatus, type Project, type ProjectJob, type JobTask, type JobMessage, type SessionAttachment } from '../../types';
import { api } from '../../services/api';

vi.mock('../../services/api', () => ({
  api: {
    uploadJobAttachments: vi.fn(),
    uploadTaskAttachments: vi.fn(),
    deleteTaskAttachment: vi.fn()
  }
}));

describe('getActiveMentionQuery helper', () => {
  it('detects mention query at beginning of string', () => {
    expect(getActiveMentionQuery('@', 1)).toEqual({ query: '', startIndex: 0 });
    expect(getActiveMentionQuery('@ast', 4)).toEqual({ query: 'ast', startIndex: 0 });
  });

  it('detects mention query after whitespace', () => {
    expect(getActiveMentionQuery('Hello @', 7)).toEqual({ query: '', startIndex: 6 });
    expect(getActiveMentionQuery('Hello @sor', 10)).toEqual({ query: 'sor', startIndex: 6 });
    expect(getActiveMentionQuery('Line 1\n@claude', 14)).toEqual({ query: 'claude', startIndex: 7 });
  });

  it('ignores email addresses where @ is preceded by a non-whitespace character', () => {
    expect(getActiveMentionQuery('contact@example.com', 20)).toBeNull();
    expect(getActiveMentionQuery('user@domain', 11)).toBeNull();
  });

  it('returns null if there is whitespace after the @ before the cursor', () => {
    expect(getActiveMentionQuery('@astra please test', 18)).toBeNull();
  });

  it('detects the active mention when multiple mentions exist', () => {
    const text = '@astra and @qa';
    expect(getActiveMentionQuery(text, text.length)).toEqual({ query: 'qa', startIndex: 11 });
  });
});

describe('JobCockpit - Team Member Mention Autocomplete', () => {
  const mockProject: Project = {
    id: 'agent-mesh',
    name: 'Agent Mesh',
    description: '',
    workspaceType: 0,
    gitBaseBranch: 'main',
    projectInstructions: '',
    defaultLeadPersonaId: 'agy-astra',
    defaultLeadPersonaName: 'Astra',
    assignedPersonas: [
      { id: 'agy-astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' },
      { id: 'agy-soren', name: 'Soren', role: 'Codebase Engineer', driverId: 'agy' }
    ],
    jobCount: 1,
    activeJobCount: 1,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockJob: ProjectJob = {
    id: 'job-1',
    projectId: 'agent-mesh',
    projectName: 'Agent Mesh',
    title: 'Enhancements',
    description: '',
    status: ProjectJobStatus.Active,
    gitBranchName: 'mesh/job-558ef323',
    tasks: [],
    messageCount: 0,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockProps = {
    project: mockProject,
    job: mockJob,
    tasks: [] as JobTask[],
    messages: [] as JobMessage[],
    onSendMessage: vi.fn(),
    onAddTask: vi.fn(),
    onToggleTask: vi.fn(),
    onDeleteTask: vi.fn(),
    onCompleteJob: vi.fn()
  };

  it('opens team mention palette when user types @ in the message area', () => {
    render(<JobCockpit {...mockProps} />);

    const textarea = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i);

    // Initial state: palette not open
    expect(screen.queryByTestId('team-mention-palette')).not.toBeInTheDocument();

    // Type @
    fireEvent.change(textarea, { target: { value: '@' } });

    // Palette appears with team members
    const palette = screen.getByTestId('team-mention-palette');
    expect(palette).toBeInTheDocument();
    expect(within(palette).getByText('Team Members')).toBeInTheDocument();
    expect(within(palette).getByText('Astra')).toBeInTheDocument();
    expect(within(palette).getByText('@agy-astra')).toBeInTheDocument();
    expect(within(palette).getByText('Soren')).toBeInTheDocument();
    expect(within(palette).getByText('@agy-soren')).toBeInTheDocument();
  });

  it('filters team members list as user continues typing query after @', () => {
    render(<JobCockpit {...mockProps} />);

    const textarea = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i);

    // Type @sor
    fireEvent.change(textarea, { target: { value: '@sor' } });

    const palette = screen.getByTestId('team-mention-palette');
    expect(palette).toBeInTheDocument();
    expect(within(palette).getByText('Soren')).toBeInTheDocument();
    expect(within(palette).queryByText('Astra')).not.toBeInTheDocument();
  });

  it('inserts selected team member mention and trailing space on click, closing palette', () => {
    render(<JobCockpit {...mockProps} />);

    const textarea = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i);

    fireEvent.change(textarea, { target: { value: '@' } });

    const sorenOption = screen.getByTestId('team-mention-option-agy-soren');
    fireEvent.click(sorenOption);

    // Prompt updated with mention
    expect((textarea as HTMLTextAreaElement).value).toBe('@agy-soren ');
    // Palette closes
    expect(screen.queryByTestId('team-mention-palette')).not.toBeInTheDocument();
  });

  it('navigates options with ArrowDown/ArrowUp and selects with Enter', () => {
    render(<JobCockpit {...mockProps} />);

    const textarea = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i);

    fireEvent.change(textarea, { target: { value: '@' } });

    // First option (Astra) is highlighted by default (index 0)
    // Press ArrowDown to highlight Soren (index 1)
    fireEvent.keyDown(textarea, { key: 'ArrowDown' });

    // Press Enter to select highlighted member
    fireEvent.keyDown(textarea, { key: 'Enter' });

    expect((textarea as HTMLTextAreaElement).value).toBe('@agy-soren ');
    expect(screen.queryByTestId('team-mention-palette')).not.toBeInTheDocument();
  });

  it('closes mention palette when Escape is pressed without modifying text', () => {
    render(<JobCockpit {...mockProps} />);

    const textarea = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i);

    fireEvent.change(textarea, { target: { value: '@ast' } });
    expect(screen.getByTestId('team-mention-palette')).toBeInTheDocument();

    fireEvent.keyDown(textarea, { key: 'Escape' });

    expect(screen.queryByTestId('team-mention-palette')).not.toBeInTheDocument();
    expect((textarea as HTMLTextAreaElement).value).toBe('@ast');
  });
});

describe('JobCockpit - Attachment Support', () => {
  const mockProject: Project = {
    id: 'agent-mesh',
    name: 'Agent Mesh',
    description: '',
    workspaceType: 0,
    gitBaseBranch: 'main',
    projectInstructions: '',
    defaultLeadPersonaId: 'agy-astra',
    defaultLeadPersonaName: 'Astra',
    assignedPersonas: [
      { id: 'agy-astra', name: 'Astra', role: 'Tech Lead', driverId: 'agy' }
    ],
    jobCount: 1,
    activeJobCount: 1,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockJob: ProjectJob = {
    id: 'job-attach-1',
    projectId: 'agent-mesh',
    projectName: 'Agent Mesh',
    title: 'Attachment Interface Test',
    description: '',
    status: ProjectJobStatus.Active,
    gitBranchName: 'mesh/job-attach-1',
    tasks: [],
    messageCount: 0,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString()
  };

  const mockProps = {
    project: mockProject,
    job: mockJob,
    tasks: [] as JobTask[],
    messages: [] as JobMessage[],
    onSendMessage: vi.fn(),
    onAddTask: vi.fn(),
    onToggleTask: vi.fn(),
    onDeleteTask: vi.fn(),
    onCompleteJob: vi.fn()
  };

  it('renders paperclip attach button and hidden file input', () => {
    render(<JobCockpit {...mockProps} />);

    expect(screen.getByTestId('job-attach-btn')).toBeInTheDocument();
    expect(screen.getByTestId('job-file-input')).toBeInTheDocument();
  });

  it('stages attachments via file input, enables sending without prompt text, and dispatches attachments', async () => {
    const onSend = vi.fn().mockResolvedValue(undefined);
    const mockUploaded: SessionAttachment[] = [
      {
        id: 'att-mock-1',
        sessionId: 'job-attach-1',
        fileName: 'architecture.png',
        contentType: 'image/png',
        sizeBytes: 1024,
        s3Key: 'jobs/job-attach-1/attachments/att-mock-1/architecture.png',
        downloadUrl: '/api/project-jobs/job-attach-1/attachments/att-mock-1/content',
        createdAt: new Date().toISOString()
      }
    ];

    const onUpload = vi.fn().mockResolvedValue(mockUploaded);

    render(
      <JobCockpit
        {...mockProps}
        onSendMessage={onSend}
        onUploadAttachments={onUpload}
      />
    );

    const fileInput = screen.getByTestId('job-file-input');
    const sendBtn = screen.getByRole('button', { name: /Send Message/i });

    // Send button disabled initially because text is empty
    expect(sendBtn).toBeDisabled();

    // Select file
    const file = new File(['png-data'], 'architecture.png', { type: 'image/png' });
    fireEvent.change(fileInput, { target: { files: [file] } });

    await waitFor(() => {
      expect(onUpload).toHaveBeenCalledWith([file]);
      expect(screen.getByTestId('job-staged-attachments')).toBeInTheDocument();
      expect(screen.getByText('architecture.png')).toBeInTheDocument();
    });

    // Send button is enabled even though prompt text is blank
    await waitFor(() => {
      expect(sendBtn).not.toBeDisabled();
    });

    // Send the message
    fireEvent.click(sendBtn);

    await waitFor(() => {
      expect(onSend).toHaveBeenCalledWith(
        'Please review the attached file(s).',
        mockUploaded
      );
    });

    // Staged attachments cleared after sending
    await waitFor(() => {
      expect(screen.queryByTestId('job-staged-attachments')).not.toBeInTheDocument();
    });
  });

  it('renders image thumbnails with zoom lightbox and file pills in message bubbles', async () => {
    const mockMessages: JobMessage[] = [
      {
        id: 'msg-with-attach',
        jobId: 'job-attach-1',
        senderType: 'user',
        content: 'Check this diagram',
        mentions: [],
        structuredPayloadJson: JSON.stringify({
          attachments: [
            {
              id: 'att-img',
              sessionId: 'job-attach-1',
              fileName: 'flowchart.png',
              contentType: 'image/png',
              sizeBytes: 2048,
              downloadUrl: '/api/project-jobs/job-attach-1/attachments/att-img/content',
              createdAt: new Date().toISOString()
            },
            {
              id: 'att-doc',
              sessionId: 'job-attach-1',
              fileName: 'notes.txt',
              contentType: 'text/plain',
              sizeBytes: 512,
              downloadUrl: '/api/project-jobs/job-attach-1/attachments/att-doc/content',
              createdAt: new Date().toISOString()
            }
          ]
        }),
        createdAt: new Date().toISOString()
      }
    ];

    const onLightbox = vi.fn();

    render(
      <JobCockpit
        {...mockProps}
        messages={mockMessages}
        onOpenLightbox={onLightbox}
      />
    );

    // Both attachments rendered
    const attachmentsContainer = screen.getByTestId('job-message-attachments-msg-with-attach');
    expect(attachmentsContainer).toBeInTheDocument();

    // Image zoom button rendered
    const zoomBtn = screen.getByRole('button', { name: /Zoom image flowchart.png/i });
    expect(zoomBtn).toBeInTheDocument();

    // File download link rendered
    const fileLink = screen.getByText('notes.txt');
    expect(fileLink).toBeInTheDocument();

    // Click zoom opens lightbox
    fireEvent.click(zoomBtn);
    expect(onLightbox).toHaveBeenCalledWith(
      expect.objectContaining({
        fileName: 'flowchart.png',
        url: '/api/project-jobs/job-attach-1/attachments/att-img/content'
      })
    );
  });

  it('handles clipboard paste of files into the textarea', async () => {
    const onUpload = vi.fn().mockResolvedValue([
      {
        id: 'att-pasted-1',
        sessionId: 'job-attach-1',
        fileName: 'screenshot.png',
        contentType: 'image/png',
        sizeBytes: 4096,
        s3Key: '',
        downloadUrl: '/api/project-jobs/job-attach-1/attachments/att-pasted-1/content',
        createdAt: new Date().toISOString()
      }
    ]);

    render(<JobCockpit {...mockProps} onUploadAttachments={onUpload} />);

    const textarea = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i);

    const pastedFile = new File(['clip-data'], 'screenshot.png', { type: 'image/png' });
    fireEvent.paste(textarea, {
      clipboardData: {
        files: [pastedFile],
        items: []
      }
    });

    await waitFor(() => {
      expect(onUpload).toHaveBeenCalledWith([pastedFile]);
      expect(screen.getByTestId('job-staged-attachments')).toBeInTheDocument();
      expect(screen.getByText('screenshot.png')).toBeInTheDocument();
    });
  });

  it('shows dropzone overlay on dragOver and stages dropped files', async () => {
    const onUpload = vi.fn().mockResolvedValue([
      {
        id: 'att-dropped-1',
        sessionId: 'job-attach-1',
        fileName: 'data.csv',
        contentType: 'text/csv',
        sizeBytes: 100,
        s3Key: '',
        downloadUrl: '/api/project-jobs/job-attach-1/attachments/att-dropped-1/content',
        createdAt: new Date().toISOString()
      }
    ]);

    render(<JobCockpit {...mockProps} onUploadAttachments={onUpload} />);

    const container = screen.getByPlaceholderText(/Type your message, tasks, or delegate with @persona/i).closest('.flex-1.flex.flex-col')!;

    // Drag over shows overlay
    fireEvent.dragOver(container);
    expect(screen.getByTestId('job-dropzone-overlay')).toBeInTheDocument();

    // Drop stages file
    const droppedFile = new File(['csv-data'], 'data.csv', { type: 'text/csv' });
    fireEvent.drop(container, {
      dataTransfer: {
        files: [droppedFile]
      }
    });

    // Overlay removed
    expect(screen.queryByTestId('job-dropzone-overlay')).not.toBeInTheDocument();

    await waitFor(() => {
      expect(onUpload).toHaveBeenCalledWith([droppedFile]);
      expect(screen.getByTestId('job-staged-attachments')).toBeInTheDocument();
      expect(screen.getByText('data.csv')).toBeInTheDocument();
    });
  });

  it('renders active turn feedback indicator when an active persona turn is in progress', () => {
    const activeMessages: JobMessage[] = [
      {
        id: 'msg-u1',
        jobId: 'job-101',
        senderType: 'user',
        content: '@astra please start the implementation',
        mentions: ['astra'],
        turnSessionId: 'sess-active-turn',
        createdAt: new Date().toISOString()
      },
      {
        id: 'msg-ack',
        jobId: 'job-101',
        senderType: 'persona',
        senderId: 'astra',
        senderName: 'Astra',
        content: "I'm on it — starting work on this now.",
        mentions: [],
        turnSessionId: 'sess-active-turn',
        createdAt: new Date().toISOString()
      }
    ];

    render(
      <JobCockpit
        {...mockProps}
        messages={activeMessages}
      />
    );

    // Active working indicator is rendered
    expect(screen.getByTestId('job-active-turn-indicator')).toBeInTheDocument();
    expect(screen.getByText(/Astra is actively working on this task.../i)).toBeInTheDocument();
    expect(screen.getByText('View Trajectory')).toBeInTheDocument();
  });

  it('renders structured collapsible task checklist, allows expanding/collapsing and filtering', async () => {
    const mockTasksList: JobTask[] = [
      { id: 't-1', title: 'Task Alpha', isCompleted: true },
      { id: 't-2', title: 'Task Beta', isCompleted: false },
      { id: 't-3', title: 'Task Gamma', isCompleted: false }
    ];

    render(
      <JobCockpit
        {...mockProps}
        tasks={mockTasksList}
      />
    );

    // Header counter and percentage
    expect(screen.getByText(/1\/3 completed/i)).toBeInTheDocument();
    expect(screen.getByTestId('job-tasks-list')).toBeInTheDocument();

    // Verify default 'pending' filter shows only pending tasks
    expect(screen.getByText('#1')).toBeInTheDocument();
    expect(screen.getByText('Task Beta')).toBeInTheDocument();
    expect(screen.getByText('Task Gamma')).toBeInTheDocument();
    expect(screen.queryByText('Task Alpha')).not.toBeInTheDocument();

    // Filter to All shows all tasks
    const allFilterBtn = screen.getByRole('button', { name: /^All/i });
    fireEvent.click(allFilterBtn);
    expect(screen.getByText('Task Alpha')).toBeInTheDocument();
    expect(screen.getByText('Task Beta')).toBeInTheDocument();
    expect(screen.getByText('Task Gamma')).toBeInTheDocument();

    // Filter to only Completed
    const doneFilterBtn = screen.getByRole('button', { name: /^Done/i });
    fireEvent.click(doneFilterBtn);

    expect(screen.getByText('Task Alpha')).toBeInTheDocument();
    expect(screen.queryByText('Task Beta')).not.toBeInTheDocument();

    // Toggle collapse
    const toggleCollapseBtn = screen.getByTestId('toggle-tasks-btn');
    fireEvent.click(toggleCollapseBtn);

    expect(screen.queryByTestId('job-tasks-list')).not.toBeInTheDocument();
  });

  it('renders edit project button and triggers onEditProject callback', () => {
    const onEditProject = vi.fn();
    render(
      <JobCockpit
        {...mockProps}
        onEditProject={onEditProject}
      />
    );

    const editBtn = screen.getByTestId('cockpit-edit-project-btn');
    expect(editBtn).toBeInTheDocument();
    fireEvent.click(editBtn);

    expect(onEditProject).toHaveBeenCalledTimes(1);
  });

  it('renders stop button in active turn feedback card and invokes onStopTurn with turnSessionId', () => {
    const onStopTurn = vi.fn();
    const activeMessages: JobMessage[] = [
      {
        id: 'msg-1',
        jobId: 'job-1',
        senderType: 'user',
        senderId: 'user-1',
        senderName: 'User',
        content: '@persona-1 please write tests',
        mentions: ['persona-1'],
        turnSessionId: 'session-turn-999',
        createdAt: '2026-10-20T10:00:00Z'
      }
    ];

    render(
      <JobCockpit
        {...mockProps}
        messages={activeMessages}
        onStopTurn={onStopTurn}
      />
    );

    const stopBtn = screen.getByTestId('job-stop-turn-btn');
    expect(stopBtn).toBeInTheDocument();
    fireEvent.click(stopBtn);

    expect(onStopTurn).toHaveBeenCalledWith('session-turn-999');
  });

  it('hides active turn feedback card when sessions list indicates turn session is idle or completed', () => {
    const activeMessages: JobMessage[] = [
      {
        id: 'msg-1',
        jobId: 'job-1',
        senderType: 'user',
        senderId: 'user-1',
        senderName: 'User',
        content: '@persona-1 please write tests',
        mentions: ['persona-1'],
        turnSessionId: 'session-turn-999',
        createdAt: '2026-10-20T10:00:00Z'
      }
    ];

    // Session is active (not thinking)
    const sessions = [
      {
        id: 'session-turn-999',
        title: 'Job Turn',
        status: 'active',
        createdAt: '2026-10-20T10:00:00Z',
        updatedAt: '2026-10-20T10:01:00Z'
      }
    ];

    render(
      <JobCockpit
        {...mockProps}
        messages={activeMessages}
        sessions={sessions as any}
      />
    );

    expect(screen.queryByTestId('job-active-turn-indicator')).not.toBeInTheDocument();
  });

  it('renders rich markdown formatting in chat messages (headings, bold, lists, code)', () => {
    const markdownMessages: JobMessage[] = [
      {
        id: 'msg-summary',
        jobId: 'job-1',
        senderType: 'persona',
        senderId: 'astra',
        senderName: 'Astra',
        content: `### Implementation Summary\n* **Status:** Complete\n1. First deliverable\n\`const ok = true;\``,
        mentions: [],
        createdAt: '2026-10-20T10:00:00Z'
      }
    ];

    render(
      <JobCockpit
        {...mockProps}
        messages={markdownMessages}
      />
    );

    // Formatted markdown elements should be rendered
    expect(screen.getByRole('heading', { level: 3, name: 'Implementation Summary' })).toBeInTheDocument();
    expect(screen.getByText('Status:')).toBeInTheDocument();
    expect(screen.getByText('First deliverable')).toBeInTheDocument();
    expect(screen.getByText('const ok = true;')).toBeInTheDocument();
  });

  it('renders team roster in sidebar and supports toggling team sidebar visibility', () => {
    render(
      <JobCockpit
        {...mockProps}
      />
    );

    // Team sidebar is visible by default
    const sidebar = screen.getByTestId('job-team-sidebar');
    expect(sidebar).toBeInTheDocument();
    expect(screen.getByText('Assigned Team Members')).toBeInTheDocument();

    // Toggle button collapses sidebar
    const toggleBtn = screen.getByTestId('toggle-team-sidebar-btn');
    fireEvent.click(toggleBtn);
    expect(screen.queryByTestId('job-team-sidebar')).not.toBeInTheDocument();

    // Toggle button re-expands sidebar
    fireEvent.click(toggleBtn);
    expect(screen.getByTestId('job-team-sidebar')).toBeInTheDocument();
  });

  it('renders archive job button and calls onArchiveJob upon confirmation', async () => {
    const onArchiveJob = vi.fn().mockResolvedValue(undefined);
    render(
      <JobCockpit
        {...mockProps}
        onArchiveJob={onArchiveJob}
      />
    );

    const archiveBtn = screen.getByTestId('cockpit-archive-job-btn');
    expect(archiveBtn).toBeInTheDocument();

    fireEvent.click(archiveBtn);
    expect(screen.getByText('Archive job?')).toBeInTheDocument();

    const confirmBtn = screen.getByTestId('cockpit-confirm-archive-btn');
    fireEvent.click(confirmBtn);

    expect(onArchiveJob).toHaveBeenCalledTimes(1);
  });

  it('renders archived job state with reopen button, disabled composer, and calls onReopenJob', async () => {
    const onReopenJob = vi.fn().mockResolvedValue(undefined);
    const archivedJob = {
      ...mockJob,
      status: ProjectJobStatus.Archived
    };

    render(
      <JobCockpit
        {...mockProps}
        job={archivedJob}
        onReopenJob={onReopenJob}
      />
    );

    // Shows Archived status badge
    expect(screen.getByText('Archived')).toBeInTheDocument();

    // Reopen button appears
    const reopenBtn = screen.getByTestId('cockpit-reopen-job-btn');
    expect(reopenBtn).toBeInTheDocument();

    // Composer is disabled with archived message
    const textarea = screen.getByPlaceholderText(/This job is archived and read-only/i);
    expect(textarea).toBeDisabled();

    // Clicking reopen calls onReopenJob
    fireEvent.click(reopenBtn);
    expect(onReopenJob).toHaveBeenCalledTimes(1);
  });

  it('renders sequential task badges (Completed, In Progress, Queued) and current focus header banner', () => {
    const sequentialTasks: JobTask[] = [
      { id: 'task-1', title: 'First Task Done', isCompleted: true, completedAt: new Date().toISOString() },
      { id: 'task-2', title: 'Second Task Active', isCompleted: false },
      { id: 'task-3', title: 'Third Task Queued', isCompleted: false }
    ];

    render(
      <JobCockpit
        {...mockProps}
        tasks={sequentialTasks}
      />
    );

    // Current focus badge in header
    expect(screen.getByTestId('current-focus-badge')).toBeInTheDocument();
    expect(screen.getByText(/#2 Second Task Active/i)).toBeInTheDocument();

    // Switch to All to see completed and pending tasks
    const allBtn = screen.getByRole('button', { name: /^All/i });
    fireEvent.click(allBtn);

    // Badges in task rows
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByTestId('task-focus-task-2')).toBeInTheDocument();
    expect(screen.getByText('Queued')).toBeInTheDocument();
  });

  it('renders priority badge in job header', () => {
    render(
      <JobCockpit
        {...mockProps}
        job={{
          ...mockJob,
          priority: 3 // Urgent
        }}
      />
    );

    const prioBadge = screen.getByTestId('job-priority-badge');
    expect(prioBadge).toBeInTheDocument();
    expect(prioBadge).toHaveTextContent('Urgent');
  });

  it('renders Queued Turns indicator in sidebar and shows queued status on team member', () => {
    const queuedTurn = {
      id: 'turn-1',
      jobId: mockJob.id,
      projectId: mockJob.projectId,
      targetPersonaId: 'agy-astra',
      senderType: 'user',
      content: 'Queued turn prompt',
      mentions: ['agy-astra'],
      priority: 1,
      createdAt: new Date().toISOString()
    };

    render(
      <JobCockpit
        {...mockProps}
        job={{
          ...mockJob,
          queuedTurns: [queuedTurn]
        }}
        personas={[
          {
            id: 'agy-astra',
            name: 'Astra',
            role: 'Tech Lead',
            personaPrompt: '',
            driverId: 'agy',
            maxConcurrentJobs: 1,
            trustTier: 1,
            createdAt: new Date().toISOString()
          }
        ]}
      />
    );

    // Queued turns indicator in sidebar
    expect(screen.getByTestId('job-queued-turns-indicator')).toBeInTheDocument();
    expect(screen.getByText(/1 Queued Turn/i)).toBeInTheDocument();
    expect(screen.getByText(/Waiting for Slot/i)).toBeInTheDocument();

    // Queued badge on team member
    expect(screen.getByText('Queued (1)')).toBeInTheDocument();
  });

  it('renders Discuss First badge, allows toggling discussion requirement, and adding a task with discussion enabled', async () => {
    const onAddTask = vi.fn().mockResolvedValue(undefined);
    const onUpdateTask = vi.fn().mockResolvedValue(undefined);

    const testTasks: JobTask[] = [
      { id: 'task-1', title: 'Task with Discussion', isCompleted: false, requiresDiscussion: true },
      { id: 'task-2', title: 'Standard Task', isCompleted: false, requiresDiscussion: false }
    ];

    render(
      <JobCockpit
        {...mockProps}
        tasks={testTasks}
        onAddTask={onAddTask}
        onUpdateTask={onUpdateTask}
      />
    );

    // Current focus badge has [Discuss] tag
    expect(screen.getByTestId('current-focus-badge')).toBeInTheDocument();
    expect(screen.getByTestId('focus-discuss-tag')).toHaveTextContent('[Discuss]');

    // Task 1 has Discuss First badge
    expect(screen.getByTestId('task-discuss-task-1')).toBeInTheDocument();
    expect(screen.getByText('Discuss First')).toBeInTheDocument();

    // Toggle discussion on Task 2
    const toggleTask2Btn = screen.getByTestId('toggle-discuss-task-task-2');
    fireEvent.click(toggleTask2Btn);
    expect(onUpdateTask).toHaveBeenCalledWith('task-2', { requiresDiscussion: true });

    // Toggle discussion on Task 1 off
    const toggleTask1Btn = screen.getByTestId('toggle-discuss-task-task-1');
    fireEvent.click(toggleTask1Btn);
    expect(onUpdateTask).toHaveBeenCalledWith('task-1', { requiresDiscussion: false });

    // Inline task form with Discuss toggle
    const input = screen.getByTestId('new-task-input');
    const discussToggle = screen.getByTestId('new-task-discuss-toggle');
    fireEvent.change(input, { target: { value: 'New Architecture Task' } });
    fireEvent.click(discussToggle);

    // Submit form
    const addBtn = screen.getByRole('button', { name: /Add Task/i });
    fireEvent.click(addBtn);

    expect(onAddTask).toHaveBeenCalledWith('New Architecture Task', true);
  });

  it('renders interactive Ready to Proceed card when agent outputs [READY_TO_PROCEED], strips tag from message, and allows proceeding to code', async () => {
    const onSendMessage = vi.fn().mockResolvedValue(undefined);
    const onUpdateTask = vi.fn().mockResolvedValue(undefined);

    const discussionTask: JobTask = {
      id: 'task-discuss-1',
      title: 'Database Schema Review',
      isCompleted: false,
      requiresDiscussion: true
    };

    const agentMsg: JobMessage = {
      id: 'msg-ready-1',
      jobId: mockJob.id,
      senderType: 'persona',
      senderId: 'astra',
      senderName: 'Astra',
      content: 'I have evaluated the database trade-offs and we are aligned on using PostgreSQL JSONB.\n\n[READY_TO_PROCEED]',
      mentions: [],
      createdAt: new Date().toISOString()
    };

    const { rerender } = render(
      <JobCockpit
        {...mockProps}
        tasks={[discussionTask]}
        messages={[agentMsg]}
        onSendMessage={onSendMessage}
        onUpdateTask={onUpdateTask}
      />
    );

    // Verify tag is stripped from rendered message text
    expect(screen.queryByText(/\[READY_TO_PROCEED\]/)).not.toBeInTheDocument();
    expect(screen.getByText(/I have evaluated the database trade-offs/i)).toBeInTheDocument();

    // Verify Ready to Proceed card is rendered
    expect(screen.getByTestId('ready-to-proceed-card-msg-ready-1')).toBeInTheDocument();
    expect(screen.getByText('Ready to Proceed')).toBeInTheDocument();
    expect(screen.getByText('Alignment Proposed')).toBeInTheDocument();

    // Click Proceed to Coding
    const proceedBtn = screen.getByTestId('proceed-to-coding-btn');
    expect(proceedBtn).toBeInTheDocument();
    fireEvent.click(proceedBtn);

    // Verify discussion requirement unflagged and message dispatched
    await waitFor(() => {
      expect(onUpdateTask).toHaveBeenCalledWith('task-discuss-1', { requiresDiscussion: false });
      expect(onSendMessage).toHaveBeenCalledWith(
        expect.stringContaining('Discussion approved — proceed with implementation of "Database Schema Review".')
      );
    });

    // Rerender with task discussion cleared
    rerender(
      <JobCockpit
        {...mockProps}
        tasks={[{ ...discussionTask, requiresDiscussion: false }]}
        messages={[agentMsg]}
        onSendMessage={onSendMessage}
        onUpdateTask={onUpdateTask}
      />
    );

    // Card now displays unlocked status
    expect(screen.getByText(/Discussion approved — implementation unlocked/i)).toBeInTheDocument();
    expect(screen.queryByTestId('proceed-to-coding-btn')).not.toBeInTheDocument();
  });

  it('renders task attachments, supports lightbox on image click, allows deleting task attachment, and adding new task with attachments', async () => {
    const onAddTask = vi.fn().mockResolvedValue(undefined);
    const onUpdateTask = vi.fn().mockResolvedValue(undefined);
    const onOpenLightbox = vi.fn();

    const sampleImgAtt: SessionAttachment = {
      id: 'att-mock-img',
      sessionId: mockJob.id,
      fileName: 'mock-ui.png',
      contentType: 'image/png',
      sizeBytes: 2048,
      s3Key: 'mock-ui.png',
      downloadUrl: '/api/project-jobs/attachments/mock-ui.png',
      createdAt: new Date().toISOString()
    };

    const sampleDocAtt: SessionAttachment = {
      id: 'att-mock-doc',
      sessionId: mockJob.id,
      fileName: 'mock-spec.pdf',
      contentType: 'application/pdf',
      sizeBytes: 4096,
      s3Key: 'mock-spec.pdf',
      downloadUrl: '/api/project-jobs/attachments/mock-spec.pdf',
      createdAt: new Date().toISOString()
    };

    const taskWithAtts: JobTask = {
      id: 'task-with-atts',
      title: 'Task With Attachments',
      isCompleted: false,
      attachments: [sampleImgAtt, sampleDocAtt]
    };

    render(
      <JobCockpit
        {...mockProps}
        tasks={[taskWithAtts]}
        onAddTask={onAddTask}
        onUpdateTask={onUpdateTask}
        onOpenLightbox={onOpenLightbox}
      />
    );

    // Verify task attachments rendered
    expect(screen.getByTestId('task-attachments-task-with-atts')).toBeInTheDocument();
    expect(screen.getByText('mock-ui.png')).toBeInTheDocument();
    expect(screen.getByText('mock-spec.pdf')).toBeInTheDocument();

    // Click image attachment to open lightbox
    fireEvent.click(screen.getByText('mock-ui.png'));
    expect(onOpenLightbox).toHaveBeenCalledWith({
      url: '/api/project-jobs/attachments/mock-ui.png',
      fileName: 'mock-ui.png',
      sizeBytes: 2048
    });

    // Delete document attachment from task
    const delDocBtn = screen.getByTestId('delete-task-att-att-mock-doc');
    fireEvent.click(delDocBtn);
    await waitFor(() => {
      expect(onUpdateTask).toHaveBeenCalledWith('task-with-atts', {
        attachments: [sampleImgAtt]
      });
    });

    // Add new task with staged attachments
    const newAtt: SessionAttachment = {
      id: 'att-staged-1',
      sessionId: mockJob.id,
      fileName: 'staged-file.txt',
      contentType: 'text/plain',
      sizeBytes: 1024,
      s3Key: 'staged-file.txt',
      downloadUrl: '/api/project-jobs/attachments/staged-file.txt',
      createdAt: new Date().toISOString()
    };
    vi.mocked(api.uploadJobAttachments).mockResolvedValue([newAtt]);

    const taskInput = screen.getByTestId('new-task-input');
    fireEvent.change(taskInput, { target: { value: 'Task with new attachment' } });

    const newTaskFileInput = screen.getByTestId('new-task-file-input');
    const txtFile = new File(['text-content'], 'staged-file.txt', { type: 'text/plain' });
    fireEvent.change(newTaskFileInput, { target: { files: [txtFile] } });

    await waitFor(() => {
      expect(screen.getByTestId('new-task-staged-attachments')).toBeInTheDocument();
      expect(screen.getAllByText('staged-file.txt').length).toBeGreaterThan(0);
    });

    const addBtn = screen.getByRole('button', { name: /Add Task/i });
    fireEvent.click(addBtn);

    await waitFor(() => {
      expect(onAddTask).toHaveBeenCalledWith(
        'Task with new attachment',
        false,
        [newAtt]
      );
    });
  });

  it('surfaces questions from agent messages, renders interactive question cards, shows Waiting for Input badge and composer hint, and allows submitting answers', async () => {
    const onAnswerQuestion = vi.fn().mockResolvedValue(undefined);

    const questionMsg: JobMessage = {
      id: 'msg-question-1',
      jobId: mockJob.id,
      senderType: 'persona',
      senderId: 'persona-astra',
      senderName: 'Astra',
      content: 'I have a question before we start coding:',
      turnSessionId: 'turn-session-qa-1',
      structuredPayloadJson: JSON.stringify({
        toolName: 'ask_question',
        questions: [
          {
            question: 'Which database should we use?',
            options: ['PostgreSQL', 'SQLite', 'MongoDB'],
            is_multi_select: false
          }
        ]
      }),
      createdAt: '2026-10-21T18:00:00Z'
    };

    const waitingSession: AgentSession = {
      id: 'turn-session-qa-1',
      title: 'Turn Session QA',
      status: 'waiting_for_input',
      createdAt: '2026-10-21T18:00:00Z',
      updatedAt: '2026-10-21T18:00:00Z'
    };

    const { rerender } = render(
      <JobCockpit
        {...mockProps}
        messages={[questionMsg]}
        sessions={[waitingSession]}
        onAnswerQuestion={onAnswerQuestion}
      />
    );

    // Question card is rendered
    expect(screen.getByTestId('question-card-msg-question-1')).toBeInTheDocument();
    expect(screen.getByText('Agent Question — Human Input Required')).toBeInTheDocument();
    expect(screen.getByText('Which database should we use?')).toBeInTheDocument();
    expect(screen.getByText('PostgreSQL')).toBeInTheDocument();
    expect(screen.getByText('SQLite')).toBeInTheDocument();
    expect(screen.getByText('MongoDB')).toBeInTheDocument();

    // Waiting for Input badge is rendered in header
    expect(screen.getByTestId('waiting-for-input-badge')).toBeInTheDocument();
    expect(screen.getByTestId('waiting-for-input-badge')).toHaveTextContent('Waiting for Input');

    // Contextual composer placeholder
    const textarea = screen.getByPlaceholderText(/Agent is waiting for your answer above/i);
    expect(textarea).toBeInTheDocument();

    // Select PostgreSQL option pill
    fireEvent.click(screen.getByText('PostgreSQL'));

    // Submit answer via button
    const submitBtn = screen.getByRole('button', { name: /Submit Answer/i });
    fireEvent.click(submitBtn);

    await waitFor(() => {
      expect(onAnswerQuestion).toHaveBeenCalledWith(
        'turn-session-qa-1',
        expect.objectContaining({
          toolCallId: 'msg-question-1',
          answers: [
            expect.objectContaining({
              question: 'Which database should we use?',
              selectedOptions: ['PostgreSQL']
            })
          ]
        })
      );
    });

    // Answered state renders
    const answerMsg: JobMessage = {
      id: 'msg-answer-1',
      jobId: mockJob.id,
      senderType: 'user',
      senderId: 'user-1',
      senderName: 'You',
      content: 'User answered question:\nQ: Which database should we use? -> A: PostgreSQL',
      createdAt: '2026-10-21T18:01:00Z'
    };

    rerender(
      <JobCockpit
        {...mockProps}
        messages={[questionMsg, answerMsg]}
        sessions={[{ ...waitingSession, status: 'active' }]}
        onAnswerQuestion={onAnswerQuestion}
      />
    );

    // Question card transitioned to Answered
    expect(screen.getByTestId('question-card-answered-msg-question-1')).toBeInTheDocument();
    expect(screen.getByText('Agent Question — Answered')).toBeInTheDocument();
    expect(screen.queryByTestId('waiting-for-input-badge')).not.toBeInTheDocument();
  });

  it('answers pending question automatically when operator types response into main composer', async () => {
    const onAnswerQuestion = vi.fn().mockResolvedValue(undefined);

    const questionMsg: JobMessage = {
      id: 'msg-question-comp',
      jobId: mockJob.id,
      senderType: 'persona',
      senderId: 'persona-astra',
      senderName: 'Astra',
      content: 'Please let me know:\n1. What architecture style should we follow?',
      turnSessionId: 'turn-session-comp-1',
      createdAt: '2026-10-21T18:10:00Z'
    };

    render(
      <JobCockpit
        {...mockProps}
        messages={[questionMsg]}
        onAnswerQuestion={onAnswerQuestion}
      />
    );

    // Question card is rendered from text question
    const qCard = screen.getByTestId('question-card-msg-question-comp');
    expect(within(qCard).getByText('What architecture style should we follow?')).toBeInTheDocument();

    // Type answer into composer textarea
    const textarea = screen.getByPlaceholderText(/Agent is waiting for your answer above/i);
    fireEvent.change(textarea, { target: { value: 'Microservices with gRPC' } });

    // Submit composer
    const sendBtn = screen.getByTitle('Send Message');
    fireEvent.click(sendBtn);

    await waitFor(() => {
      expect(onAnswerQuestion).toHaveBeenCalledWith(
        'turn-session-comp-1',
        expect.objectContaining({
          toolCallId: 'msg-question-comp',
          answers: [
            expect.objectContaining({
              question: 'What architecture style should we follow?',
              customText: 'Microservices with gRPC'
            })
          ]
        })
      );
    });
  });
});
