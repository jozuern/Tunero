// The demo desktop's English copy. Plain strings only (it is passed to the browser), with {0} for numbers or names.
const desktopEn = {
  watermark: ['Demo PC, Windows 11 Pro', 'Nothing here touches your computer.'],
  classicPage: 'Classic page',
  install: 'Install Tunero',
  installTitle: 'Download Tunero.exe, version {0}',
  start: 'Start',
  searchPlaceholder: 'Search for apps, settings and documents',
  searchHighlight: 'Trending today',
  pinned: 'Pinned',
  recommended: 'Recommended',
  recommendedItems: ['Welcome to Windows', 'Explore new apps for you', 'Screenshot 2026-10-12.png', 'Holiday photos'],
  user: 'Demo user',
  accountNag: 'Back up your PC. Sign in to keep your files safe.',
  power: 'Power',
  icons: { tunero: 'Tunero', trust: 'Why trust it', notepad: 'Changelog', settings: 'Settings', explorer: 'File Explorer' },
  startHere: 'Start here',
  toasts: {
    tips: { app: 'Windows', title: 'Get even more out of Windows', body: 'Finish setting up your device and discover what is new.' },
    feedback: { app: 'Feedback', title: 'Rate your experience', body: 'How likely are you to recommend Windows to a friend?' },
    cloud: { app: 'Cloud sync', title: 'Your storage is almost full', body: 'Upgrade your plan to keep syncing.' },
    updater: { app: 'Updater', title: 'Updates are ready', body: 'Restart three apps to finish updating.' },
    launcher: { app: 'Game launcher', title: 'Weekend sale', body: 'Up to 80 percent off. Ends Sunday.' },
  },
  dismiss: 'Dismiss',
  window: { minimize: 'Minimize', maximize: 'Maximize', restore: 'Restore', close: 'Close' },
  trayMore: 'Show hidden icons',
  clockLabel: 'Clock',
  network: 'Network',
  volume: 'Volume',
};

export type DesktopStrings = typeof desktopEn;
export default desktopEn;
