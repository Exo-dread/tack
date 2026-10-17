import { cleanTaskText, shorten } from '../model/text';
import type { Task } from '../model/types';
import type { Workspace } from '../model/workspace';
import { prefs, setPrefs } from '../store/prefs';
import { coarsePointer, h, isEditable, svg } from './dom';
import { RowGestures } from './gestures';
import { haptics } from './haptics';
import { checkMark, icons } from './icons';
import { animate, EASE_OUT, exitInPlace, measure, playFlip, POP, reducedMotion, SPRING } from './motion';
import { Orb } from './orb';
import type { Toasts } from './toast';

/** Ticking a task plays its animation, then it waits this long (a second tap un-ticks it) before moving to Done. */
const GRACE_MS = 850;

/** Beyond this many rows, rows off screen skip layout and paint. */
const LONG_LIST = 60;

export interface TasksContext {
  toasts: Toasts;
  celebrate(): void;
  dockInset(): number;
}

type Where = 'now' | 'open' | 'done';

interface Editing {
  id: string;
  row: HTMLElement;
  input: HTMLTextAreaElement;
  label: HTMLElement;
  tools: HTMLElement;
}

/**
 * The Tasks page: the task in focus as a card at the top, the open list below it, and a
 * folding Done section. Rendering is keyed and animated with FLIP, so whatever changes
 * (a tick, a swipe, a reorder, another tab) every task glides to its new place.
 */
export class TasksView {
  readonly el: HTMLElement;
  private readonly nowSlot: HTMLElement;
  private readonly openList: HTMLElement;
  private readonly empty: HTMLElement;
  private readonly emptyOrb = new Orb('orb-large');
  private readonly doneSection: HTMLElement;
  private readonly doneList: HTMLElement;
  private readonly doneToggle: HTMLButtonElement;
  private readonly doneCount: HTMLElement;
  private readonly openRows = new Map<string, HTMLElement>();
  private readonly doneRows = new Map<string, HTMLElement>();
  private nowCard: HTMLElement | null = null;
  private readonly checking = new Map<string, ReturnType<typeof setTimeout>>();
  private editing: Editing | null = null;
  private readonly gestures: RowGestures;
  private deferred = false;
  private enterFrom: DOMRect | null = null;
  private keepFocus = false;

  constructor(
    private readonly ws: Workspace,
    private readonly ctx: TasksContext,
  ) {
    this.nowSlot = h('div.now-slot');
    this.openList = h('ul.list.open-list', { 'aria-label': 'Open tasks' });
    this.empty = h(
      'div.empty',
      { hidden: true },
      this.emptyOrb.el,
      h('h2.empty-title'),
      h('p.empty-text'),
    );
    this.doneCount = h('span.count');
    this.doneToggle = h(
      'button.done-toggle',
      { type: 'button', 'aria-expanded': 'false', onclick: () => this.toggleDone() },
      svg(icons.chevron),
      h('span', { text: 'Done' }),
      this.doneCount,
    );
    this.doneList = h('ul.list.done-list', { 'aria-label': 'Done tasks' });
    this.doneSection = h(
      'section.done',
      { hidden: true, 'data-flip': 'done' },
      h(
        'div.done-head',
        {},
        this.doneToggle,
        h('button.text-button.clear-done', { type: 'button', text: 'Clear', onclick: () => this.clearDone() }),
      ),
      this.doneList,
    );
    this.el = h(
      'section.page.tasks#page-tasks',
      { role: 'tabpanel', 'aria-labelledby': 'tab-tasks', tabindex: '-1' },
      h('div.page-inner', {}, this.nowSlot, this.openList, this.empty, this.doneSection),
    );

    this.el.addEventListener('click', this.onClick);
    this.el.addEventListener('keydown', this.onKeyDown);

    this.gestures = new RowGestures(this.el, {
      scroller: this.el,
      rowSelector: 'li.row',
      swipeActions: (row) => {
        if (row.parentElement === this.doneList) {
          return {
            right: { label: 'Reopen', icon: icons.reopen, tone: 'neutral' },
            left: { label: 'Delete', icon: icons.trash, tone: 'danger' },
          };
        }
        return {
          right: { label: 'Focus', icon: icons.focus, tone: 'accent' },
          left: { label: 'Delete', icon: icons.trash, tone: 'danger' },
        };
      },
      onSwipe: (row, dir) => {
        const id = row.dataset.id!;
        if (dir === 'left') this.remove(id);
        else if (row.parentElement === this.doneList) this.ws.reopen(id);
        else this.focusTask(id);
      },
      canDrag: (row) => row.parentElement === this.openList && this.ws.open.length > 1,
      onDrop: (row, index) => this.ws.moveTask(row.dataset.id!, index),
      bottomInset: () => this.ctx.dockInset(),
      isBusy: () => this.editing !== null,
      onIdle: () => {
        if (this.deferred) this.render();
      },
    });

    this.el.addEventListener('scroll', () => this.el.classList.toggle('scrolled', this.el.scrollTop > 2), { passive: true });
  }

  // ================================================================== rendering

  /** Next new task animates from here (the composer), as if lifted into the list. */
  primeEnter(rect: DOMRect): void {
    this.enterFrom = rect;
  }

  render(animated = true): void {
    if (this.gestures.active) {
      this.deferred = true;
      return;
    }
    this.deferred = false;
    const motion = animated && !document.hidden && this.el.offsetParent !== null && !reducedMotion();
    const before = motion ? measure(this.el) : null;
    const focused = document.activeElement;
    const offsets = new Map<HTMLElement, number>();
    const record = (el: HTMLElement | null) => el && offsets.set(el, el.offsetTop);
    this.openRows.forEach(record);
    this.doneRows.forEach(record);
    record(this.nowCard);
    this.gestures.release();

    const removed: HTMLElement[] = [];

    // --- focus card
    const now = this.ws.now;
    if (this.nowCard && (!now || this.nowCard.dataset.id !== now.id)) {
      removed.push(this.nowCard);
      this.nowCard = null;
    }
    if (now) {
      if (!this.nowCard) {
        this.nowCard = this.createNowCard(now);
        this.nowSlot.append(this.nowCard);
      } else {
        this.updateText(this.nowCard, now);
      }
    }

    // --- open and done lists
    removed.push(...this.reconcile(this.openList, this.openRows, this.ws.open, 'open'));
    // Long lists skip layout for rows off screen (see .long in the CSS).
    this.openList.classList.toggle('long', this.openRows.size > LONG_LIST);
    const showDone = prefs().doneExpanded;
    removed.push(...this.reconcile(this.doneList, this.doneRows, showDone ? this.ws.done : [], 'done'));
    this.doneList.classList.toggle('long', this.doneRows.size > LONG_LIST);

    const doneCount = this.ws.done.length;
    this.doneSection.hidden = doneCount === 0;
    this.doneSection.classList.toggle('expanded', showDone);
    this.doneToggle.setAttribute('aria-expanded', String(showDone));
    if (this.doneCount.textContent !== String(doneCount)) {
      const bump = this.doneCount.textContent !== '' && motion;
      this.doneCount.textContent = String(doneCount);
      if (bump) animate(this.doneCount, [{ transform: 'scale(1.35)' }, { transform: 'none' }], { duration: POP.duration, easing: POP.easing });
    }

    this.renderEmpty();

    // Elements whose task now shows elsewhere (e.g. list → focus card) just vanish: the new
    // element glides from their position. Anything truly gone fades out where it stood.
    const liveKeys = new Set([...this.el.querySelectorAll<HTMLElement>('[data-flip]')].map((el) => el.dataset.flip));
    for (const el of removed) {
      if (!motion || liveKeys.has(el.dataset.flip)) el.remove();
      else exitInPlace(el, offsets.get(el) ?? el.offsetTop, el.classList.contains('swiping') ? 'away' : 'fade');
    }

    if (before) {
      playFlip(this.el, before);
      this.animateEntrances(before);
    }
    this.enterFrom = null;

    if (focused instanceof HTMLElement && focused !== document.activeElement && focused.isConnected) {
      focused.focus({ preventScroll: true });
    }
  }

  /** New tasks lift from the composer into place (the first few; a pasted list mostly just appears). */
  private animateEntrances(before: Map<string, DOMRect>): void {
    const from = this.enterFrom;
    if (!from) return;
    const arrivals: HTMLElement[] = [];
    for (const [id, row] of this.openRows) {
      if (!before.has(`t:${id}`)) arrivals.push(row);
      if (arrivals.length === 8) break;
    }
    // All reads, then all writes (see playFlip).
    const targets = arrivals.map((row) => ({ row, to: row.getBoundingClientRect() }));
    targets.forEach(({ row, to }, i) => {
      const dx = from.left - to.left;
      const dy = from.top + from.height / 2 - (to.top + to.height / 2);
      row.getAnimations().forEach((a) => a.cancel());
      animate(
        row,
        [
          { transform: `translate(${dx}px, ${dy}px) scale(.96)`, opacity: 0.35 },
          { transform: 'none', opacity: 1 },
        ],
        { duration: SPRING.duration + 80, easing: SPRING.easing, delay: i * 35, fill: 'backwards' },
      );
    });
  }

  private reconcile(list: HTMLElement, rows: Map<string, HTMLElement>, tasks: Task[], where: Where): HTMLElement[] {
    const keep = new Set(tasks.map((t) => t.id));
    const removed: HTMLElement[] = [];
    for (const [id, el] of rows) {
      if (!keep.has(id)) {
        rows.delete(id);
        removed.push(el);
      }
    }
    let cursor: HTMLElement | null = null;
    for (const t of tasks) {
      let el = rows.get(t.id);
      if (!el) {
        el = this.createRow(t, where);
        rows.set(t.id, el);
      } else {
        this.updateText(el, t);
      }
      if (el.parentElement !== list || previousLive(el) !== cursor) {
        if (cursor) cursor.after(el);
        else list.prepend(el);
      }
      cursor = el;
    }
    return removed;
  }

  private renderEmpty(): void {
    const { open, doneToday } = this.ws.summary();
    const isEmpty = open === 0;
    this.empty.hidden = !isEmpty;
    this.el.classList.toggle('is-empty', isEmpty);
    if (!isEmpty) return;
    this.emptyOrb.update(0, doneToday, doneToday ? 1 : 0);
    const title = this.empty.querySelector('.empty-title')!;
    const text = this.empty.querySelector('.empty-text')!;
    if (doneToday > 0) {
      title.textContent = 'All clear';
      text.textContent = doneToday === 1 ? 'One thing done today.' : `${doneToday} things done today.`;
    } else {
      title.textContent = 'A clean slate';
      text.textContent = 'Type a task and press return. It’s saved the moment you do.';
    }
  }

  // ================================================================== elements

  private createRow(t: Task, where: Where): HTMLElement {
    const done = where === 'done';
    const check = h('button.check', {
      type: 'button',
      role: 'checkbox',
      'aria-checked': String(done),
      'aria-label': done ? 'Done. Tap to reopen' : 'Mark as done',
    });
    check.append(svg(checkMark));
    const label = h('button.text', { type: 'button', 'aria-label': `${t.text}. Edit` }, h('span.label', { text: t.text }));
    const actions = h(
      'div.hover-actions',
      {},
      done
        ? null
        : h('button.icon-button.act-focus', { type: 'button', title: 'Focus on this (Ctrl+Enter)', 'aria-label': 'Focus on this' }, svg(icons.focus)),
      h('button.icon-button.act-delete', { type: 'button', title: 'Delete', 'aria-label': 'Delete' }, svg(icons.trash)),
    );
    const li = h('li.row', { 'data-id': t.id, 'data-flip': `t:${t.id}` }, h('div.row-body', {}, check, label, actions));
    if (done) li.classList.add('is-done');
    return li;
  }

  private createNowCard(t: Task): HTMLElement {
    const check = h('button.check.big', { type: 'button', role: 'checkbox', 'aria-checked': 'false', 'aria-label': 'Mark as done' });
    check.append(svg(checkMark));
    return h(
      'div.now-card',
      { 'data-id': t.id, 'data-flip': `t:${t.id}`, role: 'group', 'aria-label': 'Now' },
      h(
        'div.now-head',
        {},
        h('span.overline', {}, h('span.now-dot'), 'Now'),
        h('button.icon-button.unfocus', { type: 'button', title: 'Back to the list', 'aria-label': 'Stop focusing on this' }, svg(icons.unfocus)),
      ),
      h('div.row-body', {}, check, h('button.text.display', { type: 'button', 'aria-label': `${t.text}. Edit` }, h('span.label', { text: t.text }))),
    );
  }

  private updateText(el: HTMLElement, t: Task): void {
    // Most renders change nothing about most rows; skip the DOM entirely for those.
    if (shownText.get(el) === t.text) return;
    shownText.set(el, t.text);
    setLabel(el.querySelector<HTMLElement>(':scope > .row-body > .text'), t.text);
  }

  // ================================================================== interaction

  private where(el: HTMLElement): { id: string; where: Where; row: HTMLElement } | null {
    const row = el.closest<HTMLElement>('li.row, .now-card');
    if (!row || row.classList.contains('exiting')) return null;
    const id = row.dataset.id!;
    const where: Where = row.classList.contains('now-card') ? 'now' : row.parentElement === this.doneList ? 'done' : 'open';
    return { id, where, row };
  }

  private onClick = (e: MouseEvent) => {
    const target = e.target as HTMLElement;
    const hit = this.where(target);
    if (!hit) return;
    const { id, where, row } = hit;
    if (target.closest('.check')) {
      if (where === 'done') {
        haptics.tick();
        this.ws.reopen(id);
      } else {
        this.toggleCheck(id, row);
      }
    } else if (target.closest('.text')) {
      this.startEdit(id, row, where);
    } else if (target.closest('.act-focus')) {
      this.focusTask(id);
    } else if (target.closest('.act-delete')) {
      this.remove(id);
    } else if (target.closest('.unfocus')) {
      this.ws.clearNow();
    }
  };

  private toggleCheck(id: string, row: HTMLElement): void {
    const check = row.querySelector<HTMLElement>('.check')!;
    const pending = this.checking.get(id);
    if (pending) {
      clearTimeout(pending);
      this.checking.delete(id);
      row.classList.remove('checking');
      check.setAttribute('aria-checked', 'false');
      return;
    }
    row.classList.add('checking');
    check.setAttribute('aria-checked', 'true');
    haptics.tick();
    burst(check);
    this.checking.set(
      id,
      setTimeout(() => {
        this.checking.delete(id);
        this.complete(id);
      }, GRACE_MS),
    );
  }

  private complete(id: string): void {
    const task = this.ws.tasks.get(id);
    const before = this.ws.summary();
    this.ws.complete(id);
    const after = this.ws.summary();
    if (task) this.ctx.toasts.announce(`Done: ${task.text}`);
    if (before.open > 0 && after.open === 0) {
      haptics.success();
      this.ctx.celebrate();
    }
  }

  /** Commits any ticks still in their grace period (the app is being hidden or closed). */
  commitPending(): void {
    for (const [id, timer] of this.checking) {
      clearTimeout(timer);
      this.checking.delete(id);
      this.ws.complete(id);
    }
    if (this.editing) this.finishEdit(false);
  }

  private focusTask(id: string): void {
    haptics.tick();
    this.ws.setNow(id);
    const t = this.ws.tasks.get(id);
    if (t) this.ctx.toasts.announce(`Now: ${t.text}`);
    this.el.scrollTo({ top: 0, behavior: reducedMotion() ? 'auto' : 'smooth' });
  }

  private remove(id: string): void {
    const t = this.ws.tasks.get(id);
    if (!t) return;
    const pending = this.checking.get(id);
    if (pending) {
      clearTimeout(pending);
      this.checking.delete(id);
    }
    const undo = this.ws.deleteTask(id);
    this.ctx.toasts.show(`Deleted “${shorten(t.text, 28)}”`, undo);
  }

  private clearDone(): void {
    const n = this.ws.done.length;
    const undo = this.ws.clearDone();
    this.ctx.toasts.show(n === 1 ? 'Cleared 1 done task' : `Cleared ${n} done tasks`, undo);
  }

  private toggleDone(): void {
    setPrefs({ doneExpanded: !prefs().doneExpanded });
    this.render();
  }

  // ================================================================== editing in place

  private startEdit(id: string, row: HTMLElement, where: Where): void {
    if (this.editing?.id === id) return;
    if (this.editing) this.finishEdit(false);
    const task = this.ws.tasks.get(id);
    const label = row.querySelector<HTMLElement>(':scope > .row-body > .text');
    if (!task || !label) return;

    const input = h('textarea.edit', {
      rows: 1,
      enterkeyhint: 'done',
      autocapitalize: 'sentences',
      spellcheck: 'true',
      'aria-label': 'Edit task',
    });
    if (label.classList.contains('display')) input.classList.add('display');
    input.value = task.text;
    const tool = (cls: string, text: string, icon: string, handler: () => void, iconOnly = false) =>
      h(
        'button.tool',
        { class: cls, type: 'button', onclick: handler, 'aria-label': iconOnly ? text : null, title: iconOnly ? text : null },
        svg(icon),
        iconOnly ? null : h('span', { text }),
      );
    const tools = h(
      'div.row-tools',
      {},
      where === 'open' ? tool('t-focus', 'Focus', icons.focus, () => this.editAction(() => this.focusTask(id))) : null,
      where === 'now' ? tool('t-unfocus', 'To list', icons.down, () => this.editAction(() => this.ws.clearNow())) : null,
      where === 'open' ? tool('t-up', 'Move up', icons.up, () => this.nudge(id, -1), true) : null,
      where === 'open' ? tool('t-down', 'Move down', icons.down, () => this.nudge(id, 1), true) : null,
      where === 'done' ? tool('t-reopen', 'Reopen', icons.reopen, () => this.editAction(() => this.ws.reopen(id))) : null,
      tool('t-delete', 'Delete', icons.trash, () => this.editAction(() => this.remove(id))),
    );
    // Tapping a tool mustn't take focus from the text (that would close the keyboard).
    tools.addEventListener('pointerdown', (e) => e.preventDefault());

    label.replaceWith(input);
    row.classList.add('editing');
    row.append(tools);
    this.editing = { id, row, input, label, tools };
    autosize(input);
    input.focus({ preventScroll: true });
    input.setSelectionRange(input.value.length, input.value.length);
    animate(tools, [{ opacity: 0, transform: 'translateY(-6px)' }, { opacity: 1, transform: 'none' }], { duration: 220, easing: EASE_OUT });
    this.revealEditing();

    input.addEventListener('input', () => {
      if (/[\r\n]/.test(input.value)) {
        // A pasted or dictated newline: tasks are one line.
        const caret = input.selectionStart;
        input.value = input.value.replace(/[\r\n]+/g, ' ');
        input.setSelectionRange(caret, caret);
      }
      autosize(input);
    });
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        if ((e.ctrlKey || e.metaKey) && where === 'open') this.editAction(() => this.focusTask(id));
        else this.finishEdit(false, true);
      } else if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        this.finishEdit(true, true);
      } else if (e.key === 'Backspace' && input.value === '') {
        e.preventDefault();
        this.editAction(() => this.remove(id));
      }
    });
    input.addEventListener('blur', () => {
      // Moving the row (Up/Down) briefly blurs it; that's not the end of the edit.
      if (this.keepFocus) return;
      setTimeout(() => {
        if (this.editing?.input === input && document.activeElement !== input) this.finishEdit(false);
      }, 0);
    });
  }

  /** Keeps the edited row in view once the keyboard has opened. */
  revealEditing(): void {
    const e = this.editing;
    if (!e) return;
    requestAnimationFrame(() => {
      const r = e.row.getBoundingClientRect();
      const box = this.el.getBoundingClientRect();
      const bottom = box.bottom - this.ctx.dockInset() - 12;
      if (r.bottom > bottom) this.el.scrollBy({ top: r.bottom - bottom, behavior: 'smooth' });
      else if (r.top < box.top + 8) this.el.scrollBy({ top: r.top - box.top - 8, behavior: 'smooth' });
    });
  }

  private editAction(action: () => void): void {
    this.finishEdit(false);
    action();
  }

  private nudge(id: string, delta: number): void {
    const index = this.ws.open.findIndex((t) => t.id === id);
    if (index < 0) return;
    const e = this.editing;
    if (e) this.ws.editTask(id, e.input.value);
    this.keepFocus = true;
    this.ws.moveTask(id, index + delta);
    this.keepFocus = false;
    if (e && document.activeElement !== e.input) e.input.focus({ preventScroll: true });
    this.revealEditing();
  }

  private finishEdit(cancel: boolean, returnFocus = false): void {
    const e = this.editing;
    if (!e) return;
    this.editing = null;
    const text = cleanTaskText(e.input.value);
    const task = this.ws.tasks.get(e.id);
    if (task) setLabel(e.label, text || task.text);
    const hadFocus = document.activeElement === e.input;
    e.input.replaceWith(e.label);
    e.row.classList.remove('editing');
    e.tools.remove();
    // With a keyboard, focus stays on the task; on touch, the keyboard just goes away.
    if (returnFocus && hadFocus && !coarsePointer()) e.label.focus({ preventScroll: true });
    if (!cancel && task && !task.deleted) {
      if (!text) this.remove(e.id);
      else this.ws.editTask(e.id, text);
    }
    if (this.deferred) this.render();
  }

  // ================================================================== keyboard

  private onKeyDown = (e: KeyboardEvent) => {
    const target = e.target as HTMLElement;
    if (isEditable(target)) return;
    const hit = target.classList.contains('text') ? this.where(target) : null;
    if (!hit) return;
    const mod = e.ctrlKey || e.metaKey;
    const { id, where } = hit;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (e.altKey && where === 'open') {
        const index = this.ws.open.findIndex((t) => t.id === id);
        this.ws.moveTask(id, index + (e.key === 'ArrowDown' ? 1 : -1));
        this.openRows.get(id)?.querySelector<HTMLElement>('.text')?.focus();
      } else {
        this.moveFocus(target, e.key === 'ArrowDown' ? 1 : -1);
      }
    } else if (e.key === 'Enter' && mod && where === 'open') {
      e.preventDefault();
      this.focusTask(id);
    } else if ((e.key === 'd' || e.key === 'D') && mod && where !== 'done') {
      e.preventDefault();
      this.toggleCheck(id, hit.row);
    } else if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault();
      this.moveFocus(target, 1) || this.moveFocus(target, -1);
      this.remove(id);
    }
  };

  private moveFocus(from: HTMLElement, delta: number): boolean {
    const all = [...this.el.querySelectorAll<HTMLElement>('.now-card .text, li.row:not(.exiting) .text')];
    const next = all[all.indexOf(from) + delta];
    next?.focus();
    return !!next;
  }

  /** The first task's text, for keyboard users arriving on the page. */
  focusFirst(): void {
    this.el.querySelector<HTMLElement>('.now-card .text, li.row .text')?.focus();
  }

  get isEditing(): boolean {
    return this.editing !== null;
  }
}

/** The text each row last showed. */
const shownText = new WeakMap<HTMLElement, string>();

function setLabel(button: HTMLElement | null, text: string): void {
  const label = button?.querySelector('.label');
  if (!button || !label || label.textContent === text) return;
  label.textContent = text;
  button.setAttribute('aria-label', `${text}. Edit`);
}

function previousLive(el: Element): Element | null {
  let p = el.previousElementSibling;
  while (p && p.classList.contains('exiting')) p = p.previousElementSibling;
  return p;
}

function autosize(t: HTMLTextAreaElement): void {
  t.style.height = 'auto';
  t.style.height = `${t.scrollHeight}px`;
}

/** A small burst of light from a ticked circle. */
function burst(anchor: HTMLElement): void {
  if (reducedMotion() || typeof anchor.animate !== 'function') return;
  const n = 8;
  for (let i = 0; i < n; i++) {
    const p = h('span.spark', { 'aria-hidden': 'true' });
    anchor.append(p);
    const angle = (i / n) * Math.PI * 2 + (Math.random() - 0.5) * 0.5;
    const dist = 17 + Math.random() * 8;
    p.animate(
      [
        { transform: 'translate(-50%, -50%) scale(1)', opacity: 1 },
        { transform: `translate(calc(-50% + ${Math.cos(angle) * dist}px), calc(-50% + ${Math.sin(angle) * dist}px)) scale(.2)`, opacity: 0 },
      ],
      { duration: 520 + Math.random() * 120, easing: EASE_OUT, delay: 60 },
    ).finished.then(() => p.remove(), () => p.remove());
  }
}
