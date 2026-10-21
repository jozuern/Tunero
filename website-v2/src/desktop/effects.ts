// Shows cause and effect on the desktop: a short trail of light from where a change was made to the place on the
// desktop it changes, and back again on undo. Web Animations only; nothing runs under reduced motion.
import type { Effect } from '../lib/demo.ts';
import { reducedMotion as reduced } from './motion.ts';

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
  'refresh-rate': 'settings',
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

  target.animate([{ filter: 'brightness(1)' }, { filter: 'brightness(1.8) drop-shadow(0 0 8px var(--accent))' }, { filter: 'brightness(1)' }], {
    duration: 520,
    delay: direction === 'apply' ? 560 : 0,
    easing: 'ease-out',
  });
}
