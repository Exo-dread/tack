import { deviceOfFile, DeviceFileError, MAX_FILE_BYTES } from './devicefile';
import { retryAfterMs, SyncError, type FolderEntry, type SyncFolder } from './engine';
import { MAX_PAGES, textAtMost } from './onedrive';

/**
 * The sync folder in Google Drive, through the Drive API (v3), with the `drive.file` permission:
 * Tack sees only the files it made itself, nothing else in your Drive. So Tack Focus makes every
 * file in the folder, and Tack for Windows only ever writes into files Tack Focus made (see
 * docs/GOOGLE-DRIVE.md). In "My Drive":
 *
 *   Tack Focus/                 found by its appProperties, so renaming it doesn't lose it
 *     tack-sync.json            the marker, with "spares": true for Tack for Windows
 *     README.txt                what this folder is, for anyone who opens it
 *     devices/<id>.json         one file per device (see devicefile.ts)
 *     devices/spare-<hex>.json  empty files a PC renames to its own name, as Drive for desktop
 *                               uploads count as made by Drive, which Tack couldn't see
 *
 * Every request carries a short-lived access token (gauth.ts) for www.googleapis.com only.
 */

const DRIVE = 'https://www.googleapis.com/drive/v3';
const UPLOAD = 'https://www.googleapis.com/upload/drive/v3';
const TIMEOUT = 30_000;
const FOLDER_TYPE = 'application/vnd.google-apps.folder';
const FOLDER_NAME = 'Tack Focus';
const DEVICES = 'devices';
const MARKER = 'tack-sync.json';
const README = 'README.txt';
/** Unclaimed spares kept in devices/ (a PC claims one the first time it syncs). */
const SPARES = 3;
/** More names than this that look like spares, and Tack makes no more: something else is making them. */
const MAX_SPARE_NAMES = 20;
const SPARE = /^spare-[0-9a-f]{16}\.json$/;
const SPARE_LIKE = /^spare-/i;
/** Drive's ids, the only thing from a listing that goes into a request. */
const DRIVE_ID = /^[\w-]{1,256}$/;
const LISTED = 'id,name,mimeType,size,version,appProperties';
const CHILD = 'id,name,mimeType,createdTime,appProperties';
const WRITTEN = 'id,name,version,size';

const MARKER_TEXT = `${JSON.stringify({ format: 'tack-sync', version: 1, spares: true }, null, 2)}\n`;
const README_TEXT = [
  'This folder keeps Tack in sync across your devices (Tack Focus on your phone, Tack for Windows).',
  '',
  'Each device keeps its own file in "devices", beside a few empty "spare" files that Tack for',
  'Windows takes for its own. Please don\'t edit, move or rename them; if you delete this folder,',
  'your devices keep their tasks, and Tack Focus makes it again.',
  '',
  'Tack can open only the files it made here, nothing else in your Google Drive. It has no server',
  'for your data: these files go only between your devices and your Google Drive.',
  'More: https://tackfocus.app/ and https://github.com/Exo-dread/tack/blob/main/docs/SYNC.md',
  '',
].join('\r\n');

interface DriveFile {
  id: string;
  name: string;
  mimeType?: string;
  size?: string;
  version?: string;
  createdTime?: string;
  appProperties?: Record<string, string>;
  trashed?: boolean;
}

interface GoogleDriveEntry extends FolderEntry {
  id: string;
}

/** What a file is to Tack (its `tack` appProperty), as Tack Focus set it when it made the file. */
type Role = 'folder' | 'marker' | 'readme' | 'devices' | 'spare' | 'device';

export class GoogleDriveFolder implements SyncFolder {
  private devices: string | null = null;
  /** Device files by name, from the last listing: this device's own is updated by id. */
  private known = new Map<string, string>();

  constructor(
    /** A valid access token; `force` asks for a new one after a 401. */
    private readonly token: (force?: boolean) => Promise<string>,
    private readonly http: typeof fetch = (...args) => fetch(...args),
  ) {}

  /** Finds (or makes) the folder; returns a key that changes if it's a different folder. */
  async open(): Promise<string> {
    this.devices = null;
    this.known.clear();
    const root = await this.once(
      // Yours only: a Tack folder someone shared with you is never taken for your own.
      async () => oldest(await this.query("appProperties has { key='tack' and value='folder' } and 'me' in owners and trashed=false", CHILD), (f) => f.mimeType === FOLDER_TYPE),
      () => this.makeFolder(FOLDER_NAME, 'root', 'folder'),
    );
    const children = await this.query(`'${root.id}' in parents and trashed=false`, CHILD);
    const tagged = (role: Role, folder: boolean) => (list: DriveFile[]) => oldest(list, (f) => f.appProperties?.tack === role && (f.mimeType === FOLDER_TYPE) === folder);
    const inRoot = async () => this.query(`'${root.id}' in parents and trashed=false`, CHILD);
    const devices = tagged('devices', true)(children) ?? (await this.once(async () => tagged('devices', true)(await inRoot()), () => this.makeFolder(DEVICES, root.id, 'devices')));
    if (!tagged('marker', false)(children)) await this.upload(MARKER, MARKER_TEXT, root.id, 'marker');
    if (!tagged('readme', false)(children)) await this.upload(README, README_TEXT, root.id, 'readme', 'text/plain');
    this.devices = devices.id;
    return `google:${root.id}`;
  }

  async list(): Promise<GoogleDriveEntry[]> {
    const devices = await this.devicesId();
    // A folder in the trash still answers, and so do the files in it: sync stops there, and the
    // next cycle finds (or makes) the folder afresh.
    const folder = await this.json<DriveFile>(`${DRIVE}/files/${devices}?fields=id,trashed`);
    if (folder.trashed) {
      this.devices = null;
      throw new SyncError('gone', 'Tack’s folder in Google Drive is in the trash');
    }
    const files = (await this.query(`'${devices}' in parents and trashed=false`, LISTED)).filter((f) => f.mimeType !== FOLDER_TYPE);
    this.known.clear();
    for (const f of files) {
      // Two files with this device's name: its own (made as a device file) is the one it writes.
      if (deviceOfFile(f.name) && (!this.known.has(f.name) || f.appProperties?.tack === 'device')) this.known.set(f.name, f.id);
    }
    const spares = files.filter((f) => SPARE.test(f.name)).length;
    if (spares < SPARES && files.filter((f) => SPARE_LIKE.test(f.name)).length <= MAX_SPARE_NAMES) await this.topUp(SPARES - spares, devices);
    return files.map((f) => {
      const size = Number(f.size);
      return { name: f.name, tag: typeof f.version === 'string' ? f.version : '', id: f.id, size: f.size !== undefined && Number.isFinite(size) ? size : undefined };
    });
  }

  async read(entry: FolderEntry): Promise<string> {
    const e = entry as GoogleDriveEntry;
    if (!e.id || !DRIVE_ID.test(e.id)) throw new SyncError('gone');
    if (e.size !== undefined && e.size > MAX_FILE_BYTES) throw new DeviceFileError('unreadable');
    const response = await this.request(`${DRIVE}/files/${e.id}?alt=media`, { cache: 'no-store' });
    return textAtMost(response, MAX_FILE_BYTES);
  }

  /** Writes this device's own file: by id once it exists, made (as a device file) the first time. Never a spare. */
  async write(name: string, text: string): Promise<FolderEntry> {
    if (!deviceOfFile(name)) throw new SyncError('denied', 'Tack writes only device files');
    for (let attempt = 0; ; attempt++) {
      const devices = await this.devicesId();
      try {
        const id = this.known.get(name) ?? (await this.findOwn(name, devices));
        const file = id
          ? await this.made(`${UPLOAD}/files/${id}?uploadType=media&fields=${WRITTEN}`, {
              method: 'PATCH',
              headers: { 'Content-Type': 'application/json' },
              body: text,
            })
          : await this.upload(name, text, devices, 'device');
        this.known.set(name, file.id);
        return { name: file.name, tag: file.version ?? '' };
      } catch (error) {
        // The file or the folder was deleted (by you, in Google Drive): find or make it again, and retry once.
        if (attempt === 0 && error instanceof SyncError && error.kind === 'gone') {
          this.known.delete(name);
          await this.open();
          continue;
        }
        throw error;
      }
    }
  }

  private async devicesId(): Promise<string> {
    if (!this.devices) await this.open();
    return this.devices!;
  }

  /** This device's file, if it's in devices/ though the last listing didn't have it. */
  private async findOwn(name: string, devices: string): Promise<string | null> {
    const found = await this.query(`name='${quoted(name)}' and '${devices}' in parents and trashed=false`, LISTED);
    return (found.find((f) => f.name === name && f.appProperties?.tack === 'device') ?? found.find((f) => f.name === name))?.id ?? null;
  }

  /** Makes up to `count` spares (empty files, for PCs to claim). One that can't be made waits for the next cycle. */
  private async topUp(count: number, devices: string): Promise<void> {
    for (let i = 0; i < Math.min(count, SPARES); i++) {
      try {
        await this.upload(`spare-${randomHex(8)}.json`, '', devices, 'spare');
      } catch (error) {
        // Asked to slow down, or signed out: that stops the cycle. Anything else, next time.
        if (error instanceof SyncError && (error.kind === 'throttled' || error.kind === 'auth')) throw error;
        return;
      }
    }
  }

  /**
   * Finds something, or makes it. Two browsers connecting at once may both make it: the one made
   * first (createdTime, then id) is the one every device uses, and the other, still empty, goes to the trash.
   */
  private async once(find: () => Promise<DriveFile | null>, make: () => Promise<DriveFile>): Promise<DriveFile> {
    const found = await find();
    if (found) return found;
    const made = await make();
    const winner = (await find()) ?? made;
    if (winner.id !== made.id) {
      await this.json(`${DRIVE}/files/${made.id}?fields=id`, { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ trashed: true }) }).catch((error: unknown) => {
        if (error instanceof SyncError && error.kind === 'throttled') throw error;
      });
    }
    return winner;
  }

  private makeFolder(name: string, parent: string, role: Role): Promise<DriveFile> {
    return this.made(`${DRIVE}/files?fields=${CHILD}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, mimeType: FOLDER_TYPE, parents: [parent], appProperties: { tack: role } }),
    });
  }

  /** A new file with its content, in one request (a multipart upload). */
  private upload(name: string, text: string, parent: string, role: Role, type = 'application/json'): Promise<DriveFile> {
    let boundary = `tack${randomHex(16)}`;
    while (text.includes(boundary)) boundary = `tack${randomHex(16)}`;
    const metadata = JSON.stringify({ name, mimeType: type, parents: [parent], appProperties: { tack: role } });
    const body = `--${boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n${metadata}\r\n--${boundary}\r\nContent-Type: ${type}\r\n\r\n${text}\r\n--${boundary}--`;
    return this.made(`${UPLOAD}/files?uploadType=multipart&fields=${WRITTEN}`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/related; boundary=${boundary}` },
      body,
    });
  }

  /** Every file a query finds, page by page: at most MAX_PAGES, or 'crowded'. Only well-formed entries. */
  private async query(q: string, fields: string): Promise<DriveFile[]> {
    const out: DriveFile[] = [];
    let pageToken = '';
    for (let pages = 0; ; pages++) {
      if (pages === MAX_PAGES) throw new SyncError('crowded', 'Tack’s folder in Google Drive holds far more files than Tack keeps there');
      const params = new URLSearchParams({ q, fields: `nextPageToken,files(${fields})`, pageSize: '200', spaces: 'drive' });
      if (pageToken) params.set('pageToken', pageToken);
      const page = await this.json<{ files?: unknown; nextPageToken?: unknown }>(`${DRIVE}/files?${params}`);
      if (Array.isArray(page.files)) out.push(...page.files.filter(isDriveFile));
      pageToken = typeof page.nextPageToken === 'string' ? page.nextPageToken : '';
      if (!pageToken) return out;
    }
  }

  /** A file Drive made or changed, as it answers: its id is used in later requests, so it must be one. */
  private async made(url: string, init: RequestInit): Promise<DriveFile> {
    const file = await this.json<unknown>(url, init);
    if (!isDriveFile(file)) throw new SyncError('server', 'Google Drive gave an unexpected answer');
    return file;
  }

  private async json<T>(url: string, init: RequestInit = {}): Promise<T> {
    const response = await this.request(url, init);
    return (await response.json()) as T;
  }

  /** A Drive request with the token, turning every failure into a SyncError the scheduler understands. */
  private async request(url: string, init: RequestInit = {}): Promise<Response> {
    for (let attempt = 0; ; attempt++) {
      const token = await this.token(attempt > 0);
      let response: Response;
      try {
        response = await this.http(url, {
          ...init,
          headers: { ...(init.headers as Record<string, string>), Authorization: `Bearer ${token}` },
          signal: AbortSignal.timeout(TIMEOUT),
        });
      } catch (error) {
        throw new SyncError('offline', `Couldn't reach Google Drive (${String(error)})`);
      }
      if (response.ok) return response;
      if (response.status === 401 && attempt === 0) continue; // token expired early: once more with a new one
      throw await driveError(response);
    }
  }
}

const isDriveFile = (f: unknown): f is DriveFile =>
  !!f && typeof f === 'object' && typeof (f as DriveFile).id === 'string' && DRIVE_ID.test((f as DriveFile).id) && typeof (f as DriveFile).name === 'string';

/** The first made of those that `match` (createdTime, then id), so every device picks the same one. */
function oldest(files: DriveFile[], match: (f: DriveFile) => boolean): DriveFile | null {
  const key = (f: DriveFile) => `${f.createdTime ?? ''}\u0000${f.id}`;
  return files.filter(match).sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0))[0] ?? null;
}

/** A string inside a Drive query's quotes. */
export function quoted(value: string): string {
  return value.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
}

function randomHex(bytes: number): string {
  const b = new Uint8Array(bytes);
  crypto.getRandomValues(b);
  return Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
}

/** A failed Drive request, with its HTTP status. */
export class DriveError extends SyncError {
  constructor(
    kind: SyncError['kind'],
    message: string,
    readonly status: number,
    retryAfter?: number,
  ) {
    super(kind, message, retryAfter);
  }
}

async function driveError(response: Response): Promise<DriveError> {
  let reasons: string[] = [];
  let message = `${response.status}`;
  try {
    const body = (await response.json()) as { error?: { message?: unknown; errors?: unknown } };
    const errors = Array.isArray(body.error?.errors) ? (body.error.errors as { reason?: unknown }[]) : [];
    reasons = errors.map((e) => (typeof e?.reason === 'string' ? e.reason : ''));
    message = `${response.status} ${reasons.join(',')}: ${typeof body.error?.message === 'string' ? body.error.message : ''}`.trim();
  } catch {
    /* no JSON body */
  }
  const retryAfter = retryAfterMs(response);
  const s = response.status;
  if (s === 401) return new DriveError('auth', message, s);
  if (s === 404) return new DriveError('gone', message, s);
  if (s === 429 || s === 500 || s === 502 || s === 503 || s === 504 || (s === 403 && reasons.some((r) => r === 'rateLimitExceeded' || r === 'userRateLimitExceeded'))) {
    return new DriveError('throttled', message, s, retryAfter ?? 30_000);
  }
  if (s === 403 && reasons.includes('storageQuotaExceeded')) return new DriveError('full', message, s);
  if (s === 403) return new DriveError('denied', message, s);
  if (s === 409 || s === 412) return new DriveError('server', message, s, 2000);
  if (s >= 500) return new DriveError('server', message, s, retryAfter);
  return new DriveError('denied', message, s);
}
