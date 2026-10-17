import { isIOS, isStandalone } from './dom';

interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

let deferred: InstallPromptEvent | null = null;
const listeners = new Set<() => void>();

/** Chrome, Edge and Android offer an install prompt; we keep it for the Settings button. */
export function watchInstall(): void {
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    deferred = e as InstallPromptEvent;
    listeners.forEach((l) => l());
  });
  window.addEventListener('appinstalled', () => {
    deferred = null;
    listeners.forEach((l) => l());
  });
}

export function onInstallChange(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export type InstallKind = 'installed' | 'prompt' | 'ios' | 'manual';

export function installKind(): InstallKind {
  if (isStandalone()) return 'installed';
  if (deferred) return 'prompt';
  if (isIOS()) return 'ios';
  return 'manual';
}

export async function promptInstall(): Promise<boolean> {
  if (!deferred) return false;
  const e = deferred;
  deferred = null;
  await e.prompt();
  const choice = await e.userChoice;
  listeners.forEach((l) => l());
  return choice.outcome === 'accepted';
}
