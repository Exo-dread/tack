import { decode, encode, isPlausibleStamp, ZERO, type Stamp } from './hlc';
import { derivedId } from './ids';
import { isOrderKey, keyAfter, keyBefore } from './order';
import { cleanTaskText, noteText } from './text';
import { isoLocal, parseDate } from './time';
import type { Focus, Sheet, Task, WorkspaceData } from './types';
import type { Workspace } from './workspace';

/**
 * tack.json, the file format of Tack for Windows, extended with a "tackFocus" section.
 *
 * The top level (version, tasks, sheets) is exactly what the desktop app reads, so an export
 * opens there too. The extra section carries what multi-device merging needs (clocks, sort
 * keys, deletions); the desktop app ignores it. Imports accept either flavour.
 */

export const FILE_FORMAT = 1;

interface DesktopTask {
  id: string;
  text: string;
  created: string;
  completed?: string;
  now: boolean;
}

interface DesktopSheet {
  id: string;
  text: string;
  updated: string;
}

export interface TackFile {
  version: 1;
  tasks: DesktopTask[];
  sheets: DesktopSheet[];
  tackFocus: {
    format: number;
    exported: string;
    focus: Focus;
    tasks: Task[];
    sheets: Sheet[];
  };
}

export function exportFile(ws: Workspace, now: Date = new Date()): TackFile {
  const tasks: DesktopTask[] = [];
  const nowTask = ws.now;
  if (nowTask) tasks.push(desktopTask(nowTask, true));
  for (const t of ws.open) tasks.push(desktopTask(t, false));
  for (const t of ws.done) tasks.push(desktopTask(t, false));
  const data = ws.toData();
  return {
    version: 1,
    tasks,
    sheets: ws.sheetList.map((s) => ({ id: s.id, text: s.text, updated: s.updated })),
    tackFocus: {
      format: FILE_FORMAT,
      exported: isoLocal(now),
      focus: data.focus,
      // Deleted records travel so the deletion can be merged elsewhere, but without their text.
      tasks: data.tasks.map((t) => (t.deleted ? { ...t, text: '' } : t)),
      sheets: data.sheets.map((s) => (s.deleted ? { ...s, text: '' } : s)),
    },
  };
}

function desktopTask(t: Task, now: boolean): DesktopTask {
  const out: DesktopTask = { id: t.id, text: t.text, created: t.created, now };
  if (t.completed) out.completed = t.completed;
  return out;
}

export class FileError extends Error {}

/**
 * Reads a tack.json (from Tack for Windows or an export from here) into records ready to merge.
 * Desktop records have no clocks; they get clocks from their own dates, so re-importing the
 * same file changes nothing and anything edited here since stays as it is.
 */
export interface ReadOptions {
  /** When the file was last saved (File.lastModified), to settle edits made in both places. */
  lastModified?: number;
}

/**
 * Imports a file into the workspace. Anything new is added; for anything edited in both places,
 * the newer edit wins; and a note whose text differs is kept twice rather than overwritten.
 */
export function importFile(ws: Workspace, text: string, options: ReadOptions = {}): void {
  const { data, withClocks } = parseFile(text, ws, options);
  // Files with clocks (exports from Tack Focus) merge field by field; for notes, a differing
  // text is kept as a copy beside the newer one (base ZERO: treat every difference as concurrent).
  ws.merge(data, 'local', withClocks ? ZERO : undefined);
}

export function readFile(text: string, ws: Workspace, options: ReadOptions = {}): WorkspaceData {
  return parseFile(text, ws, options).data;
}

function parseFile(text: string, ws: Workspace, options: ReadOptions): { data: WorkspaceData; withClocks: boolean } {
  let doc: unknown;
  try {
    doc = JSON.parse(stripJsonComments(text.replace(/^﻿/, '')));
  } catch {
    throw new FileError("This file isn't valid JSON.");
  }
  if (!isObject(doc)) throw new FileError("This doesn't look like a Tack file.");

  const extra = doc.tackFocus;
  if (isObject(extra) && Array.isArray(extra.tasks) && Array.isArray(extra.sheets)) {
    const tasks = extra.tasks.map(readTaskRecord).filter((t): t is Task => t !== null);
    const sheets = extra.sheets.map(readSheetRecord).filter((s): s is Sheet => s !== null);
    const focus = readFocus(extra.focus);
    return { data: { tasks, sheets, focus }, withClocks: true };
  }

  if (!Array.isArray(doc.tasks) && !Array.isArray(doc.sheets)) {
    throw new FileError("This doesn't look like a Tack file.");
  }
  return { data: readDesktop(doc, ws, options), withClocks: false };
}

/** The device a clock came from; records read from desktop files use the empty device "". */
const nodeOf = (stamp: Stamp): string | undefined => decode(stamp)?.node;

function readDesktop(doc: Record<string, unknown>, ws: Workspace, options: ReadOptions): WorkspaceData {
  const rawTasks = Array.isArray(doc.tasks) ? doc.tasks.filter(isObject) : [];
  const rawSheets = Array.isArray(doc.sheets) ? doc.sheets.filter(isObject) : [];

  // New open tasks go above the current list, in the file's order.
  const openNew = rawTasks.filter((r) => !r.completed && !ws.tasks.has(String(r.id ?? '')));
  let key = ws.open[0]?.order ?? null;
  const keys = new Map<unknown, string>();
  for (let i = openNew.length - 1; i >= 0; i--) {
    key = key ? keyBefore(key) : 'V';
    keys.set(openNew[i], key);
  }

  // When the file was saved: as given, or at least as late as the latest date inside it.
  let latest = 0;
  for (const r of [...rawTasks, ...rawSheets]) {
    for (const v of [r.created, r.completed, r.updated]) {
      latest = Math.max(latest, parseDate(typeof v === 'string' ? v : '')?.getTime() ?? 0);
    }
  }
  const savedAt = options.lastModified ?? latest;

  /**
   * The clock for a field of something this device already has. Unchanged: the same clock (a
   * no-op). Changed, and never edited here (its clock still comes from an earlier import): the
   * desktop's change is the only news, so it wins. Changed in both places: the later one wins,
   * using the file's save time for the desktop's side.
   */
  const changedClock = (same: boolean, localClock: Stamp): Stamp =>
    same ? localClock : nodeOf(localClock) === '' ? ws.hlc.now() : stampFrom(savedAt);

  let focusId: string | null = null;
  const tasks: Task[] = [];
  const usedIds = new Set<string>();
  for (const r of rawTasks) {
    const text = cleanTaskText(String(r.text ?? ''));
    if (!text) continue;
    const id = validId(r.id, usedIds, text);
    const created = parseDate(String(r.created ?? '')) ?? new Date(0);
    const completed = r.completed ? parseDate(String(r.completed)) : null;
    const base = stampFrom(created.getTime());
    const existing = ws.tasks.get(id);
    const sameDone = !!existing && !!existing.completed === !!completed;
    tasks.push({
      id,
      text,
      created: typeof r.created === 'string' && parseDate(r.created) ? r.created : isoLocal(created),
      // Both finished: keep this device's completion time rather than flipping between them.
      completed: completed ? (sameDone && existing?.completed ? existing.completed : String(r.completed)) : null,
      order: keys.get(r) ?? existing?.order ?? 'V',
      deleted: existing?.deleted ?? false,
      clk: existing
        ? {
            text: changedClock(existing.text === text, existing.clk.text),
            completed: changedClock(sameDone, existing.clk.completed),
            order: existing.clk.order,
            deleted: existing.clk.deleted,
          }
        : { text: base, completed: completed ? stampFrom(completed.getTime()) : base, order: base, deleted: base },
    });
    if (r.now === true && !completed && focusId === null) focusId = id;
  }

  let sheetKey = ws.sheetList.at(-1)?.order ?? null;
  const nextKey = () => (sheetKey = sheetKey ? keyAfter(sheetKey) : 'V');
  const sheets: Sheet[] = [];
  for (const r of rawSheets) {
    const text = noteText(String(r.text ?? ''));
    let id = validId(r.id, usedIds, text);
    const updatedAt = parseDate(String(r.updated ?? '')) ?? new Date(0);
    const updated = typeof r.updated === 'string' && parseDate(r.updated) ? r.updated : isoLocal(updatedAt);
    const existing = ws.sheets.get(id);
    if (existing && existing.text !== text && nodeOf(existing.clk.text) !== '') {
      // Written here and on the desktop: keep both, the desktop's as a copy beside this one.
      // The copy's id comes from its content, so importing the same file again adds nothing.
      id = derivedId(`${id}:${text}`);
      if (ws.sheets.has(id)) continue;
      const c = stampFrom(updatedAt.getTime());
      sheets.push({ id, text, updated, order: nextKey(), deleted: false, clk: { text: c, order: c, deleted: c } });
      continue;
    }
    if (existing) {
      // Unchanged (a no-op), or only ever changed on the desktop (its version wins).
      const clk = existing.text === text ? existing.clk.text : ws.hlc.now();
      sheets.push({ ...existing, text, updated: existing.text === text ? existing.updated : updated, clk: { ...existing.clk, text: clk } });
      continue;
    }
    const c = stampFrom(updatedAt.getTime());
    sheets.push({ id, text, updated, order: nextKey(), deleted: false, clk: { text: c, order: c, deleted: c } });
  }

  // Importing is an explicit "bring this in": the file's focus task becomes the focus here too
  // (unless it already is, so importing the same file twice still changes nothing).
  const focus: Focus =
    focusId && ws.focus.id !== focusId ? { id: focusId, clk: ws.hlc.now() } : { id: null, clk: ZERO };
  return { tasks, sheets, focus };
}

/** Clocks for desktop records: their own time, from a pseudo-device that loses ties. */
const stampFrom = (ms: number): Stamp => encode(Math.max(0, Math.floor(ms)), 0, '');

function validId(raw: unknown, used: Set<string>, seed: string): string {
  let id = typeof raw === 'string' && /^[\w-]{1,64}$/.test(raw) ? raw : '';
  if (!id || used.has(id)) {
    // Stable for the same content, so importing the same file twice doesn't duplicate it.
    let h = 0;
    const s = `${seed}:${used.size}`;
    for (let i = 0; i < s.length; i++) h = (Math.imul(h, 31) + s.charCodeAt(i)) | 0;
    id = `imp${(h >>> 0).toString(16).padStart(8, '0')}`;
  }
  used.add(id);
  return id;
}

/** Ids from files: the shape Tack itself makes (and nothing that could upset a selector or object key). */
const ID = /^[\w-]{1,64}$/;

function readTaskRecord(r: unknown): Task | null {
  if (!isObject(r) || typeof r.id !== 'string' || !ID.test(r.id) || !isObject(r.clk)) return null;
  const clk = r.clk;
  if (![clk.text, clk.completed, clk.order, clk.deleted].every((c) => isPlausibleStamp(c))) return null;
  const text = cleanTaskText(typeof r.text === 'string' ? r.text : '');
  if (!text && r.deleted !== true) return null;
  return {
    id: r.id,
    text,
    created: typeof r.created === 'string' ? r.created : isoLocal(new Date(0)),
    completed: typeof r.completed === 'string' ? r.completed : null,
    order: isOrderKey(r.order) ? r.order : 'V',
    deleted: r.deleted === true,
    clk: { text: clk.text as Stamp, completed: clk.completed as Stamp, order: clk.order as Stamp, deleted: clk.deleted as Stamp },
  };
}

function readSheetRecord(r: unknown): Sheet | null {
  if (!isObject(r) || typeof r.id !== 'string' || !ID.test(r.id) || !isObject(r.clk)) return null;
  const clk = r.clk;
  if (![clk.text, clk.order, clk.deleted].every((c) => isPlausibleStamp(c))) return null;
  return {
    id: r.id,
    text: noteText(typeof r.text === 'string' ? r.text : ''),
    updated: typeof r.updated === 'string' ? r.updated : isoLocal(new Date(0)),
    order: isOrderKey(r.order) ? r.order : 'V',
    deleted: r.deleted === true,
    clk: { text: clk.text as Stamp, order: clk.order as Stamp, deleted: clk.deleted as Stamp },
  };
}

function readFocus(r: unknown): Focus {
  if (isObject(r) && isPlausibleStamp(r.clk) && (r.id === null || (typeof r.id === 'string' && ID.test(r.id)))) {
    return { id: r.id as string | null, clk: r.clk };
  }
  return { id: null, clk: ZERO };
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

/**
 * Tack for Windows tolerates comments and trailing commas (people edit tack.json by hand),
 * so the importer does too.
 */
export function stripJsonComments(src: string): string {
  let out = '';
  let i = 0;
  const n = src.length;
  while (i < n) {
    const c = src[i];
    if (c === '"') {
      let j = i + 1;
      while (j < n && src[j] !== '"') j += src[j] === '\\' ? 2 : 1;
      out += src.slice(i, j + 1);
      i = j + 1;
    } else if (c === '/' && src[i + 1] === '/') {
      while (i < n && src[i] !== '\n') i++;
    } else if (c === '/' && src[i + 1] === '*') {
      const end = src.indexOf('*/', i + 2);
      i = end < 0 ? n : end + 2;
    } else if (c === ',') {
      // Drop the comma if only whitespace or comments stand before a closing bracket.
      let j = i + 1;
      for (;;) {
        while (j < n && /\s/.test(src[j])) j++;
        if (src[j] === '/' && src[j + 1] === '/') {
          while (j < n && src[j] !== '\n') j++;
        } else if (src[j] === '/' && src[j + 1] === '*') {
          const end = src.indexOf('*/', j + 2);
          j = end < 0 ? n : end + 2;
        } else break;
      }
      if (src[j] !== '}' && src[j] !== ']') out += c;
      i++;
    } else {
      out += c;
      i++;
    }
  }
  return out;
}
