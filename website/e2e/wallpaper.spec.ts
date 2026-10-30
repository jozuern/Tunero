import { test, expect, visit, seen } from './demo.ts';
import { drawStill, shaderSource, stills, stillHeight, stillWidth } from '../scripts/render-wallpaper.mjs';

test('the stand-in stills are the shader\'s first frame (npm run render:wallpaper after a change to the shader)', async ({ page }) => {
  test.slow();
  const source = await shaderSource();
  await page.goto('/classic/');
  for (const still of stills) {
    const rendered = await drawStill(page, source, still, stillWidth, stillHeight);
    // Both scaled down to a size where only the picture counts, not the WebP's fine detail.
    const difference = await page.evaluate(
      async ([rendered, file]) => {
        const pixels = async (src: string) => {
          const image = new Image();
          image.src = src;
          await image.decode();
          const canvas = document.createElement('canvas');
          canvas.width = 128;
          canvas.height = 36;
          const context = canvas.getContext('2d')!;
          context.drawImage(image, 0, 0, canvas.width, canvas.height);
          return context.getImageData(0, 0, canvas.width, canvas.height).data;
        };
        const [a, b] = [await pixels(rendered), await pixels(`/wallpaper/${file}`)];
        let sum = 0;
        for (let i = 0; i < a.length; i += 4) sum += Math.abs(a[i] - b[i]) + Math.abs(a[i + 1] - b[i + 1]) + Math.abs(a[i + 2] - b[i + 2]);
        return sum / ((a.length / 4) * 3);
      },
      [rendered, still.file] as const,
    );
    expect(difference, `public/wallpaper/${still.file}: mean difference per channel, out of 255`).toBeLessThan(1.5);
  }
});

test.describe('on a 1920 x 1080 screen', () => {
  test.use({ viewport: { width: 1920, height: 1080 } });

  test('the wallpaper draws at most about 720p of pixels', async ({ page }) => {
    await visit(page, { motion: 'on', saved: seen });
    await expect(page.locator('.fallback')).toHaveClass(/\blive\b/);
    const [width, height] = await page.locator('canvas.wallpaper').evaluate((canvas: HTMLCanvasElement) => [canvas.width, canvas.height]);
    expect(width * height).toBeLessThanOrEqual(1280 * 720 * 1.01);
  });
});

test('the page asks for the still in the visitor\'s theme with the page', async ({ page }) => {
  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme });
    await page.goto('/');
    await expect(page.locator(`link[rel="preload"][href="/wallpaper/${colorScheme}-neglected.webp"]`)).toBeAttached();
  }
});
