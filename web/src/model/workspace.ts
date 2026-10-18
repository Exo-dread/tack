import { Hlc, ZERO, type Stamp } from './hlc';
import { newId } from './ids';
import { cloneSheet, cloneTask, mergeFocus, mergeSheet, mergeTask } from './merge';
import { keyAfter, keyBefore, keysAt } from './order';
import { cleanTaskText, noteText } from './text';
import { isoLocal, parseDate, sameLocalDay } from './time';
import type { Focus, Sheet, Task, WorkspaceData } from './types';

/** Where a change came from. Changes loaded from this device's own storage needn't be saved again. */
export type Origin = 'local' | 'storage' | 'remote';

export interface Change {
  tasks: Set<string>;
  sheets: Set<string>;
  focus: boolean;
  origin: Origin;
}

export type Undo = () => void;

export interface Summary {
  open: number;
  doneToday: number;
  /** Today's finished share of work, 0..1: done today ÷ (done today + open). */
  progress: number;
}

const byOrder = (a: { order: string; id: string }, b: { order: string; id: string }) =>
  a.order < b.order ? -1 : a.order > b.order ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0;

/**
 * Everything the user owns: tasks, the one task in focus ("Now") and note sheets.
 * Every mutation goes through here, is stamped with the clock, and is announced to
 * subscribers (the UI and the store) as one batched Change.
 */
export class Workspace {
  readonly tasks = new Map<string, Task>();
  readonly sheets = new Map<string, Sheet>();
  focus: Focus = { id: null, clk: ZERO };

  private readonly listeners = new Set<(change: Change) => void>();
  private pending: Change | null = null;
  private depth = 0;
  private views: { open?: Task[]; done?: Task[]; sheets?: Sheet[] } = {};

  constructor(
    readonly hlc: Hlc,
    private readonly clock: () => Date = () => new Date(),
  ) {}

  subscribe(listener: (change: Change) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  // ---------------------------------------------------------------- views

  /** The task in focus, if it's still open. */
  get now(): Task | null {
    const id = this.focus.id;
    if (!id) return null;
    const t = this.tasks.get(id);
    return t && !t.deleted && !t.completed ? t : null;
  }

  /** Open tasks in the user's order, excluding the task in focus. */
  get open(): Task[] {
    if (!this.views.open) {
      const nowId = this.now?.id;
      this.views.open = [...this.tasks.values()]
        .filter((t) => !t.deleted && !t.completed && t.id !== nowId)
        .sort(byOrder);
    }
    return this.views.open;
  }

  /** Finished tasks, most recent first. */
  get done(): Task[] {
    if (!this.views.done) {
      const time = (t: Task) => parseDate(t.completed)?.getTime() ?? 0;
      this.views.done = [...this.tasks.values()]
        .filter((t) => !t.deleted && t.completed)
        .sort((a, b) => time(b) - time(a) || byOrder(a, b));
    }
    return this.views.done;
  }

  get sheetList(): Sheet[] {
    if (!this.views.sheets) {
      this.views.sheets = [...this.sheets.values()].filter((s) => !s.deleted).sort(byOrder);
    }
    return this.views.sheets;
  }

  summary(): Summary {
    const today = this.clock();
    const open = this.open.length + (this.now ? 1 : 0);
    let doneToday = 0;
    for (const t of this.done) {
      const d = parseDate(t.completed);
      if (d && sameLocalDay(d, today)) doneToday++;
    }
    const total = open + doneToday;
    return { open, doneToday, progress: total === 0 ? 0 : doneToday / total };
  }

  // ---------------------------------------------------------------- tasks

  /** Adds a task at the top of the list (or straight into focus). */
  addTask(rawText: string, options: { asNow?: boolean } = {}): Task | null {
    const text = cleanTaskText(rawText);
    if (!text) return null;
    return this.batch(() => {
      const top = this.open[0]?.order;
      const c = this.hlc.now();
      const task: Task = {
        id: newId(),
        text,
        created: isoLocal(this.clock()),
        completed: null,
        order: top ? keyBefore(top) : 'V',
        deleted: false,
        clk: { text: c, completed: c, order: c, deleted: c },
      };
      this.putTask(task);
      if (options.asNow) {
        const previous = this.now;
        if (previous) this.setTaskField(previous, 'order', keyBefore(task.order));
        this.setFocus(task.id);
      }
      return task;
    });
  }

  /** Adds several tasks, keeping their order, at the top of the list. */
  addTasks(texts: string[]): { added: Task[]; undo: Undo } {
    const added: Task[] = [];
    this.batch(() => {
      for (let i = texts.length - 1; i >= 0; i--) {
        const t = this.addTask(texts[i]);
        if (t) added.unshift(t);
      }
    });
    return {
      added,
      undo: () => this.batch(() => added.forEach((t) => this.removeTask(t.id))),
    };
  }

  editTask(id: string, rawText: string): void {
    const t = this.tasks.get(id);
    const text = cleanTaskText(rawText);
    if (!t || !text || t.text === text) return;
    this.batch(() => this.setTaskField(t, 'text', text));
  }

  complete(id: string): void {
    const t = this.tasks.get(id);
    if (!t || t.deleted || t.completed) return;
    this.batch(() => {
      this.setTaskField(t, 'completed', isoLocal(this.clock()));
      if (this.focus.id === id) this.setFocus(null);
    });
  }

  /** A finished task goes back to the top of the list. */
  reopen(id: string): void {
    const t = this.tasks.get(id);
    if (!t || t.deleted || !t.completed) return;
    this.batch(() => {
      const top = this.open[0]?.order;
      this.setTaskField(t, 'completed', null);
      this.setTaskField(t, 'order', top ? keyBefore(top) : 'V');
    });
  }

  /** Puts a task in focus; the previous focus task takes its place in the list. */
  setNow(id: string): void {
    const t = this.tasks.get(id);
    if (!t || t.deleted || t.completed || this.now?.id === id) return;
    this.batch(() => {
      const previous = this.now;
      if (previous) {
        const list = this.open;
        const index = list.findIndex((x) => x.id === id);
        this.setOrderAt(previous, list.filter((x) => x.id !== id), Math.max(0, index));
      }
      this.setFocus(id);
    });
  }

  /** Takes the focus task out of focus; it goes to the top of the list. */
  clearNow(): void {
    const t = this.now;
    if (!t) return;
    this.batch(() => {
      const top = this.open[0]?.order;
      this.setTaskField(t, 'order', top ? keyBefore(top) : 'V');
      this.setFocus(null);
    });
  }

  /** Deletes a task; the returned action puts it back exactly where it was (and in focus, if it was). */
  deleteTask(id: string): Undo {
    const t = this.tasks.get(id);
    if (!t || t.deleted) return () => {};
    const wasNow = this.now?.id === id;
    this.batch(() => this.removeTask(id));
    return () =>
      this.batch(() => {
        const task = this.tasks.get(id);
        if (!task) return;
        this.setTaskField(task, 'deleted', false);
        if (wasNow && !task.completed) {
          const current = this.now;
          if (current && current.id !== id) {
            const top = this.open[0]?.order;
            this.setTaskField(current, 'order', top ? keyBefore(top) : 'V');
          }
          this.setFocus(id);
        }
      });
  }

  /** Moves an open task to `index` in the open list (as it would be without the task). */
  moveTask(id: string, index: number): void {
    const t = this.tasks.get(id);
    if (!t || t.deleted || t.completed) return;
    const others = this.open.filter((x) => x.id !== id);
    const clamped = Math.max(0, Math.min(index, others.length));
    const current = this.open.findIndex((x) => x.id === id);
    if (current === clamped) return;
    this.batch(() => {
      if (this.now?.id === id) this.setFocus(null);
      this.setOrderAt(t, others, clamped);
    });
  }

  clearDone(): Undo {
    const ids = this.done.map((t) => t.id);
    if (ids.length === 0) return () => {};
    this.batch(() => ids.forEach((id) => this.removeTask(id)));
    return () =>
      this.batch(() => {
        for (const id of ids) {
          const t = this.tasks.get(id);
          if (t) this.setTaskField(t, 'deleted', false);
        }
      });
  }

  // ---------------------------------------------------------------- sheets

  addSheet(text = ''): Sheet {
    return this.batch(() => {
      const last = this.sheetList.at(-1)?.order;
      const c = this.hlc.now();
      const sheet: Sheet = {
        id: newId(),
        text: noteText(text),
        updated: isoLocal(this.clock()),
        order: last ? keyAfter(last) : 'V',
        deleted: false,
        clk: { text: c, order: c, deleted: c },
      };
      this.putSheet(sheet);
      return sheet;
    });
  }

  editSheet(id: string, rawText: string): void {
    const s = this.sheets.get(id);
    const text = noteText(rawText);
    if (!s || s.deleted || s.text === text) return;
    this.batch(() => {
      const next = cloneSheet(s);
      next.text = text;
      next.updated = isoLocal(this.clock());
      next.clk.text = this.hlc.now();
      this.putSheet(next);
    });
  }

  /** Deletes a sheet. The last remaining sheet is cleared instead. Returns an undo action. */
  deleteSheet(id: string): Undo {
    const s = this.sheets.get(id);
    if (!s || s.deleted) return () => {};
    if (this.sheetList.length <= 1) {
      const text = s.text;
      this.editSheet(id, '');
      return () => this.editSheet(id, text);
    }
    this.batch(() => this.setSheetDeleted(s, true));
    return () =>
      this.batch(() => {
        const sheet = this.sheets.get(id);
        if (sheet) this.setSheetDeleted(sheet, false);
      });
  }

  /** There is always at least one sheet to write on. */
  ensureSheet(): Sheet {
    return this.sheetList[0] ?? this.addSheet();
  }

  // ---------------------------------------------------------------- loading and merging

  /**
   * Merges records from elsewhere (this device's storage, another tab, a file or another
   * device) into the workspace. Newer edits win field by field; nothing newer is lost.
   */
  merge(data: Partial<WorkspaceData>, origin: Origin, base?: Stamp): void {
    this.batch(() => {
      if (this.depth === 1) this.current().origin = origin;
      for (const remote of data.tasks ?? []) {
        this.hlc.observe(maxClock(remote.clk));
        const { value, changed } = mergeTask(this.tasks.get(remote.id), remote);
        if (changed) this.putTask(value, false);
      }
      for (const remote of data.sheets ?? []) {
        this.hlc.observe(maxClock(remote.clk));
        const { value, changed, conflict } = mergeSheet(this.sheets.get(remote.id), remote, base);
        if (changed) this.putSheet(value, false);
        if (conflict && !this.sheets.has(conflict.id)) this.putSheet(conflict, false);
      }
      if (data.focus) {
        this.hlc.observe(data.focus.clk);
        const { value, changed } = mergeFocus(this.focus, data.focus);
        if (changed) {
          this.focus = value;
          this.current().focus = true;
          this.views.open = undefined;
        }
      }
    });
  }

  /**
   * Makes the workspace look exactly like `data` (a snapshot taken earlier), as fresh edits:
   * records that differ are rewritten, records that didn't exist then are deleted. Because
   * the edits are new, they win over the state they replace everywhere it has spread.
   */
  restore(data: WorkspaceData): void {
    const tasks = new Map(data.tasks.map((t) => [t.id, t]));
    const sheets = new Map(data.sheets.map((s) => [s.id, s]));
    this.batch(() => {
      for (const id of new Set([...this.tasks.keys(), ...tasks.keys()])) {
        const target = tasks.get(id);
        const current = this.tasks.get(id);
        if (!current) {
          if (target && !target.deleted) {
            const c = this.hlc.now();
            this.putTask({ ...cloneTask(target), clk: { text: c, completed: c, order: c, deleted: c } });
          }
          continue;
        }
        const want = target ?? { ...current, deleted: true };
        if (current.text !== want.text && want.text) this.setTaskField(current, 'text', want.text);
        if (current.completed !== want.completed) this.setTaskField(current, 'completed', want.completed);
        if (current.order !== want.order) this.setTaskField(current, 'order', want.order);
        if (current.deleted !== want.deleted) this.setTaskField(current, 'deleted', want.deleted);
      }
      for (const id of new Set([...this.sheets.keys(), ...sheets.keys()])) {
        const target = sheets.get(id);
        const current = this.sheets.get(id);
        if (!current) {
          if (target && !target.deleted) {
            const c = this.hlc.now();
            this.putSheet({ ...cloneSheet(target), clk: { text: c, order: c, deleted: c } });
          }
          continue;
        }
        const want = target ?? { ...current, deleted: true };
        if (current.deleted && !want.deleted) this.setSheetDeleted(current, false);
        if (current.text !== want.text && !want.deleted) this.editSheet(id, want.text);
        if (!current.deleted && want.deleted) this.setSheetDeleted(current, true);
      }
      if (this.focus.id !== data.focus.id) this.setFocus(data.focus.id);
    });
  }

  /** Copies of every record, e.g. for export or a snapshot. */
  toData(): WorkspaceData {
    return {
      tasks: [...this.tasks.values()].map(cloneTask),
      sheets: [...this.sheets.values()].map(cloneSheet),
      focus: { ...this.focus },
    };
  }

  /** Removes tombstones older than `maxAgeMs` for good. Returns the ids removed. */
  purgeTombstones(maxAgeMs: number, stampTime: (s: Stamp) => number): { tasks: string[]; sheets: string[] } {
    const cutoff = this.clock().getTime() - maxAgeMs;
    const tasks: string[] = [];
    const sheets: string[] = [];
    for (const t of this.tasks.values()) {
      if (t.deleted && stampTime(t.clk.deleted) < cutoff) tasks.push(t.id);
    }
    for (const s of this.sheets.values()) {
      if (s.deleted && stampTime(s.clk.deleted) < cutoff) sheets.push(s.id);
    }
    tasks.forEach((id) => this.tasks.delete(id));
    sheets.forEach((id) => this.sheets.delete(id));
    if (tasks.length || sheets.length) this.views = {};
    return { tasks, sheets };
  }

  // ---------------------------------------------------------------- internals

  /** Runs mutations as one change: subscribers hear about it once, at the end. */
  batch<T>(fn: () => T): T {
    this.depth++;
    try {
      return fn();
    } finally {
      this.depth--;
      if (this.depth === 0 && this.pending) {
        const change = this.pending;
        this.pending = null;
        if (change.tasks.size || change.sheets.size || change.focus) {
          for (const l of this.listeners) l(change);
        }
      }
    }
  }

  private current(): Change {
    if (!this.pending) this.pending = { tasks: new Set(), sheets: new Set(), focus: false, origin: 'local' };
    return this.pending;
  }

  private putTask(task: Task, observe = true): void {
    if (observe) this.hlc.observe(maxClock(task.clk));
    this.tasks.set(task.id, task);
    this.current().tasks.add(task.id);
    this.views.open = undefined;
    this.views.done = undefined;
  }

  private putSheet(sheet: Sheet, observe = true): void {
    if (observe) this.hlc.observe(maxClock(sheet.clk));
    this.sheets.set(sheet.id, sheet);
    this.current().sheets.add(sheet.id);
    this.views.sheets = undefined;
  }

  private setTaskField<K extends 'text' | 'completed' | 'order' | 'deleted'>(task: Task, field: K, value: Task[K]): void {
    const next = cloneTask(this.tasks.get(task.id) ?? task);
    next[field] = value;
    next.clk[field] = this.hlc.now();
    this.putTask(next);
  }

  /**
   * Gives `task` the place at `index` in `list` (open tasks in order, without it). Neighbours
   * that share a key with the one above get new keys too, so the task really lands there.
   */
  private setOrderAt(task: Task, list: readonly Task[], index: number): void {
    const keys = keysAt(list.map((x) => x.order), index);
    this.setTaskField(task, 'order', keys[0]!);
    for (let i = 1; i < keys.length; i++) this.setTaskField(list[index + i - 1]!, 'order', keys[i]!);
  }

  private setSheetDeleted(sheet: Sheet, deleted: boolean): void {
    const next = cloneSheet(this.sheets.get(sheet.id) ?? sheet);
    next.deleted = deleted;
    next.clk.deleted = this.hlc.now();
    this.putSheet(next);
  }

  private removeTask(id: string): void {
    const t = this.tasks.get(id);
    if (!t || t.deleted) return;
    if (this.focus.id === id) this.setFocus(null);
    this.setTaskField(t, 'deleted', true);
  }

  private setFocus(id: string | null): void {
    this.focus = { id, clk: this.hlc.now() };
    this.current().focus = true;
    this.views.open = undefined;
  }
}

function maxClock(clocks: object): Stamp {
  let max = ZERO;
  for (const v of Object.values(clocks)) if (typeof v === 'string' && v > max) max = v;
  return max;
}
