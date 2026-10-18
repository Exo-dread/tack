import { decode, stampTime, ZERO, type Stamp } from '../model/hlc';
import type { Sheet } from '../model/types';
import type { Workspace } from '../model/workspace';
import { deviceOfFile, DeviceFileError, fileName, makeDeviceFile, parseDeviceFile, serializeDeviceFile, type DeviceFile } from './devicefile';

/**
 * Sync through a shared folder that every device can read and write (OneDrive's app folder, or
 * that same folder on a PC through the OneDrive app). Each device writes only its own file, with
 * everything it knows; each device merges everyone else's. Records merge field by field with
 * clocks (model/merge.ts), so devices that have read the same files agree, whatever the order,
 * however late a file arrives, and however many times it's read.
 *
 * Deletions are kept for good while sync is on (see devicefile.ts), so no copy, however out of
 * date, can bring a deleted task back. Notes written in two places at once are both kept: the
 * `seen` lists in the files say which edits each device had seen when it wrote.
 */

/** A file in the sync folder. `tag` changes whenever its content does. */
export interface FolderEntry {
  name: string;
  tag: string;
}

/** The sync folder, as a provider (OneDrive, a folder on disk, memory in tests) offers it. */
export interface SyncFolder {
  /** The files in the folder (device files and anything else that's there). */
  list(): Promise<FolderEntry[]>;
  read(entry: FolderEntry): Promise<string>;
  /** Creates or replaces a file in one go (readers see the old or the new content, never half). */
  write(name: string, text: string): Promise<FolderEntry>;
}

export type SyncErrorKind =
  /** No connection, or the request didn't complete. Try again when online. */
  | 'offline'
  /** Signed out, or the sign-in expired. */
  | 'auth'
  /** Asked to slow down; `retryAfter` says how long (ms). */
  | 'throttled'
  /** The storage is full. */
  | 'full'
  /** The file vanished between listing and reading (someone deleted it). */
  | 'gone'
  /** The folder can't be reached for a reason that won't fix itself (permissions, misconfiguration). */
  | 'denied'
  /** The service had a problem; try again later. */
  | 'server';

export class SyncError extends Error {
  constructor(
    readonly kind: SyncErrorKind,
    message: string = kind,
    readonly retryAfter?: number,
  ) {
    super(message);
  }
}

/** What a device remembers about another device's file. */
export interface FileSummary {
  tag: string;
  device: string;
  name: string;
  app: string;
  written: Stamp;
}

/** What this device keeps between cycles, next to its data. */
export interface SyncMeta {
  /** Which folder this is about; a different folder (another account) starts afresh. */
  folder: string;
  /** For each other device, the latest version of its file merged here (its `written`). */
  seen: Record<string, Stamp>;
  /** Other devices' files, by file name, as last read. */
  files: Record<string, FileSummary>;
  own: {
    tag: string | null;
    written: Stamp | null;
    /** A write whose answer never came back: if the folder has it, it's ours. */
    pending: Stamp | null;
  };
  /** When the last cycle completed (ms), for the status line. */
  lastSync: number;
}

export function emptyMeta(folder: string): SyncMeta {
  return { folder, seen: {}, files: {}, own: { tag: null, written: null, pending: null }, lastSync: 0 };
}

export interface PeerInfo {
  device: string;
  name: string;
  app: string;
  /** When its file was last written (ms). */
  written: number;
}

export interface CycleResult {
  /** Devices whose changes were merged in this time. */
  merged: string[];
  wrote: boolean;
  /** Files that couldn't be used, and why. */
  problems: { device: string; reason: DeviceFileError['reason'] }[];
  /** The other devices syncing through this folder. */
  peers: PeerInfo[];
}

/** Another device writes files under this device's id (a copied data folder): take a new id. */
export class DeviceCollision extends Error {}

export interface EngineHooks {
  /** This device's name and app, for its file. */
  label(): { name: string; app: string };
  /** Everything in the workspace is on disk (throws if it can't be saved). */
  flush(): Promise<void>;
  /** Saves `meta` (called before a write, so an interrupted one is recognised later). */
  saveMeta(meta: SyncMeta): Promise<void>;
  /** This device is joining a folder other devices already use (after merging theirs, before its first write). */
  joined?(): void;
}

const nodeOf = (stamp: Stamp): string => decode(stamp)?.node ?? '';
const later = (a: Stamp | undefined, b: Stamp | undefined): Stamp => ((a ?? ZERO) > (b ?? ZERO) ? a! : (b ?? ZERO));

/**
 * Had `device`, whose view of the others is `seen`, seen this stamp? Stamps carry the id of the
 * device that made them, and a device's file has everything it made up to its `written`.
 * Records from tack.json files (device "") count as seen by everyone.
 */
export function seenBy(stamp: Stamp, device: string, seen: Record<string, Stamp>): boolean {
  const node = nodeOf(stamp);
  return node === '' || node === device || (seen[node] ?? ZERO) >= stamp;
}

export class SyncEngine {
  constructor(
    private readonly ws: Workspace,
    private readonly folder: SyncFolder,
    private readonly hooks: EngineHooks,
  ) {}

  /**
   * One exchange with the folder: read what changed, merge it, and write this device's file if
   * it now knows anything its file doesn't say. Every step can be interrupted (network, app
   * closed) and simply runs again next time: merges are idempotent and files are written whole.
   * `meta` is updated in place; save it only if the cycle completes.
   */
  async cycle(meta: SyncMeta, now: number = Date.now()): Promise<CycleResult> {
    const me = this.ws.hlc.node;
    const result: CycleResult = { merged: [], wrote: false, problems: [], peers: [] };
    const entries = (await this.folder.list()).slice().sort((a, b) => (a.name < b.name ? -1 : 1));

    // ---- read what changed
    const fresh: DeviceFile[] = [];
    const present = new Set<string>();
    let ownEntry: FolderEntry | undefined;
    for (const entry of entries) {
      const device = deviceOfFile(entry.name);
      if (!device) continue;
      if (device === me) {
        ownEntry = entry;
        continue;
      }
      present.add(entry.name);
      if (meta.files[entry.name]?.tag === entry.tag) continue;
      try {
        const file = parseDeviceFile(await this.folder.read(entry), device, now);
        fresh.push(file);
        meta.files[entry.name] = { tag: entry.tag, device, name: file.name, app: file.app, written: file.written };
      } catch (error) {
        if (error instanceof DeviceFileError) result.problems.push({ device, reason: error.reason });
        else if (!(error instanceof SyncError && error.kind === 'gone')) throw error;
      }
    }
    for (const name of Object.keys(meta.files)) if (!present.has(name)) delete meta.files[name];

    // ---- this device's own file: still ours, and as written?
    let rewrite = !ownEntry;
    if (ownEntry && ownEntry.tag !== meta.own.tag) {
      let own: DeviceFile | null = null;
      try {
        own = parseDeviceFile(await this.folder.read(ownEntry), me, now);
      } catch (error) {
        if (error instanceof SyncError && error.kind !== 'gone') throw error;
      }
      if (own && (own.written === meta.own.written || own.written === meta.own.pending)) {
        meta.own = { tag: ownEntry.tag, written: own.written, pending: null };
      } else if (own && own.written > (meta.own.written ?? ZERO)) {
        // Someone else writes under this id (a copy of this device's data, started elsewhere).
        throw new DeviceCollision(`Another device writes as ${me}`);
      } else {
        rewrite = true; // damaged, or an older version put back
      }
    }

    // ---- merge what's new
    let news = false;
    const off = this.ws.subscribe((c) => {
      if (c.origin === 'remote') news = true;
    });
    try {
      for (const file of fresh.sort((a, b) => (a.device < b.device ? -1 : 1))) {
        if ((meta.seen[file.device] ?? ZERO) >= file.written) continue; // had it already (perhaps through another device)
        const seenHere = { ...meta.seen };
        // A note changed on both sides is kept twice only if neither side had seen the other's text.
        const concurrent = (local: Sheet, remote: Sheet) => !seenBy(local.clk.text, file.device, file.seen) && !seenBy(remote.clk.text, me, seenHere);
        this.ws.merge({ tasks: file.tasks, sheets: file.sheets, focus: file.focus }, 'remote', concurrent);
        meta.seen[file.device] = later(meta.seen[file.device], file.written);
        // Whatever that device had merged, this one now has too.
        for (const [d, w] of Object.entries(file.seen)) if (d !== me) meta.seen[d] = later(meta.seen[d], w);
        result.merged.push(file.device);
      }
    } finally {
      off();
    }

    if (meta.own.written === null && meta.own.pending === null && present.size > 0) this.hooks.joined?.();

    // ---- write this device's file if it knows something its file doesn't say
    const edited = ownClock(this.ws, me) > (meta.own.written ?? ZERO);
    if (rewrite || edited || news) {
      await this.hooks.flush();
      const written = this.ws.hlc.now();
      meta.own.pending = written;
      await this.hooks.saveMeta(meta);
      const file = makeDeviceFile(this.ws.toData(), { device: me, ...this.hooks.label(), written, seen: meta.seen });
      const entry = await this.folder.write(fileName(me), serializeDeviceFile(file));
      meta.own = { tag: entry.tag, written, pending: null };
      result.wrote = true;
    }

    await this.hooks.flush();
    meta.lastSync = now;
    result.peers = Object.values(meta.files).map((s) => ({ device: s.device, name: s.name, app: s.app, written: stampTime(s.written) }));
    return result;
  }
}

/** The newest stamp this device made that's still in the workspace: anything after its last write is news. */
function ownClock(ws: Workspace, me: string): Stamp {
  let max = ZERO;
  const consider = (s: Stamp) => {
    if (s > max && nodeOf(s) === me) max = s;
  };
  consider(ws.focus.clk);
  for (const t of ws.tasks.values()) Object.values(t.clk).forEach(consider);
  for (const s of ws.sheets.values()) Object.values(s.clk).forEach(consider);
  return max;
}
