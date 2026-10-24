import { expect, test, type Page } from '@playwright/test';
import { button, open } from './helpers';

/**
 * Search at scale: ten thousand tasks and five hundred long notes, imported. Each keystroke is
 * timed from the key to the results on screen. Its own file, without traces: snapshots of ten
 * thousand rows would take far longer than the app does.
 */

test.use({ trace: 'off' });

const desktop = () => test.info().project.name === 'desktop-chromium';
const box = (page: Page) => page.getByRole('combobox', { name: 'Search tasks and notes' });

async function openSearch(page: Page) {
  if (desktop()) await page.keyboard.press('Control+f');
  else await button(page, 'Search').click();
  await expect(box(page)).toBeFocused();
}

async function search(page: Page, query: string) {
  await box(page).fill(query);
  await expect(page.getByRole('status')).toHaveText(/result/);
}

test('a group shows fifty, then Show more; quick on 10,000 tasks and 500 long notes', async ({ page }) => {
  test.slow();
  await open(page);
  // Imported, as a tack.json from Tack for Windows (or another device) brings them in.
  const words = ['clash', 'report', 'façade', 'Ducts', 'beams', 'model', 'Navisworks', 'level', 'review', 'Revit'];
  const id = (prefix: string, i: number) => `${prefix}${String(i).padStart(11, '0')}`;
  const file = JSON.stringify({
    version: 1,
    tasks: Array.from({ length: 10_000 }, (_, i) => ({ id: id('t', i), text: `${words[i % 10]} ${words[(i * 7) % 10]} task number ${i}`, created: '2026-10-16T09:00:00+01:00', now: false })),
    sheets: Array.from({ length: 500 }, (_, i) => ({
      id: id('s', i),
      text: [`Sheet ${i}`, ...Array.from({ length: 80 }, (_, j) => `- ${words[(i + j) % 10]} item ${j} on sheet ${i}, with some more words to make it long`)].join(String.fromCharCode(10)),
      updated: '2026-10-16T10:00:00+01:00',
    })),
  });
  await button(page, 'Settings').click();
  await page.locator('dialog input[type=file]').setInputFiles({ name: 'tack.json', mimeType: 'application/json', buffer: Buffer.from(file) });
  await expect(page.locator('dialog[open]')).toHaveCount(0, { timeout: 60_000 });
  await expect(page.locator('.subtitle')).toContainText(/100\d\d open/, { timeout: 60_000 });

  await openSearch(page);
  // Every keystroke of a query, timed from the key to the results on screen (less the pause after typing).
  const times = await page.evaluate(async () => {
    const input = document.querySelector<HTMLInputElement>('.search-input')!;
    const list = document.querySelector('#search-results')!;
    const out: number[] = [];
    for (const q of ['f', 'fa', 'fac', 'faca', 'facad', 'facade', 'clash', 'sheet 49 ']) {
      const t0 = performance.now();
      // Every query here finds something, so the list is drawn again for each.
      const drawn = new Promise<void>((resolve) => {
        const watch = new MutationObserver(() => {
          watch.disconnect();
          resolve();
        });
        watch.observe(list, { childList: true });
      });
      input.value = q;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      await drawn;
      // Laid out and painted.
      await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
      out.push(performance.now() - t0 - 90);
    }
    return out;
  });
  console.log(`        (search over 10,000 tasks and 500 notes: ${times.map((t) => Math.round(t)).join(', ')} ms)`);
  // Generous budgets: they catch something accidentally slow (folding everything again on
  // every key, or drawing every result), not small slowdowns. Playwright's WebKit for Windows
  // saves to IndexedDB about a hundred times slower than Safari, and the import's save is still
  // running alongside, so there it's held only to what catches a runaway; Chromium (a phone and
  // a desktop) to the real budget.
  const webkit = test.info().project.name === 'iphone-webkit';
  expect(times[0]).toBeLessThan(webkit ? 6000 : 3000);
  expect(Math.max(...times.slice(1))).toBeLessThan(webkit ? 5000 : 600);

  await search(page, 'task number');
  await expect(page.getByRole('status')).toHaveText('10,000 results');
  await expect(page.getByRole('group', { name: 'Tasks, 10,000' }).getByRole('option')).toHaveCount(51);
  const more = page.getByRole('option', { name: /^Show 200 more/ });
  await expect(more).toHaveText('Show 200 more (9,950 left)');
  await more.click();
  await expect(page.getByRole('group', { name: 'Tasks, 10,000' }).getByRole('option')).toHaveCount(251);
  await expect(page.getByRole('option').nth(50)).toHaveAttribute('aria-selected', 'true');
  if (desktop()) await expect(box(page)).toBeFocused();
});
