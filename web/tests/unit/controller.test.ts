import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Workspace } from '../../src/model/workspace';
import { MemoryBackend } from '../../src/store/db';
import type { Store } from '../../src/store/persist';
import { SyncController } from '../../src/sync/controller';
import { emptyAuth, SCOPES } from '../../src/sync/msauth';

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

  /** Sync on and signed in, as a page that offers Reconnect in Settings. */
  async function signedIn(): Promise<SyncController> {
    const backend = new MemoryBackend();
    await backend.putMeta('sync', { enabled: true, meta: null });
    const tokens = { accessToken: 'a', accessExpires: Date.now() + 3600_000, refreshToken: 'r', refreshExpires: Date.now() + 3600_000 };
    await backend.putMeta('auth', { ...emptyAuth(), account: { username: 'ada@outlook.com', name: 'Ada', tenant: 'consumers', id: 'oid-ada' }, tokens });
    const store = { flushAll: async () => {}, allSaved: true } as unknown as Store;
    return SyncController.create({} as Workspace, store, backend, config);
  }

  beforeEach(() => {
    reload.mockClear();
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
});
