import { describe, expect, it } from 'vitest';
import { encode } from '../../src/model/hlc';
import { deviceOfFile, makeDeviceFile, parseDeviceFile, serializeDeviceFile, DeviceFileError } from '../../src/sync/devicefile';
import { emptyMeta, seenBy } from '../../src/sync/engine';
import { Cloud, DAY, Device, records, settle, visible, type Clock } from './synchelpers';

function world(...specs: (string | { label: string; lag?: boolean; skew?: number; id?: string })[]) {
  const cloud = new Cloud();
  const clock: Clock = { now: Date.UTC(2026, 9, 18, 9, 0, 0) };
  const devices = specs.map((s) => (typeof s === 'string' ? new Device(s, cloud, clock) : new Device(s.label, cloud, clock, s)));
  const tick = (ms = 1000) => (clock.now += ms);
  return { cloud, clock, devices, tick };
}

const expectSame = (devices: Device[]) => {
  for (const d of devices.slice(1)) {
    expect(visible(d.ws)).toEqual(visible(devices[0]!.ws));
    expect(records(d.ws)).toEqual(records(devices[0]!.ws));
  }
};

describe('the device file', () => {
  it('round-trips and leaves the text of deleted records behind', () => {
    const { devices } = world('phone');
    const ws = devices[0]!.ws;
    const a = ws.addTask('keep me')!;
    const b = ws.addTask('secret plan')!;
    ws.deleteTask(b.id);
    const file = makeDeviceFile(ws.toData(), { device: ws.hlc.node, name: 'Phone', app: 'Tack Focus 1.1.0', written: ws.hlc.now(), seen: {} });
    const text = serializeDeviceFile(file);
    expect(text).not.toContain('secret plan');
    const back = parseDeviceFile(text, ws.hlc.node);
    expect(back.tasks.find((t) => t.id === a.id)?.text).toBe('keep me');
    expect(back.tasks.find((t) => t.id === b.id)).toMatchObject({ deleted: true, text: '' });
  });

  it('refuses files that are damaged, newer, or under the wrong name', () => {
    const reason = (text: string, device = 'abcd1234') => {
      try {
        parseDeviceFile(text, device);
        return 'ok';
      } catch (e) {
        return (e as DeviceFileError).reason;
      }
    };
    const good = { format: 'tack-sync-device', version: 1, device: 'abcd1234', written: encode(Date.now(), 0, 'abcd1234'), tasks: [], sheets: [] };
    expect(reason(JSON.stringify(good))).toBe('ok');
    expect(reason('{"format":"tack-sync-device",')).toBe('unreadable');
    expect(reason(JSON.stringify({ ...good, version: 2 }))).toBe('newer');
    expect(reason(JSON.stringify(good), 'zzzz9999')).toBe('mismatch');
    expect(reason(JSON.stringify({ ...good, written: encode(Date.now() + 400 * DAY, 0, 'x') }))).toBe('unreadable');
    // Bad records cost only themselves.
    const mixed = { ...good, tasks: [{ id: '../../etc', text: 'x' }, { id: 'aaaaaaaaaaaa', text: 'fine', created: '', completed: null, order: 'V', deleted: false, clk: { text: good.written, completed: good.written, order: good.written, deleted: good.written } }] };
    expect(parseDeviceFile(JSON.stringify(mixed), 'abcd1234').tasks.map((t) => t.id)).toEqual(['aaaaaaaaaaaa']);
  });

  it('only counts files named like a device', () => {
    expect(deviceOfFile('abcd1234.json')).toBe('abcd1234');
    expect(deviceOfFile('abcd1234-DESKTOP-PC.json')).toBeNull(); // the OneDrive app's conflict copy
    expect(deviceOfFile('~$abcd1234.json.tmp')).toBeNull();
    expect(deviceOfFile('tack-sync.json')).toBeNull();
  });

  it('knows which device has seen a stamp', () => {
    const s = encode(1000, 0, 'phone123');
    expect(seenBy(s, 'phone123', {})).toBe(true);
    expect(seenBy(s, 'desk1234', {})).toBe(false);
    expect(seenBy(s, 'desk1234', { phone123: encode(999, 0, 'phone123') })).toBe(false);
    expect(seenBy(s, 'desk1234', { phone123: encode(1000, 0, 'phone123') })).toBe(true);
    expect(seenBy(encode(1000, 0, ''), 'desk1234', {})).toBe(true);
  });
});

describe('syncing', () => {
  it('brings each device what the others wrote', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    desk.ws.addTask('existing');
    await settle(devices);
    tick();
    phone.ws.addTask('call the plumber');
    await phone.sync();
    await desk.sync();
    expect(visible(desk.ws).open).toEqual(['call the plumber', 'existing']);
    expectSame(devices);
  });

  it('merges a day of offline work on both sides', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const shared = desk.ws.addTask('draft report')!;
    const other = desk.ws.addTask('book flights')!;
    await settle(devices);

    // Both offline for a day.
    phone.view.faults = { list: ['offline', 'offline'] };
    desk.view.faults = { list: ['offline'] };
    tick(3600_000);
    phone.ws.editTask(shared.id, 'draft drainage report');
    phone.ws.addTask('buy milk');
    expect(await phone.trySync()).toBeNull();
    tick(3600_000);
    desk.ws.complete(shared.id);
    desk.ws.deleteTask(other.id);
    desk.ws.addTask('pay invoice');
    expect(await desk.trySync()).toBeNull();
    expect(await phone.trySync()).toBeNull();
    tick(20 * 3600_000);

    await settle(devices);
    // Added at the top on both sides at once: one order, the same everywhere (ties go by id).
    expect(visible(phone.ws).open.sort()).toEqual(['buy milk', 'pay invoice']);
    expect(visible(phone.ws).done).toEqual(['draft drainage report']);
    expectSame(devices);
  });

  it('settles a simultaneous edit of the same task the same way everywhere', async () => {
    const { devices, tick } = world('phone', 'desk', 'tablet');
    const [phone, desk, tablet] = devices as [Device, Device, Device];
    const t = desk.ws.addTask('original')!;
    await settle(devices);
    tick();
    phone.ws.editTask(t.id, 'from the phone');
    desk.ws.editTask(t.id, 'from the desk'); // same millisecond: the device id breaks the tie
    tick();
    tablet.ws.editTask(t.id, 'from the tablet, a second later');
    await phone.sync();
    await desk.sync();
    await tablet.sync();
    await settle(devices);
    expect(visible(phone.ws).open).toEqual(['from the tablet, a second later']);
    expectSame(devices);
  });

  it('keeps both versions of a note written in two places at once', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const s = desk.ws.addSheet('Shopping\n- eggs');
    await settle(devices);
    tick();
    phone.ws.editSheet(s.id, 'Shopping\n- eggs\n- milk');
    tick();
    desk.ws.editSheet(s.id, 'Shopping\n- eggs\n- bread');
    await settle(devices);
    expect(visible(phone.ws).sheets.sort()).toEqual(['Shopping\n- eggs\n- bread', 'Shopping\n- eggs\n- milk']);
    expectSame(devices);
  });

  it('does not copy a note that was edited in turn', async () => {
    const { devices, tick } = world('phone', 'desk', 'tablet');
    const [phone, desk, tablet] = devices as [Device, Device, Device];
    const s = desk.ws.addSheet('v1');
    await settle(devices);
    for (const [d, text] of [[phone, 'v2 phone'], [desk, 'v3 desk'], [tablet, 'v4 tablet'], [phone, 'v5 phone']] as const) {
      tick();
      d.ws.editSheet(s.id, text);
      await settle(devices, 2); // everyone has it before the next edit
    }
    await settle(devices);
    expect(visible(desk.ws).sheets).toEqual(['v5 phone']);
    expectSame(devices);
  });

  it('does not copy a note passed along through a third device first', async () => {
    const { devices, tick } = world('phone', 'desk', 'tablet');
    const [phone, desk, tablet] = devices as [Device, Device, Device];
    const s = desk.ws.addSheet('v1');
    await settle(devices);
    tick();
    phone.ws.editSheet(s.id, 'v2 phone');
    await phone.sync();
    // The tablet sees the phone's edit and writes its own file (with the phone's text in it).
    await tablet.sync();
    tablet.ws.addTask('tablet task');
    await tablet.sync();
    // The desk edits on top of what it got.
    await desk.sync();
    tick();
    desk.ws.editSheet(s.id, 'v3 desk, on top of v2');
    await settle(devices);
    expect(visible(phone.ws).sheets).toEqual(['v3 desk, on top of v2']);
    expectSame(devices);
  });

  it('lets a later delete beat an earlier edit, and Undo bring it back', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    const t = desk.ws.addTask('x')!;
    await settle(devices);
    tick();
    phone.ws.editTask(t.id, 'x edited');
    tick();
    const undo = desk.ws.deleteTask(t.id);
    await settle(devices);
    expect(visible(phone.ws).open).toEqual([]);
    tick();
    undo();
    await settle(devices);
    expect(visible(phone.ws).open).toEqual(['x edited']);
    expectSame(devices);
  });

  it('works through a PC whose OneDrive app passes files on late', async () => {
    const { devices, tick } = world('phone', { label: 'desk', lag: true });
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('from the phone');
    await phone.sync();
    await desk.sync(); // the OneDrive app hasn't downloaded it yet
    expect(visible(desk.ws).open).toEqual([]);
    tick();
    desk.ws.addTask('from the desk');
    await desk.sync();
    await phone.sync(); // ...nor uploaded this
    expect(visible(phone.ws).open).toEqual(['from the phone']);
    desk.view.propagate();
    await desk.sync();
    await phone.sync();
    expect(visible(phone.ws).open.sort()).toEqual(['from the desk', 'from the phone']);
    await settle(devices);
    expectSame(devices);
  });

  it('converges with clocks that are hours apart', async () => {
    const { devices, tick } = world({ label: 'fast', skew: 3 * 3600_000 }, { label: 'slow', skew: -2 * 3600_000 });
    const [fast, slow] = devices as [Device, Device];
    const t = fast.ws.addTask('shared')!;
    await settle(devices);
    tick();
    fast.ws.editTask(t.id, 'edited on the fast clock');
    await settle(devices);
    tick();
    // Seen the fast device's edit, so the slow one's next edit still wins.
    slow.ws.editTask(t.id, 'edited later on the slow clock');
    await settle(devices);
    expect(visible(fast.ws).open).toEqual(['edited later on the slow clock']);
    expectSame(devices);
  });
});

describe('interruptions and recovery', () => {
  it('recognises its own upload when the answer was lost', async () => {
    const { devices, cloud } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('one');
    phone.view.faults = { write: ['lost-response'] };
    expect(await phone.trySync()).toBeNull();
    expect(cloud.files.size).toBe(1); // it did arrive
    const r = await phone.sync();
    expect(r.wrote).toBe(false); // nothing new: it knows that file is its own
    await desk.sync();
    expect(visible(desk.ws).open).toEqual(['one']);
  });

  it('retries a write that failed, and a read that failed', async () => {
    const { devices } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('one');
    phone.view.faults = { write: ['server'] };
    expect(await phone.trySync()).toBeNull();
    await phone.sync();
    // A download that fails costs only that file, this time: the cycle carries on (the desk
    // still writes its own file) and the phone's file is fetched again next time.
    desk.view.faults = { read: ['offline'] };
    const first = await desk.trySync();
    expect(first?.wrote).toBe(true);
    expect(first?.merged).toEqual([]);
    expect(visible(desk.ws).open).toEqual([]);
    await desk.sync();
    expect(visible(desk.ws).open).toEqual(['one']);
  });

  it('skips a damaged file without losing anything, and merges it once repaired', async () => {
    const { devices, cloud } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('one');
    await phone.sync();
    const name = `${phone.id}.json`;
    const good = cloud.files.get(name)!.text;
    cloud.put(name, good.slice(0, good.length / 2));
    const r = await desk.sync();
    expect(r.problems).toEqual([{ device: phone.id, reason: 'unreadable' }]);
    expect(visible(desk.ws).open).toEqual([]);
    // The phone notices its file isn't what it wrote and writes it again.
    expect((await phone.sync()).wrote).toBe(true);
    await desk.sync();
    expect(visible(desk.ws).open).toEqual(['one']);
  });

  it('writes its file again if someone deletes it, or the whole folder', async () => {
    const { devices, cloud } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('one');
    desk.ws.addTask('two');
    await settle(devices);
    cloud.files.delete(`${phone.id}.json`);
    expect((await phone.sync()).wrote).toBe(true);
    cloud.files.clear();
    await settle(devices);
    expect(cloud.files.size).toBe(2);
    const fresh = new Device('new phone', cloud, phone.clock);
    await fresh.sync();
    expect(visible(fresh.ws).open.sort()).toEqual(['one', 'two']);
  });

  it('gives itself a new id when a copy of its data starts syncing elsewhere', async () => {
    const { devices, cloud, clock, tick } = world('pc');
    const pc = devices[0]!;
    pc.ws.addTask('from the old pc');
    await pc.sync();
    // The same data folder copied to a new PC: same device id, same records.
    const copy = new Device('new pc', cloud, clock, { id: pc.id });
    copy.ws.merge(pc.ws.toData(), 'storage');
    copy.saved = structuredClone(pc.saved);
    tick();
    copy.ws.addTask('from the new pc');
    await copy.sync();
    tick();
    pc.ws.addTask('old pc again');
    await pc.sync();
    await settle([pc, copy]);
    expect(pc.id).not.toBe(copy.id);
    expect(visible(pc.ws).open.sort()).toEqual(['from the new pc', 'from the old pc', 'old pc again']);
    expectSame([pc, copy]);
  });

  it('takes its own file back after losing its sync state, and still gets the others’ news', async () => {
    const { devices, cloud, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('from the phone');
    await settle(devices);
    const id = phone.id;
    phone.saved = emptyMeta('folder'); // its sync state lost (an unreadable sync.json, say)
    tick();
    desk.ws.addTask('from the desk, meanwhile');
    await desk.sync();
    await phone.sync();
    expect(phone.id).toBe(id); // no new identity, no ghost file
    expect(cloud.files.size).toBe(2);
    expect(visible(phone.ws).open.sort()).toEqual(['from the desk, meanwhile', 'from the phone']);
  });

  it('publishes news merged in a cycle whose upload failed', async () => {
    const { devices, cloud } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('phone');
    await settle(devices);
    desk.ws.addTask('desk news');
    await desk.sync();
    phone.view.faults = { write: ['server'] };
    expect(await phone.trySync()).toBeNull(); // merged the desk's task, then the upload failed
    expect((await phone.sync()).wrote).toBe(true);
    expect(cloud.files.get(`${phone.id}.json`)!.text).toContain('desk news');
  });

  it('does not fetch a damaged file again until it changes, and keeps saying so', async () => {
    const { devices, cloud } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('one');
    await phone.sync();
    const name = `${phone.id}.json`;
    cloud.put(name, '{"format":"tack-sync-dev');
    expect((await desk.sync()).problems).toEqual([{ device: phone.id, reason: 'unreadable' }]);
    expect((await desk.sync()).problems).toEqual([{ device: phone.id, reason: 'unreadable' }]);
    expect(desk.view.reads.get(name)).toBe(1);
    await phone.sync(); // writes it again
    await desk.sync();
    expect(visible(desk.ws).open).toEqual(['one']);
  });

  it('reports a file from a newer Tack instead of misreading it', async () => {
    const { devices, cloud } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    phone.ws.addTask('one');
    await phone.sync();
    const name = `${phone.id}.json`;
    const doc = JSON.parse(cloud.files.get(name)!.text);
    cloud.put(name, JSON.stringify({ ...doc, version: 2 }));
    const r = await desk.sync();
    expect(r.problems).toEqual([{ device: phone.id, reason: 'newer' }]);
  });
});

describe('deletions over time', () => {
  it('keeps a deletion as a few bytes, and a device back after months can not bring it back', async () => {
    const { devices, tick, cloud } = world('phone', 'desk', 'old tablet');
    const [phone, desk, tablet] = devices as [Device, Device, Device];
    const doomed = phone.ws.addTask('delete me later')!;
    phone.ws.addTask('keep me');
    await settle(devices);
    // The tablet goes in a drawer, with an edit that never got out.
    tablet.view.faults = { list: Array(1000).fill('offline') };
    tick();
    tablet.ws.addTask('written in the drawer');
    tablet.ws.editTask(doomed.id, 'edited in the drawer');
    tick();
    desk.ws.deleteTask(doomed.id);
    for (let day = 0; day < 200; day += 5) {
      tick(5 * DAY);
      await phone.sync();
      await desk.sync();
      await tablet.trySync();
    }
    const deskFile = cloud.files.get(`${desk.id}.json`)!.text;
    expect(deskFile).not.toContain('delete me later');
    expect(JSON.parse(deskFile).deleted.tasks[doomed.id]).toBeTruthy();
    // Back from the drawer: its new task arrives, its edit to the deleted task doesn't revive it.
    tablet.view.faults = {};
    await settle(devices);
    expect(visible(phone.ws).open.sort()).toEqual(['keep me', 'written in the drawer']);
    expectSame(devices);
  });

  it('does not let a stale file, uploaded late, revive anything', async () => {
    const { devices, tick, cloud } = world('phone', { label: 'pc', lag: true });
    const [phone, pc] = devices as [Device, Device];
    const t = phone.ws.addTask('gone for good')!;
    await settle(devices);
    tick();
    pc.ws.addTask('written on the pc');
    await pc.sync(); // waiting in the OneDrive app's queue, which then stalls for months
    tick();
    phone.ws.deleteTask(t.id);
    for (let day = 0; day < 150; day += 10) {
      tick(10 * DAY);
      await phone.sync();
    }
    pc.view.propagate(); // the old file finally goes up
    await phone.sync();
    expect(visible(phone.ws).open).toEqual(['written on the pc']);
    await settle(devices);
    expectSame(devices);
    void cloud;
  });

  it('does not let a new device revive what an abandoned device still has', async () => {
    const { devices, tick, cloud, clock } = world('phone', 'desk', 'old tablet');
    const [phone] = devices as [Device, Device, Device];
    const t = phone.ws.addTask('gone for good')!;
    await settle(devices);
    tick();
    phone.ws.deleteTask(t.id);
    for (let day = 0; day < 400; day += 20) {
      tick(20 * DAY);
      await phone.sync();
      await devices[1]!.sync();
    }
    const fresh = new Device('new phone', cloud, clock);
    await fresh.sync();
    expect(visible(fresh.ws).open).toEqual([]);
  });

  it('still delivers the last changes of a device everyone was away from', async () => {
    const { devices, tick } = world('phone', 'desk');
    const [phone, desk] = devices as [Device, Device];
    await settle(devices);
    phone.ws.addTask('written just before the holiday');
    await phone.sync();
    // Both unused for a year and a half; the desk comes back first.
    tick(540 * DAY);
    await desk.sync();
    expect(visible(desk.ws).open).toEqual(['written just before the holiday']);
    await settle(devices);
    expectSame(devices);
  });

  it('keeps device files small: a deletion costs an id and a clock', async () => {
    const { devices, cloud } = world('phone');
    const phone = devices[0]!;
    for (let i = 0; i < 200; i++) {
      const t = phone.ws.addTask(`task number ${i} with some words in it`)!;
      phone.ws.deleteTask(t.id);
    }
    await phone.sync();
    const size = cloud.files.get(`${phone.id}.json`)!.text.length;
    expect(size / 200).toBeLessThan(50);
  });
});
