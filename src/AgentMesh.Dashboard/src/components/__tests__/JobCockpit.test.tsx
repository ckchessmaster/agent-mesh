import React from 'react';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { JobCockpit, getActiveMentionQuery } from '../JobCockpit';
import { ProjectJobStatus, type Project, type ProjectJob, type JobTask, type JobMessage } from '../../types';

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
