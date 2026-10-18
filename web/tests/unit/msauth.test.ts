import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SyncError } from '../../src/sync/engine';
import { AuthError, decodeJwt, MicrosoftAuth, SCOPES, type Tokens } from '../../src/sync/msauth';
import { AUTHORITY, CONSUMERS, FakeMicrosoft } from '../fakes/microsoft';

/** Just enough of a browser page: an address, history, localStorage, and "going" somewhere. */
function fakePage() {
  const store = new Map<string, string>();
  const page = {
    assigned: [] as string[],
    location: {
      origin: 'https://tackfocus.app',
      pathname: '/',
      search: '',
      hash: '',
      assign(url: string) {
        page.assigned.push(url);
      },
    },
    history: {
      replaceState(_s: unknown, _t: string, url: string) {
        page.location.hash = url.includes('#') ? url.slice(url.indexOf('#')) : '';
      },
    },
    localStorage: {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => void store.set(k, v),
      removeItem: (k: string) => void store.delete(k),
    },
  };
  return page;
}

describe('signing in to Microsoft', () => {
  let ms: FakeMicrosoft;
  let page: ReturnType<typeof fakePage>;
  let auth: MicrosoftAuth;
  const saved: Record<string, unknown> = {};

  beforeEach(() => {
    ms = new FakeMicrosoft();
    page = fakePage();
    for (const k of ['location', 'history', 'localStorage'] as const) {
      saved[k] = (globalThis as Record<string, unknown>)[k];
      Object.defineProperty(globalThis, k, { value: page[k], configurable: true, writable: true });
    }
    auth = new MicrosoftAuth(
      { clientId: ms.clientId, authority: AUTHORITY, redirectUri: 'https://tackfocus.app/', silentRedirectUri: 'https://tackfocus.app/auth/', scopes: SCOPES },
      ms.fetch,
    );
  });

  afterEach(() => {
    for (const k of ['location', 'history', 'localStorage'] as const) {
      Object.defineProperty(globalThis, k, { value: saved[k], configurable: true, writable: true });
    }
  });

  /** Follows the last redirect to Microsoft and back, as the browser would. */
  function goThrough(): void {
    const url = page.assigned.at(-1)!;
    const r = ms.handle({ method: 'GET', url, headers: {}, body: null }, false);
    if (r === 'abort') throw new Error('abort');
    page.location.hash = r.headers.location!.slice(r.headers.location!.indexOf('#'));
  }

  it('asks for the narrowest scopes, with PKCE, and comes back signed in', async () => {
    void auth.signIn({ loginHint: 'ada@outlook.com' });
    await new Promise((r) => setTimeout(r, 0));
    const url = new URL(page.assigned[0]!);
    expect(url.origin + url.pathname).toBe(`${AUTHORITY}/oauth2/v2.0/authorize`);
    expect(url.searchParams.get('scope')).toBe('openid profile offline_access https://graph.microsoft.com/Files.ReadWrite.AppFolder');
    expect(url.searchParams.get('code_challenge_method')).toBe('S256');
    expect(url.searchParams.get('prompt')).toBe('select_account');
    expect(url.searchParams.get('response_mode')).toBe('fragment');
    expect(url.searchParams.get('login_hint')).toBe('ada@outlook.com');
    goThrough();
    const result = await auth.completeRedirect();
    expect(page.location.hash).toBe(''); // the code is out of the address bar
    expect(result && 'account' in result ? result.account : null).toEqual({ username: 'ada@outlook.com', name: 'Ada Lovelace', tenant: CONSUMERS, id: 'oid-ada' });
    const tokens = (result as { tokens: Tokens }).tokens;
    expect(tokens.refreshExpires - Date.now()).toBeGreaterThan(23.9 * 3600_000);
  });

  it('ignores an address that only looks like a sign-in return', async () => {
    page.location.hash = '#code=stolen&state=whatever';
    expect(await auth.completeRedirect()).toBeNull();
  });

  it('says so when you cancel', async () => {
    ms.userCancels = true;
    void auth.signIn();
    await new Promise((r) => setTimeout(r, 0));
    goThrough();
    const result = await auth.completeRedirect();
    expect(result && 'error' in result ? result.error.kind : null).toBe('cancelled');
  });

  it('renews within the day, but never past 24 hours from sign-in', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(Date.UTC(2026, 9, 18, 8));
    void auth.signIn();
    await Promise.resolve();
    await vi.waitFor(() => expect(page.assigned.length).toBe(1));
    goThrough();
    const first = (await auth.completeRedirect()) as { tokens: Tokens };
    vi.setSystemTime(Date.now() + 20 * 3600_000);
    const renewed = await auth.refresh(first.tokens);
    expect(renewed.accessToken).not.toBe(first.tokens.accessToken);
    expect(renewed.refreshExpires).toBe(first.tokens.refreshExpires);
    vi.setSystemTime(Date.now() + 5 * 3600_000);
    const now = Date.now();
    // The app knows the limit and doesn't even ask...
    await expect(auth.refresh({ ...renewed, refreshExpires: now + 1000 })).rejects.toBeInstanceOf(SyncError);
    // ...and Microsoft refuses anyway.
    const error = await auth.refresh({ ...renewed, refreshExpires: now + 3600_000 }).catch((e) => e);
    expect(error).toBeInstanceOf(SyncError);
    expect((error as SyncError).kind).toBe('auth');
    vi.useRealTimers();
  });

  it('renews without a sign-in form by visiting Microsoft, and reports when it needs you', async () => {
    const account = { username: 'ada@outlook.com', name: 'Ada', tenant: CONSUMERS, id: 'oid-ada' };
    void auth.renewByVisit(account);
    await new Promise((r) => setTimeout(r, 0));
    const url = new URL(page.assigned[0]!);
    expect(url.searchParams.get('prompt')).toBe('none');
    expect(url.searchParams.get('login_hint')).toBe('ada@outlook.com');
    expect(url.searchParams.get('domain_hint')).toBe('consumers');
    goThrough();
    const ok = await auth.completeRedirect();
    expect(ok && 'tokens' in ok && ok.kind).toBe('renew');

    ms.account = null; // signed out of Microsoft meanwhile
    void auth.renewByVisit(account);
    await new Promise((r) => setTimeout(r, 0));
    goThrough();
    const failed = await auth.completeRedirect();
    expect(failed && 'error' in failed ? [failed.kind, failed.error.kind] : null).toEqual(['renew', 'interaction']);
  });

  it('reads the ID token claims', () => {
    const jwt = `x.${Buffer.from(JSON.stringify({ name: 'Zoë', preferred_username: 'z@example.com' })).toString('base64url')}.`;
    expect(decodeJwt(jwt)).toEqual({ name: 'Zoë', preferred_username: 'z@example.com' });
    expect(decodeJwt('nonsense')).toBeNull();
    expect(new AuthError('failed', 'x').kind).toBe('failed');
  });
});
