import { describe, expect, it } from 'vitest';
import { Hlc, ZERO, decode, encode } from '../../src/model/hlc';
import { keyAfter, keyBefore, keyBetween, keysAt, isOrderKey } from '../../src/model/order';
import { continueList, pastedLines, sheetTitle, noteLinesToTasks } from '../../src/model/text';
import { isoLocal, parseDate } from '../../src/model/time';
import type { Task } from '../../src/model/types';
import { Workspace, type Change } from '../../src/model/workspace';

function fixedClock(start = Date.UTC(2026, 9, 17, 9, 0, 0)) {
  let t = start;
  return { now: () => t, advance: (ms: number) => (t += ms) };
}

function makeWs(node = 'a', clock = fixedClock()) {
  const ws = new Workspace(new Hlc(node, clock.now), () => new Date(clock.now()));
  return { ws, clock };
}

describe('order keys', () => {
  it('stays strictly ordered under random inserts', () => {
    let keys: string[] = [];
    let seed = 7;
    const rand = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
    for (let i = 0; i < 3000; i++) {
      const index = Math.floor(rand() * (keys.length + 1));
      const placed = keysAt(keys, index);
      expect(placed).toHaveLength(1);
      expect(isOrderKey(placed[0])).toBe(true);
      keys = [...keys.slice(0, index), placed[0]!, ...keys.slice(index)];
    }
    for (let i = 1; i < keys.length; i++) expect(keys[i - 1] < keys[i]).toBe(true);
  });

  it('grows slowly when always adding at the top or bottom', () => {
    let top = 'V';
    let bottom = 'V';
    for (let i = 0; i < 1000; i++) {
      const nextTop = keyBefore(top);
      const nextBottom = keyAfter(bottom);
      expect(nextTop < top).toBe(true);
      expect(nextBottom > bottom).toBe(true);
      top = nextTop;
      bottom = nextBottom;
    }
    expect(top.length).toBeLessThan(25);
    expect(bottom.length).toBeLessThan(25);
  });

  it('finds keys between close neighbours', () => {
    expect(keyBetween('V', 'W') > 'V').toBe(true);
    expect(keyBetween('V', 'W') < 'W').toBe(true);
    expect(keyBetween('V', 'V05')).toBe('V02');
    expect(() => keyBetween('W', 'V')).toThrow();
  });

  it('makes room between equal keys, so an item lands where it was put', () => {
    // The desktop click-through: S, T, T, U and a task put between the two T's.
    const placed = keysAt(['S', 'T', 'T', 'U'], 2);
    expect(placed).toHaveLength(2); // the item, and the T below it
    const keys = ['S', 'T', ...placed, 'U'];
    for (let i = 2; i < keys.length; i++) expect(keys[i - 1]! < keys[i]!).toBe(true);
    // Equal keys elsewhere are left alone.
    expect(keysAt(['T', 'T', 'U'], 0)).toEqual([keyBefore('T')]);
    expect(keysAt(['T', 'T', 'U'], 3)).toEqual([keyAfter('U')]);
  });

  it('lands every item where it was put, even among equal keys (randomised)', () => {
    let seed = 13;
    const rand = (n: number) => Math.floor(((seed = (seed * 16807) % 2147483647) / 2147483647) * n);
    const pool = ['0z', 'R', 'S', 'T', 'T', 'T1', 'U', 'V', 'zz'];
    for (let round = 0; round < 2000; round++) {
      const keys = Array.from({ length: rand(8) }, () => pool[rand(pool.length)]!).sort();
      const index = rand(keys.length + 1);
      const placed = keysAt(keys, index);
      const after = [...keys.slice(0, index), ...placed, ...keys.slice(index + placed.length - 1)];
      expect(after).toHaveLength(keys.length + 1);
      expect(after.slice(0, index)).toEqual(keys.slice(0, index));
      for (const k of placed) expect(isOrderKey(k)).toBe(true);
      for (let i = 1; i < after.length; i++) expect(after[i - 1]! <= after[i]!).toBe(true);
      // Strictly between its neighbours, so it sorts at `index` whatever its id.
      if (index > 0) expect(after[index - 1]! < after[index]!).toBe(true);
      if (index < keys.length) expect(after[index]! < after[index + 1]!).toBe(true);
      // Only the neighbours that had no room (the same key as the one above) get new keys.
      const ties = index > 0 ? keys.slice(index).filter((k) => k === keys[index - 1]).length : 0;
      expect(placed).toHaveLength(1 + ties);
    }
  });
});

describe('hybrid logical clock', () => {
  it('is monotonic even when the wall clock goes backwards', () => {
    let wall = 1000;
    const hlc = new Hlc('n', () => wall);
    const a = hlc.now();
    wall = 500;
    const b = hlc.now();
    expect(b > a).toBe(true);
    expect(decode(b)?.ms).toBe(1000);
  });

  it('moves past observed stamps', () => {
    const hlc = new Hlc('a', () => 1000);
    hlc.observe(encode(5000, 3, 'b'));
    const next = hlc.now();
    expect(next > encode(5000, 3, 'b')).toBe(true);
  });

  it('moves past a fast device clock, so what you do next still wins', () => {
    const hlc = new Hlc('a', () => 1000);
    const fast = encode(1000 + 3 * 86400000, 0, 'b');
    hlc.observe(fast);
    expect(hlc.now() > fast).toBe(true);
  });

  it('ignores absurd stamps', () => {
    const hlc = new Hlc('a', () => 1000);
    hlc.observe(encode(1000 + 20 * 365 * 86400000, 0, 'b'));
    expect(decode(hlc.now())?.ms).toBe(1000);
  });

  it('orders as strings', () => {
    expect(ZERO < encode(1, 0, 'a')).toBe(true);
    expect(encode(35, 0, 'a') < encode(36, 0, 'a')).toBe(true);
  });
});

describe('dates', () => {
  it('reads .NET seven-digit fractions and offsets', () => {
    const d = parseDate('2026-10-17T21:40:12.3456789+01:00');
    expect(d?.toISOString()).toBe('2026-10-17T20:40:12.345Z');
  });

  it('round-trips local ISO', () => {
    const now = new Date(2026, 9, 17, 8, 5, 3, 21);
    expect(parseDate(isoLocal(now))?.getTime()).toBe(now.getTime());
  });

  it('rejects garbage', () => {
    expect(parseDate('yesterday-ish')).toBeNull();
    expect(parseDate(null)).toBeNull();
  });
});

describe('text helpers', () => {
  it('splits pasted lists like the desktop app', () => {
    expect(pastedLines('- one\n2. two\r\n\n[ ] three\n• four  ')).toEqual(['one', 'two', 'three', 'four']);
  });

  it('continues lists', () => {
    expect(continueList('- milk', 6)).toEqual({ insert: '\n- ' });
    expect(continueList('9. nine', 7)).toEqual({ insert: '\n10. ' });
    expect(continueList('  [x] done', 10)).toEqual({ insert: '\n  [ ] ' });
    expect(continueList('a\n- ', 4)).toEqual({ end: true, lineStart: 2, lineLength: 2 });
    expect(continueList('- milk', 3)).toBeNull();
    expect(continueList('plain', 5)).toBeNull();
  });

  it('titles sheets by their first line', () => {
    expect(sheetTitle('\n\n# Site visit\nmore')).toBe('Site visit');
    expect(sheetTitle('   ')).toBe('Untitled');
  });

  it('turns note lines into tasks', () => {
    expect(noteLinesToTasks('- a\n\n1. b\n  c  ')).toEqual(['a', 'b', 'c']);
  });
});

describe('workspace', () => {
  it('adds new tasks at the top and keeps one focus task', () => {
    const { ws } = makeWs();
    const a = ws.addTask('first')!;
    const b = ws.addTask('second')!;
    expect(ws.open.map((t) => t.text)).toEqual(['second', 'first']);
    ws.setNow(a.id);
    expect(ws.now?.id).toBe(a.id);
    expect(ws.open.map((t) => t.id)).toEqual([b.id]);
    // The previous focus task takes the new focus task's place.
    const c = ws.addTask('third')!;
    ws.setNow(b.id);
    expect(ws.now?.id).toBe(b.id);
    expect(ws.open.map((t) => t.id)).toEqual([c.id, a.id]);
  });

  it('ignores blank tasks and collapses whitespace', () => {
    const { ws } = makeWs();
    expect(ws.addTask('   ')).toBeNull();
    expect(ws.addTask('  two\n lines ')?.text).toBe('two lines');
  });

  it('adds a task straight into focus', () => {
    const { ws } = makeWs();
    const a = ws.addTask('a', { asNow: true })!;
    const b = ws.addTask('b', { asNow: true })!;
    expect(ws.now?.id).toBe(b.id);
    expect(ws.open[0].id).toBe(a.id);
  });

  it('completes, reopens and computes progress for today', () => {
    const { ws, clock } = makeWs();
    const a = ws.addTask('a')!;
    ws.addTask('b');
    ws.setNow(a.id);
    ws.complete(a.id);
    expect(ws.now).toBeNull();
    expect(ws.done.map((t) => t.id)).toEqual([a.id]);
    expect(ws.summary()).toEqual({ open: 1, doneToday: 1, progress: 0.5 });
    clock.advance(24 * 3600 * 1000);
    expect(ws.summary().doneToday).toBe(0);
    ws.reopen(a.id);
    expect(ws.open[0].id).toBe(a.id);
    expect(ws.done).toEqual([]);
  });

  it('undoes deletes exactly, including focus', () => {
    const { ws } = makeWs();
    const a = ws.addTask('a')!;
    const b = ws.addTask('b')!;
    const c = ws.addTask('c')!;
    ws.setNow(b.id);
    const undo = ws.deleteTask(b.id);
    expect(ws.now).toBeNull();
    undo();
    expect(ws.now?.id).toBe(b.id);
    const undo2 = ws.deleteTask(a.id);
    expect(ws.open.map((t) => t.id)).toEqual([c.id]);
    undo2();
    expect(ws.open.map((t) => t.id)).toEqual([c.id, a.id]);
  });

  it('moves tasks', () => {
    const { ws } = makeWs();
    const [c, b, a] = ['c', 'b', 'a'].map((t) => ws.addTask(t)!); // list: a b c
    ws.moveTask(a.id, 2);
    expect(ws.open.map((t) => t.text)).toEqual(['b', 'c', 'a']);
    ws.moveTask(a.id, 0);
    expect(ws.open.map((t) => t.text)).toEqual(['a', 'b', 'c']);
    ws.moveTask(c.id, 1);
    expect(ws.open.map((t) => t.text)).toEqual(['a', 'c', 'b']);
    expect(b).toBeTruthy();
  });

  /** Tasks from elsewhere with the given keys, e.g. as two devices left them. */
  function withKeys(ws: Workspace, keys: Record<string, string>, focus: string | null = null) {
    const c = encode(Date.UTC(2026, 9, 17, 8, 0, 0), 0, 'other');
    const tasks: Task[] = Object.entries(keys).map(([text, order]) => ({
      id: text.repeat(12), text, created: '2026-10-17T08:00:00.000Z', completed: null, order, deleted: false,
      clk: { text: c, completed: c, order: c, deleted: c },
    }));
    ws.merge({ tasks, focus: { id: focus && focus.repeat(12), clk: c } }, 'remote');
  }

  it('moves a task between two tasks that share a key', () => {
    const { ws } = makeWs();
    withKeys(ws, { a: 'S', b: 'T', d: 'T', c: 'U' });
    expect(ws.open.map((t) => t.text)).toEqual(['a', 'b', 'd', 'c']);
    ws.moveTask('c'.repeat(12), 2);
    expect(ws.open.map((t) => t.text)).toEqual(['a', 'b', 'c', 'd']);
    // c and the d below it were re-keyed; a and b kept theirs.
    expect(ws.open.map((t) => t.order)).toEqual(['S', 'T', 'U', 'V']);
    ws.moveTask('a'.repeat(12), 1);
    expect(ws.open.map((t) => t.text)).toEqual(['b', 'a', 'c', 'd']);
  });

  it('puts the previous focus task between tied neighbours', () => {
    const { ws } = makeWs();
    withKeys(ws, { p: 'Q', e: 'R', f: 'R', g: 'R', a: 'S' }, 'p');
    expect(ws.open.map((t) => t.text)).toEqual(['e', 'f', 'g', 'a']);
    ws.setNow('f'.repeat(12));
    expect(ws.now?.text).toBe('f');
    expect(ws.open.map((t) => t.text)).toEqual(['e', 'p', 'g', 'a']);
  });

  it('clears done with undo', () => {
    const { ws } = makeWs();
    const a = ws.addTask('a')!;
    ws.complete(a.id);
    const undo = ws.clearDone();
    expect(ws.done).toEqual([]);
    undo();
    expect(ws.done.map((t) => t.id)).toEqual([a.id]);
  });

  it('batches change notifications', () => {
    const { ws } = makeWs();
    const changes: Change[] = [];
    ws.subscribe((c) => changes.push(c));
    ws.addTasks(['x', 'y', 'z']);
    expect(changes).toHaveLength(1);
    expect(changes[0].tasks.size).toBe(3);
  });

  it('keeps at least one sheet; deleting the last one clears it', () => {
    const { ws } = makeWs();
    const s = ws.ensureSheet();
    ws.editSheet(s.id, 'hello');
    const undo = ws.deleteSheet(s.id);
    expect(ws.sheetList).toHaveLength(1);
    expect(ws.sheetList[0].text).toBe('');
    undo();
    expect(ws.sheetList[0].text).toBe('hello');
    const t = ws.addSheet('two');
    const undo2 = ws.deleteSheet(t.id);
    expect(ws.sheetList.map((x) => x.id)).toEqual([s.id]);
    undo2();
    expect(ws.sheetList.map((x) => x.id)).toEqual([s.id, t.id]);
  });

  it('restores a snapshot as fresh edits', () => {
    const { ws, clock } = makeWs();
    const a = ws.addTask('a')!;
    const sheet = ws.ensureSheet();
    ws.editSheet(sheet.id, 'before');
    const snap = ws.toData();
    clock.advance(1000);
    ws.editTask(a.id, 'changed');
    ws.complete(a.id);
    const b = ws.addTask('b')!;
    ws.editSheet(sheet.id, 'after');
    ws.restore(snap);
    expect(ws.open.map((t) => t.text)).toEqual(['a']);
    expect(ws.tasks.get(b.id)?.deleted).toBe(true);
    expect(ws.sheets.get(sheet.id)?.text).toBe('before');
  });
});
