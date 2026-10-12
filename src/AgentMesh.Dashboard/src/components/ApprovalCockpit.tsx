import React, { useState, useEffect } from 'react';
import { CheckCircle2, XCircle, AlertTriangle, RefreshCw, Clock } from 'lucide-react';
import type { StagedJob } from '../types';
import { api } from '../services/api';
import * as signalR from '@microsoft/signalr';

interface ApprovalCockpitProps {
  connection?: signalR.HubConnection | null;
  onJobStatusChange?: () => void;
}

export const ApprovalCockpit: React.FC<ApprovalCockpitProps> = ({ connection, onJobStatusChange }) => {
  const [pendingJobs, setPendingJobs] = useState<StagedJob[]>([]);
  const [loadingJobId, setLoadingJobId] = useState<string | null>(null);
  const [isRefreshing, setIsRefreshing] = useState(false);

  const fetchPendingJobs = async () => {
    try {
      setIsRefreshing(true);
      const data = await api.getJobs('PendingHuman');
      setPendingJobs(data);
    } catch (e) {
      console.error('Failed to load pending jobs', e);
    } finally {
      setIsRefreshing(false);
    }
  };

  useEffect(() => {
    fetchPendingJobs();
    const interval = setInterval(fetchPendingJobs, 5000);
    return () => clearInterval(interval);
  }, []);

  useEffect(() => {
    if (!connection) return;

    const handleJobStaged = () => {
      fetchPendingJobs();
      onJobStatusChange?.();
    };

    const handleApprovalUpdated = (data: { jobId: string }) => {
      setPendingJobs((prev) => prev.filter((j) => j.id !== data.jobId));
      onJobStatusChange?.();
    };

    connection.on('JobStaged', handleJobStaged);
    connection.on('JobApprovalUpdated', handleApprovalUpdated);

    return () => {
      connection.off('JobStaged', handleJobStaged);
      connection.off('JobApprovalUpdated', handleApprovalUpdated);
    };
  }, [connection, onJobStatusChange]);

  const handleDecision = async (jobId: string, approved: boolean) => {
    setLoadingJobId(jobId);
    try {
      await api.submitApproval(jobId, approved, approved ? 'Approved via Cockpit UI' : 'Rejected via Cockpit UI');
      setPendingJobs((prev) => prev.filter((j) => j.id !== jobId));
      onJobStatusChange?.();
    } catch (err) {
      console.error('Failed to submit approval decision:', err);
    } finally {
      setLoadingJobId(null);
    }
  };

  const formatPayload = (raw: string) => {
    try {
      return JSON.stringify(JSON.parse(raw), null, 2);
    } catch {
      return raw;
    }
  };

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 text-white shadow-xl">
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-2">
          <AlertTriangle className="text-amber-400 w-6 h-6 animate-pulse" />
          <h2 className="text-xl font-bold">Human Approval Cockpit</h2>
          <span className="bg-amber-500/20 text-amber-300 border border-amber-500/30 px-3 py-0.5 rounded-full text-xs font-semibold">
            {pendingJobs.length} Pending
          </span>
        </div>
        <button
          onClick={fetchPendingJobs}
          className="text-slate-400 hover:text-white transition p-1.5 rounded-lg hover:bg-slate-800"
          title="Refresh pending jobs"
        >
          <RefreshCw className={`w-4 h-4 ${isRefreshing ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {pendingJobs.length === 0 ? (
        <div className="text-center py-8 border border-dashed border-slate-800 rounded-lg">
          <CheckCircle2 className="w-10 h-10 text-emerald-400/60 mx-auto mb-2" />
          <p className="text-slate-300 text-sm font-medium">No actions currently require human review.</p>
          <p className="text-slate-500 text-xs mt-1">All agents operating within autonomous policy bounds.</p>
        </div>
      ) : (
        <div className="space-y-4">
          {pendingJobs.map((job) => {
            const isLoading = loadingJobId === job.id;
            return (
              <div key={job.id} className="bg-slate-800/80 border border-slate-700/80 p-5 rounded-lg shadow-md transition hover:border-slate-600">
                <div className="flex justify-between items-start mb-3">
                  <div>
                    <div className="flex items-center gap-2">
                      <span className="font-mono text-sm text-sky-400 font-bold bg-sky-950/60 px-2 py-0.5 rounded border border-sky-800/50">
                        {job.targetService}:{job.actionVerb}
                      </span>
                      <span className="text-xs text-amber-400 bg-amber-950/40 border border-amber-800/40 px-2 py-0.5 rounded flex items-center gap-1">
                        <Clock className="w-3 h-3" /> HITL Escalation
                      </span>
                    </div>
                    <div className="text-xs text-slate-400 mt-1">
                      Requester: <span className="text-slate-200 font-mono">{job.requestedByAgentId}</span>
                      <span className="mx-2 text-slate-600">•</span>
                      Session: <span className="text-slate-400 font-mono">{job.sessionId.slice(0, 8)}</span>
                      <span className="mx-2 text-slate-600">•</span>
                      Queued: <span className="text-slate-400">{new Date(job.createdAt).toLocaleTimeString()}</span>
                    </div>
                  </div>
                  <span className="text-xs font-mono text-slate-500 bg-slate-900 px-2 py-1 rounded">
                    {job.id.slice(0, 8)}
                  </span>
                </div>

                <div className="mb-3">
                  <div className="text-[11px] uppercase tracking-wider text-slate-400 font-semibold mb-1">
                    Intent Payload Arguments
                  </div>
                  <pre className="bg-slate-950 p-3 rounded text-xs font-mono text-slate-300 overflow-x-auto border border-slate-850 max-h-48">
                    {formatPayload(job.payloadJson)}
                  </pre>
                </div>

                <div className="flex justify-end gap-3 pt-2 border-t border-slate-700/50">
                  <button
                    disabled={isLoading}
                    onClick={() => handleDecision(job.id, false)}
                    className="flex items-center gap-1.5 px-3.5 py-1.5 bg-red-600/20 hover:bg-red-600/30 text-red-300 border border-red-500/30 rounded-lg text-sm font-medium transition disabled:opacity-50"
                  >
                    <XCircle className="w-4 h-4 text-red-400" /> Reject
                  </button>
                  <button
                    disabled={isLoading}
                    onClick={() => handleDecision(job.id, true)}
                    className="flex items-center gap-1.5 px-4 py-1.5 bg-emerald-600/25 hover:bg-emerald-600/40 text-emerald-300 border border-emerald-500/40 rounded-lg text-sm font-semibold transition disabled:opacity-50 shadow-sm"
                  >
                    <CheckCircle2 className="w-4 h-4 text-emerald-400" /> Approve & Execute
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
};
