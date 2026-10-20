import { stampTime } from '../model/hlc';
import { dayKey, isoLocal } from '../model/time';
import type { Change, Workspace } from '../model/workspace';
import type { Backend, Snapshot } from './db';

export type SaveStatus = 'saved' | 'saving' | 'retrying' | 'unsaved';

/** Tombstones are kept this long so deletions can still reach a device that's been offline. */
const TOMBSTONE_DAYS = 90;
const SNAPSHOT_DAYS = 7;
/** Typing in notes is saved after a short pause (and immediately when the app is hidden). */
const TYPING_DELAY = 350;

/**
 * Keeps storage in step with the workspace. Task changes are written immediately (a thought
 * captured just before the app is closed must survive); note typing is debounced and flushed
 * whenever the page is hidden. Writes are per record, and other tabs are told to merge in what
 * changed, so two open tabs never overwrite each other's work.
 */
export class Store {
  private dirtyTasks = new Set<string>();
  private dirtySheets = new Set<string>();
  private dirtyFocus = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private readonly inflight = new Set<Promise<void>>();
  private failures = 0;
  private readonly channel: BroadcastChannel | null;
  private readonly statusListeners = new Set<(s: SaveStatus) => void>();
  private _status: SaveStatus = 'saved';
  private persistAsked = false;
  private readonly beforeFlush = new Set<() => void>();
  /** While syncing, deletions are kept for good (sync/devicefile.ts), so housekeeping leaves them. */
  keepDeletions: () => boolean = () => false;

  constructor(
    private readonly ws: Workspace,
    readonly backend: Backend,
  ) {
    ws.subscribe((c) => this.onChange(c));
    this.channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel('tack') : null;
    this.channel?.addEventListener('message', (e) => {
      if (e.data?.type === 'saved') void this.reload();
    });
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'hidden') void this.flushAll();
      else void this.reload();
    });
    window.addEventListener('pagehide', () => void this.flushAll());
  }

  /** Runs before the page is hidden or closed, e.g. to commit a tick still in its grace period. */
  onBeforeFlush(hook: () => void): void {
    this.beforeFlush.add(hook);
  }

  /** Everything, now: the app is going to the background (or away). */
  flushAll(): Promise<void> {
    this.beforeFlush.forEach((hook) => {
      try {
        hook();
      } catch (error) {
        console.error(error);
      }
    });
    return this.flush();
  }

  get status(): SaveStatus {
    return this._status;
  }

  onStatus(listener: (s: SaveStatus) => void): () => void {
    this.statusListeners.add(listener);
    return () => this.statusListeners.delete(listener);
  }

  get hasPending(): boolean {
    return this.dirtyTasks.size > 0 || this.dirtySheets.size > 0 || this.dirtyFocus || this.inflight.size > 0;
  }

  /** Everything is on disk: nothing waiting, nothing being written, no failed writes. */
  get allSaved(): boolean {
    return !this.hasPending && this._status === 'saved';
  }

  /**
   * Writes everything pending now and resolves once it's on disk (or has failed). A new write
   * starts straight away even if another is still running (IndexedDB applies them in order, and
   * each merges with what's stored), so the last save before the app is hidden never waits.
   */
  async flush(): Promise<void> {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    if (!this.dirtyTasks.size && !this.dirtySheets.size && !this.dirtyFocus) {
      await Promise.all(this.inflight);
      return;
    }

    const taskIds = [...this.dirtyTasks];
    const sheetIds = [...this.dirtySheets];
    const focus = this.dirtyFocus;
    this.dirtyTasks.clear();
    this.dirtySheets.clear();
    this.dirtyFocus = false;

    this.setStatus(this.failures ? 'retrying' : 'saving');
    const write = this.backend
      .write({
        tasks: taskIds.map((id) => this.ws.tasks.get(id)).filter((t) => t !== undefined),
        sheets: sheetIds.map((id) => this.ws.sheets.get(id)).filter((s) => s !== undefined),
        focus: focus ? this.ws.focus : undefined,
      })
      .then(
        () => {
          this.failures = 0;
          this.channel?.postMessage({ type: 'saved' });
        },
        (error) => {
          console.error('Save failed', error);
          taskIds.forEach((id) => this.dirtyTasks.add(id));
          sheetIds.forEach((id) => this.dirtySheets.add(id));
          this.dirtyFocus ||= focus;
          this.failures++;
          this.schedule(Math.min(30000, 500 * 2 ** this.failures));
        },
      );
    this.inflight.add(write);
    try {
      await write;
    } finally {
      this.inflight.delete(write);
      if (this.failures) this.setStatus(this.failures >= 3 ? 'unsaved' : 'retrying');
      else this.setStatus(this.hasPending ? 'saving' : 'saved');
    }
  }

  /** Merges in what's on disk: picks up changes made in another tab or window. */
  async reload(): Promise<void> {
    try {
      const { data } = await this.backend.load();
      this.ws.merge(data, 'storage');
    } catch (error) {
      console.warn('Reload failed', error);
    }
  }

  /** Housekeeping at startup: a daily snapshot, and forgetting very old deletions. */
  async startup(): Promise<void> {
    try {
      const today = dayKey();
      const snaps = await this.backend.snapshots();
      if (!snaps.some((s) => s.day === today) && this.ws.tasks.size + this.ws.sheets.size > 0) {
        await this.backend.putSnapshot({ day: today, savedAt: isoLocal(), data: this.ws.toData() }, SNAPSHOT_DAYS);
      }
      if (this.keepDeletions()) return;
      const purged = this.ws.purgeTombstones(TOMBSTONE_DAYS * 86400000, stampTime);
      if (purged.tasks.length || purged.sheets.length) {
        await this.backend.write({ tasks: [], sheets: [], deleteTasks: purged.tasks, deleteSheets: purged.sheets });
      }
    } catch (error) {
      console.warn('Housekeeping failed', error);
    }
  }

  snapshots(): Promise<Snapshot[]> {
    return this.backend.snapshots();
  }

  private onChange(change: Change): void {
    if (change.origin === 'storage') return;
    change.tasks.forEach((id) => this.dirtyTasks.add(id));
    change.sheets.forEach((id) => this.dirtySheets.add(id));
    if (change.focus) this.dirtyFocus = true;
    const typingOnly = change.tasks.size === 0 && !change.focus;
    this.schedule(typingOnly ? TYPING_DELAY : 0);
    if (!this.persistAsked && !persistenceNeedsPrompt()) {
      this.persistAsked = true;
      void requestPersistence();
    }
  }

  private schedule(delay: number): void {
    if (this.timer) clearTimeout(this.timer);
    if (this._status === 'saved') this.setStatus('saving');
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.flush();
    }, delay);
  }

  private setStatus(s: SaveStatus): void {
    if (s === this._status) return;
    this._status = s;
    this.statusListeners.forEach((l) => l(s));
  }
}

/**
 * Asks the browser not to evict Tack's storage under pressure. Browsers decide for themselves
 * (Chrome grants it to installed or engaged sites, Safari to Home Screen apps), so this is a
 * request, not a guarantee; Settings shows the outcome.
 */
export async function requestPersistence(): Promise<boolean> {
  try {
    if (!navigator.storage?.persist) return false;
    if (await navigator.storage.persisted()) return true;
    return await navigator.storage.persist();
  } catch {
    return false;
  }
}

/**
 * Firefox answers persist() with a permission prompt (Chrome and Safari decide silently), so
 * there Tack only asks when you press the button in Settings.
 */
export function persistenceNeedsPrompt(): boolean {
  return /Firefox\//.test(navigator.userAgent);
}

export async function isPersisted(): Promise<boolean | null> {
  try {
    return navigator.storage?.persisted ? await navigator.storage.persisted() : null;
  } catch {
    return null;
  }
}
