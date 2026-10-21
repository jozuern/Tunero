import { test } from 'node:test';
import assert from 'node:assert/strict';
import { initial, apply, undo, undoAll, undoable, clutter, isActive, serialize, deserialize, allChanges, type Change } from './demo-state.ts';
import type { Demo } from './demo.ts';

const item = (id: string, effect: string) => ({ id, title: id, summary: '', impact: 0, basis: 'situational', writes: [], effect });
const demo = {
  checksRun: 50,
  findings: [{ ...item('F1.refresh', 'refresh-rate'), fix: 'apply' }, { ...item('F16.startupPrograms', 'tray'), fix: 'startup' }],
  tweaks: [item('a', 'tips-toasts'), item('b', 'start-recommended')],
  apps: [item('app1', 'start-app')],
  startup: [item('s1', 'tray'), item('s2', 'tray')],
} as unknown as Demo;

const tweakA: Change = { kind: 'tweak', id: 'a' };
const tweakB: Change = { kind: 'tweak', id: 'b' };

test('allChanges holds the fixable finding, tweaks, apps and startup entries, not the startup finding', () => {
  assert.deepEqual(allChanges(demo).map((c) => `${c.kind}:${c.id}`), ['finding:F1.refresh', 'tweak:a', 'tweak:b', 'app:app1', 'startup:s1', 'startup:s2']);
});

test('applying twice keeps one entry', () => {
  const state = apply(apply(initial(), [tweakA]), [tweakA]);
  assert.deepEqual(state.applied, [tweakA]);
});

test('undo removes one change', () => {
  assert.deepEqual(undo(apply(initial(), [tweakA, tweakB]), tweakA).applied, [tweakB]);
});

test('undoAll returns the steps newest first and clears applied', () => {
  const result = undoAll(apply(apply(initial(), [tweakA]), [tweakB]));
  assert.deepEqual(result.steps, [tweakB, tweakA]);
  assert.deepEqual(result.state.applied, []);
});

test('applying during a rewind leaves exactly the new apply', () => {
  const rewinding = undoAll(apply(initial(), [tweakA, tweakB])).state;
  assert.deepEqual(apply(rewinding, [tweakB]).applied, [tweakB]);
});

test('clutter falls from 1 to 0 with every apply', () => {
  let state = initial();
  let last = clutter(state, demo);
  assert.equal(last, 1);
  for (const change of allChanges(demo)) {
    state = apply(state, [change]);
    const now = clutter(state, demo);
    assert.ok(now < last);
    last = now;
  }
  assert.equal(last, 0);
});

test('an effect shows while any of its changes is not applied', () => {
  const s1: Change = { kind: 'startup', id: 's1' };
  const s2: Change = { kind: 'startup', id: 's2' };
  assert.equal(isActive(initial(), demo, 'tray'), true);
  assert.equal(isActive(apply(initial(), [s1]), demo, 'tray'), true);
  assert.equal(isActive(apply(initial(), [s1, s2]), demo, 'tray'), false);
});

test('deserialize falls back to the initial state', () => {
  assert.deepEqual(deserialize(null), initial());
  assert.deepEqual(deserialize('garbage'), initial());
  assert.deepEqual(deserialize('{"applied":[{"kind":"evil","id":1}]}'), initial());
});

test('serialize round trip keeps applied, scanned, bootSeen and window', () => {
  const state = { ...apply(initial(), [tweakA]), scanned: true, bootSeen: true, window: 'settings' as const };
  assert.deepEqual(deserialize(serialize(state)), state);
});

test('undoAll leaves removed apps removed: the app cannot undo a removal, the Store reinstalls', () => {
  const app: Change = { kind: 'app', id: 'app1' };
  const result = undoAll(apply(initial(), [tweakA, app, tweakB]));
  assert.deepEqual(result.steps, [tweakB, tweakA]);
  assert.deepEqual(result.state.applied, [app]);
});

test('undoable lists what the Changes page can undo, without removed apps', () => {
  const app: Change = { kind: 'app', id: 'app1' };
  assert.deepEqual(undoable(apply(initial(), [tweakA, app])), [tweakA]);
});
