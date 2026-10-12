import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ApprovalCockpit } from '../ApprovalCockpit';
import { api } from '../../services/api';
import type { StagedJob } from '../../types';

vi.mock('../../services/api', () => ({
  api: {
    getJobs: vi.fn(),
    submitApproval: vi.fn(),
  },
}));

describe('ApprovalCockpit', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders clean empty state when no jobs are pending review', async () => {
    vi.mocked(api.getJobs).mockResolvedValue([]);

    render(<ApprovalCockpit />);

    await waitFor(() => {
      expect(screen.getByText('Human Approval Cockpit')).toBeInTheDocument();
      expect(screen.getByText('No actions currently require human review.')).toBeInTheDocument();
    });
  });

  it('renders pending staged job with approve and reject buttons', async () => {
    const pendingJob: StagedJob = {
      id: 'job-test-123',
      sessionId: 'sess-abc',
      requestedByAgentId: 'gemini-spark',
      targetService: 'github',
      actionVerb: 'apply_diff',
      payloadJson: '{"repo":"agent-mesh","branch":"main"}',
      approvalStatus: 1,
      executionStatus: 0,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString()
    };

    vi.mocked(api.getJobs).mockResolvedValue([pendingJob]);

    render(<ApprovalCockpit />);

    await waitFor(() => {
      expect(screen.getByText('github:apply_diff')).toBeInTheDocument();
      expect(screen.getByText('Approve & Execute')).toBeInTheDocument();
      expect(screen.getByText('Reject')).toBeInTheDocument();
    });
  });
});
