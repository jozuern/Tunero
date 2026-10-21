// "Watch Tunero fix it": the clicks a visitor would make, in the order the app's pages offer them. The desktop plays
// them with a cursor on the real buttons (data-auto holds each step's target), so nothing happens that a click could
// not do.
import type { Demo } from './demo.ts';
import { apply, isApplied, type Change, type DemoState } from './demo-state.ts';

export type AutoPage = 'overview' | 'tweaks' | 'startup' | 'apps';

export interface AutoStep {
  page: AutoPage;
  /** The data-auto value of the button the step clicks. */
  target: string;
  changes: Change[];
}

export function autoplayPlan(start: DemoState, demo: Demo): AutoStep[] {
  const steps: AutoStep[] = [];
  let state = start;
  const add = (step: AutoStep) => {
    const fresh = step.changes.filter((change) => !isApplied(state, change));
    if (fresh.length === 0) return;
    steps.push({ ...step, changes: fresh });
    state = apply(state, fresh);
  };

  for (const f of demo.findings) {
    if (f.fix === 'apply') add({ page: 'overview', target: `finding:${f.id}`, changes: [{ kind: 'finding', id: f.id }] });
    if (f.fix === 'tweak') add({ page: 'overview', target: `finding:${f.id}`, changes: [{ kind: 'tweak', id: f.tweak! }] });
  }
  for (const category of new Set(demo.tweaks.map((t) => t.category ?? ''))) {
    const changes = demo.tweaks.filter((t) => (t.category ?? '') === category).map((t): Change => ({ kind: 'tweak', id: t.id }));
    add({ page: 'tweaks', target: `group:${category}`, changes });
  }
  for (const s of demo.startup) add({ page: 'startup', target: `startup:${s.id}`, changes: [{ kind: 'startup', id: s.id }] });
  for (const a of demo.apps) add({ page: 'apps', target: `app:${a.id}`, changes: [{ kind: 'app', id: a.id }] });
  return steps;
}
