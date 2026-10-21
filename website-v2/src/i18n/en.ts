// All English copy of the site. de.ts has the same shape (Strings), so a missing key fails the type check.
const en = {
  lang: 'en' as const,
  meta: {
    title: 'Tunero, the PC optimizer for Windows 11',
    description:
      'Tunero scans your Windows 11 PC for what slows games down, explains every change with sources and undoes all of it. Free, open source, signed.',
    classicTitle: 'Tunero, the PC optimizer for Windows 11 (classic page)',
  },
  skip: 'Skip to classic page',
  notFound: {
    title: 'Page not found',
    body: 'This address does not exist on tunero.dev.',
    home: 'Go to the start page',
  },
  classic: {
    heading: 'Tunero',
  },
};

export type Strings = Omit<typeof en, 'lang'> & { lang: 'en' | 'de' };
export default en;
