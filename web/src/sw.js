/* Tack Focus service worker. Generated at build time: the precache list and version are filled in
 * by vite.config.ts. Offline-first: the app shell and every asset come from the cache; the network
 * is only used to fetch updates (which arrive as a new version of this file). No data passes
 * through here: tasks and notes live in IndexedDB, and Tack makes no network requests of its own. */

const VERSION = '__TACK_VERSION__';
const PRECACHE = __TACK_PRECACHE__;
const CACHE = `tack-${VERSION}`;

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(CACHE)
      .then((cache) => cache.addAll(PRECACHE.map((url) => new Request(url, { cache: 'reload' }))))
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
    // Every page load gets the cached app shell: instant, and identical offline.
    event.respondWith(
      caches.open(CACHE).then((cache) =>
        cache.match('/index.html', { ignoreVary: true }).then((cached) => cached || fetch(request)),
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
