import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { addTask, button, flushed, nowText, open, openTexts, settled } from './helpers';

async function toNotes(page: Page) {
  if (test.info().project.name !== 'desktop-chromium') await page.getByRole('tab', { name: 'Notes' }).click();
}

test.describe('notes', () => {
  test('writes, continues lists, saves, and turns a line into a task', async ({ page }) => {
    await open(page);
    await toNotes(page);
    const editor = page.getByRole('textbox', { name: 'Note' });
    await page.locator('.sheet-add').click();
    await expect(editor).toBeFocused();
    await editor.pressSequentially('Shopping\n- milk\neggs');
    await expect(editor).toHaveValue('Shopping\n- milk\n- eggs');
    // Return on an empty item ends the list.
    await editor.press('Enter');
    await editor.press('Enter');
    await expect(editor).toHaveValue('Shopping\n- milk\n- eggs\n');
    // Make the "eggs" line a task.
    await editor.press('ArrowUp');
    await button(page, 'Make task').click();
    await expect(page.locator('.toast')).toContainText('eggs');
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText('Shopping');
    await flushed(page);
    await page.reload();
    await toNotes(page);
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText('Shopping');
    // Formatted now (the editor is hidden until you write): the text is as it was typed.
    await expect(page.locator('.md li')).toHaveText(['milk', 'eggs']);
    await expect(page.locator('textarea.editor')).toHaveValue('Shopping\n- milk\n- eggs\n');
    if (test.info().project.name !== 'desktop-chromium') await page.getByRole('tab', { name: 'Tasks' }).click();
    await expect(openTexts(page).first()).toHaveText('eggs');
  });

  test('deleting a sheet has undo; a blank sheet you leave disappears', async ({ page }) => {
    await open(page);
    await toNotes(page);
    const phone = test.info().project.name !== 'desktop-chromium';
    await page.locator('.sheet-add').click();
    await page.getByRole('textbox', { name: 'Note' }).fill('Temporary');
    // On a phone the sheet tabs come back once you're done writing.
    if (phone) await button(page, 'Done').click();
    await expect(page.locator('.sheet-pill')).toHaveCount(2);
    await button(page, 'Delete this sheet').click();
    await expect(page.locator('.sheet-pill')).toHaveCount(1);
    await button(page, 'Undo').click();
    await expect(page.locator('.sheet-pill')).toHaveCount(2);
    // A new blank sheet, then going back: the blank one is tidied away.
    await page.locator('.sheet-add').click();
    await expect(page.locator('.sheet-pill')).toHaveCount(3);
    if (phone) await button(page, 'Done').click();
    await settled(page);
    await page.locator('.sheet-pill').first().click();
    await expect(page.locator('.sheet-pill')).toHaveCount(2);
  });
});

test.describe('your data', () => {
  test('two tabs stay in step', async ({ page, context }) => {
    await open(page);
    const other = await context.newPage();
    await open(other);
    await addTask(page, 'Seen in both tabs');
    await expect(openTexts(other).first()).toHaveText('Seen in both tabs');
    await other.locator('.now-card').getByRole('checkbox').click();
    await expect(page.locator('.now-card')).toHaveCount(0, { timeout: 4000 });
  });

  test('a note edited in another window reaches this one, even mid-writing', async ({ page, context }) => {
    await open(page);
    await toNotes(page);
    const editor = page.getByRole('textbox', { name: 'Note' });
    // The note shows formatted: a click on it opens the editor.
    await page.locator('.md').click();
    await editor.press('Control+End');
    const other = await context.newPage();
    await open(other);
    await toNotes(other);
    const otherEditor = other.getByRole('textbox', { name: 'Note' });
    await other.locator('.md').click();
    await otherEditor.press('Control+End');
    await otherEditor.pressSequentially('\nAdded in the other window');
    await flushed(other);
    // This window, still focused in the editor, has picked up the other window's line…
    await expect(editor).toHaveValue(/Added in the other window$/);
    await expect(editor).toBeFocused();
    // …so typing here keeps it rather than overwriting it.
    await editor.press('Control+End');
    await editor.pressSequentially('\nAnd this one');
    await flushed(page);
    await other.reload();
    await toNotes(other);
    await expect(other.locator('textarea.editor')).toHaveValue(/Added in the other window\nAnd this one$/);
  });

  test('imports a Tack for Windows file, merging without overwriting', async ({ page }) => {
    await open(page);
    await addTask(page, 'Already here');
    const file = JSON.stringify({
      version: 1,
      tasks: [
        { id: 'aaaaaaaaaaaa', text: 'Finish the drainage report draft', created: '2026-10-16T09:00:00.1234567+01:00', now: true },
        { id: 'bbbbbbbbbbbb', text: 'Reply to Mia about the site photos', created: '2026-10-16T09:01:00+01:00', now: false },
        { id: 'dddddddddddd', text: 'Send the revised fee proposal', created: '2026-10-15T09:02:00+01:00', completed: '2026-10-16T08:00:00+01:00', now: false },
      ],
      sheets: [{ id: 's1s1s1s1s1s1', text: 'Site visit\n- rebar', updated: '2026-10-16T10:00:00+01:00' }],
      settings: { theme: 'dark' },
    });
    await button(page, 'Settings').click();
    await page.locator('dialog input[type=file]').setInputFiles({ name: 'tack.json', mimeType: 'application/json', buffer: Buffer.from(file) });
    // Settings steps aside by itself, so the toast and its Undo are in reach straight away.
    await expect(page.locator('dialog[open]')).toHaveCount(0);
    await expect(page.locator('.toast')).toContainText('Imported 3 tasks and 1 sheet');
    await expect(nowText(page)).toHaveText('Finish the drainage report draft');
    await expect(openTexts(page).filter({ hasText: 'Already here' })).toHaveCount(1);
    await expect(openTexts(page).filter({ hasText: 'Reply to Mia' })).toHaveCount(1);
    // Undo takes the import back out.
    await button(page, 'Undo').click();
    await expect(openTexts(page).filter({ hasText: 'Reply to Mia' })).toHaveCount(0);
  });

  test('import problems are explained inside Settings, not behind it', async ({ page }) => {
    await open(page);
    await button(page, 'Settings').click();
    await page.locator('dialog input[type=file]').setInputFiles({ name: 'notes.json', mimeType: 'application/json', buffer: Buffer.from('{"hello": 1}') });
    await expect(page.locator('dialog .data-status')).toHaveText("This doesn't look like a Tack file.");
    await expect(page.locator('dialog[open]')).toHaveCount(1);
  });

  test('exports a desktop-readable tack.json', async ({ page }) => {
    test.skip(test.info().project.name !== 'desktop-chromium', 'downloads on desktop');
    await open(page);
    await addTask(page, 'Exported task');
    await button(page, 'Settings').click();
    const [download] = await Promise.all([page.waitForEvent('download'), button(page, 'Export').click()]);
    expect(download.suggestedFilename()).toMatch(/^tack-\d{4}-\d{2}-\d{2}\.json$/);
    const json = JSON.parse(readFileSync(await download.path(), 'utf8'));
    expect(json.version).toBe(1);
    expect(json.tasks[0]).toMatchObject({ text: 'Tick this circle to finish your first task', now: true });
    expect(json.tasks.some((t: { text: string }) => t.text === 'Exported task')).toBe(true);
    expect(json.tackFocus.format).toBe(1);
  });

  test('keeps working offline after the first visit', async ({ page, context }) => {
    test.skip(test.info().project.name === 'iphone-webkit', 'Playwright WebKit on Windows has no service workers');
    await open(page);
    await page.evaluate(() => navigator.serviceWorker.ready);
    await addTask(page, 'Written online');
    await flushed(page);
    await context.setOffline(true);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page).first()).toHaveText('Written online');
    await addTask(page, 'Written offline');
    await flushed(page);
    await page.reload();
    await expect(openTexts(page).first()).toHaveText('Written offline');
    await context.setOffline(false);
  });
});

test.describe('privacy page', () => {
  test('is linked from Settings and opens as its own page', async ({ page }) => {
    await open(page);
    await button(page, 'Settings').click();
    // About has one; with sync offered, the Sync section has another.
    const about = page.locator('section.group').filter({ has: page.getByRole('heading', { name: 'About' }) });
    await expect(about.getByRole('link', { name: 'Privacy', exact: true })).toHaveAttribute('href', '/privacy/');
    for (const link of await page.getByRole('link', { name: 'Privacy', exact: true }).all()) await expect(link).toHaveAttribute('href', '/privacy/');
    await page.goto('/privacy/');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy');
    for (const name of ['On your devices', 'Sync with OneDrive (optional)', 'Microsoft permissions', 'Deleting your data']) {
      await expect(page.getByRole('heading', { level: 2, name })).toBeVisible();
    }
    await page.getByRole('link', { name: '← Tack Focus' }).click();
    await expect(page.locator('html.ready')).toHaveCount(1);
  });

  test('opens as itself from an installed app, offline too', async ({ page, context }) => {
    test.skip(test.info().project.name === 'iphone-webkit', 'Playwright WebKit on Windows has no service workers');
    await open(page);
    await page.evaluate(() => navigator.serviceWorker.ready);
    // The app's service worker answers page loads with the app itself: not this one.
    await page.goto('/privacy/');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy');
    expect(await page.evaluate(() => navigator.serviceWorker.controller !== null)).toBe(true);
    await context.setOffline(true);
    for (const path of ['/privacy/', '/privacy']) {
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy');
      await expect(page.locator('main')).toHaveCSS('max-width', '680px');
    }
    await page.goto('/');
    await expect(page.locator('html.ready')).toHaveCount(1);
    await context.setOffline(false);
  });
});

/**
 * Serves every page with the production Content Security Policy (from dist/_headers) and collects
 * what it refuses. Pages the service worker answers skip the route, so `documents` lists the ones
 * that really got the policy.
 */
async function underCsp(page: Page): Promise<{ violations: string[]; documents: string[] }> {
  const headers = readFileSync(join(import.meta.dirname, '../../dist/_headers'), 'utf8');
  const csp = /Content-Security-Policy: (.+)/.exec(headers)![1].replace('; upgrade-insecure-requests', '');
  const violations: string[] = [];
  const documents: string[] = [];
  await page.route('**/*', async (route) => {
    const response = await route.fetch();
    const isDoc = route.request().resourceType() === 'document';
    if (isDoc) documents.push(new URL(route.request().url()).pathname);
    await route.fulfill({ response, headers: isDoc ? { ...response.headers(), 'content-security-policy': csp } : response.headers() });
  });
  page.on('console', (m) => {
    if (/Content Security Policy|Refused to/i.test(m.text())) violations.push(m.text());
  });
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) => console.error(`Refused to: ${e.violatedDirective} ${e.blockedURI}`));
  });
  return { violations, documents };
}

test.describe('security and accessibility', () => {
  test('runs cleanly under the production Content Security Policy', async ({ page }) => {
    const { violations } = await underCsp(page);
    await open(page);
    await addTask(page, 'Under CSP');
    await page.locator('.now-card').getByRole('checkbox').click();
    await button(page, 'Settings').click();
    await page.waitForTimeout(700);
    expect(violations).toEqual([]);
  });

  test('a share from another app runs cleanly under the production Content Security Policy', async ({ page }) => {
    const { violations, documents } = await underCsp(page);
    // Markup in a share would trip the policy (an inline handler) if it were ever shown as markup.
    const fields = new URLSearchParams({ title: '<img src=/nothing.png onerror="console.log(1)">', text: '<b onclick="x()">bold</b>\nhttps://example.com/' });
    await open(page, `/?share&${fields}`);
    await expect(page.locator('.toast.ask .toast-text')).toHaveText('<img src=/nothing.png onerror="console.log(1)">');
    await page.locator('.toast.ask').getByRole('button', { name: 'Add', exact: true }).click();
    await expect(openTexts(page).first()).toHaveText('<img src=/nothing.png onerror="console.log(1)">');
    await expect(openTexts(page).nth(1)).toHaveText('<b onclick="x()">bold</b> https://example.com/');
    await page.waitForTimeout(500);
    expect(documents).toEqual(['/']);
    expect(violations).toEqual([]);
  });

  test('the privacy page runs cleanly under the production Content Security Policy', async ({ page }) => {
    const { violations, documents } = await underCsp(page);
    await page.goto('/privacy/');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy');
    // Its stylesheet loads (style-src 'self').
    await expect(page.locator('main')).toHaveCSS('max-width', '680px');
    expect(documents).toEqual(['/privacy/']);
    expect(violations).toEqual([]);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`no automatically detectable accessibility issues (${theme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' });
      await open(page);
      const scan = () => new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
      let result = await scan();
      expect(result.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
      await toNotes(page);
      await page.waitForTimeout(300);
      result = await scan();
      expect(result.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
      await button(page, 'Settings').click();
      await page.waitForTimeout(400);
      result = await scan();
      expect(result.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
      await page.goto('/privacy/');
      await expect(page.getByRole('heading', { level: 1 })).toHaveText('Privacy');
      result = await scan();
      expect(result.violations.map((v) => `${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
    });
  }
});
