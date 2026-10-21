// "Watch Tunero fix it": a cursor clicks through the plan of lib/autoplay.ts on the real buttons of the Tunero window,
// page by page, so the visitor sees the clicks the app needs and the desktop answering each one. Any key or click of
// the visitor stops it where it is. Under reduced motion the same changes apply at once, without the cursor.
import { autoplayPlan } from '../lib/autoplay.ts';
import type { DesktopStore } from './store.svelte.ts';
import { reducedMotion } from './motion.ts';
import { click } from './sound.ts';

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Pause after each kind of click, so every answer on the desktop can be seen. */
const pause = { overview: 650, tweaks: 420, startup: 90, apps: 140 } as const;

const arrow = `<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true"><path d="M5 2.5v17.2l4.3-4.1 2.9 6.6 3-1.3-2.9-6.5h6.1z" fill="#fff" stroke="#000" stroke-width="1.3" stroke-linejoin="round"/></svg>`;

function makeCursor(from: { x: number; y: number }) {
  const element = document.createElement('div');
  element.className = 'guide-cursor';
  element.innerHTML = arrow;
  document.body.append(element);
  let at = from;
  element.style.transform = `translate(${at.x}px, ${at.y}px)`;
  return {
    async moveTo(target: Element) {
      const rect = target.getBoundingClientRect();
      const to = { x: rect.left + Math.min(rect.width / 2, 40), y: rect.top + rect.height / 2 };
      const distance = Math.hypot(to.x - at.x, to.y - at.y);
      // A slight curve, as a hand moves a mouse.
      const mid = { x: (at.x + to.x) / 2 + (to.y - at.y) * 0.12, y: (at.y + to.y) / 2 - (to.x - at.x) * 0.12 };
      const animation = element.animate(
        [{ transform: `translate(${at.x}px, ${at.y}px)` }, { transform: `translate(${mid.x}px, ${mid.y}px)` }, { transform: `translate(${to.x}px, ${to.y}px)` }],
        { duration: Math.min(480, 140 + distance * 0.35), easing: 'cubic-bezier(0.4, 0, 0.2, 1)', fill: 'forwards' },
      );
      await animation.finished.catch(() => {});
      at = to;
    },
    async press() {
      click();
      const ring = document.createElement('span');
      ring.className = 'guide-click';
      ring.style.transform = `translate(${at.x}px, ${at.y}px)`;
      document.body.append(ring);
      ring.animate([{ opacity: 0.9, scale: '0.3' }, { opacity: 0, scale: '1.6' }], { duration: 420, easing: 'ease-out' }).onfinish = () => ring.remove();
      await element.animate([{ scale: '1' }, { scale: '0.85' }, { scale: '1' }], { duration: 110, composite: 'add' }).finished.catch(() => {});
    },
    remove() {
      element.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 300, fill: 'forwards' }).onfinish = () => element.remove();
    },
  };
}

/** Scrolls the Tunero page so `element` is in view, without moving the window frame. */
function reveal(element: Element) {
  const page = element.closest('.content');
  if (!page) return;
  const rect = element.getBoundingClientRect();
  const box = page.getBoundingClientRect();
  if (rect.top < box.top + 24 || rect.bottom > box.bottom - 24) page.scrollTop += rect.top - box.top - box.height / 3;
}

async function find(selector: string, tries = 20): Promise<HTMLElement | null> {
  for (let i = 0; i < tries; i++) {
    const element = document.querySelector<HTMLElement>(`[data-window="tunero"] ${selector}`);
    if (element) return element;
    await wait(50);
  }
  return null;
}

export async function playGuide(store: DesktopStore, from: { x: number; y: number }) {
  if (store.guiding) return;
  store.guiding = true;
  store.open('tunero');
  if (!store.state.scanned) store.markScanned();
  const plan = autoplayPlan(store.state, store.demo);

  if (reducedMotion()) {
    store.appPage = 'overview';
    for (const step of plan) store.apply(step.changes);
    store.guiding = false;
    return;
  }

  let stopped = false;
  const stop = (event: Event) => {
    if (event.isTrusted) stopped = true;
  };
  window.addEventListener('pointerdown', stop, true);
  window.addEventListener('keydown', stop, true);
  const cursor = makeCursor(from);
  try {
    await wait(400);
    for (const step of plan) {
      if (stopped) break;
      if (store.appPage !== step.page) {
        const nav = await find(`[data-nav="${step.page}"]`);
        if (nav) {
          await cursor.moveTo(nav);
          await cursor.press();
        }
        store.appPage = step.page;
        await wait(260);
      }
      if (stopped) break;
      const target = await find(`[data-auto="${CSS.escape(step.target)}"]`);
      if (!target) {
        store.apply(step.changes);
        continue;
      }
      reveal(target);
      await wait(60);
      await cursor.moveTo(target);
      if (stopped) break;
      await cursor.press();
      target.click();
      await wait(pause[step.page]);
    }
    if (!stopped) {
      const home = await find('[data-nav="overview"]');
      if (home) {
        await cursor.moveTo(home);
        await cursor.press();
      }
      store.appPage = 'overview';
    }
  } finally {
    window.removeEventListener('pointerdown', stop, true);
    window.removeEventListener('keydown', stop, true);
    cursor.remove();
    store.guiding = false;
  }
}
