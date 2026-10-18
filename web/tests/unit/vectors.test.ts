/// <reference types="node" />
/**
 * Shared test vectors. Tack for Windows has a C# port of this model (src/Tack/Sync) and its tests
 * (tests/Tack.Tests) replay everything here and must get exactly the same stamps, keys, ids and
 * merged records. Every value is computed by src/model, so the vectors can't drift from it:
 * `npm test` fails if tests/vectors/sync.json is out of date, and `npm run vectors` rewrites it.
 */
process.env.TZ = 'UTC';

import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it, vi } from 'vitest';
import { exportFile, importFile, readFile, type TackFile } from '../../src/model/file';
import { decode, encode, Hlc, isPlausibleStamp, isStamp, stampTime, ZERO, type Stamp } from '../../src/model/hlc';
import { derivedId } from '../../src/model/ids';
import { mergeFocus, mergeSheet, mergeTask } from '../../src/model/merge';
import { isOrderKey, keyAfter, keyBefore, keyBetween, keysAt } from '../../src/model/order';
import { cleanTaskText, noteText } from '../../src/model/text';
import { parseDate } from '../../src/model/time';
import type { Focus, Sheet, Task, WorkspaceData } from '../../src/model/types';
import { Workspace, type Undo } from '../../src/model/workspace';

const VECTORS = '../../../tests/vectors/sync.json';
const DESKTOP_EXPORT = fileURLToPath(new URL('../../../tests/vectors/desktop-export.json', import.meta.url));

/** 2026-10-17 20:00 UTC: when the scripted devices start. */
const T0 = Date.UTC(2026, 9, 17, 20, 0, 0);

/** Characters by code, so unusual ones survive any editor. */
const U = (...codes: number[]) => String.fromCharCode(...codes);
const units = (s: string) => Array.from({ length: s.length }, (_, i) => s.charCodeAt(i));

function rng(seed: number) {
  let s = seed;
  const next = () => (s = (s * 16807) % 2147483647) / 2147483647;
  return {
    int: (n: number) => Math.floor(next() * n),
    pick: <T>(xs: readonly T[]): T => xs[Math.floor(next() * xs.length)]!,
    chance: (p: number) => next() < p,
  };
}

/** Ids come from crypto.getRandomValues; a seeded stand-in makes them the same on every run. */
function seededRandomValues(seed: number) {
  let s = seed;
  return (<T extends ArrayBufferView | null>(array: T): T => {
    if (array) {
      const bytes = new Uint8Array(array.buffer, array.byteOffset, array.byteLength);
      for (let i = 0; i < bytes.length; i++) bytes[i] = (s = (s * 16807) % 2147483647) & 0xff;
    }
    return array;
  }) as typeof crypto.getRandomValues;
}

const hlcState = (h: Hlc) => {
  const inside = h as unknown as { ms: number; counter: number };
  return { ms: inside.ms, counter: inside.counter };
};

function views(ws: Workspace) {
  return {
    now: ws.now?.id ?? null,
    open: ws.open.map((t) => t.id),
    done: ws.done.map((t) => t.id),
    sheets: ws.sheetList.map((s) => s.id),
  };
}

function newestClock(data: WorkspaceData): Stamp {
  let max = data.focus.clk;
  for (const r of [...data.tasks, ...data.sheets]) for (const c of Object.values(r.clk)) if (c > max) max = c;
  return max;
}

// ------------------------------------------------------------------ clocks

function stampVectors() {
  const encodes: [number, number, string][] = [
    [0, 0, '0'],
    [T0, 0, 'phone'],
    [T0, 5, ''],
    [36 ** 9 - 1, 36 ** 4 - 1, 'zz'],
    [123, 46655, 'a_b-C'],
    [1, 1, 'x'],
  ];
  const strings = [
    ZERO,
    encode(T0, 0, 'phone'),
    encode(T0, 1295, 'web-1'),
    '',
    'abc',
    'a.b',
    'a.b.',
    '000000000.0000',
    '1..x',
    '..x',
    ' 1.2.x',
    '-1.2.x',
    '+z.1.n',
    '1x!.2.n',
    'Z.Z.Z',
    '0.0.0.0',
    '-0.0.z',
    'zzzzzzzzz.zzzz.node',
    `${U(0xa0)}1.1.n`,
    `${U(0x180e)}1.1.n`,
    `${U(0xfeff)}2.3.x`,
    `${U(0x85)}2.3.x`,
    encode(Date.UTC(2100, 0, 1), 0, 'future'),
    encode(T0 + 365 * 86400000, 0, 'soon'),
    `${encode(T0, 0, 'ph')} one`,
    encode(T0, 0, 'x').toUpperCase(),
    encode(T0, 0, 'x').replace('.0000.', '.000.'),
    `${encode(T0, 0, 'x')}\n`,
  ];
  return {
    now: T0,
    encode: encodes.map(([ms, counter, node]) => ({ ms, counter, node, stamp: encode(ms, counter, node) })),
    decode: strings.map((stamp) => ({
      stamp,
      value: decode(stamp),
      time: stampTime(stamp),
      isStamp: isStamp(stamp),
      plausible: isPlausibleStamp(stamp, T0),
    })),
  };
}

function hlcVectors() {
  const run = (node: string, plan: ([number] | [number, string])[]) => {
    let wall = 0;
    const h = new Hlc(node, () => wall);
    return {
      node,
      steps: plan.map(([w, seen]) => {
        wall = w;
        if (seen === undefined) return { wall, now: h.now() };
        h.observe(seen);
        return { wall, observe: seen };
      }),
    };
  };
  const T = T0;
  return [
    run('desk', [
      [T], [T], [T], [T - 5000], [T + 1],
      [T + 1, encode(T + 60000, 3, 'phone')], [T + 2],
      [T + 2, encode(T, 9, 'old')], [T + 3],
      [T + 3, encode(T + 11 * 365 * 86400000, 0, 'far')], [T + 4],
      [T + 4, encode(T + 60000, 7, 'phone')], [T + 5],
      [T + 5, 'garbage'], [T + 6],
      [T + 6, encode(T + 60000, 36 ** 4 - 1, 'max')], [T + 7],
      [T + 70000], [T + 70000], [T + 69999],
    ]),
    run('n', [[0], [0], [5], [5, ZERO], [6], [3, '000000005.00zz.q'], [3], [3, '000000005.00zz.a'], [3]]),
  ];
}

// ------------------------------------------------------------------ order keys

function orderVectors() {
  const between: [string | null, string | null][] = [
    [null, null], [null, 'V'], ['V', null], ['A', 'B'], ['A', 'C'], ['V', 'W'], ['Vz', 'W'], ['V', 'V1'],
    ['V0V', 'V1'], ['a', 'a1'], ['0z', '1'], ['y', 'z'], ['zy', 'zz'], ['V', 'Vz'], ['1', '2'], ['01', '02'],
    ['V', 'U'], ['V', 'V'], ['', 'V'], ['V', 'V0V'],
  ];
  const at: { keys: string[]; index: number }[] = [
    { keys: [], index: 0 },
    { keys: ['V'], index: 0 },
    { keys: ['V'], index: 1 },
    { keys: ['A', 'B'], index: 1 },
    { keys: ['V', 'V'], index: 1 },
    { keys: ['A', 'B', 'C'], index: 5 },
    { keys: ['A'], index: -1 },
    { keys: ['U', 'V', 'W'], index: 3 },
    // Equal keys: the neighbours below make room.
    { keys: ['V', 'V'], index: 0 },
    { keys: ['V', 'V'], index: 2 },
    { keys: ['V', 'V', 'X'], index: 1 },
    { keys: ['S', 'T', 'T', 'U'], index: 2 },
    { keys: ['T', 'T', 'T', 'T', 'U'], index: 1 },
    { keys: ['T', 'T', 'T', 'T', 'U'], index: 3 },
    { keys: ['T', 'T', 'T'], index: 2 },
    { keys: ['zz', 'zz'], index: 1 },
    { keys: ['V', 'V0V', 'V0V', 'V1'], index: 2 },
    { keys: ['W', 'V'], index: 1 },
    { keys: ['V', 'V', 'V'], index: -1 },
    { keys: ['V', 'V', 'V'], index: 4 },
  ];

  let top = 'V';
  let bottom = 'V';
  const tops: string[] = [];
  const bottoms: string[] = [];
  for (let i = 0; i < 130; i++) {
    tops.push((top = keyBefore(top)));
    bottoms.push((bottom = keyAfter(bottom)));
  }
  const r = rng(11);
  let keys: string[] = [];
  const inserts: { index: number; key: string }[] = [];
  for (let i = 0; i < 400; i++) {
    const index = r.int(keys.length + 1);
    const [key] = keysAt(keys, index); // these keys never tie, so it's the item's key alone
    inserts.push({ index, key: key! });
    keys = [...keys.slice(0, index), key!, ...keys.slice(index)];
  }
  // The same from a list several devices left with equal keys.
  const tiedStart = ['T', 'T', 'T', 'V', 'V', 'X', 'X', 'X', 'X'];
  let tied = tiedStart;
  const tiedInserts: { index: number; placed: string[] }[] = [];
  for (let i = 0; i < 60; i++) {
    const index = r.int(tied.length + 1);
    const placed = keysAt(tied, index);
    tiedInserts.push({ index, placed });
    tied = [...tied.slice(0, index), ...placed, ...tied.slice(index + placed.length - 1)];
  }

  return {
    after: ['', 'V', 'z', 'zz', 'Vz', 'y', '0', '01', 'A', '9', 'Z', 'zzzzzz', 'a0z', '-', 'V-'].map((a) => ({ a, key: keyAfter(a) })),
    before: ['V', '1', '2', '01', '0z', 'A', 'a', 'V1', '11', '0001', 'z', '0', '00', '-V'].map((b) => ({ b, key: keyBefore(b) })),
    between: between.map(([a, b]) => {
      try {
        return { a, b, key: keyBetween(a, b) };
      } catch {
        return { a, b, error: true };
      }
    }),
    at: at.map(({ keys, index }) => ({ keys, index, placed: keysAt(keys, index) })),
    isOrderKey: ['V', 'V0', '', '0', '01', 'a-b', 'Ab9', 'zz', ' V', 'V\n', 'V1'].map((value) => ({ value, valid: isOrderKey(value) })),
    tops,
    bottoms,
    inserts,
    tied: { start: tiedStart, inserts: tiedInserts },
  };
}

// ------------------------------------------------------------------ ids, text, dates

function idVectors() {
  const r = rng(3);
  let long = '';
  for (let i = 0; i < 300; i++) long += U(r.pick([0x41, 0x7a, 0xe9, 0x4e2d, 0xd83d, 0xde00, 0x20, 0x0a, 0xffff]));
  const sources = ['', 'a', 'abc', 'aaaaaaaaaaaa:0lvaa5zk0.0000.phone', U(0xe9), U(0xd83d, 0xde00), U(0xd800), U(0xdfff, 0xd800), 'x'.repeat(1000), long];
  return sources.map((s) => ({ units: units(s), id: derivedId(s) }));
}

function textVectors() {
  const ws = [0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x180e, 0x2000, 0x2005, 0x200a, 0x200b, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000, 0xfeff, 0x1c, 0x1f];
  const texts = [
    '  a  b  ',
    '\t\nx\r\n',
    `a${U(0xa0)}b`,
    `${U(0x85)}a${U(0x85)}`,
    `${U(0x180e)}x`,
    `${U(0xfeff)}x${U(0xfeff)}`,
    `a${U(0x2028)}b${U(0x2029)}`,
    `a${U(0x200b)}b`,
    `${U(0x3000)}${U(0x5168, 0x89d2)}${U(0x3000)}`,
    `a${U(0x1680, 0x2000, 0x200a, 0x202f, 0x205f)}b`,
    '\v\fa',
    `${U(0x1c)}a${U(0x1f)}`,
    '',
    '   ',
  ];
  const strings = ['a"b', 'a\\b', '\b\f\n\r\t', U(1, 0x1f, 0x7f), U(0xd800), U(0xdc00), U(0xd83d, 0xde00), U(0xde00, 0xd83d), U(0xe9), U(0x2028), '', 'null'];
  return {
    whitespace: ws.map((code) => ({ code, white: U(code).trim() === '' && /\s/.test(U(code)) })),
    clean: texts.map((t) => ({ text: t, clean: cleanTaskText(t), trim: t.trim() })),
    stringify: strings.map((s) => ({ units: units(s), json: JSON.stringify(s) })),
    notes: ['a\r\nb', 'a\rb', 'a\nb', '\r\r\n\n\r', 'a\n\rb', 'x\r\n', '\r', ''].map((t) => ({ text: t, note: noteText(t) })),
  };
}

function dateVectors() {
  return [
    '2026-10-16T09:00:00.1234567+01:00',
    '2026-10-16T09:00:00+01:00',
    '2026-10-16T09:00+01:00',
    '2026-10-16T09:00:00.5Z',
    '2026-10-16t09:00:00z',
    '2026-10-16T09:00:00,25+0130',
    '2026-02-30T00:00:00.000Z',
    '2026-04-31T00:00:00.000Z',
    '2026-02-32T00:00:00.000Z',
    '2026-02-00T00:00:00.000Z',
    '2026-10-16T24:00:00.000Z',
    '2026-10-16T24:00:01.000Z',
    '2026-10-16T09:00:00.000+23:59',
    '2026-10-16T09:00:00.000+24:00',
    '2026-10-16T09:00:00.000+05:60',
    '2026-10-16T09:60:00.000Z',
    '2026-10-16T09:00:60.000Z',
    '2026-13-01T00:00:00Z',
    '0000-01-01T00:00:00.000Z',
    '  2026-10-16T09:00:00Z  ',
    '2026-10-16T09:00:00.000-05:00',
    '1969-12-31T23:59:59.999Z',
    '',
    'nonsense',
  ].map((value) => ({ value, ms: parseDate(value)?.getTime() ?? null }));
}

// ------------------------------------------------------------------ merging records

function mergeVectors() {
  const r = rng(5);
  const clocks = [ZERO, encode(T0, 0, 'a'), encode(T0, 0, 'b'), encode(T0, 1, 'a'), encode(T0, 0, ''), encode(T0 + 1000, 0, ''), encode(T0 + 1000, 2, 'b'), encode(T0 + 60000, 0, 'a')];
  const texts = ['x', 'y', 'Groceries', U(0xe9), 'a"b', 'a\\b', 'line\nbreak', U(0xd83d, 0xde00), U(0x2028), 'null', '', '  '];
  const completes = [null, '2026-10-17T20:00:00.000+00:00', '2026-10-17T21:00:00.000+01:00', ''];
  const orders = ['V', 'U', 'W', 'V1', 'a', '0z'];
  const dates = ['2026-10-17T20:00:00.000+00:00', '2026-10-16T09:00:00.1234567+01:00', '', '2026-10-17T20:00:00.000Z'];
  const ids = ['aaaaaaaaaaaa', 'bbbbbbbbbbbb'];

  const task = (): Task => ({
    id: ids[0]!,
    text: r.pick(texts),
    created: r.pick(dates),
    completed: r.pick(completes),
    order: r.pick(orders),
    deleted: r.chance(0.3),
    clk: { text: r.pick(clocks), completed: r.pick(clocks), order: r.pick(clocks), deleted: r.pick(clocks) },
  });
  const sheet = (): Sheet => ({
    id: ids[1]!,
    text: r.pick(texts),
    updated: r.pick(dates),
    order: r.pick(orders),
    deleted: r.chance(0.3),
    clk: { text: r.pick(clocks), order: r.pick(clocks), deleted: r.pick(clocks) },
  });
  /** Often a copy of the other side with one field changed, so equal clocks (and equal values) come up. */
  const variant = <T extends { clk: object }>(base: T, fresh: () => T): T => {
    if (r.chance(0.4)) return fresh();
    const out = { ...base, clk: { ...base.clk } } as T;
    const other = fresh();
    const keys = Object.keys(other).filter((k) => k !== 'id' && k !== 'clk');
    const k = r.pick(keys) as keyof T;
    out[k] = other[k];
    if (r.chance(0.5)) {
      const ck = r.pick(Object.keys(other.clk)) as keyof T['clk'];
      (out.clk as Record<string, unknown>)[ck as string] = (other.clk as Record<string, unknown>)[ck as string];
    }
    return out;
  };

  const tasks = [];
  for (let i = 0; i < 160; i++) {
    const local = r.chance(0.08) ? undefined : task();
    const remote = local ? variant(local, task) : task();
    const { value, changed } = mergeTask(local, remote);
    tasks.push({ local: local ?? null, remote, value, changed });
  }
  const sheets = [];
  for (let i = 0; i < 160; i++) {
    const local = r.chance(0.08) ? undefined : sheet();
    const remote = local ? variant(local, sheet) : sheet();
    const base = r.pick([undefined, undefined, ZERO, ...clocks]);
    const { value, changed, conflict } = mergeSheet(local, remote, base);
    sheets.push({ local: local ?? null, remote, base: base ?? null, value, changed, conflict: conflict ?? null });
  }
  const focus = [];
  for (let i = 0; i < 50; i++) {
    const local: Focus = { id: r.pick([null, ...ids]), clk: r.pick(clocks) };
    const remote: Focus = r.chance(0.3) ? { ...local } : { id: r.pick([null, ...ids]), clk: r.pick(clocks) };
    focus.push({ local, remote, ...mergeFocus(local, remote) });
  }
  return { tasks, sheets, focus };
}

// ------------------------------------------------------------------ whole workspaces

type Op = { op: string; wall: number; id?: string; text?: string; index?: number; data?: WorkspaceData; base?: Stamp | null; ensured?: string };

/**
 * A device ("desk") doing random things, op by op, sometimes merging what another device
 * ("phone") did meanwhile. The C# workspace replays the same ops and must match the lists after
 * every op, and every record (clocks, keys, dates) at checkpoints and at the end.
 */
function workspaceScript(seed: number, steps: number, merges: boolean) {
  const r = rng(seed);
  let wall = T0;
  const desk = new Workspace(new Hlc('desk', () => wall), () => new Date(wall));
  const phone = new Workspace(new Hlc('phone', () => wall), () => new Date(wall));
  const ops: (Op & { views: ReturnType<typeof views>; check?: WorkspaceData })[] = [];
  const deskUndos: Undo[] = [];
  const phoneUndos: Undo[] = [];
  let snapshot: WorkspaceData | null = null;
  let agreed: Stamp | undefined;
  /** What changed since the devices last exchanged (the way sync batches will travel). */
  const since = (data: WorkspaceData): WorkspaceData => {
    if (agreed === undefined) return data;
    const newer = (clk: object) => Object.values(clk).some((c) => c > agreed!);
    return { tasks: data.tasks.filter((t) => newer(t.clk)), sheets: data.sheets.filter((s) => newer(s.clk)), focus: data.focus };
  };

  const record = (op: Op) => {
    // Tack for Windows always has a sheet to write on, as Tack Focus's UI ensures.
    if (desk.sheetList.length === 0) op.ensured = desk.ensureSheet().id;
    ops.push({ ...op, views: views(desk), ...(ops.length % 30 === 29 ? { check: desk.toData() } : {}) });
  };

  const sheetTexts = ['Shopping\n- milk', 'Plan\n1. one\n2. two', U(0x43, 0x61, 0x66, 0xe9, 0x20, 0x2615), '', 'Notes\r\nwith CRLF', '# Title\n\n- [ ] box'];

  /** One random thing a person might do; returns a description for the replay. */
  const act = (ws: Workspace, undos: Undo[], i: number): Op | null => {
    const open = ws.open;
    const done = ws.done;
    const sheets = ws.sheetList;
    const now = ws.now;
    const choices = ['add', 'add', 'addNow', 'addSheet', 'editSheet', 'editSheet', 'snapshot'];
    if (open.length) choices.push('complete', 'edit', 'setNow', 'move', 'move', 'delete');
    if (now) choices.push('completeNow', 'clearNow', 'editNow', 'deleteNow');
    if (done.length) choices.push('reopen', 'deleteDone', 'clearDone');
    if (sheets.length) choices.push('deleteSheet');
    if (undos.length) choices.push('undo', 'undo');
    if (snapshot && ws === desk) choices.push('restore');
    const op = r.pick(choices);
    switch (op) {
      case 'add':
      case 'addNow': {
        const t = ws.addTask(`task ${i} ${r.pick(['', 'call', U(0xe9, 0x74, 0xe9), '"quoted"'])}`, { asNow: op === 'addNow' })!;
        return { op, wall, id: t.id, text: t.text };
      }
      case 'edit':
      case 'editNow': {
        const t = op === 'editNow' ? now! : r.pick(open);
        const text = `${t.text}${r.pick(['!', ' more', '?'])}`;
        ws.editTask(t.id, text);
        return { op: 'edit', wall, id: t.id, text };
      }
      case 'complete':
      case 'completeNow': {
        const t = op === 'completeNow' ? now! : r.pick(open);
        ws.complete(t.id);
        return { op: 'complete', wall, id: t.id };
      }
      case 'reopen': {
        const t = r.pick(done);
        ws.reopen(t.id);
        return { op, wall, id: t.id };
      }
      case 'setNow': {
        const t = r.pick(open);
        ws.setNow(t.id);
        return { op, wall, id: t.id };
      }
      case 'clearNow':
        ws.clearNow();
        return { op, wall };
      case 'move': {
        const t = r.pick(open);
        const index = r.int(open.length);
        ws.moveTask(t.id, index);
        return { op, wall, id: t.id, index };
      }
      case 'delete':
      case 'deleteNow':
      case 'deleteDone': {
        const t = op === 'deleteNow' ? now! : op === 'deleteDone' ? r.pick(done) : r.pick(open);
        undos.push(ws.deleteTask(t.id));
        return { op: 'delete', wall, id: t.id };
      }
      case 'clearDone':
        undos.push(ws.clearDone());
        return { op, wall };
      case 'undo':
        undos.pop()!();
        return { op, wall };
      case 'addSheet': {
        const s = ws.addSheet();
        return { op, wall, id: s.id };
      }
      case 'editSheet': {
        const s = r.pick(sheets);
        const text = `${r.pick(sheetTexts)}${r.chance(0.5) ? ` ${i}` : ''}`;
        ws.editSheet(s.id, text);
        return { op, wall, id: s.id, text };
      }
      case 'deleteSheet': {
        const s = r.pick(sheets);
        undos.push(ws.deleteSheet(s.id));
        return { op, wall, id: s.id };
      }
      case 'snapshot':
        if (ws === desk) snapshot = ws.toData();
        return ws === desk ? { op, wall } : null;
      case 'restore':
        ws.restore(snapshot!);
        return { op, wall };
    }
    return null;
  };

  const first = desk.ensureSheet();
  ops.push({ op: 'start', wall, ensured: first.id, views: views(desk) });
  phone.ensureSheet();

  for (let i = 0; i < steps; i++) {
    wall += r.pick([0, 0, 1, 7, 1000, 60000]);
    if (merges && r.chance(0.35)) act(phone, phoneUndos, i);
    if (merges && r.chance(0.12)) {
      const data = since(phone.toData());
      const back = since(desk.toData());
      const base = r.pick([undefined, ZERO, agreed, agreed]);
      desk.merge(data, 'remote', base);
      phone.merge(back, 'remote', base);
      if (phone.sheetList.length === 0) phone.ensureSheet();
      agreed = newestClock(desk.toData());
      record({ op: 'merge', wall, data, base: base ?? null });
      continue;
    }
    const op = act(desk, deskUndos, i);
    if (op) record(op);
  }
  return { node: 'desk', ops, data: desk.toData() };
}

/**
 * Tasks with equal keys (what the desktop click-through of 2026-10-18 ran into). A task in focus
 * keeps its key while another one is moved onto that key; a newer focus task arrives from the
 * phone and the first one comes back to the list, tied. Later three devices have added at the
 * top at once. Moving a task between tied ones, or putting the previous focus task there, must
 * still land it there, with the same keys and clocks in both apps.
 */
function tiedScript() {
  let wall = T0;
  const desk = new Workspace(new Hlc('desk', () => wall), () => new Date(wall));
  const phone = new Workspace(new Hlc('phone', () => wall), () => new Date(wall));
  const ops: (Op & { views: ReturnType<typeof views>; check: WorkspaceData })[] = [];
  const record = (op: Omit<Op, 'wall'>) => ops.push({ ...op, wall, views: views(desk), check: desk.toData() });
  const later = () => (wall += 1000);

  record({ op: 'start', ensured: desk.ensureSheet().id });
  const [d, c, b] = ['d', 'c', 'b', 'a'].map((text) => {
    later();
    const t = desk.addTask(text)!;
    record({ op: 'add', id: t.id, text: t.text });
    return t;
  });
  phone.ensureSheet();
  phone.merge(desk.toData(), 'remote');
  later();
  desk.setNow(b!.id);
  record({ op: 'setNow', id: b!.id });
  later();
  desk.moveTask(d!.id, 1); // a d c: d gets the key b still has
  record({ op: 'move', id: d!.id, index: 1 });
  later();
  phone.addTask('from the phone', { asNow: true });
  later();
  const fromPhone = phone.toData();
  desk.merge(fromPhone, 'remote'); // the phone's focus is newer: b is back in the list, tied with d
  record({ op: 'merge', data: fromPhone, base: null });
  later();
  desk.moveTask(c!.id, 2); // up between the tied pair
  record({ op: 'move', id: c!.id, index: 2 });

  later();
  const clk = encode(wall, 0, 'tab');
  const top = (id: string): Task => ({
    id, text: `added at the top on ${id.slice(0, 4)}`, created: '2026-10-17T20:00:00.000+00:00', completed: null,
    order: 'R', deleted: false, clk: { text: clk, completed: clk, order: clk, deleted: clk },
  });
  const tops: WorkspaceData = {
    tasks: ['eeeeeeeeeeee', 'ffffffffffff', 'gggggggggggg', 'hhhhhhhhhhhh'].map(top),
    sheets: [],
    focus: { id: null, clk: ZERO },
  };
  desk.merge(tops, 'remote');
  record({ op: 'merge', data: tops, base: null });
  later();
  const bottom = desk.open.at(-1)!;
  desk.moveTask(bottom.id, 3); // from the bottom to between g and h
  record({ op: 'move', id: bottom.id, index: 3 });
  later();
  desk.setNow('ffffffffffff'); // the phone's task takes its place, between e and g
  record({ op: 'setNow', id: 'ffffffffffff' });
  return { node: 'desk', ops, data: desk.toData() };
}

/** A Tack Focus workspace with a bit of everything, exported: the desktop must read it and write it back unchanged. */
function roundTrip() {
  let wall = T0 + 3_600_000;
  const ws = new Workspace(new Hlc('phone1', () => wall), () => new Date(wall));
  const tick = () => (wall += 1500);
  const shopping = ws.ensureSheet();
  ws.editSheet(shopping.id, 'Shopping\n- milk\n- eggs');
  tick();
  const texts = [
    'Send the revised fee proposal',
    'Pay the invoice',
    'Book the crane for Thursday',
    `Reply to Mia ${U(0xd83d, 0xde00)}`,
    'Quotes "and" back\\slashes',
    'Secret task to delete',
    'Call the plumber',
  ];
  const t = texts.map((x) => {
    tick();
    return ws.addTask(x)!;
  });
  tick();
  ws.setNow(t[2]!.id);
  tick();
  ws.complete(t[0]!.id);
  tick();
  ws.complete(t[1]!.id);
  tick();
  ws.moveTask(t[6]!.id, 2);
  tick();
  ws.deleteTask(t[5]!.id);
  tick();
  ws.reopen(t[1]!.id);
  tick();
  ws.complete(t[1]!.id);
  tick();
  const site = ws.addSheet();
  ws.editSheet(site.id, 'Site visit\n- rebar\n- formwork');
  tick();
  const old = ws.addSheet();
  ws.editSheet(old.id, 'Old notes');
  tick();
  ws.deleteSheet(old.id);
  tick();
  const ideas = ws.addSheet();
  ws.editSheet(ideas.id, `Ideas\n- Caf${U(0xe9)} ${U(0x2615)}\n\ttabbed`);
  tick();
  const file = JSON.stringify(exportFile(ws, new Date(wall)), null, 2);
  return { wall, file, views: views(ws), data: ws.toData() };
}

/** A file as Tack for Windows 1.1 writes it (with some hand edits). */
const desktopFile = `${U(0xfeff)}{
  "version": 1,
  "tasks": [
    { "id": "aaaaaaaaaaaa", "text": "Finish the drainage report draft", "created": "2026-10-16T09:00:00.1234567+01:00", "now": true },
    { "id": "bbbbbbbbbbbb", "text": "Reply to Mia about the site photos", "created": "2026-10-16T09:01:00+01:00", "now": false },
    { "id": "cccccccccccc", "text": "Book the crane", "created": "2026-10-16T09:02:00+01:00", "now": false },
    { "id": "dddddddddddd", "text": "Send the revised fee proposal", "created": "2026-10-15T09:02:00+01:00", "completed": "2026-10-17T08:00:00+01:00", "now": false },
  ],
  // hand-edited comment
  "sheets": [ { "id": "s1s1s1s1s1s1", "text": "Site visit\\n- rebar", "updated": "2026-10-16T10:00:00+01:00" } ],
  "settings": { "theme": "dark", "orbX": 100 }
}`;

/** The same, with every oddity a hand-edited or old file can have. */
const quirkyFile = `{
  "version": 1,
  "tasks": [
    { "id": "aaaaaaaaaaaa", "text": "  spaced   out\\ttext  ", "created": "2026-10-16T09:00:00.1234567+01:00", "now": false },
    { "id": "aaaaaaaaaaaa", "text": "duplicate id", "created": "2026-10-16T09:00:00+01:00" },
    { "text": "no id at all", "created": "2026-10-16T09:00+01:00" },
    { "id": "bad id!", "text": "odd id", "created": "2026-10-16T09:03:00Z" },
    { "id": "eeeeeeeeeeee", "text": "   ", "created": "2026-10-16T09:04:00Z" },
    { "id": "ffffffffffff", "text": "done but now", "created": "2026-10-16T09:05:00Z", "completed": "2026-10-16T10:05:00Z", "now": true },
    { "id": "gggggggggggg", "text": "the real now", "created": "2026-10-16T09:06:00Z", "now": true },
    { "id": "hhhhhhhhhhhh", "text": "second now", "created": "2026-10-16T09:07:00Z", "now": true },
    { "id": "iiiiiiiiiiii", "text": "unreadable completion", "created": "2026-10-16T09:08:00Z", "completed": "yesterday" },
    { "id": "jjjjjjjjjjjj", "text": 42, "created": "2026-10-16T09:09:00Z" },
    { "id": "kkkkkkkkkkkk", "text": "no created" },
    "not an object",
    { "id": "llllllllllll", "text": "empty completed", "created": "2026-10-16T09:10:00Z", "completed": "" },
  ],
  "sheets": [
    { "text": "sheet without id", "updated": "2026-10-16T10:00:00+01:00" },
    { "id": "s2s2s2s2s2s2", "text": "", "updated": "not a date" },
    { "id": "aaaaaaaaaaaa", "text": "sheet whose id a task took", "updated": "2026-10-16T11:00:00Z" },
    /* block comment */
  ],
}`;

/** A device with its own wall clock, for the import cases. */
function device(node: string, start = T0) {
  let wall = start;
  const ws = new Workspace(new Hlc(node, () => wall), () => new Date(wall));
  return { ws, at: (ms: number) => (wall = ms), wall: () => wall };
}

/** Imports a file into a device and records the case: the workspace before, the file, the workspace after. */
function importCases() {
  const cases: unknown[] = [];
  const add = (name: string, d: ReturnType<typeof device>, file: string, lastModified?: number) => {
    const local = d.ws.toData();
    const clock = hlcState(d.ws.hlc);
    let error: string | null = null;
    try {
      importFile(d.ws, file, lastModified === undefined ? {} : { lastModified });
    } catch (e) {
      error = (e as Error).message;
    }
    cases.push({ name, node: d.ws.hlc.node, wall: d.wall(), clock, local, file, lastModified: lastModified ?? null, error, result: d.ws.toData(), views: views(d.ws) });
  };
  return { cases, add };
}

function fileVectors() {
  const { cases, add } = importCases();
  const make = device;

  {
    const d = make('web');
    d.ws.ensureSheet();
    add('desktop file into a fresh workspace', d, desktopFile);
    add('importing the same file again changes nothing', d, desktopFile);
    d.at(T0 + 60_000);
    d.ws.complete('bbbbbbbbbbbb');
    d.ws.editTask('cccccccccccc', 'Book the crane for Thursday');
    add('local edits survive importing it again', d, desktopFile);
  }
  {
    const d = make('web');
    d.ws.ensureSheet();
    d.ws.addTask('mine');
    add('imported tasks go above the current list', d, desktopFile);
  }
  const edited = desktopFile
    .replace('Book the crane', 'Book the crane for Thursday')
    .replace(', "completed": "2026-10-17T08:00:00+01:00"', '')
    .replace('Site visit\\n- rebar', 'Site visit\\n- rebar\\n- formwork');
  {
    const d = make('web');
    d.ws.ensureSheet();
    importFile(d.ws, desktopFile);
    add('changes made on the desktop since the last import', d, edited, T0);
  }
  const both = desktopFile.replace('Book the crane', 'Book the crane (desktop)').replace('- rebar', '- rebar\\n- desktop note');
  {
    const d = make('web');
    d.ws.ensureSheet();
    importFile(d.ws, desktopFile);
    d.at(T0 + 60_000);
    d.ws.editTask('cccccccccccc', 'Book the crane (phone)');
    d.ws.editSheet('s1s1s1s1s1s1', 'Site visit\n- rebar\n- phone note');
    add('both edited, the desktop first', d, both, T0 + 30_000);
    d.at(T0 + 61_000);
    add('both edited, the desktop later', d, both, T0 + 120_000);
  }
  {
    const a = make('a');
    a.ws.ensureSheet();
    const s = a.ws.addSheet();
    a.ws.editSheet(s.id, 'Plan\n- one');
    const x = a.ws.addTask('exported task')!;
    a.ws.addTask('focus here', { asNow: true });
    a.at(T0 + 5000);
    a.ws.deleteTask(x.id);
    const gone = a.ws.addTask('done and gone')!;
    a.ws.complete(gone.id);
    const exported = JSON.stringify(exportFile(a.ws, new Date(a.wall())), null, 2);
    const b = make('b', T0 + 10_000);
    b.ws.ensureSheet();
    b.ws.addTask('b has its own');
    add('an export from another device', b, exported);
    b.at(T0 + 20_000);
    b.ws.editSheet(s.id, 'Plan\n- one\n- two (b)');
    add('an older export comes back: the note is kept twice', b, exported);
    add('and again: still only one copy', b, exported);
  }
  {
    const a = make('a');
    a.ws.ensureSheet();
    a.ws.addTask('real');
    const file = exportFile(a.ws, new Date(T0)) as TackFile & { tackFocus: { tasks: unknown[]; sheets: unknown[]; focus: unknown } };
    const good = file.tackFocus.tasks[0] as Task;
    const future = encode(Date.UTC(2100, 0, 1), 0, 'x');
    file.tackFocus.tasks.push(
      { ...good, id: 'x"]', text: 'evil' },
      { ...good, id: 'blankblank12', text: '   ' },
      { ...good, id: 'deletedblank', text: '', deleted: true },
      { ...good, id: 'futurestamp1', clk: { ...good.clk, text: future } },
      { ...good, id: 'badorderkey1', order: 'V0' },
      { ...good, id: 'noclockfield', clk: { text: good.clk.text } },
      { ...good, id: 'spacedtext12', text: ' many   spaces\there ', completed: 5, created: 7 },
      'nonsense',
    );
    file.tackFocus.sheets.push({ id: 'sheetnoclk12', text: 'x', order: 'V' }, { id: 'sheetbad', text: 7, updated: 3, order: 'W', deleted: 'yes', clk: { text: good.clk.text, order: good.clk.text, deleted: good.clk.text } });
    file.tackFocus.focus = { id: 'not valid!', clk: good.clk.text };
    const b = make('b');
    b.ws.ensureSheet();
    add('records with odd ids, empty text or impossible clocks are skipped', b, JSON.stringify(file));
  }
  {
    const d = make('web');
    d.ws.ensureSheet();
    d.ws.addTask('already here');
    add('a hand-edited desktop file with every oddity', d, quirkyFile, T0 - 3_600_000);
  }
  for (const [name, text] of [
    ['not JSON', 'not json'],
    ['JSON that is not a Tack file', '{"hello": 1}'],
    ['an array', '[1,2]'],
    ['tasks that are not a list', '{"tasks": 5, "sheets": {}}'],
    ['an empty tackFocus section', '{"tackFocus": {"tasks": [], "sheets": [], "focus": {"id": null, "clk": "000000000.0000.0"}}}'],
  ] as const) {
    const d = make('web');
    d.ws.ensureSheet();
    add(name, d, text);
  }
  return cases;
}

/**
 * Notes as Tack for Windows' TextBox writes them, with "\r\n", against a browser's "\n". It's the
 * same note either way: importing it again, typing in it or sending it back keeps no second copy.
 */
function lineBreakFiles() {
  const { cases, add } = importCases();
  const windows = desktopFile.replace('"Site visit\\n- rebar"', '"Site visit\\r\\n- rebar\\r\\n- formwork"');
  {
    const d = device('web');
    d.ws.ensureSheet();
    add('a desktop note with Windows line breaks comes in with \\n', d, windows);
    // A textarea hands the text over with "\n": the same text, so no edit.
    d.ws.editSheet('s1s1s1s1s1s1', 'Site visit\n- rebar\n- formwork');
    add('the same file again, after typing in the note here: no copy', d, windows);
  }
  {
    // A tack.json saved by Tack for Windows before notes were kept with "\n" there: "\r\n" in its section too.
    const desk = device('desk');
    desk.ws.ensureSheet();
    const note = desk.ws.addSheet('Site visit\n- rebar');
    const saved = JSON.stringify(exportFile(desk.ws, new Date(T0)), null, 2).replaceAll('Site visit\\n- rebar', 'Site visit\\r\\n- rebar');
    const phone = device('phone', T0 + 1000);
    phone.ws.ensureSheet();
    add('a desktop file with Windows line breaks in its section', phone, saved);
    phone.at(T0 + 2000);
    phone.ws.editSheet(note.id, 'Site visit\n- rebar');
    add('imported again after typing in the note: no copy', phone, saved);
    // Back on the desktop (whose records have "\n"; its TextBox has "\r\n").
    const back = JSON.stringify(exportFile(phone.ws, new Date(phone.wall())), null, 2);
    add("Tack Focus's export of it back on the desktop: no copy", desk, back);
  }
  return cases;
}

function buildVectors() {
  const spy = vi.spyOn(globalThis.crypto, 'getRandomValues').mockImplementation(seededRandomValues(20261017));
  try {
    const vectors = {
      about: 'Generated by web/tests/unit/vectors.test.ts from web/src/model. Do not edit: run `npm run vectors` in web/.',
      stamps: stampVectors(),
      hlc: hlcVectors(),
      order: orderVectors(),
      derivedIds: idVectors(),
      text: textVectors(),
      dates: dateVectors(),
      merge: mergeVectors(),
      workspaces: [workspaceScript(1, 70, false), workspaceScript(2, 120, true), workspaceScript(3, 160, true)],
      files: fileVectors(),
      // What a Tack 1.1 file becomes here: Tack for Windows must give its own file the same records when it upgrades it.
      legacy: { file: desktopFile, records: readFile(desktopFile, new Workspace(new Hlc('web', () => T0), () => new Date(T0))) },
      roundTrip: roundTrip(),
    };
    // Added later: made last, so the ids everything above was given stay the same.
    vectors.workspaces.push(tiedScript());
    vectors.files.push(...lineBreakFiles());
    return vectors;
  } finally {
    spy.mockRestore();
  }
}

const vectors = buildVectors();

/** JSON with one record per line: readable diffs, a smaller file. */
function format(value: unknown, depth = 0): string {
  const expand = Array.isArray(value) ? depth <= 3 : depth <= 2;
  if (!expand || value === null || typeof value !== 'object') return JSON.stringify(value);
  const pad = '  '.repeat(depth + 1);
  const end = '  '.repeat(depth);
  if (Array.isArray(value)) {
    return value.length === 0 ? '[]' : `[\n${value.map((v) => pad + format(v, depth + 1)).join(',\n')}\n${end}]`;
  }
  const entries = Object.entries(value).filter(([, v]) => v !== undefined);
  if (entries.length === 0) return '{}';
  return `{\n${entries.map(([k, v]) => `${pad}${JSON.stringify(k)}: ${format(v, depth + 1)}`).join(',\n')}\n${end}}`;
}

describe('shared test vectors (tests/vectors/sync.json)', () => {
  it('are computed in UTC, so they come out the same on every machine', () => {
    expect(new Date(T0).getTimezoneOffset()).toBe(0);
  });

  it('are up to date with src/model', async () => {
    await expect(`${format(vectors)}\n`).toMatchFileSnapshot(VECTORS);
  });
});

describe('Tack for Windows', () => {
  // tests/vectors/desktop-export.json is what Tack for Windows saved after opening the round-trip
  // export as its tack.json (written by `dotnet run --project tests/Tack.Tests -- --update`).
  const desktop = existsSync(DESKTOP_EXPORT) ? readFileSync(DESKTOP_EXPORT, 'utf8') : null;

  it.skipIf(desktop === null)('writes back exactly the records it was given', () => {
    const original = JSON.parse(vectors.roundTrip.file) as TackFile;
    const saved = JSON.parse(desktop!) as TackFile;
    expect(JSON.stringify(saved.tackFocus.tasks)).toBe(JSON.stringify(original.tackFocus.tasks));
    expect(JSON.stringify(saved.tackFocus.sheets)).toBe(JSON.stringify(original.tackFocus.sheets));
    expect(saved.tackFocus.focus).toEqual(original.tackFocus.focus);
    // Its own top level shows the same lists.
    const top = (f: TackFile) => f.tasks.map((t) => [t.id, t.text, t.now, t.completed ? parseDate(t.completed)?.getTime() : null]);
    expect(top(saved)).toEqual(top(original));
    // Notes as its TextBox has them, with "\r\n" (the section keeps "\n").
    expect(saved.sheets.map((s) => [s.id, s.text])).toEqual(original.sheets.map((s) => [s.id, s.text.replaceAll('\n', '\r\n')]));
  });

  it.skipIf(desktop === null)('round-trips a Tack Focus workspace without loss', () => {
    const ws = new Workspace(new Hlc('back', () => T0), () => new Date(T0));
    importFile(ws, desktop!);
    expect(views(ws)).toEqual(vectors.roundTrip.views);
    const strip = (d: WorkspaceData) => ({
      tasks: d.tasks.map((t) => (t.deleted ? { ...t, text: '' } : t)),
      sheets: d.sheets.map((s) => (s.deleted ? { ...s, text: '' } : s)),
      focus: d.focus,
    });
    expect(ws.toData()).toEqual(strip(vectors.roundTrip.data));
    // And importing the desktop's file into the workspace that exported it changes nothing.
    const again = new Workspace(new Hlc('back', () => T0), () => new Date(T0));
    importFile(again, vectors.roundTrip.file);
    const before = JSON.stringify(again.toData());
    importFile(again, desktop!);
    expect(JSON.stringify(again.toData())).toBe(before);
  });
});
