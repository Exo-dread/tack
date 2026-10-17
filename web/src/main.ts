import './styles/main.css';
import { Hlc } from './model/hlc';
import { newDeviceId } from './model/ids';
import type { WorkspaceData } from './model/types';
import { Workspace } from './model/workspace';
import { MemoryBackend, openBackend, type Backend } from './store/db';
import { Store } from './store/persist';
import { prefs, setPrefs } from './store/prefs';
import { App, seedWelcome } from './ui/app';
import { registerServiceWorker } from './ui/sw';

async function boot(): Promise<void> {
  let backend: Backend = await openBackend();
  let loaded: { data: WorkspaceData; device: string | null };
  try {
    loaded = await backend.load();
  } catch (error) {
    // Unreadable storage: don't touch it (it may be recoverable), run from memory and say so.
    console.error('Could not read stored data', error);
    backend = new MemoryBackend();
    loaded = await backend.load();
  }

  const device = loaded.device ?? newDeviceId();
  if (!loaded.device) await backend.setDevice(device).catch(() => {});

  const ws = new Workspace(new Hlc(device));
  ws.merge(loaded.data, 'storage');
  const store = new Store(ws, backend);

  if (!prefs().seeded && ws.tasks.size === 0 && ws.sheets.size === 0) {
    seedWelcome(ws);
    setPrefs({ seeded: true });
  } else {
    ws.ensureSheet();
  }

  new App(ws, store);
  document.documentElement.classList.add('ready');
  void store.startup();
  registerServiceWorker(store);
}

boot().catch((error) => {
  console.error(error);
  const el = document.getElementById('boot-error');
  if (el) {
    el.textContent = 'Tack couldn’t start. Your data is safe; try reloading the page.';
    el.hidden = false;
  }
});
