/// <reference types="node" />
/**
 * Shared test vectors for sync (tests/vectors/sync-engine.json). Tack for Windows' port of the
 * engine (src/Tack/Sync/SyncEngine.cs, DeviceFile.cs) replays every cycle recorded here and must
 * end with exactly the same records, sync state and device file. Every value is computed by
 * src/sync, so the vectors can't drift: `npm test` fails if the file is out of date, and
 * `npm run vectors` rewrites it.
 */
process.env.TZ = 'UTC';

import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it, vi } from 'vitest';
import { encode, Hlc, ZERO } from '../../src/model/hlc';
import type { WorkspaceData } from '../../src/model/types';
import { Workspace } from '../../src/model/workspace';
import { DeviceFileError, makeDeviceFile, parseDeviceFile, serializeDeviceFile } from '../../src/sync/devicefile';
import { DeviceCollision, emptyMeta, SyncEngine, type CycleResult, type SyncMeta } from '../../src/sync/engine';
import { Cloud, FolderView } from './synchelpers';

const VECTORS = '../../../tests/vectors/sync-engine.json';
const DESKTOP_DEVICE = fileURLToPath(new URL('../../../tests/vectors/desktop-device.json', import.meta.url));
const T0 = Date.UTC(2026, 9, 18, 9, 0, 0);

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

/** Records sorted by id, so both apps compare them the same way. */
function sorted(data: WorkspaceData): WorkspaceData {
  const byId = (a: { id: string }, b: { id: string }) => (a.id < b.id ? -1 : 1);
  return { focus: data.focus, tasks: [...data.tasks].sort(byId), sheets: [...data.sheets].sort(byId) };
}

interface CycleVector {
  device: string;
  now: number;
  hlc: { ms: number; counter: number };
  before: { data: WorkspaceData; meta: SyncMeta; folder: { name: string; tag: string; text: string }[] };
  after: { data: WorkspaceData; meta: SyncMeta; result: Pick<CycleResult, 'merged' | 'wrote' | 'problems'>; collision?: true; written?: unknown };
}

/** Three devices through one folder, recording every cycle for the C# port to replay. */
async function scenario(): Promise<CycleVector[]> {
  const cycles: CycleVector[] = [];
  const cloud = new Cloud();
  let wall = T0;
  const make = (id: string) => {
    const ws = new Workspace(new Hlc(id, () => wall), () => new Date(wall));
    const view = new FolderView(cloud);
    let saved: SyncMeta = emptyMeta('folder');
    const engine = new SyncEngine(ws, view, {
      label: () => ({ name: `Device ${id}`, app: 'Tack Focus test' }),
      flush: async () => {},
      saveMeta: async (m) => void (saved = structuredClone(m)),
    });
    return {
      id,
      ws,
      view,
      /** Its sync state lost (an unreadable sync.json, say): it must take its own file back. */
      forget() {
        saved = emptyMeta('folder');
      },
      async sync() {
        const before = {
          data: sorted(ws.toData()),
          meta: structuredClone(saved),
          folder: [...cloud.files].map(([name, f]) => ({ name, tag: f.tag, text: f.text })),
        };
        const hlc = hlcState(ws.hlc);
        const meta = structuredClone(saved);
        const written = cloud.files.get(`${id}.json`)?.tag;
        let result: CycleResult | null = null;
        let collision = false;
        try {
          result = await engine.cycle(meta, wall);
          saved = meta;
        } catch (error) {
          if (!(error instanceof DeviceCollision)) throw error;
          collision = true;
        }
        const own = cloud.files.get(`${id}.json`);
        cycles.push({
          device: id,
          now: wall,
          hlc,
          before,
          after: {
            data: sorted(ws.toData()),
            meta: collision ? before.meta : structuredClone(saved),
            result: result ? { merged: result.merged, wrote: result.wrote, problems: result.problems } : { merged: [], wrote: false, problems: [] },
            ...(collision ? { collision: true as const } : {}),
            ...(own && own.tag !== written ? { written: JSON.parse(own.text) } : {}),
          },
        });
      },
    };
  };
  const tick = (ms = 1000) => (wall += ms);
  const phone = make('phone001');
  const desk = make('desk0001');
  const tablet = make('tablet01');
  // Both apps always have a sheet to write on (Tack Focus at startup, Tack for Windows when it loads).
  for (const d of [phone, desk, tablet]) d.ws.ensureSheet();
  // Everyone has something before the first sync, including a note.
  const a = phone.ws.addTask('Call the plumber')!;
  tick();
  const b = desk.ws.addTask('Draft the report')!;
  desk.ws.addTask('Book flights');
  const sheet = desk.ws.addSheet('Shopping\n- eggs');
  tick();
  tablet.ws.addTask('Water the plants');
  for (const d of [phone, desk, tablet, phone, desk]) {
    tick();
    await d.sync();
  }
  // Edits on several devices, some to the same task and note at once.
  tick();
  phone.ws.editTask(b.id, 'Draft the drainage report');
  desk.ws.complete(b.id);
  phone.ws.editSheet(sheet.id, 'Shopping\n- eggs\n- milk');
  desk.ws.editSheet(sheet.id, 'Shopping\n- eggs\n- bread');
  tablet.ws.setNow(a.id);
  tick();
  const undo = desk.ws.deleteTask(a.id);
  for (const d of [desk, phone, tablet, desk, phone]) {
    tick();
    await d.sync();
  }
  // Undo comes back; a sequential note edit; a reorder.
  tick();
  undo();
  tablet.ws.editSheet(sheet.id, `${tablet.ws.sheets.get(sheet.id)!.text}\n- tea`);
  tablet.ws.moveTask(tablet.ws.open.at(-1)!.id, 0);
  for (const d of [tablet, desk, phone, tablet]) {
    tick();
    await d.sync();
  }
  // A damaged file, a file from a newer Tack, and an answer that never came back.
  tick();
  const deskName = `${desk.id}.json`;
  const good = cloud.files.get(deskName)!.text;
  cloud.put(deskName, good.slice(0, 80));
  tick();
  await phone.sync();
  const tabletName = `${tablet.id}.json`;
  const tabletDoc = JSON.parse(cloud.files.get(tabletName)!.text);
  cloud.put(tabletName, JSON.stringify({ ...tabletDoc, version: 2 }));
  tick();
  await phone.sync();
  tick();
  await desk.sync(); // finds its own file damaged and writes it again
  tick();
  await tablet.sync(); // its own file says version 2: unreadable to it, so written again
  tick();
  phone.ws.addTask('Lost answer');
  phone.view.faults = { write: ['lost-response'] };
  tick();
  await phone.sync().catch(() => {}); // fails, and isn't recorded (only its saved "pending" write is kept)
  tick();
  await phone.sync(); // recognises its own upload
  // Someone else writes as the tablet (a copied data folder).
  tick();
  const forged = JSON.parse(cloud.files.get(tabletName)!.text);
  cloud.put(tabletName, JSON.stringify({ ...forged, written: encode(wall + 5000, 0, tablet.id) }));
  tick();
  await tablet.sync();
  // The desk loses its sync state, while the phone adds a task.
  desk.forget();
  tick();
  phone.ws.addTask('While the desk forgot');
  tick();
  await phone.sync();
  tick();
  await desk.sync();
  // Everyone settles.
  for (const d of [phone, desk, phone, desk]) {
    tick();
    await d.sync();
  }
  return cycles;
}

/** Device files written by hand, good and bad, and what reading them must give. */
function deviceFiles() {
  const now = T0;
  const w = encode(T0 - 1000, 0, 'abcd1234');
  const clk = { text: w, completed: w, order: w, deleted: w };
  const good = {
    format: 'tack-sync-device',
    version: 1,
    device: 'abcd1234',
    name: 'Phone',
    app: 'Tack Focus 1.1.0',
    written: w,
    seen: { desk0001: encode(T0 - 5000, 3, 'desk0001'), 'bad id!': w, tablet01: 'not a stamp' },
    focus: { id: 'aaaaaaaaaaaa', clk: w },
    tasks: [
      { id: 'aaaaaaaaaaaa', text: '  spaced   out  ', created: '2026-10-18T08:00:00.000Z', completed: null, order: 'V', deleted: false, clk },
      { id: 'aaaaaaaaaaaa', text: 'duplicate, newer', created: '2026-10-18T07:00:00.000Z', completed: null, order: 'W', deleted: false, clk: { ...clk, text: encode(T0 - 500, 0, 'abcd1234') } },
      { id: '../etc', text: 'bad id', clk },
      { id: 'bbbbbbbbbbbb', text: 'no clocks' },
      { id: 'cccccccccccc', text: '', created: '', completed: null, order: 'V', deleted: false, clk },
      { id: 'dddddddddddd', text: 'future', created: '', completed: null, order: 'V', deleted: false, clk: { ...clk, text: encode(T0 + 400 * 86400000, 0, 'x') } },
    ],
    sheets: [{ id: 'ssssssssssss', text: 'Note\r\nline', updated: '2026-10-18T08:00:00.000Z', order: 'V', deleted: false, clk: { text: w, order: w, deleted: w } }],
    deleted: { tasks: { eeeeeeeeeeee: w, 'bad id': w, ffffffffffff: 'nope' }, sheets: { tttttttttttt: w } },
  };
  const read = (name: string, device: string, text: string) => {
    try {
      const f = parseDeviceFile(text, device, now);
      return { name, device, now, text, expect: { problem: null, file: { ...f, ...sorted(f) } } };
    } catch (e) {
      return { name, device, now, text, expect: { problem: (e as DeviceFileError).reason, file: null } };
    }
  };
  return [
    read('good, with every kind of bad record inside', 'abcd1234', JSON.stringify(good)),
    read('byte order mark', 'abcd1234', `﻿${JSON.stringify({ ...good, tasks: [], sheets: [] })}`),
    read('truncated', 'abcd1234', JSON.stringify(good).slice(0, 120)),
    read('newer version', 'abcd1234', JSON.stringify({ ...good, version: 2 })),
    read('fractional version', 'abcd1234', JSON.stringify({ ...good, version: 1.5 })),
    read('another device under this name', 'zzzz9999', JSON.stringify(good)),
    read('not a device file', 'abcd1234', JSON.stringify({ version: 1, tasks: [] })),
    read('written in the future', 'abcd1234', JSON.stringify({ ...good, written: encode(T0 + 400 * 86400000, 0, 'abcd1234') })),
    read('no deleted section', 'abcd1234', JSON.stringify({ ...good, deleted: undefined })),
    read('long name', 'abcd1234', JSON.stringify({ ...good, name: 'x'.repeat(150) })),
  ];
}

describe('shared sync vectors (tests/vectors/sync-engine.json)', () => {
  it('are up to date with src/sync', async () => {
    const real = crypto.getRandomValues.bind(crypto);
    vi.spyOn(crypto, 'getRandomValues').mockImplementation(seededRandomValues(20261018));
    let cycles: CycleVector[];
    try {
      cycles = await scenario();
    } finally {
      vi.mocked(crypto.getRandomValues).mockRestore();
      void real;
    }
    expect(cycles.length).toBeGreaterThan(20);
    expect(cycles.some((c) => c.after.collision)).toBe(true);
    expect(cycles.some((c) => c.after.result.problems.length)).toBe(true);
    expect(cycles.some((c) => c.after.data.sheets.length > 1)).toBe(true); // a note kept twice
    const vectors = { cycles, deviceFiles: deviceFiles() };
    await expect(`${JSON.stringify(vectors, null, 1)}\n`).toMatchFileSnapshot(VECTORS);
  });

  it('reads the device file Tack for Windows writes, record for record', () => {
    // Written by `dotnet run --project tests/Tack.Tests -c Release -- --update` from the last
    // cycle's records; it must say exactly what this app would say about the same records.
    if (!existsSync(DESKTOP_DEVICE)) return;
    const desktop = JSON.parse(readFileSync(DESKTOP_DEVICE, 'utf8')) as { data: WorkspaceData; seen: Record<string, string>; written: string; text: string };
    const parsed = parseDeviceFile(desktop.text, 'desk0001', T0 + 86400000);
    const ours = parseDeviceFile(
      serializeDeviceFile(makeDeviceFile(desktop.data, { device: 'desk0001', name: parsed.name, app: parsed.app, written: desktop.written, seen: desktop.seen })),
      'desk0001',
      T0 + 86400000,
    );
    expect({ ...parsed, ...sorted(parsed) }).toEqual({ ...ours, ...sorted(ours) });
    expect(parsed.focus).not.toEqual({ id: null, clk: ZERO });
  });
});
