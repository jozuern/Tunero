// What the browser tests share: a test that fails on any error in the page, and a visit to the demo desktop with the
// visitor's choices and a saved session set up before the page's own scripts run.
import { test as base, expect, type Page } from '@playwright/test';
import { storageKeys } from '../src/lib/storage-keys.ts';

export { expect };

export interface Saved {
  applied: { kind: string; id: string }[];
  scanned: boolean;
  bootSeen: boolean;
  window: string | null;
}

export const test = base.extend<{ pageErrors: string[] }>({
  pageErrors: [
    async ({ page }, use) => {
      const errors: string[] = [];
      page.on('pageerror', (error) => errors.push(error.message));
      await use(errors);
      expect(errors, 'errors in the page').toEqual([]);
    },
    { auto: true },
  ],
});

/**
 * Opens the home page as a visitor with animation effects on or off and, if given, a session saved before. Both are set
 * once per tab, before the first visit's scripts, so a reload or a language switch keeps what the page changed since.
 */
export async function visit(page: Page, { motion = 'off', saved, path = '/' }: { motion?: 'on' | 'off'; saved?: Saved; path?: string } = {}) {
  await page.addInitScript(
    ([keys, motion, saved]) => {
      if (sessionStorage.getItem('e2e-prepared')) return;
      sessionStorage.setItem('e2e-prepared', '1');
      localStorage.setItem(keys.motion, motion);
      if (saved) sessionStorage.setItem(keys.demo, saved);
    },
    [storageKeys, motion, saved ? JSON.stringify(saved) : null] as const,
  );
  await page.goto(path);
  await expect(page.locator('.desktop.ready')).toBeAttached();
}

/** The session the page saved, as the next visit reads it. */
export function saved(page: Page): Promise<Saved | null> {
  return page.evaluate((key) => JSON.parse(sessionStorage.getItem(key) ?? 'null'), storageKeys.demo);
}

/** A session that saw the boot, with nothing changed: the desktop at once, as on a reload. */
export const seen: Saved = { applied: [], scanned: false, bootSeen: true, window: null };

export const widget = (page: Page) => page.getByRole('complementary', { name: 'This PC' });
export const tunero = (page: Page) => page.getByRole('dialog', { name: 'Tunero' });
export const nav = (page: Page, name: string) => tunero(page).locator(`[data-nav="${name}"]`);

/** Opens Tunero from the desktop and runs the scan; with animation effects off it finishes at once. */
export async function scan(page: Page) {
  await page.getByRole('button', { name: 'Fix it yourself' }).click();
  await tunero(page).getByRole('button', { name: 'Scan this PC' }).click();
  await expect(tunero(page).getByRole('button', { name: 'Scan again' })).toBeVisible({ timeout: 20_000 });
}
