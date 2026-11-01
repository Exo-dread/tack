import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { makeHandler, safeReturn, type Env } from '../../worker/google';

// The sign-in Worker (web/worker/google.ts), called directly with Requests and a fake Google.

const SITE = 'https://tackfocus.app';
const BASE = `${SITE}/api/google/`;
const CLIENT_ID = 'test-client.apps.googleusercontent.com';
const SECRET = 'test-client-secret-XYZ';
const KEY_BYTES = new Uint8Array(32).map((_, i) => i * 7 + 1);
const SESSION_KEY = btoa(String.fromCharCode(...KEY_BYTES));
const DRIVE_FILE = 'https://www.googleapis.com/auth/drive.file';
const RT = 'refresh-token-1//secret-ish';
const AT = 'ya29.access-token-1';
const CODE = 'auth-code-4/0AbCd';
const EMAIL = 'tester@example.com';

const env = (over: Partial<Env> = {}): Env => ({
  GOOGLE_CLIENT_ID: CLIENT_ID,
  GOOGLE_CLIENT_SECRET: SECRET,
  SESSION_KEY,
  SITE_ORIGIN: SITE,
  ...over,
});

function b64url(bytes: Uint8Array): string {
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function idToken(claims: Record<string, unknown>): string {
  const part = (o: unknown) => b64url(new TextEncoder().encode(JSON.stringify(o)));
  return `${part({ alg: 'RS256' })}.${part(claims)}.sig`;
}

/** An independent AES-GCM sealer with the test key, to forge cookies the way the contract says they're made. */
async function seal(aad: string, value: unknown, keyBytes = KEY_BYTES): Promise<string> {
  const key = await crypto.subtle.importKey('raw', keyBytes, 'AES-GCM', false, ['encrypt']);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(
    await crypto.subtle.encrypt(
      { name: 'AES-GCM', iv, additionalData: new TextEncoder().encode(aad) },
      key,
      new TextEncoder().encode(JSON.stringify(value)),
    ),
  );
  const out = new Uint8Array(12 + ct.length);
  out.set(iv);
  out.set(ct, 12);
  return b64url(out);
}

async function unseal(aad: string, value: string): Promise<Record<string, unknown>> {
  const bytes = Uint8Array.from(atob(value.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
  const key = await crypto.subtle.importKey('raw', KEY_BYTES, 'AES-GCM', false, ['decrypt']);
  const plain = await crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: bytes.slice(0, 12), additionalData: new TextEncoder().encode(aad) },
    key,
    bytes.slice(12),
  );
  return JSON.parse(new TextDecoder().decode(plain));
}

interface Call {
  url: string;
  form: URLSearchParams;
  signal: AbortSignal | null | undefined;
}

type Answer = Response | (() => Response | Promise<Response>) | 'network' | 'hang';

/** Google's token and revoke endpoints. Answers are queued per URL; the default is a happy Google. */
class FakeGoogle {
  calls: Call[] = [];
  queue = new Map<string, Answer[]>();
  tokenScope = `openid ${DRIVE_FILE} https://www.googleapis.com/auth/userinfo.email`;
  refreshToken: string | undefined = RT;

  answer(url: string, a: Answer): void {
    const q = this.queue.get(url) ?? [];
    q.push(a);
    this.queue.set(url, q);
  }

  fetch = async (input: string, init: RequestInit): Promise<Response> => {
    const form = new URLSearchParams(String(init.body ?? ''));
    this.calls.push({ url: input, form, signal: init.signal });
    const a = this.queue.get(input)?.shift();
    if (a === 'network') throw new TypeError('fetch failed');
    if (a === 'hang') {
      return new Promise((_, reject) => {
        init.signal?.addEventListener('abort', () => reject(init.signal?.reason));
      });
    }
    if (a) return typeof a === 'function' ? a() : a;
    if (input === 'https://oauth2.googleapis.com/revoke') return new Response('', { status: 200 });
    if (input === 'https://oauth2.googleapis.com/token') {
      if (form.get('grant_type') === 'authorization_code') {
        return Response.json({
          access_token: AT,
          expires_in: 3599,
          scope: this.tokenScope,
          token_type: 'Bearer',
          id_token: idToken({ iss: 'https://accounts.google.com', aud: CLIENT_ID, sub: '1234567890', email: EMAIL }),
          ...(this.refreshToken ? { refresh_token: this.refreshToken } : {}),
        });
      }
      return Response.json({ access_token: 'ya29.access-token-2', expires_in: 3599, scope: this.tokenScope });
    }
    return new Response('not found', { status: 404 });
  };

  of(url: string): Call[] {
    return this.calls.filter((c) => c.url === url);
  }
}

const TOKEN = 'https://oauth2.googleapis.com/token';
const REVOKE = 'https://oauth2.googleapis.com/revoke';

/** Set-Cookie values from a response, by name. */
function cookies(r: Response): Map<string, string> {
  const out = new Map<string, string>();
  for (const c of r.headers.getSetCookie()) out.set(c.slice(0, c.indexOf('=')), c);
  return out;
}

function cookieValue(setCookie: string): string {
  return setCookie.slice(setCookie.indexOf('=') + 1, setCookie.indexOf(';'));
}

function get(path: string, cookie?: string, headers: Record<string, string> = {}): Request {
  return new Request(BASE + path, { headers: { ...(cookie ? { Cookie: cookie } : {}), ...headers } });
}

function post(path: string, cookie?: string, headers: Record<string, string> = { 'X-Tack': '1', Origin: SITE }): Request {
  return new Request(BASE + path, { method: 'POST', headers: { ...(cookie ? { Cookie: cookie } : {}), ...headers } });
}

function expectBaseHeaders(r: Response): void {
  expect(r.headers.get('Cache-Control')).toBe('no-store');
  expect(r.headers.get('X-Content-Type-Options')).toBe('nosniff');
  expect(r.headers.get('Referrer-Policy')).toBe('no-referrer');
  expect(r.headers.get('Access-Control-Allow-Origin')).toBeNull();
}

/** Everything a response shows besides its Set-Cookie headers. */
async function visible(r: Response): Promise<string> {
  const headers = [...r.headers].filter(([k]) => k.toLowerCase() !== 'set-cookie');
  return JSON.stringify(headers) + (await r.clone().text());
}

describe('the Google sign-in Worker', () => {
  let google: FakeGoogle;
  let handler: ReturnType<typeof makeHandler>;
  let clock: number;
  let warn: ReturnType<typeof vi.spyOn>;
  let log: ReturnType<typeof vi.spyOn>;
  let error: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    google = new FakeGoogle();
    clock = Date.UTC(2026, 10, 1, 12);
    handler = makeHandler(google.fetch, { timeoutMs: 50, now: () => clock });
    warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    log = vi.spyOn(console, 'log').mockImplementation(() => {});
    error = vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    // Nothing secret is ever logged: only error codes.
    const logged = JSON.stringify([...warn.mock.calls, ...log.mock.calls, ...error.mock.calls]);
    for (const secret of [RT, AT, CODE, SECRET, SESSION_KEY, EMAIL, 'refresh-token-2', 'ya29.access-token-2']) {
      expect(logged).not.toContain(secret);
    }
    expect(log).not.toHaveBeenCalled();
    vi.restoreAllMocks();
  });

  const call = (req: Request, e: Env = env()) => handler.fetch(req, e);

  /** start → Google → callback; returns the callback's response and the session cookie pair. */
  async function signIn(ret = '/', e: Env = env()) {
    const s = await call(get(`start?return=${encodeURIComponent(ret)}`), e);
    expect(s.status).toBe(302);
    const flowSet = cookies(s).get('__Secure-tack-gflow')!;
    const state = new URL(s.headers.get('Location')!).searchParams.get('state')!;
    const cb = await call(get(`callback?code=${encodeURIComponent(CODE)}&state=${state}`, `__Secure-tack-gflow=${cookieValue(flowSet)}`), e);
    const sessionSet = cookies(cb).get('__Secure-tack-g');
    return { start: s, callback: cb, flowSet, state, session: sessionSet ? `__Secure-tack-g=${cookieValue(sessionSet)}` : '' };
  }

  it('signs in: start, callback, token', async () => {
    const { start, callback, flowSet, session } = await signIn('/');
    expectBaseHeaders(start);
    const to = new URL(start.headers.get('Location')!);
    expect(to.origin + to.pathname).toBe('https://accounts.google.com/o/oauth2/v2/auth');
    const p = to.searchParams;
    expect(p.get('client_id')).toBe(CLIENT_ID);
    expect(p.get('redirect_uri')).toBe(`${SITE}/api/google/callback`);
    expect(p.get('response_type')).toBe('code');
    expect(p.get('scope')).toBe(`openid email ${DRIVE_FILE}`);
    expect(p.get('access_type')).toBe('offline');
    expect(p.get('prompt')).toBe('consent');
    expect(p.get('code_challenge_method')).toBe('S256');
    expect(p.get('include_granted_scopes')).toBeNull();
    expect(flowSet).toMatch(/; Max-Age=600; Path=\/api\/google\/; HttpOnly; Secure; SameSite=Lax$/);

    // The verifier sent at exchange matches the challenge sent to Google.
    const ex = google.of(TOKEN)[0]!;
    expect(ex.form.get('grant_type')).toBe('authorization_code');
    expect(ex.form.get('code')).toBe(CODE);
    expect(ex.form.get('client_secret')).toBe(SECRET);
    expect(ex.form.get('redirect_uri')).toBe(`${SITE}/api/google/callback`);
    const verifier = ex.form.get('code_verifier')!;
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
    expect(b64url(digest)).toBe(p.get('code_challenge'));
    expect(ex.signal).toBeInstanceOf(AbortSignal);

    expect(callback.status).toBe(302);
    expect(callback.headers.get('Location')).toBe('/?google=connected');
    expectBaseHeaders(callback);
    const set = cookies(callback);
    expect(set.get('__Secure-tack-gflow')).toMatch(/^__Secure-tack-gflow=; Max-Age=0;/);
    const sessionSet = set.get('__Secure-tack-g')!;
    expect(sessionSet).toMatch(/; Max-Age=15552000; Path=\/api\/google\/; HttpOnly; Secure; SameSite=Strict$/);
    const payload = await unseal('__Secure-tack-g', cookieValue(sessionSet));
    expect(payload).toEqual({ v: 1, rt: RT, email: EMAIL, sub: '1234567890', iat: Math.floor(clock / 1000) });

    const t = await call(post('token', session));
    expect(t.status).toBe(200);
    expect(t.headers.get('Content-Type')).toBe('application/json');
    expectBaseHeaders(t);
    expect(await t.json()).toEqual({ access_token: 'ya29.access-token-2', expires_in: 3599, email: EMAIL });
    expect(t.headers.getSetCookie()).toEqual([]);
    const rf = google.of(TOKEN)[1]!;
    expect(rf.form.get('grant_type')).toBe('refresh_token');
    expect(rf.form.get('refresh_token')).toBe(RT);
    expect(rf.form.get('client_id')).toBe(CLIENT_ID);
    expect(rf.form.get('client_secret')).toBe(SECRET);
  });

  it('never shows the refresh token, client secret or session key outside the session cookie', async () => {
    const { start, callback, session } = await signIn('/');
    const t = await call(post('token', session));
    const s = await call(post('signout', session));
    for (const r of [start, callback, t, s]) {
      const shown = await visible(r);
      for (const secret of [RT, SECRET, SESSION_KEY, CODE]) expect(shown).not.toContain(secret);
    }
    // And not readable in the cookie either: it's encrypted.
    expect(session).not.toContain(RT);
  });

  it('keeps the return path, adding google=connected', async () => {
    expect((await signIn('/tasks')).callback.headers.get('Location')).toBe('/tasks?google=connected');
    expect((await signIn('/?sheet=2')).callback.headers.get('Location')).toBe('/?sheet=2&google=connected');
  });

  it('turns any other return into /', async () => {
    for (const bad of ['//evil.com', '/\\evil', 'https://evil', 'evil', '/a\\b', '/' + 'a'.repeat(200), '/a\nb', '/a\tb', '/a\x00', '/a b', '/a#x', '/é']) {
      expect(safeReturn(bad)).toBe('/');
      expect((await signIn(bad)).callback.headers.get('Location')).toBe('/?google=connected');
    }
    expect(safeReturn('/' + 'a'.repeat(199))).toBe('/' + 'a'.repeat(199));
    expect(safeReturn(null)).toBe('/');
  });

  it('refuses a callback whose state does not match the flow cookie', async () => {
    const s = await call(get('start?return=/'));
    const flow = cookieValue(cookies(s).get('__Secure-tack-gflow')!);
    const r = await call(get(`callback?code=${CODE}&state=wrong`, `__Secure-tack-gflow=${flow}`));
    expect(r.status).toBe(302);
    expect(r.headers.get('Location')).toBe('/?google=error&reason=state');
    expect(cookies(r).get('__Secure-tack-gflow')).toMatch(/Max-Age=0/);
    expect(cookies(r).has('__Secure-tack-g')).toBe(false);
    expect(google.calls).toEqual([]);
  });

  it('refuses a callback with no flow cookie, a stale one, or a forged one', async () => {
    const s = await call(get('start'));
    const state = new URL(s.headers.get('Location')!).searchParams.get('state')!;
    const flow = cookieValue(cookies(s).get('__Secure-tack-gflow')!);
    const nowS = Math.floor(clock / 1000);
    const forgedKey = new Uint8Array(32).fill(9);
    for (const cookie of [
      undefined,
      `__Secure-tack-gflow=${flow.slice(0, 30)}${flow[30] === 'A' ? 'B' : 'A'}${flow.slice(31)}`,
      `__Secure-tack-gflow=${await seal('__Secure-tack-g', { v: 1, state, verifier: 'x'.repeat(43), ret: '/', iat: nowS })}`, // sealed as the session cookie
      `__Secure-tack-gflow=${await seal('__Secure-tack-gflow', { v: 1, state, verifier: 'x'.repeat(43), ret: '/', iat: nowS }, forgedKey)}`,
      `__Secure-tack-gflow=${await seal('__Secure-tack-gflow', { v: 1, state, verifier: 'x'.repeat(43), ret: '/', iat: nowS - 601 })}`,
      `__Secure-tack-gflow=${await seal('__Secure-tack-gflow', { v: 2, state, verifier: 'x'.repeat(43), ret: '/', iat: nowS })}`,
    ]) {
      const r = await call(get(`callback?code=${CODE}&state=${state}`, cookie));
      expect(r.headers.get('Location')).toBe('/?google=error&reason=state');
    }
    expect(google.calls).toEqual([]);
  });

  it('says denied when the user says no', async () => {
    const r = await call(get('callback?error=access_denied&state=x'));
    expect(r.headers.get('Location')).toBe('/?google=error&reason=denied');
  });

  it('refuses broader scopes and revokes what Google gave', async () => {
    google.tokenScope = `openid ${DRIVE_FILE} https://www.googleapis.com/auth/drive`;
    const { callback } = await signIn();
    expect(callback.headers.get('Location')).toBe('/?google=error&reason=scope');
    expect(cookies(callback).has('__Secure-tack-g')).toBe(false);
    const rv = google.of(REVOKE);
    expect(rv).toHaveLength(1);
    expect(rv[0]!.form.get('token')).toBe(RT);
  });

  it('refuses a grant without drive.file', async () => {
    google.tokenScope = 'openid email';
    const { callback } = await signIn();
    expect(callback.headers.get('Location')).toBe('/?google=error&reason=scope');
    expect(google.of(REVOKE)).toHaveLength(1);
  });

  it('needs a refresh token', async () => {
    google.refreshToken = undefined;
    const { callback } = await signIn();
    expect(callback.headers.get('Location')).toBe('/?google=error&reason=exchange');
    expect(cookies(callback).has('__Secure-tack-g')).toBe(false);
  });

  it('refuses an id_token for another client', async () => {
    google.answer(TOKEN, () =>
      Response.json({
        access_token: AT,
        expires_in: 3599,
        refresh_token: RT,
        scope: google.tokenScope,
        id_token: idToken({ aud: 'someone-else', sub: '1', email: EMAIL }),
      }),
    );
    const { callback } = await signIn();
    expect(callback.headers.get('Location')).toBe('/?google=error&reason=exchange');
  });

  it('says exchange when Google throttles, fails or hangs at the exchange', async () => {
    for (const a of [
      new Response('{}', { status: 429, headers: { 'Retry-After': '120' } }),
      new Response('{}', { status: 503 }),
      new Response('{"error":"invalid_grant"}', { status: 400 }),
      'network' as const,
      'hang' as const,
    ]) {
      google.answer(TOKEN, a);
      const { callback } = await signIn();
      expect(callback.headers.get('Location')).toBe('/?google=error&reason=exchange');
      expect(cookies(callback).has('__Secure-tack-g')).toBe(false);
    }
  });

  it('says config when settings are missing', async () => {
    for (const e of [
      env({ GOOGLE_CLIENT_ID: '' }),
      env({ GOOGLE_CLIENT_SECRET: undefined }),
      env({ SESSION_KEY: undefined }),
      env({ SESSION_KEY: btoa('too short') }),
    ]) {
      const s = await call(get('start'), e);
      expect(s.headers.get('Location')).toBe('/?google=error&reason=config');
      const c = await call(get('callback?code=x&state=y'), e);
      expect(c.headers.get('Location')).toBe('/?google=error&reason=config');
      const t = await call(post('token', '__Secure-tack-g=abc'), e);
      expect(t.status).toBe(503);
      expect(await t.json()).toEqual({ error: 'config' });
    }
    expect(google.calls).toEqual([]);
  });

  describe('token', () => {
    let session: string;
    beforeEach(async () => {
      session = (await signIn()).session;
      google.calls = [];
    });

    it('is signed_out with no cookie, a tampered one, or one from another key', async () => {
      const value = session.slice('__Secure-tack-g='.length);
      const flipped = value.slice(0, 20) + (value[20] === 'A' ? 'B' : 'A') + value.slice(21);
      for (const cookie of [
        undefined,
        '__Secure-tack-g=',
        `__Secure-tack-g=${flipped}`,
        `__Secure-tack-g=${value.slice(0, 30)}`,
        '__Secure-tack-g=not*base64',
        `__Secure-tack-g=${await seal('__Secure-tack-g', { v: 1, rt: RT, email: EMAIL, sub: '1', iat: Math.floor(clock / 1000) }, new Uint8Array(32).fill(3))}`,
        `__Secure-tack-g=${'A'.repeat(5000)}`,
      ]) {
        const r = await call(post('token', cookie));
        expect(r.status).toBe(401);
        expect(await r.json()).toEqual({ error: 'signed_out' });
        expect(cookies(r).get('__Secure-tack-g')).toMatch(/^__Secure-tack-g=; Max-Age=0; Path=\/api\/google\/;/);
      }
      expect(google.calls).toEqual([]);
    });

    it('is signed_out for a wrong version, a bad shape, or a session older than 180 days', async () => {
      const nowS = Math.floor(clock / 1000);
      const ok = { v: 1, rt: RT, email: EMAIL, sub: '1', iat: nowS };
      for (const payload of [
        { ...ok, v: 2 },
        { ...ok, v: '1' },
        { ...ok, rt: '' },
        { ...ok, rt: 5 },
        { ...ok, email: undefined },
        { ...ok, iat: 'now' },
        { ...ok, iat: nowS - 15_552_000 },
        { ...ok, iat: nowS + 3600 },
        [ok],
      ]) {
        const r = await call(post('token', `__Secure-tack-g=${await seal('__Secure-tack-g', payload)}`));
        expect(r.status).toBe(401);
        expect(await r.json()).toEqual({ error: 'signed_out' });
      }
      // A day short of 180 still works, independently sealed: the format is as the contract says.
      const r = await call(post('token', `__Secure-tack-g=${await seal('__Secure-tack-g', { ...ok, iat: nowS - 15_552_000 + 86_400 })}`));
      expect(r.status).toBe(200);
    });

    it('expires the real session after 180 days', async () => {
      clock += 15_552_000 * 1000;
      const r = await call(post('token', session));
      expect(r.status).toBe(401);
      expect(await r.json()).toEqual({ error: 'signed_out' });
    });

    it('does not take the flow cookie as a session (the cookie name is authenticated)', async () => {
      const s = await call(get('start'));
      const flow = cookieValue(cookies(s).get('__Secure-tack-gflow')!);
      const r1 = await call(post('token', `__Secure-tack-g=${flow}`));
      expect(r1.status).toBe(401);
      // Even a perfect session payload, sealed under the flow cookie's name, is refused.
      const forged = await seal('__Secure-tack-gflow', { v: 1, rt: RT, email: EMAIL, sub: '1', iat: Math.floor(clock / 1000) });
      const r2 = await call(post('token', `__Secure-tack-g=${forged}`));
      expect(r2.status).toBe(401);
      expect(google.calls).toEqual([]);
    });

    it('says revoked and clears the cookie on invalid_grant', async () => {
      google.answer(TOKEN, new Response('{"error":"invalid_grant","error_description":"Token has been expired or revoked."}', { status: 400 }));
      const r = await call(post('token', session));
      expect(r.status).toBe(401);
      expect(await r.json()).toEqual({ error: 'revoked' });
      expect(cookies(r).get('__Secure-tack-g')).toMatch(/^__Secure-tack-g=; Max-Age=0;/);
    });

    it('answers 503 with Google\'s Retry-After when Google throttles (429)', async () => {
      google.answer(TOKEN, new Response('{}', { status: 429, headers: { 'Retry-After': '120' } }));
      const r = await call(post('token', session));
      expect(r.status).toBe(503);
      expect(r.headers.get('Retry-After')).toBe('120');
      expect(cookies(r).has('__Secure-tack-g')).toBe(false); // the session stays
    });

    it('answers 503 on Google 503, with a sane Retry-After', async () => {
      const cases: [Record<string, string>, string][] = [
        [{}, '30'],
        [{ 'Retry-After': '7' }, '7'],
        [{ 'Retry-After': '999999' }, '21600'],
        [{ 'Retry-After': 'soon' }, '30'],
        [{ 'Retry-After': '0' }, '30'],
        [{ 'Retry-After': new Date(clock + 90_000).toUTCString() }, '90'],
      ];
      for (const [headers, expected] of cases) {
        google.answer(TOKEN, new Response('', { status: 503, headers }));
        const r = await call(post('token', session));
        expect(r.status).toBe(503);
        expect(r.headers.get('Retry-After')).toBe(expected);
      }
      google.answer(TOKEN, new Response('', { status: 500 }));
      expect((await call(post('token', session))).status).toBe(503);
    });

    it('answers 503 with Retry-After on a timeout or network failure', async () => {
      for (const a of ['hang', 'network'] as const) {
        google.answer(TOKEN, a);
        const r = await call(post('token', session));
        expect(r.status).toBe(503);
        expect(r.headers.get('Retry-After')).toBe('30');
      }
      const call0 = google.of(TOKEN)[0]!;
      expect(call0.signal?.aborted).toBe(true);
    });

    it('answers 503 when Google answers 200 without a token', async () => {
      google.answer(TOKEN, Response.json({ expires_in: 3599 }));
      expect((await call(post('token', session))).status).toBe(503);
    });

    it('keeps a new refresh token in the cookie, with the same sign-in time', async () => {
      const before = await unseal('__Secure-tack-g', session.slice('__Secure-tack-g='.length));
      clock += 86_400_000;
      google.answer(TOKEN, Response.json({ access_token: 'ya29.access-token-2', expires_in: 3599, refresh_token: 'refresh-token-2' }));
      const r = await call(post('token', session));
      expect(r.status).toBe(200);
      expect(await visible(r)).not.toContain('refresh-token-2');
      const set = cookies(r).get('__Secure-tack-g')!;
      expect(set).toMatch(/; Max-Age=15552000; Path=\/api\/google\/; HttpOnly; Secure; SameSite=Strict$/);
      const after = await unseal('__Secure-tack-g', cookieValue(set));
      expect(after).toEqual({ ...before, rt: 'refresh-token-2' });
      // And it works.
      const r2 = await call(post('token', `__Secure-tack-g=${cookieValue(set)}`));
      expect(r2.status).toBe(200);
      expect(google.of(TOKEN)[1]!.form.get('refresh_token')).toBe('refresh-token-2');
    });

    it('refuses cross-site and header-less requests (CSRF)', async () => {
      const cases: [Request, number][] = [
        [post('token', session, { Origin: SITE }), 403], // no X-Tack
        [post('token', session, { 'X-Tack': '2', Origin: SITE }), 403],
        [post('token', session, { 'X-Tack': '1', Origin: 'https://evil.example' }), 403],
        [post('token', session, { 'X-Tack': '1', Origin: 'null' }), 403],
        [post('token', session, { 'X-Tack': '1', Origin: 'https://tackfocus.app.evil.example' }), 403],
        [post('token', session, { 'X-Tack': '1', 'Sec-Fetch-Site': 'cross-site' }), 403],
        [post('token', session, { 'X-Tack': '1', 'Sec-Fetch-Site': 'same-site' }), 403],
        [post('token', session, { 'X-Tack': '1' }), 403],
        [get('token', session, { 'X-Tack': '1', Origin: SITE }), 405],
        [new Request(BASE + 'token', { method: 'OPTIONS', headers: { Origin: SITE, 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'x-tack' } }), 405],
        [new Request(BASE + 'token', { method: 'PUT', headers: { 'X-Tack': '1', Origin: SITE, Cookie: session } }), 405],
      ];
      for (const [req, status] of cases) {
        const r = await call(req);
        expect(r.status).toBe(status);
        expectBaseHeaders(r);
        expect(r.headers.get('Access-Control-Allow-Headers')).toBeNull();
        expect(r.headers.get('Access-Control-Allow-Methods')).toBeNull();
      }
      expect(google.calls).toEqual([]);
      // Same origin without an Origin header is fine.
      const ok = await call(post('token', session, { 'X-Tack': '1', 'Sec-Fetch-Site': 'same-origin' }));
      expect(ok.status).toBe(200);
    });
  });

  describe('signout', () => {
    it('clears this browser’s cookie and asks Google nothing (a revoke would sign out your other browsers too)', async () => {
      const { session } = await signIn();
      google.calls = [];
      const r = await call(post('signout', session));
      expect(r.status).toBe(204);
      expectBaseHeaders(r);
      expect(cookies(r).get('__Secure-tack-g')).toMatch(/^__Secure-tack-g=; Max-Age=0; Path=\/api\/google\/; HttpOnly; Secure; SameSite=Strict$/);
      expect(google.calls).toEqual([]);
    });

    it('answers 204 with no session, without calling Google', async () => {
      const r = await call(post('signout'));
      expect(r.status).toBe(204);
      expect(google.calls).toEqual([]);
    });

    it('checks CSRF', async () => {
      const { session } = await signIn();
      google.calls = [];
      expect((await call(post('signout', session, { Origin: 'https://evil.example', 'X-Tack': '1' }))).status).toBe(403);
      expect((await call(post('signout', session, { Origin: SITE }))).status).toBe(403);
      expect((await call(get('signout', session))).status).toBe(405);
      expect(google.calls).toEqual([]);
    });
  });

  it('revoke on a refused scope survives Google throttling it', async () => {
    google.tokenScope = `${DRIVE_FILE} https://www.googleapis.com/auth/gmail.readonly`;
    google.answer(REVOKE, new Response('', { status: 429, headers: { 'Retry-After': '60' } }));
    const { callback } = await signIn();
    expect(callback.headers.get('Location')).toBe('/?google=error&reason=scope');
  });

  it('answers 404 for other paths and 405 for other methods', async () => {
    for (const path of ['', 'tokens', 'start/x', '../token', 'constructor', '__proto__', 'hasOwnProperty']) {
      const r = await call(new Request(BASE + path, { method: 'POST', headers: { 'X-Tack': '1', Origin: SITE } }));
      expect(r.status).toBe(404);
      expectBaseHeaders(r);
      expect(r.headers.get('Content-Type')).toBe('application/json');
    }
    expect((await call(new Request(`${SITE}/api/googlex/token`, { method: 'POST' }))).status).toBe(404);
    expect((await call(new Request(BASE + 'start', { method: 'POST' }))).status).toBe(405);
    expect((await call(new Request(BASE + 'callback', { method: 'OPTIONS' }))).status).toBe(405);
  });

  it('rate-limits per IP and path: 429 with Retry-After: 60', async () => {
    const from = (ip: string) => post('signout', undefined, { 'X-Tack': '1', Origin: SITE, 'CF-Connecting-IP': ip });
    for (let i = 0; i < 30; i++) expect((await call(from('203.0.113.1'))).status).toBe(204);
    const r = await call(from('203.0.113.1'));
    expect(r.status).toBe(429);
    expect(r.headers.get('Retry-After')).toBe('60');
    expectBaseHeaders(r);
    // Another IP, or another path, has its own budget.
    expect((await call(from('203.0.113.2'))).status).toBe(204);
    expect((await call(new Request(BASE + 'start', { headers: { 'CF-Connecting-IP': '203.0.113.1' } }))).status).toBe(302);
    // A minute later it's back.
    clock += 60_000;
    expect((await call(from('203.0.113.1'))).status).toBe(204);
  });

  it('uses the RATE_LIMITER binding when there is one, keyed by IP and path', async () => {
    const keys: string[] = [];
    const limiter = { limit: async ({ key }: { key: string }) => (keys.push(key), { success: keys.length < 2 }) };
    const e = env({ RATE_LIMITER: limiter });
    const req = () => post('signout', undefined, { 'X-Tack': '1', Origin: SITE, 'CF-Connecting-IP': '198.51.100.7' });
    expect((await call(req(), e)).status).toBe(204);
    const r = await call(req(), e);
    expect(r.status).toBe(429);
    expect(r.headers.get('Retry-After')).toBe('60');
    expect(keys).toEqual(['198.51.100.7:/api/google/signout', '198.51.100.7:/api/google/signout']);
  });
});
