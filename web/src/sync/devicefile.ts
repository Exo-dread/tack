import { focusRecord, isObject, readFocus, readSheetRecord, readSpaceRecord, readTaskRecord, sheetRecord, spaceRecord, taskRecord } from '../model/file';
import { isPlausibleStamp, ZERO, type Stamp } from '../model/hlc';
import { mergeSheet, mergeSpace, mergeTask } from '../model/merge';
import { MAX_SPACE_NAME } from '../model/spaces';
import { wellFormedValues } from '../model/text';
import type { Focus, Sheet, Space, Task, WorkspaceData } from '../model/types';

/**
 * A device file: everything one device knows, written only by that device, read by all the
 * others. The sync folder holds one per device (`<device id>.json`), so devices never write the
 * same file and a sync client can't make "conflicted copies" of anything that matters.
 *
 *   format, version   what this is (a newer, incompatible Tack would bump `version`)
 *   device            the writer's id: the same id its clock puts in every stamp
 *   name, app         for people: "Tack Focus on Android", "Tack Focus 1.1.0"
 *   written           the writer's clock when it wrote the file (later than every stamp inside)
 *   seen              for each other device, the `written` of the latest version of its file
 *                     merged into this one (directly, or through a third device's file)
 *   focus, tasks, sheets   live records, exactly as in tack.json's tackFocus section (a record's
 *                     `space` and its clock only once it has been put in one)
 *   spaces            live spaces, only once there are any
 *   deleted           deleted records, as id → the clock of the deletion. Deletions are kept
 *                     for good while sync is on (about 45 bytes each), so a copy that's out of
 *                     date (a device back after months, a file uploaded late) can never bring
 *                     back something that was deleted.
 */

export const DEVICE_FORMAT = 'tack-sync-device';
export const DEVICE_VERSION = 1;
/** Device files bigger than this can't be Tack's (thousands of tasks are well under 2 MB). */
export const MAX_FILE_BYTES = 20 * 1024 * 1024;
/**
 * Limits inside a device file, far beyond any real use. A file over them is refused whole (and
 * set aside), and an over-long record is skipped, so a damaged or crafted file can't swell every
 * device's own file for good. Tack for Windows uses the same numbers. (A space name is the one
 * thing cut rather than skipped: every device keeps names to MAX_SPACE_NAME wherever they come
 * from, so no device can hold a name its peers would refuse.)
 */
export const MAX_RECORDS = 200_000; // tasks + sheets + spaces + deletions
export const MAX_SEEN = 1_000;
export const MAX_TASK_TEXT = 100_000;
export const MAX_NOTE_TEXT = 1_000_000;
export { MAX_SPACE_NAME };

const DEVICE_ID = /^[0-9A-Za-z]{4,32}$/;
const FILE_NAME = /^([0-9A-Za-z]{4,32})\.json$/;
const RECORD_ID = /^[\w-]{1,64}$/;

export interface DeviceFile {
  format: typeof DEVICE_FORMAT;
  version: number;
  device: string;
  name: string;
  app: string;
  written: Stamp;
  seen: Record<string, Stamp>;
  focus: Focus;
  /** Live and deleted records (deleted ones carry only their id and deletion clock). */
  tasks: Task[];
  sheets: Sheet[];
  spaces: Space[];
}

export const isDeviceId = (id: unknown): id is string => typeof id === 'string' && DEVICE_ID.test(id);

export const fileName = (device: string): string => `${device}.json`;

/** The device a file in the folder belongs to, or null for anything else (sync clients' copies, temp files). */
export function deviceOfFile(name: string): string | null {
  return FILE_NAME.exec(name)?.[1] ?? null;
}

/** A deleted task as a device file carries it: just enough to win against any older copy. */
export function deadTask(id: string, deleted: Stamp): Task {
  return { id, text: '', created: '', completed: null, order: 'V', deleted: true, space: '', clk: { text: ZERO, completed: ZERO, order: ZERO, deleted, space: ZERO } };
}

export function deadSheet(id: string, deleted: Stamp): Sheet {
  return { id, text: '', updated: '', order: 'V', deleted: true, space: '', clk: { text: ZERO, order: ZERO, deleted, space: ZERO } };
}

export function deadSpace(id: string, deleted: Stamp): Space {
  return { id, name: '', order: 'V', deleted: true, clk: { name: ZERO, order: ZERO, deleted } };
}

/**
 * The file's content, key by key (never memory's objects as they are): with no spaces and nothing
 * put in one, it's byte for byte what versions before spaces wrote. Tack for Windows writes the same.
 */
export function makeDeviceFile(
  data: WorkspaceData,
  meta: { device: string; name: string; app: string; written: Stamp; seen: Record<string, Stamp> },
): Record<string, unknown> {
  const byId = (a: { id: string }, b: { id: string }) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
  const gone = (records: { id: string; deleted: boolean; clk: { deleted: Stamp } }[]) => {
    // No prototype: an id such as "__proto__" stays a plain key.
    const out: Record<string, Stamp> = Object.create(null);
    for (const r of records.filter((x) => x.deleted).sort(byId)) out[r.id] = r.clk.deleted;
    return out;
  };
  const spaces = data.spaces ?? [];
  const live = spaces.filter((s) => !s.deleted).sort(byId);
  const deadSpaces = gone(spaces);
  return {
    format: DEVICE_FORMAT,
    version: DEVICE_VERSION,
    device: meta.device,
    name: meta.name,
    app: meta.app,
    written: meta.written,
    seen: sortedMap(meta.seen, meta.device),
    focus: focusRecord(data.focus),
    tasks: data.tasks.filter((t) => !t.deleted).sort(byId).map((t) => taskRecord(t)),
    sheets: data.sheets.filter((s) => !s.deleted).sort(byId).map((s) => sheetRecord(s)),
    ...(live.length ? { spaces: live.map((s) => spaceRecord(s)) } : {}),
    deleted: {
      tasks: gone(data.tasks),
      sheets: gone(data.sheets),
      ...(Object.keys(deadSpaces).length ? { spaces: deadSpaces } : {}),
    },
  };
}

export function serializeDeviceFile(file: Record<string, unknown>): string {
  return JSON.stringify(file, wellFormedValues);
}

export class DeviceFileError extends Error {
  constructor(readonly reason: 'unreadable' | 'newer' | 'mismatch') {
    super(reason === 'newer' ? 'Written by a newer version of Tack' : reason === 'mismatch' ? 'Device id does not match the file name' : 'Not a readable Tack device file');
  }
}

/**
 * Reads a device file, checking everything: a damaged or hand-edited file costs that file's
 * records at worst, never the rest. `device` is the id its name says it belongs to.
 */
export function parseDeviceFile(text: string, device: string, now = Date.now()): DeviceFile {
  if (text.length > MAX_FILE_BYTES) throw new DeviceFileError('unreadable');
  let doc: unknown;
  try {
    // Lone surrogates ("\ud800") are valid JSON but not text: they become U+FFFD here, so they
    // can never travel on to Tack for Windows, which can't read them.
    doc = JSON.parse(text.replace(/^﻿/, ''), wellFormedValues);
  } catch {
    throw new DeviceFileError('unreadable');
  }
  if (!isObject(doc) || doc.format !== DEVICE_FORMAT || typeof doc.version !== 'number' || !Number.isInteger(doc.version) || doc.version < 1) {
    throw new DeviceFileError('unreadable');
  }
  if (doc.version > DEVICE_VERSION) throw new DeviceFileError('newer');
  if (doc.device !== device || !isDeviceId(doc.device)) throw new DeviceFileError('mismatch');
  if (!isPlausibleStamp(doc.written, now) || !Array.isArray(doc.tasks) || !Array.isArray(doc.sheets)) {
    throw new DeviceFileError('unreadable');
  }
  const deleted = isObject(doc.deleted) ? doc.deleted : {};
  // Files from before spaces have none (and anything but a list is none).
  const rawSpaces = Array.isArray(doc.spaces) ? doc.spaces : [];
  const count = (map: unknown) => (isObject(map) ? Object.keys(map).length : 0);
  const records = doc.tasks.length + doc.sheets.length + rawSpaces.length + count(deleted.tasks) + count(deleted.sheets) + count(deleted.spaces);
  if (records > MAX_RECORDS || count(doc.seen) > MAX_SEEN) {
    throw new DeviceFileError('unreadable');
  }

  // Duplicate ids (only a hand edit makes them) merge rather than one silently replacing the other.
  const tasks = new Map<string, Task>();
  const addTask = (t: Task | null) => {
    if (t && t.text.length <= MAX_TASK_TEXT) tasks.set(t.id, mergeTask(tasks.get(t.id), t).value);
  };
  const sheets = new Map<string, Sheet>();
  const addSheet = (s: Sheet | null) => {
    if (s && s.text.length <= MAX_NOTE_TEXT) sheets.set(s.id, mergeSheet(sheets.get(s.id), s).value);
  };
  const spaces = new Map<string, Space>();
  // Names arrive cut to MAX_SPACE_NAME (readSpaceRecord), as every device keeps them.
  const addSpace = (s: Space | null) => {
    if (s) spaces.set(s.id, mergeSpace(spaces.get(s.id), s).value);
  };
  doc.tasks.forEach((raw) => addTask(readTaskRecord(raw, now)));
  doc.sheets.forEach((raw) => addSheet(readSheetRecord(raw, now)));
  rawSpaces.forEach((raw) => addSpace(readSpaceRecord(raw, now)));
  for (const [id, stamp] of entries(deleted.tasks, now)) addTask(deadTask(id, stamp));
  for (const [id, stamp] of entries(deleted.sheets, now)) addSheet(deadSheet(id, stamp));
  for (const [id, stamp] of entries(deleted.spaces, now)) addSpace(deadSpace(id, stamp));

  return {
    format: DEVICE_FORMAT,
    version: doc.version,
    device,
    name: typeof doc.name === 'string' ? doc.name.slice(0, 100) : '',
    app: typeof doc.app === 'string' ? doc.app.slice(0, 100) : '',
    written: doc.written,
    seen: Object.fromEntries(entries(doc.seen, now).filter(([k]) => isDeviceId(k))),
    focus: readFocus(doc.focus, now),
    tasks: [...tasks.values()],
    sheets: [...sheets.values()],
    spaces: [...spaces.values()],
  };
}

/** Well-formed `id: clock` pairs of a JSON object (anything else is skipped). */
function entries(raw: unknown, now: number): [string, Stamp][] {
  if (!isObject(raw)) return [];
  return Object.entries(raw).filter((e): e is [string, Stamp] => RECORD_ID.test(e[0]) && isPlausibleStamp(e[1], now));
}

function sortedMap(map: Record<string, Stamp>, without: string): Record<string, Stamp> {
  const out: Record<string, Stamp> = Object.create(null);
  for (const k of Object.keys(map).sort()) if (k !== without && isDeviceId(k)) out[k] = map[k]!;
  return out;
}

/**
 * A device's entry in a map keyed by device id, ignoring what every object inherits: an id such
 * as "constructor" is valid, and must read as absent until that device is actually seen.
 */
export function stampOf(map: Record<string, Stamp>, device: string): Stamp | undefined {
  return Object.hasOwn(map, device) ? map[device] : undefined;
}
