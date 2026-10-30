import { test } from 'node:test';
import assert from 'node:assert/strict';
import { autoplayPlan } from './autoplay.ts';
import { apply, initial } from './demo-state.ts';
import { buildDemo } from './demo.ts';

const demo = buildDemo('en');

test('the plan fixes the problems first, then turns on each tweak group, then startup, then apps', () => {
  const plan = autoplayPlan(initial(), demo);
  assert.deepEqual(plan.slice(0, 2).map((s) => s.target), ['finding:F1.refresh', 'finding:F6.turbo']);
  assert.deepEqual([...new Set(plan.map((s) => s.page))], ['overview', 'tweaks', 'startup', 'apps']);
  // Turbo restore is already on after its finding's Fix, so the Power group has nothing left to turn on.
  assert.ok(!plan.some((s) => s.target === 'group:Power'));
});

test('after the plan nothing is left to change', () => {
  const end = autoplayPlan(initial(), demo).reduce((state, step) => apply(state, step.changes), initial());
  assert.deepEqual(autoplayPlan(end, demo), []);
});

test('the plan skips what the visitor already changed', () => {
  const state = apply(initial(), [{ kind: 'finding', id: 'F1.refresh' }, { kind: 'app', id: demo.apps[0].id }]);
  const targets = autoplayPlan(state, demo).map((s) => s.target);
  assert.ok(!targets.includes('finding:F1.refresh'));
  assert.ok(!targets.includes(`app:${demo.apps[0].id}`));
});
