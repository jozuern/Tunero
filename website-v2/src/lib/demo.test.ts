import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildDemo, scenario } from './demo.ts';
import { checkImpacts, inboxAppReinstall, tweak, tweakFixes } from './repo.ts';

for (const lang of ['en', 'de'] as const) {
  test(`the scenario resolves in ${lang}`, () => {
    const demo = buildDemo(lang);
    assert.equal(demo.findings.length, 3);
    assert.equal(demo.tweaks.length, 7);
    assert.equal(demo.apps.length, 6);
    assert.equal(demo.startup.length, 9);
    for (const item of [...demo.findings, ...demo.tweaks, ...demo.apps, ...demo.startup]) assert.ok(item.title, item.id);
  });
}

test('finding ratings are ones the check code can give', () => {
  for (const f of buildDemo('en').findings) assert.ok(checkImpacts(f.id).includes(f.impact), f.id);
});

test('tweak ratings and writes come from the catalog', () => {
  for (const t of buildDemo('en').tweaks) {
    if (t.id !== 'power.turboRestore') assert.equal(t.impact, tweak(t.id).impact, t.id);
    assert.equal(t.writes.length, tweak(t.id).actions.length, t.id);
  }
});

test('the turbo finding is fixed by the tweak the catalog says fixes it', () => {
  const demo = buildDemo('en');
  const turbo = demo.findings.find((f) => f.id === 'F6.turbo')!;
  assert.equal(turbo.fix, 'tweak');
  assert.equal(turbo.tweak, 'power.turboRestore');
  assert.ok(tweakFixes('power.turboRestore').includes('F6.turbo'));
  assert.ok(demo.tweaks.some((t) => t.id === 'power.turboRestore'));
});

test('on the demo PC turbo restore takes the rating the catalog gives it when turbo is blocked', () => {
  const restore = buildDemo('en').tweaks.find((t) => t.id === 'power.turboRestore')!;
  assert.equal(tweak('power.turboRestore').impact, 0);
  assert.equal(restore.impact, 4);
  assert.match(restore.ratingReason ?? '', /boost is blocked on this PC/);
});

test('power plan values are shown in words, without a registry dive', () => {
  const restore = buildDemo('en').tweaks.find((t) => t.id === 'power.turboRestore')!;
  assert.deepEqual(
    restore.writes.map((w) => [w.where, w.from, w.to, w.registry]),
    [
      ['Power plan, plugged in: Maximum processor state', '99 %', '100 %', false],
      ['Power plan, plugged in: Processor performance boost mode', 'Disabled', 'Aggressive', false],
    ],
  );
  const suggestions = buildDemo('en').tweaks.find((t) => t.id === 'privacy.suggestionsOff')!;
  assert.equal(suggestions.writes[0].registry, true);
});

test('writes show the demo PC value before and the catalog value after', () => {
  const tweaks = buildDemo('en').tweaks;
  const suggestions = tweaks.find((t) => t.id === 'privacy.suggestionsOff')!;
  assert.equal(suggestions.writes[0].where, 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager\\SubscribedContent-338389Enabled');
  assert.deepEqual([suggestions.writes[0].from, suggestions.writes[0].to], ['1', '0']);
  const recommended = tweaks.find((t) => t.id === 'personalize.startRecommendedOff')!;
  assert.match(recommended.writes[0].where, /^HKLM\\SOFTWARE\\Policies\\/);
  assert.deepEqual([recommended.writes[0].from, recommended.writes[0].to], ['not set', '1']);
});

test('no demo tweak is limited to Enterprise and Education or unconfirmed by Microsoft', () => {
  for (const t of buildDemo('en').tweaks) assert.doesNotMatch(t.summary, /only on Enterprise and Education|not confirmed/, t.id);
});

test('an unknown id fails the build', () => {
  assert.throws(() => buildDemo('en', { ...scenario, tweaks: [...scenario.tweaks, { id: 'nothing.here', effect: 'none' }] }));
});

test('tweaks carry their category label', () => {
  const suggestions = buildDemo('de').tweaks.find((t) => t.id === 'privacy.suggestionsOff')!;
  assert.equal(suggestions.category, 'Datenschutz');
});

test('every demo app can be installed again from the Microsoft Store', () => {
  for (const id of scenario.apps) assert.equal(inboxAppReinstall(id), 'store', id);
});
