import { test as base, expect, type Page } from '@playwright/test';
import { readFile, stat } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import { extname, join, resolve, sep } from 'node:path';
import { composer, isTouch } from './helpers';

/**
 * Updates of the app (src/ui/sw.ts), end to end: the real build and service worker, served by a
 * small server of our own that can publish a new version (the same files, but sw.js changed by a
 * trailing comment: the browser installs it, the worker precaches, skips waiting and claims the
 * page, and the page sees `controllerchange`). An update applies by reloading the page, only once
 * everything is saved and nothing unsaved is on screen: when you leave the app, or when you open
 * it or come back to it and haven't touched it yet (within 30 s of it coming into view).
 *
 * Headless browsers keep every page "visible" (bringing another page to the front changes nothing),
 * so leaving and coming back are emulated in the page: `document.visibilityState` answers what the
 * test says, and `visibilitychange` fires as the browser would fire it. The state survives a reload
 * (a page reloaded in the background boots hidden), as it would on a phone.
 */

const DIST = resolve(import.meta.dirname, '../../dist');
/** Ports for this test's own server (never 4173, another project's; the config's server is E2E_PORT). */
const PORTS = [4194, 4195, 4196, 4197, 4198, 4199];
/** How long a reload that shouldn't happen is given to happen anyway (it follows a save: milliseconds). */
const QUIET = 1000;
/** How late the app's storage answers in a slow start (an update takes over in 1-2 s; it gives up at 10). */
const SLOW_START = 5000;

const TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.webmanifest': 'application/manifest+json',
  '.json': 'application/json',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.txt': 'text/plain; charset=utf-8',
};

type Site = {
  url: string;
  /** The version on offer: 0 is the build as it is; each publish changes sw.js by a comment. */
  version: number;
  /** Puts the next version up. */
  publish(): void;
  /** What was asked for since the test began, and the version on offer at the time. */
  requests: { path: string; version: number }[];
  reset(): void;
};

async function listen(server: Server, first: number): Promise<number> {
  for (let i = 0; i < PORTS.length; i++) {
    const port = PORTS[(first + i) % PORTS.length];
    try {
      await new Promise<void>((done, fail) => {
        server.once('error', fail);
        server.listen(port, '127.0.0.1', () => {
          server.off('error', fail);
          done();
        });
      });
      return port;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'EADDRINUSE') throw error;
    }
  }
  throw new Error(`No free port in ${PORTS[0]}-${PORTS.at(-1)}`);
}

const test = base.extend<{ site: Site }, { server: Site }>({
  // One server per worker (tests in a worker run one at a time; each has its own browser context).
  server: [
    async ({}, use, workerInfo) => {
      const site: Site = {
        url: '',
        version: 0,
        publish: () => void (site.version += 1),
        requests: [],
        reset: () => {
          site.version = 0;
          site.requests = [];
        },
      };
      const server = createServer(async (req, res) => {
        const path = new URL(req.url ?? '/', 'http://x').pathname;
        site.requests.push({ path, version: site.version });
        try {
          let file = resolve(DIST, `.${decodeURIComponent(path.endsWith('/') ? `${path}index.html` : path)}`);
          if (!file.startsWith(DIST + sep) || /[\\/]_/.test(file.slice(DIST.length))) throw new Error('not served');
          if ((await stat(file)).isDirectory()) file = join(file, 'index.html');
          let body = await readFile(file);
          if (path === '/sw.js' && site.version) body = Buffer.concat([body, Buffer.from(`\n// update ${site.version}\n`)]);
          const fresh = path === '/sw.js' || file.endsWith('.html');
          res.writeHead(200, {
            'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream',
            'Cache-Control': fresh ? 'no-cache' : path.startsWith('/assets/') ? 'public, max-age=31536000, immutable' : 'public, max-age=3600',
          });
          res.end(body);
        } catch {
          res.writeHead(404, { 'Content-Type': 'text/plain' });
          res.end('Not found');
        }
      });
      const port = await listen(server, workerInfo.parallelIndex);
      site.url = `http://127.0.0.1:${port}/`;
      await use(site);
      server.closeAllConnections();
      await new Promise((done) => server.close(done));
    },
    { scope: 'worker' },
  ],
  site: async ({ server }, use) => {
    server.reset();
    await use(server);
  },
});

declare global {
  interface Window {
    /** Set on the page under test: gone after a reload. */
    __old?: boolean;
    /** The page saw a new service worker take over. */
    __updated?: boolean;
    __pointerdowns?: number;
    /** Leaves (hidden) or comes back (visible), one `visibilitychange` per state, all in one go. */
    __setVisibility?: (...states: DocumentVisibilityState[]) => void;
  }
}

/** Runs before the app in every page: visibility the test controls (see the top of the file). */
function emulateVisibility(): void {
  if (window.top !== window) return;
  const KEY = 'e2e-visibility';
  let state: DocumentVisibilityState = 'visible';
  try {
    if (sessionStorage.getItem(KEY) === 'hidden') state = 'hidden';
  } catch {}
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => state === 'hidden' });
  window.__setVisibility = (...states) => {
    for (const next of states) {
      state = next;
      sessionStorage.setItem(KEY, next);
      document.dispatchEvent(new Event('visibilitychange'));
    }
  };
}

/**
 * Runs before the app in every page: when the test asks for it (once), the app's storage opens
 * `ms` late, as on a phone slow to start, and the page notes whether a new worker took it over
 * while the app was still starting.
 */
function slowStart(ms: number): void {
  if (window.top !== window || !sessionStorage.getItem('e2e-slow-start')) return;
  sessionStorage.removeItem('e2e-slow-start');
  const open = IDBFactory.prototype.open;
  const onsuccess = Object.getOwnPropertyDescriptor(IDBRequest.prototype, 'onsuccess')!;
  IDBFactory.prototype.open = function (this: IDBFactory, name: string, version?: number) {
    IDBFactory.prototype.open = open;
    const req = open.call(this, name, version);
    Object.defineProperty(req, 'onsuccess', {
      configurable: true,
      get: () => onsuccess.get!.call(req),
      set: (handler: (this: IDBRequest, event: Event) => void) =>
        onsuccess.set!.call(req, (event: Event) => void setTimeout(() => handler.call(req, event), ms)),
    });
    return req;
  };
  navigator.serviceWorker.addEventListener('controllerchange', () =>
    sessionStorage.setItem('e2e-takeover', document.documentElement.classList.contains('ready') ? 'after starting' : 'while starting'),
  );
}

type Visit = { loads: () => number; errors: Error[] };

/** A first visit: the app opens, and its worker installs and takes the page over. */
async function opened(page: Page, site: Site): Promise<Error[]> {
  await page.context().addInitScript(emulateVisibility);
  const errors: Error[] = [];
  page.on('pageerror', (error) => errors.push(error));
  await page.goto(site.url);
  await expect(page.locator('html.ready')).toHaveCount(1);
  await page.evaluate(() => navigator.serviceWorker.ready);
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);
  return errors;
}

/** Marks the page as it is now (a reload clears the marks) and counts page loads from here on. */
async function watch(page: Page, errors: Error[]): Promise<Visit> {
  await page.evaluate(() => {
    window.__old = true;
    window.__pointerdowns = 0;
    navigator.serviceWorker.addEventListener('controllerchange', () => (window.__updated = true));
    addEventListener('pointerdown', () => (window.__pointerdowns! += 1), { capture: true });
    // Could the page still be touched as it went?
    addEventListener('pagehide', () => sessionStorage.setItem('e2e-inert', String(document.body.inert)));
  });
  let loads = 0;
  page.on('load', () => (loads += 1));
  return { loads: () => loads, errors };
}

/**
 * The app as on any visit after the first: installed (the first visit put the worker in, and
 * `meanwhile` runs then), then opened again, so a worker controls the page from the start.
 */
async function installed(page: Page, site: Site, meanwhile?: () => Promise<void>): Promise<Visit> {
  const errors = await opened(page, site);
  await meanwhile?.();
  await page.reload();
  await expect(page.locator('html.ready')).toHaveCount(1);
  expect(await page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);
  return watch(page, errors);
}

/** A tap (or a click) on something that does nothing: the subtitle. */
async function touch(page: Page): Promise<void> {
  const box = await page.locator('.subtitle').boundingBox();
  if (!box) throw new Error('subtitle not on screen');
  const [x, y] = [box.x + box.width / 2, box.y + box.height / 2];
  if (await isTouch(page)) await page.touchscreen.tap(x, y);
  else await page.mouse.click(x, y);
  expect(await page.evaluate(() => window.__pointerdowns)).toBeGreaterThan(0);
}

/** Publishes a new version and has the browser look for it, as it does when the app is opened. */
async function update(page: Page, site: Site): Promise<void> {
  site.publish();
  await page.evaluate(async () => {
    const reg = await navigator.serviceWorker.getRegistration();
    if (!reg) throw new Error('no service worker registration');
    reg.update().catch((error) => console.error('update failed', error));
  });
}

/** The new worker has taken over this (not yet reloaded) page. */
async function arrived(page: Page): Promise<void> {
  await expect.poll(() => page.evaluate(() => window.__updated === true)).toBe(true);
}

/** Leaves the app and/or comes back, in a task of its own (the page may reload straight after). */
async function setVisibility(page: Page, ...states: DocumentVisibilityState[]): Promise<void> {
  await page.evaluate((s) => void setTimeout(() => window.__setVisibility!(...s)), states);
}

/** No reload: given time to happen, it didn't, and this is still the page from before. */
async function stays(page: Page, visit: Visit): Promise<void> {
  const before = visit.loads();
  await page.waitForTimeout(QUIET);
  expect(visit.loads()).toBe(before);
  expect(await page.evaluate(() => window.__old)).toBe(true);
}

/** The page reloaded (once more) and runs under the new worker, which installed the new version. */
async function reloadedIntoNew(page: Page, site: Site, visit: Visit, loads = 1): Promise<void> {
  await expect.poll(visit.loads, { timeout: 10_000 }).toBe(loads);
  await expect(page.locator('html.ready')).toHaveCount(1);
  expect(await page.evaluate(() => window.__old)).toBeUndefined();
  // The new sw.js was fetched and installed: it precached the app afresh (the update's own fetches).
  expect(site.requests.some((r) => r.path === '/sw.js' && r.version === site.version)).toBe(true);
  expect(site.requests.some((r) => r.path.startsWith('/assets/index-') && r.version === site.version)).toBe(true);
  const reg = await page.evaluate(async () => {
    const reg = await navigator.serviceWorker.getRegistration();
    const controller = navigator.serviceWorker.controller;
    return { controlled: !!controller && reg?.active === controller, waiting: !!reg?.waiting, installing: !!reg?.installing };
  });
  expect(reg).toEqual({ controlled: true, waiting: false, installing: false });
}

// Each test also checks that nothing threw in the page on the way (e.g. in the update's handlers).
test.describe('updates', () => {
  test('opened and untouched: an update reloads into the new version at once', async ({ page, site }) => {
    const visit = await installed(page, site);
    await update(page, site);
    await reloadedIntoNew(page, site, visit);
    // In view, the old page could no longer be touched as it went (a tap then could be lost).
    expect(await page.evaluate(() => sessionStorage.getItem('e2e-inert'))).toBe('true');
    expect(visit.errors).toEqual([]);
  });

  test('touched before it arrives: no reload while you are there; it applies when you leave', async ({ page, site }) => {
    const visit = await installed(page, site);
    await touch(page);
    await update(page, site);
    await arrived(page);
    await stays(page, visit);

    await setVisibility(page, 'hidden');
    await reloadedIntoNew(page, site, visit);
    // Reloaded in the background, it's still away; coming back to the new version reloads nothing.
    expect(await page.evaluate(() => document.visibilityState)).toBe('hidden');
    await page.evaluate(() => (window.__old = true));
    await setVisibility(page, 'visible');
    await stays(page, visit);
    expect(visit.errors).toEqual([]);
  });

  test('typing a task holds it back while you are in the box; leaving applies it, and the draft comes back', async ({ page, site }) => {
    const visit = await installed(page, site);
    await composer(page).click();
    await composer(page).pressSequentially('Half a thou');
    await update(page, site);
    await arrived(page);
    await stays(page, visit);

    // Away and back before the reload on leaving could run: back in the box (it kept the focus),
    // you may type on at once, so it holds.
    await setVisibility(page, 'hidden', 'visible');
    await stays(page, visit);
    await expect(composer(page)).toBeFocused();

    // A draft that isn't the one kept (another tab kept its own since) holds it back even when away.
    await page.evaluate(() => localStorage.setItem('tack.draft', 'Another tab’s'));
    await setVisibility(page, 'hidden');
    await stays(page, visit);
    await setVisibility(page, 'visible');
    await stays(page, visit);
    await expect(composer(page)).toHaveValue('Half a thou');

    // Typed on, it's kept again: leaving applies the update, and the draft comes back with the page.
    await composer(page).pressSequentially('g');
    await setVisibility(page, 'hidden');
    await reloadedIntoNew(page, site, visit);
    await expect(composer(page)).toHaveValue('Half a thoug');
    expect(visit.errors).toEqual([]);
  });

  test('a draft kept from last time does not hold it back: opened untouched, it reloads at once and the draft comes back', async ({ page, site }) => {
    const visit = await installed(page, site, async () => {
      await composer(page).click();
      await composer(page).pressSequentially('Parked');
    });
    await expect(composer(page)).toHaveValue('Parked');
    await expect(composer(page)).not.toBeFocused();
    await update(page, site);
    await reloadedIntoNew(page, site, visit);
    await expect(composer(page)).toHaveValue('Parked');
    expect(visit.errors).toEqual([]);
  });

  test('a share that opened the app holds it back while its card is on show; leaving applies it, and the new page offers it again', async ({ page, site }) => {
    const first = await installed(page, site);
    // Opened by the share sheet: the page is new, and untouched.
    await page.goto(`${site.url}?share&text=${encodeURIComponent('Shared just now')}`);
    await expect(page.locator('html.ready')).toHaveCount(1);
    const card = page.locator('.toast.ask:not([inert])');
    await expect(card.locator('.toast-text')).toHaveText('Shared just now');
    const visit = await watch(page, first.errors);
    await update(page, site);
    await arrived(page);
    await stays(page, visit);
    await expect(card.getByRole('button', { name: 'Add', exact: true })).toBeVisible();

    await setVisibility(page, 'hidden');
    await reloadedIntoNew(page, site, visit);
    // Not added by itself, before or after: the new page asks again.
    await expect(page.locator('.open-list .label').filter({ hasText: 'Shared just now' })).toHaveCount(0);
    await card.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(page.locator('.open-list .label').filter({ hasText: 'Shared just now' })).toHaveCount(1);
    expect(visit.errors).toEqual([]);
  });

  test('a share just added holds it back while its Undo is on show; leaving applies it, and the share is kept once', async ({ page, site }) => {
    const first = await installed(page, site);
    await page.goto(`${site.url}?share&text=${encodeURIComponent('Added just now')}`);
    await expect(page.locator('html.ready')).toHaveCount(1);
    await page.locator('.toast.ask:not([inert])').getByRole('button', { name: 'Add', exact: true }).click();
    const undo = page.locator('.toast:not([inert])');
    await expect(undo.locator('.toast-text')).toHaveText('Added from Share');
    const visit = await watch(page, first.errors);
    await update(page, site);
    await arrived(page);
    await stays(page, visit);
    await expect(undo.getByRole('button', { name: 'Undo' })).toBeVisible();

    await setVisibility(page, 'hidden');
    await reloadedIntoNew(page, site, visit);
    await expect(page.locator('.open-list .label').filter({ hasText: 'Added just now' })).toHaveCount(1);
    await expect(page.locator('.toast.ask')).toHaveCount(0);
    expect(visit.errors).toEqual([]);
  });

  test('arrived while touched, and the leaving reload never ran (a phone froze the page): coming back applies it', async ({ page, site }) => {
    const visit = await installed(page, site);
    await touch(page);
    await update(page, site);
    await arrived(page);
    await stays(page, visit);

    // Away and back before the reload on leaving could run: it must see the page visible and hold off.
    await setVisibility(page, 'hidden', 'visible');
    await reloadedIntoNew(page, site, visit);
    expect(await page.evaluate(() => document.visibilityState)).toBe('visible');
    expect(visit.errors).toEqual([]);
  });

  test('arrived while away: it applies there and then, out of sight; coming back reloads nothing more', async ({ page, site }) => {
    const visit = await installed(page, site);
    await touch(page);
    await setVisibility(page, 'hidden');
    await expect.poll(() => page.evaluate(() => document.visibilityState)).toBe('hidden');
    await update(page, site);
    await reloadedIntoNew(page, site, visit);
    expect(await page.evaluate(() => document.visibilityState)).toBe('hidden');
    // Out of sight there was nothing to keep from being touched.
    expect(await page.evaluate(() => sessionStorage.getItem('e2e-inert'))).toBe('false');

    await page.evaluate(() => (window.__old = true));
    await setVisibility(page, 'visible');
    await stays(page, visit);
    expect(visit.errors).toEqual([]);
  });

  test('took over while the app was still starting (a slow start): it reloads into the new version once up', async ({ page, site }) => {
    await page.context().addInitScript(slowStart, SLOW_START);
    const visit = await installed(page, site);
    site.publish();
    // Opened again: the old worker serves the old app, and the browser finds the new one meanwhile.
    await page.evaluate(() => sessionStorage.setItem('e2e-slow-start', '1'));
    await page.reload();
    await reloadedIntoNew(page, site, visit, 2);
    expect(await page.evaluate(() => sessionStorage.getItem('e2e-takeover'))).toBe('while starting');

    // And that's the end of it: the new version doesn't reload again.
    await page.evaluate(() => (window.__old = true));
    await stays(page, visit);
    expect(visit.errors).toEqual([]);
  });

  test('first opened before it was installed: a later update applies too', async ({ page, site }) => {
    // The first install taking the page over is no update: this is still the page first opened...
    const visit = await watch(page, await opened(page, site));
    await stays(page, visit);
    expect(await page.evaluate(() => (performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming).type)).toBe('navigate');
    // ...but the next new version is one.
    await update(page, site);
    await reloadedIntoNew(page, site, visit);
    expect(visit.errors).toEqual([]);
  });

  test('opened but left untouched over 30 s: it no longer counts as just opened; leaving applies it', async ({ page, site }) => {
    await page.clock.install();
    const visit = await installed(page, site);
    await page.clock.fastForward(31_000);
    await update(page, site);
    await arrived(page);
    await stays(page, visit);

    await setVisibility(page, 'hidden');
    await reloadedIntoNew(page, site, visit);
    expect(visit.errors).toEqual([]);
  });
});
