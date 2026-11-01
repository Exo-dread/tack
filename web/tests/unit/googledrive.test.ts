import { beforeEach, describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { Workspace } from '../../src/model/workspace';
import { DeviceFileError, makeDeviceFile, MAX_FILE_BYTES, serializeDeviceFile } from '../../src/sync/devicefile';
import { emptyMeta, SyncEngine, SyncError, type FolderEntry, type SyncMeta } from '../../src/sync/engine';
import { GoogleDriveFolder, quoted } from '../../src/sync/googledrive';
import { FakeGoogleDrive } from '../fakes/google';

const SPARE = /^spare-[0-9a-f]{16}\.json$/;

/** A token source as gauth.ts gives one: the same token until asked for a new one. */
function tokens(drive: FakeGoogleDrive) {
  let n = 1;
  let forced = 0;
  return {
    get forced() {
      return forced;
    },
    token: async (force?: boolean) => {
      if (force) {
        forced++;
        drive.tokens.add(`token-${++n}`);
      }
      return `token-${n}`;
    },
  };
}

const kind = async (op: () => Promise<unknown>) => {
  try {
    await op();
    return 'ok';
  } catch (e) {
    return e instanceof SyncError ? `${e.kind}${e.retryAfter ? `:${e.retryAfter}` : ''}` : String(e);
  }
};

describe('the Google Drive folder', () => {
  let drive: FakeGoogleDrive;
  let t: ReturnType<typeof tokens>;
  beforeEach(() => {
    drive = new FakeGoogleDrive();
    t = tokens(drive);
  });

  it('makes its folder once, as the contract says: marker (with spares), README, devices, each tagged', async () => {
    const key = await new GoogleDriveFolder(t.token, drive.fetch).open();
    const [root] = drive.tagged('folder');
    expect(root).toMatchObject({ name: 'Tack Focus', parents: ['root'], mimeType: 'application/vnd.google-apps.folder' });
    expect(key).toBe(`google:${root!.id}`);
    const inside = drive.childrenOf(root!.id).map((f) => [f.name, f.appProperties.tack, f.mimeType]);
    expect(inside.sort()).toEqual([
      ['README.txt', 'readme', 'text/plain'],
      ['devices', 'devices', 'application/vnd.google-apps.folder'],
      ['tack-sync.json', 'marker', 'application/json'],
    ]);
    expect(JSON.parse(drive.tagged('marker')[0]!.content)).toEqual({ format: 'tack-sync', version: 1, spares: true });
    expect(drive.tagged('readme')[0]!.content).toContain('Google Drive');
    // Again (another device, or after a reload): the same folder, nothing new.
    const count = drive.files.size;
    expect(await new GoogleDriveFolder(t.token, drive.fetch).open()).toBe(key);
    expect(drive.files.size).toBe(count);
    // Every request carried the token, and went to Drive only.
    expect(drive.log.every((r) => r.authorization === 'Bearer token-1' && r.url.startsWith('https://www.googleapis.com/'))).toBe(true);
  });

  it('finds its folder by what it is, not by name: renamed, it is still the one', async () => {
    const key = await new GoogleDriveFolder(t.token, drive.fetch).open();
    drive.pcRename(drive.tagged('folder')[0]!.id, 'My Tack');
    expect(await new GoogleDriveFolder(t.token, drive.fetch).open()).toBe(key);
    expect(drive.tagged('folder')).toHaveLength(1);
  });

  it('with two folders (two browsers connecting at once), uses the one made first, as every device does', async () => {
    const later = drive.appCreate('Tack Focus', 'root', { folder: true, tack: 'folder', createdTime: '2026-11-01T10:00:00.000Z', id: 'aaa' });
    const first = drive.appCreate('Tack Focus', 'root', { folder: true, tack: 'folder', createdTime: '2026-11-01T09:00:00.000Z', id: 'zzz' });
    expect(await new GoogleDriveFolder(t.token, drive.fetch).open()).toBe(`google:${first.id}`);
    expect(drive.childrenOf(later.id)).toEqual([]);
    // Made in the same instant: the lower id.
    const twin = drive.appCreate('Tack Focus', 'root', { folder: true, tack: 'folder', createdTime: '2026-11-01T09:00:00.000Z', id: 'mmm' });
    expect(await new GoogleDriveFolder(t.token, drive.fetch).open()).toBe(`google:${twin.id}`);
  });

  it('never takes a Tack folder someone shared with you for your own, however old', async () => {
    const theirs = drive.appCreate('Tack Focus', 'root', { folder: true, tack: 'folder', createdTime: '2020-01-01T00:00:00.000Z', id: 'aaa' });
    theirs.shared = true;
    const key = await new GoogleDriveFolder(t.token, drive.fetch).open();
    expect(key).not.toBe(`google:${theirs.id}`);
    expect(drive.childrenOf(theirs.id)).toEqual([]);
  });

  it('when another browser made one just before it, uses that one, and puts its own (empty) in the trash', async () => {
    drive.onCreate = (made) => {
      drive.onCreate = null;
      if (made.appProperties.tack === 'folder') drive.appCreate('Tack Focus', 'root', { folder: true, tack: 'folder', createdTime: '2026-01-01T00:00:00.000Z', id: 'other' });
    };
    expect(await new GoogleDriveFolder(t.token, drive.fetch).open()).toBe('google:other');
    expect(drive.tagged('folder').map((f) => f.id)).toEqual(['other']);
    expect(drive.childrenOf('other').map((f) => f.name).sort()).toEqual(['README.txt', 'devices', 'tack-sync.json']);
  });

  it('keeps three spares in devices: three at first, then one for each a PC claims, none while there are three', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    await f.open();
    await f.list();
    const devices = drive.devices()!.id;
    const spares = () => drive.childrenOf(devices).filter((x) => SPARE.test(x.name));
    expect(spares()).toHaveLength(3);
    expect(spares().every((x) => x.appProperties.tack === 'spare' && x.content === '' && x.byApp)).toBe(true);
    expect(new Set(spares().map((x) => x.name)).size).toBe(3);
    await f.list();
    expect(spares()).toHaveLength(3);
    // A PC claims one (renames it to its own name, then writes it): one more is made.
    const claimed = spares()[0]!;
    drive.pcRename(claimed.id, 'desk0001.json');
    drive.pcWrite(claimed.id, 'its file');
    const listed = await f.list();
    expect(listed.map((e) => e.name)).toContain('desk0001.json');
    expect(spares()).toHaveLength(3);
    expect(drive.childrenOf(devices)).toHaveLength(4);
  });

  it('makes no more than three in one cycle, however many are missing', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    await f.list();
    const devices = drive.devices()!.id;
    for (const s of drive.childrenOf(devices)) drive.remove(s.id);
    await f.list();
    expect(drive.childrenOf(devices)).toHaveLength(3);
  });

  it('lists only files it made, not in the trash, and no folders', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    await f.list();
    const devices = drive.devices()!.id;
    drive.pcCreate('pc000001.json', devices, 'made on a PC'); // exists, invisible to drive.file
    const binned = drive.appCreate('bin00001.json', devices, { tack: 'device', content: 'x' });
    drive.trash(binned.id);
    drive.appCreate('sub', devices, { folder: true });
    const names = (await f.list()).map((e) => e.name);
    expect(names.filter((n) => !SPARE.test(n))).toEqual([]);
    expect(names).toHaveLength(3);
  });

  it('writes its own file once as a device file, then by id; its tag is Drive’s version', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    await f.list();
    const devices = drive.devices()!.id;
    const first = await f.write('abcd1234.json', 'one');
    const [made] = drive.childrenOf(devices).filter((x) => x.name === 'abcd1234.json');
    expect(made).toMatchObject({ content: 'one', appProperties: { tack: 'device' }, parents: [devices], mimeType: 'application/json' });
    const second = await f.write('abcd1234.json', 'two');
    expect(drive.childrenOf(devices).filter((x) => x.name === 'abcd1234.json')).toHaveLength(1);
    expect(made!.content).toBe('two');
    expect(second.tag).not.toBe(first.tag);
    const listed = (await f.list()).find((e) => e.name === 'abcd1234.json')!;
    expect(listed.tag).toBe(second.tag);
    expect(await f.read(listed)).toBe('two');
    // A fresh start (no listing yet) finds it by name rather than making a second one.
    await new GoogleDriveFolder(t.token, drive.fetch).write('abcd1234.json', 'three');
    expect(drive.childrenOf(devices).filter((x) => x.name === 'abcd1234.json')).toHaveLength(1);
    expect(made!.content).toBe('three');
  });

  it('never writes to a spare, or to anything but a device file', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    await f.list();
    const spare = drive.childrenOf(drive.devices()!.id).find((x) => SPARE.test(x.name))!;
    const before = drive.log.length;
    expect(await kind(() => f.write(spare.name, 'mine now'))).toBe('denied');
    expect(await kind(() => f.write('README.txt', 'x'))).toBe('denied');
    expect(drive.log.length).toBe(before);
    expect(spare.content).toBe('');
  });

  it('quotes names in queries', () => {
    expect(quoted("it's a \\ test")).toBe("it\\'s a \\\\ test");
  });

  it('makes the folder again if you deleted it in Google Drive', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    const key = await f.open();
    await f.write('abcd1234.json', 'one');
    drive.remove(drive.tagged('folder')[0]!.id);
    await f.write('abcd1234.json', 'two');
    const entry = (await f.list()).find((e) => e.name === 'abcd1234.json')!;
    expect(await f.read(entry)).toBe('two');
    expect(drive.tagged('marker')).toHaveLength(1);
    expect(await f.open()).not.toBe(key);
  });

  it('stops at a folder in the trash ("gone"), and makes a new one next time', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    const key = await f.open();
    await f.write('abcd1234.json', 'one');
    drive.trash(drive.tagged('folder')[0]!.id);
    expect(await kind(() => f.list())).toBe('gone');
    const again = await f.open();
    expect(again).not.toBe(key);
    expect((await f.list()).filter((e) => !SPARE.test(e.name))).toEqual([]);
  });

  it('asks for a new token once when Drive says the old one is no good, and says so if that fails too', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    await f.open();
    drive.tokens.delete('token-1');
    await f.list();
    expect(t.forced).toBe(1);
    drive.fail(/\/files\?/, { status: 401, times: 2 });
    expect(await kind(() => f.list())).toBe('auth');
    expect(t.forced).toBe(2);
  });

  it('turns every failure into something the scheduler understands', async () => {
    const f = new GoogleDriveFolder(t.token, drive.fetch);
    await f.open();
    const list = /\/files\?q=/;
    drive.fail(list, { status: 429, retryAfter: 7 });
    expect(await kind(() => f.list())).toBe('throttled:7000');
    for (const status of [500, 502, 503, 504]) {
      drive.fail(list, { status });
      expect(await kind(() => f.list())).toBe('throttled:30000');
    }
    drive.fail(list, { status: 403, reason: 'rateLimitExceeded', retryAfter: 60 });
    expect(await kind(() => f.list())).toBe('throttled:60000');
    drive.fail(list, { status: 403, reason: 'userRateLimitExceeded' });
    expect(await kind(() => f.list())).toBe('throttled:30000');
    drive.fail(/upload\/drive/, { status: 403, reason: 'storageQuotaExceeded' });
    expect(await kind(() => f.write('abcd1234.json', 'x'))).toBe('full');
    drive.fail(list, { status: 403, reason: 'insufficientFilePermissions' });
    expect(await kind(() => f.list())).toBe('denied');
    drive.fail(list, { abort: true });
    expect(await kind(() => f.list())).toBe('offline');
    drive.fail(list, { status: 404 });
    expect(await kind(() => f.list())).toBe('gone');
    expect(await kind(() => f.list())).toBe('ok');
  });

  /*
   * CLAUDE.md: every new call to a provider gets a test with a fake that answers 429. Each request
   * a first sync makes (finding the folder, making it and what's in it, the trash check, listing,
   * the spares, the first write and the update after it, a download) is answered 429 in turn:
   * every time, the operation stops there, with the wait Drive asked for, and asks nothing more.
   */
  it('stops at a 429 on any call it makes, and waits as asked', async () => {
    const steps = async (f: GoogleDriveFolder) => {
      await f.open();
      await f.list();
      await f.write('abcd1234.json', 'one');
      await f.write('abcd1234.json', 'two');
      const entry = (await f.list()).find((e) => e.name === 'abcd1234.json')!;
      await f.read(entry);
    };
    const clean = new FakeGoogleDrive();
    await steps(new GoogleDriveFolder(tokens(clean).token, clean.fetch));
    const calls = clean.log.length;
    expect(calls).toBeGreaterThan(12);
    const kinds = new Set<string>();
    for (let n = 0; n < calls; n++) {
      const d = new FakeGoogleDrive();
      const at = n;
      let seen = 0;
      const http: typeof fetch = async (input, init) => {
        if (seen++ === at) {
          kinds.add(`${init?.method ?? 'GET'} ${String(input).replace(/\?.*/, '').replace(/files\/[\w-]+/, 'files/{id}')}`);
          return new Response('{}', { status: 429, headers: { 'Retry-After': '90' } });
        }
        return d.fetch(input, init);
      };
      const error = await steps(new GoogleDriveFolder(tokens(d).token, http)).catch((e: unknown) => e);
      expect(error).toBeInstanceOf(SyncError);
      expect(error).toMatchObject({ kind: 'throttled', retryAfter: 90_000 });
      expect(seen).toBe(at + 1); // nothing asked after the 429
    }
    // Every kind of call was among them.
    expect([...kinds].sort()).toEqual([
      'GET https://www.googleapis.com/drive/v3/files',
      'GET https://www.googleapis.com/drive/v3/files/{id}',
      'PATCH https://www.googleapis.com/upload/drive/v3/files/{id}',
      'POST https://www.googleapis.com/drive/v3/files',
      'POST https://www.googleapis.com/upload/drive/v3/files',
    ]);
  });

  it('a 429 when putting a folder made at the same moment in the trash stops there too', async () => {
    drive.onCreate = (made) => {
      drive.onCreate = null;
      if (made.appProperties.tack === 'folder') drive.appCreate('Tack Focus', 'root', { folder: true, tack: 'folder', createdTime: '2026-01-01T00:00:00.000Z', id: 'other' });
    };
    drive.fail(/\/files\/[\w-]+\?fields=id$/, { status: 429, method: 'PATCH', retryAfter: 5 });
    expect(await kind(() => new GoogleDriveFolder(t.token, drive.fetch).open())).toBe('throttled:5000');
  });

  it('never takes in more than a device file may hold, whatever the listing said', async () => {
    const chunk = new Uint8Array(1024 * 1024).fill(0x20);
    let pulled = 0;
    const big: typeof fetch = async (input, init) => {
      if (!String(input).includes('alt=media')) return drive.fetch(input, init);
      let left = 25;
      const body = new ReadableStream<Uint8Array>({
        pull(c) {
          if (left-- === 0) return c.close();
          pulled += chunk.length;
          c.enqueue(chunk);
        },
      });
      return new Response(body, { status: 200 });
    };
    const entry = { name: 'abcd1234.json', tag: '1', id: 'x', size: 10 } as FolderEntry;
    const error = await new GoogleDriveFolder(t.token, big).read(entry).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(DeviceFileError);
    expect(pulled).toBeLessThan(MAX_FILE_BYTES + 3 * chunk.length);
    // Said up front by the listing: not even asked for.
    const before = drive.log.length;
    await expect(new GoogleDriveFolder(t.token, drive.fetch).read({ ...entry, size: MAX_FILE_BYTES + 1 } as FolderEntry)).rejects.toBeInstanceOf(DeviceFileError);
    expect(drive.log.length).toBe(before);
  });

  it('syncs two browsers and a PC (through a spare it claimed) end to end', async () => {
    const wall = Date.UTC(2026, 10, 1, 9, 0, 0);
    const device = (node: string) => {
      const ws = new Workspace(new Hlc(node, () => wall), () => new Date(wall));
      const folder = new GoogleDriveFolder(t.token, drive.fetch);
      let meta: SyncMeta | null = null;
      const engine = new SyncEngine(ws, folder, { label: () => ({ name: node, app: 'test' }), flush: async () => {}, saveMeta: async () => {} });
      return {
        ws,
        sync: async () => {
          const key = await folder.open();
          meta ??= emptyMeta(key);
          return engine.cycle(meta, wall);
        },
      };
    };
    const phone = device('phone001');
    const tablet = device('tablet01');
    phone.ws.addTask('from the phone');
    await phone.sync();
    tablet.ws.addTask('from the tablet');
    await tablet.sync();
    // Tack for Windows: claims a spare by renaming it, then writes its file there (Drive for desktop uploads it).
    const pc = new Workspace(new Hlc('desk0001', () => wall), () => new Date(wall));
    pc.addTask('from the PC');
    const spare = drive.childrenOf(drive.devices()!.id).find((x) => SPARE.test(x.name))!;
    drive.pcRename(spare.id, 'desk0001.json');
    drive.pcWrite(spare.id, serializeDeviceFile(makeDeviceFile(pc.toData(), { device: 'desk0001', name: 'PC', app: 'Tack', written: pc.hlc.now(), seen: {} })));
    // A file the PC made itself would be invisible: this one is ignored by everyone.
    drive.pcCreate('ghost001.json', drive.devices()!.id, 'never seen');
    await phone.sync();
    expect(phone.ws.open.map((x) => x.text).sort()).toEqual(['from the PC', 'from the phone', 'from the tablet']);
    const names = drive.childrenOf(drive.devices()!.id).filter((x) => !SPARE.test(x.name)).map((x) => x.name);
    expect(names.sort()).toEqual(['desk0001.json', 'ghost001.json', 'phone001.json', 'tablet01.json']);
    expect(drive.childrenOf(drive.devices()!.id).filter((x) => SPARE.test(x.name))).toHaveLength(3);
  });
});
