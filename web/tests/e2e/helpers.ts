import { expect, type Locator, type Page } from '@playwright/test';

export async function open(page: Page, path = '/'): Promise<void> {
  await page.goto(path);
  await expect(page.locator('html.ready')).toHaveCount(1);
}

export const isTouch = (page: Page): Promise<boolean> => page.evaluate(() => matchMedia('(pointer: coarse)').matches);

export const openTexts = (page: Page) => page.locator('.open-list > li.row:not(.exiting) .label');
export const nowText = (page: Page) => page.locator('.now-card:not(.exiting) .label');
export const composer = (page: Page) => page.getByRole('textbox', { name: 'Add a task' });

/** Waits for every animation on the page to finish (rows land before we touch them). */
export async function settled(page: Page): Promise<void> {
  await page.evaluate(() => Promise.all(document.getAnimations().map((a) => a.finished.catch(() => {}))));
}

/** Adds a task through the composer, as a person would. */
export async function addTask(page: Page, text: string): Promise<void> {
  const input = composer(page);
  await input.click();
  await input.fill(text);
  await input.press('Enter');
  await expect(openTexts(page).first()).toHaveText(text);
  await settled(page);
}

export const button = (page: Page, name: string) => page.getByRole('button', { name, exact: true });

/** Every write is in IndexedDB before the page goes away (the app flushes on hide). */
export async function flushed(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>((r) => setTimeout(r, 450)));
}

/**
 * A touch gesture made of pointer events (Playwright's touchscreen only taps). Moves are spread
 * over time so velocity is realistic.
 */
export async function touchDrag(
  page: Page,
  target: Locator,
  path: { dx: number; dy: number },
  options: { holdMs?: number; steps?: number; stepMs?: number } = {},
): Promise<void> {
  const box = await target.boundingBox();
  if (!box) throw new Error('target not visible');
  const x = box.x + box.width / 2;
  const y = box.y + Math.min(box.height / 2, 26);
  await page.evaluate(
    async ({ x, y, dx, dy, holdMs, steps, stepMs }) => {
      const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
      const init = (cx: number, cy: number): PointerEventInit => ({
        bubbles: true,
        cancelable: true,
        composed: true,
        pointerId: 11,
        pointerType: 'touch',
        isPrimary: true,
        clientX: cx,
        clientY: cy,
        button: 0,
        buttons: 1,
      });
      const el = document.elementFromPoint(x, y)!;
      el.dispatchEvent(new PointerEvent('pointerdown', init(x, y)));
      if (holdMs) await wait(holdMs);
      for (let i = 1; i <= steps; i++) {
        el.dispatchEvent(new PointerEvent('pointermove', init(x + (dx * i) / steps, y + (dy * i) / steps)));
        await wait(stepMs);
      }
      el.dispatchEvent(new PointerEvent('pointerup', { ...init(x + dx, y + dy), buttons: 0 }));
    },
    { x, y, dx: path.dx, dy: path.dy, holdMs: options.holdMs ?? 0, steps: options.steps ?? 12, stepMs: options.stepMs ?? 16 },
  );
}
