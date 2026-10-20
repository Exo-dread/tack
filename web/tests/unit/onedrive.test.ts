import { beforeEach, describe, expect, it } from 'vitest';
import { Hlc } from '../../src/model/hlc';
import { Workspace } from '../../src/model/workspace';
import { emptyMeta, SyncEngine, SyncError, type SyncMeta } from '../../src/sync/engine';
import { OneDriveFolder } from '../../src/sync/onedrive';
import { FakeMicrosoft } from '../fakes/microsoft';

/** A signed-in session against the fake: an access token, renewed when asked to. */
async function session(ms: FakeMicrosoft) {
  // Sign in the way the app does (authorize, then token), but directly.
  const verifier = 'v'.repeat(43);
  const { createHash } = await import('node:crypto');
  const challenge = createHash('sha256').update(verifier).digest('base64url');
  const auth = ms.handle(
    {
      method: 'GET',
      url: `https://login.microsoftonline.com/common/oauth2/v2.0/authorize?${new URLSearchParams({ client_id: ms.clientId, response_type: 'code', redirect_uri: 'https://tackfocus.app/', scope: 'openid offline_access https://graph.microsoft.com/Files.ReadWrite.AppFolder', code_challenge: challenge, code_challenge_method: 'S256', state: 's', nonce: 'n' })}`,
      headers: {},
      body: null,
    },
    false,
  );
  if (auth === 'abort') throw new Error('abort');
  const code = new URLSearchParams(auth.headers.location!.split('#')[1]).get('code')!;
  const token = async (fields: Record<string, string>) => {
    const r = await ms.fetch('https://login.microsoftonline.com/common/oauth2/v2.0/token', {
      method: 'POST',
      body: new URLSearchParams({ client_id: ms.clientId, ...fields }),
    });
    return (await r.json()) as { access_token: string; refresh_token: string };
  };
  let t = await token({ grant_type: 'authorization_code', code, redirect_uri: 'https://tackfocus.app/', code_verifier: verifier });
  let forced = 0;
  return {
    get forced() {
      return forced;
    },
    token: async (force?: boolean) => {
      if (force) {
        forced++;
        t = await token({ grant_type: 'refresh_token', refresh_token: t.refresh_token });
      }
      return t.access_token;
    },
  };
}

describe('the OneDrive folder', () => {
  let ms: FakeMicrosoft;
  beforeEach(() => {
    ms = new FakeMicrosoft();
  });

  it('makes its app folder once, with a marker and a README for people', async () => {
    const s = await session(ms);
    const a = new OneDriveFolder(s.token, ms.fetch);
    const key = await a.open();
    const again = await new OneDriveFolder(s.token, ms.fetch).open();
    expect(again).toBe(key);
    expect(ms.paths()).toEqual(['Apps', 'Apps/Tack Focus', 'Apps/Tack Focus/README.txt', 'Apps/Tack Focus/devices', 'Apps/Tack Focus/tack-sync.json']);
  });

  it('writes, lists (across pages) and reads files, never sending the token to download links', async () => {
    const s = await session(ms);
    const f = new OneDriveFolder(s.token, ms.fetch);
    for (let i = 0; i < 7; i++) await f.write(`dev${i}aaa.json`, `content ${i}`);
    const list = await f.list();
    expect(list.map((e) => e.name)).toEqual(['dev0aaa.json', 'dev1aaa.json', 'dev2aaa.json', 'dev3aaa.json', 'dev4aaa.json', 'dev5aaa.json', 'dev6aaa.json']);
    expect(await f.read(list[3]!)).toBe('content 3');
    const downloads = ms.log.filter((r) => r.url.startsWith('https://dl.files.1drv.com/'));
    expect(downloads.length).toBe(1);
    expect(downloads[0]!.headers.authorization).toBeUndefined();
    // The tag changes when the content does.
    const before = list[3]!.tag;
    await f.write('dev3aaa.json', 'changed');
    expect((await f.list())[3]!.tag).not.toBe(before);
  });

  it('gets a fresh link when a download link has expired', async () => {
    const s = await session(ms);
    const f = new OneDriveFolder(s.token, ms.fetch);
    await f.write('abcd1234.json', 'hello');
    const [entry] = await f.list();
    ms.linkLifetime = -1; // every link handed out from now on is already stale...
    const stale = { ...entry!, url: entry!.url!.replace(/until=\d+/, 'until=1') };
    ms.linkLifetime = 3600_000; // ...but a fresh one works
    expect(await f.read(stale)).toBe('hello');
  });

  it('makes the folder again if you deleted it in OneDrive', async () => {
    const s = await session(ms);
    const f = new OneDriveFolder(s.token, ms.fetch);
    await f.write('abcd1234.json', 'one');
    ms.remove('Apps/Tack Focus');
    await f.write('abcd1234.json', 'two');
    const [entry] = await f.list();
    expect(await f.read(entry!)).toBe('two');
    expect(ms.paths()).toContain('Apps/Tack Focus/tack-sync.json');
  });

  it('asks for a new token once when Graph says the old one is no good', async () => {
    const s = await session(ms);
    const f = new OneDriveFolder(s.token, ms.fetch);
    await f.open();
    ms.fail(/\/children/, { status: 401 });
    await f.list();
    expect(s.forced).toBe(1);
  });

  it('turns every failure into something the scheduler understands', async () => {
    const s = await session(ms);
    const f = new OneDriveFolder(s.token, ms.fetch);
    await f.open();
    const kind = async (op: () => Promise<unknown>) => {
      try {
        await op();
        return 'ok';
      } catch (e) {
        return e instanceof SyncError ? `${e.kind}${e.retryAfter ? `:${e.retryAfter}` : ''}` : String(e);
      }
    };
    ms.fail(/\/children/, { status: 429, retryAfter: 7 });
    expect(await kind(() => f.list())).toBe('throttled:7000');
    ms.fail(/:\/content/, { status: 507 });
    expect(await kind(() => f.write('abcd1234.json', 'x'))).toBe('full');
    ms.fail(/\/children/, { status: 403 });
    expect(await kind(() => f.list())).toBe('denied');
    ms.fail(/\/children/, { abort: true });
    expect(await kind(() => f.list())).toBe('offline');
    ms.fail(/\/children/, { status: 503 });
    expect(await kind(() => f.list())).toBe('throttled:30000');
    ms.fail(/\/children/, { status: 500 });
    expect(await kind(() => f.list())).toBe('server');
    expect(await kind(() => f.list())).toBe('ok');
  });

  it('syncs two devices through it, end to end', async () => {
    const s = await session(ms);
    const wall = Date.now();
    const device = (node: string) => {
      const ws = new Workspace(new Hlc(node, () => wall), () => new Date(wall));
      const folder = new OneDriveFolder(s.token, ms.fetch);
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
    const desk = device('desk0001');
    phone.ws.addTask('from the phone');
    await phone.sync();
    desk.ws.addTask('from the desk');
    await desk.sync();
    await phone.sync();
    expect(phone.ws.open.map((t) => t.text).sort()).toEqual(['from the desk', 'from the phone']);
    expect([...ms.deviceFiles().keys()].sort()).toEqual(['desk0001.json', 'phone001.json']);
  });
});
