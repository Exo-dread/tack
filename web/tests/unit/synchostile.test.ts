import { describe, expect, it } from 'vitest';
import { FileError, importFile, isFileDate, readSheetRecord, readTaskRecord, readFile } from '../../src/model/file';
import { encode, Hlc, isStamp, MAX_AHEAD_MS, stampTime, ZERO } from '../../src/model/hlc';
import { isOrderKey, MAX_ORDER_KEY } from '../../src/model/order';
import { addShared, keptShare, readShare } from '../../src/model/share';
import { wellFormed } from '../../src/model/text';
import { Workspace } from '../../src/model/workspace';
import { MAX_PASTE_LINES, MAX_SPACES_LISTED } from '../../src/model/limits';
import { listed, MAIN } from '../../src/model/spaces';
import { deadTask, DeviceFileError, fileName, makeDeviceFile, MAX_FILE_BYTES, MAX_NOTE_TEXT, MAX_RECORDS, MAX_SEEN, MAX_TAKEN, MAX_TASK_TEXT, MAX_TRICKLE, parseDeviceFile, serializeDeviceFile, trimSeen } from '../../src/sync/devicefile';
import { emptyMeta, seenBy, takeIn } from '../../src/sync/engine';
import { Cloud, DAY, Device, records, settle, visible, type Clock } from './synchelpers';

/**
 * Files in the sync folder that a broken or hostile writer made: anyone who can write that folder
 * (the user's Microsoft account, an app given their whole OneDrive) is trusted to edit, by
 * design, but a bad file must never stop sync, swell everyone's files for good, or hide another
 * device's changes after it's gone. The same cases run against Tack for Windows (SyncTests.cs).
 */

function world(...specs: (string | { label: string; id?: string })[]) {
  const cloud = new Cloud();
  const clock: Clock = { now: Date.UTC(2026, 9, 21, 9, 0, 0) };
  const devices = specs.map((s) => (typeof s === 'string' ? new Device(s, cloud, clock) : new Device(s.label, cloud, clock, s)));
  const tick = (ms = 1000) => (clock.now += ms);
  return { cloud, clock, devices, tick };
}

const NOW = Date.UTC(2026, 9, 21, 9, 0, 0);

/** A device's file in the cloud, as JSON to tamper with, and back. */
const fileOf = (cloud: Cloud, d: Device) => JSON.parse(cloud.files.get(`${d.id}.json`)!.text) as Record<string, any>;
const putFile = (cloud: Cloud, name: string, doc: unknown) => cloud.put(name, JSON.stringify(doc));

/** A hand-made device file: one live task, whatever else the test adds. */
function craft(device: string, now: number, extra: Record<string, unknown> = {}) {
  const at = encode(now, 0, device);
  return {
    format: 'tack-sync-device',
    version: 1,
    device,
    name: 'crafted',
    app: 'test',
    written: encode(now, 1, device),
    seen: {},
    focus: { id: null, clk: ZERO },
    tasks: [{ id: `t${device}`, text: `from ${device}`, created: '2026-10-21T09:00:00.000Z', completed: null, order: 'V', deleted: false, clk: { text: at, completed: ZERO, order: at, deleted: ZERO } }],
    sheets: [],
    deleted: { tasks: {}, sheets: {} },
    ...extra,
  };
}

describe('M1: text that only half spells a character', () => {
  it('is read as U+FFFD, so it can never travel on to a reader that refuses it', async () => {
    const { cloud, devices } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('fine');
    await phone.sync();
    const doc = fileOf(cloud, phone);
    doc.tasks[0].text = 'half \uD83D an emoji';
    putFile(cloud, `${phone.id}.json`, doc); // JSON.stringify spells it "\ud83d": valid JSON
    expect(cloud.files.get(`${phone.id}.json`)!.text).toContain('\\ud83d');

    await desk.sync();
    expect(visible(desk.ws).open).toEqual(['half � an emoji']);
    const own = cloud.files.get(`${desk.id}.json`)!.text;
    expect(own).not.toMatch(/\\ud[89ab]/i);
    expect(wellFormed(own)).toBe(own);
  });

  it('never leaves this device in its own file, even if it got into the data', () => {
    const ws = new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
    ws.addTask('broken \uDC00 text');
    const text = serializeDeviceFile(makeDeviceFile(ws.toData(), { device: 'aaaa1111', name: 'a', app: 'a', written: ws.hlc.now(), seen: {} }));
    expect(text).toContain('broken � text');
    expect(text).not.toMatch(/\\udc00/i);
  });

  it('is cleaned in imports too', () => {
    const ws = new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
    const data = readFile('{"version":1,"tasks":[{"id":"x","text":"a\\ud800b","created":"2026-10-21T09:00:00Z","now":false}],"sheets":[]}', ws);
    expect(data.tasks[0]!.text).toBe('a�b');
  });

  it('keeps whole characters as they are', () => {
    expect(wellFormed('emoji 😀 ok')).toBe('emoji 😀 ok');
    expect(wellFormed('😀\uD83D')).toBe('😀�');
  });
});

describe('M2: device ids that are also built-in object properties', () => {
  it('sync like any other id ("constructor", "toString"), and the sync state still saves', async () => {
    const { devices } = world('phone', 'desk', { label: 'odd one', id: 'constructor' }, { label: 'odder', id: 'toString' });
    const [phone, desk, odd, odder] = devices as [Device, Device, Device, Device];
    phone.ws.addTask('from the phone');
    odd.ws.addTask('from constructor');
    odder.ws.addTask('from toString');
    // Device.sync saves the meta with structuredClone, as IndexedDB does: a function in it would throw.
    await settle(devices);
    for (const d of devices) expect(visible(d.ws).open.sort()).toEqual(['from constructor', 'from the phone', 'from toString']);
    expect(Object.hasOwn(desk.saved.seen, 'constructor')).toBe(true);
    expect(typeof desk.saved.seen.constructor).toBe('string');
    expect(records(phone.ws)).toEqual(records(odder.ws));
  });

  it('read as unseen until that device is actually seen', () => {
    const at = encode(NOW, 0, 'constructor');
    expect(seenBy(at, 'other', {})).toBe(false);
    expect(seenBy(encode(NOW, 0, '__proto__'), 'other', {})).toBe(false);
    expect(seenBy(at, 'other', { constructor: encode(NOW + 1, 0, 'constructor') })).toBe(true);
  });

  it('a deletion whose id is "__proto__" survives the round trip', () => {
    const ws = new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
    ws.merge({ tasks: [{ id: '__proto__', text: '', created: '', completed: null, order: 'V', deleted: true, space: '', clk: { text: ZERO, completed: ZERO, order: ZERO, deleted: encode(NOW, 0, 'aaaa1111'), space: ZERO } }], sheets: [], focus: { id: null, clk: ZERO } }, 'remote');
    const text = serializeDeviceFile(makeDeviceFile(ws.toData(), { device: 'aaaa1111', name: 'a', app: 'a', written: encode(NOW, 5, 'aaaa1111'), seen: {} }));
    expect(text).toContain('"__proto__"');
    const back = parseDeviceFile(text, 'aaaa1111', NOW);
    expect(back.tasks.find((t) => t.id === '__proto__')?.deleted).toBe(true);
  });
});

describe('M3: one file vouching for another device', () => {
  it("can't hide that device's changes, even after the vouching file is gone", async () => {
    const { cloud, clock, devices, tick } = world('phone', 'desk', 'mallory');
    const [phone, desk, mallory] = devices as [Device, Device, Device];
    phone.ws.addTask('first');
    await settle(devices);
    // Mallory's file claims to have seen the phone's writes ~300 days from now.
    const doc = fileOf(cloud, mallory);
    const later = clock.now + 300 * DAY;
    doc.written = encode(later, 0, mallory.id);
    doc.seen = { [phone.id]: encode(later, 0, phone.id) };
    putFile(cloud, `${mallory.id}.json`, doc);
    await desk.sync();

    tick();
    phone.ws.addTask('second');
    await phone.sync();
    await desk.sync();
    expect(visible(desk.ws).open.sort()).toEqual(['first', 'second']);

    cloud.files.delete(`${mallory.id}.json`);
    tick();
    phone.ws.addTask('third');
    await phone.sync();
    await desk.sync();
    expect(visible(desk.ws).open.sort()).toEqual(['first', 'second', 'third']);
  });

  it('is believed no further than the vouching device\'s own clock', async () => {
    const { cloud, clock, devices } = world('phone', 'desk', 'mallory');
    const [phone, desk, mallory] = devices as [Device, Device, Device];
    await settle(devices);
    const doc = fileOf(cloud, mallory);
    doc.seen = { [phone.id]: encode(clock.now + 300 * DAY, 0, phone.id) };
    putFile(cloud, `${mallory.id}.json`, doc);
    await desk.sync();
    // The claim reaches 300 days ahead; what the desk keeps is no later than today.
    expect(stampTime(desk.saved.seen[phone.id]!)).toBeLessThanOrEqual(clock.now);
  });
});

describe('M4: limits inside a file', () => {
  it('refuses a file with more records than any real one has, and marks one with more devices seen', () => {
    const many = craft('mmmm0001', NOW, { tasks: new Array(MAX_RECORDS + 1).fill({}) });
    expect(() => parseDeviceFile(JSON.stringify(many), 'mmmm0001', NOW)).toThrowError(DeviceFileError);
    // More `seen` claims than any device writes (a file passing on a crafted one's, before that
    // was trimmed): the file is still read, marked so the engine counts only the claims that matter.
    const seen = Object.fromEntries(Array.from({ length: MAX_SEEN + 1 }, (_, i) => [`dev${String(i).padStart(5, '0')}`, encode(NOW, 0, 'x')]));
    const relayed = parseDeviceFile(JSON.stringify(craft('mmmm0001', NOW, { seen })), 'mmmm0001', NOW);
    expect(relayed.seenOver).toBe(true);
    expect(Object.keys(relayed.seen)).toHaveLength(MAX_SEEN + 1);
    expect(relayed.tasks.map((t) => t.text)).toEqual(['from mmmm0001']);
    expect(parseDeviceFile(JSON.stringify(craft('mmmm0001', NOW, { seen: { aaaa0001: encode(NOW, 0, 'x') } })), 'mmmm0001', NOW).seenOver).toBeUndefined();
    // Right at the limits it's fine.
    expect(() => parseDeviceFile(JSON.stringify(craft('mmmm0001', NOW, { tasks: new Array(MAX_RECORDS).fill({}) })), 'mmmm0001', NOW)).not.toThrow();
  });

  it('skips an over-long task text, keeping the rest of the file', () => {
    const doc = craft('mmmm0001', NOW);
    doc.tasks.push({ ...doc.tasks[0]!, id: 'long', text: 'x'.repeat(MAX_TASK_TEXT + 1) });
    const file = parseDeviceFile(JSON.stringify(doc), 'mmmm0001', NOW);
    expect(file.tasks.map((t) => t.id)).toEqual(['tmmmm0001']);
  });

  it('refuses clocks with an over-long device part', () => {
    expect(isStamp(encode(NOW, 0, 'x'.repeat(32)))).toBe(true);
    expect(isStamp(encode(NOW, 0, 'x'.repeat(33)))).toBe(false);
  });

  it('takes only real dates: an odd "created" can no longer win every merge', () => {
    const at = encode(NOW, 0, 'aaaa1111');
    const raw = { id: 't1', text: 'x', created: '!', completed: null, order: 'V', deleted: false, clk: { text: at, completed: ZERO, order: at, deleted: ZERO } };
    expect(readTaskRecord(raw, NOW)!.created).not.toBe('!');
    expect(readTaskRecord({ ...raw, created: 'x'.repeat(5_000_000) }, NOW)!.created.length).toBeLessThan(40);
    expect(readTaskRecord({ ...raw, created: '2026-10-21T09:00:00.1234567+03:00' }, NOW)!.created).toBe('2026-10-21T09:00:00.1234567+03:00');
    expect(isFileDate('2026-10-21T09:00:00Z')).toBe(true);
    expect(isFileDate('2026-10-21T09:00:00.000+03:00 ')).toBe(false);
    expect(readSheetRecord({ id: 's1', text: 'n', updated: '!', order: 'V', deleted: false, clk: { text: at, order: at, deleted: ZERO } }, NOW)!.updated).not.toBe('!');
  });

  it('takes only order keys of a sane length', () => {
    expect(isOrderKey('V'.repeat(MAX_ORDER_KEY))).toBe(true);
    expect(isOrderKey('V'.repeat(MAX_ORDER_KEY + 1))).toBe(false);
  });

  it("won't write a file of its own that every other device would refuse", async () => {
    const { cloud, devices } = world('phone');
    const [phone] = devices as [Device];
    phone.ws.addSheet('a'.repeat(MAX_FILE_BYTES + 1000));
    await expect(phone.sync()).rejects.toMatchObject({ kind: 'toolarge' });
    expect(cloud.files.has(`${phone.id}.json`)).toBe(false);
    expect(phone.saved.own.pending).toBeNull();
  });
});

describe('L1: files that are too big, or that fail to download', () => {
  it('never downloads a file the listing shows to be too big', async () => {
    const { cloud, devices } = world('desk');
    const [desk] = devices as [Device];
    cloud.put('bigbig01.json', 'x'.repeat(MAX_FILE_BYTES + 1));
    const r = await desk.sync();
    expect(r.problems).toEqual([{ device: 'bigbig01', reason: 'unreadable' }]);
    expect(desk.view.reads.get('bigbig01.json')).toBeUndefined();
    await desk.sync(); // and isn't looked at again until it changes
    expect(desk.view.reads.get('bigbig01.json')).toBeUndefined();
  });

  it('a file that fails to download costs only that file, this time', async () => {
    const { devices } = world('ann', 'bob', 'desk');
    const [ann, bob, desk] = devices as [Device, Device, Device];
    ann.ws.addTask('from ann');
    bob.ws.addTask('from bob');
    await ann.sync();
    await bob.sync();
    desk.view.faults = { read: ['server'] };
    const first = await desk.sync();
    // The other file was merged and the desk wrote its own; the one that failed comes next time.
    expect(first.merged).toHaveLength(1);
    expect(first.wrote).toBe(true);
    const second = await desk.sync();
    expect([...first.merged, ...second.merged].sort()).toEqual([ann.id, bob.id].sort());
    expect(visible(desk.ws).open.sort()).toEqual(['from ann', 'from bob']);
  });
});

describe('L7: an empty "completed"', () => {
  it('means not done, as in Tack for Windows', () => {
    const at = encode(NOW, 0, 'aaaa1111');
    const raw = { id: 't1', text: 'x', created: '2026-10-21T09:00:00Z', completed: '', order: 'V', deleted: false, clk: { text: at, completed: at, order: at, deleted: ZERO } };
    expect(readTaskRecord(raw, NOW)!.completed).toBeNull();
    expect(readTaskRecord({ ...raw, completed: 'not a date' }, NOW)!.completed).toBeNull();
    expect(readTaskRecord({ ...raw, completed: '2026-10-21T10:00:00Z' }, NOW)!.completed).toBe('2026-10-21T10:00:00Z');
  });
});

describe('L8: a clock that ran far ahead', () => {
  it('is not adopted from more than a year and a day ahead (the window files use too)', () => {
    const h = new Hlc('aaaa1111', () => NOW);
    h.observe(encode(NOW + MAX_AHEAD_MS + 1, 0, 'bbbb2222'));
    expect(stampTime(h.now())).toBe(NOW);
    h.observe(encode(NOW + 30 * DAY, 0, 'bbbb2222'));
    expect(stampTime(h.now())).toBe(NOW + 30 * DAY);
  });

  it('so stamps after a fixed clock are ones other devices accept', () => {
    const ws = new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
    ws.merge({ tasks: [{ id: 'f', text: 'from the future', created: '2028-10-21T09:00:00Z', completed: null, order: 'V', deleted: false, space: '', clk: { text: encode(NOW + 730 * DAY, 0, 'aaaa1111'), completed: ZERO, order: ZERO, deleted: ZERO, space: ZERO } }], sheets: [], focus: { id: null, clk: ZERO } }, 'storage');
    const written = ws.hlc.now();
    expect(() => parseDeviceFile(serializeDeviceFile(makeDeviceFile(ws.toData(), { device: 'aaaa1111', name: 'a', app: 'a', written, seen: {} })), 'aaaa1111', NOW)).not.toThrow();
  });
});

describe('a bad file never stops sync', () => {
  it('is set aside while everyone else keeps syncing', async () => {
    const { cloud, devices } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('fine');
    await phone.sync();
    cloud.put('mmmm0001.json', '{"format":"tack-sync-device","version":1,"device":"mmmm0001","written":"nope","tasks":[],"sheets":[]}');
    const r = await desk.sync();
    expect(r.problems).toEqual([{ device: 'mmmm0001', reason: 'unreadable' }]);
    expect(visible(desk.ws).open).toEqual(['fine']);
    expect(r.wrote).toBe(true);
  });
});

/*
 * The second audit (24 October 2026): limits on what a device writes and takes in, so that no file
 * within every reader's limits, alone or with others, can make an honest device write a file its
 * peers refuse. The same cases run against Tack for Windows (HostileFileTests.cs).
 */

/** `n` made-up deletions, all at `at` (each about 45 bytes in a file). */
const deletions = (n: number, at: string, prefix = 'd') => {
  const out: Record<string, string> = {};
  for (let i = 0; i < n; i++) out[`${prefix}${i}`] = at;
  return out;
};

/** Does every other device take this device's file? */
const takes = (cloud: Cloud, d: Device, now: number) => {
  try {
    parseDeviceFile(cloud.files.get(`${d.id}.json`)!.text, d.id, now);
    return true;
  } catch {
    return false;
  }
};

describe('M1 (second audit): a file claiming to have seen a thousand devices', () => {
  it("can't stop honest devices syncing, even after it's gone", async () => {
    const { cloud, clock, devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('before');
    await settle(devices);
    // Within every limit: exactly MAX_SEEN made-up devices.
    const seen = Object.fromEntries(Array.from({ length: MAX_SEEN }, (_, i) => [`zz${String(i).padStart(6, '0')}`, encode(clock.now - 5000, 0, 'q')]));
    putFile(cloud, 'evil0001.json', craft('evil0001', clock.now, { seen }));
    tick();
    await phone.sync();
    await desk.sync();
    for (const d of devices) {
      expect(Object.keys(fileOf(cloud, d).seen).length).toBeLessThanOrEqual(MAX_SEEN);
      expect(takes(cloud, d, clock.now)).toBe(true);
    }

    cloud.files.delete('evil0001.json');
    tick();
    phone.ws.addTask('after');
    await settle(devices);
    expect(visible(desk.ws).open.sort()).toEqual(['after', 'before', 'from evil0001']);
    expect(desk.log.at(-1)!.problems).toEqual([]);
  });

  // The regression review: ignoring every claim of such a file made a device that passes them on
  // (any version before trimSeen) look as if it had seen nothing, so each note it edited was kept twice.
  it('still counts its claims about devices in the folder, so a note edited in turn is kept once', async () => {
    const { cloud, clock, devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const s = phone.ws.addSheet('Plan: v1');
    await settle(devices, 2);
    tick();
    phone.ws.editSheet(s.id, 'Plan: v2 (phone)');
    await phone.sync();
    await desk.sync();
    tick();
    desk.ws.editSheet(s.id, 'Plan: v3 (desk, after seeing v2)');
    await desk.sync();
    // The desk passes on a crafted file's thousand claims, as versions before trimSeen do.
    const doc = fileOf(cloud, desk);
    for (let i = 0; i < MAX_SEEN; i++) doc.seen[`zz${String(i).padStart(6, '0')}`] = encode(clock.now - 5000, 0, 'q');
    putFile(cloud, `${desk.id}.json`, doc);
    tick();
    await phone.sync();
    expect(visible(phone.ws).sheets).toEqual(['Plan: v3 (desk, after seeing v2)']);
    // And the made-up claims go no further.
    expect(Object.keys(fileOf(cloud, phone).seen)).toEqual([desk.id]);
  });

  it('keeps the devices in the folder, then the latest claims', () => {
    const at = (ms: number, node: string) => encode(NOW + ms, 0, node);
    const seen: Record<string, string> = {};
    for (let i = 0; i < MAX_SEEN + 5; i++) seen[`zz${String(i).padStart(6, '0')}`] = at(i, 'q');
    seen.real0001 = at(-1000, 'real0001'); // older than every made-up claim, but its file is there
    trimSeen(seen, 'mine0001', new Set([fileName('real0001')]));
    expect(Object.keys(seen)).toHaveLength(MAX_SEEN);
    expect(seen.real0001).toBeDefined();
    // The oldest made-up claims went.
    for (let i = 0; i < 6; i++) expect(seen[`zz${String(i).padStart(6, '0')}`]).toBeUndefined();
    const few = { aaaa0001: at(0, 'aaaa0001') };
    trimSeen(few, 'mine0001', new Set());
    expect(few).toEqual({ aaaa0001: at(0, 'aaaa0001') });
  });
});

describe('M2 (second audit): files that would fill a device', () => {
  it("are set aside, so this device's own file stays one its peers take", async () => {
    const { cloud, clock, devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('before');
    await settle(devices);
    // Within every limit: one task and MAX_RECORDS - 1 deletions.
    const doc = craft('evil0002', clock.now, { deleted: { tasks: deletions(MAX_RECORDS - 1, encode(clock.now - 5000, 0, 'evil0002')), sheets: {} } });
    expect(() => parseDeviceFile(JSON.stringify(doc), 'evil0002', clock.now)).not.toThrow();
    putFile(cloud, 'evil0002.json', doc);
    tick();
    const r = await phone.sync();
    expect(r.problems).toEqual([{ device: 'evil0002', reason: 'full' }]);
    expect(phone.ws.tasks.size).toBe(1);
    await desk.sync();

    cloud.files.delete('evil0002.json');
    tick();
    phone.ws.addTask('after');
    await settle(devices);
    expect(takes(cloud, phone, clock.now)).toBe(true);
    expect(visible(desk.ws).open.sort()).toEqual(['after', 'before']);
  });

  it('takes files in while there is room, together', async () => {
    const { cloud, clock, devices } = world('desk');
    const [desk] = devices as [Device];
    const half = Math.floor(MAX_TAKEN / 2);
    const at = encode(clock.now - 5000, 0, 'q');
    putFile(cloud, 'aaaa0001.json', craft('aaaa0001', clock.now, { deleted: { tasks: deletions(half, at, 'a'), sheets: {} } }));
    putFile(cloud, 'bbbb0001.json', craft('bbbb0001', clock.now, { deleted: { tasks: deletions(half, at, 'b'), sheets: {} } }));
    const r = await desk.sync();
    // The first fits; the second would take the desk past what it takes in.
    expect(r.merged).toEqual(['aaaa0001']);
    expect(r.problems).toEqual([{ device: 'bbbb0001', reason: 'full' }]);
    expect(takes(cloud, desk, clock.now)).toBe(true);
    // Set aside until it changes.
    expect((await desk.sync()).problems).toEqual([{ device: 'bbbb0001', reason: 'full' }]);
    expect(desk.view.reads.get('bbbb0001.json')).toBe(1);
  });

  it('says so on every cycle while its own file would be refused, and writes none', async () => {
    const { cloud, clock, devices } = world('phone');
    const [phone] = devices as [Device];
    // Holding more than any file may carry (merged before this was checked, say).
    const at = encode(clock.now - 5000, 0, 'evil0003');
    phone.ws.merge({ tasks: Array.from({ length: MAX_RECORDS + 1 }, (_, i) => deadTask(`d${i}`, at)), sheets: [], focus: { id: null, clk: ZERO } }, 'remote');
    await expect(phone.sync()).rejects.toMatchObject({ kind: 'toolarge' });
    expect(cloud.files.has(`${phone.id}.json`)).toBe(false);
    // The next cycle finds nothing new, but still says so rather than "synced".
    await expect(phone.sync()).rejects.toMatchObject({ kind: 'toolarge' });
    expect(phone.saved.own.pending).toBeNull();
  });

  // The regression review: a budget that honest devices fill with each other's edits stops them.
  it('still takes a peer’s everyday edits once a flood has filled this device to what it takes in at once', async () => {
    const { cloud, clock, devices, tick } = world('phone', 'laptop');
    const [phone, laptop] = devices as [Device, Device];
    for (let i = 0; i < 20; i++) phone.ws.addTask(`shared ${i}`);
    await settle(devices, 2);
    // Within every limit, and within what a device takes in at once: MAX_TAKEN less what's held, less 3.
    const held = phone.ws.tasks.size + phone.ws.sheets.size + phone.ws.spaces.size;
    putFile(cloud, 'evil0004.json', craft('evil0004', clock.now, { deleted: { tasks: deletions(MAX_TAKEN - held - 4, encode(clock.now - 5000, 0, 'evil0004')), sheets: {} } }));
    tick();
    await settle(devices, 1);
    expect(phone.ws.tasks.size + phone.ws.sheets.size + phone.ws.spaces.size).toBe(MAX_TAKEN - 3);
    cloud.files.delete('evil0004.json');
    for (let round = 0; round < 2; round++) {
      tick();
      for (const d of devices) {
        d.ws.addTask(`${d.label} ${round}a`);
        d.ws.complete(d.ws.addTask(`${d.label} ${round}b`)!.id);
      }
      await settle(devices, 2);
    }
    for (const d of devices) {
      expect(d.log.flatMap((r) => r.problems)).toEqual([]);
      expect(takes(cloud, d, clock.now)).toBe(true);
    }
    expect(visible(laptop.ws).open.filter((t) => !t.startsWith('shared')).sort()).toEqual(['from evil0004', 'laptop 0a', 'laptop 1a', 'phone 0a', 'phone 1a']);
    expect(visible(laptop.ws).done).toEqual(['laptop 0b', 'laptop 1b', 'phone 0b', 'phone 1b']);
    expect(records(laptop.ws)).toEqual(records(phone.ws));
  }, 60_000);

  it('still holds a file adding many at once to what a device takes in at once, and every file to the most one carries', async () => {
    const { cloud, clock, devices } = world('desk');
    const [desk] = devices as [Device];
    const at = encode(clock.now - 5000, 0, 'q');
    putFile(cloud, 'aaaa0001.json', craft('aaaa0001', clock.now, { deleted: { tasks: deletions(MAX_TAKEN - MAX_TRICKLE - 10, at, 'a'), sheets: {} } }));
    expect((await desk.sync()).problems).toEqual([]);
    // More than a few at once past MAX_TAKEN: held.
    putFile(cloud, 'bbbb0001.json', craft('bbbb0001', clock.now, { deleted: { tasks: deletions(MAX_TRICKLE + 20, at, 'b'), sheets: {} } }));
    expect((await desk.sync()).problems).toEqual([{ device: 'bbbb0001', reason: 'full' }]);
    // A few at a time go in, up to the most a file may carry, and no further.
    const room = MAX_RECORDS - desk.ws.tasks.size - desk.ws.sheets.size - desk.ws.spaces.size;
    for (let i = 0; i * MAX_TRICKLE < room + MAX_TRICKLE; i++) {
      putFile(cloud, `cccc${String(i).padStart(4, '0')}.json`, craft(`cccc${String(i).padStart(4, '0')}`, clock.now, { deleted: { tasks: deletions(MAX_TRICKLE - 1, at, `c${i}x`), sheets: {} } }));
    }
    const r = await desk.sync();
    expect(r.problems.length).toBeGreaterThan(0);
    expect(r.problems.every((p) => p.reason === 'full')).toBe(true);
    expect(desk.ws.tasks.size + desk.ws.sheets.size + desk.ws.spaces.size).toBeLessThanOrEqual(MAX_RECORDS);
    expect(takes(cloud, desk, clock.now)).toBe(true);
  }, 60_000);

  it('counts only the spaces that show: deleted ones are records like any other', async () => {
    const { cloud, clock, devices, tick } = world('phone', 'laptop');
    const [phone, laptop] = devices as [Device, Device];
    phone.ws.addTask('before');
    await settle(devices, 2);
    // Within every limit (31 KB): 995 deleted spaces. Every device takes them in.
    putFile(cloud, 'evil0005.json', craft('evil0005', clock.now, { deleted: { tasks: {}, sheets: {}, spaces: deletions(995, encode(clock.now - 5000, 0, 'evil0005'), 's') } }));
    tick();
    await settle(devices, 1);
    cloud.files.delete('evil0005.json');
    tick();
    for (let i = 0; i < 6; i++) phone.ws.addSpace(`space ${i}`);
    phone.ws.addTask('after');
    await settle(devices);
    expect(laptop.log.flatMap((r) => r.problems)).toEqual([]);
    expect(visible(laptop.ws).open.sort()).toEqual(['after', 'before', 'from evil0005']);
    expect([...laptop.ws.spaces.values()].filter((s) => !s.deleted)).toHaveLength(6);
  });

  it('takes spaces in as records: however many show, new ones or deleted ones brought back', async () => {
    const { cloud, clock, devices, tick } = world('desk');
    const [desk] = devices as [Device];
    const at = encode(clock.now - 5000, 0, 'evil0006');
    const n = MAX_SPACES_LISTED + 1;
    const live = (deleted: string) => Array.from({ length: n }, (_, i) => ({ id: `s${i}`, name: `junk ${i}`, order: 'V', deleted: false, clk: { name: at, order: at, deleted } }));
    putFile(cloud, 'evil0006.json', craft('evil0006', clock.now, { deleted: { tasks: {}, sheets: {}, spaces: deletions(n, at, 's') } }));
    expect((await desk.sync()).problems).toEqual([]);
    expect(desk.ws.spaces.size).toBe(n);
    tick();
    putFile(cloud, 'evil0007.json', craft('evil0007', clock.now, { spaces: live(encode(clock.now - 1000, 0, 'evil0007')) }));
    expect((await desk.sync()).problems).toEqual([]);
    expect(desk.ws.spaceList).toHaveLength(n);
    expect(takes(cloud, desk, clock.now)).toBe(true);
  });
});

// The final pass: a limit on how many spaces show, applied as files come in, let one file of junk
// spaces bring honest devices to the limit, after which the next honest change that showed one
// more space had the acting device's whole file set aside by every peer, for good.
describe('M1 (final pass): spaces are records, never a reason to set a file aside', () => {
  /** A folder writer's file of live spaces bringing `d` to exactly `cap` spaces on show. */
  const junkSpaces = (d: Device, now: number, cap = 1000) => {
    const at = encode(now - 5000, 0, 'evil0001');
    const n = cap - d.ws.spaceList.length;
    return craft('evil0001', now, { spaces: Array.from({ length: n }, (_, i) => ({ id: `junk${i}`, name: `junk ${i}`, order: 'W', deleted: false, clk: { name: at, order: at, deleted: ZERO } })) });
  };

  const work = (ws: Workspace) => ws.spaceList.find((s) => s.name === 'Work')!.id;
  let undo = () => {};
  /** What the laptop does (after `before`, synced everywhere before the folder writer's file lands). */
  const actions: [string, (ws: Workspace) => void, ((ws: Workspace) => void)?][] = [
    ['adds a space', (ws) => void ws.addSpace('Groceries')],
    ['renames Main', (ws) => ws.renameSpace(MAIN, 'Inbox')],
    ['moves Main', (ws) => ws.moveSpace(MAIN, 1)],
    ['undoes the deletion of a space', () => undo(), (ws) => void (undo = ws.deleteSpace(work(ws)))],
    ['deletes one space and adds another', (ws) => {
      ws.deleteSpace(work(ws));
      ws.addSpace('Garden');
    }],
  ];
  for (const [what, act, before] of actions) {
    it(`an honest device that ${what} at a thousand spaces still reaches every other device`, async () => {
      const { cloud, clock, devices, tick } = world('phone', 'laptop');
      const [phone, laptop] = devices as [Device, Device];
      phone.ws.addTask('base');
      phone.ws.addSpace('Home');
      phone.ws.addSpace('Work');
      await settle(devices, 2);
      if (before) {
        tick();
        before(laptop.ws);
        await settle(devices, 2);
      }
      putFile(cloud, 'evil0001.json', junkSpaces(phone, clock.now));
      tick();
      await settle(devices, 1);
      expect(phone.ws.spaceList).toHaveLength(1000);
      tick();
      act(laptop.ws);
      laptop.ws.addTask('call mom');
      await settle(devices, 2);
      expect([...phone.log, ...laptop.log].flatMap((r) => r.problems)).toEqual([]);
      expect(visible(phone.ws).open).toContain('call mom');
      expect(records(phone.ws)).toEqual(records(laptop.ws));
    });
  }

  it('a file that lands while one device has a space the other hasn’t seen yet splits neither way', async () => {
    const { cloud, clock, devices, tick } = world('phone', 'laptop');
    const [phone, laptop] = devices as [Device, Device];
    phone.ws.addTask('base');
    phone.ws.addSpace('Home');
    await settle(devices, 2);
    tick();
    phone.ws.addTask('milk', { space: phone.ws.addSpace('Groceries').id });
    await phone.sync();
    // Sized for the laptop, which hasn't seen Groceries yet: one too many for the phone.
    putFile(cloud, 'evil0001.json', junkSpaces(laptop, clock.now));
    tick();
    await laptop.sync();
    await phone.sync();
    for (let i = 0; i < 3; i++) {
      tick();
      phone.ws.addTask(`phone ${i}`);
      laptop.ws.addTask(`laptop ${i}`);
      await settle(devices, 1);
    }
    await settle(devices, 1);
    expect([...phone.log, ...laptop.log].flatMap((r) => r.problems)).toEqual([]);
    expect(visible(phone.ws).open).toContain('laptop 2');
    expect(visible(laptop.ws).open).toEqual(expect.arrayContaining(['phone 2', 'milk']));
    expect(records(phone.ws)).toEqual(records(laptop.ws));
  });

  it('an import with more than a thousand spaces is taken, here and by every peer', async () => {
    const { cloud, devices } = world('phone', 'laptop');
    const [phone, laptop] = devices as [Device, Device];
    phone.ws.addTask('base');
    await settle(devices, 2);
    const at = encode(NOW - 5000, 0, 'aaaa1111');
    const spaces = Array.from({ length: 1001 }, (_, i) => ({ id: `imp${i}`, name: `imported ${i}`, order: 'W', deleted: false, clk: { name: at, order: at, deleted: ZERO } }));
    importFile(laptop.ws, JSON.stringify({ version: 1, tasks: [], sheets: [], tackFocus: { tasks: [], sheets: [], spaces } }));
    laptop.ws.addTask('after the import');
    await settle(devices, 2);
    expect([...phone.log, ...laptop.log].flatMap((r) => r.problems)).toEqual([]);
    expect(phone.ws.spaceList).toHaveLength(1001);
    expect(visible(phone.ws).open).toContain('after the import');
    expect(takes(cloud, laptop, NOW)).toBe(true);
  });

  it('lists draw a thousand spaces at a time, and always the one on show', () => {
    const tabs = Array.from({ length: MAX_SPACES_LISTED + 5 }, (_, i) => ({ id: `s${i}` }));
    expect(listed(tabs.slice(0, 3), MAX_SPACES_LISTED)).toEqual({ list: tabs.slice(0, 3).map((tab, index) => ({ tab, index })), more: 0 });
    const first = listed(tabs, MAX_SPACES_LISTED);
    expect(first.list).toHaveLength(MAX_SPACES_LISTED);
    expect(first.more).toBe(5);
    const kept = listed(tabs, MAX_SPACES_LISTED, `s${MAX_SPACES_LISTED + 3}`);
    expect(kept.list.at(-1)).toEqual({ tab: tabs[MAX_SPACES_LISTED + 3], index: MAX_SPACES_LISTED + 3 });
    expect(kept.more).toBe(4);
    expect(listed(tabs, 2 * MAX_SPACES_LISTED)).toMatchObject({ more: 0 });
  });
});

// The final pass: whether a device takes a file in at once depends on what it already holds, and
// honest devices differ by their unsynced edits: one file sized between two devices' totals is
// taken by one and set aside by the other, which then sets aside the first one's whole file too.
describe('L1 (final pass): a file sized between two devices’ totals', () => {
  it('can be taken in anyway, which heals the split (and still no further than a file may carry)', async () => {
    const { cloud, clock, devices, tick } = world({ label: 'h1', id: 'hone0001' }, { label: 'h2', id: 'htwo0001' });
    const [h1, h2] = devices as [Device, Device];
    h1.ws.addTask('base');
    await settle(devices, 2);
    tick();
    for (let i = 0; i < 5; i++) h1.ws.addTask(`unsynced ${i}`);
    const held = (d: Device) => d.ws.tasks.size + d.ws.sheets.size + d.ws.spaces.size;
    putFile(cloud, 'evil0001.json', craft('evil0001', clock.now, { deleted: { tasks: deletions(MAX_TAKEN - held(h2) - 1, encode(clock.now - 5000, 0, 'evil0001')), sheets: {} } }));
    tick();
    expect((await h2.sync()).problems).toEqual([]);
    expect(held(h2)).toBe(MAX_TAKEN);
    tick();
    h2.ws.addTask('h2 work');
    await h2.sync();
    // h1 holds a few more (its unsynced tasks): the file is too much at once for it, and so is
    // h2's, which now carries it. Without a way out, it would stay so.
    expect((await h1.sync()).problems).toEqual([{ device: 'evil0001', reason: 'full' }, { device: 'htwo0001', reason: 'full' }]);
    expect((await h1.sync()).problems).toEqual([{ device: 'evil0001', reason: 'full' }, { device: 'htwo0001', reason: 'full' }]);
    expect(visible(h1.ws).open).not.toContain('h2 work');
    // Settings → Take it in anyway.
    expect(takeIn(h1.saved, 'htwo0001')).toBe(true);
    expect(takeIn(h1.saved, 'hone0001')).toBe(false);
    expect((await h1.sync()).problems).toEqual([{ device: 'evil0001', reason: 'full' }]);
    expect(visible(h1.ws).open).toContain('h2 work');
    expect(h1.saved.take).toBeUndefined();
    // From then on, both ways, and with the folder writer's file gone too.
    cloud.files.delete('evil0001.json');
    for (let i = 0; i < 2; i++) {
      tick();
      h1.ws.addTask(`h1 ${i}`);
      h2.ws.addTask(`h2 ${i}`);
      await settle(devices, 1);
    }
    await settle(devices, 1);
    expect(h1.log.at(-1)!.problems).toEqual([]);
    expect(h2.log.at(-1)!.problems).toEqual([]);
    expect(visible(h1.ws).open).toContain('h2 1');
    expect(visible(h2.ws).open).toContain('h1 1');
    expect(takes(cloud, h1, clock.now)).toBe(true);
  }, 120_000);

  it('a request is for the version set aside, and never past what a file may carry', async () => {
    const { cloud, clock, devices, tick } = world('desk');
    const [desk] = devices as [Device];
    const at = encode(clock.now - 5000, 0, 'q');
    putFile(cloud, 'aaaa0001.json', craft('aaaa0001', clock.now, { deleted: { tasks: deletions(MAX_TAKEN - 10, at, 'a'), sheets: {} } }));
    putFile(cloud, 'bbbb0001.json', craft('bbbb0001', clock.now, { deleted: { tasks: deletions(MAX_RECORDS - MAX_TAKEN + 100, at, 'b'), sheets: {} } }));
    expect((await desk.sync()).problems).toEqual([{ device: 'bbbb0001', reason: 'full' }]);
    expect(takeIn(desk.saved, 'bbbb0001')).toBe(true);
    // Past what this device's own file may carry: still set aside, and the request is spent.
    expect((await desk.sync()).problems).toEqual([{ device: 'bbbb0001', reason: 'full' }]);
    expect(desk.ws.tasks.size).toBeLessThanOrEqual(MAX_RECORDS);
    expect(takes(cloud, desk, clock.now)).toBe(true);
    expect(desk.saved.take).toBeUndefined();
    expect(desk.view.reads.get('bbbb0001.json')).toBe(2);
    expect((await desk.sync()).problems).toEqual([{ device: 'bbbb0001', reason: 'full' }]);
    expect(desk.view.reads.get('bbbb0001.json')).toBe(2);
    // A new version is judged afresh: the request was for the version you saw.
    tick();
    putFile(cloud, 'bbbb0001.json', craft('bbbb0001', clock.now, { deleted: { tasks: deletions(MAX_TRICKLE + 5, at, 'b'), sheets: {} } }));
    expect(takeIn(desk.saved, 'bbbb0001')).toBe(true);
    expect((await desk.sync()).problems).toEqual([{ device: 'bbbb0001', reason: 'full' }]);
    expect(desk.saved.take).toBeUndefined();
    expect(takeIn(desk.saved, 'bbbb0001')).toBe(true);
    expect((await desk.sync()).problems).toEqual([]);
    expect(desk.saved.take).toBeUndefined();
  }, 60_000);
});

// The final pass, with the user's own files: a device that lost its sync state takes back its own
// file, but went on to judge an out-of-date device's older text of a note as written at the same
// time as its own, and kept a copy of it that then went to every device.
describe('L2 (final pass): a device taking back its own file', () => {
  it('knows again what that file had seen, so an out-of-date device’s older text is not kept as a copy', async () => {
    // Merged in this order: the old tab's file before the phone's.
    const { devices, tick } = world({ label: 'old tab', id: 'AAAAold1' }, { label: 'pc', id: 'mmmmpc01' }, { label: 'phone', id: 'zzzzphon' });
    const [old, pc, phone] = devices as [Device, Device, Device];
    const note = phone.ws.addSheet('Notes v1');
    await settle(devices, 2);
    // The old tab is left behind; the phone edits the note, and the PC takes it in.
    tick();
    phone.ws.editSheet(note.id, 'Notes v2');
    await settle([phone, pc], 2);
    expect(visible(pc.ws).sheets).toEqual(['Notes v2']);
    // The PC loses its sync state (not its data or its id), and takes its own file back.
    pc.saved = emptyMeta('folder');
    tick();
    const r = await pc.sync();
    expect(r.merged).toEqual(['AAAAold1', 'zzzzphon']);
    expect(visible(pc.ws).sheets).toEqual(['Notes v2']);
    expect(pc.saved.seen.zzzzphon).toBeDefined();
    void old;
  });
});

describe('L1 (second audit): imports, pastes and edits', () => {
  const ws = () => new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
  const desktopTasks = (n: number) => Array.from({ length: n }, (_, i) => ({ id: `i${i}`, text: 'x', created: '2026-10-20T09:00:00Z', now: false }));

  it('imports no more than a device can pass on, and says so', () => {
    const w = ws();
    // Holding nearly all a device takes in (deletions, say: Undo after a huge import leaves those).
    const at = encode(NOW - 5000, 0, 'aaaa1111');
    w.merge({ tasks: Array.from({ length: MAX_TAKEN - 2 }, (_, i) => deadTask(`d${i}`, at)), sheets: [], focus: { id: null, clk: ZERO } }, 'storage');
    // Up to the limit is fine, and importing the same again adds nothing, so it's fine too.
    const fits = JSON.stringify({ version: 1, tasks: desktopTasks(2), sheets: [] });
    importFile(w, fits);
    expect(w.tasks.size).toBe(MAX_TAKEN);
    expect(() => importFile(w, fits)).not.toThrow();
    // One more is refused, and nothing changes.
    const more = JSON.stringify({ version: 1, tasks: [{ id: 'one', text: 'one more', created: '2026-10-20T09:00:00Z' }], sheets: [] });
    expect(() => importFile(w, more)).toThrowError(new FileError('This file holds more tasks and notes than Tack can keep in sync.'));
    expect(w.tasks.size).toBe(MAX_TAKEN);
    // A file with more records than any device may hold is refused before reading them.
    expect(() => importFile(ws(), JSON.stringify({ version: 1, tasks: new Array(MAX_RECORDS + 1).fill({}), sheets: [] }))).toThrowError(FileError);
  });

  it('skips text longer than any device reads, in both kinds of file', () => {
    const long = 'x'.repeat(MAX_TASK_TEXT + 1);
    const w = ws();
    importFile(w, JSON.stringify({ version: 1, tasks: [{ id: 'a1', text: long, created: '2026-10-20T09:00:00Z' }, { id: 'a2', text: 'kept', created: '2026-10-20T09:00:00Z' }], sheets: [{ id: 's1', text: 'n'.repeat(MAX_NOTE_TEXT + 1), updated: '2026-10-20T09:00:00Z' }] }));
    expect(visible(w).open).toEqual(['kept']);
    expect(w.sheets.has('s1')).toBe(false);
    const at = encode(NOW, 0, 'bbbb2222');
    const record = (id: string, text: string) => ({ id, text, created: '2026-10-20T09:00:00Z', completed: null, order: 'V', deleted: false, clk: { text: at, completed: at, order: at, deleted: ZERO } });
    const w2 = ws();
    importFile(w2, JSON.stringify({ version: 1, tasks: [], sheets: [], tackFocus: { format: 1, focus: { id: null, clk: ZERO }, tasks: [record('b1', long), record('b2', 'kept')], sheets: [] } }));
    expect(visible(w2).open).toEqual(['kept']);
  });

  it('adds or edits no task longer than any device reads', () => {
    const w = ws();
    expect(w.addTask('x'.repeat(MAX_TASK_TEXT + 1))).toBeNull();
    const t = w.addTask('short')!;
    w.editTask(t.id, 'y'.repeat(MAX_TASK_TEXT + 1));
    expect(w.tasks.get(t.id)!.text).toBe('short');
    expect(w.addTask('z'.repeat(MAX_TASK_TEXT))).not.toBeNull();
  });
});

describe('shares from other apps (the address: anyone can write a link to /?share)', () => {
  const ws = () => new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
  const at = (search: string) => readShare(search)!;
  const lines = (n: number) => Array.from({ length: n }, (_, i) => `line ${i}`).join('\n');

  it('add no more lines at once than a paste, and nothing at all past that', () => {
    const w = ws();
    expect(addShared(w, at(`?share&text=${encodeURIComponent(lines(MAX_PASTE_LINES + 1))}`)).kind).toBe('too-many');
    expect(w.tasks.size).toBe(0);
    const fits = addShared(w, at(`?share&text=${encodeURIComponent(lines(MAX_PASTE_LINES))}`));
    expect(fits.kind === 'added' && fits.added.length).toBe(MAX_PASTE_LINES);
  });

  it('add no task longer than any device reads, even with an address joined on', () => {
    const w = ws();
    const long = 'x'.repeat(MAX_TASK_TEXT - 5);
    // The address can't join that line (it would be too long for every device): it's its own task.
    const result = addShared(w, at(`?share&text=${encodeURIComponent(`${long}\nhttps://example.com/a`)}`));
    expect(result.kind === 'added' && result.added.map((t) => t.text)).toEqual([long, 'https://example.com/a']);
    expect(addShared(w, at(`?share&title=${'y'.repeat(MAX_TASK_TEXT + 1)}`)).kind).toBe('too-long');
    expect([...w.tasks.values()].every((t) => t.text.length <= MAX_TASK_TEXT)).toBe(true);
  });

  it('are only ever text, which every device reads back exactly as written', () => {
    const w = ws();
    const hostile = { title: '<img src=x onerror=alert(1)>', text: '</li><script>alert(2)</script>\njavascript:alert(3)\n\u0000\u001b[31m red', url: 'javascript:alert(4)' };
    const result = addShared(w, at(`?share&${new URLSearchParams(hostile)}`));
    expect(result.kind === 'added' && result.added.map((t) => t.text)).toEqual([
      '<img src=x onerror=alert(1)>',
      '</li><script>alert(2)</script>',
      'javascript:alert(3)',
      '\u0000\u001b[31m red',
      'javascript:alert(4)',
    ]);
    // One rule per field: what a share stores is what the sync reader takes, unchanged.
    const file = makeDeviceFile(w.toData(), { device: 'aaaa1111', name: 'a', app: 'a', written: w.hlc.now(), seen: {} });
    const back = parseDeviceFile(serializeDeviceFile(file), 'aaaa1111', NOW);
    for (const t of w.tasks.values()) expect(back.tasks.find((r) => r.id === t.id)?.text).toBe(t.text);
    for (const t of w.tasks.values()) expect(readTaskRecord(t, NOW)?.text).toBe(t.text);
  });

  it('kept in browser storage for the next start, are read back as three strings or not at all', () => {
    expect(keptShare(JSON.parse('{"title":"a","text":"b","url":"c","__proto__":{"text":"x"}}'))).toEqual({ title: 'a', text: 'b', url: 'c' });
    expect(keptShare(JSON.parse('{"title":"a","text":["b"],"url":"c"}'))).toBeNull();
    expect(keptShare(JSON.parse('{"__proto__":{"title":"a","text":"b","url":"c"}}'))).toBeNull();
  });
});

describe('L2 (second audit): dates in imported desktop files', () => {
  it('are kept only as every device reads them, so a finished task stays finished everywhere', async () => {
    const { devices } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    await settle(devices);
    importFile(phone.ws, JSON.stringify({ version: 1, tasks: [{ id: 'a1', text: 'done one', created: ' 2026-10-20T09:00:00Z', completed: '2026-10-21', now: false }], sheets: [{ id: 's1', text: 'note', updated: 'October 21, 2026 10:00' }] }));
    const t = phone.ws.tasks.get('a1')!;
    expect(isFileDate(t.created)).toBe(true);
    expect(isFileDate(t.completed)).toBe(true);
    expect(isFileDate(phone.ws.sheets.get('s1')!.updated)).toBe(true);
    expect(visible(phone.ws).done).toEqual(['done one']);
    phone.ws.addTask('nudge'); // an import of a file without clocks is no news on its own
    await settle(devices);
    expect(visible(phone.ws).done).toEqual(['done one']);
    expect(visible(desk.ws).done).toEqual(['done one']);
    expect(desk.ws.tasks.get('a1')!.created).toBe(t.created);
  });

  it('as written, when they already are', () => {
    const w = new Workspace(new Hlc('aaaa1111', () => NOW), () => new Date(NOW));
    importFile(w, JSON.stringify({ version: 1, tasks: [{ id: 'a1', text: 'x', created: '2026-10-20T09:00:00.1234567+01:00', completed: '2026-10-21T10:00:00+01:00' }], sheets: [] }));
    expect(w.tasks.get('a1')!.created).toBe('2026-10-20T09:00:00.1234567+01:00');
    expect(w.tasks.get('a1')!.completed).toBe('2026-10-21T10:00:00+01:00');
  });
});
