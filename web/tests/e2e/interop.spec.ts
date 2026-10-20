import { expect, test, type BrowserContext } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { FakeMicrosoft } from '../fakes/microsoft';
import { addTask, open, openTexts } from './helpers';

/**
 * Tack Focus in a browser and Tack for Windows' own sync code, through the same folder: what the
 * browser uploads to (fake) OneDrive is put on "the PC", the PC's sync (tests/Tack.Tests --interop,
 * the app's engine and folder provider) merges it and adds a task, and its file goes back up. On
 * Windows only (Tack for Windows is WPF), after `dotnet build tests/Tack.Tests -c Release`.
 */

const RUNNER = join(import.meta.dirname, '../../../tests/Tack.Tests/bin/Release/net8.0-windows/Tack.Tests.exe');
const MICROSOFT = /^https:\/\/(login\.microsoftonline\.com|graph\.microsoft\.com|dl\.files\.1drv\.com)\//;

test.use({ serviceWorkers: 'block' });

async function useMicrosoft(context: BrowserContext, ms: FakeMicrosoft): Promise<void> {
  await context.route(MICROSOFT, async (route) => {
    const req = route.request();
    const result = ms.handle({ method: req.method(), url: req.url(), headers: await req.allHeaders(), body: req.postData() }, false);
    if (result === 'abort') return route.abort('connectionfailed');
    if (result.status === 302 && req.isNavigationRequest() && test.info().project.name.includes('webkit')) {
      return route.fulfill({ status: 200, contentType: 'text/html', body: `<!doctype html><script>location.replace(${JSON.stringify(result.headers.location)})</script>` });
    }
    await route.fulfill({ status: result.status, headers: result.headers, body: result.body });
  });
}

test('Tack Focus and Tack for Windows exchange tasks through the same folder', async ({ page, context }) => {
  test.skip(process.platform !== 'win32' || !existsSync(RUNNER), 'needs Windows and the built C# test runner');
  test.skip(test.info().project.name !== 'desktop-chromium', 'once is enough');
  const ms = new FakeMicrosoft();
  await useMicrosoft(context, ms);
  await open(page);
  await addTask(page, 'From the phone');
  const settings = page.locator('dialog.sheet');
  await page.getByRole('button', { name: /^Settings/ }).click();
  await settings.getByRole('button', { name: 'Turn on sync' }).click();
  await expect(page.locator('.toast-text')).toHaveText(/Sync is on/);
  await page.getByRole('button', { name: /^Settings/ }).click();
  const syncNow = async () => {
    const status = settings.locator('.sync-status');
    const before = (await status.getAttribute('data-synced')) ?? '0';
    await settings.getByRole('button', { name: 'Sync now' }).click();
    await expect(status).not.toHaveAttribute('data-synced', before, { timeout: 10_000 });
    await expect(status).toHaveText(/Up to date/, { timeout: 10_000 });
  };
  await syncNow();

  // OneDrive brings the phone's file to the PC; the PC syncs and adds a task.
  const root = mkdtempSync(join(tmpdir(), 'tack-interop-'));
  const folder = join(root, 'Tack Focus');
  mkdirSync(join(folder, 'devices'), { recursive: true });
  const toPc = () => ms.deviceFiles().forEach((text, name) => writeFileSync(join(folder, 'devices', name), text));
  const pc = (task: string) => execFileSync(RUNNER, ['--interop', folder, task], { encoding: 'utf8' }).split(/\r?\n/);
  toPc();
  const shown = pc('From the PC');
  expect(shown).toContain('open: From the phone');
  expect(shown).toContain('open: From the PC');

  // And back up to OneDrive, to the phone.
  ms.putDeviceFile('pcINTEROP.json', readFileSync(join(folder, 'devices', 'pcINTEROP.json'), 'utf8'));
  await syncNow();
  await expect(settings).toContainText('Tack for Windows on INTEROP-PC');
  await settings.getByRole('button', { name: 'Done' }).click();
  await expect(openTexts(page).filter({ hasText: 'From the PC' })).toHaveCount(1);

  // Finished on the phone: the PC sees it finished.
  await openTexts(page).filter({ hasText: 'From the PC' }).locator('xpath=ancestor::li').getByRole('checkbox').click();
  await page.waitForTimeout(1200);
  await page.getByRole('button', { name: /^Settings/ }).click();
  await syncNow();
  toPc();
  const after = pc('Second from the PC');
  expect(after).not.toContain('open: From the PC');
  expect(after).toContain('open: Second from the PC');
  expect(after).toContain('open: From the phone');
});
