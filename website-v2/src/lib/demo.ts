// The demo PC: what the scan finds on it and what Tunero can change there, each with the desktop effect it drives.
// Titles, ratings, summaries and registry values come from the catalog; an id that no longer exists fails the build.
import { checkImpacts, checkTitles, doc, inboxApp, tweak, tweakCategory, tweakTitle, type CheckStatus } from './repo.ts';
import { t, type Lang } from '../i18n/index.ts';

/** Something the visitor sees on the demo desktop until the changes behind it are applied. */
export type Effect =
  | 'tips-toasts'
  | 'start-recommended'
  | 'search-highlights'
  | 'feedback-toast'
  | 'explorer-sync-ad'
  | 'start-account'
  | 'tray'
  | 'start-app'
  | 'refresh-rate'
  | 'none';

export interface Write {
  /** A registry value (HKCU\...\Name) or a setting described in words. */
  where: string;
  /** The value on the demo PC before the change. */
  from: string;
  to: string;
}

export interface DemoItem {
  id: string;
  title: string;
  summary: string;
  /** Rating for games, 0 to 5; the app shows it for findings and tweaks only. */
  impact: number;
  /** measured, situational or disputed for a tweak; empty for the rest. */
  basis: string;
  writes: Write[];
  effect: Effect;
  /** The category label of a tweak, as the app groups it; empty for the rest. */
  category?: string;
}

export interface DemoFinding extends DemoItem {
  /** apply: Fix changes it; startup: Fix opens the startup list. */
  fix: 'apply' | 'startup';
}

export interface Demo {
  checksRun: number;
  /** Check titles the scan shows passing by. */
  checkTitles: string[];
  findings: DemoFinding[];
  tweaks: DemoItem[];
  apps: DemoItem[];
  startup: DemoItem[];
}

/**
 * The scenario. No tweak here is one Microsoft supports only on Enterprise and Education or whose effect it has not
 * confirmed; two are documented for Pro and up, so the demo PC is Windows 11 Pro.
 */
export const scenario = {
  findings: [
    { id: 'F1.refresh', status: 'Problem' as CheckStatus, impact: 5, fix: 'apply' as const, effect: 'refresh-rate' as Effect },
    { id: 'F16.startupPrograms', status: 'Info' as CheckStatus, impact: 1, fix: 'startup' as const, effect: 'tray' as Effect },
  ],
  tweaks: [
    { id: 'privacy.suggestionsOff', effect: 'tips-toasts' },
    { id: 'personalize.startRecommendedOff', effect: 'start-recommended' },
    { id: 'privacy.searchHighlightsOff', effect: 'search-highlights' },
    { id: 'privacy.feedbackNotificationsOff', effect: 'feedback-toast' },
    { id: 'explorer.syncProviderNotificationsOff', effect: 'explorer-sync-ad' },
    { id: 'focus.startAccountNotificationsOff', effect: 'start-account' },
  ] as { id: string; effect: Effect }[],
  apps: [
    'Microsoft.BingNews',
    'Microsoft.BingWeather',
    'Microsoft.MicrosoftOfficeHub',
    'Microsoft.MicrosoftSolitaireCollection',
    'Clipchamp.Clipchamp',
    'Microsoft.BingSearch',
  ],
};

const refresh = { current: '60', max: '144' };

/** The value a registry action replaces on the demo PC: policies are not set there, switches are on. */
function demoFrom(path: string, value: unknown, notSet: string): string {
  if (/\\Policies\\/i.test(path)) return notSet;
  if (value === 0) return '1';
  if (value === 1) return '0';
  return notSet;
}

export function buildDemo(lang: Lang, s: typeof scenario = scenario): Demo {
  const copy = t(lang).demo;
  const findings = s.findings.map((f): DemoFinding => {
    if (!checkImpacts(f.id).includes(f.impact)) throw new Error(`${f.id} cannot rate ${f.impact}`);
    const vars: Record<string, string> =
      f.id === 'F1.refresh'
        ? { display: copy.display, ...refresh }
        : { count: String(copy.startup.length) };
    const page = doc(f.id, lang, { status: f.status, vars });
    return {
      id: f.id,
      title: page.title.replace(/\s*\{\{\w+\}\}/g, ''),
      summary: page.summary,
      impact: f.impact,
      basis: '',
      writes: f.id === 'F1.refresh' ? [{ where: copy.refreshWhere, from: `${refresh.current} Hz`, to: `${refresh.max} Hz` }] : [],
      effect: f.effect,
      fix: f.fix,
    };
  });
  const tweaks = s.tweaks.map(({ id, effect }): DemoItem => {
    const catalog = tweak(id);
    return {
      id,
      title: tweakTitle(id, lang),
      summary: doc(id, lang).summary,
      impact: catalog.impact,
      basis: catalog.basis,
      writes: catalog.actions.map((a) => {
        if (a.type !== 'registry' || !a.path || !a.name) throw new Error(`${id}: the demo shows registry values only`);
        return { where: `${a.hive === 'user' ? 'HKCU' : 'HKLM'}\\${a.path}\\${a.name}`, from: demoFrom(a.path, a.value, copy.notSet), to: String(a.value) };
      }),
      effect,
      category: tweakCategory(id, lang),
    };
  });
  const apps = s.apps.map((name): DemoItem => ({
    id: name,
    title: inboxApp(name, lang),
    summary: '',
    impact: 0,
    basis: '',
    writes: [],
    effect: 'start-app',
  }));
  const startup = copy.startup.map((name, i): DemoItem => ({
    id: `startup-${i + 1}`,
    title: name,
    summary: '',
    impact: 0,
    basis: '',
    writes: [],
    effect: 'tray',
  }));
  const titles = checkTitles(lang);
  return { checksRun: titles.length, checkTitles: titles, findings, tweaks, apps, startup };
}
