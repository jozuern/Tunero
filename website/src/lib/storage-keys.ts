// The names the site stores under, in one place: the inline scripts that run before the first paint (Base.astro,
// DesktopPage.astro, through define:vars) and the desktop's modules read and write the same ones. The privacy pages list
// what is kept under each.
export const storageKeys = {
  /** localStorage: light or dark, chosen in the demo's Settings. */
  theme: 'theme',
  /** localStorage: on or off, the demo's Animation effects. */
  motion: 'motion',
  /** localStorage: on or off, the tray speaker. */
  sound: 'sound',
  /** sessionStorage: the demo PC's state for this visit (lib/demo-state.ts). */
  demo: 'tunero-demo',
} as const;
