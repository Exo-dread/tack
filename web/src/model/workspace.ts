import { Hlc, ZERO, type Stamp } from './hlc';
import { newId } from './ids';
import { MAX_TASK_TEXT } from './limits';
import { cloneSheet, cloneSpace, cloneTask, mergeFocus, mergeSheet, mergeSpace, mergeTask, type ConcurrentText } from './merge';
import { keyAfter, keyBefore, keyBetween, keysAt } from './order';
import { ALL, cleanSpaceName, isSpaceId, MAIN, normalSheet, normalTask, spaceLabel } from './spaces';
import { cleanTaskText, noteText } from './text';
import { isoLocal, parseDate, sameLocalDay } from './time';
import type { Focus, Sheet, Space, Task, WorkspaceData } from './types';

/** Where a change came from. Changes loaded from this device's own storage needn't be saved again. */
export type Origin = 'local' | 'storage' | 'remote';

export interface Change {
  tasks: Set<string>;
  sheets: Set<string>;
  spaces: Set<string>;
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

/** A space as the lists show it: its id ('main' for Main), its own name, and the name shown. */
export interface SpaceTab {
  id: string;
  name: string;
  label: string;
}

const byOrder = (a: { order: string; id: string }, b: { order: string; id: string }) =>
  a.order < b.order ? -1 : a.order > b.order ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0;

/**
 * Everything the user owns: tasks, the one task in focus ("Now"), note sheets and the spaces
 * they're in. Every mutation goes through here, is stamped with the clock, and is announced to
 * subscribers (the UI and the store) as one batched Change.
 */
export class Workspace {
  readonly tasks = new Map<string, Task>();
  readonly sheets = new Map<string, Sheet>();
  /** Spaces (see spaces.ts). Focus and order stay global: one focus task, one order for all. */
  readonly spaces = new Map<string, Space>();
  focus: Focus = { id: null, clk: ZERO };

  private readonly listeners = new Set<(change: Change) => void>();
  private pending: Change | null = null;
  private depth = 0;
  private views: { open?: Task[]; done?: Task[]; sheets?: Sheet[]; spaces?: Space[]; tabs?: SpaceTab[]; live?: Set<string> } = {};

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

  /**
   * Live spaces in order (Main's record among them, once it has one). A record with All's id
   * (only a hand-made file has one) is never a space: it isn't listed, and what's in it is in Main.
   */
  get spaceList(): Space[] {
    if (!this.views.spaces) {
      this.views.spaces = [...this.spaces.values()].filter((s) => !s.deleted && s.id !== ALL).sort(byOrder);
    }
    return this.views.spaces;
  }

  /** Whether there's a space besides Main. Until there is, nothing about spaces shows anywhere. */
  get hasSpaces(): boolean {
    return this.spaceList.some((s) => s.id !== MAIN);
  }

  /**
   * Spaces as every list shows them: Main first while it has no place of its own (no record, or
   * a record that was only ever renamed: its order never stamped), then every live space (Main's
   * record among them, once it has a place) by order key, then id.
   */
  get spaceTabs(): readonly SpaceTab[] {
    if (!this.views.tabs) {
      const placed = this.mainHasPlace();
      const tabs = this.spaceList
        .filter((s) => placed || s.id !== MAIN)
        .map((s) => ({ id: s.id, name: s.name, label: spaceLabel(s.id, s.name) }));
      if (!placed) {
        const main = this.spaces.get(MAIN);
        const name = main && !main.deleted ? main.name : '';
        tabs.unshift({ id: MAIN, name, label: spaceLabel(MAIN, name) });
      }
      this.views.tabs = tabs;
    }
    return this.views.tabs;
  }

  /** How a space is shown ("Main" for Main until it's renamed). */
  labelOf(id: string): string {
    return this.spaceTabs.find((s) => s.id === id)?.label ?? spaceLabel(MAIN, this.spaces.get(MAIN)?.name ?? '');
  }

  /**
   * What a device shows for its choice (see Prefs): All, or a space. A space that's gone (deleted,
   * here or elsewhere) or never arrived shows Main.
   */
  shown(choice: string): string {
    return choice === ALL ? ALL : this.spaceOf({ space: choice });
  }

  /** The space a task or sheet shows in: its own while that's a live space, else Main (see effectiveSpace). */
  spaceOf(item: { space: string }): string {
    const s = item.space;
    if (!s || s === MAIN) return MAIN;
    if (!this.views.live) this.views.live = new Set(this.spaceList.map((x) => x.id));
    return this.views.live.has(s) ? s : MAIN;
  }

  /** Whether `item` shows in `show` (All, or a space as `shown` gives it). */
  shows(item: { space: string }, show: string): boolean {
    return show === ALL || this.spaceOf(item) === show;
  }

  /** Open tasks as `show` shows them: the one order, filtered. */
  openIn(show: string): Task[] {
    return show === ALL ? this.open : this.open.filter((t) => this.spaceOf(t) === show);
  }

  doneIn(show: string): Task[] {
    return show === ALL ? this.done : this.done.filter((t) => this.spaceOf(t) === show);
  }

  sheetsIn(show: string): Sheet[] {
    return show === ALL ? this.sheetList : this.sheetList.filter((s) => this.spaceOf(s) === show);
  }

  /** Open tasks (the one in focus included) per space, and in all. */
  openCounts(): Map<string, number> {
    const counts = new Map<string, number>();
    const now = this.now;
    let all = 0;
    for (const t of now ? [now, ...this.open] : this.open) {
      const s = this.spaceOf(t);
      counts.set(s, (counts.get(s) ?? 0) + 1);
      all++;
    }
    counts.set(ALL, all);
    return counts;
  }

  /** Open and done-today counts for `show` (everything, unless a space is given). */
  summary(show: string = ALL): Summary {
    const today = this.clock();
    const now = this.now;
    const open = this.openIn(show).length + (now && this.shows(now, show) ? 1 : 0);
    let doneToday = 0;
    for (const t of this.doneIn(show)) {
      const d = parseDate(t.completed);
      if (d && sameLocalDay(d, today)) doneToday++;
    }
    const total = open + doneToday;
    return { open, doneToday, progress: total === 0 ? 0 : doneToday / total };
  }

  // ---------------------------------------------------------------- tasks

  /**
   * Adds a task at the top of the list (or straight into focus). In `space`, if that's a live
   * space other than Main (stamped with the task's own clock); anywhere else, in no space (Main).
   */
  addTask(rawText: string, options: { asNow?: boolean; space?: string } = {}): Task | null {
    const text = cleanTaskText(rawText);
    // Longer than any device reads from a sync file: it would stay on this one alone.
    if (!text || text.length > MAX_TASK_TEXT) return null;
    return this.batch(() => {
      const top = this.open[0]?.order;
      const space = this.placeIn(options.space);
      const c = this.hlc.now();
      const task: Task = {
        id: newId(),
        text,
        created: isoLocal(this.clock()),
        completed: null,
        order: top ? keyBefore(top) : 'V',
        deleted: false,
        space,
        clk: { text: c, completed: c, order: c, deleted: c, space: space ? c : ZERO },
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

  /** Adds several tasks, keeping their order, at the top of the list (in `space`, as addTask puts them). */
  addTasks(texts: string[], space?: string): { added: Task[]; undo: Undo } {
    const added: Task[] = [];
    this.batch(() => {
      for (let i = texts.length - 1; i >= 0; i--) {
        const t = this.addTask(texts[i], { space });
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
    if (!t || !text || text.length > MAX_TASK_TEXT || t.text === text) return;
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

  /**
   * Moves an open task to `index` among the open tasks `show` shows (as they'd be without it),
   * within the one order all spaces share: it lands just past the last task it passes in this
   * view (below it going down, above it going up), so tasks in other spaces keep their places.
   *
   * Tasks in other spaces are never re-keyed. Every place between the task's new neighbours in
   * this view looks the same here, so where keys tie (two devices adding at the top at once) it
   * takes the nearest of those places whose neighbours' keys differ, and gets a key of its own
   * there. If they all tie, only this view's tasks make room.
   */
  moveTaskIn(show: string, id: string, index: number): void {
    if (show === ALL) return this.moveTask(id, index);
    const view = this.openIn(show);
    const from = view.findIndex((t) => t.id === id);
    if (from < 0) return;
    const others = view.filter((t) => t.id !== id);
    const to = Math.max(0, Math.min(index, others.length));
    if (to === from) return;
    const all = this.open.filter((t) => t.id !== id);
    const lo = to > 0 ? all.indexOf(others[to - 1]!) + 1 : 0;
    const hi = to < others.length ? all.indexOf(others[to]!) : all.length;
    const free = (g: number) => g === 0 || g === all.length || all[g - 1]!.order < all[g]!.order;
    for (let k = 0; k <= hi - lo; k++) {
      const g = to > from ? lo + k : hi - k;
      if (free(g)) return this.moveTask(id, g);
    }
    const task = this.tasks.get(id)!;
    this.batch(() => this.setOrderAt(task, others, to));
  }

  /** Deletes the finished tasks `show` shows (all of them, unless a space is given). */
  clearDone(show: string = ALL): Undo {
    const ids = this.doneIn(show).map((t) => t.id);
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

  /** Adds a sheet after the last one, in `space` as addTask puts tasks there. */
  addSheet(text = '', space?: string): Sheet {
    return this.batch(() => {
      const last = this.sheetList.at(-1)?.order;
      const where = this.placeIn(space);
      const c = this.hlc.now();
      const sheet: Sheet = {
        id: newId(),
        text: noteText(text),
        updated: isoLocal(this.clock()),
        order: last ? keyAfter(last) : 'V',
        deleted: false,
        space: where,
        clk: { text: c, order: c, deleted: c, space: where ? c : ZERO },
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

  // ---------------------------------------------------------------- spaces (nothing shows them yet)

  /** Adds a space after the last one. */
  addSpace(rawName: string): Space {
    return this.batch(() => {
      const last = this.spaceList.at(-1)?.order;
      const c = this.hlc.now();
      const space: Space = {
        id: newId(),
        name: cleanSpaceName(rawName),
        order: last ? keyAfter(last) : 'V',
        deleted: false,
        clk: { name: c, order: c, deleted: c },
      };
      this.putSpace(space);
      return space;
    });
  }

  /**
   * Renames a space. Main gets a record of its own the first time it's renamed: the name alone,
   * with no place (its order never stamped), so Main stays where it was, here and on every
   * device the name reaches.
   */
  renameSpace(id: string, rawName: string): void {
    const name = cleanSpaceName(rawName);
    const s = this.spaces.get(id);
    if (!s && id === MAIN) {
      this.batch(() => this.putSpace({ id, name, order: 'V', deleted: false, clk: { name: this.hlc.now(), order: ZERO, deleted: ZERO } }));
      return;
    }
    if (!s || s.deleted || s.name === name) return;
    this.batch(() => this.setSpaceField(s, 'name', name));
  }

  /**
   * Deletes a space. Its tasks and sheets keep their `space` and show in Main meanwhile (see
   * effectiveSpace), so the returned undo puts everything back as it was. Main can't be deleted.
   */
  deleteSpace(id: string): Undo {
    const s = this.spaces.get(id);
    if (id === MAIN || !s || s.deleted) return () => {};
    this.batch(() => this.setSpaceField(s, 'deleted', true));
    return () =>
      this.batch(() => {
        const space = this.spaces.get(id);
        if (space) this.setSpaceField(space, 'deleted', false);
      });
  }

  /**
   * Moves a space to `index` in the list as spaceTabs shows it (as it would be without the space):
   * a new order key between its new neighbours, its order stamped (neighbours tied with the one
   * above it get new keys too, as tasks do, so it really lands there). Main has no key until it
   * has a record: moving Main makes one, nameless (it still shows as "Main") with only its order
   * stamped; a space going above a Main with no place first gives Main one the same way, at the top.
   * A Main that was renamed but never moved has a record without a place: placing it stamps its
   * order alone, keeping its name.
   */
  moveSpace(id: string, index: number): void {
    const tabs = this.spaceTabs;
    const from = tabs.findIndex((s) => s.id === id);
    if (from < 0) return;
    const to = Math.max(0, Math.min(index, tabs.length - 1));
    if (to === from) return;
    const placed = this.mainHasPlace();
    // Keyed records in their order, without the one moving (nor a Main with no place).
    const records = this.spaceList.filter((s) => s.id !== id && (placed || s.id !== MAIN));
    // Main, with no place of its own, sits above every key (unless it's the one moving).
    const pinned = !placed && id !== MAIN;
    if (pinned && to === 0 && this.spaces.get(MAIN)?.deleted) return; // a deleted Main record (only a damaged file has one)
    this.batch(() => {
      let at = pinned ? to - 1 : to;
      if (pinned && to === 0) {
        // Going above Main: Main takes the top key first.
        const main = this.placeMain(keyBetween(null, records[0]?.order ?? null));
        records.unshift(main);
        at = 0;
      }
      const keys = keysAt(records.map((s) => s.order), at);
      if (id === MAIN) this.placeMain(keys[0]!);
      else this.setSpaceField(this.spaces.get(id)!, 'order', keys[0]!);
      for (let i = 1; i < keys.length; i++) this.setSpaceField(records[at + i - 1]!, 'order', keys[i]!);
    });
  }

  /** Puts a task in a space ('main' for Main). Its place in the list and the focus stay as they are. */
  moveTaskToSpace(taskId: string, spaceId: string): void {
    const t = this.tasks.get(taskId);
    if (!t || !isSpaceId(spaceId) || t.space === spaceId) return;
    this.batch(() => this.setTaskField(t, 'space', spaceId));
  }

  /** Puts a sheet in a space ('main' for Main), leaving its place and text as they are. */
  moveSheetToSpace(sheetId: string, spaceId: string): void {
    const s = this.sheets.get(sheetId);
    if (!s || !isSpaceId(spaceId) || s.space === spaceId) return;
    this.batch(() => {
      const next = cloneSheet(s);
      next.space = spaceId;
      next.clk.space = this.hlc.now();
      this.putSheet(next);
    });
  }

  // ---------------------------------------------------------------- loading and merging

  /**
   * Merges records from elsewhere (this device's storage, another tab, a file or another
   * device) into the workspace. Newer edits win field by field; nothing newer is lost.
   */
  merge(data: Partial<WorkspaceData>, origin: Origin, base?: Stamp | ConcurrentText): void {
    this.batch(() => {
      if (this.depth === 1) this.current().origin = origin;
      // Records from a version without spaces have neither field: they're in no space ('', ZERO).
      for (const raw of data.tasks ?? []) {
        const remote = normalTask(raw);
        this.hlc.observe(maxClock(remote.clk));
        const { value, changed } = mergeTask(this.tasks.get(remote.id), remote);
        if (changed) this.putTask(value, false);
      }
      for (const raw of data.sheets ?? []) {
        const remote = normalSheet(raw);
        this.hlc.observe(maxClock(remote.clk));
        const { value, changed, conflict } = mergeSheet(this.sheets.get(remote.id), remote, base);
        if (changed) this.putSheet(value, false);
        if (conflict && !this.sheets.has(conflict.id)) this.putSheet(conflict, false);
      }
      for (const remote of data.spaces ?? []) {
        this.hlc.observe(maxClock(remote.clk));
        const { value, changed } = mergeSpace(this.spaces.get(remote.id), remote);
        if (changed) this.putSpace(value, false);
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
    // A snapshot from before spaces has neither the fields nor any spaces: everything was in Main.
    const tasks = new Map(data.tasks.map((t) => [t.id, normalTask(t)]));
    const sheets = new Map(data.sheets.map((s) => [s.id, normalSheet(s)]));
    // Names as they're kept now (a snapshot may hold one from before the limit).
    const spaces = new Map((data.spaces ?? []).map((s) => [s.id, { ...s, name: cleanSpaceName(s.name) }]));
    // Never put anywhere ('') and moved back ('main') are both Main; going back there is a move to 'main'.
    const where = (space: string) => space || MAIN;
    this.batch(() => {
      for (const id of new Set([...this.tasks.keys(), ...tasks.keys()])) {
        const target = tasks.get(id);
        const current = this.tasks.get(id);
        if (!current) {
          if (target && !target.deleted) {
            const c = this.hlc.now();
            this.putTask({ ...cloneTask(target), clk: { text: c, completed: c, order: c, deleted: c, space: target.space ? c : ZERO } });
          }
          continue;
        }
        const want = target ?? { ...current, deleted: true };
        if (current.text !== want.text && want.text) this.setTaskField(current, 'text', want.text);
        if (current.completed !== want.completed) this.setTaskField(current, 'completed', want.completed);
        if (current.order !== want.order) this.setTaskField(current, 'order', want.order);
        if (current.deleted !== want.deleted) this.setTaskField(current, 'deleted', want.deleted);
        if (where(current.space) !== where(want.space)) this.setTaskField(current, 'space', where(want.space));
      }
      for (const id of new Set([...this.sheets.keys(), ...sheets.keys()])) {
        const target = sheets.get(id);
        const current = this.sheets.get(id);
        if (!current) {
          if (target && !target.deleted) {
            const c = this.hlc.now();
            this.putSheet({ ...cloneSheet(target), clk: { text: c, order: c, deleted: c, space: target.space ? c : ZERO } });
          }
          continue;
        }
        const want = target ?? { ...current, deleted: true };
        if (current.deleted && !want.deleted) this.setSheetDeleted(current, false);
        if (current.text !== want.text && !want.deleted) this.editSheet(id, want.text);
        if (!current.deleted && want.deleted) this.setSheetDeleted(current, true);
        if (where(current.space) !== where(want.space)) this.moveSheetToSpace(id, where(want.space));
      }
      for (const id of new Set([...this.spaces.keys(), ...spaces.keys()])) {
        const target = spaces.get(id);
        const current = this.spaces.get(id);
        if (!current) {
          if (target && !target.deleted) {
            const c = this.hlc.now();
            this.putSpace({ ...cloneSpace(target), clk: { name: c, order: c, deleted: c } });
          }
          continue;
        }
        // Main is never deleted: before it was renamed, it had no name of its own.
        const want = target ?? (id === MAIN ? { ...current, name: '' } : { ...current, deleted: true });
        if (current.name !== want.name && !want.deleted) this.setSpaceField(current, 'name', want.name);
        if (current.order !== want.order && !want.deleted) this.setSpaceField(current, 'order', want.order);
        if (current.deleted !== want.deleted && id !== MAIN) this.setSpaceField(current, 'deleted', want.deleted);
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
      spaces: [...this.spaces.values()].map(cloneSpace),
    };
  }

  /** Removes tombstones older than `maxAgeMs` for good. Returns the ids removed. */
  purgeTombstones(maxAgeMs: number, stampTime: (s: Stamp) => number): { tasks: string[]; sheets: string[]; spaces: string[] } {
    const cutoff = this.clock().getTime() - maxAgeMs;
    const tasks: string[] = [];
    const sheets: string[] = [];
    const spaces: string[] = [];
    for (const t of this.tasks.values()) {
      if (t.deleted && stampTime(t.clk.deleted) < cutoff) tasks.push(t.id);
    }
    for (const s of this.sheets.values()) {
      if (s.deleted && stampTime(s.clk.deleted) < cutoff) sheets.push(s.id);
    }
    for (const s of this.spaces.values()) {
      if (s.deleted && stampTime(s.clk.deleted) < cutoff) spaces.push(s.id);
    }
    tasks.forEach((id) => this.tasks.delete(id));
    sheets.forEach((id) => this.sheets.delete(id));
    spaces.forEach((id) => this.spaces.delete(id));
    if (tasks.length || sheets.length || spaces.length) this.views = {};
    return { tasks, sheets, spaces };
  }

  /**
   * Deletes records on sync's behalf (a new device's untouched welcome, when it joins), as fresh
   * deletions so they win over the copies they replace.
   */
  discard(ids: { tasks: string[]; sheets: string[] }, origin: Origin = 'remote'): void {
    this.batch(() => {
      if (this.depth === 1) this.current().origin = origin;
      for (const id of ids.tasks) this.removeTask(id);
      for (const id of ids.sheets) {
        const s = this.sheets.get(id);
        if (s && !s.deleted) this.setSheetDeleted(s, true);
      }
    });
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
        if (change.tasks.size || change.sheets.size || change.spaces.size || change.focus) {
          for (const l of this.listeners) l(change);
        }
      }
    }
  }

  private current(): Change {
    if (!this.pending) this.pending = { tasks: new Set(), sheets: new Set(), spaces: new Set(), focus: false, origin: 'local' };
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

  private putSpace(space: Space, observe = true): void {
    if (observe) this.hlc.observe(maxClock(space.clk));
    this.spaces.set(space.id, space);
    this.current().spaces.add(space.id);
    this.views.spaces = this.views.tabs = this.views.live = undefined;
  }

  /** A new task's or sheet's space: `space` if it's a live space other than Main, else none (''). */
  private placeIn(space: string | undefined): string {
    return space && space !== MAIN && this.spaceOf({ space }) === space ? space : '';
  }

  /**
   * Gives Main a place: its record's order, stamped (a name it was given stays). With no record
   * yet, one is made: no name (it shows as "Main"), only the order stamped.
   */
  private placeMain(order: string): Space {
    const existing = this.spaces.get(MAIN);
    if (existing) {
      this.setSpaceField(existing, 'order', order);
      return this.spaces.get(MAIN)!;
    }
    const main: Space = { id: MAIN, name: '', order, deleted: false, clk: { name: ZERO, order: this.hlc.now(), deleted: ZERO } };
    this.putSpace(main);
    return main;
  }

  /**
   * Whether Main has a place among the spaces' order keys: a live record whose order was stamped
   * (Main was moved, here or elsewhere). A record made by renaming alone has no place; Main then
   * sits first, as it does with no record at all.
   */
  private mainHasPlace(): boolean {
    const main = this.spaces.get(MAIN);
    return !!main && !main.deleted && main.clk.order !== ZERO;
  }

  private setTaskField<K extends 'text' | 'completed' | 'order' | 'deleted' | 'space'>(task: Task, field: K, value: Task[K]): void {
    const next = cloneTask(this.tasks.get(task.id) ?? task);
    next[field] = value;
    next.clk[field] = this.hlc.now();
    this.putTask(next);
  }

  private setSpaceField<K extends 'name' | 'order' | 'deleted'>(space: Space, field: K, value: Space[K]): void {
    const next = cloneSpace(this.spaces.get(space.id) ?? space);
    next[field] = value;
    next.clk[field] = this.hlc.now();
    this.putSpace(next);
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
