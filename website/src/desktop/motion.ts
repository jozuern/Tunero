// Whether the desktop animates: always, unless the visitor turned it off in the demo's Settings ("Animation effects",
// saved in localStorage as motion = on or off). The system's reduced motion setting is not followed (owner,
// 2026-10-21): the demo is the show. CSS follows data-motion on <html> (reduce or full), which Base.astro sets before
// the first paint.
import { storageKeys } from '../lib/storage-keys.ts';

const key = storageKeys.motion;

export function motionChoice(): 'on' | 'off' | null {
  try {
    const value = localStorage.getItem(key);
    return value === 'on' || value === 'off' ? value : null;
  } catch {
    return null;
  }
}

export function setMotionChoice(value: 'on' | 'off') {
  try {
    localStorage.setItem(key, value);
  } catch {
    // Storage blocked: the choice holds until the page is left.
  }
  override = value;
  document.documentElement.dataset.motion = reducedMotion() ? 'reduce' : 'full';
  for (const listener of listeners) listener();
}

let override: 'on' | 'off' | null = null;
const listeners = new Set<() => void>();

/** Calls back when the visitor changes the choice; returns the unsubscribe function. */
export function onMotionChange(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function reducedMotion(): boolean {
  const choice = override ?? motionChoice();
  return choice === 'off';
}

/**
 * A transition's duration, delay or distance: the value while the desktop animates, 0 with animation effects off. The
 * Svelte transitions take their numbers through this; the Web Animations (effects.ts, guide.ts, Dive) return early under
 * reducedMotion() instead.
 */
export const animated = (value: number) => (reducedMotion() ? 0 : value);
