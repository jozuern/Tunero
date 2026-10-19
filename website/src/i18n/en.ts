// English copy. Same voice as the app: plain words, real examples, no dashes; strong claims only where they are true (DESIGN.md).
import type { Copy } from './types';

const en: Copy = {
  lang: 'en',
  meta: {
    title: 'Tunero: PC optimizer for Windows 11',
    description:
      'PC optimizer for Windows 11, built to be the only one you need. Tunero finds what costs your games frame rate, explains every change with sources and can undo every setting it changes. Free and open source.',
  },
  nav: { download: 'Download', source: 'Source code', support: 'Support', questions: 'Questions', other: 'Deutsch', otherHref: '/de/', skip: 'Skip to content' },
  hero: {
    title: 'Built to be the only PC optimizer you need.',
    lead: 'Tunero scans your PC for what costs your games frame rate and fixes what it can. It is built to replace the tweak tools, debloat scripts, startup managers and cleaners you would otherwise collect. Every change is explained with sources, and every setting it changes is backed up first and can be undone.',
    download: 'Download Tunero.exe',
    winget: 'or with winget',
    copy: 'Copy',
    copied: 'Copied',
    facts: (v) => `Version ${v} for Windows 11 25H2 or newer, x64. In English and German. Free software under the GPL-3.0-only.`,
    changes: 'What changed',
    preview: 'Tunero.exe is code signed from version 1.2.3 on. Windows SmartScreen may still warn on the first start.',
    previewLink: 'Why',
    started: 'The download has started. Windows warns when you start it?',
    startedLink: 'Here is what to do',
    shotCaption: 'The Overview after a scan, on a test PC in a virtual machine.',
    shotAlt:
      'The Overview page of Tunero: the verdict of the scan, the readiness score in one line, the findings with their impact rating, and the start of the recommended changes.',
  },
  impact: {
    title: 'Only problems the scan finds rate 5. No tweak does.',
    body: (n, matter) => [
      'Checks and tweaks share one scale for games, from 0 to 5. The largest gains come from problems the scan finds, such as a monitor below its refresh rate or a processor held at its base clock.',
      `Of the ${n} tweaks, ${matter} can matter for games, some only on some PCs: Restore processor turbo rates 4 where the power plan holds the processor at its base clock, and 0 everywhere else. The app rates each tweak for the PC it runs on and shows the rating next to every check and tweak, as a number and five bars.`,
    ],
    top: 'Problems the scan finds. It fixes them or tells you how.',
    topRow: (n) => `Rated 5: ${n} problems the scan finds`,
    measured: 'measured',
    disputed: 'disputed',
    some: (base) => `on some PCs, otherwise ${base}`,
    zeroNamed: 'Popular gaming tweaks with disputed evidence. Offered, rated 0, and their pages explain the dispute.',
    zeroRest: (n) => `And ${n} more, most of them for privacy, the browser and Windows. They do not promise frame rate.`,
    row: (level, count) => `Rated ${level}: ${count} ${count === 1 ? 'tweak' : 'tweaks'}`,
  },
  scan: {
    title: 'The scan looks for what really costs frame rate',
    body: (n) => [
      `${n} read-only checks look for problems that cost frame rate or smoothness. A check that cannot read a value says unknown and gives no advice.`,
      'A profile decides what is recommended and which impact counts: Gaming, Laptop gaming, Battery, Office, Quiet and cool, or Older PC. It changes nothing by itself.',
    ],
    list: (n) => `The ${n} checks`,
  },
  tweak: {
    title: 'Every change is explained before it happens, and every setting can be undone',
    intro:
      'Each check and each tweak has a page in English and German: what it does, what it writes, why it can help, how solid the evidence is, and the sources. The app shows it next to the switch. This is the page for turning off mouse acceleration:',
    docLabel: 'Explanation page in the app',
    impact: (n) => `Impact ${n} of 5`,
    changes: 'What changes',
    columns: ['Value', 'Now', 'New'],
    caption: 'Now shows the Windows defaults. The app shows the values of your PC.',
    show: ['Summary', 'How it works', 'Evidence', 'Trade-offs & risks'],
    sourcesLabel: 'Sources',
    disputed: {
      intro: (title) => `Where the evidence does not hold up, the page says so. The page for ${title}, a popular tweak:`,
      link: 'Read the whole page',
    },
    stepsTitle: 'What happens when you apply',
    steps: [
      { name: 'Restore point', text: 'Created before the first change, when System Protection is on.' },
      { name: 'Backup', text: 'Each value is saved before it changes.' },
      { name: 'Check', text: 'After applying, each change is checked.' },
      { name: 'Undo', text: 'The Changes page lists every change with Undo, also after a restart or an update of the app.' },
    ],
    body: [
      'After a Windows update, the app shows which changes Windows reset and offers to apply them again. Removed apps and deleted files are the exception, and the app says so before it removes them.',
      'Changes that turn off security features or touch the boot configuration stay behind Expert mode, and none of them is ever recommended automatically. A tweak that is not yet proven on real hardware carries the badge "Not yet tested on a real PC" and is never part of Apply recommended.',
    ],
    shotAlt:
      'The Tweaks page of Tunero with Mouse acceleration off selected: groups of tweaks with their impact rating and switches, and its explanation in the details pane.',
    shotCaption: 'The same tweak on the Tweaks page.',
  },
  left: {
    title: 'What it leaves out, and why',
    intro: 'Some popular tweaks are not offered at all, because they do harm or change nothing. Search for one in the app, and it tells you why.',
    rows: [
      {
        tweak: 'Realtime priority for games',
        why: 'Microsoft warns that a realtime process preempts operating system threads and can make the mouse unresponsive.',
        source: 'https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass',
      },
      {
        tweak: 'Spectre and Meltdown mitigations off',
        why: 'Microsoft measured single-digit slowdowns that most users do not notice on the processors Windows 11 supports. A real security loss for a gain too small to notice.',
        source: 'https://www.microsoft.com/en-us/security/blog/2018/01/09/understanding-the-performance-impact-of-spectre-and-meltdown-mitigations-on-windows-systems/',
      },
      {
        tweak: 'Win32PrioritySeparation = 0x26',
        why: 'It is the value Windows already writes for "Adjust for best performance of: Programs". The tweak changes nothing on Windows 11.',
        source: 'https://learn.microsoft.com/en-us/previous-versions/cc976120(v=technet.10)',
      },
      {
        tweak: 'Turning off IPv6 completely',
        why: 'Microsoft advises against it because some Windows components might stop working. The app offers "Prefer IPv4 over IPv6" instead.',
        source: 'https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-ipv6-in-windows',
      },
      {
        tweak: 'netsh TCP global tweaks',
        why: 'They change every connection of the PC, and no measurement shows a gain in games. Ping in online games depends mostly on the route to the server.',
      },
      {
        tweak: 'Registry cleaning',
        why: 'Keys an uninstaller left behind take no measurable space, and no measurement shows that they slow Windows down. A wrong guess breaks programs that share them.',
      },
    ],
    sourceLink: 'Source',
    all: 'The full list, with sources',
  },
  areas: {
    title: 'Built to replace a folder full of tools',
    intro:
      'Tunero brings together what people otherwise collect from tweak tools, debloat scripts, startup managers, cleaners and system utilities, and leaves out what does nothing. What it leaves to other tools, such as a driver cleaner, is listed in its README. These are its main pages, grouped as in the app.',
    groups: (c) => [
      {
        name: '',
        items: [['Overview', `${c.checks} checks that find what costs frame rate, with a fix or the steps for the BIOS and drivers.`]],
      },
      {
        name: 'Optimize',
        items: [
          ['Tweaks', `${c.tweaks} documented settings for games, privacy, the browser, Windows and Windows Update, each with its evidence.`],
          ['Graphics & DNS', 'NVIDIA driver settings, maximum performance only for the games you pick, and a DNS benchmark.'],
          ['Startup', 'Everything that starts with Windows, with signature checks and snapshots to compare.'],
          ['Services & tasks', 'Start types, with what each service does.'],
        ],
      },
      {
        name: 'Clean up',
        items: [
          ['Remove apps', `${c.apps} reviewed inbox and manufacturer apps, and programs with their own uninstaller.`],
          ['Cleanup', `${c.cleanup} categories of temporary files, caches and leftovers, without following links.`],
          ['Storage', 'A drive as a folder tree beside a treemap, the space by file type, its largest files and duplicate files, read only when you ask. What you delete goes to the Recycle Bin.'],
        ],
      },
      {
        name: 'Maintain and check',
        items: [
          ['Install & update', 'Install and update apps with winget, and see how old the drivers are.'],
          ['Tools', 'Windows features, network fixes, Windows Update repair, SFC and DISM.'],
          ['Health', 'A frame time benchmark with PresentMon, a throttle check and drive health.'],
        ],
      },
      {
        name: '',
        items: [['Changes', 'Every change with Undo, and an export to move your changes to another PC or a fresh install.']],
      },
    ],
  },
  privacy: {
    title: 'It goes online only when you ask it to',
    body: [
      'No telemetry, no account, no ads, no service and no tray icon: it runs when you start it, or once after you sign in if you turn on applying your changes again in Settings. Installing it in Program Files is optional.',
      'The update check is off until you turn it on. A VirusTotal lookup needs your own key and sends file hashes, never files. The DNS benchmark and app installs run when you start them.',
    ],
    link: 'Everything the app sends, and to whom',
  },
  support: {
    title: 'Support Tunero',
    body: [
      'Tunero is free, with no ads, no account and no paid version. If it helps you and you want to give something back, you can support its development on Ko-fi or GitHub Sponsors. Bug reports and ideas help just as much.',
    ],
    kofi: 'Support on Ko-fi',
    sponsors: 'Sponsor on GitHub',
    note: 'Support unlocks nothing: every feature is in the free exe.',
  },
  install: {
    title: 'Get Tunero',
    steps: [
      { name: 'Download', text: 'The exe of the latest release.' },
      { name: 'Check', text: 'Right-click it, Properties > Digital Signatures: it names Open Source Developer Joshua Zürn. Or compare its SHA-256 with the .sha256 file on the release page.' },
      { name: 'Start', text: 'On the first start it offers to install itself in Program Files with a Start menu entry, or to run from where it is.' },
    ],
    managers: 'With a package manager',
    releases: 'All releases',
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
        a: 'Settings > Uninstall offers to undo every change first. Then it removes the exe, its data folder and its Start menu entry, and for an installed copy also its Program Files folder and its entry under Installed apps. Uninstall under Windows Settings > Apps does the same.',
      },
    ],
  },
  footer: {
    license: (v) => `Tunero ${v}. Free software under the GPL-3.0-only.`,
    links: { releases: 'Releases', changelog: 'Changelog', source: 'Source code', privacy: 'Privacy', imprint: 'Legal notice', kofi: 'Support on Ko-fi', sponsors: 'GitHub Sponsors' },
    privacyHref: '/privacy/',
    imprintHref: '/legal/',
  },
};

export default en;
