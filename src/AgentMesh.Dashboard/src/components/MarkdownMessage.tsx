import React, { useState } from 'react';
import { Check, Copy, Info, AlertTriangle, Lightbulb, ShieldAlert } from 'lucide-react';
import { MermaidViewer } from './MermaidViewer';

interface MarkdownMessageProps {
  content: string;
  className?: string;
  isUser?: boolean;
}

/**
 * Parses inline markdown:
 * - Links: [text](url)
 * - Bold: **text**
 * - Italic: *text*
 * - Inline code: `code`
 * - Mentions: @persona-name or @agent-id
 */
export function renderInlineMarkdown(text: string, isUser = false): React.ReactNode[] {
  const elements: React.ReactNode[] = [];
  // Tokenize regex for links, bold, code, italic
  const tokenRegex = /(\[[^\]]+\]\([^)]+\)|\*\*[^*]+\*\*|`[^`]+`|\*[^*]+\*)/g;
  let lastIndex = 0;
  let match: RegExpExecArray | null;

  while ((match = tokenRegex.exec(text)) !== null) {
    if (match.index > lastIndex) {
      elements.push(text.substring(lastIndex, match.index));
    }

    const token = match[0];
    const key = `inline-${match.index}`;

    if (token.startsWith('[') && token.includes('](')) {
      const closingBracket = token.indexOf('](');
      const label = token.slice(1, closingBracket);
      const url = token.slice(closingBracket + 2, -1);
      elements.push(
        <a
          key={key}
          href={url}
          target="_blank"
          rel="noopener noreferrer"
          className={
            isUser
              ? 'underline font-medium text-sky-200 hover:text-white transition'
              : 'underline underline-offset-2 font-medium text-sky-400 hover:text-sky-300 transition'
          }
        >
          {label}
        </a>
      );
    } else if (token.startsWith('**') && token.endsWith('**')) {
      elements.push(
        <strong key={key} className={isUser ? 'font-semibold text-white' : 'font-semibold text-slate-100'}>
          {token.slice(2, -2)}
        </strong>
      );
    } else if (token.startsWith('`') && token.endsWith('`')) {
      elements.push(
        <code
          key={key}
          className={
            isUser
              ? 'px-1.5 py-0.5 rounded bg-sky-950/60 border border-sky-400/30 text-sky-200 font-mono text-[11px]'
              : 'px-1.5 py-0.5 rounded bg-slate-950 border border-slate-800 text-sky-300 font-mono text-[11px]'
          }
        >
          {token.slice(1, -1)}
        </code>
      );
    } else if (token.startsWith('*') && token.endsWith('*')) {
      elements.push(
        <em key={key} className="italic text-slate-300">
          {token.slice(1, -1)}
        </em>
      );
    } else {
      elements.push(token);
    }

    lastIndex = match.index + token.length;
  }

  if (lastIndex < text.length) {
    elements.push(text.substring(lastIndex));
  }

  return elements.length > 0 ? elements : [text];
}

interface CodeBlockProps {
  language: string;
  code: string;
}

function CodeBlock({ language, code }: CodeBlockProps) {
  const [copied, setCopied] = useState(false);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // ignore
    }
  };

  return (
    <div className="my-2.5 rounded-xl border border-slate-800 bg-slate-950 overflow-hidden shadow-sm text-left">
      <div className="flex items-center justify-between px-3 py-1.5 bg-slate-900/90 border-b border-slate-800 text-[10px] font-mono text-slate-400 font-semibold">
        <span className="uppercase">{(language || 'text').toUpperCase()}</span>
        <button
          type="button"
          onClick={handleCopy}
          className="flex items-center gap-1 text-[10px] text-slate-400 hover:text-slate-200 transition cursor-pointer"
          title="Copy code snippet"
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
      <pre className="p-3 font-mono text-xs text-slate-200 overflow-x-auto whitespace-pre leading-relaxed">
        <code>{code}</code>
      </pre>
    </div>
  );
}

export const MarkdownMessage: React.FC<MarkdownMessageProps> = ({
  content,
  className = '',
  isUser = false
}) => {
  if (!content) return null;

  const lines = content.split('\n');
  const nodes: React.ReactNode[] = [];

  let inCode = false;
  let codeLang = '';
  let codeBuffer: string[] = [];

  let inAlert = false;
  let alertType = 'NOTE';
  let alertBuffer: string[] = [];

  const flushCode = (key: string) => {
    if (codeLang.toLowerCase() === 'mermaid') {
      nodes.push(<MermaidViewer key={key} code={codeBuffer.join('\n')} />);
    } else {
      nodes.push(<CodeBlock key={key} language={codeLang} code={codeBuffer.join('\n')} />);
    }
    inCode = false;
    codeLang = '';
    codeBuffer = [];
  };

  const flushAlert = (key: string) => {
    const isWarn = alertType === 'WARNING' || alertType === 'CAUTION';
    const isTip = alertType === 'TIP';
    const isImp = alertType === 'IMPORTANT';

    const IconComp = isWarn
      ? AlertTriangle
      : isTip
      ? Lightbulb
      : isImp
      ? ShieldAlert
      : Info;

    const colorClasses = isWarn
      ? 'bg-amber-950/30 border-amber-500/40 text-amber-200'
      : isTip
      ? 'bg-emerald-950/30 border-emerald-500/40 text-emerald-200'
      : isImp
      ? 'bg-rose-950/30 border-rose-500/40 text-rose-200'
      : 'bg-sky-950/30 border-sky-500/40 text-sky-200';

    nodes.push(
      <div
        key={key}
        className={`my-2 p-3 rounded-xl border text-xs flex items-start gap-2.5 ${colorClasses}`}
      >
        <IconComp className="w-4 h-4 shrink-0 mt-0.5 text-current" />
        <div className="space-y-1 min-w-0 flex-1 leading-relaxed">
          <div className="font-semibold uppercase tracking-wider text-[10px] opacity-90">
            {alertType}
          </div>
          <div>{alertBuffer.map((line, lIdx) => (
            <div key={`al-${lIdx}`}>{renderInlineMarkdown(line, isUser)}</div>
          ))}</div>
        </div>
      </div>
    );

    inAlert = false;
    alertType = 'NOTE';
    alertBuffer = [];
  };

  for (let i = 0; i < lines.length; i++) {
    const rawLine = lines[i];

    // Fenced Code Blocks
    if (rawLine.trim().startsWith('```')) {
      if (!inCode) {
        if (inAlert) flushAlert(`alert-${i}`);
        inCode = true;
        codeLang = rawLine.trim().replace(/^```/, '').trim();
        codeBuffer = [];
      } else {
        flushCode(`code-${i}`);
      }
      continue;
    }

    if (inCode) {
      codeBuffer.push(rawLine);
      continue;
    }

    // GitHub Alerts
    const alertMatch = rawLine.match(/^>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]/i);
    if (alertMatch) {
      if (inAlert) flushAlert(`alert-${i}`);
      inAlert = true;
      alertType = alertMatch[1].toUpperCase();
      continue;
    }

    if (inAlert) {
      if (rawLine.startsWith('>')) {
        alertBuffer.push(rawLine.replace(/^>\s?/, ''));
        continue;
      } else {
        flushAlert(`alert-${i}`);
      }
    }

    // Horizontal Rule
    if (/^(\*{3,}|-{3,}|_{3,})$/.test(rawLine.trim())) {
      nodes.push(
        <hr
          key={`hr-${i}`}
          className={`my-3 border-0 h-px ${isUser ? 'bg-sky-400/30' : 'bg-slate-800'}`}
        />
      );
      continue;
    }

    // Headings
    if (rawLine.startsWith('# ')) {
      nodes.push(
        <h1
          key={`h1-${i}`}
          className={`text-base font-bold mt-3 mb-1.5 pb-1 border-b ${
            isUser ? 'text-white border-sky-400/30' : 'text-white border-slate-800'
          }`}
        >
          {renderInlineMarkdown(rawLine.replace(/^#\s+/, ''), isUser)}
        </h1>
      );
      continue;
    }

    if (rawLine.startsWith('## ')) {
      nodes.push(
        <h2
          key={`h2-${i}`}
          className={`text-sm font-bold mt-2.5 mb-1 pb-0.5 border-b ${
            isUser ? 'text-sky-100 border-sky-400/20' : 'text-slate-100 border-slate-800/60'
          }`}
        >
          {renderInlineMarkdown(rawLine.replace(/^##\s+/, ''), isUser)}
        </h2>
      );
      continue;
    }

    if (rawLine.startsWith('### ')) {
      nodes.push(
        <h3
          key={`h3-${i}`}
          className={`text-xs font-bold mt-2 mb-1 ${
            isUser ? 'text-sky-200' : 'text-sky-300'
          }`}
        >
          {renderInlineMarkdown(rawLine.replace(/^###\s+/, ''), isUser)}
        </h3>
      );
      continue;
    }

    // Numbered lists: "1. Foo", " 2. Bar"
    const numMatch = rawLine.match(/^(\s*)(\d+)\.\s+(.*)$/);
    if (numMatch) {
      const indentLevel = Math.floor((numMatch[1] || '').length / 2);
      const num = numMatch[2];
      const text = numMatch[3];
      nodes.push(
        <div
          key={`ol-${i}`}
          className={`flex items-start gap-2 my-0.5 text-xs leading-relaxed ${
            indentLevel > 0 ? `ml-${Math.min(indentLevel * 4, 12)} pl-2` : 'ml-1'
          }`}
        >
          <span className={`font-mono font-semibold shrink-0 ${isUser ? 'text-sky-300' : 'text-sky-400'}`}>
            {num}.
          </span>
          <div className="flex-1 min-w-0">{renderInlineMarkdown(text, isUser)}</div>
        </div>
      );
      continue;
    }

    // Bullet lists: "* Foo", "- Foo", "  * Bar"
    const bulletMatch = rawLine.match(/^(\s*)([-*])\s+(.*)$/);
    if (bulletMatch) {
      const indentLevel = Math.floor((bulletMatch[1] || '').length / 2);
      const text = bulletMatch[3];
      nodes.push(
        <div
          key={`ul-${i}`}
          className={`flex items-start gap-2 my-0.5 text-xs leading-relaxed ${
            indentLevel > 0 ? 'ml-5 pl-2 border-l border-slate-800' : 'ml-1'
          }`}
        >
          <span className={`text-base leading-none select-none shrink-0 mt-0.5 ${isUser ? 'text-sky-300' : 'text-sky-400'}`}>
            •
          </span>
          <div className="flex-1 min-w-0">{renderInlineMarkdown(text, isUser)}</div>
        </div>
      );
      continue;
    }

    // Empty lines (spacing)
    if (rawLine.trim().length === 0) {
      nodes.push(<div key={`sp-${i}`} className="h-2" />);
      continue;
    }

    // Normal paragraph
    nodes.push(
      <p
        key={`p-${i}`}
        className={`my-1 text-xs leading-relaxed ${
          isUser ? 'text-sky-100' : 'text-slate-200'
        }`}
      >
        {renderInlineMarkdown(rawLine, isUser)}
      </p>
    );
  }

  if (inCode) flushCode('code-final');
  if (inAlert) flushAlert('alert-final');

  return (
    <div className={`space-y-0.5 select-text ${className}`}>
      {nodes}
    </div>
  );
};
