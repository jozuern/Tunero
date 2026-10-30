import { test } from 'node:test';
import assert from 'node:assert/strict';
import { latestRelease } from './release.ts';
import { changelog } from './repo.ts';

test('offline, the newest changelog version stands in', async () => {
  const release = await latestRelease(async () => {
    throw new Error('offline');
  });
  assert.deepEqual(release, { version: changelog(1)[0].version });
});

test('the latest release gives version and size of Tunero.exe in MB', async () => {
  const body = { tag_name: 'v9.8.7', assets: [{ name: 'Tunero.exe', size: 75 * 1048576 }] };
  const release = await latestRelease(async () => new Response(JSON.stringify(body)));
  assert.deepEqual(release, { version: '9.8.7', sizeMb: 75 });
});
