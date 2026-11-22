import React, { useEffect } from 'react';
import { Download, Image as ImageIcon, X } from 'lucide-react';
import { formatAttachmentSize } from '../utils/jobAttachments';

export interface LightboxAttachmentItem {
  url: string;
  fileName: string;
  sizeBytes?: number;
}

interface AttachmentLightboxModalProps {
  attachment: LightboxAttachmentItem | null;
  onClose: () => void;
}

export const AttachmentLightboxModal: React.FC<AttachmentLightboxModalProps> = ({
  attachment,
  onClose
}) => {
  useEffect(() => {
    if (!attachment) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        onClose();
      }
    };
    document.addEventListener('keydown', handleKeyDown, true);
    return () => document.removeEventListener('keydown', handleKeyDown, true);
  }, [attachment, onClose]);

  if (!attachment) return null;

  const downloadHref = `${attachment.url}${attachment.url.includes('?') ? '&' : '?'}download=true`;

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label={`Image preview: ${attachment.fileName}`}
      data-testid="attachment-lightbox"
      onClick={(e) => {
        e.stopPropagation();
        onClose();
      }}
      className="fixed inset-0 z-[60] bg-black/85 backdrop-blur-md flex flex-col items-center justify-center p-2 sm:p-6 pt-[max(0.75rem,env(safe-area-inset-top))] pb-[max(0.75rem,env(safe-area-inset-bottom))]"
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="max-w-5xl w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl overflow-hidden flex flex-col max-h-[90vh]"
      >
        <div className="px-5 py-3 border-b border-slate-800 flex items-center justify-between bg-slate-950/80">
          <div className="flex items-center gap-2 min-w-0">
            <ImageIcon className="w-4 h-4 text-sky-400 shrink-0" />
            <span className="font-mono text-xs font-semibold text-white truncate">
              {attachment.fileName}
            </span>
            {attachment.sizeBytes ? (
              <span className="text-[11px] font-mono text-slate-400">
                ({formatAttachmentSize(attachment.sizeBytes)})
              </span>
            ) : null}
          </div>
          <div className="flex items-center gap-2">
            <a
              href={downloadHref}
              download={attachment.fileName}
              data-testid="lightbox-download-link"
              className="px-3 py-1.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-medium flex items-center gap-1.5 transition"
            >
              <Download className="w-3.5 h-3.5 text-sky-400" />
              <span>Download</span>
            </a>
            <button
              type="button"
              onClick={onClose}
              aria-label="Close image preview"
              data-testid="lightbox-close-btn"
              className="p-1.5 rounded-lg text-slate-400 hover:text-white hover:bg-slate-800 transition cursor-pointer"
            >
              <X className="w-4 h-4" />
            </button>
          </div>
        </div>
        <div className="flex-1 p-6 bg-slate-950 flex items-center justify-center overflow-auto">
          <img
            src={attachment.url}
            alt={attachment.fileName}
            className="max-h-[75vh] max-w-full object-contain rounded-lg shadow-xl"
            data-testid="lightbox-image"
          />
        </div>
      </div>
    </div>
  );
};
