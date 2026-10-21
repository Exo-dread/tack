import { SyncError, type FolderEntry, type SyncFolder } from './engine';

/**
 * The sync folder in OneDrive, through Microsoft Graph. With the Files.ReadWrite.AppFolder
 * permission, Tack can reach exactly one folder in your OneDrive, "Apps/Tack Focus" (the name is
 * the app's), and nothing else. Inside it:
 *
 *   tack-sync.json     a marker, so Tack for Windows can find this folder on your PC
 *   README.txt         what this folder is, for anyone who opens it
 *   devices/<id>.json  one file per device (see devicefile.ts)
 *
 * Every request carries a short-lived access token for Graph only; file contents are fetched
 * from OneDrive's pre-signed download links, which need (and get) no token.
 */

const GRAPH = 'https://graph.microsoft.com/v1.0';
const TIMEOUT = 30_000;
const DEVICES = 'devices';
const MARKER = 'tack-sync.json';
const README = 'README.txt';

const MARKER_TEXT = `${JSON.stringify({ format: 'tack-sync', version: 1 }, null, 2)}\n`;
const README_TEXT = [
  'This folder keeps Tack in sync across your devices (Tack Focus on your phone, Tack for Windows).',
  '',
  'Each device keeps its own file in "devices". Please don\'t edit or move them; if you delete',
  'this folder, your devices keep their tasks and simply write it again.',
  '',
  'Tack has no server: these files go only between your devices and your OneDrive.',
  'More: https://tackfocus.app/ and https://github.com/Exo-dread/tack/blob/main/docs/SYNC.md',
  '',
].join('\r\n');

interface DriveItem {
  id: string;
  name: string;
  eTag?: string;
  cTag?: string;
  folder?: unknown;
  file?: unknown;
  size?: number;
  parentReference?: { driveId?: string };
  '@microsoft.graph.downloadUrl'?: string;
}

interface OneDriveEntry extends FolderEntry {
  id: string;
  url?: string;
}

export class OneDriveFolder implements SyncFolder {
  private root: { id: string; driveId: string } | null = null;
  private devices: string | null = null;

  constructor(
    /** A valid access token (refreshed if needed); `force` asks for a new one after a 401. */
    private readonly token: (force?: boolean) => Promise<string>,
    private readonly http: typeof fetch = (...args) => fetch(...args),
  ) {}

  /** Finds (or creates) the folder; returns a key that changes if it's a different folder. */
  async open(): Promise<string> {
    const root = await this.json<DriveItem>(`${GRAPH}/me/drive/special/approot`);
    this.root = { id: root.id, driveId: root.parentReference?.driveId ?? '' };
    const children = await this.children(root.id);
    let devices = children.find((c) => c.name === DEVICES && c.folder);
    if (!devices) {
      try {
        devices = await this.json<DriveItem>(`${GRAPH}/me/drive/items/${root.id}/children`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          // "fail", never "replace": another device may have just made it, with files inside.
          body: JSON.stringify({ name: DEVICES, folder: {}, '@microsoft.graph.conflictBehavior': 'fail' }),
        });
      } catch (error) {
        if (!(error instanceof GraphError && error.status === 409)) throw error;
        devices = (await this.children(root.id)).find((c) => c.name === DEVICES && c.folder);
        if (!devices) throw error;
      }
    }
    this.devices = devices.id;
    for (const [name, text] of [
      [MARKER, MARKER_TEXT],
      [README, README_TEXT],
    ] as const) {
      if (!children.some((c) => c.name === name)) {
        await this.put(`${GRAPH}/me/drive/items/${root.id}:/${name}:/content?@microsoft.graph.conflictBehavior=fail`, text, true);
      }
    }
    return `${this.root.driveId}:${root.id}`;
  }

  async list(): Promise<OneDriveEntry[]> {
    const id = await this.devicesId();
    const items = await this.children(id);
    return items.filter((i) => i.file).map((i) => ({ name: i.name, tag: i.cTag ?? i.eTag ?? '', id: i.id, url: i['@microsoft.graph.downloadUrl'], size: i.size }));
  }

  async read(entry: FolderEntry): Promise<string> {
    const e = entry as OneDriveEntry;
    let url = e.url;
    for (let attempt = 0; attempt < 2; attempt++) {
      // Contents come from a pre-signed download link, fetched without the token (Graph's own
      // /content answers with a redirect that browsers can't follow across origins).
      url ??= (await this.json<DriveItem>(`${GRAPH}/me/drive/items/${e.id}`))['@microsoft.graph.downloadUrl'];
      if (!url) throw new SyncError('server', 'OneDrive gave no download link');
      let response: Response;
      try {
        response = await this.http(url, { signal: AbortSignal.timeout(TIMEOUT), cache: 'no-store' });
      } catch (error) {
        throw new SyncError('offline', `Couldn't download from OneDrive (${String(error)})`);
      }
      if (response.ok) return response.text();
      if (response.status === 404) throw new SyncError('gone');
      url = undefined; // the link expired: ask for a fresh one
    }
    throw new SyncError('server', 'OneDrive download failed');
  }

  async write(name: string, text: string): Promise<FolderEntry> {
    for (let attempt = 0; ; attempt++) {
      const id = await this.devicesId();
      try {
        const item = await this.put(`${GRAPH}/me/drive/items/${id}:/${encodeURIComponent(name)}:/content`, text);
        return { name: item!.name, tag: item!.cTag ?? item!.eTag ?? '' };
      } catch (error) {
        // The folder was deleted (by you, in OneDrive): make it again and retry once.
        if (attempt === 0 && error instanceof SyncError && error.kind === 'gone') {
          this.devices = null;
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

  private async children(id: string): Promise<DriveItem[]> {
    const out: DriveItem[] = [];
    let url: string | undefined = `${GRAPH}/me/drive/items/${id}/children?$top=200`;
    while (url) {
      const page: { value: DriveItem[]; '@odata.nextLink'?: string } = await this.json(url);
      out.push(...page.value);
      url = page['@odata.nextLink'];
    }
    return out;
  }

  private async put(url: string, text: string, conflictOk = false): Promise<DriveItem | null> {
    try {
      const response = await this.request(url, { method: 'PUT', headers: { 'Content-Type': 'text/plain; charset=utf-8' }, body: text });
      return (await response.json()) as DriveItem;
    } catch (error) {
      if (conflictOk && error instanceof GraphError && error.status === 409) return null;
      throw error;
    }
  }

  private async json<T>(url: string, init: RequestInit = {}): Promise<T> {
    const response = await this.request(url, init);
    return (await response.json()) as T;
  }

  /** A Graph request with the token, turning every failure into a SyncError the scheduler understands. */
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
        throw new SyncError('offline', `Couldn't reach OneDrive (${String(error)})`);
      }
      if (response.ok) return response;
      if (response.status === 401 && attempt === 0) continue; // token expired early: once more with a new one
      throw await graphError(response);
    }
  }
}

/** A failed Graph request, with its HTTP status. */
export class GraphError extends SyncError {
  constructor(
    kind: SyncError['kind'],
    message: string,
    readonly status: number,
    retryAfter?: number,
  ) {
    super(kind, message, retryAfter);
  }
}

async function graphError(response: Response): Promise<GraphError> {
  let code = '';
  let message = `${response.status}`;
  try {
    const body = (await response.json()) as { error?: { code?: string; message?: string } };
    code = body.error?.code ?? '';
    message = `${response.status} ${code}: ${body.error?.message ?? ''}`.trim();
  } catch {
    /* no JSON body */
  }
  const retry = Number(response.headers.get('Retry-After'));
  const retryAfter = Number.isFinite(retry) && retry > 0 ? retry * 1000 : undefined;
  const s = response.status;
  if (s === 401) return new GraphError('auth', message, s);
  if (s === 404) return new GraphError('gone', message, s);
  if (s === 429 || s === 503 || s === 509) return new GraphError('throttled', message, s, retryAfter ?? 30_000);
  if (s === 507 || code === 'quotaLimitReached') return new GraphError('full', message, s);
  if (s === 403) return new GraphError('denied', message, s);
  if (s === 409 || s === 412 || s === 423) return new GraphError('server', message, s, 2000);
  if (s >= 500) return new GraphError('server', message, s, retryAfter);
  return new GraphError('denied', message, s);
}
