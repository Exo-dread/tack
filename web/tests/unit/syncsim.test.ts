import { describe, expect, it, vi } from 'vitest';
import type { Stamp } from '../../src/model/hlc';
import type { Undo } from '../../src/model/workspace';
import { Cloud, DAY, Device, records, settle, visible, type Clock, type Fault } from './synchelpers';
export type { Cloud };

/**
 * Many devices, months of random use, a flaky network, PCs whose OneDrive app lags, devices left
 * in a drawer for months. Afterwards everyone syncs, and:
 *  - every replica is identical, record for record;
 *  - nothing anyone created is lost unless someone deleted it;
 *  - nothing deleted comes back unless someone undid the deletion;
 * judged against every deletion and undo anyone made, by the clocks they were stamped with.
 */

function rng(seed: number) {
  let a = seed >>> 0;
  const next = () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  return {
    next,
    int: (n: number) => Math.floor(next() * n),
    chance: (p: number) => next() < p,
    pick: <T>(xs: readonly T[]): T => xs[Math.floor(next() * xs.length)]!,
  };
}

interface Write {
  value: boolean;
  stamp: Stamp;
}

let STEPS = 600;
export async function simulate(seed: number, steps: number, trace?: (step: number, devices: Device[], cloud: Cloud, clock: Clock, what: string) => void) {
  STEPS = steps;
  const r = rng(seed);
  // Ids (tasks, devices) from the same seed, so a failing seed replays exactly.
  const ids = rng(seed ^ 0x5bd1e995);
  const spy = vi.spyOn(globalThis.crypto, 'getRandomValues').mockImplementation(<T extends ArrayBufferView | null>(array: T): T => {
    const bytes = array as unknown as Uint8Array;
    for (let i = 0; i < bytes.length; i++) bytes[i] = ids.int(256);
    return array;
  });
  try {
    return await run(r, trace);
  } finally {
    spy.mockRestore();
  }
}

async function run(r: ReturnType<typeof rng>, trace?: (step: number, devices: Device[], cloud: Cloud, clock: Clock, what: string) => void) {
  const steps = STEPS;
  const cloud = new Cloud();
  const clock: Clock = { now: Date.UTC(2026, 0, 1, 8) };
  const count = 2 + r.int(3);
  const devices = Array.from(
    { length: count },
    (_, i) => new Device(`d${i}`, cloud, clock, { lag: r.chance(0.4), skew: r.int(20 * 60_000) - 10 * 60_000 }),
  );
  const darkUntil = new Map<Device, number>();
  const undos = new Map<Device, Undo[]>();
  // Every change to a record's "deleted" field made by a person (creating counts as "not deleted").
  const writes = new Map<string, Write[]>();
  const note = (key: string, value: boolean, stamp: Stamp) => {
    const list = writes.get(key) ?? [];
    if (!list.some((w) => w.stamp === stamp)) list.push({ value, stamp });
    writes.set(key, list);
  };
  const observe = (d: Device, before: Map<string, Stamp>) => {
    for (const t of d.ws.tasks.values()) if (before.get(`t:${t.id}`) !== t.clk.deleted && t.clk.deleted.endsWith(`.${d.id}`)) note(`t:${t.id}`, t.deleted, t.clk.deleted);
    for (const s of d.ws.sheets.values()) if (before.get(`s:${s.id}`) !== s.clk.deleted && s.clk.deleted.endsWith(`.${d.id}`)) note(`s:${s.id}`, s.deleted, s.clk.deleted);
  };
  const snapshot = (d: Device) => {
    const m = new Map<string, Stamp>();
    for (const t of d.ws.tasks.values()) m.set(`t:${t.id}`, t.clk.deleted);
    for (const s of d.ws.sheets.values()) m.set(`s:${s.id}`, s.clk.deleted);
    return m;
  };

  let words = 0;
  const word = () => `w${++words}`;
  let cloned = false;

  for (let step = 0; step < steps; step++) {
    // Time passes: mostly seconds, sometimes hours, now and then days.
    const roll = r.next();
    clock.now += roll < 0.7 ? 1000 + r.int(60_000) : roll < 0.95 ? 3600_000 * (1 + r.int(6)) : DAY * (1 + r.int(15));

    // Rare accidents: a damaged file, the whole folder deleted, a data folder copied to a new PC.
    const accident = r.next();
    if (accident < 0.002 && cloud.files.size) {
      const name = r.pick([...cloud.files.keys()]);
      const f = cloud.files.get(name)!;
      cloud.put(name, f.text.slice(0, r.int(f.text.length)));
    } else if (accident < 0.003) {
      cloud.files.clear();
    } else if (accident < 0.0035 && !cloned) {
      const source = r.pick(devices);
      const copy = new Device(`${source.label}-copy`, cloud, clock, { id: source.id, lag: r.chance(0.5) });
      copy.ws.merge(source.ws.toData(), 'storage');
      copy.saved = structuredClone(source.saved);
      devices.push(copy);
      cloned = true;
    }

    const d = r.pick(devices);
    const dark = (darkUntil.get(d) ?? 0) > clock.now;
    const action = r.next();
    const what = `${d.label}:${action < 0.55 ? 'edit' : action < 0.62 && !dark ? 'maybe-dark' : dark ? 'dark' : 'sync'}`;

    if (action < 0.55) {
      // Someone uses Tack on this device (online or not).
      const before = snapshot(d);
      const ws = d.ws;
      const open = [...ws.open, ...(ws.now ? [ws.now] : [])];
      const done = ws.done;
      const sheets = ws.sheetList;
      const op = r.int(13);
      if (op <= 2 || open.length === 0) ws.addTask(word(), { asNow: r.chance(0.1) });
      else if (op === 3) ws.editTask(r.pick(open).id, word());
      else if (op === 4) ws.complete(r.pick(open).id);
      else if (op === 5 && done.length) ws.reopen(r.pick(done).id);
      else if (op === 6) undos.set(d, [...(undos.get(d) ?? []), ws.deleteTask(r.pick([...open, ...done]).id)]);
      else if (op === 7 && undos.get(d)?.length) undos.get(d)!.pop()!();
      else if (op === 8) ws.moveTask(r.pick(open).id, r.int(open.length + 1));
      else if (op === 9) ws.setNow(r.pick(open).id);
      else if (op === 10 && r.chance(0.2)) undos.set(d, [...(undos.get(d) ?? []), ws.clearDone()]);
      else if (op === 11 && sheets.length && r.chance(0.5)) ws.editSheet(r.pick(sheets).id, `${r.pick(sheets).text} ${word()}`.trim());
      else if (op === 11) ws.addSheet(word());
      else if (op === 12 && sheets.length) undos.set(d, [...(undos.get(d) ?? []), ws.deleteSheet(r.pick(sheets).id)]);
      observe(d, before);
    } else if (action < 0.62 && !dark && r.chance(0.3)) {
      // Into a drawer for a while: no sync at all (edits can still happen offline).
      darkUntil.set(d, clock.now + DAY * (20 + r.int(130)));
    } else if (!dark) {
      if (d.view.lag && r.chance(0.6)) d.view.propagate();
      if (r.chance(0.15)) {
        const fault: Fault = r.pick(['offline', 'lost-response', 'server', 'throttled'] as const);
        d.view.faults = { [r.pick(['list', 'read', 'write'] as const)]: [fault] };
      }
      const before = snapshot(d);
      await d.trySync();
      d.view.faults = {};
      void before;
    }
    trace?.(step, devices, cloud, clock, what);
  }

  // Everyone back, online, and syncing until it settles.
  darkUntil.clear();
  for (const d of devices) d.view.faults = {};
  clock.now += 60_000;
  await settle(devices, 6);

  return { devices, writes };
}

function check(devices: Device[], writes: Map<string, Write[]>) {
  const first = devices[0]!;
  for (const d of devices.slice(1)) {
    expect(records(d.ws)).toEqual(records(first.ws));
    expect(visible(d.ws)).toEqual(visible(first.ws));
  }
  const problems: string[] = [];
  for (const [key, list] of writes) {
    const latest = list.reduce((a, b) => (b.stamp > a.stamp ? b : a));
    const id = key.slice(2);
    const rec = key.startsWith('t:') ? first.ws.tasks.get(id) : first.ws.sheets.get(id);
    if (latest.value === false && (!rec || rec.deleted)) problems.push(`lost ${key} (${rec ? 'deleted' : 'gone'})`);
    if (latest.value === true && rec && !rec.deleted) problems.push(`came back: ${key}`);
  }
  expect(problems).toEqual([]);
}

describe('months of random use on several devices', () => {
  // SIM_SEEDS=400 SIM_STEPS=1500 npx vitest run tests/unit/syncsim.test.ts for a longer soak.
  const count = Number(process.env.SIM_SEEDS ?? 40);
  const steps = Number(process.env.SIM_STEPS ?? 600);
  const seeds = Array.from({ length: count }, (_, i) => 1000 + i * 7919);
  it.each(seeds)('seed %i: everyone agrees, nothing lost, nothing revived', async (seed) => {
    const { devices, writes } = await simulate(seed, steps);
    check(devices, writes);
  });
});
