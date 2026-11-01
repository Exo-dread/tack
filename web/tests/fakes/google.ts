/**
 * An in-memory Google Drive (API v3), as an app with only the `drive.file` permission sees it:
 * it lists and reads only files it made itself. Files a PC makes (Drive for desktop uploads them)
 * exist, but are invisible to it; renaming or rewriting a file on the PC keeps its id, so a file
 * Tack Focus made stays visible whatever the PC does to it (docs/GOOGLE-DRIVE.md, "What was tested").
 * Only the requests googledrive.ts makes are understood; anything else is a 400.
 */

export const DRIVE = 'https://www.googleapis.com/drive/v3';
export const UPLOAD = 'https://www.googleapis.com/upload/drive/v3';
const FOLDER = 'application/vnd.google-apps.folder';

export interface DriveFile {
  id: string;
  name: string;
  mimeType: string;
  parents: string[];
  appProperties: Record<string, string>;
  content: string;
  version: number;
  createdTime: string;
  trashed: boolean;
  /** Made through the API by this app (else by a PC, through Drive for desktop: invisible). */
  byApp: boolean;
  /** What the listing says its size is, if not its content's (a file that grew since, say). */
  sizeSaid?: number;
  /** Someone else's, shared with you (a Tack folder of theirs, say): never in a listing that asks for 'me' in owners. */
  shared?: boolean;
}

interface Fault {
  match: RegExp;
  method?: string;
  status?: number;
  reason?: string;
  retryAfter?: number;
  abort?: boolean;
  times: number;
}

export class FakeGoogleDrive {
  readonly files = new Map<string, DriveFile>();
  readonly log: { method: string; url: string; authorization: string | null }[] = [];
  /** Most items a listing page holds, whatever was asked (Drive's own is 1000). */
  pageLimit = 200;
  /** Tokens Drive takes. */
  readonly tokens = new Set<string>(['token-1']);
  /** Runs as the app makes a file (another browser making its own at that moment, say). */
  onCreate: ((file: DriveFile) => void) | null = null;
  private faults: Fault[] = [];
  private n = 0;
  private clock = Date.UTC(2026, 10, 1, 9, 0, 0);

  /** The next `times` requests matching `match` fail this way. */
  fail(match: RegExp, options: { status?: number; reason?: string; retryAfter?: number; abort?: boolean; times?: number; method?: string } = {}): void {
    this.faults.push({ match, ...options, times: options.times ?? 1 });
  }

  /** A file or folder made through the API by this app, as Tack Focus (or another browser of it) would. */
  appCreate(name: string, parent: string, options: { folder?: boolean; tack?: string; content?: string; createdTime?: string; id?: string } = {}): DriveFile {
    return this.add({ name, parent, byApp: true, ...options });
  }

  /** A file a PC made in a folder (Drive for desktop uploaded it): it exists, and the app can't see it. */
  pcCreate(name: string, parent: string, content: string): DriveFile {
    return this.add({ name, parent, byApp: false, content });
  }

  /** The PC renames a file: same id, new name. */
  pcRename(id: string, name: string): void {
    const f = this.files.get(id)!;
    f.name = name;
    f.version++;
  }

  /** The PC writes a file in place (or with a temp file moved over it): same id, new content. */
  pcWrite(id: string, content: string): void {
    const f = this.files.get(id)!;
    f.content = content;
    f.version++;
  }

  /** In the trash (by you, in Drive). */
  trash(id: string): void {
    this.files.get(id)!.trashed = true;
  }

  /** Deleted for good, with everything in it. */
  remove(id: string): void {
    for (const f of [...this.files.values()]) if (f.parents.includes(id)) this.remove(f.id);
    this.files.delete(id);
  }

  /** Every file the app made with this `tack` appProperty, not in the trash. */
  tagged(tack: string): DriveFile[] {
    return [...this.files.values()].filter((f) => f.appProperties.tack === tack && !this.inTrash(f));
  }

  /** The files in a folder, not in the trash, made by anyone. */
  childrenOf(id: string): DriveFile[] {
    return [...this.files.values()].filter((f) => f.parents.includes(id) && !this.inTrash(f));
  }

  /** The `devices` folder Tack uses (the oldest, as every device picks). */
  devices(): DriveFile | undefined {
    const root = this.oldest(this.tagged('folder'));
    return root && this.oldest(this.childrenOf(root.id).filter((f) => f.appProperties.tack === 'devices'));
  }

  requests(match: RegExp, method?: string): number {
    return this.log.filter((r) => match.test(r.url) && (!method || r.method === method)).length;
  }

  readonly fetch = async (input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> => {
    const url = String(input instanceof Request ? input.url : input);
    const method = (init.method ?? 'GET').toUpperCase();
    const headers = new Headers(init.headers);
    this.log.push({ method, url, authorization: headers.get('authorization') });
    const fault = this.faults.find((f) => f.match.test(url) && (!f.method || f.method === method));
    if (fault) {
      if (--fault.times <= 0) this.faults.splice(this.faults.indexOf(fault), 1);
      if (fault.abort) throw new TypeError('Failed to fetch');
      return this.error(fault.status ?? 500, fault.reason ?? 'backendError', fault.retryAfter);
    }
    const token = /^Bearer (.+)$/.exec(headers.get('authorization') ?? '')?.[1];
    if (!token || !this.tokens.has(token)) return this.error(401, 'authError');
    return this.handle(method, new URL(url), headers, typeof init.body === 'string' ? init.body : '');
  };

  private handle(method: string, url: URL, headers: Headers, body: string): Response {
    const path = `${url.origin}${url.pathname}`;
    const one = /^https:\/\/www\.googleapis\.com\/(upload\/)?drive\/v3\/files\/([\w-]+)$/.exec(path);
    if (path === `${DRIVE}/files` && method === 'GET') return this.list(url.searchParams);
    if (path === `${DRIVE}/files` && method === 'POST') {
      const meta = JSON.parse(body) as Partial<DriveFile> & { parents?: string[] };
      return this.created(meta, '');
    }
    if (path === `${UPLOAD}/files` && method === 'POST' && url.searchParams.get('uploadType') === 'multipart') {
      const boundary = /boundary=(.+)$/.exec(headers.get('content-type') ?? '')?.[1];
      if (!boundary) return this.error(400, 'badMultipart');
      const parts = body.split(`--${boundary}`).slice(1, -1);
      if (parts.length !== 2 || !body.endsWith(`--${boundary}--`)) return this.error(400, 'badMultipart');
      const [meta, media] = parts.map((p) => {
        const at = p.indexOf('\r\n\r\n');
        return p.slice(at + 4, p.length - 2); // without the closing CRLF
      });
      return this.created(JSON.parse(meta!), media!);
    }
    if (one) {
      const file = this.files.get(one[2]!);
      if (!file || !file.byApp) return this.error(404, 'notFound');
      if (one[1] && method === 'PATCH' && url.searchParams.get('uploadType') === 'media') {
        file.content = body;
        file.version++;
        delete file.sizeSaid;
        return json(this.view(file));
      }
      if (!one[1] && method === 'PATCH') {
        const patch = JSON.parse(body) as { trashed?: boolean };
        if (patch.trashed !== undefined) file.trashed = patch.trashed;
        file.version++;
        return json(this.view(file));
      }
      if (!one[1] && method === 'GET' && url.searchParams.get('alt') === 'media') return new Response(file.content, { status: 200 });
      if (!one[1] && method === 'GET') return json(this.view(file));
    }
    return this.error(400, 'badRequest');
  }

  private created(meta: { name?: string; mimeType?: string; parents?: string[]; appProperties?: Record<string, string> }, content: string): Response {
    const parent = meta.parents?.[0] ?? 'root';
    if (parent !== 'root') {
      const p = this.files.get(parent);
      if (!p || !p.byApp || p.mimeType !== FOLDER) return this.error(404, 'notFound');
    }
    const file = this.add({ name: meta.name ?? 'Untitled', parent, byApp: true, folder: meta.mimeType === FOLDER, content, mimeType: meta.mimeType, appProperties: meta.appProperties });
    this.onCreate?.(file);
    return json(this.view(file));
  }

  /** `q` as googledrive.ts writes it: by appProperty, by parent, or by name and parent; always trashed=false. */
  private list(params: URLSearchParams): Response {
    const q = params.get('q') ?? '';
    let match: ((f: DriveFile) => boolean) | null = null;
    let m: RegExpExecArray | null;
    if ((m = /^appProperties has \{ key='(\w+)' and value='(\w+)' \}( and 'me' in owners)? and trashed=false$/.exec(q))) {
      const [, key, value, mine] = m;
      match = (f) => f.appProperties[key!] === value && !(mine && f.shared);
    } else if ((m = /^'([\w-]+)' in parents and trashed=false$/.exec(q))) {
      const parent = m[1]!;
      match = (f) => f.parents.includes(parent);
    } else if ((m = /^name='((?:[^'\\]|\\.)*)' and '([\w-]+)' in parents and trashed=false$/.exec(q))) {
      const name = m[1]!.replace(/\\(.)/g, '$1');
      const parent = m[2]!;
      match = (f) => f.name === name && f.parents.includes(parent);
    }
    if (!match) return this.error(400, 'invalidQuery');
    const all = [...this.files.values()].filter((f) => f.byApp && !this.inTrash(f) && match(f));
    const size = Math.min(Number(params.get('pageSize') ?? 100), this.pageLimit);
    const from = Number(params.get('pageToken') ?? 0);
    const page = all.slice(from, from + size);
    return json({ files: page.map((f) => this.view(f)), ...(from + size < all.length ? { nextPageToken: String(from + size) } : {}) });
  }

  private add(o: { name: string; parent: string; byApp: boolean; folder?: boolean; tack?: string; content?: string; createdTime?: string; id?: string; mimeType?: string; appProperties?: Record<string, string> }): DriveFile {
    const file: DriveFile = {
      id: o.id ?? `id${String(++this.n).padStart(4, '0')}`,
      name: o.name,
      mimeType: o.folder ? FOLDER : (o.mimeType ?? 'application/json'),
      parents: [o.parent],
      appProperties: o.appProperties ?? (o.tack ? { tack: o.tack } : {}),
      content: o.content ?? '',
      version: 1,
      createdTime: o.createdTime ?? new Date((this.clock += 1000)).toISOString(),
      trashed: false,
      byApp: o.byApp,
    };
    this.files.set(file.id, file);
    return file;
  }

  /** Trashed, or in a folder that is: Drive says `trashed` for both. */
  private inTrash(f: DriveFile): boolean {
    for (let at: DriveFile | undefined = f; at; at = this.files.get(at.parents[0] ?? '')) if (at.trashed) return true;
    return false;
  }

  private oldest(list: DriveFile[]): DriveFile | undefined {
    return list.sort((a, b) => (a.createdTime + a.id < b.createdTime + b.id ? -1 : 1))[0];
  }

  private view(f: DriveFile) {
    const folder = f.mimeType === FOLDER;
    return {
      id: f.id,
      name: f.name,
      mimeType: f.mimeType,
      ...(folder ? {} : { size: String(f.sizeSaid ?? new TextEncoder().encode(f.content).length) }),
      version: String(f.version),
      createdTime: f.createdTime,
      appProperties: f.appProperties,
      trashed: this.inTrash(f),
      parents: f.parents,
    };
  }

  private error(status: number, reason: string, retryAfter?: number): Response {
    const headers: Record<string, string> = { 'content-type': 'application/json' };
    if (retryAfter !== undefined) headers['Retry-After'] = String(retryAfter);
    return new Response(JSON.stringify({ error: { code: status, message: reason, errors: [{ domain: 'global', reason, message: reason }] } }), { status, headers });
  }
}

function json(value: unknown): Response {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'content-type': 'application/json' } });
}
