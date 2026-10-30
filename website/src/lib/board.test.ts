import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ratingBoard } from './board.ts';
import { counts, tweakImpacts } from './repo.ts';
import en from '../i18n/en.ts';
import de from '../i18n/de.ts';

for (const lang of ['en', 'de'] as const) {
  test(`the board (${lang}) shows every check and every tweak exactly once`, () => {
    const board = ratingBoard(lang);
    const checks = [...board.rows.flatMap((row) => row.checks.map((entry) => entry.id)), ...board.unrated.map((entry) => entry.id)];
    const tweaks = board.rows.flatMap((row) => row.tweaks.map((entry) => entry.id));
    assert.equal(checks.length, counts.checks);
    assert.equal(new Set(checks).size, counts.checks);
    assert.equal(tweaks.length, counts.tweaks);
    assert.equal(new Set(tweaks).size, counts.tweaks);
    assert.deepEqual(board.rows.map((row) => row.level), [5, 4, 3, 2, 1, 0]);
  });
}

test('checks sit in the row of their highest rating, with the lower ones noted', () => {
  const board = ratingBoard('en');
  const refresh = board.rows[0].checks.find((entry) => entry.id === 'F1.refresh');
  assert.deepEqual(refresh?.also, [0]);
  const xmp = board.rows.find((row) => row.level === 3)!.checks.find((entry) => entry.id === 'A.xmp');
  assert.deepEqual(xmp?.also, [1]);
  assert.deepEqual(board.unrated.map((entry) => [entry.id, entry.why]).sort(), [['A.raptorlake', 'critical'], ['G.access', 'access']]);
  assert.ok(board.rows.find((row) => row.level === 2)!.checks.some((entry) => entry.id === 'A.arrowlake'));
});

// The board's title and text state facts the catalog decides, so a test fails when they stop being true.
test('no tweak rates 5, and Restore processor turbo rates 4 on some PCs and 0 elsewhere', () => {
  const board = ratingBoard('en');
  assert.equal(board.rows[0].tweaks.length, 0);
  assert.ok(en.board.title.includes('No tweak') && de.board.title.includes('keinen Tweak'));
  const turbo = tweakImpacts('en').find((tweak) => tweak.id === 'power.turboRestore');
  assert.equal(turbo?.impact, 0);
  assert.equal(turbo?.higher?.impact, 4);
});

test('the trust items link only to the repository', () => {
  for (const copy of [en, de]) {
    assert.ok(copy.trust.items.length >= 7);
    for (const item of copy.trust.items)
      if (item.link) assert.ok(item.link.href.startsWith('https://github.com/jozuern/Tunero'), item.link.href);
  }
});
