import 'fake-indexeddb/auto';
import { describe, expect, it } from 'vitest';
import { exportFile, importFile, readSheetRecord, readSpaceRecord, readTaskRecord, type TackFile } from '../../src/model/file';
import { encode, Hlc, stampTime, ZERO, type Stamp } from '../../src/model/hlc';
import { mergeSheet, mergeSpace, mergeTask } from '../../src/model/merge';
import { effectiveSpace, MAIN } from '../../src/model/spaces';
import { wellFormedValues } from '../../src/model/text';
import type { Sheet, Space, Task, WorkspaceData } from '../../src/model/types';
import { untouchedWelcome } from '../../src/model/welcome';
import { Workspace, type Change } from '../../src/model/workspace';
import { IdbBackend, MemoryBackend } from '../../src/store/db';
import { DeviceFileError, makeDeviceFile, MAX_RECORDS, parseDeviceFile, serializeDeviceFile } from '../../src/sync/devicefile';
import { READER, WRITER } from '../../src/sync/engine';
import { Cloud, Device, records, settle, shownIn, visible, withoutReader, type Clock } from './synchelpers';

/**
 * Spaces groundwork: the data, merging, files, storage and sync behind spaces (Work, Dev,
 * Personal...), with nothing in the UI yet. The same cases run against Tack for Windows, and the
 * shared vectors (vectors.test.ts, syncvectors.test.ts) hold the two apps to the same results.
 */

const NOW = Date.UTC(2026, 9, 21, 9, 0, 0);
const at = (ms: number, node = 'abcd1234') => encode(ms, 0, node);
const W = at(NOW - 60_000);
const LATER = at(NOW - 30_000);

/** Characters by code, so unusual ones survive any editor. */
const U = (...codes: number[]) => String.fromCharCode(...codes);

function device(node = 'desk', start = NOW) {
  let wall = start;
  const ws = new Workspace(new Hlc(node, () => wall), () => new Date(wall));
  return { ws, tick: (ms = 1000) => (wall += ms), wall: () => wall };
}

/** A task as memory has it, in a space or not. */
function task(id: string, space = '', spaceClk: Stamp = space ? W : ZERO, text = 'x', textClk = W): Task {
  return { id, text, created: '2026-10-21T08:00:00.000Z', completed: null, order: 'V', deleted: false, space, clk: { text: textClk, completed: W, order: W, deleted: W, space: spaceClk } };
}

function sheet(id: string, space = '', spaceClk: Stamp = space ? W : ZERO, text = 'note', textClk = W): Sheet {
  return { id, text, updated: '2026-10-21T08:00:00.000Z', order: 'V', deleted: false, space, clk: { text: textClk, order: W, deleted: W, space: spaceClk } };
}

function space(id: string, name = id, deleted = false, clk: Stamp = W): Space {
  return { id, name, order: 'V', deleted, clk: { name: clk, order: clk, deleted: clk } };
}

/** The record as a version before spaces has it: no `space`, and no clock for it. */
function old<T extends Task | Sheet>(r: T): T {
  const out = { ...r, clk: { ...r.clk } } as Record<string, unknown> & { clk: Record<string, unknown> };
  delete out.space;
  delete out.clk.space;
  return out as unknown as T;
}

describe("a task's or sheet's space, merged", () => {
  it('goes to the later move, whichever side it arrives on', () => {
    const a = task('t', 'work', at(NOW - 2000));
    const b = task('t', 'dev', at(NOW - 1000));
    expect(mergeTask(a, b).value.space).toBe('dev');
    expect(mergeTask(b, a)).toMatchObject({ changed: false, value: { space: 'dev' } });
    // Back to Main is a move like any other.
    const back = task('t', MAIN, at(NOW));
    expect(mergeTask(b, back).value).toMatchObject({ space: MAIN, clk: { space: back.clk.space } });
  });

  it('is never undone by a copy from a version before spaces, which may still bring other edits', () => {
    const moved = task('t', 'work');
    const edited = old(task('t', '', ZERO, 'edited by an old version', LATER));
    const { value } = mergeTask(moved, edited);
    expect(value).toMatchObject({ text: 'edited by an old version', space: 'work', clk: { space: W, text: LATER } });
    // The other way round: the old copy is here, the move arrives.
    expect(mergeTask(edited, moved).value).toMatchObject({ text: 'edited by an old version', space: 'work', clk: { space: W } });
    // Nor by a record that was never put anywhere.
    expect(mergeTask(moved, task('t')).changed).toBe(false);
    expect(mergeSheet(sheet('s', 'work'), old(sheet('s'))).changed).toBe(false);
  });

  it('settles equal clocks with different ids (a hand edit) the same way on both sides', () => {
    const a = task('t', 'work', W);
    const b = task('t', 'dev', W);
    expect(mergeTask(a, b).value.space).toBe('work');
    expect(mergeTask(b, a).value.space).toBe('work');
    expect(mergeSheet(sheet('s', 'dev'), sheet('s', 'work')).value.space).toBe('work');
  });

  it('keeps the copy of a note written in two places at once where the losing writer had it', () => {
    const desk = sheet('s', 'work', at(NOW - 5000), 'from the desk', at(NOW - 2000));
    const phone = sheet('s', 'dev', at(NOW - 4000), 'from the phone', at(NOW - 1000));
    for (const [local, remote] of [[desk, phone], [phone, desk]] as const) {
      const { value, conflict } = mergeSheet(local, remote, ZERO);
      expect(value).toMatchObject({ text: 'from the phone', space: 'dev' });
      expect(conflict).toMatchObject({ text: 'from the desk', space: 'work', clk: { space: desk.clk.space, text: desk.clk.text } });
    }
    // A loser in no space (or from a version before spaces) makes a copy in no space.
    const { conflict } = mergeSheet(phone, old(desk), ZERO);
    expect(conflict).toMatchObject({ space: '', clk: { space: ZERO } });
  });
});

describe('space records, merged', () => {
  it('go field by field, later clock first', () => {
    const named = space('w', 'Work', false, at(NOW - 2000));
    const renamed = { ...named, name: 'Office', clk: { ...named.clk, name: at(NOW - 1000) } };
    const deleted = { ...named, deleted: true, clk: { ...named.clk, deleted: at(NOW - 500) } };
    expect(mergeSpace(renamed, deleted).value).toMatchObject({ name: 'Office', deleted: true });
    expect(mergeSpace(deleted, renamed).value).toMatchObject({ name: 'Office', deleted: true });
    expect(mergeSpace(named, named).changed).toBe(false);
    expect(mergeSpace(undefined, named)).toMatchObject({ changed: true, value: named });
    // A deletion as device files carry it keeps the name it had here.
    const dead: Space = { id: 'w', name: '', order: 'V', deleted: true, clk: { name: ZERO, order: ZERO, deleted: at(NOW) } };
    expect(mergeSpace(renamed, dead).value).toMatchObject({ name: 'Office', deleted: true });
  });
});

describe('reading a space from a file', () => {
  const raw = (space: unknown, clk: unknown) => {
    const r: Record<string, unknown> = { id: 't1', text: 'x', created: '2026-10-21T08:00:00.000Z', completed: null, order: 'V', deleted: false, clk: { text: W, completed: W, order: W, deleted: W } };
    if (space !== undefined) r.space = space;
    if (clk !== undefined) (r.clk as Record<string, unknown>).space = clk;
    return r;
  };

  it('treats anything missing, odd or implausible as "in no space", and never skips the record', () => {
    const future = at(NOW + 400 * 86_400_000, 'x');
    const cases: [unknown, unknown][] = [
      [undefined, undefined], // a version before spaces
      ['work', undefined],
      [undefined, W],
      ['work', ZERO],
      ['work', future],
      ['work', 'not a stamp'],
      ['work', 42],
      ['bad id!', W],
      ['x'.repeat(65), W],
      ['', W],
      [7, W],
      [null, W],
      [{ id: 'work' }, W],
    ];
    for (const [s, c] of cases) {
      expect(readTaskRecord(raw(s, c), NOW), JSON.stringify([s, c])).toMatchObject({ id: 't1', space: '', clk: { space: ZERO } });
      expect(readSheetRecord({ ...raw(s, c), text: 'n', updated: '2026-10-21T08:00:00.000Z' }, NOW)).toMatchObject({ id: 't1', space: '', clk: { space: ZERO } });
    }
  });

  it('keeps a sound assignment, Main and an id no space here has (yet) included', () => {
    for (const s of ['work', MAIN, 'nosuchspace', 'a_b-c']) {
      expect(readTaskRecord(raw(s, W), NOW)).toMatchObject({ space: s, clk: { space: W } });
    }
  });

  it('reads space records: unsound ones skipped, names one line, odd keys mended', () => {
    const good = space('work', 'Work');
    expect(readSpaceRecord(good, NOW)).toEqual(good);
    expect(readSpaceRecord({ ...good, name: '  Side \t project\n' }, NOW)?.name).toBe('Side project');
    expect(readSpaceRecord({ ...good, name: 42 }, NOW)?.name).toBe('');
    expect(readSpaceRecord({ ...good, name: '' }, NOW)?.name).toBe('');
    expect(readSpaceRecord({ ...good, order: 'V0', deleted: 'yes' }, NOW)).toMatchObject({ order: 'V', deleted: false });
    // A name over 100 characters is cut there, as when one is typed (an emoji never in half).
    expect(readSpaceRecord({ ...good, name: 'x'.repeat(150) }, NOW)?.name).toBe('x'.repeat(100));
    expect(readSpaceRecord({ ...good, name: `${'x'.repeat(99)}${U(0xd83d, 0xde00)}` }, NOW)?.name).toBe('x'.repeat(99));
    expect(readSpaceRecord({ ...good, name: ` ${'y'.repeat(100)} ` }, NOW)?.name).toBe('y'.repeat(100));
    for (const bad of [
      'nonsense',
      null,
      [good],
      { ...good, id: 'bad id!' },
      { ...good, id: 42 },
      { ...good, clk: undefined },
      { ...good, clk: { name: W, order: W } },
      { ...good, clk: { ...good.clk, deleted: at(NOW + 400 * 86_400_000) } },
      { ...good, clk: { ...good.clk, name: 'garbage' } },
    ]) {
      expect(readSpaceRecord(bad, NOW)).toBeNull();
    }
  });

  it('keeps records from a version before spaces in no space when they arrive any other way', () => {
    const { ws } = device();
    ws.merge({ tasks: [old(task('t'))], sheets: [old(sheet('s'))] }, 'remote');
    expect(ws.tasks.get('t')).toMatchObject({ space: '', clk: { space: ZERO } });
    expect(ws.sheets.get('s')).toMatchObject({ space: '', clk: { space: ZERO } });
    // A space without a clock (or the reverse) can't be told from nothing.
    ws.merge({ tasks: [{ ...task('u'), space: 'work' }, task('v', '', W)] }, 'remote');
    expect(ws.tasks.get('u')?.space).toBe('');
    expect(ws.tasks.get('v')?.clk.space).toBe(ZERO);
  });
});

describe('effectiveSpace', () => {
  it("is the record's space if that's a live space, else Main", () => {
    const spaces = [space('work'), space('gone', 'Gone', true), space(MAIN, 'Inbox')];
    expect(effectiveSpace('', spaces)).toBe(MAIN);
    expect(effectiveSpace(MAIN, spaces)).toBe(MAIN);
    expect(effectiveSpace(MAIN, [])).toBe(MAIN);
    expect(effectiveSpace('work', spaces)).toBe('work');
    expect(effectiveSpace('gone', spaces)).toBe(MAIN);
    expect(effectiveSpace('nosuchspace', spaces)).toBe(MAIN);
    expect(effectiveSpace('Work', spaces)).toBe(MAIN);
  });
});

describe('workspace operations', () => {
  /** Everything but the space fields, to show a move changed nothing else. */
  const rest = (ws: Workspace) => ({
    focus: ws.focus,
    tasks: [...ws.tasks.values()].map((t) => ({ ...t, space: undefined, clk: { ...t.clk, space: undefined } })),
    sheets: [...ws.sheets.values()].map((s) => ({ ...s, space: undefined, clk: { ...s.clk, space: undefined } })),
    open: ws.open.map((t) => t.id),
    now: ws.now?.id,
  });

  it('adds spaces one after another, each with one stamp for all its clocks', () => {
    const { ws, tick } = device();
    const work = ws.addSpace('  Work \t stuff ');
    expect(work).toMatchObject({ name: 'Work stuff', order: 'V', deleted: false });
    expect(work.clk.name).toBe(work.clk.order);
    expect(work.clk.name).toBe(work.clk.deleted);
    tick();
    const dev = ws.addSpace('Dev');
    expect(dev.order > work.order).toBe(true);
    ws.deleteSpace(dev.id);
    // After the last live one.
    expect(ws.addSpace('').order).toBe('W');
    expect(ws.spaceList.map((s) => s.name)).toEqual(['Work stuff', '']);
  });

  it('renames, and gives Main a record of its own the first time', () => {
    const { ws, tick } = device();
    const work = ws.addSpace('Work');
    tick();
    ws.renameSpace(work.id, ' Office ');
    expect(ws.spaces.get(work.id)).toMatchObject({ name: 'Office', clk: { order: work.clk.order } });
    expect(ws.spaces.get(work.id)!.clk.name > work.clk.name).toBe(true);
    ws.renameSpace(MAIN, 'Inbox');
    expect(ws.spaces.get(MAIN)).toMatchObject({ id: MAIN, name: 'Inbox', order: 'V', deleted: false, clk: { order: ZERO, deleted: ZERO } });
    expect(ws.spaces.get(MAIN)!.clk.name).not.toBe(ZERO);
    ws.renameSpace(MAIN, 'Today');
    expect(ws.spaces.get(MAIN)!.name).toBe('Today');
    // Nothing to rename: nothing happens.
    ws.renameSpace('nosuchspace', 'x');
    expect(ws.spaces.has('nosuchspace')).toBe(false);
  });

  it('deletes a space and undoes it; its tasks keep their space and show in Main meanwhile', () => {
    const { ws, tick } = device();
    const work = ws.addSpace('Work');
    const t = ws.addTask('in work')!;
    const s = ws.addSheet('notes');
    ws.moveTaskToSpace(t.id, work.id);
    ws.moveSheetToSpace(s.id, work.id);
    tick();
    const undo = ws.deleteSpace(work.id);
    expect(ws.spaces.get(work.id)!.deleted).toBe(true);
    expect(ws.tasks.get(t.id)!.space).toBe(work.id);
    expect(effectiveSpace(ws.tasks.get(t.id)!.space, ws.spaces.values())).toBe(MAIN);
    expect(effectiveSpace(ws.sheets.get(s.id)!.space, ws.spaces.values())).toBe(MAIN);
    tick();
    undo();
    expect(ws.spaces.get(work.id)!.deleted).toBe(false);
    expect(effectiveSpace(ws.tasks.get(t.id)!.space, ws.spaces.values())).toBe(work.id);
  });

  it("won't delete Main", () => {
    const { ws } = device();
    ws.renameSpace(MAIN, 'Inbox');
    const before = ws.toData();
    ws.deleteSpace(MAIN)();
    expect(ws.toData()).toEqual(before);
  });

  it('moves tasks and sheets, the one in focus included, without touching anything else', () => {
    const { ws, tick } = device();
    const work = ws.addSpace('Work');
    const [c, b, a] = ['c', 'b', 'a'].map((x) => ws.addTask(x)!);
    ws.setNow(b!.id);
    const s = ws.addSheet('notes');
    const before = rest(ws);
    tick();
    ws.moveTaskToSpace(b!.id, work.id);
    ws.moveTaskToSpace(a!.id, work.id);
    ws.moveSheetToSpace(s.id, work.id);
    ws.moveTaskToSpace(c!.id, MAIN);
    expect(rest(ws)).toEqual(before);
    expect(ws.tasks.get(b!.id)).toMatchObject({ space: work.id });
    expect(ws.tasks.get(b!.id)!.clk.space).not.toBe(ZERO);
    expect(ws.tasks.get(c!.id)!.space).toBe(MAIN);
    expect(ws.sheets.get(s.id)!.space).toBe(work.id);
    // Not an id, or already there: nothing happens.
    const clk = ws.tasks.get(a!.id)!.clk.space;
    ws.moveTaskToSpace(a!.id, work.id);
    ws.moveTaskToSpace(a!.id, '');
    ws.moveTaskToSpace(a!.id, 'bad id!');
    ws.moveSheetToSpace(s.id, '');
    expect(ws.tasks.get(a!.id)!.clk.space).toBe(clk);
    expect(ws.sheets.get(s.id)!.space).toBe(work.id);
  });

  it('keeps a name to 100 characters, never cutting a character in half', () => {
    const { ws, tick } = device();
    const long = ws.addSpace(`  ${'x'.repeat(150)}`);
    expect(long.name).toBe('x'.repeat(100));
    tick();
    // An emoji (two UTF-16 units) across the limit goes whole; a space left at the end goes too.
    ws.renameSpace(long.id, `${'y'.repeat(99)}${U(0xd83d, 0xde00)} and more`);
    expect(ws.spaces.get(long.id)!.name).toBe('y'.repeat(99));
    ws.renameSpace(MAIN, `${'z'.repeat(99)}   tail`);
    expect(ws.spaces.get(MAIN)!.name).toBe('z'.repeat(99));
    // What it comes to is the name it has: nothing changes.
    const clk = ws.spaces.get(long.id)!.clk.name;
    tick();
    ws.renameSpace(long.id, `${'y'.repeat(99)}${U(0xd83d, 0xde00)} other`);
    expect(ws.spaces.get(long.id)!.clk.name).toBe(clk);
    // A snapshot from before the limit is kept to it too.
    const snap = ws.toData();
    snap.spaces = snap.spaces.map((s) => (s.id === long.id ? { ...s, name: 'w'.repeat(120) } : s));
    tick();
    ws.restore(snap);
    expect(ws.spaces.get(long.id)!.name).toBe('w'.repeat(100));
  });

  it('starts new tasks and sheets in no space', () => {
    const { ws } = device();
    expect(ws.addTask('x')).toMatchObject({ space: '', clk: { space: ZERO } });
    expect(ws.addSheet()).toMatchObject({ space: '', clk: { space: ZERO } });
  });

  it('announces space changes, and changes merged from elsewhere', () => {
    const { ws } = device();
    const changes: Change[] = [];
    ws.subscribe((c) => changes.push(c));
    const work = ws.addSpace('Work');
    ws.renameSpace(work.id, 'Office');
    expect(changes.map((c) => [...c.spaces])).toEqual([[work.id], [work.id]]);
    const other = device('phone');
    other.ws.merge(ws.toData(), 'remote');
    const heard: Change[] = [];
    ws.subscribe((c) => heard.push(c));
    other.tick();
    other.ws.renameSpace(work.id, 'Work again');
    ws.merge({ spaces: other.ws.toData().spaces }, 'remote');
    expect(heard).toMatchObject([{ origin: 'remote', tasks: new Set(), sheets: new Set(), spaces: new Set([work.id]) }]);
    ws.merge({ spaces: other.ws.toData().spaces }, 'remote');
    expect(heard).toHaveLength(1); // nothing new the second time
  });

  it('restores a snapshot: spaces, names and assignments as they were, as fresh edits', () => {
    const { ws, tick } = device();
    const work = ws.addSpace('Work');
    const t = ws.addTask('t')!;
    const u = ws.addTask('u')!;
    ws.moveTaskToSpace(t.id, work.id);
    const snap = ws.toData();
    tick();
    ws.renameSpace(work.id, 'Office');
    ws.renameSpace(MAIN, 'Inbox');
    ws.moveTaskToSpace(t.id, MAIN);
    ws.moveTaskToSpace(u.id, work.id);
    const later = ws.addSpace('Later');
    const undo = ws.deleteSpace(work.id);
    tick();
    ws.restore(snap);
    expect(ws.spaces.get(work.id)).toMatchObject({ name: 'Work', deleted: false });
    expect(ws.spaces.get(later.id)!.deleted).toBe(true);
    expect(ws.spaces.get(MAIN)).toMatchObject({ name: '', deleted: false }); // unnamed then
    expect(ws.tasks.get(t.id)!.space).toBe(work.id);
    // "In no space" comes back as a move to Main (a fresh edit can't be "never assigned").
    expect(ws.tasks.get(u.id)).toMatchObject({ space: MAIN });
    expect(ws.tasks.get(u.id)!.clk.space > snap.tasks.find((x) => x.id === u.id)!.clk.space).toBe(true);
    void undo;
  });

  it('restores a snapshot from before spaces: everything back in Main, spaces gone', () => {
    const { ws, tick } = device();
    const t = ws.addTask('t')!;
    const snap = ws.toData();
    const before: WorkspaceData = { tasks: snap.tasks.map(old), sheets: snap.sheets.map(old), focus: snap.focus } as unknown as WorkspaceData;
    tick();
    const work = ws.addSpace('Work');
    ws.moveTaskToSpace(t.id, work.id);
    tick();
    ws.restore(before);
    expect(ws.spaces.get(work.id)!.deleted).toBe(true);
    expect(effectiveSpace(ws.tasks.get(t.id)!.space, ws.spaces.values())).toBe(MAIN);
  });

  it('forgets deleted spaces with the other old tombstones', () => {
    const { ws, tick } = device();
    const work = ws.addSpace('Work');
    const keep = ws.addSpace('Keep');
    ws.deleteSpace(work.id);
    tick(91 * 86_400_000);
    const purged = ws.purgeTombstones(90 * 86_400_000, stampTime);
    expect(purged.spaces).toEqual([work.id]);
    expect([...ws.spaces.keys()]).toEqual([keep.id]);
  });
});

describe('device files', () => {
  const meta = (ws: Workspace) => ({ device: ws.hlc.node, name: 'Phone', app: 'Tack Focus test', written: ws.hlc.now(), seen: {} });

  it('carry spaces, deleted spaces and assignments there and back', () => {
    const { ws } = device('abcd1234');
    const work = ws.addSpace('Work');
    const gone = ws.addSpace('Gone');
    ws.renameSpace(MAIN, 'Inbox');
    const t = ws.addTask('in work')!;
    ws.moveTaskToSpace(t.id, work.id);
    const s = ws.addSheet('notes');
    ws.moveSheetToSpace(s.id, work.id);
    const plain = ws.addTask('nowhere')!;
    ws.deleteSpace(gone.id);
    const doc = makeDeviceFile(ws.toData(), meta(ws)) as Record<string, any>;
    // Live spaces sorted by id, deleted ones among the deletions; assignments only where there are any.
    expect(doc.spaces.map((x: Space) => x.id)).toEqual([work.id, MAIN].sort());
    expect(doc.deleted.spaces).toEqual({ [gone.id]: ws.spaces.get(gone.id)!.clk.deleted });
    expect(Object.keys(doc.tasks.find((x: Task) => x.id === t.id))).toEqual(['id', 'text', 'created', 'completed', 'order', 'deleted', 'space', 'clk']);
    expect(Object.keys(doc.tasks.find((x: Task) => x.id === t.id).clk)).toEqual(['text', 'completed', 'order', 'deleted', 'space']);
    expect(doc.tasks.find((x: Task) => x.id === plain.id)).not.toHaveProperty('space');
    expect(doc.tasks.find((x: Task) => x.id === plain.id).clk).not.toHaveProperty('space');
    expect(Object.keys(doc)).toEqual(['format', 'version', 'device', 'name', 'app', 'written', 'seen', 'focus', 'tasks', 'sheets', 'spaces', 'deleted']);

    const back = parseDeviceFile(serializeDeviceFile(doc), 'abcd1234');
    const other = device('other');
    other.ws.merge(back, 'remote');
    expect(records(other.ws)).toEqual(records(ws));
  });

  it('without spaces, are byte for byte what versions before spaces wrote', () => {
    const { ws } = device('abcd1234');
    const a = ws.addTask('a')!;
    ws.addTask('b', { asNow: true });
    ws.complete(a.id);
    ws.addSheet('notes');
    ws.deleteTask(ws.addTask('gone')!.id);
    const data = ws.toData();
    const m = meta(ws);
    // What makeDeviceFile did before spaces, on records as they were then.
    const byId = (x: { id: string }, y: { id: string }) => (x.id < y.id ? -1 : x.id > y.id ? 1 : 0);
    const was = { tasks: data.tasks.map(old), sheets: data.sheets.map(old) };
    const gone = (rs: { id: string; deleted: boolean; clk: { deleted: Stamp } }[]) => Object.fromEntries(rs.filter((r) => r.deleted).sort(byId).map((r) => [r.id, r.clk.deleted]));
    const legacy = JSON.stringify(
      {
        format: 'tack-sync-device', version: 1, device: m.device, name: m.name, app: m.app, written: m.written, seen: {},
        focus: { ...data.focus },
        tasks: was.tasks.filter((t) => !t.deleted).sort(byId),
        sheets: was.sheets.filter((s) => !s.deleted).sort(byId),
        deleted: { tasks: gone(was.tasks), sheets: gone(was.sheets) },
      },
      wellFormedValues,
    );
    const text = serializeDeviceFile(makeDeviceFile(data, m));
    expect(text).toBe(legacy);
    expect(text).not.toMatch(/"spaces?"/);
  });

  it('cut a space name over 100 characters (the space still comes in), and count spaces against the record limit', () => {
    const w = at(NOW - 1000);
    const base = { format: 'tack-sync-device', version: 1, device: 'abcd1234', written: w, tasks: [], sheets: [] };
    const sp = (id: string, name: string) => ({ id, name, order: 'V', deleted: false, clk: { name: w, order: w, deleted: w } });
    const file = parseDeviceFile(JSON.stringify({ ...base, spaces: [sp('ok', 'y'.repeat(100)), { ...sp('long', 'x'.repeat(101)), order: 'X' }, sp('tidy', ` ${'z'.repeat(100)} `)], deleted: { spaces: { gone: w, 'bad id': w } } }), 'abcd1234', NOW);
    expect(file.spaces.map((s) => s.id).sort()).toEqual(['gone', 'long', 'ok', 'tidy']);
    expect(file.spaces.find((s) => s.id === 'long')).toEqual({ ...sp('long', 'x'.repeat(100)), order: 'X' });
    expect(file.spaces.find((s) => s.id === 'gone')).toEqual({ id: 'gone', name: '', order: 'V', deleted: true, clk: { name: ZERO, order: ZERO, deleted: w } });
    // Duplicates merge.
    const dup = parseDeviceFile(JSON.stringify({ ...base, spaces: [sp('w', 'Work'), { ...sp('w', 'Office'), clk: { name: at(NOW - 500), order: w, deleted: w } }] }), 'abcd1234', NOW);
    expect(dup.spaces).toMatchObject([{ id: 'w', name: 'Office' }]);
    // The record limit counts spaces and deleted spaces too.
    const many = (n: number) => Object.fromEntries(Array.from({ length: n }, (_, i) => [`s${i}`, w]));
    const half = MAX_RECORDS / 2;
    expect(() => parseDeviceFile(JSON.stringify({ ...base, spaces: Array.from({ length: half }, () => 0), deleted: { spaces: many(half + 1) } }), 'abcd1234', NOW)).toThrow(DeviceFileError);
    expect(() => parseDeviceFile(JSON.stringify({ ...base, spaces: Array.from({ length: half }, () => 0), deleted: { spaces: many(half) } }), 'abcd1234', NOW)).not.toThrow();
  });
});

describe('tack.json', () => {
  it('exports spaces (deleted ones nameless) and assignments, and imports them back', () => {
    const a = device('a');
    const work = a.ws.addSpace('Work');
    const gone = a.ws.addSpace('Secret project');
    a.ws.renameSpace(MAIN, 'Inbox');
    const t = a.ws.addTask('in work')!;
    a.ws.moveTaskToSpace(t.id, work.id);
    const s = a.ws.addSheet('notes');
    a.ws.moveSheetToSpace(s.id, work.id);
    a.ws.deleteSpace(gone.id);
    const file = exportFile(a.ws);
    // The top level lists every task and sheet, whatever space it's in.
    expect(file.tasks.map((x) => x.id)).toContain(t.id);
    expect(file.sheets.map((x) => x.id)).toContain(s.id);
    expect(file.tackFocus.spaces?.find((x) => x.id === gone.id)).toMatchObject({ name: '', deleted: true });
    const text = JSON.stringify(file);
    expect(text).not.toContain('Secret project');

    const b = device('b');
    importFile(b.ws, text);
    expect(b.ws.tasks.get(t.id)).toMatchObject({ space: work.id, clk: { space: a.ws.tasks.get(t.id)!.clk.space } });
    expect(b.ws.sheets.get(s.id)!.space).toBe(work.id);
    expect(b.ws.spaces.get(MAIN)!.name).toBe('Inbox');
    expect(b.ws.spaces.get(gone.id)!.deleted).toBe(true);
    const before = JSON.stringify(b.ws.toData());
    importFile(b.ws, text);
    expect(JSON.stringify(b.ws.toData())).toBe(before);
  });

  it('imports a space name over 100 characters cut to 100, as any device reads it', () => {
    const a = device('a');
    const work = a.ws.addSpace('Work');
    const file = exportFile(a.ws);
    file.tackFocus.spaces = file.tackFocus.spaces!.map((s) => ({ ...s, name: 'x'.repeat(150) }));
    const b = device('b');
    importFile(b.ws, JSON.stringify(file));
    expect(b.ws.spaces.get(work.id)!.name).toBe('x'.repeat(100));
  });

  it("writes a deleted space without its name and without its name's clock, so the blank never ties with the name", () => {
    // The phone names a space "!Urgent" (JSON sorts it before ""); the desk has it, then the deletion.
    const phone = device('phone');
    const urgent = phone.ws.addSpace('!Urgent');
    const desk = device('desk');
    desk.ws.merge(phone.ws.toData(), 'remote');
    phone.tick();
    const undo = phone.ws.deleteSpace(urgent.id);
    desk.ws.merge({ spaces: [{ id: urgent.id, name: '', order: 'V', deleted: true, clk: { name: ZERO, order: ZERO, deleted: phone.ws.spaces.get(urgent.id)!.clk.deleted } }] }, 'remote');
    const file = exportFile(desk.ws);
    expect(file.tackFocus.spaces).toEqual([{ id: urgent.id, name: '', order: urgent.order, deleted: true, clk: { name: ZERO, order: urgent.clk.order, deleted: desk.ws.spaces.get(urgent.id)!.clk.deleted } }]);
    // The desk opens its file again (Tack for Windows keeps its records in tack.json); then the phone undoes.
    const restarted = device('desk');
    importFile(restarted.ws, JSON.stringify(file));
    phone.tick();
    undo();
    restarted.ws.merge({ spaces: phone.ws.toData().spaces }, 'remote');
    expect(restarted.ws.spaces.get(urgent.id)).toMatchObject({ name: '!Urgent', deleted: false });
    phone.ws.merge(restarted.ws.toData(), 'remote');
    expect(phone.ws.spaces.get(urgent.id)).toMatchObject({ name: '!Urgent', deleted: false });
  });

  it('without spaces, says nothing of them (as before spaces)', () => {
    const a = device('a');
    a.ws.addTask('x');
    a.ws.deleteTask(a.ws.addTask('y')!.id);
    a.ws.addSheet('n');
    const file = exportFile(a.ws, new Date(NOW));
    const text = JSON.stringify(file, null, 2);
    expect(text).not.toMatch(/"spaces?"/);
    // What exportFile did before spaces, on records as they were then.
    const data = a.ws.toData();
    const was = { tasks: data.tasks.map(old), sheets: data.sheets.map(old) };
    const legacy: TackFile['tackFocus'] = {
      format: 1,
      exported: file.tackFocus.exported,
      focus: data.focus,
      tasks: was.tasks.map((t) => (t.deleted ? { ...t, text: '' } : t)),
      sheets: was.sheets.map((s) => (s.deleted ? { ...s, text: '' } : s)),
    };
    expect(JSON.stringify(file.tackFocus)).toBe(JSON.stringify(legacy));
  });
});

describe('storage', () => {
  it('keeps spaces in the meta store, merged record by record, so a stale tab undoes nothing', async () => {
    const db = await IdbBackend.open();
    const tabA = device('tabA');
    const work = tabA.ws.addSpace('Work');
    await db.write({ tasks: [], sheets: [], spaces: [work] });
    const tabB = device('tabB');
    tabB.ws.merge((await db.load()).data, 'storage');
    tabA.tick();
    tabA.ws.renameSpace(work.id, 'Office');
    await db.write({ tasks: [], sheets: [], spaces: [tabA.ws.spaces.get(work.id)!] });
    // Tab B hadn't heard of the rename: it deletes the space and saves its whole record.
    tabB.tick(2000);
    tabB.ws.deleteSpace(work.id);
    await db.write({ tasks: [], sheets: [], spaces: [tabB.ws.spaces.get(work.id)!] });
    const stored = (await db.load()).data.spaces.find((s) => s.id === work.id)!;
    expect(stored).toMatchObject({ name: 'Office', deleted: true });
    await db.write({ tasks: [], sheets: [], deleteSpaces: [work.id] });
    expect((await db.load()).data.spaces.find((s) => s.id === work.id)).toBeUndefined();
  });

  it('loads records saved before spaces as in no space, and leaves out what is not a space', async () => {
    const db = await IdbBackend.open();
    await db.write({ tasks: [old(task('oldtask00001', '', ZERO))], sheets: [old(sheet('oldsheet0001'))] });
    const { data } = await db.load();
    expect(data.tasks.find((t) => t.id === 'oldtask00001')).toMatchObject({ space: '', clk: { space: ZERO } });
    expect(data.sheets.find((s) => s.id === 'oldsheet0001')).toMatchObject({ space: '', clk: { space: ZERO } });

    const mem = new MemoryBackend();
    (mem as unknown as { set(k: string, v: string): void }).set('tack.spaces', JSON.stringify([space('ok'), { id: 'bad id!' }, 'x', space('noclock', 'n', false, 'nope')]));
    expect((await mem.load()).data.spaces.map((s) => s.id)).toEqual(['ok']);
    (mem as unknown as { set(k: string, v: string): void }).set('tack.spaces', '{"not":"a list"}');
    expect((await mem.load()).data.spaces).toEqual([]);
  });

  it('does the same where only localStorage works', async () => {
    const mem = new MemoryBackend();
    const a = device('a');
    const work = a.ws.addSpace('Work');
    const t = a.ws.addTask('t')!;
    a.ws.moveTaskToSpace(t.id, work.id);
    await mem.write({ tasks: [a.ws.tasks.get(t.id)!], sheets: [], spaces: [work] });
    a.tick();
    a.ws.renameSpace(work.id, 'Office');
    await mem.write({ tasks: [], sheets: [], spaces: [a.ws.spaces.get(work.id)!] });
    await mem.write({ tasks: [], sheets: [], spaces: [work] }); // stale: changes nothing
    const { data } = await mem.load();
    expect(data.spaces).toMatchObject([{ id: work.id, name: 'Office' }]);
    expect(data.tasks[0]).toMatchObject({ space: work.id });
  });

  it('keeps spaces in snapshots, and restoring one brings them back', async () => {
    const db = await IdbBackend.open();
    const a = device('a');
    const work = a.ws.addSpace('Work');
    const t = a.ws.addTask('t')!;
    a.ws.moveTaskToSpace(t.id, work.id);
    await db.putSnapshot({ day: '2026-10-21', savedAt: '2026-10-21T09:00:00.000+00:00', data: a.ws.toData() }, 7);
    a.tick();
    a.ws.deleteSpace(work.id);
    a.ws.moveTaskToSpace(t.id, MAIN);
    const [snap] = await db.snapshots();
    a.tick();
    a.ws.restore(snap!.data);
    expect(a.ws.spaces.get(work.id)!.deleted).toBe(false);
    expect(effectiveSpace(a.ws.tasks.get(t.id)!.space, a.ws.spaces.values())).toBe(work.id);
  });
});

describe('joining a sync folder already in use', () => {
  it('keeps a welcome task or sheet you moved to a space: moving it is touching it', () => {
    const { ws } = device('newphone');
    const tips = ['Tip one', 'Tip two', 'Tip three'].map((text) => ws.addTask(text)!);
    const welcomeSheet = ws.ensureSheet();
    ws.editSheet(welcomeSheet.id, 'Scrap paper');
    const blank = ws.addSheet();
    const other = ws.addSheet('Mine');
    const welcome = { tasks: Object.fromEntries(tips.map((t) => [t.id, t.text])), sheets: { [welcomeSheet.id]: 'Scrap paper' } };
    expect(untouchedWelcome(ws, welcome)).toEqual({ tasks: tips.map((t) => t.id), sheets: [welcomeSheet.id, blank.id] });
    const work = ws.addSpace('Work');
    ws.moveTaskToSpace(tips[1]!.id, work.id);
    ws.moveSheetToSpace(welcomeSheet.id, work.id);
    ws.moveSheetToSpace(blank.id, MAIN);
    expect(untouchedWelcome(ws, welcome)).toEqual({ tasks: [tips[0]!.id, tips[2]!.id], sheets: [] });
    void other;
  });
});

describe('syncing spaces', () => {
  function world(...labels: string[]) {
    const cloud = new Cloud();
    const clock: Clock = { now: NOW };
    const devices = labels.map((l) => new Device(l, cloud, clock));
    return { cloud, devices, tick: (ms = 1000) => (clock.now += ms) };
  }
  const expectSame = (devices: Device[]) => {
    for (const d of devices.slice(1)) {
      expect(records(d.ws)).toEqual(records(devices[0]!.ws));
      expect(visible(d.ws)).toEqual(visible(devices[0]!.ws));
    }
  };

  it('keeps a move when a version before spaces edits the task, and the upgraded version learns it', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    desk.old = true;
    const t = phone.ws.addTask('Draft the report')!;
    await settle(devices);
    tick();
    const work = phone.ws.addSpace('Work');
    phone.ws.moveTaskToSpace(t.id, work.id);
    await settle(devices);
    expect(desk.ws.tasks.get(t.id)!.space).toBe(''); // it can't read it
    tick();
    desk.ws.editTask(t.id, 'Draft the drainage report');
    await settle(devices);
    expect(phone.ws.tasks.get(t.id)).toMatchObject({ text: 'Draft the drainage report', space: work.id });
    // Upgraded: no file has changed, but it reads them all once more and learns the move.
    desk.old = false;
    tick();
    await desk.sync();
    expect(desk.ws.tasks.get(t.id)).toMatchObject({ text: 'Draft the drainage report', space: work.id });
    expect(desk.ws.spaces.get(work.id)?.name).toBe('Work');
    await settle(devices);
    expectSame(devices);
  });

  it("shows a deleted space's tasks in Main everywhere, and Undo puts them back", async () => {
    const { devices, tick } = world('phone', 'desk', 'tablet');
    const [phone, desk, tablet] = devices as [Device, Device, Device];
    const t = phone.ws.addTask('Fix the build')!;
    const s = phone.ws.addSheet('Build notes');
    const dev = phone.ws.addSpace('Dev');
    phone.ws.moveTaskToSpace(t.id, dev.id);
    phone.ws.moveSheetToSpace(s.id, dev.id);
    await settle(devices);
    expect(shownIn(tablet.ws)).toMatchObject({ 'Fix the build': dev.id, 'Build notes': dev.id });
    tick();
    const undo = desk.ws.deleteSpace(dev.id);
    await settle(devices);
    for (const d of devices) {
      expect(shownIn(d.ws)).toMatchObject({ 'Fix the build': MAIN, 'Build notes': MAIN });
      expect(d.ws.tasks.get(t.id)!.space).toBe(dev.id);
    }
    tick();
    undo();
    await settle(devices);
    for (const d of devices) expect(shownIn(d.ws)).toMatchObject({ 'Fix the build': dev.id, 'Build notes': dev.id });
    expectSame(devices);
  });

  it('reads a file once more when an older reader read it, then skips it until it changes', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('x');
    await settle(devices);
    const name = `${phone.id}.json`;
    expect(desk.saved.files[name]!.reader).toBe(READER);
    const reads = () => desk.view.reads.get(name) ?? 0;
    const before = reads();
    tick();
    await desk.sync();
    expect(reads()).toBe(before); // unchanged, read by this reader: skipped
    // Sync state written by an older version (an old tab left open, say): no reader.
    withoutReader(desk.saved);
    tick();
    expect((await desk.sync()).merged).toEqual([phone.id]);
    expect(reads()).toBe(before + 1);
    expect(desk.saved.files[name]!.reader).toBe(READER);
    tick();
    expect((await desk.sync()).merged).toEqual([]);
    expect(reads()).toBe(before + 1);
    // A problem file too is noted with the reader, and read once more after an older one noted it.
    phone.view.cloud.put(name, '{"format":"tack-sync-device",');
    tick();
    await desk.sync();
    expect(desk.saved.files[name]).toMatchObject({ problem: 'unreadable', reader: READER });
    withoutReader(desk.saved);
    tick();
    expect((await desk.sync()).problems).toEqual([{ device: phone.id, reason: 'unreadable' }]);
    expect(reads()).toBe(before + 3);
  });

  it('writes its file for a space change alone: a move, a rename, a deletion', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const t = phone.ws.addTask('t')!;
    const s = phone.ws.addSheet('n');
    const work = phone.ws.addSpace('Work');
    await settle(devices);
    for (const change of [
      () => phone.ws.moveTaskToSpace(t.id, work.id),
      () => phone.ws.renameSpace(work.id, 'Office'),
      () => phone.ws.renameSpace(MAIN, 'Inbox'),
      () => phone.ws.moveSheetToSpace(s.id, work.id),
      () => phone.ws.deleteSpace(work.id),
    ]) {
      tick();
      change();
      expect((await phone.sync()).wrote).toBe(true);
      expect((await desk.sync()).merged).toEqual([phone.id]);
      expect(records(desk.ws)).toEqual(records(phone.ws));
    }
    expect((await desk.sync()).wrote).toBe(false);
  });

  it('writes its file again in full after an older version wrote it under the same id (an old tab left open)', async () => {
    const { devices, tick, cloud } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const report = phone.ws.addTask('report')!;
    const work = phone.ws.addSpace('Work');
    phone.ws.moveTaskToSpace(report.id, work.id);
    await phone.sync();
    expect(phone.saved.own.writer).toBe(WRITER);
    // A tab of an older version, open in the same browser: the same device id and sync state. It
    // edits something and writes the file its way, without spaces, noting the write its way too.
    tick();
    phone.old = true;
    phone.ws.editTask(report.id, 'report, from the old tab');
    expect((await phone.sync()).wrote).toBe(true);
    expect(cloud.files.get(`${phone.id}.json`)!.text).not.toContain('"space');
    expect(phone.saved.own.writer).toBeUndefined();
    // This version's next cycle has nothing new, but writes the file again, spaces and all.
    phone.old = false;
    tick();
    expect((await phone.sync()).wrote).toBe(true);
    expect(phone.saved.own.writer).toBe(WRITER);
    tick();
    expect((await phone.sync()).wrote).toBe(false);
    // So a device that reads it only now gets the space and what's in it.
    await desk.sync();
    expect(shownIn(desk.ws)['report, from the old tab']).toBe(work.id);
  });

  it('gives a space with a long name, and whatever is in it, to every device alike', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const report = phone.ws.addTask('report')!;
    const long = phone.ws.addSpace('x'.repeat(101));
    phone.ws.moveTaskToSpace(report.id, long.id);
    await settle(devices);
    expect(shownIn(desk.ws).report).toBe(long.id);
    expectSame(devices);
    // Deleted, renamed long, undone: every step reaches the other device.
    const work = phone.ws.addSpace('Work');
    phone.ws.moveTaskToSpace(report.id, work.id);
    await settle(devices);
    tick();
    const undo = phone.ws.deleteSpace(work.id);
    await settle(devices);
    expect(shownIn(desk.ws).report).toBe(MAIN);
    tick();
    undo();
    phone.ws.renameSpace(work.id, 'y'.repeat(120));
    await settle(devices);
    expect(desk.ws.spaces.get(work.id)).toMatchObject({ name: 'y'.repeat(100), deleted: false });
    expect(shownIn(desk.ws).report).toBe(work.id);
    expectSame(devices);
  });

  it('keeps both versions of a note written in two places at once, neither lost to a space', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const s = phone.ws.addSheet('Plan');
    const work = phone.ws.addSpace('Work');
    await settle(devices);
    tick();
    phone.ws.moveSheetToSpace(s.id, work.id);
    phone.ws.editSheet(s.id, 'Plan - phone');
    desk.ws.editSheet(s.id, 'Plan - desk');
    await settle(devices);
    const texts = visible(phone.ws).sheets.sort();
    expect(texts).toEqual(['Plan - desk', 'Plan - phone']);
    // Both versions end up in the space the note was moved to, or in Main: never lost.
    for (const d of devices) {
      const shown = shownIn(d.ws);
      expect(shown['Plan - phone']).toBe(work.id);
      expect([work.id, MAIN]).toContain(shown['Plan - desk']);
    }
    expectSame(devices);
  });
});
