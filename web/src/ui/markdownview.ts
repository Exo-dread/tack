import type { Block, Inline, Item } from '../model/markdown';

/**
 * A note's Markdown tree as page elements (docs/NOTES-MARKDOWN.md). Built from elements and text
 * nodes only: the note's text never becomes markup, a URL to follow or anything to run. A link
 * is a styled span with its url as the tooltip (plain text), never an <a>.
 *
 * Every line on show is an element with `data-ln` (its line in the note), so a click can put the
 * caret on the line it landed on; every checkbox carries `data-at`, the offset of its box.
 *
 * The note's headings sit under the page's own (h1 is the app's title): # is h2, ## h3, ### h4.
 *
 * Blocks come in chunks of about CHUNK_LINES lines, each laid out only once it nears the screen
 * (content-visibility: auto, main.css), so a long note costs little more to show than a short one.
 * A chunk never starts with a heading: margins don't meet across chunks, and a heading's would
 * double the gap above it.
 */
export function renderNote(blocks: Block[]): DocumentFragment {
  const out = document.createDocumentFragment();
  let chunk: HTMLElement | null = null;
  let chunkLines = 0;
  const close = () => {
    // Until it's first laid out, a chunk is as tall as its lines would be as plain text.
    if (chunk) chunk.style.setProperty('contain-intrinsic-size', `auto ${(chunkLines * 1.62).toFixed(2)}em`);
  };
  for (const b of blocks) {
    if (!chunk || (chunkLines >= CHUNK_LINES && !('h' in b))) {
      close();
      chunk = el('div', 'md-chunk');
      chunkLines = 0;
      out.append(chunk);
    }
    const before = lineCount;
    chunk.append(block(b));
    chunkLines += lineCount - before;
  }
  close();
  return out;
}

const CHUNK_LINES = 40;

/** Lines built so far (for sizing chunks). */
let lineCount = 0;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  return e;
}

function block(b: Block): Node {
  if ('h' in b) {
    const e = el(b.h === 1 ? 'h2' : b.h === 2 ? 'h3' : 'h4', 'md-h');
    e.append(line(b.c, b.ln ?? 0));
    return e;
  }
  if ('p' in b) {
    const e = el('p');
    lines(e, b.p, b.lns ?? []);
    return e;
  }
  if ('ul' in b || 'ol' in b) {
    const e = 'ul' in b ? el('ul') : el('ol');
    if ('ol' in b && b.start !== 1) e.setAttribute('start', String(b.start));
    const items = 'ul' in b ? b.ul : b.ol;
    for (const it of items) e.append(item(it));
    return e;
  }
  if ('quote' in b) {
    const e = el('blockquote');
    for (const x of b.quote) e.append(block(x));
    return e;
  }
  if ('code' in b) {
    const pre = el('pre');
    const code = el('code');
    const first = b.ln ?? 0;
    const parts = b.code ? b.code.split('\n') : [];
    if (!parts.length) {
      const empty = el('span', 'ln');
      empty.dataset.ln = String(first);
      code.append(empty);
    }
    lineCount += parts.length + 1;
    parts.forEach((text, i) => {
      if (i > 0) code.append('\n');
      const span = el('span', 'ln');
      span.dataset.ln = String(first + 1 + i);
      span.textContent = text;
      code.append(span);
    });
    pre.append(code);
    return pre;
  }
  const hr = el('hr');
  hr.dataset.ln = String(b.ln ?? 0);
  return hr;
}

function item(it: Item): HTMLLIElement {
  const li = el('li');
  const body = el('span', 'li-text');
  lines(body, it.c, it.lns ?? []);
  if (it.task !== null) {
    li.className = it.task ? 'task done' : 'task';
    const box = el('input', 'md-check');
    box.type = 'checkbox';
    box.checked = it.task;
    box.dataset.at = String(it.at);
    // Its label is the item's own text, read as plain text.
    box.setAttribute('aria-label', body.textContent?.trim() || 'Checklist item');
    li.append(box);
  }
  li.append(body);
  for (const b of it.sub) li.append(block(b));
  return li;
}

/** Lines of inline content (split at its breaks) into `into`, each line its own span. */
function lines(into: HTMLElement, c: Inline[], lns: number[]): void {
  let start = 0;
  let n = 0;
  for (let i = 0; i <= c.length; i++) {
    const x = c[i];
    if (i < c.length && !(typeof x === 'object' && 'br' in x)) continue;
    if (n > 0) into.append(el('br'));
    into.append(line(c.slice(start, i), lns[n] ?? lns[lns.length - 1] ?? 0));
    n++;
    start = i + 1;
  }
}

function line(c: Inline[], ln: number): HTMLSpanElement {
  lineCount++;
  const span = el('span', 'ln');
  span.dataset.ln = String(ln);
  inlines(span, c);
  return span;
}

function inlines(into: HTMLElement, c: Inline[]): void {
  for (const x of c) {
    if (typeof x === 'string') {
      into.append(x);
      continue;
    }
    let e: HTMLElement;
    if ('b' in x) {
      e = el('strong');
      inlines(e, x.b);
    } else if ('i' in x) {
      e = el('em');
      inlines(e, x.i);
    } else if ('s' in x) {
      e = el('s');
      inlines(e, x.s);
    } else if ('code' in x) {
      e = el('code');
      e.textContent = x.code;
    } else if ('link' in x) {
      e = el('span', 'md-link');
      e.setAttribute('title', x.url);
      inlines(e, x.link);
    } else {
      e = el('br');
    }
    into.append(e);
  }
}
