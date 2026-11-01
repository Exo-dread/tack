import { retryAfterMs, SyncError } from './engine';

/**
 * Connecting Google Drive, the page's side (see docs/GOOGLE-DRIVE.md, "The sign-in Worker").
 * Google gives a website a refresh token only with a client secret, so a small Worker on this
 * site's own /api/google/ does the signing in and keeps the refresh token, encrypted, in a cookie
 * this page can't read. The page only ever holds an access token for Drive, in memory, for the
 * hour it lasts: never in IndexedDB or localStorage, never in a log or a URL.
 */

const API = '/api/google';
const TIMEOUT = 30_000;
/** An access token is used until this long before it runs out, then a new one is asked for. */
const AHEAD = 2 * 60_000;
/** Marks a connection this tab set off on, so a link to /?google=connected can't turn sync on or switch it. */
const PENDING = 'tack.google.connect';
const PENDING_TTL = 10 * 60_000;

/** Why the Worker sent you back without connecting (anything else it might say reads as 'failed'). */
export type GoogleFailure = 'denied' | 'scope' | 'state' | 'exchange' | 'config' | 'failed';

export type GoogleReturn = { connected: true } | { connected: false; reason: GoogleFailure };

const FAILURES: readonly GoogleFailure[] = ['denied', 'scope', 'state', 'exchange', 'config'];

export class GoogleAuth {
  private cached: { token: string; expires: number } | null = null;
  private asking: Promise<string> | null = null;
  private _email = '';

  constructor(private readonly http: typeof fetch = (...args) => fetch(...args)) {}

  /** The account's address, as the Worker last said ('' until a token has been fetched). */
  get email(): string {
    return this._email;
  }

  /** Goes to Google, through the Worker; you come back to the app with ?google= (see `takeGoogleReturn`). */
  connect(): Promise<never> {
    try {
      localStorage.setItem(PENDING, String(Date.now()));
    } catch {
      /* without storage the return isn't recognised: connecting again will say so */
    }
    location.assign(`${API}/start?return=/`);
    return new Promise<never>(() => {});
  }

  /**
   * An access token for Drive: the one in memory while it has more than two minutes left, else a
   * new one from the Worker (`force`: a new one anyway, after Drive refused the last). Throws
   * SyncError: 'auth' when the Worker says you're signed out or Google revoked the access,
   * 'throttled' with Retry-After when it or Google is busy, 'offline' when it can't be reached.
   */
  token(force = false): Promise<string> {
    if (!force && this.cached && this.cached.expires - Date.now() > AHEAD) return Promise.resolve(this.cached.token);
    this.asking ??= this.fetchToken().finally(() => (this.asking = null));
    return this.asking;
  }

  /** Forgets the access token (sync was turned off, or the Worker said you're signed out). */
  forget(): void {
    this.cached = null;
    this._email = '';
  }

  /** Signs out at the Worker, which revokes the access at Google and clears its cookie. Best effort. */
  async signOut(): Promise<void> {
    this.forget();
    try {
      await this.http(`${API}/signout`, { method: 'POST', headers: { 'X-Tack': '1' }, credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(TIMEOUT) });
    } catch {
      /* offline: the cookie stays until it runs out, and is no use without this site */
    }
  }

  private async fetchToken(): Promise<string> {
    let response: Response;
    try {
      response = await this.http(`${API}/token`, { method: 'POST', headers: { 'X-Tack': '1' }, credentials: 'same-origin', cache: 'no-store', signal: AbortSignal.timeout(TIMEOUT) });
    } catch (error) {
      throw new SyncError('offline', `Couldn't reach Tack's sign-in for Google (${String(error)})`);
    }
    if (response.status === 429 || response.status === 503) {
      throw new SyncError('throttled', `Google's sign-in is busy (${response.status})`, retryAfterMs(response) ?? 30_000);
    }
    let body: unknown = null;
    try {
      body = await response.json();
    } catch {
      /* no JSON body */
    }
    const field = (name: string): unknown => (body && typeof body === 'object' ? (body as Record<string, unknown>)[name] : undefined);
    if (response.status === 401) {
      this.forget();
      throw new SyncError('auth', field('error') === 'revoked' ? 'revoked' : 'signed_out');
    }
    const token = field('access_token');
    const expiresIn = Number(field('expires_in'));
    if (!response.ok || typeof token !== 'string' || !token || token.length > 8192) {
      throw new SyncError(response.status >= 500 || response.ok ? 'server' : 'denied', `Unexpected answer from Tack's sign-in for Google (${response.status})`);
    }
    const email = field('email');
    this._email = typeof email === 'string' ? email.slice(0, 320) : '';
    this.cached = { token, expires: Date.now() + Math.min(Math.max(Number.isFinite(expiresIn) ? expiresIn : 3600, 60), 86_400) * 1000 };
    return token;
  }
}

/**
 * The return from connecting Google Drive, if this page load is one, taken out of the address bar
 * at once (call before the app shows anything). Anyone can write a link to /?google=connected, so
 * it counts only if this browser set off to connect within the last ten minutes.
 */
export function takeGoogleReturn(): GoogleReturn | null {
  if (typeof location === 'undefined') return null;
  const params = new URLSearchParams(location.search ?? '');
  if (!params.has('google')) return null;
  const value = params.get('google');
  const reason = params.get('reason') ?? '';
  params.delete('google');
  params.delete('reason');
  const rest = params.toString();
  history.replaceState(null, '', `${location.pathname}${rest ? `?${rest}` : ''}${location.hash}`);
  let started = 0;
  try {
    started = Number(localStorage.getItem(PENDING)) || 0;
    localStorage.removeItem(PENDING);
  } catch {
    /* no storage */
  }
  if (!started || Date.now() - started >= PENDING_TTL || started > Date.now()) return null;
  if (value === 'connected') return { connected: true };
  if (value === 'error') return { connected: false, reason: (FAILURES as readonly string[]).includes(reason) ? (reason as GoogleFailure) : 'failed' };
  return null;
}
