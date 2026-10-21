import { decode, stampTime, ZERO, type Stamp } from '../model/hlc';
import type { Sheet } from '../model/types';
import type { Workspace } from '../model/workspace';
import { deviceOfFile, DeviceFileError, fileName, makeDeviceFile, MAX_FILE_BYTES, parseDeviceFile, serializeDeviceFile, stampOf, type DeviceFile } from './devicefile';

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
  /** Its size in bytes, if the listing says (a file too big to be Tack's is never downloaded). */
  size?: number;
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
  | 'server'
  /** This device's own file would be over the size every device accepts, so it isn't written. */
  | 'toolarge';

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
  /** This version couldn't be used (and isn't fetched again until it changes). */
  problem?: DeviceFileError['reason'];
  /**
   * The reader that read it (READER). A file read by an older one is read once more, whether or
   * not it changed: that reader dropped what it didn't know (spaces), and only a new read learns it.
   */
  reader?: number;
}

/**
 * This version of the device file reader. Raise it whenever a new version learns to read
 * something older ones dropped: every device then reads every file once more. 2: spaces.
 */
export const READER = 2;

/**
 * This version of the device file writer, noted with each write of this device's own file.
 * Raise it whenever a new version writes something older ones leave out: a file last written by
 * an older version under this device's id (an old tab still open in the same browser, or the app
 * downgraded and upgraded again) is then written again in full, though nothing else changed. 2: spaces.
 */
export const WRITER = 2;

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
    /**
     * The writer (WRITER) of this device's latest write of its file, a pending one included.
     * None: an older version wrote it (it notes its writes without this), so it's written again.
     */
    writer?: number;
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
  return node === '' || node === device || (stampOf(seen, node) ?? ZERO) >= stamp;
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

    // ---- this device's own file first: still ours, and as written? (Before anything else is
    // noted down, so a cycle that stops here leaves nothing half done.)
    const ownEntry = entries.find((e) => deviceOfFile(e.name) === me);
    let damaged = false;
    let adopted: DeviceFile | null = null;
    if (ownEntry && ownEntry.tag !== meta.own.tag) {
      let own: DeviceFile | null = null;
      try {
        if (!tooBig(ownEntry)) own = parseDeviceFile(await this.folder.read(ownEntry), me, now);
      } catch (error) {
        if (error instanceof SyncError && error.kind !== 'gone') throw error;
      }
      if (own && (own.written === meta.own.written || own.written === meta.own.pending)) {
        // The write noted (or the pending one, now arrived): the writer noted with it stays.
        const writer = meta.own.writer;
        meta.own = { tag: ownEntry.tag, written: own.written, pending: null, ...(writer === undefined ? {} : { writer }) };
      } else if (own && meta.own.written === null && meta.own.pending === null) {
        // No memory of ever writing here (this device's sync state was lost): it's this device's
        // own file from before. Take it back rather than mistaking it for another device's.
        adopted = own;
        meta.own = { tag: ownEntry.tag, written: own.written, pending: null };
      } else if (own && own.written > (meta.own.written ?? ZERO)) {
        // Someone else writes under this id (a copy of this device's data, started elsewhere).
        throw new DeviceCollision(`Another device writes as ${me}`);
      } else {
        damaged = true; // damaged, or an older version put back
      }
    }
    // Missing, damaged, a write whose arrival was never confirmed, or one by an older version (it
    // leaves out what it doesn't know, such as spaces): say it all again.
    const rewrite = !ownEntry || damaged || meta.own.pending !== null || meta.own.writer !== WRITER;

    // ---- read what changed
    const fresh: DeviceFile[] = [];
    const present = new Set<string>();
    for (const entry of entries) {
      const device = deviceOfFile(entry.name);
      if (!device || device === me) continue;
      present.add(entry.name);
      const known = Object.hasOwn(meta.files, entry.name) ? meta.files[entry.name] : undefined;
      // Unchanged since this reader read it (one before it may have dropped what it couldn't read).
      if (known?.tag === entry.tag && known.reader === READER) {
        if (known.problem) result.problems.push({ device, reason: known.problem });
        continue;
      }
      // Noted, so it isn't fetched again until it changes.
      const unusable = (reason: DeviceFileError['reason']) => {
        result.problems.push({ device, reason });
        meta.files[entry.name] = { tag: entry.tag, device, name: known?.name ?? '', app: known?.app ?? '', written: known?.written ?? ZERO, problem: reason, reader: READER };
      };
      if (tooBig(entry)) {
        unusable('unreadable'); // never downloaded
        continue;
      }
      try {
        const file = parseDeviceFile(await this.folder.read(entry), device, now);
        fresh.push(file);
        meta.files[entry.name] = { tag: entry.tag, device, name: file.name, app: file.app, written: file.written, reader: READER };
      } catch (error) {
        if (error instanceof DeviceFileError) unusable(error.reason);
        else if (error instanceof SyncError && (error.kind === 'offline' || error.kind === 'server')) {
          // This one file couldn't be fetched (a dropped download, a link that failed): the rest
          // still sync, and it's tried again next time. Merges don't depend on order.
        } else if (!(error instanceof SyncError && error.kind === 'gone')) throw error;
      }
    }
    for (const name of Object.keys(meta.files)) if (!present.has(name)) delete meta.files[name];

    // ---- merge what's new
    let news = false;
    const off = this.ws.subscribe((c) => {
      if (c.origin === 'remote') news = true;
    });
    try {
      if (adopted) this.ws.merge({ tasks: adopted.tasks, sheets: adopted.sheets, focus: adopted.focus, spaces: adopted.spaces }, 'remote');
      for (const file of fresh.sort((a, b) => (a.device < b.device ? -1 : 1))) {
        // Every changed file is merged, even one that seems already had through another device:
        // merges are idempotent, and skipping on another file's word would let one bad file
        // hide a device's changes from everyone.
        const seenHere = { ...meta.seen };
        // A note changed on both sides is kept twice only if neither side had seen the other's text.
        const concurrent = (local: Sheet, remote: Sheet) => !seenBy(local.clk.text, file.device, file.seen) && !seenBy(remote.clk.text, me, seenHere);
        this.ws.merge({ tasks: file.tasks, sheets: file.sheets, focus: file.focus, spaces: file.spaces }, 'remote', concurrent);
        meta.seen[file.device] = later(stampOf(meta.seen, file.device), file.written);
        // Whatever that device had merged, this one now has too; no device can have seen past its
        // own clock, so a claim beyond the file's `written` is cut back to it.
        for (const [d, w] of Object.entries(file.seen)) {
          if (d !== me) meta.seen[d] = later(stampOf(meta.seen, d), w < file.written ? w : file.written);
        }
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
      // The clock and the records together, with no await between: the file holds exactly what
      // this device knew at `written`, and anything later is news for the next write.
      const written = this.ws.hlc.now();
      const file = makeDeviceFile(this.ws.toData(), { device: me, ...this.hooks.label(), written, seen: meta.seen });
      const text = serializeDeviceFile(file);
      // Every device refuses a file this big, so writing it would only look like syncing.
      if (new TextEncoder().encode(text).length > MAX_FILE_BYTES) throw new SyncError('toolarge', 'This device’s sync file would be over 20 MB');
      meta.own.pending = written;
      meta.own.writer = WRITER;
      await this.hooks.saveMeta(meta);
      const entry = await this.folder.write(fileName(me), text);
      meta.own = { tag: entry.tag, written, pending: null, writer: WRITER };
      result.wrote = true;
    }

    await this.hooks.flush();
    meta.lastSync = now;
    result.peers = Object.values(meta.files).map((s) => ({ device: s.device, name: s.name, app: s.app, written: stampTime(s.written) }));
    return result;
  }
}

/** A file the listing already shows to be too big to be Tack's. */
const tooBig = (entry: FolderEntry): boolean => entry.size !== undefined && entry.size > MAX_FILE_BYTES;

/** The newest stamp this device made that's still in the workspace: anything after its last write is news. */
function ownClock(ws: Workspace, me: string): Stamp {
  let max = ZERO;
  const consider = (s: Stamp) => {
    if (s > max && nodeOf(s) === me) max = s;
  };
  consider(ws.focus.clk);
  // Every clock a record has, a move to another space (clk.space) included.
  for (const t of ws.tasks.values()) Object.values(t.clk).forEach(consider);
  for (const s of ws.sheets.values()) Object.values(s.clk).forEach(consider);
  for (const s of ws.spaces.values()) Object.values(s.clk).forEach(consider);
  return max;
}
