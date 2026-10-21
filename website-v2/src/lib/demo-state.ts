// The demo's state and what follows from it, without the DOM: the desktop island renders it and animates the steps.
import type { Demo, Effect } from './demo.ts';

export type WindowId = 'tunero' | 'trust' | 'notepad' | 'settings';
const windowIds: WindowId[] = ['tunero', 'trust', 'notepad', 'settings'];

export type ChangeKind = 'finding' | 'tweak' | 'app' | 'startup';
const kinds: ChangeKind[] = ['finding', 'tweak', 'app', 'startup'];

export interface Change {
  kind: ChangeKind;
  id: string;
}

export interface DemoState {
  /** Applied changes, oldest first. */
  applied: Change[];
  scanned: boolean;
  bootSeen: boolean;
  window: WindowId | null;
}

export const initial = (): DemoState => ({ applied: [], scanned: false, bootSeen: false, window: null });

const same = (a: Change, b: Change) => a.kind === b.kind && a.id === b.id;

/** Everything the visitor can change on the demo PC, with its effect. The startup finding is fixed by its entries. */
export function allChanges(demo: Demo): (Change & { effect: Effect })[] {
  return [
    ...demo.findings.filter((f) => f.fix === 'apply').map((f) => ({ kind: 'finding' as const, id: f.id, effect: f.effect })),
    ...demo.tweaks.map((x) => ({ kind: 'tweak' as const, id: x.id, effect: x.effect })),
    ...demo.apps.map((x) => ({ kind: 'app' as const, id: x.id, effect: x.effect })),
    ...demo.startup.map((x) => ({ kind: 'startup' as const, id: x.id, effect: x.effect })),
  ];
}

export const isApplied = (state: DemoState, change: Change) => state.applied.some((a) => same(a, change));

export function apply(state: DemoState, changes: Change[]): DemoState {
  const applied = [...state.applied];
  for (const change of changes) if (!applied.some((a) => same(a, change))) applied.push({ kind: change.kind, id: change.id });
  return { ...state, applied };
}

export const undo = (state: DemoState, change: Change): DemoState => ({ ...state, applied: state.applied.filter((a) => !same(a, change)) });

/** What the Changes page can undo: everything but removed apps, which only the Microsoft Store brings back. */
export const undoable = (state: DemoState) => state.applied.filter((c) => c.kind !== 'app');

/** Undo all: the end state, and the changes in the order the rewind shows them (newest first). Removed apps stay. */
export function undoAll(state: DemoState): { state: DemoState; steps: Change[] } {
  return { state: { ...state, applied: state.applied.filter((c) => c.kind === 'app') }, steps: undoable(state).reverse() };
}

/** How cluttered the demo PC is: 1 with nothing applied, 0 with everything applied. */
export function clutter(state: DemoState, demo: Demo): number {
  const all = allChanges(demo);
  return all.length === 0 ? 0 : 1 - all.filter((c) => isApplied(state, c)).length / all.length;
}

/** An effect shows while any of the changes behind it is not applied. */
export function isActive(state: DemoState, demo: Demo, effect: Effect): boolean {
  return allChanges(demo).some((c) => c.effect === effect && !isApplied(state, c));
}

export const serialize = (state: DemoState) => JSON.stringify(state);

/** The saved state, or the initial one when there is none or it is not one this code wrote. */
export function deserialize(text: string | null): DemoState {
  try {
    const data = JSON.parse(text ?? '');
    const valid =
      Array.isArray(data?.applied) &&
      data.applied.every((c: Change) => kinds.includes(c?.kind) && typeof c.id === 'string') &&
      typeof data.scanned === 'boolean' &&
      typeof data.bootSeen === 'boolean' &&
      (data.window === null || windowIds.includes(data.window));
    if (!valid) return initial();
    return { applied: data.applied.map((c: Change) => ({ kind: c.kind, id: c.id })), scanned: data.scanned, bootSeen: data.bootSeen, window: data.window };
  } catch {
    return initial();
  }
}
