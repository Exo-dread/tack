import './styles/main.css';
import { Hlc } from './model/hlc';
import { newDeviceId } from './model/ids';
import type { WorkspaceData } from './model/types';
import { Workspace } from './model/workspace';
import { openBackend, StorageUnavailable, stranded, type Backend } from './store/db';
import { Store } from './store/persist';
import { prefs, setPrefs } from './store/prefs';
import { SyncController } from './sync/controller';
import { isSignInReturn } from './sync/msauth';
import { App, dropUntouchedWelcome, seedWelcome } from './ui/app';
import { isReloading, showLeaving, showReturning } from './ui/renewing';
import { registerServiceWorker } from './ui/sw';

async function boot(): Promise<void> {
  const backend: Backend = await openBackend();
  let loaded: { data: WorkspaceData; device: string | null };
  try {
    loaded = await backend.load();
  } catch (error) {
    // Unreadable storage: never start an empty list on top of it (it may come back). Say so.
    throw new StorageUnavailable(String(error));
  }

  const device = loaded.device ?? newDeviceId();
  if (!loaded.device) await backend.setDevice(device).catch(() => {});

  const ws = new Workspace(new Hlc(device));
  ws.merge(loaded.data, 'storage');
  const store = new Store(ws, backend);
  backend.onVersionChange = () => store.flushAll();

  // Anything a past session had to keep in localStorage comes home to IndexedDB.
  if (backend.kind === 'indexeddb') {
    const left = await stranded.read();
    if (left) {
      ws.merge(left, 'local');
      await store.flush();
      if (store.allSaved) stranded.clear();
    }
  }

  // Sync (off unless you turned it on). Back from signing in to Microsoft? Finish that first,
  // and if the sign-in is due for its daily renewal, go and renew it before anything shows (saying
  // so while it takes, and opening anyway if Microsoft is slow to send you back). If Microsoft
  // can't be reached just now, Tack starts as usual and renews another time.
  const sync = await SyncController.create(ws, store, backend);
  const finished = isSignInReturn() ? showReturning() : () => {};
  const returned = await sync.handleReturn().catch((error) => {
    console.error('Sign-in failed', error);
    return 'failed' as const;
  });
  finished();
  if (sync.renewalDueAtStart()) {
    const stay = showLeaving();
    if (await sync.renewBeforeStart()) return;
    stay();
  }
  // "Open Tack now" (or the wait ran out) meanwhile: a fresh page is on its way, so not this one.
  if (isReloading()) return;
  sync.onJoin = () => dropUntouchedWelcome(ws);

  if (!prefs().seeded && ws.tasks.size === 0 && ws.sheets.size === 0) {
    setPrefs({ seeded: true, welcome: seedWelcome(ws) });
  } else {
    ws.ensureSheet();
  }

  const app = new App(ws, store, sync);
  document.documentElement.classList.add('ready');
  void store.startup();
  registerServiceWorker(store, {
    settled: () => app.settled,
    // A reload on the way to Microsoft would cut the sign-in's renewal short.
    leaving: () => sync.leaving,
    renewing: () => sync.visiting,
  });
  sync.start();
  if (returned === 'signed-in' || returned === 'signed-in-again') app.syncTurnedOn(returned === 'signed-in-again');
}

function showBootError(error: unknown): void {
  console.error(error);
  const el = document.getElementById('boot-error');
  if (!el) return;
  const storage = error instanceof StorageUnavailable;
  el.textContent = storage
    ? 'Tack couldn’t open its storage on this device just now. Your tasks are safe where they are. '
    : 'Tack couldn’t start. Your data is safe; try reloading the page. ';
  const retry = document.createElement('button');
  retry.type = 'button';
  retry.className = 'button primary';
  retry.textContent = 'Try again';
  retry.addEventListener('click', () => location.reload());
  el.append(retry);
  el.hidden = false;
}

boot().catch(showBootError);
