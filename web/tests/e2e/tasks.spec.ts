import { expect, test } from '@playwright/test';
import { addTask, button, composer, flushed, isTouch, nowText, open, openTexts, settled, touchDrag } from './helpers';

test.describe('tasks', () => {
  test('first run teaches by doing', async ({ page }) => {
    await open(page);
    await expect(nowText(page)).toHaveText('Tick this circle to finish your first task');
    expect(await openTexts(page).count()).toBeGreaterThanOrEqual(3);
    await expect(page.locator('.orb-header')).toHaveAttribute('aria-label', /open/);
  });

  test('captures a thought in seconds, and keeps it', async ({ page }) => {
    await open(page);
    await addTask(page, 'Call the plumber tomorrow');
    await expect(page.locator('.subtitle')).toContainText('open');
    // A half-typed thought survives closing the app too.
    await composer(page).fill('Half a thought');
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page).first()).toHaveText('Call the plumber tomorrow');
    await expect(composer(page)).toHaveValue('Half a thought');
  });

  test('return on an empty box puts the keyboard away', async ({ page }) => {
    await open(page);
    await composer(page).click();
    await composer(page).press('Enter');
    await expect(composer(page)).not.toBeFocused();
  });

  test('Ctrl+Enter adds a task straight into focus', async ({ page }) => {
    await open(page);
    await composer(page).fill('Write the drainage report');
    await composer(page).press('Control+Enter');
    await expect(nowText(page)).toHaveText('Write the drainage report');
    // The previous focus task went back to the top of the list.
    await expect(openTexts(page).first()).toHaveText('Tick this circle to finish your first task');
  });

  test('pasting a list adds one task per line, with undo', async ({ page }) => {
    await open(page);
    const before = await openTexts(page).count();
    await composer(page).click();
    await page.evaluate(() => {
      const dt = new DataTransfer();
      dt.setData('text/plain', '- milk\n2. eggs\n[ ] bread\n');
      document.querySelector('.composer-input')!.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
    });
    await expect(openTexts(page).nth(0)).toHaveText('milk');
    await expect(openTexts(page).nth(1)).toHaveText('eggs');
    await expect(openTexts(page).nth(2)).toHaveText('bread');
    await button(page, 'Undo').click();
    await expect(openTexts(page)).toHaveCount(before);
  });

  test('ticking: grace period, then Done; progress moves', async ({ page }) => {
    await open(page);
    const card = page.locator('.now-card');
    await card.getByRole('checkbox').click();
    await expect(card.getByRole('checkbox')).toHaveAttribute('aria-checked', 'true');
    // A second tap within the grace period changes your mind.
    await card.getByRole('checkbox').click();
    await page.waitForTimeout(1100);
    await expect(nowText(page)).toHaveCount(1);
    // Tick for real.
    await card.getByRole('checkbox').click();
    await expect(page.locator('.now-card')).toHaveCount(0, { timeout: 3000 });
    await expect(page.locator('.done-toggle .count')).toHaveText('1');
    await expect(page.locator('.orb-header')).toHaveAttribute('aria-label', /1 done today/);
    await page.locator('.done-toggle').click();
    await expect(page.locator('.done-list .label')).toHaveText(['Tick this circle to finish your first task']);
    // Reopen from Done.
    await page.locator('.done-list .check').click();
    await expect(openTexts(page).first()).toHaveText('Tick this circle to finish your first task');
  });

  test('a tick survives closing the app at once', async ({ page }) => {
    await open(page);
    await addTask(page, 'Quick one');
    await page.locator('.open-list li').first().getByRole('checkbox').click();
    // The app is hidden or closed before the grace period ends.
    await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide')));
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(openTexts(page).filter({ hasText: 'Quick one' })).toHaveCount(0);
    await expect(page.locator('.done-toggle .count')).toHaveText('1');
  });

  test('edit in place; clearing the text deletes with undo', async ({ page }) => {
    await open(page);
    await addTask(page, 'Book the crane');
    await openTexts(page).first().click();
    const edit = page.locator('textarea.edit');
    await expect(edit).toBeFocused();
    await edit.fill('Book the crane for Thursday');
    await edit.press('Enter');
    await expect(openTexts(page).first()).toHaveText('Book the crane for Thursday');
    await openTexts(page).first().click();
    await page.locator('textarea.edit').fill('');
    await page.locator('textarea.edit').press('Enter');
    await expect(openTexts(page).filter({ hasText: 'Book the crane' })).toHaveCount(0);
    await button(page, 'Undo').click();
    await expect(openTexts(page).first()).toHaveText('Book the crane for Thursday');
  });

  test('edit tools: focus, move and delete without gestures', async ({ page }) => {
    await open(page);
    await addTask(page, 'Second');
    await addTask(page, 'First');
    await openTexts(page).first().click();
    await button(page, 'Move down').click();
    // While editing, the row holds a text box instead of its label.
    const rows = page.locator('.open-list > li.row:not(.exiting)');
    await expect(rows.nth(0).locator('.label')).toHaveText('Second');
    await expect(rows.nth(1).locator('textarea.edit')).toHaveValue('First');
    await expect(page.locator('textarea.edit')).toBeFocused();
    await page.locator('.row-tools').getByRole('button', { name: 'Focus' }).click();
    await expect(nowText(page)).toHaveText('First');
  });

  test('swipes: right to focus, left to delete (with undo)', async ({ page }) => {
    test.skip(test.info().project.name === 'desktop-chromium', 'touch gestures');
    await open(page);
    await addTask(page, 'Swipe me right');
    const row = page.locator('.open-list li').first();
    await touchDrag(page, row, { dx: 220, dy: 0 });
    await expect(nowText(page)).toHaveText('Swipe me right');

    await addTask(page, 'Swipe me left');
    await touchDrag(page, page.locator('.open-list li').first(), { dx: -240, dy: 0 });
    await expect(openTexts(page).filter({ hasText: 'Swipe me left' })).toHaveCount(0);
    await expect(page.locator('.toast')).toContainText('Deleted');
    await button(page, 'Undo').click();
    await expect(openTexts(page).first()).toHaveText('Swipe me left');

    // A short swipe springs back and changes nothing.
    await touchDrag(page, page.locator('.open-list li').first(), { dx: -30, dy: 0 }, { stepMs: 40 });
    await page.waitForTimeout(600);
    await expect(openTexts(page).first()).toHaveText('Swipe me left');
  });

  test('hold and drag to reorder', async ({ page }) => {
    await open(page);
    await addTask(page, 'C');
    await addTask(page, 'B');
    await addTask(page, 'A');
    const rows = page.locator('.open-list li');
    const h = (await rows.first().boundingBox())!.height;
    if (await isTouch(page)) {
      await touchDrag(page, rows.first(), { dx: 0, dy: h * 2.2 }, { holdMs: 480, steps: 16, stepMs: 20 });
    } else {
      const box = (await rows.first().boundingBox())!;
      await page.mouse.move(box.x + box.width / 2, box.y + 20);
      await page.mouse.down();
      for (let i = 1; i <= 12; i++) await page.mouse.move(box.x + box.width / 2, box.y + 20 + (h * 2.2 * i) / 12);
      await page.mouse.up();
    }
    await expect(openTexts(page).nth(0)).toHaveText('B');
    await expect(openTexts(page).nth(1)).toHaveText('C');
    await expect(openTexts(page).nth(2)).toHaveText('A');
    // The new order is saved.
    await flushed(page);
    await page.reload();
    await expect(openTexts(page).nth(2)).toHaveText('A');
  });

  test('keyboard: N to add, arrows to move between and reorder tasks, Delete and Ctrl+Z', async ({ page }) => {
    test.skip(test.info().project.name !== 'desktop-chromium', 'hardware keyboard');
    await open(page);
    await page.locator('body').press('n');
    await expect(composer(page)).toBeFocused();
    await page.keyboard.type('Two');
    await page.keyboard.press('Enter');
    await page.keyboard.type('One');
    await page.keyboard.press('Enter');
    await page.keyboard.press('Escape');
    await openTexts(page).first().locator('..').focus();
    await page.keyboard.press('Alt+ArrowDown');
    await expect(openTexts(page).nth(1)).toHaveText('One');
    await page.keyboard.press('ArrowUp');
    await expect(page.locator('.open-list li').nth(0).locator('.text')).toBeFocused();
    await page.keyboard.press('Delete');
    await expect(openTexts(page).first()).toHaveText('One');
    await page.locator('body').press('Control+z');
    await expect(openTexts(page).first()).toHaveText('Two');
  });

  test('suggests Add to Home Screen once, on a later day (iPhone)', async ({ page }) => {
    test.skip(test.info().project.name !== 'iphone-webkit', 'iOS Safari suggestion');
    await open(page);
    await expect(page.locator('.install-card')).toHaveCount(0); // never on the first day
    await page.evaluate(() => {
      const p = JSON.parse(localStorage.getItem('tack.prefs') ?? '{}');
      localStorage.setItem('tack.prefs', JSON.stringify({ ...p, firstDay: '2000-01-01' }));
    });
    await page.reload();
    await expect(page.locator('.install-card')).toContainText('Add to Home Screen');
    await button(page, 'Not now').click();
    await expect(page.locator('.install-card')).toHaveCount(0);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await expect(page.locator('.install-card')).toHaveCount(0);
  });

  test('stays quick with a long list', async ({ page }) => {
    await open(page);
    const before = await page.locator('.open-list > li').count();
    await composer(page).click();
    const pasteMs = await page.evaluate(() => {
      const dt = new DataTransfer();
      dt.setData('text/plain', Array.from({ length: 500 }, (_, i) => `Task ${i + 1}`).join('\n'));
      const t0 = performance.now();
      document.querySelector('.composer-input')!.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
      return performance.now() - t0;
    });
    await expect(page.locator('.open-list > li')).toHaveCount(before + 500);
    // Generous budgets: these catch an accidentally quadratic render, not small slowdowns.
    expect(pasteMs).toBeLessThan(1500);
    const addMs = await page.evaluate(() => {
      const input = document.querySelector<HTMLInputElement>('.composer-input')!;
      input.value = 'One more';
      const t0 = performance.now();
      input.form!.requestSubmit();
      return performance.now() - t0;
    });
    expect(addMs).toBeLessThan(250);
  });

  test('works with reduced motion', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await open(page);
    await addTask(page, 'Calm');
    await page.locator('.open-list li').first().getByRole('checkbox').click();
    await expect(openTexts(page).filter({ hasText: 'Calm' })).toHaveCount(0, { timeout: 3000 });
  });
});
