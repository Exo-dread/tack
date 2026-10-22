import { expect, test, type BrowserContext, type BrowserContextOptions, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { FakeMicrosoft } from '../fakes/microsoft';
import { addTask, open, openTexts } from './helpers';

/**
 * Sync from end to end in real browser engines: signing in, two devices exchanging through
 * OneDrive, going offline, interrupted uploads, the daily sign-in renewal (in a hidden frame,
 * or by a quick visit to Microsoft where frames can't, and what shows when that visit is slow,
 * stuck or can't get through), and being asked to sign in only when Microsoft insists. Microsoft itself is played by tests/fakes/microsoft.ts behind Playwright
 * routes; everything in the page is the production build.
 */

const MICROSOFT = /^https:\/\/(login\.microsoftonline\.com|login\.live\.com|graph\.microsoft\.com|dl\.files\.1drv\.com)\//;

// Pages controlled by a service worker escape Playwright's routing in WebKit, and these requests
// must never reach the real Microsoft. Tack's worker leaves other origins alone anyway.
test.use({ serviceWorkers: 'block' });

async function useMicrosoft(context: BrowserContext, ms: FakeMicrosoft): Promise<void> {
  const local = new URL(test.info().project.use.baseURL!).origin;
  // Anything else leaving the machine is a test bug: fail it loudly.
  await context.route((url) => url.origin !== local && !MICROSOFT.test(url.href), (route) => {
    ms.log.push({ method: route.request().method(), url: `UNEXPECTED ${route.request().url()}`, headers: {}, body: null, frame: false });
    return route.abort('blockedbyclient');
  });
  await context.route(MICROSOFT, async (route) => {
    const req = route.request();
    let frame = false;
    try {
      frame = !!req.frame().parentFrame();
    } catch {
      /* not from a frame */
    }
    const result = ms.handle({ method: req.method(), url: req.url(), headers: await req.allHeaders(), body: req.postData() }, frame);
    const answer = async () => {
      if (result === 'abort') return route.abort('connectionfailed');
      if (result.status === 302 && req.isNavigationRequest() && test.info().project.name.includes('webkit')) {
        // Playwright can't answer a navigation with a redirect in WebKit; a page that moves on does the same.
        const to = JSON.stringify(result.headers.location);
        return route.fulfill({ status: 200, contentType: 'text/html', body: `<!doctype html><script>location.replace(${to})</script>` });
      }
      await route.fulfill({ status: result.status, headers: result.headers, body: result.body });
    };
    const held = ms.held({ method: req.method(), url: req.url() });
    if (!held) return answer();
    await held;
    // Slow or stuck: the page may have moved on meanwhile (reloaded, say), which cancels the request.
    await answer().catch(() => {});
  });
}

/** A second device: its own browser profile (storage), same kind of browser. */
async function device(browser: import('@playwright/test').Browser, ms: FakeMicrosoft): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext(test.info().project.use as BrowserContextOptions);
  await useMicrosoft(context, ms);
  const page = await context.newPage();
  await open(page);
  return { context, page };
}

const settings = (page: Page) => page.locator('dialog.sheet');
const syncStatus = (page: Page) => settings(page).locator('.sync-status');

async function openSettings(page: Page): Promise<void> {
  if (await settings(page).isVisible()) return;
  await page.getByRole('button', { name: /^Settings/ }).click();
  await expect(settings(page)).toBeVisible();
}

async function closeSettings(page: Page): Promise<void> {
  if (!(await settings(page).isVisible())) return;
  await settings(page).getByRole('button', { name: 'Done' }).click();
  await expect(settings(page)).toBeHidden();
}

/** Settings → Turn on sync → Microsoft → back in the app, syncing. */
async function turnOn(page: Page): Promise<void> {
  await openSettings(page);
  await settings(page).getByRole('button', { name: 'Turn on sync' }).click();
  await expect(page.locator('html.ready')).toHaveCount(1);
  await expect(page.locator('.toast-text')).toHaveText(/Sync is on/);
  await expect.poll(() => page.evaluate(() => location.hash)).toBe('');
}

/** What Tack keeps about the sign-in on this device. */
interface AuthRecord {
  tokens: { refreshExpires: number } | null;
  frameWorks: boolean | null;
  frameRefused: number;
  lastRenewVisit: number;
  visitReturned: boolean;
}

async function authRecord(page: Page): Promise<AuthRecord> {
  return page.evaluate(async () => {
    const db = await new Promise<IDBDatabase>((r) => { const q = indexedDB.open('tack'); q.onsuccess = () => r(q.result); });
    const auth = await new Promise<AuthRecord>((r) => { const q = db.transaction('meta').objectStore('meta').get('x:auth'); q.onsuccess = () => r(q.result); });
    db.close();
    return auth;
  });
}

/**
 * Time passes for the sign-in: its last renewal visit moves `hours` into the past. Done from a
 * plain page of Tack's (the privacy page), so the app can't write over it meanwhile.
 */
async function visitedHoursAgo(page: Page, hours: number): Promise<void> {
  await page.goto('/privacy/');
  await page.evaluate(async (ms) => {
    const db = await new Promise<IDBDatabase>((r) => { const q = indexedDB.open('tack'); q.onsuccess = () => r(q.result); });
    const store = () => db.transaction('meta', 'readwrite').objectStore('meta');
    const auth = await new Promise<{ lastRenewVisit: number }>((r) => { const q = store().get('x:auth'); q.onsuccess = () => r(q.result); });
    await new Promise((r) => { const q = store().put({ ...auth, lastRenewVisit: auth.lastRenewVisit - ms }, 'x:auth'); q.onsuccess = r; });
    db.close();
  }, hours * 3600_000);
}

/** Microsoft's quiet sign-in, as the renewal visit (and the hidden frame) asks for it. */
const PROMPT_NONE = /\/oauth2\/v2\.0\/authorize\?.*prompt=none/;
const TOKEN = /\/oauth2\/v2\.0\/token$/;
/** What Tack asks first, to know whether Microsoft can be reached at all. */
const DISCOVERY = /\/v2\.0\/\.well-known\/openid-configuration$/;
/** The notice while Tack is at Microsoft and back for the daily renewal (ui/renewing.ts). */
const renewing = (page: Page) => page.locator('div.renewing[role=status]');

/**
 * Sync on, and the hidden frame refused (as in Safari): the next time the app opens, it first
 * visits Microsoft to renew the sign-in. Returns how many such visits Microsoft has seen.
 */
async function dueForVisit(page: Page, context: BrowserContext, ms: FakeMicrosoft): Promise<() => number> {
  ms.framesWork = false;
  ms.refreshLifetime = 2 * 3600_000; // inside the 4 hours Tack renews ahead
  await useMicrosoft(context, ms);
  await open(page);
  await addTask(page, 'Before the renewal');
  await turnOn(page);
  await syncNow(page);
  await expect.poll(async () => (await authRecord(page)).frameRefused).toBeGreaterThan(0);
  const visits = () => ms.log.filter((r) => !r.frame && PROMPT_NONE.test(r.url)).length;
  expect(visits()).toBe(0);
  return visits;
}

/** The renewal notice as the page showed it at one moment. */
interface Shown {
  /** Which page load (each load of Tack's page has its own). */
  doc: string;
  /** null: it went away. */
  text: string | null;
  /** It's what you see: on top, mid-screen. */
  onTop: boolean;
  button: { label: string; x: number; y: number; pressable: boolean } | null;
  /** The app was showing too. */
  ready: boolean;
}

/**
 * Every change to the renewal notice from the next page load on, as the page itself sees it and
 * reports it. Playwright can't look into a page that's on its way to another site (in Chromium it
 * waits for that navigation to finish), which is exactly while the notice is up.
 */
async function watchNotices(page: Page): Promise<Shown[]> {
  const shown: Shown[] = [];
  await page.route('**/__e2e/notice', async (route) => {
    shown.push(JSON.parse(route.request().postData()!));
    await route.fulfill({ status: 204 });
  });
  await page.addInitScript(() => {
    // Once per page load (WebKit can run an init script twice in one).
    const w = window as typeof window & { e2eWatching?: boolean };
    if (w.e2eWatching) return;
    w.e2eWatching = true;
    const doc = Math.random().toString(36).slice(2, 8);
    let last = 'null';
    new MutationObserver(() => {
      // The live region goes in empty (so its words are announced when they come): shown once it has words.
      const region = document.querySelector('.renewing');
      const el = region?.textContent!.trim() ? region : null;
      if (!el && last === 'null') return;
      let state: Omit<Shown, 'doc'> = { text: null, onTop: false, button: null, ready: false };
      if (el) {
        const p = el.querySelector('p')!.getBoundingClientRect();
        const hit = document.elementFromPoint(p.left + p.width / 2, p.top + p.height / 2);
        const b = el.querySelector('button');
        const r = b?.getBoundingClientRect();
        const at = r && { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
        state = {
          text: el.textContent!.trim(),
          onTop: !!hit && el.contains(hit),
          button: b && at ? { label: b.textContent!, ...at, pressable: document.elementFromPoint(at.x, at.y) === b } : null,
          ready: document.documentElement.classList.contains('ready'),
        };
      }
      const json = JSON.stringify(state);
      if (json === last) return;
      last = el ? json : 'null';
      void fetch('/__e2e/notice', { method: 'POST', body: JSON.stringify({ doc, ...state }), keepalive: true });
    }).observe(document, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class'] });
  });
  return shown;
}

/** Sync now, and wait for that sync (not an earlier one) to finish. */
async function syncNow(page: Page): Promise<void> {
  await openSettings(page);
  const before = (await syncStatus(page).getAttribute('data-synced')) ?? '0';
  await settings(page).getByRole('button', { name: 'Sync now' }).click();
  await expect(syncStatus(page)).not.toHaveAttribute('data-synced', before, { timeout: 10_000 });
  await expect(syncStatus(page)).toHaveText(/Up to date/, { timeout: 10_000 });
}

test.describe('sync with OneDrive', () => {
  test('one tap turns it on, and two devices keep each other up to date', async ({ page, context, browser }) => {
    const ms = new FakeMicrosoft();
    await useMicrosoft(context, ms);
    await open(page);
    await addTask(page, 'Written on the phone');
    await turnOn(page);
    await syncNow(page);
    await expect(settings(page)).toContainText('ada@outlook.com');
    expect(ms.paths()).toEqual(expect.arrayContaining(['Apps/Tack Focus/tack-sync.json', 'Apps/Tack Focus/README.txt']));
    expect(ms.deviceFiles().size).toBe(1);

    // The second device joins: its own untouched welcome tasks stay behind.
    const pc = await device(browser, ms);
    await turnOn(pc.page);
    await syncNow(pc.page);
    await closeSettings(pc.page);
    await expect(openTexts(pc.page).filter({ hasText: 'Written on the phone' })).toHaveCount(1);
    // One "Tick this circle..." (the first device's), not two.
    await expect(pc.page.locator('.label', { hasText: 'Tick this circle to finish your first task' })).toHaveCount(1);

    // A change on either side reaches the other.
    await addTask(pc.page, 'Written on the second device');
    await syncNow(pc.page);
    await syncNow(page);
    await closeSettings(page);
    await expect(openTexts(page).filter({ hasText: 'Written on the second device' })).toHaveCount(1);
    await expect(settings(page)).toBeHidden();
    await pc.context.close();
  });

  test('works offline, and catches up when the connection is back', async ({ page, context, browser }) => {
    const ms = new FakeMicrosoft();
    await useMicrosoft(context, ms);
    await open(page);
    await turnOn(page);
    await syncNow(page);
    const other = await device(browser, ms);
    await turnOn(other.page);
    await syncNow(other.page);

    await context.setOffline(true);
    await closeSettings(page);
    await addTask(page, 'Written in a tunnel');
    await openSettings(page);
    await settings(page).getByRole('button', { name: 'Sync now' }).click();
    await expect(syncStatus(page)).toHaveText(/Offline/);
    await context.setOffline(false);
    await expect(syncStatus(page)).toHaveText(/Up to date/, { timeout: 15_000 });

    await syncNow(other.page);
    await closeSettings(other.page);
    await expect(openTexts(other.page).filter({ hasText: 'Written in a tunnel' })).toHaveCount(1);
    await other.context.close();
  });

  test('an upload cut off half way is simply made again', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    await useMicrosoft(context, ms);
    await open(page);
    await turnOn(page);
    await syncNow(page);
    await closeSettings(page);
    ms.fail(/:\/content$/, { abort: true, method: 'PUT' });
    await addTask(page, 'Survives a dropped connection');
    await expect.poll(() => [...ms.deviceFiles().values()].join(), { timeout: 15_000 }).toContain('Survives a dropped connection');
  });

  test('renews the daily sign-in in a hidden frame where the browser allows it', async ({ page, context, browserName }) => {
    test.skip(browserName === 'webkit', 'Safari blocks the cookie a hidden frame needs; covered by the next test');
    const ms = new FakeMicrosoft();
    ms.refreshLifetime = 2 * 3600_000; // inside the 4 hours Tack renews ahead
    await useMicrosoft(context, ms);
    await open(page);
    await turnOn(page);
    await syncNow(page);
    await expect.poll(() => ms.log.some((r) => r.frame && r.url.includes('/authorize') && r.url.includes('prompt=none'))).toBe(true);
    await expect.poll(() => page.evaluate(async () => {
      const db = await new Promise<IDBDatabase>((r) => { const q = indexedDB.open('tack'); q.onsuccess = () => r(q.result); });
      const auth = await new Promise<{ frameWorks: boolean | null }>((r) => { const q = db.transaction('meta').objectStore('meta').get('x:auth'); q.onsuccess = () => r(q.result); });
      return auth.frameWorks;
    }), { timeout: 15_000 }).toBe(true);
    await expect(page.locator('iframe')).toHaveCount(0);
  });

  test('where frames can’t, renews with a quick visit to Microsoft when the app opens, no questions asked', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    ms.framesWork = false;
    ms.refreshLifetime = 2 * 3600_000;
    await useMicrosoft(context, ms);
    await open(page);
    await addTask(page, 'Before the renewal');
    await turnOn(page);
    await syncNow(page);
    // The hidden frame was tried and refused: next time the app opens, it visits Microsoft first.
    await expect.poll(() => ms.log.some((r) => r.frame && r.url.includes('prompt=none'))).toBe(true);
    const visits = () => ms.log.filter((r) => !r.frame && r.url.includes('/authorize') && r.url.includes('prompt=none')).length;
    expect(visits()).toBe(0);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    expect(visits()).toBe(1);
    await expect(openTexts(page).filter({ hasText: 'Before the renewal' })).toHaveCount(1);
    await syncNow(page);
    // And not again straight away (at most once every few hours).
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    expect(visits()).toBe(1);
  });

  test('asks you to sign in again only when Microsoft wants you to', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    ms.framesWork = false;
    ms.refreshLifetime = 2 * 3600_000;
    await useMicrosoft(context, ms);
    await open(page);
    await turnOn(page);
    await syncNow(page);
    await expect.poll(() => ms.log.some((r) => r.frame && r.url.includes('prompt=none'))).toBe(true);
    await closeSettings(page);
    ms.account = null; // signed out of Microsoft in this browser meanwhile
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    const gear = page.getByRole('button', { name: 'Settings (sync needs attention)' });
    await expect(gear).toBeVisible();
    await gear.click();
    await expect(syncStatus(page)).toHaveText(/sign in again/);
    await settings(page).getByRole('button', { name: 'Sign in' }).click();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Settings', exact: true })).toBeVisible();
    await syncNow(page);
  });

  test('a copy of this device syncing elsewhere makes it take a new id, losing nothing', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    await useMicrosoft(context, ms);
    await open(page);
    await addTask(page, 'Mine');
    await turnOn(page);
    await syncNow(page);
    const [[name, text]] = [...ms.deviceFiles()];
    // Someone else now writes under this device's id (a copied browser profile, say).
    const doc = JSON.parse(text!);
    const later = (Number.parseInt(doc.written.slice(0, 9), 36) + 60_000).toString(36).padStart(9, '0');
    ms.putDeviceFile(name!, JSON.stringify({ ...doc, written: `${later}.0000.${doc.device}`, tasks: [] }));
    await syncNow(page);
    const files = [...ms.deviceFiles().keys()];
    expect(files.length).toBe(2);
    const device = await page.evaluate(async () => {
      const db = await new Promise<IDBDatabase>((r) => { const q = indexedDB.open('tack'); q.onsuccess = () => r(q.result); });
      return new Promise<string>((r) => { const q = db.transaction('meta').objectStore('meta').get('device'); q.onsuccess = () => r(q.result); });
    });
    expect(`${device}.json`).not.toBe(name);
    expect(files).toContain(`${device}.json`);
    expect(ms.deviceFiles().get(`${device}.json`)).toContain('Mine');
    await closeSettings(page);
    await expect(openTexts(page).filter({ hasText: 'Mine' })).toHaveCount(1);
  });

  test('turning it off keeps your tasks and forgets the sign-in', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    await useMicrosoft(context, ms);
    await open(page);
    await addTask(page, 'Stays here');
    await turnOn(page);
    await syncNow(page);
    // With the keyboard (Safari doesn't focus a clicked button): focus follows to the new state.
    await settings(page).getByRole('button', { name: 'Turn off' }).focus();
    await page.keyboard.press('Enter');
    await expect(settings(page).getByRole('button', { name: 'Turn on sync' })).toBeVisible();
    await expect(settings(page).locator('.sync')).toContainText('Sync is off');
    await expect(settings(page).getByRole('button', { name: 'Turn on sync' })).toBeFocused();
    await closeSettings(page);
    await expect(openTexts(page).filter({ hasText: 'Stays here' })).toHaveCount(1);
    const tokens = await page.evaluate(async () => {
      const db = await new Promise<IDBDatabase>((r) => { const q = indexedDB.open('tack'); q.onsuccess = () => r(q.result); });
      return new Promise((r) => { const q = db.transaction('meta').objectStore('meta').get('x:auth'); q.onsuccess = () => r(q.result?.tokens ?? null); });
    });
    expect(tokens).toBeNull();
    expect(ms.deviceFiles().size).toBe(1); // nothing deleted in OneDrive either
  });

  test('stays within the production Content Security Policy', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const headers = readFileSync(join(import.meta.dirname, '../../dist/_headers'), 'utf8');
    const csp = /Content-Security-Policy: (.+)/.exec(headers)![1]!.replace('; upgrade-insecure-requests', '');
    const origin = new URL(test.info().project.use.baseURL!).origin;
    await context.route((url) => url.origin === origin, async (route) => {
      const response = await route.fetch();
      const isDoc = route.request().resourceType() === 'document';
      await route.fulfill({ response, headers: isDoc ? { ...response.headers(), 'content-security-policy': csp } : response.headers() });
    });
    await useMicrosoft(context, ms);
    const violations: string[] = [];
    page.on('console', (m) => {
      if (/Content Security Policy|Refused to/i.test(m.text())) violations.push(m.text());
    });
    await page.addInitScript(() => {
      document.addEventListener('securitypolicyviolation', (e) => console.error(`Refused to: ${e.violatedDirective} ${e.blockedURI}`));
    });
    ms.refreshLifetime = 2 * 3600_000; // so the hidden-frame renewal runs too
    await open(page);
    await turnOn(page);
    await syncNow(page);
    await page.waitForTimeout(500);
    expect(violations).toEqual([]);

    // And the renewal by a visit, where frames are refused (with its check that Microsoft can be reached).
    ms.framesWork = false;
    await syncNow(page);
    await expect.poll(async () => (await authRecord(page)).frameRefused).toBeGreaterThan(0);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    expect(ms.log.filter((r) => !r.frame && PROMPT_NONE.test(r.url)).length).toBe(1);
    expect(ms.log.some((r) => DISCOVERY.test(r.url))).toBe(true);
    await page.waitForTimeout(500);
    expect(violations).toEqual([]);
  });
});

/**
 * The renewal visit happens before the app starts, and Microsoft's redirects leave Tack's page up
 * until Tack comes back: while that takes, Tack says so; slow or stuck there, you can open Tack
 * anyway, and after a while it opens by itself (without going again).
 */
test.describe('the daily renewal visit, on screen', () => {
  // Each signs in and syncs first, then waits on Microsoft: more than most tests (WebKit especially).
  test.describe.configure({ timeout: 60_000 });
  const ready = (page: Page, timeout = 10_000) => expect(page.locator('html.ready')).toHaveCount(1, { timeout });
  const tasksIntact = (page: Page) => expect(openTexts(page).filter({ hasText: 'Before the renewal' })).toHaveCount(1);
  const RENEWING = 'Renewing your OneDrive sign-in…';
  const LONGER = 'Renewing your OneDrive sign-in is taking longer than usual. Open Tack now';
  const FINISHING = 'Finishing your OneDrive sign-in…';
  const FINISHING_LONGER = 'Finishing your OneDrive sign-in is taking longer than usual. Open Tack now';
  /** Up and on top, and the app not yet showing. */
  const up = (text: string, button: object | null = null) => ({ doc: expect.any(String), text, onTop: true, button, ready: false });
  const gone = { doc: expect.any(String), text: null, onTop: false, button: null, ready: false };
  const openNow = { label: 'Open Tack now', x: expect.any(Number), y: expect.any(Number), pressable: true };

  /** Presses at a point as a person would on this device: a tap on a phone, a click elsewhere. */
  async function press(page: Page, at: { x: number; y: number }): Promise<void> {
    if (test.info().project.use.hasTouch) await page.touchscreen.tap(at.x, at.y);
    else await page.mouse.click(at.x, at.y);
  }

  /** This time Microsoft shows a page instead of sending you straight back (an outage notice, say). */
  async function microsoftShowsAPage(context: BrowserContext, ms: FakeMicrosoft): Promise<void> {
    await context.route(PROMPT_NONE, async (route) => {
      if (route.request().frame().parentFrame()) return route.fallback();
      ms.log.push({ method: 'GET', url: route.request().url(), headers: {}, body: null, frame: false });
      await route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: '<!doctype html><title>Microsoft</title><h1>Sorry, we&rsquo;re having trouble signing you in</h1>' });
    });
  }

  test('a slow trip says what’s happening, then opens renewed', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    const before = await authRecord(page);
    ms.hold(PROMPT_NONE, { ms: 2000 });
    await page.reload({ waitUntil: 'commit' });
    await expect.poll(() => shown.length).toBe(1);
    await ready(page);
    await expect(renewing(page)).toHaveCount(0);
    expect(shown).toEqual([up(RENEWING)]);
    expect(visits()).toBe(1);
    // Renewed: a newer sign-in, and this browser now known to need the visit.
    const after = await authRecord(page);
    expect(after.tokens!.refreshExpires).toBeGreaterThan(before.tokens!.refreshExpires);
    expect(after.frameWorks).toBe(false);
    expect(after.visitReturned).toBe(true); // the next one waits the full six hours
    await tasksIntact(page);
    await syncNow(page);
  });

  test('a quick trip shows nothing at all', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    const before = await authRecord(page);
    await page.reload({ waitUntil: 'commit' });
    await ready(page);
    await page.waitForTimeout(500);
    expect(shown).toEqual([]);
    expect(visits()).toBe(1);
    expect((await authRecord(page)).tokens!.refreshExpires).toBeGreaterThan(before.tokens!.refreshExpires);
    await tasksIntact(page);
  });

  test('stuck at Microsoft: after a while, a way to open Tack anyway, without going again', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    const local = new URL(page.url()).origin;
    ms.hold(PROMPT_NONE); // no answer at all (until released)
    const start = Date.now();
    await page.reload({ waitUntil: 'commit' });
    await expect.poll(() => shown.length, { timeout: 12_000 }).toBe(2);
    expect(Date.now() - start).toBeGreaterThanOrEqual(8000);
    expect(shown).toEqual([up(RENEWING), up(LONGER, openNow)]);
    expect(shown[1]!.doc).toBe(shown[0]!.doc);
    expect(visits()).toBe(1);

    await press(page, shown[1]!.button!);
    await ready(page);
    await expect(renewing(page)).toHaveCount(0);
    await tasksIntact(page);
    expect(visits()).toBe(1);
    await syncNow(page);

    // Microsoft answering after all changes nothing: that trip was called off.
    ms.release();
    await page.waitForTimeout(1000);
    expect(new URL(page.url()).origin).toBe(local);
    await expect(page.locator('html.ready')).toHaveCount(1);
    expect(visits()).toBe(1);
    expect(shown).toHaveLength(2);

    // It never came back, so it's tried again after an hour, not six (the sign-in would run out first).
    const before = await authRecord(page);
    expect(before.visitReturned).toBe(false);
    await visitedHoursAgo(page, 0.9);
    await page.goto('/');
    await ready(page);
    expect(visits()).toBe(1);
    await visitedHoursAgo(page, 0.2);
    await page.goto('/');
    await ready(page);
    expect(visits()).toBe(2);
    const after = await authRecord(page);
    expect(after.visitReturned).toBe(true);
    expect(after.tokens!.refreshExpires).toBeGreaterThan(before.tokens!.refreshExpires);
    await tasksIntact(page);
  });

  test('stuck and left alone: Tack opens by itself after 25 seconds, without going again', async ({ page, context }) => {
    // Playwright's fake clock can't reach a page that's on its way elsewhere, so this waits it out:
    // once, on the kind of phone that had the trouble.
    test.skip(test.info().project.name !== 'android-chromium', 'Waits the full 25 seconds: once is enough');
    test.setTimeout(90_000);
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    ms.hold(PROMPT_NONE);
    const start = Date.now();
    await page.reload({ waitUntil: 'commit' });
    await expect.poll(() => shown.length, { timeout: 12_000 }).toBe(2);
    await ready(page, 30_000);
    expect(Date.now() - start).toBeGreaterThanOrEqual(25_000);
    expect(shown).toEqual([up(RENEWING), up(LONGER, openNow)]);
    await expect(renewing(page)).toHaveCount(0);
    await tasksIntact(page);
    expect(visits()).toBe(1);
    await syncNow(page);
    ms.release();
  });

  test('back from a page of Microsoft’s own: Tack opens, without going again', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    await microsoftShowsAPage(context, ms);
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByRole('heading', { name: 'Sorry, we’re having trouble signing you in' })).toBeVisible();
    expect(page.url()).toMatch(PROMPT_NONE);
    expect(visits()).toBe(1);

    await page.goBack();
    await ready(page);
    await expect(renewing(page)).toHaveCount(0);
    await tasksIntact(page);
    expect(visits()).toBe(1);
    // Restored from the back/forward cache, the page reloads itself; otherwise Back loads it afresh.
    const how = await page.evaluate(() => (performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming).type);
    test.info().annotations.push({ type: 'back', description: how === 'reload' ? 'restored from the back/forward cache, then reloaded' : `loaded afresh (${how})` });
    await syncNow(page);
  });

  test('back to a page the browser kept for Back (the back/forward cache): Tack reloads and opens', async ({ page, context }) => {
    // Playwright's browsers don't keep pages for Back (Back above loads Tack afresh in all three),
    // so the return is played: once the notice is up, the event a kept page gets when it's back.
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    let back = () => {};
    const backNow = new Promise<void>((resolve) => (back = resolve));
    await page.route('**/__e2e/back', async (route) => {
      await backNow;
      await route.fulfill({ status: 204 }).catch(() => {});
    });
    await page.addInitScript(() => {
      const w = window as typeof window & { e2eBack?: boolean };
      if (w.e2eBack) return;
      w.e2eBack = true;
      new MutationObserver((_, observer) => {
        if (!document.querySelector('.renewing p')) return;
        observer.disconnect();
        void fetch('/__e2e/back').then(() => dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })));
      }).observe(document, { childList: true, subtree: true });
    });
    ms.hold(PROMPT_NONE);
    const start = Date.now();
    await page.reload({ waitUntil: 'commit' });
    await expect.poll(() => shown.length).toBe(1); // up, and waiting for Microsoft
    back();
    await ready(page);
    expect(Date.now() - start).toBeLessThan(8000); // by the restore, not the safety net
    await expect(renewing(page)).toHaveCount(0);
    await tasksIntact(page);
    expect(shown).toEqual([up(RENEWING)]);
    expect(visits()).toBe(1);
    ms.release();
  });

  test('back from Microsoft with a slow answer: says it’s finishing, then gets out of the way', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    const before = await authRecord(page);
    ms.hold(TOKEN, { ms: 2000, method: 'POST' });
    await page.reload({ waitUntil: 'commit' });
    await expect(renewing(page)).toHaveText(FINISHING);
    await expect(renewing(page)).toBeVisible();
    await ready(page);
    await expect(renewing(page)).toHaveCount(0);
    await expect.poll(() => shown.length).toBe(2);
    expect(shown).toEqual([up(FINISHING), gone]);
    expect(visits()).toBe(1);
    expect((await authRecord(page)).tokens!.refreshExpires).toBeGreaterThan(before.tokens!.refreshExpires);
    await tasksIntact(page);
    await syncNow(page);
  });

  test('back from Microsoft and stuck finishing: after a while, a way to open Tack anyway, without going again', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    ms.hold(TOKEN, { method: 'POST' }); // the last step of the sign-in gets no answer (until released)
    const start = Date.now();
    await page.reload({ waitUntil: 'commit' });
    await expect.poll(() => shown.length, { timeout: 12_000 }).toBe(2);
    expect(Date.now() - start).toBeGreaterThanOrEqual(8000);
    expect(shown).toEqual([up(FINISHING), up(FINISHING_LONGER, openNow)]);
    expect(shown[1]!.doc).toBe(shown[0]!.doc);
    expect(visits()).toBe(1);

    await press(page, shown[1]!.button!);
    await ready(page);
    await expect(renewing(page)).toHaveCount(0);
    await tasksIntact(page);
    expect(visits()).toBe(1);
    // Not back as far as renewing goes: tried again in an hour, and sync carries on meanwhile.
    expect((await authRecord(page)).visitReturned).toBe(false);
    await syncNow(page);
    ms.release();
    await page.waitForTimeout(500);
    expect(visits()).toBe(1);
    expect(shown).toHaveLength(2);
  });

  test('no way through to Microsoft: Tack opens as usual, not the browser’s error page, and goes once it can', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    const visits = await dueForVisit(page, context, ms);
    const shown = await watchNotices(page);
    const local = new URL(page.url()).origin;
    const before = await authRecord(page);
    // On Wi-Fi without internet (the browser still says it's online): nothing gets through, slowly.
    ms.hold(DISCOVERY, { ms: 1500 });
    ms.fail(DISCOVERY, { abort: true });
    ms.fail(PROMPT_NONE, { abort: true }); // going anyway would end on the browser's error page
    await page.reload({ waitUntil: 'commit' });
    await ready(page);
    expect(new URL(page.url()).origin).toBe(local);
    await expect.poll(() => shown.length).toBe(2);
    expect(shown).toEqual([up(RENEWING), gone]); // said while it tried, gone when Tack opened
    await expect(renewing(page)).toHaveCount(0);
    expect(visits()).toBe(0);
    await tasksIntact(page);
    // Not counted as a visit: the next time Tack opens with a way through, it goes.
    expect((await authRecord(page)).lastRenewVisit).toBe(before.lastRenewVisit);
    ms.faults.splice(0);
    await page.reload();
    await ready(page);
    expect(visits()).toBe(1);
    expect((await authRecord(page)).tokens!.refreshExpires).toBeGreaterThan(before.tokens!.refreshExpires);
    await tasksIntact(page);
    await syncNow(page);
  });

  test('turning sync on with a slow answer from Microsoft says so too', async ({ page, context }) => {
    const ms = new FakeMicrosoft();
    await useMicrosoft(context, ms);
    await open(page);
    const shown = await watchNotices(page);
    ms.hold(TOKEN, { ms: 2000, method: 'POST' });
    await openSettings(page);
    await settings(page).getByRole('button', { name: 'Turn on sync' }).click();
    await expect(renewing(page)).toHaveText(FINISHING);
    await ready(page);
    await expect(renewing(page)).toHaveCount(0);
    await expect(page.locator('.toast-text')).toHaveText(/Sync is on/);
    await expect.poll(() => shown.length).toBe(2);
    expect(shown).toEqual([up(FINISHING), gone]);
  });
});
