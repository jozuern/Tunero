// Static site for tunero.dev (v2), built by .github/workflows/pages.yml and served by GitHub Pages. website/ (v1) is no
// longer deployed.
import { defineConfig } from 'astro/config';
import svelte from '@astrojs/svelte';
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Astro starts loading an island's scripts only when the browser reaches the end of the island, which on the demo page
 * is the end of the document, and the shared Svelte runtime only after that script has arrived. This adds
 * <link rel="modulepreload"> to the head for the island's component, its renderer and every module they import
 * statically, so all of them load in parallel with the rest of the page.
 */
function preloadIslands() {
  return {
    name: 'preload-islands',
    hooks: {
      'astro:build:done': async ({ dir }) => {
        const root = fileURLToPath(dir);
        const pages = (await readdir(root, { recursive: true })).filter((file) => file.endsWith('.html'));
        for (const page of pages) {
          const path = join(root, page);
          const html = await readFile(path, 'utf8');
          // The island's tag (its props are escaped, so it holds no ">"), then its two script addresses.
          const entries = [...html.matchAll(/<astro-island[^>]*>/g)].flatMap((tag) =>
            [...tag[0].matchAll(/\s(?:component|renderer)-url="([^"]+)"/g)].map((u) => u[1]),
          );
          if (entries.length === 0) continue;
          const seen = new Set();
          const visit = async (url) => {
            if (seen.has(url)) return;
            seen.add(url);
            const code = await readFile(join(root, url), 'utf8');
            for (const m of code.matchAll(/(?:^|[;}\s])import(?:[^'"`()]*?from)?\s*["'](\.\/[^"']+\.js)["']/g))
              await visit(new URL(m[1], `https://x${url}`).pathname);
          };
          for (const url of entries) await visit(url);
          const links = [...seen].map((url) => `<link rel="modulepreload" href="${url}">`).join('');
          await writeFile(path, html.replace('</head>', `${links}</head>`));
        }
      },
    },
  };
}

export default defineConfig({
  site: 'https://tunero.dev',
  trailingSlash: 'ignore',
  build: { format: 'directory' },
  integrations: [svelte(), preloadIslands()],
  devToolbar: { enabled: false },
  i18n: {
    locales: ['en', 'de'],
    defaultLocale: 'en',
    routing: { prefixDefaultLocale: false },
  },
});
