import React from 'react';
import { JOB_PANEL_TABS, type JobPanelTab } from '../types';

/**
 * The tab strip shared by the job panel's two tabs (Artifacts and Changes).
 *
 * Why it lives inside the panels rather than only in the Cockpit header: on a phone the panel renders
 * as a `fixed inset-0` drawer that covers the header entirely, so header-only toggles would leave the
 * operator stuck in whichever tab they opened, with the other one unreachable until they close the
 * panel. Rendering the strip in the panel's own header keeps the switch possible wherever the panel is,
 * and keeps one component owning the label order so the header and the drawer cannot drift apart.
 *
 * `aria-selected` plus the `tab` role rather than a pair of buttons: the two panels swap in the same
 * column and only one is on screen at a time, which is what a tablist describes.
 */
interface PanelTabStripProps {
  active: JobPanelTab;
  onSelect: (tab: JobPanelTab) => void;
  /** Per-tab badge, e.g. the artifact count. Omitted for a tab whose number is not already in hand. */
  counts?: Partial<Record<JobPanelTab, number>>;
}

export const PanelTabStrip: React.FC<PanelTabStripProps> = ({ active, onSelect, counts }) => (
  <div
    className="flex items-center gap-1 rounded-lg bg-slate-900 border border-slate-800 p-0.5 shrink-0"
    role="tablist"
    aria-label="Job side panel tabs"
    data-testid="panel-tab-strip"
  >
    {JOB_PANEL_TABS.map(({ id, label }) => {
      const isActive = id === active;
      const count = counts?.[id];

      return (
        <button
          key={id}
          type="button"
          role="tab"
          aria-selected={isActive}
          onClick={() => onSelect(id)}
          data-testid={`panel-tab-${id}`}
          className={`px-2 py-0.5 rounded text-[11px] font-semibold transition cursor-pointer whitespace-nowrap ${
            isActive
              ? 'bg-indigo-600 text-white'
              : 'text-slate-400 hover:text-white'
          }`}
        >
          <span>{label}</span>
          {typeof count === 'number' && (
            <span className={isActive ? 'text-white/80' : 'text-slate-500'}> ({count})</span>
          )}
        </button>
      );
    })}
  </div>
);
