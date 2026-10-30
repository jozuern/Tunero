// Shows cause and effect on the desktop: what a change removes bursts into specks that fly to Tunero, and a short trail
// of light runs from where a change was made to the place on the desktop it changes, and back again on undo. Web
// Animations only; nothing runs under reduced motion. The store (store.svelte.ts) calls these after each step.
import type { Demo, Effect } from '../lib/demo.ts';
import { allChanges, isActive, isApplied, turnedOff, type Change, type ChangeKind, type DemoState } from '../lib/demo-state.ts';
import { reducedMotion as reduced } from './motion.ts';
import { shatter } from './particles.ts';
import { ripple } from './wallpaper/wallpaper.ts';
import { pop } from './sound.ts';

/** Where each effect lives on the desktop: the element marked with data-anchor. */
const anchors: Record<Effect, string> = {
  'tips-toasts': 'toasts',
  'feedback-toast': 'toasts',
  'search-highlights': 'search',
  'start-recommended': 'start',
  'start-account': 'start',
  'start-app': 'start',
  'explorer-sync-ad': 'explorer',
  tray: 'tray',
  'refresh-rate': 'refresh',
  turbo: 'turbo',
  none: '',
};

let lastPointer = { x: 0, y: 0, time: 0 };
if (typeof window !== 'undefined') {
  window.addEventListener('pointerdown', (event) => (lastPointer = { x: event.clientX, y: event.clientY, time: performance.now() }), {
    capture: true,
    passive: true,
  });
}

function center(element: Element) {
  const rect = element.getBoundingClientRect();
  return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
}

/** The point a change came from: the last click if it was just now, else the middle of the Tunero window. */
function origin() {
  if (performance.now() - lastPointer.time < 1500) return lastPointer;
  const tunero = document.querySelector('[data-window="tunero"]');
  return tunero ? center(tunero) : { x: innerWidth / 2, y: innerHeight / 2 };
}

export function trail(effect: Effect, direction: 'apply' | 'undo') {
  if (reduced() || effect === 'none') return;
  // The first visible one: phones show the strip, wider screens the taskbar.
  const target = [...document.querySelectorAll(`[data-anchor="${anchors[effect]}"]`)].find((element) => element.getClientRects().length > 0);
  if (!target) return;
  const a = origin();
  const b = center(target);
  const [from, to] = direction === 'apply' ? [a, b] : [b, a];

  const dot = document.createElement('span');
  dot.className = `trail ${direction}`;
  document.body.append(dot);
  // A gentle arc: the midpoint rises above the straight line.
  const mid = { x: (from.x + to.x) / 2, y: Math.min(from.y, to.y) - Math.min(160, Math.abs(to.x - from.x) * 0.25) };
  const at = (p: { x: number; y: number }, scale: number, opacity: number) => ({ transform: `translate(${p.x}px, ${p.y}px) scale(${scale})`, opacity });
  const flight = dot.animate([at(from, 0.6, 0), at(from, 1, 1), at(mid, 1.3, 1), at(to, 0.8, 1), at(to, 2.4, 0)], {
    duration: 760,
    easing: 'cubic-bezier(0.45, 0, 0.2, 1)',
  });
  flight.onfinish = () => dot.remove();
  flight.oncancel = () => dot.remove();

  glow(target, direction, direction === 'apply' ? 560 : 0);
}

/**
 * Lights the place the trail reaches: a glowing outline laid over it whose opacity is animated. The place itself is
 * left alone: a filter on it would repaint it on every frame and, on the notifications, cut their background blur.
 */
function glow(target: Element, direction: 'apply' | 'undo', delay: number) {
  // Measured when it lights, not when the trail starts: the place can move meanwhile (a notification above it leaves).
  setTimeout(() => {
    const rect = target.getBoundingClientRect();
    if (!target.isConnected || rect.width === 0 || rect.height === 0) return;
    const ring = document.createElement('span');
    ring.className = `trail-glow ${direction}`;
    Object.assign(ring.style, { left: `${rect.left}px`, top: `${rect.top}px`, width: `${rect.width}px`, height: `${rect.height}px` });
    document.body.append(ring);
    const light = ring.animate([{ opacity: 0 }, { opacity: 1, offset: 0.3 }, { opacity: 0 }], { duration: 620, easing: 'ease-out' });
    light.onfinish = () => ring.remove();
    light.oncancel = () => ring.remove();
  }, delay);
}

/** Shows each change travelling to its place on the desktop, a little apart when several go at once. */
export function trails(changes: Change[], direction: 'apply' | 'undo', demo: Demo, skip = new Set<Effect>()) {
  const effects = allChanges(demo);
  changes.forEach((change, i) => {
    const effect = effects.find((c) => c.kind === change.kind && c.id === change.id)?.effect;
    if (effect && !skip.has(effect)) setTimeout(() => trail(effect, direction), i * 90);
  });
}

/**
 * Bursts what a step removes from the desktop (marked data-effect or data-change) into specks that fly to Tunero,
 * with a ripple in the wallpaper under it. Returns the effects that burst, which need no trail.
 */
export function shatterGone(before: DemoState, after: DemoState, fresh: Change[], demo: Demo): Set<Effect> {
  const gone = turnedOff(before, after, demo);
  const selectors = [...gone.map((e) => `[data-effect~="${e}"]`), ...fresh.map((c) => `[data-change="${c.kind}:${c.id}"]`)];
  const burst = new Set<Effect>();
  if (selectors.length === 0 || reduced()) return burst;
  for (const element of document.querySelectorAll(selectors.join(','))) {
    if (element.getClientRects().length === 0 || element.closest('.minimized')) continue;
    const rect = element.getBoundingClientRect();
    shatter(element);
    element.classList.add('shattered');
    ripple(rect.left + rect.width / 2, rect.top + rect.height / 2, Math.max(rect.width, rect.height));
    for (const effect of gone) if (element.matches(`[data-effect~="${effect}"]`)) burst.add(effect);
    for (const c of fresh) if (element.matches(`[data-change="${c.kind}:${c.id}"]`)) {
      const effect = allChanges(demo).find((x) => x.kind === c.kind && x.id === c.id)?.effect;
      if (effect) burst.add(effect);
    }
  }
  if (burst.size > 0) pop();
  return burst;
}

/**
 * After an undo: what shattered and comes back before its leaving transition ended is the same element (Svelte keeps
 * it), still marked as shattered and so invisible. It loses the mark once its change is undone or its effect shows.
 */
export function revive(state: DemoState, demo: Demo) {
  if (typeof document === 'undefined') return;
  for (const element of document.querySelectorAll<HTMLElement>('.shattered')) {
    const marked = element.dataset.change;
    const back = marked
      ? !isApplied(state, { kind: marked.slice(0, marked.indexOf(':')) as ChangeKind, id: marked.slice(marked.indexOf(':') + 1) })
      : (element.dataset.effect ?? '').split(/\s+/).some((effect) => effect !== '' && isActive(state, demo, effect as Effect));
    if (back) element.classList.remove('shattered');
  }
}
