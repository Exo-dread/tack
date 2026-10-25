import { findIn, originalRange, planJump, SearchIndex, type Hit, type HitKind, type Results } from '../model/search';
import { sheetTitle, shorten } from '../model/text';
import type { Sheet, Task } from '../model/types';
import type { Workspace } from '../model/workspace';
import { prefs, setPrefs, type Mode } from '../store/prefs';
import { coarsePointer, h, svg } from './dom';
import { icons } from './icons';
import type { NotesView } from './notes';
import { presentSheet, type SheetHandle } from './sheet';
import type { SpaceUi } from './spaces';
import type { TasksView } from './tasks';

export interface SearchContext {
  spaces: SpaceUi;
  tasks: TasksView;
  notes: NotesView;
  setMode(mode: Mode): void;
}

/** Rows a group draws at first; "Show more" draws this many again (lists stay quick to draw). */
export const FIRST_ROWS = 50;
export const MORE_ROWS = 200;

/** Typing is followed this long after the last key, so a burst of keys is one search. */
const PAUSE_MS = 90;

const GROUPS: { kind: HitKind; title: string; key: 'tasks' | 'done' | 'notes' }[] = [
  { kind: 'task', title: 'Tasks', key: 'tasks' },
  { kind: 'done', title: 'Done', key: 'done' },
  { kind: 'note', title: 'Notes', key: 'notes' },
];

interface Row {
  el: HTMLElement;
  hit?: Hit;
  more?: HitKind;
}

/**
 * Search (Ctrl+F, or the search button on a phone): a box, and what matches it from every space,
 * grouped Tasks, Done and Notes, each with its space while there are spaces. It's a sheet like the
 * others: within thumb reach on a phone (as tall as the screen above the keyboard), near the top
 * on a wide screen. The caret stays in the box: arrows choose a result, Enter (or a tap) opens
 * it, in its space and on its page, and Esc closes search and puts the focus back.
 */
export class SearchUi {
  private readonly index = new SearchIndex();
  private sheet: SheetHandle | null = null;
  private input!: HTMLInputElement;
  private list!: HTMLElement;
  private hint!: HTMLElement;
  private status!: HTMLElement;
  private count!: HTMLElement;
  private results: Results | null = null;
  private shown = '';
  private rows: Row[] = [];
  private current = -1;
  private readonly limits = new Map<HitKind, number>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  /** The query from last time: it's back (selected) when search opens again. */
  private kept = '';
  private returnTo: HTMLElement | null = null;
  private opening = false;

  constructor(
    private readonly ws: Workspace,
    private readonly ctx: SearchContext,
  ) {}

  get isOpen(): boolean {
    return this.sheet !== null;
  }

  open(): void {
    if (this.sheet) {
      this.input.focus();
      this.input.select();
      return;
    }
    const active = document.activeElement;
    this.returnTo = active instanceof HTMLElement && active !== document.body ? active : null;

    this.input = h('input.search-input', {
      type: 'text',
      role: 'combobox',
      'aria-label': 'Search tasks and notes',
      'aria-expanded': 'false',
      'aria-controls': 'search-results',
      'aria-autocomplete': 'list',
      'aria-describedby': 'search-keys',
      placeholder: 'Search tasks and notes',
      enterkeyhint: 'search',
      inputmode: 'search',
      autocomplete: 'off',
      autocapitalize: 'off',
      spellcheck: 'false',
      maxlength: '1000',
    });
    this.count = h('span.search-count', { 'aria-hidden': 'true' });
    const field = h('div.search-field', {}, svg(icons.search), this.input, this.count);
    this.status = h('p.sr-only', { role: 'status' });
    const keys = h('p.sr-only#search-keys', { text: 'Up and Down choose a result, Enter opens it, Escape closes search.' });
    this.hint = h('div.search-hint', {}, h('p.search-hint-title'), h('p.search-hint-text'));
    this.list = h('div.search-results#search-results', { role: 'listbox', 'aria-label': 'Results' });

    this.input.addEventListener('input', () => this.schedule());
    this.input.addEventListener('keydown', this.onKey);
    this.list.addEventListener('click', (e) => {
      const el = (e.target as HTMLElement).closest<HTMLElement>('.search-option');
      const i = el ? this.rows.findIndex((r) => r.el === el) : -1;
      if (i >= 0) this.pick(i);
    });
    // A click keeps the caret in the box (with a mouse; on touch the keyboard may go).
    this.list.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'mouse') e.preventDefault();
    });

    this.sheet = presentSheet('Search', [keys, this.status, this.hint, this.list], {
      head: [field],
      className: 'search-sheet',
      onClose: () => {
        this.kept = this.input.value;
        if (this.timer) clearTimeout(this.timer);
        this.timer = null;
        this.sheet = null;
      },
      onClosed: () => {
        if (this.opening) return;
        // Esc, Done or a tap outside: the focus goes back where it was.
        const back = this.returnTo;
        this.returnTo = null;
        if (back?.isConnected) back.focus({ preventScroll: true });
      },
    });
    this.input.value = this.kept;
    this.input.focus();
    this.input.select();
    this.limits.clear();
    this.results = null;
    this.run();
  }

  close(): void {
    this.sheet?.close();
  }

  // ---------------------------------------------------------------- searching

  private schedule(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.run(), PAUSE_MS);
  }

  /** Searches now for what's in the box, if it hasn't been yet (Enter mustn't open a stale row). */
  private flush(): void {
    if (this.timer) this.run();
  }

  private run(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    const query = this.input.value;
    if (this.results && query === this.shown) return;
    this.shown = query;
    this.limits.clear();
    this.results = this.index.find(this.ws, query);
    this.draw(0);
    const r = this.results;
    this.count.textContent = r.query ? r.total.toLocaleString('en') : '';
    if (r.query) this.status.textContent = r.total === 0 ? 'No results' : r.total === 1 ? '1 result' : `${r.total.toLocaleString('en')} results`;
    else this.status.textContent = '';
  }

  // ---------------------------------------------------------------- drawing

  private draw(select: number): void {
    const r = this.results!;
    this.rows = [];
    // About as many characters before a match as leave it in sight beside its space.
    const width = this.list.clientWidth || 360;
    const lead = Math.max(12, Math.min(60, Math.floor((width - 150) / 8)));
    const groups: HTMLElement[] = [];
    for (const g of GROUPS) {
      const hits = r[g.key];
      if (!hits.length) continue;
      const n = hits.length.toLocaleString('en');
      const group = h('div.search-group', { role: 'group', 'aria-label': `${g.title}, ${n}` }, h('div.search-group-title', { 'aria-hidden': 'true', text: `${g.title} · ${n}` }));
      const limit = this.limits.get(g.kind) ?? FIRST_ROWS;
      for (const hit of hits.slice(0, limit)) {
        const el = this.option(hit, r.query.length, lead);
        if (el) {
          group.append(el);
          this.rows.push({ el, hit });
        }
      }
      const left = hits.length - limit;
      if (left > 0) {
        const text = left <= MORE_ROWS ? `Show ${left.toLocaleString('en')} more` : `Show ${MORE_ROWS} more (${left.toLocaleString('en')} left)`;
        const el = h('div.search-option.search-more', { role: 'option', id: `search-option-${this.rows.length}`, 'aria-selected': 'false' }, h('span.search-icon', { 'aria-hidden': 'true' }, svg(icons.more)), h('span.search-main', {}, h('span.search-line', { text })));
        group.append(el);
        this.rows.push({ el, more: g.kind });
      }
      groups.push(group);
    }
    this.list.replaceChildren(...groups);
    this.input.setAttribute('aria-expanded', String(this.rows.length > 0));
    this.renderHint();
    this.current = -1;
    if (this.rows.length) this.choose(Math.min(select, this.rows.length - 1), false);
    else this.input.removeAttribute('aria-activedescendant');
  }

  private option(hit: Hit, length: number, lead: number): HTMLElement | null {
    const ws = this.ws;
    const item: Task | Sheet | undefined = hit.kind === 'note' ? ws.sheets.get(hit.id) : ws.tasks.get(hit.id);
    if (!item) return null;
    const text = item.text;
    const [start, end] = originalRange(text, hit.at, hit.at + length);
    const sp = this.ctx.spaces;
    const space = sp.on ? sp.label(ws.spaceOf(item)) : '';
    const now = hit.kind === 'task' && ws.now?.id === hit.id;
    const meta = [now ? 'Now' : '', space].filter(Boolean).join(' · ');
    const where = space ? `, in ${space}` : '';
    const id = `search-option-${this.rows.length}`;
    const icon = hit.kind === 'note' ? icons.notes : hit.kind === 'done' ? icons.tasks : icons.ring;
    let main: HTMLElement;
    let name: string;
    if (hit.kind === 'note') {
      const [titleStart, titleEnd] = titleLine(text);
      const line = cut(text, start, end, lead);
      if (start >= titleStart && start < Math.max(titleEnd, titleStart + 1)) {
        main = h('span.search-main', {}, excerpt('span.search-line', line));
        name = `Note: ${line.join('')}${where}`;
      } else {
        const title = sheetTitle(text);
        main = h('span.search-main', {}, h('span.search-line', { text: title }), excerpt('span.search-second', line));
        name = `Note ${title}: ${line.join('')}${where}`;
      }
    } else {
      main = h('span.search-main', {}, excerpt('span.search-line', cut(text, start, end, lead)));
      const spoken = shorten(text, 300);
      name = `${now ? 'In focus: ' : hit.kind === 'done' ? 'Done: ' : ''}${spoken}${where}`;
    }
    return h(
      'div.search-option',
      { role: 'option', id, 'aria-selected': 'false', 'aria-label': name, 'data-kind': hit.kind },
      h('span.search-icon', { 'aria-hidden': 'true' }, svg(icon)),
      main,
      meta ? h('span.search-meta', { 'aria-hidden': 'true', text: meta }) : null,
    );
  }

  private renderHint(): void {
    const r = this.results!;
    const empty = !!r.query && r.total === 0;
    this.hint.hidden = !!r.query && !empty;
    this.list.hidden = !r.query || empty;
    const [title, text] = [this.hint.firstElementChild!, this.hint.lastElementChild!];
    if (empty) {
      title.textContent = `Nothing matches “${shorten(this.shown.trim(), 40)}”`;
      text.textContent = 'Search looks at every word of every task and note, whatever its case or accents.';
    } else if (!r.query) {
      title.textContent = 'Search tasks and notes';
      text.textContent =
        (this.ctx.spaces.on ? 'Open and finished tasks and notes, in every space.' : 'Open and finished tasks, and every note.') +
        (coarsePointer() ? '' : ' ↑ ↓ to choose, Enter to open, Esc to close.');
    }
  }

  // ---------------------------------------------------------------- choosing and opening

  private choose(i: number, reveal = true): void {
    const prev = this.rows[this.current];
    prev?.el.setAttribute('aria-selected', 'false');
    this.current = i;
    const row = this.rows[i]!;
    row.el.setAttribute('aria-selected', 'true');
    this.input.setAttribute('aria-activedescendant', row.el.id);
    if (reveal) row.el.scrollIntoView({ block: 'nearest' });
  }

  private onKey = (e: KeyboardEvent) => {
    if (e.isComposing) return;
    const step = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : e.key === 'PageDown' ? 8 : e.key === 'PageUp' ? -8 : 0;
    if (step && !e.altKey && !e.ctrlKey && !e.metaKey) {
      e.preventDefault();
      this.flush();
      if (!this.rows.length) return;
      const i = this.current < 0 ? (step > 0 ? 0 : this.rows.length - 1) : this.current + step;
      this.choose(Math.max(0, Math.min(this.rows.length - 1, i)));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      this.flush();
      if (this.rows.length) this.pick(Math.max(0, this.current));
    }
  };

  private pick(i: number): void {
    const row = this.rows[i];
    if (!row) return;
    if (row.more) {
      this.limits.set(row.more, (this.limits.get(row.more) ?? FIRST_ROWS) + MORE_ROWS);
      this.draw(i);
      this.rows[i]?.el.scrollIntoView({ block: 'nearest' });
      this.input.focus({ preventScroll: true });
      return;
    }
    if (row.hit) this.go(row.hit);
  }

  /**
   * Opens what was found: its space (unless the one on show shows it already), its page, and the
   * task scrolled to and lit up, or the sheet with the match selected. Search closes at once, so
   * the focus lands there within the tap (iOS opens the keyboard only then).
   */
  private go(hit: Hit): void {
    const query = this.shown;
    this.opening = true;
    try {
      this.sheet?.close(true);
    } finally {
      this.opening = false;
    }
    this.returnTo = null;
    const ws = this.ws;
    const item = hit.kind === 'note' ? ws.sheets.get(hit.id) : ws.tasks.get(hit.id);
    if (!item || item.deleted) return; // gone since it was found (another device, say)
    const sp = this.ctx.spaces;
    const plan = planJump(ws, item, hit.kind === 'note', sp.show, prefs().doneExpanded);
    if (plan.space) sp.setShow(plan.space);
    this.ctx.setMode(plan.mode);
    if (hit.kind === 'note') {
      this.ctx.notes.reveal(item.id, findIn(item.text, query));
    } else {
      if (plan.openDone) {
        setPrefs({ doneExpanded: true });
        this.ctx.tasks.render(false);
      }
      this.ctx.tasks.reveal(item.id);
    }
  }
}

/** The match on its line, with a little of the line before it (from a word's start) and the rest after; a cut is marked "…". */
export function cut(text: string, start: number, end: number, lead = 16, tail = 160): [string, string, string] {
  let lineStart = start === 0 ? 0 : text.lastIndexOf('\n', start - 1) + 1;
  let lineEnd = text.indexOf('\n', end);
  if (lineEnd < 0) lineEnd = text.length;
  while (lineStart < start && (text[lineStart] === ' ' || text[lineStart] === '\t')) lineStart++;
  // A few characters more than the lead aren't worth a cut.
  let from = start - lineStart > lead + 4 ? start - lead : lineStart;
  if (from > lineStart) {
    const space = text.indexOf(' ', from);
    if (space >= 0 && space < start) from = space + 1;
  }
  let to = Math.min(lineEnd, end + tail);
  if (to < text.length && /[\uD800-\uDBFF]/.test(text[to - 1] ?? '')) to++;
  const flat = (s: string) => s.replace(/[\r\n\t]/g, ' ');
  return [(from > lineStart ? '…' : '') + flat(text.slice(from, start)), flat(text.slice(start, end)), flat(text.slice(end, to)) + (to < lineEnd ? '…' : '')];
}

/** Where a sheet's title is: its first line with anything on it (as sheetTitle finds it). */
function titleLine(text: string): [number, number] {
  let i = 0;
  while (i <= text.length) {
    let end = text.indexOf('\n', i);
    if (end < 0) end = text.length;
    if (text.slice(i, end).trim().replace(/^[#\-*•\s]+/, '').trim()) return [i, end];
    if (end >= text.length) break;
    i = end + 1;
  }
  return [0, 0];
}

function excerpt(spec: 'span.search-line' | 'span.search-second', [before, match, after]: [string, string, string]): HTMLElement {
  return h(spec, {}, before, match ? h('mark', { text: match }) : null, after);
}
