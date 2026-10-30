// Static site for tunero.dev, built by .github/workflows/pages.yml and served by GitHub Pages.
import { defineConfig } from 'astro/config';
import svelte from '@astrojs/svelte';
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { gzipSync } from 'node:zlib';
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
 *
 * Both read Astro's output with patterns, so the build fails when that output changes shape instead of shipping pages
 * without them: a stylesheet link or an island the patterns do not read, or island scripts over the budget of DESIGN.md.
 */
const islandBudget = 150 * 1024;

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
          const links = html.match(/<link rel="stylesheet" href="[^"]+">/g) ?? [];
          if (links.length !== (html.match(/rel="stylesheet"/g) ?? []).length) throw new Error(`${page}: a stylesheet link of a new shape`);
          for (const link of links) {
            const css = await readFile(join(root, link.match(/href="([^"]+)"/)[1]), 'utf8');
            if (sheets.has(css)) html = html.replace(link, '');
            sheets.add(css);
          }
          // The island's tag (its props are escaped, so it holds no ">"), then its two script addresses.
          const entries = [...html.matchAll(/<astro-island[^>]*>/g)].flatMap((tag) =>
            [...tag[0].matchAll(/\s(?:component|renderer)-url="([^"]+)"/g)].map((u) => u[1]),
          );
          if (html.includes('<astro-island') && entries.length === 0) throw new Error(`${page}: an island without script addresses`);
          if (entries.length === 0) {
            await writeFile(path, html);
            continue;
          }
          const seen = new Set();
          const visit = async (url) => {
            if (seen.has(url)) return;
            seen.add(url);
            const code = await readFile(join(root, url), 'utf8');
            // import x from "./x.js", import "./x.js", export { x } from "./x.js"
            for (const m of code.matchAll(/(?:^|[;}\s])(?:import|export)(?:[^'"`()]*?from)?\s*["'](\.\/[^"']+\.js)["']/g))
              await visit(new URL(m[1], `https://x${url}`).pathname);
          };
          for (const url of entries) await visit(url);
          let gzip = 0;
          for (const url of seen) gzip += gzipSync(await readFile(join(root, url))).length;
          if (gzip > islandBudget) throw new Error(`${page}: the island's scripts are ${Math.round(gzip / 1024)} KB (gzip), over the budget of ${islandBudget / 1024} KB`);
          const preloads = [...seen].map((url) => `<link rel="modulepreload" href="${url}">`).join('');
          await writeFile(path, html.replace('</head>', `${preloads}</head>`));
        }
      },
    },
  };
}

/**
 * virtual:desktop-data/<lang>: what the demo desktop shows in that language (src/lib/desktop-data.ts), read from the
 * repository at build time and served as a module, so the island imports it into a script the browser caches instead
 * of finding it in every page's HTML. In the build Node runs the TypeScript itself, as it does for the tests (npm test);
 * the dev server loads it through Vite and reloads the page when anything it is made from changes. Both run in
 * website/, which repo.ts expects too.
 */
function desktopData() {
  const prefix = 'virtual:desktop-data/';
  /** What the data is read from: the site's own code, and the files of the app repository that repo.ts reads. */
  // As Vite names files: with forward slashes, also on Windows.
  const posix = (path) => path.replace(/\\/g, '/');
  const sources = ['src/lib', 'src/i18n', 'src/desktop/app/appKeys.ts'].map((path) => posix(join(process.cwd(), path)));
  const repository = ['README.md', 'CHANGELOG.md', 'src/Optimizer.Core/Catalog', 'src/Optimizer.Core/Findings', 'src/Optimizer.App/Resources'].map(
    (path) => posix(join(process.cwd(), '..', path)),
  );
  /** The dev server, when there is one: it loads the data through Vite, which then knows its files. */
  let server;
  return {
    name: 'desktop-data',
    configureServer(dev) {
      server = dev;
      // The repository's files lie outside website/, which Vite watches by itself.
      dev.watcher.add(repository);
    },
    resolveId: (id) => (id.startsWith(prefix) ? `\0${id}` : undefined),
    async load(id) {
      if (!id.startsWith(`\0${prefix}`)) return undefined;
      const lang = id.slice(prefix.length + 1);
      if (lang !== 'en' && lang !== 'de') throw new Error(`${id}: no such language`);
      // Only for what the dev server itself asks: Astro also starts a server during the build, whose loader must not be
      // used from the build's own environments.
      const dev = server && this.environment?.mode === 'dev';
      const { desktopData: read } = dev
        ? await server.ssrLoadModule('/src/lib/desktop-data.ts')
        : await import(pathToFileURL(join(process.cwd(), 'src/lib/desktop-data.ts')).href);
      return `export default ${JSON.stringify(read(lang))};`;
    },
    // In dev, an edit to anything the data is made from (a copy file, the catalog) shows on the next load: the server's
    // modules are read again (repo.ts reads the repository when it loads) and the page reloads.
    // Vite asks once per environment (the server's and the browser's).
    hotUpdate({ file }) {
      const changed = posix(file);
      if (![...sources, ...repository].some((source) => changed === source || changed.startsWith(source + '/'))) return undefined;
      this.environment.moduleGraph.invalidateAll();
      if (this.environment.name === 'client') this.environment.hot.send({ type: 'full-reload' });
      return [];
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
