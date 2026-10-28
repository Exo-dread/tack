import { MAX_NOTE_TEXT, MAX_PASTE_LINES } from '../model/limits';
import { canFormat, lineStarts, parse, setTask } from '../model/markdown';
import { continueList, noteLinesToTasks, sheetTitle, shorten } from '../model/text';
import type { Sheet } from '../model/types';
import type { Origin, Workspace } from '../model/workspace';
import { onPrefs, prefs, setPrefs } from '../store/prefs';
import { h, svg } from './dom';
import { haptics } from './haptics';
import { icons } from './icons';
import { renderNote } from './markdownview';
import { animate, EASE_OUT, measure, playFlip, reducedMotion } from './motion';
import type { SpaceUi } from './spaces';
import type { Toasts } from './toast';

export interface NotesContext {
  toasts: Toasts;
  /** A task was made from a note: nudge the Tasks switch so you can see where it went. */
  sentToTasks(count: number): void;
  /** The space on show: its sheets are the ones on the tabs. */
  spaces: SpaceUi;
  /** The sheet being written on changed, or there's none (a space's blank paper). */
  activeChanged(): void;
}

/**
 * Notes: sheets of scratch paper. Plain text that saves as you type, titled by its first line,
 * with the desktop app's quiet helpers: lists continue themselves, a timestamp is a tap away,
 * and any line (or selection) can become a task.
 *
 * While you read, the note shows formatted (its Markdown: docs/NOTES-MARKDOWN.md); while you
 * write, it's the plain editor. A click, a tap or a keystroke on the formatted note opens the
 * editor at that line. The text stays exactly what was typed: formatting is only how it's shown.
 */
export class NotesView {
  readonly el: HTMLElement;
  readonly editor: HTMLTextAreaElement;
  /** The note formatted, shown instead of the editor while you read (Format notes on). */
  readonly view: HTMLElement;
  /** Sheet tabs. Lives in the dock on phones and above the page on wide screens. */
  readonly sheetBar: HTMLElement;
  /** "Make task", "Time", "Done": shown above the keyboard while writing. */
  readonly tools: HTMLElement;
  private readonly strip: HTMLElement;
  private activeId = '';
  private readonly scrollMemory = new Map<string, { top: number; caret: number }>();

  constructor(
    private readonly ws: Workspace,
    private readonly ctx: NotesContext,
  ) {
    this.editor = h('textarea.editor', {
      'aria-label': 'Note',
      maxlength: String(MAX_NOTE_TEXT),
      placeholder: 'Scrap paper. Type anything; it saves as you go.',
      autocapitalize: 'sentences',
      spellcheck: 'true',
      autocomplete: 'off',
    });
    this.view = h('div.md', {
      role: 'document',
      'aria-label': 'Note',
      'aria-keyshortcuts': 'Enter F2 F5 Control+Enter',
      tabindex: '0',
      hidden: true,
      onclick: (e: Event) => this.onViewClick(e as MouseEvent),
      onkeydown: (e: Event) => this.onViewKey(e as KeyboardEvent),
      onchange: (e: Event) => this.onTick(e),
    });
    this.el = h(
      'section.page.notes#page-notes',
      { role: 'tabpanel', 'aria-labelledby': 'tab-notes', tabindex: '-1' },
      h('div.paper', {}, this.editor, this.view),
    );

    this.strip = h('div.sheet-strip', { role: 'tablist', 'aria-label': 'Sheets' });
    this.sheetBar = h(
      'div.sheet-bar',
      {},
      this.strip,
      h('button.icon-button.sheet-add', { type: 'button', title: 'New sheet', 'aria-label': 'New sheet', onclick: () => this.newSheet() }, svg(icons.plus)),
    );

    const tool = (cls: string, label: string, icon: string, title: string, handler: () => void) =>
      h('button.tool', { class: cls, type: 'button', title, 'aria-label': label, onclick: handler }, svg(icon), h('span', { text: label }));
    this.tools = h(
      'div.note-tools',
      { role: 'toolbar', 'aria-label': 'Note tools' },
      tool('n-task', 'Make task', icons.toTask, 'Turn this line or selection into a task (Ctrl+Enter)', () => this.toTasks()),
      tool('n-time', 'Time', icons.clock, 'Insert the date and time (F5)', () => this.stamp()),
      h('span.spacer'),
      tool('n-done', 'Done', icons.keyboardDown, 'Stop editing', () => this.editor.blur()),
    );
    // Tools must not steal focus from the editor: that would drop the selection and the keyboard.
    this.tools.addEventListener('pointerdown', (e) => e.preventDefault());

    this.editor.addEventListener('input', () => this.onInput());
    this.editor.addEventListener('beforeinput', this.onBeforeInput);
    this.editor.addEventListener('keydown', this.onKeyDown);
    this.editor.addEventListener('blur', () => {
      // The window losing the focus leaves the editor the active element: still writing.
      if (document.activeElement === this.editor) return;
      if (this.activeId) this.caretMemo.set(this.activeId, this.editor.selectionStart);
      this.syncEditor();
      this.refreshView();
    });
    // The app hidden (another app, another tab, the phone locked): the note shows formatted again.
    document.addEventListener('visibilitychange', () => {
      if (document.hidden && document.activeElement === this.editor && this.formats(this.activeText)) this.editor.blur();
    });
    let formatting = prefs().formatNotes;
    onPrefs((p) => {
      if (p.formatNotes === formatting) return;
      formatting = p.formatNotes;
      this.refreshView();
    });
    this.editor.addEventListener('compositionstart', () => (this.composing = true));
    this.editor.addEventListener('compositionend', () => {
      this.composing = false;
      if (this.syncAfterComposition) {
        this.syncAfterComposition = false;
        this.syncEditor(true);
      }
    });
    this.strip.addEventListener('click', (e) => {
      const pill = (e.target as HTMLElement).closest<HTMLElement>('.sheet-pill');
      if (pill) this.select(pill.dataset.id!);
    });
    this.strip.addEventListener('keydown', this.onStripKey);
    // The strip narrows when writing ends (the dock gives the mode switch its room back): with
    // spaces, the sheet just written on stays in sight.
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => this.revealActive()).observe(this.strip);
  }

  get active(): string {
    return this.activeId;
  }

  get activeText(): string {
    return this.ws.sheets.get(this.activeId)?.text ?? '';
  }

  /** The sheets on the tabs: the space on show's (all of them in All, or with no spaces). */
  private list(): Sheet[] {
    return this.ws.sheetsIn(this.ctx.spaces.show);
  }

  /** The sheet each space showed last (this session), so switching back lands on it again. */
  private readonly lastIn = new Map<string, string>();

  private remember(): void {
    if (this.activeId) this.lastIn.set(this.ctx.spaces.show, this.activeId);
  }

  private onInput(): void {
    if (this.activeId) {
      this.ws.editSheet(this.activeId, this.editor.value);
      return;
    }
    // A space with no sheets shows blank paper: the first keystroke makes its sheet. Input that
    // leaves it blank (Undo bringing back the empty paper) makes nothing, as on the desktop.
    if (!this.editor.value) return;
    const sheet = this.ws.addSheet(this.editor.value, this.ctx.spaces.here);
    this.activeId = sheet.id;
    setPrefs({ activeSheet: sheet.id });
    this.remember();
    this.renderStrip();
  }

  /**
   * `origin` says where the change came from. Your own typing is already in the editor; a change
   * from another tab or window (or an import) must reach it even while you're writing, or your
   * next keystroke would save the old text over it.
   */
  render(origin: Origin = 'local'): void {
    const sheets = this.list();
    if (!sheets.some((s) => s.id === this.activeId)) {
      const last = this.lastIn.get(this.ctx.spaces.show);
      const remembered = prefs().activeSheet;
      // In a space with no sheets: blank paper (switching spaces never makes empty sheets).
      const pick =
        sheets.find((s) => s.id === last) ?? sheets.find((s) => s.id === remembered) ?? sheets[0] ?? (this.ctx.spaces.on ? null : this.ws.ensureSheet());
      this.activeId = pick?.id ?? '';
      this.editor.value = this.activeText;
    }
    this.remember();
    this.renderStrip();
    this.syncEditor(origin !== 'local');
    this.refreshView();
  }

  /** Shows the model's text. While you're writing, only changes from elsewhere are applied, keeping your place. */
  private syncEditor(fromElsewhere = false): void {
    const text = this.activeText;
    const ta = this.editor;
    if (ta.value === text) return;
    if (document.activeElement !== ta) {
      ta.value = text;
      return;
    }
    if (!fromElsewhere) return;
    if (this.composing) {
      this.syncAfterComposition = true;
      return;
    }
    const { selectionStart, selectionEnd, scrollTop } = ta;
    ta.value = text;
    ta.setSelectionRange(Math.min(selectionStart, text.length), Math.min(selectionEnd, text.length));
    ta.scrollTop = scrollTop;
  }

  private composing = false;
  private syncAfterComposition = false;
  private stripIds = '';

  private renderStrip(): void {
    const sheets = this.list();
    // Animate only when sheets were added, removed or reordered, not on every keystroke.
    const ids = sheets.map((s) => s.id).join(',');
    const reshaped = ids !== this.stripIds;
    this.stripIds = ids;
    const before = reshaped && !reducedMotion() && this.strip.isConnected ? measure(this.strip) : null;
    const existing = new Map([...this.strip.querySelectorAll<HTMLElement>('.sheet-pill')].map((p) => [p.dataset.id!, p]));
    let cursor: HTMLElement | null = null;
    for (const s of sheets) {
      let pill = existing.get(s.id);
      existing.delete(s.id);
      if (!pill) {
        pill = h('button.sheet-pill', { type: 'button', role: 'tab', 'data-id': s.id, 'data-flip': `s:${s.id}` });
      }
      const title = s.text.trim() ? sheetTitle(s.text) : 'New sheet';
      if (pill.textContent !== title) pill.textContent = title;
      const selected = s.id === this.activeId;
      pill.setAttribute('aria-selected', String(selected));
      pill.tabIndex = selected ? 0 : -1;
      if (pill.previousElementSibling !== cursor || pill.parentElement !== this.strip) {
        if (cursor) cursor.after(pill);
        else this.strip.prepend(pill);
      }
      cursor = pill;
    }
    existing.forEach((p) => p.remove());
    // A space's blank paper: a quiet stand-in for the sheet its first keystroke makes.
    const ghost = this.strip.querySelector('.sheet-ghost');
    if (sheets.length) ghost?.remove();
    else if (!ghost) this.strip.append(h('span.sheet-ghost', { 'aria-hidden': 'true', text: 'New sheet' }));
    this.strip.classList.toggle('single', sheets.length <= 1);
    if (before) playFlip(this.strip, before);
    this.ctx.activeChanged();
  }

  /**
   * Brings the active sheet's tab into sight in the strip (only the strip scrolls, at once).
   * Only once there are spaces: with Main alone the strip stays where it always has.
   */
  private revealActive(): void {
    if (!this.ctx.spaces.on) return;
    const pill = this.strip.querySelector<HTMLElement>('.sheet-pill[aria-selected="true"]');
    if (!pill) return;
    const strip = this.strip.getBoundingClientRect();
    const r = pill.getBoundingClientRect();
    const fade = 18; // the strip fades out at its right edge
    if (r.left < strip.left) this.strip.scrollLeft -= strip.left - r.left + 12;
    else if (r.right > strip.right - fade) this.strip.scrollLeft += r.right - strip.right + fade;
  }

  /**
   * Shows sheet `id`. Switching sheet doesn't put the caret in the editor (the note shows
   * formatted) unless `focusEditor` asks for it, or you were writing and the next sheet isn't
   * formatted (Format notes off, an empty or a very long note): then you write on, as before.
   */
  select(id: string, focusEditor = false): void {
    if (id === this.activeId) {
      if (focusEditor) this.focusEditor();
      return;
    }
    const wasWriting = document.activeElement === this.editor;
    const leaving = this.ws.sheets.get(this.activeId);
    this.scrollMemory.set(this.activeId, { top: this.reading ? this.view.scrollTop : this.editor.scrollTop, caret: this.editor.selectionStart });
    if (wasWriting && this.activeId) this.caretMemo.set(this.activeId, this.editor.selectionStart);
    const list = this.list();
    const goingRight = list.findIndex((s) => s.id === id) > list.findIndex((s) => s.id === this.activeId);
    this.activeId = id;
    setPrefs({ activeSheet: id });
    this.remember();
    // An empty sheet you walk away from isn't worth keeping.
    if (leaving && !leaving.text.trim() && this.ws.sheetList.length > 1) this.ws.deleteSheet(leaving.id);
    this.editor.value = this.activeText;
    const memory = this.scrollMemory.get(id);
    const writeOn = focusEditor || (wasWriting && !this.formats(this.activeText));
    if (wasWriting && !writeOn) {
      // Off the editor, onto the formatted note: the keyboard stays in the notes. (The blur
      // would remember this sheet's caret as wherever the old one's was: keep its own.)
      const caret = this.caretMemo.get(id);
      this.editor.blur();
      if (caret === undefined) this.caretMemo.delete(id);
      else this.caretMemo.set(id, caret);
      this.view.focus({ preventScroll: true });
    }
    this.renderStrip();
    this.refreshView();
    this.editor.scrollTop = memory?.top ?? 0;
    this.view.scrollTop = memory?.top ?? 0;
    this.strip.querySelector<HTMLElement>(`[data-id="${CSS.escape(id)}"]`)?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
    animate(this.reading ? this.view : this.editor, [{ opacity: 0, transform: `translateX(${goingRight ? 14 : -14}px)` }, { opacity: 1, transform: 'none' }], {
      duration: 240,
      easing: EASE_OUT,
    });
    if (writeOn) {
      const caret = memory?.caret ?? this.editor.value.length;
      this.focusEditor();
      this.editor.setSelectionRange(caret, caret);
    }
  }

  /**
   * Search opened this sheet: it's on the paper with the caret in it, the match selected and
   * scrolled to about a third of the way down (again once a phone's keyboard has come up).
   */
  reveal(id: string, match: [number, number] | null): void {
    this.select(id);
    const ta = this.editor;
    this.focusEditor();
    if (!match) return;
    const [start, end] = match;
    ta.setSelectionRange(start, end);
    const place = () => {
      ta.scrollTop = Math.max(0, caretTop(ta, start) - ta.clientHeight / 3);
    };
    place();
    const vv = window.visualViewport;
    if (!vv) return;
    const again = () => {
      vv.removeEventListener('resize', again);
      requestAnimationFrame(place);
    };
    vv.addEventListener('resize', again);
    setTimeout(() => vv.removeEventListener('resize', again), 1000);
  }

  newSheet(): void {
    const current = this.ws.sheets.get(this.activeId);
    if ((current && !current.text.trim()) || !this.activeId) {
      // Already on a blank sheet (or a space's blank paper): just start writing.
      this.focusEditor();
      return;
    }
    haptics.tick();
    const sheet = this.ws.addSheet('', this.ctx.spaces.here);
    this.select(sheet.id, true);
  }

  deleteActive(): void {
    const sheet = this.ws.sheets.get(this.activeId);
    if (!sheet) return;
    const list = this.list();
    const index = list.findIndex((s) => s.id === sheet.id);
    // The very last sheet is cleared instead (a space's last one goes, leaving blank paper).
    const wasOnly = this.ws.sheetList.length <= 1;
    if (wasOnly && !sheet.text.trim()) return;
    const undo = this.ws.deleteSheet(sheet.id);
    haptics.warn();
    if (!wasOnly) {
      const rest = this.list();
      const next = rest[Math.min(index, rest.length - 1)];
      this.activeId = '';
      this.render();
      if (next) this.select(next.id);
    }
    this.editor.value = this.activeText;
    this.refreshView();
    const title = sheet.text.trim() ? `“${shorten(sheetTitle(sheet.text), 24)}”` : 'sheet';
    this.ctx.toasts.show(wasOnly ? 'Sheet cleared' : `Deleted ${title}`, () => {
      undo();
      this.select(sheet.id);
      this.editor.value = this.activeText;
      this.refreshView();
    });
  }

  /** The selection, or the line the caret is on, becomes one task per line. */
  toTasks(): void {
    const ta = this.editor;
    const { value, selectionStart: start, selectionEnd: end } = ta;
    let source = value.slice(start, end);
    if (!source.trim()) {
      const lineStart = value.lastIndexOf('\n', start - 1) + 1;
      let lineEnd = value.indexOf('\n', start);
      if (lineEnd < 0) lineEnd = value.length;
      source = value.slice(lineStart, lineEnd);
    }
    const lines = noteLinesToTasks(source);
    if (lines.length === 0) {
      this.ctx.toasts.show('Put the cursor on a line, or select some text, to make a task');
      return;
    }
    if (lines.length > MAX_PASTE_LINES) {
      this.ctx.toasts.show(`Make up to ${MAX_PASTE_LINES.toLocaleString('en')} tasks at once`);
      return;
    }
    // Into the sheet's own space.
    const sheet = this.ws.sheets.get(this.activeId);
    const { added, undo } = this.ws.addTasks(lines, sheet ? this.ws.spaceOf(sheet) : this.ctx.spaces.here);
    haptics.success();
    const label = added.length === 1 ? `Added to Tasks: “${shorten(added[0].text, 26)}”` : `Added ${added.length} tasks`;
    this.ctx.toasts.show(label, undo);
    this.ctx.sentToTasks(added.length);
  }

  stamp(): void {
    const stamp = new Intl.DateTimeFormat(undefined, {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      hour: 'numeric',
      minute: '2-digit',
    }).format(new Date());
    // From the formatted note (the tools sit beside it on wide screens): into the editor first.
    if (document.activeElement !== this.editor) this.focusEditor();
    insertText(this.editor, stamp);
  }

  private onBeforeInput = (e: InputEvent) => {
    // Inserting the next list marker (a newline, then "- ") raises another line-break event
    // in WebKit; let our own insertions through.
    if (inserting) return;
    if (e.inputType !== 'insertLineBreak' && e.inputType !== 'insertParagraph') return;
    const ta = this.editor;
    if (ta.selectionStart !== ta.selectionEnd) return;
    const next = continueList(ta.value, ta.selectionStart);
    if (!next) return;
    e.preventDefault();
    if ('insert' in next) {
      insertText(ta, next.insert);
    } else {
      ta.setSelectionRange(next.lineStart, next.lineStart + next.lineLength);
      deleteSelection(ta);
    }
  };

  private onKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      this.toTasks();
    } else if (e.key === 'F5') {
      e.preventDefault();
      this.stamp();
    } else if (e.key === 'Escape') {
      this.editor.blur();
      // Back to the formatted note, keeping the keyboard in it (the app's own Escape would take it away).
      if (this.reading) {
        e.preventDefault();
        this.view.focus({ preventScroll: true });
      }
    }
  };

  private onStripKey = (e: KeyboardEvent) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    const list = this.list();
    const i = list.findIndex((s) => s.id === this.activeId);
    const next = list[(i + (e.key === 'ArrowRight' ? 1 : -1) + list.length) % list.length];
    if (!next) return;
    e.preventDefault();
    this.select(next.id);
    this.strip.querySelector<HTMLElement>(`[data-id="${CSS.escape(next.id)}"]`)?.focus();
  };

  /** Next sheet (Ctrl+Tab on desktop). */
  cycle(delta: number): void {
    const list = this.list();
    const i = list.findIndex((s) => s.id === this.activeId);
    const next = list[(i + delta + list.length) % list.length];
    if (next) this.select(next.id);
  }

  // ================================================================ formatted view

  /** The note formatted is on show (else the editor is). */
  private get reading(): boolean {
    return !this.view.hidden;
  }

  /** Would this text show formatted while you read it? */
  private formats(text: string): boolean {
    return prefs().formatNotes && canFormat(text);
  }

  /** What the view shows: the text it was laid out from, its sheet, and where its lines start. */
  private shown: { id: string; text: string; starts: number[] | null } | null = null;

  /**
   * Formatted, or the editor: formatted whenever the editor doesn't have the keyboard (and the
   * note can be). Lays the note out again only if it changed since it was last shown, and never
   * while you're writing, so typing costs nothing more.
   */
  private refreshView(): void {
    const text = this.activeText;
    const formatted = document.activeElement !== this.editor && this.formats(text);
    if (formatted && (this.shown?.text !== text || this.shown.id !== this.activeId)) this.paint(text);
    if (this.editor.hidden !== formatted) {
      // The keyboard on the hidden view would be lost: the editor takes it.
      const hadFocus = !formatted && this.view.contains(document.activeElement);
      this.editor.hidden = formatted;
      this.view.hidden = !formatted;
      if (hadFocus) this.editor.focus({ preventScroll: true });
    }
  }

  private paint(text: string): void {
    const view = this.view;
    const sameSheet = this.shown?.id === this.activeId;
    // Keep your place: the scroll position, and the checkbox that had the keyboard.
    const focused = view.contains(document.activeElement) ? (document.activeElement as HTMLElement) : null;
    const at = focused?.dataset.at;
    const top = view.scrollTop;
    view.replaceChildren(renderNote(parse(text, { lines: true })));
    this.shown = { id: this.activeId, text, starts: null };
    if (sameSheet) view.scrollTop = top;
    if (focused && focused !== view) {
      const again = at !== undefined ? view.querySelector<HTMLElement>(`input[data-at="${CSS.escape(at)}"]`) : null;
      (again ?? view).focus({ preventScroll: true });
    }
  }

  /** Where line `ln` of the note on show starts. */
  private lineOffset(ln: number): number {
    const shown = this.shown!;
    shown.starts ??= lineStarts(shown.text);
    return shown.starts[Math.min(ln, shown.starts.length - 1)] ?? 0;
  }

  /** The editor, in sight, with the keyboard (and the caret where it was, if it was hidden). */
  private focusEditor(): void {
    const ta = this.editor;
    const wasHidden = ta.hidden !== false;
    ta.hidden = false;
    this.view.hidden = true;
    // WebKit won't focus an element it hasn't laid out since it was hidden: lay it out first.
    void ta.offsetHeight;
    ta.focus({ preventScroll: wasHidden });
    if (wasHidden) {
      const caret = Math.min(this.caretMemo.get(this.activeId) ?? ta.value.length, ta.value.length);
      ta.setSelectionRange(caret, caret);
    }
  }

  /** The caret in each sheet when you last stopped writing in it (this session). */
  private readonly caretMemo = new Map<string, number>();

  /**
   * Opens the editor with the caret at `caret`, scrolled so that the line at `keep.offset` stays
   * `keep.top` pixels below the top, where it was in the formatted note.
   */
  private write(caret: number, keep: { offset: number; top: number } | null): void {
    const ta = this.editor;
    this.view.hidden = true;
    ta.hidden = false;
    void ta.offsetHeight; // (see focusEditor)
    ta.focus({ preventScroll: true });
    ta.setSelectionRange(caret, caret);
    if (keep) ta.scrollTop = Math.max(0, caretTop(ta, keep.offset) - keep.top);
    const inSight = () => {
      const y = caretTop(ta, ta.selectionStart);
      if (y < ta.scrollTop || y > ta.scrollTop + ta.clientHeight - 40) ta.scrollTop = Math.max(0, y - ta.clientHeight / 3);
    };
    inSight();
    // A phone's keyboard coming up shrinks the page: keep the caret above it.
    const vv = window.visualViewport;
    if (!vv) return;
    const again = () => {
      vv.removeEventListener('resize', again);
      requestAnimationFrame(() => {
        if (document.activeElement === ta) inSight();
      });
    };
    vv.addEventListener('resize', again);
    setTimeout(() => vv.removeEventListener('resize', again), 1000);
  }

  /** The line elements on show, top to bottom. */
  private lines(): HTMLElement[] {
    return [...this.view.querySelectorAll<HTMLElement>('[data-ln]')];
  }

  /** A click or tap on the formatted note: the editor, with the caret at the start of that line. */
  private onViewClick(e: MouseEvent): void {
    if (!this.reading || e.button !== 0) return;
    if ((e.target as HTMLElement).closest('input')) return; // a checkbox ticks
    // A drag that selected text is for copying, not writing.
    const sel = document.getSelection();
    if (sel && !sel.isCollapsed && sel.anchorNode && this.view.contains(sel.anchorNode)) return;
    const box = this.view.getBoundingClientRect();
    if (e.clientX >= box.left + this.view.clientLeft + this.view.clientWidth) return; // the scroll bar
    const lines = this.lines();
    let pick: HTMLElement | undefined;
    let rect: DOMRect | undefined;
    for (const line of lines) {
      const r = line.getBoundingClientRect();
      if (r.top > e.clientY) break;
      pick = line;
      rect = r;
      if (e.clientY < r.bottom) break;
    }
    // Above the first line: the first line.
    if (!pick && lines[0]) {
      pick = lines[0];
      rect = pick.getBoundingClientRect();
    }
    if (!pick || !rect) {
      this.write(this.activeText.length, null);
      return;
    }
    const offset = this.lineOffset(Number(pick.dataset.ln));
    // Below the last line: the end of the note, as in the editor.
    const end = pick === lines[lines.length - 1] && e.clientY >= rect.bottom;
    this.write(end ? this.activeText.length : offset, { offset, top: rect.top - box.top });
  }

  /**
   * A key on the formatted note. Keys that write open the editor first (writeFromKeyboard), then
   * do what they do there: a character is typed, Enter and F2 type nothing, F5 stamps, Ctrl+Enter
   * makes a task of the caret's line, Ctrl+V pastes. Space on a checkbox ticks it.
   */
  private onViewKey(e: KeyboardEvent): void {
    if (e.isComposing) return;
    const mod = e.ctrlKey || e.metaKey;
    const box = (e.target as HTMLElement).closest<HTMLInputElement>('input.md-check');
    if (e.key === 'F5') {
      // As in the editor (which takes F5 with any modifier): never the browser's reload.
      e.preventDefault();
      this.writeFromKeyboard(box);
      this.stamp();
      return;
    }
    if (mod && !e.altKey && !e.shiftKey && (e.key === 'z' || e.key === 'Z')) {
      if (this.undoTick()) e.preventDefault();
      return;
    }
    if (mod && !e.altKey && !e.shiftKey && e.key === 'Enter') {
      e.preventDefault();
      this.writeFromKeyboard(box);
      this.toTasks();
      return;
    }
    if ((mod && !e.altKey && (e.key === 'v' || e.key === 'V')) || (e.shiftKey && !mod && !e.altKey && e.key === 'Insert')) {
      // The editor takes the focus now, so the paste this key makes lands in it, at the caret.
      this.writeFromKeyboard(box);
      return;
    }
    if (mod || e.altKey) return;
    if (box && e.key === ' ') return; // ticks it
    if (e.key === 'F2' || e.key === 'Enter') {
      e.preventDefault();
      this.writeFromKeyboard(box);
    } else if ([...e.key].length === 1 || e.key === 'Dead' || e.key === 'Process') {
      // The editor takes the focus now, so this very key is typed into it.
      this.writeFromKeyboard(box);
    }
  }

  /**
   * The keyboard on the formatted note: the editor, with the caret where it was when you last
   * wrote in this sheet (the end, if you haven't), nothing selected; or, when a checkbox had the
   * keyboard, at the end of its item's line. The lines stay in sight where they were.
   */
  private writeFromKeyboard(box: HTMLInputElement | null): void {
    const viewTop = this.view.getBoundingClientRect().top;
    let anchor: HTMLElement | undefined;
    if (box) anchor = box.closest('li')?.querySelector<HTMLElement>('[data-ln]') ?? undefined;
    anchor ??= this.lines().find((l) => l.getBoundingClientRect().bottom > viewTop);
    const keep = anchor ? { offset: this.lineOffset(Number(anchor.dataset.ln)), top: anchor.getBoundingClientRect().top - viewTop } : null;
    const text = this.activeText;
    let caret = Math.min(this.caretMemo.get(this.activeId) ?? text.length, text.length);
    if (box && keep) {
      const end = text.indexOf('\n', keep.offset);
      caret = end < 0 ? text.length : end;
    }
    this.write(caret, keep);
  }

  /** Ticks this session, newest last, for Ctrl+Z on the formatted note. */
  private ticks: { id: string; before: string; after: string }[] = [];

  /** A checkbox ticked: exactly its box changes in the text, as one edit (saved and synced like typing). */
  private onTick(e: Event): void {
    const box = e.target as HTMLInputElement;
    if (!box.matches('input.md-check')) return;
    const before = this.activeText;
    const after = setTask(before, Number(box.dataset.at), box.checked);
    if (after === null) {
      // The note changed under it: show it as it is now.
      this.paint(before);
      return;
    }
    if (after === before) return;
    this.ticks.push({ id: this.activeId, before, after });
    if (this.ticks.length > 20) this.ticks.shift();
    box.closest('li')?.classList.toggle('done', box.checked);
    // The view already shows it: no need to lay it out again.
    this.shown = { id: this.activeId, text: after, starts: this.shown?.starts ?? null };
    this.ws.editSheet(this.activeId, after);
    if (box.checked) haptics.tick();
  }

  /** Ctrl+Z on the formatted note: the last tick, if nothing has changed the note since. */
  private undoTick(): boolean {
    const last = this.ticks[this.ticks.length - 1];
    if (!last || last.id !== this.activeId || last.after !== this.activeText) {
      this.ticks = [];
      return false;
    }
    this.ticks.pop();
    this.ws.editSheet(last.id, last.before);
    return true;
  }
}

let inserting = false;

/** How far down a textarea's text `index` is, as laid out: measured on a hidden copy of it. */
function caretTop(ta: HTMLTextAreaElement, index: number): number {
  const style = getComputedStyle(ta);
  const copy = document.createElement('div');
  for (const p of ['padding-top', 'padding-right', 'padding-bottom', 'padding-left', 'font-family', 'font-size', 'font-weight', 'font-style', 'letter-spacing', 'line-height', 'text-transform', 'word-spacing', 'tab-size', 'text-indent', 'word-break']) {
    copy.style.setProperty(p, style.getPropertyValue(p));
  }
  copy.style.setProperty('box-sizing', 'border-box');
  copy.style.setProperty('width', `${ta.clientWidth}px`);
  copy.style.setProperty('position', 'absolute');
  copy.style.setProperty('visibility', 'hidden');
  copy.style.setProperty('left', '-10000px');
  copy.style.setProperty('top', '0');
  copy.style.setProperty('white-space', 'pre-wrap');
  copy.style.setProperty('overflow-wrap', 'break-word');
  // Safari on a phone enlarges the text of long blocks of text (text autosizing): not this copy's.
  copy.style.setProperty('-webkit-text-size-adjust', 'none');
  copy.style.setProperty('text-size-adjust', 'none');
  copy.textContent = ta.value.slice(0, index);
  const mark = document.createElement('span');
  mark.textContent = String.fromCharCode(0x200b); // a zero-width space: where the caret would be
  copy.append(mark);
  document.body.append(copy);
  const top = mark.offsetTop;
  copy.remove();
  return top;
}

/** Inserts text at the caret, keeping the browser's own undo history where possible. */
function insertText(ta: HTMLTextAreaElement, text: string): void {
  if (document.activeElement !== ta) ta.focus();
  let ok = false;
  inserting = true;
  try {
    ok = document.execCommand('insertText', false, text);
  } catch {
    ok = false;
  } finally {
    inserting = false;
  }
  if (!ok) {
    ta.setRangeText(text, ta.selectionStart, ta.selectionEnd, 'end');
    ta.dispatchEvent(new Event('input', { bubbles: true }));
  }
}

function deleteSelection(ta: HTMLTextAreaElement): void {
  let ok = false;
  inserting = true;
  try {
    ok = document.execCommand('delete', false);
  } catch {
    ok = false;
  } finally {
    inserting = false;
  }
  if (!ok) {
    ta.setRangeText('', ta.selectionStart, ta.selectionEnd, 'end');
    ta.dispatchEvent(new Event('input', { bubbles: true }));
  }
}
