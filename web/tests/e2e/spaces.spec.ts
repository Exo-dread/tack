import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { addTask, button, composer, flushed, nowText, open, openTexts, settled } from './helpers';

/**
 * Spaces (Work, Dev, Personal...) on screen. With Main alone Tack must look and behave exactly
 * as before (the other suites run that way, untouched); these cover what appears once there's a
 * second space: Settings → Spaces, the title's switcher, Alt+0…9, adding into a space, moving
 * tasks and sheets with Undo, All with each task's space, the "Now in …" line, notes per space,
 * deleting a space with Undo, and the space on show surviving a reload.
 */

const desktop = () => test.info().project.name === 'desktop-chromium';
const sheet = (page: Page) => page.locator('dialog[open]');
const title = (page: Page) => page.locator('.space-title');
/** The toast on show: one that is fading out can linger a moment, inert. */
const toast = (page: Page) => page.locator('.toast:not([inert])');

async function openSettings(page: Page) {
  await button(page, 'Settings').click();
  await expect(sheet(page).getByRole('heading', { name: 'Settings' })).toBeVisible();
}

async function closeSheet(page: Page) {
  await sheet(page).getByRole('button', { name: 'Done', exact: true }).first().click();
  await expect(sheet(page)).toHaveCount(0);
}

/** Settings → Spaces → New space, for each name. */
async function makeSpaces(page: Page, ...names: string[]) {
  await openSettings(page);
  for (const name of names) {
    await sheet(page).getByRole('button', { name: 'New space' }).click();
    const field = sheet(page).getByRole('textbox', { name: 'Name of the new space' });
    await field.fill(name);
    await field.press('Enter');
    await expect(sheet(page).getByRole('textbox', { name: `Name of ${name}` })).toHaveValue(name);
  }
  await closeSheet(page);
}

/** The title's switcher: pick a space (or All). */
async function show(page: Page, name: string) {
  await title(page).click();
  await sheet(page).getByRole('button', { name: new RegExp(`^${name}, \\d+ open$`) }).click();
  await expect(sheet(page)).toHaveCount(0);
  await expect(title(page)).toHaveText(name);
  await settled(page);
}

async function toNotes(page: Page) {
  if (!desktop()) await page.getByRole('tab', { name: 'Notes' }).click();
}

async function toTasks(page: Page) {
  if (!desktop()) await page.getByRole('tab', { name: 'Tasks' }).click();
}

/** The names in Settings → Spaces, in order. */
const names = (page: Page) => sheet(page).locator('.space-name').evaluateAll((els) => els.map((e) => (e as HTMLInputElement).value));

const row = (page: Page, text: string) => page.locator('.open-list > li.row:not(.exiting)').filter({ has: page.locator('.label', { hasText: text }) });

test.describe('spaces', () => {
  test('with Main alone, nothing shows but Settings → Spaces', async ({ page }) => {
    await open(page);
    await expect(page.locator('.space-title, .now-line, .row-space, .sheet-more')).toHaveCount(0);
    await expect(page.locator('.title')).toHaveText('TasksNotesTack');
    await expect(composer(page)).toHaveAttribute('placeholder', 'Add a task');
    await expect(composer(page)).toHaveAttribute('aria-label', 'Add a task');
    // No Move among a task's tools.
    await openTexts(page).first().click();
    await expect(page.locator('.row-tools .t-move')).toHaveCount(0);
    await page.locator('textarea.edit').press('Escape');
    // The keys do nothing yet.
    if (desktop()) {
      await page.locator('body').press('Alt+1');
      await page.locator('body').press('Alt+0');
      await expect(page.locator('.space-title')).toHaveCount(0);
    }
    // Settings has the one new group: Main (to rename) and New space.
    await openSettings(page);
    const group = sheet(page).locator('section.group').filter({ has: page.getByRole('heading', { name: 'Spaces' }) });
    await expect(group.getByRole('textbox', { name: 'Name of Main' })).toHaveValue('Main');
    await expect(group.getByRole('button', { name: 'New space' })).toBeVisible();
    await expect(group.getByRole('button', { name: /Move|Delete/ })).toHaveCount(0);
    // Renaming Main alone changes nothing else either.
    await group.getByRole('textbox', { name: 'Name of Main' }).fill('Work');
    await group.getByRole('textbox', { name: 'Name of Main' }).press('Enter');
    await expect(group.getByRole('textbox', { name: 'Name of Work' })).toHaveValue('Work');
    await closeSheet(page);
    await expect(page.locator('.space-title')).toHaveCount(0);
  });

  test('make spaces in Settings, switch from the title, add into the space on show', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work', 'Dev');
    // Main is still on show: everything so far is in it.
    await expect(title(page)).toHaveText('Main');
    await expect(page.locator('.subtitle')).toHaveText(desktop() ? /· \d+ open/ : /^Tasks · \d+ open/);
    await title(page).click();
    await expect(sheet(page).getByRole('heading', { name: 'Spaces' })).toBeVisible();
    await expect(sheet(page).locator('.space-option-name')).toHaveText(['All', 'Main', 'Work', 'Dev', 'New space']);
    await expect(sheet(page).getByRole('button', { name: /^Main, \d+ open$/ })).toHaveAttribute('aria-current', 'true');
    await expect(sheet(page).getByRole('button', { name: 'Work, 0 open' })).toBeVisible();
    await sheet(page).getByRole('button', { name: 'Work, 0 open' }).click();
    await expect(title(page)).toHaveText('Work');
    await expect(openTexts(page)).toHaveCount(0);
    await expect(nowText(page)).toHaveCount(0);
    await expect(page.locator('.empty-title')).toHaveText('A clean slate');

    await expect(composer(page)).toHaveAttribute('placeholder', 'Add to Work');
    await addTask(page, 'Draft the drainage report');
    await addTask(page, 'Coordinate the clash run');
    await expect(openTexts(page)).toHaveText(['Coordinate the clash run', 'Draft the drainage report']);
    await expect(page.locator('.subtitle')).toContainText('2 open');
    await expect(page.locator('.orb-header')).toHaveAttribute('aria-label', 'Work: 2 open');
    // Main doesn't show them.
    await show(page, 'Main');
    await expect(openTexts(page).filter({ hasText: 'drainage' })).toHaveCount(0);
    // A new space from the switcher, shown at once.
    await title(page).click();
    await sheet(page).getByRole('button', { name: 'New space' }).click();
    await sheet(page).getByRole('textbox', { name: 'Name of the new space' }).fill('Personal');
    await sheet(page).getByRole('textbox', { name: 'Name of the new space' }).press('Enter');
    await expect(sheet(page)).toHaveCount(0);
    await expect(title(page)).toHaveText('Personal');
  });

  test('the task box adds to the space on show, and its draft stays through a switch', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work', 'Personal');
    await show(page, 'Work');
    await expect(composer(page)).toHaveAttribute('aria-label', 'Add a task to Work');
    await composer(page).click();
    await composer(page).fill('Book the dentist');
    // Switching from the title keeps what's typed; the task goes where you switched to.
    await show(page, 'Personal');
    await expect(composer(page)).toHaveValue('Book the dentist');
    await expect(composer(page)).toHaveAttribute('placeholder', 'Add to Personal');
    await composer(page).click();
    await composer(page).press('Enter');
    await expect(openTexts(page)).toHaveText(['Book the dentist']);
    await show(page, 'Work');
    await expect(openTexts(page).filter({ hasText: 'dentist' })).toHaveCount(0);
    // A pasted list goes to the space on show too.
    await composer(page).click();
    await page.evaluate(() => {
      const input = document.querySelector<HTMLInputElement>('.composer-input')!;
      const data = new DataTransfer();
      data.setData('text/plain', 'Order the plans\nCall the surveyor');
      input.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    });
    await expect(toast(page)).toContainText('Added 2 tasks');
    await expect(openTexts(page)).toHaveText(['Order the plans', 'Call the surveyor']);
    // From All, new tasks go to Main.
    await show(page, 'All');
    await expect(composer(page)).toHaveAttribute('aria-label', 'Add a task');
    await addTask(page, 'Unfiled');
    await expect(row(page, 'Unfiled').locator('.row-space')).toHaveText('Main');
  });

  test('move a task to another space, with Undo; All shows every task with its space', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work', 'Personal');
    await show(page, 'Work');
    await addTask(page, 'Pay the gas bill');
    await addTask(page, 'Review the model');
    await openTexts(page).filter({ hasText: 'Pay the gas bill' }).click();
    await button(page, 'Move').click();
    await expect(sheet(page).getByRole('heading', { name: 'Move to…' })).toBeVisible();
    // Where it is now can't be picked.
    await expect(sheet(page).getByRole('button', { name: 'Work' })).toBeDisabled();
    await sheet(page).getByRole('button', { name: 'Personal' }).click();
    await expect(toast(page)).toContainText('Moved to Personal');
    await expect(openTexts(page)).toHaveText(['Review the model']);
    await button(page, 'Undo').click();
    await expect(openTexts(page)).toHaveText(['Review the model', 'Pay the gas bill']);
    // Again, for good.
    await openTexts(page).filter({ hasText: 'Pay the gas bill' }).click();
    await button(page, 'Move').click();
    await sheet(page).getByRole('button', { name: 'Personal' }).click();
    await expect(openTexts(page)).toHaveText(['Review the model']);

    // All: the one order, as before, each task with its space.
    await show(page, 'All');
    await expect(composer(page)).toHaveAttribute('placeholder', 'Add a task');
    await expect(row(page, 'Review the model').locator('.row-space')).toHaveText('Work');
    await expect(row(page, 'Pay the gas bill').locator('.row-space')).toHaveText('Personal');
    await expect(row(page, 'Swipe').first().locator('.row-space').or(row(page, 'Hover').first().locator('.row-space'))).toHaveText('Main');
    await expect(openTexts(page).first()).toHaveText('Review the model');
    await expect(page.locator('.now-card .now-space')).toHaveText('· Main');
    await expect(page.locator('.subtitle')).toContainText(/\d+ open/);
    // From All, new tasks go to Main.
    await addTask(page, 'Unsorted thought');
    await expect(row(page, 'Unsorted thought').locator('.row-space')).toHaveText('Main');
  });

  test('the task in focus shows as a line in other spaces, which takes you there', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    const line = page.locator('.now-line');
    await expect(line).toHaveText('Now in Main:Tick this circle to finish your first task');
    // No tick, no ×: just the way there.
    await expect(line.getByRole('checkbox')).toHaveCount(0);
    await line.click();
    await expect(title(page)).toHaveText('Main');
    await expect(nowText(page)).toHaveText('Tick this circle to finish your first task');
    await expect(line).toHaveCount(0);
  });

  test('the keyboard focus is never lost: the Now line, Move (picked or not), a switch while editing', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    // The Now line goes with the switch; the focus lands on the card it pointed to.
    await page.locator('.now-line').focus();
    await page.keyboard.press('Enter');
    await expect(title(page)).toHaveText('Main');
    await expect(page.locator('.now-card .text')).toBeFocused();

    // Move, then Escape: back on the task.
    await addTask(page, 'Second');
    await addTask(page, 'First');
    await row(page, 'First').locator('.text').click();
    await button(page, 'Move').click();
    await expect(sheet(page).getByRole('heading', { name: 'Move to…' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(sheet(page)).toHaveCount(0);
    await expect(row(page, 'First').locator('.text')).toBeFocused();
    // Moved out of Main: the task that took its place.
    await row(page, 'First').locator('.text').click();
    await button(page, 'Move').click();
    await sheet(page).getByRole('button', { name: 'Work' }).click();
    await expect(sheet(page)).toHaveCount(0);
    await expect(row(page, 'Second').locator('.text')).toBeFocused();

    if (desktop()) {
      // Alt+2 while editing a task Work doesn't show: typing goes on in the task box.
      await row(page, 'Second').locator('.text').click();
      await expect(page.locator('textarea.edit')).toBeFocused();
      await page.keyboard.press('Alt+2');
      await expect(title(page)).toHaveText('Work');
      await expect(composer(page)).toBeFocused();
      // Alt+0 while editing a task All shows: the focus stays with it.
      await row(page, 'First').locator('.text').click();
      await page.keyboard.press('Alt+0');
      await expect(title(page)).toHaveText('All');
      await expect(row(page, 'First').locator('.text')).toBeFocused();
    }
  });

  test('a new space typed but not confirmed is kept when the sheet closes; Escape drops it', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    // The switcher: Done with a name typed makes it, without switching to it (as the desktop strip does);
    // Enter would have shown it.
    await title(page).click();
    await sheet(page).getByRole('button', { name: 'New space' }).click();
    await sheet(page).getByRole('textbox', { name: 'Name of the new space' }).fill('Personal');
    await closeSheet(page);
    await expect(title(page)).toHaveText('Main');
    await title(page).click();
    await expect(sheet(page).getByRole('button', { name: /^Personal, \d+ open$/ })).toBeVisible();
    await closeSheet(page);
    // Settings: likewise; and Escape in the field drops what's typed.
    await openSettings(page);
    await sheet(page).getByRole('button', { name: 'New space' }).click();
    await sheet(page).getByRole('textbox', { name: 'Name of the new space' }).fill('Not this');
    await sheet(page).getByRole('textbox', { name: 'Name of the new space' }).press('Escape');
    await expect(sheet(page).getByRole('heading', { name: 'Settings' })).toBeVisible();
    await sheet(page).getByRole('button', { name: 'New space' }).click();
    await sheet(page).getByRole('textbox', { name: 'Name of the new space' }).fill('Errands');
    await closeSheet(page);
    await openSettings(page);
    await expect.poll(() => names(page)).toEqual(['Main', 'Work', 'Personal', 'Errands']);
  });

  test('on a narrow phone: the space name keeps its room in Notes, the tools one line, the box its hint', async ({ page }) => {
    test.skip(desktop(), 'phone header');
    await page.setViewportSize({ width: 360, height: 780 });
    await open(page);
    // Without spaces: Delete in the header as always, the box's text clipped as always.
    await toNotes(page);
    await expect(page.getByRole('button', { name: 'Delete this sheet' })).toBeVisible();
    await toTasks(page);
    await expect(composer(page)).toHaveCSS('text-overflow', 'clip');

    await makeSpaces(page, 'Development', 'BIM coordination');
    await show(page, 'Development');
    await toNotes(page);
    // Blank paper: no sheet to delete or move yet.
    await expect(page.locator('.sheet-delete, .sheet-more').filter({ visible: true })).toHaveCount(0);
    const titleFits = () => page.locator('.space-title-name').evaluate((el) => el.scrollWidth <= el.clientWidth);
    expect(await titleFits()).toBe(true);
    await page.getByRole('textbox', { name: 'Note' }).click();
    await page.getByRole('textbox', { name: 'Note' }).pressSequentially('Clash list');
    await expect(page.locator('.sheet-pill')).toHaveText(['Clash list']);
    await button(page, 'Done').click();
    // ⋯ alone beside Search and Settings, holding Move and Delete: the title keeps well over the
    // room ⋯, Delete and Settings left it (140 px), the buttons a little narrower on a narrow phone
    // (search.spec.ts). Measured as room, since whether a name fits depends on the font (Linux's
    // fallback is wider than Roboto or Segoe UI).
    await expect(page.locator('.sheet-delete')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'More for this sheet' })).toBeVisible();
    expect(await page.locator('header h1').evaluate((el) => el.getBoundingClientRect().width)).toBeGreaterThanOrEqual(172);
    await page.getByRole('button', { name: 'More for this sheet' }).click();
    await expect(page.getByRole('menuitem')).toHaveText(['Move sheet to…', 'Delete sheet']);
    await page.getByRole('menuitem', { name: 'Delete sheet' }).click();
    await expect(toast(page)).toContainText('Deleted “Clash list”');
    await expect(page.locator('.sheet-more').filter({ visible: true })).toHaveCount(0);

    // A task's five tools on one line, Focus and Delete by their names still.
    await toTasks(page);
    await addTask(page, 'Run the clash test');
    await openTexts(page).first().click();
    const tools = page.locator('.row-tools .tool');
    await expect(tools).toHaveCount(5);
    const tops = await tools.evaluateAll((els) => new Set(els.map((e) => Math.round(e.getBoundingClientRect().top))).size);
    expect(tops).toBe(1);
    await expect(button(page, 'Focus')).toBeVisible();
    await expect(button(page, 'Delete')).toBeVisible();
    await page.locator('textarea.edit').press('Escape');
    // The box's hint ends in an ellipsis, not mid-word.
    await show(page, 'BIM coordination');
    await expect(composer(page)).toHaveCSS('text-overflow', 'ellipsis');
  });

  test('reordering in a space keeps the other spaces where they are', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    await addTask(page, 'W2');
    await show(page, 'Main');
    await addTask(page, 'M1');
    await show(page, 'Work');
    await addTask(page, 'W1');
    // Edit tools: W1 down past W2 (with M1 between them in the one order).
    await openTexts(page).filter({ hasText: 'W1' }).click();
    await button(page, 'Move down').click();
    await page.locator('textarea.edit').press('Escape');
    await expect(openTexts(page)).toHaveText(['W2', 'W1']);
    await show(page, 'All');
    await expect(openTexts(page).filter({ hasText: /^(W1|W2|M1)$/ })).toHaveText(['M1', 'W2', 'W1']);
  });

  test('done and Clear go by space', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    await addTask(page, 'Finish me');
    await row(page, 'Finish me').getByRole('checkbox').click();
    await expect(page.locator('.done-toggle .count')).toHaveText('1', { timeout: 3000 });
    await show(page, 'Main');
    await expect(page.locator('section.done')).toBeHidden();
    await page.locator('.now-card').getByRole('checkbox').click();
    await expect(page.locator('.done-toggle .count')).toHaveText('1', { timeout: 3000 });
    await page.locator('.done-toggle').click();
    await expect(page.locator('.done-list .label')).toHaveText(['Tick this circle to finish your first task']);
    await button(page, 'Clear').click();
    await expect(toast(page)).toContainText('Cleared 1 done task');
    await show(page, 'All');
    await expect(page.locator('.done-list .label')).toHaveText(['Finish me']);
    await expect(page.locator('.done-list .row-space')).toHaveText(['Work']);
  });

  test('notes: each space its own sheets, blank paper until you write, and Move sheet to…', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    await toNotes(page);
    const editor = page.getByRole('textbox', { name: 'Note' });
    // Nothing yet: blank paper, and switching made no sheet.
    await expect(page.locator('.sheet-pill')).toHaveCount(0);
    await expect(editor).toHaveValue('');
    await editor.click();
    await editor.pressSequentially('Site visit');
    await expect(page.locator('.sheet-pill')).toHaveText(['Site visit']);
    // Make task: into the sheet's space.
    await button(page, 'Make task').click();
    await expect(toast(page)).toContainText('Site visit');
    if (!desktop()) await button(page, 'Done').click();
    await show(page, 'Main');
    await expect(page.locator('.sheet-pill')).toHaveText(['Scrap paper']);
    await toTasks(page);
    await expect(openTexts(page).filter({ hasText: 'Site visit' })).toHaveCount(0);
    await show(page, 'Work');
    await expect(openTexts(page)).toHaveText(['Site visit']);

    // Notes ⋯ → Move sheet to…
    await toNotes(page);
    await page.getByRole('button', { name: 'More for this sheet' }).click();
    await page.getByRole('menuitem', { name: 'Move sheet to…' }).click();
    await expect(sheet(page).getByRole('heading', { name: 'Move sheet to…' })).toBeVisible();
    await sheet(page).getByRole('button', { name: 'Main' }).click();
    await expect(toast(page)).toContainText('Moved to Main');
    await expect(page.locator('.sheet-pill')).toHaveCount(0);
    await button(page, 'Undo').click();
    await expect(page.locator('.sheet-pill')).toHaveText(['Site visit']);

    // Each space comes back on the sheet it showed last.
    await button(page, 'New sheet').click();
    await editor.pressSequentially('Second');
    await expect(page.locator('.sheet-pill')).toHaveText(['Site visit', 'Second']);
    await show(page, 'Main');
    await button(page, 'New sheet').click();
    await editor.pressSequentially('Main notes');
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText('Main notes');
    await show(page, 'Work');
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText('Second');
    await expect(page.locator('textarea.editor')).toHaveValue('Second');
  });

  test('Undo on a space\'s blank paper makes no sheet (it stays blank, as on the desktop)', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work', 'Personal');
    await show(page, 'Work');
    await toNotes(page);
    const editor = page.getByRole('textbox', { name: 'Note' });
    await editor.click();
    await editor.pressSequentially('Clash list');
    await expect(page.locator('.sheet-pill')).toHaveText(['Clash list']);
    if (!desktop()) await button(page, 'Done').click();
    await show(page, 'Personal');
    await expect(page.locator('.sheet-pill')).toHaveCount(0);
    await editor.click();
    await editor.press('Control+z');
    await expect(editor).toHaveValue('');
    await expect(page.locator('.sheet-pill')).toHaveCount(0);
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(title(page)).toHaveText('Personal');
    await toNotes(page);
    await expect(page.locator('.sheet-pill')).toHaveCount(0);
    // Writing there still makes its sheet.
    await editor.click();
    await editor.pressSequentially('Dentist');
    await expect(page.locator('.sheet-pill')).toHaveText(['Dentist']);
  });

  test('with Main alone the sheet strip stays where it always did; with spaces it keeps the sheet on show in sight', async ({ page }) => {
    await open(page);
    await toNotes(page);
    const editor = page.getByRole('textbox', { name: 'Note' });
    const strip = page.locator('.sheet-strip');
    const titles = ['Coordination meeting agenda', 'Clash detection follow-up', 'Site visit checklist north', 'Model audit for the client', 'Last sheet on the far right'];
    for (const text of titles) {
      await button(page, 'New sheet').click();
      await editor.pressSequentially(text);
      // Writing ends (on a phone the sheets come back into the dock in place of the tools).
      await editor.press('Escape');
    }
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText(titles[titles.length - 1]);
    // More sheets than fit: the strip scrolls.
    expect(await strip.evaluate((s) => s.scrollWidth > s.clientWidth)).toBe(true);
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await toNotes(page);
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText(titles[titles.length - 1]);
    await settled(page);
    // Main alone: as before spaces, the strip opens at its start.
    await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
    expect(await strip.evaluate((s) => s.scrollLeft)).toBe(0);

    // With spaces, the sheet on show is brought into sight.
    await makeSpaces(page, 'Work');
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await toNotes(page);
    await expect(page.locator('.sheet-pill[aria-selected="true"]')).toHaveText(titles[titles.length - 1]);
    await expect
      .poll(() =>
        page.evaluate(() => {
          const s = document.querySelector('.sheet-strip')!.getBoundingClientRect();
          const p = document.querySelector('.sheet-pill[aria-selected="true"]')!.getBoundingClientRect();
          return p.left >= s.left - 1 && p.right <= s.right + 1;
        }),
      )
      .toBe(true);
  });

  test('delete a space in Settings: its tasks show in Main, and Undo puts them back', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work', 'Dev');
    await show(page, 'Dev');
    await addTask(page, 'Fix the build');
    await openSettings(page);
    await sheet(page).getByRole('button', { name: 'Delete Dev' }).click();
    // The toast shows over Settings.
    await expect(sheet(page).locator('.toast:not([inert])')).toContainText('Deleted Dev');
    await expect(sheet(page).getByRole('textbox', { name: 'Name of Dev' })).toHaveCount(0);
    await closeSheet(page);
    // Dev was on show; it's gone, so Main shows, with Dev's task.
    await expect(title(page)).toHaveText('Main');
    await expect(openTexts(page).filter({ hasText: 'Fix the build' })).toHaveCount(1);
    // The toast carried on outside Settings: Undo.
    await toast(page).getByRole('button', { name: 'Undo' }).click();
    await expect(title(page)).toHaveText('Dev');
    await expect(openTexts(page)).toHaveText(['Fix the build']);
  });

  test('rename and reorder in Settings; the keys follow the order', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work', 'Dev');
    await openSettings(page);
    const main = sheet(page).getByRole('textbox', { name: 'Name of Main' });
    await main.fill('Inbox');
    await main.press('Enter');
    await expect(sheet(page).getByRole('textbox', { name: 'Name of Inbox' })).toHaveValue('Inbox');
    // Renaming moved nothing: Main, now Inbox, is still first.
    await expect.poll(() => names(page)).toEqual(['Inbox', 'Work', 'Dev']);
    await sheet(page).getByRole('button', { name: 'Move Dev up' }).click();
    // The focus stays on the moved space's button.
    await expect(sheet(page).getByRole('button', { name: 'Move Dev up' })).toBeFocused();
    await sheet(page).getByRole('button', { name: 'Move Dev up' }).click();
    await expect(sheet(page).getByRole('button', { name: 'Move Dev up' })).toBeDisabled();
    await expect(sheet(page).getByRole('button', { name: 'Move Dev down' })).toBeFocused();
    await expect.poll(() => names(page)).toEqual(['Dev', 'Inbox', 'Work']);
    // Main is renamed only: it can't be deleted, and it moves as the spaces around it do.
    await expect(sheet(page).getByRole('button', { name: /^(Delete|Move) Inbox/ })).toHaveCount(0);
    await closeSheet(page);
    await expect(title(page)).toHaveText('Inbox');
    await title(page).click();
    await expect(sheet(page).locator('.space-option-name')).toHaveText(['All', 'Dev', 'Inbox', 'Work', 'New space']);
    await closeSheet(page);
    // Called "Main" again, it's plain Main (no name of its own), where it was.
    await openSettings(page);
    await sheet(page).getByRole('textbox', { name: 'Name of Inbox' }).fill('Main');
    await sheet(page).getByRole('textbox', { name: 'Name of Inbox' }).press('Enter');
    await expect.poll(() => names(page)).toEqual(['Dev', 'Main', 'Work']);
    await expect(sheet(page).getByRole('textbox', { name: 'Name of Main' })).toHaveValue('Main');
    await closeSheet(page);
    await expect(title(page)).toHaveText('Main');
    if (desktop()) {
      await page.locator('body').press('Alt+1');
      await expect(title(page)).toHaveText('Dev');
      await page.locator('body').press('Alt+3');
      await expect(title(page)).toHaveText('Work');
      await page.locator('body').press('Alt+9'); // no ninth space: nothing
      await expect(title(page)).toHaveText('Work');
    }
  });

  test('Alt+0…9 switch even while typing, and the draft stays', async ({ page }) => {
    test.skip(!desktop(), 'hardware keyboard');
    await open(page);
    await makeSpaces(page, 'Work', 'Dev');
    await composer(page).click();
    await page.keyboard.type('Half a thought');
    await page.keyboard.press('Alt+2');
    await expect(title(page)).toHaveText('Work');
    await expect(composer(page)).toBeFocused();
    await expect(composer(page)).toHaveValue('Half a thought');
    await expect(composer(page)).toHaveAttribute('placeholder', 'Add to Work');
    await page.keyboard.press('Alt+0');
    await expect(title(page)).toHaveText('All');
    await page.keyboard.press('Alt+3');
    await expect(title(page)).toHaveText('Dev');
    await page.keyboard.press('Enter');
    await expect(openTexts(page)).toHaveText(['Half a thought']);
    // And from the notes.
    await page.getByRole('textbox', { name: 'Note' }).click();
    await page.keyboard.press('Alt+1');
    await expect(title(page)).toHaveText('Main');
  });

  test('the space on show and the spaces survive a reload', async ({ page }) => {
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    await addTask(page, 'Kept in Work');
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(title(page)).toHaveText('Work');
    await expect(openTexts(page)).toHaveText(['Kept in Work']);
    await show(page, 'All');
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(title(page)).toHaveText('All');
    await expect(row(page, 'Kept in Work').locator('.row-space')).toHaveText('Work');
  });

  // Only a crafted file or import makes this many: every one is kept and synced, and each list
  // draws a thousand at a time, so the screen stays usable.
  test('more spaces than a list draws: a thousand at a time, and More spaces for the rest', async ({ page }) => {
    await open(page);
    const at = `${Date.now().toString(36).padStart(9, '0')}.0000.aaaa1111`;
    const spaces = Array.from({ length: 1002 }, (_, i) => ({ id: `junk${String(i).padStart(4, '0')}`, name: `junk ${i}`, order: 'W', deleted: false, clk: { name: at, order: at, deleted: '000000000.0000.0' } }));
    await button(page, 'Settings').click();
    await page.locator('dialog input[type=file]').setInputFiles({
      name: 'tack.json',
      mimeType: 'application/json',
      buffer: Buffer.from(JSON.stringify({ version: 1, tasks: [], sheets: [], tackFocus: { tasks: [], sheets: [], spaces } })),
    });
    await expect(page.locator('dialog[open]')).toHaveCount(0);
    // Main and the 1,002: a thousand drawn, then More spaces.
    await title(page).click();
    const options = sheet(page).locator('.space-option[data-space]');
    await expect(options).toHaveCount(1 + 1000);
    const more = sheet(page).getByRole('button', { name: 'More spaces, 3 more' });
    await more.click();
    await expect(options).toHaveCount(1 + 1003);
    await expect(more).toHaveCount(0);
    await expect(sheet(page).locator('[data-space="junk0999"]')).toBeFocused();
    // The last one can be shown, and stays drawn the next time.
    await sheet(page).locator('[data-space="junk1001"]').click();
    await expect(title(page)).toHaveText('junk 1001');
    await title(page).click();
    await expect(options).toHaveCount(1 + 1001);
    await expect(sheet(page).locator('[data-space="junk1001"]')).toHaveAttribute('aria-current', 'true');
    await closeSheet(page);
    // Settings → Spaces likewise.
    await openSettings(page);
    await expect(sheet(page).locator('.space-row')).toHaveCount(1000);
    await sheet(page).getByRole('button', { name: 'More spaces, 3 more' }).click();
    await expect(sheet(page).locator('.space-row')).toHaveCount(1003);
    await expect(sheet(page).getByRole('textbox', { name: 'Name of junk 999' })).toBeFocused();
  });

  test('works with reduced motion', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await open(page);
    await makeSpaces(page, 'Work');
    await show(page, 'Work');
    await addTask(page, 'Calm');
    await expect(openTexts(page)).toHaveText(['Calm']);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`no automatically detectable accessibility issues with spaces (${theme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: theme, reducedMotion: 'reduce' });
      await open(page);
      await makeSpaces(page, 'Work', 'Personal');
      const scan = async (where: string) => {
        await page.waitForTimeout(300);
        const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
        expect(result.violations.map((v) => `${where}: ${v.id}: ${v.nodes.map((n) => n.target).join(', ')}`)).toEqual([]);
      };
      // A space with the "Now in Main" line, a task's tools, the box with its space.
      await show(page, 'Work');
      await addTask(page, 'Draft the report');
      await scan('Work');
      await composer(page).click();
      await composer(page).fill('Typing');
      await scan('typing');
      await composer(page).press('Escape');
      await openTexts(page).first().click();
      await scan('tools');
      await button(page, 'Move').click();
      await scan('Move to…');
      await closeSheet(page);
      // All, with each task's space.
      await show(page, 'All');
      await scan('All');
      await title(page).click();
      await scan('switcher');
      await closeSheet(page);
      // Notes and the ⋯ menu.
      await toNotes(page);
      await page.getByRole('button', { name: 'More for this sheet' }).click();
      await scan('notes menu');
      await page.keyboard.press('Escape');
      // Settings → Spaces, and a deleted space's toast over it.
      await openSettings(page);
      await sheet(page).getByRole('heading', { name: 'Spaces' }).scrollIntoViewIfNeeded();
      await scan('Settings');
      await sheet(page).getByRole('button', { name: 'Delete Personal' }).click();
      await scan('Settings, deleted');
    });
  }
});
