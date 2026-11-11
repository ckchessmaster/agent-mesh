import React, { useState } from 'react';
import type { JobTimelineCard, DiscoveryPayload } from '../../../types/timeline';
import { CardComments } from './CardComments';
import { HelpCircle, CheckCircle, Send, Sparkles } from 'lucide-react';

export interface DiscoveryQaCardProps {
  card: JobTimelineCard;
  onSubmitAnswers?: (cardId: string, answers: Record<string, string>, feedback?: string) => Promise<void> | void;
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

export const DiscoveryQaCard: React.FC<DiscoveryQaCardProps> = ({
  card,
  onSubmitAnswers,
  onAddComment
}) => {
  let payload: DiscoveryPayload = { questions: [], summary: '' };
  try {
    payload = JSON.parse(card.structuredPayloadJson || '{}');
  } catch {
    // fallback
  }

  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [generalFeedback, setGeneralFeedback] = useState<string>('');
  const [submitting, setSubmitting] = useState(false);
  const [isSubmitted, setIsSubmitted] = useState(card.status === 'Approved' || card.status === 'Resolved');

  const handleSelectOption = (questionId: string, option: string) => {
    if (isSubmitted) return;
    setAnswers((prev) => ({ ...prev, [questionId]: option }));
  };

  const handleTextChange = (questionId: string, text: string) => {
    if (isSubmitted) return;
    setAnswers((prev) => ({ ...prev, [questionId]: text }));
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (submitting || isSubmitted || !onSubmitAnswers) return;

    try {
      setSubmitting(true);
      await onSubmitAnswers(card.id, answers, generalFeedback);
      setIsSubmitted(true);
    } catch (err) {
      console.error('Failed to submit discovery answers:', err);
    } finally {
      setSubmitting(false);
    }
  };

  const isPending = card.status === 'PendingInput' && !isSubmitted;

  return (
    <div
      data-testid="discovery-qa-card"
      className="p-4 rounded-lg bg-slate-900/90 border border-indigo-500/30 shadow-md text-slate-200 mb-4 transition"
    >
      {/* Header */}
      <div className="flex items-center justify-between pb-3 border-b border-slate-800">
        <div className="flex items-center space-x-2">
          <div className="p-1.5 rounded bg-indigo-500/20 text-indigo-400">
            <HelpCircle className="w-4 h-4" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-slate-100 flex items-center space-x-2">
              <span>{card.title || 'Discovery Q&A'}</span>
              <Sparkles className="w-3.5 h-3.5 text-indigo-400" />
            </h3>
            <p className="text-xs text-slate-400">{payload.summary || 'Clarification required before decomposition'}</p>
          </div>
        </div>
        <span
          className={`px-2 py-0.5 text-xs font-mono rounded ${
            isPending
              ? 'bg-amber-500/10 text-amber-400 border border-amber-500/30 animate-pulse'
              : 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/30'
          }`}
        >
          {isPending ? 'Pending Input' : 'Completed'}
        </span>
      </div>

      {/* Questions Form */}
      <form onSubmit={handleSubmit} className="mt-4 space-y-4">
        {payload.questions?.map((q, idx) => {
          const selectedVal = answers[q.id] || '';
          return (
            <div key={q.id || idx} className="space-y-1.5 p-3 rounded bg-slate-950/40 border border-slate-800/80">
              <label className="block text-xs font-medium text-slate-300">
                <span className="text-indigo-400 font-mono mr-1.5">Q{idx + 1}:</span>
                {q.question}
              </label>

              {/* Option Pills */}
              {q.options && q.options.length > 0 && (
                <div className="flex flex-wrap gap-2 pt-1">
                  {q.options.map((opt) => {
                    const isSelected = selectedVal === opt;
                    return (
                      <button
                        type="button"
                        key={opt}
                        disabled={!isPending}
                        onClick={() => handleSelectOption(q.id, opt)}
                        className={`px-2.5 py-1 rounded-full text-xs transition border ${
                          isSelected
                            ? 'bg-indigo-600 text-white border-indigo-500 font-medium shadow-sm'
                            : 'bg-slate-800 text-slate-300 border-slate-700 hover:bg-slate-700/80 disabled:opacity-50'
                        }`}
                      >
                        {opt}
                      </button>
                    );
                  })}
                </div>
              )}

              {/* Custom Response Textbox */}
              {isPending ? (
                <input
                  type="text"
                  placeholder="Or enter custom instruction..."
                  value={selectedVal}
                  onChange={(e) => handleTextChange(q.id, e.target.value)}
                  className="w-full mt-1.5 bg-slate-900 border border-slate-700/80 rounded px-2.5 py-1 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-indigo-500"
                />
              ) : (
                <div className="mt-1 text-xs text-slate-400 font-mono">
                  Answer: <span className="text-indigo-300">{selectedVal || '(No response provided)'}</span>
                </div>
              )}
            </div>
          );
        })}

        {/* General Feedback / Guidance */}
        {isPending && (
          <div className="space-y-1">
            <label className="block text-xs font-medium text-slate-400">
              Additional Requirements or Constraints (Optional)
            </label>
            <textarea
              rows={2}
              placeholder="e.g. Do not touch legacy tables; target Postgres 15+ syntax."
              value={generalFeedback}
              onChange={(e) => setGeneralFeedback(e.target.value)}
              className="w-full bg-slate-900 border border-slate-700/80 rounded px-2.5 py-1.5 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-indigo-500"
            />
          </div>
        )}

        {/* Action Button */}
        {isPending && (
          <div className="flex justify-end pt-2">
            <button
              type="submit"
              disabled={submitting || !onSubmitAnswers}
              className="px-4 py-2 bg-indigo-600 hover:bg-indigo-500 text-white rounded text-xs font-semibold flex items-center space-x-1.5 shadow transition disabled:opacity-50"
            >
              <Send className="w-3.5 h-3.5" />
              <span>{submitting ? 'Submitting Answers...' : 'Submit Answers & Continue'}</span>
            </button>
          </div>
        )}

        {!isPending && (
          <div className="p-2 rounded bg-emerald-950/20 border border-emerald-500/20 flex items-center space-x-2 text-xs text-emerald-400">
            <CheckCircle className="w-4 h-4 flex-shrink-0" />
            <span>Answers submitted. Task decomposition unlocked.</span>
          </div>
        )}
      </form>

      {/* Card Comments */}
      <CardComments cardId={card.id} comments={card.comments} onAddComment={onAddComment} />
    </div>
  );
};
