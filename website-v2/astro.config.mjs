// Static site for tunero.dev (v2), built by .github/workflows/pages.yml and served by GitHub Pages. website/ (v1) is no
// longer deployed.
import { defineConfig } from 'astro/config';
import svelte from '@astrojs/svelte';

export default defineConfig({
  site: 'https://tunero.dev',
  trailingSlash: 'ignore',
  build: { format: 'directory' },
  integrations: [svelte()],
  devToolbar: { enabled: false },
  i18n: {
    locales: ['en', 'de'],
    defaultLocale: 'en',
    routing: { prefixDefaultLocale: false },
  },
});
