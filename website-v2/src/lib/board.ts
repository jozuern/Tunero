// The rating board of the classic page: every check and every tweak the app shows, in the row of its highest rating
// for games (0 to 5), read from the repository at build time.
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import type { Lang } from '../i18n/index.ts';
import { checkImpacts, counts, tweakImpacts } from './repo.ts';

const root = join(process.cwd(), '..');
const read = (path: string) => readFileSync(join(root, path), 'utf8').replace(/\r\n/g, '\n');
const docsDir = (lang: Lang) => `src/Optimizer.Core/Catalog/Docs/${lang}`;

export interface BoardEntry {
  id: string;
  title: string;
  /** A tweak's basis: measured, situational or disputed. */
  basis?: string;
  /** A check: the lower ratings it gives for other findings, highest first. */
  also?: number[];
  /** A tweak that rates higher on some PCs: its rating elsewhere, and the app's reason for the higher one. */
  otherwise?: { impact: number; reason: string };
}

export interface BoardRow {
  level: number;
  checks: BoardEntry[];
  tweaks: BoardEntry[];
}

/** A check the app does not rate: the game access panel, or a problem it marks critical instead. */
export interface UnratedCheck {
  id: string;
  title: string;
  why: 'access' | 'critical';
}

export interface Board {
  rows: BoardRow[];
  unrated: UnratedCheck[];
  checkCount: number;
  tweakCount: number;
  /** Tweaks that rate above 0 on most PCs or on some. */
  matter: number;
}

const pageTitle = (lang: Lang, id: string) =>
  read(`${docsDir(lang)}/${id}.md`).match(/^# (.+)$/m)![1].replace(/\s*\{\{\w+\}\}/g, '').trim();

/**
 * The microcode advisories (cpu.json) share one check class without an id of their own: a critical one is marked
 * critical instead of rated, the others get the rating that class assigns (Impact = rule.Critical ? null : 2).
 */
function microcodeRatings(): Map<string, number[] | 'critical'> {
  const rules = JSON.parse(read('src/Optimizer.Core/Catalog/Data/cpu.json')).microcodeRules as { id: string; critical: boolean }[];
  const source = read('src/Optimizer.Core/Findings/Checks/CpuPowerChecks.cs');
  const expression = source.match(/class MicrocodeCheck[\s\S]*?Impact = rule\.Critical \? null : (\d),/);
  if (!expression) throw new Error('MicrocodeCheck no longer rates as Impact = rule.Critical ? null : n');
  return new Map(rules.map((rule) => [rule.id, rule.critical ? 'critical' : [Number(expression[1])]]));
}

/** The board in one language. Fails the build when a check has no rating the site can read. */
export function ratingBoard(lang: Lang): Board {
  const microcode = microcodeRatings();
  const ids = readdirSync(join(root, docsDir(lang)))
    .filter((file) => /^[AFG]\d*\.[^.]+\.md$/.test(file))
    .map((file) => file.slice(0, -3));
  if (ids.length !== counts.checks) throw new Error(`${ids.length} check pages, but the README says ${counts.checks} checks`);

  const byTitle = (a: BoardEntry, b: BoardEntry) => a.title.localeCompare(b.title, lang);
  const rows: BoardRow[] = [5, 4, 3, 2, 1, 0].map((level) => ({ level, checks: [], tweaks: [] }));
  const row = (level: number) => rows.find((r) => r.level === level)!;
  const unrated: UnratedCheck[] = [];

  for (const id of ids) {
    const title = pageTitle(lang, id);
    const fromMicrocode = microcode.get(id);
    if (fromMicrocode === 'critical') {
      unrated.push({ id, title, why: 'critical' });
      continue;
    }
    const levels = (fromMicrocode ?? checkImpacts(id)).slice().sort((a, b) => b - a);
    if (levels.length === 0) {
      // The game access panel shows what anti-cheats need; the app keeps it out of the rating.
      if (id !== 'G.access') throw new Error(`Check ${id} has no rating the site can read`);
      unrated.push({ id, title, why: 'access' });
      continue;
    }
    const [highest, ...lower] = levels;
    row(highest).checks.push({ id, title, also: lower.length > 0 ? lower : undefined });
  }

  const tweaks = tweakImpacts(lang);
  for (const tweak of tweaks) {
    const entry: BoardEntry = { id: tweak.id, title: tweak.title, basis: tweak.basis };
    if (tweak.higher) entry.otherwise = { impact: tweak.impact, reason: tweak.higher.reason };
    const level = tweak.higher?.impact ?? tweak.impact;
    if (level > 5 || level < 0) throw new Error(`Tweak ${tweak.id} rates ${level}, outside 0 to 5`);
    row(level).tweaks.push(entry);
  }
  for (const r of rows) {
    r.checks.sort(byTitle);
    r.tweaks.sort(byTitle);
  }
  unrated.sort((a, b) => a.title.localeCompare(b.title, lang));

  return {
    rows,
    unrated,
    checkCount: ids.length,
    tweakCount: tweaks.length,
    matter: tweaks.filter((tweak) => tweak.impact > 0 || tweak.higher).length,
  };
}
