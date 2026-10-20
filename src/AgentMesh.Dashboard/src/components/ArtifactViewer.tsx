import React, { useState } from 'react';
import {
  FileText,
  Copy,
  Check,
  Download,
  X,
  RefreshCw,
  Code,
  Eye,
  Info,
  Layers
} from 'lucide-react';
import type { SessionArtifact } from '../types';

interface ArtifactViewerProps {
  artifacts: SessionArtifact[];
  selectedArtifactId: string | null;
  onSelectArtifact: (id: string) => void;
  onClose: () => void;
  onRefresh?: () => void;
}

export const ArtifactViewer: React.FC<ArtifactViewerProps> = ({
  artifacts,
  selectedArtifactId,
  onSelectArtifact,
  onClose,
  onRefresh
}) => {
  const [viewMode, setViewMode] = useState<'rendered' | 'raw'>('rendered');
  const [copied, setCopied] = useState(false);

  const activeArtifact = artifacts.find((a) => a.id === selectedArtifactId) || artifacts[0] || null;

  const handleCopyContent = async () => {
    if (!activeArtifact?.content) return;
    try {
      await navigator.clipboard.writeText(activeArtifact.content);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard fallback
    }
  };

  const getArtifactStreamUrl = (artifact: SessionArtifact, download = false) => {
    const base = artifact.downloadUrl || `/api/sessions/${artifact.sessionId}/artifacts/${encodeURIComponent(artifact.id)}/content`;
    return download ? `${base}${base.includes('?') ? '&' : '?'}download=true` : base;
  };

  const isImageArtifact = (artifact?: SessionArtifact | null) => {
    if (!artifact) return false;
    const ct = (artifact.contentType || '').toLowerCase();
    const name = (artifact.name || '').toLowerCase();
    return ct.startsWith('image/') || /\.(png|jpg|jpeg|gif|webp|svg|bmp|ico)$/.test(name);
  };

  const isPdfArtifact = (artifact?: SessionArtifact | null) => {
    if (!artifact) return false;
    const ct = (artifact.contentType || '').toLowerCase();
    const name = (artifact.name || '').toLowerCase();
    return ct === 'application/pdf' || name.endsWith('.pdf');
  };

  const handleDownloadFile = () => {
    if (!activeArtifact) return;
    if ((isImageArtifact(activeArtifact) || isPdfArtifact(activeArtifact)) && !activeArtifact.content) {
      const a = document.createElement('a');
      a.href = getArtifactStreamUrl(activeArtifact, true);
      a.download = activeArtifact.name;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      return;
    }
    const content = activeArtifact.content || '';
    const blob = new Blob([content], { type: activeArtifact.contentType || 'text/markdown' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = activeArtifact.name;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  };

  const formatFileSize = (bytes?: number) => {
    if (!bytes || bytes <= 0) return '0 B';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const renderMarkdownContent = (content: string) => {
    // Split into blocks (code blocks, mermaid diagrams, alerts, paragraphs)
    const blocks: React.ReactNode[] = [];
    const lines = content.split('\n');
    let inCode = false;
    let codeLang = '';
    let codeBuffer: string[] = [];
    let alertType: string | null = null;
    let alertBuffer: string[] = [];

    const flushCode = (key: string) => {
      const codeText = codeBuffer.join('\n');
      if (codeLang.toLowerCase() === 'mermaid') {
        blocks.push(
          <div key={key} className="my-3 rounded-xl border border-indigo-500/30 bg-slate-900/90 p-3.5 shadow-lg">
            <div className="flex items-center justify-between mb-2 pb-1.5 border-b border-indigo-500/20 text-[11px] font-mono text-indigo-300">
              <span className="flex items-center gap-1.5 font-bold uppercase tracking-wider">
                <Layers className="w-3.5 h-3.5 text-indigo-400" />
                <span>Mermaid Architecture Diagram</span>
              </span>
            </div>
            <pre className="font-mono text-xs text-indigo-200 overflow-x-auto whitespace-pre p-2 bg-slate-950/70 rounded-lg">
              {codeText}
            </pre>
          </div>
        );
      } else {
        blocks.push(
          <div key={key} className="my-3 rounded-xl border border-slate-800 bg-slate-950/90 overflow-hidden shadow-sm">
            {codeLang && (
              <div className="px-3 py-1.5 bg-slate-900 border-b border-slate-800 text-[10px] font-mono text-slate-400 uppercase font-semibold">
                {codeLang}
              </div>
            )}
            <pre className="p-3 font-mono text-xs text-slate-200 overflow-x-auto whitespace-pre">
              {codeText}
            </pre>
          </div>
        );
      }
      codeBuffer = [];
      codeLang = '';
      inCode = false;
    };

    const flushAlert = (key: string) => {
      const alertText = alertBuffer.join('\n');
      const isWarn = alertType === 'WARNING' || alertType === 'CAUTION';
      blocks.push(
        <div
          key={key}
          className={`my-3 p-3 rounded-xl border text-xs flex items-start gap-2.5 ${
            isWarn
              ? 'bg-amber-950/40 border-amber-600/40 text-amber-200'
              : 'bg-sky-950/40 border-sky-600/40 text-sky-200'
          }`}
        >
          <Info className="w-4 h-4 shrink-0 mt-0.5 text-current" />
          <div className="whitespace-pre-wrap">{alertText}</div>
        </div>
      );
      alertBuffer = [];
      alertType = null;
    };

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];

      if (line.startsWith('```')) {
        if (!inCode) {
          inCode = true;
          codeLang = line.replace('```', '').trim();
          codeBuffer = [];
        } else {
          flushCode(`code-${i}`);
        }
        continue;
      }

      if (inCode) {
        codeBuffer.push(line);
        continue;
      }

      const alertMatch = line.match(/^>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]/i);
      if (alertMatch) {
        if (alertType) flushAlert(`alert-${i}`);
        alertType = alertMatch[1].toUpperCase();
        continue;
      }

      if (alertType) {
        if (line.startsWith('>')) {
          alertBuffer.push(line.replace(/^>\s?/, ''));
          continue;
        } else {
          flushAlert(`alert-${i}`);
        }
      }

      // Headings
      if (line.startsWith('# ')) {
        blocks.push(
          <h1 key={`h1-${i}`} className="text-lg font-bold text-white mt-4 mb-2 pb-1 border-b border-slate-800">
            {line.replace('# ', '')}
          </h1>
        );
      } else if (line.startsWith('## ')) {
        blocks.push(
          <h2 key={`h2-${i}`} className="text-base font-semibold text-slate-100 mt-3 mb-1.5 pb-0.5 border-b border-slate-800/60">
            {line.replace('## ', '')}
          </h2>
        );
      } else if (line.startsWith('### ')) {
        blocks.push(
          <h3 key={`h3-${i}`} className="text-sm font-semibold text-sky-300 mt-2.5 mb-1">
            {line.replace('### ', '')}
          </h3>
        );
      } else if (line.trim().startsWith('- ') || line.trim().startsWith('* ')) {
        blocks.push(
          <li key={`li-${i}`} className="ml-4 list-disc text-xs text-slate-300 my-0.5 leading-relaxed">
            {line.trim().replace(/^[-*]\s+/, '')}
          </li>
        );
      } else if (line.trim().length === 0) {
        // empty spacing
      } else {
        blocks.push(
          <p key={`p-${i}`} className="text-xs text-slate-300 my-1 leading-relaxed whitespace-pre-wrap">
            {line}
          </p>
        );
      }
    }

    if (inCode) flushCode('code-end');
    if (alertType) flushAlert('alert-end');

    return blocks;
  };

  return (
    <div className="h-full flex flex-col min-h-0 bg-slate-950 border-l border-slate-800 select-text" data-testid="artifact-viewer">
      {/* Top Header */}
      <div className="p-3 border-b border-slate-800 flex items-center justify-between shrink-0 bg-slate-900/50">
        <div className="flex items-center gap-2 min-w-0">
          <FileText className="w-4 h-4 text-indigo-400 shrink-0" />
          <span className="font-semibold text-xs text-white">Artifacts</span>
          <span className="px-1.5 py-0.2 rounded-full text-[10px] font-bold bg-indigo-500/20 text-indigo-300 border border-indigo-500/30">
            {artifacts.length}
          </span>
        </div>

        <div className="flex items-center gap-1.5">
          {onRefresh && (
            <button
              type="button"
              onClick={onRefresh}
              title="Refresh artifacts list"
              className="p-1 rounded-md text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
            >
              <RefreshCw className="w-3.5 h-3.5" />
            </button>
          )}
          <button
            type="button"
            onClick={onClose}
            title="Close artifacts panel"
            className="p-1 rounded-md text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
          >
            <X className="w-4 h-4" />
          </button>
        </div>
      </div>

      {artifacts.length === 0 ? (
        <div className="flex-1 flex flex-col items-center justify-center p-6 text-center text-slate-500 space-y-2.5">
          <FileText className="w-10 h-10 text-slate-700" />
          <div className="text-xs font-semibold text-slate-400">No artifacts generated yet</div>
          <p className="text-[11px] text-slate-500 max-w-xs leading-relaxed">
            When this agent creates implementation plans, architecture diagrams, or files in <code className="text-amber-400 font-mono">plan</code> mode, they will appear here automatically.
          </p>
        </div>
      ) : (
        <>
          {/* Artifact Selector Tabs (if multiple) */}
          {artifacts.length > 1 && (
            <div className="px-3 py-2 border-b border-slate-800 flex items-center gap-1.5 overflow-x-auto shrink-0 bg-slate-900/30">
              {artifacts.map((a) => {
                const isSelected = a.id === activeArtifact?.id;
                return (
                  <button
                    key={a.id}
                    type="button"
                    onClick={() => onSelectArtifact(a.id)}
                    className={`px-2.5 py-1 rounded-lg text-xs font-mono font-medium shrink-0 flex items-center gap-1.5 transition cursor-pointer border ${
                      isSelected
                        ? 'bg-indigo-600/30 text-indigo-200 border-indigo-500/50 shadow-sm'
                        : 'bg-slate-900 text-slate-400 hover:text-slate-200 hover:bg-slate-800 border-slate-800'
                    }`}
                    title={a.name}
                  >
                    <FileText className="w-3 h-3 text-indigo-400" />
                    <span className="truncate max-w-[140px]">{a.name}</span>
                  </button>
                );
              })}
            </div>
          )}

          {/* Active Artifact Meta & Actions Bar */}
          {activeArtifact && (
            <div className="px-4 py-2.5 border-b border-slate-800/80 bg-slate-900/40 flex items-center justify-between gap-3 shrink-0">
              <div className="min-w-0">
                <div className="font-mono font-semibold text-xs text-white truncate flex items-center gap-1.5">
                  <span>{activeArtifact.name}</span>
                  <span className="text-[10px] text-slate-500 font-normal">
                    ({formatFileSize(activeArtifact.sizeBytes)})
                  </span>
                </div>
                {activeArtifact.summary && (
                  <div className="text-[11px] text-indigo-300 truncate mt-0.5">
                    {activeArtifact.summary}
                  </div>
                )}
              </div>

              <div className="flex items-center gap-1 shrink-0">
                {/* View Mode Toggle */}
                <div className="flex items-center rounded-lg bg-slate-900 border border-slate-800 p-0.5 text-[11px]">
                  <button
                    type="button"
                    onClick={() => setViewMode('rendered')}
                    className={`px-2 py-0.5 rounded flex items-center gap-1 cursor-pointer transition ${
                      viewMode === 'rendered'
                        ? 'bg-indigo-600 text-white font-semibold'
                        : 'text-slate-400 hover:text-white'
                    }`}
                  >
                    <Eye className="w-3 h-3" />
                    <span>View</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => setViewMode('raw')}
                    className={`px-2 py-0.5 rounded flex items-center gap-1 cursor-pointer transition ${
                      viewMode === 'raw'
                        ? 'bg-indigo-600 text-white font-semibold'
                        : 'text-slate-400 hover:text-white'
                    }`}
                  >
                    <Code className="w-3 h-3" />
                    <span>Raw</span>
                  </button>
                </div>

                {/* Copy Button */}
                <button
                  type="button"
                  onClick={handleCopyContent}
                  title="Copy artifact content"
                  className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                >
                  {copied ? <Check className="w-3.5 h-3.5 text-emerald-400" /> : <Copy className="w-3.5 h-3.5" />}
                </button>

                {/* Download Button */}
                <button
                  type="button"
                  onClick={handleDownloadFile}
                  title="Download artifact file"
                  className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
                >
                  <Download className="w-3.5 h-3.5" />
                </button>
              </div>
            </div>
          )}

          {/* Artifact Content Area */}
          <div className="flex-1 overflow-y-auto p-4 min-h-0">
            {activeArtifact ? (
              isImageArtifact(activeArtifact) && viewMode === 'rendered' ? (
                <div className="flex flex-col items-center justify-center p-4 bg-slate-900/60 rounded-xl border border-slate-800/80">
                  <img
                    src={getArtifactStreamUrl(activeArtifact)}
                    alt={activeArtifact.name}
                    className="max-w-full max-h-[70vh] rounded-lg object-contain shadow-lg"
                    data-testid="artifact-image-preview"
                  />
                </div>
              ) : isPdfArtifact(activeArtifact) && viewMode === 'rendered' ? (
                <div className="w-full h-[70vh] bg-slate-900/60 rounded-xl border border-slate-800/80 overflow-hidden">
                  <iframe
                    src={getArtifactStreamUrl(activeArtifact)}
                    title={activeArtifact.name}
                    className="w-full h-full border-0"
                    data-testid="artifact-pdf-preview"
                  />
                </div>
              ) : viewMode === 'raw' ? (
                <pre className="font-mono text-xs text-slate-200 bg-slate-900/60 p-4 rounded-xl border border-slate-800/80 overflow-x-auto whitespace-pre-wrap leading-relaxed">
                  {activeArtifact.content || '(binary artifact — switch to View or Download)'}
                </pre>
              ) : (
                <div className="prose prose-invert max-w-none text-xs">
                  {renderMarkdownContent(activeArtifact.content || '')}
                </div>
              )
            ) : null}
          </div>
        </>
      )}
    </div>
  );
};
