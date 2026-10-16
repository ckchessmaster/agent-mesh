import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import App from '../App';
import { api } from '../services/api';
import type { SystemStatusResponse } from '../types';

vi.mock('../services/api', () => ({
  api: {
    getStatus: vi.fn(),
  },
}));

describe('App - Health Cockpit', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders loading or error state and service card titles', async () => {
    const mockStatus: SystemStatusResponse = {
      status: 'Healthy',
      timestamp: new Date().toISOString(),
      environment: 'Development',
      services: {
        api: { status: 'Healthy', latencyMs: 2, description: 'ASP.NET Core 10 Web API' },
        postgres: { status: 'Healthy', latencyMs: 4, description: 'PostgreSQL 18 Database' },
        redis: { status: 'Healthy', latencyMs: 1, description: 'Redis 8.6 Cache' },
        temporal: { status: 'Healthy', latencyMs: 3, description: 'Temporal Dev Server' },
        s3: { status: 'Healthy', latencyMs: 5, description: 'SeaweedFS S3 Storage' },
      },
    };

    vi.mocked(api.getStatus).mockResolvedValueOnce(mockStatus);

    render(<App />);

    expect(screen.getByText('AgentMesh')).toBeInTheDocument();
    expect(screen.getByText('Aspire Skeleton v1.0')).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText('ASP.NET Core 10 Web API')).toBeInTheDocument();
      expect(screen.getByText('PostgreSQL Relational DB')).toBeInTheDocument();
      expect(screen.getByText('Redis Distributed Cache')).toBeInTheDocument();
      expect(screen.getByText('Temporal Dev Server')).toBeInTheDocument();
      expect(screen.getByText('SeaweedFS S3 Storage')).toBeInTheDocument();
    });
  });

  it('displays API error banner when getStatus fails', async () => {
    vi.mocked(api.getStatus).mockRejectedValueOnce(new Error('Network connection refused'));

    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('API Gateway Unreachable')).toBeInTheDocument();
      expect(screen.getByText('Network connection refused')).toBeInTheDocument();
    });
  });
});
