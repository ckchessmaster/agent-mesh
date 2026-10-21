import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { JobCockpit, getActiveMentionQuery } from '../JobCockpit';
import { ProjectJobStatus, type Project, type ProjectJob, type JobTask, type JobMessage, type SessionAttachment } from '../../types';

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
});
