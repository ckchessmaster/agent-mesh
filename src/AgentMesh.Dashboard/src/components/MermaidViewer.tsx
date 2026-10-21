import React, { useEffect, useState } from 'react';
import mermaid from 'mermaid';
import { Layers, Copy, Check, Eye, Code2, AlertTriangle, RefreshCw } from 'lucide-react';

let isMermaidInitialized = false;

function initMermaid() {
  if (isMermaidInitialized) return;
  try {
    mermaid.initialize({
      startOnLoad: false,
      theme: 'dark',
      securityLevel: 'loose',
      fontFamily: 'ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
      themeVariables: {
        darkMode: true,
        background: '#090d16',
        mainBkg: '#0f172a',
        nodeBorder: '#6366f1',
        clusterBkg: '#0b0f19',
        clusterBorder: '#334155',
        defaultLinkColor: '#94a3b8',
        titleColor: '#f8fafc',
        edgeLabelBackground: '#020617',
        nodeTextColor: '#f8fafc',
        actorTextColor: '#f8fafc',
        actorLineColor: '#6366f1',
        signalColor: '#94a3b8',
        signalTextColor: '#f8fafc',
        labelBoxBkgColor: '#0f172a',
        labelBoxBorderColor: '#475569',
        labelTextColor: '#f8fafc',
        loopTextColor: '#cbd5e1',
        noteBorderColor: '#4f46e5',
        noteBkgColor: '#1e1b4b',
        noteTextColor: '#e0e7ff',
      },
      suppressErrorRendering: true
    });
    isMermaidInitialized = true;
  } catch (err) {
    console.error('Failed to initialize Mermaid:', err);
  }
}

export interface MermaidViewerProps {
  code: string;
  title?: string;
  className?: string;
  showRawDefault?: boolean;
}

export const MermaidViewer: React.FC<MermaidViewerProps> = ({
  code,
  title = 'Mermaid Architecture Diagram',
  className = '',
  showRawDefault = false
}) => {
  const [svg, setSvg] = useState<string>('');
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [viewMode, setViewMode] = useState<'diagram' | 'code'>(showRawDefault ? 'code' : 'diagram');
  const [copied, setCopied] = useState<boolean>(false);

  useEffect(() => {
    let isMounted = true;
    initMermaid();

    const renderDiagram = async () => {
      const trimmedCode = code?.trim() || '';
      if (!trimmedCode) {
        setSvg('');
        setError(null);
        setIsLoading(false);
        return;
      }

      setIsLoading(true);
      setError(null);

      const renderId = `mermaid-${Math.random().toString(36).substring(2, 9)}`;

      try {
        const { svg: renderedSvg } = await mermaid.render(renderId, trimmedCode);
        if (isMounted) {
          setSvg(renderedSvg);
          setError(null);
          setIsLoading(false);
        }
      } catch (err: any) {
        // Clean up temporary DOM nodes that Mermaid creates on render failure
        const tempElement = document.getElementById(`d${renderId}`);
        if (tempElement) tempElement.remove();
        const directElement = document.getElementById(renderId);
        if (directElement) directElement.remove();

        if (isMounted) {
          const errorMsg = err?.message || err?.str || String(err) || 'Invalid Mermaid diagram syntax';
          setError(errorMsg);
          setIsLoading(false);
        }
      }
    };

    renderDiagram();

    return () => {
      isMounted = false;
    };
  }, [code]);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // ignore clipboard error
    }
  };

  return (
    <div
      data-testid="mermaid-viewer"
      className={`my-3 rounded-xl border border-indigo-500/30 bg-slate-900/90 shadow-lg overflow-hidden text-left ${className}`}
    >
      {/* Header bar */}
      <div className="flex items-center justify-between px-3.5 py-2 bg-slate-900 border-b border-indigo-500/20 text-xs font-mono text-indigo-300">
        <span className="flex items-center gap-2 font-bold uppercase tracking-wider">
          <Layers className="w-4 h-4 text-indigo-400 shrink-0" />
          <span>{title}</span>
        </span>

        <div className="flex items-center gap-2">
          {/* Mode toggle */}
          <div className="flex items-center bg-slate-950 rounded-lg p-0.5 border border-slate-800">
            <button
              type="button"
              onClick={() => setViewMode('diagram')}
              className={`px-2 py-0.5 rounded text-[10px] font-sans font-semibold transition cursor-pointer flex items-center gap-1 ${
                viewMode === 'diagram'
                  ? 'bg-indigo-600/30 text-indigo-300 border border-indigo-500/40'
                  : 'text-slate-400 hover:text-slate-200'
              }`}
              title="View visual diagram"
            >
              <Eye className="w-3 h-3" />
              <span>Diagram</span>
            </button>
            <button
              type="button"
              onClick={() => setViewMode('code')}
              className={`px-2 py-0.5 rounded text-[10px] font-sans font-semibold transition cursor-pointer flex items-center gap-1 ${
                viewMode === 'code'
                  ? 'bg-indigo-600/30 text-indigo-300 border border-indigo-500/40'
                  : 'text-slate-400 hover:text-slate-200'
              }`}
              title="View mermaid code"
            >
              <Code2 className="w-3 h-3" />
              <span>Code</span>
            </button>
          </div>

          {/* Copy code button */}
          <button
            type="button"
            onClick={handleCopy}
            className="flex items-center gap-1 text-[10px] text-slate-400 hover:text-slate-200 transition px-2 py-1 rounded hover:bg-slate-800 cursor-pointer"
            title="Copy diagram code"
          >
            {copied ? (
              <>
                <Check className="w-3 h-3 text-emerald-400" />
                <span className="text-emerald-400 font-semibold">Copied</span>
              </>
            ) : (
              <>
                <Copy className="w-3 h-3" />
                <span>Copy</span>
              </>
            )}
          </button>
        </div>
      </div>

      {/* Content Area */}
      {viewMode === 'code' ? (
        <pre className="p-3 font-mono text-xs text-indigo-200 bg-slate-950/90 overflow-x-auto whitespace-pre leading-relaxed">
          <code>{code}</code>
        </pre>
      ) : (
        <div className="p-3 bg-slate-950/70">
          {isLoading ? (
            <div className="flex items-center justify-center gap-2 py-8 text-xs text-indigo-300/70 font-mono">
              <RefreshCw className="w-4 h-4 animate-spin text-indigo-400" />
              <span>Rendering diagram...</span>
            </div>
          ) : error ? (
            <div className="space-y-3">
              <div className="p-3 bg-rose-950/40 border border-rose-500/40 rounded-xl text-xs space-y-1.5 text-rose-200">
                <div className="flex items-center gap-2 font-semibold text-rose-400">
                  <AlertTriangle className="w-4 h-4 shrink-0" />
                  <span>Failed to render Mermaid diagram</span>
                </div>
                <p className="text-[11px] font-mono text-rose-300/90 whitespace-pre-wrap">{error}</p>
              </div>
              <pre className="p-3 font-mono text-xs text-slate-300 bg-slate-950 border border-slate-800 rounded-xl overflow-x-auto whitespace-pre leading-relaxed">
                <code>{code}</code>
              </pre>
            </div>
          ) : (
            <div
              data-testid="mermaid-diagram-container"
              className="overflow-x-auto flex justify-center items-center py-2 [&>svg]:max-w-full [&>svg]:h-auto text-center"
              dangerouslySetInnerHTML={{ __html: svg }}
            />
          )}
        </div>
      )}
    </div>
  );
};
