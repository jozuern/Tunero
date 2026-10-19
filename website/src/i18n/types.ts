import type { Lang } from '../lib/data';

export interface Counts {
  checks: number;
  tweaks: number;
  apps: number;
  cleanup: number;
}

export interface Copy {
  lang: Lang;
  meta: { title: string; description: string };
  nav: { label: string; download: string; source: string; support: string; questions: string; other: string; skip: string };
  hero: {
    title: string;
    lead: string;
    download: string;
    winget: string;
    copy: string;
    copied: string;
    /** One line under the button: version, system, languages, license. */
    facts: (version: string) => string;
    changes: string;
    preview: string;
    previewLink: string;
    /** Shown under the button once a download link was clicked, with a link to the SmartScreen answer. */
    started: string;
    startedLink: string;
    shotCaption: string;
    shotAlt: string;
  };
  impact: {
    title: string;
    body: (tweaks: number, matter: number) => string[];
    /** Over the checks in the top row. */
    top: string;
    topRow: (count: number) => string;
    measured: string;
    disputed: string;
    /** A tweak that rates higher on some PCs, with its rating elsewhere. */
    some: (base: number) => string;
    zeroNamed: string;
    zeroRest: (count: number) => string;
    row: (level: number, count: number) => string;
  };
  scan: { title: string; body: (checks: number) => string[]; list: (checks: number) => string };
  /** One tweak followed end to end: its explanation page with what it writes, the apply steps, the app window. */
  tweak: {
    title: string;
    intro: string;
    docLabel: string;
    /** The tweak's rating, as the app writes it. */
    impact: (level: number) => string;
    /** The app's generated section with the registry values, between the summary and the rest. */
    changes: string;
    columns: [string, string, string];
    caption: string;
    /** The sections of the page shown on the site, in order; the first is followed by the values. */
    show: string[];
    sourcesLabel: string;
    /** Under the page: the evidence a popular tweak's own page gives, where it does not hold up. */
    disputed: { intro: (title: string) => string; link: string };
    stepsTitle: string;
    steps: { name: string; text: string }[];
    body: string[];
    shotAlt: string;
    shotCaption: string;
  };
  left: {
    title: string;
    intro: string;
    rows: { tweak: string; why: string; source?: string }[];
    sourceLink: string;
    all: string;
  };
  areas: { title: string; intro: string; groups: (c: Counts) => { name: string; items: [string, string][] }[] };
  privacy: { title: string; body: string[]; link: string };
  install: { title: string; steps: { name: string; text: string }[]; managers: string; releases: string };
  support: { title: string; body: string[]; kofi: string; sponsors: string; note: string };
  faq: { title: string; items: { id: string; q: string; a: string }[] };
  footer: {
    label: string;
    license: (version: string) => string;
    links: { releases: string; changelog: string; source: string; privacy: string; imprint: string; kofi: string; sponsors: string };
    privacyHref: string;
    imprintHref: string;
  };
}
