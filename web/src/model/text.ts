/** List markers stripped from pasted lines (same set as Tack for Windows). */
const PASTE_MARKER = /^\s*(?:[-*+•·◦▪–]\s+|\d{1,3}[.)]\s+|\[[ xX]?\]\s*)/;

/** A list line in a note: indent, then a bullet, a checkbox or a number. */
export const LIST_PREFIX = /^(\s*)(?:([-*•])|\[[ xX]\]|(\d{1,3})([.)]))\s+/;

/** Task text is one line: newlines and runs of spaces collapse. */
export function cleanTaskText(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

/**
 * Note text breaks lines with "\n", as a browser's textarea does. Tack for Windows' TextBox
 * writes "\r\n"; stored either way, the same note would look edited on one side.
 */
export function noteText(text: string): string {
  return text.replace(/\r\n?/g, '\n');
}

/**
 * Replaces lone surrogates (half of an emoji, which valid JSON can still spell as "\ud83d") with
 * U+FFFD. Strings read from files go through this, and so does every file Tack writes: other
 * readers, Tack for Windows among them, refuse such text.
 */
export function wellFormed(text: string): string {
  return text.replace(/[\uD800-\uDBFF][\uDC00-\uDFFF]|[\uD800-\uDFFF]/g, (c) => (c.length === 2 ? c : '�'));
}

/** For JSON.parse and JSON.stringify: every string value comes out well-formed. */
export const wellFormedValues = (_key: string, value: unknown): unknown => (typeof value === 'string' ? wellFormed(value) : value);

/** Splits pasted text into task lines, stripping bullets and numbering (twice, for "1. [ ] x"). */
export function pastedLines(text: string): string[] {
  return text
    .replace(/\r/g, '')
    .split('\n')
    .map((line) => cleanTaskText(line.replace(PASTE_MARKER, '').replace(PASTE_MARKER, '')))
    .filter((line) => line.length > 0);
}

/** Lines of a note turned into tasks: list prefixes removed, blanks dropped. */
export function noteLinesToTasks(text: string): string[] {
  return text
    .replace(/\r/g, '')
    .split('\n')
    .map((line) => cleanTaskText(line.replace(LIST_PREFIX, '')))
    .filter((line) => line.length > 0);
}

/** A sheet's title is its first non-empty line, without heading or bullet marks. */
export function sheetTitle(text: string): string {
  for (const raw of text.split('\n')) {
    const line = raw.trim().replace(/^[#\-*•\s]+/, '').trim();
    if (line) return line;
  }
  return 'Untitled';
}

/**
 * What Enter should insert to continue a list on the line ending at `caret`, or
 * { end: true } when the line is an empty item (Enter then ends the list), or null.
 */
export function continueList(
  text: string,
  caret: number,
): { insert: string } | { end: true; lineStart: number; lineLength: number } | null {
  const lineStart = text.lastIndexOf('\n', caret - 1) + 1;
  let lineEnd = text.indexOf('\n', caret);
  if (lineEnd < 0) lineEnd = text.length;
  if (caret !== lineEnd) return null; // only at the end of the line
  const line = text.slice(lineStart, lineEnd);
  const m = LIST_PREFIX.exec(line);
  if (!m) return null;
  if (line.length === m[0].length) return { end: true, lineStart, lineLength: line.length };
  const indent = m[1];
  let marker: string;
  if (m[2]) marker = `${m[2]} `;
  else if (m[3]) marker = `${Number(m[3]) + 1}${m[4]} `;
  else marker = '[ ] ';
  return { insert: `\n${indent}${marker}` };
}

export function shorten(text: string, max = 32): string {
  return text.length > max ? `${text.slice(0, max - 1).trimEnd()}…` : text;
}
