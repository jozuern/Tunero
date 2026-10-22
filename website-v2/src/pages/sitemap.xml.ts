// The sitemap for search engines, built from the pages in this folder, so a new page is listed without a second edit.
// Left out: the 404 page, and the classic pages, which are the phone version of the home page (Base.astro phoneOf) and
// whose canonical address is the home page's.
import type { APIRoute } from 'astro';

const paths = Object.keys(import.meta.glob('./**/*.astro'))
  .map((file) => file.replace(/^\.\//, '/').replace(/index\.astro$/, '').replace(/\.astro$/, '/'))
  .filter((path) => path !== '/404/' && !path.endsWith('/classic/'))
  .sort();

export const GET: APIRoute = ({ site }) =>
  new Response(
    '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' +
      paths.map((path) => `  <url><loc>${new URL(path, site)}</loc></url>`).join('\n') +
      '\n</urlset>\n',
    { headers: { 'Content-Type': 'application/xml; charset=utf-8' } },
  );
