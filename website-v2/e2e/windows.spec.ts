import type { Page } from '@playwright/test';
import { test, expect, visit, seen } from './demo.ts';

const taskbarHeight = 48;
const order = ['trust', 'notepad', 'settings', 'tunero'] as const;

/** Every caption button of every open window can be reached: on screen and above the taskbar. */
async function captionsInReach(page: Page) {
  const viewport = page.viewportSize()!;
  for (const button of await page.locator('.window .caption button').all()) {
    const box = (await button.boundingBox())!;
    expect(box.x, `${await button.getAttribute('aria-label')}`).toBeGreaterThanOrEqual(0);
    expect(box.y).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
    expect(box.y + box.height).toBeLessThanOrEqual(viewport.height - taskbarHeight);
  }
}

test.describe('on a 1024 x 768 screen', () => {
  test.use({ viewport: { width: 1024, height: 768 } });

  for (const ids of [order, [...order].reverse()]) {
    test(`every window keeps its caption buttons in reach, opened as ${ids.join(', ')}`, async ({ page }) => {
      // Tunero's were off screen here once, opened after the others.
      await visit(page, { saved: seen });
      for (const id of ids) await page.locator(`[data-taskbar="${id}"]`).click();
      await expect(page.locator('.window')).toHaveCount(4);
      await captionsInReach(page);
    });
  }
});

test('a window that opens takes the focus; Esc closes it and gives the focus back', async ({ page }) => {
  await visit(page, { saved: seen });
  const icon = page.locator('.icons').getByRole('button', { name: 'Changelog' });
  await icon.click();
  const notepad = page.getByRole('dialog', { name: 'Changelog' });
  await expect(notepad).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(notepad).toBeHidden();
  await expect(icon).toBeFocused();
});

test('a taskbar button opens its window, minimizes it when in front, and brings it back with the focus', async ({ page }) => {
  await visit(page, { saved: seen });
  const button = page.locator('[data-taskbar="notepad"]');
  const notepad = page.locator('[data-window="notepad"]');
  await button.click();
  await expect(notepad).toBeFocused();
  await button.click();
  await expect(notepad).toHaveClass(/\bminimized\b/);
  // The focus goes to the taskbar button, not to the page, where Esc and a screen reader would find nothing.
  await expect(button).toBeFocused();
  await button.click();
  await expect(notepad).not.toHaveClass(/\bminimized\b/);
  await expect(notepad).toBeFocused();
});

test('a window behind comes to the front from the taskbar, with the focus', async ({ page }) => {
  await visit(page, { saved: seen });
  await page.locator('[data-taskbar="notepad"]').click();
  await page.locator('[data-taskbar="settings"]').click();
  await expect(page.locator('[data-window="settings"]')).toHaveClass(/\bfocused\b/);
  await page.locator('[data-taskbar="notepad"]').click();
  const notepad = page.locator('[data-window="notepad"]');
  await expect(notepad).toHaveClass(/\bfocused\b/);
  await expect(notepad).toBeFocused();
  await expect(page.locator('[data-window="settings"]')).toBeVisible();
});

test('open windows move back in reach when the browser window gets smaller', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await visit(page, { saved: seen });
  for (const id of order) await page.locator(`[data-taskbar="${id}"]`).click();
  await page.setViewportSize({ width: 1024, height: 700 });
  await expect(async () => captionsInReach(page)).toPass();
});
