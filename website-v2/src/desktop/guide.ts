// "Watch Tunero fix it": a cursor clicks through the plan of lib/autoplay.ts on the real buttons of the Tunero window,
// page by page, so the visitor sees the clicks the app needs and the desktop answering each one. It moves like a hand:
// quick over a long way and settling on short ones, a moment on the button (which shows its hover state) before it
// presses it, the page gliding to what comes next, and a caption naming the step. Any key or click of the visitor
// stops it where it is. Under reduced motion the same changes apply at once, without the cursor.
import { autoplayPlan, type AutoPage } from '../lib/autoplay.ts';
import type { DesktopStore } from './store.svelte.ts';
import { reducedMotion } from './motion.ts';
import { click } from './sound.ts';
import { cascadeStep } from './app/flash.ts';

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * The rhythm per page: how long the cursor rests on a button before it presses it, and how long it lets the answer
 * play after the press (a fixed problem unfolds what it changed, a group's switches flip one after the other).
 */
const rhythm: Record<AutoPage, { dwell: number; after: number }> = {
  overview: { dwell: 260, after: 1150 },
  tweaks: { dwell: 180, after: 360 },
  startup: { dwell: 90, after: 160 },
  apps: { dwell: 140, after: 330 },
};
/** After a page change, before the cursor moves on: the page is seen first. */
const pageSettle = 280;

const arrow = `<svg viewBox="0 0 24 24" width="28" height="28" aria-hidden="true"><path d="M5 2.5v17.2l4.3-4.1 2.9 6.6 3-1.3-2.9-6.5h6.1z" fill="#fff" stroke="#000" stroke-width="1.2" stroke-linejoin="round"/></svg>`;

type Point = { x: number; y: number };

/** Where the cursor aims on an element: a switch by its track (at the left of its label), anything else in the middle. */
function aim(target: Element): Point {
  const rect = target.getBoundingClientRect();
  const x = target.matches('label') ? rect.left + Math.min(20, rect.width / 2) : rect.left + rect.width / 2;
  return { x, y: rect.top + rect.height / 2 };
}

function makeCursor(from: Point) {
  const element = document.createElement('div');
  element.className = 'guide-cursor';
  element.innerHTML = arrow;
  document.body.append(element);
  let at = from;
  element.style.transform = `translate(${at.x}px, ${at.y}px)`;
  element.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 240 });
  return {
    async moveTo(target: Element) {
      const to = aim(target);
      const distance = Math.hypot(to.x - at.x, to.y - at.y);
      if (distance < 1) return;
      // Like a hand: the time grows with the logarithm of the distance (Fitts), so long moves are quick and short ones
      // still take a moment. The path bows a little to one side, as a wrist turns.
      const duration = Math.min(760, 200 + 100 * Math.log2(1 + distance / 60));
      const bow = { x: (to.y - at.y) * 0.1, y: -(to.x - at.x) * 0.1 };
      const frames = [0, 0.25, 0.5, 0.75, 1].map((t) => {
        const lift = 4 * t * (1 - t);
        return { transform: `translate(${at.x + (to.x - at.x) * t + bow.x * lift}px, ${at.y + (to.y - at.y) * t + bow.y * lift}px)` };
      });
      const animation = element.animate(frames, { duration, easing: 'cubic-bezier(0.35, 0, 0.15, 1)', fill: 'forwards' });
      await animation.finished.catch(() => {});
      at = to;
    },
    /** Rests on the button, which shows its hover state meanwhile. */
    async hover(target: Element, ms: number) {
      target.classList.add('guide-hover');
      await wait(ms);
    },
    /** Presses: the cursor and the button dip, and a ring spreads from the point. */
    async press(target: Element) {
      click();
      const ring = document.createElement('span');
      ring.className = 'guide-click';
      ring.style.transform = `translate(${at.x}px, ${at.y}px)`;
      document.body.append(ring);
      ring.animate(
        [
          { opacity: 0.9, transform: `translate(${at.x}px, ${at.y}px) scale(0.3)` },
          { opacity: 0, transform: `translate(${at.x}px, ${at.y}px) scale(1.6)` },
        ],
        { duration: 620, easing: 'cubic-bezier(0.2, 0.7, 0.2, 1)' },
      ).onfinish = () => ring.remove();
      target.animate([{ scale: '1' }, { scale: '0.95' }, { scale: '1' }], { duration: 180, easing: 'ease-out' });
      // The arrow dips around its tip; scaling the cursor itself would scale its position on the page too.
      const arrowElement = element.firstElementChild as SVGElement;
      arrowElement.animate([{ transform: 'scale(1)' }, { transform: 'scale(0.82)' }, { transform: 'scale(1)' }], { duration: 160 });
      // The click lands at the bottom of the dip.
      await wait(80);
      target.classList.remove('guide-hover');
    },
    remove() {
      element.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 300, fill: 'forwards' }).onfinish = () => element.remove();
    },
  };
}

/** True when what is on screen at the cursor's aim on `element` is `element` itself, not a notification over it. */
function uncovered(element: Element) {
  const point = aim(element);
  const hit = document.elementFromPoint(point.x, point.y);
  return hit !== null && (hit === element || element.contains(hit));
}

/**
 * Glides the Tunero page so `element` is in view and not under a notification, without moving the window frame;
 * resolves when it is there.
 */
async function reveal(element: Element) {
  const page = element.closest('.content');
  if (!page) return;
  const rect = element.getBoundingClientRect();
  const box = page.getBoundingClientRect();
  if (rect.top >= box.top + 24 && rect.bottom <= box.bottom - 24 && uncovered(element)) return;
  const start = page.scrollTop;
  const end = Math.max(0, Math.min(page.scrollHeight - page.clientHeight, start + rect.top - box.top - box.height / 3));
  const duration = 420;
  const begun = performance.now();
  await new Promise<void>((resolve) => {
    const step = (now: number) => {
      const t = Math.min(1, (now - begun) / duration);
      const eased = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
      page.scrollTop = start + (end - start) * eased;
      if (t < 1) requestAnimationFrame(step);
      else resolve();
    };
    requestAnimationFrame(step);
  });
}

async function find(selector: string, tries = 20): Promise<HTMLElement | null> {
  for (let i = 0; i < tries; i++) {
    const element = document.querySelector<HTMLElement>(`[data-window="tunero"] ${selector}`);
    if (element) return element;
    await wait(50);
  }
  return null;
}

export async function playGuide(store: DesktopStore, from: Point) {
  if (store.guiding) return;
  store.guiding = true;
  // A running Undo all ends first: the plan must start from the state it was heading for, or its first click (which
  // ends the rewind) would undo what the plan counts as done.
  store.stopRewind();
  store.open('tunero');
  if (!store.state.scanned) store.markScanned();
  const plan = autoplayPlan(store.state, store.demo);
  const pages = [...new Set(plan.map((step) => step.page))];

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
  let hovered: Element | null = null;
  try {
    await wait(450);
    for (const step of plan) {
      if (stopped) break;
      if (store.guideStep?.page !== step.page) store.guideStep = { page: step.page, index: pages.indexOf(step.page) + 1, total: pages.length };
      if (store.appPage !== step.page) {
        const nav = await find(`[data-nav="${step.page}"]`);
        if (nav) {
          await cursor.moveTo(nav);
          await cursor.hover((hovered = nav), 110);
          if (stopped) break;
          await cursor.press(nav);
          if (stopped) break;
        }
        store.appPage = step.page;
        await wait(pageSettle);
      }
      if (stopped) break;
      const target = await find(`[data-auto="${CSS.escape(step.target)}"]`);
      if (!target) {
        store.apply(step.changes);
        continue;
      }
      await reveal(target);
      await cursor.moveTo(target);
      if (stopped) break;
      await cursor.hover((hovered = target), rhythm[step.page].dwell);
      if (stopped) break;
      await cursor.press(target);
      // The visitor may have taken over during the press: their click, not the guide's, counts then.
      if (stopped) break;
      target.click();
      const cascade = step.page === 'tweaks' ? step.changes.length * cascadeStep : 0;
      await wait(rhythm[step.page].after + cascade);
    }
    if (!stopped) {
      store.guideStep = null;
      const home = await find('[data-nav="overview"]');
      if (home) {
        await cursor.moveTo(home);
        await cursor.hover((hovered = home), 140);
        await cursor.press(home);
      }
      store.appPage = 'overview';
    }
  } finally {
    window.removeEventListener('pointerdown', stop, true);
    window.removeEventListener('keydown', stop, true);
    hovered?.classList.remove('guide-hover');
    cursor.remove();
    store.guideStep = null;
    store.guiding = false;
  }
}
