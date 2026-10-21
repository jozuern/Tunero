import { test } from 'node:test';
import assert from 'node:assert/strict';
import { counts, checkTitles, checkImpacts, tweak, doc, inboxApp, changelog } from './repo.ts';

test('the check pages match the README count', () => {
  assert.equal(checkTitles('en').length, counts.checks);
  assert.equal(checkTitles('de').length, counts.checks);
});

test('checkImpacts reads every rating a check can give', () => {
  assert.ok(checkImpacts('F1.refresh').includes(5));
  assert.deepEqual(checkImpacts('F16.startupPrograms'), [1]);
  assert.throws(() => checkImpacts('F99.nothing'));
});

test('tweak reads rating and actions from the catalog', () => {
  const t = tweak('privacy.suggestionsOff');
  assert.equal(t.impact, 0);
  assert.equal(t.basis, 'situational');
  assert.ok(t.actions.length >= 7);
  assert.throws(() => tweak('nothing.here'));
});

test('doc picks the summary block of a status and fills placeholders', () => {
  assert.match(doc('F16.startupPrograms', 'en', { status: 'Info', vars: { count: '9' } }).summary, /^9 programs from other vendors/);
  assert.match(doc('F16.startupPrograms', 'de', { status: 'Info', vars: { count: '9' } }).summary, /^9 Programme/);
  const refresh = doc('F1.refresh', 'en', { status: 'Problem', vars: { display: 'Display 1', current: '60', max: '144' } });
  assert.match(refresh.summary, /^Display 1 runs at 60 Hz, but Windows offers up to 144 Hz/);
  assert.doesNotMatch(refresh.summary, /\{\{|\[\d\]/);
});

test('doc of a tweak page has title, summary and sources', () => {
  const d = doc('privacy.suggestionsOff', 'en');
  assert.ok(d.title.length > 0);
  assert.ok(d.summary.length > 0 && d.summary.length <= 200);
  assert.ok(d.sources.length > 0);
});

test('inboxApp gives the display title in both languages', () => {
  assert.equal(inboxApp('Microsoft.BingWeather', 'en'), 'Weather');
  assert.equal(inboxApp('Microsoft.BingWeather', 'de'), 'Wetter');
});

test('changelog gives the newest versions first', () => {
  const entries = changelog(2);
  assert.equal(entries.length, 2);
  assert.match(entries[0].version, /^\d+\.\d+\.\d+$/);
  assert.ok(entries[0].body.length > 0);
});
