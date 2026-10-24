import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Hlc, ZERO } from '../../src/model/hlc';
import { Workspace } from '../../src/model/workspace';
import { MemoryBackend } from '../../src/store/db';
import type { Store } from '../../src/store/persist';
import { SyncController } from '../../src/sync/controller';
import { emptyMeta, type SyncMeta } from '../../src/sync/engine';
import { AuthError, emptyAuth, SCOPES, type AuthRecord } from '../../src/sync/msauth';
import { AUTHORITY, CONSUMERS, FakeMicrosoft } from '../fakes/microsoft';

/**
 * While the page is on its way to Microsoft to renew the sign-in, an update of the app holds off
 * (a reload would cut the visit short): `leaving` says so. It must not stay set once the page is
 * plainly staying, or updates would wait for good.
 */
describe('setting off to Microsoft to renew the sign-in', () => {
  const config = {
    clientId: '11111111-2222-3333-4444-555555555555',
    authority: 'https://login.microsoftonline.com/common',
    redirectUri: 'https://tackfocus.app/',
    silentRedirectUri: 'https://tackfocus.app/auth/',
    scopes: SCOPES,
  };
  const reload = vi.fn();
  let failed: ReturnType<typeof vi.spyOn>;
  /** The network as far as Microsoft is concerned: reachable unless a test says otherwise. */
  let network: typeof fetch;

  /** Sync on and signed in, as a page that offers Reconnect in Settings. */
  async function signedIn(backend = new MemoryBackend()): Promise<SyncController> {
    await backend.putMeta('sync', { enabled: true, meta: null });
    const tokens = { accessToken: 'a', accessExpires: Date.now() + 3600_000, refreshToken: 'r', refreshExpires: Date.now() + 3600_000 };
    await backend.putMeta('auth', { ...emptyAuth(), account: { username: 'ada@outlook.com', name: 'Ada', tenant: 'consumers', id: 'oid-ada' }, tokens });
    const store = { flushAll: async () => {}, allSaved: true } as unknown as Store;
    return SyncController.create({} as Workspace, store, backend, config, (...args) => network(...args));
  }

  beforeEach(() => {
    reload.mockClear();
    network = async () => new Response('{}');
    const kept = new Map<string, string>();
    vi.stubGlobal('localStorage', { getItem: (k: string) => kept.get(k) ?? null, setItem: (k: string, v: string) => void kept.set(k, v), removeItem: (k: string) => void kept.delete(k) });
    failed = vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
    failed.mockRestore();
  });

  it('is not on the way once the visit fails to start (Reconnect: the page stays)', async () => {
    vi.stubGlobal('location', {
      assign: () => {
        throw new Error('navigation blocked');
      },
      reload,
    });
    const sync = await signedIn();
    await sync.reconnect();
    await vi.waitFor(() => expect(failed).toHaveBeenCalledWith('Renewal visit failed to start', expect.any(Error)));
    expect(sync.leaving).toBe(false);
    expect(reload).not.toHaveBeenCalled();
  });

  it('is on the way while it goes, and not once it has plainly stayed (the way there was cancelled)', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    let went!: () => void;
    const gone = new Promise<void>((resolve) => (went = resolve));
    vi.stubGlobal('location', { assign: () => went(), reload });
    const sync = await signedIn();
    expect(sync.leaving).toBe(false);
    await sync.reconnect();
    await gone;
    expect(sync.leaving).toBe(true);
    vi.advanceTimersByTime(29_000);
    expect(sync.leaving).toBe(true);
    vi.advanceTimersByTime(1_000);
    expect(sync.leaving).toBe(false);
    expect(failed).not.toHaveBeenCalled();
  });

  it('stays when Microsoft can’t be reached, says so, and doesn’t count it as a visit', async () => {
    const assign = vi.fn();
    vi.stubGlobal('location', { assign, reload });
    const warned = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const backend = new MemoryBackend();
    const sync = await signedIn(backend);
    network = async () => {
      throw new TypeError('Failed to fetch'); // Wi-Fi without internet, say
    };
    await sync.reconnect();
    expect(assign).not.toHaveBeenCalled();
    expect(sync.leaving).toBe(false);
    expect(sync.status.message).toBe('Couldn’t reach Microsoft just now. Check your connection, then try again.');
    expect((await backend.getMeta<AuthRecord>('auth'))!.lastRenewVisit).toBe(0);
    expect(warned).toHaveBeenCalledWith('Microsoft can’t be reached: the sign-in renews another time');

    network = async () => new Response('{}'); // back
    await sync.reconnect();
    await vi.waitFor(() => expect(assign).toHaveBeenCalledTimes(1));
    const auth = (await backend.getMeta<AuthRecord>('auth'))!;
    expect(auth.lastRenewVisit).toBeGreaterThan(0);
    expect(auth.visitReturned).toBe(false); // on its way, not back yet
    warned.mockRestore();
  });
});

/** Just enough of a top-level page for the renewal visit: online, an address, storage, and "going" somewhere. */
function stubPage(): { assigned: string[]; handlers: Map<string, () => void>; doc: { visibilityState: string; activeElement: { tagName: string } | null } } {
  const assigned: string[] = [];
  const handlers = new Map<string, () => void>();
  const kept = new Map<string, string>();
  const page = { top: null as unknown, addEventListener: () => {} };
  page.top = page;
  const doc = {
    visibilityState: 'visible',
    activeElement: null as { tagName: string } | null,
    querySelector: () => null,
    addEventListener: (type: string, handler: () => void) => void handlers.set(type, handler),
  };
  vi.stubGlobal('window', page);
  vi.stubGlobal('document', doc);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('location', { origin: 'https://tackfocus.app', pathname: '/', search: '', hash: '', assign: (url: string) => void assigned.push(url), reload: () => {} });
  vi.stubGlobal('history', { replaceState: (_s: unknown, _t: string, url: string) => (location.hash = url.includes('#') ? url.slice(url.indexOf('#')) : '') });
  vi.stubGlobal('localStorage', {
    get length() {
      return kept.size;
    },
    key: (i: number) => [...kept.keys()][i] ?? null,
    getItem: (k: string) => kept.get(k) ?? null,
    setItem: (k: string, v: string) => void kept.set(k, v),
    removeItem: (k: string) => void kept.delete(k),
  });
  return { assigned, handlers, doc };
}

/**
 * At most one renewal visit every six hours, so it can never loop. But one that never came back
 * (Tack was opened before Microsoft answered, or you went Back from Microsoft's page) is tried
 * again after an hour: six would let the sign-in run out first (visits start four hours before).
 */
describe('how soon a renewal visit is tried again', () => {
  const ms = new FakeMicrosoft();
  const config = { clientId: ms.clientId, authority: AUTHORITY, redirectUri: 'https://tackfocus.app/', silentRedirectUri: 'https://tackfocus.app/auth/', scopes: SCOPES };
  const account = { username: 'ada@outlook.com', name: 'Ada Lovelace', tenant: CONSUMERS, id: 'oid-ada' };
  const store = { flushAll: async () => {}, allSaved: true } as unknown as Store;
  const HOUR = 3600_000;
  let assigned: string[];

  beforeEach(() => {
    assigned = stubPage().assigned;
  });

  afterEach(() => vi.unstubAllGlobals());

  /** Sync on, frames refused here (as in Safari), the sign-in running out within the four hours. */
  async function dueSoon(visit: Partial<AuthRecord>): Promise<MemoryBackend> {
    const backend = new MemoryBackend();
    await backend.putMeta('sync', { enabled: true, meta: null });
    const tokens = { accessToken: 'a', accessExpires: Date.now() + HOUR, refreshToken: 'r', refreshExpires: Date.now() + 2 * HOUR };
    await backend.putMeta('auth', { ...emptyAuth(), account, tokens, frameWorks: false, ...visit });
    return backend;
  }

  const due = async (visit: Partial<AuthRecord>) => (await SyncController.create({} as Workspace, store, await dueSoon(visit), config, ms.fetch)).renewalDueAtStart();

  it('waits six hours after a visit that came back, one hour after one that didn’t', async () => {
    const ago = (h: number) => Date.now() - h * HOUR;
    expect(await due({ lastRenewVisit: 0 })).toBe(true);
    expect(await due({ lastRenewVisit: ago(2), visitReturned: true })).toBe(false);
    expect(await due({ lastRenewVisit: ago(6.1), visitReturned: true })).toBe(true);
    expect(await due({ lastRenewVisit: ago(0.5), visitReturned: false })).toBe(false); // e.g. just reloaded by "Open Tack now"
    expect(await due({ lastRenewVisit: ago(1.1), visitReturned: false })).toBe(true);
    // Saved before Tack kept track of this: as before, six hours.
    const old = await dueSoon({ lastRenewVisit: ago(2) });
    const { visitReturned: _, ...before } = (await old.getMeta<AuthRecord>('auth'))!;
    await old.putMeta('auth', before);
    expect((await SyncController.create({} as Workspace, store, old, config, ms.fetch)).renewalDueAtStart()).toBe(false);
  });

  it('marks a visit as on its way when it goes, and as back when Microsoft sends you back', async () => {
    const backend = await dueSoon({});
    const leaving = await SyncController.create({} as Workspace, store, backend, config, ms.fetch);
    expect(await leaving.renewBeforeStart()).toBe(true);
    await vi.waitFor(() => expect(assigned).toHaveLength(1));
    expect((await backend.getMeta<AuthRecord>('auth'))!.visitReturned).toBe(false);

    // Microsoft sends the page back with a code: the next page load finishes the renewal.
    const answer = ms.handle({ method: 'GET', url: assigned[0]!, headers: {}, body: null }, false);
    if (answer === 'abort') throw new Error('abort');
    location.hash = answer.headers.location!.slice(answer.headers.location!.indexOf('#'));
    const back = await SyncController.create({} as Workspace, store, backend, config, ms.fetch);
    expect(await back.handleReturn()).toBe('renewed');
    const auth = (await backend.getMeta<AuthRecord>('auth'))!;
    expect(auth.visitReturned).toBe(true);
    expect(auth.tokens!.refreshExpires).toBeGreaterThan(Date.now() + 20 * HOUR);
    expect(back.renewalDueAtStart()).toBe(false);
  });

  it('counts a renewal that failed on the way back (or was cut short by opening Tack) as not back', async () => {
    const backend = await dueSoon({});
    const leaving = await SyncController.create({} as Workspace, store, backend, config, ms.fetch);
    expect(await leaving.renewBeforeStart()).toBe(true);
    await vi.waitFor(() => expect(assigned).toHaveLength(1));
    const answer = ms.handle({ method: 'GET', url: assigned[0]!, headers: {}, body: null }, false);
    if (answer === 'abort') throw new Error('abort');
    location.hash = answer.headers.location!.slice(answer.headers.location!.indexOf('#'));

    // The exchange for tokens never gets through: the page was reloaded, or the network dropped.
    const cut: typeof fetch = (input, init) =>
      String(input instanceof Request ? input.url : input).includes('/token') ? Promise.reject(new TypeError('Load failed')) : ms.fetch(input, init);
    const back = await SyncController.create({} as Workspace, store, backend, config, cut);
    expect(await back.handleReturn()).toBe('failed');
    // Tried again after the short gap, well before the sign-in runs out.
    expect((await backend.getMeta<AuthRecord>('auth'))!.visitReturned).toBe(false);
  });
});

/**
 * Coming back to the app after a while, the visit waits for a quiet moment: nothing being typed.
 * Knowing Microsoft can be reached takes a moment, so that's asked again just before going.
 */
describe('a renewal visit when you come back to the app', () => {
  const ms = new FakeMicrosoft();
  const config = { clientId: ms.clientId, authority: AUTHORITY, redirectUri: 'https://tackfocus.app/', silentRedirectUri: 'https://tackfocus.app/auth/', scopes: SCOPES };
  const account = { username: 'ada@outlook.com', name: 'Ada Lovelace', tenant: CONSUMERS, id: 'oid-ada' };
  const store = { flushAll: async () => {}, allSaved: true } as unknown as Store;
  const ws = { subscribe: () => () => {} } as unknown as Workspace;

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** On screen again after ten minutes away, the sign-in run out, frames refused here. */
  async function comingBack(): Promise<{ page: ReturnType<typeof stubPage>; back: () => void; answer: () => void; started: Promise<void> }> {
    const page = stubPage();
    const backend = new MemoryBackend();
    await backend.putMeta('sync', { enabled: true, meta: null });
    const tokens = { accessToken: 'a', accessExpires: Date.now() - 1000, refreshToken: 'r', refreshExpires: Date.now() - 1000 };
    await backend.putMeta('auth', { ...emptyAuth(), account, tokens, frameWorks: false });
    let answer!: () => void;
    let asked!: () => void;
    const started = new Promise<void>((resolve) => (asked = resolve));
    // Microsoft answers the "can you be reached?" request when the test says so; the rest as usual.
    const http: typeof fetch = (input, init) => {
      if (!String(input).includes('.well-known')) return ms.fetch(input, init);
      asked();
      return new Promise((resolve) => (answer = () => resolve(new Response('{}'))));
    };
    const sync = await SyncController.create(ws, store, backend, config, http);
    vi.spyOn(sync as unknown as { schedule: () => void }, 'schedule').mockImplementation(() => {});
    vi.spyOn(sync as unknown as { run: () => Promise<void> }, 'run').mockResolvedValue(undefined);
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    sync.start();
    const visibility = page.handlers.get('visibilitychange')!;
    page.doc.visibilityState = 'hidden';
    visibility();
    (sync as unknown as { hiddenAt: number }).hiddenAt = Date.now() - 10 * 60_000;
    page.doc.visibilityState = 'visible';
    return { page, back: visibility, answer: () => answer(), started };
  }

  it('goes when it’s still quiet', async () => {
    const { page, back, answer, started } = await comingBack();
    back();
    await started;
    answer();
    await vi.waitFor(() => expect(page.assigned).toHaveLength(1));
  });

  it('stays if you started typing meanwhile', async () => {
    const { page, back, answer, started } = await comingBack();
    back();
    await started;
    page.doc.activeElement = { tagName: 'TEXTAREA' }; // writing a task, say
    answer();
    await new Promise((r) => setTimeout(r, 50));
    expect(page.assigned).toHaveLength(0);
  });
});

/*
 * The second audit (24 October 2026): Tack never hammers a user's Microsoft account. When Microsoft
 * says "slow down", nothing local (an edit, switching apps) brings the next request forward; a
 * hidden frame that keeps timing out is tried less and less often, then handed to a visit.
 */
describe('waiting as long as Microsoft asks', () => {
  const config = { clientId: '11111111-2222-3333-4444-555555555555', authority: AUTHORITY, redirectUri: 'https://tackfocus.app/', silentRedirectUri: 'https://tackfocus.app/auth/', scopes: SCOPES };
  const account = { username: 'ada@outlook.com', name: 'Ada', tenant: CONSUMERS, id: 'oid-ada' };
  const HOUR = 3600_000;
  const GRAPH = 'https://graph.microsoft.com/v1.0';

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    vi.spyOn(console, 'warn').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** A page on screen, signed in (the refresh token good for `refreshFor`), sync on, Graph answering through `http`. */
  async function running(http: typeof fetch, refreshFor = 20 * HOUR, auth: Partial<AuthRecord> = {}) {
    const page = stubPage();
    vi.stubGlobal('navigator', { onLine: true, userAgent: 'test' });
    vi.stubGlobal('fetch', http); // the controller's OneDrive folder uses the page's fetch
    const backend = new MemoryBackend();
    await backend.putMeta('sync', { enabled: true, meta: null });
    const tokens = { accessToken: 'a', accessExpires: Date.now() + 10 * HOUR, refreshToken: 'r', refreshExpires: Date.now() + refreshFor };
    await backend.putMeta('auth', { ...emptyAuth(), account, tokens, ...auth });
    const ws = new Workspace(new Hlc('dev12345'));
    const store = { flushAll: async () => {}, flush: async () => {}, status: 'saved', allSaved: true } as unknown as Store;
    const sync = await SyncController.create(ws, store, backend, config, http);
    return { page, backend, ws, sync };
  }

  /** A healthy Graph with an app folder and devices/, keeping uploads; `requests` says when it was asked. */
  function graph(): { http: typeof fetch; requests: number[] } {
    const files = new Map<string, string>();
    const requests: number[] = [];
    const json = (v: unknown) => new Response(JSON.stringify(v), { status: 200, headers: { 'content-type': 'application/json' } });
    const http: typeof fetch = async (input, init) => {
      requests.push(Date.now());
      const url = String(input);
      if (url === `${GRAPH}/me/drive/special/approot`) return json({ id: 'root', name: 'Tack Focus', parentReference: { driveId: 'd' } });
      if (url.startsWith(`${GRAPH}/me/drive/items/root/children`)) return json({ value: [{ id: 'dev', name: 'devices', folder: {} }, { id: 'm', name: 'tack-sync.json', file: {} }, { id: 'r', name: 'README.txt', file: {} }] });
      if (url.startsWith(`${GRAPH}/me/drive/items/dev/children`)) return json({ value: [...files.keys()].map((name) => ({ id: name, name, file: {}, cTag: `c${files.get(name)!.length}`, size: files.get(name)!.length })) });
      const m = /items\/dev:\/(.+):\/content$/.exec(url);
      if (m && init?.method === 'PUT') {
        const name = decodeURIComponent(m[1]!);
        files.set(name, String(init.body));
        return json({ id: name, name, cTag: `c${String(init.body).length}` });
      }
      return new Response('{}', { status: 404 });
    };
    return { http, requests };
  }

  it('Retry-After holds against edits, switching apps and Sync now (L4)', async () => {
    const requests: number[] = [];
    let throttling = true;
    const healthy = graph();
    const http: typeof fetch = async (input, init) => {
      if (!throttling) return healthy.http(input, init);
      requests.push(Date.now());
      return new Response(JSON.stringify({ error: { code: 'activityLimitReached', message: 'slow down' } }), { status: 429, headers: { 'Retry-After': '120', 'content-type': 'application/json' } });
    };
    const { page, ws, sync } = await running(http);
    sync.start();
    await vi.advanceTimersByTimeAsync(1500);
    expect(requests).toHaveLength(1); // the first sync: 429, Retry-After 120 s
    const first = requests[0]!;
    // Within the next two minutes: an edit every ten seconds, the app hidden and shown, Sync now.
    for (let s = 0; s < 100; s += 10) {
      ws.addTask(`task ${s}`);
      await vi.advanceTimersByTimeAsync(10_000);
    }
    page.doc.visibilityState = 'hidden';
    page.handlers.get('visibilitychange')!();
    await vi.advanceTimersByTimeAsync(1);
    page.doc.visibilityState = 'visible';
    page.handlers.get('visibilitychange')!();
    sync.syncNow();
    await vi.advanceTimersByTimeAsync(1);
    expect(requests.filter((t) => t > first && t < first + 120_000)).toEqual([]);
    // Then it goes again.
    throttling = false;
    await vi.advanceTimersByTimeAsync(20_000);
    expect(healthy.requests.length).toBeGreaterThan(0);
    expect(sync.status.phase).toBe('synced');
  });

  it('a hidden frame that keeps timing out is tried less often, then a visit takes over (L5)', async () => {
    const g = graph();
    // Signed in 21 hours ago: renewing ahead of time is due (four hours before the end).
    const { backend, ws, sync } = await running(g.http, 3 * HOUR);
    let frames = 0;
    const msal = (sync as unknown as { msal: { renewInFrame: () => Promise<never> } }).msal;
    vi.spyOn(msal, 'renewInFrame').mockImplementation(() => {
      frames++;
      return new Promise((_, reject) => setTimeout(() => reject(new AuthError('failed', 'Silent renewal timed out')), 10_000));
    });
    sync.start();
    // An hour on screen, typing now and then: it used to try once a minute, and after every edit.
    for (let i = 0; i < 60; i++) {
      if (i % 5 === 0) ws.addTask(`t${i}`);
      await vi.advanceTimersByTimeAsync(60_000);
    }
    expect(frames).toBe(3); // at once, after 15 minutes, after 30 more
    const auth = (await backend.getMeta<AuthRecord>('auth'))!;
    expect([auth.frameTimeouts, auth.frameRefused]).toEqual([3, 0]); // timed out, not refused
    // ...so a renewal visit is due at the next quiet moment, well before the sign-in runs out.
    expect(sync.renewalDueAtStart()).toBe(true);
  });

  // The regression review: counted as refused, one slow evening switched frames off for good.
  it('a few frames timing out don’t make this a browser that refuses them: after the visit, frames again (L5)', async () => {
    const g = graph();
    const { backend, sync } = await running(g.http, 3 * HOUR, { frameWorks: true });
    let frames = 0;
    const msal = (sync as unknown as { msal: { renewInFrame: () => Promise<never>; completeRedirect: () => Promise<unknown> } }).msal;
    vi.spyOn(msal, 'renewInFrame').mockImplementation(() => {
      frames++;
      return new Promise((_, reject) => setTimeout(() => reject(new AuthError('failed', 'Silent renewal timed out')), 10_000));
    });
    sync.start();
    await vi.advanceTimersByTimeAsync(HOUR);
    expect(frames).toBe(3);
    expect(sync.renewalDueAtStart()).toBe(true);
    // The visit comes back renewed.
    const fresh = { accessToken: 'b', accessExpires: Date.now() + HOUR, refreshToken: 'r2', refreshExpires: Date.now() + 24 * HOUR };
    vi.spyOn(msal, 'completeRedirect').mockResolvedValue({ kind: 'renew', account, tokens: fresh });
    expect(await sync.handleReturn()).toBe('renewed');
    const auth = (await backend.getMeta<AuthRecord>('auth'))!;
    expect([auth.frameWorks, auth.frameTimeouts, auth.frameRefused]).toEqual([true, 0, 0]);
    // The next day the hidden frame renews again, with no trip to Microsoft's page.
    vi.setSystemTime(Date.now() + 21 * HOUR);
    expect(sync.renewalDueAtStart()).toBe(false);
    const renewing = (sync as unknown as { renewInFrame(): Promise<void> }).renewInFrame();
    await vi.advanceTimersByTimeAsync(10_000);
    await renewing;
    expect(frames).toBe(4);
  });

  // The regression review: "Sync now" waited for Tack's own backoff (up to half an hour) too.
  it('after a long storm with no Retry-After, Sync now waits only the 30 seconds Microsoft is owed (L4)', async () => {
    let throttling = true;
    const healthy = graph();
    const failed: number[] = [];
    const http: typeof fetch = async (input, init) => {
      if (!throttling) return healthy.http(input, init);
      failed.push(Date.now());
      return new Response('{}', { status: 503 });
    };
    const { sync } = await running(http);
    sync.start();
    await vi.advanceTimersByTimeAsync(2 * HOUR); // the backoff grows to half an hour
    expect(failed.at(-1)! - failed.at(-2)!).toBeGreaterThanOrEqual(29 * 60_000);
    expect(Date.now() - failed.at(-1)!).toBeGreaterThan(60_000);
    // Microsoft has recovered: Sync now goes at once, not when Tack's half hour is up.
    throttling = false;
    sync.syncNow();
    await vi.advanceTimersByTimeAsync(1000);
    expect(healthy.requests.length).toBeGreaterThan(0);
    expect(sync.status.phase).toBe('synced');
  });

  it('a frame that works resets it all', async () => {
    const g = graph();
    const { backend, sync } = await running(g.http, 3 * HOUR);
    await backend.putMeta('auth', { ...(await backend.getMeta<AuthRecord>('auth'))!, frameTimeouts: 2, frameTimedOut: Date.now() - 31 * 60_000 });
    const fresh = { accessToken: 'b', accessExpires: Date.now() + HOUR, refreshToken: 'r2', refreshExpires: Date.now() + 24 * HOUR };
    const msal = (sync as unknown as { msal: { renewInFrame: () => Promise<unknown> } }).msal;
    const renew = vi.spyOn(msal, 'renewInFrame').mockResolvedValue({ account, tokens: fresh });
    sync.start();
    await vi.advanceTimersByTimeAsync(2000);
    expect(renew).toHaveBeenCalledTimes(1);
    const auth = (await backend.getMeta<AuthRecord>('auth'))!;
    expect([auth.frameTimeouts, auth.frameWorks, auth.tokens!.refreshToken]).toEqual([0, true, 'r2']);
  });
});

/**
 * Settings → Take it in anyway: a file set aside as more than this device takes in at once is
 * marked, by the version (tag) set aside, in the sync state every tab reads, for the next cycle.
 */
describe('taking in anyway a file set aside as full', () => {
  it('marks that version in the stored sync state, and nothing else', async () => {
    const backend = new MemoryBackend();
    const meta: SyncMeta = {
      ...emptyMeta('folder'),
      files: {
        'abcd1234.json': { tag: 't7', device: 'abcd1234', name: 'Phone', app: 'Tack Focus', written: ZERO, problem: 'full', reader: 2 },
        'efgh5678.json': { tag: 't8', device: 'efgh5678', name: 'Laptop', app: 'Tack Focus', written: ZERO, reader: 2 },
        'ijkl9012.json': { tag: 't9', device: 'ijkl9012', name: 'Tablet', app: 'Tack Focus', written: ZERO, problem: 'unreadable', reader: 2 },
      },
    };
    await backend.putMeta('sync', { enabled: true, meta });
    const sync = await SyncController.create({} as Workspace, {} as Store, backend, null);
    await sync.takeIn('abcd1234');
    await sync.takeIn('efgh5678');
    await sync.takeIn('ijkl9012');
    await sync.takeIn('mnop3456');
    const stored = (await backend.getMeta<{ enabled: boolean; meta: SyncMeta }>('sync'))!;
    expect(stored.enabled).toBe(true);
    expect(stored.meta.take).toEqual({ 'abcd1234.json': 't7' });
    expect(stored.meta.files).toEqual(meta.files);
  });
});
