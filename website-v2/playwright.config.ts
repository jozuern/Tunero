// Browser tests of the demo desktop (e2e/), run against the built site: npm run build, then npm run test:e2e. They cover
// what node --test cannot reach (focus, windows, timers, the wallpaper) and what broke once and was fixed by hand.
// Playwright brings its own Chromium (npx playwright install chromium); CHROMIUM_PATH names another one instead.
import { defineConfig, devices } from '@playwright/test';

const port = 4321;

export default defineConfig({
  testDir: 'e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  // A failure is a finding, not a flake to retry away.
  retries: 0,
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
        // WebGL through SwiftShader, so the wallpaper's shader runs without a graphics card.
        launchOptions: { executablePath: process.env.CHROMIUM_PATH || undefined, args: ['--enable-unsafe-swiftshader'] },
      },
    },
  ],
  webServer: {
    command: `npx astro preview --port ${port}`,
    url: `http://localhost:${port}`,
    reuseExistingServer: !process.env.CI,
  },
});
