import en, { type Strings } from './en.ts';
import de from './de.ts';

export type Lang = 'en' | 'de';
export type { Strings };

export const t = (lang: Lang): Strings => (lang === 'de' ? de : en);

/** The address of a page in a language: `path` is the English one ("/", "/classic/"). */
export const localPath = (lang: Lang, path: string) => (lang === 'de' ? `/de${path}` : path);
