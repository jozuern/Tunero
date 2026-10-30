import { test, expect, visit, saved, seen, scan, tunero, nav, widget } from './demo.ts';

test('a fix and the window in front survive a reload', async ({ page }) => {
  await visit(page, { saved: seen });
  await scan(page);
  await tunero(page).getByRole('button', { name: 'Fix: Display refresh rate below its maximum' }).click();
  await expect(widget(page)).toContainText('144 Hz');
  await page.reload();
  await expect(page.locator('.desktop.ready')).toBeAttached();
  await expect(tunero(page)).toBeVisible();
  await expect(widget(page)).toContainText('144 Hz');
});

test('switching the language keeps the demo PC as it is', async ({ page }) => {
  await visit(page, { saved: seen });
  await scan(page);
  await tunero(page).getByRole('button', { name: 'Fix: Display refresh rate below its maximum' }).click();
  await page.locator('[data-taskbar="settings"]').click();
  const settings = page.getByRole('dialog', { name: 'Settings' });
  await settings.getByRole('button', { name: 'Time & language' }).click();
  await settings.getByRole('link', { name: 'Deutsch' }).click();
  await expect(page).toHaveURL(/\/de\/$/);
  await expect(page.locator('.desktop.ready')).toBeAttached();
  await expect(page.getByRole('complementary', { name: 'Dieser PC' })).toContainText('144 Hz');
});

test.describe('the boot', () => {
  test('shows on the first visit of a session, ends on a key, and not again on a reload', async ({ page }) => {
    await visit(page, { motion: 'on' });
    const boot = page.locator('.boot');
    await expect(boot).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(boot).toBeHidden();
    expect((await saved(page))?.bootSeen).toBe(true);
    await page.reload();
    await expect(page.locator('.desktop.ready')).toBeAttached();
    await expect(page.locator('html')).not.toHaveAttribute('data-boot');
    await expect(boot).toHaveCount(0);
  });

  test('a visit that started without it does not replay it when animation effects come back on', async ({ page }) => {
    await visit(page, { motion: 'off' });
    await expect(page.locator('.boot')).toHaveCount(0);
    await page.evaluate(() => localStorage.setItem('motion', 'on'));
    await page.reload();
    await expect(page.locator('.desktop.ready')).toBeAttached();
    await expect(page.locator('.boot')).toHaveCount(0);
  });
});

test('a session saved by an older deploy, with a change this one no longer has, opens without errors', async ({ page }) => {
  // Changes crashed on such a session once.
  await visit(page, { saved: { applied: [{ kind: 'tweak', id: 'no.such.tweak' }], scanned: true, bootSeen: true, window: 'tunero' } });
  await expect(tunero(page)).toBeVisible();
  await nav(page, 'changes').click();
  await expect(tunero(page).getByText('No changes in this session.')).toBeVisible();
  expect((await saved(page))?.applied).toEqual([]);
});

test('a returning visitor with changes does not see the untouched demo PC before the desktop has restored them', async ({ page }) => {
  // The island's scripts never arrive: what the page's HTML shows on its own is the wallpaper and nothing else.
  await page.route('**/_astro/*.js', (route) => route.abort());
  await page.addInitScript(() => {
    if (!sessionStorage.getItem('e2e-prepared'))
      sessionStorage.setItem('tunero-demo', JSON.stringify({ applied: [{ kind: 'tweak', id: 'x' }], scanned: true, bootSeen: true, window: null }));
    sessionStorage.setItem('e2e-prepared', '1');
  });
  await page.goto('/');
  await expect(page.locator('html')).toHaveAttribute('data-restoring', '');
  for (const part of ['.hero', '.taskbar', '.icons']) {
    await expect(page.locator(part)).toBeAttached();
    await expect(page.locator(part)).toBeHidden();
  }
  await expect(page.locator('.fallback')).toBeVisible();
});
