import { useCallback, useEffect, useState } from 'react';
import { api } from '../services/api';
import type {
  JobCardComment,
  JobTimelineCard,
  AddCommentRequest,
  DiscoveryAnswersPayload,
  PlanDecisionPayload,
  PrReviewDecisionPayload,
  AbortTurnRequest,
  InterventionRequest
} from '../types/timeline';
import { useSignalR } from './useSignalR';

export function useJobTimeline(jobId: string) {
  const [cards, setCards] = useState<JobTimelineCard[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<string | null>(null);

  const { connection, connected } = useSignalR('/hubs/agent-mesh');

  const fetchCards = useCallback(async () => {
    if (!jobId) return;
    if (typeof api.getTimelineCards !== 'function') {
      setLoading(false);
      return;
    }
    try {
      setLoading(true);
      setError(null);
      const data = await api.getTimelineCards(jobId);
      setCards(data || []);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      setError(msg);
    } finally {
      setLoading(false);
    }
  }, [jobId]);

  const refreshCardsQuietly = useCallback(async () => {
    if (!jobId || typeof api.getTimelineCards !== 'function') return;
    try {
      const data = await api.getTimelineCards(jobId);
      if (Array.isArray(data)) {
        setCards(data);
      }
    } catch {
      // ignore transient background poll errors
    }
  }, [jobId]);

  useEffect(() => {
    void fetchCards();
  }, [fetchCards]);

  useEffect(() => {
    if (!jobId || import.meta.env.MODE === 'test') return;
    const interval = setInterval(() => {
      void refreshCardsQuietly();
    }, 4000);
    return () => clearInterval(interval);
  }, [jobId, refreshCardsQuietly]);

  useEffect(() => {
    if (!connection || !connected || !jobId) return;

    connection.invoke('JoinJob', jobId).catch(() => {});

    const handleCardUpdated = (
      firstArg: JobTimelineCard | string,
      secondArg?: JobTimelineCard
    ) => {
      const updatedCard =
        typeof firstArg === 'object' && firstArg !== null ? firstArg : secondArg;
      if (!updatedCard || !updatedCard.id) return;
      setCards((prev) => {
        const idx = prev.findIndex((c) => c.id === updatedCard.id);
        if (idx >= 0) {
          const next = [...prev];
          next[idx] = {
            ...updatedCard,
            comments: updatedCard.comments?.length ? updatedCard.comments : prev[idx].comments || []
          };
          return next;
        }
        return [...prev, updatedCard];
      });
    };

    const handleComment = (_jobId: string, cardId: string, comment: JobCardComment) => {
      if (!comment) return;
      setCards((prev) =>
        prev.map((c) => {
          if (c.id === cardId) {
            const comments = c.comments || [];
            if (comments.some((m) => m.id === comment.id)) return c;
            return {
              ...c,
              comments: [...comments, comment]
            };
          }
          return c;
        })
      );
    };

    connection.on('ReceiveTimelineCardUpdated', handleCardUpdated);
    connection.on('ReceiveTimelineComment', handleComment);

    return () => {
      connection.off('ReceiveTimelineCardUpdated', handleCardUpdated);
      connection.off('ReceiveTimelineComment', handleComment);
      connection.invoke('LeaveJob', jobId).catch(() => {});
    };
  }, [connection, connected, jobId]);

  const addComment = useCallback(
    async (cardId: string, content: string, senderId = 'operator', senderType = 'operator') => {
      const req: AddCommentRequest = { senderId, senderType, content };
      const comment = await api.addTimelineComment(jobId, cardId, req);
      setCards((prev) =>
        prev.map((c) => {
          if (c.id === cardId) {
            const comments = c.comments || [];
            if (!comments.some((m) => m.id === comment.id)) {
              return { ...c, comments: [...comments, comment] };
            }
          }
          return c;
        })
      );
      return comment;
    },
    [jobId]
  );

  const submitAnswers = useCallback(
    async (cardId: string, answers: Record<string, string>, feedback?: string) => {
      const payload: DiscoveryAnswersPayload = { answers, feedback };
      await api.submitTimelineAnswers(jobId, cardId, payload);
      setCards((prev) =>
        prev.map((c) => (c.id === cardId ? { ...c, status: 'Approved' } : c))
      );
    },
    [jobId]
  );

  const submitPlanDecision = useCallback(
    async (cardId: string, approved: boolean, feedback?: string, approverId = 'operator') => {
      const payload: PlanDecisionPayload = { approved, feedback, approverId };
      await api.submitTimelinePlanDecision(jobId, cardId, payload);
      setCards((prev) =>
        prev.map((c) =>
          c.id === cardId ? { ...c, status: approved ? 'Approved' : 'Rejected' } : c
        )
      );
    },
    [jobId]
  );

  const submitPrReview = useCallback(
    async (cardId: string, approved: boolean, feedback?: string, prNumber?: number) => {
      // Sign as operator (the human at the keyboard), matching submitPlanDecision's approverId.
      // approvedHeadSha is intentionally omitted: the server binds the approval to the head SHA on
      // the gate's own card, which is the durable value a stale browser render must not override.
      const payload: PrReviewDecisionPayload = {
        isApproved: approved,
        reviewComments: feedback,
        reviewerId: 'operator',
        prNumber,
      };
      await api.submitJobPrReview(jobId, payload);
      // Optimistic flip only until the workflow's own ReceiveTimelineCardUpdated arrives with the
      // real transition (Approved then Resolved on merge, Rejected into a remediation round). A throw
      // leaves the card untouched so the operator can read the error and retry.
      setCards((prev) =>
        prev.map((c) =>
          c.id === cardId ? { ...c, status: approved ? 'Approved' : 'Rejected' } : c
        )
      );
    },
    [jobId]
  );

  const abortActiveTurn = useCallback(
    async (reason = 'Turn interrupted by operator', requestedBy = 'operator') => {
      const req: AbortTurnRequest = { reason, requestedBy };
      await api.abortTimelineTurn(jobId, req);
    },
    [jobId]
  );

  const submitIntervention = useCallback(
    async (action: string, additionalContext?: string) => {
      const req: InterventionRequest = { action, additionalContext };
      await api.submitTimelineIntervention(jobId, req);
    },
    [jobId]
  );

  return {
    cards,
    loading,
    error,
    connected,
    refreshCards: fetchCards,
    addComment,
    submitAnswers,
    submitPlanDecision,
    submitPrReview,
    abortActiveTurn,
    submitIntervention
  };
}
