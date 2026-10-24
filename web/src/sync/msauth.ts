import { retryAfterMs, SyncError } from './engine';

/**
 * Signing in to a Microsoft account (personal, work or school) from the browser: OAuth 2.0
 * authorization code flow with PKCE, as Microsoft requires for single-page apps. No client
 * secret, no server, no library: the whole exchange is the few requests below.
 *
 * Staying signed in (see docs/SYNC.md, "Staying signed in"): Microsoft gives browser apps a
 * refresh token that works for 24 hours from sign-in and can't be extended. Within that, renewal
 * is invisible. After it, Tack gets a new one without asking you anything, if you're still
 * signed in to Microsoft in this browser:
 *  1. in a hidden frame (works where third-party cookies are allowed, e.g. Chrome and Edge);
 *  2. otherwise (Safari, Firefox) with a quick visit to Microsoft's sign-in page and straight back,
 *     at a quiet moment: when the app starts or comes back after a while, never while you type.
 * Only if Microsoft wants you to actually sign in again does Tack ask.
 */

export interface AuthConfig {
  clientId: string;
  /** e.g. https://login.microsoftonline.com/common */
  authority: string;
  /** Where Microsoft sends you back after signing in: the app itself. */
  redirectUri: string;
  /** Where the hidden frame lands: a blank page. */
  silentRedirectUri: string;
  scopes: string[];
}

/** The narrowest scopes that work: Tack's own OneDrive folder, plus staying signed in and knowing which account it is. */
export const SCOPES = ['openid', 'profile', 'offline_access', 'https://graph.microsoft.com/Files.ReadWrite.AppFolder'];

export interface Account {
  /** The sign-in name (an email address, usually). */
  username: string;
  name: string;
  tenant: string;
  id: string;
}

export interface Tokens {
  accessToken: string;
  /** ms since the epoch. */
  accessExpires: number;
  refreshToken: string;
  /** When the refresh token stops working (24 hours after sign-in, for browser apps). */
  refreshExpires: number;
}

/** What's kept between visits (in IndexedDB, on this device only). */
export interface AuthRecord {
  account: Account | null;
  tokens: Tokens | null;
  /**
   * Does silent renewal in a hidden frame work in this browser? null until known: false only once
   * a frame was refused and a visit to Microsoft then worked (so it's the browser, not the session).
   */
  frameWorks: boolean | null;
  /** When a hidden frame was last refused by Microsoft (ms), 0 if not since the last visit. */
  frameRefused: number;
  /** When Tack last went to Microsoft's page to renew (ms), so it never loops. */
  lastRenewVisit: number;
  /**
   * That visit came back settled: renewed, or with Microsoft asking for a sign-in. One that didn't
   * (Tack was opened before it did, you went Back from Microsoft's page, or it failed on the way)
   * may be tried again sooner.
   */
  visitReturned: boolean;
  /** Microsoft wants you to sign in again (password changed, session ended, consent removed...). */
  needsSignIn: boolean;
  /**
   * Hidden-frame renewals that timed out (or failed on the way) in a row, and when the last one
   * did (ms). Each waits longer before the next; after a few a visit renews instead, and frames are
   * tried again once one has been made (only those since the last visit count). Not a refusal:
   * frameWorks stays as it was. Missing (in records earlier versions saved): none.
   */
  frameTimeouts?: number;
  frameTimedOut?: number;
}

export const emptyAuth = (): AuthRecord => ({ account: null, tokens: null, frameWorks: null, frameRefused: 0, lastRenewVisit: 0, visitReturned: true, needsSignIn: false });

/** A sign-in in progress across the page load it needs (kept in localStorage for that moment only). */
interface Pending {
  state: string;
  verifier: string;
  nonce: string;
  kind: 'sign-in' | 'renew';
  at: number;
}

/** Each sign-in in progress is kept under its own state, so two tabs can't spoil each other's. */
const PENDING_PREFIX = 'tack.signin.';
const PENDING_TTL = 10 * 60_000;
const CONSUMER_TENANT = '9188040d-6c67-4c5b-b112-36a304b66dad';
const DAY = 86_400_000;
const FRAME_TIMEOUT = 10_000;
const REQUEST_TIMEOUT = 30_000;
/** Microsoft's sign-in service answers a small request within this long, or counts as out of reach. */
const REACH_TIMEOUT = 5000;

export class AuthError extends Error {
  constructor(
    /** 'interaction': Microsoft needs you on its page; 'cancelled': you said no; 'failed': anything else. */
    readonly kind: 'interaction' | 'cancelled' | 'failed',
    message: string,
  ) {
    super(message);
  }
}

export class MicrosoftAuth {
  constructor(
    readonly config: AuthConfig,
    private readonly http: typeof fetch = (...args) => fetch(...args),
  ) {}

  // ------------------------------------------------------------------ starting a sign-in

  /** Goes to Microsoft's sign-in page; you come back to the app (see `completeRedirect`). */
  async signIn(options: { loginHint?: string } = {}): Promise<never> {
    const url = await this.authorizeUrl('sign-in', { prompt: 'select_account', loginHint: options.loginHint });
    location.assign(url);
    return new Promise<never>(() => {});
  }

  /**
   * Renewal without asking: a quick visit to Microsoft's page, which sends you straight back
   * if you're still signed in there (and with an error, never a sign-in form, if not).
   */
  async renewByVisit(account: Account): Promise<never> {
    const url = await this.authorizeUrl('renew', { prompt: 'none', loginHint: account.username, tenant: account.tenant });
    location.assign(url);
    return new Promise<never>(() => {});
  }

  /**
   * Can Microsoft's sign-in service be reached right now? Asked before a renewal visit: one that
   * can't get there leaves the browser's own error page in Tack's place, and navigator.onLine says
   * yes on Wi-Fi without internet or while a phone's connection is still coming up. Any answer
   * counts (only a network failure or no answer at all doesn't), and nothing about you is sent.
   */
  async reachable(): Promise<boolean> {
    try {
      await this.http(`${this.config.authority}/v2.0/.well-known/openid-configuration`, {
        mode: 'no-cors',
        credentials: 'omit',
        cache: 'no-store',
        signal: AbortSignal.timeout(REACH_TIMEOUT),
      });
      return true;
    } catch {
      return false;
    }
  }

  private async authorizeUrl(
    kind: Pending['kind'],
    options: { prompt: 'select_account' | 'none'; loginHint?: string; tenant?: string },
    redirectUri = this.config.redirectUri,
  ): Promise<string> {
    const pending: Pending = { state: random(16), verifier: random(32), nonce: random(16), kind, at: Date.now() };
    if (redirectUri === this.config.redirectUri) savePending(pending);
    else framePending.set(pending.state, pending);
    const params = new URLSearchParams({
      client_id: this.config.clientId,
      response_type: 'code',
      redirect_uri: redirectUri,
      response_mode: 'fragment',
      scope: this.config.scopes.join(' '),
      state: pending.state,
      nonce: pending.nonce,
      code_challenge: await challenge(pending.verifier),
      code_challenge_method: 'S256',
      prompt: options.prompt,
    });
    if (options.loginHint) params.set('login_hint', options.loginHint);
    if (options.tenant) params.set('domain_hint', options.tenant === CONSUMER_TENANT ? 'consumers' : 'organizations');
    return `${this.config.authority}/oauth2/v2.0/authorize?${params}`;
  }

  // ------------------------------------------------------------------ coming back

  /**
   * Handles the return from Microsoft's page, if this page load is one. Returns null otherwise.
   * The code is removed from the address bar before anything else.
   */
  async completeRedirect(): Promise<{ kind: Pending['kind']; account: Account; tokens: Tokens } | { kind: Pending['kind']; error: AuthError } | null> {
    const hash = location.hash.startsWith('#') ? location.hash.slice(1) : '';
    const params = new URLSearchParams(hash);
    sweepPending();
    if (!params.has('state') || (!params.has('code') && !params.has('error'))) return null;
    history.replaceState(null, '', location.pathname + location.search);
    const pending = takePending(params.get('state') ?? '');
    if (!pending) return null; // not ours, or stale
    const result = await this.finish(params, pending, this.config.redirectUri);
    return 'error' in result ? { kind: pending.kind, error: result.error } : { kind: pending.kind, ...result };
  }

  private async finish(params: URLSearchParams, pending: Pending, redirectUri: string): Promise<{ account: Account; tokens: Tokens } | { error: AuthError }> {
    const error = params.get('error');
    if (error) {
      const kind = error === 'access_denied' ? 'cancelled' : ['login_required', 'interaction_required', 'consent_required', 'account_selection_required'].includes(error) ? 'interaction' : 'failed';
      return { error: new AuthError(kind, params.get('error_description') ?? error) };
    }
    try {
      const response = await this.token({
        grant_type: 'authorization_code',
        code: params.get('code') ?? '',
        redirect_uri: redirectUri,
        code_verifier: pending.verifier,
      });
      const account = accountFrom(response.id_token, pending.nonce);
      return { account, tokens: tokensFrom(response, Date.now(), null) };
    } catch (e) {
      return { error: e instanceof AuthError ? e : new AuthError('failed', String(e)) };
    }
  }

  // ------------------------------------------------------------------ keeping it going

  /** A fresh access token, using the refresh token. Throws SyncError('auth') if that no longer works. */
  async refresh(tokens: Tokens): Promise<Tokens> {
    if (tokens.refreshExpires <= Date.now()) throw new SyncError('auth', 'Sign-in expired');
    try {
      const response = await this.token({ grant_type: 'refresh_token', refresh_token: tokens.refreshToken });
      return tokensFrom(response, Date.now(), tokens.refreshExpires);
    } catch (e) {
      if (e instanceof SyncError) throw e;
      throw new SyncError('auth', e instanceof Error ? e.message : String(e));
    }
  }

  /**
   * Renewal in a hidden frame, if this browser allows Microsoft's sign-in cookie there. Resolves
   * with new tokens, or throws AuthError ('interaction' if it can't be done this way).
   */
  async renewInFrame(account: Account): Promise<{ account: Account; tokens: Tokens }> {
    const url = await this.authorizeUrl('renew', { prompt: 'none', loginHint: account.username, tenant: account.tenant }, this.config.silentRedirectUri);
    const state = new URL(url).searchParams.get('state')!;
    const pending = framePending.get(state)!;
    framePending.delete(state);
    const hash = await frameRedirect(url, this.config.silentRedirectUri);
    const result = await this.finish(new URLSearchParams(hash), pending, this.config.silentRedirectUri);
    if ('error' in result) throw result.error;
    if (result.account.id !== account.id || result.account.tenant !== account.tenant) {
      throw new AuthError('interaction', 'A different account is signed in');
    }
    return result;
  }

  private async token(fields: Record<string, string>): Promise<TokenResponse> {
    const body = new URLSearchParams({ client_id: this.config.clientId, scope: this.config.scopes.join(' '), ...fields });
    let response: Response;
    try {
      response = await this.http(`${this.config.authority}/oauth2/v2.0/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body,
        signal: AbortSignal.timeout(REQUEST_TIMEOUT),
      });
    } catch (e) {
      throw new SyncError('offline', `Couldn't reach Microsoft (${String(e)})`);
    }
    // Asked to slow down, whatever the body says: not a spent sign-in, and not one to ask again at once.
    if (response.status === 429 || response.status === 503) {
      throw new SyncError('throttled', `Microsoft’s sign-in service is busy (${response.status})`, retryAfterMs(response) ?? 30_000);
    }
    let json: TokenResponse & { error?: string; error_description?: string };
    try {
      json = await response.json();
    } catch {
      throw new SyncError(response.status >= 500 ? 'server' : 'auth', `Unexpected answer from Microsoft (${response.status})`);
    }
    if (!response.ok || !json.access_token) {
      if (response.status >= 500 || json.error === 'temporarily_unavailable' || json.error === 'server_error') {
        throw new SyncError('server', json.error_description ?? `Microsoft error ${response.status}`);
      }
      const interaction = ['invalid_grant', 'interaction_required', 'consent_required', 'login_required'].includes(json.error ?? '');
      throw new AuthError(interaction ? 'interaction' : 'failed', json.error_description ?? json.error ?? `Error ${response.status}`);
    }
    return json;
  }
}

interface TokenResponse {
  access_token: string;
  expires_in?: number;
  refresh_token?: string;
  refresh_token_expires_in?: number;
  id_token?: string;
}

function tokensFrom(r: TokenResponse, now: number, previousRefreshExpires: number | null): Tokens {
  const accessExpires = now + Math.max(60, Number(r.expires_in) || 3600) * 1000;
  // Browser apps' refresh tokens last 24 hours from sign-in, and renewing them doesn't extend that.
  let refreshExpires = previousRefreshExpires ?? now + DAY;
  if (Number(r.refresh_token_expires_in) > 0) refreshExpires = Math.min(refreshExpires, now + Number(r.refresh_token_expires_in) * 1000);
  if (!r.refresh_token) throw new AuthError('failed', 'Microsoft sent no refresh token');
  return { accessToken: r.access_token, accessExpires, refreshToken: r.refresh_token, refreshExpires };
}

/** Who signed in, from the ID token. It came straight from Microsoft over TLS, so its claims are only read, not verified. */
function accountFrom(idToken: string | undefined, nonce: string): Account {
  const claims = decodeJwt(idToken);
  if (!claims) throw new AuthError('failed', 'Microsoft sent no ID token');
  if (claims.nonce !== nonce) throw new AuthError('failed', 'The sign-in answer did not match the request');
  const str = (v: unknown) => (typeof v === 'string' ? v : '');
  return {
    username: str(claims.preferred_username) || str(claims.email),
    name: str(claims.name),
    tenant: str(claims.tid),
    id: str(claims.oid) || str(claims.sub),
  };
}

export function decodeJwt(token: string | undefined): Record<string, unknown> | null {
  const part = token?.split('.')[1];
  if (!part) return null;
  try {
    const bytes = Uint8Array.from(atob(part.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
    const value = JSON.parse(new TextDecoder().decode(bytes));
    return typeof value === 'object' && value !== null ? value : null;
  } catch {
    return null;
  }
}

/** Loads `url` in a hidden frame and resolves with the fragment it lands on at `landing`. */
function frameRedirect(url: string, landing: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const frame = document.createElement('iframe');
    frame.hidden = true;
    frame.setAttribute('aria-hidden', 'true');
    frame.tabIndex = -1;
    // No top navigation, no popups: the frame can only land on the blank page.
    frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms');
    frame.src = url;
    const started = Date.now();
    const done = (fn: () => void) => {
      clearInterval(timer);
      frame.remove();
      fn();
    };
    const timer = setInterval(() => {
      let href = '';
      try {
        href = frame.contentWindow?.location.href ?? '';
      } catch {
        // Still on Microsoft's side (cross-origin).
      }
      if (href.startsWith(landing)) {
        const hash = frame.contentWindow!.location.hash.slice(1);
        done(() => resolve(hash));
      } else if (Date.now() - started > FRAME_TIMEOUT) {
        // Slow, not refused: try again later rather than concluding frames don't work here.
        done(() => reject(new AuthError('failed', 'Silent renewal timed out')));
      }
    }, 50);
    document.body.append(frame);
  });
}

const framePending = new Map<string, Pending>();

function savePending(p: Pending): void {
  try {
    localStorage.setItem(PENDING_PREFIX + p.state, JSON.stringify(p));
  } catch {
    /* without storage the sign-in can't complete; Microsoft's page will still show */
  }
}

/** The sign-in started with this state (each is used once), if it's recent. */
function takePending(state: string): Pending | null {
  try {
    const raw = localStorage.getItem(PENDING_PREFIX + state);
    localStorage.removeItem(PENDING_PREFIX + state);
    const p = raw ? (JSON.parse(raw) as Pending) : null;
    return p && p.state === state && Date.now() - p.at < PENDING_TTL ? p : null;
  } catch {
    return null;
  }
}

/** Forgets sign-ins that were never completed (after ten minutes). */
function sweepPending(): void {
  try {
    localStorage.removeItem('tack.signin'); // a single slot, before 0.2.0
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const key = localStorage.key(i);
      if (!key?.startsWith(PENDING_PREFIX)) continue;
      let at = 0;
      try {
        at = (JSON.parse(localStorage.getItem(key) ?? '{}') as Partial<Pending>).at ?? 0;
      } catch {
        /* unreadable: forget it */
      }
      if (Date.now() - at >= PENDING_TTL) localStorage.removeItem(key);
    }
  } catch {
    /* no storage */
  }
}

/** Was this page load the return from a sign-in (so the app shouldn't redirect again)? */
export function isSignInReturn(): boolean {
  return /(^|[#&])state=/.test(location.hash);
}

function random(bytes: number): string {
  const b = new Uint8Array(bytes);
  crypto.getRandomValues(b);
  return base64url(b);
}

async function challenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64url(new Uint8Array(digest));
}

function base64url(bytes: Uint8Array): string {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
