// The sign-in Worker for Google Drive: tackfocus.app/api/google/* (docs/GOOGLE-DRIVE.md, "The sign-in Worker").
//
// Google gives a website a refresh token only with a client secret, and the refresh token must not sit
// where page scripts can read it. This Worker holds the secret, keeps the refresh token encrypted in an
// HttpOnly cookie (no database), and turns that cookie into a one-hour access token for the page.
// It never calls the Drive API: Drive contents go between the page and www.googleapis.com only.
//
// Standard Web APIs only (Request, Response, URL, crypto.subtle, fetch, AbortSignal.timeout): no
// dependencies, no Cloudflare type packages. Nothing is logged but error codes: never a token, code,
// cookie or email.

/** Cloudflare's Workers rate-limit binding, as much of it as we use. */
export interface RateLimiter {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}

export interface Env {
  /** Public OAuth client id (a var). */
  GOOGLE_CLIENT_ID?: string;
  /** OAuth client secret (a Worker secret: `wrangler secret put GOOGLE_CLIENT_SECRET`, never in the repo). */
  GOOGLE_CLIENT_SECRET?: string;
  /** 32 random bytes, base64 (a Worker secret: `wrangler secret put SESSION_KEY`, never in the repo). */
  SESSION_KEY?: string;
  /** "https://tackfocus.app" (a var). */
  SITE_ORIGIN?: string;
  /** Per IP and path, 30 a minute. Without it (tests, local runs) a small in-memory limiter stands in. */
  RATE_LIMITER?: RateLimiter;
}

type FetchLike = (input: string, init: RequestInit) => Promise<Response>;

export interface HandlerOptions {
  /** Timeout for every Google call, in ms. 10 s; tests shorten it. */
  timeoutMs?: number;
  /** The clock, in ms. Tests move it. */
  now?: () => number;
}

const PREFIX = '/api/google/';
const SESSION_COOKIE = '__Secure-tack-g';
const FLOW_COOKIE = '__Secure-tack-gflow';
const SESSION_MAX_AGE = 15_552_000; // 180 days, in seconds
const FLOW_MAX_AGE = 600; // 10 minutes
const MAX_COOKIE_VALUE = 4096;
const MAX_REFRESH_TOKEN = 2048;
const MAX_ACCESS_TOKEN = 4096;
const MAX_ID_TOKEN = 16_384;
const MAX_EMAIL = 320;
const MAX_SUB = 255;
const MAX_RETURN = 200;
const MAX_GOOGLE_BODY = 65_536;
const RETRY_DEFAULT = 30;
const RETRY_MAX = 21_600; // 6 hours
const RATE_LIMIT = 30;
const RATE_WINDOW_MS = 60_000;
const RATE_TABLE_MAX = 10_000;

const AUTH_URL = 'https://accounts.google.com/o/oauth2/v2/auth';
const TOKEN_URL = 'https://oauth2.googleapis.com/token';
const REVOKE_URL = 'https://oauth2.googleapis.com/revoke';
const SCOPE = 'openid email https://www.googleapis.com/auth/drive.file';
const DRIVE_FILE = 'https://www.googleapis.com/auth/drive.file';
/** Everything Google may report back for what we asked (it expands email/profile into the userinfo names). */
const ALLOWED_SCOPES = new Set([
  'openid',
  'email',
  'profile',
  'https://www.googleapis.com/auth/userinfo.email',
  'https://www.googleapis.com/auth/userinfo.profile',
  DRIVE_FILE,
]);

type Reason = 'denied' | 'scope' | 'state' | 'exchange' | 'config';

interface Session {
  v: 1;
  rt: string;
  email: string;
  sub: string;
  /** Sign-in time, Unix seconds. Never moved forward by a refresh. */
  iat: number;
}

interface Flow {
  v: 1;
  state: string;
  verifier: string;
  ret: string;
  iat: number;
}

interface Config {
  clientId: string;
  secret: string;
  origin: string;
  key: CryptoKey;
}

const enc = new TextEncoder();
const dec = new TextDecoder('utf-8', { fatal: true });

// ---- small helpers -------------------------------------------------------------------------------

function b64urlEncode(bytes: Uint8Array): string {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function b64urlDecode(text: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]*$/.test(text) || text.length % 4 === 1) return null;
  const b64 = text.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (text.length % 4)) % 4);
  try {
    const s = atob(b64);
    const out = new Uint8Array(s.length);
    for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
    // One spelling per value: no stray padding bits.
    return b64urlEncode(out) === text ? out : null;
  } catch {
    return null;
  }
}

function b64Decode(text: string): Uint8Array | null {
  try {
    const s = atob(text.trim());
    const out = new Uint8Array(s.length);
    for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
    return out;
  } catch {
    return null;
  }
}

function randomToken(bytes = 32): string {
  return b64urlEncode(crypto.getRandomValues(new Uint8Array(bytes)));
}

function sameString(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function boundedString(v: unknown, max: number): v is string {
  return typeof v === 'string' && v.length > 0 && v.length <= max;
}

/** Logs an error code and nothing else. */
function warn(code: string): void {
  console.warn(`google-worker: ${code}`);
}

/**
 * `return`: a same-site path only. One leading `/` (not `//`), printable ASCII without spaces,
 * backslashes or `#` (a fragment would swallow `?google=…`), at most 200 characters. Anything else is `/`.
 */
export function safeReturn(value: string | null | undefined): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > MAX_RETURN) return '/';
  if (value[0] !== '/' || value[1] === '/' || value[1] === '\\') return '/';
  if (!/^[\x21-\x7e]+$/.test(value) || value.includes('\\') || value.includes('#')) return '/';
  return value;
}

function connectedUrl(ret: string): string {
  return ret + (ret.includes('?') ? '&' : '?') + 'google=connected';
}

function errorUrl(reason: Reason): string {
  return `/?google=error&reason=${reason}`;
}

// ---- responses -----------------------------------------------------------------------------------

function baseHeaders(): Headers {
  return new Headers({
    'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
  });
}

function json(status: number, body: unknown, extra?: Record<string, string>, cookies: string[] = []): Response {
  const headers = baseHeaders();
  headers.set('Content-Type', 'application/json');
  for (const [k, v] of Object.entries(extra ?? {})) headers.set(k, v);
  for (const c of cookies) headers.append('Set-Cookie', c);
  return new Response(JSON.stringify(body), { status, headers });
}

function empty(status: number, extra?: Record<string, string>, cookies: string[] = []): Response {
  const headers = baseHeaders();
  for (const [k, v] of Object.entries(extra ?? {})) headers.set(k, v);
  for (const c of cookies) headers.append('Set-Cookie', c);
  return new Response(null, { status, headers });
}

function redirect(location: string, cookies: string[] = []): Response {
  return empty(302, { Location: location }, cookies);
}

function setCookie(name: string, value: string, maxAge: number, sameSite: 'Strict' | 'Lax'): string {
  return `${name}=${value}; Max-Age=${maxAge}; Path=${PREFIX}; HttpOnly; Secure; SameSite=${sameSite}`;
}

const clearSession = () => setCookie(SESSION_COOKIE, '', 0, 'Strict');
const clearFlow = () => setCookie(FLOW_COOKIE, '', 0, 'Lax');

function readCookie(request: Request, name: string): string | null {
  const header = request.headers.get('Cookie');
  if (!header) return null;
  for (const part of header.split(';')) {
    const eq = part.indexOf('=');
    if (eq < 0) continue;
    if (part.slice(0, eq).trim() !== name) continue;
    const value = part.slice(eq + 1).trim();
    return value.length > 0 && value.length <= MAX_COOKIE_VALUE ? value : null;
  }
  return null;
}

/** Retry-After from Google if it's sane (seconds or an HTTP date), else 30; never more than 6 hours. */
function retryAfter(header: string | null, nowMs: number): number {
  if (header) {
    const t = header.trim();
    if (/^\d{1,10}$/.test(t)) {
      const s = Number(t);
      if (s >= 1) return Math.min(s, RETRY_MAX);
    } else {
      const at = Date.parse(t);
      if (Number.isFinite(at)) {
        const s = Math.ceil((at - nowMs) / 1000);
        if (s >= 1) return Math.min(s, RETRY_MAX);
      }
    }
  }
  return RETRY_DEFAULT;
}

// ---- the handler ---------------------------------------------------------------------------------

export function makeHandler(fetchImpl?: FetchLike, options: HandlerOptions = {}) {
  const doFetch: FetchLike = fetchImpl ?? ((input, init) => fetch(input, init));
  const timeoutMs = options.timeoutMs ?? 10_000;
  const now = options.now ?? (() => Date.now());

  // The session key, imported once per isolate (and again only if the secret changes).
  let keyCache: { raw: string; key: Promise<CryptoKey | null> } | null = null;
  // The stand-in rate limiter: per isolate, fixed one-minute windows.
  const windows = new Map<string, { start: number; count: number }>();

  function importKey(raw: string): Promise<CryptoKey | null> {
    if (keyCache?.raw !== raw) {
      const bytes = b64Decode(raw);
      const key =
        bytes && bytes.length === 32
          ? crypto.subtle
              .importKey('raw', bytes as Uint8Array<ArrayBuffer>, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt'])
              .catch(() => null)
          : Promise.resolve(null);
      keyCache = { raw, key };
    }
    return keyCache.key;
  }

  async function config(env: Env): Promise<Config | null> {
    const clientId = env.GOOGLE_CLIENT_ID?.trim();
    const secret = env.GOOGLE_CLIENT_SECRET?.trim();
    const origin = env.SITE_ORIGIN?.trim();
    if (!clientId || !secret || !origin || !env.SESSION_KEY) return null;
    const key = await importKey(env.SESSION_KEY);
    return key ? { clientId, secret, origin, key } : null;
  }

  async function seal(key: CryptoKey, name: string, value: unknown): Promise<string> {
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const data = await crypto.subtle.encrypt(
      { name: 'AES-GCM', iv, additionalData: enc.encode(name) },
      key,
      enc.encode(JSON.stringify(value)),
    );
    const out = new Uint8Array(12 + data.byteLength);
    out.set(iv);
    out.set(new Uint8Array(data), 12);
    return b64urlEncode(out);
  }

  async function open(key: CryptoKey, name: string, value: string | null): Promise<Record<string, unknown> | null> {
    if (!value) return null;
    const bytes = b64urlDecode(value);
    if (!bytes || bytes.length < 12 + 16 + 2) return null;
    try {
      const plain = await crypto.subtle.decrypt(
        { name: 'AES-GCM', iv: bytes.slice(0, 12), additionalData: enc.encode(name) },
        key,
        bytes.slice(12),
      );
      const parsed: unknown = JSON.parse(dec.decode(plain));
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : null;
    } catch {
      return null;
    }
  }

  async function readSession(key: CryptoKey, request: Request): Promise<Session | null> {
    const o = await open(key, SESSION_COOKIE, readCookie(request, SESSION_COOKIE));
    if (!o || o.v !== 1) return null;
    const { rt, email, sub, iat } = o;
    if (!boundedString(rt, MAX_REFRESH_TOKEN) || !boundedString(email, MAX_EMAIL) || !boundedString(sub, MAX_SUB)) return null;
    if (typeof iat !== 'number' || !Number.isFinite(iat)) return null;
    const nowS = Math.floor(now() / 1000);
    if (iat > nowS + 300 || nowS >= iat + SESSION_MAX_AGE) return null;
    return { v: 1, rt, email, sub, iat };
  }

  async function readFlow(key: CryptoKey, request: Request): Promise<Flow | null> {
    const o = await open(key, FLOW_COOKIE, readCookie(request, FLOW_COOKIE));
    if (!o || o.v !== 1) return null;
    const { state, verifier, ret, iat } = o;
    if (!boundedString(state, 128) || !boundedString(verifier, 128) || typeof ret !== 'string') return null;
    if (typeof iat !== 'number' || !Number.isFinite(iat)) return null;
    const nowS = Math.floor(now() / 1000);
    if (iat > nowS + 60 || nowS >= iat + FLOW_MAX_AGE) return null;
    return { v: 1, state, verifier, ret: safeReturn(ret), iat };
  }

  async function sessionCookie(key: CryptoKey, session: Session): Promise<string> {
    return setCookie(SESSION_COOKIE, await seal(key, SESSION_COOKIE, session), SESSION_MAX_AGE, 'Strict');
  }

  /** A form POST to Google with the timeout. Throws on network failure or timeout. */
  function post(url: string, form: Record<string, string>): Promise<Response> {
    return doFetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
      body: new URLSearchParams(form).toString(),
      redirect: 'manual',
      signal: AbortSignal.timeout(timeoutMs),
    });
  }

  async function readJson(response: Response): Promise<Record<string, unknown> | null> {
    try {
      const text = await response.text();
      if (text.length > MAX_GOOGLE_BODY) return null;
      const parsed: unknown = JSON.parse(text);
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : null;
    } catch {
      return null;
    }
  }

  /** Best effort: tells Google to forget a token. Failures are ignored. */
  async function revoke(token: string): Promise<void> {
    try {
      const r = await post(REVOKE_URL, { token });
      if (!r.ok) warn(`revoke_${r.status}`);
      await r.body?.cancel();
    } catch {
      warn('revoke_failed');
    }
  }

  async function rateLimited(request: Request, env: Env, path: string): Promise<boolean> {
    const ip = request.headers.get('CF-Connecting-IP') ?? 'unknown';
    const key = `${ip}:${path}`;
    if (env.RATE_LIMITER) {
      try {
        return !(await env.RATE_LIMITER.limit({ key })).success;
      } catch {
        warn('ratelimit_failed');
        return false;
      }
    }
    const t = now();
    let w = windows.get(key);
    if (!w || t - w.start >= RATE_WINDOW_MS) {
      if (windows.size >= RATE_TABLE_MAX) windows.clear();
      w = { start: t, count: 0 };
      windows.set(key, w);
    }
    w.count++;
    return w.count > RATE_LIMIT;
  }

  /** `token` and `signout`: a custom header (forces a preflight cross-site, which we never answer) and our origin. */
  function csrfOk(request: Request, env: Env): boolean {
    if (request.headers.get('X-Tack') !== '1') return false;
    const origin = request.headers.get('Origin');
    const site = env.SITE_ORIGIN?.trim();
    if (origin !== null) return !!site && origin === site;
    return request.headers.get('Sec-Fetch-Site') === 'same-origin';
  }

  // ---- endpoints ----

  async function start(request: Request, env: Env): Promise<Response> {
    const cfg = await config(env);
    if (!cfg) {
      warn('config_missing');
      return redirect(errorUrl('config'));
    }
    const ret = safeReturn(new URL(request.url).searchParams.get('return'));
    const state = randomToken();
    const verifier = randomToken();
    const challenge = b64urlEncode(new Uint8Array(await crypto.subtle.digest('SHA-256', enc.encode(verifier))));
    const flow: Flow = { v: 1, state, verifier, ret, iat: Math.floor(now() / 1000) };
    const google = new URL(AUTH_URL);
    google.search = new URLSearchParams({
      client_id: cfg.clientId,
      redirect_uri: `${cfg.origin}${PREFIX}callback`,
      response_type: 'code',
      scope: SCOPE,
      access_type: 'offline',
      prompt: 'consent',
      code_challenge: challenge,
      code_challenge_method: 'S256',
      state,
    }).toString();
    return redirect(google.toString(), [setCookie(FLOW_COOKIE, await seal(cfg.key, FLOW_COOKIE, flow), FLOW_MAX_AGE, 'Lax')]);
  }

  async function callback(request: Request, env: Env): Promise<Response> {
    const fail = (reason: Reason) => redirect(errorUrl(reason), [clearFlow()]);
    const cfg = await config(env);
    if (!cfg) {
      warn('config_missing');
      return fail('config');
    }
    const params = new URL(request.url).searchParams;
    const error = params.get('error');
    if (error !== null) return fail(error === 'access_denied' ? 'denied' : 'exchange');

    const flow = await readFlow(cfg.key, request);
    const state = params.get('state');
    if (!flow || state === null || !sameString(state, flow.state)) return fail('state');
    const code = params.get('code');
    if (!code || code.length > 2048) return fail('exchange');

    let body: Record<string, unknown> | null;
    try {
      const r = await post(TOKEN_URL, {
        grant_type: 'authorization_code',
        code,
        client_id: cfg.clientId,
        client_secret: cfg.secret,
        redirect_uri: `${cfg.origin}${PREFIX}callback`,
        code_verifier: flow.verifier,
      });
      if (!r.ok) {
        warn(`exchange_${r.status}`);
        await r.body?.cancel();
        return fail('exchange');
      }
      body = await readJson(r);
    } catch {
      warn('exchange_failed');
      return fail('exchange');
    }
    if (!body) return fail('exchange');
    const rt = body.refresh_token;
    const access = body.access_token;
    // Revoking either token ends the whole grant.
    const anyToken = boundedString(rt, MAX_REFRESH_TOKEN) ? rt : boundedString(access, MAX_ACCESS_TOKEN) ? access : null;

    const granted = typeof body.scope === 'string' ? body.scope.split(' ').filter(Boolean) : [];
    if (!granted.includes(DRIVE_FILE) || granted.some((s) => !ALLOWED_SCOPES.has(s))) {
      warn('scope_mismatch');
      if (anyToken) await revoke(anyToken);
      return fail('scope');
    }
    if (!boundedString(rt, MAX_REFRESH_TOKEN)) {
      warn('no_refresh_token');
      if (anyToken) await revoke(anyToken);
      return fail('exchange');
    }
    // The id_token came straight from Google's token endpoint over TLS, in answer to our own code and
    // secret, so its signature needs no check here: we only read who signed in.
    const who = idTokenClaims(body.id_token, cfg.clientId);
    if (!who) {
      warn('bad_id_token');
      await revoke(rt);
      return fail('exchange');
    }
    const session: Session = { v: 1, rt, email: who.email, sub: who.sub, iat: Math.floor(now() / 1000) };
    return redirect(connectedUrl(flow.ret), [await sessionCookie(cfg.key, session), clearFlow()]);
  }

  async function token(request: Request, env: Env): Promise<Response> {
    if (!csrfOk(request, env)) return json(403, { error: 'forbidden' });
    const cfg = await config(env);
    if (!cfg) {
      warn('config_missing');
      return json(503, { error: 'config' }, { 'Retry-After': '3600' });
    }
    const session = await readSession(cfg.key, request);
    if (!session) return json(401, { error: 'signed_out' }, undefined, [clearSession()]);

    let r: Response;
    try {
      r = await post(TOKEN_URL, {
        grant_type: 'refresh_token',
        refresh_token: session.rt,
        client_id: cfg.clientId,
        client_secret: cfg.secret,
      });
    } catch {
      warn('refresh_failed');
      return json(503, { error: 'unavailable' }, { 'Retry-After': String(RETRY_DEFAULT) });
    }
    if (!r.ok) {
      const status = r.status;
      const retry = retryAfter(r.headers.get('Retry-After'), now());
      const body = await readJson(r);
      if ((status === 400 || status === 401) && body?.error === 'invalid_grant') {
        warn('refresh_invalid_grant');
        return json(401, { error: 'revoked' }, undefined, [clearSession()]);
      }
      warn(`refresh_${status}`);
      return json(503, { error: 'unavailable' }, { 'Retry-After': String(status === 429 || status >= 500 ? retry : RETRY_DEFAULT) });
    }
    const body = await readJson(r);
    const access = body?.access_token;
    const expires = body?.expires_in;
    if (!boundedString(access, MAX_ACCESS_TOKEN) || typeof expires !== 'number' || !Number.isFinite(expires) || expires <= 0) {
      warn('refresh_bad_answer');
      return json(503, { error: 'unavailable' }, { 'Retry-After': String(RETRY_DEFAULT) });
    }
    const cookies: string[] = [];
    const newRt = body?.refresh_token;
    if (boundedString(newRt, MAX_REFRESH_TOKEN) && newRt !== session.rt) {
      cookies.push(await sessionCookie(cfg.key, { ...session, rt: newRt }));
    }
    return json(200, { access_token: access, expires_in: Math.floor(expires), email: session.email }, undefined, cookies);
  }

  /**
   * Signs this browser out: its cookie, the only copy of its refresh token, is cleared. Not revoked at
   * Google: a revoke ends the whole grant, which would sign out your other browsers too. Removing
   * Tack's access everywhere is myaccount.google.com/permissions (the privacy page says so).
   */
  async function signout(request: Request, env: Env): Promise<Response> {
    if (!csrfOk(request, env)) return json(403, { error: 'forbidden' });
    return empty(204, undefined, [clearSession()]);
  }

  const routes: Record<string, { method: 'GET' | 'POST'; run: (request: Request, env: Env) => Promise<Response> }> = {
    start: { method: 'GET', run: start },
    callback: { method: 'GET', run: callback },
    token: { method: 'POST', run: token },
    signout: { method: 'POST', run: signout },
  };

  return {
    async fetch(request: Request, env: Env, _ctx?: unknown): Promise<Response> {
      const path = new URL(request.url).pathname;
      const name = path.startsWith(PREFIX) ? path.slice(PREFIX.length) : '';
      const route = Object.hasOwn(routes, name) ? routes[name] : undefined;
      if (!route) return json(404, { error: 'not_found' });
      if (await rateLimited(request, env, path)) return json(429, { error: 'rate_limited' }, { 'Retry-After': '60' });
      // No CORS: OPTIONS is just another method we don't answer.
      if (request.method !== route.method) return json(405, { error: 'method' }, { Allow: route.method });
      try {
        return await route.run(request, env);
      } catch {
        warn('internal');
        return json(500, { error: 'internal' });
      }
    },
  };
}

/** email and sub from an id_token's payload, bounded; and it must be meant for us. */
function idTokenClaims(idToken: unknown, clientId: string): { email: string; sub: string } | null {
  if (!boundedString(idToken, MAX_ID_TOKEN)) return null;
  const parts = idToken.split('.');
  if (parts.length !== 3) return null;
  const bytes = b64urlDecode(parts[1]!);
  if (!bytes) return null;
  try {
    const claims: unknown = JSON.parse(dec.decode(bytes));
    if (!claims || typeof claims !== 'object') return null;
    const { email, sub, aud } = claims as Record<string, unknown>;
    if (aud !== clientId) return null;
    if (!boundedString(email, MAX_EMAIL) || !boundedString(sub, MAX_SUB)) return null;
    if (/[\x00-\x1f\x7f]/.test(email) || /[\x00-\x1f\x7f]/.test(sub)) return null;
    return { email, sub };
  } catch {
    return null;
  }
}

export default makeHandler();
