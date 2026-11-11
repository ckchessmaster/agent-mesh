import { useCallback, useEffect, useState } from 'react';
import { api } from '../services/api';
import type {
  JobCardComment,
  JobTimelineCard,
  AddCommentRequest,
  DiscoveryAnswersPayload,
  PlanDecisionPayload,
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

  useEffect(() => {
    void fetchCards();
  }, [fetchCards]);

  useEffect(() => {
    if (!connection || !connected || !jobId) return;

    connection.invoke('JoinJob', jobId).catch(() => {});

    const handleCardUpdated = (updatedCard: JobTimelineCard) => {
      if (!updatedCard) return;
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
    abortActiveTurn,
    submitIntervention
  };
}
