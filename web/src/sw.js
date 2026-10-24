/* Tack Focus service worker. Generated at build time: the precache list and version are filled in
 * by vite.config.ts. Offline-first: the app shell and every asset come from the cache; the network
 * is only used to fetch updates (which arrive as a new version of this file). No data passes
 * through here: tasks and notes live in IndexedDB, and sync (if you turn it on) talks to
 * Microsoft's servers directly, which this worker leaves alone. */

const VERSION = '__TACK_VERSION__';
const PRECACHE = __TACK_PRECACHE__;
const CACHE = `tack-${VERSION}`;
/** The app shell is cached under "/", the URL hosts serve it from without redirecting. */
const SHELL = '/';

/**
 * Fetches and stores one file. A redirected response can't be used to answer a page load, so
 * one that arrived through a redirect (some hosts send /index.html to /) is stored as a plain
 * copy of the final response.
 */
async function precache(cache, url) {
  const response = await fetch(new Request(url, { cache: 'reload' }));
  if (!response.ok) throw new Error(`Precache failed for ${url}: ${response.status}`);
  const clean = response.redirected
    ? new Response(await response.blob(), { status: response.status, statusText: response.statusText, headers: response.headers })
    : response;
  await cache.put(url, clean);
}

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(CACHE)
      .then((cache) => Promise.all(PRECACHE.map((url) => precache(cache, url))))
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith('tack-') && k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (request.mode === 'navigate') {
    // A few plain pages live beside the app (the sign-in landing page, privacy): those load as themselves, offline too.
    // ("/" maps to "/index.html", which isn't in the list: the shell is cached as "/".)
    const page = `${url.pathname.replace(/\/?$/, '/')}index.html`;
    if (PRECACHE.includes(page)) {
      event.respondWith(caches.open(CACHE).then((cache) => cache.match(page, { ignoreVary: true }).then((cached) => cached || fetch(request))));
      return;
    }
    // Every other page load gets the cached app shell: instant, and identical offline. That
    // includes a share from another app (/?share&text=…), so what was shared stays on the device.
    event.respondWith(
      caches.open(CACHE).then((cache) =>
        cache.match(SHELL, { ignoreSearch: true, ignoreVary: true }).then((cached) => cached || fetch(request)),
      ),
    );
    return;
  }

  event.respondWith(
    caches.open(CACHE).then((cache) =>
      // ignoreVary: module scripts are requested with an Origin header that the precache request
      // didn't have; a server that answers "Vary: Origin" would otherwise make every lookup miss.
      cache.match(request, { ignoreSearch: true, ignoreVary: true }).then((cached) => cached || fetch(request)),
    ),
  );
});
