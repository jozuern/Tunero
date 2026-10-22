// The sitemap for search engines, built from the pages in this folder (the 404 page left out), so a new page is listed
// without a second edit.
import type { APIRoute } from 'astro';

const paths = Object.keys(import.meta.glob('./**/*.astro'))
  .map((file) => file.replace(/^\.\//, '/').replace(/index\.astro$/, '').replace(/\.astro$/, '/'))
  .filter((path) => path !== '/404/')
  .sort();

export const GET: APIRoute = ({ site }) =>
  new Response(
    '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' +
      paths.map((path) => `  <url><loc>${new URL(path, site)}</loc></url>`).join('\n') +
      '\n</urlset>\n',
    { headers: { 'Content-Type': 'application/xml; charset=utf-8' } },
  );
