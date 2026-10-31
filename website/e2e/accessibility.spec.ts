// axe-core's WCAG 2.0, 2.1 and 2.2 A and AA rules on every kind of page the site has, in light and dark: the start page
// (the demo desktop) as it opens, with Tunero scanned on each of its pages, with each other window and Start, in the
// wide and the compact layout; the classic page, also on a phone; the legal pages and the 404 page. Contrast, names and
// landmarks are what a review by eye misses. axe cannot judge the contrast of text drawn over the wallpaper (the hero,
// the widget, the taskbar): it reports those as "incomplete", not as findings, so they stay a check by eye.
import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { test, expect, visit, seen, scan, tunero, nav } from './demo.ts';

/** The WCAG 2 A and AA rules, with every finding as one readable line: rule, impact, element, what is wrong. */
async function findings(page: Page): Promise<string[]> {
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  return result.violations.flatMap((v) =>
    v.nodes.map((n) => `${v.id} (${v.impact}): ${n.target.join(' ')}: ${n.failureSummary?.replace(/\s+/g, ' ')}`),
  );
}

for (const colorScheme of ['light', 'dark'] as const) {
  test.describe(`${colorScheme} theme`, () => {
    test.use({ colorScheme });

    for (const path of ['/', '/de/']) {
      test(`the start page ${path} as it opens`, async ({ page }) => {
        await visit(page, { saved: seen, path });
        expect(await findings(page)).toEqual([]);
      });
    }

    test('the demo desktop with Tunero scanned, wide', async ({ page }) => {
      await visit(page, { saved: seen });
      await scan(page);
      expect(await findings(page)).toEqual([]);
    });

    test('every page of the demo Tunero', async ({ page }) => {
      await visit(page, { saved: seen });
      await scan(page);
      const found: string[] = [];
      for (const name of ['tweaks', 'startup', 'apps', 'changes']) {
        await nav(page, name).click();
        await expect(nav(page, name)).toHaveAttribute('aria-current', 'page');
        found.push(...(await findings(page)).map((f) => `${name}: ${f}`));
      }
      expect(found).toEqual([]);
    });

    // One window at a time, in front: with several open, the ones behind are partly covered, and axe counts a button
    // under another window as too small a target (target-size), as it would on a real desktop.
    for (const id of ['trust', 'notepad', 'settings']) {
      test(`the demo's ${id} window`, async ({ page }) => {
        await visit(page, { saved: seen });
        await page.locator(`[data-taskbar="${id}"]`).click();
        await expect(page.locator(`[data-window="${id}"]`)).toBeVisible();
        expect(await findings(page)).toEqual([]);
      });
    }

    test("the demo's Start menu", async ({ page }) => {
      await visit(page, { saved: seen });
      await page.locator('[data-anchor="start"]').click();
      await expect(page.getByRole('dialog', { name: 'Start' })).toBeVisible();
      expect(await findings(page)).toEqual([]);
    });

    test('the demo desktop with Tunero scanned, compact', async ({ page }) => {
      // Narrower than 900 px the page sends a fresh visit to the classic page (Base.astro, phoneTo), so the compact
      // layout is what a desktop browser window gets when it is made narrow after the start.
      await page.setViewportSize({ width: 1024, height: 768 });
      await visit(page, { saved: seen });
      await scan(page);
      await page.setViewportSize({ width: 860, height: 768 });
      await expect(tunero(page).getByRole('button', { name: 'Tweaks', exact: true })).toBeVisible();
      expect(await findings(page)).toEqual([]);
    });

    test('the classic page on a phone, where the start page sends phones', async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await page.goto('/');
      await expect(page).toHaveURL(/\/classic\/$/);
      expect(await findings(page)).toEqual([]);
    });

    for (const path of ['/classic/', '/de/classic/', '/legal/', '/privacy/', '/de/impressum/', '/de/datenschutz/', '/no-such-page/']) {
      test(`the page ${path}`, async ({ page }) => {
        const response = await page.goto(path);
        expect(response?.status()).toBe(path === '/no-such-page/' ? 404 : 200);
        expect(await findings(page)).toEqual([]);
      });
    }
  });
}
