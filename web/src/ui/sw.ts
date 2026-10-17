import type { Store } from '../store/persist';

/**
 * Offline support. The service worker keeps the whole app (it's small) so Tack opens instantly
 * with or without a connection. Updates install in the background and apply silently the next
 * time you leave the app, never under your fingers, and only after your data is saved.
 */
export function registerServiceWorker(store: Store): void {
  if (!('serviceWorker' in navigator) || !import.meta.env.PROD) return;
  const sw = navigator.serviceWorker;
  const hadController = !!sw.controller;
  let updateReady = false;

  sw.addEventListener('controllerchange', () => {
    // The first install takes control of a page that was already fine; only updates reload.
    if (hadController) updateReady = true;
  });

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden' && updateReady) {
      void store.flushAll().then(() => location.reload());
    }
  });

  const register = async () => {
    try {
      const reg = await sw.register('/sw.js', { scope: '/', updateViaCache: 'none' });
      // Installed apps can stay open for days; look for an update when brought back.
      let lastCheck = Date.now();
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible' && navigator.onLine && Date.now() - lastCheck > 30 * 60 * 1000) {
          lastCheck = Date.now();
          reg.update().catch(() => {});
        }
      });
    } catch (error) {
      console.warn('Service worker registration failed', error);
    }
  };
  if (document.readyState === 'complete') void register();
  else window.addEventListener('load', () => void register(), { once: true });
}
