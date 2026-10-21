// Whether the desktop animates. The system's reduced motion setting decides, unless the visitor chose otherwise in
// the demo's Settings ("Animation effects", saved in localStorage as motion = on or off). CSS follows data-motion on
// <html> (reduce or full), which Base.astro sets before the first paint.
const key = 'motion';

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
  if (choice) return choice === 'off';
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}
