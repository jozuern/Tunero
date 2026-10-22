// Static site for tunero.dev (v2), built by .github/workflows/pages.yml and served by GitHub Pages. website/ (v1) is no
// longer deployed.
import { defineConfig } from 'astro/config';
import svelte from '@astrojs/svelte';
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

/**
 * After the build, per page: drops a stylesheet link whose file is byte for byte one the page already links. The two
 * language islands share the desktop component, and the build writes its CSS twice, once for the page and once for the
 * shared module, under two names; the page would load the same 42 KB twice.
 *
 * Astro starts loading an island's scripts only when the browser reaches the end of the island, which on the demo page
 * is the end of the document, and the shared Svelte runtime only after that script has arrived. This adds
 * <link rel="modulepreload"> to the head for the island's component, its renderer and every module they import
 * statically, so all of them load in parallel with the rest of the page.
 */
function finishPages() {
  return {
    name: 'finish-pages',
    hooks: {
      'astro:build:done': async ({ dir }) => {
        const root = fileURLToPath(dir);
        const pages = (await readdir(root, { recursive: true })).filter((file) => file.endsWith('.html'));
        for (const page of pages) {
          const path = join(root, page);
          let html = await readFile(path, 'utf8');
          const sheets = new Set();
          for (const link of html.match(/<link rel="stylesheet" href="[^"]+">/g) ?? []) {
            const css = await readFile(join(root, link.match(/href="([^"]+)"/)[1]), 'utf8');
            if (sheets.has(css)) html = html.replace(link, '');
            sheets.add(css);
          }
          // The island's tag (its props are escaped, so it holds no ">"), then its two script addresses.
          const entries = [...html.matchAll(/<astro-island[^>]*>/g)].flatMap((tag) =>
            [...tag[0].matchAll(/\s(?:component|renderer)-url="([^"]+)"/g)].map((u) => u[1]),
          );
          if (entries.length === 0) {
            await writeFile(path, html);
            continue;
          }
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

/**
 * virtual:desktop-data/<lang>: what the demo desktop shows in that language (src/lib/desktop-data.ts), read from the
 * repository at build time and served as a module, so the island imports it into a script the browser caches instead
 * of finding it in every page's HTML. Node runs the TypeScript itself, as it does for the tests (npm test); the build
 * runs in website-v2/, which repo.ts expects too.
 */
function desktopData() {
  const prefix = 'virtual:desktop-data/';
  return {
    name: 'desktop-data',
    resolveId: (id) => (id.startsWith(prefix) ? `\0${id}` : undefined),
    async load(id) {
      if (!id.startsWith(`\0${prefix}`)) return undefined;
      const lang = id.slice(prefix.length + 1);
      if (lang !== 'en' && lang !== 'de') throw new Error(`${id}: no such language`);
      const source = pathToFileURL(join(process.cwd(), 'src/lib/desktop-data.ts')).href;
      const { desktopData: read } = await import(source);
      return `export default ${JSON.stringify(read(lang))};`;
    },
  };
}

export default defineConfig({
  site: 'https://tunero.dev',
  trailingSlash: 'ignore',
  build: { format: 'directory' },
  integrations: [svelte(), finishPages()],
  devToolbar: { enabled: false },
  vite: { plugins: [desktopData()] },
  i18n: {
    locales: ['en', 'de'],
    defaultLocale: 'en',
    routing: { prefixDefaultLocale: false },
  },
});
