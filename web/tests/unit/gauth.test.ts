import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SyncError } from '../../src/sync/engine';
import { GoogleAuth, takeGoogleReturn } from '../../src/sync/gauth';

/** Browser storage and the address bar, enough for gauth.ts, with what was written to storage. */
function stubPage(search = '', hash = '') {
  const kept = new Map<string, string>();
  const writes: [string, string][] = [];
  const assigned: string[] = [];
  const replaced: string[] = [];
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => kept.get(k) ?? null,
    setItem: (k: string, v: string) => {
      writes.push([k, v]);
      kept.set(k, v);
    },
    removeItem: (k: string) => void kept.delete(k),
  });
  vi.stubGlobal('location', { pathname: '/', search, hash, assign: (url: string) => void assigned.push(url) });
  vi.stubGlobal('history', { replaceState: (_s: unknown, _t: string, url: string) => void replaced.push(url) });
  return { kept, writes, assigned, replaced };
}

/** The Worker's /api/google/token, answering as told; `calls` are the requests it got. */
function worker(answer: () => Response | Promise<Response>) {
  const calls: { url: string; init: RequestInit }[] = [];
  const http: typeof fetch = async (input, init = {}) => {
    calls.push({ url: String(input), init });
    return answer();
  };
  return { http, calls };
}

const granted = (token = 'at-1', expires = 3600) => new Response(JSON.stringify({ access_token: token, expires_in: expires, email: 'ada@gmail.com' }), { status: 200 });

const failure = async (p: Promise<unknown>) => {
  const e = await p.catch((x: unknown) => x);
  expect(e).toBeInstanceOf(SyncError);
  return e as SyncError;
};

describe('the Google access token', () => {
  let page: ReturnType<typeof stubPage>;
  beforeEach(() => {
    page = stubPage();
    vi.useFakeTimers({ toFake: ['Date'] });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('comes from the Worker, same-origin with the custom header, and is kept in memory only', async () => {
    let n = 0;
    const w = worker(() => granted(`at-${++n}`));
    const g = new GoogleAuth(w.http);
    expect(await g.token()).toBe('at-1');
    expect(await g.token()).toBe('at-1');
    expect(w.calls).toHaveLength(1);
    const [{ url, init }] = w.calls as [{ url: string; init: RequestInit }];
    expect(url).toBe('/api/google/token');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('same-origin');
    expect(new Headers(init.headers).get('X-Tack')).toBe('1');
    expect(init.signal).toBeInstanceOf(AbortSignal);
    expect(g.email).toBe('ada@gmail.com');
    // Never written anywhere: not to storage, and never in the address.
    expect(page.writes).toEqual([]);
    expect(page.assigned).toEqual([]);
  });

  it('is asked for again two minutes before it runs out, or when forced', async () => {
    let n = 0;
    const w = worker(() => granted(`at-${++n}`));
    const g = new GoogleAuth(w.http);
    await g.token();
    vi.setSystemTime(Date.now() + 57 * 60_000);
    expect(await g.token()).toBe('at-1');
    vi.setSystemTime(Date.now() + 61_000); // 58 min 1 s: under two minutes left
    expect(await g.token()).toBe('at-2');
    expect(await g.token(true)).toBe('at-3');
    expect(w.calls).toHaveLength(3);
  });

  it('asks once for callers waiting at the same time', async () => {
    const w = worker(() => granted());
    const g = new GoogleAuth(w.http);
    expect(await Promise.all([g.token(), g.token(), g.token()])).toEqual(['at-1', 'at-1', 'at-1']);
    expect(w.calls).toHaveLength(1);
  });

  it('says "auth" when the Worker says signed out or revoked, and forgets the token it had', async () => {
    let answer = granted();
    const w = worker(() => answer);
    const g = new GoogleAuth(w.http);
    await g.token();
    answer = new Response(JSON.stringify({ error: 'revoked' }), { status: 401 });
    expect(await failure(g.token(true))).toMatchObject({ kind: 'auth', message: 'revoked' });
    answer = new Response(JSON.stringify({ error: 'signed_out' }), { status: 401 });
    expect(await failure(g.token())).toMatchObject({ kind: 'auth', message: 'signed_out' });
    expect(g.email).toBe('');
  });

  it('says "throttled" with the Worker’s Retry-After (30 s if none) when it or Google is busy', async () => {
    let answer = new Response('{}', { status: 503, headers: { 'Retry-After': '120' } });
    const g = new GoogleAuth(worker(() => answer).http);
    expect(await failure(g.token())).toMatchObject({ kind: 'throttled', retryAfter: 120_000 });
    answer = new Response('{}', { status: 429 });
    expect(await failure(g.token())).toMatchObject({ kind: 'throttled', retryAfter: 30_000 });
  });

  it('says "offline" when the Worker can’t be reached, and "server" for an answer it can’t use', async () => {
    const g = new GoogleAuth(async () => {
      throw new TypeError('Failed to fetch');
    });
    expect(await failure(g.token())).toMatchObject({ kind: 'offline' });
    const odd = new GoogleAuth(async () => new Response(JSON.stringify({ access_token: 42 }), { status: 200 }));
    expect(await failure(odd.token())).toMatchObject({ kind: 'server' });
  });

  it('signs out at the Worker, and forgets the token', async () => {
    const w = worker(() => granted());
    const g = new GoogleAuth(w.http);
    await g.token();
    await g.signOut();
    expect(w.calls[1]!.url).toBe('/api/google/signout');
    expect(w.calls[1]!.init.method).toBe('POST');
    expect(new Headers(w.calls[1]!.init.headers).get('X-Tack')).toBe('1');
    await g.token();
    expect(w.calls).toHaveLength(3);
    // Offline: nothing to do but forget.
    await new GoogleAuth(async () => {
      throw new TypeError('Failed to fetch');
    }).signOut();
  });

  it('connects by going to the Worker’s start, noting that this browser set off', async () => {
    const g = new GoogleAuth(worker(() => granted()).http);
    void g.connect();
    expect(page.assigned).toEqual(['/api/google/start?return=/']);
    expect(page.kept.has('tack.google.connect')).toBe(true);
  });
});

describe('back from connecting Google Drive', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  const setOff = (page: ReturnType<typeof stubPage>, ago = 1000) => page.kept.set('tack.google.connect', String(Date.now() - ago));

  it('reads ?google=connected and takes it out of the address bar, keeping the rest', () => {
    const page = stubPage('?google=connected&x=1', '#top');
    setOff(page);
    expect(takeGoogleReturn()).toEqual({ connected: true });
    expect(page.replaced).toEqual(['/?x=1#top']);
    expect(page.kept.has('tack.google.connect')).toBe(false); // used once
  });

  it('reads an error and its reason; a reason it doesn’t know is just "failed"', () => {
    let page = stubPage('?google=error&reason=scope');
    setOff(page);
    expect(takeGoogleReturn()).toEqual({ connected: false, reason: 'scope' });
    expect(page.replaced).toEqual(['/']);
    page = stubPage('?google=error&reason=%3Cscript%3E');
    setOff(page);
    expect(takeGoogleReturn()).toEqual({ connected: false, reason: 'failed' });
  });

  it('ignores (but still clears) a return this browser didn’t set off on, or set off on long ago', () => {
    let page = stubPage('?google=connected');
    expect(takeGoogleReturn()).toBeNull();
    expect(page.replaced).toEqual(['/']);
    page = stubPage('?google=connected');
    setOff(page, 11 * 60_000);
    expect(takeGoogleReturn()).toBeNull();
  });

  it('leaves an address without ?google= alone', () => {
    const page = stubPage('?share&text=hi');
    expect(takeGoogleReturn()).toBeNull();
    expect(page.replaced).toEqual([]);
  });
});
