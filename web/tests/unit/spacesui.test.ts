import { describe, expect, it } from 'vitest';
import { encode, Hlc, ZERO } from '../../src/model/hlc';
import { ALL, MAIN, spaceLabel } from '../../src/model/spaces';
import type { Task } from '../../src/model/types';
import { Workspace } from '../../src/model/workspace';

/**
 * Spaces on screen: what a space shows (its tasks in the one order, its done, its sheets, its
 * counts), adding into a space, reordering inside one, and the new model ops (addTask/addSheet in
 * a space, moveSpace). Tack for Windows has the same cases, and the shared vectors
 * (vectors.test.ts) hold both apps to the same stamps.
 */

const NOW = Date.UTC(2026, 10, 2, 9, 0, 0);

function device(node = 'desk', start = NOW) {
  let wall = start;
  const ws = new Workspace(new Hlc(node, () => wall), () => new Date(wall));
  return { ws, tick: (ms = 1000) => (wall += ms) };
}

/** Tasks added in this order end up last-added first; returns them top to bottom. */
function tasks(ws: Workspace, ...specs: [string, string?][]): Task[] {
  const out: Task[] = [];
  for (let i = specs.length - 1; i >= 0; i--) out.unshift(ws.addTask(specs[i]![0], { space: specs[i]![1] })!);
  return out;
}

const texts = (list: Task[]) => list.map((t) => t.text);
const orders = (ws: Workspace) => new Map([...ws.tasks.values()].map((t) => [t.id, t.order]));

describe('adding into a space', () => {
  it('puts a task in a live space, stamped with the clock of everything else about it', () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const t = ws.addTask('Draft the report', { space: work.id })!;
    expect(t).toMatchObject({ space: work.id, clk: { space: t.clk.text } });
    expect(t.clk.space).toBe(t.clk.order);
    expect(ws.spaceOf(t)).toBe(work.id);
  });

  it('puts it in no space for Main, All, an unknown space or a deleted one', () => {
    const { ws } = device();
    const gone = ws.addSpace('Gone');
    ws.deleteSpace(gone.id);
    for (const space of [undefined, '', MAIN, ALL, 'nosuchspace', gone.id, 'not an id!']) {
      expect(ws.addTask(`in ${space}`, { space })).toMatchObject({ space: '', clk: { space: ZERO } });
      expect(ws.addSheet('', space)).toMatchObject({ space: '', clk: { space: ZERO } });
    }
  });

  it('keeps adding at the top of the one order, and into focus too', () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const [a] = tasks(ws, ['a']);
    const b = ws.addTask('b', { space: work.id, asNow: true })!;
    expect(ws.now?.id).toBe(b.id);
    expect(b.order < a!.order).toBe(true);
    const c = ws.addTask('c', { space: work.id })!;
    expect(ws.open.map((t) => t.id)).toEqual([c.id, a!.id]);
  });

  it('adds pasted lines to a space, in order, with one undo', () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const { added, undo } = ws.addTasks(['one', 'two', 'three'], work.id);
    expect(texts(ws.openIn(work.id))).toEqual(['one', 'two', 'three']);
    expect(added.every((t) => t.space === work.id && t.clk.space === t.clk.text)).toBe(true);
    undo();
    expect(ws.openIn(work.id)).toEqual([]);
  });

  it('puts a sheet in a space, after the last sheet of all', () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const first = ws.addSheet('first');
    const s = ws.addSheet('', work.id);
    expect(s).toMatchObject({ text: '', space: work.id, clk: { space: s.clk.text } });
    expect(s.order > first.order).toBe(true);
    expect(ws.sheetsIn(work.id).map((x) => x.id)).toEqual([s.id]);
    expect(ws.sheetsIn(MAIN).map((x) => x.id)).toEqual([first.id]);
    expect(ws.sheetsIn(ALL).map((x) => x.id)).toEqual([first.id, s.id]);
  });
});

describe('what a space shows', () => {
  it('shows nothing of spaces until there is one besides Main', () => {
    const { ws } = device();
    expect(ws.hasSpaces).toBe(false);
    ws.renameSpace(MAIN, 'Work');
    expect(ws.hasSpaces).toBe(false);
    const dev = ws.addSpace('Dev');
    expect(ws.hasSpaces).toBe(true);
    ws.deleteSpace(dev.id);
    expect(ws.hasSpaces).toBe(false);
  });

  it('falls back to Main for a space that is gone or never arrived', () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const dev = ws.addSpace('Dev');
    expect(ws.shown(ALL)).toBe(ALL);
    expect(ws.shown(MAIN)).toBe(MAIN);
    expect(ws.shown(work.id)).toBe(work.id);
    expect(ws.shown('')).toBe(MAIN);
    expect(ws.shown('nosuchspace')).toBe(MAIN);
    const undo = ws.deleteSpace(dev.id);
    expect(ws.shown(dev.id)).toBe(MAIN);
    undo();
    expect(ws.shown(dev.id)).toBe(dev.id);
  });

  it("filters the one order, done and sheets by space; a deleted space's things show in Main", () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const dev = ws.addSpace('Dev');
    const [w1, m1, d1, w2, m2] = tasks(ws, ['w1', work.id], ['m1'], ['d1', dev.id], ['w2', work.id], ['m2']);
    ws.moveTaskToSpace(m2!.id, MAIN);
    expect(texts(ws.openIn(ALL))).toEqual(['w1', 'm1', 'd1', 'w2', 'm2']);
    expect(texts(ws.openIn(work.id))).toEqual(['w1', 'w2']);
    expect(texts(ws.openIn(MAIN))).toEqual(['m1', 'm2']);
    ws.complete(w2!.id);
    ws.complete(m1!.id);
    expect(texts(ws.doneIn(work.id))).toEqual(['w2']);
    expect(texts(ws.doneIn(MAIN))).toEqual(['m1']);
    expect(ws.doneIn(ALL)).toHaveLength(2);
    ws.deleteSpace(dev.id);
    expect(texts(ws.openIn(MAIN))).toEqual(['d1', 'm2']);
    expect(ws.spaceOf(d1!)).toBe(MAIN);
    expect(ws.spaceOf(w1!)).toBe(work.id);
  });

  it('counts what the space shows, the task in focus included where it is', () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const [w1, , w3] = tasks(ws, ['w1', work.id], ['m1'], ['w3', work.id], ['m2']);
    ws.setNow(w1!.id);
    ws.complete(w3!.id);
    expect(ws.summary(work.id)).toMatchObject({ open: 1, doneToday: 1, progress: 0.5 });
    expect(ws.summary(MAIN)).toMatchObject({ open: 2, doneToday: 0, progress: 0 });
    expect(ws.summary()).toEqual(ws.summary(ALL));
    expect(ws.summary()).toMatchObject({ open: 3, doneToday: 1 });
    expect(Object.fromEntries(ws.openCounts())).toEqual({ [ALL]: 3, [work.id]: 1, [MAIN]: 2 });
  });

  it("never makes a space of a record with All's id (only a hand-made file has one): its things are in Main", () => {
    const { ws, tick } = device();
    const work = ws.addSpace('Work');
    tick();
    const c = encode(NOW + 1000, 0, 'phone');
    const crafted = (id: string, text: string): Task => ({
      id, text, created: '2026-11-02T09:00:00.000+00:00', completed: null, order: 'A', deleted: false, space: ALL,
      clk: { text: c, completed: c, order: c, deleted: c, space: c },
    });
    ws.merge({ tasks: [crafted('inall0000001', 'a'), crafted('inall0000002', 'b'), crafted('inall0000003', 'c')], sheets: [], focus: { id: null, clk: ZERO }, spaces: [{ id: ALL, name: 'Everything', order: 'W', deleted: false, clk: { name: c, order: c, deleted: c } }] }, 'remote');
    tasks(ws, ['mine']);
    expect(ws.spaces.get(ALL)).toBeDefined(); // kept, and synced, as it came
    expect(ws.spaceTabs.map((s) => s.id)).toEqual([MAIN, work.id]);
    expect(ws.spaceList.map((s) => s.id)).toEqual([work.id]);
    expect(ws.shown(ALL)).toBe(ALL);
    expect(ws.spaceOf(ws.tasks.get('inall0000001')!)).toBe(MAIN);
    expect(texts(ws.openIn(MAIN))).toEqual(['mine', 'a', 'b', 'c']);
    // All counts each task once.
    expect(Object.fromEntries(ws.openCounts())).toEqual({ [ALL]: 4, [MAIN]: 4 });
    // Nothing new goes in it.
    expect(ws.addTask('new', { space: ALL })!.space).toBe('');
    expect(ws.addSheet('note', ALL).space).toBe('');
    expect(ws.labelOf(ALL)).toBe('Main');
  });

  it('lists spaces with Main first until its record says otherwise', () => {
    const { ws, tick } = device();
    const labels = () => ws.spaceTabs.map((s) => s.label);
    expect(labels()).toEqual(['Main']);
    const work = ws.addSpace('Work');
    tick();
    ws.addSpace('');
    expect(labels()).toEqual(['Main', 'Work', 'Untitled']);
    expect(ws.labelOf(MAIN)).toBe('Main');
    // Renamed, Main has a record, but no place: its key ('V', tied with Work's) means nothing
    // while its order was never stamped, so it stays first.
    ws.renameSpace(MAIN, 'Inbox');
    expect(work.order).toBe('V');
    expect(ws.spaces.get(MAIN)).toMatchObject({ order: 'V', clk: { order: ZERO } });
    expect(labels()).toEqual(['Inbox', 'Work', 'Untitled']);
    expect(ws.labelOf(MAIN)).toBe('Inbox');
    ws.renameSpace(MAIN, '');
    expect(ws.labelOf(MAIN)).toBe('Main');
    expect(labels()).toEqual(['Main', 'Work', 'Untitled']);
    expect(spaceLabel(MAIN, '')).toBe('Main');
  });

  it('never moves Main for a rename, on the device that renamed it or on any other', () => {
    // The phone renames Main while it has no spaces; the PC meanwhile made spaces (its first key 'V').
    const phone = device('phone');
    const pc = device('pc');
    phone.ws.renameSpace(MAIN, 'Inbox');
    const work = pc.ws.addSpace('Work');
    pc.tick();
    pc.ws.addSpace('Dev');
    expect(work.order).toBe('V');
    const labels = (ws: Workspace) => ws.spaceTabs.map((s) => s.label);
    const phoneData = phone.ws.toData();
    phone.ws.merge(pc.ws.toData(), 'remote');
    pc.ws.merge(phoneData, 'remote');
    expect(labels(phone.ws)).toEqual(['Inbox', 'Work', 'Dev']);
    expect(labels(pc.ws)).toEqual(['Inbox', 'Work', 'Dev']);
    // Alt+1 is still Main, everywhere.
    expect(phone.ws.spaceTabs[0]!.id).toBe(MAIN);
    expect(pc.ws.spaceTabs[0]!.id).toBe(MAIN);
    // Renamed again on the PC: still first, both ways.
    pc.tick();
    pc.ws.renameSpace(MAIN, 'Today');
    phone.ws.merge(pc.ws.toData(), 'remote');
    expect(labels(phone.ws)).toEqual(['Today', 'Work', 'Dev']);
    expect(labels(pc.ws)).toEqual(['Today', 'Work', 'Dev']);
  });

  it('gives a renamed Main its place, keeping its name, when it or a space goes above it', () => {
    const { ws, tick } = device();
    const work = ws.addSpace('Work');
    const dev = ws.addSpace('Dev');
    ws.renameSpace(MAIN, 'Inbox');
    const named = ws.spaces.get(MAIN)!;
    tick();
    ws.moveSpace(dev.id, 0);
    expect(ws.spaceTabs.map((s) => s.label)).toEqual(['Dev', 'Inbox', 'Work']);
    const placed = ws.spaces.get(MAIN)!;
    expect(placed).toMatchObject({ name: 'Inbox', clk: { name: named.clk.name, deleted: ZERO } });
    expect(placed.clk.order).not.toBe(ZERO);
    expect(placed.order < work.order).toBe(true);

    // Main itself, renamed but never moved: its order is stamped, its name kept.
    const other = device('other');
    other.ws.addSpace('Work');
    other.ws.addSpace('Dev');
    other.ws.renameSpace(MAIN, 'Inbox');
    other.tick();
    other.ws.moveSpace(MAIN, 2);
    expect(other.ws.spaceTabs.map((s) => s.label)).toEqual(['Work', 'Dev', 'Inbox']);
    expect(other.ws.spaces.get(MAIN)).toMatchObject({ name: 'Inbox' });
  });
});

describe('reordering inside a space', () => {
  /** Global: w1 m1 w2 m2 w3 m3. */
  function mixed() {
    const d = device();
    const work = d.ws.addSpace('Work');
    const list = tasks(d.ws, ['w1', work.id], ['m1'], ['w2', work.id], ['m2'], ['w3', work.id], ['m3']);
    d.tick();
    return { ...d, work: work.id, list };
  }

  it('moves a task past the one below it, staying above the tasks of other spaces that follow', () => {
    const { ws, work, list } = mixed();
    const before = orders(ws);
    ws.moveTaskIn(work, list[0]!.id, 1);
    expect(texts(ws.openIn(work))).toEqual(['w2', 'w1', 'w3']);
    expect(texts(ws.open)).toEqual(['m1', 'w2', 'w1', 'm2', 'w3', 'm3']);
    // Only the moved task got a new key.
    const after = orders(ws);
    expect([...after].filter(([id, o]) => before.get(id) !== o).map(([id]) => id)).toEqual([list[0]!.id]);
  });

  it('moves a task up past the one above it, staying below the tasks of other spaces before that', () => {
    const { ws, work, list } = mixed();
    ws.moveTaskIn(work, list[4]!.id, 1);
    expect(texts(ws.openIn(work))).toEqual(['w1', 'w3', 'w2']);
    expect(texts(ws.open)).toEqual(['w1', 'm1', 'w3', 'w2', 'm2', 'm3']);
  });

  it('moves to the very top and bottom of the space, and clamps beyond them', () => {
    const { ws, work, list } = mixed();
    ws.moveTaskIn(work, list[4]!.id, -5);
    expect(texts(ws.open)).toEqual(['w3', 'w1', 'm1', 'w2', 'm2', 'm3']);
    // Down to the bottom: just below the last task it passes (w2), above m2.
    ws.moveTaskIn(work, list[4]!.id, 99);
    expect(texts(ws.open)).toEqual(['w1', 'm1', 'w2', 'w3', 'm2', 'm3']);
    ws.moveTaskIn(MAIN, list[1]!.id, 2);
    expect(texts(ws.openIn(MAIN))).toEqual(['m2', 'm3', 'm1']);
    expect(texts(ws.open)).toEqual(['w1', 'w2', 'w3', 'm2', 'm3', 'm1']);
  });

  it('changes nothing for a move to where it is, or for a task the space does not show', () => {
    const { ws, work, list } = mixed();
    const before = ws.toData();
    ws.moveTaskIn(work, list[2]!.id, 1);
    ws.moveTaskIn(work, list[1]!.id, 0);
    ws.moveTaskIn(work, 'nosuchtask', 0);
    expect(ws.toData()).toEqual(before);
  });

  it('is the plain move in All', () => {
    const { ws, list } = mixed();
    ws.moveTaskIn(ALL, list[0]!.id, 2);
    expect(texts(ws.open)).toEqual(['m1', 'w2', 'w1', 'm2', 'w3', 'm3']);
  });

  describe('where keys tie (two devices added at the top at once)', () => {
    /** Tasks from elsewhere, by id (their text) with the keys given; `work` puts them in Work. */
    function tied(...specs: [string, string, boolean][]) {
      const d = device();
      const work = d.ws.addSpace('Work');
      const w = encode(NOW - 5000, 0, 'x');
      const task = ([id, order, inWork]: [string, string, boolean]): Task => ({
        id, text: id, created: '2026-11-02T08:00:00.000+00:00', completed: null, order, deleted: false,
        space: inWork ? work.id : '', clk: { text: w, completed: w, order: w, deleted: w, space: inWork ? w : ZERO },
      });
      d.ws.merge({ tasks: specs.map(task) }, 'remote');
      d.tick();
      return { ...d, work: work.id };
    }
    const changed = (ws: Workspace, before: Map<string, string>) => [...orders(ws)].filter(([id, o]) => before.get(id) !== o).map(([id]) => ws.tasks.get(id)!.text);

    it('going down, lands where it needs no other key, never re-keying another space', () => {
      // P (Work) and Q (Main) share a key: just below P would re-key Q. Below Q looks the same in Work.
      const { ws, work } = tied(['pppppppppppp', 'T', true], ['qqqqqqqqqqqq', 'T', false], ['rrrrrrrrrrrr', 'V', true]);
      const s = ws.addTask('S', { space: work })!;
      const before = orders(ws);
      ws.moveTaskIn(work, s.id, 1);
      expect(texts(ws.openIn(work))).toEqual(['pppppppppppp', 'S', 'rrrrrrrrrrrr']);
      expect(changed(ws, before)).toEqual(['S']);
      expect(ws.tasks.get('qqqqqqqqqqqq')!.clk.order).toBe(encode(NOW - 5000, 0, 'x'));
    });

    it('going up, likewise', () => {
      // Just above N would re-key N and the Main task tied with it below.
      const { ws, work } = tied(['hhhhhhhhhhhh', 'T', false], ['nnnnnnnnnnnn', 'T', true], ['oooooooooooo', 'T', false], ['sssssssssss1', 'X', true]);
      const before = orders(ws);
      ws.moveTaskIn(work, 'sssssssssss1', 0);
      expect(texts(ws.openIn(work))).toEqual(['sssssssssss1', 'nnnnnnnnnnnn']);
      expect(changed(ws, before)).toEqual(['sssssssssss1']);
    });

    it('when every place between its neighbours ties, only tasks of this space make room', () => {
      const { ws, work } = tied(['pppppppppppp', 'T', true], ['qqqqqqqqqqqq', 'T', false], ['rrrrrrrrrrrr', 'T', true]);
      const s = ws.addTask('S', { space: work })!;
      const before = orders(ws);
      ws.moveTaskIn(work, s.id, 1);
      expect(texts(ws.openIn(work))).toEqual(['pppppppppppp', 'S', 'rrrrrrrrrrrr']);
      expect(changed(ws, before).sort()).toEqual(['S', 'rrrrrrrrrrrr']);
      // One stamp for the move, then one for the task making room, as a plain move does.
      expect(ws.tasks.get(s.id)!.clk.order < ws.tasks.get('rrrrrrrrrrrr')!.clk.order).toBe(true);
    });
  });
});

describe('clearing done in a space', () => {
  it('clears only what the space shows, and undo brings it back', () => {
    const { ws } = device();
    const work = ws.addSpace('Work');
    const list = tasks(ws, ['w1', work.id], ['m1'], ['w2', work.id]);
    list.forEach((t) => ws.complete(t.id));
    const undo = ws.clearDone(work.id);
    expect(texts(ws.done)).toEqual(['m1']);
    undo();
    expect(ws.done).toHaveLength(3);
    ws.clearDone();
    expect(ws.done).toHaveLength(0);
  });
});

describe('moveSpace', () => {
  function three() {
    const d = device();
    const a = d.ws.addSpace('A');
    const b = d.ws.addSpace('B');
    const c = d.ws.addSpace('C');
    d.tick();
    return { ...d, a, b, c, ids: () => d.ws.spaceTabs.map((s) => s.label) };
  }

  it('gives the space a key between its new neighbours, with one stamp', () => {
    const { ws, a, b, c, ids } = three();
    expect(ids()).toEqual(['Main', 'A', 'B', 'C']);
    ws.moveSpace(c.id, 1);
    expect(ids()).toEqual(['Main', 'C', 'A', 'B']);
    const moved = ws.spaces.get(c.id)!;
    expect(moved.order > ' ' && moved.order < a.order).toBe(true);
    expect(moved.clk.order > c.clk.order).toBe(true);
    expect(moved.clk.name).toBe(c.clk.name);
    expect(ws.spaces.get(a.id)).toEqual(a);
    expect(ws.spaces.get(b.id)).toEqual(b);
    ws.moveSpace(c.id, 3);
    expect(ids()).toEqual(['Main', 'A', 'B', 'C']);
    // Main still has no record.
    expect(ws.spaces.has(MAIN)).toBe(false);
  });

  it('moves Main by giving it a record: no name, only the order stamped', () => {
    const { ws, a, b, ids } = three();
    ws.moveSpace(MAIN, 2);
    expect(ids()).toEqual(['A', 'B', 'Main', 'C']);
    const main = ws.spaces.get(MAIN)!;
    expect(main).toMatchObject({ name: '', deleted: false, clk: { name: ZERO, deleted: ZERO } });
    expect(main.order > b.order).toBe(true);
    expect(main.clk.order).not.toBe(ZERO);
    expect(ws.labelOf(MAIN)).toBe('Main');
    ws.moveSpace(MAIN, 0);
    expect(ids()).toEqual(['Main', 'A', 'B', 'C']);
    expect(ws.spaces.get(MAIN)!.order < a.order).toBe(true);
  });

  it('puts a space above a Main that has no record by giving Main the top key first', () => {
    const { ws, a, b, ids } = three();
    const before = ws.hlc.now();
    ws.moveSpace(b.id, 0);
    expect(ids()).toEqual(['B', 'Main', 'A', 'C']);
    const main = ws.spaces.get(MAIN)!;
    const moved = ws.spaces.get(b.id)!;
    expect(main).toMatchObject({ name: '', clk: { name: ZERO, deleted: ZERO } });
    expect(main.order < a.order && moved.order < main.order).toBe(true);
    // Main's stamp first, then the space's.
    expect(before < main.clk.order && main.clk.order < moved.clk.order).toBe(true);
  });

  it('makes room among tied keys, as a task move does', () => {
    const { ws } = device();
    const w = encode(NOW - 5000, 0, 'x');
    const tie = (id: string, name: string) => ({ id, name, order: 'V', deleted: false, clk: { name: w, order: w, deleted: w } });
    ws.merge({ spaces: [tie('aaaaaaaaaaaa', 'A'), tie('bbbbbbbbbbbb', 'B'), tie('cccccccccccc', 'C')] }, 'remote');
    ws.moveSpace('cccccccccccc', 2);
    expect(ws.spaceTabs.map((s) => s.label)).toEqual(['Main', 'A', 'C', 'B']);
    const keys = ws.spaceList.map((s) => s.order);
    expect(keys[0]! < keys[1]! && keys[1]! < keys[2]!).toBe(true);
  });

  it('changes nothing for its own place, an unknown or deleted space', () => {
    const { ws, b, c } = three();
    ws.deleteSpace(c.id);
    const before = ws.toData();
    ws.moveSpace(b.id, 2);
    ws.moveSpace(MAIN, 0);
    ws.moveSpace(MAIN, -3);
    ws.moveSpace('nosuchspace', 0);
    ws.moveSpace(c.id, 0);
    expect(ws.toData()).toEqual(before);
  });

  it('announces itself, so every view and the store hear of it', () => {
    const { ws, a } = three();
    const changes: string[][] = [];
    ws.subscribe((ch) => changes.push([...ch.spaces]));
    ws.moveSpace(a.id, 3);
    ws.moveSpace(MAIN, 1);
    expect(changes).toEqual([[a.id], [MAIN]]);
  });
});
