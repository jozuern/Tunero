// The latest release, which is what the download link gets. Read once per build; GITHUB_TOKEN raises the rate limit.
import { changelog } from './repo.ts';

type Fetcher = (url: string, init: RequestInit) => Promise<Response>;

/**
 * The version of the latest release and the size of its Tunero.exe in MB as Windows counts them (1024 x 1024 bytes).
 * Offline, the newest version heading of the changelog stands in: the project file is raised by every commit and runs
 * ahead.
 */
export function latestRelease(fetcher?: Fetcher): Promise<{ version: string; sizeMb?: number }> {
  // Every page that shows the version asks; GitHub is asked once per build, so all pages show the same release.
  if (fetcher) return readRelease(fetcher);
  cached ??= readRelease(fetch);
  return cached;
}

let cached: Promise<{ version: string; sizeMb?: number }> | undefined;

async function readRelease(fetcher: Fetcher): Promise<{ version: string; sizeMb?: number }> {
  try {
    const headers: Record<string, string> = { Accept: 'application/vnd.github+json', 'User-Agent': 'tunero-website' };
    if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
    const res = await fetcher('https://api.github.com/repos/jozuern/Tunero/releases/latest', { headers, signal: AbortSignal.timeout(10000) });
    if (!res.ok) throw new Error(`GitHub answered ${res.status}`);
    const release = await res.json();
    const exe = (release.assets as { name: string; size: number }[]).find((a) => a.name === 'Tunero.exe');
    const version = (release.tag_name as string).replace(/^v/, '');
    return exe ? { version, sizeMb: Math.round(exe.size / 1048576) } : { version };
  } catch (error) {
    // Shown in the build log, so a failing GitHub API does not go unnoticed behind the stand-in.
    console.warn(`latestRelease: ${error instanceof Error ? error.message : error}; the changelog's newest version stands in`);
    return { version: changelog(1)[0].version };
  }
}
