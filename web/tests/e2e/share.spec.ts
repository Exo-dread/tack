import { expect, test, type Page } from '@playwright/test';
import { button, flushed, open, openTexts, settled } from './helpers';

/**
 * Sharing to Tack Focus from another app. Android's share sheet lists an installed Tack Focus
 * (the manifest's share_target) and opens /?share&title=…&text=…; iPhone doesn't let web apps
 * receive shares, and Playwright can't open a share sheet, so these load that address as the
 * share sheet would, in every project.
 */

const desktop = () => test.info().project.name === 'desktop-chromium';
const sheet = (page: Page) => page.locator('dialog[open]');
/** The toast on show: one that is fading out can linger a moment, inert. */
const toast = (page: Page) => page.locator('.toast:not([inert])');
const shared = (fields: Record<string, string>) => `/?share&${new URLSearchParams(fields)}`;

/** The page as the share sheet opens it, once Tack Focus has been opened before (it's installed). */
async function share(page: Page, fields: Record<string, string>): Promise<void> {
  await open(page, shared(fields));
  await settled(page);
}

test.describe('sharing to Tack Focus', () => {
  test('the manifest puts Tack Focus in the share sheet', async ({ request }) => {
    const manifest = await (await request.get('/manifest.webmanifest')).json();
    expect(manifest.share_target).toEqual({
      action: '/?share',
      method: 'GET',
      enctype: 'application/x-www-form-urlencoded',
      params: { title: 'title', text: 'text', url: 'url' },
    });
  });

  test('a shared page becomes one task, with Undo, and leaves nothing in the address', async ({ page }) => {
    await open(page);
    await flushed(page);
    const before = await openTexts(page).count();
    // Shared while Notes was open: it opens on Tasks, where the task is.
    if (!desktop()) {
      await page.getByRole('tab', { name: 'Notes' }).click();
      await flushed(page);
    }
    await share(page, { title: 'Revit 2024 release notes', text: 'https://help.autodesk.com/view/RVT/2024/ENU/' });
    expect(new URL(page.url()).search).toBe('');
    expect(new URL(page.url()).pathname).toBe('/');
    await expect(page.locator('.app')).toHaveAttribute('data-mode', 'tasks');
    await expect(openTexts(page).first()).toHaveText('Revit 2024 release notes https://help.autodesk.com/view/RVT/2024/ENU/');
    await expect(openTexts(page)).toHaveCount(before + 1);
    await expect(toast(page).locator('.toast-text')).toHaveText('Added from Share');
    await toast(page).getByRole('button', { name: 'Undo' }).click();
    await expect(openTexts(page)).toHaveCount(before);
    // Nothing comes back with a reload.
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page)).toHaveCount(before);
  });

  test('is added once: a reload adds nothing more', async ({ page }) => {
    await open(page);
    await flushed(page);
    const before = await openTexts(page).count();
    await share(page, { text: 'Check the level 3 clashes' });
    await expect(openTexts(page).first()).toHaveText('Check the level 3 clashes');
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page)).toHaveCount(before + 1);
    await expect(toast(page)).toHaveCount(0);
  });

  test('shared text of several lines is a task per line, as a paste is', async ({ page }) => {
    await open(page);
    await flushed(page);
    const before = await openTexts(page).count();
    await share(page, { title: 'Snags', text: '- Door 1.04\n- Door 1.05\n\nSite office\nhttps://maps.example/office' });
    await expect(openTexts(page).nth(0)).toHaveText('Snags');
    await expect(openTexts(page).nth(1)).toHaveText('Door 1.04');
    await expect(openTexts(page).nth(2)).toHaveText('Door 1.05');
    await expect(openTexts(page).nth(3)).toHaveText('Site office https://maps.example/office');
    await expect(toast(page).locator('.toast-text')).toHaveText('Added 4 tasks from Share');
    await toast(page).getByRole('button', { name: 'Undo' }).click();
    await expect(openTexts(page)).toHaveCount(before);
  });

  test('what is shared is only ever text: never markup, never a link', async ({ page }) => {
    await open(page);
    await flushed(page);
    await share(page, { title: '<img src=x onerror="window.__shared=1">', text: 'javascript:window.__shared=2', url: 'javascript:window.__shared=3' });
    await expect(openTexts(page).first()).toHaveText('<img src=x onerror="window.__shared=1">: javascript:window.__shared=2 javascript:window.__shared=3');
    expect(await page.locator('.pages img, .pages a, .pages script').count()).toBe(0);
    await openTexts(page).first().click();
    await expect(page.locator('textarea.edit')).toBeFocused();
    await page.locator('textarea.edit').press('Escape');
    expect(await page.evaluate(() => (window as Window & { __shared?: number }).__shared)).toBeUndefined();
    expect(new URL(page.url()).pathname).toBe('/');
  });

  test('lands in the space on show', async ({ page }) => {
    await open(page);
    await flushed(page);
    await button(page, 'Settings').click();
    await sheet(page).getByRole('button', { name: 'New space' }).click();
    const field = sheet(page).getByRole('textbox', { name: 'Name of the new space' });
    await field.fill('Work');
    await field.press('Enter');
    await expect(sheet(page).getByRole('textbox', { name: 'Name of Work' })).toHaveValue('Work');
    await sheet(page).getByRole('button', { name: 'Done', exact: true }).first().click();
    await page.locator('.space-title').click();
    await sheet(page).getByRole('button', { name: /^Work, \d+ open$/ }).click();
    await expect(page.locator('.space-title')).toHaveText('Work');
    await flushed(page);
    await share(page, { text: 'Send the federated model' });
    await expect(page.locator('.space-title')).toHaveText('Work');
    await expect(openTexts(page)).toHaveText(['Send the federated model']);
    await page.locator('.space-title').click();
    await sheet(page).getByRole('button', { name: /^Main, \d+ open$/ }).click();
    await expect(page.locator('.space-title')).toHaveText('Main');
    await expect(openTexts(page).filter({ hasText: 'Send the federated model' })).toHaveCount(0);
  });

  test('more lines than a paste takes adds nothing, and says so', async ({ page }) => {
    await open(page);
    await flushed(page);
    const before = await openTexts(page).count();
    await share(page, { text: Array.from({ length: 10_001 }, (_, i) => `line ${i}`).join('\n') });
    await expect(toast(page).locator('.toast-text')).toHaveText('Share up to 10,000 lines at once');
    await expect(openTexts(page)).toHaveCount(before);
  });

  test('nothing to add says so, and adds nothing', async ({ page }) => {
    await open(page);
    await flushed(page);
    const before = await openTexts(page).count();
    await share(page, { text: '   ' });
    await expect(toast(page).locator('.toast-text')).toHaveText('Nothing to add from Share');
    await expect(openTexts(page)).toHaveCount(before);
  });

  test('a share from a start that went no further is added when the page comes back, once', async ({ page }) => {
    await open(page);
    await flushed(page);
    const before = await openTexts(page).count();
    // As takeShare leaves it when the page goes to Microsoft first (the daily renewal visit).
    await page.evaluate(() => sessionStorage.setItem('tack.share', JSON.stringify({ title: '', text: 'Kept through the visit', url: '' })));
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page).first()).toHaveText('Kept through the visit');
    expect(await page.evaluate(() => sessionStorage.getItem('tack.share'))).toBeNull();
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page)).toHaveCount(before + 1);
    // One that isn't three strings is dropped.
    await page.evaluate(() => sessionStorage.setItem('tack.share', '{"text":["not","text"]}'));
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page)).toHaveCount(before + 1);
    expect(await page.evaluate(() => sessionStorage.getItem('tack.share'))).toBeNull();
  });

  test('works offline: the installed app answers the share itself', async ({ page, context }) => {
    test.skip(test.info().project.name === 'iphone-webkit', 'Playwright WebKit on Windows has no service workers');
    await open(page);
    await flushed(page);
    await page.evaluate(() => navigator.serviceWorker.ready);
    await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);
    await context.setOffline(true);
    await share(page, { title: 'Written on site', text: 'Measure the riser' });
    await expect(openTexts(page).first()).toHaveText('Written on site: Measure the riser');
    expect(new URL(page.url()).search).toBe('');
    await context.setOffline(false);
  });
});
