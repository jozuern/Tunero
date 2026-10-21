// The demo PC: what the scan finds on it and what Tunero can change there, each with the desktop effect it drives.
// Titles, ratings, summaries and registry values come from the catalog; an id that no longer exists fails the build.
import { checkImpacts, checkTitles, doc, inboxApp, tweak, tweakCategory, tweakImpactWhen, tweakTitle, type CheckStatus, type TweakAction } from './repo.ts';
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
  /** True for a registry value, which the dive can show. */
  registry: boolean;
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
  /** Why the rating is higher on the demo PC than on most PCs, in the app's words. */
  ratingReason?: string;
  /** measured, situational or disputed for a tweak; empty for the rest. */
  basis: string;
  writes: Write[];
  effect: Effect;
  /** The category label of a tweak, as the app groups it; empty for the rest. */
  category?: string;
}

export interface DemoFinding extends DemoItem {
  /** apply: Fix changes it; tweak: Fix turns on the tweak that fixes it; startup: Fix opens the startup list. */
  fix: 'apply' | 'tweak' | 'startup';
  /** The tweak behind a `tweak` fix. */
  tweak?: string;
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
 * The scenario. The two problems are the kind the scan rates highest, and turbo restore carries the rating the catalog
 * gives it on a PC whose power plan blocks turbo, which this one does. No tweak here is one Microsoft supports only on Enterprise and Education or whose effect it has not
 * confirmed; two are documented for Pro and up, so the demo PC is Windows 11 Pro.
 */
export const scenario = {
  findings: [
    { id: 'F1.refresh', status: 'Problem' as CheckStatus, impact: 5, fix: 'apply' as const, effect: 'refresh-rate' as Effect },
    { id: 'F6.turbo', status: 'Problem' as CheckStatus, impact: 5, fix: 'tweak' as const, tweak: 'power.turboRestore', effect: 'none' as Effect },
    { id: 'F16.startupPrograms', status: 'Info' as CheckStatus, impact: 1, fix: 'startup' as const, effect: 'tray' as Effect },
  ],
  tweaks: [
    { id: 'power.turboRestore', effect: 'none', ratedFor: 'finding.F6.turbo.problem' },
    { id: 'privacy.suggestionsOff', effect: 'tips-toasts' },
    { id: 'personalize.startRecommendedOff', effect: 'start-recommended' },
    { id: 'privacy.searchHighlightsOff', effect: 'search-highlights' },
    { id: 'privacy.feedbackNotificationsOff', effect: 'feedback-toast' },
    { id: 'explorer.syncProviderNotificationsOff', effect: 'explorer-sync-ad' },
    { id: 'focus.startAccountNotificationsOff', effect: 'start-account' },
  ] as { id: string; effect: Effect; ratedFor?: string }[],
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
/** The demo PC's power plan keeps the processor below its turbo clock: both settings F6.turbo reads are off. */
const power = { maxState: '99 %' };

/** What a catalog action changes on the demo PC, as the app's detail pane lists it. */
function demoWrite(id: string, a: TweakAction, copy: ReturnType<typeof t>['demo']): Write {
  if (a.type === 'registry' && a.path && a.name)
    return { where: `${a.hive === 'user' ? 'HKCU' : 'HKLM'}\\${a.path}\\${a.name}`, registry: true, from: demoFrom(a.path, a.value, copy.notSet), to: String(a.value) };
  if (a.type === 'powerSetting' && a.setting === 'PROCTHROTTLEMAX')
    return { where: copy.power.maxState, registry: false, from: power.maxState, to: `${a.ac} %` };
  if (a.type === 'powerSetting' && a.setting === 'PERFBOOSTMODE' && a.ac === 2)
    return { where: copy.power.boostMode, registry: false, from: copy.power.boostOff, to: copy.power.boostOn };
  throw new Error(`${id}: the demo cannot show a ${a.type} action`);
}

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
      writes: f.id === 'F1.refresh' ? [{ where: copy.refreshWhere, registry: false, from: `${refresh.current} Hz`, to: `${refresh.max} Hz` }] : [],
      effect: f.effect,
      fix: f.fix,
      tweak: 'tweak' in f ? f.tweak : undefined,
    };
  });
  const tweaks = s.tweaks.map(({ id, effect, ratedFor }): DemoItem => {
    const catalog = tweak(id);
    const rated = ratedFor ? tweakImpactWhen(id, ratedFor, lang) : undefined;
    return {
      id,
      title: tweakTitle(id, lang),
      summary: doc(id, lang).summary,
      impact: rated?.impact ?? catalog.impact,
      ratingReason: rated?.reason,
      basis: catalog.basis,
      writes: catalog.actions.map((a) => demoWrite(id, a, copy)),
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
  // A finding fixed by a tweak writes what that tweak writes.
  for (const f of findings) if (f.tweak) f.writes = tweaks.find((x) => x.id === f.tweak)!.writes;
  return { checksRun: titles.length, checkTitles: titles, findings, tweaks, apps, startup };
}
