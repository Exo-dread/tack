/**
 * Markdown in Notes (docs/NOTES-MARKDOWN.md): how a note's text is shown while you read it. The
 * text itself never changes; this only says what it looks like. Line-based and CommonMark-flavoured,
 * with no HTML, images or tables. Tack for Windows has a port (src/Tack/Model/Markdown.cs) that must
 * give exactly the same tree for every case in tests/vectors/markdown.json.
 *
 * Linear time: every line is looked at a bounded number of times (quotes nest at most
 * MAX_QUOTE_DEPTH deep, lists MAX_LIST_DEPTH), inline delimiters are matched with the usual
 * delimiter stack and per-character bottoms, code spans find their closer through an index of
 * backtick runs and links theirs through a table of next ')': no regex, no backtracking.
 */

export type Inline =
  | string
  | { b: Inline[] }
  | { i: Inline[] }
  | { s: Inline[] }
  | { code: string }
  | { link: Inline[]; url: string }
  | { br: 1 };

export interface Item {
  c: Inline[];
  /** null: not a task; false: "[ ]"; true: "[x]" or "[X]". */
  task: boolean | null;
  /** UTF-16 index in the note's text of the character between [ and ] (null if not a task). */
  at: number | null;
  sub: Block[];
  /** With `lines`: the line index of each of the item's own lines (one per piece of `c` between breaks). */
  lns?: number[];
}

export type Block =
  | { h: 1 | 2 | 3; c: Inline[]; ln?: number }
  | { p: Inline[]; lns?: number[] }
  | { ul: Item[] }
  | { ol: Item[]; start: number }
  | { quote: Block[] }
  | { code: string; ln?: number }
  | { hr: 1; ln?: number };

/** Inline formatting (bold, italic, struck, links) nests at most this deep. */
export const MAX_INLINE_DEPTH = 8;
/** Lists nest at most this deep: an item indented further joins the deepest list. */
export const MAX_LIST_DEPTH = 8;
/** Quotes nest at most this deep: past it, `>` is plain text. */
export const MAX_QUOTE_DEPTH = 8;
/** A note longer than this is never shown formatted (it stays the editor). */
export const MAX_FORMATTED = 200_000;

/** Whether a note is shown formatted at all (Format notes on): not when empty, nor past MAX_FORMATTED. */
export function canFormat(text: string): boolean {
  return text.length > 0 && text.length <= MAX_FORMATTED;
}

export interface ParseOptions {
  /**
   * Also say which line of the note each line shown came from (`ln` on headings, code blocks and
   * rules, where code's is its opening fence; `lns` on paragraphs and items): the formatted view
   * uses it to put the caret on the line you clicked. Not part of the shared cases.
   */
  lines?: boolean;
}

/** A line of the note, or of a quote's content: its text is s[a, b). */
interface Line {
  a: number;
  b: number;
  ln: number;
}

const SP = 32;
const TAB = 9;
const isWs = (c: number) => c === SP || c === TAB;

export function parse(text: string, options: ParseOptions = {}): Block[] {
  return new BlockParser(text, options.lines === true).run();
}

/** Where each line of `text` starts (\r\n, \r and \n all end a line). */
export function lineStarts(text: string): number[] {
  const starts = [0];
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c === 10) starts.push(i + 1);
    else if (c === 13) {
      if (text.charCodeAt(i + 1) === 10) i++;
      starts.push(i + 1);
    }
  }
  return starts;
}

interface Marker {
  kind: 'ul' | 'ol';
  start: number;
  /** The marker's column (its indent, a tab counting 4). */
  w: number;
  /** The column the item's text starts at. */
  textCol: number;
  /** Where the item's text starts (after a task's box), and its box. */
  textP: number;
  task: boolean | null;
  at: number | null;
}

interface OpenList {
  kind: 'ul' | 'ol';
  items: Item[];
  /** The last item, still open: its marker's column and its text's. */
  ind: number;
  textCol: number;
}

class BlockParser {
  constructor(
    private readonly s: string,
    private readonly withLines: boolean,
  ) {}

  run(): Block[] {
    const s = this.s;
    const lines: Line[] = [];
    let a = 0;
    let ln = 0;
    for (let i = 0; i < s.length; i++) {
      const c = s.charCodeAt(i);
      if (c === 10 || c === 13) {
        lines.push({ a, b: i, ln: ln++ });
        if (c === 13 && s.charCodeAt(i + 1) === 10) i++;
        a = i + 1;
      }
    }
    lines.push({ a, b: s.length, ln });
    return this.blocks(lines, 0);
  }

  /** The first non-space of a line, and its column (a tab counts 4). */
  private indent(l: Line): { p: number; w: number } {
    const s = this.s;
    let p = l.a;
    let w = 0;
    while (p < l.b) {
      const c = s.charCodeAt(p);
      if (c === SP) w += 1;
      else if (c === TAB) w += 4;
      else break;
      p++;
    }
    return { p, w };
  }

  private blocks(lines: Line[], quoteDepth: number): Block[] {
    const out: Block[] = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i]!;
      const { p } = this.indent(line);
      if (p === line.b) {
        i++;
        continue;
      }
      const fence = this.fence(line, p);
      if (fence) {
        i = this.code(lines, i, fence, out);
        continue;
      }
      if (this.isQuote(line, p, quoteDepth)) {
        const inner: Line[] = [];
        while (i < lines.length) {
          const l = lines[i]!;
          const q = this.indent(l).p;
          if (!this.isQuote(l, q, quoteDepth)) break;
          const from = q + 1 < l.b && this.s.charCodeAt(q + 1) === SP ? q + 2 : q + 1;
          inner.push({ a: from, b: l.b, ln: l.ln });
          i++;
        }
        out.push({ quote: this.blocks(inner, quoteDepth + 1) });
        continue;
      }
      const heading = this.heading(line, p);
      if (heading) {
        out.push(heading);
        i++;
        continue;
      }
      if (this.isRule(line, p)) {
        out.push(this.withLines ? { hr: 1, ln: line.ln } : { hr: 1 });
        i++;
        continue;
      }
      if (this.marker(line)) {
        i = this.list(lines, i, out);
        continue;
      }
      // A paragraph: every line until a blank one or another block, each line break kept.
      const c: Inline[] = [];
      const lns: number[] = [];
      while (i < lines.length) {
        const l = lines[i]!;
        const q = this.indent(l).p;
        if (q === l.b) break;
        if (lns.length > 0 && this.startsBlock(l, q, quoteDepth)) break;
        if (lns.length > 0) c.push({ br: 1 });
        pushAll(c, inline(this.s, q, trimEnd(this.s, q, l.b)));
        lns.push(l.ln);
        i++;
      }
      out.push(this.withLines ? { p: merge(c), lns } : { p: merge(c) });
    }
    return out;
  }

  private startsBlock(l: Line, p: number, quoteDepth: number): boolean {
    return !!this.fence(l, p) || this.isQuote(l, p, quoteDepth) || !!this.heading(l, p) || this.isRule(l, p) || !!this.marker(l);
  }

  private isQuote(l: Line, p: number, quoteDepth: number): boolean {
    return quoteDepth < MAX_QUOTE_DEPTH && p < l.b && this.s.charCodeAt(p) === 62; // >
  }

  /** A fence's character and length, if the line opens a code block. */
  private fence(l: Line, p: number): { ch: number; n: number } | null {
    const s = this.s;
    const ch = s.charCodeAt(p);
    if (ch !== 96 && ch !== 126) return null; // ` ~
    let q = p;
    while (q < l.b && s.charCodeAt(q) === ch) q++;
    const n = q - p;
    if (n < 3) return null;
    // A backtick fence's language word can't hold a backtick (``` a ``` is inline code).
    if (ch === 96) for (let k = q; k < l.b; k++) if (s.charCodeAt(k) === 96) return null;
    return { ch, n };
  }

  private code(lines: Line[], i: number, fence: { ch: number; n: number }, out: Block[]): number {
    const s = this.s;
    const ln = lines[i]!.ln;
    const parts: string[] = [];
    i++;
    while (i < lines.length) {
      const l = lines[i]!;
      const { p } = this.indent(l);
      let q = p;
      while (q < l.b && s.charCodeAt(q) === fence.ch) q++;
      if (q - p >= fence.n && trimEnd(s, q, l.b) === q) {
        i++;
        break;
      }
      parts.push(s.slice(l.a, l.b));
      i++;
    }
    out.push(this.withLines ? { code: parts.join('\n'), ln } : { code: parts.join('\n') });
    return i;
  }

  private heading(l: Line, p: number): Block | null {
    const s = this.s;
    let q = p;
    while (q < l.b && s.charCodeAt(q) === 35) q++; // #
    const n = q - p;
    if (n < 1 || n > 6 || q >= l.b || !isWs(s.charCodeAt(q))) return null;
    let end = trimEnd(s, q, l.b);
    // A closing run of # goes, if a space comes before it (or it's all there is).
    let k = end;
    while (k > q && s.charCodeAt(k - 1) === 35) k--;
    if (k < end && (k === q || isWs(s.charCodeAt(k - 1)))) end = trimEnd(s, q, k);
    const level = Math.min(n, 3) as 1 | 2 | 3;
    const start = trimStart(s, q, end);
    const c = merge(inline(s, start, end));
    return this.withLines ? { h: level, c, ln: l.ln } : { h: level, c };
  }

  /** 3+ of the same -, * or _ with nothing else but spaces. */
  private isRule(l: Line, p: number): boolean {
    const s = this.s;
    const ch = s.charCodeAt(p);
    if (ch !== 45 && ch !== 42 && ch !== 95) return false;
    let n = 0;
    for (let k = p; k < l.b; k++) {
      const c = s.charCodeAt(k);
      if (c === ch) n++;
      else if (!isWs(c)) return false;
    }
    return n >= 3;
  }

  /** A list item's marker (and box, for a task), if the line is an item. */
  private marker(l: Line): Marker | null {
    const s = this.s;
    const { p, w } = this.indent(l);
    if (p >= l.b || this.isRule(l, p)) return null;
    const c = s.charCodeAt(p);
    let kind: 'ul' | 'ol';
    let start = 0;
    let q: number; // just after the marker
    if (c === 45 || c === 42 || c === 43) {
      kind = 'ul';
      q = p + 1;
    } else if (c >= 48 && c <= 57) {
      q = p;
      while (q < l.b && q - p < 10 && s.charCodeAt(q) >= 48 && s.charCodeAt(q) <= 57) q++;
      const digits = q - p;
      const d = s.charCodeAt(q);
      if (digits > 9 || (d !== 46 && d !== 41)) return null; // . )
      kind = 'ol';
      start = Number(s.slice(p, q));
      q++;
    } else {
      return null;
    }
    if (q >= l.b || s.charCodeAt(q) !== SP) return null;
    let col = w + (q - p) + 1;
    let t = q + 1;
    while (t < l.b) {
      const ch = s.charCodeAt(t);
      if (ch === SP) col += 1;
      else if (ch === TAB) col += 4;
      else break;
      t++;
    }
    let task: boolean | null = null;
    let at: number | null = null;
    let textP = t;
    if (t + 2 < l.b && s.charCodeAt(t) === 91 && s.charCodeAt(t + 2) === 93) {
      const box = s.charCodeAt(t + 1);
      const after = t + 3;
      if ((box === SP || box === 120 || box === 88) && (after === l.b || s.charCodeAt(after) === SP)) {
        task = box !== SP;
        at = t + 1;
        textP = trimStart(s, after, l.b);
      }
    }
    return { kind, start, w, textCol: col, textP, task, at };
  }

  /** A list and everything nested in it, from lines[i] (an item). Returns the next line to read. */
  private list(lines: Line[], i: number, out: Block[]): number {
    const s = this.s;
    const first = this.marker(lines[i]!)!;
    const stack: OpenList[] = [];
    const newItem = (m: Marker, l: Line): Item => {
      const item: Item = { c: merge(inline(s, m.textP, trimEnd(s, m.textP, l.b))), task: m.task, at: m.at, sub: [] };
      if (this.withLines) item.lns = [l.ln];
      return item;
    };
    const open = (m: Marker, l: Line, into: Block[]) => {
      const items: Item[] = [newItem(m, l)];
      into.push(m.kind === 'ul' ? { ul: items } : { ol: items, start: m.start });
      stack.push({ kind: m.kind, items, ind: m.w, textCol: m.textCol });
    };
    open(first, lines[i]!, out);
    i++;
    while (i < lines.length) {
      const l = lines[i]!;
      const { p, w } = this.indent(l);
      if (p === l.b) {
        i++; // a blank line ends nothing by itself
        continue;
      }
      const m = this.marker(l);
      if (m) {
        // Down the open lists while the marker is 2+ columns right of that level's last item's.
        let k = 0;
        while (k + 1 < stack.length && m.w >= stack[k]!.ind + 2) k++;
        const deeper = m.w >= stack[k]!.ind + 2 && k === stack.length - 1;
        if (deeper && stack.length < MAX_LIST_DEPTH) {
          const parent = stack[k]!;
          open(m, l, parent.items[parent.items.length - 1]!.sub);
          i++;
          continue;
        }
        stack.length = k + 1;
        const level = stack[k]!;
        if (level.kind === m.kind) {
          level.items.push(newItem(m, l));
          level.ind = m.w;
          level.textCol = m.textCol;
        } else if (k === 0) {
          break; // another kind of list starts here
        } else {
          stack.length = k;
          const parent = stack[k - 1]!;
          open(m, l, parent.items[parent.items.length - 1]!.sub);
        }
        i++;
        continue;
      }
      // Not an item: it carries on the deepest open item whose text it's indented to, or ends the list.
      let k = stack.length - 1;
      while (k >= 0 && w < stack[k]!.textCol) k--;
      if (k < 0) break;
      stack.length = k + 1;
      const level = stack[k]!;
      const item = level.items[level.items.length - 1]!;
      const content = inline(s, p, trimEnd(s, p, l.b));
      // (A break comes between, so no two text pieces meet: nothing to merge.)
      if (item.sub.length === 0) {
        item.c.push({ br: 1 });
        pushAll(item.c, content);
        item.lns?.push(l.ln);
      } else {
        const last = item.sub[item.sub.length - 1]!;
        if ('p' in last) {
          last.p.push({ br: 1 });
          pushAll(last.p, content);
          last.lns?.push(l.ln);
        } else {
          item.sub.push(this.withLines ? { p: merge(content), lns: [l.ln] } : { p: merge(content) });
        }
      }
      i++;
    }
    return i;
  }
}

function trimEnd(s: string, a: number, b: number): number {
  while (b > a && isWs(s.charCodeAt(b - 1))) b--;
  return b;
}

function trimStart(s: string, a: number, b: number): number {
  while (a < b && isWs(s.charCodeAt(a))) a++;
  return a;
}

function pushAll<T>(to: T[], from: T[]): void {
  for (const x of from) to.push(x);
}

/** Adjacent text pieces as one string. */
function merge(xs: Inline[]): Inline[] {
  const out: Inline[] = [];
  for (const x of xs) {
    const last = out.length - 1;
    if (typeof x === 'string') {
      if (!x) continue;
      if (last >= 0 && typeof out[last] === 'string') out[last] = (out[last] as string) + x;
      else out.push(x);
    } else {
      out.push(x);
    }
  }
  return out;
}

// ====================================================================== inline

/** A piece of the line being read: text (a delimiter run's is its characters) or a finished element. */
interface Node {
  v: string | Inline;
  /** How deep the formatting in it nests (text: 0). */
  depth: number;
  prev: Node | null;
  next: Node | null;
}

interface Delim {
  node: Node;
  ch: number;
  n: number;
  open: boolean;
  close: boolean;
  prev: Delim | null;
  next: Delim | null;
}

interface Bracket {
  node: Node;
  /** The delimiter on top when it opened: emphasis inside the link stops there. */
  bottom: Delim | null;
  seq: number;
  prev: Bracket | null;
}

/** ASCII punctuation: what a backslash makes literal. */
function isPunct(c: number): boolean {
  return (c >= 33 && c <= 47) || (c >= 58 && c <= 64) || (c >= 91 && c <= 96) || (c >= 123 && c <= 126);
}

/** Letters and digits, for `_` (snake_case stays plain): ASCII ones, and anything past ASCII but a space. */
function isWord(c: number): boolean {
  return (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || (c >= 128 && c !== 0xa0 && c !== 0x3000);
}

const STAR = 42;
const UNDER = 95;
const TILDE = 126;
const TICK = 96;

/** The inline content of s[a, b) (one line). */
export function inline(s: string, a: number, b: number): Inline[] {
  return new InlineParser(s, a, b).run();
}

class InlineParser {
  private head: Node | null = null;
  private tail: Node | null = null;
  private delims: Delim | null = null; // top of the delimiter stack
  private brackets: Bracket | null = null;
  private seq = 0;
  /** Brackets opened before this one can't make links (a link holds no link). */
  private linkedBelow = -1;
  /** Backtick runs by length: where each starts, and how far through them a search has got. */
  private runs: Map<number, { at: number[]; next: number }> | null = null;
  /** For each index, the next ')' at or after it (-1: none). Made on first use. */
  private parens: Int32Array | null = null;

  constructor(
    private readonly s: string,
    private readonly a: number,
    private readonly b: number,
  ) {}

  run(): Inline[] {
    const { s, b } = this;
    let i = this.a;
    let textFrom = i;
    const flush = (to: number) => {
      if (to > textFrom) this.append(s.slice(textFrom, to));
    };
    while (i < b) {
      const c = s.charCodeAt(i);
      if (c === 92) {
        // \ before ASCII punctuation: that character, literally.
        if (i + 1 < b && isPunct(s.charCodeAt(i + 1))) {
          flush(i);
          this.append(s[i + 1]!);
          i += 2;
          textFrom = i;
        } else {
          i++;
        }
      } else if (c === TICK) {
        flush(i);
        i = this.codeSpan(i);
        textFrom = i;
      } else if (c === STAR || c === UNDER || c === TILDE) {
        flush(i);
        i = this.delimRun(i, c);
        textFrom = i;
      } else if (c === 91) {
        flush(i);
        const node = this.append('[');
        this.brackets = { node, bottom: this.delims, seq: this.seq++, prev: this.brackets };
        i++;
        textFrom = i;
      } else if (c === 93) {
        flush(i);
        i = this.closeBracket(i);
        textFrom = i;
      } else {
        i++;
      }
    }
    flush(b);
    this.emphasis(null);
    return this.collect(this.head, null);
  }

  private append(v: string | Inline, depth = 0): Node {
    const node: Node = { v, depth, prev: this.tail, next: null };
    if (this.tail) this.tail.next = node;
    else this.head = node;
    this.tail = node;
    return node;
  }

  private codeSpan(i: number): number {
    const { s, b } = this;
    let q = i;
    while (q < b && s.charCodeAt(q) === TICK) q++;
    const n = q - i;
    const close = this.nextRun(n, q);
    if (close < 0) {
      this.append(s.slice(i, q));
      return q;
    }
    let content = s.slice(q, close);
    if (content.length >= 2 && content.charCodeAt(0) === SP && content.charCodeAt(content.length - 1) === SP) {
      let allSpace = true;
      for (let k = 0; k < content.length; k++) if (content.charCodeAt(k) !== SP) allSpace = false;
      if (!allSpace) content = content.slice(1, -1);
    }
    this.append({ code: content });
    return close + n;
  }

  /** Where the next run of exactly n backticks starts at or after `from` (-1: none). */
  private nextRun(n: number, from: number): number {
    if (!this.runs) {
      this.runs = new Map();
      const { s, b } = this;
      let k = this.a;
      while (k < b) {
        if (s.charCodeAt(k) !== TICK) {
          k++;
          continue;
        }
        let e = k;
        while (e < b && s.charCodeAt(e) === TICK) e++;
        let r = this.runs.get(e - k);
        if (!r) this.runs.set(e - k, (r = { at: [], next: 0 }));
        r.at.push(k);
        k = e;
      }
    }
    const r = this.runs.get(n);
    if (!r) return -1;
    while (r.next < r.at.length && r.at[r.next]! < from) r.next++;
    return r.next < r.at.length ? r.at[r.next]! : -1;
  }

  private delimRun(i: number, c: number): number {
    const { s, a, b } = this;
    let q = i;
    while (q < b && s.charCodeAt(q) === c) q++;
    const n = q - i;
    const node = this.append(s.slice(i, q));
    if (c === TILDE && n !== 2) return q;
    const before = i > a ? s.charCodeAt(i - 1) : -1;
    const after = q < b ? s.charCodeAt(q) : -1;
    let open = after >= 0 && !isWs(after);
    let close = before >= 0 && !isWs(before);
    if (c === UNDER) {
      open &&= !(before >= 0 && isWord(before));
      close &&= !(after >= 0 && isWord(after));
    }
    if (open || close) {
      const d: Delim = { node, ch: c, n, open, close, prev: this.delims, next: null };
      if (this.delims) this.delims.next = d;
      this.delims = d;
    }
    return q;
  }

  private closeBracket(i: number): number {
    const { s, b } = this;
    const opener = this.brackets;
    if (!opener) {
      this.append(']');
      return i + 1;
    }
    this.brackets = opener.prev;
    if (opener.seq < this.linkedBelow || i + 1 >= b || s.charCodeAt(i + 1) !== 40) {
      this.append(']');
      return i + 1;
    }
    const close = this.nextParen(i + 2);
    if (close < 0) {
      this.append(']');
      return i + 1;
    }
    // Emphasis inside the link text first, then the link holds it.
    this.emphasis(opener.bottom);
    let depth = 0;
    for (let n = opener.node.next; n; n = n.next) depth = Math.max(depth, n.depth);
    if (depth + 1 > MAX_INLINE_DEPTH) {
      this.append(']');
      return i + 1;
    }
    const link: Inline = { link: this.collect(opener.node.next, null), url: s.slice(trimStart(s, i + 2, close), trimEnd(s, i + 2, close)) };
    // The '[' node becomes the link; everything after it is inside.
    opener.node.v = link;
    opener.node.depth = depth + 1;
    opener.node.next = null;
    this.tail = opener.node;
    this.linkedBelow = opener.seq;
    return close + 1;
  }

  private nextParen(from: number): number {
    if (!this.parens) {
      const { s, a, b } = this;
      const t = new Int32Array(b - a + 1);
      let next = -1;
      t[b - a] = -1;
      for (let k = b - 1; k >= a; k--) {
        if (s.charCodeAt(k) === 41) next = k;
        t[k - a] = next;
      }
      this.parens = t;
    }
    return from > this.b ? -1 : this.parens[from - this.a]!;
  }

  private removeDelim(d: Delim): void {
    if (d.prev) d.prev.next = d.next;
    if (d.next) d.next.prev = d.prev;
    else this.delims = d.prev;
  }

  private removeNode(n: Node): void {
    if (n.prev) n.prev.next = n.next;
    else this.head = n.next;
    if (n.next) n.next.prev = n.prev;
    else this.tail = n.prev;
  }

  /** Matches the delimiters above `bottom` (CommonMark's "process emphasis", without the rule of 3). */
  private emphasis(bottom: Delim | null): void {
    // The first delimiter above bottom.
    let closer: Delim | null = this.delims;
    if (closer === bottom) return;
    while (closer && closer.prev !== bottom) closer = closer.prev;
    const openersBottom = new Map<number, Delim | null>([
      [STAR, bottom],
      [UNDER, bottom],
      [TILDE, bottom],
    ]);
    while (closer) {
      if (!closer.close) {
        closer = closer.next;
        continue;
      }
      let opener = closer.prev;
      const stop = openersBottom.get(closer.ch)!;
      while (opener && opener !== bottom && opener !== stop && !(opener.ch === closer.ch && opener.open)) opener = opener.prev;
      let matched = false;
      if (opener && opener !== bottom && opener !== stop) {
        const use = closer.ch === TILDE ? 2 : closer.n >= 2 && opener.n >= 2 ? 2 : 1;
        let depth = 0;
        for (let n = opener.node.next; n && n !== closer.node; n = n.next) depth = Math.max(depth, n.depth);
        if (depth + 1 <= MAX_INLINE_DEPTH) {
          matched = true;
          const inner = this.collect(opener.node.next, closer.node);
          const el: Inline = closer.ch === TILDE ? { s: inner } : use === 2 ? { b: inner } : { i: inner };
          const node: Node = { v: el, depth: depth + 1, prev: opener.node, next: closer.node };
          opener.node.next = node;
          closer.node.prev = node;
          // Delimiters between them are plain text now.
          opener.next = closer;
          closer.prev = opener;
          opener.n -= use;
          closer.n -= use;
          opener.node.v = (opener.node.v as string).slice(use);
          closer.node.v = (closer.node.v as string).slice(use);
          if (opener.n === 0) {
            this.removeNode(opener.node);
            this.removeDelim(opener);
          }
          if (closer.n === 0) {
            const next = closer.next;
            this.removeNode(closer.node);
            this.removeDelim(closer);
            closer = next;
          }
        }
      }
      if (!matched) {
        // (Nothing above changed `closer` when there was no match.)
        const unmatched: Delim = closer!;
        openersBottom.set(unmatched.ch, unmatched.prev);
        if (!unmatched.open) this.removeDelim(unmatched);
        closer = unmatched.next;
      }
    }
    // Whatever is left above bottom is plain text.
    while (this.delims && this.delims !== bottom) this.removeDelim(this.delims);
  }

  /** The nodes from `from` up to (not including) `to`, as inline content. */
  private collect(from: Node | null, to: Node | null): Inline[] {
    const out: Inline[] = [];
    for (let n = from; n && n !== to; n = n.next) out.push(n.v);
    return merge(out);
  }
}
