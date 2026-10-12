import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { AgentInventoryCard } from '../AgentInventoryCard';
import type { Agent } from '../../types';

describe('AgentInventoryCard', () => {
  it('renders agent name, id, and Tier 2 Senior Manager badge correctly', () => {
    const agent: Agent = {
      id: 'gemini-spark',
      name: 'Gemini Spark',
      runtimeType: 0,
      trustTier: 2,
      personaPrompt: 'Lead cloud agent',
      memorySummary: 'Active operational journal synchronized.',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    render(<AgentInventoryCard agent={agent} />);

    expect(screen.getByText('Gemini Spark')).toBeInTheDocument();
    expect(screen.getByText('gemini-spark')).toBeInTheDocument();
    expect(screen.getByText('Tier 2 (Senior Manager)')).toBeInTheDocument();
    expect(screen.getByText('Active operational journal synchronized.')).toBeInTheDocument();
  });

  it('renders Tier 1 Intern badge for junior agents', () => {
    const agent: Agent = {
      id: 'intern-bot',
      name: 'Intern Bot',
      runtimeType: 1,
      trustTier: 1,
      personaPrompt: 'Junior worker',
      memorySummary: '',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    render(<AgentInventoryCard agent={agent} />);

    expect(screen.getByText('Intern Bot')).toBeInTheDocument();
    expect(screen.getByText('Tier 1 (Intern)')).toBeInTheDocument();
  });
});
