import type { Sheet, Task } from './types';
import type { Workspace } from './workspace';

/**
 * Search (Ctrl+F, or the search button on a phone): what matches. Tack for Windows has the same
 * rule (Model/Search.cs), held to the same cases (tests/shared/search.json).
 *
 * A query matches any task, finished task or note containing it, anywhere in its text (all of a
 * note, not just its title), in every space, whatever its case or accents. Text and query are
 * both folded first, one character at a time:
 * - a CR LF pair is one line break, and every line break, tab and other space is a plain space;
 * - curly quotes are straight ones, dashes and the minus sign a hyphen, an ellipsis three dots;
 * - soft hyphens and zero-width characters are left out;
 * - ß is ss, æ ae and œ oe; ø ł đ ħ ŧ and ı are their plain letters;
 * - anything else is decomposed (NFD), its accents (Mn) dropped, and lower-cased (ς as σ).
 * The query then loses the spaces at either end. A match maps back to the text as typed
 * (`originalRange`), so a note can open with it selected.
 */

/** Characters folded to something of their own (anything else: NFD, no accents, lower case). */
const SPECIAL = new Map<number, string>();
const put = (to: string, ...codes: number[]) => codes.forEach((c) => SPECIAL.set(c, to));
put(' ', 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000);
put('', 0xad, 0x200b, 0x200c, 0x200d, 0x2060, 0xfeff);
put("'", 0x2018, 0x2019, 0x201a, 0x201b, 0x2032);
put('"', 0x201c, 0x201d, 0x201e, 0x201f, 0x2033);
put('-', 0x2010, 0x2011, 0x2012, 0x2013, 0x2014, 0x2015, 0x2212);
put('...', 0x2026);
put('ss', 0xdf, 0x1e9e);
put('ae', 0xe6, 0xc6);
put('oe', 0x153, 0x152);
put('o', 0xf8, 0xd8);
put('l', 0x142, 0x141);
put('d', 0x111, 0x110);
put('h', 0x127, 0x126);
put('t', 0x167, 0x166);
put('i', 0x131);

const MARK = /^\p{Mn}$/u;

/** Each ASCII character folded: capitals lower-cased, tab and line breaks (and VT, FF) a space. */
const ASCII = Array.from({ length: 128 }, (_, c) => (c >= 0x41 && c <= 0x5a ? String.fromCharCode(c + 32) : c >= 0x09 && c <= 0x0d ? ' ' : String.fromCharCode(c)));

const memo = new Map<string, string>();

/** One character (a code point; a lone surrogate stays as it is) folded. */
function foldChar(ch: string): string {
  const c = ch.charCodeAt(0);
  if (ch.length === 1 && c < 0x80) return ASCII[c]!;
  let folded = memo.get(ch);
  if (folded === undefined) {
    folded = foldOther(ch);
    if (memo.size < 65_536) memo.set(ch, folded);
  }
  return folded;
}

function foldOther(ch: string): string {
  const special = SPECIAL.get(ch.codePointAt(0)!);
  if (special !== undefined) return special;
  const c = ch.charCodeAt(0);
  if (ch.length === 1 && c >= 0xd800 && c <= 0xdfff) return ch;
  let out = '';
  for (const part of ch.normalize('NFD')) {
    const p = part.codePointAt(0)!;
    if (p < 0x80) {
      out += ASCII[p];
      continue;
    }
    if (MARK.test(part)) continue;
    const s = SPECIAL.get(p);
    if (s !== undefined) {
      out += s;
      continue;
    }
    const lower = part.toLowerCase();
    out += lower === 'ς' ? 'σ' : lower;
  }
  return out;
}

/** Printable ASCII without capitals folds to itself: most text, at no cost. */
const PLAIN = /^[\x20-\x40\x5b-\x7e]*$/;

/** What changes when folding: CR LF, capitals and control spaces, anything not ASCII (a code point at a time). */
const FOLDS = /\r\n|[A-Z\t-\r]|[^\x00-\x7f]/gu;

/** What can make folded text longer or shorter than the text: anything not ASCII, and CR LF. */
const RESIZES = /[^\x00-\x7f]|\r\n/;

/** The text as search compares it (see above). */
export function fold(text: string): string {
  if (PLAIN.test(text)) return text;
  return text.replace(FOLDS, (m) => (m === '\r\n' ? ' ' : foldChar(m)));
}

/** A query as search looks for it: folded, without the spaces at either end ('' finds nothing). */
export function foldQuery(query: string): string {
  return fold(query).replace(/^ +| +$/g, '');
}

/** Where a query (as `foldQuery` gives it) first matches in folded text, or -1. */
export function matchAt(folded: string, query: string): number {
  return query ? folded.indexOf(query) : -1;
}

/**
 * The part of `text` (as typed) that folds to [start, end) of `fold(text)`: whole characters,
 * with the accents that follow the last one. Folding is done again only up to the match.
 */
export function originalRange(text: string, start: number, end: number): [number, number] {
  // ASCII without CR LF folds character for character.
  if (!RESIZES.test(text)) return [Math.min(start, text.length), Math.min(end, text.length)];
  let at = 0; // folded length so far
  let from = -1;
  for (let i = 0; i < text.length; ) {
    const c = text.charCodeAt(i);
    let n = 1;
    let len = 1;
    if (c === 13 && text.charCodeAt(i + 1) === 10) {
      n = 2;
    } else if (c >= 0x80) {
      if (c >= 0xd800 && c <= 0xdbff && (text.charCodeAt(i + 1) & 0xfc00) === 0xdc00) n = 2;
      len = foldChar(text.slice(i, i + n)).length;
    }
    if (len > 0) {
      if (from >= 0 && at >= end) return [from, i];
      if (from < 0 && at + len > start) from = i;
    }
    at += len;
    i += n;
  }
  return [from < 0 ? text.length : from, text.length];
}

/** Where in `text` (as typed) a query first matches, or null. */
export function findIn(text: string, query: string): [number, number] | null {
  const q = foldQuery(query);
  const at = matchAt(fold(text), q);
  return at < 0 ? null : originalRange(text, at, at + q.length);
}

// ------------------------------------------------------------------ the workspace

export type HitKind = 'task' | 'done' | 'note';

/** One task or sheet that matches: `at` is where (in its folded text) the query first does. */
export interface Hit {
  kind: HitKind;
  id: string;
  at: number;
}

export interface Results {
  /** The query as matched (folded); '' when there's nothing to look for. */
  query: string;
  /** The task in focus, then the open list in its order. */
  tasks: Hit[];
  /** Finished tasks, most recent first. */
  done: Hit[];
  /** Sheets, in order. */
  notes: Hit[];
  total: number;
}

/**
 * Searches a workspace: every space, open and finished tasks and notes. Folded text is kept per
 * task and sheet until its text changes, so typing a query (even into ten thousand tasks and
 * hundreds of long notes) only compares.
 */
export class SearchIndex {
  private folded = new Map<string, { text: string; folded: string }>();

  find(ws: Workspace, raw: string): Results {
    const query = foldQuery(raw);
    const results: Results = { query, tasks: [], done: [], notes: [], total: 0 };
    if (!query) return results;
    const kept = new Map<string, { text: string; folded: string }>();
    const look = (kind: HitKind, item: Task | Sheet, into: Hit[]) => {
      const key = `${kind === 'note' ? 's' : 't'}:${item.id}`;
      let entry = this.folded.get(key);
      if (!entry || entry.text !== item.text) entry = { text: item.text, folded: fold(item.text) };
      kept.set(key, entry);
      const at = matchAt(entry.folded, query);
      if (at >= 0) into.push({ kind, id: item.id, at });
    };
    const now = ws.now;
    if (now) look('task', now, results.tasks);
    for (const t of ws.open) look('task', t, results.tasks);
    for (const t of ws.done) look('done', t, results.done);
    for (const s of ws.sheetList) look('note', s, results.notes);
    // What's gone (deleted, or never asked about again) is let go.
    this.folded = kept;
    results.total = results.tasks.length + results.done.length + results.notes.length;
    return results;
  }
}
