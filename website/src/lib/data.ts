// Facts read from the repository at build time, so the site never repeats a number or a text by hand.
// The build runs in website/, so the repository root is one folder up.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const root = join(process.cwd(), '..');
const read = (path: string) => readFileSync(join(root, path), 'utf8');

/** The repository on GitHub. The old PCOptimizer address redirects here. */
export const repo = 'https://github.com/jozuern/Tunero';
/** Where people can support the project, as in .github/FUNDING.yml. */
export const support = { kofi: 'https://ko-fi.com/jozuern', sponsors: 'https://github.com/sponsors/jozuern' };
export const download = `${repo}/releases/latest/download/Tunero.exe`;

/**
 * The latest release, which is what the download link gets: its version and the size of Tunero.exe.
 */
async function latestRelease(): Promise<{ version: string; size?: number } | undefined> {
  try {
    const headers: Record<string, string> = { Accept: 'application/vnd.github+json', 'User-Agent': 'tunero-website' };
    if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
    const res = await fetch('https://api.github.com/repos/jozuern/Tunero/releases/latest', { headers, signal: AbortSignal.timeout(10000) });
    if (!res.ok) return undefined;
    const release = await res.json();
    const exe = (release.assets as { name: string; size: number }[]).find((a) => a.name === 'Tunero.exe');
    return { version: (release.tag_name as string).replace(/^v/, ''), size: exe?.size };
  } catch {
    return undefined;
  }
}

const release = await latestRelease();
// Offline, the newest version heading of the changelog stands in: the project file is raised by every commit and runs ahead.
export const version = release?.version ?? read('CHANGELOG.md').match(/^## (\d+\.\d+\.\d+)/m)![1];
/** Size of the exe in MB as Windows counts them (1024 x 1024 bytes), when the release could be read. */
export const sizeMb = release?.size ? Math.round(release.size / 1048576) : undefined;

// The README's counts are checked against the catalog by DocsConsistencyTests, so they are safe to reuse.
const readme = read('README.md');
const count = (pattern: RegExp) => {
  const m = readme.match(pattern);
  if (!m) throw new Error(`README no longer contains ${pattern}`);
  return Number(m[1]);
};

/** Tweaks shown in the app (the catalog minus hidden entries). */
export const tweakCount = readdirSync(join(root, 'src/Optimizer.Core/Catalog/Tweaks'))
  .filter((f) => f.endsWith('.json'))
  .flatMap((f) => JSON.parse(read(`src/Optimizer.Core/Catalog/Tweaks/${f}`)).tweaks as { hidden?: boolean }[])
  .filter((t) => !t.hidden).length;

export const counts = {
  checks: count(/(\d+) read-only checks/),
  tweaks: tweakCount,
  apps: count(/(\d+) removable apps/),
  cleanup: count(/(\d+) cleanup categories/),
};

export type Lang = 'en' | 'de';

export interface Explanation {
  title: string;
  sections: { heading: string; body: string }[];
  sources: string[];
}

/** An explanation page of the app (Catalog/Docs/{lang}/{id}.md), split into its sections. */
export function explanation(id: string, lang: Lang): Explanation {
  const text = read(`src/Optimizer.Core/Catalog/Docs/${lang}/${id}.md`).replace(/\r\n/g, '\n');
  const title = text.match(/^# (.+)$/m)![1];
  const parts = text.split(/^## /m).slice(1).map((p) => {
    const [heading, ...rest] = p.split('\n');
    return { heading: heading.trim(), body: rest.join('\n').trim() };
  });
  const sourcesPart = parts.pop()!;
  const sources = sourcesPart.body.split('\n').map((l) => l.replace(/^\d+\.\s*/, '').trim()).filter(Boolean);
  return { title, sections: parts, sources };
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

/**
 * The checks whose problem rates 5 for games, the top of the scale, by the title of their page. Checks set their rating
 * in code, so this reads each check class (from `const string Id` to the next one) for an `Impact = ...` that can give 5:
 * a plain 5 or one branch of a conditional. Fails the build when it finds none or a check without a page.
 */
export function topChecks(lang: Lang): string[] {
  const dir = join(root, 'src/Optimizer.Core/Findings');
  const ids = new Set<string>();
  for (const file of readdirSync(dir, { recursive: true }) as string[]) {
    if (!file.endsWith('.cs')) continue;
    for (const part of readFileSync(join(dir, file), 'utf8').split('const string Id = "').slice(1)) {
      const results = [...part.matchAll(/Impact = ([^,;\n]+)/g)]
        .flatMap((m) => m[1].split(/[?:]/))
        .map((x) => x.trim());
      if (results.includes('5')) ids.add(part.slice(0, part.indexOf('"')));
    }
  }
  const titles = [...ids].map((id) => docTitle(lang, id)?.replace(/\s*\{\{\w+\}\}/g, ''));
  if (titles.length === 0 || titles.some((t) => !t)) throw new Error(`Checks rated 5: ${[...ids].join(', ') || 'none found'}`);
  return (titles as string[]).sort((a, b) => a.localeCompare(b, lang));
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
  type Override = { gaming?: number; reasonKey: string };
  type Tweak = { id: string; docs?: string; subject?: string; hidden?: boolean; impact: { gaming: number; basis: string }; impactOverrides?: Override[] };
  return readdirSync(join(root, 'src/Optimizer.Core/Catalog/Tweaks'))
    .filter((f) => f.endsWith('.json'))
    .sort()
    .flatMap((f) => JSON.parse(read(`src/Optimizer.Core/Catalog/Tweaks/${f}`)).tweaks as Tweak[])
    .filter((t) => !t.hidden)
    .map((t) => {
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

/** One registry change of a catalog tweak, with the Windows default it replaces. */
export interface RegistryChange {
  key: string;
  name: string;
  from: string;
  to: string;
}

/** The registry values a tweak writes. `defaults` gives the Windows default of each value for the "Now" column. */
export function registryChanges(id: string, defaults: Record<string, string>): RegistryChange[] {
  for (const f of readdirSync(join(root, 'src/Optimizer.Core/Catalog/Tweaks'))) {
    if (!f.endsWith('.json')) continue;
    const tweak = JSON.parse(read(`src/Optimizer.Core/Catalog/Tweaks/${f}`)).tweaks.find((t: { id: string }) => t.id === id);
    if (!tweak) continue;
    return tweak.actions
      .filter((a: { type: string }) => a.type === 'registry')
      .map((a: { hive: string; path: string; name: string; value: string }) => ({
        key: `${a.hive === 'user' ? 'HKCU' : 'HKLM'}\\${a.path}`,
        name: a.name,
        from: defaults[a.name] ?? '',
        to: String(a.value),
      }));
  }
  throw new Error(`Tweak ${id} not found in the catalog`);
}

/**
 * Package managers that already offer Tunero. Set one to true once its package is live: winget and Chocolatey after
 * their review of the first version, Scoop once the jozuern/scoop-bucket repository exists. The site shows only these.
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
