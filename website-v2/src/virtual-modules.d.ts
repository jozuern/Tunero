// The modules astro.config.mjs makes at build time (see lib/desktop-data.ts).
declare module 'virtual:desktop-data/*' {
  const data: import('./lib/desktop-data.ts').DesktopData;
  export default data;
}
