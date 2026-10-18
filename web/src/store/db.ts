import { mergeFocus, mergeSheet, mergeTask } from '../model/merge';
import type { Focus, Sheet, Task, WorkspaceData } from '../model/types';

/**
 * Where records live on this device. IndexedDB normally; localStorage only where IndexedDB is
 * missing or forbidden (some private modes); memory as a last resort, with a warning in the UI.
 *
 * Records are written individually, and every write merges with what's already stored (inside
 * the same transaction), so two tabs writing the same record can never overwrite each other's
 * newer fields: storage always holds the merge of everything written to it.
 */

export interface Batch {
  tasks: Task[];
  sheets: Sheet[];
  focus?: Focus;
  deleteTasks?: string[];
  deleteSheets?: string[];
}

export interface Snapshot {
  day: string;
  savedAt: string;
  data: WorkspaceData;
}

export interface Backend {
  readonly kind: 'indexeddb' | 'localstorage' | 'memory';
  /** Called before this tab gives up its connection to a newer version of Tack (then it reloads). */
  onVersionChange?: () => Promise<void>;
  load(): Promise<{ data: WorkspaceData; device: string | null }>;
  write(batch: Batch): Promise<void>;
  setDevice(id: string): Promise<void>;
  /** Small records kept beside the data (sync state, sign-in). Never synced. */
  getMeta<T>(key: MetaKey): Promise<T | undefined>;
  putMeta(key: MetaKey, value: unknown): Promise<void>;
  putSnapshot(s: Snapshot, keep: number): Promise<void>;
  snapshots(): Promise<Snapshot[]>;
}

export type MetaKey = 'sync' | 'auth';

/** IndexedDB exists but won't open (yet). Boot shows a message with Try again, never an empty list. */
export class StorageUnavailable extends Error {}

const DB_NAME = 'tack';
const DB_VERSION = 1;
const OPEN_TIMEOUT_MS = 10000;
const EMPTY_FOCUS: Focus = { id: null, clk: '000000000.0000.0' };

export async function openBackend(): Promise<Backend> {
  if (typeof indexedDB !== 'undefined') {
    try {
      return await IdbBackend.open();
    } catch (error) {
      // Only a browser that forbids IndexedDB outright gets the fallback. Anything else (slow,
      // busy, a transient error) must not quietly start an empty store somewhere else.
      const name = (error as DOMException)?.name;
      if (name !== 'SecurityError' && name !== 'InvalidStateError') throw new StorageUnavailable(String(error));
      console.warn('IndexedDB not allowed here, falling back', error);
    }
  }
  try {
    const probe = '__tack_probe__';
    localStorage.setItem(probe, '1');
    localStorage.removeItem(probe);
    return new LocalBackend();
  } catch {
    return new MemoryBackend();
  }
}

// ---------------------------------------------------------------------------- IndexedDB

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function done(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error ?? new DOMException('Transaction aborted', 'AbortError'));
  });
}

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      reject(new DOMException('IndexedDB took too long to open', 'TimeoutError'));
    }, OPEN_TIMEOUT_MS);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains('tasks')) db.createObjectStore('tasks', { keyPath: 'id' });
      if (!db.objectStoreNames.contains('sheets')) db.createObjectStore('sheets', { keyPath: 'id' });
      if (!db.objectStoreNames.contains('meta')) db.createObjectStore('meta');
      if (!db.objectStoreNames.contains('snapshots')) db.createObjectStore('snapshots', { keyPath: 'day' });
    };
    req.onsuccess = () => {
      clearTimeout(timer);
      // Opened after we gave up: don't leave a connection nobody uses.
      if (timedOut) req.result.close();
      else resolve(req.result);
    };
    req.onerror = () => {
      clearTimeout(timer);
      reject(req.error);
    };
    req.onblocked = () => console.warn('IndexedDB upgrade blocked by another tab');
  });
}

/** Errors that mean the connection itself is gone (iOS can drop it in the background). */
const isConnectionLost = (error: unknown): boolean => {
  const name = (error as DOMException)?.name;
  return name === 'InvalidStateError' || name === 'UnknownError' || name === 'TransactionInactiveError';
};

export class IdbBackend implements Backend {
  readonly kind = 'indexeddb' as const;
  onVersionChange?: () => Promise<void>;
  private db: IDBDatabase | null;

  private constructor(db: IDBDatabase) {
    this.db = db;
    this.watch(db);
  }

  static async open(): Promise<IdbBackend> {
    return new IdbBackend(await openDatabase());
  }

  private watch(db: IDBDatabase): void {
    // A newer Tack in another tab wants to upgrade the database: save what's pending, step aside,
    // and reload into the new version.
    db.onversionchange = () => {
      const saved = this.onVersionChange?.() ?? Promise.resolve();
      void saved.finally(() => {
        db.close();
        this.db = null;
        location.reload();
      });
    };
    // Closed by the browser (storage cleared, or the connection lost): reopen on next use.
    db.onclose = () => {
      if (this.db === db) this.db = null;
    };
  }

  /** Runs `work` on an open connection, reopening once if the connection had been lost. */
  private async run<T>(work: (db: IDBDatabase) => Promise<T>): Promise<T> {
    for (let attempt = 0; ; attempt++) {
      try {
        if (!this.db) {
          this.db = await openDatabase();
          this.watch(this.db);
        }
        return await work(this.db);
      } catch (error) {
        if (attempt > 0 || !isConnectionLost(error)) throw error;
        try {
          this.db?.close();
        } catch {
          /* already closed */
        }
        this.db = null;
      }
    }
  }

  load() {
    return this.run(async (db) => {
      const tx = db.transaction(['tasks', 'sheets', 'meta'], 'readonly');
      const [tasks, sheets, focus, device] = await Promise.all([
        request(tx.objectStore('tasks').getAll() as IDBRequest<Task[]>),
        request(tx.objectStore('sheets').getAll() as IDBRequest<Sheet[]>),
        request(tx.objectStore('meta').get('focus') as IDBRequest<Focus | undefined>),
        request(tx.objectStore('meta').get('device') as IDBRequest<string | undefined>),
      ]);
      return { data: { tasks, sheets, focus: focus ?? EMPTY_FOCUS }, device: device ?? null };
    });
  }

  write(batch: Batch) {
    return this.run(async (db) => {
      // "strict" asks the browser to flush to disk before reporting success: Tack's writes are
      // tiny, and a thought captured just before the app is swiped away must survive.
      const tx = db.transaction(['tasks', 'sheets', 'meta'], 'readwrite', { durability: 'strict' });
      const finished = done(tx);
      const tasks = tx.objectStore('tasks');
      const sheets = tx.objectStore('sheets');
      const meta = tx.objectStore('meta');
      for (const t of batch.tasks) {
        const get = tasks.get(t.id) as IDBRequest<Task | undefined>;
        get.onsuccess = () => tasks.put(mergeTask(get.result, t).value);
      }
      for (const s of batch.sheets) {
        const get = sheets.get(s.id) as IDBRequest<Sheet | undefined>;
        get.onsuccess = () => sheets.put(mergeSheet(get.result, s).value);
      }
      for (const id of batch.deleteTasks ?? []) tasks.delete(id);
      for (const id of batch.deleteSheets ?? []) sheets.delete(id);
      if (batch.focus) {
        const focus = batch.focus;
        const get = meta.get('focus') as IDBRequest<Focus | undefined>;
        get.onsuccess = () => meta.put(get.result ? mergeFocus(get.result, focus).value : focus, 'focus');
      }
      await finished;
    });
  }

  setDevice(id: string) {
    return this.run(async (db) => {
      const tx = db.transaction('meta', 'readwrite');
      tx.objectStore('meta').put(id, 'device');
      await done(tx);
    });
  }

  getMeta<T>(key: MetaKey) {
    return this.run(async (db) => {
      const tx = db.transaction('meta', 'readonly');
      return (await request(tx.objectStore('meta').get(`x:${key}`))) as T | undefined;
    });
  }

  putMeta(key: MetaKey, value: unknown) {
    return this.run(async (db) => {
      const tx = db.transaction('meta', 'readwrite', { durability: 'strict' });
      tx.objectStore('meta').put(value, `x:${key}`);
      await done(tx);
    });
  }

  putSnapshot(s: Snapshot, keep: number) {
    return this.run(async (db) => {
      const tx = db.transaction('snapshots', 'readwrite');
      const finished = done(tx);
      const store = tx.objectStore('snapshots');
      store.put(s);
      const keys = store.getAllKeys();
      keys.onsuccess = () => {
        const all = (keys.result as string[]).sort();
        for (const k of all.slice(0, Math.max(0, all.length - keep))) store.delete(k);
      };
      await finished;
    });
  }

  snapshots() {
    return this.run(async (db) => {
      const tx = db.transaction('snapshots', 'readonly');
      const all = await request(tx.objectStore('snapshots').getAll() as IDBRequest<Snapshot[]>);
      return all.sort((a, b) => (a.day < b.day ? 1 : -1));
    });
  }
}

// ---------------------------------------------------------------------------- localStorage

const KEYS = { tasks: 'tack.tasks', sheets: 'tack.sheets', focus: 'tack.focus', device: 'tack.device', snapshots: 'tack.snapshots' };

/** Same shape, kept as a few JSON blobs. Fine for the size of a task list. */
export class LocalBackend implements Backend {
  readonly kind: 'localstorage' | 'memory' = 'localstorage';
  onVersionChange?: () => Promise<void>;
  protected mem = new Map<string, string>();

  protected get(key: string): string | null {
    return localStorage.getItem(key);
  }

  protected set(key: string, value: string): void {
    localStorage.setItem(key, value);
  }

  private read<T>(key: string, fallback: T): T {
    try {
      const raw = this.get(key);
      return raw ? (JSON.parse(raw) as T) : fallback;
    } catch {
      return fallback;
    }
  }

  async load() {
    const tasks = Object.values(this.read<Record<string, Task>>(KEYS.tasks, {}));
    const sheets = Object.values(this.read<Record<string, Sheet>>(KEYS.sheets, {}));
    const focus = this.read<Focus>(KEYS.focus, EMPTY_FOCUS);
    return { data: { tasks, sheets, focus }, device: this.get(KEYS.device) };
  }

  async write(batch: Batch) {
    // Maps rather than plain objects: record ids come from imported files too.
    const tasks = new Map(Object.entries(this.read<Record<string, Task>>(KEYS.tasks, {})));
    const sheets = new Map(Object.entries(this.read<Record<string, Sheet>>(KEYS.sheets, {})));
    for (const t of batch.tasks) tasks.set(t.id, mergeTask(tasks.get(t.id), t).value);
    for (const s of batch.sheets) sheets.set(s.id, mergeSheet(sheets.get(s.id), s).value);
    for (const id of batch.deleteTasks ?? []) tasks.delete(id);
    for (const id of batch.deleteSheets ?? []) sheets.delete(id);
    this.set(KEYS.tasks, JSON.stringify(Object.fromEntries(tasks)));
    this.set(KEYS.sheets, JSON.stringify(Object.fromEntries(sheets)));
    if (batch.focus) {
      const stored = this.read<Focus | null>(KEYS.focus, null);
      this.set(KEYS.focus, JSON.stringify(stored ? mergeFocus(stored, batch.focus).value : batch.focus));
    }
  }

  async setDevice(id: string) {
    this.set(KEYS.device, id);
  }

  async getMeta<T>(key: MetaKey) {
    return this.read<T | undefined>(`tack.meta.${key}`, undefined);
  }

  async putMeta(key: MetaKey, value: unknown) {
    this.set(`tack.meta.${key}`, JSON.stringify(value));
  }

  async putSnapshot(s: Snapshot, keep: number) {
    // localStorage is small (about 5 MB); a couple of snapshots, and never at the data's expense.
    const all = this.read<Snapshot[]>(KEYS.snapshots, []).filter((x) => x.day !== s.day);
    all.push(s);
    all.sort((a, b) => (a.day < b.day ? -1 : 1));
    try {
      this.set(KEYS.snapshots, JSON.stringify(all.slice(-Math.min(keep, 2))));
    } catch {
      try {
        this.set(KEYS.snapshots, '[]');
      } catch {
        /* nothing more to free here */
      }
    }
  }

  async snapshots() {
    return this.read<Snapshot[]>(KEYS.snapshots, []).sort((a, b) => (a.day < b.day ? 1 : -1));
  }
}

export class MemoryBackend extends LocalBackend {
  override readonly kind = 'memory' as const;

  protected override get(key: string) {
    return this.mem.get(key) ?? null;
  }

  protected override set(key: string, value: string) {
    this.mem.set(key, value);
  }
}

/**
 * Data left in localStorage by a session that couldn't use IndexedDB (an older Tack, or a
 * private-mode visit). Once IndexedDB works again it's merged in, then removed.
 */
export const stranded = {
  async read(): Promise<WorkspaceData | null> {
    try {
      if (!localStorage.getItem(KEYS.tasks) && !localStorage.getItem(KEYS.sheets)) return null;
      return (await new LocalBackend().load()).data;
    } catch {
      return null;
    }
  },
  clear(): void {
    try {
      for (const k of [KEYS.tasks, KEYS.sheets, KEYS.focus, KEYS.snapshots]) localStorage.removeItem(k);
    } catch {
      /* ignore */
    }
  },
};
