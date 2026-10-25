import { test, expect, visit, saved, seen, nav, tunero, widget } from './demo.ts';

const watch = 'Watch Tunero fix it';

test('with animation effects off, Watch Tunero fix it fixes everything at once and the end card shows', async ({ page }) => {
  await visit(page, { saved: seen });
  await page.getByRole('button', { name: watch }).click();
  await expect(page.getByRole('dialog', { name: 'This PC is fixed.' })).toBeVisible();
  await expect(widget(page)).toContainText('144 Hz');
  const applied = (await saved(page))!.applied;
  expect(applied.map((c) => c.kind)).toEqual(expect.arrayContaining(['finding', 'tweak', 'startup', 'app']));
});

test('Undo all on Changes takes back everything but the removed apps', async ({ page }) => {
  await visit(page, { saved: seen });
  await page.getByRole('button', { name: watch }).click();
  await page.getByRole('dialog', { name: 'This PC is fixed.' }).getByRole('button', { name: 'Close' }).click();
  // The guided fix left Tunero open.
  await nav(page, 'changes').click();
  await tunero(page).getByRole('button', { name: 'Undo all' }).click();
  await expect(widget(page)).toContainText('60 Hz');
  const applied = (await saved(page))!.applied;
  expect(applied.length).toBeGreaterThan(0);
  expect(applied.every((c) => c.kind === 'app')).toBe(true);
});

test('a key of the visitor stops the guided fix where it is', async ({ page }) => {
  await visit(page, { motion: 'on', saved: seen });
  await page.getByRole('button', { name: watch }).click();
  await expect.poll(async () => (await saved(page))?.applied.length ?? 0, { timeout: 20_000 }).toBeGreaterThan(0);
  await page.keyboard.press('Shift');
  await expect(page.locator('.guide-cursor')).toHaveCount(0);
  const taken = (await saved(page))!.applied.length;
  // A press that was under way when the visitor took over must not click after all.
  await page.waitForTimeout(3000);
  expect((await saved(page))!.applied.length).toBe(taken);
});

test.describe('on a screen with room for the notifications beside a window', () => {
  test.use({ viewport: { width: 1600, height: 1000 } });

  test('a notification switched off and right on again comes back visible', async ({ page }) => {
    // The shatter marks what it bursts as invisible; a notification that comes back before its leaving transition ended
    // is the same element, and it kept the mark once.
    await visit(page, { motion: 'on', saved: { ...seen, scanned: true, window: 'tunero' } });
    const tips = page.locator('.toasts [data-effect~="tips-toasts"]');
    await expect(tips).toBeVisible();
    await nav(page, 'tweaks').click();
    const toggle = tunero(page).getByRole('switch', { name: 'Tips, suggestions and welcome screens off' });
    // By keyboard: the notifications lie over the right side of the list.
    await toggle.focus();
    await page.keyboard.press('Space');
    await page.keyboard.press('Space');
    await page.waitForTimeout(1000);
    await expect(tips).toBeVisible();
    await expect(tips).not.toHaveClass(/\bshattered\b/);
  });
});

test('the before and after comparison only shows: it changes nothing', async ({ page }) => {
  await visit(page, { saved: seen });
  await page.getByRole('button', { name: 'Compare before and after' }).click();
  const divider = page.getByRole('slider');
  await expect(divider).toBeFocused();
  await divider.press('ArrowLeft');
  await divider.press('ArrowRight');
  const box = (await divider.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(200, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  await page.keyboard.press('Escape');
  await expect(divider).toBeHidden();
  await expect(widget(page)).toContainText('60 Hz');
  expect((await saved(page))?.applied ?? []).toEqual([]);
});
