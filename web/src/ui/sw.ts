import type { Store } from '../store/persist';
import { isEditable } from './dom';

/** An update that arrives this soon after the page came into view, untouched, applies at once. */
const FRESH = 30_000;

/**
 * The worker in charge as the page started (read before the app's first wait): another one by the
 * time the app is up means an update took over while it was starting, before anything listened.
 */
const startController = typeof navigator !== 'undefined' && 'serviceWorker' in navigator ? navigator.serviceWorker.controller : null;

/**
 * Offline support. The service worker keeps the whole app (it's small) so Tack opens instantly
 * with or without a connection. Updates install in the background and apply silently, never under
 * your fingers and only after your data is saved: when you're away from the app (leaving it, or
 * it arrives while you're gone), or when you open it or come back to it, before you've touched
 * anything. (Leaving alone isn't enough: a phone can freeze a page in the background before it
 * reloads, and you'd come back to the old version.)
 */
export function registerServiceWorker(
  store: Store,
  app: {
    /** Nothing on screen that a reload would lose. */
    settled: () => boolean;
    /** On the way to Microsoft to renew the sign-in: a reload would cut that short. */
    leaving: () => boolean;
    /** The same, or due to go when you come back. */
    renewing: () => boolean;
  },
): void {
  if (!('serviceWorker' in navigator) || !import.meta.env.PROD) return;
  const sw = navigator.serviceWorker;
  // On a first visit the first install takes control of a page that was already fine; only a
  // worker taking over from another one is an update.
  let controlled = !!startController;
  let updateReady = false;

  // When the page last came into view (opened, or brought back), and when you last touched it
  // (or typed into it any way at all: dictation and handwriting may not press a key).
  let shownAt = performance.now();
  let touchedAt = -Infinity;
  for (const type of ['pointerdown', 'keydown', 'wheel', 'beforeinput'] as const) {
    addEventListener(type, () => (touchedAt = performance.now()), { capture: true, passive: true });
  }
  // Just opened or brought back, untouched, and not about to be typed into (a box still has the focus).
  const untouched = () =>
    document.visibilityState === 'visible' &&
    touchedAt < shownAt &&
    performance.now() - shownAt < FRESH &&
    !isEditable(document.activeElement) &&
    !app.renewing();
  const away = () => document.visibilityState === 'hidden' && !app.leaving();

  /** Reloads into the new version once everything is safely on disk, if `still` holds by then. */
  const apply = (still: () => boolean) => {
    // If saving is failing, or something you're writing is on screen, the update can wait.
    void store.flushAll().then(() => {
      if (!store.allSaved || !app.settled() || !still()) return;
      if (document.visibilityState === 'visible') {
        // In view: nothing new can start in the moment before the page goes (a tap could be lost).
        document.body.inert = true;
        setTimeout(() => (document.body.inert = false), 5000);
      }
      location.reload();
    });
  };

  /** An update took over: in now, if you're away or have only just opened the app; else it waits. */
  const arrived = () => {
    updateReady = true;
    if (away()) apply(away);
    else if (untouched()) apply(untouched);
  };

  sw.addEventListener('controllerchange', () => {
    if (controlled) arrived();
    controlled = true;
  });
  if (startController && sw.controller && sw.controller !== startController) arrived();

  document.addEventListener('visibilitychange', () => {
    const hidden = document.visibilityState === 'hidden';
    if (!hidden) shownAt = performance.now();
    if (updateReady) apply(hidden ? away : untouched);
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
