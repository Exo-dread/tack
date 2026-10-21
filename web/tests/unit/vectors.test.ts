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
import { mergeFocus, mergeSheet, mergeSpace, mergeTask } from '../../src/model/merge';
import { isOrderKey, keyAfter, keyBefore, keyBetween, keysAt } from '../../src/model/order';
import { cleanSpaceName, effectiveSpace, MAIN } from '../../src/model/spaces';
import { cleanTaskText, noteText } from '../../src/model/text';
import { parseDate } from '../../src/model/time';
import type { Focus, Sheet, Space, Task, WorkspaceData } from '../../src/model/types';
import { Workspace, type Undo } from '../../src/model/workspace';
import { unsetSpacesLeftOut } from './vectorjson';

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
  for (const r of [...data.tasks, ...data.sheets, ...data.spaces]) for (const c of Object.values(r.clk)) if (c > max) max = c;
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

  // Records as versions before spaces have them: no `space`, and no clock for it (see spaceMergeVectors).
  const task = (): Task =>
    ({
      id: ids[0]!,
      text: r.pick(texts),
      created: r.pick(dates),
      completed: r.pick(completes),
      order: r.pick(orders),
      deleted: r.chance(0.3),
      clk: { text: r.pick(clocks), completed: r.pick(clocks), order: r.pick(clocks), deleted: r.pick(clocks) },
    }) as Task;
  const sheet = (): Sheet =>
    ({
      id: ids[1]!,
      text: r.pick(texts),
      updated: r.pick(dates),
      order: r.pick(orders),
      deleted: r.chance(0.3),
      clk: { text: r.pick(clocks), order: r.pick(clocks), deleted: r.pick(clocks) },
    }) as Sheet;
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

type Op = { op: string; wall: number; id?: string; text?: string; index?: number; data?: WorkspaceData; base?: Stamp | null; ensured?: string; space?: string };

/**
 * A device ("desk") doing random things, op by op, sometimes merging what another device
 * ("phone") did meanwhile. The C# workspace replays the same ops and must match the lists after
 * every op, and every record (clocks, keys, dates) at checkpoints and at the end.
 *
 * With `spaces`, both devices also add, rename (Main included), delete and undelete spaces and
 * move tasks and sheets between them. Ops: addSpace (id: the new space's, text: the name as typed),
 * renameSpace (id, text), deleteSpace (id; its undo goes on the undo stack), moveTaskToSpace and
 * moveSheetToSpace (id, space).
 */
function workspaceScript(seed: number, steps: number, merges: boolean, spaces = false, checkEvery = 30) {
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
    return { tasks: data.tasks.filter((t) => newer(t.clk)), sheets: data.sheets.filter((s) => newer(s.clk)), focus: data.focus, spaces: data.spaces.filter((s) => newer(s.clk)) };
  };

  const record = (op: Op) => {
    // Tack for Windows always has a sheet to write on, as Tack Focus's UI ensures.
    if (desk.sheetList.length === 0) op.ensured = desk.ensureSheet().id;
    ops.push({ ...op, views: views(desk), ...(ops.length % checkEvery === checkEvery - 1 ? { check: desk.toData() } : {}) });
  };

  const sheetTexts = ['Shopping\n- milk', 'Plan\n1. one\n2. two', U(0x43, 0x61, 0x66, 0xe9, 0x20, 0x2615), '', 'Notes\r\nwith CRLF', '# Title\n\n- [ ] box'];
  const spaceNames = ['Work', ' Dev  ops ', 'Personal', U(0x43, 0x61, 0x66, 0xe9), '"Side"\tproject', ''];

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
    if (spaces) {
      choices.push('addSpace', 'renameSpace', 'moveTaskToSpace', 'moveTaskToSpace', 'moveSheetToSpace');
      if (ws.spaceList.some((s) => s.id !== MAIN)) choices.push('deleteSpace');
    }
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
      case 'addSpace': {
        const text = r.pick(spaceNames);
        const s = ws.addSpace(text);
        return { op, wall, id: s.id, text };
      }
      case 'renameSpace': {
        // Main gets a record of its own the first time.
        const id = r.pick([MAIN, ...ws.spaceList.map((s) => s.id)]);
        const text = `${r.pick(spaceNames)} ${i}`;
        ws.renameSpace(id, text);
        return { op, wall, id, text };
      }
      case 'deleteSpace': {
        const s = r.pick(ws.spaceList.filter((x) => x.id !== MAIN));
        undos.push(ws.deleteSpace(s.id));
        return { op, wall, id: s.id };
      }
      case 'moveTaskToSpace':
      case 'moveSheetToSpace': {
        const all: (Task | Sheet)[] = op === 'moveTaskToSpace' ? [...open, ...done, ...(now ? [now] : [])] : sheets;
        if (!all.length) return null;
        const item = r.pick(all);
        // Somewhere else: Main, or a live space.
        const targets = [MAIN, ...ws.spaceList.map((s) => s.id)].filter((id) => id !== item.space);
        const space = r.pick(targets);
        if (op === 'moveTaskToSpace') ws.moveTaskToSpace(item.id, space);
        else ws.moveSheetToSpace(item.id, space);
        return { op, wall, id: item.id, space };
      }
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
    order: 'R', deleted: false, space: '', clk: { text: clk, completed: clk, order: clk, deleted: clk, space: ZERO },
  });
  const tops: WorkspaceData = {
    tasks: ['eeeeeeeeeeee', 'ffffffffffff', 'gggggggggggg', 'hhhhhhhhhhhh'].map(top),
    sheets: [],
    focus: { id: null, clk: ZERO },
    spaces: [],
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

// ------------------------------------------------------------------ spaces

/** A record as versions before spaces wrote it (and read it): no `space`, and no clock for it. */
function withoutSpaces<T extends Task | Sheet>(rec: T): T {
  const out = { ...rec, clk: { ...rec.clk } } as Record<string, unknown> & { clk: Record<string, unknown> };
  delete out.space;
  delete out.clk.space;
  return out as unknown as T;
}

/** Workspace data as a version before spaces sends it. */
const oldVersion = (data: WorkspaceData): WorkspaceData => ({ tasks: data.tasks.map(withoutSpaces), sheets: data.sheets.map(withoutSpaces), focus: data.focus, spaces: [] });

/** Where a record is: a space (or '' with ZERO), or null for a copy from a version before spaces. */
type Where = { space: string; clk: Stamp } | null;

/** The record put there, its keys in the usual order (`space` just before `clk`, and last in it). */
function placed<T extends Task | Sheet>(rec: T, where: Where): T {
  const bare = withoutSpaces(rec);
  if (!where) return bare;
  const { clk, ...rest } = bare;
  return { ...rest, space: where.space, clk: { ...clk, space: where.clk } } as T;
}

/**
 * Merging a record's space (cases appended to merge.tasks and merge.sheets) and space records
 * (merge.spaces). A record's space comes three ways: missing altogether (a version before spaces
 * wrote it: no clock for it counts as ZERO, so its copy never undoes an assignment), never assigned
 * ('' with ZERO), or an id ('main' included) with its clock. Equal clocks with different ids (only
 * a hand edit makes them) go to the larger JSON value, as for every other field.
 */
function spaceMergeVectors() {
  const r = rng(23);
  const c1 = encode(T0, 0, 'a');
  const c2 = encode(T0 + 1000, 0, 'b');
  const c3 = encode(T0 + 60000, 0, 'a');
  const clocks = [c1, encode(T0, 0, 'b'), encode(T0, 1, 'a'), c2, encode(T0 + 1000, 2, 'b'), c3, encode(T0 + 1000, 0, '')];
  const spaceIds = [MAIN, 'work', 'dev-1', 'zzzzzzzzzzzz', 'Work', 'a_b'];
  const where = (): Where => {
    const how = r.int(5);
    if (how === 0) return null;
    if (how === 1) return { space: '', clk: ZERO };
    return { space: r.pick(spaceIds), clk: r.pick(clocks) };
  };
  const whereOf = (x: Task | Sheet): Where => ('space' in x ? { space: x.space, clk: x.clk.space } : null);
  /** Elsewhere: a hand edit under the same clock, the same place, or anywhere. */
  const elsewhere = (w: Where): Where => {
    const how = r.int(4);
    if (how === 0 && w && w.clk !== ZERO) return { space: r.pick(spaceIds.filter((s) => s !== w.space)), clk: w.clk };
    if (how === 1) return w;
    return where();
  };
  const work = (clk: Stamp): Where => ({ space: 'work', clk });

  // ---- tasks
  const task: Task = {
    id: 'dddddddddddd', text: 'Draft the report', created: '2026-10-17T20:00:00.000+00:00', completed: null, order: 'V', deleted: false,
    space: '', clk: { text: c1, completed: c1, order: c1, deleted: c1, space: ZERO },
  };
  const at = (w: Where, text = task.text, textClk = c1): Task => placed({ ...task, text, clk: { ...task.clk, text: textClk } }, w);
  const taskPairs: [Task | undefined, Task][] = [
    // Moved here; a version before spaces edited the text since: both changes stay, either way round.
    [at(work(c2)), at(null, 'Draft the drainage report', c3)],
    [at(null, 'Draft the drainage report', c3), at(work(c2))],
    // Never assigned against an assignment: the assignment stays, either way round.
    [at(work(c2)), at({ space: '', clk: ZERO })],
    [at({ space: '', clk: ZERO }), at(work(c2))],
    // Moved in two places: the later move wins. Back to Main is a move like any other.
    [at(work(c2)), at({ space: MAIN, clk: c3 })],
    [at({ space: MAIN, clk: c3 }), at(work(c2))],
    // The same clock, different ids (a hand edit): the larger JSON value, on both sides.
    [at(work(c2)), at({ space: 'dev-1', clk: c2 })],
    [at({ space: 'dev-1', clk: c2 }), at(work(c2))],
    // Both from versions before spaces.
    [at(null), at(null, 'x', c3)],
    // New here.
    [undefined, at(work(c2))],
    [undefined, at(null)],
  ];
  const randomTask = (): Task =>
    placed(
      {
        ...task,
        text: r.pick(['x', 'y', 'Groceries']),
        completed: r.pick([null, '2026-10-17T21:00:00.000+00:00']),
        order: r.pick(['V', 'W']),
        deleted: r.chance(0.2),
        clk: { text: r.pick(clocks), completed: r.pick(clocks), order: r.pick(clocks), deleted: r.pick(clocks), space: ZERO },
      },
      where(),
    );
  for (let i = 0; i < 100; i++) {
    const local = r.chance(0.06) ? undefined : randomTask();
    let remote: Task;
    if (!local || r.chance(0.25)) remote = randomTask();
    else {
      const how = r.int(3);
      // Sometimes edited elsewhere since, sometimes by a version before spaces.
      const edited = how === 0 ? { ...local, text: `${local.text} (edited)`, clk: { ...local.clk, text: r.pick(clocks) } } : local;
      remote = placed(edited, how === 2 ? null : elsewhere(whereOf(local)));
    }
    taskPairs.push([local, remote]);
  }
  const tasks = taskPairs.map(([local, remote]) => ({ local: local ?? null, remote, ...mergeTask(local, remote) }));

  // ---- sheets (and the copy kept when two devices wrote the same note at once)
  const note: Sheet = {
    id: 'eeeeeeeeeeee', text: 'Sprint notes', updated: '2026-10-17T20:00:00.000+00:00', order: 'V', deleted: false,
    space: '', clk: { text: c1, order: c1, deleted: c1, space: ZERO },
  };
  const sat = (w: Where, text = note.text, textClk = c1): Sheet => placed({ ...note, text, clk: { ...note.clk, text: textClk } }, w);
  const sheetCases: [Sheet | undefined, Sheet, Stamp | undefined][] = [
    // Written in two places at once: the copy of the losing text stays where its writer had it.
    [sat(work(c2), 'Sprint notes - desk', c2), sat({ space: 'dev-1', clk: c1 }, 'Sprint notes - phone', c3), ZERO],
    [sat({ space: 'dev-1', clk: c3 }, 'Sprint notes - desk', c3), sat(work(c2), 'Sprint notes - phone', c2), ZERO],
    // The losing side had none (or a version before spaces wrote it): the copy is in no space.
    [sat({ space: MAIN, clk: c3 }, 'A', c3), sat(null, 'B', c2), ZERO],
    [sat(null, 'A', c2), sat(work(c2), 'B', c3), ZERO],
    [sat({ space: '', clk: ZERO }, 'A', c3), sat(work(c2), 'B', c2), ZERO],
    // Edited in turn (not at once): no copy, and the move stays.
    [sat(work(c2), 'A', c2), sat(null, 'B', c3), c2],
  ];
  const randomSheet = (): Sheet =>
    placed(
      {
        ...note,
        text: r.pick(['Plan', 'Plan\n- one', 'Shopping', '']),
        order: r.pick(['V', 'W']),
        deleted: r.chance(0.2),
        clk: { text: r.pick(clocks), order: r.pick(clocks), deleted: r.pick(clocks), space: ZERO },
      },
      where(),
    );
  for (let i = 0; i < 100; i++) {
    const local = r.chance(0.06) ? undefined : randomSheet();
    let remote: Sheet;
    if (!local || r.chance(0.25)) remote = randomSheet();
    else {
      const how = r.int(3);
      const edited = how === 0 ? { ...local, text: `${local.text} (edited)`, clk: { ...local.clk, text: r.pick(clocks) } } : local;
      remote = placed(edited, how === 2 ? null : elsewhere(whereOf(local)));
    }
    sheetCases.push([local, remote, r.pick([undefined, ZERO, ZERO, ...clocks])]);
  }
  const sheets = sheetCases.map(([local, remote, base]) => {
    const { value, changed, conflict } = mergeSheet(local, remote, base);
    return { local: local ?? null, remote, base: base ?? null, value, changed, conflict: conflict ?? null };
  });

  // ---- space records
  const names = ['Work', 'Dev', '', 'Personal', 'a"b', U(0xe9)];
  const randomSpace = (): Space => ({
    id: 'ffffffffffff',
    name: r.pick(names),
    order: r.pick(['V', 'W', 'U', 'V1']),
    deleted: r.chance(0.3),
    clk: { name: r.pick([ZERO, ...clocks]), order: r.pick([ZERO, ...clocks]), deleted: r.pick(clocks) },
  });
  /** A deletion as device files carry it (deadSpace). */
  const dead = (deleted: Stamp): Space => ({ id: 'ffffffffffff', name: '', order: 'V', deleted: true, clk: { name: ZERO, order: ZERO, deleted } });
  const named = (name: string, clk: Stamp, deleted = false, deletedClk = c1): Space => ({ id: 'ffffffffffff', name, order: 'V', deleted, clk: { name: clk, order: c1, deleted: deletedClk } });
  const spacePairs: [Space | undefined, Space][] = [
    // Renamed in two places: the later name wins.
    [named('Work', c2), named('Office', c3)],
    [named('Office', c3), named('Work', c2)],
    // The same clock, different names (a hand edit): the larger.
    [named('Work', c2), named('Dev', c2)],
    // A deletion as a device file carries it: deleted, and the name stays as it was here.
    [named('Work', c2), dead(c3)],
    // Undeleted later than deleted: back.
    [named('Work', c2, true, c2), named('Work', c2, false, c3)],
    [undefined, dead(c2)],
    [undefined, named('Work', c2)],
  ];
  for (let i = 0; i < 60; i++) {
    const local = r.chance(0.08) ? undefined : randomSpace();
    let remote: Space;
    if (!local || r.chance(0.3)) remote = randomSpace();
    else {
      const how = r.int(4);
      if (how === 0) remote = dead(r.pick(clocks));
      else if (how === 1) remote = local;
      else {
        // One field changed, with its clock or under the same one.
        const other = randomSpace();
        const field = r.pick(['name', 'order', 'deleted'] as const);
        remote = { ...local, [field]: other[field], clk: { ...local.clk, ...(how === 2 ? { [field]: other.clk[field] } : {}) } };
      }
    }
    spacePairs.push([local, remote]);
  }
  const spaces = spacePairs.map(([local, remote]) => ({ local: local ?? null, remote, ...mergeSpace(local, remote) }));

  return { tasks, sheets, spaces };
}

function allMergeVectors() {
  const m = mergeVectors();
  const s = spaceMergeVectors();
  return { tasks: [...m.tasks, ...s.tasks], sheets: [...m.sheets, ...s.sheets], focus: m.focus, spaces: s.spaces };
}

/** effectiveSpace: what a record's space resolves to, given the spaces a device knows. */
function effectiveVectors() {
  const c = encode(T0, 0, 'a');
  const space = (id: string, deleted: boolean): Space => ({ id, name: id, order: 'V', deleted, clk: { name: c, order: c, deleted: c } });
  const asked = ['', MAIN, 'work', 'old', 'dev-1', 'nosuchspace', 'Work', 'WORK'];
  return [
    { spaces: [space('work', false), space('old', true), space(MAIN, false), space('dev-1', false)], asked },
    { spaces: [], asked },
    // Main stays Main, even with a deleted record (only a hand edit makes one).
    { spaces: [space(MAIN, true), space('work', false)], asked: ['', MAIN, 'work'] },
  ].map(({ spaces, asked }) => ({ spaces, cases: asked.map((s) => ({ space: s, value: effectiveSpace(s, spaces) })) }));
}

/**
 * Spaces, op by op, with a record check after every op: Main renamed, moves (the task in focus
 * included: it stays in focus), a move made on two devices at once, a note written on two devices
 * in different spaces (the copy stays in the loser's), records from a version before spaces, a hand
 * edit, Main refusing deletion, a deleted space undone, and a snapshot restored across all of it.
 */
function spacesScript() {
  let wall = T0 + 7_200_000;
  const desk = new Workspace(new Hlc('desk', () => wall), () => new Date(wall));
  const phone = new Workspace(new Hlc('phone', () => wall), () => new Date(wall));
  const ops: (Op & { views: ReturnType<typeof views>; check: WorkspaceData })[] = [];
  const record = (op: Omit<Op, 'wall'>) => ops.push({ ...op, wall, views: views(desk), check: desk.toData() });
  const later = () => (wall += 1000);

  record({ op: 'start', ensured: desk.ensureSheet().id });
  const [report, build, milk] = ['Draft the report', 'Fix the build', 'Buy milk'].map((text) => {
    later();
    const t = desk.addTask(text)!;
    record({ op: 'add', id: t.id, text: t.text });
    return t;
  }) as [Task, Task, Task];
  later();
  const note = desk.addSheet();
  record({ op: 'addSheet', id: note.id });
  later();
  desk.editSheet(note.id, 'Sprint notes');
  record({ op: 'editSheet', id: note.id, text: 'Sprint notes' });
  later();
  const work = desk.addSpace(' Work ');
  record({ op: 'addSpace', id: work.id, text: ' Work ' });
  later();
  const dev = desk.addSpace('Dev');
  record({ op: 'addSpace', id: dev.id, text: 'Dev' });
  later();
  desk.renameSpace(MAIN, 'Inbox');
  record({ op: 'renameSpace', id: MAIN, text: 'Inbox' });
  later();
  desk.moveTaskToSpace(report.id, work.id);
  record({ op: 'moveTaskToSpace', id: report.id, space: work.id });
  later();
  desk.setNow(build.id);
  record({ op: 'setNow', id: build.id });
  later();
  desk.moveTaskToSpace(build.id, dev.id); // still in focus, still where it was in the list
  record({ op: 'moveTaskToSpace', id: build.id, space: dev.id });
  later();
  desk.moveSheetToSpace(note.id, dev.id);
  record({ op: 'moveSheetToSpace', id: note.id, space: dev.id });
  later();
  record({ op: 'snapshot' });
  const snapshot = desk.toData();

  // The phone gets all of it; then both change things at once.
  phone.ensureSheet();
  phone.merge(desk.toData(), 'remote');
  const agreed = newestClock(desk.toData());
  later();
  phone.moveTaskToSpace(report.id, dev.id);
  phone.moveSheetToSpace(note.id, work.id);
  phone.editSheet(note.id, 'Sprint notes\n- from the phone');
  phone.renameSpace(work.id, 'Work stuff');
  desk.moveTaskToSpace(report.id, MAIN);
  record({ op: 'moveTaskToSpace', id: report.id, space: MAIN });
  desk.editSheet(note.id, 'Sprint notes\n- from the desk');
  record({ op: 'editSheet', id: note.id, text: 'Sprint notes\n- from the desk' });
  later();
  const fromPhone = phone.toData();
  desk.merge(fromPhone, 'remote', agreed);
  record({ op: 'merge', data: fromPhone, base: agreed });

  // A version before spaces edits a task the desk moved: its copy has no space, and moves nothing.
  later();
  phone.editTask(build.id, 'Fix the build (old version)');
  phone.editTask(milk.id, 'Buy oat milk');
  const old = oldVersion(phone.toData());
  desk.merge(old, 'remote');
  record({ op: 'merge', data: old, base: null });

  // A hand-edited copy: the same clock, another space. Both apps settle on the larger id.
  later();
  const current = desk.tasks.get(build.id)!;
  const hand: WorkspaceData = { tasks: [{ ...current, space: 'zzzzzzzzzzzz' }], sheets: [], focus: { id: null, clk: ZERO }, spaces: [] };
  desk.merge(hand, 'remote');
  record({ op: 'merge', data: hand, base: null });

  // Main can't be deleted; a deleted space's tasks show in Main until it's undone.
  const undos: Undo[] = [];
  later();
  undos.push(desk.deleteSpace(MAIN));
  record({ op: 'deleteSpace', id: MAIN });
  later();
  undos.pop()!();
  record({ op: 'undo' });
  later();
  undos.push(desk.deleteSpace(dev.id));
  record({ op: 'deleteSpace', id: dev.id });
  later();
  const fresh = desk.addTask('New, in no space')!;
  record({ op: 'add', id: fresh.id, text: fresh.text });
  later();
  undos.pop()!();
  record({ op: 'undo' });

  // Back to the snapshot: moves undone (to 'main' for "in no space"), names back, later spaces gone.
  later();
  desk.restore(snapshot);
  record({ op: 'restore' });
  return { node: 'desk', ops, data: desk.toData() };
}

/** tack.json with spaces: exports and imports, a version before spaces, and odd space fields. */
function spaceFiles() {
  const { cases, add } = importCases();

  const a = device('a', T0 + 3_600_000);
  const tick = () => a.at(a.wall() + 1000);
  a.ws.ensureSheet();
  const work = a.ws.addSpace('Work');
  tick();
  const gone = a.ws.addSpace('Gone');
  a.ws.renameSpace(MAIN, 'Inbox');
  tick();
  const inWork = a.ws.addTask('In work')!;
  a.ws.moveTaskToSpace(inWork.id, work.id);
  const back = a.ws.addTask('Back in Main')!;
  a.ws.moveTaskToSpace(back.id, work.id);
  tick();
  a.ws.moveTaskToSpace(back.id, MAIN);
  a.ws.addTask('Never moved');
  const sheet = a.ws.addSheet('Work notes');
  a.ws.moveSheetToSpace(sheet.id, work.id);
  tick();
  const lost = a.ws.addTask('In a deleted space')!;
  a.ws.moveTaskToSpace(lost.id, gone.id);
  a.ws.deleteSpace(gone.id);
  const binned = a.ws.addTask('Deleted, in work')!;
  a.ws.moveTaskToSpace(binned.id, work.id);
  a.ws.deleteTask(binned.id);
  const exported = JSON.stringify(exportFile(a.ws, new Date(a.wall())), null, 2);

  const b = device('b', T0 + 4_000_000);
  b.ws.ensureSheet();
  add('an export with spaces, into a fresh workspace', b, exported);
  add('the same export again changes nothing', b, exported);
  // The same records exported by a version before spaces (newer text on one): nothing moves.
  const before = JSON.parse(exported) as TackFile;
  before.tackFocus.tasks = before.tackFocus.tasks.map((t) => withoutSpaces(t as Task));
  before.tackFocus.sheets = before.tackFocus.sheets.map((s) => withoutSpaces(s as Sheet));
  delete before.tackFocus.spaces;
  const edited = before.tackFocus.tasks.find((t) => t.id === inWork.id)!;
  edited.text = 'In work, edited by an older version';
  edited.clk.text = encode(T0 + 4_100_000, 0, 'old');
  b.at(T0 + 4_200_000);
  add('an export from a version before spaces moves nothing back', b, JSON.stringify(before, null, 2));

  // Odd space fields: the record always comes in, the odd field as "in no space". Odd space records are skipped.
  const odd = JSON.parse(exported) as { tackFocus: { tasks: Record<string, unknown>[]; sheets: Record<string, unknown>[]; spaces: unknown[] } };
  const good = odd.tackFocus.tasks.find((t) => t.id === inWork.id)! as unknown as Task;
  const s = good.clk.space;
  const oddTask = (id: string, space: unknown, clk: unknown) => {
    const t: Record<string, unknown> = { ...good, id, text: `odd ${id}`, clk: { ...good.clk, space: clk } };
    if (space === undefined) delete t.space;
    else t.space = space;
    if (clk === undefined) delete (t.clk as Record<string, unknown>).space;
    return t;
  };
  odd.tackFocus.tasks.push(
    oddTask('spacenoclk01', 'work', undefined),
    oddTask('clknospace01', undefined, s),
    oddTask('spacebadid01', 'bad id!', s),
    oddTask('spacezero001', work.id, ZERO),
    oddTask('spacefuture', work.id, encode(Date.UTC(2100, 0, 1), 0, 'x')),
    oddTask('spacenumber', 42, s),
    oddTask('spacebadclk', work.id, 'garbage'),
    oddTask('spacelongid', 'x'.repeat(65), s),
    oddTask('spaceempty1', '', s),
    oddTask('spaceunknown', 'nosuchspace', s), // kept: its space may be on its way
    oddTask('spacemain001', MAIN, s),
  );
  const goodSheet = odd.tackFocus.sheets.find((x) => x.id === sheet.id)!;
  odd.tackFocus.sheets.push({ ...goodSheet, id: 'sheetzero001', clk: { ...(goodSheet.clk as object), space: ZERO } }, { ...goodSheet, id: 'sheetnull001', space: null });
  const goodSpace = odd.tackFocus.spaces.find((x) => (x as Space).id === work.id) as Space;
  odd.tackFocus.spaces.push(
    { ...goodSpace, id: 'spacetidy001', name: '  Side \t project\n' },
    { ...goodSpace, id: 'spacenoname1', name: 42 },
    { ...goodSpace, id: 'no good!' },
    { ...goodSpace, id: 'spacenoclk01', clk: undefined },
    { ...goodSpace, id: 'spacefuture1', clk: { ...goodSpace.clk, deleted: encode(Date.UTC(2100, 0, 1), 0, 'x') } },
    { ...goodSpace, id: 'spacehalfclk', clk: { name: goodSpace.clk.name } },
    { ...goodSpace, id: 'spacebadkey1', order: 'V0', deleted: 'yes' },
    { ...goodSpace, name: 'Work, renamed later', clk: { ...goodSpace.clk, name: encode(T0 + 3_700_000, 0, 'a') } },
    'nonsense',
  );
  const c = device('c', T0 + 5_000_000);
  c.ws.ensureSheet();
  add('odd space fields and odd space records', c, JSON.stringify(odd));

  // Tack for Windows' own lists know nothing of spaces: importing one leaves tasks where they are.
  const d = device('web', T0);
  d.ws.ensureSheet();
  importFile(d.ws, desktopFile);
  d.at(T0 + 60_000);
  const deskWork = d.ws.addSpace('Work');
  d.ws.moveTaskToSpace('bbbbbbbbbbbb', deskWork.id);
  d.ws.moveSheetToSpace('s1s1s1s1s1s1', deskWork.id);
  add('a desktop file leaves tasks and notes in their spaces', d, desktopFile.replace('Reply to Mia', 'Reply to Mia today'), T0 + 120_000);
  return cases;
}

/** Space names as both apps keep them: one line, at most 100 UTF-16 units, never half a character. */
function spaceNameVectors() {
  const e99 = 'e'.repeat(99);
  const names = [
    '', '  Work  ', ` Dev ${U(9)} ops${U(10)}`, 'x'.repeat(100), 'x'.repeat(101), 'x'.repeat(250), ` ${'y'.repeat(100)} `,
    `${e99}${U(0xd83d, 0xde00)}`, `${'e'.repeat(98)}${U(0xd83d, 0xde00)}z`, `${e99}${U(0xd83d, 0xde00)} more`,
    `${e99} z`, `${e99}${U(0x3000)}z`, `${'e'.repeat(98)} ${U(0xa0)}z`, `${e99}${U(0xd83d)}zz`, `${e99}${U(0xde00)}zz`,
    `${e99}${U(0xe9)}z`, U(0xd83d, 0xde00).repeat(60),
  ];
  return names.map((name) => ({ units: units(name), clean: units(cleanSpaceName(name)) }));
}

/**
 * Space names kept to 100 characters, as both apps keep them wherever a name comes from: typed
 * long, an emoji across the limit, white space left at the cut, Main renamed long, the same cut
 * name again (nothing changes). Ends with the tackFocus section both apps write: a deleted space
 * without its name, and without its name's clock.
 */
function spaceNamesScript() {
  let wall = T0 + 9_000_000;
  const desk = new Workspace(new Hlc('desk', () => wall), () => new Date(wall));
  const ops: (Op & { views: ReturnType<typeof views>; check: WorkspaceData })[] = [];
  const record = (op: Omit<Op, 'wall'>) => ops.push({ ...op, wall, views: views(desk), check: desk.toData() });
  const later = () => (wall += 1000);

  record({ op: 'start', ensured: desk.ensureSheet().id });
  const e99 = 'e'.repeat(99);
  const typed = [`   ${'L'.repeat(140)}`, `${e99}${U(0xd83d, 0xde00)} after`, `${'s'.repeat(99)}${U(0x3000)}${'t'.repeat(10)}`, `${e99}${U(0xd83d, 0xde00)} other`];
  later();
  const long = desk.addSpace(typed[0]!);
  record({ op: 'addSpace', id: long.id, text: typed[0] });
  later();
  desk.renameSpace(long.id, typed[1]!);
  record({ op: 'renameSpace', id: long.id, text: typed[1] });
  later();
  desk.renameSpace(MAIN, typed[2]!);
  record({ op: 'renameSpace', id: MAIN, text: typed[2] });
  later();
  desk.renameSpace(long.id, typed[3]!); // cut, the same name: nothing changes
  record({ op: 'renameSpace', id: long.id, text: typed[3] });
  later();
  const gone = desk.addSpace('Gone soon');
  record({ op: 'addSpace', id: gone.id, text: 'Gone soon' });
  later();
  const t = desk.addTask('In a long-named space')!;
  record({ op: 'add', id: t.id, text: t.text });
  later();
  desk.moveTaskToSpace(t.id, long.id);
  record({ op: 'moveTaskToSpace', id: t.id, space: long.id });
  later();
  desk.deleteSpace(gone.id);
  record({ op: 'deleteSpace', id: gone.id });
  const { focus, tasks, sheets, spaces } = exportFile(desk, new Date(wall)).tackFocus;
  return { node: 'desk', ops, data: desk.toData(), section: { focus, tasks, sheets, spaces } };
}

/** tack.json with space names over 100 characters: cut to 100 on the way in, like everywhere else. */
function spaceNameFiles() {
  const { cases, add } = importCases();
  const a = device('a', T0 + 9_500_000);
  a.ws.ensureSheet();
  const work = a.ws.addSpace('Work');
  const t = a.ws.addTask('In work')!;
  a.ws.moveTaskToSpace(t.id, work.id);
  const file = exportFile(a.ws, new Date(a.wall()));
  file.tackFocus.spaces = file.tackFocus.spaces!.map((s) => ({ ...s, name: `${'n'.repeat(99)}${U(0xd83d, 0xde00)}${'N'.repeat(50)}` }));
  const b = device('b', T0 + 9_600_000);
  b.ws.ensureSheet();
  add('a space name over 100 characters comes in cut to 100', b, JSON.stringify(file, null, 2));
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
      merge: allMergeVectors(),
      effectiveSpace: effectiveVectors(),
      workspaces: [workspaceScript(1, 70, false), workspaceScript(2, 120, true), workspaceScript(3, 160, true)],
      files: fileVectors(),
      // What a Tack 1.1 file becomes here: Tack for Windows must give its own file the same records when it upgrades it.
      legacy: { file: desktopFile, records: readFile(desktopFile, new Workspace(new Hlc('web', () => T0), () => new Date(T0))) },
      roundTrip: roundTrip(),
    };
    // Added later: made last, so the ids everything above was given stay the same.
    vectors.workspaces.push(tiedScript());
    vectors.files.push(...lineBreakFiles());
    // Spaces.
    vectors.workspaces.push(workspaceScript(4, 220, true, true, 5), spacesScript());
    vectors.files.push(...spaceFiles());
    // Space names kept to 100 characters.
    const named = { ...vectors, spaceNames: spaceNameVectors() };
    named.workspaces.push(spaceNamesScript());
    named.files.push(...spaceNameFiles());
    return named;
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
    await expect(`${format(unsetSpacesLeftOut(vectors))}\n`).toMatchFileSnapshot(VECTORS);
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
      spaces: d.spaces,
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
