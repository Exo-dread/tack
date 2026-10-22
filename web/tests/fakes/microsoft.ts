import { createHash } from 'node:crypto';

/**
 * A stand-in for Microsoft's sign-in service, Microsoft Graph and OneDrive, faithful to what Tack
 * relies on, for unit tests (as a `fetch`) and browser tests (behind Playwright routes):
 *
 *  - sign-in: /authorize redirects back with a code (or an error for prompt=none without a
 *    session, or in a frame where third-party cookies are blocked); /token checks PKCE and the
 *    redirect URI, and issues single-page-app refresh tokens that expire 24 hours after sign-in
 *    no matter how often they're used; the OpenID discovery document answers (Tack asks for it
 *    to see whether Microsoft can be reached before a renewal visit);
 *  - Graph: needs a valid access token; the app folder, children (with paging), folder creation
 *    with conflict behaviour, simple uploads, and item metadata with pre-signed download links;
 *  - downloads: pre-signed links that refuse a bearer token (like OneDrive personal's host) and
 *    expire;
 *  - CORS: every answer carries Access-Control-Allow-Origin, preflights are answered.
 *
 * Faults can be injected per request pattern, and answers held back (a slow or stuck Microsoft:
 * browser tests' routes wait on `held` before answering).
 */

export interface FakeRequest {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: string | null;
}

export interface FakeResponse {
  status: number;
  headers: Record<string, string>;
  body: string;
}

interface Item {
  id: string;
  name: string;
  parent: string | null;
  folder: boolean;
  text: string;
  version: number;
}

interface Fault {
  match: RegExp;
  method?: string;
  status?: number;
  abort?: boolean;
  times: number;
  retryAfter?: number;
}

interface Hold {
  match: RegExp;
  method?: string;
  /** Held this long; without it, until `release()`. */
  ms?: number;
  times: number;
}

export const CONSUMERS = '9188040d-6c67-4c5b-b112-36a304b66dad';
export const AUTHORITY = 'https://login.microsoftonline.com/common';
const GRAPH = 'https://graph.microsoft.com/v1.0';
const DOWNLOADS = 'https://dl.files.1drv.com/fake/';
const HOUR = 3600_000;

export class FakeMicrosoft {
  /** The account signed in at Microsoft in "the browser" (null: nobody, so prompt=none fails). */
  account: { username: string; name: string; tid: string; oid: string } | null = { username: 'ada@outlook.com', name: 'Ada Lovelace', tid: CONSUMERS, oid: 'oid-ada' };
  /** Third-party cookies allowed (Chrome, Edge): renewal in a hidden frame works. */
  framesWork = true;
  /** The person cancels on Microsoft's page. */
  userCancels = false;
  accessLifetime = HOUR;
  refreshLifetime = 24 * HOUR;
  /** How long pre-signed download links last. */
  linkLifetime = HOUR;
  clientId = '11111111-2222-3333-4444-555555555555';
  clock: () => number = () => Date.now();
  readonly log: (FakeRequest & { frame: boolean })[] = [];
  readonly faults: Fault[] = [];
  readonly holds: Hold[] = [];

  private readonly waiting = new Set<() => void>();
  private codes = new Map<string, { challenge: string; redirect: string; nonce: string; oid: string }>();
  private refresh = new Map<string, { expires: number; oid: string }>();
  private access = new Map<string, number>();
  private items = new Map<string, Item>([['root', { id: 'root', name: 'root', parent: null, folder: true, text: '', version: 1 }]]);
  private n = 0;

  /** Fails the next `times` requests matching `match` (by status, or by dropping the connection). */
  fail(match: RegExp, options: { status?: number; abort?: boolean; times?: number; method?: string; retryAfter?: number } = {}): void {
    this.faults.push({ match, times: 1, ...options });
  }

  /**
   * Holds back the answers to the next `times` requests matching `match`: for `ms` (a slow
   * Microsoft), or without it until `release()` (a stuck one). The request is answered as usual,
   * only later; browser tests' routes wait on `held` for this.
   */
  hold(match: RegExp, options: { ms?: number; times?: number; method?: string } = {}): void {
    this.holds.push({ match, times: 1, ...options });
  }

  /** Lets every answer held back now go. */
  release(): void {
    for (const go of [...this.waiting]) go();
  }

  /** If a hold matches this request: a promise that resolves when its answer may go. */
  held(req: { method: string; url: string }): Promise<void> | null {
    const hold = this.holds.find((h) => h.times > 0 && h.match.test(req.url) && (!h.method || h.method === req.method));
    if (!hold) return null;
    hold.times--;
    return new Promise((resolve) => {
      const go = () => {
        clearTimeout(timer);
        this.waiting.delete(go);
        resolve();
      };
      const timer = hold.ms === undefined ? undefined : setTimeout(go, hold.ms);
      this.waiting.add(go);
    });
  }

  // ---------------------------------------------------------------- inspecting the drive

  /** The app folder's files under `devices/`, by name. */
  deviceFiles(): Map<string, string> {
    const devices = [...this.items.values()].find((i) => i.name === 'devices' && i.folder);
    const out = new Map<string, string>();
    if (!devices) return out;
    for (const i of this.items.values()) if (i.parent === devices.id && !i.folder) out.set(i.name, i.text);
    return out;
  }

  /** Puts a file into devices/ as another device would (e.g. one Tack for Windows wrote on a PC). */
  putDeviceFile(name: string, text: string): void {
    const devices = [...this.items.values()].find((i) => i.name === 'devices' && i.folder);
    if (!devices) throw new Error('No devices folder yet');
    const existing = [...this.items.values()].find((i) => i.parent === devices.id && i.name === name);
    const item = existing ?? this.create(devices.id, name, false);
    item.text = text;
    item.version++;
  }

  /** Every item's path, e.g. "Apps/Tack Focus/devices/abc.json". */
  paths(): string[] {
    const path = (i: Item): string => (i.parent && i.parent !== 'root' ? `${path(this.items.get(i.parent)!)}/${i.name}` : i.name);
    return [...this.items.values()].filter((i) => i.id !== 'root').map(path).sort();
  }

  /** Deletes an item by path (as a person might in OneDrive). */
  remove(path: string): void {
    const target = [...this.items.values()].find((i) => this.pathOf(i) === path);
    if (!target) return;
    const drop = (id: string) => {
      for (const c of [...this.items.values()].filter((i) => i.parent === id)) drop(c.id);
      this.items.delete(id);
    };
    drop(target.id);
  }

  /** Overwrites a file's content by path (a damaged or hand-edited file). */
  overwrite(path: string, text: string): void {
    const target = [...this.items.values()].find((i) => this.pathOf(i) === path);
    if (target) {
      target.text = text;
      target.version++;
    }
  }

  private pathOf(i: Item): string {
    return i.parent && i.parent !== 'root' ? `${this.pathOf(this.items.get(i.parent)!)}/${i.name}` : i.name;
  }

  // ---------------------------------------------------------------- as fetch

  /** For unit tests: a fetch that talks to this fake. */
  readonly fetch = async (input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const headers: Record<string, string> = {};
    new Headers(init.headers).forEach((v, k) => (headers[k.toLowerCase()] = v));
    headers.origin ??= 'https://tackfocus.app';
    const body = init.body == null ? null : typeof init.body === 'string' ? init.body : init.body instanceof URLSearchParams ? init.body.toString() : String(init.body);
    const result = this.handle({ method: (init.method ?? 'GET').toUpperCase(), url, headers, body }, false);
    if (result === 'abort') throw new TypeError('Failed to fetch');
    return new Response(result.status === 204 || result.status === 302 ? null : result.body, { status: result.status, headers: result.headers });
  };

  // ---------------------------------------------------------------- the service

  handle(req: FakeRequest, frame: boolean): FakeResponse | 'abort' {
    this.log.push({ ...req, frame });
    const origin = req.headers.origin ?? 'https://tackfocus.app';
    const cors = { 'access-control-allow-origin': origin, 'access-control-expose-headers': 'Retry-After' };
    if (req.method === 'OPTIONS') {
      return {
        status: 204,
        headers: { ...cors, 'access-control-allow-methods': 'GET, POST, PUT, DELETE', 'access-control-allow-headers': 'authorization, content-type', 'access-control-max-age': '600' },
        body: '',
      };
    }
    const fault = this.faults.find((f) => f.times > 0 && f.match.test(req.url) && (!f.method || f.method === req.method));
    if (fault) {
      fault.times--;
      if (fault.abort) return 'abort';
      const headers: Record<string, string> = { ...cors, 'content-type': 'application/json' };
      if (fault.retryAfter) headers['retry-after'] = String(fault.retryAfter);
      return { status: fault.status ?? 500, headers, body: JSON.stringify({ error: { code: 'injected', message: 'Injected fault' } }) };
    }
    const url = new URL(req.url);
    if (url.href.startsWith(`${AUTHORITY}/oauth2/v2.0/authorize`)) return this.authorize(url, frame);
    if (url.href.startsWith(`${AUTHORITY}/oauth2/v2.0/token`)) return this.token(req, cors);
    if (url.href === `${AUTHORITY}/v2.0/.well-known/openid-configuration`) {
      const configuration = { issuer: 'https://login.microsoftonline.com/{tenantid}/v2.0', authorization_endpoint: `${AUTHORITY}/oauth2/v2.0/authorize`, token_endpoint: `${AUTHORITY}/oauth2/v2.0/token` };
      return { status: 200, headers: { ...cors, 'content-type': 'application/json' }, body: JSON.stringify(configuration) };
    }
    if (url.href.startsWith(DOWNLOADS)) return this.download(req, url, cors);
    if (url.href.startsWith(GRAPH)) return this.graph(req, url, cors);
    return { status: 404, headers: cors, body: 'unknown host' };
  }

  private authorize(url: URL, frame: boolean): FakeResponse {
    const p = url.searchParams;
    const redirect = p.get('redirect_uri') ?? '';
    const state = p.get('state') ?? '';
    const back = (fields: Record<string, string>) => ({ status: 302, headers: { location: `${redirect}#${new URLSearchParams({ ...fields, state })}` }, body: '' });
    if (p.get('client_id') !== this.clientId || p.get('response_type') !== 'code' || p.get('code_challenge_method') !== 'S256' || !p.get('code_challenge')) {
      return back({ error: 'invalid_request', error_description: 'Bad authorize request' });
    }
    const scope = (p.get('scope') ?? '').split(' ');
    if (!scope.includes('offline_access') || !scope.includes('https://graph.microsoft.com/Files.ReadWrite.AppFolder')) return back({ error: 'invalid_scope' });
    const silent = p.get('prompt') === 'none';
    if (silent && (!this.account || (frame && !this.framesWork))) return back({ error: 'login_required', error_description: 'AADSTS50058: no session' });
    if (!silent && this.userCancels) return back({ error: 'access_denied', error_description: 'The user cancelled' });
    if (!this.account) this.account = { username: 'ada@outlook.com', name: 'Ada Lovelace', tid: CONSUMERS, oid: 'oid-ada' };
    const code = `code-${++this.n}`;
    this.codes.set(code, { challenge: p.get('code_challenge')!, redirect, nonce: p.get('nonce') ?? '', oid: this.account.oid });
    return back({ code });
  }

  private token(req: FakeRequest, cors: Record<string, string>): FakeResponse {
    const json = (status: number, body: unknown) => ({ status, headers: { ...cors, 'content-type': 'application/json' }, body: JSON.stringify(body) });
    if (!req.headers.origin) return json(400, { error: 'invalid_request', error_description: 'AADSTS9002327: SPA tokens need an Origin' });
    const f = new URLSearchParams(req.body ?? '');
    if (f.get('client_id') !== this.clientId) return json(400, { error: 'invalid_client' });
    const now = this.clock();
    let oid: string;
    let refreshExpires: number;
    let nonce: string | null = null;
    if (f.get('grant_type') === 'authorization_code') {
      const code = this.codes.get(f.get('code') ?? '');
      this.codes.delete(f.get('code') ?? '');
      if (!code) return json(400, { error: 'invalid_grant', error_description: 'AADSTS70008: code expired or used' });
      if (code.redirect !== f.get('redirect_uri')) return json(400, { error: 'invalid_grant', error_description: 'AADSTS50148: redirect_uri mismatch' });
      const challenge = createHash('sha256').update(f.get('code_verifier') ?? '').digest('base64url');
      if (challenge !== code.challenge) return json(400, { error: 'invalid_grant', error_description: 'AADSTS501481: PKCE verifier mismatch' });
      oid = code.oid;
      nonce = code.nonce;
      refreshExpires = now + this.refreshLifetime;
    } else if (f.get('grant_type') === 'refresh_token') {
      const rt = this.refresh.get(f.get('refresh_token') ?? '');
      if (!rt || rt.expires <= now) {
        return json(400, { error: 'invalid_grant', error_description: 'AADSTS700084: The refresh token was issued to a single page app (SPA), and therefore has a fixed, limited lifetime of 1.00:00:00, which cannot be extended.' });
      }
      oid = rt.oid;
      refreshExpires = rt.expires; // carried over: never extended
    } else {
      return json(400, { error: 'unsupported_grant_type' });
    }
    const access = `at-${++this.n}`;
    const refresh = `rt-${++this.n}`;
    this.access.set(access, now + this.accessLifetime);
    this.refresh.set(refresh, { expires: refreshExpires, oid });
    const account = this.account ?? { username: 'ada@outlook.com', name: 'Ada Lovelace', tid: CONSUMERS, oid };
    const claims = { aud: this.clientId, tid: account.tid, oid, sub: `sub-${oid}`, name: account.name, preferred_username: account.username, ...(nonce ? { nonce } : {}) };
    const jwt = `${b64({ alg: 'none', typ: 'JWT' })}.${b64(claims)}.`;
    return json(200, {
      token_type: 'Bearer',
      scope: 'openid profile offline_access https://graph.microsoft.com/Files.ReadWrite.AppFolder',
      expires_in: Math.round(this.accessLifetime / 1000),
      access_token: access,
      refresh_token: refresh,
      refresh_token_expires_in: Math.max(1, Math.round((refreshExpires - now) / 1000)),
      id_token: jwt,
    });
  }

  private graph(req: FakeRequest, url: URL, cors: Record<string, string>): FakeResponse {
    const json = (status: number, body: unknown, extra: Record<string, string> = {}) => ({ status, headers: { ...cors, 'content-type': 'application/json', ...extra }, body: JSON.stringify(body) });
    const error = (status: number, code: string) => json(status, { error: { code, message: code } });
    const token = (req.headers.authorization ?? '').replace(/^Bearer /, '');
    const expires = this.access.get(token);
    if (!expires || expires <= this.clock()) return error(401, 'InvalidAuthenticationToken');

    const path = decodeURIComponent(url.pathname.replace('/v1.0', ''));
    if (req.method === 'GET' && path === '/me/drive/special/approot') return json(200, this.view(this.appRoot()));

    let m = /^\/me\/drive\/items\/([^/:]+)\/children$/.exec(path);
    if (m && req.method === 'GET') {
      const parent = this.items.get(m[1]!);
      if (!parent) return error(404, 'itemNotFound');
      const all = [...this.items.values()].filter((i) => i.parent === parent.id).sort((a, b) => (a.name < b.name ? -1 : 1));
      const top = Number(url.searchParams.get('$top') ?? 200);
      const skip = Number(url.searchParams.get('$skiptoken') ?? 0);
      const page = all.slice(skip, skip + Math.min(top, 3)); // small pages, so paging is exercised
      const next = skip + page.length < all.length ? `${GRAPH}/me/drive/items/${parent.id}/children?$top=${top}&$skiptoken=${skip + page.length}` : undefined;
      return json(200, { value: page.map((i) => this.view(i)), ...(next ? { '@odata.nextLink': next } : {}) });
    }
    if (m && req.method === 'POST') {
      const parent = this.items.get(m[1]!);
      if (!parent) return error(404, 'itemNotFound');
      const body = JSON.parse(req.body ?? '{}') as { name: string; folder?: object; '@microsoft.graph.conflictBehavior'?: string };
      const existing = [...this.items.values()].find((i) => i.parent === parent.id && i.name === body.name);
      if (existing) {
        if (body['@microsoft.graph.conflictBehavior'] === 'fail') return error(409, 'nameAlreadyExists');
        if (body['@microsoft.graph.conflictBehavior'] === 'replace') this.remove(this.pathOf(existing));
      }
      return json(201, this.view(this.create(parent.id, body.name, true)));
    }
    m = /^\/me\/drive\/items\/([^/:]+):\/(.+):\/content$/.exec(path);
    if (m && req.method === 'PUT') {
      const parent = this.items.get(m[1]!);
      if (!parent) return error(404, 'itemNotFound');
      const existing = [...this.items.values()].find((i) => i.parent === parent.id && i.name === m![2]);
      if (existing && url.searchParams.get('@microsoft.graph.conflictBehavior') === 'fail') return error(409, 'nameAlreadyExists');
      const item = existing ?? this.create(parent.id, m[2]!, false);
      item.text = req.body ?? '';
      item.version++;
      return json(existing ? 200 : 201, this.view(item));
    }
    m = /^\/me\/drive\/items\/([^/:]+)$/.exec(path);
    if (m && req.method === 'GET') {
      const item = this.items.get(m[1]!);
      return item ? json(200, this.view(item)) : error(404, 'itemNotFound');
    }
    m = /^\/me\/drive\/items\/([^/:]+)\/content$/.exec(path);
    if (m && req.method === 'GET') {
      // The real service redirects to another host here, which a browser can't follow with a token.
      return { status: 302, headers: { ...cors, location: `${DOWNLOADS}${m[1]}` }, body: '' };
    }
    return error(400, 'invalidRequest');
  }

  private download(req: FakeRequest, url: URL, cors: Record<string, string>): FakeResponse {
    if (req.headers.authorization) return { status: 401, headers: cors, body: 'no tokens here' };
    const id = url.pathname.split('/').pop()!;
    const until = Number(url.searchParams.get('until'));
    if (!until || until < this.clock()) return { status: 401, headers: cors, body: 'link expired' };
    const item = this.items.get(id);
    if (!item) return { status: 404, headers: cors, body: '' };
    return { status: 200, headers: { ...cors, 'content-type': 'application/octet-stream' }, body: item.text };
  }

  private appRoot(): Item {
    const apps = [...this.items.values()].find((i) => i.parent === 'root' && i.name === 'Apps') ?? this.create('root', 'Apps', true);
    return [...this.items.values()].find((i) => i.parent === apps.id && i.name === 'Tack Focus') ?? this.create(apps.id, 'Tack Focus', true);
  }

  private create(parent: string, name: string, folder: boolean): Item {
    const item: Item = { id: `item${++this.n}`, name, parent, folder, text: '', version: 1 };
    this.items.set(item.id, item);
    return item;
  }

  private view(i: Item): Record<string, unknown> {
    return {
      id: i.id,
      name: i.name,
      eTag: `"{${i.id}},${i.version}"`,
      cTag: `"c:{${i.id}},${i.version}"`,
      size: i.text.length,
      parentReference: { driveId: 'drive-ada', id: i.parent },
      ...(i.folder ? { folder: { childCount: [...this.items.values()].filter((c) => c.parent === i.id).length } } : { file: { mimeType: 'application/octet-stream' } }),
      ...(i.folder ? {} : { '@microsoft.graph.downloadUrl': `${DOWNLOADS}${i.id}?v=${i.version}&until=${this.clock() + this.linkLifetime}` }),
    };
  }
}

function b64(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString('base64url');
}
