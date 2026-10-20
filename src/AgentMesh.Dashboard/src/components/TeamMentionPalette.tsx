import React, { useEffect, useRef } from 'react';
import { AtSign, Bot, Crown, Users, X } from 'lucide-react';

export interface MentionableMember {
  id: string;
  name: string;
  role: string;
  driverId: string;
  isLead?: boolean;
  isAssigned?: boolean;
}

export interface TeamMentionPaletteProps {
  members: MentionableMember[];
  query: string;
  isOpen: boolean;
  highlightedIndex: number;
  onSelect: (member: MentionableMember) => void;
  onClose: () => void;
  triggerRef?: React.RefObject<HTMLElement | null>;
}

export const TeamMentionPalette: React.FC<TeamMentionPaletteProps> = ({
  members,
  query,
  isOpen,
  highlightedIndex,
  onSelect,
  onClose,
  triggerRef
}) => {
  const containerRef = useRef<HTMLDivElement | null>(null);

  const cleanQuery = query.toLowerCase().trim();

  const filteredMembers = members.filter((member) => {
    if (!cleanQuery) return true;
    return (
      member.id.toLowerCase().includes(cleanQuery) ||
      member.name.toLowerCase().includes(cleanQuery) ||
      member.role.toLowerCase().includes(cleanQuery) ||
      member.driverId.toLowerCase().includes(cleanQuery)
    );
  });

  useEffect(() => {
    const handleOutsideClick = (event: MouseEvent) => {
      const target = event.target as Node;
      if (
        containerRef.current &&
        !containerRef.current.contains(target) &&
        (!triggerRef?.current || !triggerRef.current.contains(target))
      ) {
        onClose();
      }
    };

    if (isOpen) {
      document.addEventListener('mousedown', handleOutsideClick);
    }
    return () => {
      document.removeEventListener('mousedown', handleOutsideClick);
    };
  }, [isOpen, onClose, triggerRef]);

  useEffect(() => {
    if (containerRef.current && highlightedIndex >= 0) {
      const activeElement = containerRef.current.querySelector(
        `[data-index="${highlightedIndex}"]`
      );
      if (activeElement && typeof activeElement.scrollIntoView === 'function') {
        activeElement.scrollIntoView({ block: 'nearest' });
      }
    }
  }, [highlightedIndex]);

  if (!isOpen) return null;

  return (
    <div
      ref={containerRef}
      className="absolute left-0 bottom-full mb-2 w-full min-w-[320px] sm:min-w-[460px] max-w-xl max-h-80 overflow-y-auto rounded-xl bg-slate-900 border border-slate-700 shadow-2xl z-50 p-2 text-xs divide-y divide-slate-800"
      data-testid="team-mention-palette"
      role="listbox"
      aria-label="Team member mentions"
    >
      <div className="px-2 py-1 flex items-center justify-between text-[11px] font-mono text-slate-400">
        <span className="flex items-center gap-1.5 font-semibold text-slate-300">
          <Users className="w-3.5 h-3.5 text-sky-400" />
          <span>Team Members</span>
        </span>
        <div className="flex items-center gap-2">
          <span className="text-[10px] text-slate-500">
            {filteredMembers.length} member{filteredMembers.length === 1 ? '' : 's'} • ↑↓ navigate • Enter/Tab select
          </span>
          <button
            type="button"
            onClick={onClose}
            title="Close mention list (Esc)"
            aria-label="Close mention list"
            className="p-0.5 rounded text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
          >
            <X className="w-3.5 h-3.5" />
          </button>
        </div>
      </div>

      <div className="pt-1.5 space-y-1">
        {filteredMembers.length === 0 ? (
          <div className="px-3 py-4 text-center text-slate-400" data-testid="no-team-members-match">
            <AtSign className="w-5 h-5 mx-auto mb-1.5 text-slate-600" />
            <p className="text-xs font-medium">No team members match "{query}"</p>
            <p className="text-[11px] text-slate-500 mt-0.5">
              Only project team members can receive delegated job tasks.
            </p>
          </div>
        ) : (
          filteredMembers.map((member, idx) => {
            const isSelected = idx === highlightedIndex;

            return (
              <div
                key={member.id}
                data-index={idx}
                data-testid={`team-mention-option-${member.id}`}
                role="option"
                aria-selected={isSelected}
                onMouseDown={(e) => {
                  e.preventDefault();
                  onSelect(member);
                }}
                onClick={() => onSelect(member)}
                className={`px-2.5 py-2 rounded-lg cursor-pointer transition flex items-center justify-between gap-3 ${
                  isSelected
                    ? 'bg-sky-950/70 border border-sky-500/50 text-white shadow-sm'
                    : 'hover:bg-slate-800/80 text-slate-300 border border-transparent'
                }`}
              >
                <div className="flex items-center gap-2.5 min-w-0">
                  <div
                    className={`p-1.5 rounded-lg shrink-0 ${
                      isSelected
                        ? 'bg-sky-500/30 text-sky-200 border border-sky-400/50'
                        : 'bg-sky-500/10 text-sky-400 border border-sky-500/20'
                    }`}
                  >
                    <Bot className="w-4 h-4" />
                  </div>

                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="font-semibold text-xs text-white truncate">
                        {member.name}
                      </span>
                      <span className="font-mono text-[11px] text-sky-400">
                        @{member.id}
                      </span>
                      {member.isLead && (
                        <span className="inline-flex items-center gap-1 px-1.5 py-0.2 rounded text-[10px] font-medium bg-amber-500/10 text-amber-300 border border-amber-500/20">
                          <Crown className="w-2.5 h-2.5" />
                          <span>Lead</span>
                        </span>
                      )}
                    </div>
                    <div className="text-[11px] text-slate-400 truncate mt-0.5">
                      {member.role}
                    </div>
                  </div>
                </div>

                <div className="shrink-0 flex items-center gap-1.5">
                  <span className="px-1.5 py-0.5 rounded text-[10px] font-mono font-medium bg-slate-800 text-slate-400 border border-slate-700">
                    {member.driverId}
                  </span>
                </div>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
};
