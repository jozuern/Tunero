// What the demo desktop shows in one language, read from the repository at build time: the scenario, the app's strings,
// the copy, the trust items and the changelog. It changes only with a deploy, so it is not written into every page's
// HTML: astro.config.mjs serves it as the module virtual:desktop-data/<lang>, which the island of that language
// imports (desktop/DesktopEn.svelte, DesktopDe.svelte) and the browser caches with the scripts.
import { buildDemo } from './demo.ts';
import { appString, changelog, download, repo } from './repo.ts';
import { t, localPath, type Lang } from '../i18n/index.ts';
import desktopEn from '../i18n/desktop-en.ts';
import desktopDe from '../i18n/desktop-de.ts';
import { appKeys, type AppStrings } from '../desktop/app/appKeys.ts';

export function desktopData(lang: Lang) {
  const copy = t(lang);
  return {
    lang,
    demo: buildDemo(lang),
    copy: lang === 'de' ? desktopDe : desktopEn,
    app: Object.fromEntries(appKeys.map((key) => [key, appString(key, lang)])) as AppStrings,
    trust: { intro: copy.trust.intro, items: copy.trust.items },
    changelog: { entries: changelog(3), href: `${repo}/blob/main/CHANGELOG.md` },
    links: {
      download,
      faq: `${localPath(lang, '/classic/')}#faq`,
      privacy: lang === 'de' ? '/de/datenschutz/' : '/privacy/',
    },
  };
}

export type DesktopData = ReturnType<typeof desktopData>;
