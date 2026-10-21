// Facts read from the repository at build time, so the site never repeats a number or a text by hand.
// The build and the tests run in website-v2/, so the repository root is one folder up.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import type { Lang } from '../i18n/index.ts';

const root = join(process.cwd(), '..');
const read = (path: string) => readFileSync(join(root, path), 'utf8').replace(/\r\n/g, '\n');

/** The repository on GitHub. The old PCOptimizer address redirects here. */
export const repo = 'https://github.com/jozuern/Tunero';
/** Where people can support the project, as in .github/FUNDING.yml. */
export const support = { kofi: 'https://ko-fi.com/jozuern', sponsors: 'https://github.com/sponsors/jozuern' };
export const download = `${repo}/releases/latest/download/Tunero.exe`;

/**
 * Package managers that already offer Tunero. Set one to true once its package is live: winget after its review of
 * the first version. The site shows only these.
 */
export const packages = {
  winget: { live: false, commands: ['winget install jozuern.Tunero'] },
  choco: { live: true, commands: ['choco install tunero'] },
  scoop: { live: true, commands: ['scoop bucket add tunero https://github.com/jozuern/scoop-bucket', 'scoop install tunero/tunero'] },
};

/** Operator details for the legal notice (Impressum, § 5 DDG and § 18 MStV). */
export const operator = {
  name: 'Joshua Zürn',
  street: 'Jagststraße 49',
  city: '74861 Neudenau',
  country: { en: 'Germany', de: 'Deutschland' },
  email: 'contact@tunero.dev',
};

// The README's counts are checked against the catalog by DocsConsistencyTests, so they are safe to reuse.
const readme = read('README.md');
const count = (pattern: RegExp) => {
  const m = readme.match(pattern);
  if (!m) throw new Error(`README no longer contains ${pattern}`);
  return Number(m[1]);
};

type Action = { type: string; hive?: string; path?: string; name?: string; value?: unknown; kind?: string; missingMeans?: unknown };
type Override = { gaming?: number; reasonKey: string };
type CatalogTweak = {
  id: string;
  docs?: string;
  subject?: string;
  hidden?: boolean;
  risk: string;
  impact: { gaming: number; basis: string };
  impactOverrides?: Override[];
  actions: Action[];
};

const tweaksDir = 'src/Optimizer.Core/Catalog/Tweaks';
const catalog: CatalogTweak[] = readdirSync(join(root, tweaksDir))
  .filter((f) => f.endsWith('.json'))
  .sort()
  .flatMap((f) => JSON.parse(read(`${tweaksDir}/${f}`)).tweaks as CatalogTweak[])
  .filter((t) => !t.hidden);

export const counts = {
  checks: count(/(\d+) read-only checks/),
  tweaks: catalog.length,
  apps: count(/(\d+) removable apps/),
  cleanup: count(/(\d+) cleanup categories/),
};

export type CheckStatus = 'Ok' | 'Info' | 'Problem' | 'Unknown' | 'Unsupported';

export interface Doc {
  title: string;
  summary: string;
  sources: string[];
}

/**
 * An explanation page of the app (Catalog/Docs/{lang}/{id}.md): its title, the summary (the first section; on a check
 * page the block for `status`) with `{{name}}` placeholders filled from `vars` and citation marks removed, and sources.
 */
export function doc(id: string, lang: Lang, options: { status?: CheckStatus; vars?: Record<string, string> } = {}): Doc {
  const text = read(`src/Optimizer.Core/Catalog/Docs/${lang}/${id}.md`);
  const title = text.match(/^# (.+)$/m)![1].trim();
  const sections = text.split(/^## /m).slice(1).map((p) => p.slice(p.indexOf('\n') + 1).trim());
  let summary = sections[0];
  const blocks = [...summary.matchAll(/^::: status ([\w,]+)\n([\s\S]*?)\n:::/gm)];
  if (blocks.length > 0) {
    const block = blocks.find((b) => b[1].split(',').includes(options.status ?? 'Problem'));
    if (!block) throw new Error(`${id} has no summary for status ${options.status}`);
    summary = block[2];
  }
  summary = summary
    .replace(/\{\{(\w+)\}\}/g, (_, name: string) => {
      const value = options.vars?.[name];
      if (value === undefined) throw new Error(`${id}: no value for {{${name}}}`);
      return value;
    })
    .replace(/\s*\[\d+\]/g, '')
    .trim();
  const sources = sections.at(-1)!.split('\n').map((l) => l.replace(/^\d+\.\s*/, '').trim()).filter(Boolean);
  return { title, summary, sources };
}

const docTitle = (lang: Lang, id: string) => {
  try {
    return read(`src/Optimizer.Core/Catalog/Docs/${lang}/${id}.md`).match(/^# (.+)$/m)![1].trim();
  } catch {
    return undefined;
  }
};

/**
 * The titles of the scan's checks: the explanation pages A.*, F*.* and G.* (a placeholder such as {{min}} is dropped),
 * alphabetical. Fails the build when their number no longer matches the README's count.
 */
export function checkTitles(lang: Lang): string[] {
  const titles = readdirSync(join(root, `src/Optimizer.Core/Catalog/Docs/${lang}`))
    .filter((f) => /^[AFG]\d*\.[^.]+\.md$/.test(f))
    .map((f) => docTitle(lang, f.slice(0, -3))!.replace(/\s*\{\{\w+\}\}/g, ''))
    .sort((a, b) => a.localeCompare(b, lang));
  if (titles.length !== counts.checks) throw new Error(`${titles.length} check pages, but the README says ${counts.checks} checks`);
  return titles;
}

/** The source of every check class, by check id (from `const string Id` to the next one). */
const checkSources = (() => {
  const dir = join(root, 'src/Optimizer.Core/Findings');
  const sources = new Map<string, string>();
  for (const file of readdirSync(dir, { recursive: true }) as string[]) {
    if (!file.endsWith('.cs')) continue;
    for (const part of readFileSync(join(dir, file), 'utf8').split('const string Id = "').slice(1))
      sources.set(part.slice(0, part.indexOf('"')), part);
  }
  return sources;
})();

/**
 * Every rating for games a check can give: the literal values of its `Impact = ...`, a plain number or each branch of
 * a conditional. Checks set their rating in code, so this is how the site knows a rating it shows is a real one.
 */
export function checkImpacts(id: string): number[] {
  const source = checkSources.get(id);
  if (!source) throw new Error(`Check ${id} not found in Findings`);
  const expressions = [...source.matchAll(/Impact = ([^,;\n]+)/g)].map((m) => m[1]);
  // A rating computed by a method of the class (A.xmp: Impact = Impact(p.Cpu, c)) counts with every value it returns.
  if (/Impact = Impact\(/.test(source)) {
    const method = source.match(/static int Impact\([^)]*\)\s*\{([\s\S]*?)\n    \}/);
    if (!method) throw new Error(`Check ${id} computes its rating in a method the site cannot read`);
    expressions.push(...[...method[1].matchAll(/return ([^;]+);/g)].map((m) => m[1]));
  }
  const values = expressions
    .flatMap((expression) => expression.split(/[?:]/))
    .map((x) => x.trim())
    .filter((x) => /^\d$/.test(x))
    .map(Number);
  return [...new Set(values)].sort();
}

/** The checks whose problem rates 5 for games, the top of the scale, by the title of their page. */
export function topChecks(lang: Lang): string[] {
  const titles = [...checkSources.keys()]
    .filter((id) => checkImpacts(id).includes(5))
    .map((id) => docTitle(lang, id)?.replace(/\s*\{\{\w+\}\}/g, ''));
  if (titles.length === 0 || titles.some((t) => !t)) throw new Error('Checks rated 5: a page is missing or none found');
  return (titles as string[]).sort((a, b) => a.localeCompare(b, lang));
}

export type TweakAction = Action;

/** A catalog tweak's rating for games on most PCs, the basis of that rating, its risk and what it does. */
export function tweak(id: string): { impact: number; basis: string; risk: string; actions: TweakAction[] } {
  const t = catalog.find((x) => x.id === id);
  if (!t) throw new Error(`Tweak ${id} not found in the catalog`);
  return { impact: t.impact.gaming, basis: t.impact.basis, risk: t.risk, actions: t.actions };
}

/** A tweak's gaming impact (0 to 5) on most PCs, and the highest rating a condition on some PCs gives it. */
export interface TweakImpact {
  id: string;
  title: string;
  impact: number;
  basis: string;
  higher?: { impact: number; reason: string };
}

/** The gaming impact of every tweak the app shows, in catalog order. */
export function tweakImpacts(lang: Lang): TweakImpact[] {
  const labels = JSON.parse(read('src/Optimizer.Core/Catalog/Data/labels.json'))[lang] as Record<string, string>;
  return catalog.map((t) => {
    const title = docTitle(lang, t.docs ?? t.id) ?? t.id;
    const top = (t.impactOverrides ?? [])
      .filter((o) => (o.gaming ?? 0) > t.impact.gaming)
      .sort((a, b) => b.gaming! - a.gaming!)[0];
    return {
      id: t.id,
      title: t.subject ? `${title}: ${t.subject}` : title,
      impact: t.impact.gaming,
      basis: t.impact.basis,
      higher: top && { impact: top.gaming!, reason: labels[top.reasonKey] ?? '' },
    };
  });
}

/** The title of a tweak's explanation page. */
export const tweakTitle = (id: string, lang: Lang) => {
  const t = catalog.find((x) => x.id === id);
  const title = t && docTitle(lang, t.docs ?? t.id);
  if (!title) throw new Error(`Tweak ${id} has no ${lang} page`);
  return t.subject ? `${title}: ${t.subject}` : title;
};

/** The display title of a removable inbox app (Catalog/Data/appx.json). */
export function inboxApp(name: string, lang: Lang): string {
  const apps = JSON.parse(read('src/Optimizer.Core/Catalog/Data/appx.json')).apps as { name: string; title: string; titleDe?: string }[];
  const app = apps.find((a) => a.name === name);
  if (!app) throw new Error(`Inbox app ${name} not found in appx.json`);
  return lang === 'de' ? (app.titleDe ?? app.title) : app.title;
}

/** How a removed inbox app comes back (appx.json): store = from the Microsoft Store, none = not offered there. */
export function inboxAppReinstall(name: string): string {
  const apps = JSON.parse(read('src/Optimizer.Core/Catalog/Data/appx.json')).apps as { name: string; reinstall: string }[];
  const app = apps.find((a) => a.name === name);
  if (!app) throw new Error(`Inbox app ${name} not found in appx.json`);
  return app.reinstall;
}

/** The newest `versions` released sections of CHANGELOG.md (English only), as written there; Unreleased is left out. */
export function changelog(versions: number): { version: string; body: string }[] {
  return read('CHANGELOG.md')
    .split(/^## /m)
    .slice(1)
    .filter((part) => /^\d+\.\d+\.\d+\s/.test(part))
    .slice(0, versions)
    .map((part) => ({ version: part.slice(0, part.indexOf('\n')).trim(), body: part.slice(part.indexOf('\n') + 1).trim() }));
}

const resx = (lang: Lang) => {
  const text = read(`src/Optimizer.App/Resources/Strings${lang === 'de' ? '.de' : ''}.resx`);
  const entities: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };
  return new Map(
    [...text.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)].map((m) => [m[1], m[2].replace(/&(\w+);/g, (_, name: string) => entities[name] ?? `&${name};`)]),
  );
};
const appStrings = { en: resx('en'), de: resx('de') };

/** A text of the app itself (Optimizer.App/Resources/Strings*.resx), so the demo app says what the real one says. */
export function appString(key: string, lang: Lang): string {
  const value = appStrings[lang].get(key);
  if (value === undefined) throw new Error(`Strings.resx has no ${key}`);
  return value;
}

/** The label of a tweak's category, as the app shows it (Catalog/Data/labels.json). */
export function tweakCategory(id: string, lang: Lang): string {
  const t = catalog.find((x) => x.id === id) as (CatalogTweak & { category?: string }) | undefined;
  const labels = JSON.parse(read('src/Optimizer.Core/Catalog/Data/labels.json'))[lang] as Record<string, string>;
  const label = t?.category && labels[`category.${t.category}`];
  if (!label) throw new Error(`Tweak ${id} has no category label`);
  return label;
}
