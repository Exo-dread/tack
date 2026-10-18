import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end tests run against the production build (service worker, CSP and all), on an
 * emulated iPhone in WebKit (Safari's engine), an emulated Android phone in Chromium, and a
 * desktop browser. Emulation is not a real device: see TESTING.md for what to check by hand.
 */
export default defineConfig({
  testDir: 'tests/e2e',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: true,
  // GitHub's shared runners are slow, and WebKit especially so with several browsers at once:
  // fewer at a time there, and a retry for timing hiccups (a real bug fails every attempt).
  workers: process.env.CI ? 2 : 4,
  retries: process.env.CI ? 2 : 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:4173',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'npx vite build && npx vite preview --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
  projects: [
    { name: 'iphone-webkit', use: { ...devices['iPhone 15'] } },
    { name: 'android-chromium', use: { ...devices['Pixel 7'] } },
    { name: 'desktop-chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 820 } } },
  ],
});
