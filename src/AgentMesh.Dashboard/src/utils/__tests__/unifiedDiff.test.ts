import { describe, expect, it } from 'vitest';
import {
  looksTruncated,
  parseDiffGitPath,
  parseHunkHeader,
  parseUnifiedDiff
} from '../unifiedDiff';

/**
 * Contract fixtures, not synthetic pretty-printing: every patch below is shaped the way `git diff
 * --find-copies-harder -U3` emits it, because that is the payload the Changes panel renders and the
 * ambiguity it has to resolve (is a `-` line a deletion or a file header?) is resolved by the hunk's
 * declared counts, not by the prefix.
 */

const twoSidedPatch = [
  'diff --git a/src/A.cs b/src/A.cs',
  'index 83db48f..1a2b3c4 100644',
  '--- a/src/A.cs',
  '+++ b/src/A.cs',
  '@@ -10,7 +10,9 @@ public class A',
  '     context line one',
  '-    var removed = true;',
  '+    var added = true;',
  '+    var addedToo = 2;',
  '     context line two',
  ' ',
  '     context line three',
  '\\ No newline at end of file',
  '@@ -40,2 +42,3 @@',
  '-    old tail',
  '+    new tail',
  '+    extra tail'
].join('\n');

describe('parseHunkHeader', () => {
  it('reads starts and counts from a standard header', () => {
    expect(parseHunkHeader('@@ -16,0 +16,8 @@ JobChangesController.GetChanges')).toEqual({
      oldStart: 16,
      oldCount: 0,
      newStart: 16,
      newCount: 8
    });
  });

  it('treats an omitted count as exactly one line, not zero', () => {
    // `@@ -5 +5 @@` is legal and means one line on each side. Reading it as zero would freeze the
    // gutters and print `5` beside every line of the hunk.
    expect(parseHunkHeader('@@ -5 +5 @@')).toEqual({
      oldStart: 5,
      oldCount: 1,
      newStart: 5,
      newCount: 1
    });
  });

  it('returns null on a malformed header instead of throwing', () => {
    expect(parseHunkHeader('@@ not a header')).toBeNull();
    expect(parseHunkHeader('')).toBeNull();
  });

  it('refuses a combined merge header (`@@@`) rather than guessing an old side', () => {
    // A combined diff has N preimages and one postimage, so there is no single old-side number to
    // report. Returning null makes the parser leave that hunk's gutters blank; parsing it as if it
    // were two-sided would print `1..5` beside lines that were never at those positions.
    expect(parseHunkHeader('@@@ -1,5 -1,5 +1,6 @@@')).toBeNull();
  });
});

describe('parseDiffGitPath', () => {
  it('takes the postimage, which is the new name for a rename', () => {
    expect(parseDiffGitPath('diff --git a/old/Name.cs b/new/Name.cs')).toBe('new/Name.cs');
  });

  it('unwraps the quoted form git uses for any path containing a space', () => {
    // Unquoted, `a/old b/x b/new b/x` is genuinely ambiguous to a split — which is exactly why git
    // quotes it. The quoted branch is therefore exact rather than heuristic, and this is the rename
    // with spaces that a naive first/last-indexOf split gets wrong in both directions.
    expect(parseDiffGitPath('diff --git "a/old b/x.txt" "b/new dir/x.txt"')).toBe('new dir/x.txt');
  });

  it('takes the same-path rename on both sides without being fooled by an interior " b/"', () => {
    expect(parseDiffGitPath('diff --git a/src/A.cs b/src/A.cs')).toBe('src/A.cs');
  });

  it('falls back to the raw pair when no separator is recognisable', () => {
    expect(parseDiffGitPath('diff --git weird')).toBe('weird');
    expect(parseDiffGitPath('--- a/x')).toBeNull();
  });
});

describe('parseUnifiedDiff — line classification', () => {
  const parsed = parseUnifiedDiff(twoSidedPatch);

  it('counts only the hunk body, so a `---` file header is not one deletion', () => {
    // The fixture carries 2 added + 1 deleted lines in hunk one and 2 + 1 in hunk two. The `--- a/…`
    // and `+++ b/…` file headers are deliberately inside that arithmetic's reach — a prefix-only parser
    // would read them as one deletion and one addition and overstate every count on the row.
    expect(parsed.additions).toBe(4);
    expect(parsed.deletions).toBe(2);
    expect(parsed.hunks).toBe(2);
  });

  it('labels the file and metadata lines distinctly from hunk content', () => {
    const kinds = parsed.lines.map((l) => l.kind);
    expect(kinds[0]).toBe('file');
    expect(kinds[1]).toBe('meta'); // index 83db48f..
    expect(kinds[2]).toBe('meta'); // --- a/src/A.cs
    expect(kinds[3]).toBe('meta'); // +++ b/src/A.cs
    expect(kinds[4]).toBe('hunk');
    expect(kinds[5]).toBe('ctx');
    expect(kinds[6]).toBe('del');
    expect(kinds[7]).toBe('add');
  });

  it('keeps a deletion whose text itself starts with a minus inside the hunk', () => {
    const minus = parseUnifiedDiff(['@@ -1,1 +1,1 @@', '--minus-in-text', '+plus-in-text'].join('\n'));
    expect(minus.deletions).toBe(1);
    expect(minus.lines[1].kind).toBe('del');
    expect(minus.lines[1].text).toBe('-minus-in-text');
  });

  it('marks the no-newline marker as metadata attributed to neither side', () => {
    const marker = parsed.lines.find((l) => l.text.startsWith('\\'));
    expect(marker?.kind).toBe('meta');
    expect(marker?.oldLine).toBeNull();
    expect(marker?.newLine).toBeNull();
  });

  it('strips exactly one prefix character from hunk content', () => {
    const added = parsed.lines.find((l) => l.kind === 'add');
    expect(added?.text).toBe('    var added = true;');
    const context = parsed.lines.find((l) => l.kind === 'ctx');
    expect(context?.text).toBe('    context line one');
  });
});

describe('parseUnifiedDiff — gutters', () => {
  it('advances old and new numbers independently from the hunk header', () => {
    const parsed = parseUnifiedDiff(twoSidedPatch);
    const first = parsed.lines.find((l) => l.kind === 'del');
    const firstAdd = parsed.lines.find((l) => l.kind === 'add');

    // @@ -10,7 +10,9: the first context line is 10/10, the deletion sits on old 11, the addition on new 11.
    expect(first?.oldLine).toBe(11);
    expect(first?.newLine).toBeNull();
    expect(firstAdd?.newLine).toBe(11);
    expect(firstAdd?.oldLine).toBeNull();
  });

  it('restarts both gutters at the second hunk from its own header', () => {
    const parsed = parseUnifiedDiff(twoSidedPatch);
    const second = parsed.lines.filter((l) => l.kind === 'hunk');
    expect(second).toHaveLength(2);

    const afterSecond = parsed.lines.slice(second[1].id + 1);
    expect(afterSecond[0].oldLine).toBe(40);
    expect(afterSecond[0].kind).toBe('del');
    expect(afterSecond[1].newLine).toBe(42);
  });

  it('gives a pure insertion a null old gutter and a numbered new one', () => {
    const parsed = parseUnifiedDiff([
      '--- /dev/null',
      '+++ b/new.txt',
      '@@ -0,0 +1,2 @@',
      '+first',
      '+second'
    ].join('\n'));

    expect(parsed.additions).toBe(2);
    expect(parsed.deletions).toBe(0);
    expect(parsed.lines.filter((l) => l.kind === 'add').map((l) => [l.oldLine, l.newLine])).toEqual([
      [null, 1],
      [null, 2]
    ]);
  });

  it('handles a zero-length new side (a pure deletion)', () => {
    const parsed = parseUnifiedDiff([
      '@@ -3,2 +2,0 @@',
      '-gone one',
      '-gone two'
    ].join('\n'));
    expect(parsed.deletions).toBe(2);
    expect(parsed.lines.filter((l) => l.kind === 'del').map((l) => l.oldLine)).toEqual([3, 4]);
  });

  it('advances both gutters for a context line and for a bare blank line', () => {
    // git emits a truly empty line (no leading space) for an empty context line; a parser that treated
    // it as "outside the hunk" would lose the gutter for everything below it.
    const parsed = parseUnifiedDiff(['@@ -1,3 +1,3 @@', ' a', '', ' c'].join('\n'));
    const context = parsed.lines.filter((l) => l.kind === 'ctx');
    expect(context.map((l) => [l.oldLine, l.newLine])).toEqual([[1, 1], [2, 2], [3, 3]]);
  });
});

describe('parseUnifiedDiff — degenerate bodies', () => {
  it('reports an empty body as empty rather than throwing', () => {
    expect(parseUnifiedDiff('').empty).toBe(true);
    expect(parseUnifiedDiff(null).empty).toBe(true);
    expect(parseUnifiedDiff(undefined).empty).toBe(true);
    expect(parseUnifiedDiff('   \n  ').empty).toBe(true);
    expect(parseUnifiedDiff('').lines).toEqual([]);
  });

  it('flags a binary body', () => {
    const parsed = parseUnifiedDiff([
      'diff --git a/x.png b/x.png',
      'index 3a4f1c2..9b8d7e6 100644',
      'Binary files a/x.png and b/x.png differ'
    ].join('\n'));

    expect(parsed.binary).toBe(true);
    expect(parsed.additions).toBe(0);
    expect(parsed.deletions).toBe(0);
  });

  it('collects the paths of every file in a whole-branch body, in order', () => {
    const parsed = parseUnifiedDiff([
      'diff --git a/one.cs b/one.cs',
      'index 1..2 100644',
      '--- a/one.cs',
      '+++ b/one.cs',
      '@@ -1 +1 @@',
      '-a',
      '+b',
      'diff --git a/two.cs b/two.cs',
      'index 3..4 100644',
      '--- a/two.cs',
      '+++ b/two.cs',
      '@@ -1 +1 @@',
      '-c',
      '+d'
    ].join('\n'));

    expect(parsed.paths).toEqual(['one.cs', 'two.cs']);
    expect(parsed.hunks).toBe(2);
    expect(parsed.additions).toBe(2);
  });

  it('survives a body cut mid-line by the server byte cap and marks it as truncated', () => {
    // The panel must show what arrived and say the rest is missing, not blank the pane.
    const cut = twoSidedPatch.slice(0, twoSidedPatch.length - 12);
    const parsed = parseUnifiedDiff(cut);
    expect(parsed.lines.length).toBeGreaterThan(5);
    expect(looksTruncated(parsed)).toBe(true);
  });

  it('keeps classification sane when a hunk header is missing entirely', () => {
    const parsed = parseUnifiedDiff(['+++ b/x', '@@ -1,1 +1,1 @@', '-old', '+new'].join('\n'));
    expect(parsed.lines[0].kind).toBe('meta');
    expect(parsed.deletions).toBe(1);
    expect(parsed.additions).toBe(1);
  });

  it('treats prose from a fallback source as plain text, not as a broken diff', () => {
    const parsed = parseUnifiedDiff('# (no textual patch)');
    expect(parsed.lines[0].kind).toBe('plain');
    expect(parsed.hunks).toBe(0);
  });

  it('normalises CRLF line endings out of the gutter and the text', () => {
    const parsed = parseUnifiedDiff('@@ -1,1 +1,1 @@\r\n-a\r\n+b\r\n');
    expect(parsed.lines[1].text).toBe('a');
    expect(parsed.lines[2].text).toBe('b');
  });
});
