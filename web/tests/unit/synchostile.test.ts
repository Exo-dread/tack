import { describe, expect, it } from 'vitest';
import { isFileDate, readSheetRecord, readTaskRecord, readFile } from '../../src/model/file';
import { encode, Hlc, isStamp, MAX_AHEAD_MS, stampTime, ZERO } from '../../src/model/hlc';
import { isOrderKey, MAX_ORDER_KEY } from '../../src/model/order';
import { wellFormed } from '../../src/model/text';
import { Workspace } from '../../src/model/workspace';
import { DeviceFileError, makeDeviceFile, MAX_FILE_BYTES, MAX_RECORDS, MAX_SEEN, MAX_TASK_TEXT, parseDeviceFile, serializeDeviceFile } from '../../src/sync/devicefile';
import { seenBy } from '../../src/sync/engine';
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
  it('refuses a file with more records, or more devices seen, than any real one has', () => {
    const many = craft('mmmm0001', NOW, { tasks: new Array(MAX_RECORDS + 1).fill({}) });
    expect(() => parseDeviceFile(JSON.stringify(many), 'mmmm0001', NOW)).toThrowError(DeviceFileError);
    const seen = Object.fromEntries(Array.from({ length: MAX_SEEN + 1 }, (_, i) => [`dev${String(i).padStart(5, '0')}`, encode(NOW, 0, 'x')]));
    expect(() => parseDeviceFile(JSON.stringify(craft('mmmm0001', NOW, { seen })), 'mmmm0001', NOW)).toThrowError(DeviceFileError);
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
