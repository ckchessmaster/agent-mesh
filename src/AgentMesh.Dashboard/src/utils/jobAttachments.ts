export const MAX_JOB_ATTACHMENTS = 10;
export const MAX_JOB_ATTACHMENT_BYTES = 25 * 1024 * 1024; // 25 MB

export const ALLOWED_JOB_ATTACHMENT_EXTENSIONS = new Set<string>([
  // Images
  '.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg', '.bmp', '.ico',
  // PDFs
  '.pdf',
  // Markdown / Text / Docs
  '.md', '.markdown', '.txt', '.log', '.rst', '.adoc',
  // Data & Config
  '.json', '.jsonl', '.csv', '.tsv', '.yaml', '.yml', '.toml', '.xml', '.ini', '.env', '.conf', '.config', '.properties',
  // Source Code
  '.cs', '.csproj', '.sln', '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.py', '.go', '.rs', '.java', '.kt', '.swift',
  '.c', '.cpp', '.h', '.hpp', '.rb', '.php', '.sh', '.bash', '.zsh', '.ps1', '.sql', '.graphql', '.gql', '.html', '.htm',
  '.css', '.scss', '.less', '.vue', '.svelte', '.dockerfile', '.makefile', '.proto', '.tf', '.hcl', '.bicep', '.gradle',
  // Archives
  '.zip', '.tar', '.gz', '.tgz'
]);

export const JOB_ATTACHMENT_ACCEPT = [
  'image/*',
  '.pdf',
  '.md',
  '.markdown',
  '.txt',
  '.log',
  '.json',
  '.jsonl',
  '.csv',
  '.tsv',
  '.yaml',
  '.yml',
  '.toml',
  '.xml',
  '.cs',
  '.csproj',
  '.sln',
  '.ts',
  '.tsx',
  '.js',
  '.jsx',
  '.py',
  '.go',
  '.rs',
  '.java',
  '.sh',
  '.sql',
  '.html',
  '.css',
  '.zip',
  '.tar',
  '.gz',
  '.tgz'
].join(',');

export interface StagedLocalAttachment {
  localId: string;
  file: File;
  name: string;
  contentType: string;
  sizeBytes: number;
  previewUrl?: string;
}

export function formatAttachmentSize(bytes?: number | null): string {
  if (!bytes || bytes <= 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function isImageAttachment(contentType?: string | null, fileName?: string | null): boolean {
  const ct = (contentType || '').toLowerCase();
  const name = (fileName || '').toLowerCase();
  return ct.startsWith('image/') || /\.(png|jpg|jpeg|gif|webp|svg|bmp|ico)$/.test(name);
}

export function getFileExtension(fileName?: string | null): string {
  const trimmed = (fileName || '').trim();
  const lastDot = trimmed.lastIndexOf('.');
  if (lastDot <= 0 || lastDot === trimmed.length - 1) return '';
  return trimmed.slice(lastDot).toLowerCase();
}

export function extensionFromMimeType(contentType?: string | null): string {
  const ct = (contentType || '').trim().toLowerCase().split(';')[0];
  switch (ct) {
    case 'image/png':
      return '.png';
    case 'image/jpeg':
    case 'image/jpg':
      return '.jpg';
    case 'image/gif':
      return '.gif';
    case 'image/webp':
      return '.webp';
    case 'image/svg+xml':
      return '.svg';
    case 'image/bmp':
      return '.bmp';
    case 'image/x-icon':
    case 'image/vnd.microsoft.icon':
      return '.ico';
    case 'application/pdf':
      return '.pdf';
    case 'text/markdown':
      return '.md';
    case 'application/json':
      return '.json';
    case 'text/csv':
      return '.csv';
    case 'application/zip':
      return '.zip';
    default:
      if (ct.startsWith('image/')) return '.png';
      if (ct.startsWith('text/')) return '.txt';
      return '';
  }
}

export function isAllowedJobAttachmentFile(fileName?: string | null, contentType?: string | null): boolean {
  const ext = getFileExtension(fileName);
  const ct = (contentType || '').trim().toLowerCase().split(';')[0];

  if (!ext) {
    return (
      ct.startsWith('image/') ||
      ct === 'application/pdf' ||
      ct.startsWith('text/') ||
      ct === 'application/json'
    );
  }

  return ALLOWED_JOB_ATTACHMENT_EXTENSIONS.has(ext);
}

export function normalizePastedClipboardFile(file: File, sequenceNumber: number): File {
  const rawName = (file.name || '').trim();
  const isGenericOrUnnamed =
    !rawName ||
    rawName.toLowerCase() === 'blob' ||
    /^image(\.(png|jpe?g|gif|webp|bmp|svg|ico))?$/i.test(rawName);

  if (isGenericOrUnnamed) {
    const ext = getFileExtension(rawName) || extensionFromMimeType(file.type) || '.png';
    const nextName = `screenshot-${sequenceNumber}${ext}`;
    const resolvedType = file.type || (ext === '.png' ? 'image/png' : 'application/octet-stream');
    return new File([file], nextName, {
      type: resolvedType,
      lastModified: file.lastModified || Date.now()
    });
  }

  if (!getFileExtension(rawName)) {
    const ext = extensionFromMimeType(file.type);
    if (ext) {
      return new File([file], `${rawName}${ext}`, {
        type: file.type,
        lastModified: file.lastModified || Date.now()
      });
    }
  }

  return file;
}

export function extractFilesFromTransfer(transfer?: DataTransfer | null): File[] {
  if (!transfer) return [];
  if (transfer.files && transfer.files.length > 0) {
    return Array.from(transfer.files);
  }
  const files: File[] = [];
  if (transfer.items) {
    for (let i = 0; i < transfer.items.length; i++) {
      const item = transfer.items[i];
      if (item.kind === 'file') {
        const f = item.getAsFile();
        if (f) files.push(f);
      }
    }
  }
  return files;
}

export function validateJobAttachmentFiles(
  incoming: File[],
  existingCount = 0,
  maxTotal: number = MAX_JOB_ATTACHMENTS
): string | null {
  if (incoming.length === 0) return null;
  if (existingCount + incoming.length > maxTotal) {
    return `A maximum of ${maxTotal} files can be attached at once.`;
  }

  for (const file of incoming) {
    const label = file.name || 'clipboard-file';
    if (file.size <= 0) {
      return `File '${label}' is empty.`;
    }
    if (file.size > MAX_JOB_ATTACHMENT_BYTES) {
      return `File '${label}' exceeds the 25 MB maximum file size limit.`;
    }
    if (!isAllowedJobAttachmentFile(file.name, file.type)) {
      return `File '${label}' has an unsupported file type.`;
    }
  }

  return null;
}

export function createImagePreviewUrl(file: File): string | undefined {
  if (
    isImageAttachment(file.type, file.name) &&
    typeof URL !== 'undefined' &&
    typeof URL.createObjectURL === 'function'
  ) {
    try {
      return URL.createObjectURL(file);
    } catch {
      return undefined;
    }
  }
  return undefined;
}

export function revokeImagePreviewUrl(url?: string): void {
  if (url && typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
    try {
      URL.revokeObjectURL(url);
    } catch {
      // Ignore revoke failures in non-browser test environments
    }
  }
}
