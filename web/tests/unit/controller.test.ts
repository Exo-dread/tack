import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Workspace } from '../../src/model/workspace';
import { MemoryBackend } from '../../src/store/db';
import type { Store } from '../../src/store/persist';
import { SyncController } from '../../src/sync/controller';
import { emptyAuth, SCOPES, type AuthRecord } from '../../src/sync/msauth';
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
