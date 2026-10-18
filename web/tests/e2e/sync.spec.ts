import { expect, test, type BrowserContext, type BrowserContextOptions, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { FakeMicrosoft } from '../fakes/microsoft';
import { addTask, open, openTexts } from './helpers';

/**
 * Sync from end to end in real browser engines: signing in, two devices exchanging through
 * OneDrive, going offline, interrupted uploads, the daily sign-in renewal (in a hidden frame,
 * or by a quick visit to Microsoft where frames can't), and being asked to sign in only when
 * Microsoft insists. Microsoft itself is played by tests/fakes/microsoft.ts behind Playwright
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
    if (result === 'abort') return route.abort('connectionfailed');
    if (result.status === 302 && req.isNavigationRequest() && test.info().project.name.includes('webkit')) {
      // Playwright can't answer a navigation with a redirect in WebKit; a page that moves on does the same.
      const to = JSON.stringify(result.headers.location);
      return route.fulfill({ status: 200, contentType: 'text/html', body: `<!doctype html><script>location.replace(${to})</script>` });
    }
    await route.fulfill({ status: result.status, headers: result.headers, body: result.body });
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
    await settings(page).getByRole('button', { name: 'Turn off' }).click();
    await expect(settings(page).getByRole('button', { name: 'Turn on sync' })).toBeVisible();
    await expect(settings(page).locator('.data-status')).toHaveText(/Sync is off/);
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
  });
});
