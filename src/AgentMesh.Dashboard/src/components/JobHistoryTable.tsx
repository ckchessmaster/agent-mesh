import React, { useState, useEffect } from 'react';
import { ExternalLink, CheckCircle, Clock, XCircle, PlayCircle, Database } from 'lucide-react';
import type { StagedJob } from '../types';
import { api } from '../services/api';
import * as signalR from '@microsoft/signalr';

interface JobHistoryTableProps {
  connection?: signalR.HubConnection | null;
  refreshKey?: number;
}

export const JobHistoryTable: React.FC<JobHistoryTableProps> = ({ connection, refreshKey }) => {
  const [jobs, setJobs] = useState<StagedJob[]>([]);
  const [selectedJob, setSelectedJob] = useState<StagedJob | null>(null);

  const loadJobs = async () => {
    try {
      const data = await api.getJobs();
      setJobs(data);
    } catch (err) {
      console.error('Failed to load job history', err);
    }
  };

  useEffect(() => {
    loadJobs();
  }, [refreshKey]);

  useEffect(() => {
    if (!connection) return;

    const handleUpdate = () => {
      loadJobs();
    };

    connection.on('JobStaged', handleUpdate);
    connection.on('JobApprovalUpdated', handleUpdate);

    return () => {
      connection.off('JobStaged', handleUpdate);
      connection.off('JobApprovalUpdated', handleUpdate);
    };
  }, [connection]);

  const renderApprovalBadge = (status: number | string) => {
    const s = String(status).toLowerCase();
    if (s === '0' || s === 'autoapproved' || s === 'auto_approved') {
      return (
        <span className="text-[11px] px-2 py-0.5 rounded-full bg-emerald-500/10 text-emerald-400 border border-emerald-500/30 flex items-center gap-1 w-fit">
          <CheckCircle className="w-3 h-3" /> Auto-Approved
        </span>
      );
    }
    if (s === '1' || s === 'pendinghuman' || s === 'pending_human') {
      return (
        <span className="text-[11px] px-2 py-0.5 rounded-full bg-amber-500/10 text-amber-400 border border-amber-500/30 flex items-center gap-1 w-fit">
          <Clock className="w-3 h-3" /> Pending Human
        </span>
      );
    }
    if (s === '2' || s === 'approved') {
      return (
        <span className="text-[11px] px-2 py-0.5 rounded-full bg-emerald-500/10 text-emerald-300 border border-emerald-500/30 flex items-center gap-1 w-fit">
          <CheckCircle className="w-3 h-3" /> Approved
        </span>
      );
    }
    return (
      <span className="text-[11px] px-2 py-0.5 rounded-full bg-red-500/10 text-red-400 border border-red-500/30 flex items-center gap-1 w-fit">
        <XCircle className="w-3 h-3" /> Rejected
      </span>
    );
  };

  const renderExecutionBadge = (status: number | string) => {
    const s = String(status).toLowerCase();
    if (s === '0' || s === 'staged') {
      return <span className="text-slate-400 text-xs font-mono">Staged</span>;
    }
    if (s === '1' || s === 'running') {
      return (
        <span className="text-sky-400 text-xs font-mono flex items-center gap-1">
          <PlayCircle className="w-3 h-3 animate-spin" /> Running
        </span>
      );
    }
    if (s === '2' || s === 'completed') {
      return <span className="text-emerald-400 text-xs font-mono font-semibold">Completed</span>;
    }
    return <span className="text-red-400 text-xs font-mono">Failed</span>;
  };

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 text-white shadow-xl">
      <div className="flex items-center justify-between mb-4">
        <div className="flex items-center gap-2">
          <Database className="w-5 h-5 text-sky-400" />
          <h2 className="text-xl font-bold">Job Execution Ledger</h2>
        </div>
        <span className="text-xs text-slate-400 font-mono">Total Recorded: {jobs.length}</span>
      </div>

      <div className="overflow-x-auto">
        <table className="w-full text-left border-collapse text-xs">
          <thead>
            <tr className="border-b border-slate-800 text-slate-400">
              <th className="py-2.5 px-3">Job ID</th>
              <th className="py-2.5 px-3">Target Subsystem</th>
              <th className="py-2.5 px-3">Agent</th>
              <th className="py-2.5 px-3">Approval</th>
              <th className="py-2.5 px-3">Execution</th>
              <th className="py-2.5 px-3">Temporal Workflow</th>
              <th className="py-2.5 px-3 text-right">Actions</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-800/60 font-mono">
            {jobs.length === 0 ? (
              <tr>
                <td colSpan={7} className="py-8 text-center text-slate-500 font-sans">
                  No jobs logged in the central ledger yet.
                </td>
              </tr>
            ) : (
              jobs.map((job) => (
                <tr key={job.id} className="hover:bg-slate-800/40 transition">
                  <td className="py-3 px-3 text-slate-300">{job.id.slice(0, 8)}</td>
                  <td className="py-3 px-3">
                    <span className="text-sky-300 font-semibold bg-sky-950/50 px-2 py-0.5 rounded border border-sky-800/40">
                      {job.targetService}:{job.actionVerb}
                    </span>
                  </td>
                  <td className="py-3 px-3 text-slate-400">{job.requestedByAgentId}</td>
                  <td className="py-3 px-3 font-sans">{renderApprovalBadge(job.approvalStatus)}</td>
                  <td className="py-3 px-3">{renderExecutionBadge(job.executionStatus)}</td>
                  <td className="py-3 px-3 text-slate-400">
                    {job.temporalWorkflowId ? (
                      <a
                        href={`http://localhost:8233/namespaces/default/workflows/${job.temporalWorkflowId}`}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center gap-1 text-sky-400 hover:text-sky-300 underline"
                      >
                        {job.temporalWorkflowId.slice(0, 16)}...
                        <ExternalLink className="w-3 h-3" />
                      </a>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className="py-3 px-3 text-right font-sans">
                    <button
                      onClick={() => setSelectedJob(job)}
                      className="px-2.5 py-1 text-[11px] rounded bg-slate-800 hover:bg-slate-700 text-slate-300 transition"
                    >
                      Inspect
                    </button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {selectedJob && (
        <div className="fixed inset-0 bg-black/70 backdrop-blur-sm flex items-center justify-center p-4 z-50">
          <div className="bg-slate-900 border border-slate-700 rounded-xl max-w-2xl w-full p-6 text-white shadow-2xl max-h-[85vh] overflow-y-auto">
            <div className="flex justify-between items-center mb-4 pb-2 border-b border-slate-800">
              <h3 className="text-base font-bold text-sky-400">
                Job Inspection: {selectedJob.id}
              </h3>
              <button
                onClick={() => setSelectedJob(null)}
                className="text-slate-400 hover:text-white text-sm"
              >
                ✕ Close
              </button>
            </div>

            <div className="space-y-4 text-xs font-mono">
              <div>
                <span className="text-slate-400 uppercase text-[10px] block mb-1">Target Service & Action</span>
                <p className="text-sky-300 bg-slate-950 p-2 rounded">{selectedJob.targetService}:{selectedJob.actionVerb}</p>
              </div>

              <div>
                <span className="text-slate-400 uppercase text-[10px] block mb-1">Payload JSON</span>
                <pre className="bg-slate-950 p-3 rounded text-slate-200 overflow-x-auto border border-slate-800">
                  {JSON.stringify(JSON.parse(selectedJob.payloadJson || '{}'), null, 2)}
                </pre>
              </div>

              {selectedJob.resultJson && (
                <div>
                  <span className="text-slate-400 uppercase text-[10px] block mb-1">Execution Result</span>
                  <pre className="bg-slate-950 p-3 rounded text-emerald-300 overflow-x-auto border border-slate-800">
                    {JSON.stringify(JSON.parse(selectedJob.resultJson), null, 2)}
                  </pre>
                </div>
              )}

              {selectedJob.errorMessage && (
                <div>
                  <span className="text-red-400 uppercase text-[10px] block mb-1">Error Trace</span>
                  <p className="text-red-300 bg-red-950/30 border border-red-800 p-2 rounded">
                    {selectedJob.errorMessage}
                  </p>
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
