import React, { useState } from 'react';
import type { JobCardComment } from '../../../types/timeline';
import { MessageSquare, Send, ChevronDown, ChevronRight, User, Bot, ShieldAlert } from 'lucide-react';

export interface CardCommentsProps {
  cardId: string;
  comments?: JobCardComment[];
  onAddComment?: (cardId: string, content: string) => Promise<void> | void;
}

export const CardComments: React.FC<CardCommentsProps> = ({
  cardId,
  comments = [],
  onAddComment
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const [newComment, setNewComment] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newComment.trim() || submitting || !onAddComment) return;

    try {
      setSubmitting(true);
      await onAddComment(cardId, newComment.trim());
      setNewComment('');
    } catch (err) {
      console.error('Failed to post comment:', err);
    } finally {
      setSubmitting(false);
    }
  };

  const getSenderBadge = (type: string) => {
    switch (type.toLowerCase()) {
      case 'operator':
        return (
          <span className="inline-flex items-center space-x-1 px-1.5 py-0.5 rounded text-[10px] bg-sky-500/10 text-sky-400 border border-sky-500/20">
            <User className="w-3 h-3" />
            <span>Operator</span>
          </span>
        );
      case 'agent':
        return (
          <span className="inline-flex items-center space-x-1 px-1.5 py-0.5 rounded text-[10px] bg-purple-500/10 text-purple-400 border border-purple-500/20">
            <Bot className="w-3 h-3" />
            <span>Agent</span>
          </span>
        );
      default:
        return (
          <span className="inline-flex items-center space-x-1 px-1.5 py-0.5 rounded text-[10px] bg-slate-700/50 text-slate-400 border border-slate-600/30">
            <ShieldAlert className="w-3 h-3" />
            <span>System</span>
          </span>
        );
    }
  };

  return (
    <div className="mt-3 pt-3 border-t border-slate-800/80 text-xs">
      <button
        type="button"
        onClick={() => setIsOpen(!isOpen)}
        className="flex items-center space-x-1.5 text-slate-400 hover:text-slate-200 transition font-medium"
      >
        {isOpen ? <ChevronDown className="w-3.5 h-3.5" /> : <ChevronRight className="w-3.5 h-3.5" />}
        <MessageSquare className="w-3.5 h-3.5 text-slate-400" />
        <span>Comments ({comments.length})</span>
      </button>

      {isOpen && (
        <div className="mt-3 space-y-3 pl-2 border-l border-slate-800">
          {comments.length > 0 && (
            <div className="space-y-2 max-h-56 overflow-y-auto pr-1">
              {comments.map((comment) => (
                <div
                  key={comment.id}
                  className="p-2 rounded bg-slate-900/60 border border-slate-800 text-slate-300"
                >
                  <div className="flex items-center justify-between mb-1">
                    <div className="flex items-center space-x-1.5">
                      {getSenderBadge(comment.senderType)}
                      <span className="font-mono text-[11px] text-slate-400">{comment.senderId}</span>
                    </div>
                    <span className="text-[10px] text-slate-500">
                      {new Date(comment.createdAt).toLocaleTimeString([], {
                        hour: '2-digit',
                        minute: '2-digit'
                      })}
                    </span>
                  </div>
                  <p className="text-xs whitespace-pre-wrap">{comment.content}</p>
                </div>
              ))}
            </div>
          )}

          {onAddComment && (
            <form onSubmit={handleSubmit} className="flex items-center space-x-2 pt-1">
              <input
                type="text"
                placeholder="Leave feedback or instructions on this card..."
                value={newComment}
                onChange={(e) => setNewComment(e.target.value)}
                disabled={submitting}
                className="flex-1 bg-slate-900 border border-slate-700/80 rounded px-2.5 py-1.5 text-xs text-slate-200 placeholder-slate-500 focus:outline-none focus:border-indigo-500"
              />
              <button
                type="submit"
                disabled={!newComment.trim() || submitting}
                className="px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 text-white rounded text-xs font-medium flex items-center space-x-1 disabled:opacity-40 transition"
              >
                <Send className="w-3 h-3" />
                <span>Send</span>
              </button>
            </form>
          )}
        </div>
      )}
    </div>
  );
};
