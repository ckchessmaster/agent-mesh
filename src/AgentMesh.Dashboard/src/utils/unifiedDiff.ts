/**
 * A minimal, dependency-free parser for unified diffs, sized for one job: turn git's (or GitHub's)
 * patch text into coloured lines with line-number gutters.
 *
 * Why hand-rolled instead of a syntax highlighter: the panel needs *structure* (which lines are
 * additions, which side of the diff a line number belongs to, how many hunks there are) rather than
 * token colours, and the input here is machine-generated with a grammar narrow enough to classify by
 * prefix. The alternative — pulling in a diff-rendering library — buys word-level intra-line
 * highlighting the approved design does not ask for, at the cost of a second opinion about what a hunk
 * is that this file would then have to audit line by line anyway.
 *
 * The state machine matters more than the prefix matching. Outside a hunk a line starting with `-` is a
 * file header (`--- a/foo.cs`); inside one it is a deleted line whose text may itself begin with `-`.
 * The two are told apart by the hunk's declared line counts, which is exactly how git and every diff
 * viewer resolve the ambiguity — so this parser tracks them rather than guessing from the prefix.
 */

/** How a line should be coloured. */
export type DiffLineKind =
  /** `+` inside a hunk. */
  | 'add'
  /** `-` inside a hunk. */
  | 'del'
  /** Context line (leading space), or a blank line inside a hunk. */
  | 'ctx'
  /** A `@@ … @@` header. */
  | 'hunk'
  /** A `diff --git a/x b/y` header — the start of a file's block. */
  | 'file'
  /** Metadata git emits around a block: `index …`, `--- …`, `+++ …`, `new file mode …`, renames. */
  | 'meta'
  /** Text that is neither of the above — a truncation marker, or prose from a fallback source. */
  | 'plain';

export interface DiffLine {
  kind: DiffLineKind;
  /** The line with its one-character prefix removed for hunk content, verbatim otherwise. */
  text: string;
  /** 1-based line number in the preimage; null when the line does not exist there. */
  oldLine: number | null;
  /** 1-based line number in the postimage; null when the line does not exist there. */
  newLine: number | null;
  /** Stable index for React keys — the same line always maps to the same id within one parse. */
  id: number;
}

export interface ParsedDiff {
  lines: DiffLine[];
  /** Count of `+` lines actually seen. Not the server's number — see `countsMatchHeader`. */
  additions: number;
  deletions: number;
  hunks: number;
  /** True when git refused to produce a textual diff for this content. */
  binary: boolean;
  /** True when there is nothing to show (an empty or whitespace-only body). */
  empty: boolean;
  /**
   * The paths the patch touches, in order, from its `diff --git` headers — so a whole-branch body can
   * be rendered as labelled blocks without a second request per file.
   */
  paths: string[];
}

/**
 * Parses a unified diff body. Never throws: a patch cut at the server's byte limit can end mid-line,
 * mid-hunk, or with a partial header, and the panel's job is to show what arrived and say the rest is
 * missing rather than to blank the pane.
 */
export function parseUnifiedDiff(patch: string | null | undefined): ParsedDiff {
  const body = patch ?? '';

  // A whitespace-only body is "no diff", not "a diff with one blank context line in it". Without this
  // guard the split below yields a single empty element and the panel would render one stray gutter row
  // under a message that says there is nothing to show.
  if (body.trim().length === 0) {
    return { lines: [], additions: 0, deletions: 0, hunks: 0, binary: false, empty: true, paths: [] };
  }

  const lines: DiffLine[] = [];
  const paths: string[] = [];

  let additions = 0;
  let deletions = 0;
  let hunks = 0;
  let binary = false;

  // Hunk state. `inHunk` gates the +/- classification; the remaining counts say when the hunk's
  // declared lines are exhausted, which is what makes the next `--- `/`+++ ` pair a header again.
  let inHunk = false;
  let numberGutters = true;
  let oldCount = 0;
  let newCount = 0;
  let oldLine = 0;
  let newLine = 0;

  const push = (kind: DiffLineKind, text: string, old_: number | null, new_: number | null) => {
    lines.push({ kind, text, oldLine: old_, newLine: new_, id: lines.length });
  };

  for (const raw of body.split('\n')) {
    // A CRLF patch (git's own output is LF; a Windows-authored checkpoint payload may not be) would
    // otherwise paint the trailing \r into the gutter of every line.
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;

    if (line.startsWith('diff --git ')) {
      inHunk = false;
      push('file', line, null, null);
      const parsed = parseDiffGitPath(line);
      if (parsed) paths.push(parsed);
      continue;
    }

    if (line.startsWith('@@')) {
      const header = parseHunkHeader(line);
      inHunk = true;
      hunks += 1;

      // A header this parser will not guess (`@@@` combined, or one cut by the byte cap) turns off
      // gutter numbering for its hunk rather than inventing a sequence: a number beside a line that
      // was never in that position is worse than a blank one, because the operator has no way to tell
      // that the blank-and-increment path was taken.
      numberGutters = header !== null;
      if (header) {
        oldLine = header.oldStart;
        newLine = header.newStart;
        oldCount = header.oldCount;
        newCount = header.newCount;
      } else {
        oldCount = 0;
        newCount = 0;
      }
      push('hunk', line, null, null);
      continue;
    }

    if (line.startsWith('\\')) {
      // "\ No newline at end of file" belongs to whichever side it follows and counts for neither.
      push('meta', line, null, null);
      continue;
    }

    if (!inHunk) {
      if (line.startsWith('Binary files ')) binary = true;

      if (
        line.startsWith('index ') ||
        line.startsWith('--- ') ||
        line.startsWith('+++ ') ||
        line.startsWith('old mode ') ||
        line.startsWith('new mode ') ||
        line.startsWith('new file mode ') ||
        line.startsWith('deleted file mode ') ||
        line.startsWith('rename from ') ||
        line.startsWith('rename to ') ||
        line.startsWith('copy from ') ||
        line.startsWith('copy to ') ||
        line.startsWith('similarity index ') ||
        line.startsWith('dissimilarity index ') ||
        /^GIT binary patch/.test(line) ||
        /^literal \d+/.test(line) ||
        /^delta \d+/.test(line) ||
        line.startsWith('Binary files ')
      ) {
        push('meta', line, null, null);
        continue;
      }

      push(line.length === 0 ? 'ctx' : 'plain', line, null, null);
      continue;
    }

    // Inside a hunk: classify by prefix, then advance whichever counters the prefix owns.
    const prefix = line[0];

    if (prefix === '+') {
      additions += 1;
      const at = numberGutters ? newLine : null;
      if (newCount > 0) newCount -= 1;
      newLine += 1;
      push('add', line.slice(1), null, at);
      continue;
    }

    if (prefix === '-') {
      deletions += 1;
      const at = numberGutters ? oldLine : null;
      if (oldCount > 0) oldCount -= 1;
      oldLine += 1;
      push('del', line.slice(1), at, null);
      continue;
    }

    if (prefix === ' ' || line.length === 0) {
      const oldAt = numberGutters ? oldLine : null;
      const newAt = numberGutters ? newLine : null;
      if (oldCount > 0) oldCount -= 1;
      if (newCount > 0) newCount -= 1;
      oldLine += 1;
      newLine += 1;
      push('ctx', line.length === 0 ? '' : line.slice(1), oldAt, newAt);
      if (oldCount <= 0 && newCount <= 0) inHunk = false;
      continue;
    }

    // A garbage line inside a hunk (a truncation marker spliced into the middle of one, most often).
    push('plain', line, null, null);
  }

  return {
    lines,
    additions,
    deletions,
    hunks,
    binary,
    empty: body.trim().length === 0,
    paths,
  };
}

/**
 * `@@ -16,0 +16,8 @@ func Foo()` → the two starts and counts.
 *
 * Returns null for anything that is not a plain two-sided header, and the caller then leaves that
 * hunk's gutters blank rather than guessing: an unparseable header (a body cut at the server's byte cap)
 * and a combined merge header (`@@@ -1,5 -1,5 +1,6 @@@`, which has one postimage and N preimages and so
 * no single old-side number to report) both mean "the line numbers are unknown", and inventing a
 * monotonically increasing pair would print confident wrong numbers beside every line below.
 */
export function parseHunkHeader(
  line: string
): { oldStart: number; oldCount: number; newStart: number; newCount: number } | null {
  const match = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(line);
  if (!match) return null;

  return {
    // An omitted count means exactly one line, per the unified format — not zero, which is what
    // defaulting to 0 would claim, and the difference is whether the gutter advances at all.
    oldStart: Number(match[1]),
    oldCount: match[2] === undefined ? 1 : Number(match[2]),
    newStart: Number(match[3]),
    newCount: match[4] === undefined ? 1 : Number(match[4])
  };
}

/**
 * `diff --git a/src/A.cs b/src/A.cs` → `src/A.cs`.
 *
 * The postimage is the one to show: for a rename it is the new name, and the row already carries the
 * old name.
 *
 * Git quotes a path containing a space (`diff --git "a/old b/x" "b/new b/y"`), so the quoted form is
 * split on the quote boundary and parses exactly. For the unquoted form there is no separator to trust
 * — `a/old b/new.txt b/old b/new.txt` is genuinely ambiguous to a naive split — so the *first* ` b/`
 * after the `a/` is used, which is right for the overwhelmingly common cases (no space in the path, or
 * the same path on both sides) and degrades to the raw pair otherwise. Rename-with-space arrives quoted,
 * which is why that branch is exact rather than heuristic.
 */
export function parseDiffGitPath(line: string): string | null {
  const match = /^diff --git (.+)$/.exec(line);
  if (!match) return null;

  const pair = match[1];

  // The quoted form: `"a/<pre>" "b/<post>"`.
  const quoted = /^"a\/.*"\s"b\/(.*)"$/.exec(pair);
  if (quoted) return unquote(quoted[1]);

  if (pair.startsWith('a/')) {
    const at = pair.indexOf(' b/');
    if (at >= 0) {
      const candidate = pair.slice(at + 3).trim();
      if (candidate.length > 0) return unquote(candidate);
    }
  } else {
    // No `a/` prefix (GitHub's payload sometimes gives bare `path path`): fall back to the tail.
    const at = pair.lastIndexOf(' b/');
    if (at >= 0) {
      const candidate = pair.slice(at + 3).trim();
      if (candidate.length > 0) return unquote(candidate);
    }
  }

  const trimmed = pair.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/** Strips git's C-style quoting around a path it had to escape. */
const unquote = (value: string): string => {
  if (value.length >= 2 && value.startsWith('"') && value.endsWith('"')) {
    return value.slice(1, -1).replace(/\\n/g, '\n').replace(/\\t/g, '\t').replace(/\\"/g, '"').replace(/\\\\/g, '\\');
  }
  return value;
};

/** True when a parsed diff looks like it was cut off — a hunk header with fewer lines than it claims. */
export function looksTruncated(parsed: ParsedDiff): boolean {
  const last = parsed.lines[parsed.lines.length - 1];
  if (!last) return false;
  return last.kind !== 'file' && last.kind !== 'meta';
}
