import type { Store } from '../store/persist';
import { isEditable } from './dom';

/** An update that arrives this soon after the page came into view, untouched, applies at once. */
const FRESH = 30_000;
/** An offer waiting for a quiet moment looks again this often (on the device: nothing goes to the network). */
const QUIET_CHECK = 10_000;
/** Not this soon after a tap or a key, so the offer never lands under your finger. */
const STILL = 2000;
/** After Later, the offer comes back when you return to the app, or after this long in it. */
const LATER = 4 * 60 * 60 * 1000;
/** Installed apps can stay open for days: look for an update on coming back, at most this often. */
const CHECK_EVERY = 30 * 60 * 1000;

/**
 * The worker in charge as the page started (read before the app's first wait): another one by the
 * time the app is up means an update took over while it was starting, before anything listened.
 */
const startController = typeof navigator !== 'undefined' && 'serviceWorker' in navigator ? navigator.serviceWorker.controller : null;

/**
 * Offline support. The service worker keeps the whole app (it's small) so Tack opens instantly
 * with or without a connection. Updates install in the background (the new files are all in the
 * new cache before it takes over) and apply silently, never under your fingers and only after your
 * data is saved and no sync is under way: when you're away from the app (leaving it, or it arrives
 * while you're gone), or when you open it or come back to it, before you've touched anything.
 * (Leaving alone isn't enough: a phone can freeze a page in the background before it reloads, and
 * you'd come back to the old version.) While you're using the app, a small card offers it instead,
 * at a quiet moment: Update now, or Later (and it's offered again when you come back, or in a few
 * hours). Nothing here polls the network: the browser checks for a new sw.js on page loads, and
 * this asks it to when the app comes back after a while.
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
    /** A sync cycle is under way. */
    syncing: () => boolean;
    /** Resolves once no sync cycle is under way. */
    syncIdle: () => Promise<void>;
    /** A toast or question is on show (an offer would take its place, and an Undo with it). */
    toastShowing: () => boolean;
    /** Shows the offer: Update now runs `update`; Later runs `later(true)`, a newer toast `later(false)`. */
    offer: (update: () => void, later: (byYou: boolean) => void) => void;
    /** Says something in a toast. */
    say: (message: string) => void;
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

  /** Reloads into the new version (the new worker serves it from its own cache). */
  const reload = () => {
    if (document.visibilityState === 'visible') {
      // In view: nothing new can start in the moment before the page goes (a tap could be lost).
      document.body.inert = true;
      setTimeout(() => (document.body.inert = false), 5000);
    }
    location.reload();
  };

  /**
   * Waits for a sync under way to finish and for everything to be safely on disk. False if saving
   * is failing or something you're writing is on screen: then the update waits.
   */
  const ready = async () => {
    await app.syncIdle();
    await store.flushAll();
    return store.allSaved && app.settled();
  };

  /** Reloads into the new version once it's `ready`, if `still` holds by then. */
  const apply = (still: () => boolean) => {
    void ready().then((ok) => {
      if (ok && still()) reload();
    });
  };

  // The offer, while you're using the app: on show, or held back by Later until a time (or your return).
  let offered = false;
  let laterUntil = 0;
  let offerTimer: ReturnType<typeof setTimeout> | undefined;
  /** In view and in use (so not a moment the silent update takes), with nothing it would get in the way of. */
  const quiet = () =>
    document.visibilityState === 'visible' &&
    touchedAt >= shownAt &&
    performance.now() - touchedAt > STILL &&
    !isEditable(document.activeElement) &&
    !app.toastShowing() &&
    !app.syncing() &&
    !app.renewing() &&
    app.settled();
  const offer = () => {
    clearTimeout(offerTimer);
    if (!updateReady || offered || document.visibilityState !== 'visible' || performance.now() < laterUntil) return;
    if (!quiet()) {
      offerTimer = setTimeout(offer, QUIET_CHECK);
      return;
    }
    offered = true;
    app.offer(updateNow, (byYou) => {
      offered = false;
      // Later: not again until you come back, or for a few hours. Pushed aside by a newer toast: once it's gone.
      if (byYou) laterUntil = performance.now() + LATER;
      offerTimer = setTimeout(offer, byYou ? LATER : QUIET_CHECK);
    });
  };
  /** Update now: once a sync under way is done and everything is saved. */
  const updateNow = () => {
    offered = false;
    void ready().then((ok) => {
      if (ok) return reload();
      app.say(store.allSaved ? 'Tack will update once you’re done here.' : 'Tack will update once your changes are saved.');
      offerTimer = setTimeout(offer, QUIET_CHECK);
    });
  };

  /** An update took over: in now, if you're away or have only just opened the app; else it's offered. */
  const arrived = () => {
    updateReady = true;
    if (away()) apply(away);
    else if (untouched()) apply(untouched);
    offer();
  };

  sw.addEventListener('controllerchange', () => {
    if (controlled) arrived();
    controlled = true;
  });
  if (startController && sw.controller && sw.controller !== startController) arrived();

  document.addEventListener('visibilitychange', () => {
    const hidden = document.visibilityState === 'hidden';
    if (!hidden) shownAt = performance.now();
    if (!updateReady) return;
    apply(hidden ? away : untouched);
    // Back after Later: it's offered again, once you're using the app.
    if (!hidden) {
      laterUntil = 0;
      offer();
    }
  });

  const register = async () => {
    try {
      const reg = await sw.register('/sw.js', { scope: '/', updateViaCache: 'none' });
      // Installed apps can stay open for days; look for an update when brought back (a desktop
      // window that never hides is brought back by focus), at most every half hour.
      let lastCheck = Date.now();
      const check = () => {
        if (document.visibilityState === 'visible' && navigator.onLine && Date.now() - lastCheck > CHECK_EVERY) {
          lastCheck = Date.now();
          reg.update().catch(() => {});
        }
      };
      document.addEventListener('visibilitychange', check);
      window.addEventListener('focus', check);
    } catch (error) {
      console.warn('Service worker registration failed', error);
    }
  };
  if (document.readyState === 'complete') void register();
  else window.addEventListener('load', () => void register(), { once: true });
}
