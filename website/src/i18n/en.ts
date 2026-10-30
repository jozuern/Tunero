// All English copy of the site. de.ts has the same shape (Strings), so a missing key fails the type check.

/** A paragraph with links in it: plain text and links, in reading order. */
export type Rich = (string | { text: string; href: string; lang?: 'en' | 'de' })[];

/** One fact of "Why trust it", shared by the classic page and the desktop's Explorer window. */
export interface TrustItem {
  title: string;
  body: string;
  /** A command that checks the fact, shown as code after the body. */
  code?: string;
  link?: { label: string; href: string };
}

const repo = 'https://github.com/jozuern/Tunero';

const en = {
  lang: 'en' as const,
  meta: {
    title: 'Tunero, the PC optimizer for Windows 11',
    description:
      'Tunero scans your Windows 11 PC for what slows games down, explains every change with sources and undoes all of it. Free, open source, signed.',
    classicTitle: 'Tunero, the PC optimizer for Windows 11 (classic page)',
  },
  skip: 'Skip to classic page',
  noscript: 'The demo PC needs JavaScript. Everything about Tunero is also on the classic page:',
  /** The skip link on a page that is already the classic page or one of its neighbours. */
  skipContent: 'Skip to content',
  notFound: {
    title: 'Page not found',
    body: 'This address does not exist on tunero.dev.',
    home: 'Go to the start page',
  },
  demo: {
    notSet: 'not set',
    display: 'Display 1',
    refreshWhere: 'Display 1: refresh rate',
    power: {
      maxState: 'Power plan, plugged in: Maximum processor state',
      boostMode: 'Power plan, plugged in: Processor performance boost mode',
      boostOff: 'Disabled',
      boostOn: 'Aggressive',
    },
    startup: ['Game launcher', 'Chat', 'RGB control', 'Cloud sync', 'Updater', 'Music', 'Peripheral tool', 'Screenshot tool', 'Voice chat'],
  },
  classic: {
    heading: 'Tunero',
    claim: 'Built to be the only PC optimizer you need.',
    lead: 'Tunero scans your PC for what costs your games frame rate and fixes what it can. It is built to replace the tweak tools, debloat scripts, startup managers and cleaners you would otherwise collect. Every change is explained with sources, and every setting it changes is backed up first and can be undone.',
    nav: 'Site',
    demo: 'Open the demo PC',
    languages: 'Language',
    /** The language switch: the short name and the full one of each language. */
    english: { short: 'EN', name: 'English' },
    german: { short: 'DE', name: 'Deutsch' },
  },
  download: {
    title: 'Download',
    button: 'Download Tunero.exe',
    facts: (version: string) => `Version ${version} for Windows 11 25H2 or newer, x64. In English and German. Free software under the GPL‑3.0‑only.`,
    changes: 'What changed',
    signed: 'Tunero.exe is code signed from version 1.2.3 on. Windows SmartScreen may still warn on the first start.',
    signedLink: 'Why',
    /** Shown under the button once it was clicked, with a link to the SmartScreen answer. */
    started: 'The download has started. Windows warns when you start it?',
    startedLink: 'Here is what to do',
    firstStart: 'On the first start it offers to install itself in Program Files with a Start menu entry, or to run from where it is.',
    managers: 'With a package manager',
    copy: 'Copy',
    copied: 'Copied',
    releases: 'All releases',
  },
  trust: {
    title: 'Why you can trust it',
    intro: 'Tunero asks for administrator rights, because most settings it changes apply to the whole PC. Here is what stands behind it, and how to check it yourself.',
    items: [
      {
        title: 'Signed by its developer',
        body: 'Tunero.exe is code signed from version 1.2.3 on, with a Certum certificate for open source developers. Windows names the publisher, Open Source Developer Joshua Zürn: right-click the exe, Properties > Digital Signatures, or in PowerShell:',
        code: 'Get-AuthenticodeSignature Tunero.exe',
        link: { label: 'How releases are signed', href: `${repo}#code-signing` },
      },
      {
        title: 'Open source',
        body: 'The app, its tests, its explanation pages and this website are on GitHub. Each release is built by the release workflow on GitHub from a tagged commit, and that can be checked:',
        code: 'gh attestation verify Tunero.exe --repo jozuern/Tunero',
        link: { label: 'Source code', href: repo },
      },
      {
        title: 'Free software under the GPL‑3.0‑only',
        body: 'You can use, study, change and share it. Versions you share, changed or not, must stay under the same license and come with their source code. There is no paid version, and support unlocks nothing.',
        link: { label: 'License', href: `${repo}/blob/main/LICENSE` },
      },
      {
        title: 'No telemetry',
        body: 'No telemetry, no account, no ads, no service and no tray icon. The app goes online only for features you start or turn on: the update check, a VirusTotal lookup with your own key, the DNS benchmark, app installs and the Windows download for install media.',
        link: { label: 'Everything the app sends, and to whom', href: `${repo}/blob/main/docs/PRIVACY.md` },
      },
      {
        title: 'Every setting it changes can be undone',
        body: 'Each setting is backed up before it changes and checked after applying, and a restore point is created before the first change when System Protection is on. The Changes page lists every change with Undo, also after a restart or an update of the app, and Uninstall offers to undo everything first. Removed apps and deleted files are the exception, and the app says so before.',
      },
      {
        title: 'Check the download',
        body: 'Compare the SHA-256 of Tunero.exe with the .sha256 file on the release page. PowerShell prints it with:',
        code: 'Get-FileHash Tunero.exe',
        link: { label: 'Release page', href: `${repo}/releases/latest` },
      },
      {
        title: 'What it leaves out on purpose',
        body: 'Some popular tweaks are not offered at all, because they do harm or change nothing, such as realtime priority for games, turning off the Spectre and Meltdown mitigations, or registry cleaning. Search for one in the app, and it tells you why.',
        link: { label: 'The full list, with sources', href: `${repo}/blob/main/docs/not-included.md` },
      },
    ] satisfies TrustItem[] as TrustItem[],
  },
  board: {
    title: 'Only problems the scan finds rate 5. No tweak does.',
    body: (tweaks: number, matter: number) => [
      'Checks and tweaks share one scale for games, from 0 to 5. The largest gains come from problems the scan finds, such as a monitor below its refresh rate or a processor held at its base clock.',
      `Of the ${tweaks} tweaks, ${matter} can matter for games, some only on some PCs: Restore processor turbo rates 4 where the power plan holds the processor at its base clock, and 0 everywhere else. The app rates each tweak for the PC it runs on and shows the rating next to every check and tweak, as a number and five bars.`,
    ],
    all: (checks: number, tweaks: number) => `Here are all ${checks} checks and all ${tweaks} tweaks, each in the row of its highest rating.`,
    rowLabel: (level: number) => `Rated ${level}`,
    checks: 'Problems the scan finds',
    top: 'It fixes them or tells you how.',
    tweaks: 'Tweaks',
    measured: 'measured',
    disputed: 'disputed',
    /** A tweak that rates higher on some PCs, with its rating elsewhere. */
    some: (base: number) => `on some PCs, otherwise ${base}`,
    /** A check that also gives lower ratings, such as 0 for a laptop on battery. */
    also: (levels: string) => `also ${levels}, depending on what it finds`,
    zeroNamed: 'Popular gaming tweaks with disputed evidence. Offered, rated 0, and their pages explain the dispute.',
    zeroRest: (count: number) => `And ${count} more, most of them for privacy, the browser and Windows. They do not promise frame rate.`,
    unrated: 'Not on the scale',
    access: 'What kernel anti-cheats check before a game starts. Not part of the impact rating.',
    critical: 'Marked Critical instead of rated, because it is about the stability of the processor.',
  },
  faq: {
    title: 'Questions',
    items: [
      {
        id: 'faster',
        q: 'Will it make my games faster?',
        a: 'When the scan finds a real problem, often noticeably: a monitor stuck at 60 Hz, memory without its XMP or EXPO profile, or a processor held at its base clock costs a lot of frame rate, and Tunero fixes it or tells you how. Most tweaks are about privacy, the browser and Windows and are rated 0 for games; the ones that can matter are rated for your PC, and each page says how solid the evidence is. The Health page has a frame time benchmark, so you can measure the difference in your own games.',
      },
      {
        id: 'smartscreen',
        q: 'Why does Windows warn when I start it?',
        a: 'SmartScreen warns about programs that not enough people have run yet, signed or not. From 1.2.3 on Tunero.exe is signed, so More info names the publisher, Open Source Developer Joshua Zürn, instead of Unknown publisher, and later releases signed with the same certificate can build on its reputation. While it warns, click More info, then Run anyway. To check the download, look at Properties > Digital Signatures, or compare its SHA-256 with the .sha256 file on the release page.',
      },
      {
        id: 'smartappcontrol',
        q: 'What if Smart App Control blocks it?',
        a: 'Smart App Control is stricter than SmartScreen: it blocks unsigned apps that Microsoft does not know yet, and its message has no Run anyway button. It lets apps with a valid signature run unless Microsoft rates them unsafe, so it should not stop Tunero 1.2.3 or newer; if it blocked an older version, get the current release. You can also turn it off in Windows Security > App & browser control > Smart App Control settings; with current Windows 11 updates you can turn it on again there.',
      },
      {
        id: 'admin',
        q: 'Why does it need administrator rights?',
        a: 'Most settings it changes apply to the whole PC. Its data folder is locked to administrators.',
      },
      {
        id: 'home',
        q: 'Does it work on Windows 11 Home?',
        a: 'Yes. Some tweaks that rely on Group Policy are offered only on Pro, Enterprise and Education. Home has been tested less than Pro so far.',
      },
      {
        id: 'remove',
        q: 'Can I remove it completely?',
        a: 'Settings > Uninstall offers to undo every change first. Then it removes the exe, its data folder and its Start menu entry, and for an installed copy also its Program Files folder and its entry under Installed apps. Uninstall under Windows Settings > Apps does the same. A copy from Chocolatey or Scoop is removed with choco uninstall tunero or scoop uninstall tunero.',
      },
    ],
  },
  footer: {
    label: 'Project links',
    license: 'Free software under the GPL‑3.0‑only.',
    version: (version: string) => `Tunero ${version}.`,
    links: {
      releases: 'Releases',
      changelog: 'Changelog',
      source: 'Source code',
      kofi: 'Support on Ko-fi',
      sponsors: 'GitHub Sponsors',
      privacy: 'Privacy',
      legal: 'Legal notice',
    },
  },
  privacy: {
    title: 'Privacy',
    metaTitle: 'Privacy, Tunero',
    description: 'Privacy policy for tunero.dev.',
    intro: [
      'This policy covers the website tunero.dev. What the Tunero app itself sends is in the ',
      { text: 'privacy policy of the app', href: `${repo}/blob/main/docs/PRIVACY.md` },
      '.',
    ] satisfies Rich as Rich,
    controller: 'Controller',
    email: 'Email',
    sections: (email: string): { heading: string; paragraphs: Rich[] }[] => [
      {
        heading: 'In short',
        paragraphs: [
          ['This website sets no cookies, uses no analytics and no ads, and loads no fonts, scripts or images from other servers. It is made of static files.'],
          ['The demo PC on the start page keeps the theme you pick, whether you turned its animation effects and its sound on or off, whether you have seen its start animation, and what you changed on it in the storage of your browser. That stays in your browser and is never sent anywhere. Storing it needs no consent under § 25 (2) no. 2 TDDDG, because the demo needs it to work the way you set it; you can delete it at any time in the site data of your browser.'],
        ],
      },
      {
        heading: 'Hosting on GitHub Pages',
        paragraphs: [
          [
            'The website is hosted on GitHub Pages, a service of GitHub, Inc., 88 Colin P. Kelly Jr. St., San Francisco, CA 94107, USA. When you open a page, GitHub processes your IP address to deliver it and, by its own account, stores it for security purposes. The legal basis is Art. 6(1)(f) GDPR: a legitimate interest in providing the website securely and reliably. GitHub participates in the EU-U.S. Data Privacy Framework and also uses the European Commission\'s standard contractual clauses. See the ',
            { text: 'GitHub Privacy Statement', href: 'https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement' },
            '.',
          ],
        ],
      },
      {
        heading: 'Domain and email at Cloudflare',
        paragraphs: [
          [
            `The domain tunero.dev is managed through Cloudflare, Inc. Cloudflare answers the DNS queries for the domain but does not deliver the pages. When you write to ${email}, Cloudflare Email Routing forwards the message to my mailbox. I use what you send only to answer you (Art. 6(1)(b) and (f) GDPR) and delete the message once it is dealt with and no retention duty applies. See the `,
            { text: 'Cloudflare Privacy Policy', href: 'https://www.cloudflare.com/privacypolicy/' },
            '.',
          ],
        ],
      },
      {
        heading: 'Links to other sites',
        paragraphs: [
          ['The download, the source code and the releases are on GitHub, and support goes through Ko-fi. When you follow one of these links, that provider\'s privacy policy applies.'],
        ],
      },
      {
        heading: 'Your rights',
        paragraphs: [
          [
            `You have the right of access, rectification, erasure and restriction of processing, the right to data portability, and the right to object to processing based on legitimate interest (Art. 15 to 21 GDPR). Write to ${email}. You can also complain to a data protection authority, for example the `,
            { text: 'State Commissioner for Data Protection and Freedom of Information Baden-Württemberg', href: 'https://www.baden-wuerttemberg.datenschutz.de/' },
            '.',
          ],
        ],
      },
    ],
  },
  legal: {
    title: 'Legal notice',
    metaTitle: 'Legal notice, Tunero',
    description: 'Legal notice (Impressum) for tunero.dev.',
    intro: [
      'Information under § 5 of the German Digital Services Act (DDG). The ',
      { text: 'German version', href: '/de/impressum/', lang: 'de' },
      ' is the binding one.',
    ] satisfies Rich as Rich,
    operator: 'Operator',
    contact: 'Contact',
    email: 'Email',
    responsible: 'Responsible for the content under § 18 (2) MStV',
  },
};

export type Strings = Omit<typeof en, 'lang'> & { lang: 'en' | 'de' };
export default en;
