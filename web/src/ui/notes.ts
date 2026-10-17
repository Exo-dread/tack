import { continueList, noteLinesToTasks, sheetTitle, shorten } from '../model/text';
import type { Workspace } from '../model/workspace';
import { prefs, setPrefs } from '../store/prefs';
import { h, svg } from './dom';
import { haptics } from './haptics';
import { icons } from './icons';
import { animate, EASE_OUT, measure, playFlip, reducedMotion } from './motion';
import type { Toasts } from './toast';

export interface NotesContext {
  toasts: Toasts;
  /** A task was made from a note: nudge the Tasks switch so you can see where it went. */
  sentToTasks(count: number): void;
}

/**
 * Notes: sheets of scratch paper. Plain text that saves as you type, titled by its first line,
 * with the desktop app's quiet helpers: lists continue themselves, a timestamp is a tap away,
 * and any line (or selection) can become a task.
 */
export class NotesView {
  readonly el: HTMLElement;
  readonly editor: HTMLTextAreaElement;
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
      placeholder: 'Scrap paper. Type anything; it saves as you go.',
      autocapitalize: 'sentences',
      spellcheck: 'true',
      autocomplete: 'off',
    });
    this.el = h(
      'section.page.notes#page-notes',
      { role: 'tabpanel', 'aria-labelledby': 'tab-notes', tabindex: '-1' },
      h('div.paper', {}, this.editor),
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

    this.editor.addEventListener('input', () => this.ws.editSheet(this.activeId, this.editor.value));
    this.editor.addEventListener('beforeinput', this.onBeforeInput);
    this.editor.addEventListener('keydown', this.onKeyDown);
    this.editor.addEventListener('blur', () => this.syncEditor());
    this.strip.addEventListener('click', (e) => {
      const pill = (e.target as HTMLElement).closest<HTMLElement>('.sheet-pill');
      if (pill) this.select(pill.dataset.id!);
    });
    this.strip.addEventListener('keydown', this.onStripKey);
  }

  get active(): string {
    return this.activeId;
  }

  get activeText(): string {
    return this.ws.sheets.get(this.activeId)?.text ?? '';
  }

  render(): void {
    const sheets = this.ws.sheetList;
    if (!sheets.some((s) => s.id === this.activeId)) {
      const remembered = prefs().activeSheet;
      this.activeId = (sheets.find((s) => s.id === remembered) ?? this.ws.ensureSheet()).id;
      this.editor.value = this.activeText;
    }
    this.renderStrip();
    this.syncEditor();
  }

  /** Shows the model's text unless you're mid-edit (then your typing wins and is saved). */
  private syncEditor(): void {
    if (document.activeElement === this.editor) return;
    const text = this.activeText;
    if (this.editor.value !== text) this.editor.value = text;
  }

  private renderStrip(): void {
    const sheets = this.ws.sheetList;
    const before = !reducedMotion() && this.strip.isConnected ? measure(this.strip) : null;
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
    this.strip.classList.toggle('single', sheets.length <= 1);
    if (before) playFlip(this.strip, before);
  }

  select(id: string, focusEditor = false): void {
    if (id === this.activeId) {
      if (focusEditor) this.editor.focus();
      return;
    }
    const leaving = this.ws.sheets.get(this.activeId);
    this.scrollMemory.set(this.activeId, { top: this.editor.scrollTop, caret: this.editor.selectionStart });
    const goingRight = this.ws.sheetList.findIndex((s) => s.id === id) > this.ws.sheetList.findIndex((s) => s.id === this.activeId);
    this.activeId = id;
    setPrefs({ activeSheet: id });
    // An empty sheet you walk away from isn't worth keeping.
    if (leaving && !leaving.text.trim() && this.ws.sheetList.length > 1) this.ws.deleteSheet(leaving.id);
    this.editor.value = this.activeText;
    const memory = this.scrollMemory.get(id);
    this.editor.scrollTop = memory?.top ?? 0;
    this.renderStrip();
    this.strip.querySelector<HTMLElement>(`[data-id="${id}"]`)?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' });
    animate(this.editor, [{ opacity: 0, transform: `translateX(${goingRight ? 14 : -14}px)` }, { opacity: 1, transform: 'none' }], {
      duration: 240,
      easing: EASE_OUT,
    });
    if (focusEditor) {
      this.editor.focus({ preventScroll: true });
      const caret = memory?.caret ?? this.editor.value.length;
      this.editor.setSelectionRange(caret, caret);
    }
  }

  newSheet(): void {
    const current = this.ws.sheets.get(this.activeId);
    if (current && !current.text.trim()) {
      // Already on a blank sheet: just start writing.
      this.editor.focus();
      return;
    }
    haptics.tick();
    const sheet = this.ws.addSheet();
    this.select(sheet.id, true);
  }

  deleteActive(): void {
    const sheet = this.ws.sheets.get(this.activeId);
    if (!sheet) return;
    const list = this.ws.sheetList;
    const index = list.findIndex((s) => s.id === sheet.id);
    const wasOnly = list.length <= 1;
    if (wasOnly && !sheet.text.trim()) return;
    const undo = this.ws.deleteSheet(sheet.id);
    haptics.warn();
    if (!wasOnly) {
      const next = this.ws.sheetList[Math.min(index, this.ws.sheetList.length - 1)];
      this.activeId = '';
      this.render();
      if (next) this.select(next.id);
    }
    this.editor.value = this.activeText;
    const title = sheet.text.trim() ? `“${shorten(sheetTitle(sheet.text), 24)}”` : 'sheet';
    this.ctx.toasts.show(wasOnly ? 'Sheet cleared' : `Deleted ${title}`, () => {
      undo();
      this.select(sheet.id);
      this.editor.value = this.activeText;
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
    const { added, undo } = this.ws.addTasks(lines);
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
    }
  };

  private onStripKey = (e: KeyboardEvent) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    const list = this.ws.sheetList;
    const i = list.findIndex((s) => s.id === this.activeId);
    const next = list[(i + (e.key === 'ArrowRight' ? 1 : -1) + list.length) % list.length];
    if (!next) return;
    e.preventDefault();
    this.select(next.id);
    this.strip.querySelector<HTMLElement>(`[data-id="${next.id}"]`)?.focus();
  };

  /** Next sheet (Ctrl+Tab on desktop). */
  cycle(delta: number): void {
    const list = this.ws.sheetList;
    const i = list.findIndex((s) => s.id === this.activeId);
    const next = list[(i + delta + list.length) % list.length];
    if (next) this.select(next.id, document.activeElement === this.editor);
  }
}

let inserting = false;

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
