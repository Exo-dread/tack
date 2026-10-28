import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { button, flushed, open, settled } from './helpers';

/**
 * Markdown in Notes (docs/NOTES-MARKDOWN.md): formatted while you read, the plain editor while
 * you write, and the text always exactly what was typed.
 */

const desktop = () => test.info().project.name === 'desktop-chromium';
const phone = () => !desktop();

async function toNotes(page: Page) {
  if (phone()) await page.getByRole('tab', { name: 'Notes' }).click();
}

// (By its class: hidden while the note shows formatted, it's out of the accessibility tree.)
const editor = (page: Page) => page.locator('textarea.editor');
const view = (page: Page) => page.locator('.md');

/** A new sheet with this text, then writing ends: the note shows formatted. */
async function writeSheet(page: Page, text: string) {
  await button(page, 'New sheet').click();
  await expect(editor(page)).toBeFocused();
  await setText(page, text);
  await editor(page).press('Escape');
  await expect(editor(page)).not.toBeFocused();
}

/** The editor's whole text, as one input (typing 200 KB key by key would take minutes). */
async function setText(page: Page, text: string) {
  if (text.length < 5_000) {
    await editor(page).fill(text);
    return;
  }
  await editor(page).evaluate((el: HTMLTextAreaElement, text) => {
    el.value = text;
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }, text);
}

/** A click on a desktop, a tap on a phone. */
async function press(page: Page, target: ReturnType<Page['locator']>) {
  if (phone()) await target.tap();
  else await target.click();
}

const NOTE = [
  '# Site visit', // 0
  '', // 1
  'Met **Sam** at the *gate*.', // 2
  '- [ ] Bring the keys', // 3
  '- [x] Print drawings', // 4
  '  - A1 and `A2`', // 5
  '> Call before 9', // 6
  '1. Rebar', // 7
  '2. Ties', // 8
  '[plan](https://example.com/plan) and ~~old~~', // 9
].join('\n');

const lineStart = (text: string, ln: number) => text.split('\n').slice(0, ln).reduce((n, l) => n + l.length + 1, 0);

test.describe('notes in Markdown', () => {
  test('a note shows formatted on opening Notes, its text as typed', async ({ page }) => {
    await open(page);
    await toNotes(page);
    await writeSheet(page, NOTE);
    await flushed(page);
    await page.reload();
    await expect(page.locator('html.ready')).toHaveCount(1);
    await toNotes(page);
    // Opening Notes doesn't put the caret in the editor: the note is formatted.
    await expect(view(page)).toBeVisible();
    await expect(editor(page)).toBeHidden();
    await expect(editor(page)).toHaveValue(NOTE);
    const md = view(page);
    await expect(md.getByRole('heading', { name: 'Site visit', level: 2 })).toBeVisible();
    await expect(md.locator('strong')).toHaveText('Sam');
    await expect(md.locator('em')).toHaveText('gate');
    await expect(md.getByRole('checkbox', { name: 'Bring the keys' })).not.toBeChecked();
    await expect(md.getByRole('checkbox', { name: 'Print drawings' })).toBeChecked();
    await expect(md.locator('li.task.done > .li-text')).toHaveText('Print drawings');
    await expect(md.locator('li li')).toHaveText('A1 and A2');
    await expect(md.locator('code')).toHaveText('A2');
    await expect(md.locator('blockquote')).toHaveText('Call before 9');
    await expect(md.locator('ol > li')).toHaveText(['Rebar', 'Ties']);
    await expect(md.locator('s')).toHaveText('old');
    // A link is shown, never followed: no <a>, the url only as its tooltip.
    await expect(md.locator('.md-link')).toHaveText('plan');
    await expect(md.locator('.md-link')).toHaveAttribute('title', 'https://example.com/plan');
    await expect(md.locator('a')).toHaveCount(0);
    await expect(md.getByRole('list')).toHaveCount(3);
  });

  test('note text never becomes markup, a link or anything to run', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(e.message));
    page.on('dialog', (d) => {
      errors.push(`dialog: ${d.message()}`);
      void d.dismiss();
    });
    await open(page);
    await toNotes(page);
    const hostile = '# <img src=x onerror=alert(1)>\n<b>bold?</b> <script>alert(2)</script>\n- [ ] [click](javascript:alert(3))\n[x](" onmouseover="alert(4))';
    await writeSheet(page, hostile);
    const md = view(page);
    await expect(md).toBeVisible();
    await expect(md.locator('img, script, b, a, iframe, [onerror], [onmouseover], [href], [src]')).toHaveCount(0);
    await expect(md.getByRole('heading', { level: 2 })).toHaveText('<img src=x onerror=alert(1)>');
    await expect(md).toContainText('<b>bold?</b> <script>alert(2)</script>');
    await expect(md.locator('.md-link').first()).toHaveAttribute('title', 'javascript:alert(3');
    await md.locator('.md-link').first().hover();
    expect(errors).toEqual([]);
  });

  test('a click or tap opens the editor at the start of that line, keeping it where it was', async ({ page }) => {
    await open(page);
    await toNotes(page);
    const lines = Array.from({ length: 120 }, (_, i) => (i % 10 === 0 ? `## Part ${i / 10}` : `Line ${i}, **bold**`));
    const text = lines.join('\n');
    await writeSheet(page, text);
    const md = view(page);
    const target = md.locator('[data-ln="73"]');
    await target.evaluate((el) => el.scrollIntoView({ block: 'center' }));
    await settled(page);
    const before = (await target.boundingBox())!;
    await press(page, target);
    const ta = editor(page);
    await expect(ta).toBeFocused();
    await expect(md).toBeHidden();
    const caret = await ta.evaluate((el: HTMLTextAreaElement) => [el.selectionStart, el.selectionEnd]);
    expect(caret).toEqual([lineStart(text, 73), lineStart(text, 73)]);
    // The line is about where it was (the lines are short: none wraps in the editor).
    const after = await ta.evaluate((el: HTMLTextAreaElement) => {
      const style = getComputedStyle(el);
      const line = el.value.slice(0, el.selectionStart).split(String.fromCharCode(10)).length - 1;
      return el.getBoundingClientRect().top + parseFloat(style.paddingTop) + line * parseFloat(style.lineHeight) - el.scrollTop;
    });
    expect(Math.abs(after - before.y)).toBeLessThan(30);
    // Typing goes in at that line.
    await page.keyboard.type('>> ');
    await expect(ta).toHaveValue(text.slice(0, lineStart(text, 73)) + '>> ' + text.slice(lineStart(text, 73)));
  });

  test('a click below the last line puts the caret at the end; a drag that selects text does not open the editor', async ({ page }) => {
    test.skip(phone(), 'mouse');
    await open(page);
    await writeSheet(page, 'First line with words\nSecond');
    const md = view(page);
    const box = (await md.boundingBox())!;
    // Select a word by dragging across it.
    const first = (await md.locator('[data-ln="0"]').boundingBox())!;
    await page.mouse.move(first.x + 2, first.y + first.height / 2);
    await page.mouse.down();
    await page.mouse.move(first.x + 60, first.y + first.height / 2, { steps: 5 });
    await page.mouse.up();
    await expect(md).toBeVisible();
    await expect(editor(page)).toBeHidden();
    // Into the empty paper below.
    await page.mouse.click(box.x + box.width / 2, box.y + box.height - 20);
    await expect(editor(page)).toBeFocused();
    expect(await editor(page).evaluate((el: HTMLTextAreaElement) => el.selectionStart)).toBe('First line with words\nSecond'.length);
  });

  test('typing on the formatted note types into the editor; F2 and Enter open it', async ({ page }) => {
    test.skip(phone(), 'keyboard');
    await open(page);
    await writeSheet(page, '# Title\nbody');
    await view(page).focus();
    await page.keyboard.press('F2');
    await expect(editor(page)).toBeFocused();
    await expect(editor(page)).toHaveValue('# Title\nbody');
    // Escape: back to the formatted note, the keyboard still in it.
    await page.keyboard.press('Escape');
    await expect(view(page)).toBeFocused();
    await page.keyboard.type('!more');
    await expect(editor(page)).toBeFocused();
    // Where the caret was when writing ended (the end, here).
    await expect(editor(page)).toHaveValue('# Title\nbody!more');
    await page.keyboard.press('Escape');
    await page.keyboard.press('Enter');
    await expect(editor(page)).toBeFocused();
    await expect(editor(page)).toHaveValue('# Title\nbody!more');
  });

  test('from the formatted note, F5 stamps, Ctrl+Enter makes a task and Ctrl+V pastes, at the caret; on a checkbox, at the end of its line', async ({ page, context }) => {
    test.skip(phone(), 'keyboard');
    await open(page);
    await writeSheet(page, '# Plan\n- [ ] ring Sam\nlast');
    const value = () => editor(page).evaluate((el: HTMLTextAreaElement) => el.value);
    const caret = () => editor(page).evaluate((el: HTMLTextAreaElement) => [el.selectionStart, el.selectionEnd]);

    // F5: the editor, the date and time at the caret (the end: nothing was written here yet), and no reload.
    await page.evaluate(() => ((window as unknown as { notReloaded: boolean }).notReloaded = true));
    await view(page).focus();
    await page.keyboard.press('F5');
    await expect(editor(page)).toBeFocused();
    expect((await value()).startsWith('# Plan\n- [ ] ring Sam\nlast')).toBe(true);
    expect((await value()).length).toBeGreaterThan('# Plan\n- [ ] ring Sam\nlast'.length + 4);
    expect(await page.evaluate(() => (window as unknown as { notReloaded?: boolean }).notReloaded)).toBe(true);
    await setText(page, '# Plan\n- [ ] ring Sam\nlast');
    await editor(page).evaluate((el: HTMLTextAreaElement) => el.setSelectionRange(el.value.length, el.value.length));
    await page.keyboard.press('Escape');
    await expect(view(page)).toBeFocused();

    // Ctrl+Enter: the caret's line becomes a task; the note keeps its text.
    await page.keyboard.press('Control+Enter');
    await expect(editor(page)).toBeFocused();
    await expect(page.getByText('Added to Tasks: “last”', { exact: true })).toBeVisible();
    await expect(editor(page)).toHaveValue('# Plan\n- [ ] ring Sam\nlast');
    await page.keyboard.press('Escape');

    // A key on a checkbox: typed at the end of its item's line. Enter there types nothing.
    const box = view(page).getByRole('checkbox', { name: 'ring Sam' });
    await box.focus();
    await page.keyboard.type('!');
    await expect(editor(page)).toHaveValue('# Plan\n- [ ] ring Sam!\nlast');
    expect(await caret()).toEqual(['# Plan\n- [ ] ring Sam!'.length, '# Plan\n- [ ] ring Sam!'.length]);
    await page.keyboard.press('Escape');
    await box.focus();
    await page.keyboard.press('Enter');
    await expect(editor(page)).toBeFocused();
    await expect(editor(page)).toHaveValue('# Plan\n- [ ] ring Sam!\nlast');
    expect(await caret()).toEqual(['# Plan\n- [ ] ring Sam!'.length, '# Plan\n- [ ] ring Sam!'.length]);
    await page.keyboard.press('Escape');

    // Ctrl+V: pasted at the caret (where writing last ended: the end of that line).
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.evaluate(() => navigator.clipboard.writeText(' (pasted)'));
    await view(page).focus();
    await page.keyboard.press('Control+V');
    await expect(editor(page)).toBeFocused();
    await expect(editor(page)).toHaveValue('# Plan\n- [ ] ring Sam! (pasted)\nlast');
  });

  test('keys on the formatted note write at the caret, never over a selection search left', async ({ page }) => {
    test.skip(phone(), 'keyboard');
    await open(page);
    await writeSheet(page, '# Agenda\n- [ ] ducts vs beams\n- [ ] walls');
    await page.keyboard.press('Control+f');
    await page.getByRole('combobox', { name: 'Search tasks and notes' }).fill('beams');
    await page.getByRole('listbox', { name: 'Results' }).getByRole('option').first().click();
    await expect(editor(page)).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(view(page)).toBeFocused();
    await page.keyboard.type('!');
    await expect(editor(page)).toBeFocused();
    const value = await editor(page).evaluate((el: HTMLTextAreaElement) => el.value);
    expect(value.replace('!', '')).toBe('# Agenda\n- [ ] ducts vs beams\n- [ ] walls');
    expect(value).toContain('beams');
  });

  test('ticking a box changes exactly that box, as one edit: Undo puts it back, and it syncs like typing', async ({ page, context }) => {
    await open(page);
    await toNotes(page);
    await writeSheet(page, 'Today\n- [ ] Call Sam\n- [X] Order ties\n\t- [ ] nested');
    const other = await context.newPage();
    await open(other);
    await toNotes(other);
    await other.locator('.sheet-pill', { hasText: 'Today' }).click();

    const box = view(page).getByRole('checkbox', { name: 'Call Sam' });
    await press(page, box);
    await expect(box).toBeChecked();
    await expect(editor(page)).toHaveValue('Today\n- [x] Call Sam\n- [X] Order ties\n\t- [ ] nested');
    // Still reading, the box still has the keyboard.
    await expect(view(page)).toBeVisible();
    await expect(view(page).locator('li.task').first()).toHaveClass(/done/);
    // The other tab gets it, as it would any edit.
    await expect(view(other).getByRole('checkbox', { name: 'Call Sam' })).toBeChecked();
    // Unticking an [X] writes [ ].
    await press(page, view(page).getByRole('checkbox', { name: 'Order ties' }));
    await expect(editor(page)).toHaveValue('Today\n- [x] Call Sam\n- [ ] Order ties\n\t- [ ] nested');
    if (desktop()) {
      // Ctrl+Z: the last tick, then the one before.
      await page.keyboard.press('Control+z');
      await expect(editor(page)).toHaveValue('Today\n- [x] Call Sam\n- [X] Order ties\n\t- [ ] nested');
      await expect(view(page).getByRole('checkbox', { name: 'Order ties' })).toBeChecked();
      await expect(view(page).getByRole('checkbox', { name: 'Order ties' })).toBeFocused();
      await page.keyboard.press('Control+z');
      await expect(editor(page)).toHaveValue('Today\n- [ ] Call Sam\n- [X] Order ties\n\t- [ ] nested');
      await expect(view(other).getByRole('checkbox', { name: 'Call Sam' })).not.toBeChecked();
      // Space on a box ticks it.
      await view(page).getByRole('checkbox', { name: 'nested' }).focus();
      await page.keyboard.press(' ');
      await expect(editor(page)).toHaveValue('Today\n- [ ] Call Sam\n- [X] Order ties\n\t- [x] nested');
    }
    await flushed(page);
    await page.reload();
    await toNotes(page);
    await expect(editor(page)).toHaveValue(desktop() ? 'Today\n- [ ] Call Sam\n- [X] Order ties\n\t- [x] nested' : 'Today\n- [x] Call Sam\n- [ ] Order ties\n\t- [ ] nested');
  });

  test('switching sheet shows it formatted; New sheet and blank paper are the editor', async ({ page }) => {
    await open(page);
    await toNotes(page);
    await writeSheet(page, '# One');
    await button(page, 'New sheet').click();
    // A new sheet is empty: the editor, with the keyboard.
    await expect(editor(page)).toBeFocused();
    await editor(page).fill('# Two');
    if (phone()) await button(page, 'Done').click();
    await page.locator('.sheet-pill', { hasText: 'One' }).click();
    await expect(view(page).getByRole('heading', { name: 'One' })).toBeVisible();
    await expect(editor(page)).toBeHidden();
    await page.locator('.sheet-pill', { hasText: 'Two' }).click();
    await expect(view(page).getByRole('heading', { name: 'Two' })).toBeVisible();
  });

  test('a note over 200,000 characters stays the editor', async ({ page }) => {
    await open(page);
    await toNotes(page);
    const long = `# Big\n${'- [ ] item **bold**\n'.repeat(10_600)}`;
    expect(long.length).toBeGreaterThan(200_000);
    await writeSheet(page, long);
    await expect(editor(page)).toBeVisible();
    await expect(view(page)).toBeHidden();
    // Down to 200,000: formatted again. (Laying out 200 KB in emulated WebKit takes seconds on a
    // busy test machine: the boundary is checked in desktop Chrome.)
    if (phone()) return;
    await press(page, editor(page));
    await setText(page, long.slice(0, 200_000));
    await editor(page).press('Escape');
    await expect(view(page)).toBeVisible();
  });

  test('Format notes off: always the editor, as in 0.5.0, on this device only', async ({ page }) => {
    await open(page);
    await toNotes(page);
    await writeSheet(page, '# Heading\n- [ ] box');
    await expect(view(page)).toBeVisible();
    await button(page, 'Settings').click();
    const toggle = page.getByRole('switch', { name: 'Format notes' });
    await expect(toggle).toHaveAttribute('aria-checked', 'true');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-checked', 'false');
    await page.keyboard.press('Escape');
    await expect(page.locator('dialog[open]')).toHaveCount(0);
    await expect(view(page)).toBeHidden();
    await expect(editor(page)).toBeVisible();
    await expect(editor(page)).toHaveValue('# Heading\n- [ ] box');
    await flushed(page);
    await page.reload();
    await toNotes(page);
    await expect(editor(page)).toBeVisible();
    await expect(view(page)).toBeHidden();
    // Writing and leaving: still the editor.
    await press(page, editor(page));
    await editor(page).press('End');
    await editor(page).press('Escape');
    await expect(editor(page)).toBeVisible();
    // Never in an export: Settings are this device's own.
    expect(await page.evaluate(() => JSON.parse(localStorage.getItem('tack.prefs') ?? '{}').formatNotes)).toBe(false);
  });

  test('search opens the editor with the match selected', async ({ page }) => {
    await open(page);
    await toNotes(page);
    await writeSheet(page, '# Agenda\n- [ ] ducts vs beams\n- [ ] walls');
    await expect(view(page)).toBeVisible();
    if (desktop()) await page.keyboard.press('Control+f');
    else await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.getByRole('combobox', { name: 'Search tasks and notes' }).fill('beams');
    await page.getByRole('listbox', { name: 'Results' }).getByRole('option').first().click();
    await expect(editor(page)).toBeFocused();
    const selected = await editor(page).evaluate((el: HTMLTextAreaElement) => el.value.slice(el.selectionStart, el.selectionEnd));
    expect(selected).toBe('beams');
  });

  test('render time for a 20 KB and a 200 KB note', async ({ page }) => {
    test.skip(!desktop(), 'measured in desktop Chrome');
    await open(page);
    const unit = '## Section\nSome text with **bold**, *italic*, `code` and a [link](https://example.com).\n- [ ] a task to do\n- [x] one done\n  - nested item\n> a quote\n\n';
    const make = (size: number) => unit.repeat(Math.ceil(size / unit.length)).slice(0, size);
    await writeSheet(page, make(20_000));
    await writeSheet(page, make(200_000));
    await writeSheet(page, 'small');
    // Two sheets start "## Section": tell them apart by length.
    const sizes = await page.evaluate(() => [...document.querySelectorAll<HTMLElement>('.sheet-pill')].map((p) => p.textContent));
    expect(sizes.filter((s) => s === 'Section')).toHaveLength(2);
    // From the click on its tab to the note laid out: the switch, parsing, building and the
    // layout of every chunk in sight (forced here: the browser would do it before the next paint).
    // And, for the record, to the next frame's end (which includes waiting for the frame).
    const timeNth = (n: number) =>
      page.evaluate(async (n) => {
        const pills = [...document.querySelectorAll<HTMLElement>('.sheet-pill')];
        const target = pills.filter((p) => p.textContent === 'Section')[n]!;
        const away = pills.find((p) => p.textContent === 'small')!;
        const work: number[] = [];
        const frame: number[] = [];
        for (let i = 0; i < 9; i++) {
          away.click();
          await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 50)));
          const t = performance.now();
          target.click();
          const md = document.querySelector<HTMLElement>('.md')!;
          const bottom = md.getBoundingClientRect().bottom;
          for (const chunk of md.children) {
            if (chunk.getBoundingClientRect().top > bottom) break;
            void chunk.lastElementChild?.getBoundingClientRect();
          }
          work.push(performance.now() - t);
          await new Promise<void>((r) => requestAnimationFrame(() => setTimeout(r, 0)));
          frame.push(performance.now() - t);
        }
        const median = (xs: number[]) => xs.sort((a, b) => a - b)[Math.floor(xs.length / 2)]!;
        return { work: median(work), frame: median(frame) };
      }, n);
    const kb20 = await timeNth(0);
    const kb200 = await timeNth(1);
    const line = `20 KB: ${kb20.work.toFixed(1)} ms (${kb20.frame.toFixed(1)} ms to the next frame), 200 KB: ${kb200.work.toFixed(1)} ms (${kb200.frame.toFixed(1)} ms), median of 9`;
    test.info().annotations.push({ type: 'render', description: line });
    console.log(`[render] ${line}`);
    // Budgets (docs/NOTES-MARKDOWN.md): 8 ms and 150 ms in Chrome; slack here for a busy machine.
    expect(kb20.work).toBeLessThan(8 * 3);
    expect(kb200.work).toBeLessThan(150 * 1.5);
  });

  test('screenshots for review', async ({ page }) => {
    const dir = process.env.MD_SHOTS;
    test.skip(!dir || test.info().project.name === 'iphone-webkit', 'only when MD_SHOTS names a folder');
    mkdirSync(dir!, { recursive: true });
    for (const scheme of ['light', 'dark'] as const) {
      await page.emulateMedia({ colorScheme: scheme, reducedMotion: 'reduce' });
      await open(page);
      await toNotes(page);
      await writeSheet(
        page,
        '# Site visit\n\nMet **Sam** at the *gate*. Bring the `A1` sheets.\n\n## Checklist\n- [x] Keys\n- [ ] Print drawings\n  - A1 and A2\n  - [ ] Rebar schedule\n- [ ] Call before 9\n\n> The north gate is locked after 5.\n> - use the side door\n\n---\n1. Rebar\n2. Ties\n3. ~~Concrete~~ next week\n\n```\nlength = 12.5 m\n```\nSee the [plan](https://example.com/plan).',
      );
      await settled(page);
      await page.screenshot({ path: join(dir!, `${test.info().project.name}-${scheme}.png`) });
      await page.reload();
    }
  });
});
