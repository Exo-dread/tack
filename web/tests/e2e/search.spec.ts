import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { addTask, button, composer, nowText, open, openTexts, settled } from './helpers';

/**
 * Search (Ctrl+F; on a phone, the button beside Settings): every space, open and finished tasks
 * and notes, whatever the case or accents; results grouped, each with its space while there are
 * spaces; arrows and Enter (or a tap) go to what's found: its space, its page, the task lit up or
 * the sheet with the match selected; Esc closes and puts the focus back. The matching rule itself
 * is held to the cases Tack for Windows shares (tests/unit/search.test.ts).
 */

const desktop = () => test.info().project.name === 'desktop-chromium';
const dialog = (page: Page) => page.locator('dialog[open]');
const box = (page: Page) => page.getByRole('combobox', { name: 'Search tasks and notes' });
const options = (page: Page) => page.getByRole('listbox', { name: 'Results' }).getByRole('option');
const title = (page: Page) => page.locator('.space-title');

async function openSearch(page: Page) {
  if (desktop()) await page.keyboard.press('Control+f');
  else await button(page, 'Search').click();
  await expect(box(page)).toBeFocused();
}

/** Types a query and waits for search to have run (it follows typing a moment later). */
async function search(page: Page, query: string) {
  await box(page).fill(query);
  await expect(page.getByRole('status')).toHaveText(/result/);
  await settled(page);
}

async function makeSpaces(page: Page, ...names: string[]) {
  await button(page, 'Settings').click();
  for (const name of names) {
    await dialog(page).getByRole('button', { name: 'New space' }).click();
    const field = dialog(page).getByRole('textbox', { name: 'Name of the new space' });
    await field.fill(name);
    await field.press('Enter');
    await expect(dialog(page).getByRole('textbox', { name: `Name of ${name}` })).toHaveValue(name);
  }
  await dialog(page).getByRole('button', { name: 'Done', exact: true }).first().click();
  await expect(dialog(page)).toHaveCount(0);
}

async function show(page: Page, name: string) {
  await title(page).click();
  await dialog(page).getByRole('button', { name: new RegExp(`^${name}, \\d+ open$`) }).click();
  await expect(dialog(page)).toHaveCount(0);
  await expect(title(page)).toHaveText(name);
  await settled(page);
}

async function toNotes(page: Page) {
  if (desktop()) return;
  // While a box has the caret the dock's switch steps aside: put the keyboard away first.
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.getByRole('tab', { name: 'Notes' }).click();
}

async function toTasks(page: Page) {
  if (!desktop()) await page.getByRole('tab', { name: 'Tasks' }).click();
}

/** Writes a note on a new sheet. */
async function writeNote(page: Page, text: string) {
  await toNotes(page);
  await button(page, 'New sheet').click();
  const note = page.getByRole('textbox', { name: 'Note' });
  await note.fill(text);
  await note.blur();
  await toTasks(page);
}

test.describe('search', () => {
  test('nothing new on screen until it is used: on a phone a button beside Settings, on a wide screen Ctrl+F', async ({ page }) => {
    await open(page);
    await expect(dialog(page)).toHaveCount(0);
    const searchButton = page.getByRole('button', { name: 'Search', exact: true });
    if (desktop()) {
      await expect(searchButton).toBeHidden();
    } else {
      await expect(searchButton).toBeVisible();
      // Beside Settings, in the header.
      const next = await searchButton.evaluate((el) => (el.nextElementSibling as HTMLElement | null)?.getAttribute('aria-label'));
      expect(next).toBe('Settings');
    }
    // Settings lists the key.
    await button(page, 'Settings').click();
    await dialog(page).getByText('Keyboard shortcuts').click();
    await expect(dialog(page).locator('dt', { hasText: 'Ctrl+F' })).toBeVisible();
    await expect(dialog(page).locator('dd', { hasText: 'Search tasks and notes' })).toBeVisible();
  });

  test('finds tasks, done and notes whatever the case or accents; Esc closes and puts the focus back', async ({ page }) => {
    await open(page);
    await addTask(page, 'Send the clash report to the MEP team');
    await addTask(page, 'Café order for the meeting');
    // Finish one.
    await page.locator('.open-list > li.row', { hasText: 'Café order' }).locator('.check').click();
    await expect(openTexts(page).filter({ hasText: 'Café order' })).toHaveCount(0);
    await writeNote(page, 'Coordination meeting\n- level 3 ducts vs beams\n- CLASH list: Friday');

    if (desktop()) await composer(page).click();
    await openSearch(page);
    await expect(dialog(page)).toHaveAttribute('aria-label', 'Search');
    await expect(page.locator('.search-hint-title')).toHaveText('Search tasks and notes');
    await search(page, 'clash');
    await expect(page.getByRole('status')).toHaveText('2 results');
    await expect(page.locator('.search-count')).toHaveText('2');
    await expect(page.getByRole('listbox', { name: 'Results' }).getByRole('group')).toHaveCount(2);
    await expect(page.getByRole('group', { name: 'Tasks, 1' })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Notes, 1' })).toBeVisible();
    await expect(options(page)).toHaveCount(2);
    await expect(options(page).first()).toHaveAttribute('aria-selected', 'true');
    await expect(box(page)).toHaveAttribute('aria-activedescendant', (await options(page).first().getAttribute('id'))!);
    await expect(options(page).first().locator('mark')).toHaveText('clash');
    // A note matching below its title: the title, then the line with the match picked out.
    await expect(options(page).nth(1).locator('.search-line')).toHaveText('Coordination meeting');
    await expect(options(page).nth(1).locator('.search-second mark')).toHaveText('CLASH');
    // Accents and case don't matter, and done tasks are found too.
    await search(page, 'CAFE');
    await expect(page.getByRole('status')).toHaveText('1 result');
    await expect(page.getByRole('group', { name: 'Done, 1' })).toBeVisible();
    await expect(options(page).first().locator('mark')).toHaveText('Café');
    // Line breaks match spaces.
    await search(page, 'beams - clash');
    await expect(options(page)).toHaveCount(1);
    // Nothing found: said so.
    await search(page, 'zebra crossing');
    await expect(page.getByRole('status')).toHaveText('No results');
    await expect(page.locator('.search-hint-title')).toHaveText('Nothing matches “zebra crossing”');
    await expect(box(page)).toHaveAttribute('aria-expanded', 'false');

    await page.keyboard.press('Escape');
    await expect(dialog(page)).toHaveCount(0);
    if (desktop()) await expect(composer(page)).toBeFocused();
    // Open again: the last query is back, selected, so typing replaces it.
    await openSearch(page);
    await expect(box(page)).toHaveValue('zebra crossing');
    expect(await box(page).evaluate((el: HTMLInputElement) => [el.selectionStart, el.selectionEnd])).toEqual([0, 14]);
    await page.keyboard.type('report');
    await expect(box(page)).toHaveValue('report');
    await expect(page.getByRole('status')).toHaveText('1 result');
    await button(page, 'Done').click();
    await expect(dialog(page)).toHaveCount(0);
  });

  test('every space is searched; Enter goes to a task in another space, scrolled to, lit up and focused', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work', 'Dev');
    await show(page, 'Dev');
    await addTask(page, 'Fix the flaky sync report');
    await show(page, 'Work');
    // Enough tasks in Work that the one we look for is far below the fold.
    await composer(page).click();
    await page.evaluate(() => {
      const dt = new DataTransfer();
      dt.setData('text/plain', [...Array.from({ length: 40 }, (_, i) => `Filler task ${i + 1}`), 'Send the clash report'].join('\n'));
      document.querySelector('.composer-input')!.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
    });
    await expect(openTexts(page)).toHaveCount(41);
    await expect(openTexts(page).last()).toHaveText('Send the clash report');
    await settled(page);
    await show(page, 'Main');

    await openSearch(page);
    await search(page, 'REPORT');
    await expect(page.getByRole('status')).toHaveText('2 results');
    // In the one order, each with its space while there are spaces.
    await expect(options(page).locator('.search-meta')).toHaveText(['Work', 'Dev']);
    await expect(options(page).first()).toHaveAttribute('aria-label', 'Send the clash report, in Work');
    await page.keyboard.press('ArrowDown');
    await expect(options(page).nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(options(page).first()).toHaveAttribute('aria-selected', 'false');
    await page.keyboard.press('ArrowDown');
    await expect(options(page).nth(1)).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('ArrowUp');
    await expect(options(page).first()).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Enter');

    await expect(dialog(page)).toHaveCount(0);
    await expect(title(page)).toHaveText('Work');
    const row = page.locator('.open-list > li.row', { hasText: 'Send the clash report' });
    await expect(row.locator('.found-glow')).toHaveCount(1);
    await expect(row.locator('.text')).toBeFocused();
    await expect(row).toBeInViewport();
    // The light fades by itself.
    await expect(row.locator('.found-glow')).toHaveCount(0, { timeout: 4000 });

    // From All, nothing switches: All shows it already.
    await show(page, 'All');
    await openSearch(page);
    await search(page, 'flaky');
    await options(page).first().click();
    await expect(title(page)).toHaveText('All');
    await expect(page.locator('.open-list > li.row', { hasText: 'Fix the flaky sync report' }).locator('.text')).toBeFocused();
  });

  test('a finished task opens Done; the task in focus lights its card', async ({ page }) => {
    await open(page);
    await addTask(page, 'Old drawing register');
    await page.locator('.open-list > li.row', { hasText: 'Old drawing register' }).locator('.check').click();
    await expect(page.locator('.done')).toBeVisible();
    await expect(page.locator('.done-toggle')).toHaveAttribute('aria-expanded', 'false');
    await openSearch(page);
    await search(page, 'drawing');
    await page.keyboard.press('Enter');
    await expect(page.locator('.done-toggle')).toHaveAttribute('aria-expanded', 'true');
    const row = page.locator('.done-list > li.row', { hasText: 'Old drawing register' });
    await expect(row).toBeInViewport();
    await expect(row.locator('.found-glow')).toHaveCount(1);

    await openSearch(page);
    await search(page, 'first task');
    await expect(options(page).first().locator('.search-meta')).toHaveText('Now');
    await page.keyboard.press('Enter');
    await expect(page.locator('.now-card .found-glow')).toHaveCount(1);
    await expect(nowText(page)).toHaveText('Tick this circle to finish your first task');
  });

  test('a note found opens on its sheet, in its space, with the match selected and in sight', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    const lines = Array.from({ length: 120 }, (_, i) => (i === 100 ? '- level 3 ducts vs beams (Crème brûlée)' : `Line ${i + 1} of the coordination notes`));
    await writeNote(page, `Coordination\n${lines.join('\n')}`);
    await writeNote(page, 'Another sheet');
    await show(page, 'Main');

    await openSearch(page);
    await search(page, 'creme brulee');
    await expect(options(page)).toHaveCount(1);
    await expect(options(page).first()).toHaveAttribute('aria-label', 'Note Coordination: - level 3 ducts vs beams (Crème brûlée), in Work');
    await options(page).first().click();
    await expect(dialog(page)).toHaveCount(0);
    await expect(title(page)).toHaveText('Work');
    const note = page.getByRole('textbox', { name: 'Note' });
    await expect(note).toBeFocused();
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText('Coordination');
    const selected = await note.evaluate((el: HTMLTextAreaElement) => el.value.slice(el.selectionStart, el.selectionEnd));
    expect(selected).toBe('Crème brûlée');
    // In sight: the paper scrolled down to it.
    const sight = await note.evaluate((ta: HTMLTextAreaElement) => {
      const line = ta.value.slice(0, ta.selectionStart).split('\n').length;
      const lineHeight = parseFloat(getComputedStyle(ta).lineHeight);
      return { scrolled: ta.scrollTop, approx: line * lineHeight, height: ta.clientHeight };
    });
    expect(sight.scrolled).toBeGreaterThan(0);
    expect(sight.approx - sight.scrolled).toBeGreaterThan(0);
    expect(sight.approx - sight.scrolled).toBeLessThan(sight.height + 60);
  });

  test('on a 360 px phone the header keeps room for the space name beside ⋯, Search and Settings', async ({ page }) => {
    test.skip(desktop(), 'phone header');
    await page.setViewportSize({ width: 360, height: 780 });
    await open(page);
    const room = () => page.locator('header h1').evaluate((el) => el.getBoundingClientRect().width);
    expect(await room()).toBeGreaterThanOrEqual(200);
    await makeSpaces(page, 'Development', 'BIM coordination');
    await show(page, 'Development');
    await toNotes(page);
    await page.getByRole('textbox', { name: 'Note' }).click();
    await page.getByRole('textbox', { name: 'Note' }).pressSequentially('Clash list');
    await button(page, 'Done').click();
    await expect(page.getByRole('button', { name: 'More for this sheet' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Search', exact: true })).toBeVisible();
    // Three buttons and the name: the name keeps room enough for "Development" (measured as room,
    // since whether a name fits depends on the font), nothing overlaps, every button a full-height target.
    expect(await room()).toBeGreaterThanOrEqual(172);
    const boxes = await page.locator('header .actions .icon-button').evaluateAll((els) => els.filter((e) => (e as HTMLElement).offsetParent).map((e) => e.getBoundingClientRect().toJSON() as DOMRect));
    expect(boxes).toHaveLength(3);
    for (const b of boxes) {
      expect(b.width).toBeGreaterThanOrEqual(36);
      expect(b.height).toBeGreaterThanOrEqual(44);
      expect(b.right).toBeLessThanOrEqual(360);
    }
    for (let i = 1; i < boxes.length; i++) expect(boxes[i]!.left).toBeGreaterThanOrEqual(boxes[i - 1]!.right - 0.5);
    if (process.env.TACK_SHOTS) await page.screenshot({ path: `${process.env.TACK_SHOTS}/phone-360-notes-${test.info().project.name}.png` });
    await toTasks(page);
    if (process.env.TACK_SHOTS) await page.screenshot({ path: `${process.env.TACK_SHOTS}/phone-360-tasks-${test.info().project.name}.png` });
    await openSearch(page);
    await search(page, 'clash');
    if (process.env.TACK_SHOTS) await page.screenshot({ path: `${process.env.TACK_SHOTS}/phone-360-search-${test.info().project.name}.png` });
    // The box at the top of the sheet, all of it in sight.
    const field = await page.locator('.search-field').boundingBox();
    expect(field!.y).toBeGreaterThanOrEqual(0);
    expect(field!.y).toBeLessThan(120);
  });

  test('works with reduced motion: the found task stays lit, then plainly isn’t', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await open(page);
    await addTask(page, 'Calm search');
    await openSearch(page);
    await search(page, 'calm');
    await page.keyboard.press('Enter');
    const row = page.locator('.open-list > li.row', { hasText: 'Calm search' });
    await expect(row.locator('.found-glow')).toHaveCount(1);
    await expect(row.locator('.found-glow')).toHaveCount(0, { timeout: 4000 });
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`no automatically detectable accessibility issues in search (${theme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' });
      await open(page);
      await makeSpaces(page, 'Work');
      await show(page, 'Work');
      await addTask(page, 'Send the clash report');
      await page.locator('.open-list > li.row', { hasText: 'Send the clash report' }).locator('.check').click();
      await addTask(page, 'Clash run on level 3');
      await writeNote(page, 'Coordination\n- clash list: Friday');
      const scan = async (where: string) => {
        await page.waitForTimeout(300);
        const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
        expect(result.violations.map((v) => `${where}: ${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
      };
      await openSearch(page);
      await scan('empty');
      await search(page, 'clash');
      await scan('results');
      await page.keyboard.press('ArrowDown');
      await scan('moved');
      await search(page, 'nothing like it');
      await scan('no results');
      await page.keyboard.press('Escape');
      if (!desktop()) await scan('phone header');
      if (process.env.TACK_SHOTS) {
        await openSearch(page);
        await search(page, 'clash');
        await page.screenshot({ path: `${process.env.TACK_SHOTS}/search-${theme}-${test.info().project.name}.png` });
      }
    });
  }
});

test('a task being edited is kept when search opens over it', async ({ page }) => {
  await open(page);
  await addTask(page, 'Draft the drainage report');
  await openTexts(page).first().click();
  await page.locator('textarea.edit').fill('Draft the drainage report v2');
  await openSearch(page);
  await search(page, 'v2');
  await expect(options(page)).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(openTexts(page).first()).toHaveText('Draft the drainage report v2');
});
