import { Hlc } from '../../src/model/hlc';
import { newDeviceId } from '../../src/model/ids';
import { effectiveSpace } from '../../src/model/spaces';
import { Workspace } from '../../src/model/workspace';
import { DeviceCollision, emptyMeta, SyncEngine, SyncError, WRITER, type CycleResult, type FolderEntry, type SyncFolder, type SyncMeta } from '../../src/sync/engine';

export const DAY = 86_400_000;

/**
 * A device file as a version before spaces reads it, and writes it: no `space` (or clock for it)
 * on any record, no spaces, no deleted spaces. Anything that isn't a device file stays as it is.
 */
export function beforeSpaces(text: string): string {
  let doc: Record<string, any>;
  try {
    doc = JSON.parse(text);
  } catch {
    return text;
  }
  if (!doc || typeof doc !== 'object' || Array.isArray(doc)) return text;
  for (const list of [doc.tasks, doc.sheets]) {
    for (const r of Array.isArray(list) ? list : []) {
      if (!r || typeof r !== 'object') continue;
      delete r.space;
      if (r.clk && typeof r.clk === 'object') delete r.clk.space;
    }
  }
  delete doc.spaces;
  if (doc.deleted && typeof doc.deleted === 'object') delete doc.deleted.spaces;
  return JSON.stringify(doc);
}

/** Sync state as a version before spaces keeps it: no reader on any file it read. */
export function withoutReader(meta: SyncMeta): SyncMeta {
  for (const f of Object.values(meta.files)) delete f.reader;
  return meta;
}

/**
 * A version before spaces never looks at the writer mark: a cycle it runs starts with the mark
 * set (so this version's engine, standing in for it, writes only for that version's reasons).
 * Returns `own` as the cycle found it, for asOldVersion.
 */
export function asOldCycle(meta: SyncMeta): { tag: string | null; writer?: number } {
  const found = { tag: meta.own.tag, writer: meta.own.writer };
  meta.own.writer = WRITER;
  return found;
}

/**
 * Sync state as a version before spaces leaves it (changed in place): no reader on any file it
 * read, and no writer mark once it has replaced `own` (it writes its own file, or finds it there,
 * and notes it anew), else the mark as it found it. `found`: from asOldCycle.
 */
export function asOldVersion(meta: SyncMeta, found: { tag: string | null; writer?: number }): SyncMeta {
  withoutReader(meta);
  if (meta.own.tag !== found.tag || found.writer === undefined) delete meta.own.writer;
  else meta.own.writer = found.writer;
  return meta;
}

/** A file's size in bytes, as a listing reports it. */
const bytes = (text: string): number => new TextEncoder().encode(text).length;

/** The shared folder in the cloud (OneDrive's app folder, say). */
export class Cloud {
  readonly files = new Map<string, { text: string; tag: string }>();
  private n = 0;

  put(name: string, text: string): FolderEntry {
    const tag = `t${++this.n}`;
    this.files.set(name, { text, tag });
    return { name, tag };
  }

  list(): FolderEntry[] {
    return [...this.files].map(([name, f]) => ({ name, tag: f.tag, size: bytes(f.text) }));
  }
}

export type Fault = 'offline' | 'lost-response' | 'throttled' | 'server';

/**
 * How one device sees the cloud folder. A browser sees it directly (Graph); a PC sees its own copy,
 * which the OneDrive app keeps in step only now and then (`lag`): `propagate()` is that app.
 */
export class FolderView implements SyncFolder {
  /** Faults to inject: the next calls of each kind fail this way. */
  faults: { list?: Fault[]; read?: Fault[]; write?: Fault[] } = {};
  /** Files read, by name (to check nothing is fetched needlessly). */
  readonly reads = new Map<string, number>();
  private local = new Map<string, { text: string; tag: string }>();
  private outbox = new Map<string, string>();
  private n = 0;

  constructor(
    readonly cloud: Cloud,
    readonly lag = false,
  ) {}

  /** The OneDrive app: uploads this PC's changes and downloads everyone else's. */
  propagate(): void {
    for (const [name, text] of this.outbox) this.cloud.put(name, text);
    this.outbox.clear();
    this.local = new Map([...this.cloud.files].map(([name, f]) => [name, { text: f.text, tag: f.tag }]));
  }

  private fault(op: 'list' | 'read' | 'write'): Fault | undefined {
    return this.faults[op]?.shift();
  }

  private throwFor(f: Fault): never {
    if (f === 'throttled') throw new SyncError('throttled', 'slow down', 1000);
    if (f === 'server') throw new SyncError('server', 'service unavailable');
    throw new SyncError('offline', 'no connection');
  }

  async list(): Promise<FolderEntry[]> {
    const f = this.fault('list');
    if (f) this.throwFor(f);
    if (this.lag) return [...this.local].map(([name, x]) => ({ name, tag: x.tag, size: bytes(x.text) }));
    return this.cloud.list();
  }

  async read(entry: FolderEntry): Promise<string> {
    const f = this.fault('read');
    if (f) this.throwFor(f);
    this.reads.set(entry.name, (this.reads.get(entry.name) ?? 0) + 1);
    const file = this.lag ? this.local.get(entry.name) : this.cloud.files.get(entry.name);
    if (!file) throw new SyncError('gone');
    return file.text;
  }

  async write(name: string, text: string): Promise<FolderEntry> {
    const f = this.fault('write');
    if (f && f !== 'lost-response') this.throwFor(f);
    let entry: FolderEntry;
    if (this.lag) {
      const tag = `w${++this.n}`;
      this.local.set(name, { text, tag });
      this.outbox.set(name, text);
      entry = { name, tag };
    } else {
      entry = this.cloud.put(name, text);
    }
    if (f === 'lost-response') throw new SyncError('offline', 'the answer never came');
    return entry;
  }
}

export interface Clock {
  now: number;
}

/** A device: its workspace, what it remembers about the folder, and its engine. */
export class Device {
  /** How often a device found its id taken (for checking that tests reach that path). */
  static collisions = 0;
  readonly ws: Workspace;
  readonly view: FolderView;
  meta: SyncMeta;
  saved: SyncMeta;
  skew = 0;
  /**
   * Runs a version before spaces: what it reads loses its space fields (so it never learns them),
   * what it writes has none, and its sync state says nothing of the reader or the writer.
   */
  old = false;
  readonly log: CycleResult[] = [];

  constructor(
    readonly label: string,
    cloud: Cloud,
    readonly clock: Clock,
    options: { id?: string; lag?: boolean; skew?: number } = {},
  ) {
    this.skew = options.skew ?? 0;
    this.ws = new Workspace(new Hlc(options.id ?? newDeviceId(), () => this.clock.now + this.skew), () => new Date(this.clock.now + this.skew));
    this.view = new FolderView(cloud, options.lag);
    this.meta = emptyMeta('folder');
    this.saved = structuredClone(this.meta);
  }

  get id(): string {
    return this.ws.hlc.node;
  }

  /** While a cycle of a version before spaces runs: its own state as that cycle found it. */
  private found: { tag: string | null; writer?: number } | null = null;

  private engine(): SyncEngine {
    const view = this.view;
    const folder: SyncFolder = this.old
      ? {
          list: () => view.list(),
          read: async (entry) => beforeSpaces(await view.read(entry)),
          write: (name, text) => view.write(name, beforeSpaces(text)),
        }
      : view;
    return new SyncEngine(this.ws, folder, {
      label: () => ({ name: this.label, app: 'test' }),
      flush: async () => {},
      saveMeta: async (m) => void (this.saved = this.found ? asOldVersion(structuredClone(m), this.found) : structuredClone(m)),
    });
  }

  /**
   * A sync cycle as the app runs it: on a copy of what's saved, kept only if the cycle finishes.
   * A device whose id turns out to be taken picks a new one and goes again.
   */
  async sync(): Promise<CycleResult> {
    for (let attempt = 0; ; attempt++) {
      const meta = structuredClone(this.saved);
      const found = this.old ? asOldCycle(meta) : null;
      this.found = found;
      try {
        const result = await this.engine().cycle(meta, this.clock.now + this.skew);
        if (found) asOldVersion(meta, found);
        this.saved = meta;
        this.meta = meta;
        this.log.push(result);
        return result;
      } catch (error) {
        if (error instanceof DeviceCollision && attempt === 0) {
          Device.collisions++;
          this.ws.hlc.renew(newDeviceId());
          this.saved = emptyMeta(this.saved.folder);
          continue;
        }
        throw error;
      } finally {
        this.found = null;
      }
    }
  }

  /** Syncs, swallowing the transient failures the app would retry later. */
  async trySync(): Promise<CycleResult | null> {
    try {
      return await this.sync();
    } catch (error) {
      if (error instanceof SyncError) return null;
      throw error;
    }
  }
}

/** What a person sees: the focus, the open list in order, done, and the sheets. */
export function visible(ws: Workspace) {
  return {
    now: ws.now?.text ?? null,
    open: ws.open.map((t) => t.text),
    done: ws.done.map((t) => t.text).sort(),
    sheets: ws.sheetList.map((s) => s.text),
  };
}

/**
 * Every record, for comparing replicas exactly. A deleted record is compared by its deletion
 * only: the rest of it (its text, say) stays just on the device where it was deleted.
 */
export function records(ws: Workspace) {
  const data = ws.toData();
  const byId = (a: { id: string }, b: { id: string }) => (a.id < b.id ? -1 : 1);
  const dead = (r: { id: string; deleted: boolean; clk: { deleted: string } }) => ({ id: r.id, deleted: true, at: r.clk.deleted });
  return {
    focus: data.focus,
    tasks: data.tasks.sort(byId).map((t) => (t.deleted ? dead(t) : t)),
    sheets: data.sheets.sort(byId).map((s) => (s.deleted ? dead(s) : s)),
    spaces: data.spaces.sort(byId).map((s) => (s.deleted ? dead(s) : s)),
  };
}

/** Where each live task and sheet shows, by its text: the space it resolves to (see effectiveSpace). */
export function shownIn(ws: Workspace): Record<string, string> {
  const out: Record<string, string> = {};
  const spaces = [...ws.spaces.values()];
  for (const r of [...ws.tasks.values(), ...ws.sheets.values()]) if (!r.deleted) out[r.text] = effectiveSpace(r.space, spaces);
  return out;
}

/** Everyone syncs until nothing changes (with the OneDrive app on PCs passing files along). */
export async function settle(devices: Device[], rounds = 4): Promise<void> {
  for (let i = 0; i < rounds; i++) {
    for (const d of devices) {
      if (d.view.lag) d.view.propagate();
      await d.sync();
      if (d.view.lag) d.view.propagate();
    }
  }
}
