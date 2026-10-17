import type { Focus, Sheet, Task, WorkspaceData } from '../model/types';

/**
 * Where records live on this device. IndexedDB normally; localStorage if IndexedDB is
 * unavailable (some private modes); memory as a last resort, with a warning in the UI.
 * Records are written individually so a change rewrites only what changed.
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
  putSnapshot(s: Snapshot, keep: number): Promise<void>;
  snapshots(): Promise<Snapshot[]>;
}

const DB_NAME = 'tack';
const DB_VERSION = 1;
const EMPTY_FOCUS: Focus = { id: null, clk: '000000000.0000.0' };

export async function openBackend(): Promise<Backend> {
  try {
    return await IdbBackend.open();
  } catch (error) {
    console.warn('IndexedDB unavailable, falling back', error);
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

export class IdbBackend implements Backend {
  readonly kind = 'indexeddb' as const;
  onVersionChange?: () => Promise<void>;

  private constructor(private db: IDBDatabase) {
    // A newer Tack in another tab wants to upgrade the database: save what's pending, step aside,
    // and reload into the new version.
    db.onversionchange = () => {
      const saved = this.onVersionChange?.() ?? Promise.resolve();
      void saved.finally(() => {
        db.close();
        location.reload();
      });
    };
  }

  static open(): Promise<IdbBackend> {
    return new Promise((resolve, reject) => {
      if (typeof indexedDB === 'undefined') return reject(new Error('No IndexedDB'));
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      const timer = setTimeout(() => reject(new Error('IndexedDB open timed out')), 8000);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains('tasks')) db.createObjectStore('tasks', { keyPath: 'id' });
        if (!db.objectStoreNames.contains('sheets')) db.createObjectStore('sheets', { keyPath: 'id' });
        if (!db.objectStoreNames.contains('meta')) db.createObjectStore('meta');
        if (!db.objectStoreNames.contains('snapshots')) db.createObjectStore('snapshots', { keyPath: 'day' });
      };
      req.onsuccess = () => {
        clearTimeout(timer);
        resolve(new IdbBackend(req.result));
      };
      req.onerror = () => {
        clearTimeout(timer);
        reject(req.error);
      };
      req.onblocked = () => console.warn('IndexedDB upgrade blocked by another tab');
    });
  }

  async load() {
    const tx = this.db.transaction(['tasks', 'sheets', 'meta'], 'readonly');
    const [tasks, sheets, focus, device] = await Promise.all([
      request(tx.objectStore('tasks').getAll() as IDBRequest<Task[]>),
      request(tx.objectStore('sheets').getAll() as IDBRequest<Sheet[]>),
      request(tx.objectStore('meta').get('focus') as IDBRequest<Focus | undefined>),
      request(tx.objectStore('meta').get('device') as IDBRequest<string | undefined>),
    ]);
    return { data: { tasks, sheets, focus: focus ?? EMPTY_FOCUS }, device: device ?? null };
  }

  async write(batch: Batch) {
    // "strict" asks the browser to flush to disk before reporting success: Tack's writes are
    // tiny, and a thought captured just before the app is swiped away must survive.
    const tx = this.db.transaction(['tasks', 'sheets', 'meta'], 'readwrite', { durability: 'strict' });
    const tasks = tx.objectStore('tasks');
    const sheets = tx.objectStore('sheets');
    for (const t of batch.tasks) tasks.put(t);
    for (const s of batch.sheets) sheets.put(s);
    for (const id of batch.deleteTasks ?? []) tasks.delete(id);
    for (const id of batch.deleteSheets ?? []) sheets.delete(id);
    if (batch.focus) tx.objectStore('meta').put(batch.focus, 'focus');
    await done(tx);
  }

  async setDevice(id: string) {
    const tx = this.db.transaction('meta', 'readwrite');
    tx.objectStore('meta').put(id, 'device');
    await done(tx);
  }

  async putSnapshot(s: Snapshot, keep: number) {
    const tx = this.db.transaction('snapshots', 'readwrite');
    const store = tx.objectStore('snapshots');
    store.put(s);
    const keys = (await request(store.getAllKeys())) as string[];
    keys.sort();
    for (const k of keys.slice(0, Math.max(0, keys.length - keep))) store.delete(k);
    await done(tx);
  }

  async snapshots() {
    const tx = this.db.transaction('snapshots', 'readonly');
    const all = await request(tx.objectStore('snapshots').getAll() as IDBRequest<Snapshot[]>);
    return all.sort((a, b) => (a.day < b.day ? 1 : -1));
  }
}

// ---------------------------------------------------------------------------- localStorage

/** Same shape, kept as a few JSON blobs. Fine for the size of a task list. */
export class LocalBackend implements Backend {
  readonly kind: 'localstorage' | 'memory' = 'localstorage';
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
    const tasks = Object.values(this.read<Record<string, Task>>('tack.tasks', {}));
    const sheets = Object.values(this.read<Record<string, Sheet>>('tack.sheets', {}));
    const focus = this.read<Focus>('tack.focus', EMPTY_FOCUS);
    return { data: { tasks, sheets, focus }, device: this.get('tack.device') };
  }

  async write(batch: Batch) {
    const tasks = this.read<Record<string, Task>>('tack.tasks', {});
    const sheets = this.read<Record<string, Sheet>>('tack.sheets', {});
    for (const t of batch.tasks) tasks[t.id] = t;
    for (const s of batch.sheets) sheets[s.id] = s;
    for (const id of batch.deleteTasks ?? []) delete tasks[id];
    for (const id of batch.deleteSheets ?? []) delete sheets[id];
    this.set('tack.tasks', JSON.stringify(tasks));
    this.set('tack.sheets', JSON.stringify(sheets));
    if (batch.focus) this.set('tack.focus', JSON.stringify(batch.focus));
  }

  async setDevice(id: string) {
    this.set('tack.device', id);
  }

  async putSnapshot(s: Snapshot, keep: number) {
    const all = this.read<Snapshot[]>('tack.snapshots', []).filter((x) => x.day !== s.day);
    all.push(s);
    all.sort((a, b) => (a.day < b.day ? -1 : 1));
    try {
      this.set('tack.snapshots', JSON.stringify(all.slice(-keep)));
    } catch {
      // Snapshots are a nicety; never let them crowd out the data itself.
      this.set('tack.snapshots', JSON.stringify(all.slice(-1)));
    }
  }

  async snapshots() {
    return this.read<Snapshot[]>('tack.snapshots', []).sort((a, b) => (a.day < b.day ? 1 : -1));
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
