import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import {
  TeamMentionPalette,
  type MentionableMember
} from '../TeamMentionPalette';

describe('TeamMentionPalette', () => {
  const mockMembers: MentionableMember[] = [
    {
      id: 'astra',
      name: 'Astra',
      role: 'Tech Lead',
      driverId: 'agy',
      isLead: true,
      isAssigned: true
    },
    {
      id: 'soren',
      name: 'Soren',
      role: 'Codebase Engineer',
      driverId: 'agy',
      isLead: false,
      isAssigned: true
    },
    {
      id: 'claude-arch',
      name: 'Claude Architect',
      role: 'System Architect',
      driverId: 'claude',
      isLead: false,
      isAssigned: true
    }
  ];

  it('does not render when isOpen is false', () => {
    const { container } = render(
      <TeamMentionPalette
        members={mockMembers}
        query=""
        isOpen={false}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );
    expect(container.firstChild).toBeNull();
  });

  it('renders all team members when isOpen is true and query is empty', () => {
    render(
      <TeamMentionPalette
        members={mockMembers}
        query=""
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );

    expect(screen.getByTestId('team-mention-palette')).toBeInTheDocument();
    expect(screen.getByText('Team Members')).toBeInTheDocument();
    expect(screen.getByText(/3 members/i)).toBeInTheDocument();

    expect(screen.getByText('Astra')).toBeInTheDocument();
    expect(screen.getByText('@astra')).toBeInTheDocument();
    expect(screen.getByText('Tech Lead')).toBeInTheDocument();
    expect(screen.getByText('Lead')).toBeInTheDocument();

    expect(screen.getByText('Soren')).toBeInTheDocument();
    expect(screen.getByText('@soren')).toBeInTheDocument();

    expect(screen.getByText('Claude Architect')).toBeInTheDocument();
    expect(screen.getByText('@claude-arch')).toBeInTheDocument();
  });

  it('filters team members by name, handle, role, or driver', () => {
    const { rerender } = render(
      <TeamMentionPalette
        members={mockMembers}
        query="soren"
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );

    expect(screen.getByText('Soren')).toBeInTheDocument();
    expect(screen.queryByText('Astra')).not.toBeInTheDocument();
    expect(screen.queryByText('Claude Architect')).not.toBeInTheDocument();

    // Query by handle
    rerender(
      <TeamMentionPalette
        members={mockMembers}
        query="claude"
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );
    expect(screen.getByText('Claude Architect')).toBeInTheDocument();
    expect(screen.queryByText('Astra')).not.toBeInTheDocument();

    // Query by role
    rerender(
      <TeamMentionPalette
        members={mockMembers}
        query="tech"
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );
    expect(screen.getByText('Astra')).toBeInTheDocument();
    expect(screen.queryByText('Soren')).not.toBeInTheDocument();
  });

  it('renders empty feedback when no members match query', () => {
    render(
      <TeamMentionPalette
        members={mockMembers}
        query="unknown-person"
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={vi.fn()}
      />
    );

    expect(screen.getByTestId('no-team-members-match')).toBeInTheDocument();
    expect(screen.getByText(/No team members match "unknown-person"/i)).toBeInTheDocument();
  });

  it('calls onSelect when member option is clicked', () => {
    const handleSelect = vi.fn();
    render(
      <TeamMentionPalette
        members={mockMembers}
        query=""
        isOpen={true}
        highlightedIndex={0}
        onSelect={handleSelect}
        onClose={vi.fn()}
      />
    );

    const sorenOption = screen.getByTestId('team-mention-option-soren');
    fireEvent.click(sorenOption);

    expect(handleSelect).toHaveBeenCalledWith(mockMembers[1]);
  });

  it('calls onClose when close button is clicked', () => {
    const handleClose = vi.fn();
    render(
      <TeamMentionPalette
        members={mockMembers}
        query=""
        isOpen={true}
        highlightedIndex={0}
        onSelect={vi.fn()}
        onClose={handleClose}
      />
    );

    const closeBtn = screen.getByLabelText(/Close mention list/i);
    fireEvent.click(closeBtn);

    expect(handleClose).toHaveBeenCalledTimes(1);
  });
});
